using System.Diagnostics;
using TextFlow.Contracts.Insertion;
using TextFlow.Contracts.Targeting;

namespace TextFlow.Core.Operations;

/// <param name="SendInputMaxLength">
/// Single-line texts up to this length are typed instead of pasted. 0 (default) = always paste first:
/// Win11 Notepad garbled fast SendInput bursts in the S3 test (ADR-0001, 2026-10-02).
/// </param>
public sealed record InsertionOptions(int SendInputMaxLength = 0);

/// <summary>
/// Validates the captured target and runs insertion strategies in capability order (ADR-0001).
/// Never inserts into a target that failed validation; never falls back once input reached the target.
/// </summary>
public sealed class InsertionCoordinator : ITextInsertionService
{
    private readonly ITargetResolver _resolver;
    private readonly IReadOnlyList<IInsertionStrategy> _strategies;
    private readonly InsertionOptions _options;

    public InsertionCoordinator(ITargetResolver resolver, IEnumerable<IInsertionStrategy> strategies, InsertionOptions options)
    {
        _resolver = resolver;
        _strategies = strategies.ToArray();
        _options = options;
    }

    public async Task<InsertionResult> InsertAsync(InsertionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var clock = Stopwatch.StartNew();

        if (request.Target.IsElevated)
        {
            return Fail(InsertionStatus.PermissionDenied, clock, "Target runs elevated.");
        }

        InsertionResult? last = null;
        foreach (var strategy in Order(request))
        {
            if (ct.IsCancellationRequested)
            {
                return Fail(InsertionStatus.Cancelled, clock);
            }

            var validation = _resolver.ValidateTarget(request.Target);
            if (!validation.IsValid)
            {
                return Fail(InsertionStatus.TargetChanged, clock, validation.Status.ToString());
            }

            last = await strategy.InsertAsync(request, ct).ConfigureAwait(false);
            if (last.Succeeded || last.InputSent || last.Status == InsertionStatus.Cancelled)
            {
                return last with { Elapsed = clock.Elapsed };
            }
        }

        if (ct.IsCancellationRequested)
        {
            return Fail(InsertionStatus.Cancelled, clock);
        }

        return last is null
            ? Fail(InsertionStatus.UnsupportedTarget, clock, "No strategy can handle this target.")
            : last with { Elapsed = clock.Elapsed };
    }

    private IEnumerable<IInsertionStrategy> Order(InsertionRequest request)
    {
        var preferred = request.PreferredStrategy ?? DefaultPreference(request.Text);
        return _strategies
            .Where(s => s.CanHandle(request))
            .OrderBy(s => s.Kind == preferred ? 0 : 1)
            .ThenBy(s => s.Kind == InsertionStrategyKind.Clipboard ? 0 : 1);
    }

    private InsertionStrategyKind DefaultPreference(string text) =>
        text.Length <= _options.SendInputMaxLength && !text.Contains('\n', StringComparison.Ordinal)
            ? InsertionStrategyKind.SendInput
            : InsertionStrategyKind.Clipboard;

    private static InsertionResult Fail(InsertionStatus status, Stopwatch clock, string? detail = null) =>
        new(status, null, clock.Elapsed, detail);
}

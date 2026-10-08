using TextFlow.Contracts.Insertion;

namespace TextFlow.Core.Diagnostics;

/// <summary>
/// Metrics of one day of <see cref="DiagnosticEvent"/>s for the Diagnóstico page (H5.4). Content-free like its input:
/// counts, times and process names only.
/// </summary>
public sealed record DiagnosticSummary(
    int Expansions,
    int Successes,
    IReadOnlyDictionary<InsertionStatus, int> Failures,
    double? MedianMs,
    double? P95Ms,
    double? MaxMs,
    IReadOnlyList<(string Process, int Count)> TopApps,
    int MenusShown,
    int MenusAnchoredToCaret,
    IReadOnlyDictionary<MenuCloseReason, int> MenuCloses,
    int FieldsShown,
    IReadOnlyDictionary<FieldsCloseReason, int> FieldsCloses,
    IReadOnlyDictionary<RejectionReason, int> Rejections,
    int HookReinstalls,
    int Faults,
    int Startups,
    double? LastStartupMs)
{
    public const int TopAppCount = 5;

    /// <summary>Successful share of expansions, 0–1; null when there were none.</summary>
    public double? SuccessRate => Expansions == 0 ? null : Successes / (double)Expansions;

    public static DiagnosticSummary From(IEnumerable<DiagnosticEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var all = events.ToArray();
        var expansions = all.OfType<ExpansionCompleted>().ToArray();
        var latencies = expansions.Where(e => e.Status == InsertionStatus.Success).Select(e => e.ElapsedMs).Order().ToArray();
        var menus = all.OfType<MenuShown>().ToArray();
        var startups = StartupDurations(all.OfType<EngineStateChanged>()).ToArray();

        return new DiagnosticSummary(
            expansions.Length,
            latencies.Length,
            CountBy(expansions.Where(e => e.Status != InsertionStatus.Success), e => e.Status),
            Median(latencies),
            Percentile(latencies, 0.95),
            latencies.Length == 0 ? null : latencies[^1],
            expansions.GroupBy(e => e.TargetProcess, StringComparer.OrdinalIgnoreCase)
                .Select(g => (g.Key, g.Count()))
                .OrderByDescending(a => a.Item2)
                .ThenBy(a => a.Key, StringComparer.OrdinalIgnoreCase)
                .Take(TopAppCount)
                .ToArray(),
            menus.Length,
            menus.Count(m => m.AnchoredToCaret),
            CountBy(all.OfType<MenuClosed>(), m => m.Reason),
            all.OfType<FieldsShown>().Count(),
            CountBy(all.OfType<FieldsClosed>(), f => f.Reason),
            CountBy(all.OfType<TargetRejected>(), r => r.Reason),
            all.OfType<HookReinstalled>().Count(),
            all.OfType<EngineFault>().Count(),
            startups.Length,
            startups.Length == 0 ? null : startups[^1]);
    }

    /// <summary>Starting → Running, in ms. A Paused → Running resume is not a startup.</summary>
    private static IEnumerable<double> StartupDurations(IEnumerable<EngineStateChanged> states)
    {
        DateTimeOffset? starting = null;
        foreach (var state in states)
        {
            if (state.State == EngineState.Starting)
            {
                starting = state.At;
            }
            else if (state.State == EngineState.Running && starting is { } since)
            {
                yield return (state.At - since).TotalMilliseconds;
                starting = null;
            }
        }
    }

    private static Dictionary<TKey, int> CountBy<T, TKey>(IEnumerable<T> items, Func<T, TKey> key)
        where TKey : notnull =>
        items.GroupBy(key).ToDictionary(g => g.Key, g => g.Count());

    private static double? Median(double[] sorted) => sorted.Length switch
    {
        0 => null,
        _ when sorted.Length % 2 == 1 => sorted[sorted.Length / 2],
        _ => (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2,
    };

    /// <summary>Nearest-rank percentile.</summary>
    private static double? Percentile(double[] sorted, double fraction) =>
        sorted.Length == 0 ? null : sorted[Math.Max(0, (int)Math.Ceiling(fraction * sorted.Length) - 1)];
}

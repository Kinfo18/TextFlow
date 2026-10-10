using TextFlow.Core.Engine;

namespace TextFlow.Core.Library;

/// <summary>
/// Buffers snippet uses in memory (ids and counts only) so the engine never waits for the disk; the app flushes the
/// batch to storage every so often and on exit. Thread-safe.
/// </summary>
public sealed class UsageTracker : IUsageRecorder
{
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private Dictionary<string, SnippetUsage> _pending = new(StringComparer.Ordinal);

    public UsageTracker(TimeProvider time) => _time = time;

    public void Record(string snippetId)
    {
        ArgumentException.ThrowIfNullOrEmpty(snippetId);
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            _pending[snippetId] = _pending.TryGetValue(snippetId, out var use) ? new SnippetUsage(use.Uses + 1, now) : new SnippetUsage(1, now);
        }
    }

    /// <summary>The uses recorded since the last call; the buffer starts empty again.</summary>
    public IReadOnlyDictionary<string, SnippetUsage> TakePending()
    {
        lock (_lock)
        {
            var taken = _pending;
            _pending = new Dictionary<string, SnippetUsage>(StringComparer.Ordinal);
            return taken;
        }
    }

    /// <summary>A flush failed: its uses go back into the buffer for the next attempt.</summary>
    public void Restore(IReadOnlyDictionary<string, SnippetUsage> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        lock (_lock)
        {
            foreach (var (id, use) in batch)
            {
                _pending[id] = Add(_pending.GetValueOrDefault(id), use);
            }
        }
    }

    /// <summary>Stored counts plus the ones not flushed yet, for the UI.</summary>
    public IReadOnlyDictionary<string, SnippetUsage> Merge(IReadOnlyDictionary<string, SnippetUsage> stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var merged = new Dictionary<string, SnippetUsage>(stored, StringComparer.Ordinal);
        lock (_lock)
        {
            foreach (var (id, use) in _pending)
            {
                merged[id] = Add(merged.GetValueOrDefault(id), use);
            }
        }

        return merged;
    }

    private static SnippetUsage Add(SnippetUsage? a, SnippetUsage b) =>
        a is null ? b : new SnippetUsage(a.Uses + b.Uses, a.LastUsedAt > b.LastUsedAt ? a.LastUsedAt : b.LastUsedAt);
}

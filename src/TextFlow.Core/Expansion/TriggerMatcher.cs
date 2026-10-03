using System.Text;

namespace TextFlow.Core.Expansion;

/// <summary>
/// Detects typed triggers from a stream of characters.
/// Holds a small bounded buffer of what the user typed (user content): never log it,
/// and call <see cref="Reset"/> on focus change, mouse click or navigation keys.
/// Not thread-safe: feed it from the keyboard hook thread only.
/// </summary>
/// <remarks>
/// An immediate trigger that is also the start of a longer one ("dir" vs "dir1") is held pending:
/// the longer trigger wins if completed; any other character fires the held one with that character as a
/// swallowed <see cref="TriggerMatch.Delimiter"/>; otherwise the caller fires it via <see cref="FlushPending"/>
/// after a short timeout.
/// </remarks>
public sealed class TriggerMatcher
{
    private readonly TriggerOptions _options;
    private readonly StringBuilder _buffer;
    private TriggerSet _afterDelimiter = TriggerSet.Empty;
    private TriggerSet _immediate = TriggerSet.Empty;
    private TriggerDefinition? _pending;
    private int _pendingStart;

    public TriggerMatcher(IEnumerable<TriggerDefinition> triggers, TriggerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _buffer = new StringBuilder(options.MaxBufferLength);
        ReplaceTriggers(triggers);
    }

    public int BufferLength => _buffer.Length;

    public bool HasPending => _pending is not null;

    /// <summary>Changes whenever the pending trigger changes; pass it back to <see cref="FlushPending"/>.</summary>
    public int PendingVersion { get; private set; }

    /// <summary>The held trigger as it would fire now, or null.</summary>
    public TriggerMatch? PendingMatch =>
        _pending is { } held ? new TriggerMatch(held.SnippetId, held.Trigger, Delimiter: null, held.Trigger.Length) : null;

    public void ReplaceTriggers(IEnumerable<TriggerDefinition> triggers)
    {
        ArgumentNullException.ThrowIfNull(triggers);

        var map = new Dictionary<string, TriggerDefinition>(StringComparer.Ordinal);
        foreach (var definition in triggers)
        {
            if (definition.Mode == TriggerMode.AfterDelimiter && definition.Trigger.Any(_options.Delimiters.Contains))
            {
                throw new ArgumentException($"Trigger of snippet '{definition.SnippetId}' contains a delimiter.", nameof(triggers));
            }

            if (definition.Trigger.Length >= _options.MaxBufferLength)
            {
                throw new ArgumentException($"Trigger of snippet '{definition.SnippetId}' exceeds the buffer length.", nameof(triggers));
            }

            map[definition.Trigger] = definition;
        }

        _afterDelimiter = TriggerSet.Create(map.Values.Where(d => d.Mode == TriggerMode.AfterDelimiter));
        _immediate = TriggerSet.Create(map.Values.Where(d => d.Mode == TriggerMode.Immediate));
        Reset();
    }

    /// <summary>
    /// Feeds one translated character. Returns a match when a delimiter completes an
    /// <see cref="TriggerMode.AfterDelimiter"/> trigger, when <paramref name="c"/> completes an unambiguous
    /// immediate one, or when <paramref name="c"/> breaks a pending one.
    /// </summary>
    public TriggerMatch? OnCharacter(char c)
    {
        if (_options.Delimiters.Contains(c))
        {
            var delimited = FindMatch(_afterDelimiter);
            if (delimited is not null)
            {
                Reset();
                return new TriggerMatch(delimited.SnippetId, delimited.Trigger, c, delimited.Trigger.Length);
            }

        }

        Append(c);
        return _pending is null ? MatchImmediate() : ContinuePending(c);
    }

    /// <summary>
    /// Fires the pending trigger if it is still the one identified by <paramref name="version"/> and nothing
    /// was typed after it (otherwise it is discarded: deleting the extra characters would lose user text).
    /// </summary>
    public TriggerMatch? FlushPending(int version)
    {
        if (_pending is not { } held || version != PendingVersion)
        {
            return null;
        }

        var exact = _buffer.Length - _pendingStart == held.Trigger.Length;
        Reset();
        return exact ? new TriggerMatch(held.SnippetId, held.Trigger, Delimiter: null, held.Trigger.Length) : null;
    }

    /// <summary>True if typing <paramref name="c"/> would complete or extend a longer trigger than the pending one.</summary>
    public bool ContinuesPending(char c)
    {
        if (_pending is null)
        {
            return false;
        }

        var next = string.Concat(_buffer.ToString(_pendingStart, _buffer.Length - _pendingStart), c.ToString());
        return _immediate.IsProperPrefix(next) || _immediate.Lookup(next) is not null;
    }

    public void OnBackspace()
    {
        if (_buffer.Length > 0)
        {
            _buffer.Length--;
        }

        ClearPending();
    }

    public void Reset()
    {
        _buffer.Clear();
        ClearPending();
    }

    private TriggerMatch? MatchImmediate()
    {
        var immediate = FindMatch(_immediate);
        if (immediate is null)
        {
            return null;
        }

        if (_immediate.IsProperPrefix(immediate.Trigger))
        {
            SetPending(immediate, _buffer.Length - immediate.Trigger.Length);
            return null;
        }

        Reset();
        return new TriggerMatch(immediate.SnippetId, immediate.Trigger, Delimiter: null, immediate.Trigger.Length);
    }

    private TriggerMatch? ContinuePending(char c)
    {
        var held = _pending!;
        var tail = _buffer.ToString(_pendingStart, _buffer.Length - _pendingStart);
        var longer = _immediate.Lookup(tail);
        var stillPrefix = _immediate.IsProperPrefix(tail);

        if (longer is not null && stillPrefix)
        {
            SetPending(longer, _pendingStart);
            return null;
        }

        if (longer is not null)
        {
            Reset();
            return new TriggerMatch(longer.SnippetId, longer.Trigger, Delimiter: null, longer.Trigger.Length);
        }

        if (stillPrefix)
        {
            return null;
        }

        // c rules out the longer candidates. The held trigger fires (c is swallowed, to be re-emitted) only if
        // nothing was typed after it; "foto " + 'x' while waiting for "foto valida" is ordinary text.
        var exact = tail.Length - 1 == held.Trigger.Length;
        Reset();
        return exact ? new TriggerMatch(held.SnippetId, held.Trigger, c, held.Trigger.Length) : null;
    }

    private void SetPending(TriggerDefinition definition, int start)
    {
        _pending = definition;
        _pendingStart = start;
        PendingVersion++;
    }

    private void ClearPending()
    {
        if (_pending is not null)
        {
            _pending = null;
            PendingVersion++;
        }
    }

    /// <summary>Longest trigger of <paramref name="set"/> that ends the buffer at a word boundary.</summary>
    private TriggerDefinition? FindMatch(TriggerSet set)
    {
        foreach (var length in set.LengthsDescending)
        {
            if (length > _buffer.Length)
            {
                continue;
            }

            var start = _buffer.Length - length;
            if (IsBoundary(start) && set.Lookup(_buffer.ToString(start, length)) is { } definition)
            {
                return definition;
            }
        }

        return null;
    }

    private bool IsBoundary(int start) =>
        !_options.RequireWordBoundary || start == 0 || !char.IsLetterOrDigit(_buffer[start - 1]);

    private void Append(char c)
    {
        if (_buffer.Length >= _options.MaxBufferLength)
        {
            var removed = _buffer.Length - _options.MaxBufferLength + 1;
            _buffer.Remove(0, removed);
            _pendingStart -= removed;
            if (_pendingStart < 0)
            {
                ClearPending();
            }
        }

        _buffer.Append(c);
    }

    private sealed record TriggerSet(
        IReadOnlyDictionary<string, TriggerDefinition> Exact,
        IReadOnlyDictionary<string, TriggerDefinition> Loose,
        IReadOnlySet<string> ExactPrefixes,
        IReadOnlySet<string> LoosePrefixes,
        int[] LengthsDescending)
    {
        public static TriggerSet Empty { get; } = Create([]);

        public TriggerDefinition? Lookup(string text) =>
            Exact.TryGetValue(text, out var definition) || Loose.TryGetValue(text, out definition) ? definition : null;

        /// <summary>True when <paramref name="text"/> is the start of a longer trigger.</summary>
        public bool IsProperPrefix(string text) => ExactPrefixes.Contains(text) || LoosePrefixes.Contains(text);

        public static TriggerSet Create(IEnumerable<TriggerDefinition> definitions)
        {
            var all = definitions.ToArray();
            var exact = all.Where(d => !d.IgnoreCase).ToDictionary(d => d.Trigger, StringComparer.Ordinal);
            var loose = new Dictionary<string, TriggerDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var definition in all.Where(d => d.IgnoreCase))
            {
                loose[definition.Trigger] = definition; // case-only duplicates are reported by TriggerValidator
            }

            return new TriggerSet(
                exact,
                loose,
                Prefixes(exact.Keys, StringComparer.Ordinal),
                Prefixes(loose.Keys, StringComparer.OrdinalIgnoreCase),
                all.Select(d => d.Trigger.Length).Distinct().OrderDescending().ToArray());
        }

        private static HashSet<string> Prefixes(IEnumerable<string> triggers, StringComparer comparer) =>
            new(triggers.SelectMany(t => Enumerable.Range(1, t.Length - 1).Select(n => t[..n])), comparer);
    }
}

using System.Text;

namespace TextFlow.Core.Expansion;

/// <summary>
/// Detects typed triggers from a stream of characters.
/// Holds a small bounded buffer of what the user typed (user content): never log it,
/// and call <see cref="Reset"/> on focus change, mouse click or navigation keys.
/// Not thread-safe: feed it from the keyboard hook thread only.
/// </summary>
public sealed class TriggerMatcher
{
    private readonly TriggerOptions _options;
    private readonly StringBuilder _buffer;
    private TriggerSet _afterDelimiter = TriggerSet.Empty;
    private TriggerSet _immediate = TriggerSet.Empty;

    public TriggerMatcher(IEnumerable<TriggerDefinition> triggers, TriggerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _buffer = new StringBuilder(options.MaxBufferLength);
        ReplaceTriggers(triggers);
    }

    public int BufferLength => _buffer.Length;

    public void ReplaceTriggers(IEnumerable<TriggerDefinition> triggers)
    {
        ArgumentNullException.ThrowIfNull(triggers);

        var map = new Dictionary<string, TriggerDefinition>(StringComparer.Ordinal);
        foreach (var definition in triggers)
        {
            if (definition.Trigger.Any(_options.Delimiters.Contains))
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
    /// <see cref="TriggerMode.AfterDelimiter"/> trigger, or when <paramref name="c"/> completes an immediate one.
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

        var immediate = FindMatch(_immediate);
        if (immediate is null)
        {
            return null;
        }

        Reset();
        return new TriggerMatch(immediate.SnippetId, immediate.Trigger, Delimiter: null, immediate.Trigger.Length);
    }

    public void OnBackspace()
    {
        if (_buffer.Length > 0)
        {
            _buffer.Length--;
        }
    }

    public void Reset() => _buffer.Clear();

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
            if (!IsBoundary(start))
            {
                continue;
            }

            var candidate = _buffer.ToString(start, length);
            if (set.Exact.TryGetValue(candidate, out var definition) || set.Loose.TryGetValue(candidate, out definition))
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
            _buffer.Remove(0, _buffer.Length - _options.MaxBufferLength + 1);
        }

        _buffer.Append(c);
    }

    private sealed record TriggerSet(
        IReadOnlyDictionary<string, TriggerDefinition> Exact,
        IReadOnlyDictionary<string, TriggerDefinition> Loose,
        int[] LengthsDescending)
    {
        public static TriggerSet Empty { get; } = Create([]);

        public static TriggerSet Create(IEnumerable<TriggerDefinition> definitions)
        {
            var all = definitions.ToArray();
            var exact = all.Where(d => !d.IgnoreCase).ToDictionary(d => d.Trigger, StringComparer.Ordinal);
            var loose = new Dictionary<string, TriggerDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var definition in all.Where(d => d.IgnoreCase))
            {
                loose[definition.Trigger] = definition; // case-only duplicates are reported by TriggerValidator
            }

            return new TriggerSet(exact, loose, all.Select(d => d.Trigger.Length).Distinct().OrderDescending().ToArray());
        }
    }
}

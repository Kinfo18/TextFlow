namespace TextFlow.Core.Expansion;

public enum TriggerMode
{
    /// <summary>Expands as soon as the last character is typed (aText default).</summary>
    Immediate,

    /// <summary>Expands only when a delimiter follows the trigger; the delimiter key is swallowed.</summary>
    AfterDelimiter,
}

/// <param name="IgnoreCase">Matches regardless of letter case ("lc" fires "LC"). An exact-case trigger with the same text wins.</param>
public sealed record TriggerDefinition(
    string SnippetId,
    string Trigger,
    TriggerMode Mode = TriggerMode.Immediate,
    bool IgnoreCase = false);

/// <param name="Delimiter">Swallowed delimiter for <see cref="TriggerMode.AfterDelimiter"/>; null for immediate triggers.</param>
/// <param name="Backspaces">Characters the target must delete. A swallowed delimiter is not counted.</param>
public sealed record TriggerMatch(string SnippetId, string Trigger, char? Delimiter, int Backspaces);

/// <param name="Delimiters">Characters that confirm a trigger. '\r' is Enter.</param>
/// <param name="RequireWordBoundary">Trigger must start the buffer or follow a non letter/digit.</param>
/// <param name="MaxBufferLength">Upper bound of typed characters kept in memory.</param>
public sealed record TriggerOptions(IReadOnlySet<char> Delimiters, bool RequireWordBoundary, int MaxBufferLength)
{
    public static TriggerOptions Default { get; } = new(
        new HashSet<char> { ' ', '\r', '\t', '.', ',', ':', '!', '?', ')' },
        RequireWordBoundary: true,
        MaxBufferLength: 256);
}

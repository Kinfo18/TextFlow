namespace TextFlow.Core.Library;

/// <summary>How a typed abbreviation fires (spec §8); stored per snippet.</summary>
public enum SnippetMode
{
    Immediate,
    AfterDelimiter,
}

/// <param name="Name">Menu label: the first abbreviation, or a content preview when there is none.</param>
/// <param name="IsRichText">aText stored it as rich text ("h"); imported as plain text for now.</param>
/// <param name="Abbreviations">
/// aText snippet abbreviations; they also label the snippet in group menus.
/// </param>
/// <param name="Mode">Immediate (aText default) or after a delimiter.</param>
/// <param name="Enabled">Disabled snippets stay in the library and menus but never expand.</param>
/// <param name="SendEnter">Presses Enter after the text, to send a chat message (off unless the user turns it on).</param>
public sealed record LibrarySnippet(
    string Id,
    string Name,
    string Content,
    bool IsRichText,
    IReadOnlyList<string> Abbreviations,
    SnippetMode Mode = SnippetMode.Immediate,
    bool Enabled = true,
    bool SendEnter = false)
{
    /// <summary>Empty snippets are notes the user reads in the group menu; selecting one inserts nothing.</summary>
    public bool IsInfoOnly => Content.Length == 0;

    /// <summary>
    /// Abbreviations that work as typed triggers. As in aText, spaces are allowed ("Foto valida");
    /// line breaks and tabs are not.
    /// </summary>
    public IReadOnlyList<string> TypeableAbbreviations => Abbreviations
        .Where(a => a.Length > 0 && !a.Any(c => c is '\r' or '\n' or '\t'))
        .ToArray();
}

/// <param name="Abbreviation">
/// Typing it opens a menu with the group's snippets and subgroups (aText group abbreviation).
/// Groups sharing an abbreviation are listed together in one menu.
/// </param>
public sealed record LibraryGroup(
    string Id,
    string Name,
    string? Abbreviation,
    bool IgnoreCase,
    IReadOnlyList<LibraryGroup> Groups,
    IReadOnlyList<LibrarySnippet> Snippets);

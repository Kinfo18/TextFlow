namespace TextFlow.Core.Templates;

public abstract record TemplateSegment;

public sealed record LiteralSegment(string Text) : TemplateSegment;

/// <summary>Built-in value resolved at render time: date, time, clipboard, selection.</summary>
public sealed record VariableSegment(BuiltinVariable Variable, string? Argument) : TemplateSegment;

/// <summary>User-filled field, e.g. <c>{{cliente}}</c> or <c>{{empresa=ACME}}</c>.</summary>
public sealed record FieldSegment(string Name, string? DefaultValue) : TemplateSegment;

public sealed record CursorSegment : TemplateSegment;

public enum BuiltinVariable
{
    Date,
    Time,
    Clipboard,
    Selection,
}

public sealed record TemplateField(string Name, string? DefaultValue);

public sealed record ParsedTemplate(IReadOnlyList<TemplateSegment> Segments, IReadOnlyList<TemplateField> Fields)
{
    public IReadOnlyList<string> FieldNames => Fields.Select(f => f.Name).ToArray();

    public bool RequiresInput => Fields.Count > 0;

    public bool UsesClipboard => Segments.Any(s => s is VariableSegment { Variable: BuiltinVariable.Clipboard });

    public bool UsesSelection => Segments.Any(s => s is VariableSegment { Variable: BuiltinVariable.Selection });
}

/// <param name="Text">Final text, CRLF line endings. In-memory only.</param>
/// <param name="CaretOffsetFromEnd">Caret steps (text elements) to move left after insertion; 0 = end.</param>
public sealed record RenderedTemplate(string Text, int CaretOffsetFromEnd, bool UsedClipboard, bool UsedSelection);

/// <summary>Supplies runtime values. Implementations must not cache returned text.</summary>
public interface IVariableSource
{
    DateTimeOffset Now { get; }

    string? GetClipboardText();

    string? GetSelectionText();
}

using System.Globalization;
using System.Text;

namespace TextFlow.Core.Templates;

public static class TemplateRenderer
{
    private const string DefaultTimeFormat = "HH:mm";

    public static RenderedTemplate Render(
        ParsedTemplate template,
        IReadOnlyDictionary<string, string> fieldValues,
        IVariableSource variables,
        CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(fieldValues);
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(culture);

        var before = new StringBuilder();
        var after = new StringBuilder();
        var cursorSeen = false;
        var usedClipboard = false;
        var usedSelection = false;

        foreach (var segment in template.Segments)
        {
            var target = cursorSeen ? after : before;
            switch (segment)
            {
                case LiteralSegment literal:
                    target.Append(literal.Text);
                    break;
                case FieldSegment field:
                    target.Append(fieldValues.TryGetValue(field.Name, out var value) ? value : field.DefaultValue ?? string.Empty);
                    break;
                case VariableSegment variable:
                    usedClipboard |= variable.Variable == BuiltinVariable.Clipboard;
                    usedSelection |= variable.Variable == BuiltinVariable.Selection;
                    target.Append(Resolve(variable, variables, culture));
                    break;
                case CursorSegment:
                    cursorSeen = true;
                    break;
            }
        }

        var head = NormalizeLineEndings(before.ToString());
        var tail = NormalizeLineEndings(after.ToString());
        var caretOffset = cursorSeen ? new StringInfo(tail).LengthInTextElements : 0;

        return new RenderedTemplate(head + tail, caretOffset, usedClipboard, usedSelection);
    }

    private static string Resolve(VariableSegment variable, IVariableSource source, CultureInfo culture) => variable.Variable switch
    {
        BuiltinVariable.Date => source.Now.ToString(variable.Argument ?? culture.DateTimeFormat.ShortDatePattern, culture),
        BuiltinVariable.Time => source.Now.ToString(variable.Argument ?? DefaultTimeFormat, culture),
        BuiltinVariable.Clipboard => source.GetClipboardText() ?? string.Empty,
        BuiltinVariable.Selection => source.GetSelectionText() ?? string.Empty,
        _ => string.Empty,
    };

    private static string NormalizeLineEndings(string text) =>
        text.ReplaceLineEndings("\r\n");
}

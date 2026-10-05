using System.Text;
using System.Text.RegularExpressions;

namespace TextFlow.Core.Templates;

/// <summary>
/// Parses <c>{{name}}</c>, <c>{{name=default}}</c>, <c>{{date:format}}</c> placeholders.
/// Anything that is not a well-formed placeholder is kept as literal text; <c>\{{</c> escapes braces.
/// </summary>
public static partial class TemplateParser
{
    private const string Open = "{{";
    private const string Close = "}}";
    private const string EscapedOpen = @"\{{";

    private static readonly Dictionary<string, BuiltinVariable> Builtins = new(StringComparer.OrdinalIgnoreCase)
    {
        ["date"] = BuiltinVariable.Date,
        ["time"] = BuiltinVariable.Time,
        ["clipboard"] = BuiltinVariable.Clipboard,
        ["selection"] = BuiltinVariable.Selection,
    };

    private const string CursorName = "cursor";

    [GeneratedRegex(@"^[\p{L}\p{N}_\-.]+$")]
    private static partial Regex NamePattern();

    /// <summary>
    /// Whether <paramref name="name"/> can name a field the user fills in: letters, digits, <c>_ - .</c>, and not a
    /// built-in variable or <c>cursor</c> (those never ask anything).
    /// </summary>
    public static bool IsValidFieldName(string name) =>
        NamePattern().IsMatch(name) && !Builtins.ContainsKey(name) && !name.Equals(CursorName, StringComparison.OrdinalIgnoreCase);

    public static ParsedTemplate Parse(string template)
    {
        ArgumentNullException.ThrowIfNull(template);

        var segments = new List<TemplateSegment>();
        var fields = new List<TemplateField>();
        var literal = new StringBuilder();
        var i = 0;

        while (i < template.Length)
        {
            if (string.CompareOrdinal(template, i, EscapedOpen, 0, EscapedOpen.Length) == 0)
            {
                literal.Append(Open);
                i += EscapedOpen.Length;
                continue;
            }

            if (string.CompareOrdinal(template, i, Open, 0, Open.Length) == 0
                && TryReadPlaceholder(template, i, out var segment, out var consumed))
            {
                FlushLiteral(literal, segments);
                segments.Add(segment);
                if (segment is FieldSegment field && !fields.Exists(f => f.Name == field.Name))
                {
                    fields.Add(new TemplateField(field.Name, field.DefaultValue));
                }

                i += consumed;
                continue;
            }

            literal.Append(template[i]);
            i++;
        }

        FlushLiteral(literal, segments);
        return new ParsedTemplate(segments, fields);
    }

    private static bool TryReadPlaceholder(string template, int start, out TemplateSegment segment, out int consumed)
    {
        segment = null!;
        consumed = 0;

        var innerStart = start + Open.Length;
        var closeIndex = template.IndexOf(Close, innerStart, StringComparison.Ordinal);
        if (closeIndex < 0)
        {
            return false;
        }

        var inner = template[innerStart..closeIndex];
        if (inner.Contains(Open, StringComparison.Ordinal))
        {
            return false; // let the scanner retry at the inner "{{"
        }

        if (!TryCreateSegment(inner.Trim(), out segment))
        {
            return false;
        }

        consumed = closeIndex + Close.Length - start;
        return true;
    }

    private static bool TryCreateSegment(string inner, out TemplateSegment segment)
    {
        segment = null!;
        var separator = inner.IndexOfAny([':', '=']);
        var name = (separator < 0 ? inner : inner[..separator]).Trim();
        if (name.Length == 0 || !NamePattern().IsMatch(name))
        {
            return false;
        }

        var argument = separator < 0 ? null : inner[(separator + 1)..];
        var isDefault = separator >= 0 && inner[separator] == '=';

        if (name.Equals(CursorName, StringComparison.OrdinalIgnoreCase))
        {
            segment = new CursorSegment();
            return true;
        }

        if (Builtins.TryGetValue(name, out var variable))
        {
            segment = new VariableSegment(variable, isDefault ? null : argument);
            return true;
        }

        segment = new FieldSegment(name, isDefault ? argument : null);
        return true;
    }

    private static void FlushLiteral(StringBuilder literal, List<TemplateSegment> segments)
    {
        if (literal.Length == 0)
        {
            return;
        }

        segments.Add(new LiteralSegment(literal.ToString()));
        literal.Clear();
    }
}

using System.Globalization;
using TextFlow.Core.Templates;

namespace TextFlow.Core.Tests.Templates;

public class TemplateEngineTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 1, 14, 5, 9, TimeSpan.FromHours(-5));

    private static FakeVariableSource Vars(string? clipboard = null, string? selection = null) =>
        new(FixedNow, clipboard, selection);

    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");

    private static RenderedTemplate Render(string template, IReadOnlyDictionary<string, string>? fields = null, FakeVariableSource? vars = null) =>
        TemplateRenderer.Render(TemplateParser.Parse(template), fields ?? new Dictionary<string, string>(), vars ?? Vars(), Es);

    [Fact]
    public void Parse_PlainText_ReturnsSingleLiteralAndNoFields()
    {
        var parsed = TemplateParser.Parse("Saludos cordiales");

        Assert.Equal([new LiteralSegment("Saludos cordiales")], parsed.Segments);
        Assert.Empty(parsed.FieldNames);
        Assert.False(parsed.RequiresInput);
    }

    [Fact]
    public void Parse_CollectsDistinctFieldNamesInOrderOfAppearance()
    {
        var parsed = TemplateParser.Parse("Hola {{cliente}}, pedido {{pedido}} de {{cliente}} — {{date}}");

        Assert.Equal(["cliente", "pedido"], parsed.FieldNames);
        Assert.True(parsed.RequiresInput);
    }

    [Fact]
    public void Parse_BuiltinNamesAreVariablesNotFields()
    {
        var parsed = TemplateParser.Parse("{{date}} {{time}} {{clipboard}} {{selection}} {{cursor}}");

        Assert.Empty(parsed.FieldNames);
        Assert.Contains(parsed.Segments, s => s is CursorSegment);
    }

    [Fact]
    public void Parse_BuiltinNamesAreCaseInsensitive()
    {
        var parsed = TemplateParser.Parse("{{DATE}} {{Cursor}}");

        Assert.Empty(parsed.FieldNames);
    }

    [Fact]
    public void Parse_FieldWithDefault_ExposesDefault()
    {
        var parsed = TemplateParser.Parse("{{empresa=ACME S.L.}}");

        var field = Assert.Single(parsed.Fields);
        Assert.Equal("empresa", field.Name);
        Assert.Equal("ACME S.L.", field.DefaultValue);
    }

    [Theory]
    [InlineData("precio {{ sin cerrar")]
    [InlineData("llaves }} sueltas")]
    [InlineData("vacío {{}} aquí")]
    [InlineData("espacios {{   }} aquí")]
    public void Render_MalformedPlaceholders_ArePreservedLiterally(string template)
    {
        Assert.Equal(template, Render(template).Text);
    }

    [Fact]
    public void Render_EscapedBraces_ProduceLiteralBraces()
    {
        Assert.Equal("uso {{cliente}} literal", Render(@"uso \{{cliente}} literal").Text);
    }

    [Fact]
    public void Render_ReplacesFieldsWithProvidedValues()
    {
        var result = Render("Hola {{cliente}}, su pedido {{pedido}}.", new Dictionary<string, string>
        {
            ["cliente"] = "Ana",
            ["pedido"] = "#4521",
        });

        Assert.Equal("Hola Ana, su pedido #4521.", result.Text);
    }

    [Fact]
    public void Render_MissingField_UsesDefaultThenEmpty_NeverLeavesMarker()
    {
        var result = Render("A={{a=uno}} B={{b}} C");

        Assert.Equal("A=uno B= C", result.Text);
        Assert.DoesNotContain("{{", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_FieldValueContainingBraces_IsNotReinterpreted()
    {
        var result = Render("{{x}}", new Dictionary<string, string> { ["x"] = "{{date}}" });

        Assert.Equal("{{date}}", result.Text);
    }

    [Fact]
    public void Render_DateAndTime_UseCultureByDefault()
    {
        Assert.Equal($"{FixedNow.ToString("d", Es)} 14:05", Render("{{date}} {{time}}").Text);
    }

    [Fact]
    public void Render_DateWithFormatArgument()
    {
        Assert.Equal("2026-10-01", Render("{{date:yyyy-MM-dd}}").Text);
    }

    [Fact]
    public void Render_ClipboardAndSelection_ComeFromSource_EmptyWhenUnavailable()
    {
        Assert.Equal("[clip|sel]", Render("[{{clipboard}}|{{selection}}]", vars: Vars("clip", "sel")).Text);
        Assert.Equal("[|]", Render("[{{clipboard}}|{{selection}}]").Text);
    }

    [Fact]
    public void Render_Cursor_ReportsCaretOffsetFromEnd_AndIsRemovedFromText()
    {
        var result = Render("Hola {{cursor}}, adiós");

        Assert.Equal("Hola , adiós", result.Text);
        Assert.Equal(", adiós".Length, result.CaretOffsetFromEnd);
    }

    [Fact]
    public void Render_WithoutCursor_CaretOffsetIsZero()
    {
        Assert.Equal(0, Render("texto").CaretOffsetFromEnd);
    }

    [Fact]
    public void Render_OnlyFirstCursorCounts()
    {
        var result = Render("a{{cursor}}b{{cursor}}c");

        Assert.Equal("abc", result.Text);
        Assert.Equal(2, result.CaretOffsetFromEnd);
    }

    [Fact]
    public void Render_CaretOffset_CountsTextElementsNotUtf16Units()
    {
        // An emoji is 2 UTF-16 units but one caret step (one Left arrow press).
        var result = Render("{{cursor}}👍é");

        Assert.Equal(2, result.CaretOffsetFromEnd);
    }

    [Fact]
    public void Render_NormalizesLineEndingsToCrLf()
    {
        Assert.Equal("a\r\nb\r\nc", Render("a\nb\r\nc").Text);
    }

    [Fact]
    public void Render_ReportsWhichVariablesWereUsed()
    {
        var result = Render("{{clipboard}} {{date}}");

        Assert.True(result.UsedClipboard);
        Assert.False(result.UsedSelection);
    }

    private sealed record FakeVariableSource(DateTimeOffset Now, string? Clipboard, string? Selection) : IVariableSource
    {
        public string? GetClipboardText() => Clipboard;

        public string? GetSelectionText() => Selection;
    }
}

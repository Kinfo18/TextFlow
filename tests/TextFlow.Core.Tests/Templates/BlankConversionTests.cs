using TextFlow.Core.Library;
using TextFlow.Core.Templates;

namespace TextFlow.Core.Tests.Templates;

public class BlankConversionTests
{
    [Fact]
    public void Count_FindsXxxBlanks_InAnyCase_ButNotInsideWords()
    {
        Assert.Equal(3, BlankConversion.Count("Hola, XXX. Pedido xxx de XxXx; talla XXXL, xx"));
    }

    [Fact]
    public void Apply_ReplacesEachBlankInOrder_WithItsField()
    {
        var converted = BlankConversion.Apply("XXX: XXX/XXX por XXX", ["cliente", "mes", "año", "monto"]);

        Assert.Equal("{{cliente}}: {{mes}}/{{año}} por {{monto}}", converted);
    }

    [Fact]
    public void Apply_KeepsABlank_WhoseNameIsEmpty()
    {
        Assert.Equal("{{cliente}} y XXX", BlankConversion.Apply("XXX y XXX", ["cliente", " "]));
    }

    [Fact]
    public void Apply_TheSameNameTwice_MeansTheSameValue()
    {
        var converted = BlankConversion.Apply("XXX, XXX", ["cliente", "cliente"]);

        Assert.Equal(["cliente"], TemplateParser.Parse(converted).FieldNames);
    }

    [Fact]
    public void Apply_RejectsAWrongNumberOfNames()
    {
        Assert.Throws<ArgumentException>(() => BlankConversion.Apply("XXX y XXX", ["cliente"]));
    }

    [Theory]
    [InlineData("cliente", true)]
    [InlineData("año", true)]
    [InlineData("numero_pedido", true)]
    [InlineData("monto total", false)] // spaces are not allowed in a field name
    [InlineData("date", false)]        // built-in variables would not ask anything
    [InlineData("Cursor", false)]
    [InlineData("a}b", false)]
    public void IsValidFieldName(string name, bool expected)
    {
        Assert.Equal(expected, TemplateParser.IsValidFieldName(name));
    }

    [Fact]
    public void Candidates_ListsSnippetsWithBlanks_WithTheirGroup()
    {
        var root = new LibraryGroup("root", "root", null, true,
            [
                new LibraryGroup("g1", "Saludos", null, true, [],
                [
                    new LibrarySnippet("s1", "s1", "Hola, XXX", false, ["s1"]),
                    new LibrarySnippet("s2", "s2", "Sin huecos", false, ["s2"]),
                ]),
                new LibraryGroup("g2", "Facturas", null, true, [], [new LibrarySnippet("f1", "frec1", "XXX XXX", false, ["frec1"])]),
            ],
            []);

        var candidates = BlankConversion.Candidates(root);

        Assert.Equal([("g1", "s1", 1), ("g2", "f1", 2)], candidates.Select(c => (c.GroupId, c.Snippet.Id, c.Blanks)));
    }
}

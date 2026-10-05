using System.Text.RegularExpressions;
using TextFlow.Core.Library;

namespace TextFlow.Core.Templates;

/// <summary>A snippet that still uses hand-filled <c>XXX</c> blanks, and where it lives.</summary>
public sealed record BlankCandidate(string GroupId, LibrarySnippet Snippet, int Blanks);

/// <summary>
/// Turns hand-filled <c>XXX</c> blanks into template fields (H5.2 part 2): the user names each blank in order and the
/// blank becomes <c>{{name}}</c>. A blank whose name is left empty stays as it was.
/// </summary>
public static partial class BlankConversion
{
    /// <summary>Three or more x/X standing alone: "XXX", "xxxx"; not "XXXL" or "xx".</summary>
    [GeneratedRegex(@"(?<![\p{L}\p{N}])[xX]{3,}(?![\p{L}\p{N}])")]
    private static partial Regex Blank();

    public static int Count(string content) => Blank().Count(content);

    /// <summary>The text around the blanks: <c>Count + 1</c> parts (for showing where each blank is).</summary>
    public static string[] Split(string content) => Blank().Split(content);

    /// <param name="names">One per blank, in order of appearance; empty keeps that blank.</param>
    public static string Apply(string content, IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count != Count(content))
        {
            throw new ArgumentException($"Expected {Count(content)} names, got {names.Count}.", nameof(names));
        }

        var index = 0;
        return Blank().Replace(content, match =>
        {
            var name = names[index++].Trim();
            return name.Length == 0 ? match.Value : "{{" + name + "}}";
        });
    }

    /// <summary>Every snippet with at least one blank, in library order.</summary>
    public static IReadOnlyList<BlankCandidate> Candidates(LibraryGroup root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var found = new List<BlankCandidate>();
        Collect(root, found);
        return found;
    }

    private static void Collect(LibraryGroup group, List<BlankCandidate> found)
    {
        foreach (var snippet in group.Snippets)
        {
            var blanks = Count(snippet.Content);
            if (blanks > 0)
            {
                found.Add(new BlankCandidate(group.Id, snippet, blanks));
            }
        }

        foreach (var child in group.Groups)
        {
            Collect(child, found);
        }
    }
}

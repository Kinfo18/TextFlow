using TextFlow.Core.Expansion;
using TextFlow.Core.Import;
using TextFlow.Core.Library;

namespace TextFlow.Core.Menus;

/// <summary>
/// Turns a group tree into the triggers the hook listens for:
/// <list type="bullet">
/// <item>group abbreviations open a <see cref="GroupMenu"/>; groups whose abbreviations collide (ignoring case
/// when either group does) share one menu listing each group, as aText does for the user's "OD"/"FP";</item>
/// <item>typeable snippet abbreviations ("cc", "s1", "orca3") expand directly, inheriting the case setting of
/// their group. A snippet abbreviation that collides with an earlier trigger is skipped (menus win), and so is
/// one too long to type; both remain menu labels.</item>
/// </list>
/// </summary>
public sealed class LibraryIndex
{
    private const string MenuPrefix = "menu:";
    private const string SnippetPrefix = "snippet:";

    /// <summary>Longer abbreviations (the user's library has labels up to 95 chars) cannot be typed into the matcher buffer.</summary>
    private static readonly int MaxTriggerLength = TriggerOptions.Default.MaxBufferLength;

    private readonly Dictionary<string, GroupMenu> _menus;
    private readonly Dictionary<string, MenuSnippetEntry> _snippets;

    private LibraryIndex(IReadOnlyList<GroupMenu> menus, Dictionary<string, MenuSnippetEntry> snippets, IReadOnlyList<TriggerDefinition> triggers)
    {
        Menus = menus;
        _menus = menus.ToDictionary(m => m.Id, StringComparer.Ordinal);
        _snippets = snippets;
        Triggers = triggers;
    }

    public IReadOnlyList<GroupMenu> Menus { get; }

    public IReadOnlyList<TriggerDefinition> Triggers { get; }

    public static LibraryIndex Build(LibraryGroup root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var groups = Flatten(root).ToArray();
        var menus = BuildMenus(groups.Where(g => g.Abbreviation is not null));
        var triggers = menus.Select(m => new TriggerDefinition(m.Id, m.Trigger, TriggerMode.Immediate, m.IgnoreCase)).ToList();
        var snippets = new Dictionary<string, MenuSnippetEntry>(StringComparer.Ordinal);

        foreach (var group in groups)
        {
            foreach (var snippet in group.Snippets.Where(s => !s.IsInfoOnly && s.Enabled))
            {
                var mode = snippet.Mode == SnippetMode.AfterDelimiter ? TriggerMode.AfterDelimiter : TriggerMode.Immediate;
                foreach (var abbreviation in snippet.TypeableAbbreviations.Where(a => Typeable(a, mode)))
                {
                    var candidate = new TriggerDefinition($"{SnippetPrefix}{snippets.Count}", abbreviation, mode, group.IgnoreCase);
                    if (triggers.Any(t => Collides(t.Trigger, t.IgnoreCase, candidate.Trigger, candidate.IgnoreCase)))
                    {
                        continue;
                    }

                    snippets[candidate.SnippetId] = new MenuSnippetEntry(snippet.Name, snippet.Content, snippet.SendEnter, snippet.Id);
                    triggers.Add(candidate);
                }
            }
        }

        return new LibraryIndex(menus, snippets, triggers);
    }

    /// <summary>Fits the matcher buffer; an after-delimiter abbreviation cannot itself contain a delimiter ("Foto valida").</summary>
    private static bool Typeable(string abbreviation, TriggerMode mode) =>
        abbreviation.Length < MaxTriggerLength
        && (mode == TriggerMode.Immediate || !abbreviation.Any(TriggerOptions.Default.Delimiters.Contains));

    public GroupMenu? FindMenu(string triggerId) => _menus.GetValueOrDefault(triggerId);

    public MenuSnippetEntry? FindSnippet(string triggerId) => _snippets.GetValueOrDefault(triggerId);

    private static GroupMenu[] BuildMenus(IEnumerable<LibraryGroup> abbreviated)
    {
        var buckets = new List<List<LibraryGroup>>();
        foreach (var group in abbreviated)
        {
            var bucket = buckets.FirstOrDefault(b => Collides(b[0].Abbreviation!, b[0].IgnoreCase, group.Abbreviation!, group.IgnoreCase));
            if (bucket is null)
            {
                buckets.Add([group]);
            }
            else
            {
                bucket.Add(group);
            }
        }

        return buckets.Select((groups, i) => ToMenu($"{MenuPrefix}{i}", groups)).ToArray();
    }

    private static bool Collides(string a, bool aIgnoresCase, string b, bool bIgnoresCase) =>
        string.Equals(a, b, aIgnoresCase || bIgnoresCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static GroupMenu ToMenu(string id, List<LibraryGroup> groups)
    {
        var first = groups[0];
        var entries = groups.Count == 1
            ? Children(first)
            : groups.Select(g => (MenuEntry)new MenuGroupEntry(g.Name, Children(g))).ToArray();

        return new GroupMenu(id, first.Abbreviation!, groups.Any(g => g.IgnoreCase), entries);
    }

    private static MenuEntry[] Children(LibraryGroup group) =>
    [
        .. group.Groups.Select(g => new MenuGroupEntry(g.Name, Children(g))),
        .. group.Snippets.Select(s => new MenuSnippetEntry(s.Name, s.Content, s.SendEnter, s.Id)),
    ];

    private static IEnumerable<LibraryGroup> Flatten(LibraryGroup group) => group.Groups.SelectMany(Flatten).Prepend(group);
}

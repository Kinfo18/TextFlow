using TextFlow.Core.Expansion;
using TextFlow.Core.Import;

namespace TextFlow.Core.Menus;

/// <summary>
/// Turns a group tree into the menus opened by group abbreviations, and the triggers that open them.
/// Groups whose abbreviations collide (ignoring case when either group does) share one menu that lists
/// each group as an entry, as aText does for the user's duplicated "OD"/"FP".
/// </summary>
public sealed class GroupMenuIndex
{
    private const string IdPrefix = "menu:";

    private readonly Dictionary<string, GroupMenu> _byId;

    private GroupMenuIndex(IReadOnlyList<GroupMenu> menus)
    {
        Menus = menus;
        _byId = menus.ToDictionary(m => m.Id, StringComparer.Ordinal);
        Triggers = menus.Select(m => new TriggerDefinition(m.Id, m.Trigger, TriggerMode.Immediate, m.IgnoreCase)).ToArray();
    }

    public IReadOnlyList<GroupMenu> Menus { get; }

    public IReadOnlyList<TriggerDefinition> Triggers { get; }

    public static GroupMenuIndex Build(ImportedGroup root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var buckets = new List<List<ImportedGroup>>();
        foreach (var group in Flatten(root).Where(g => g.Abbreviation is not null))
        {
            var bucket = buckets.FirstOrDefault(b => Collides(b[0], group));
            if (bucket is null)
            {
                buckets.Add([group]);
            }
            else
            {
                bucket.Add(group);
            }
        }

        return new GroupMenuIndex(buckets.Select((groups, i) => ToMenu($"{IdPrefix}{i}", groups)).ToArray());
    }

    public GroupMenu? Find(string triggerId) => _byId.GetValueOrDefault(triggerId);

    private static bool Collides(ImportedGroup a, ImportedGroup b) => string.Equals(
        a.Abbreviation,
        b.Abbreviation,
        a.IgnoreCase || b.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static GroupMenu ToMenu(string id, List<ImportedGroup> groups)
    {
        var first = groups[0];
        var entries = groups.Count == 1
            ? Children(first)
            : groups.Select(g => (MenuEntry)new MenuGroupEntry(g.Name, Children(g))).ToArray();

        return new GroupMenu(id, first.Abbreviation!, groups.Any(g => g.IgnoreCase), entries);
    }

    private static MenuEntry[] Children(ImportedGroup group) =>
    [
        .. group.Groups.Select(g => new MenuGroupEntry(g.Name, Children(g))),
        .. group.Snippets.Select(s => new MenuSnippetEntry(s.Name, s.Content)),
    ];

    private static IEnumerable<ImportedGroup> Flatten(ImportedGroup group) => group.Groups.SelectMany(Flatten).Prepend(group);
}

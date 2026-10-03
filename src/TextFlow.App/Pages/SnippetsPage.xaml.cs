using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TextFlow.Core.Library;

namespace TextFlow.App.Pages;

/// <summary>
/// Snippets (H3.2): group tree (drag a group onto another to move it), the selected group's snippets or the search
/// results, and the snippet's details. Editing the snippet itself arrives in H3.3.
/// </summary>
public sealed partial class SnippetsPage : Page, IRefreshable
{
    private const int PreviewLength = 70;

    private readonly Dictionary<TreeViewNode, LibraryGroup> _groups = [];
    private string? _selectedGroupId;
    private string? _selectedSnippetId;

    public SnippetsPage()
    {
        InitializeComponent();
        Refresh();
    }

    private static LibraryGroup Library =>
        App.Current.Library?.Service.Current ?? new LibraryGroup("root", "Biblioteca", null, true, [], []);

    /// <summary>Rebuilds from the stored library (after imports and edits), keeping expansion and selection.</summary>
    public void Refresh()
    {
        var expanded = _groups.Where(pair => pair.Key.IsExpanded).Select(pair => pair.Value.Id).ToHashSet(StringComparer.Ordinal);
        GroupTree.RootNodes.Clear();
        _groups.Clear();

        var root = Library;
        var rootNode = Node(root, expanded, isRoot: true);
        GroupTree.RootNodes.Add(rootNode);

        var selected = _groups.FirstOrDefault(pair => pair.Value.Id == _selectedGroupId).Key ?? rootNode;
        GroupTree.SelectedNode = selected;

        if (SearchBox.Text.Trim().Length > 0)
        {
            ShowResults(SearchBox.Text);
        }
        else
        {
            ShowGroup(_groups[selected]);
        }
    }

    private TreeViewNode Node(LibraryGroup group, HashSet<string> expanded, bool isRoot = false)
    {
        var label = isRoot ? $"{group.Name}" : group.Abbreviation is { } abbreviation ? $"{group.Name}   ·  {abbreviation}" : group.Name;
        var node = new TreeViewNode { Content = label, IsExpanded = isRoot || expanded.Contains(group.Id) };
        _groups[node] = group;
        foreach (var child in group.Groups)
        {
            node.Children.Add(Node(child, expanded));
        }

        return node;
    }

    private void OnGroupInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is TreeViewNode node && _groups.TryGetValue(node, out var group))
        {
            _selectedGroupId = group.Id;
            SearchBox.Text = string.Empty;
            ShowGroup(group);
        }
    }

    private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (sender.Text.Trim().Length > 0)
        {
            ShowResults(sender.Text);
        }
        else if (GroupTree.SelectedNode is { } node && _groups.TryGetValue(node, out var group))
        {
            ShowGroup(group);
        }
    }

    private void ShowGroup(LibraryGroup group)
    {
        ListTitle.Text = group.Name;
        var count = group.Snippets.Count == 1 ? "1 comando" : $"{group.Snippets.Count} comandos";
        ListSubtitle.Text = group.Abbreviation is { } abbreviation ? $"{count} · se abre con {abbreviation}" : count;
        Fill(group.Snippets.Select(s => (s, group.Id, (string?)null)));
    }

    private void ShowResults(string query)
    {
        var hits = LibrarySearch.Find(Library, query);
        ListTitle.Text = "Resultados";
        ListSubtitle.Text = hits.Count switch
        {
            0 => "Nada coincide con la búsqueda",
            1 => "1 comando",
            _ => $"{hits.Count} comandos",
        };
        Fill(hits.Select(h => (h.Snippet, h.GroupId, (string?)string.Join(" › ", h.GroupPath))));
    }

    private void Fill(IEnumerable<(LibrarySnippet Snippet, string GroupId, string? Path)> rows)
    {
        SnippetList.Items.Clear();
        ListViewItem? reselect = null;
        foreach (var (snippet, groupId, path) in rows)
        {
            var item = Row(snippet, path);
            item.Tag = (snippet, groupId, path);
            SnippetList.Items.Add(item);
            if (snippet.Id == _selectedSnippetId)
            {
                reselect = item;
            }
        }

        SnippetList.SelectedItem = reselect;
        if (reselect is null)
        {
            ShowDetails(null);
        }
    }

    private static ListViewItem Row(LibrarySnippet snippet, string? path)
    {
        var abbreviation = new TextBlock
        {
            Text = snippet.Abbreviations.Count > 0 ? snippet.Abbreviations[0] : string.Empty,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var secondLine = new TextBlock
        {
            Text = path ?? Preview(snippet),
            Opacity = 0.65,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var stack = new StackPanel { Padding = new Thickness(4, 6, 4, 6), Opacity = snippet.Enabled ? 1 : 0.5 };
        stack.Children.Add(abbreviation);
        stack.Children.Add(secondLine);
        return new ListViewItem { Content = stack };
    }

    private static string Preview(LibrarySnippet snippet)
    {
        if (snippet.IsInfoOnly)
        {
            return "Nota informativa";
        }

        var oneLine = snippet.Content.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= PreviewLength ? oneLine : string.Concat(oneLine.AsSpan(0, PreviewLength), "…");
    }

    private void OnSnippetSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SnippetList.SelectedItem is ListViewItem { Tag: ValueTuple<LibrarySnippet, string, string?> row })
        {
            _selectedSnippetId = row.Item1.Id;
            ShowDetails(row);
        }
    }

    private void ShowDetails((LibrarySnippet Snippet, string GroupId, string? Path)? row)
    {
        DetailAbbreviations.Children.Clear();
        if (row is not { } selected)
        {
            DetailTitle.Text = "Elige un comando";
            DetailPath.Text = "Selecciona un grupo a la izquierda o busca por abreviatura, nombre o texto.";
            DetailFlags.Text = string.Empty;
            DetailContent.Text = string.Empty;
            return;
        }

        var snippet = selected.Snippet;
        DetailTitle.Text = snippet.Name;
        DetailPath.Text = selected.Path ?? GroupPathOf(selected.GroupId);
        foreach (var abbreviation in snippet.Abbreviations)
        {
            DetailAbbreviations.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 2, 8, 2),
                Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
                Child = new TextBlock
                {
                    Text = abbreviation,
                    Foreground = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"],
                    FontWeight = FontWeights.SemiBold,
                },
            });
        }

        var flags = new List<string> { snippet.Mode == SnippetMode.AfterDelimiter ? "Se expande tras un espacio o signo" : "Se expande al escribirla" };
        if (!snippet.Enabled)
        {
            flags.Add("desactivado");
        }

        if (snippet.IsRichText)
        {
            flags.Add("tenía formato en aText (se inserta como texto)");
        }

        DetailFlags.Text = string.Join(" · ", flags);
        DetailContent.Text = snippet.IsInfoOnly ? "(Nota informativa: se muestra en el menú y no inserta texto)" : snippet.Content;
    }

    private static string GroupPathOf(string groupId)
    {
        static IEnumerable<string>? Find(LibraryGroup group, string id, IEnumerable<string> path) =>
            group.Id == id
                ? path
                : group.Groups.Select(child => Find(child, id, path.Append(child.Name))).FirstOrDefault(found => found is not null);

        return string.Join(" › ", Find(Library, groupId, []) ?? []);
    }

    /// <summary>A group dropped under another one moves there (reordering among siblings is not stored yet).</summary>
    /// <remarks>async void: every failure is caught and shown; an escaping exception freezes WinUI.</remarks>
    private async void OnGroupDragged(TreeView sender, TreeViewDragItemsCompletedEventArgs args)
    {
        try
        {
            var parents = ParentIds(Library);
            var newParent = args.NewParentItem is TreeViewNode node && _groups.TryGetValue(node, out var target) ? target : Library;
            foreach (var moved in args.Items.OfType<TreeViewNode>().Select(n => _groups.GetValueOrDefault(n)).OfType<LibraryGroup>())
            {
                if (parents.GetValueOrDefault(moved.Id) == newParent.Id || moved.Id == Library.Id)
                {
                    continue;
                }

                await App.Current.EditLibraryAsync(XamlRoot, service => service.SaveGroupAsync(
                    new GroupInfo(moved.Id, newParent.Id, moved.Name, moved.Abbreviation, moved.IgnoreCase), CancellationToken.None));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            App.Current.RecordFault(ex);
            ListSubtitle.Text = $"No se pudo mover el grupo: {ex.Message}";
        }
        finally
        {
            Refresh(); // the tree shows what was stored (a cancelled move snaps back)
        }
    }

    private static Dictionary<string, string> ParentIds(LibraryGroup root)
    {
        var parents = new Dictionary<string, string>(StringComparer.Ordinal);
        void Walk(LibraryGroup group)
        {
            foreach (var child in group.Groups)
            {
                parents[child.Id] = group.Id;
                Walk(child);
            }
        }

        Walk(root);
        return parents;
    }
}

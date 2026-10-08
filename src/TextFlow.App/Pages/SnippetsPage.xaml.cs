using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TextFlow.Core.Library;

namespace TextFlow.App.Pages;

/// <summary>
/// Snippets (H3.2–H3.3): group tree (drag a group onto another to move it), the selected group's commands or the
/// search results, and the command editor. Unsaved edits survive refreshes (pause, imports) and are never dropped
/// without asking.
/// </summary>
public sealed partial class SnippetsPage : Page, IRefreshable
{
    private const int PreviewLength = 70;

    private readonly Dictionary<TreeViewNode, LibraryGroup> _groups = [];
    private string? _selectedGroupId;
    private string? _selectedSnippetId;

    // Editor state.
    private SnippetDraft? _draft;
    private string? _draftGroupId;
    private string _lineEnding = "\n";
    private bool _isNew;
    private bool _dirty;
    private bool _loadingEditor;
    private bool _revertingSelection;

    public SnippetsPage()
    {
        InitializeComponent();
        Refresh();
    }

    private static LibraryGroup Library =>
        App.Current.Library?.Service.Current ?? new LibraryGroup("root", "Biblioteca", null, true, [], []);

    /// <summary>Rebuilds from the stored library (after imports and edits), keeping expansion, selection and unsaved edits.</summary>
    public void Refresh()
    {
        var expanded = _groups.Where(pair => pair.Key.IsExpanded).Select(pair => pair.Value.Id).ToHashSet(StringComparer.Ordinal);
        GroupTree.RootNodes.Clear();
        _groups.Clear();

        UpdateBlankHint();
        var rootNode = Node(Library, expanded, isRoot: true);
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
        var label = isRoot ? group.Name : group.Abbreviation is { } abbreviation ? $"{group.Name}   ·  {abbreviation}" : group.Name;
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
        var isRoot = group.Id == Library.Id;
        EditGroupItem.IsEnabled = !isRoot;
        DeleteGroupItem.IsEnabled = !isRoot;
        GroupButton.IsEnabled = true;
        ListTitle.Text = group.Name;
        var count = group.Snippets.Count == 1 ? "1 comando" : $"{group.Snippets.Count} comandos";
        ListSubtitle.Text = group.Abbreviation is { } abbreviation ? $"{count} · se abre con {abbreviation}" : count;
        NewSnippetButton.IsEnabled = true;
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
        NewSnippetButton.IsEnabled = false; // a new command needs a group: pick one in the tree
        GroupButton.IsEnabled = false;
        Fill(hits.Select(h => (h.Snippet, h.GroupId, (string?)string.Join(" › ", h.GroupPath))));
    }

    private void Fill(IEnumerable<(LibrarySnippet Snippet, string GroupId, string? Path)> rows)
    {
        _revertingSelection = true; // rebuilding the list is not a user selection
        try
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
        }
        finally
        {
            _revertingSelection = false;
        }

        if (_dirty)
        {
            return; // never overwrite unsaved edits
        }

        if (SnippetList.SelectedItem is ListViewItem { Tag: ValueTuple<LibrarySnippet, string, string?> row })
        {
            LoadEditor(SnippetDraft.From(row.Item1), row.Item2, row.Item3, isNew: false);
        }
        else
        {
            ShowEmptyEditor();
        }
    }

    private static ListViewItem Row(LibrarySnippet snippet, string? path)
    {
        var abbreviation = new TextBlock
        {
            Text = snippet.Abbreviations.Count > 0 ? snippet.Abbreviations[0] : snippet.Name,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var secondLine = new TextBlock { Text = path ?? Preview(snippet), Opacity = 0.65, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
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

    /// <remarks>async void: every failure is caught; an escaping exception freezes WinUI.</remarks>
    private async void OnSnippetSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_revertingSelection || SnippetList.SelectedItem is not ListViewItem { Tag: ValueTuple<LibrarySnippet, string, string?> row })
        {
            return;
        }

        try
        {
            if (row.Item1.Id != _draft?.Id && !await ConfirmLeaveAsync())
            {
                ReselectCurrent();
                return;
            }

            _selectedSnippetId = row.Item1.Id;
            LoadEditor(SnippetDraft.From(row.Item1), row.Item2, row.Item3, isNew: false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            Fail(ex);
        }
    }

    private async void OnNewSnippet(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await ConfirmLeaveAsync())
            {
                return;
            }

            var groupId = GroupTree.SelectedNode is { } node && _groups.TryGetValue(node, out var group) ? group.Id : Library.Id;
            _selectedSnippetId = null;
            SnippetList.SelectedItem = null;
            LoadEditor(SnippetDraft.New(), groupId, GroupPathOf(groupId), isNew: true);
            MarkDirty();
            AbbreviationsBox.Focus(FocusState.Programmatic);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            Fail(ex);
        }
    }

    private void LoadEditor(SnippetDraft draft, string groupId, string? path, bool isNew)
    {
        _loadingEditor = true;
        try
        {
            _draft = draft;
            _draftGroupId = groupId;
            _isNew = isNew;
            _lineEnding = draft.Content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            DetailTitle.Text = isNew ? "Nuevo comando" : draft.Name;
            DetailPath.Text = path ?? GroupPathOf(groupId);
            AbbreviationsBox.Text = draft.AbbreviationsText;
            NameBox.Text = draft.Name;
            ContentBox.Text = draft.Content;
            ModeChoice.SelectedIndex = draft.Mode == SnippetMode.AfterDelimiter ? 1 : 0;
            EnabledSwitch.IsOn = draft.Enabled;
            SendEnterBox.IsChecked = draft.SendEnter;
            DetailFlags.Text = draft.IsRichText ? "En aText tenía formato; se inserta como texto." : string.Empty;
            EditorMessage.Text = string.Empty;
            DeleteButton.IsEnabled = !isNew;
            EmptyState.Visibility = Visibility.Collapsed;
            EditorScroll.Visibility = Visibility.Visible;
            EditorActions.Visibility = Visibility.Visible;
            SetDirty(false);
        }
        finally
        {
            _loadingEditor = false;
        }

        UpdateWarnings();
    }

    private void ShowEmptyEditor()
    {
        _draft = null;
        _draftGroupId = null;
        EmptyState.Visibility = Visibility.Visible;
        EditorScroll.Visibility = Visibility.Collapsed;
        EditorActions.Visibility = Visibility.Collapsed;
        SetDirty(false);
    }

    private void OnEdited(object sender, TextChangedEventArgs e) => MarkDirty();

    private void OnModeChanged(object sender, SelectionChangedEventArgs e) => MarkDirty();

    private void OnEnabledToggled(object sender, RoutedEventArgs e) => MarkDirty();

    private void OnSendEnterChanged(object sender, RoutedEventArgs e) => MarkDirty();

    private void MarkDirty()
    {
        if (_draft is not null)
        {
            UpdateWarnings();
        }

        if (!_loadingEditor && _draft is not null)
        {
            // TextChanged arrives after LoadEditor returns: compare with what was loaded instead of trusting the event.
            var changed = !CurrentDraft().HasSameEdits(_draft);
            SetDirty(changed);
            if (changed)
            {
                EditorMessage.Text = string.Empty;
            }
        }
    }

    /// <summary>H3.5: clashes, prefix waits and common words, live while typing (never blocks saving).</summary>
    private void UpdateWarnings()
    {
        if (_draft is null || _draftGroupId is not { } groupId)
        {
            return;
        }

        var draft = CurrentDraft();
        AbbreviationWarningsView.Render(
            AbbreviationWarnings, AbbreviationAdvisor.ForSnippet(Library, draft.Id, groupId, draft.Abbreviations, draft.Mode));
    }

    private void SetDirty(bool dirty)
    {
        _dirty = dirty;
        SaveButton.IsEnabled = dirty;
        DiscardButton.IsEnabled = dirty;
    }

    /// <summary>The form as a draft; the TextBox's "\r" line breaks go back to the snippet's own style.</summary>
    private SnippetDraft CurrentDraft() => _draft! with
    {
        AbbreviationsText = AbbreviationsBox.Text.Replace('\r', '\n'),
        Name = NameBox.Text,
        Content = ContentBox.Text.ReplaceLineEndings(_lineEnding),
        Mode = ModeChoice.SelectedIndex == 1 ? SnippetMode.AfterDelimiter : SnippetMode.Immediate,
        Enabled = EnabledSwitch.IsOn,
        SendEnter = SendEnterBox.IsChecked == true,
    };

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            Fail(ex);
        }
    }

    /// <returns>False when nothing was saved (invalid form or the user cancelled the first-edit question).</returns>
    private async Task<bool> SaveAsync()
    {
        if (_draft is null || _draftGroupId is not { } groupId)
        {
            return false;
        }

        var (snippet, errors) = CurrentDraft().ToSnippet();
        if (snippet is null)
        {
            EditorMessage.Text = string.Join(" ", errors.Select(Describe));
            return false;
        }

        if (!await App.Current.EditLibraryAsync(XamlRoot, service => service.SaveSnippetAsync(groupId, snippet, CancellationToken.None)))
        {
            return false;
        }

        _selectedSnippetId = snippet.Id;
        SetDirty(false);
        LoadEditor(SnippetDraft.From(snippet), groupId, DetailPath.Text, isNew: false);
        Refresh();
        return true;
    }

    private void OnDiscard(object sender, RoutedEventArgs e) => Discard();

    private void Discard()
    {
        SetDirty(false);
        if (_isNew || _draft is null)
        {
            ShowEmptyEditor();
        }
        else
        {
            Refresh(); // reloads the stored version
        }
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_draft is not { } draft || _isNew)
            {
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Eliminar comando",
                Content = $"Se eliminará «{draft.Name}» con sus abreviaturas. Antes de cada importación se guarda una copia, pero este borrado no.",
                PrimaryButtonText = "Eliminar",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            if (await App.Current.EditLibraryAsync(XamlRoot, service => service.DeleteSnippetAsync(draft.Id, CancellationToken.None)))
            {
                _selectedSnippetId = null;
                SetDirty(false);
                ShowEmptyEditor();
                Refresh();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            Fail(ex);
        }
    }

    /// <summary>Before leaving a modified command: save, discard or stay.</summary>
    internal async Task<bool> ConfirmLeaveAsync()
    {
        if (!_dirty)
        {
            return true;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Cambios sin guardar",
            Content = "Este comando tiene cambios sin guardar.",
            PrimaryButtonText = "Guardar",
            SecondaryButtonText = "Descartar",
            CloseButtonText = "Seguir editando",
            DefaultButton = ContentDialogButton.Primary,
        };

        switch (await dialog.ShowAsync())
        {
            case ContentDialogResult.Primary:
                return await SaveAsync();
            case ContentDialogResult.Secondary:
                SetDirty(false);
                return true;
            default:
                return false;
        }
    }

    private void ReselectCurrent()
    {
        _revertingSelection = true;
        try
        {
            SnippetList.SelectedItem = SnippetList.Items.OfType<ListViewItem>()
                .FirstOrDefault(i => i.Tag is ValueTuple<LibrarySnippet, string, string?> row && row.Item1.Id == _draft?.Id);
        }
        finally
        {
            _revertingSelection = false;
        }
    }

    private static string Describe(SnippetDraftError error) => error switch
    {
        SnippetDraftError.NeedsAbbreviationOrName => "Escribe al menos una abreviatura o un nombre.",
        SnippetDraftError.AbbreviationTooLong => $"Una abreviatura es demasiado larga (máximo {SnippetDraft.MaxAbbreviationLength} caracteres).",
        SnippetDraftError.DelimiterInAbbreviation => "Tras un espacio o signo, las abreviaturas no pueden tener espacios ni signos.",
        _ => "No se puede guardar.",
    };

    private void Fail(Exception ex)
    {
        App.Current.RecordFault(ex);
        EditorMessage.Text = $"No se pudo guardar: {ex.Message}";
    }

    private static string GroupPathOf(string groupId)
    {
        static IEnumerable<string>? Find(LibraryGroup group, string id, IEnumerable<string> path) =>
            group.Id == id
                ? path
                : group.Groups.Select(child => Find(child, id, path.Append(child.Name))).FirstOrDefault(found => found is not null);

        var path = Find(Library, groupId, []) ?? [];
        return path.Any() ? string.Join(" › ", path) : Library.Name;
    }

    private LibraryGroup SelectedGroup =>
        GroupTree.SelectedNode is { } node && _groups.TryGetValue(node, out var group) ? group : Library;

    /// <summary>Right-click on the tree: select the group under the pointer, then offer its actions.</summary>
    private void OnTreeContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs args)
    {
        var container = args.OriginalSource as DependencyObject;
        while (container is not null and not TreeViewItem)
        {
            container = VisualTreeHelper.GetParent(container);
        }

        if (container is TreeViewItem item && GroupTree.NodeFromContainer(item) is { } node && _groups.TryGetValue(node, out var group))
        {
            GroupTree.SelectedNode = node;
            _selectedGroupId = group.Id;
            SearchBox.Text = string.Empty;
            ShowGroup(group);
            if (args.TryGetPosition(item, out var point))
            {
                GroupActions.ShowAt(item, point);
            }
            else
            {
                GroupActions.ShowAt(item);
            }

            args.Handled = true;
        }
    }

    /// <remarks>async void: every failure is caught; an escaping exception freezes WinUI.</remarks>
    private async void OnNewGroup(object sender, RoutedEventArgs e)
    {
        try
        {
            var parent = SelectedGroup;
            if (await GroupDialog.EditAsync(XamlRoot, GroupDraft.New(parent.Id), isNew: true, Library) is { } info
                && await App.Current.EditLibraryAsync(XamlRoot, service => service.SaveGroupAsync(info, CancellationToken.None)))
            {
                _selectedGroupId = info.Id;
                Refresh();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            GroupFail(ex);
        }
    }

    private async void OnEditGroup(object sender, RoutedEventArgs e)
    {
        try
        {
            var group = SelectedGroup;
            if (group.Id == Library.Id || !ParentIds(Library).TryGetValue(group.Id, out var parentId))
            {
                return;
            }

            if (await GroupDialog.EditAsync(XamlRoot, GroupDraft.From(group, parentId), isNew: false, Library) is { } info
                && await App.Current.EditLibraryAsync(XamlRoot, service => service.SaveGroupAsync(info, CancellationToken.None)))
            {
                Refresh();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            GroupFail(ex);
        }
    }

    private async void OnDeleteGroup(object sender, RoutedEventArgs e)
    {
        try
        {
            var group = SelectedGroup;
            if (group.Id == Library.Id)
            {
                return;
            }

            var (subgroups, commands) = Count(group);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Eliminar grupo",
                Content = $"Se eliminará «{group.Name}» con {subgroups} subgrupos y {commands} comandos. Este borrado no se guarda como copia.",
                PrimaryButtonText = "Eliminar",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary
                && await App.Current.EditLibraryAsync(XamlRoot, service => service.DeleteGroupAsync(group.Id, CancellationToken.None)))
            {
                _selectedGroupId = ParentIds(Library).GetValueOrDefault(group.Id);
                _selectedSnippetId = null;
                SetDirty(false);
                Refresh();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            GroupFail(ex);
        }
    }

    private static (int Subgroups, int Commands) Count(LibraryGroup group) =>
        group.Groups.Select(Count).Aggregate((group.Groups.Count, group.Snippets.Count), (sum, child) => (sum.Item1 + child.Subgroups, sum.Item2 + child.Commands));

    private void GroupFail(Exception ex)
    {
        App.Current.RecordFault(ex);
        ListSubtitle.Text = $"No se pudo guardar el grupo: {ex.Message}";
    }

    /// <summary>
    /// A dropped group keeps exactly the place it was dropped at: moved under its new parent if that changed, and the
    /// new order among its siblings stored as shown.
    /// </summary>
    /// <remarks>async void: every failure is caught and shown; an escaping exception freezes WinUI.</remarks>
    private async void OnGroupDragged(TreeView sender, TreeViewDragItemsCompletedEventArgs args)
    {
        try
        {
            var root = Library;
            var parents = ParentIds(root);
            var parentNode = args.NewParentItem as TreeViewNode;
            var newParent = parentNode is not null && _groups.TryGetValue(parentNode, out var target) ? target : root;
            var moved = args.Items.OfType<TreeViewNode>()
                .Select(n => _groups.GetValueOrDefault(n))
                .OfType<LibraryGroup>()
                .Where(g => g.Id != root.Id)
                .ToArray();

            // Dropped beside the root node there is no visible order to keep: the group goes last under the root.
            var siblings = parentNode?.Children
                .Select(n => _groups.TryGetValue(n, out var group) ? group.Id : null)
                .OfType<string>()
                .ToArray();

            await App.Current.EditLibraryAsync(XamlRoot, async service =>
            {
                foreach (var group in moved.Where(g => parents.GetValueOrDefault(g.Id) != newParent.Id))
                {
                    await service.SaveGroupAsync(
                        new GroupInfo(group.Id, newParent.Id, group.Name, group.Abbreviation, group.IgnoreCase), CancellationToken.None);
                }

                if (siblings is { Length: > 0 })
                {
                    await service.ReorderGroupsAsync(newParent.Id, siblings, CancellationToken.None);
                }
            });
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

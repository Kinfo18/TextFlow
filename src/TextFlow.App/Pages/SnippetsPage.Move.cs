using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TextFlow.Core.Library;

namespace TextFlow.App.Pages;

/// <summary>
/// Moving commands to another group: "Mover a" in the editor, on the list's right-click menu and for several commands
/// at once (Ctrl+click or Shift+click in the list).
/// </summary>
public sealed partial class SnippetsPage
{
    private string? _noticeGroupId;

    /// <summary>The list rows the user selected, top to bottom.</summary>
    private List<(LibrarySnippet Snippet, string GroupId, string? Path)> SelectedRows() =>
        SnippetList.Items.OfType<ListViewItem>()
            .Where(item => item.IsSelected)
            .Select(item => item.Tag)
            .OfType<ValueTuple<LibrarySnippet, string, string?>>()
            .Select(row => (row.Item1, row.Item2, row.Item3))
            .ToList();

    private void ShowMultiState(int count)
    {
        _draft = null;
        _draftGroupId = null;
        EmptyState.Visibility = Visibility.Collapsed;
        EditorScroll.Visibility = Visibility.Collapsed;
        EditorActions.Visibility = Visibility.Collapsed;
        MultiState.Visibility = Visibility.Visible;
        MultiTitle.Text = $"{count} comandos seleccionados";
        SetDirty(false);
    }

    /// <summary>The editor's "Mover a": an unsaved new command just changes the group it will be saved in.</summary>
    private void OnEditorMoveOpening(object sender, object e)
    {
        if (_draft is not { } draft)
        {
            return;
        }

        FillMoveMenu(EditorMoveFlyout.Items, _draftGroupId, target =>
        {
            if (_isNew)
            {
                _draftGroupId = target.Id;
                DetailPath.Text = GroupPathOf(target.Id);
                UpdateWarnings();
                return Task.CompletedTask;
            }

            return MoveAsync([draft.Id], target);
        });
    }

    private void OnMultiMoveOpening(object sender, object e)
    {
        var rows = SelectedRows();
        var groups = rows.Select(row => row.GroupId).Distinct(StringComparer.Ordinal).ToList();
        FillMoveMenu(MultiMoveFlyout.Items, groups.Count == 1 ? groups[0] : null, target => MoveAsync([.. rows.Select(row => row.Snippet.Id)], target));
    }

    /// <summary>Right-click on a command: move it (or the whole selection), or jump to its group from search results.</summary>
    private void OnListContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs args)
    {
        var container = args.OriginalSource as DependencyObject;
        while (container is not null and not ListViewItem)
        {
            container = VisualTreeHelper.GetParent(container);
        }

        if (container is not ListViewItem { Tag: ValueTuple<LibrarySnippet, string, string?> clicked } item)
        {
            return;
        }

        var rows = item.IsSelected ? SelectedRows() : [(clicked.Item1, clicked.Item2, clicked.Item3)];
        var groups = rows.Select(row => row.GroupId).Distinct(StringComparer.Ordinal).ToList();
        var menu = new MenuFlyout();
        var move = new MenuFlyoutSubItem
        {
            Text = rows.Count == 1 ? "Mover a" : $"Mover {rows.Count} comandos a",
            Icon = new FontIcon { Glyph = "" },
        };
        FillMoveMenu(move.Items, groups.Count == 1 ? groups[0] : null, target => MoveAsync([.. rows.Select(row => row.Snippet.Id)], target));
        menu.Items.Add(move);
        if (rows.Count == 1)
        {
            AddOrderItems(menu.Items, clicked.Item1.Id);
        }

        if (clicked.Item3 is not null)
        {
            var goTo = new MenuFlyoutItem { Text = "Ir al grupo", Icon = new FontIcon { Glyph = "" } };
            goTo.Click += (_, _) => GoToGroup(clicked.Item2);
            menu.Items.Add(goTo);
        }

        if (args.TryGetPosition(item, out var point))
        {
            menu.ShowAt(item, point);
        }
        else
        {
            menu.ShowAt(item);
        }

        args.Handled = true;
    }

    /// <summary>
    /// Every group as a menu entry, nested like the tree, with its abbreviation on the right. The group the commands are
    /// already in is shown checked and cannot be picked.
    /// </summary>
    private static void FillMoveMenu(IList<MenuFlyoutItemBase> items, string? currentGroupId, Func<LibraryGroup, Task> move)
    {
        items.Clear();
        items.Add(TargetItem(Library, Library.Name, currentGroupId, move));
        items.Add(new MenuFlyoutSeparator());
        foreach (var group in Library.Groups)
        {
            items.Add(TargetEntry(group, currentGroupId, move));
        }
    }

    private static MenuFlyoutItemBase TargetEntry(LibraryGroup group, string? currentGroupId, Func<LibraryGroup, Task> move)
    {
        if (group.Groups.Count == 0)
        {
            return TargetItem(group, group.Name, currentGroupId, move);
        }

        var sub = new MenuFlyoutSubItem { Text = group.Name, Icon = new FontIcon { Glyph = FolderGlyph } };
        sub.Items.Add(TargetItem(group, $"{group.Name} (este grupo)", currentGroupId, move));
        sub.Items.Add(new MenuFlyoutSeparator());
        foreach (var child in group.Groups)
        {
            sub.Items.Add(TargetEntry(child, currentGroupId, move));
        }

        return sub;
    }

    private static MenuFlyoutItem TargetItem(LibraryGroup group, string text, string? currentGroupId, Func<LibraryGroup, Task> move)
    {
        var isCurrent = group.Id == currentGroupId;
        var item = new MenuFlyoutItem
        {
            Text = text,
            IsEnabled = !isCurrent,
            Icon = new FontIcon { Glyph = isCurrent ? "" : group.Id == Library.Id ? LibraryGlyph : FolderGlyph },
            KeyboardAcceleratorTextOverride = group.Abbreviation ?? string.Empty,
        };
        item.Click += async (_, _) => await move(group);
        return item;
    }

    /// <summary>Unsaved edits are saved or discarded first; then the commands go last in the target, in list order.</summary>
    private async Task MoveAsync(IReadOnlyList<string> snippetIds, LibraryGroup target)
    {
        try
        {
            if (snippetIds.Count == 0 || !await ConfirmLeaveAsync())
            {
                return;
            }

            if (await App.Current.EditLibraryAsync(XamlRoot, service => service.MoveSnippetsAsync(snippetIds, target.Id, CancellationToken.None)))
            {
                _noticeGroupId = target.Id;
                MoveNotice.Message = snippetIds.Count == 1
                    ? $"Comando movido a «{GroupPathOf(target.Id)}»."
                    : $"{snippetIds.Count} comandos movidos a «{GroupPathOf(target.Id)}».";
                MoveNotice.IsOpen = true;
                Refresh();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            App.Current.RecordFault(ex);
            MoveNotice.IsOpen = false;
            ListSubtitle.Text = $"No se pudo mover: {ex.Message}";
        }
    }

    private void OnNoticeGoToGroup(object sender, RoutedEventArgs e)
    {
        MoveNotice.IsOpen = false;
        if (_noticeGroupId is { } groupId)
        {
            GoToGroup(groupId);
        }
    }
}

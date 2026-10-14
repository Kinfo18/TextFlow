using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TextFlow.Core.Library;

namespace TextFlow.App.Pages;

/// <summary>
/// Ordering the commands of a group: drag and drop in the list, "Subir / Bajar / Al principio / Al final" on the
/// right-click menu, or Alt+↑ / Alt+↓. The order is the one the group's menu shows. Search results and the
/// "sin usar" filter are not a group's order, so they cannot be reordered.
/// </summary>
public sealed partial class SnippetsPage
{
    private string? _reorderGroupId;
    private List<string> _filledIds = [];

    private void AllowReorder(string? groupId, IEnumerable<string> ids)
    {
        _reorderGroupId = groupId;
        _filledIds = [.. ids];
        var canReorder = groupId is not null;
        SnippetList.CanDragItems = canReorder;
        SnippetList.CanReorderItems = canReorder;
        SnippetList.AllowDrop = canReorder;
    }

    /// <summary>The listed commands top to bottom, as the list shows them now.</summary>
    private List<string> ListedIds() =>
        SnippetList.Items.OfType<ListViewItem>()
            .Select(item => item.Tag)
            .OfType<ValueTuple<LibrarySnippet, string, string?>>()
            .Select(row => row.Item1.Id)
            .ToList();

    /// <remarks>async void: every failure is caught and shown; an escaping exception freezes WinUI.</remarks>
    private async void OnSnippetsDragged(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (_reorderGroupId is not { } groupId || args.DropResult != Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move)
        {
            return;
        }

        var ids = ListedIds();
        if (!ids.SequenceEqual(_filledIds, StringComparer.Ordinal))
        {
            await ReorderAsync(groupId, ids); // a drop back in place stores nothing
        }
    }

    private void AddOrderItems(IList<MenuFlyoutItemBase> items, string snippetId)
    {
        if (_reorderGroupId is not { } groupId)
        {
            return;
        }

        var ids = ListedIds();
        items.Add(new MenuFlyoutSeparator());
        items.Add(OrderItem("Subir", "", "Alt+↑", ids, snippetId, OrderMove.Up, groupId));
        items.Add(OrderItem("Bajar", "", "Alt+↓", ids, snippetId, OrderMove.Down, groupId));
        items.Add(OrderItem("Al principio", "", string.Empty, ids, snippetId, OrderMove.First, groupId));
        items.Add(OrderItem("Al final", "", string.Empty, ids, snippetId, OrderMove.Last, groupId));
    }

    /// <summary>Disabled when the command is already at that end of the group.</summary>
    private MenuFlyoutItem OrderItem(
        string text, string glyph, string shortcut, IReadOnlyList<string> ids, string snippetId, OrderMove move, string groupId)
    {
        var order = SnippetOrder.Move(ids, snippetId, move);
        var item = new MenuFlyoutItem
        {
            Text = text,
            Icon = new FontIcon { Glyph = glyph },
            IsEnabled = order is not null,
            KeyboardAcceleratorTextOverride = shortcut,
        };
        item.Click += async (_, _) =>
        {
            if (order is not null)
            {
                await ReorderAsync(groupId, order);
            }
        };
        return item;
    }

    private void OnMoveUpInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => MoveSelected(OrderMove.Up, args);

    private void OnMoveDownInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => MoveSelected(OrderMove.Down, args);

    /// <summary>Alt+arrow on the one selected command; with several selected the keys do nothing.</summary>
    private async void MoveSelected(OrderMove move, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_reorderGroupId is not { } groupId || SelectedRows() is not [var row]
            || SnippetOrder.Move(ListedIds(), row.Snippet.Id, move) is not { } order)
        {
            return;
        }

        args.Handled = true;
        await ReorderAsync(groupId, order);
    }

    /// <summary>Stores the order; the list is rebuilt from what was stored either way (a failed or cancelled move snaps back).</summary>
    private async Task ReorderAsync(string groupId, IReadOnlyList<string> ids)
    {
        try
        {
            await App.Current.EditLibraryAsync(XamlRoot, service => service.ReorderSnippetsAsync(groupId, ids, CancellationToken.None));
        }
        catch (Exception ex) // called from async void handlers: anything escaping would freeze WinUI
        {
            App.Current.RecordFault(ex);
            Refresh();
            ListSubtitle.Text = $"No se pudo cambiar el orden: {ex.Message}";
            return;
        }

        Refresh();
        DispatcherQueue.TryEnqueue(() => (SnippetList.SelectedItem as ListViewItem)?.Focus(Microsoft.UI.Xaml.FocusState.Keyboard));
    }
}

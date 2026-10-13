using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TextFlow.Core.Library;

namespace TextFlow.App.Pages;

/// <summary>
/// Group navigation: tree rows with icon, abbreviation and command count, a "you are here" mark on the group shown,
/// and the breadcrumb above the list (click a level to go up).
/// </summary>
public sealed partial class SnippetsPage
{
    private const string LibraryGlyph = "";
    private const string FolderGlyph = "";
    private const string OpenFolderGlyph = "";
    private const double IdleIconOpacity = 0.7;
    private const double ChipMaxWidth = 64;

    private readonly Dictionary<string, GroupRow> _rows = new(StringComparer.Ordinal);
    private readonly List<string> _crumbIds = [];
    private GroupRow? _marked;

    /// <summary>The parts of a tree row that change when it becomes the current group.</summary>
    private sealed record GroupRow(FontIcon Icon, TextBlock Name, string Glyph, string ActiveGlyph);

    private Grid GroupRowContent(LibraryGroup group, bool isRoot)
    {
        var glyph = isRoot ? LibraryGlyph : FolderGlyph;
        var icon = new FontIcon { Glyph = glyph, FontSize = 14, Opacity = IdleIconOpacity, VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { Text = group.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var grid = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 3, 6, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(icon);
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        if (group.Abbreviation is { } abbreviation)
        {
            var chip = AbbreviationChip(abbreviation);
            Grid.SetColumn(chip, 2);
            grid.Children.Add(chip);
        }

        // Everything inside, subgroups included: a parent group with no commands of its own is not "empty".
        var total = Count(group).Commands;
        var count = new TextBlock
        {
            Text = total.ToString(System.Globalization.CultureInfo.CurrentCulture),
            FontSize = 12,
            Opacity = 0.6,
            MinWidth = 22,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(count, 3);
        grid.Children.Add(count);

        var description = Describe(group, group.Snippets.Count, total);
        ToolTipService.SetToolTip(grid, description);
        // Screen readers read the row's container, which would otherwise announce the node's type name.
        grid.Loaded += (_, _) => NameContainer(grid, description);
        _rows[group.Id] = new GroupRow(icon, name, glyph, isRoot ? LibraryGlyph : OpenFolderGlyph);
        return grid;
    }

    private static void NameContainer(DependencyObject content, string name)
    {
        var container = VisualTreeHelper.GetParent(content);
        while (container is not null and not TreeViewItem)
        {
            container = VisualTreeHelper.GetParent(container);
        }

        if (container is TreeViewItem item)
        {
            AutomationProperties.SetName(item, name);
        }
    }

    /// <summary>The abbreviation that opens a group's menu, drawn as a small tag.</summary>
    private static Border AbbreviationChip(string abbreviation) => new()
    {
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(6, 1, 6, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Background = ThemeBrushes.Get("SubtleFillColorSecondaryBrush"),
        MaxWidth = ChipMaxWidth, // a long abbreviation is cut before the group's name is
        Child = new TextBlock
        {
            Text = abbreviation,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 12,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            Foreground = ThemeBrushes.Get("AccentTextFillColorPrimaryBrush"),
        },
    };

    private static string Describe(LibraryGroup group, int own, int total)
    {
        var commands = total == 1 ? "1 comando" : $"{total} comandos";
        var below = total > own ? $" ({total - own} en subgrupos)" : string.Empty;
        var opens = group.Abbreviation is { } abbreviation ? $" · se abre con {abbreviation}" : string.Empty;
        return $"{group.Name}: {commands}{below}{opens}";
    }

    /// <summary>Bold name and an open, accent-colored folder on the group whose commands are listed.</summary>
    private void MarkGroup(string groupId)
    {
        if (_marked is { } old)
        {
            old.Name.FontWeight = FontWeights.Normal;
            old.Icon.Glyph = old.Glyph;
            old.Icon.Opacity = IdleIconOpacity;
            old.Icon.ClearValue(IconElement.ForegroundProperty);
        }

        _marked = _rows.GetValueOrDefault(groupId);
        if (_marked is { } row)
        {
            row.Name.FontWeight = FontWeights.SemiBold;
            row.Icon.Glyph = row.ActiveGlyph;
            row.Icon.Opacity = 1;
            row.Icon.Foreground = ThemeBrushes.Get("AccentTextFillColorPrimaryBrush");
        }
    }

    private void ShowBreadcrumb(LibraryGroup group)
    {
        var chain = PathTo(Library, group.Id) ?? [Library];
        _crumbIds.Clear();
        _crumbIds.AddRange(chain.Select(g => g.Id));
        GroupCrumbs.ItemsSource = chain.Select(g => g.Name).ToList();
        GroupCrumbs.Visibility = Visibility.Visible;
        ListTitle.Visibility = Visibility.Collapsed;
    }

    private void ShowPlainTitle()
    {
        GroupCrumbs.Visibility = Visibility.Collapsed;
        ListTitle.Visibility = Visibility.Visible;
    }

    private void OnCrumbClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Index >= 0 && args.Index < _crumbIds.Count)
        {
            GoToGroup(_crumbIds[args.Index]);
        }
    }

    /// <summary>Shows a group's commands and selects it in the tree, opening its parents (breadcrumb, "Ir al grupo").</summary>
    private void GoToGroup(string groupId)
    {
        if (PathTo(Library, groupId) is not [.., var group])
        {
            return;
        }

        RevealGroup(groupId);
        UnusedToggle.IsChecked = false;
        SearchBox.Text = string.Empty;
        ShowGroup(group);
    }

    /// <summary>Selects a group in the tree with its parents expanded and scrolls it into view; the list is left alone.</summary>
    private void RevealGroup(string groupId)
    {
        if (_groups.FirstOrDefault(pair => pair.Value.Id == groupId).Key is not { } node)
        {
            return;
        }

        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            parent.IsExpanded = true;
        }

        _selectedGroupId = groupId;
        GroupTree.SelectedNode = node;
        MarkGroup(groupId);
        // The row may not exist yet right after its parent expands.
        DispatcherQueue.TryEnqueue(() => (GroupTree.ContainerFromNode(node) as UIElement)?.StartBringIntoView());
    }

    /// <summary>Groups from the root down to <paramref name="id"/>; null when it is not in the tree.</summary>
    private static IReadOnlyList<LibraryGroup>? PathTo(LibraryGroup from, string id)
    {
        if (from.Id == id)
        {
            return [from];
        }

        foreach (var child in from.Groups)
        {
            if (PathTo(child, id) is { } rest)
            {
                return [from, .. rest];
            }
        }

        return null;
    }
}

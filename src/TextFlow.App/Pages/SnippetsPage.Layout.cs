using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TextFlow.Core.Library;

namespace TextFlow.App.Pages;

/// <summary>Snippets (C9): three columns when the window is wide, use counts per command and the "unused" filter.</summary>
public sealed partial class SnippetsPage
{
    /// <summary>Below this page width the editor goes under the list again (groups 240 + list 260 + editor 340 + gaps).</summary>
    private const double ThreeColumnWidth = 940;

    private const int UnusedDays = 30;

    private IReadOnlyDictionary<string, SnippetUsage> _usage = new Dictionary<string, SnippetUsage>();
    private bool? _threeColumns;

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout(e.NewSize.Width >= ThreeColumnWidth);

    private void ApplyLayout(bool threeColumns)
    {
        if (_threeColumns == threeColumns)
        {
            return;
        }

        _threeColumns = threeColumns;
        if (threeColumns)
        {
            GroupColumn.MinWidth = 240;
            ListColumn.Width = new GridLength(3, GridUnitType.Star);
            ListColumn.MinWidth = 260;
            EditorColumn.Width = new GridLength(4, GridUnitType.Star);
            EditorColumn.MinWidth = 340;
            TopRow.Height = new GridLength(1, GridUnitType.Star);
            BottomRow.Height = new GridLength(0);
            BottomRow.MinHeight = 0;
            Grid.SetRow(EditorPane, 1);
            Grid.SetColumn(EditorPane, 2);
            Grid.SetRowSpan(ListPane, 1);
        }
        else
        {
            GroupColumn.MinWidth = 200;
            ListColumn.Width = new GridLength(5, GridUnitType.Star);
            ListColumn.MinWidth = 320;
            EditorColumn.Width = new GridLength(0);
            EditorColumn.MinWidth = 0;
            TopRow.Height = new GridLength(2, GridUnitType.Star);
            BottomRow.Height = new GridLength(3, GridUnitType.Star);
            BottomRow.MinHeight = 240;
            Grid.SetRow(EditorPane, 2);
            Grid.SetColumn(EditorPane, 1);
        }
    }

    /// <summary>Use counts arrive after the page shows; the list is redrawn once they do (selection and edits kept).</summary>
    private async Task LoadUsageAsync()
    {
        if (App.Current.Library is not { } library)
        {
            return;
        }

        try
        {
            _usage = await library.LoadUsageAsync(CancellationToken.None);
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            App.Current.RecordFault(ex); // the list just shows no counts
        }
    }

    /// <summary>"12×" next to a command; null when it was never used (or the counts are not loaded yet).</summary>
    private string? UsesText(string snippetId) =>
        _usage.TryGetValue(snippetId, out var use) && use.Uses > 0 ? $"{use.Uses}×" : null;

    private void OnUnusedToggled(object sender, RoutedEventArgs e)
    {
        if (UnusedToggle.IsChecked == true)
        {
            SearchBox.Text = string.Empty;
            ShowUnused();
        }
        else if (GroupTree.SelectedNode is { } node && _groups.TryGetValue(node, out var group))
        {
            ShowGroup(group);
        }
    }

    /// <summary>Enabled commands not used in the last 30 days: candidates to tidy up, never deleted automatically.</summary>
    private void ShowUnused()
    {
        var unused = UsageReport.Unused(Library, _usage, DateTimeOffset.UtcNow, UnusedDays).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var rows = WithPaths(Library, []).Where(row => unused.Contains(row.Snippet.Id)).ToList();
        ListTitle.Text = $"Sin usar en {UnusedDays} días";
        ListSubtitle.Text = (rows.Count switch
        {
            0 => "Has usado todos tus comandos últimamente",
            1 => "1 comando",
            _ => $"{rows.Count} comandos",
        }) + " · se cuenta desde que instalaste esta versión";
        NewSnippetButton.IsEnabled = false;
        GroupButton.IsEnabled = false;
        Fill(rows);
    }

    private static IEnumerable<(LibrarySnippet Snippet, string GroupId, string? Path)> WithPaths(LibraryGroup group, IReadOnlyList<string> path)
    {
        var here = path.Count == 0 ? group.Name : string.Join(" › ", path);
        foreach (var snippet in group.Snippets)
        {
            yield return (snippet, group.Id, here);
        }

        foreach (var child in group.Groups)
        {
            foreach (var row in WithPaths(child, [.. path, child.Name]))
            {
                yield return row;
            }
        }
    }
}

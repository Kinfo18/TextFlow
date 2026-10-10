using System.Globalization;
using TextFlow.App.Engine;
using TextFlow.Contracts.Targeting;
using TextFlow.Core.Library;

namespace TextFlow.App.Palette;

/// <summary>
/// Command palette (D11): on the shortcut, captures the field the user is in, opens <see cref="PaletteWindow"/> and,
/// on a choice, hands the snippet to the engine for that field or runs the action. UI thread only.
/// </summary>
internal sealed class PaletteHost
{
    private const int TopUsed = 8;
    private const int MaxHits = 40;

    private readonly PaletteWindow _window;
    private readonly EngineHost _engine;
    private readonly LibraryHost _library;
    private readonly Func<IReadOnlyList<PaletteItem>> _actions;
    private ActiveTarget? _target;
    private IReadOnlyDictionary<string, SnippetUsage> _usage = new Dictionary<string, SnippetUsage>();

    /// <param name="actions">TextFlow's own actions, built fresh each time (the pause one names the current state).</param>
    public PaletteHost(PaletteWindow window, EngineHost engine, LibraryHost library, Func<IReadOnlyList<PaletteItem>> actions)
    {
        _window = window;
        _engine = engine;
        _library = library;
        _actions = actions;
        _window.Finished += OnFinished;
    }

    /// <remarks>async void from the hotkey: every failure is reported and the palette simply does not open.</remarks>
    public async void Open()
    {
        if (_window.IsOpen)
        {
            return;
        }

        try
        {
            _target = await _engine.CapturePaletteTargetAsync();
            _usage = await _library.LoadUsageAsync(CancellationToken.None);
            _window.Open(Items, _target?.Monitor);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            App.Current.RecordFault(ex);
        }
    }

    private IReadOnlyList<PaletteItem> Items(string query)
    {
        var text = query.Trim();
        var actions = _actions().Where(a => text.Length == 0 || Contains(a.Title, text));
        if (_target is null)
        {
            return actions.ToList(); // no text field to insert into: TextFlow's actions only
        }

        var root = _library.Service.Current;
        var snippets = text.Length == 0
            ? UsageReport.Top(root, _usage, TopUsed).Select(u => Item(u.Snippet, u.GroupName.Length > 0 ? u.GroupName : root.Name, $" · {Uses(u.Uses)}"))
            : LibrarySearch.Find(root, text)
                .Where(hit => hit.Snippet.Enabled && !hit.Snippet.IsInfoOnly)
                .Take(MaxHits)
                .Select(hit => Item(hit.Snippet, hit.GroupPath.Count > 0 ? string.Join(" › ", hit.GroupPath) : root.Name, string.Empty));

        // Typing looks for snippets first; with nothing typed, the actions come after the most used.
        return [.. snippets, .. actions];
    }

    private static PaletteItem Item(LibrarySnippet snippet, string where, string suffix)
    {
        var abbreviation = snippet.Abbreviations.Count > 0 ? snippet.Abbreviations[0] : null;
        var title = abbreviation is null || abbreviation == snippet.Name ? snippet.Name : $"{abbreviation}  ·  {snippet.Name}";
        return new PaletteItem(title, where + suffix, SnippetId: snippet.Id);
    }

    private static string Uses(int uses) => uses == 1 ? "usado 1 vez" : $"usado {uses} veces";

    private static bool Contains(string text, string query) =>
        CultureInfo.InvariantCulture.CompareInfo.IndexOf(text, query, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

    private void OnFinished(PaletteItem? choice)
    {
        var target = _target;
        _target = null;
        if (choice?.SnippetId is { } id && target is not null)
        {
            _engine.InsertFromPalette(id, target); // the engine returns the focus to the field, then inserts
        }
        else
        {
            choice?.Run?.Invoke();
        }
    }
}

using Microsoft.UI.Dispatching;
using TextFlow.Contracts.Targeting;
using TextFlow.Core.Engine;
using TextFlow.Core.Menus;
using TextFlow.Core.Templates;
using Windows.Graphics;

namespace TextFlow.App.Fields;

/// <summary><see cref="IFieldPrompt"/> over <see cref="FieldPromptWindow"/>: called from the engine thread, runs on the UI thread.</summary>
internal sealed class WinUiFieldPrompt : IFieldPrompt
{
    private const int GapDip = 6;

    private readonly DispatcherQueue _ui;
    private readonly FieldPromptWindow _window;

    // UI thread only.
    private int _session;
    private PixelRect _anchor;
    private MonitorInfo? _monitor;

    public WinUiFieldPrompt(DispatcherQueue ui, FieldPromptWindow window)
    {
        _ui = ui;
        _window = window;
        _window.Finished += values => Finished?.Invoke(_session, values);
    }

    public event Action<int, IReadOnlyDictionary<string, string>?>? Finished;

    public void Show(IReadOnlyList<TemplateField> fields, PixelRect anchor, MonitorInfo monitor, int session) => _ui.TryEnqueue(() =>
    {
        _session = session;
        _anchor = anchor;
        _monitor = monitor;
        _window.ShowFields(fields, Bounds(fields.Count));
    });

    public void Cancel() => _ui.TryEnqueue(_window.HidePrompt);

    public void ShowNotInserted(string text) => _ui.TryEnqueue(() => _window.ShowNotInserted(text, Bounds(0)));

    public void ShowWaiting() => _ui.TryEnqueue(() => _window.ShowWaiting(Bounds(0)));

    private RectInt32 Bounds(int fieldCount)
    {
        var monitor = _monitor!;
        var size = FieldPromptWindow.SizeFor(fieldCount, monitor.Scale);
        var (x, y) = MenuPlacement.Place(_anchor, monitor.Bounds, size.Width, size.Height, (int)Math.Round(GapDip * monitor.Scale));
        return new RectInt32(x, y, size.Width, size.Height);
    }
}

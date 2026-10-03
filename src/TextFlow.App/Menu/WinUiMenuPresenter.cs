using Microsoft.UI.Dispatching;
using TextFlow.Contracts.Targeting;
using TextFlow.Core.Engine;
using TextFlow.Core.Menus;
using Windows.Graphics;

namespace TextFlow.App.Menu;

/// <summary>
/// <see cref="IMenuPresenter"/> over <see cref="MenuPopup"/>: called from the engine thread, marshals every
/// change to the UI thread and drives navigation with <see cref="MenuNavigator"/>.
/// </summary>
internal sealed class WinUiMenuPresenter : IMenuPresenter
{
    private const int GapDip = 4;

    private readonly DispatcherQueue _ui;
    private readonly MenuPopup _popup;
    private readonly Lock _boundsGate = new();
    private PixelRect _bounds;

    // UI thread only.
    private MenuState? _state;
    private int _session;
    private PixelRect _anchor;
    private MonitorInfo? _monitor;

    public WinUiMenuPresenter(DispatcherQueue ui, MenuPopup popup)
    {
        _ui = ui;
        _popup = popup;
        _popup.RowActivated += index =>
        {
            if (_state is not null)
            {
                Apply(MenuNavigator.Activate(_state, index));
            }
        };
    }

    public event Action<int, MenuStep>? Finished;

    public void Show(GroupMenu menu, PixelRect anchor, MonitorInfo monitor, int session) => Enqueue(() =>
    {
        _session = session;
        _anchor = anchor;
        _monitor = monitor;
        Present(MenuNavigator.Open(menu));
    });

    public void Send(MenuInput input) => Enqueue(() =>
    {
        if (_state is not null)
        {
            Apply(MenuNavigator.Apply(_state, input));
        }
    });

    public void Dismiss() => Enqueue(() =>
    {
        if (_state is not null)
        {
            Apply(new MenuStep(_state, Closed: true));
        }
    });

    public void Cancel() => Enqueue(Hide);

    public bool Contains(int x, int y)
    {
        lock (_boundsGate)
        {
            return x >= _bounds.Left && x < _bounds.Right && y >= _bounds.Top && y < _bounds.Bottom;
        }
    }

    private void Apply(MenuStep step)
    {
        if (step.IsFinished)
        {
            Hide();
            Finished?.Invoke(_session, step);
        }
        else if (step.State.Path.Count != _state!.Path.Count)
        {
            Present(step.State); // entered or left a subgroup: new rows, new size
        }
        else
        {
            _state = step.State;
            _popup.Select(step.State.Current.Selected);
        }
    }

    private void Present(MenuState state)
    {
        _state = state;
        var monitor = _monitor!;
        var size = _popup.Present(state, monitor.Scale);
        var (x, y) = MenuPlacement.Place(_anchor, monitor.Bounds, size.Width, size.Height, (int)Math.Round(GapDip * monitor.Scale));
        _popup.ShowAt(new RectInt32(x, y, size.Width, size.Height));
        SetBounds(new PixelRect(x, y, x + size.Width, y + size.Height));
    }

    private void Hide()
    {
        _state = null;
        _popup.HidePopup();
        SetBounds(default);
    }

    private void SetBounds(PixelRect bounds)
    {
        lock (_boundsGate)
        {
            _bounds = bounds;
        }
    }

    /// <summary>False only while the app shuts down, when there is no menu left to update.</summary>
    private void Enqueue(Action work) => _ui.TryEnqueue(() => work());
}

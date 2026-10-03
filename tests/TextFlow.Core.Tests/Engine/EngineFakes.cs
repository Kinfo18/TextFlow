using System.Threading.Channels;
using TextFlow.Contracts.Insertion;
using TextFlow.Contracts.Targeting;
using TextFlow.Core.Diagnostics;
using TextFlow.Core.Engine;
using TextFlow.Core.Expansion;
using TextFlow.Core.Input;
using TextFlow.Core.Menus;

namespace TextFlow.Core.Tests.Engine;

internal sealed class FakeHook : IInputHook
{
    private readonly Channel<HookEvent> _events = Channel.CreateUnbounded<HookEvent>();

    public ChannelReader<HookEvent> Events => _events.Reader;

    public bool CaptureEnabled { get; set; }

    /// <summary>Like KeyboardHook: bumped by every focus or foreground change.</summary>
    public int FocusVersion { get; set; }

    public bool TryEnableCapture(int focusVersion)
    {
        if (focusVersion != FocusVersion)
        {
            return false;
        }

        CaptureEnabled = true;
        return true;
    }

    /// <summary>Raises a focus change the way the hook does: version bump, capture off at once.</summary>
    public void RaiseFocusChange(HookEvent evt)
    {
        FocusVersion++;
        CaptureEnabled = false;
        Raise(evt);
    }

    /// <summary>Like KeyboardHook: turning menu mode off clears the matcher, including a pending trigger.</summary>
    public bool MenuMode
    {
        get => _menuMode;
        set
        {
            _menuMode = value;
            if (!value)
            {
                PendingCleared++;
            }
        }
    }

    public int PendingCleared { get; private set; }

    private bool _menuMode;

    public List<int> Flushed { get; } = [];

    public List<TriggerDefinition> Triggers { get; private set; } = [];

    public void Raise(HookEvent evt) => _events.Writer.TryWrite(evt);

    public void Close() => _events.Writer.TryComplete();

    public Task FlushPendingAsync(int version)
    {
        Flushed.Add(version);
        return Task.CompletedTask;
    }

    public Task ReplaceTriggersAsync(IEnumerable<TriggerDefinition> triggers)
    {
        Triggers = triggers.ToList();
        return Task.CompletedTask;
    }
}

internal sealed class FakeResolver : ITargetResolver
{
    public const nint Window = 0x1234;

    public static ActiveTarget Notepad { get; } = new(
        Window, 0x5678, 100, 200, "notepad.exe", "Notepad", "t",
        new MonitorInfo(1, "D1", 120, 120, new PixelRect(0, 0, 1920, 1080)),
        FocusedControlInfo.Unknown with { CaretBounds = new PixelRect(50, 60, 51, 80) },
        IsElevated: false, DateTimeOffset.UnixEpoch);

    public ActiveTarget? Target { get; set; } = Notepad;

    public int Captures { get; private set; }

    /// <summary>Runs inside CaptureTarget: simulates something happening while UIA is busy.</summary>
    public Action? DuringCapture { get; set; }

    public ActiveTarget? CaptureTarget()
    {
        Captures++;
        DuringCapture?.Invoke();
        return Target;
    }

    public TargetValidation ValidateTarget(ActiveTarget target) => TargetValidation.Valid;
}

internal sealed class FakeInsertion : ITextInsertionService
{
    public List<InsertionRequest> Requests { get; } = [];

    public InsertionStatus NextStatus { get; set; } = InsertionStatus.Success;

    public Task<InsertionResult> InsertAsync(InsertionRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(new InsertionResult(NextStatus, InsertionStrategyKind.Clipboard, TimeSpan.FromMilliseconds(10)));
    }
}

internal sealed class FakeMenu : IMenuPresenter
{
    public event Action<int, MenuStep>? Finished;

    public GroupMenu? Shown { get; private set; }

    public PixelRect? Anchor { get; private set; }

    public int Session { get; private set; }

    public List<MenuInput> Inputs { get; } = [];

    public int Cancels { get; private set; }

    public int Dismissals { get; private set; }

    public bool ContainsResult { get; set; }

    public void Show(GroupMenu menu, PixelRect anchor, MonitorInfo monitor, int session)
    {
        Shown = menu;
        Anchor = anchor;
        Session = session;
    }

    public void Send(MenuInput input) => Inputs.Add(input);

    public void Dismiss()
    {
        Dismissals++;
        Finish(new MenuStep(MenuNavigator.Open(Shown!), Closed: true));
    }

    public void Cancel() => Cancels++;

    public bool Contains(int x, int y) => ContainsResult;

    public void Finish(MenuStep step) => Finish(step, Session);

    public void Finish(MenuStep step, int session) => Finished?.Invoke(session, step);
}

internal sealed class FakeFeedback : IExpansionFeedback
{
    public int Plays { get; private set; }

    public int Warms { get; private set; }

    public void Warm() => Warms++;

    public bool Play()
    {
        Plays++;
        return true;
    }
}

internal sealed class FakePointer : IPointerLocator
{
    public PixelRect CursorAnchor() => new(900, 500, 900, 516);
}

internal sealed class ListSink : IDiagnosticSink
{
    private readonly Lock _gate = new();
    private readonly List<DiagnosticEvent> _events = [];

    public IReadOnlyList<DiagnosticEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }
    }

    public void Record(DiagnosticEvent diagnostic)
    {
        lock (_gate)
        {
            _events.Add(diagnostic);
        }
    }
}

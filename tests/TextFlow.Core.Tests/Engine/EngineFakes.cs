using System.Threading.Channels;
using TextFlow.Contracts.Insertion;
using TextFlow.Contracts.Targeting;
using TextFlow.Core.Diagnostics;
using TextFlow.Core.Engine;
using TextFlow.Core.Expansion;
using TextFlow.Core.Input;
using TextFlow.Core.Menus;
using TextFlow.Core.Templates;

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

    public int Activations { get; private set; }

    public bool ActivateResult { get; set; } = true;

    public bool Activate(ActiveTarget target)
    {
        Activations++;
        return ActivateResult;
    }

    public List<ActiveTarget> Bookmarks { get; } = [];

    public int Restores { get; private set; }

    /// <summary>What giving the focus back does; null = the field refuses.</summary>
    public Func<ActiveTarget, bool>? OnRestore { get; set; }

    public void BookmarkField(ActiveTarget target) => Bookmarks.Add(target);

    public bool RestoreField(ActiveTarget target)
    {
        Restores++;
        return OnRestore?.Invoke(target) ?? false;
    }
}

internal sealed class FakeInsertion : ITextInsertionService
{
    public List<InsertionRequest> Requests { get; } = [];

    public InsertionStatus NextStatus { get; set; } = InsertionStatus.Success;

    /// <summary>When set, the fake reports delivery like a real strategy and then waits here (clipboard restore).</summary>
    public TaskCompletionSource? HoldAfterDelivery { get; set; }

    /// <summary>Completes once the fake reported delivery.</summary>
    public TaskCompletionSource Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<InsertionResult> InsertAsync(InsertionRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        if (HoldAfterDelivery is { } hold)
        {
            request.Delivered?.Invoke();
            Delivered.TrySetResult();
            await hold.Task.ConfigureAwait(false);
        }

        return new InsertionResult(NextStatus, InsertionStrategyKind.Clipboard, TimeSpan.FromMilliseconds(10), InputSent: HoldAfterDelivery is not null);
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

internal sealed class FakeFieldPrompt : IFieldPrompt
{
    public event Action<int, IReadOnlyDictionary<string, string>?>? Finished;

    public IReadOnlyList<TemplateField>? Shown { get; private set; }

    public PixelRect? Anchor { get; private set; }

    public int Session { get; private set; }

    public int Cancels { get; private set; }

    public string? NotInsertedText { get; private set; }

    public void Show(IReadOnlyList<TemplateField> fields, PixelRect anchor, MonitorInfo monitor, int session)
    {
        Shown = fields;
        Anchor = anchor;
        Session = session;
    }

    public void Cancel() => Cancels++;

    public void ShowNotInserted(string text) => NotInsertedText = text;

    public int WaitingShown { get; private set; }

    public void ShowWaiting() => WaitingShown++;

    public void Finish(IReadOnlyDictionary<string, string>? values) => Finished?.Invoke(Session, values);

    public void Finish(IReadOnlyDictionary<string, string>? values, int session) => Finished?.Invoke(session, values);
}

internal sealed class FakeVariables(TimeProvider time) : IVariableSource
{
    public DateTimeOffset Now => time.GetLocalNow();

    public string? Clipboard { get; set; }

    public int ClipboardReads { get; private set; }

    public string? GetClipboardText()
    {
        ClipboardReads++;
        return Clipboard;
    }

    public string? GetSelectionText() => null;
}

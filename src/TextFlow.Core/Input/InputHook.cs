using System.Threading.Channels;
using TextFlow.Core.Expansion;
using TextFlow.Core.Menus;

namespace TextFlow.Core.Input;

public abstract record HookEvent;

/// <summary>A trigger was typed. If <see cref="TriggerMatch.Delimiter"/> is set, that key was swallowed and must be re-emitted if expansion is aborted.</summary>
public sealed record TriggerTyped(TriggerMatch Match, nint ForegroundWindow) : HookEvent;

public sealed record ForegroundChanged(nint Window) : HookEvent;

/// <summary>
/// An ambiguous trigger ("cp" while "cp1" exists) is waiting. A menu trigger can open its menu right away:
/// in <see cref="IInputHook.MenuMode"/> a key that continues a longer trigger still fires it. Otherwise call
/// <see cref="IInputHook.FlushPendingAsync"/> after a timeout.
/// </summary>
public sealed record TriggerPending(TriggerMatch Match, int Version, nint ForegroundWindow) : HookEvent;

/// <summary>Menu mode only: a navigation key was swallowed and belongs to the open menu.</summary>
public sealed record MenuKeyPressed(MenuInput Input) : HookEvent;

/// <summary>Menu mode only: a key the menu does not handle was typed (and passed through) or the mouse was pressed.</summary>
/// <param name="ClickX">Physical screen coordinates of a mouse press; null for a key.</param>
public sealed record MenuInterrupted(int? ClickX = null, int? ClickY = null) : HookEvent;

/// <summary>Content-free hook health counters.</summary>
public sealed record HookStats(long KeyEvents, double MaxCallbackMs);

/// <summary>Global keyboard/mouse hook as seen by the engine (implemented by Infrastructure's KeyboardHook).</summary>
public interface IInputHook
{
    ChannelReader<HookEvent> Events { get; }

    /// <summary>False while paused or when the foreground app is excluded: nothing is buffered (fail closed).</summary>
    bool CaptureEnabled { get; set; }

    /// <summary>While true, navigation keys go to the open menu instead of the target.</summary>
    bool MenuMode { get; set; }

    Task FlushPendingAsync(int version);

    Task ReplaceTriggersAsync(IEnumerable<TriggerDefinition> triggers);
}

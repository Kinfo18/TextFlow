using TextFlow.Contracts.Targeting;
using TextFlow.Core.Menus;

namespace TextFlow.Core.Engine;

/// <summary>The group menu UI (WinUI popup in the app, ADR-0008). Never activates; input arrives via <see cref="Send"/>.</summary>
public interface IMenuPresenter
{
    /// <summary>
    /// Raised (on any thread) when the user chooses a snippet or the menu closes after <see cref="Send"/>/<see cref="Dismiss"/>,
    /// with the <c>session</c> given to <see cref="Show"/>: a result can arrive after the engine already moved on to another menu.
    /// </summary>
    event Action<int, MenuStep>? Finished;

    void Show(GroupMenu menu, PixelRect anchor, MonitorInfo monitor, int session);

    void Send(MenuInput input);

    /// <summary>Closes and reports <see cref="Finished"/> with a closed step.</summary>
    void Dismiss();

    /// <summary>Hides without reporting (the trigger turned out to be a longer one, or focus moved).</summary>
    void Cancel();

    /// <summary>Whether a physical screen point is inside the visible menu (clicks inside must not dismiss it).</summary>
    bool Contains(int x, int y);
}

/// <summary>Expansion chime (ADR: own synthesized sound, user volume).</summary>
public interface IExpansionFeedback
{
    /// <returns>False if disabled or Windows refused to play.</returns>
    bool Play();
}

/// <summary>Fallback anchor when the target exposes no caret (some browsers, VS Code).</summary>
public interface IPointerLocator
{
    PixelRect CursorAnchor();
}

/// <param name="PendingTimeout">How long an ambiguous snippet trigger ("dir1" while "dir12" exists) waits (spec revisions 2026-10-02).</param>
/// <param name="CaptureTimeout">Longest wait for the target (UIA can hang); after it the target counts as missing.</param>
public sealed record ExpansionEngineOptions(TimeSpan PendingTimeout, TimeSpan CaptureTimeout)
{
    public static ExpansionEngineOptions Default { get; } = new(TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(500));
}

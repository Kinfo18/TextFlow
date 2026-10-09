using TextFlow.Contracts.Targeting;
using TextFlow.Core.Menus;
using TextFlow.Core.Templates;

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

/// <summary>
/// Asks for a template's field values (H5.2, ADR-0002 rev. 2026-10-04). Unlike the menu it takes the focus and stays
/// open while the user goes to copy the values elsewhere; values are user content and never logged.
/// </summary>
public interface IFieldPrompt
{
    /// <summary>Raised (any thread) with the <c>session</c> given to <see cref="Show"/>: the values, or null when cancelled.</summary>
    event Action<int, IReadOnlyDictionary<string, string>?>? Finished;

    void Show(IReadOnlyList<TemplateField> fields, PixelRect anchor, MonitorInfo monitor, int session);

    /// <summary>Closes without reporting (another template replaced it, or the engine stopped).</summary>
    void Cancel();

    /// <summary>The original field could not be reached again: hand <paramref name="text"/> to the user to paste.</summary>
    void ShowNotInserted(string text);

    /// <summary>
    /// The values are in but the original field is not focused (the user is still on the page they copied from):
    /// say that the text goes in as soon as they return. Cancelling still reports <see cref="Finished"/> with null.
    /// </summary>
    void ShowWaiting();
}

/// <summary>Expansion chime (ADR: own synthesized sound, user volume).</summary>
public interface IExpansionFeedback
{
    /// <returns>False if disabled or Windows refused to play.</returns>
    bool Play();

    /// <summary>Keeps an idle audio endpoint awake while the user types (inaudible), so the next chime is not swallowed.</summary>
    void Warm();
}

/// <summary>Fallback anchor when the target exposes no caret (some browsers, VS Code).</summary>
public interface IPointerLocator
{
    PixelRect CursorAnchor();
}

/// <param name="PendingTimeout">How long an ambiguous snippet trigger ("dir1" while "dir12" exists) waits (spec revisions 2026-10-02).</param>
/// <param name="CaptureTimeout">Longest wait for the target (UIA can hang); after it the target counts as missing.</param>
/// <param name="FocusReturnTimeout">After a fields prompt, how long to wait for the original field to get the focus back.</param>
/// <param name="ReturnWaitTimeout">How long filled-in fields wait for the user to go back to the original field.</param>
public sealed record ExpansionEngineOptions(
    TimeSpan PendingTimeout, TimeSpan CaptureTimeout, TimeSpan FocusReturnTimeout, TimeSpan ReturnWaitTimeout)
{
    public static ExpansionEngineOptions Default { get; } = new(
        TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(400), TimeSpan.FromMinutes(2));

    /// <summary>
    /// A group menu is drawn only after this pause, so typing on through a word that starts with a group abbreviation
    /// ("dir" in "dirección") never flashes it. Menu keys typed from memory before it show the menu at once.
    /// </summary>
    public TimeSpan MenuDelay { get; init; } = TimeSpan.FromMilliseconds(250);
}

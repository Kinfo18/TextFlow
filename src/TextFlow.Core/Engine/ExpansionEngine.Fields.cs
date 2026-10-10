using System.Globalization;
using TextFlow.Contracts.Insertion;
using TextFlow.Contracts.Targeting;
using TextFlow.Core.Diagnostics;
using TextFlow.Core.Input;
using TextFlow.Core.Menus;
using TextFlow.Core.Templates;

namespace TextFlow.Core.Engine;

/// <summary>
/// Templates (H5.2, ADR-0002 rev. 2026-10-04). Variables and <c>{{cursor}}</c> expand at once; a template with fields
/// removes its trigger, asks for the values in <see cref="IFieldPrompt"/> (the user may go and copy them from another
/// window) and then returns to the original field to insert the final text in one go. If that field cannot be reached
/// again the text is offered to the user instead of being typed somewhere else.
/// </summary>
public sealed partial class ExpansionEngine
{
    private static readonly IReadOnlyDictionary<string, string> NoValues = new Dictionary<string, string>();

    private OpenFields? _openFields;
    private Waiting? _waiting;
    private int _fieldsSession;

    /// <summary>Expands snippet content: plain text as is, templates rendered or, with fields, asked for first.</summary>
    /// <returns>Null when the fields prompt took over (nothing inserted yet).</returns>
    private async Task<InsertionResult?> ExpandContentAsync(
        ActiveTarget target, MenuSnippetEntry snippet, int backspaces, string trailing, bool fromMenu)
    {
        var content = snippet.Content;
        if (!content.Contains("{{", StringComparison.Ordinal))
        {
            return CountUse(snippet, await ExpandAsync(target, content + trailing, backspaces, fromMenu, sendEnter: snippet.SendEnter)
                .ConfigureAwait(false)); // fast path
        }

        var template = TemplateParser.Parse(content);
        if (template.RequiresInput && _fieldPrompt is not null)
        {
            CountUse(snippet); // its form opened: the user reached for this snippet, whatever happens to the fields
            await AskFieldsAsync(target, template, backspaces, trailing, fromMenu, snippet.SendEnter).ConfigureAwait(false);
            return null;
        }

        var rendered = Render(template, NoValues);
        return CountUse(snippet, await ExpandAsync(target, rendered.Text + trailing, backspaces, fromMenu, CaretOffset(rendered, trailing), snippet.SendEnter)
            .ConfigureAwait(false));
    }

    private InsertionResult CountUse(MenuSnippetEntry snippet, InsertionResult result)
    {
        if (result.Succeeded)
        {
            CountUse(snippet);
        }

        return result;
    }

    private void CountUse(MenuSnippetEntry snippet)
    {
        if (snippet.Id is { } id)
        {
            _usage?.Record(id);
        }
    }

    private async Task AskFieldsAsync(
        ActiveTarget target, ParsedTemplate template, int backspaces, string trailing, bool fromMenu, bool sendEnter)
    {
        CancelFields(FieldsCloseReason.Replaced);

        if (backspaces > 0)
        {
            // The trigger goes now: the user sees the template was recognised, and nothing is left to delete later.
            var removal = await _insertion.InsertAsync(
                new InsertionRequest(target, string.Empty, backspaces, PreferredStrategy: InsertionStrategyKind.SendInput),
                _stopping).ConfigureAwait(false);
            if (!removal.Succeeded)
            {
                return;
            }
        }

        if (target.Control.ElementId is not null)
        {
            // Before the prompt takes the focus, and once the target has had a moment to delete the trigger.
            await Task.Delay(BookmarkSettle, TimeProvider.System, _stopping).ConfigureAwait(false);
            await Task.Run(() => _resolver.BookmarkField(target), _stopping).ConfigureAwait(false);
        }

        var caret = target.Control.CaretBounds;
        // Filling the fields means copying, so {{clipboard}} keeps what was copied when the trigger fired.
        var variables = template.UsesClipboard ? new FrozenClipboard(_variables, _variables.GetClipboardText()) : _variables;
        var open = new OpenFields(target, template, variables, trailing, fromMenu, sendEnter, ++_fieldsSession);
        _openFields = open;
        _fieldPrompt!.Show(template.Fields, caret ?? _pointer.CursorAnchor(), target.Monitor, open.Session);
        Record(new FieldsShown(Now, target.ProcessName, template.Fields.Count, AnchoredToCaret: caret is not null));
    }

    private async Task FinishFieldsAsync(FieldsFinished finished)
    {
        if (_waiting is { } waiting && waiting.Session == finished.Session && finished.Values is null)
        {
            StopWaiting();
            Record(new FieldsClosed(Now, FieldsCloseReason.Cancelled)); // "Cancelar" on the waiting message
            return;
        }

        if (_openFields is not { } open || open.Session != finished.Session)
        {
            return; // an answer for a prompt that was already replaced
        }

        _openFields = null;
        if (finished.Values is null)
        {
            Record(new FieldsClosed(Now, FieldsCloseReason.Cancelled));
            return;
        }

        var rendered = Render(open.Template, finished.Values, open.Variables);
        var text = rendered.Text + open.Trailing;
        var caretOffset = CaretOffset(rendered, open.Trailing);
        if (_paused)
        {
            OfferText(text);
            return;
        }

        var target = await ReturnToAsync(open.Target).ConfigureAwait(false);
        if (target is null)
        {
            // Still on the page the values came from (another tab cannot be switched to from here): wait for the user.
            var timer = _time.CreateTimer(
                _ => _inbox.Writer.TryWrite(new WaitElapsed(open.Session)), null, _options.ReturnWaitTimeout, Timeout.InfiniteTimeSpan);
            _waiting = new Waiting(open.Target, text, caretOffset, open.FromMenu, open.SendEnter, open.Session, timer);
            _fieldPrompt!.ShowWaiting();
            return;
        }

        await DeliverAsync(target, text, caretOffset, open.FromMenu, open.SendEnter).ConfigureAwait(false);
    }

    private async Task DeliverAsync(ActiveTarget target, string text, int caretOffset, bool fromMenu, bool sendEnter)
    {
        var result = await ExpandAsync(target, text, 0, fromMenu, caretOffset, sendEnter).ConfigureAwait(false);
        if (result is { Succeeded: true } or { InputSent: true })
        {
            Record(new FieldsClosed(Now, FieldsCloseReason.Inserted));
            return;
        }

        OfferText(text);
    }

    private void OfferText(string text)
    {
        _fieldPrompt!.ShowNotInserted(text);
        Record(new FieldsClosed(Now, FieldsCloseReason.TargetLost));
    }

    /// <summary>While filled-in fields wait, every focus move checks whether the user is back in the original field.</summary>
    private async Task FocusMovedWhileWaitingAsync(HookEvent moved)
    {
        if (_waiting is { } waiting && !_paused)
        {
            var current = await CaptureTargetAsync().ConfigureAwait(false);
            if (_waiting == waiting && IsSameField(waiting.Target, current))
            {
                StopWaiting();
                _fieldPrompt!.Cancel(); // the "go back" message has done its job
                await DeliverAsync(current!, waiting.Text, waiting.CaretOffset, waiting.FromMenu, waiting.SendEnter).ConfigureAwait(false);
            }
        }

        if (moved is ForegroundChanged)
        {
            await HandleForegroundChangedAsync().ConfigureAwait(false);
        }
        else if (_open is null)
        {
            await RefreshCaptureUnlessSupersededAsync().ConfigureAwait(false);
        }
    }

    private Task GiveUpWaitingAsync(WaitElapsed elapsed)
    {
        if (_waiting is { } waiting && waiting.Session == elapsed.Session)
        {
            StopWaiting();
            OfferText(waiting.Text);
        }

        return Task.CompletedTask;
    }

    private void StopWaiting()
    {
        _waiting?.Timer.Dispose();
        _waiting = null;
    }

    /// <summary>Brings the original window back and waits until its original field has the focus again.</summary>
    private async Task<ActiveTarget?> ReturnToAsync(ActiveTarget original)
    {
        if (!_resolver.Activate(original))
        {
            return null;
        }

        var deadline = TimeProvider.System.GetTimestamp();
        var restored = false;
        while (true)
        {
            var current = await CaptureTargetAsync().ConfigureAwait(false);
            if (IsSameField(original, current))
            {
                return current;
            }

            if (!restored && IsSameWindowOtherField(original, current))
            {
                // The value was copied from the same page (2026-10-08): put the focus back instead of waiting for a click.
                restored = true;
                await Task.Run(() => _resolver.RestoreField(original), _stopping).ConfigureAwait(false);
                continue;
            }

            if (TimeProvider.System.GetElapsedTime(deadline) >= _options.FocusReturnTimeout)
            {
                return null;
            }

            // Real time on purpose: Windows moves the focus back on its own schedule, not the engine's clock.
            await Task.Delay(FocusReturnPoll, TimeProvider.System, _stopping).ConfigureAwait(false);
        }
    }

    private static readonly TimeSpan FocusReturnPoll = TimeSpan.FromMilliseconds(40);

    private static readonly TimeSpan BookmarkSettle = TimeSpan.FromMilliseconds(60);

    private static bool IsSameWindowOtherField(ActiveTarget original, ActiveTarget? current) =>
        current is not null
        && current.WindowHandle == original.WindowHandle
        && current.ProcessId == original.ProcessId
        && original.Control.ElementId is not null;

    /// <summary>Same window, control and (when known) UI Automation element: browser tabs share one window.</summary>
    private static bool IsSameField(ActiveTarget original, ActiveTarget? current) =>
        current is not null
        && current.WindowHandle == original.WindowHandle
        && current.ProcessId == original.ProcessId
        && (original.FocusHandle == 0 || current.FocusHandle == original.FocusHandle)
        && (original.Control.ElementId is null || current.Control.ElementId == original.Control.ElementId);

    /// <summary>Hides an open prompt without inserting (another template, pause, shutdown).</summary>
    private void CancelFields(FieldsCloseReason reason)
    {
        if (_openFields is null && _waiting is null)
        {
            return;
        }

        _openFields = null;
        StopWaiting();
        _fieldPrompt?.Cancel();
        Record(new FieldsClosed(Now, reason));
    }

    private RenderedTemplate Render(
        ParsedTemplate template, IReadOnlyDictionary<string, string> values, IVariableSource? variables = null) =>
        TemplateRenderer.Render(template, values, variables ?? _variables, CultureInfo.CurrentCulture);

    /// <summary><c>{{cursor}}</c> counts from the end of the text, so a trailing key typed after it moves it too.</summary>
    private static int CaretOffset(RenderedTemplate rendered, string trailing) =>
        rendered.CaretOffsetFromEnd == 0 ? 0 : rendered.CaretOffsetFromEnd + trailing.Length;

    private void OnFieldsFinished(int session, IReadOnlyDictionary<string, string>? values) =>
        _inbox.Writer.TryWrite(new FieldsFinished(session, values));

    private sealed record OpenFields(
        ActiveTarget Target, ParsedTemplate Template, IVariableSource Variables, string Trailing, bool FromMenu, bool SendEnter, int Session);

    private sealed record FieldsFinished(int Session, IReadOnlyDictionary<string, string>? Values) : EngineWork;

    private sealed record Waiting(
        ActiveTarget Target, string Text, int CaretOffset, bool FromMenu, bool SendEnter, int Session, ITimer Timer);

    private sealed record WaitElapsed(int Session) : EngineWork;

    /// <summary>The clipboard as it was when the fields prompt opened; the rest stays live. Lives only while the prompt is open.</summary>
    private sealed class FrozenClipboard(IVariableSource live, string? clipboard) : IVariableSource
    {
        public DateTimeOffset Now => live.Now;

        public string? GetClipboardText() => clipboard;

        public string? GetSelectionText() => live.GetSelectionText();
    }

    /// <summary>Date and time only: used when the host gives no variable source.</summary>
    private sealed class ClockVariables(TimeProvider time) : IVariableSource
    {
        public DateTimeOffset Now => time.GetLocalNow();

        public string? GetClipboardText() => null;

        public string? GetSelectionText() => null;
    }
}

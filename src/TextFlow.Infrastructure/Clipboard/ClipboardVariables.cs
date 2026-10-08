using TextFlow.Core.Templates;

namespace TextFlow.Infrastructure.Clipboard;

/// <summary>Runtime values for templates: the clock and the user's clipboard (read on demand, never cached).</summary>
public sealed class ClipboardVariables(ClipboardStrategy clipboard, TimeProvider time) : IVariableSource
{
    public DateTimeOffset Now => time.GetLocalNow();

    public string? GetClipboardText() => clipboard.ReadText();

    public string? GetSelectionText() => null; // {{selection}}: later (pendiente técnico 3)
}

using TextFlow.Contracts.Insertion;
using TextFlow.Contracts.Targeting;
using TextFlow.Core.Operations;
using TextFlow.Infrastructure.Clipboard;
using TextFlow.Infrastructure.Input;
using TextFlow.Infrastructure.Targeting;

namespace TextFlow.Infrastructure.Tests.Desktop;

/// <summary>
/// Real-desktop tests: they inject input into a window owned by the test process.
/// Skipped when Windows refuses to give that window the foreground (e.g. headless CI).
/// </summary>
[Trait("Category", "Desktop")]
[Collection(DesktopTestGroup.Name)]
public sealed class InsertionDesktopTests
{
    private const string Sentinel = "clipboard-del-usuario-✓";
    private const string Sample = "Hola, ¿qué tal? Ñandú — 42 € 👍\r\nSegunda línea";

    [SkippableFact]
    public async Task ClipboardStrategy_PastesText_AndRestoresUserClipboard()
    {
        using var window = await TestWindow.OpenAsync();
        Skip.IfNot(window.IsForeground, "Test window could not take the foreground.");
        await window.SetClipboardTextAsync(Sentinel);
        using var strategy = new ClipboardStrategy();

        var result = await strategy.InsertAsync(new InsertionRequest(window.CaptureTarget(), Sample), CancellationToken.None);

        Assert.Equal(InsertionStatus.Success, result.Status);
        Assert.Equal(Sample, await window.GetTextAsync());
        Assert.Equal(Sentinel, await window.GetClipboardTextAsync());
    }

    [SkippableFact]
    public async Task SendInputStrategy_TypesUnicodeAndNewlines()
    {
        using var window = await TestWindow.OpenAsync();
        Skip.IfNot(window.IsForeground, "Test window could not take the foreground.");

        var result = await new SendInputStrategy().InsertAsync(new InsertionRequest(window.CaptureTarget(), Sample), CancellationToken.None);

        Assert.Equal(InsertionStatus.Success, result.Status);
        Assert.Equal(Sample, await WaitForTextAsync(window, Sample));
    }

    [SkippableFact]
    public async Task Backspaces_DeleteTypedTrigger_BeforeInserting()
    {
        using var window = await TestWindow.OpenAsync(initialText: "Hola ;firma");
        Skip.IfNot(window.IsForeground, "Test window could not take the foreground.");
        using var strategy = new ClipboardStrategy();

        await strategy.InsertAsync(new InsertionRequest(window.CaptureTarget(), "Saludos", BackspacesBefore: ";firma".Length), CancellationToken.None);

        Assert.Equal("Hola Saludos", await window.GetTextAsync());
    }

    [SkippableFact]
    public async Task CaretOffset_PlacesCaretInsideInsertedText()
    {
        using var window = await TestWindow.OpenAsync();
        Skip.IfNot(window.IsForeground, "Test window could not take the foreground.");

        await new SendInputStrategy().InsertAsync(
            new InsertionRequest(window.CaptureTarget(), "Estimado :", CaretOffsetFromEnd: 1), CancellationToken.None);
        await WaitForTextAsync(window, "Estimado :");
        await new SendInputStrategy().InsertAsync(new InsertionRequest(window.CaptureTarget(), "Ana"), CancellationToken.None);

        Assert.Equal("Estimado Ana:", await WaitForTextAsync(window, "Estimado Ana:"));
    }

    [SkippableFact]
    public async Task Coordinator_RefusesToInsert_WhenTargetLostForeground()
    {
        ActiveTarget staleTarget;
        using (var first = await TestWindow.OpenAsync())
        {
            Skip.IfNot(first.IsForeground, "Test window could not take the foreground.");
            staleTarget = first.CaptureTarget();
        }

        using var second = await TestWindow.OpenAsync();
        Skip.IfNot(second.IsForeground, "Second window could not take the foreground.");
        using var clipboard = new ClipboardStrategy();
        var coordinator = new InsertionCoordinator(
            new Win32TargetResolver(new UiaFocusedControlInspector()), [clipboard, new SendInputStrategy()], new InsertionOptions());

        var result = await coordinator.InsertAsync(new InsertionRequest(staleTarget, "no debe llegar"), CancellationToken.None);

        Assert.Equal(InsertionStatus.TargetChanged, result.Status);
        Assert.Equal(string.Empty, await second.GetTextAsync());
    }

    /// <summary>SendInput is asynchronous for the receiving thread; poll briefly for the final text.</summary>
    private static async Task<string> WaitForTextAsync(TestWindow window, string expected)
    {
        var text = string.Empty;
        for (var i = 0; i < 50 && text != expected; i++)
        {
            await Task.Delay(20);
            text = await window.GetTextAsync();
        }

        return text;
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DesktopTestGroup
{
    public const string Name = "Desktop";
}

using TextFlow.Infrastructure.Clipboard;

namespace TextFlow.Infrastructure.Tests.Desktop;

/// <summary>
/// {{clipboard}} against the real clipboard (H5.3). No window and no focus change; the user's clipboard text is put back.
/// </summary>
[Trait("Category", "Desktop")]
[Collection(DesktopTestGroup.Name)]
public sealed class ClipboardVariablesDesktopTests
{
    [Fact]
    public void ReadsTheUserClipboardText()
    {
        const string Copied = "pedido 4411 — Ñandú 👍\r\nsegunda línea";
        var previous = OnSta(() => System.Windows.Forms.Clipboard.ContainsText() ? System.Windows.Forms.Clipboard.GetText() : null);
        using var strategy = new ClipboardStrategy();
        var variables = new ClipboardVariables(strategy, TimeProvider.System);
        try
        {
            OnSta(() => System.Windows.Forms.Clipboard.SetText(Copied));

            Assert.Equal(Copied, variables.GetClipboardText());
        }
        finally
        {
            OnSta(() =>
            {
                if (previous is null)
                {
                    System.Windows.Forms.Clipboard.Clear();
                }
                else
                {
                    System.Windows.Forms.Clipboard.SetText(previous);
                }
            });
        }
    }

    [Fact]
    public void WithNoText_ReturnsNull()
    {
        var previous = OnSta(() => System.Windows.Forms.Clipboard.ContainsText() ? System.Windows.Forms.Clipboard.GetText() : null);
        using var strategy = new ClipboardStrategy();
        try
        {
            OnSta(System.Windows.Forms.Clipboard.Clear);

            Assert.Null(strategy.ReadText());
        }
        finally
        {
            if (previous is not null)
            {
                OnSta(() => System.Windows.Forms.Clipboard.SetText(previous));
            }
        }
    }

    private static void OnSta(Action action) => OnSta<object?>(() =>
    {
        action();
        return null;
    });

    private static T OnSta<T>(Func<T> func)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = func();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return error is null ? result : throw error;
    }
}

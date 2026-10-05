using TextFlow.Core.Templates;
using Windows.Graphics;

namespace TextFlow.App.Fields;

/// <summary>
/// Design check (H5.2): <c>TextFlow.exe --preview-fields[=light|dark]</c> shows the field prompt with made-up field
/// names for a few seconds and exits. No hook, no library: safe to capture.
/// </summary>
internal static class FieldsPreview
{
    private const string Argument = "--preview-fields";
    private static readonly TimeSpan Visible = TimeSpan.FromSeconds(8);

    public static bool TryShow(string[] args)
    {
        var flag = args.FirstOrDefault(a => a.StartsWith(Argument, StringComparison.OrdinalIgnoreCase));
        if (flag is null)
        {
            return false;
        }

        var window = new FieldPromptWindow();
        window.ApplyTheme(flag.EndsWith("dark", StringComparison.OrdinalIgnoreCase) ? AppTheme.Dark
            : flag.EndsWith("light", StringComparison.OrdinalIgnoreCase) ? AppTheme.Light
            : AppTheme.System);

        TemplateField[] fields = [new("cliente", null), new("mes", null), new("año", null), new("monto", null)];
        var size = FieldPromptWindow.SizeFor(fields.Length, Menu.PopupInterop.DpiScale(window.Hwnd));
        window.ShowFields(fields, new RectInt32(300, 200, size.Width, size.Height));

        var timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = Visible;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => Microsoft.UI.Xaml.Application.Current.Exit();
        timer.Start();
        return true;
    }
}

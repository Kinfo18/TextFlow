using TextFlow.Core.Menus;
using Windows.Graphics;

namespace TextFlow.App.Menu;

/// <summary>
/// Design check for the group menu (H4.1): <c>TextFlow.exe --preview-menu[=light|dark] [--preview-at=X,Y]</c> shows the
/// popup with made-up entries for a few seconds and exits. <c>--preview-at</c> (physical pixels) puts it on another
/// monitor, to check DPI changes. Never shows the user's library, so it can be captured safely.
/// </summary>
internal static partial class MenuPreview
{
    private const string Argument = "--preview-menu";
    private const string AtArgument = "--preview-at=";
    private static readonly TimeSpan Visible = TimeSpan.FromSeconds(8);

    public static bool TryShow(string[] args)
    {
        var flag = args.FirstOrDefault(a => a.StartsWith(Argument, StringComparison.OrdinalIgnoreCase));
        if (flag is null)
        {
            return false;
        }

        var theme = flag.EndsWith("dark", StringComparison.OrdinalIgnoreCase) ? AppTheme.Dark
            : flag.EndsWith("light", StringComparison.OrdinalIgnoreCase) ? AppTheme.Light
            : AppTheme.System;

        var popup = new MenuPopup();
        popup.Prime();
        popup.ApplyTheme(theme);

        var (x, y) = Position(args);
        var state = MenuNavigator.Apply(MenuNavigator.Open(Sample()), MenuInput.Down).State;
        var size = popup.Present(state, MonitorScale(x, y));
        popup.ShowAt(new RectInt32(x, y, size.Width, size.Height));

        var timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = Visible;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => Microsoft.UI.Xaml.Application.Current.Exit();
        timer.Start();
        return true;
    }

    private static (int X, int Y) Position(string[] args)
    {
        var at = args.FirstOrDefault(a => a.StartsWith(AtArgument, StringComparison.OrdinalIgnoreCase))?[AtArgument.Length..].Split(',');
        return at is [var x, var y] && int.TryParse(x, out var px) && int.TryParse(y, out var py) ? (px, py) : (300, 300);
    }

    private static double MonitorScale(int x, int y)
    {
        var monitor = MonitorFromPoint(new Point { X = x, Y = y }, MonitorDefaultToNearest);
        return GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1.0;
    }

    private const uint MonitorDefaultToNearest = 2;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(Point point, uint flags);

    [System.Runtime.InteropServices.LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

    private static GroupMenu Sample() => new("menu:preview", "LC", true,
    [
        new MenuSnippetEntry("No confirmado", "x"),
        new MenuSnippetEntry("Cliente ausente en la entrega", "x"),
        new MenuGroupEntry("Reasignación", [new MenuSnippetEntry("Otro repartidor", "x")]),
        new MenuSnippetEntry("Revisa la dirección antes de enviar", string.Empty),
        new MenuSnippetEntry("Pedido duplicado", "x"),
        new MenuSnippetEntry("Local cerrado por inventario", "x"),
    ]);
}

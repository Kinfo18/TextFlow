using TextFlow.Core.Menus;
using Windows.Graphics;

namespace TextFlow.App.Menu;

/// <summary>
/// Design check for the group menu (H4.1): <c>TextFlow.exe --preview-menu[=light|dark]</c> shows the popup with made-up
/// entries for a few seconds and exits. Never shows the user's library, so it can be captured safely.
/// </summary>
internal static class MenuPreview
{
    private const string Argument = "--preview-menu";
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

        var state = MenuNavigator.Apply(MenuNavigator.Open(Sample()), MenuInput.Down).State;
        var size = popup.Present(state, PopupInterop.DpiScale(popup.Hwnd));
        popup.ShowAt(new RectInt32(300, 300, size.Width, size.Height));

        var timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = Visible;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => Microsoft.UI.Xaml.Application.Current.Exit();
        timer.Start();
        return true;
    }

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

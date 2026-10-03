using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace TextFlow.App;

/// <summary>Main window shell (spec §23): Inicio · Snippets · Configuración · Diagnóstico. Sections fill in during H1–H5.</summary>
public sealed partial class MainWindow : Window
{
    private static readonly Dictionary<string, (string Title, string Body)> Sections = new()
    {
        ["home"] = ("Inicio", string.Empty), // filled from the engine state
        ["snippets"] = ("Snippets", "Biblioteca: grupos, abreviaturas e importación de aText (H2–H3)."),
        ["settings"] = ("Configuración", "Sonido y volumen, arranque con Windows, hotkey de pausa, exclusiones (H4)."),
        ["diagnostics"] = ("Diagnóstico", "Métricas técnicas sin contenido: inserciones, destinos rechazados, latencias (H5)."),
    };

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.Resize(new SizeInt32(1100, 720));
        Show("home");
    }

    private string _current = "home";

    /// <summary>Re-reads the engine state (pause, library) into the visible section.</summary>
    public void Refresh() => Show(_current);

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag })
        {
            Show(tag);
        }
    }

    private void Show(string tag)
    {
        _current = tag;
        var (title, body) = Sections[tag];
        SectionTitle.Text = title;
        SectionBody.Text = tag == "home" ? HomeStatus() : body;
    }

    private static string HomeStatus()
    {
        if (App.Current.StartupError is { } startupError)
        {
            return $"TextFlow no pudo arrancar y las expansiones están desactivadas.\n\n{startupError}\n\nCierra esta ventana para salir.";
        }

        if (App.Current.Engine is not { } engine)
        {
            return "El motor se está iniciando…";
        }

        var state = engine.IsPaused
            ? "Expansiones en pausa. Reanúdalas desde el icono de la bandeja."
            : "Expansiones activas. Escribe una abreviatura en cualquier aplicación.";
        var shortcut = App.Current.PauseHotkey switch
        {
            { Registered: true } hotkey => $"Atajo para pausar/reanudar: {hotkey.Gesture}.",
            { } hotkey => $"El atajo {hotkey.Gesture} lo usa otra aplicación; pausa desde la bandeja.",
            null => string.Empty,
        };
        var library = engine.Library switch
        {
            { Error: { } error } => $"No se pudo cargar la biblioteca: {error}",
            { Menus: 0, Commands: 0 } => "Sin biblioteca: indica la ruta de tu backup de aText en \"ATextBackupPath\" de settings.json.",
            var loaded => $"Biblioteca cargada: {loaded.Menus} menús y {loaded.Commands} comandos directos.",
        };

        return string.Join("\n\n", new[] { state, shortcut, library }.Where(s => s.Length > 0));
    }
}

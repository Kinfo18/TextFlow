using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace TextFlow.App;

/// <summary>Main window shell (spec §23): Inicio · Snippets · Configuración · Diagnóstico. Sections fill in during H1–H5.</summary>
public sealed partial class MainWindow : Window
{
    private static readonly Dictionary<string, (string Title, string Body)> Sections = new()
    {
        ["home"] = ("Inicio", "Estado del motor, snippets recientes y accesos rápidos (H1)."),
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

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag })
        {
            Show(tag);
        }
    }

    private void Show(string tag)
    {
        var (title, body) = Sections[tag];
        SectionTitle.Text = title;
        SectionBody.Text = body;
    }
}

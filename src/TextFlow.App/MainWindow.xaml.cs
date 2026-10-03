using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using TextFlow.App.Engine;
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

    private string _current = "home";
    private string? _actionError;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.Resize(new SizeInt32(1100, 720));
        Show("home");
    }

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
        SectionBody.Text = tag == "home" ? HomeStatus() + (_actionError is { } error ? $"\n\n{error}" : string.Empty) : body;
        HomeActions.Visibility = tag == "home" && App.Current.Engine is not null ? Visibility.Visible : Visibility.Collapsed;
        ReloadLibraryButton.IsEnabled = App.Current.Library?.Status.SourcePath is not null;
    }

    /// <remarks>
    /// async void: an escaping exception would freeze WinUI (its error reporting deadlocked here), so every failure
    /// is caught and shown on Inicio. Classic picker + InitializeWithWindow: the WinAppSDK picker failed with
    /// RPC_E_WRONG_THREAD in this unpackaged app.
    /// </remarks>
    private async void OnChooseLibrary(object sender, RoutedEventArgs e)
    {
        ChooseLibraryButton.IsEnabled = false;
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(".atext");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            if (await picker.PickSingleFileAsync() is { Path: { Length: > 0 } path })
            {
                await App.Current.ChooseLibraryAsync(path);
            }

            _actionError = null;
        }
        catch (Exception ex)
        {
            _actionError = $"No se pudo elegir el archivo: {ex.Message}";
            App.Current.RecordFault(ex);
        }
        finally
        {
            ChooseLibraryButton.IsEnabled = true;
            Refresh();
        }
    }

    private async void OnReloadLibrary(object sender, RoutedEventArgs e)
    {
        if (App.Current.Library is not { } library)
        {
            return;
        }

        ReloadLibraryButton.IsEnabled = false;
        try
        {
            await library.ReimportAsync(CancellationToken.None);
            _actionError = null;
        }
        catch (Exception ex)
        {
            _actionError = $"No se pudo recargar: {ex.Message}";
            App.Current.RecordFault(ex);
        }
        finally
        {
            Refresh();
        }
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

        var hook = engine.HookReinstalls > 0
            ? $"Windows retiró el hook de teclado y TextFlow lo reinstaló {engine.HookReinstalls} {(engine.HookReinstalls == 1 ? "vez" : "veces")}."
            : string.Empty;

        return string.Join("\n\n", new[] { state, shortcut, LibraryText(App.Current.Library?.Status), hook }.Where(s => s.Length > 0));
    }

    private static string LibraryText(LibraryStatus? library)
    {
        if (library?.Summary is not { } summary || (summary.Menus == 0 && summary.Commands == 0))
        {
            return library?.Error is { } failed
                ? $"No se pudo importar la biblioteca: {failed}"
                : "Biblioteca vacía: importa tu backup de aText (.atext) para empezar a expandir.";
        }

        var text = $"Biblioteca de TextFlow: {summary.Menus} menús y {summary.Commands} comandos "
            + $"({summary.DirectTriggers} abreviaturas se expanden al escribirlas)."
            + (summary.Issues > 0 ? $" {summary.Issues} avisos en la última importación." : string.Empty);
        if (library.SourcePath is { } source)
        {
            text += $"\nSe importa de «{Path.GetFileName(source)}» y se actualiza sola cuando ese archivo cambia.";
        }

        return library.Error is { } error
            ? $"{text}\n\nLa última importación falló ({error}); se mantiene la biblioteca anterior."
            : text;
    }
}

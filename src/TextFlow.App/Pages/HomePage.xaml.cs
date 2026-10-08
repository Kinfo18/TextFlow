using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TextFlow.App.Engine;
using Windows.Storage.Pickers;

namespace TextFlow.App.Pages;

/// <summary>Inicio: engine state, pause shortcut, library counts and the library actions (import, export, restore).</summary>
public sealed partial class HomePage : Page, IRefreshable
{
    private string? _actionError;

    public HomePage()
    {
        InitializeComponent();
        Refresh();
    }

    /// <summary>Re-reads the engine and library state.</summary>
    public void Refresh()
    {
        SectionBody.Text = HomeStatus() + (_actionError is { } error ? $"\n\n{error}" : string.Empty);
        HomeActions.Visibility = App.Current.Engine is not null ? Visibility.Visible : Visibility.Collapsed;
        ReloadLibraryButton.IsEnabled = App.Current.Library?.Status.SourcePath is not null;
        UpdateBar.IsOpen = App.Current.UpdateVersion is not null;
        UpdateBar.Message = $"TextFlow {App.Current.UpdateVersion} ya está descargado. Se aplica al reiniciar (unos segundos).";
    }

    private void OnRestartToUpdate(object sender, RoutedEventArgs e) => App.Current.RestartToUpdate();

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
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Current.MainWindowHandle);
            if (await picker.PickSingleFileAsync() is { Path: { Length: > 0 } path } && App.Current.Library is { } library)
            {
                var (import, preview) = await library.PreviewATextAsync(path, CancellationToken.None);
                if (await ImportDialog.ConfirmAsync(XamlRoot, $"Importar «{Path.GetFileName(path)}»", preview))
                {
                    await App.Current.ChooseLibraryAsync(path, import);
                }
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

    /// <remarks>async void: every failure is caught and shown on Inicio (an escaping exception freezes WinUI).</remarks>
    private async void OnExportLibrary(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"TextFlow {DateTime.Now:yyyy-MM-dd}",
            };
            picker.FileTypeChoices.Add("Biblioteca de TextFlow", [".json"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Current.MainWindowHandle);
            if (await picker.PickSaveFileAsync() is { Path: { Length: > 0 } path } && App.Current.Library is { } library)
            {
                await library.ExportAsync(path, CancellationToken.None);
                _actionError = $"Biblioteca exportada a «{Path.GetFileName(path)}».";
            }
        }
        catch (Exception ex)
        {
            _actionError = $"No se pudo exportar: {ex.Message}";
            App.Current.RecordFault(ex);
        }
        finally
        {
            Refresh();
        }
    }

    private async void OnImportLibraryFile(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(".json");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Current.MainWindowHandle);
            if (await picker.PickSingleFileAsync() is { Path: { Length: > 0 } path } && App.Current.Library is { } library)
            {
                var (root, preview) = await library.PreviewLibraryFileAsync(path, CancellationToken.None);
                var following = library.Status.SourcePath is { } source ? Path.GetFileName(source) : null;
                if (await ImportDialog.ConfirmAsync(XamlRoot, $"Importar «{Path.GetFileName(path)}»", preview, following))
                {
                    await App.Current.ImportLibraryFileAsync(root);
                }
            }

            _actionError = null;
        }
        catch (Exception ex)
        {
            _actionError = $"No se pudo importar: {ex.Message}";
            App.Current.RecordFault(ex);
        }
        finally
        {
            Refresh();
        }
    }

    private async void OnRestoreBackup(object sender, RoutedEventArgs e)
    {
        try
        {
            if (App.Current.Library is not { } library
                || await ImportDialog.PickBackupAsync(XamlRoot, library.ListBackups()) is not { } backup)
            {
                return;
            }

            var (root, preview) = await library.PreviewBackupAsync(backup, CancellationToken.None);
            var following = library.Status.SourcePath is { } source ? Path.GetFileName(source) : null;
            var title = $"Restaurar la copia del {backup.CreatedAt.ToString("d 'de' MMMM, HH:mm", System.Globalization.CultureInfo.CurrentCulture)}";
            if (await ImportDialog.ConfirmAsync(XamlRoot, title, preview, following))
            {
                await App.Current.ImportLibraryFileAsync(root);
                _actionError = "Copia restaurada. La biblioteca anterior quedó guardada como otra copia.";
            }
        }
        catch (Exception ex)
        {
            _actionError = $"No se pudo restaurar: {ex.Message}";
            App.Current.RecordFault(ex);
        }
        finally
        {
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

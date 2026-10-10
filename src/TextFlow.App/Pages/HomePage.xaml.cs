using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TextFlow.App.Engine;
using TextFlow.Core.Diagnostics;
using TextFlow.Core.Library;
using TextFlow.Infrastructure.Diagnostics;
using Windows.Storage.Pickers;

namespace TextFlow.App.Pages;

/// <summary>
/// Inicio (C8): engine state with a pause switch, today's numbers, the most used snippets and the library with its
/// actions (import, export, restore). Numbers only, never content.
/// </summary>
public sealed partial class HomePage : Page, IRefreshable
{
    private const int TopCount = 5;
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");

    private string? _actionError;
    private bool _loading;

    public HomePage()
    {
        InitializeComponent();
        Refresh();
    }

    /// <summary>Re-reads the engine, today's log, the use counts and the library state.</summary>
    public void Refresh()
    {
        _loading = true;
        try
        {
            ShowState();
            ShowToday();
            LibraryBody.Text = LibraryText(App.Current.Library?.Status);
            HomeActions.Visibility = App.Current.Engine is not null ? Visibility.Visible : Visibility.Collapsed;
            ReloadLibraryButton.Visibility = App.Current.Library?.Status.SourcePath is not null ? Visibility.Visible : Visibility.Collapsed;
            UpdateBar.IsOpen = App.Current.UpdateVersion is not null;
            UpdateBar.Message = $"TextFlow {App.Current.UpdateVersion} ya está descargado. Se aplica al reiniciar (unos segundos).";
            MessageBar.IsOpen = _actionError is not null;
            MessageBar.Message = _actionError ?? string.Empty;
            MessageBar.Severity = _actionError?.StartsWith("No se pudo", StringComparison.Ordinal) == true ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
        }
        finally
        {
            _loading = false;
        }

        _ = ShowTopAsync();
    }

    private void ShowState()
    {
        var engine = App.Current.Engine;
        var (title, detail, glyph, brush) = (App.Current.StartupError, engine) switch
        {
            ({ } error, _) => ("TextFlow no pudo arrancar", $"{error}\nCierra esta ventana para salir.", "\uE783", "SystemFillColorCriticalBrush"),
            (_, null) => ("Iniciando…", "El motor se está preparando.", "\uE895", "SystemFillColorNeutralBrush"),
            (_, { IsPaused: true }) => ("Expansiones en pausa", "Nada se expande hasta que las reanudes.", "\uE769", "SystemFillColorCautionBrush"),
            _ => ("Expansiones activas", "Escribe una abreviatura en cualquier aplicación.", "\uE73E", "SystemFillColorSuccessBrush"),
        };

        var shortcut = App.Current.PauseHotkey switch
        {
            { Registered: true } hotkey => $"Pausar o reanudar: {hotkey.Gesture}.",
            { } hotkey => $"El atajo {hotkey.Gesture} lo usa otra aplicación: pausa desde aquí o desde la bandeja.",
            null => null,
        };
        var hook = engine is { HookReinstalls: > 0 } e
            ? $"Windows retiró el hook de teclado y TextFlow lo reinstaló {e.HookReinstalls} {(e.HookReinstalls == 1 ? "vez" : "veces")}."
            : null;

        StateTitle.Text = title;
        StateDetail.Text = string.Join(" ", new[] { detail, shortcut, hook }.OfType<string>());
        StateIcon.Glyph = glyph;
        StateBadge.Background = ThemeBrushes.Get(brush);
        ActiveSwitch.Visibility = engine is not null && App.Current.StartupError is null ? Visibility.Visible : Visibility.Collapsed;
        ActiveSwitch.IsOn = engine is { IsPaused: false };
    }

    private void ShowToday()
    {
        var summary = TodaySummary();
        TodayPanel.Visibility = summary is null ? Visibility.Collapsed : Visibility.Visible;
        if (summary is null)
        {
            return;
        }

        TodayExpansions.Text = summary.Expansions.ToString("N0", Es);
        TodaySuccess.Text = summary.SuccessRate is { } rate ? rate.ToString("P1", Es) : "—";
        TodayLatency.Text = summary.MedianMs is { } ms ? $"{ms:0} ms" : "—";
    }

    /// <summary>Today's content-free log; null when it cannot be read (the tiles are hidden, nothing else breaks).</summary>
    private static DiagnosticSummary? TodaySummary()
    {
        try
        {
            var log = App.Current.Services.GetRequiredService<DiagnosticFileSink>();
            return DiagnosticSummary.From(log.Read(DateOnly.FromDateTime(DateTime.Now)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Fire and forget from Refresh: every failure is caught and the list keeps what it showed.</summary>
    private async Task ShowTopAsync()
    {
        if (App.Current.Library is not { } library)
        {
            return;
        }

        try
        {
            var usage = await library.LoadUsageAsync(CancellationToken.None);
            var top = UsageReport.Top(library.Service.Current, usage, TopCount);
            TopList.Children.Clear();
            if (top.Count == 0)
            {
                TopList.Children.Add(new TextBlock
                {
                    Text = "Aún no hay datos: TextFlow cuenta cada expansión desde esta versión (solo cuántas veces, nunca el texto).",
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap,
                });
                return;
            }

            foreach (var used in top)
            {
                TopList.Children.Add(TopRow(used));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            App.Current.RecordFault(ex);
        }
    }

    private static Grid TopRow(UsedSnippet used)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var abbreviation = used.Snippet.Abbreviations.Count > 0 ? used.Snippet.Abbreviations[0] : used.Snippet.Name;
        var where = used.GroupName.Length > 0 ? used.GroupName : "Biblioteca";
        var label = new StackPanel();
        label.Children.Add(new TextBlock
        {
            Text = abbreviation,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeBrushes.Get("AccentTextFillColorPrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        label.Children.Add(new TextBlock { Text = where, FontSize = 12, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis });
        var count = new TextBlock
        {
            Text = used.Uses == 1 ? "1 vez" : $"{used.Uses.ToString("N0", Es)} veces",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(count, 1);
        grid.Children.Add(label);
        grid.Children.Add(count);
        AutomationProperties.SetName(grid, $"{abbreviation}, {where}: {count.Text}");
        return grid;
    }

    private void OnActiveToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading && App.Current.Engine is { } engine && engine.IsPaused == ActiveSwitch.IsOn)
        {
            engine.TogglePause(); // PausedChanged refreshes the window and the tray icon
        }
    }

    private void OnShowDiagnostics(object sender, RoutedEventArgs e) => App.Current.ShowSection("diagnostics");

    private void OnMessageClosed(InfoBar sender, InfoBarClosedEventArgs args) => _actionError = null;

    private void OnRestartToUpdate(object sender, RoutedEventArgs e) => App.Current.RestartToUpdate();

    /// <remarks>
    /// async void: an escaping exception would freeze WinUI (its error reporting deadlocked here), so every failure
    /// is caught and shown on Inicio. Classic picker + InitializeWithWindow: the WinAppSDK picker failed with
    /// RPC_E_WRONG_THREAD in this unpackaged app.
    /// </remarks>
    private async void OnChooseLibrary(object sender, RoutedEventArgs e)
    {
        ChooseLibraryItem.IsEnabled = false;
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
            ChooseLibraryItem.IsEnabled = true;
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

    private static string LibraryText(LibraryStatus? library)
    {
        if (library?.Summary is not { } summary || (summary.Menus == 0 && summary.Commands == 0))
        {
            return library?.Error is { } failed
                ? $"No se pudo importar la biblioteca: {failed}"
                : "Biblioteca vacía: importa tu backup de aText (.atext) desde «Más» para empezar a expandir.";
        }

        var text = $"{summary.Menus} menús y {summary.Commands} comandos "
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

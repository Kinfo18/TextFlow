using Microsoft.UI.Xaml;
using TextFlow.App.Updates;

namespace TextFlow.App.Pages;

/// <summary>"Acerca de TextFlow": installed version and a manual check for updates (ADR-0004).</summary>
public sealed partial class SettingsPage
{
    private void LoadAbout()
    {
        var updater = App.Current.Updater;
        VersionText.Text = $"TextFlow {updater?.CurrentVersion ?? AppVersion.Display}";
        var installed = updater?.IsInstalled == true;
        InstallKindText.Text = installed
            ? "Instalada: busca actualizaciones sola al arrancar y cada 6 horas."
            : "Copia portable o de desarrollo: no se actualiza sola. Para recibir actualizaciones, instala TextFlow con el instalador de GitHub.";
        CheckUpdatesButton.IsEnabled = installed;
        if (App.Current.UpdateVersion is { } ready)
        {
            ShowReady(ready);
        }
    }

    /// <remarks>async void: every outcome is shown here, CheckNowAsync does not throw.</remarks>
    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        if (App.Current.Updater is not { } updater)
        {
            return;
        }

        CheckUpdatesButton.IsEnabled = false;
        UpdateProgress.IsActive = true;
        ShowUpdateStatus("Buscando actualizaciones…");
        var ui = DispatcherQueue;
        var result = await updater.CheckNowAsync(percent => ui.TryEnqueue(() => ShowUpdateStatus($"Descargando… {percent} %")));
        UpdateProgress.IsActive = false;
        CheckUpdatesButton.IsEnabled = true;
        switch (result)
        {
            case UpdateCheck.Ready:
                ShowReady(updater.ReadyVersion);
                break;
            case UpdateCheck.UpToDate:
                ShowUpdateStatus($"Tienes la última versión ({updater.CurrentVersion}).");
                break;
            case UpdateCheck.Failed:
                ShowUpdateStatus("No se pudo consultar GitHub. Revisa la conexión a internet e inténtalo de nuevo.");
                break;
            case UpdateCheck.NotInstalled:
                ShowUpdateStatus("Esta copia no se actualiza sola.");
                break;
        }
    }

    private void ShowReady(string? version)
    {
        ShowUpdateStatus($"TextFlow {version} está descargado. Se aplica al reiniciar (unos segundos).");
        RestartToUpdateButton.Visibility = Visibility.Visible;
    }

    private void ShowUpdateStatus(string text)
    {
        UpdateStatus.Text = text;
        UpdateStatus.Visibility = Visibility.Visible;
    }

    private void OnRestartToUpdate(object sender, RoutedEventArgs e) => App.Current.RestartToUpdate();
}

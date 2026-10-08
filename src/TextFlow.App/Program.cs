using TextFlow.Infrastructure.Windows;
using Velopack;

namespace TextFlow.App;

/// <summary>
/// Own entry point (DISABLE_XAML_GENERATED_MAIN): Velopack must run before WinUI starts, because the installer and
/// the updater launch the exe with hook arguments and expect it to exit at once.
/// </summary>
public static class Program
{
    [STAThread]
    private static void Main()
    {
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => RemoveStartWithWindows())
            .Run();
        XamlGeneratedProgram.XamlGeneratedMain();
    }

    /// <summary>Uninstalling must not leave a Run entry that points at a deleted exe.</summary>
    private static void RemoveStartWithWindows()
    {
        try
        {
            new StartupRegistration(App.InstanceName, Environment.ProcessPath!).Apply(enabled: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // policy-locked registry: nothing else to do while uninstalling
        }
    }
}

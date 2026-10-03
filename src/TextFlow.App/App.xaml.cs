using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;
using TextFlow.Core.Diagnostics;
using TextFlow.Infrastructure.Diagnostics;

namespace TextFlow.App;

/// <summary>
/// Composition root: builds the generic host (DI, lifetime) and shows the main window.
/// Engine, tray and single instance arrive in H1 (docs/v0.1-plan.md).
/// </summary>
public partial class App : Application, IDisposable
{
    private readonly IHost _host;
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(AppPaths.Default);
                services.AddSingleton<DiagnosticFileSink>(sp => new DiagnosticFileSink(sp.GetRequiredService<AppPaths>().Logs));
                services.AddSingleton<IDiagnosticSink>(sp => sp.GetRequiredService<DiagnosticFileSink>());
                services.AddSingleton<MainWindow>();
            })
            .Build();
    }

    public static new App Current => (App)Application.Current;

    public IServiceProvider Services => _host.Services;

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        await _host.StartAsync();
        Services.GetRequiredService<IDiagnosticSink>().Record(new EngineStateChanged(DateTimeOffset.Now, EngineState.Starting));

        _window = Services.GetRequiredService<MainWindow>();
        _window.Closed += async (_, _) =>
        {
            Services.GetRequiredService<IDiagnosticSink>().Record(new EngineStateChanged(DateTimeOffset.Now, EngineState.Stopped));
            await _host.StopAsync();
            Dispose();
        };
        _window.Activate();
    }

    public void Dispose()
    {
        _host.Dispose();
        GC.SuppressFinalize(this);
    }
}

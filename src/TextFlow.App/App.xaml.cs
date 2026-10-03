using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using TextFlow.App.Engine;
using TextFlow.App.Menu;
using TextFlow.Core.Diagnostics;
using TextFlow.Infrastructure.Diagnostics;
using TextFlow.Infrastructure.Windows;

namespace TextFlow.App;

/// <summary>
/// Composition root: generic host (DI, lifetime), expansion engine, group menu popup and tray icon.
/// TextFlow lives in the notification area: closing the window only hides it; "Salir" in the tray exits.
/// </summary>
public partial class App : Application, IDisposable
{
    /// <summary>Start in the tray without showing the window (used by start with Windows, H1.3).</summary>
    private const string BackgroundArgument = "--background";

    private readonly IHost _host;
    private DispatcherQueue? _ui;
    private EngineHost? _engine;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private bool _exiting;

    public App()
    {
        InitializeComponent();
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown; // hiding the last window must not exit
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(AppPaths.Default);
                services.AddSingleton<DiagnosticFileSink>(sp => new DiagnosticFileSink(sp.GetRequiredService<AppPaths>().Logs));
                services.AddSingleton<IDiagnosticSink>(sp => sp.GetRequiredService<DiagnosticFileSink>());
            })
            .Build();
    }

    public static new App Current => (App)Application.Current;

    public IServiceProvider Services => _host.Services;

    internal EngineHost? Engine => _engine;

    /// <summary>Why startup failed ("Type: message"), shown on the Inicio page; null when it worked.</summary>
    internal string? StartupError { get; private set; }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _ui = DispatcherQueue.GetForCurrentThread();
        try
        {
            await StartAsync(_ui);
        }
        catch (Exception ex)
        {
            // Never leave a half-started process: report it in the window (closing it then exits).
            StartupError = $"{ex.GetType().Name}: {ex.Message}";
            RecordFault(ex);
            ShowWindow();
            return;
        }

        if (!Environment.GetCommandLineArgs().Contains(BackgroundArgument, StringComparer.OrdinalIgnoreCase))
        {
            ShowWindow();
        }
    }

    private async Task StartAsync(DispatcherQueue ui)
    {
        await _host.StartAsync();

        var popup = new MenuPopup();
        popup.Prime();

        var paths = Services.GetRequiredService<AppPaths>();
        _engine = new EngineHost(AppSettings.Load(paths.Settings), new WinUiMenuPresenter(ui, popup), Services.GetRequiredService<IDiagnosticSink>());
        await _engine.StartAsync();

        var tray = new TrayIcon();
        tray.CommandInvoked += command => ui.TryEnqueue(() => OnTrayCommand(command));
        tray.Unavailable += () => ui.TryEnqueue(ShowWindow); // no icon: the window is the only way in
        _engine.PausedChanged += paused =>
        {
            tray.SetPaused(paused);
            ui.TryEnqueue(() => _window?.Refresh());
        };
        _tray = tray;
    }

    private void OnTrayCommand(TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.Open:
                ShowWindow();
                break;
            case TrayCommand.TogglePause:
                _engine?.TogglePause();
                break;
            case TrayCommand.Exit:
                _ = ExitAsync();
                break;
        }
    }

    private void ShowWindow()
    {
        if (_window is null)
        {
            _window = new MainWindow();
            _window.AppWindow.Closing += (sender, e) =>
            {
                if (_exiting)
                {
                    return;
                }

                e.Cancel = true;
                if (_tray is null)
                {
                    _ = ExitAsync(); // no tray to come back from
                }
                else
                {
                    sender.Hide(); // keep running in the tray
                }
            };
        }

        _window.Refresh();
        _window.Activate();
    }

    /// <summary>Always ends the process, even if a shutdown step fails (a zombie would keep the hook installed).</summary>
    private async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        try
        {
            _tray?.Dispose();
            if (_engine is not null)
            {
                await _engine.DisposeAsync();
            }

            _window?.Close();
            await _host.StopAsync();
        }
        catch (Exception ex)
        {
            RecordFault(ex);
        }
        finally
        {
            _host.Dispose();
            Exit();
        }
    }

    /// <summary>Content-free: only the exception type reaches the log.</summary>
    private void RecordFault(Exception ex)
    {
        try
        {
            Services.GetService<IDiagnosticSink>()?.Record(new EngineFault(DateTimeOffset.UtcNow, ex.GetType().Name));
        }
        catch (Exception logging) when (logging is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            // The log itself is unavailable; the error is still shown in the window.
        }
    }

    public void Dispose()
    {
        _tray?.Dispose();
        _engine?.DisposeAsync().AsTask().GetAwaiter().GetResult(); // no-op after ExitAsync
        _host.Dispose();
        GC.SuppressFinalize(this);
    }
}

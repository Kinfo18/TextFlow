using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using TextFlow.App.Engine;
using TextFlow.App.Menu;
using TextFlow.Core.Diagnostics;
using TextFlow.Core.Input;
using TextFlow.Infrastructure.Diagnostics;
using TextFlow.Infrastructure.Windows;

namespace TextFlow.App;

/// <summary>
/// Composition root: generic host (DI, lifetime), expansion engine, group menu popup and tray icon.
/// TextFlow lives in the notification area: closing the window only hides it; "Salir" in the tray exits.
/// </summary>
public partial class App : Application, IDisposable
{
    private const string InstanceName = "TextFlow";

    private readonly IHost _host;
    private DispatcherQueue? _ui;
    private EngineHost? _engine;
    private LibraryHost? _library;
    private TrayIcon? _tray;
    private SingleInstance? _instance;
    private AppSettings _settings = new();
    private StartupRegistration? _startup;
    private GlobalHotkey? _pauseHotkey;
    private MainWindow? _window;
    private bool _exiting;

    public App()
    {
        InitializeComponent();
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown; // hiding the last window must not exit
        UnhandledException += (_, e) =>
        {
            // Last resort for UI-thread exceptions: log the type and keep TextFlow (and its hook) alive.
            RecordFault(e.Exception);
            e.Handled = true;
        };
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

    internal LibraryHost? Library => _library;

    /// <summary>Why startup failed ("Type: message"), shown on the Inicio page; null when it worked.</summary>
    internal string? StartupError { get; private set; }

    /// <summary>Pause shortcut as shown to the user, and whether Windows let TextFlow have it.</summary>
    internal (string Gesture, bool Registered)? PauseHotkey =>
        _pauseHotkey is null ? null : (_pauseHotkey.Gesture.ToString(), _pauseHotkey.IsRegistered);

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _ui = DispatcherQueue.GetForCurrentThread();
        var background = Environment.GetCommandLineArgs().Contains(StartupRegistration.BackgroundArgument, StringComparer.OrdinalIgnoreCase);

        _instance = new SingleInstance(InstanceName);
        if (!_instance.IsFirst)
        {
            // Two hooks would expand everything twice: hand over to the running instance and leave.
            if (!background)
            {
                _instance.ActivateFirst();
            }

            _instance.Dispose();
            Exit();
            return;
        }

        var ui = _ui;
        _instance.Activated += () => ui.TryEnqueue(ShowWindow);
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

        if (!background)
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
        _settings = AppSettings.Load(paths.Settings);
        var sink = Services.GetRequiredService<IDiagnosticSink>();
        _library = new LibraryHost(paths, sink);
        var library = await _library.InitializeAsync(_settings.ATextBackupPath, CancellationToken.None);

        _engine = new EngineHost(_settings.ChimeVolume, new WinUiMenuPresenter(ui, popup), sink);
        await _engine.StartAsync(library);

        // Every import or edit re-indexes the engine at once (H2.2), and the window shows the new counts.
        var engine = _engine;
        _library.Service.Changed += root => _ = ApplyLibraryAsync(engine, root);
        _library.StatusChanged += () => ui.TryEnqueue(() => _window?.Refresh());

        var tray = new TrayIcon();
        tray.CommandInvoked += command => ui.TryEnqueue(() => OnTrayCommand(command));
        tray.Unavailable += () => ui.TryEnqueue(ShowWindow); // no icon: the window is the only way in
        _engine.PausedChanged += paused =>
        {
            tray.SetPaused(paused);
            ui.TryEnqueue(() => _window?.Refresh());
        };
        _tray = tray;

        _startup = new StartupRegistration(InstanceName, Environment.ProcessPath!);
        ApplyStartWithWindows(_settings.StartWithWindows);

        StartPauseHotkey(ui, tray);
    }

    /// <summary>Optional: if it cannot be created TextFlow keeps running and pauses from the tray.</summary>
    private void StartPauseHotkey(DispatcherQueue ui, TrayIcon tray)
    {
        var gesture = HotkeyGesture.TryParse(_settings.PauseHotkey, out var configured) ? configured! : HotkeyGesture.DefaultPause;
        try
        {
            _pauseHotkey = new GlobalHotkey(gesture);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            RecordFault(ex);
            tray.SetPauseShortcut(null);
            return;
        }

        _pauseHotkey.Pressed += () => ui.TryEnqueue(() =>
        {
            if (!_exiting)
            {
                _engine?.TogglePause();
            }
        });
        tray.SetPauseShortcut(_pauseHotkey.IsRegistered ? gesture.ToString() : null);
    }

    /// <summary>Syncs the Run entry (also repairs its path if the app moved) and the tray check mark.</summary>
    private void ApplyStartWithWindows(bool enabled)
    {
        try
        {
            _startup?.Apply(enabled);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            RecordFault(ex); // policy-locked registry: TextFlow still runs, just not at logon
        }

        _tray?.SetStartWithWindows(_startup?.IsEnabled ?? false);
    }

    private async Task ApplyLibraryAsync(EngineHost engine, Core.Library.LibraryGroup root)
    {
        try
        {
            await engine.UseLibraryAsync(root);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ObjectDisposedException)
        {
            RecordFault(ex); // the previous triggers stay active
        }
    }

    /// <summary>Imports another aText backup and follows it from now on (Inicio → "Importar desde aText…").</summary>
    internal async Task ChooseLibraryAsync(string path, Core.Import.ATextImport import)
    {
        _settings = _settings with { ATextBackupPath = path };
        SaveSettings();
        if (_library is not null)
        {
            await _library.UseATextSourceAsync(path, import, CancellationToken.None);
        }
    }

    private void SaveSettings()
    {
        try
        {
            _settings.Save(Services.GetRequiredService<AppPaths>().Settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RecordFault(ex); // the choice still applies until TextFlow restarts
        }
    }

    private void ToggleStartWithWindows()
    {
        _settings = _settings with { StartWithWindows = !_settings.StartWithWindows };
        SaveSettings();

        ApplyStartWithWindows(_settings.StartWithWindows);
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
            case TrayCommand.ToggleStartWithWindows:
                ToggleStartWithWindows();
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
            _pauseHotkey?.Dispose();
            _tray?.Dispose();
            if (_engine is not null)
            {
                await _engine.DisposeAsync();
            }

            if (_library is not null)
            {
                await _library.DisposeAsync();
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
            _instance?.Dispose();
            Exit();
        }
    }

    /// <summary>Content-free: only the exception type reaches the log.</summary>
    internal void RecordFault(Exception ex)
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
        _pauseHotkey?.Dispose();
        _tray?.Dispose();
        _engine?.DisposeAsync().AsTask().GetAwaiter().GetResult(); // no-op after ExitAsync
        _library?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _host.Dispose();
        _instance?.Dispose();
        GC.SuppressFinalize(this);
    }
}

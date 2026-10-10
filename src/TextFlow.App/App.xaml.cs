using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using TextFlow.App.Engine;
using TextFlow.App.Fields;
using TextFlow.App.Menu;
using TextFlow.App.Updates;
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
    internal const string InstanceName = "TextFlow";

    /// <summary>App icon next to the exe (copied by the csproj); window, taskbar and tray use it.</summary>
    internal static readonly string IconFile = Path.Combine(AppContext.BaseDirectory, "Assets", "TextFlow.ico");
    private static readonly string PausedIconFile = Path.Combine(AppContext.BaseDirectory, "Assets", "TextFlow-paused.ico");

    private readonly IHost _host;
    private DispatcherQueue? _ui;
    private EngineHost? _engine;
    private LibraryHost? _library;
    private TrayIcon? _tray;
    private MenuPopup? _popup;
    private FieldPromptWindow? _fieldsWindow;
    private SingleInstance? _instance;
    private AppSettings _settings = new();
    private StartupRegistration? _startup;
    private GlobalHotkey? _pauseHotkey;
    private AppUpdater? _updater;
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

    internal AppTheme Theme => _settings.Theme;

    internal bool SoundEnabled => _settings.SoundEnabled;

    internal double ChimeVolume => _settings.ChimeVolume;

    internal int PrefixTimeoutMs => (int)EngineHost.PendingTimeout(_settings.PrefixTimeoutMs).TotalMilliseconds;

    internal IReadOnlyList<string> ExcludedProcesses => _settings.ExcludedProcesses ?? [];

    internal bool StartsWithWindows => _startup?.IsEnabled ?? false;

    /// <summary>Null until startup finishes (and in design previews).</summary>
    internal AppUpdater? Updater => _updater;

    /// <summary>Version downloaded and waiting for "Reiniciar y actualizar"; null when there is none.</summary>
    internal string? UpdateVersion => _updater?.ReadyVersion;

    /// <summary>Closes TextFlow; Velopack applies the downloaded update and starts it again.</summary>
    internal void RestartToUpdate()
    {
        if (_updater?.ApplyOnExit() == true)
        {
            _ = ExitAsync();
        }
    }

    /// <summary>Owner window for file pickers and dialogs.</summary>
    internal nint MainWindowHandle => _window is null ? 0 : WinRT.Interop.WindowNative.GetWindowHandle(_window);

    internal void SetTheme(AppTheme theme)
    {
        _settings = _settings with { Theme = theme };
        SaveSettings();
        _window?.ApplyTheme(theme);
        _popup?.ApplyTheme(theme);
        _fieldsWindow?.ApplyTheme(theme);
    }

    internal void SetSound(bool enabled, double volume)
    {
        _settings = _settings with { SoundEnabled = enabled, ChimeVolume = Math.Clamp(volume, 0, 1) };
        SaveSettings();
        _engine?.ConfigureSound(_settings.SoundEnabled, _settings.ChimeVolume);
    }

    /// <summary>Plays the chime at the current settings; false if sound is off, the volume is 0 or there is no audio device.</summary>
    internal bool PlayChime() => _engine?.PlayChime() ?? false;

    internal void WarmSound() => _engine?.WarmSound();

    internal void SetPrefixTimeout(int milliseconds)
    {
        _settings = _settings with { PrefixTimeoutMs = milliseconds };
        SaveSettings();
        _engine?.SetPendingTimeout(milliseconds);
    }

    internal void SetExcludedProcesses(IReadOnlyList<string> processes)
    {
        _settings = _settings with { ExcludedProcesses = processes };
        SaveSettings();
        _engine?.SetExclusions(processes);
    }

    /// <summary>
    /// Swaps the global pause shortcut. If Windows refuses the new one (another app owns it) the previous one is
    /// registered again and nothing is saved.
    /// </summary>
    /// <returns>False when the new shortcut could not be registered.</returns>
    internal bool SetPauseHotkey(HotkeyGesture gesture)
    {
        if (_ui is null || _tray is null)
        {
            return false;
        }

        var previous = _pauseHotkey?.Gesture ?? HotkeyGesture.DefaultPause;
        if (TryUsePauseHotkey(gesture, _ui, _tray))
        {
            _settings = _settings with { PauseHotkey = gesture.ToString() };
            SaveSettings();
            _window?.Refresh();
            return true;
        }

        TryUsePauseHotkey(previous, _ui, _tray);
        return false;
    }

    internal void SetStartWithWindows(bool enabled)
    {
        _settings = _settings with { StartWithWindows = enabled };
        SaveSettings();
        ApplyStartWithWindows(enabled);
    }

    /// <summary>Why startup failed ("Type: message"), shown on the Inicio page; null when it worked.</summary>
    internal string? StartupError { get; private set; }

    /// <summary>Pause shortcut as shown to the user, and whether Windows let TextFlow have it.</summary>
    internal (string Gesture, bool Registered)? PauseHotkey =>
        _pauseHotkey is null ? null : (_pauseHotkey.Gesture.ToString(), _pauseHotkey.IsRegistered);

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _ui = DispatcherQueue.GetForCurrentThread();
        var background = Environment.GetCommandLineArgs().Contains(StartupRegistration.BackgroundArgument, StringComparer.OrdinalIgnoreCase);

        if (MenuPreview.TryShow(Environment.GetCommandLineArgs()) || FieldsPreview.TryShow(Environment.GetCommandLineArgs()))
        {
            return; // design preview only: no hook, no tray, no single-instance check
        }

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

        if (background)
        {
            IdleMemory.ReleaseSoon(_ui); // started with Windows: straight to the tray
        }
        else
        {
            ShowWindow();
        }
    }

    private async Task StartAsync(DispatcherQueue ui)
    {
        await _host.StartAsync();

        var popup = new MenuPopup();
        popup.Prime();
        _popup = popup;

        var paths = Services.GetRequiredService<AppPaths>();
        _settings = AppSettings.Load(paths.Settings);
        popup.ApplyTheme(_settings.Theme); // after Load: before it _settings still holds the defaults
        _fieldsWindow = new FieldPromptWindow();
        _fieldsWindow.ApplyTheme(_settings.Theme);
        var sink = Services.GetRequiredService<IDiagnosticSink>();
        _library = new LibraryHost(paths, sink);
        var library = await _library.InitializeAsync(_settings.ATextBackupPath, CancellationToken.None);

        _engine = new EngineHost(_settings, new WinUiMenuPresenter(ui, popup), new WinUiFieldPrompt(ui, _fieldsWindow), sink, _library.Usage);
        await _engine.StartAsync(library);

        // Every import or edit re-indexes the engine at once (H2.2), and the window shows the new counts.
        var engine = _engine;
        _library.Service.Changed += root => _ = ApplyLibraryAsync(engine, root);
        _library.StatusChanged += () => ui.TryEnqueue(() => _window?.Refresh());

        var tray = new TrayIcon(IconFile, PausedIconFile);
        tray.CommandInvoked += command => ui.TryEnqueue(() => OnTrayCommand(command));
        tray.Unavailable += () => ui.TryEnqueue(ShowWindow); // no icon: the window is the only way in
        _engine.PausedChanged += paused =>
        {
            tray.SetPaused(paused);
            ui.TryEnqueue(() => _window?.Refresh());
        };
        _tray = tray;

        _startup = new StartupRegistration(InstanceName, Environment.ProcessPath!);

        StartPauseHotkey(ui, tray);

        var updater = new AppUpdater(RecordFault);
        updater.UpdateReady += () => ui.TryEnqueue(() =>
        {
            tray.SetUpdateVersion(updater.ReadyVersion);
            _window?.Refresh();
        });
        updater.Start();
        _updater = updater;

        // After the updater: only the installed copy may point the Run entry at itself on startup.
        ApplyStartWithWindows(_settings.StartWithWindows, takeOverOtherCopy: updater.IsInstalled);
    }

    /// <summary>Optional: if it cannot be created TextFlow keeps running and pauses from the tray.</summary>
    private void StartPauseHotkey(DispatcherQueue ui, TrayIcon tray)
    {
        var gesture = HotkeyGesture.TryParse(_settings.PauseHotkey, out var configured) ? configured! : HotkeyGesture.DefaultPause;
        TryUsePauseHotkey(gesture, ui, tray);
    }

    /// <summary>Replaces the current shortcut with <paramref name="gesture"/>; false if Windows did not give it to TextFlow.</summary>
    private bool TryUsePauseHotkey(HotkeyGesture gesture, DispatcherQueue ui, TrayIcon tray)
    {
        _pauseHotkey?.Dispose();
        _pauseHotkey = null;
        try
        {
            _pauseHotkey = new GlobalHotkey(gesture);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            RecordFault(ex);
            tray.SetPauseShortcut(null);
            return false;
        }

        _pauseHotkey.Pressed += () => ui.TryEnqueue(() =>
        {
            if (!_exiting)
            {
                _engine?.TogglePause();
            }
        });
        tray.SetPauseShortcut(_pauseHotkey.IsRegistered ? gesture.ToString() : null);
        return _pauseHotkey.IsRegistered;
    }

    /// <summary>Syncs the Run entry (also repairs its path if the app moved) and the tray check mark.</summary>
    /// <param name="takeOverOtherCopy">True for the user's own choice; on startup, only for the installed copy.</param>
    private void ApplyStartWithWindows(bool enabled, bool takeOverOtherCopy = true)
    {
        try
        {
            _startup?.Apply(enabled, takeOverOtherCopy);
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

    /// <summary>Imports a TextFlow library file; aText is no longer followed.</summary>
    internal async Task ImportLibraryFileAsync(Core.Library.LibraryGroup root)
    {
        _settings = _settings with { ATextBackupPath = null };
        SaveSettings();
        if (_library is not null)
        {
            await _library.ImportLibraryFileAsync(root, CancellationToken.None);
        }
    }

    /// <summary>
    /// Runs an edit of the library (H3). The first edit while aText is still followed asks first, then stops following
    /// it: otherwise the next change to the .atext file would overwrite the edit (user's choice, 2026-10-03).
    /// </summary>
    /// <returns>False when the user cancelled.</returns>
    internal async Task<bool> EditLibraryAsync(Microsoft.UI.Xaml.XamlRoot xamlRoot, Func<Core.Library.LibraryService, Task> edit)
    {
        if (_library is null)
        {
            return false;
        }

        if (_library.Status.SourcePath is { } source)
        {
            var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = "Editar en TextFlow",
                Content = new Microsoft.UI.Xaml.Controls.TextBlock
                {
                    Text = $"Tu biblioteca se importa de «{Path.GetFileName(source)}» (aText). Si editas aquí, TextFlow dejará de "
                        + "importar los cambios de ese archivo para no pisar tus ediciones. Siempre podrás volver a importarlo a mano.",
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                    MaxWidth = 440,
                },
                PrimaryButtonText = "Editar en TextFlow",
                CloseButtonText = "Cancelar",
                DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
            {
                return false;
            }

            _settings = _settings with { ATextBackupPath = null };
            SaveSettings();
            _library.StopFollowingATextSource();
        }

        await edit(_library.Service);
        return true;
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

    private void ToggleStartWithWindows() => SetStartWithWindows(!_settings.StartWithWindows);

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
            case TrayCommand.Update:
                RestartToUpdate();
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
            var window = new MainWindow();
            window.AppWindow.Closing += (_, e) => OnWindowClosing(window, e);
            window.Closed += (_, _) =>
            {
                // Closed, not hidden: a tray app does not need the XAML tree in memory (V0.1 idle RAM < 150 MB).
                _window = null;
                if (!_exiting && _ui is not null)
                {
                    IdleMemory.ReleaseSoon(_ui);
                }
            };
            _window = window;
        }

        _window.Refresh();
        _window.Activate();
    }

    /// <summary>The window closes for real (TextFlow stays in the tray), but never drops an unsaved command silently.</summary>
    private async void OnWindowClosing(MainWindow window, AppWindowClosingEventArgs e)
    {
        if (_exiting || window.CloseConfirmed)
        {
            return;
        }

        e.Cancel = true;
        if (_tray is null)
        {
            _ = ExitAsync(); // no tray to come back from
            return;
        }

        try
        {
            if (await window.ConfirmCloseAsync())
            {
                window.Close();
            }
        }
        catch (Exception ex)
        {
            RecordFault(ex); // async void: an escaping exception would end the process
        }
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
            _updater?.Dispose();
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

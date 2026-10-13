using System.Windows;
using System.Windows.Threading;
using SC3RGBController.Models;
using SC3RGBController.Services;
using SC3RGBController.UI.Themes;

namespace SC3RGBController;

public partial class App : Application
{
    private const string InstanceMutexName = "Local\\FIFINE-SC3-RGB-PLUS";
    private const string ActivationEventName = "Local\\FIFINE-SC3-RGB-PLUS-ACTIVATE";

    public static bool IsSessionEnding { get; private set; }

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationEvent;
    private CancellationTokenSource? _activationCancellation;
    private StartupBackgroundHost? _backgroundHost;
    private SystemTrayService? _startupTray;
    private TrayCommandRouter? _startupTrayCommands;

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        // Swap design tokens for system colors before any window loads (most markup uses StaticResource).
        ThemeManager.ApplyHighContrastIfEnabled(Current.Resources);
        ThemeManager.UseDarkTitleBarsForAllWindows();
        SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;

        _instanceMutex = new Mutex(true, InstanceMutexName, out bool firstInstance);
        if (!firstInstance)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    using EventWaitHandle existing = EventWaitHandle.OpenExisting(ActivationEventName);
                    existing.Set();
                    break;
                }
                catch (WaitHandleCannotBeOpenedException) when (attempt < 9)
                {
                    Thread.Sleep(50);
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    break;
                }
            }
            Shutdown();
            return;
        }

        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        _activationCancellation = new CancellationTokenSource();
        _ = Task.Run(() => WatchForActivation(_activationCancellation.Token));

        bool startupLaunch = e.Args.Any(argument => string.Equals(argument, "--startup", StringComparison.OrdinalIgnoreCase));
        if (startupLaunch)
        {
            StartBackgroundSession();
        }
        else
        {
            Dispatcher.BeginInvoke(new Action(async () => await OpenMainWindowAsync()));
        }
    }

    private void SystemParameters_StaticPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SystemParameters.HighContrast)) return;

        // Re-apply when High Contrast is switched on while running. Elements that used
        // StaticResource keep their already-resolved brushes until the app restarts, and
        // turning High Contrast off is not reverted live; both are acceptable (a restart applies it).
        Dispatcher.BeginInvoke(new Action(() => ThemeManager.ApplyHighContrastIfEnabled(Current.Resources)));
    }

    private void WatchForActivation(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            EventWaitHandle? activationEvent = _activationEvent;
            if (activationEvent is null) return;

            try
            {
                if (!activationEvent.WaitOne(500)) continue;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (cancellationToken.IsCancellationRequested) return;
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
            {
                if (MainWindow is MainWindow window)
                    await window.OpenFromExternalLaunchAsync();
                else
                    await OpenMainWindowAsync();
            }));
        }
    }

    private void StartBackgroundSession()
    {
        AppSettings settings = SettingsStore.Load();
        bool restoreStockMode = settings.CustomShortcutsEnabled &&
            !CustomShortcutStabilityPolicy.EventDrivenTransportEnabled;
        if (restoreStockMode)
        {
            settings.CustomShortcutsEnabled = false;
            SettingsStore.Save(settings);
        }

        _backgroundHost = new StartupBackgroundHost(settings, restoreStockMode);
        _backgroundHost.Start();

        _startupTrayCommands = new TrayCommandRouter();
        _startupTrayCommands.OpenRequested += async (_, _) => await OpenMainWindowAsync();
        _startupTrayCommands.DisableShortcutsRequested += async (_, _) => await DisableBackgroundShortcutsAsync(settings);
        _startupTrayCommands.ExitRequested += async (_, _) => await ExitBackgroundSessionAsync();
        _startupTray = new SystemTrayService(_startupTrayCommands);
        _startupTray.Show();
    }

    private async Task OpenMainWindowAsync()
    {
        if (MainWindow is MainWindow existing)
        {
            await existing.OpenFromExternalLaunchAsync();
            return;
        }

        await StopBackgroundSessionAsync();
        MainWindow window = new(isStartupLaunch: false);
        MainWindow = window;
        window.Show();
    }

    private async Task DisableBackgroundShortcutsAsync(AppSettings settings)
    {
        settings.CustomShortcutsEnabled = false;
        SettingsStore.Save(settings);
        if (_backgroundHost is not null)
            await _backgroundHost.DisableShortcutsAsync();
    }

    private async Task ExitBackgroundSessionAsync()
    {
        await StopBackgroundSessionAsync();
        Shutdown();
    }

    private async Task StopBackgroundSessionAsync()
    {
        SystemTrayService? tray = _startupTray;
        _startupTray = null;
        tray?.Dispose();
        StartupBackgroundHost? host = _backgroundHost;
        _backgroundHost = null;
        if (host is not null) await host.DisposeAsync();
    }

    private void Application_SessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        IsSessionEnding = true;
    }

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        SystemParameters.StaticPropertyChanged -= SystemParameters_StaticPropertyChanged;
        StopBackgroundSessionAsync().GetAwaiter().GetResult();
        _activationCancellation?.Cancel();
        try { _activationEvent?.Set(); } catch (ObjectDisposedException) { }
        _activationEvent?.Dispose();
        _activationEvent = null;
        _activationCancellation?.Dispose();
        _activationCancellation = null;
        _instanceMutex?.Dispose();
        _instanceMutex = null;
    }
}

using System.Windows;
using System.Windows.Threading;

namespace SC3RGBController;

public partial class App : Application
{
    private const string InstanceMutexName = "Local\\FIFINE-SC3-RGB-PLUS";
    private const string ActivationEventName = "Local\\FIFINE-SC3-RGB-PLUS-ACTIVATE";

    public static bool IsSessionEnding { get; private set; }

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationEvent;
    private CancellationTokenSource? _activationCancellation;

    private void Application_Startup(object sender, StartupEventArgs e)
    {
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
            }));
        }
    }

    private void Application_SessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        IsSessionEnding = true;
    }

    private void Application_Exit(object sender, ExitEventArgs e)
    {
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
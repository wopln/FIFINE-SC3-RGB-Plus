using System.Windows;

namespace SC3RGBController;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private void Application_Startup(object sender, StartupEventArgs e)
    {
        _instanceMutex = new Mutex(true, "Local\\FIFINE-SC3-RGB-PLUS", out bool first);
        if (!first) Shutdown();
    }

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        _instanceMutex?.Dispose();
        _instanceMutex = null;
    }
}

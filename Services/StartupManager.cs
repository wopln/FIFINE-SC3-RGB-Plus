using Microsoft.Win32;

namespace SC3RGBController.Services;

public static class StartupManager
{
    private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string ValueName = "FIFINE SC3 RGB+";
    private const string LegacyValueName = "SC3RGBController";

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null) return;

            if (!enabled)
            {
                key.DeleteValue(ValueName, false);
                key.DeleteValue(LegacyValueName, false);
                return;
            }

            string? executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath)) return;
            key.DeleteValue(LegacyValueName, false);
            key.SetValue(ValueName, $"\"{executablePath}\" --startup");
        }
        catch
        {
            // Startup integration is optional; a registry failure must not interrupt the app.
        }
    }
}

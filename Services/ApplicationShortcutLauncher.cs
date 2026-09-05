using System.Diagnostics;
using System.IO;

namespace SC3RGBController.Services;

public enum ShortcutLaunchResult { Launched, Disabled, Unassigned, MissingTarget, UnsupportedTarget, Failed }

public interface IApplicationShortcutLauncher
{
    ShortcutLaunchResult Launch(bool enabled, string? path);
}

public sealed class ApplicationShortcutLauncher : IApplicationShortcutLauncher
{
    public ShortcutLaunchResult Launch(bool enabled, string? path)
    {
        if (!enabled) return ShortcutLaunchResult.Disabled;
        if (string.IsNullOrWhiteSpace(path)) return ShortcutLaunchResult.Unassigned;

        string extension = Path.GetExtension(path);
        if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            return ShortcutLaunchResult.UnsupportedTarget;

        if (!File.Exists(path)) return ShortcutLaunchResult.MissingTarget;

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return ShortcutLaunchResult.Launched;
        }
        catch
        {
            return ShortcutLaunchResult.Failed;
        }
    }
}

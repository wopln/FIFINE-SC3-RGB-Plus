using System.Drawing;
using Forms = System.Windows.Forms;

namespace SC3RGBController.Services;

public static class CustomShortcutHostPolicy
{
    public static bool KeepRunningOnWindowClose(bool shortcutsEnabled) => shortcutsEnabled;
    public static bool ShouldRegisterStartup(bool startWithWindows, bool shortcutsEnabled) =>
        startWithWindows || shortcutsEnabled;
}

public sealed class TrayCommandRouter
{
    public event EventHandler? OpenRequested;
    public event EventHandler? DisableShortcutsRequested;
    public event EventHandler? ExitRequested;

    public void Open() => OpenRequested?.Invoke(this, EventArgs.Empty);
    public void DisableShortcuts() => DisableShortcutsRequested?.Invoke(this, EventArgs.Empty);
    public void Exit() => ExitRequested?.Invoke(this, EventArgs.Empty);
}

public sealed class SystemTrayService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Icon? _ownedIcon;

    private readonly TrayCommandRouter _commands;

    public SystemTrayService(TrayCommandRouter commands)
    {
        _commands = commands;
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
            _ownedIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath);

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "FIFINE SC3 RGB+",
            Icon = _ownedIcon ?? SystemIcons.Application,
            Visible = false
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open FIFINE SC3 RGB+", null, (_, _) => _commands.Open());
        menu.Items.Add("Disable Custom Button Shortcuts", null, (_, _) => _commands.DisableShortcuts());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => _commands.Exit());
        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.DoubleClick += (_, _) => _commands.Open();
    }

    public bool Visible => _notifyIcon.Visible;
    public void Show() => _notifyIcon.Visible = true;
    public void Hide() => _notifyIcon.Visible = false;

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _ownedIcon?.Dispose();
    }
}

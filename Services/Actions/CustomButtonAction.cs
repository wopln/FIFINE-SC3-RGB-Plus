using System.IO;

namespace SC3RGBController.Services.Actions;

public enum CustomActionKind
{
    None,
    LaunchProgram,      // Target: full path to .exe/.lnk (legacy assignments are this kind)
    LaunchApp,          // Target: Start-menu AppUserModelID / parsing name (shell:AppsFolder\{Target})
    AppMuteToggle,      // Target: process name without extension, e.g. "Discord"
    AppVolumeUp,        // Target: process name; Step: percent
    AppVolumeDown,      // Target: process name; Step: percent
    MicMuteToggle,      // default communications/console capture device
    SystemMuteToggle,
    SystemVolumeUp,     // Step: percent
    SystemVolumeDown,   // Step: percent
    MediaPlayPause,
    MediaNext,
    MediaPrevious,
}

/// <summary>
/// What a Custom A–D button does. Persisted in the existing Custom*Path setting as a string so
/// older settings files and the background host keep working: plain paths are LaunchProgram,
/// everything else is "sc3action:{kind}?target={target}&amp;step={step}".
/// </summary>
public sealed record CustomButtonAction(CustomActionKind Kind, string? Target = null, int Step = 10)
{
    public const string Scheme = "sc3action:";
    public const int DefaultStep = 10;

    public static CustomButtonAction None { get; } = new(CustomActionKind.None);

    public bool RequiresTarget => Kind is CustomActionKind.LaunchProgram or CustomActionKind.LaunchApp
        or CustomActionKind.AppMuteToggle or CustomActionKind.AppVolumeUp or CustomActionKind.AppVolumeDown;

    public bool UsesStep => Kind is CustomActionKind.AppVolumeUp or CustomActionKind.AppVolumeDown
        or CustomActionKind.SystemVolumeUp or CustomActionKind.SystemVolumeDown;

    public string Serialize()
    {
        if (Kind == CustomActionKind.None) return string.Empty;
        if (Kind == CustomActionKind.LaunchProgram) return Target ?? string.Empty;
        string text = Scheme + Kind;
        List<string> query = [];
        if (!string.IsNullOrEmpty(Target)) query.Add("target=" + Uri.EscapeDataString(Target));
        if (UsesStep) query.Add("step=" + Math.Clamp(Step, 1, 100));
        return query.Count == 0 ? text : text + "?" + string.Join("&", query);
    }

    public static CustomButtonAction Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return None;
        if (!stored.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
            return new(CustomActionKind.LaunchProgram, stored);

        string body = stored[Scheme.Length..];
        string kindText = body;
        string? target = null;
        int step = DefaultStep;
        int q = body.IndexOf('?');
        if (q >= 0)
        {
            kindText = body[..q];
            foreach (string pair in body[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                string key = pair[..eq], value = Uri.UnescapeDataString(pair[(eq + 1)..]);
                if (key == "target") target = value;
                else if (key == "step" && int.TryParse(value, out int parsed)) step = Math.Clamp(parsed, 1, 100);
            }
        }
        return Enum.TryParse(kindText, ignoreCase: true, out CustomActionKind kind) && kind != CustomActionKind.LaunchProgram
            ? new(kind, target, step)
            : None;
    }

    /// <summary>Short, user-facing description, e.g. "Mute Discord" or "Volume up 10% · Spotify".</summary>
    public string Describe(string? targetDisplayName = null)
    {
        string name = !string.IsNullOrWhiteSpace(targetDisplayName) ? targetDisplayName
            : Kind == CustomActionKind.LaunchProgram && Target is not null ? Path.GetFileNameWithoutExtension(Target)
            : Target ?? string.Empty;
        return Kind switch
        {
            CustomActionKind.None => "Not assigned",
            CustomActionKind.LaunchProgram or CustomActionKind.LaunchApp => $"Open {name}",
            CustomActionKind.AppMuteToggle => $"Mute / unmute {name}",
            CustomActionKind.AppVolumeUp => $"{name} volume up {Step}%",
            CustomActionKind.AppVolumeDown => $"{name} volume down {Step}%",
            CustomActionKind.MicMuteToggle => "Mute / unmute microphone",
            CustomActionKind.SystemMuteToggle => "Mute / unmute system sound",
            CustomActionKind.SystemVolumeUp => $"System volume up {Step}%",
            CustomActionKind.SystemVolumeDown => $"System volume down {Step}%",
            CustomActionKind.MediaPlayPause => "Play / pause media",
            CustomActionKind.MediaNext => "Next track",
            CustomActionKind.MediaPrevious => "Previous track",
            _ => Kind.ToString()
        };
    }
}

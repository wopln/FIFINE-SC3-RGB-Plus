using System.Diagnostics;
using SC3RGBController.Services.Audio;

namespace SC3RGBController.Services.Actions;

/// <summary>
/// Executes a Custom A–D assignment (see <see cref="CustomButtonAction"/>). Program launches keep
/// the original <see cref="ApplicationShortcutLauncher"/> behavior; audio and media actions are
/// dispatched to <see cref="IAudioControl"/> / <see cref="IMediaKeySender"/> and report a
/// user-facing message through <see cref="Feedback"/>. Never throws.
/// </summary>
public sealed class CustomButtonActionRunner : IApplicationShortcutLauncher
{
    private readonly IApplicationShortcutLauncher _programLauncher;
    private readonly IAudioControl _audio;
    private readonly IMediaKeySender _mediaKeys;
    private readonly Func<ProcessStartInfo, bool> _shellStart;

    /// <summary>Raised with the result message of every executed audio/media action (success or not).
    /// May be raised on a background thread.</summary>
    public event EventHandler<string>? Feedback;

    public CustomButtonActionRunner()
        : this(new ApplicationShortcutLauncher(), new AudioControlService(), new MediaKeySender())
    {
    }

    public CustomButtonActionRunner(
        IApplicationShortcutLauncher programLauncher,
        IAudioControl audio,
        IMediaKeySender mediaKeys,
        Func<ProcessStartInfo, bool>? shellStart = null)
    {
        _programLauncher = programLauncher;
        _audio = audio;
        _mediaKeys = mediaKeys;
        _shellStart = shellStart ?? DefaultShellStart;
    }

    public ShortcutLaunchResult Launch(bool enabled, string? path)
    {
        if (!enabled) return ShortcutLaunchResult.Disabled;
        CustomButtonAction action;
        try { action = CustomButtonAction.Parse(path); }
        catch { return ShortcutLaunchResult.Failed; }

        try
        {
            return action.Kind switch
            {
                CustomActionKind.None => ShortcutLaunchResult.Unassigned,
                CustomActionKind.LaunchProgram => _programLauncher.Launch(true, action.Target),
                CustomActionKind.LaunchApp => LaunchApp(action.Target),
                CustomActionKind.AppMuteToggle => Report(RequireTarget(action, t => _audio.ToggleAppMute(t))),
                CustomActionKind.AppVolumeUp => Report(RequireTarget(action, t => _audio.StepAppVolume(t, action.Step))),
                CustomActionKind.AppVolumeDown => Report(RequireTarget(action, t => _audio.StepAppVolume(t, -action.Step))),
                CustomActionKind.MicMuteToggle => Report(_audio.ToggleMicrophoneMute()),
                CustomActionKind.SystemMuteToggle => Report(_audio.ToggleSystemMute()),
                CustomActionKind.SystemVolumeUp => Report(_audio.StepSystemVolume(action.Step)),
                CustomActionKind.SystemVolumeDown => Report(_audio.StepSystemVolume(-action.Step)),
                CustomActionKind.MediaPlayPause => SendMedia(MediaKey.PlayPause, "Play / pause"),
                CustomActionKind.MediaNext => SendMedia(MediaKey.Next, "Next track"),
                CustomActionKind.MediaPrevious => SendMedia(MediaKey.Previous, "Previous track"),
                _ => ShortcutLaunchResult.UnsupportedTarget
            };
        }
        catch
        {
            if (IsAudioOrMedia(action.Kind)) RaiseFeedback(FailureMessage(action.Kind));
            return ShortcutLaunchResult.Failed;
        }
    }

    public static bool IsLaunchKind(CustomActionKind kind) =>
        kind is CustomActionKind.LaunchProgram or CustomActionKind.LaunchApp;

    private static bool IsAudioOrMedia(CustomActionKind kind) => kind is not (CustomActionKind.None
        or CustomActionKind.LaunchProgram or CustomActionKind.LaunchApp);

    private static string FailureMessage(CustomActionKind kind) => kind switch
    {
        CustomActionKind.MediaPlayPause or CustomActionKind.MediaNext or CustomActionKind.MediaPrevious =>
            "Unable to send media key",
        CustomActionKind.MicMuteToggle => "Unable to change microphone",
        _ => "Unable to change audio"
    };

    private static AudioActionResult RequireTarget(CustomButtonAction action, Func<string, AudioActionResult> run) =>
        string.IsNullOrWhiteSpace(action.Target)
            ? AudioActionResult.Missing("No app selected for this button")
            : run(action.Target);

    private ShortcutLaunchResult Report(AudioActionResult result)
    {
        RaiseFeedback(result.Message);
        if (result.Success) return ShortcutLaunchResult.Launched;
        return result.TargetMissing ? ShortcutLaunchResult.MissingTarget : ShortcutLaunchResult.Failed;
    }

    private ShortcutLaunchResult SendMedia(MediaKey key, string label)
    {
        bool ok = _mediaKeys.Send(key);
        RaiseFeedback(ok ? label : "Unable to send media key");
        return ok ? ShortcutLaunchResult.Launched : ShortcutLaunchResult.Failed;
    }

    private ShortcutLaunchResult LaunchApp(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return ShortcutLaunchResult.MissingTarget;
        if (appId.IndexOfAny(['"', '\r', '\n', '\0']) >= 0) return ShortcutLaunchResult.UnsupportedTarget;
        var info = new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + appId.Trim()) { UseShellExecute = true };
        return _shellStart(info) ? ShortcutLaunchResult.Launched : ShortcutLaunchResult.Failed;
    }

    private static bool DefaultShellStart(ProcessStartInfo info)
    {
        try
        {
            using Process? _ = Process.Start(info);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void RaiseFeedback(string message)
    {
        try { Feedback?.Invoke(this, message); }
        catch { /* a subscriber failure must not break the button handler */ }
    }
}

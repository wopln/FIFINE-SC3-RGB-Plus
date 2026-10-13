using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SC3RGBController.Services.Audio;

public sealed record AudioSessionInfo(
    string ProcessName,
    string DisplayName,
    int ProcessId,
    bool IsMuted,
    float Volume,
    string? ExecutablePath);

/// <summary>
/// Outcome of an audio action. <see cref="TargetMissing"/> is true when the requested app or
/// device has no audio session/endpoint right now (not an error, just nothing to act on).
/// </summary>
public sealed record AudioActionResult(bool Success, string Message, bool TargetMissing = false)
{
    public static AudioActionResult Ok(string message) => new(true, message);
    public static AudioActionResult Missing(string message) => new(false, message, TargetMissing: true);
    public static AudioActionResult Fail(string message) => new(false, message);
}

public interface IAudioControl
{
    IReadOnlyList<AudioSessionInfo> GetActiveSessions();
    AudioActionResult ToggleAppMute(string processName);
    AudioActionResult StepAppVolume(string processName, int deltaPercent);
    AudioActionResult ToggleMicrophoneMute();
    bool? IsMicrophoneMuted();
    AudioActionResult ToggleSystemMute();
    AudioActionResult StepSystemVolume(int deltaPercent);
}

/// <summary>
/// Windows Core Audio control (per-app sessions, default render/capture endpoints).
/// Safe to call from any thread: work always runs on an MTA thread (inline when the caller is
/// already MTA, otherwise on the thread pool) and every COM object is released before returning.
/// Methods never throw; failures are reported through <see cref="AudioActionResult"/>.
/// </summary>
public sealed class AudioControlService : IAudioControl
{
    private static readonly object Gate = new();

    public IReadOnlyList<AudioSessionInfo> GetActiveSessions() =>
        RunMta(() =>
        {
            List<AudioSessionInfo> result = [];
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            WithSessions(sessions =>
            {
                foreach (SessionEntry s in sessions)
                {
                    if (!seen.Add(s.ProcessName)) continue;
                    s.Volume.GetMute(out int muted);
                    s.Volume.GetMasterVolume(out float level);
                    result.Add(new AudioSessionInfo(s.ProcessName, FriendlyName(s), (int)s.ProcessId,
                        muted != 0, level, s.ExecutablePath));
                }
            });
            result.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
            return (IReadOnlyList<AudioSessionInfo>)result;
        }, []);

    public AudioActionResult ToggleAppMute(string processName) =>
        RunMta(() =>
        {
            string name = NormalizeProcessName(processName);
            if (name.Length == 0) return AudioActionResult.Fail("No app selected");
            AudioActionResult? outcome = null;
            WithSessions(sessions =>
            {
                List<SessionEntry> matches = Matching(sessions, name);
                if (matches.Count == 0) { outcome = NotPlaying(name); return; }
                Check(matches[0].Volume.GetMute(out int firstMuted));
                int target = firstMuted != 0 ? 0 : 1;
                Guid ctx = Guid.Empty;
                foreach (SessionEntry s in matches) Check(s.Volume.SetMute(target, ref ctx));
                outcome = AudioActionResult.Ok($"{FriendlyName(matches[0])} {(target != 0 ? "muted" : "unmuted")}");
            });
            return outcome ?? NotPlaying(name);
        }, AudioActionResult.Fail("Unable to change app audio"));

    public AudioActionResult StepAppVolume(string processName, int deltaPercent) =>
        RunMta(() =>
        {
            string name = NormalizeProcessName(processName);
            if (name.Length == 0) return AudioActionResult.Fail("No app selected");
            AudioActionResult? outcome = null;
            WithSessions(sessions =>
            {
                List<SessionEntry> matches = Matching(sessions, name);
                if (matches.Count == 0) { outcome = NotPlaying(name); return; }
                Check(matches[0].Volume.GetMasterVolume(out float current));
                int percent = StepPercent(current, deltaPercent);
                Guid ctx = Guid.Empty;
                foreach (SessionEntry s in matches)
                {
                    Check(s.Volume.SetMasterVolume(percent / 100f, ref ctx));
                    if (deltaPercent > 0) s.Volume.SetMute(0, ref ctx); // volume up unmutes, like Windows
                }
                outcome = AudioActionResult.Ok($"{FriendlyName(matches[0])} volume {percent}%");
            });
            return outcome ?? NotPlaying(name);
        }, AudioActionResult.Fail("Unable to change app volume"));

    public AudioActionResult ToggleMicrophoneMute() =>
        RunMta(() => WithEndpointVolume(EDataFlow.eCapture, [ERole.eCommunications, ERole.eConsole], volume =>
        {
            Check(volume.GetMute(out int muted));
            int target = muted != 0 ? 0 : 1;
            Guid ctx = Guid.Empty;
            Check(volume.SetMute(target, ref ctx));
            return AudioActionResult.Ok(target != 0 ? "Microphone muted" : "Microphone on");
        }) ?? AudioActionResult.Missing("No microphone found"),
        AudioActionResult.Fail("Unable to change microphone"));

    public bool? IsMicrophoneMuted() =>
        RunMta<bool?>(() => WithEndpointVolume<bool?>(EDataFlow.eCapture, [ERole.eCommunications, ERole.eConsole], volume =>
        {
            Check(volume.GetMute(out int muted));
            return muted != 0;
        }), null);

    public AudioActionResult ToggleSystemMute() =>
        RunMta(() => WithEndpointVolume(EDataFlow.eRender, [ERole.eMultimedia, ERole.eConsole], volume =>
        {
            Check(volume.GetMute(out int muted));
            int target = muted != 0 ? 0 : 1;
            Guid ctx = Guid.Empty;
            Check(volume.SetMute(target, ref ctx));
            return AudioActionResult.Ok(target != 0 ? "System sound muted" : "System sound on");
        }) ?? AudioActionResult.Missing("No speakers or headphones found"),
        AudioActionResult.Fail("Unable to change system sound"));

    public AudioActionResult StepSystemVolume(int deltaPercent) =>
        RunMta(() => WithEndpointVolume(EDataFlow.eRender, [ERole.eMultimedia, ERole.eConsole], volume =>
        {
            Check(volume.GetMasterVolumeLevelScalar(out float current));
            int percent = StepPercent(current, deltaPercent);
            Guid ctx = Guid.Empty;
            Check(volume.SetMasterVolumeLevelScalar(percent / 100f, ref ctx));
            if (deltaPercent > 0) volume.SetMute(0, ref ctx); // volume up unmutes, like Windows
            return AudioActionResult.Ok($"System volume {percent}%");
        }) ?? AudioActionResult.Missing("No speakers or headphones found"),
        AudioActionResult.Fail("Unable to change system volume"));

    // ---- helpers -------------------------------------------------------------------------

    public static int StepPercent(float currentScalar, int deltaPercent)
    {
        int current = (int)Math.Round(Math.Clamp(currentScalar, 0f, 1f) * 100f);
        return Math.Clamp(current + deltaPercent, 0, 100);
    }

    private static string NormalizeProcessName(string? processName)
    {
        string name = (processName ?? string.Empty).Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name;
    }

    private static AudioActionResult NotPlaying(string name) =>
        AudioActionResult.Missing($"{name} isn't playing audio right now");

    private static List<SessionEntry> Matching(List<SessionEntry> sessions, string name) =>
        sessions.Where(s => s.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    /// <summary>Runs <paramref name="work"/> on an MTA thread and converts any exception into <paramref name="fallback"/>.</summary>
    private static T RunMta<T>(Func<T> work, T fallback)
    {
        T Guarded()
        {
            try
            {
                // Serialize Core Audio access; button presses and UI refreshes are infrequent.
                lock (Gate) return work();
            }
            catch
            {
                return fallback;
            }
        }

        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA)
            return Guarded();
        return Task.Run(Guarded).GetAwaiter().GetResult();
    }

    private static IMMDeviceEnumerator CreateEnumerator() =>
        (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

    /// <summary>Activates IAudioEndpointVolume on the first available default endpoint for the given roles.
    /// Returns default(T) when no endpoint exists.</summary>
    private static T? WithEndpointVolume<T>(EDataFlow flow, ERole[] roles, Func<IAudioEndpointVolume, T> body)
    {
        IMMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = CreateEnumerator();
            foreach (ERole role in roles)
            {
                IMMDevice? device = null;
                object? activated = null;
                try
                {
                    int hr = enumerator.GetDefaultAudioEndpoint(flow, role, out device);
                    if (hr < 0 || device is null) continue;
                    Guid iid = CoreAudioConstants.IID_IAudioEndpointVolume;
                    Check(device.Activate(ref iid, CoreAudioConstants.CLSCTX_ALL, IntPtr.Zero, out activated));
                    return body((IAudioEndpointVolume)activated);
                }
                finally
                {
                    ComRelease.Release(activated);
                    ComRelease.Release(device);
                }
            }
            return default;
        }
        finally
        {
            ComRelease.Release(enumerator);
        }
    }

    private sealed class SessionEntry
    {
        public required IAudioSessionControl2 Control { get; init; }
        public required ISimpleAudioVolume Volume { get; init; }
        public required uint ProcessId { get; init; }
        public required string ProcessName { get; init; }
        public required string? ExecutablePath { get; init; }
        public string? SessionDisplayName { get; init; }
    }

    /// <summary>Collects every non-expired, non-system session on all active render endpoints,
    /// invokes <paramref name="body"/>, then releases every COM object.</summary>
    private static void WithSessions(Action<List<SessionEntry>> body)
    {
        List<object> toRelease = [];
        List<SessionEntry> sessions = [];
        Dictionary<uint, (string Name, string? Path)?> processCache = [];
        try
        {
            IMMDeviceEnumerator enumerator = CreateEnumerator();
            toRelease.Add(enumerator);
            Check(enumerator.EnumAudioEndpoints(EDataFlow.eRender, CoreAudioConstants.DEVICE_STATE_ACTIVE,
                out IMMDeviceCollection collection));
            toRelease.Add(collection);
            Check(collection.GetCount(out uint deviceCount));

            for (uint d = 0; d < deviceCount; d++)
            {
                if (collection.Item(d, out IMMDevice device) < 0 || device is null) continue;
                toRelease.Add(device);
                Guid iid = CoreAudioConstants.IID_IAudioSessionManager2;
                if (device.Activate(ref iid, CoreAudioConstants.CLSCTX_ALL, IntPtr.Zero, out object managerObj) < 0 ||
                    managerObj is null)
                    continue;
                toRelease.Add(managerObj);
                var manager = (IAudioSessionManager2)managerObj;
                if (manager.GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum) < 0 || sessionEnum is null)
                    continue;
                toRelease.Add(sessionEnum);
                if (sessionEnum.GetCount(out int sessionCount) < 0) continue;

                for (int i = 0; i < sessionCount; i++)
                {
                    if (sessionEnum.GetSession(i, out IAudioSessionControl control) < 0 || control is null) continue;
                    toRelease.Add(control); // QI casts below share this RCW; release it once.
                    if (control is not IAudioSessionControl2 control2 || control is not ISimpleAudioVolume volume)
                        continue;
                    if (control2.GetState(out AudioSessionState state) < 0 || state == AudioSessionState.Expired)
                        continue;
                    if (control2.IsSystemSoundsSession() == CoreAudioConstants.S_OK) continue;
                    if (control2.GetProcessId(out uint pid) < 0 || pid == 0) continue;

                    if (!processCache.TryGetValue(pid, out var proc))
                        processCache[pid] = proc = ResolveProcess(pid);
                    if (proc is null) continue; // process already exited

                    string? sessionName = control2.GetDisplayName(out string dn) >= 0 ? dn : null;
                    sessions.Add(new SessionEntry
                    {
                        Control = control2,
                        Volume = volume,
                        ProcessId = pid,
                        ProcessName = proc.Value.Name,
                        ExecutablePath = proc.Value.Path,
                        SessionDisplayName = sessionName,
                    });
                }
            }

            body(sessions);
        }
        finally
        {
            for (int i = toRelease.Count - 1; i >= 0; i--) ComRelease.Release(toRelease[i]);
        }
    }

    private static string FriendlyName(SessionEntry s)
    {
        string? name = s.SessionDisplayName;
        if (!string.IsNullOrWhiteSpace(name) && !name.StartsWith('@')) return name.Trim();
        if (s.ExecutablePath is { Length: > 0 } path)
        {
            try
            {
                string? description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
            }
            catch { /* unreadable file */ }
        }
        return s.ProcessName;
    }

    private static (string Name, string? Path)? ResolveProcess(uint pid)
    {
        string? name = null;
        try
        {
            using Process process = Process.GetProcessById((int)pid);
            name = process.ProcessName;
        }
        catch
        {
            // Exited, or inaccessible; fall back to the image path below.
        }
        string? path = QueryImagePath(pid);
        if (string.IsNullOrEmpty(name) && path is not null) name = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrEmpty(name) ? null : (name, path);
    }

    // PROCESS_QUERY_LIMITED_INFORMATION works for most elevated processes, unlike Process.MainModule.
    private static string? QueryImagePath(uint pid)
    {
        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}

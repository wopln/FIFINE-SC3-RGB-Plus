using SC3FirmwareTool.Core;
using SC3RGBController.Models;
using SC3RGBController.Services;

var tests = new (string Name, Func<Task> Run)[]
{
    ("1 toggle ON enters SHORTCUT without keepalive", ToggleOnEntersShortcut),
    ("2 toggle OFF restores STOCK", ToggleOffRestoresStock),
    ("3 close while ON does not disable shortcuts", CloseWhileOnDoesNotDisable),
    ("4 close while ON keeps background engine running", CloseKeepsEngineRunning),
    ("5 A-D launch while UI is hidden", BackgroundLaunchesAllButtons),
    ("6 reopening preserves enabled preference", ReopenPreservesPreference),
    ("7 toggle OFF after reopen restores STOCK", ToggleOffAfterReopen),
    ("8 tray Open routes correctly", TrayOpenWorks),
    ("9 tray Disable routes correctly", TrayDisableWorks),
    ("10 tray Exit routes correctly", TrayExitWorks),
    ("11 persisted ON survives app restart", PersistedOnSurvivesRestart),
    ("12 Windows startup restores shortcut session", StartupRestoresSession),
    ("13 reopening does not create duplicate poll loops", NoDuplicatePollingLoops),
    ("background reconnect/hotplug restores session", BackgroundReconnects),
    ("mapping remains A19 B12 C18 D13", ProvenMapping),
    ("counter duplicate/wrap/baseline behavior", CounterBehavior),
    ("diagnostic firmware cannot enter final shortcut mode", DiagnosticRejected),
    ("protocol is query + explicit enable/disable", ProtocolBytes),
    ("Mod 1.5 candidate verification", Mod15CandidateVerification),
    ("final Firmware 1.5 sound-suppression build lock", FinalSoundSuppressionFirmwareLock),
    ("firmware update presentation policy", FirmwareUpdatePresentationPolicy),
    ("RGB transport regression", RgbRegression),
    ("Effect Speed regression", EffectSpeedRegression),
    ("startup policy keeps background registered when shortcuts ON", StartupPolicy)
};

foreach (var test in tests)
{
    await test.Run();
    Console.WriteLine($"PASS: {test.Name}");
}
Console.WriteLine($"PASS: {tests.Length} Custom Buttons background/host checks");

static async Task ToggleOnEntersShortcut()
{
    var transport = SessionTransport();
    await using var controller = NewController(transport, _ => "A.exe", new FakeLauncher());
    await controller.SetPreferenceAsync(true);
    await WaitUntil(() => controller.IsActive);
    Require(transport.EnabledWrites == 1, "toggle ON must send exactly one explicit enable");
    await Task.Delay(80);
    Require(transport.EnabledWrites == 1, "FC/03 was repeated like a keepalive");
}

static async Task ToggleOffRestoresStock()
{
    var transport = SessionTransport();
    await using var controller = NewController(transport, _ => "A.exe", new FakeLauncher());
    await controller.SetPreferenceAsync(true);
    await WaitUntil(() => controller.IsActive);
    await controller.SetPreferenceAsync(false);
    Require(transport.OffWrites == 1, "toggle OFF must send STOCK command");
    Require(!controller.IsActive && !controller.IsRunning, "controller remained active after OFF");
}

static async Task CloseWhileOnDoesNotDisable()
{
    var transport = SessionTransport();
    await using var controller = NewController(transport, _ => "A.exe", new FakeLauncher());
    await controller.SetPreferenceAsync(true);
    await WaitUntil(() => controller.IsActive);
    Require(CustomShortcutHostPolicy.KeepRunningOnWindowClose(true), "close policy must hide rather than terminate when ON");
    await Task.Delay(60);
    Require(transport.OffWrites == 0, "normal close policy sent OFF");
    Require(controller.IsActive, "normal close policy stopped shortcut session");
}

static async Task CloseKeepsEngineRunning()
{
    var transport = SessionTransport();
    await using var controller = NewController(transport, _ => "A.exe", new FakeLauncher());
    await controller.SetPreferenceAsync(true);
    await WaitUntil(() => controller.IsActive);
    Require(CustomShortcutHostPolicy.KeepRunningOnWindowClose(true), "background mode policy failed");
    await Task.Delay(60);
    Require(controller.IsRunning && controller.IsActive, "background engine stopped while UI would be hidden");
}

static async Task BackgroundLaunchesAllButtons()
{
    var transport = FakeTransport.WithSequence(
        Reply(0, 10, 0), Reply(0, 10, 1),
        Reply(19, 11, 1), Reply(19, 11, 1),
        Reply(12, 12, 1), Reply(18, 13, 1), Reply(13, 14, 1));
    var launcher = new FakeLauncher();
    var paths = new Dictionary<CustomButtonId, string?>
    {
        [CustomButtonId.A] = "A.exe", [CustomButtonId.B] = "B.exe",
        [CustomButtonId.C] = "C.exe", [CustomButtonId.D] = "D.exe"
    };
    await using var controller = NewController(transport, b => paths[b], launcher);
    await controller.SetPreferenceAsync(true);
    await WaitUntil(() => launcher.Launched.Count == 4);
    Require(launcher.Launched.SequenceEqual(new[] { "A.exe", "B.exe", "C.exe", "D.exe" }), "background mapping/launch order wrong");
}

static Task ReopenPreservesPreference()
{
    var settings = new AppSettings { CustomShortcutsEnabled = true };
    AppSettings reopened = SettingsStore.Deserialize(SettingsStore.Serialize(settings));
    Require(reopened.CustomShortcutsEnabled, "reopen lost ON preference");
    return Task.CompletedTask;
}

static async Task ToggleOffAfterReopen()
{
    AppSettings reopened = SettingsStore.Deserialize(SettingsStore.Serialize(new AppSettings { CustomShortcutsEnabled = true }));
    var transport = SessionTransport();
    await using var controller = NewController(transport, _ => "A.exe", new FakeLauncher());
    await controller.SetPreferenceAsync(reopened.CustomShortcutsEnabled);
    await WaitUntil(() => controller.IsActive);
    reopened.CustomShortcutsEnabled = false;
    await controller.SetPreferenceAsync(false);
    Require(transport.OffWrites == 1 && !controller.IsActive, "OFF after reopen did not restore STOCK");
}

static Task TrayOpenWorks()
{
    var router = new TrayCommandRouter();
    int calls = 0; router.OpenRequested += (_, _) => calls++;
    router.Open(); Require(calls == 1, "tray Open did not route once");
    return Task.CompletedTask;
}

static Task TrayDisableWorks()
{
    var router = new TrayCommandRouter();
    int calls = 0; router.DisableShortcutsRequested += (_, _) => calls++;
    router.DisableShortcuts(); Require(calls == 1, "tray Disable did not route once");
    return Task.CompletedTask;
}

static async Task TrayExitWorks()
{
    var transport = SessionTransport();
    await using var controller = NewController(transport, _ => "A.exe", new FakeLauncher());
    await controller.SetPreferenceAsync(true);
    await WaitUntil(() => controller.IsActive);

    var router = new TrayCommandRouter();
    bool exitRequested = false;
    router.ExitRequested += (_, _) => exitRequested = true;
    router.Exit();
    Require(exitRequested, "tray Exit did not route");

    await controller.StopAsync(sendOff: true);
    Require(!controller.IsRunning && !controller.IsActive && transport.OffWrites == 1, "tray Exit cleanup did not stop background engine cleanly");
}

static Task PersistedOnSurvivesRestart()
{
    var before = new AppSettings { CustomShortcutsEnabled = true, CustomAPath = @"C:\Apps\Discord.exe", CustomAName = "Discord" };
    AppSettings after = SettingsStore.Deserialize(SettingsStore.Serialize(before));
    Require(after.CustomShortcutsEnabled && after.CustomAPath == before.CustomAPath && after.CustomAName == "Discord", "persisted ON/assignment did not survive restart");
    return Task.CompletedTask;
}

static async Task StartupRestoresSession()
{
    Require(CustomShortcutHostPolicy.ShouldRegisterStartup(false, true), "shortcuts ON must force startup registration");
    AppSettings startupSettings = SettingsStore.Deserialize(SettingsStore.Serialize(new AppSettings { StartWithWindows = false, CustomShortcutsEnabled = true }));
    var transport = SessionTransport();
    await using var controller = NewController(transport, _ => "A.exe", new FakeLauncher());
    await controller.SetPreferenceAsync(startupSettings.CustomShortcutsEnabled);
    await WaitUntil(() => controller.IsActive);
    Require(transport.EnabledWrites == 1, "startup did not restore SHORTCUT mode");
}

static async Task NoDuplicatePollingLoops()
{
    var transport = SessionTransport();
    await using var controller = NewController(transport, _ => "A.exe", new FakeLauncher());
    await controller.SetPreferenceAsync(true);
    await WaitUntil(() => controller.IsActive);
    await Task.WhenAll(controller.StartAsync(), controller.StartAsync(), controller.StartAsync());
    await Task.Delay(80);
    Require(transport.EnabledWrites == 1, "reopen/start created duplicate shortcut sessions");
    Require(transport.MaxConcurrentQueries == 1, "overlapping FC/02 query loops detected");
}

static async Task BackgroundReconnects()
{
    var transport = SessionTransport();
    transport.OpenOk = false;
    await using var controller = NewController(transport, _ => "A.exe", new FakeLauncher(), retryMs: 10);
    await controller.SetPreferenceAsync(true);
    await Task.Delay(30);
    Require(!controller.IsActive && controller.IsRunning, "engine stopped instead of waiting for hotplug");
    transport.OpenOk = true;
    await WaitUntil(() => controller.IsActive, 1000);
    Require(transport.EnabledWrites == 1, "reconnect did not establish shortcut mode exactly once");
}

static Task ProvenMapping()
{
    Require(CustomButtonEventTracker.TryMapKey(19, out var a) && a == CustomButtonId.A, "A mapping wrong");
    Require(CustomButtonEventTracker.TryMapKey(12, out var b) && b == CustomButtonId.B, "B mapping wrong");
    Require(CustomButtonEventTracker.TryMapKey(18, out var c) && c == CustomButtonId.C, "C mapping wrong");
    Require(CustomButtonEventTracker.TryMapKey(13, out var d) && d == CustomButtonId.D, "D mapping wrong");
    return Task.CompletedTask;
}

static Task CounterBehavior()
{
    var tracker = new CustomButtonEventTracker();
    tracker.SetBaseline(ushort.MaxValue);
    Require(tracker.TryAccept(19, 0, out var button) && button == CustomButtonId.A, "counter wrap not accepted");
    Require(!tracker.TryAccept(19, 0, out _), "duplicate counter was emitted twice");
    tracker.SetBaseline(77); Require(!tracker.TryAccept(19, 77, out _), "baseline stale event launched");
    return Task.CompletedTask;
}

static async Task DiagnosticRejected()
{
    var transport = FakeTransport.WithSequence(new Sc3QueryReply(Sc3FirmwareFlavor.DiagnosticMod14, true, true, 1, 0, 0, 0));
    await using var controller = NewController(transport, _ => "A.exe", new FakeLauncher(), retryMs: 20);
    await controller.SetPreferenceAsync(true);
    await Task.Delay(50);
    Require(!controller.IsActive && transport.EnabledWrites == 0 && transport.OffWrites == 0, "diagnostic firmware received Mod 1.5 mode command");
}

static Task ProtocolBytes()
{
    CheckReport(HidDeviceClient.BuildFirmwareQueryReport(), new byte[] { 0xA5,0x5A,0xFC,0x01,0x02,0x16 });
    CheckReport(HidDeviceClient.BuildCustomButtonModeReport(true), new byte[] { 0xA5,0x5A,0xFC,0x01,0x03,0x16 });
    CheckReport(HidDeviceClient.BuildCustomButtonModeReport(false), new byte[] { 0xA5,0x5A,0xFC,0x01,0x04,0x16 });
    return Task.CompletedTask;
}

static Task Mod15CandidateVerification()
{
    string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    string candidate = Path.Combine(repo, "firmware", "candidates", "mod15", Mod15CandidatePolicy.FirmwareFileName);
    MvaPackage package = MvaPackage.LoadMod15Candidate(candidate);
    Require(package.Sha256 == "589b2fcb590b999c905693df6aba6a343343ac6a8241b4aa9802853a72fa525b", "updated Mod 1.5 SHA mismatch");
    Require(package.Data.LongLength == Mod15CandidatePolicy.FirmwareSize, "Mod 1.5 size mismatch");
    return Task.CompletedTask;
}

static Task FinalSoundSuppressionFirmwareLock()
{
    const string finalSha = "589b2fcb590b999c905693df6aba6a343343ac6a8241b4aa9802853a72fa525b";
    const string retiredSha = "8f3c5f56770b9e822481bebf9fa303857dd8324eff53c1cd2eca8969ddd384a4";
    Require(ReleasePolicy.FirmwareSha256 == finalSha, "production policy no longer locks the physically validated sound-suppression firmware");
    Require(ReleasePolicy.FirmwareCrc16 == 0xC12C, "production firmware CRC lock changed");
    Require(ReleasePolicy.FirmwareSha256 != retiredSha, "retired pre-sound-fix firmware became production again");
    return Task.CompletedTask;
}
static Task FirmwareUpdatePresentationPolicy()
{
    MixerFirmwarePresentation mod14 = MixerFirmwarePresentation.Create(true, true, Sc3FirmwareFlavor.Mod14, false);
    Require(mod14.UpdateAvailable && !mod14.CustomButtonsAvailable && mod14.Current == "RGB+ Firmware 1.4", "Mod 1.4 migration presentation");

    MixerFirmwarePresentation stock = MixerFirmwarePresentation.Create(true, true, Sc3FirmwareFlavor.Stock, false);
    Require(stock.UpdateAvailable && !stock.CustomButtonsAvailable && stock.Status == "RGB+ firmware not installed", "Stock -> Mod 1.5 presentation");

    MixerFirmwarePresentation current = MixerFirmwarePresentation.Create(true, true, Sc3FirmwareFlavor.Mod15, true);
    Require(!current.UpdateAvailable && current.CustomButtonsAvailable && current.Status == "Up to date", "current Mod 1.5 must avoid reflash");

    MixerFirmwarePresentation invalid = MixerFirmwarePresentation.Create(true, true, Sc3FirmwareFlavor.Mod15, false);
    Require(!invalid.UpdateAvailable && !invalid.CustomButtonsAvailable && invalid.Status == "Verification failed", "unverified Mod 1.5 must fail closed");
    return Task.CompletedTask;
}
static Task RgbRegression()
{
    byte[] report = HidDeviceClient.BuildCustomRgbReport(255,120,33);
    HidDeviceClient.ValidateCustomRgbReport(report,255,120,33);
    return Task.CompletedTask;
}

static Task EffectSpeedRegression()
{
    Require(!EffectSpeedPolicy.SupportsSpeed(LightingEffect.Static), "Static speed regression");
    foreach (var effect in new[] { LightingEffect.Breathing, LightingEffect.Rainbow, LightingEffect.Pulse, LightingEffect.ColorCycle })
        Require(EffectSpeedPolicy.SupportsSpeed(effect), $"{effect} speed regression");
    return Task.CompletedTask;
}

static Task StartupPolicy()
{
    Require(!CustomShortcutHostPolicy.ShouldRegisterStartup(false,false), "startup unexpectedly enabled");
    Require(CustomShortcutHostPolicy.ShouldRegisterStartup(true,false), "normal Start with Windows lost");
    Require(CustomShortcutHostPolicy.ShouldRegisterStartup(false,true), "shortcut background startup not forced");
    return Task.CompletedTask;
}

static CustomButtonShortcutController NewController(FakeTransport transport, Func<CustomButtonId,string?> resolver,
    FakeLauncher launcher, int pollMs = 3, int retryMs = 20) =>
    new(transport, resolver, launcher, TimeSpan.FromMilliseconds(pollMs), TimeSpan.FromMilliseconds(retryMs));

static FakeTransport SessionTransport() => FakeTransport.WithSequence(Reply(0,1,0), Reply(0,1,1), Reply(0,1,1));
static Sc3QueryReply Reply(byte key, ushort counter, byte mode) => new(Sc3FirmwareFlavor.Mod15,true,true,2,key,counter,mode);

static void CheckReport(byte[] report, byte[] payload)
{
    Require(report.Length == HidDeviceClient.ExpectedOutputLength && report[0] == 0, "Windows HID report shape wrong");
    Require(report.AsSpan(1,payload.Length).SequenceEqual(payload), "protocol payload mismatch");
    Require(report.AsSpan(1+payload.Length).IndexOfAnyExcept((byte)0) < 0, "non-zero HID padding");
}

static async Task WaitUntil(Func<bool> condition, int timeoutMs = 1000)
{
    using var cts = new CancellationTokenSource(timeoutMs);
    while (!condition())
    {
        try { await Task.Delay(2, cts.Token); }
        catch (OperationCanceledException) { throw new Exception("Timed out waiting for expected async state"); }
    }
}

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

sealed class FakeLauncher : IApplicationShortcutLauncher
{
    public List<string> Launched { get; } = [];
    public ShortcutLaunchResult Launch(bool enabled, string? path)
    {
        if (!enabled) return ShortcutLaunchResult.Disabled;
        if (string.IsNullOrWhiteSpace(path)) return ShortcutLaunchResult.Unassigned;
        Launched.Add(path); return ShortcutLaunchResult.Launched;
    }
}

sealed class FakeTransport : ISc3CustomButtonTransport
{
    private readonly object _gate = new();
    private readonly Queue<Sc3QueryReply> _replies = new();
    private Sc3QueryReply _last = new(Sc3FirmwareFlavor.Mod15,true,true,2,0,0,0);
    private int _queriesInFlight;
    public bool OpenOk { get; set; } = true;
    public bool QueryOk { get; set; } = true;
    public int EnabledWrites { get; private set; }
    public int OffWrites { get; private set; }
    public int QueryCalls { get; private set; }
    public int MaxConcurrentQueries { get; private set; }

    public static FakeTransport WithSequence(params Sc3QueryReply[] replies)
    {
        var t = new FakeTransport();
        foreach (var r in replies) t._replies.Enqueue(r);
        if (replies.Length > 0) t._last = replies[0];
        return t;
    }

    public bool TryOpen(out string detail) { detail=OpenOk?"Connected":"Unavailable"; return OpenOk; }

    public bool TryQuerySc3(out Sc3QueryReply reply, out string detail)
    {
        int active = Interlocked.Increment(ref _queriesInFlight);
        lock (_gate) MaxConcurrentQueries = Math.Max(MaxConcurrentQueries, active);
        try
        {
            lock (_gate)
            {
                QueryCalls++;
                if (!QueryOk) { reply=default!; detail="Query failed"; return false; }
                if (_replies.Count>0) _last=_replies.Dequeue();
                reply=_last; detail="OK"; return true;
            }
        }
        finally { Interlocked.Decrement(ref _queriesInFlight); }
    }

    public bool TrySetCustomButtonMode(bool enabled, out string detail)
    {
        lock (_gate)
        {
            if (enabled) { EnabledWrites++; _last=_last with { RuntimeMode=1 }; }
            else { OffWrites++; _last=_last with { RuntimeMode=0 }; }
            detail="OK"; return true;
        }
    }
}

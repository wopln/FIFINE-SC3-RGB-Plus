using SC3FirmwareTool.Core;
using SC3RGBController.Models;
using SC3RGBController.Services;
using SC3RGBController.Services.Actions;
using SC3RGBController.Services.Audio;

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
    ("final Firmware 1.5 event-driven build lock", FinalEventDrivenFirmwareLock),
    ("firmware update presentation policy", FirmwareUpdatePresentationPolicy),
    ("RGB transport regression", RgbRegression),
    ("Effect Speed regression", EffectSpeedRegression),
    ("saved lighting state restores without a MainWindow", SavedLightingStateRestore),
    ("lighting worker does not read WPF controls", LightingWorkerAvoidsWpfControls),
    ("background lighting retries and restores once per connection", BackgroundLightingReconnectRestore),
    ("startup policy keeps background registered when shortcuts ON", StartupPolicy),
    ("production controller blocks unavailable event transport", ProductionPollingSafetyGuard),
    ("stability policy requires event-driven transport", StabilityPolicy),
    ("MI_03 event profile is distinct from MI_04 control", EventInterfaceProfile),
    ("shortcut session uses interrupt events without FC/02 polling", EventDrivenShortcuts),
    ("accent foreground and outline colors stay readable", AccentContrast),
    ("custom action Serialize/Parse round-trips every kind", CustomActionRoundTrip),
    ("custom action legacy path, garbage and step clamping", CustomActionLegacyAndClamping),
    ("action runner disabled/unassigned/program dispatch", RunnerBasicDispatch),
    ("action runner app mute raises feedback", RunnerAppMuteFeedback),
    ("action runner volume/mic/system/media dispatch", RunnerAudioAndMediaDispatch),
    ("action runner reports missing audio session", RunnerMissingSession),
    ("action runner never throws on audio failures", RunnerSwallowsExceptions),
    ("action runner LaunchApp validates AppUserModelID", RunnerLaunchAppValidation),
    ("audio step math and INPUT struct layout", AudioStepAndInputLayout)
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
    Require(transport.OffWrites == 2, "session pre-clear and toggle OFF must each send STOCK once");
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
    Require(transport.OffWrites == 1, "normal close sent an extra OFF after the session pre-clear");
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
        Reply(0, 10, 0),
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
    Require(transport.OffWrites == 2 && !controller.IsActive, "OFF after reopen did not restore STOCK");
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
    Require(!controller.IsRunning && !controller.IsActive && transport.OffWrites == 2, "tray Exit cleanup did not stop background engine cleanly");
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
    tracker.ArmForInterruptEvents();
    Require(tracker.TryAccept(12, 78, out var first) && first == CustomButtonId.B,
        "first clean interrupt event was not accepted");
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
    string repo = FindRepoRoot();
    string candidate = Path.Combine(repo, "firmware", "candidates", "mod15", Mod15CandidatePolicy.FirmwareFileName);
    MvaPackage package = MvaPackage.LoadMod15Candidate(candidate);
    Require(package.Sha256 == "02dacd0f2692e4ea064647378718047ded3c197081b53a26a95a0e8bdf6f9acf", "updated Mod 1.5 SHA mismatch");
    Require(package.Data.LongLength == Mod15CandidatePolicy.FirmwareSize, "Mod 1.5 size mismatch");
    return Task.CompletedTask;
}

static string FindRepoRoot()
{
    foreach (string seed in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        DirectoryInfo? current = new(seed);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "SC3RGBController.csproj")))
                return current.FullName;
            current = current.Parent;
        }
    }
    throw new DirectoryNotFoundException("FIFINE SC3 RGB+ repository root was not found.");
}

static Task FinalEventDrivenFirmwareLock()
{
    const string finalSha = "02dacd0f2692e4ea064647378718047ded3c197081b53a26a95a0e8bdf6f9acf";
    const string pollingSha = "589b2fcb590b999c905693df6aba6a343343ac6a8241b4aa9802853a72fa525b";
    const string retiredSha = "8f3c5f56770b9e822481bebf9fa303857dd8324eff53c1cd2eca8969ddd384a4";
    Require(ReleasePolicy.FirmwareSha256 == finalSha, "production policy no longer locks the physically validated event-driven firmware");
    Require(ReleasePolicy.FirmwareCrc16 == 0xFB45, "production firmware CRC lock changed");
    Require(ReleasePolicy.CbtnVersion == 4, "production CBTN capability is not event-driven v4");
    Require(ReleasePolicy.FirmwareSha256 != pollingSha, "polling-only firmware became production again");
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

static Task SavedLightingStateRestore()
{
    AppSettings staticSettings = new() { LastHex = "#804020", Brightness = 50, Effect = "Static", IsLightingEnabled = true };
    SavedLightingState staticState = SavedLightingState.From(staticSettings);
    var output = staticState.GetOutput(DateTime.UnixEpoch, DateTime.UnixEpoch);
    Require(output.R == 64 && output.G == 32 && output.B == 16, "static color or brightness did not restore");

    foreach (LightingEffect effect in new[] { LightingEffect.Breathing, LightingEffect.Rainbow, LightingEffect.Pulse, LightingEffect.ColorCycle })
    {
        AppSettings settings = new() { LastHex = "#4080C0", Brightness = 77, Effect = effect.ToString(), IsLightingEnabled = true };
        SavedLightingState state = SavedLightingState.From(settings);
        var effectOutput = state.GetOutput(DateTime.UnixEpoch, DateTime.UnixEpoch.AddMilliseconds(700));
        Require(effectOutput != System.Windows.Media.Colors.Black, $"{effect} did not restore a visible output");
    }

    SavedLightingState off = SavedLightingState.From(new AppSettings { IsLightingEnabled = false, Effect = "Rainbow", Brightness = 100 });
    Require(off.GetOutput(DateTime.UnixEpoch, DateTime.UtcNow) == System.Windows.Media.Colors.Black, "saved lighting OFF was not preserved");
    return Task.CompletedTask;
}

static Task LightingWorkerAvoidsWpfControls()
{
    string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "MainWindow.xaml.cs"));
    int start = source.IndexOf("private Color GetEffectColor", StringComparison.Ordinal);
    int end = source.IndexOf("private static LightingEffect ParseLightingEffect", start, StringComparison.Ordinal);
    Require(start >= 0 && end > start, "GetEffectColor source block was not found");
    string workerMethod = source[start..end];
    Require(!workerMethod.Contains("CurrentBrightness", StringComparison.Ordinal),
        "background lighting worker reads the WPF brightness slider");
    Require(workerMethod.Contains("_settings.Brightness", StringComparison.Ordinal),
        "background lighting worker does not use the thread-safe saved brightness value");
    return Task.CompletedTask;
}

static async Task BackgroundLightingReconnectRestore()
{
    var transport = new FakeLightingTransport { OpensAfterAttempt = 3 };
    SavedLightingState state = SavedLightingState.From(new AppSettings { LastHex = "#204060", Brightness = 100, Effect = "Static", IsLightingEnabled = true });
    await using var restorer = new BackgroundLightingRestorer(transport, () => true, () => state,
        TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(8));
    restorer.Start();
    await WaitUntil(() => transport.Writes.Count == 1);
    await Task.Delay(35);
    Require(transport.Writes.Count == 1, "healthy static connection was reset repeatedly");

    transport.Disconnect();
    await WaitUntil(() => transport.Writes.Count == 2);
    Require(transport.OpenAttempts >= 4, "delayed/reconnected SC3 did not retry before restore");
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

static FakeTransport SessionTransport() => FakeTransport.WithSequence(Reply(0,1,0), Reply(0,1,1));
static Sc3QueryReply Reply(byte key, ushort counter, byte mode) => new(Sc3FirmwareFlavor.Mod15,true,true,4,key,counter,mode);

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

static Task AccentContrast()
{
    var panel = System.Windows.Media.Color.FromRgb(9, 9, 9);
    foreach (string hex in new[] { "#FFFFFF", "#000000", "#FF7800", "#FFD000", "#0000FF", "#00FF00", "#9D00FF", "#202020", "#808080" })
    {
        Require(ColorText.TryParseHex(hex, out var color), "parse " + hex);
        Require(ColorText.Contrast(ColorText.ForegroundOn(color), color) >= 4.5, $"content on {hex} must reach 4.5:1");
        Require(ColorText.Contrast(ColorText.VisibleOnDark(color), panel) >= 4.5, $"{hex} accent text must reach 4.5:1 on panels");
    }
    Require(ColorText.ForegroundOn(System.Windows.Media.Colors.White) == System.Windows.Media.Colors.Black, "white accent uses black content");
    Require(ColorText.VisibleOnDark(System.Windows.Media.Colors.White) == System.Windows.Media.Colors.White, "bright accents are unchanged");
    return Task.CompletedTask;
}

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static async Task ProductionPollingSafetyGuard()
{
    var transport = SessionTransport();
    await using var controller = new CustomButtonShortcutController(
        transport, _ => "A.exe", new FakeLauncher(),
        TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(750),
        eventDrivenTransportAllowed: false);
    await controller.SetPreferenceAsync(true);
    await Task.Delay(150);
    Require(!controller.IsRunning && transport.QueryCalls == 0,
        "stability guard allowed continuous FC/02 traffic");
}

static Task StabilityPolicy()
{
    Require(CustomShortcutStabilityPolicy.EventDrivenTransportEnabled,
        "event-driven shortcut transport was unexpectedly disabled");
    Require(CustomShortcutStabilityPolicy.Message.Contains("event-driven", StringComparison.OrdinalIgnoreCase),
        "event firmware requirement is missing");
    return Task.CompletedTask;
}

static Task EventInterfaceProfile()
{
    const string control = @"\\?\hid#vid_3142&pid_0c33&mi_04#control";
    const string events = @"\\?\hid#vid_3142&pid_0c33&mi_03#events";
    Require(HidDeviceClient.MatchesExpectedIdentity(
        control, 0x3142, 0x0C33, "MV-SILICON", "fifine SC3", "20190808",
        0xFF00, 0x55AA, 257, 257, 9), "MI_04 control profile was rejected");
    Require(!HidDeviceClient.MatchesExpectedIdentity(
        events, 0x3142, 0x0C33, "MV-SILICON", "fifine SC3", "20190808",
        0x00FF, 0x0001, 2, 0, 0), "MI_03 was accepted as the control profile");
    Require(HidDeviceClient.MatchesExpectedEventIdentity(
        events, 0x3142, 0x0C33, "MV-SILICON", "fifine SC3", "20190808",
        0x00FF, 0x0001, 2, 0, 0), "MI_03 event profile was rejected");
    Require(!HidDeviceClient.MatchesExpectedEventIdentity(
        control, 0x3142, 0x0C33, "MV-SILICON", "fifine SC3", "20190808",
        0xFF00, 0x55AA, 257, 257, 9), "MI_04 was accepted as the event profile");
    return Task.CompletedTask;
}

static async Task EventDrivenShortcuts()
{
    var transport = SessionTransport();
    await using var controller = NewController(transport, _ => "A.exe", new FakeLauncher());
    await controller.SetPreferenceAsync(true);
    await WaitUntil(() => controller.IsActive);
    await Task.Delay(40);
    Require(transport.SessionPrimeCalls == 1, "interrupt session was initialized more than once");
    Require(transport.DrainCalls == 1, "stale interrupt queue was not drained exactly once");
    Require(transport.OffWrites == 1, "interrupt session did not return to STOCK before draining");
    Require(transport.Operations.Take(4).SequenceEqual(new[] { "BEGIN", "OFF", "DRAIN", "ON" }),
        "event session order was not BEGIN -> STOCK -> DRAIN -> SHORTCUT");
    Require(transport.EventReadCalls >= 2, "interrupt event reads did not continue");
    Require(transport.QueryCalls == 0, "shortcut loop issued FC/02 control queries");
}

static Task CustomActionRoundTrip()
{
    foreach (CustomActionKind kind in Enum.GetValues<CustomActionKind>())
    {
        string? target = kind switch
        {
            CustomActionKind.LaunchProgram => @"C:\Program Files\App Folder\app.exe",
            CustomActionKind.LaunchApp => "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
            CustomActionKind.AppMuteToggle or CustomActionKind.AppVolumeUp or CustomActionKind.AppVolumeDown => "Discord & Co=1?",
            _ => null
        };
        var action = new CustomButtonAction(kind, target, 25);
        string stored = action.Serialize();
        CustomButtonAction parsed = CustomButtonAction.Parse(stored);
        Require(parsed.Kind == kind, $"{kind} kind did not round-trip ({stored})");
        Require(parsed.Target == target, $"{kind} target did not round-trip ({stored})");
        if (action.UsesStep) Require(parsed.Step == 25, $"{kind} step did not round-trip ({stored})");
        if (kind == CustomActionKind.None) Require(stored.Length == 0, "None must serialize to empty");
        if (kind == CustomActionKind.LaunchProgram) Require(stored == target, "LaunchProgram must stay a plain path");
    }
    return Task.CompletedTask;
}

static Task CustomActionLegacyAndClamping()
{
    CustomButtonAction legacy = CustomButtonAction.Parse(@"C:\Tools\obs64.exe");
    Require(legacy.Kind == CustomActionKind.LaunchProgram && legacy.Target == @"C:\Tools\obs64.exe", "legacy path must be LaunchProgram");
    Require(CustomButtonAction.Parse("sc3action:Nope").Kind == CustomActionKind.None, "unknown kind must be None");
    Require(CustomButtonAction.Parse("sc3action:LaunchProgram?target=x.exe").Kind == CustomActionKind.None, "LaunchProgram is never scheme-encoded");
    Require(CustomButtonAction.Parse(null).Kind == CustomActionKind.None && CustomButtonAction.Parse("  ").Kind == CustomActionKind.None, "blank must be None");
    Require(CustomButtonAction.Parse("SC3ACTION:mediaplaypause").Kind == CustomActionKind.MediaPlayPause, "scheme/kind must be case-insensitive");
    Require(CustomButtonAction.Parse("sc3action:SystemVolumeUp?step=500").Step == 100, "step must clamp to 100");
    Require(CustomButtonAction.Parse("sc3action:SystemVolumeUp?step=-3").Step == 1, "step must clamp to 1");
    Require(CustomButtonAction.Parse("sc3action:SystemVolumeUp?step=abc").Step == CustomButtonAction.DefaultStep, "bad step uses default");
    Require(new CustomButtonAction(CustomActionKind.SystemVolumeDown, Step: 0).Serialize().EndsWith("step=1"), "serialize must clamp step");
    Require(new CustomButtonAction(CustomActionKind.AppVolumeUp, "Spotify", 999).Serialize().EndsWith("step=100"), "serialize must clamp step high");
    return Task.CompletedTask;
}

static Task RunnerBasicDispatch()
{
    var programs = new FakeLauncher();
    var audio = new FakeAudio();
    var media = new FakeMediaKeys();
    var runner = new CustomButtonActionRunner(programs, audio, media, _ => true);
    Require(runner.Launch(false, "sc3action:MediaNext") == ShortcutLaunchResult.Disabled, "disabled must not run");
    Require(media.Sent.Count == 0, "disabled still sent a key");
    Require(runner.Launch(true, null) == ShortcutLaunchResult.Unassigned, "null must be unassigned");
    Require(runner.Launch(true, "") == ShortcutLaunchResult.Unassigned, "empty must be unassigned");
    Require(runner.Launch(true, "sc3action:Nope") == ShortcutLaunchResult.Unassigned, "garbage must be unassigned");
    Require(runner.Launch(true, @"C:\Apps\obs.exe") == ShortcutLaunchResult.Launched, "legacy path must use program launcher");
    Require(programs.Launched.SequenceEqual(new[] { @"C:\Apps\obs.exe" }), "program launcher received wrong path");
    Require(audio.Calls.Count == 0, "launch touched audio");
    return Task.CompletedTask;
}

static Task RunnerAppMuteFeedback()
{
    var audio = new FakeAudio();
    var runner = new CustomButtonActionRunner(new FakeLauncher(), audio, new FakeMediaKeys(), _ => true);
    List<string> feedback = [];
    runner.Feedback += (_, m) => feedback.Add(m);
    string stored = new CustomButtonAction(CustomActionKind.AppMuteToggle, "Discord").Serialize();
    Require(runner.Launch(true, stored) == ShortcutLaunchResult.Launched, "app mute should report Launched");
    Require(audio.Calls.SequenceEqual(new[] { "ToggleAppMute:Discord" }), "ToggleAppMute(\"Discord\") not called");
    Require(feedback.SequenceEqual(new[] { "Discord muted" }), "feedback not raised with result message");
    return Task.CompletedTask;
}

static Task RunnerAudioAndMediaDispatch()
{
    var audio = new FakeAudio();
    var media = new FakeMediaKeys();
    var runner = new CustomButtonActionRunner(new FakeLauncher(), audio, media, _ => true);
    int feedbackCount = 0;
    runner.Feedback += (_, _) => feedbackCount++;
    string S(CustomActionKind k, string? t = null, int step = 10) => new CustomButtonAction(k, t, step).Serialize();
    Require(runner.Launch(true, S(CustomActionKind.AppVolumeUp, "Spotify", 15)) == ShortcutLaunchResult.Launched, "app vol up");
    Require(runner.Launch(true, S(CustomActionKind.AppVolumeDown, "Spotify", 5)) == ShortcutLaunchResult.Launched, "app vol down");
    Require(runner.Launch(true, S(CustomActionKind.MicMuteToggle)) == ShortcutLaunchResult.Launched, "mic");
    Require(runner.Launch(true, S(CustomActionKind.SystemMuteToggle)) == ShortcutLaunchResult.Launched, "sys mute");
    Require(runner.Launch(true, S(CustomActionKind.SystemVolumeUp, step: 20)) == ShortcutLaunchResult.Launched, "sys up");
    Require(runner.Launch(true, S(CustomActionKind.SystemVolumeDown, step: 30)) == ShortcutLaunchResult.Launched, "sys down");
    Require(audio.Calls.SequenceEqual(new[]
    {
        "StepAppVolume:Spotify:15", "StepAppVolume:Spotify:-5", "ToggleMicrophoneMute",
        "ToggleSystemMute", "StepSystemVolume:20", "StepSystemVolume:-30"
    }), "audio dispatch wrong: " + string.Join(",", audio.Calls));
    Require(runner.Launch(true, S(CustomActionKind.MediaPlayPause)) == ShortcutLaunchResult.Launched, "play/pause");
    Require(runner.Launch(true, S(CustomActionKind.MediaNext)) == ShortcutLaunchResult.Launched, "next");
    Require(runner.Launch(true, S(CustomActionKind.MediaPrevious)) == ShortcutLaunchResult.Launched, "prev");
    Require(media.Sent.SequenceEqual(new[] { MediaKey.PlayPause, MediaKey.Next, MediaKey.Previous }), "media dispatch wrong");
    Require(feedbackCount == 9, $"every audio/media action must raise feedback (got {feedbackCount})");
    media.Succeed = false;
    Require(runner.Launch(true, S(CustomActionKind.MediaNext)) == ShortcutLaunchResult.Failed, "rejected SendInput must be Failed");
    Require(feedbackCount == 10, "failed media key must still raise feedback");
    Require(runner.Launch(true, "sc3action:AppMuteToggle") == ShortcutLaunchResult.MissingTarget, "app action without target");
    return Task.CompletedTask;
}

static Task RunnerMissingSession()
{
    var audio = new FakeAudio { NoSession = true };
    var runner = new CustomButtonActionRunner(new FakeLauncher(), audio, new FakeMediaKeys(), _ => true);
    List<string> feedback = [];
    runner.Feedback += (_, m) => feedback.Add(m);
    Require(runner.Launch(true, "sc3action:AppMuteToggle?target=Discord") == ShortcutLaunchResult.MissingTarget, "no session must be MissingTarget");
    Require(feedback.SequenceEqual(new[] { "Discord isn't playing audio right now" }), "missing session feedback wrong");
    return Task.CompletedTask;
}

static Task RunnerSwallowsExceptions()
{
    var audio = new FakeAudio { Throw = true };
    var media = new FakeMediaKeys { Throw = true };
    var runner = new CustomButtonActionRunner(new FakeLauncher(), audio, media, _ => throw new InvalidOperationException());
    int feedbackCount = 0;
    runner.Feedback += (_, _) => { feedbackCount++; throw new InvalidOperationException("bad subscriber"); };
    foreach (string stored in new[] { "sc3action:AppMuteToggle?target=Discord", "sc3action:MicMuteToggle",
                 "sc3action:SystemVolumeUp?step=10", "sc3action:MediaPlayPause", "sc3action:LaunchApp?target=Foo!App" })
        Require(runner.Launch(true, stored) == ShortcutLaunchResult.Failed, stored + " must be Failed when the backend throws");
    Require(feedbackCount == 4, "audio/media failures must still raise feedback");
    return Task.CompletedTask;
}

static Task RunnerLaunchAppValidation()
{
    List<System.Diagnostics.ProcessStartInfo> started = [];
    var runner = new CustomButtonActionRunner(new FakeLauncher(), new FakeAudio(), new FakeMediaKeys(),
        psi => { started.Add(psi); return true; });
    string stored = new CustomButtonAction(CustomActionKind.LaunchApp, "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App").Serialize();
    Require(runner.Launch(true, stored) == ShortcutLaunchResult.Launched, "LaunchApp should launch");
    Require(started.Count == 1 && started[0].FileName == "explorer.exe" && started[0].UseShellExecute &&
            started[0].Arguments == @"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "LaunchApp start info wrong");
    Require(runner.Launch(true, "sc3action:LaunchApp") == ShortcutLaunchResult.MissingTarget, "empty app id");
    Require(runner.Launch(true, new CustomButtonAction(CustomActionKind.LaunchApp, "a\" & calc").Serialize()) == ShortcutLaunchResult.UnsupportedTarget, "quote must be rejected");
    Require(runner.Launch(true, new CustomButtonAction(CustomActionKind.LaunchApp, "a\nb").Serialize()) == ShortcutLaunchResult.UnsupportedTarget, "newline must be rejected");
    Require(started.Count == 1, "invalid app ids must not start a process");
    return Task.CompletedTask;
}

static Task AudioStepAndInputLayout()
{
    Require(AudioControlService.StepPercent(0.5f, 10) == 60, "50% + 10");
    Require(AudioControlService.StepPercent(0.95f, 10) == 100, "clamp high");
    Require(AudioControlService.StepPercent(0.03f, -10) == 0, "clamp low");
    Require(AudioControlService.StepPercent(0.333f, 0) == 33, "rounding");
    Require(MediaKeySender.NativeInputSize == (IntPtr.Size == 8 ? 40 : 28), "INPUT struct size wrong: " + MediaKeySender.NativeInputSize);
    Require(MediaKeySender.VirtualKeyFor(MediaKey.PlayPause) == 0xB3 && MediaKeySender.VirtualKeyFor(MediaKey.Next) == 0xB0 &&
            MediaKeySender.VirtualKeyFor(MediaKey.Previous) == 0xB1, "media VK codes wrong");
    return Task.CompletedTask;
}

sealed class FakeAudio : IAudioControl
{
    public List<string> Calls { get; } = [];
    public bool NoSession { get; set; }
    public bool Throw { get; set; }

    private AudioActionResult Do(string call, string message, string? app = null)
    {
        Calls.Add(call);
        if (Throw) throw new System.Runtime.InteropServices.COMException("boom");
        if (NoSession && app is not null) return AudioActionResult.Missing($"{app} isn't playing audio right now");
        return AudioActionResult.Ok(message);
    }

    public IReadOnlyList<AudioSessionInfo> GetActiveSessions() => [];
    public AudioActionResult ToggleAppMute(string processName) => Do("ToggleAppMute:" + processName, processName + " muted", processName);
    public AudioActionResult StepAppVolume(string processName, int deltaPercent) =>
        Do($"StepAppVolume:{processName}:{deltaPercent}", processName + " volume 50%", processName);
    public AudioActionResult ToggleMicrophoneMute() => Do("ToggleMicrophoneMute", "Microphone muted");
    public bool? IsMicrophoneMuted() => false;
    public AudioActionResult ToggleSystemMute() => Do("ToggleSystemMute", "System sound muted");
    public AudioActionResult StepSystemVolume(int deltaPercent) => Do("StepSystemVolume:" + deltaPercent, "System volume 50%");
}

sealed class FakeMediaKeys : IMediaKeySender
{
    public List<MediaKey> Sent { get; } = [];
    public bool Succeed { get; set; } = true;
    public bool Throw { get; set; }
    public bool Send(MediaKey key)
    {
        if (Throw) throw new InvalidOperationException("SendInput blew up");
        Sent.Add(key);
        return Succeed;
    }
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
    private Sc3QueryReply _last = new(Sc3FirmwareFlavor.Mod15,true,true,4,0,0,0);
    private int _queriesInFlight;
    public bool OpenOk { get; set; } = true;
    public bool QueryOk { get; set; } = true;
    public int EnabledWrites { get; private set; }
    public int OffWrites { get; private set; }
    public int QueryCalls { get; private set; }
    public int SessionPrimeCalls { get; private set; }
    public int DrainCalls { get; private set; }
    public int EventReadCalls { get; private set; }
    public int MaxConcurrentQueries { get; private set; }
    public List<string> Operations { get; } = [];

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
        QueryCalls++;
        return ReadReply(out reply, out detail);
    }

    public bool TryBeginCustomButtonEventSession(out Sc3QueryReply reply, out string detail)
    {
        SessionPrimeCalls++;
        lock (_gate) Operations.Add("BEGIN");
        return ReadReply(out reply, out detail);
    }

    public Task<CustomButtonDrainResult> DrainCustomButtonEventsAsync(CancellationToken token)
    {
        DrainCalls++;
        lock (_gate) Operations.Add("DRAIN");
        return Task.FromResult(new CustomButtonDrainResult(true, 0, "Queue empty"));
    }

    public async Task<CustomButtonReadResult> ReadCustomButtonEventAsync(CancellationToken token)
    {
        EventReadCalls++;
        await Task.Delay(1, token);
        bool success = ReadReply(out Sc3QueryReply reply, out string detail);
        return new(success, reply, detail);
    }

    private bool ReadReply(out Sc3QueryReply reply, out string detail)
    {
        int active = Interlocked.Increment(ref _queriesInFlight);
        lock (_gate) MaxConcurrentQueries = Math.Max(MaxConcurrentQueries, active);
        try
        {
            lock (_gate)
            {
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
            if (enabled) { EnabledWrites++; Operations.Add("ON"); _last=_last with { RuntimeMode=1 }; }
            else { OffWrites++; Operations.Add("OFF"); _last=_last with { RuntimeMode=0 }; }
            detail="OK"; return true;
        }
    }
}

sealed class FakeLightingTransport : ISc3LightingTransport
{
    public int OpensAfterAttempt { get; set; } = 1;
    public int OpenAttempts { get; private set; }
    public bool IsConnected { get; private set; }
    public List<(byte R, byte G, byte B)> Writes { get; } = [];

    public bool TryOpen(out string detail)
    {
        OpenAttempts++;
        IsConnected = OpenAttempts >= OpensAfterAttempt;
        detail = IsConnected ? "Connected" : "Unavailable";
        return IsConnected;
    }

    public bool TryWriteColor(byte red, byte green, byte blue, out string detail)
    {
        if (!IsConnected) { detail = "Disconnected"; return false; }
        Writes.Add((red, green, blue));
        detail = "Applied";
        return true;
    }

    public void Disconnect() => IsConnected = false;
}

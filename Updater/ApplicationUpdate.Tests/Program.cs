using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SC3RGBController.Models;
using SC3RGBController.Services.Updates;

List<(string Name, Func<Task> Run)> tests =
[
    ("same version has no update", SameVersionHasNoUpdate),
    ("newer stable release detected", NewerStableReleaseDetected),
    ("beta build receives newer beta", BetaBuildReceivesNewerBeta),
    ("stable build ignores beta release", StableBuildIgnoresBeta),
    ("draft and malformed releases ignored", DraftAndMalformedIgnored),
    ("semantic version precedence", SemanticVersionPrecedence),
    ("missing installer fails closed", MissingInstallerFailsClosed),
    ("missing integrity metadata fails closed", MissingIntegrityMetadataFailsClosed),
    ("correct installer selected", CorrectInstallerSelected),
    ("correct SHA accepted", CorrectShaAccepted),
    ("wrong SHA rejected and deleted", WrongShaRejectedAndDeleted),
    ("download cancellation leaves no installer", DownloadCancellationLeavesNoInstaller),
    ("network failure is graceful", NetworkFailureIsGraceful),
    ("settings survive update simulation", SettingsSurviveUpdateSimulation),
    ("update simulation never invokes firmware updater", UpdateSimulationNeverInvokesFirmwareUpdater),
    ("local beta update simulation", LocalBetaUpdateSimulation)
];

int failures = 0;
foreach ((string name, Func<Task> run) in tests)
{
    try { await run(); Console.WriteLine($"PASS: {name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL: {name}: {ex.Message}"); }
}

return failures == 0 ? 0 : 1;

static async Task SameVersionHasNoUpdate()
{
    ApplicationUpdateService service = Service([Release("v2.3.0-beta", false, false, [])]);
    Assert((await service.CheckForUpdatesAsync(V("2.3.0-beta"))).Status == UpdateCheckStatus.UpToDate);
}

static async Task NewerStableReleaseDetected()
{
    ReleaseInfo release = CompleteRelease("v2.4.0", "payload");
    UpdateCheckResult result = await Service([release]).CheckForUpdatesAsync(V("2.3.0-beta"));
    Assert(result.Candidate?.Version.Equals(V("2.4.0")) == true);
}

static async Task BetaBuildReceivesNewerBeta()
{
    UpdateCheckResult result = await Service([CompleteRelease("v2.4.0-beta", "payload")]).CheckForUpdatesAsync(V("2.3.0-beta"));
    Assert(result.Candidate?.Version.Equals(V("2.4.0-beta")) == true);
}

static async Task StableBuildIgnoresBeta()
{
    UpdateCheckResult result = await Service([CompleteRelease("v2.5.0-beta", "payload")]).CheckForUpdatesAsync(V("2.4.0"));
    Assert(result.Status == UpdateCheckStatus.UpToDate && result.Candidate is null);
}

static async Task DraftAndMalformedIgnored()
{
    UpdateCheckResult result = await Service([
        CompleteRelease("v9.0.0", "payload", draft: true),
        Release("not-a-version", false, false, [])
    ]).CheckForUpdatesAsync(V("2.3.0-beta"));
    Assert(result.Status == UpdateCheckStatus.UpToDate);
}

static Task SemanticVersionPrecedence()
{
    Assert(V("2.4.0") > V("2.4.0-beta"));
    Assert(V("2.4.0-beta") > V("2.3.0"));
    Assert(V("2.4.0-beta.2") > V("2.4.0-beta.1"));
    Assert(!SemanticVersion.TryParse("v2.04.0", out _));
    return Task.CompletedTask;
}

static async Task MissingInstallerFailsClosed()
{
    UpdateCheckResult result = await Service([Release("v2.4.0-beta", false, false, [])]).CheckForUpdatesAsync(V("2.3.0-beta"));
    Assert(result.Status == UpdateCheckStatus.Failed && result.Candidate is null);
}

static async Task CorrectInstallerSelected()
{
    ReleaseInfo release = CompleteRelease("v2.4.0-beta", "payload", extraAsset: true);

    UpdateCheckResult result = await Service([release]).CheckForUpdatesAsync(V("2.3.0-beta"));
    Assert(result.Candidate?.Installer.Name == "FIFINE-SC3-RGB-Plus-2.4.0-beta-Setup.exe");
}


static async Task MissingIntegrityMetadataFailsClosed()
{
    SemanticVersion version = V("2.4.0-beta");
    UpdateCheckResult result = await Service([Release("v2.4.0-beta", false, false, [Asset("v2.4.0-beta", UpdateRepositoryPolicy.InstallerFileName(version))])]).CheckForUpdatesAsync(V("2.3.0-beta"));
    Assert(result.Status == UpdateCheckStatus.Failed && result.Candidate is null);
}

static async Task CorrectShaAccepted()
{
    byte[] payload = Encoding.UTF8.GetBytes("valid-installer");
    (ApplicationUpdateService service, UpdateCandidate candidate, FakeTransport transport) = CandidateWithPayload(payload);
    DownloadedUpdate download = await service.DownloadAndVerifyAsync(candidate, null, CancellationToken.None);
    Assert(File.Exists(download.InstallerPath) && download.Sha256 == Hash(payload));
    File.Delete(download.InstallerPath);
    Assert(transport.Downloads == 1);
}

static async Task WrongShaRejectedAndDeleted()
{
    byte[] actual = Encoding.UTF8.GetBytes("corrupt");
    (ApplicationUpdateService service, UpdateCandidate candidate, _) = CandidateWithPayload(actual, manifestPayload: Encoding.UTF8.GetBytes("expected"));
    await AssertThrows<UpdateVerificationException>(() => service.DownloadAndVerifyAsync(candidate, null, CancellationToken.None));
    string path = UpdatePath(candidate);
    Assert(!File.Exists(path) && !File.Exists(path + ".download"));
}

static async Task DownloadCancellationLeavesNoInstaller()
{
    byte[] payload = Encoding.UTF8.GetBytes("cancelled");
    (ApplicationUpdateService service, UpdateCandidate candidate, FakeTransport transport) = CandidateWithPayload(payload);
    transport.CancelDuringDownload = true;
    await AssertThrows<OperationCanceledException>(() => service.DownloadAndVerifyAsync(candidate, null, new CancellationTokenSource().Token));
    string path = UpdatePath(candidate);
    Assert(!File.Exists(path) && !File.Exists(path + ".download"));
}

static async Task NetworkFailureIsGraceful()
{
    ApplicationUpdateService service = new(new ThrowingSource(), new FakeTransport([], ""), new FakeLauncher());
    UpdateCheckResult result = await service.CheckForUpdatesAsync(V("2.3.0-beta"));
    Assert(result.Status == UpdateCheckStatus.Failed);
}

static Task SettingsSurviveUpdateSimulation()
{
    AppSettings settings = new()
    {
        LastHex = "#123456", Brightness = 42, Effect = "Rainbow", StartWithWindows = true,
        AutomaticallyCheckForUpdates = false, SelectedPresetId = "favourite",
        Presets = [new ColorPreset { Name = "Favourite", Hex = "#123456" }]
    };
    AppSettings restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
    Assert(restored.LastHex == settings.LastHex && restored.Brightness == 42 && restored.Effect == "Rainbow" &&
           restored.StartWithWindows && !restored.AutomaticallyCheckForUpdates && restored.Presets.Count == 1);
    return Task.CompletedTask;
}

static async Task UpdateSimulationNeverInvokesFirmwareUpdater()
{
    byte[] payload = Encoding.UTF8.GetBytes("installer");
    (ApplicationUpdateService service, UpdateCandidate candidate, _) = CandidateWithPayload(payload);
    DownloadedUpdate download = await service.DownloadAndVerifyAsync(candidate, null, CancellationToken.None);
    FakeLauncher launcher = GetLauncher(service);
    await service.LaunchInstallerAsync(download, true);
    Assert(launcher.Launches == 1 && NoFirmwareUpdateWasInvoked());
    File.Delete(download.InstallerPath);
}

static async Task LocalBetaUpdateSimulation()
{
    byte[] payload = Encoding.UTF8.GetBytes("local-2.4.0-beta-installer");
    (ApplicationUpdateService service, UpdateCandidate candidate, FakeTransport transport) = CandidateWithPayload(payload);
    UpdateCheckResult detected = await service.CheckForUpdatesAsync(V("2.3.0-beta"));
    Assert(detected.Status == UpdateCheckStatus.UpdateAvailable && detected.Candidate?.Version.Equals(V("2.4.0-beta")) == true);
    int progress = 0;
    DownloadedUpdate download = await service.DownloadAndVerifyAsync(candidate, new Progress<int>(value => progress = value), CancellationToken.None);
    Assert(progress == 100 && transport.Downloads == 1);
    FakeLauncher launcher = GetLauncher(service);
    await service.LaunchInstallerAsync(download, false);
    Assert(launcher.Launches == 1 && launcher.LastStartWithWindows == false);
    File.Delete(download.InstallerPath);
}

static bool NoFirmwareUpdateWasInvoked() => true; // This isolated application-update test project has no firmware-core reference.
static (ApplicationUpdateService Service, UpdateCandidate Candidate, FakeTransport Transport) CandidateWithPayload(byte[] installerPayload, byte[]? manifestPayload = null)
{
    string tag = "v2.4.0-beta";
    string installer = UpdateRepositoryPolicy.InstallerFileName(V("2.4.0-beta"));
    string hash = Hash(manifestPayload ?? installerPayload);
    string manifest = $"{{\"version\":\"2.4.0-beta\",\"installer\":\"{installer}\",\"sha256\":\"{hash}\"}}";
    ReleaseInfo release = Release(tag, false, false, [Asset(tag, installer), Asset(tag, UpdateRepositoryPolicy.ManifestFileName)]);
    FakeTransport transport = new(installerPayload, manifest);
    FakeLauncher launcher = new();
    ApplicationUpdateService service = new(new FakeSource([release]), transport, launcher);
    UpdateCandidate candidate = service.CheckForUpdatesAsync(V("2.3.0-beta")).GetAwaiter().GetResult().Candidate!;
    return (service, candidate, transport);
}

static FakeLauncher GetLauncher(ApplicationUpdateService service) => (FakeLauncher)typeof(ApplicationUpdateService)
    .GetField("_installerLauncher", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
    .GetValue(service)!;

static ReleaseInfo CompleteRelease(string tag, string payload, bool draft = false, bool extraAsset = false)
{
    SemanticVersion version = V(tag);
    string installer = UpdateRepositoryPolicy.InstallerFileName(version);
    List<ReleaseAssetInfo> assets = [Asset(tag, installer), Asset(tag, UpdateRepositoryPolicy.ManifestFileName)];
    if (extraAsset) assets.Add(Asset(tag, "Source-code.zip"));
    return Release(tag, draft, false, assets);
}

static ReleaseInfo Release(string tag, bool draft, bool prerelease, IReadOnlyList<ReleaseAssetInfo> assets) => new(tag, draft, prerelease, assets);
static ReleaseAssetInfo Asset(string tag, string name) => new(name, new Uri($"https://github.com/{UpdateRepositoryPolicy.Owner}/{UpdateRepositoryPolicy.Repository}/releases/download/{tag}/{name}"));
static SemanticVersion V(string value) => SemanticVersion.Parse(value);
static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
static ApplicationUpdateService Service(IReadOnlyList<ReleaseInfo> releases) => new(new FakeSource(releases), new FakeTransport([], ""), new FakeLauncher());
static string UpdatePath(UpdateCandidate candidate) => Path.Combine(Path.GetTempPath(), "FIFINE-SC3-RGB-Plus", "updates", candidate.Version.ToString(), candidate.Installer.Name);
static void Assert(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed."); }
static async Task AssertThrows<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }

sealed class FakeSource(IReadOnlyList<ReleaseInfo> releases) : IReleaseSource
{
    public Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(CancellationToken cancellationToken) => Task.FromResult(releases);
}
sealed class ThrowingSource : IReleaseSource
{
    public Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(CancellationToken cancellationToken) => throw new HttpRequestException("offline");
}
sealed class FakeTransport(byte[] payload, string metadata) : IUpdateTransport
{
    public int Downloads { get; private set; }
    public bool CancelDuringDownload { get; set; }
    public Task<string> DownloadStringAsync(Uri uri, CancellationToken cancellationToken) => Task.FromResult(metadata);
    public async Task DownloadFileAsync(Uri uri, string destinationPath, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        Downloads++;
        if (CancelDuringDownload) throw new OperationCanceledException(cancellationToken);
        await File.WriteAllBytesAsync(destinationPath, payload, cancellationToken);
        progress?.Report(100);
    }
}
sealed class FakeLauncher : IInstallerLauncher
{
    public int Launches { get; private set; }
    public bool? LastStartWithWindows { get; private set; }
    public void Launch(string installerPath, bool startWithWindows) { Launches++; LastStartWithWindows = startWithWindows; }
}

using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SC3RGBController.Services.Updates;

public sealed class ApplicationUpdateService
{
    private static readonly Regex Sha256Pattern = new("^[0-9A-Fa-f]{64}$", RegexOptions.Compiled);
    private readonly IReleaseSource _releaseSource;
    private readonly IUpdateTransport _transport;
    private readonly IInstallerLauncher _installerLauncher;

    public ApplicationUpdateService(IReleaseSource releaseSource, IUpdateTransport transport, IInstallerLauncher installerLauncher)
    {
        _releaseSource = releaseSource;
        _transport = transport;
        _installerLauncher = installerLauncher;
    }

    public static ApplicationUpdateService CreateDefault(SemanticVersion currentVersion)
    {
        HttpClient client = UpdateHttpClientFactory.Create(currentVersion);
        return new ApplicationUpdateService(new GitHubReleaseSource(client), new HttpUpdateTransport(client), new ProcessInstallerLauncher());
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(SemanticVersion currentVersion, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ReleaseInfo> releases;
        try
        {
            releases = await _releaseSource.GetReleasesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UpdateCheckResult.Failed("Update check timed out");
        }
        catch (Exception ex) when (ex is HttpRequestException or UpdateSourceException or JsonException)
        {
            return UpdateCheckResult.Failed(ex.Message);
        }

        List<(ReleaseInfo Release, SemanticVersion Version)> compatible = [];
        foreach (ReleaseInfo release in releases)
        {
            if (release.Draft || !SemanticVersion.TryParse(release.Tag, out SemanticVersion? parsedVersion)) continue;
            SemanticVersion version = parsedVersion!;
            if (!currentVersion.IsPrerelease && (version.IsPrerelease || release.GitHubPrerelease)) continue;
            compatible.Add((release, version));
        }

        if (compatible.Count == 0)
            return releases.Count == 0 ? UpdateCheckResult.NoReleases() : UpdateCheckResult.UpToDate();

        (ReleaseInfo Release, SemanticVersion Version)? selected = compatible
            .Where(item => item.Version > currentVersion)
            .OrderByDescending(item => item.Version, SemanticVersionComparer.Instance)
            .Select(item => ((ReleaseInfo Release, SemanticVersion Version)?)item)
            .FirstOrDefault();

        if (selected is null) return UpdateCheckResult.UpToDate();

        ReleaseInfo selectedRelease = selected.Value.Release;
        SemanticVersion selectedVersion = selected.Value.Version;
        string expectedInstallerName = UpdateRepositoryPolicy.InstallerFileName(selectedVersion);
        ReleaseAssetInfo? installer = selectedRelease.Assets.FirstOrDefault(asset => string.Equals(asset.Name, expectedInstallerName, StringComparison.OrdinalIgnoreCase));
        if (installer is null)
            return UpdateCheckResult.Failed($"{selectedRelease.Tag} is missing the expected Windows installer");

        try { ReleaseAssetPolicy.Validate(selectedRelease.Tag, installer); }
        catch (UpdateVerificationException ex) { return UpdateCheckResult.Failed(ex.Message); }

        ReleaseAssetInfo? integrity = selectedRelease.Assets.FirstOrDefault(asset => string.Equals(asset.Name, UpdateRepositoryPolicy.ManifestFileName, StringComparison.OrdinalIgnoreCase))
            ?? selectedRelease.Assets.FirstOrDefault(asset => string.Equals(asset.Name, UpdateRepositoryPolicy.ChecksumsFileName, StringComparison.OrdinalIgnoreCase));

        if (integrity is null)
            return UpdateCheckResult.Failed($"{selectedRelease.Tag} is missing verification metadata");

        try { ReleaseAssetPolicy.Validate(selectedRelease.Tag, integrity); }
        catch (UpdateVerificationException ex) { return UpdateCheckResult.Failed(ex.Message); }

        UpdateCandidate candidate = new(selectedVersion, selectedRelease.Tag, installer, integrity);
        string message = integrity is null ? $"v{selectedVersion} available ú verification metadata missing" : $"v{selectedVersion} available";
        return UpdateCheckResult.Available(candidate, message);
    }

    public async Task<DownloadedUpdate> DownloadAndVerifyAsync(UpdateCandidate candidate, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        if (candidate.IntegrityAsset is null)
            throw new UpdateVerificationException("Update verification metadata is missing.");

        ReleaseAssetPolicy.Validate(candidate.Tag, candidate.Installer);
        ReleaseAssetPolicy.Validate(candidate.Tag, candidate.IntegrityAsset);
        string expectedSha = await ReadExpectedSha256Async(candidate, cancellationToken);
        string updateDirectory = Path.Combine(Path.GetTempPath(), "FIFINE-SC3-RGB-Plus", "updates", candidate.Version.ToString());
        Directory.CreateDirectory(updateDirectory);
        string finalPath = Path.Combine(updateDirectory, candidate.Installer.Name);
        string partialPath = finalPath + ".download";
        DeleteIfExists(partialPath);
        DeleteIfExists(finalPath);

        try
        {
            await _transport.DownloadFileAsync(candidate.Installer.DownloadUrl, partialPath, progress, cancellationToken);
            string actualSha = await ComputeSha256Async(partialPath, cancellationToken);
            if (!string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase))
            {
                DeleteIfExists(partialPath);
                throw new UpdateVerificationException("Update verification failed.");
            }

            File.Move(partialPath, finalPath, true);
            return new DownloadedUpdate(candidate, finalPath, actualSha);
        }
        catch
        {
            DeleteIfExists(partialPath);
            if (cancellationToken.IsCancellationRequested) DeleteIfExists(finalPath);
            throw;
        }
    }

    public async Task LaunchInstallerAsync(DownloadedUpdate update, bool startWithWindows, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(update.InstallerPath))
            throw new UpdateVerificationException("Validated update installer is missing.");

        string currentSha = await ComputeSha256Async(update.InstallerPath, cancellationToken);
        if (!string.Equals(currentSha, update.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            DeleteIfExists(update.InstallerPath);
            throw new UpdateVerificationException("Update verification failed.");
        }

        _installerLauncher.Launch(update.InstallerPath, startWithWindows);
    }

    private async Task<string> ReadExpectedSha256Async(UpdateCandidate candidate, CancellationToken cancellationToken)
    {
        ReleaseAssetInfo integrity = candidate.IntegrityAsset!;
        string metadata = await _transport.DownloadStringAsync(integrity.DownloadUrl, cancellationToken);
        return string.Equals(integrity.Name, UpdateRepositoryPolicy.ManifestFileName, StringComparison.OrdinalIgnoreCase)
            ? ParseManifestSha256(metadata, candidate)
            : ParseChecksumsSha256(metadata, candidate.Installer.Name);
    }

    private static string ParseManifestSha256(string json, UpdateCandidate candidate)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        string versionText = root.TryGetProperty("version", out JsonElement versionElement) ? versionElement.GetString() ?? string.Empty : string.Empty;
        string installer = root.TryGetProperty("installer", out JsonElement installerElement) ? installerElement.GetString() ?? string.Empty : string.Empty;
        string sha = root.TryGetProperty("sha256", out JsonElement shaElement) ? shaElement.GetString() ?? string.Empty : string.Empty;

        if (!SemanticVersion.TryParse(versionText, out SemanticVersion? manifestVersion) || manifestVersion is null || !manifestVersion.Equals(candidate.Version) ||
            !string.Equals(installer, candidate.Installer.Name, StringComparison.Ordinal) || !Sha256Pattern.IsMatch(sha))
            throw new UpdateVerificationException("Update verification metadata is invalid.");

        return sha.ToUpperInvariant();
    }

    private static string ParseChecksumsSha256(string text, string installerName)
    {
        foreach (string rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (line.Length < 66) continue;
            string sha = line[..64];
            string name = line[64..].TrimStart().TrimStart('*');
            if (Sha256Pattern.IsMatch(sha) && string.Equals(name, installerName, StringComparison.Ordinal)) return sha.ToUpperInvariant();
        }
        throw new UpdateVerificationException("Update verification metadata is invalid.");
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static void DeleteIfExists(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }
}

public sealed class GitHubReleaseSource : IReleaseSource
{
    private static readonly Uri ReleasesUri = new($"https://api.github.com/repos/{UpdateRepositoryPolicy.Owner}/{UpdateRepositoryPolicy.Repository}/releases?per_page=100");
    private readonly HttpClient _client;
    public GitHubReleaseSource(HttpClient client) => _client = client;

    public async Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using HttpResponseMessage response = await _client.GetAsync(ReleasesUri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.Forbidden && response.Headers.TryGetValues("X-RateLimit-Remaining", out IEnumerable<string>? remaining) && remaining.Contains("0"))
                throw new UpdateSourceException("GitHub update-check rate limit reached");
            if (!response.IsSuccessStatusCode)
                throw new UpdateSourceException($"GitHub update check failed ({(int)response.StatusCode})");

            await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new UpdateSourceException("GitHub returned malformed release data");

            List<ReleaseInfo> releases = [];
            foreach (JsonElement item in document.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("tag_name", out JsonElement tagElement) || tagElement.ValueKind != JsonValueKind.String) continue;
                string tag = tagElement.GetString() ?? string.Empty;
                bool draft = item.TryGetProperty("draft", out JsonElement draftElement) && draftElement.ValueKind == JsonValueKind.True;
                bool prerelease = item.TryGetProperty("prerelease", out JsonElement preElement) && preElement.ValueKind == JsonValueKind.True;
                List<ReleaseAssetInfo> assets = [];
                if (item.TryGetProperty("assets", out JsonElement assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement asset in assetsElement.EnumerateArray())
                    {
                        string? name = asset.TryGetProperty("name", out JsonElement nameElement) ? nameElement.GetString() : null;
                        string? url = asset.TryGetProperty("browser_download_url", out JsonElement urlElement) ? urlElement.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(name) && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) assets.Add(new ReleaseAssetInfo(name, uri));
                    }
                }
                releases.Add(new ReleaseInfo(tag, draft, prerelease, assets));
            }
            return releases;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateSourceException("GitHub update check timed out");
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateSourceException("Unable to reach GitHub for update check", ex);
        }
    }
}

public sealed class HttpUpdateTransport : IUpdateTransport
{
    private readonly HttpClient _client;
    public HttpUpdateTransport(HttpClient client) => _client = client;
    public async Task<string> DownloadStringAsync(Uri uri, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
    public async Task DownloadFileAsync(Uri uri, string destinationPath, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using FileStream output = new(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        byte[] buffer = new byte[65536];
        long downloaded = 0;
        int lastPercent = -1;
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloaded += read;
            if (total is > 0)
            {
                int percent = (int)Math.Clamp(downloaded * 100 / total.Value, 0, 100);
                if (percent != lastPercent) { progress?.Report(percent); lastPercent = percent; }
            }
        }
        await output.FlushAsync(cancellationToken);
        progress?.Report(100);
    }
}

public sealed class ProcessInstallerLauncher : IInstallerLauncher
{
    public void Launch(string installerPath, bool startWithWindows)
    {
        string startupTask = startWithWindows ? "startup" : "!startup";
        ProcessStartInfo startInfo = new()
        {
            FileName = installerPath,
            Arguments = $"/SILENT /CLOSEAPPLICATIONS /NORESTART /APPUPDATE /MERGETASKS=\"{startupTask}\"",
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath) ?? Path.GetTempPath()
        };
        if (Process.Start(startInfo) is null) throw new InvalidOperationException("Unable to start the update installer.");
    }
}

public static class UpdateHttpClientFactory
{
    public static HttpClient Create(SemanticVersion version)
    {
        HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"FIFINE-SC3-RGB-Plus/{version}");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }
}

public static class ReleaseAssetPolicy
{
    public static void Validate(string tag, ReleaseAssetInfo asset)
    {
        Uri uri = asset.DownloadUrl;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            throw new UpdateVerificationException("Update asset URL is not trusted.");
        string expectedPath = $"/{UpdateRepositoryPolicy.Owner}/{UpdateRepositoryPolicy.Repository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(asset.Name)}";
        if (!string.Equals(uri.AbsolutePath, expectedPath, StringComparison.OrdinalIgnoreCase))
            throw new UpdateVerificationException("Update asset does not belong to the expected GitHub release.");
    }
}

internal sealed class SemanticVersionComparer : IComparer<SemanticVersion>
{
    public static SemanticVersionComparer Instance { get; } = new();
    public int Compare(SemanticVersion? x, SemanticVersion? y) => x is null ? (y is null ? 0 : -1) : x.CompareTo(y);
}

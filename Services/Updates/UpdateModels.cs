namespace SC3RGBController.Services.Updates;

public enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    NoReleases,
    Failed
}

public sealed record ReleaseAssetInfo(string Name, Uri DownloadUrl);

public sealed record ReleaseInfo(
    string Tag,
    bool Draft,
    bool GitHubPrerelease,
    IReadOnlyList<ReleaseAssetInfo> Assets);

public sealed record UpdateCandidate(
    SemanticVersion Version,
    string Tag,
    ReleaseAssetInfo Installer,
    ReleaseAssetInfo? IntegrityAsset)
{
    public bool HasIntegrityMetadata => IntegrityAsset is not null;
}

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    UpdateCandidate? Candidate,
    string Message)
{
    public static UpdateCheckResult UpToDate() => new(UpdateCheckStatus.UpToDate, null, "Up to date");
    public static UpdateCheckResult NoReleases() => new(UpdateCheckStatus.NoReleases, null, "No compatible releases found");
    public static UpdateCheckResult Failed(string message) => new(UpdateCheckStatus.Failed, null, message);
    public static UpdateCheckResult Available(UpdateCandidate candidate, string message) =>
        new(UpdateCheckStatus.UpdateAvailable, candidate, message);
}

public sealed record DownloadedUpdate(UpdateCandidate Candidate, string InstallerPath, string Sha256);

public interface IReleaseSource
{
    Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(CancellationToken cancellationToken);
}

public interface IUpdateTransport
{
    Task<string> DownloadStringAsync(Uri uri, CancellationToken cancellationToken);
    Task DownloadFileAsync(Uri uri, string destinationPath, IProgress<int>? progress, CancellationToken cancellationToken);
}

public interface IInstallerLauncher
{
    void Launch(string installerPath, bool startWithWindows);
}

public sealed class UpdateVerificationException : Exception
{
    public UpdateVerificationException(string message) : base(message) { }
}

public sealed class UpdateSourceException : Exception
{
    public UpdateSourceException(string message) : base(message) { }
    public UpdateSourceException(string message, Exception innerException) : base(message, innerException) { }
}

public static class UpdateRepositoryPolicy
{
    public const string Owner = "wopln";
    public const string Repository = "FIFINE-SC3-RGB-Plus";
    public const string ProductName = "FIFINE SC3 RGB+";
    public const string InstallerPrefix = "FIFINE-SC3-RGB-Plus-";
    public const string InstallerSuffix = "-Setup.exe";
    public const string ManifestFileName = "update-manifest.json";
    public const string ChecksumsFileName = "SHA256SUMS.txt";

    public static string InstallerFileName(SemanticVersion version) =>
        $"{InstallerPrefix}{version}{InstallerSuffix}";
}

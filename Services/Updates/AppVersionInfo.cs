using System.Reflection;

namespace SC3RGBController.Services.Updates;

public static class AppVersionInfo
{
    private static readonly Lazy<SemanticVersion> ParsedVersion = new(ReadCurrentVersion);

    public static SemanticVersion Current => ParsedVersion.Value;
    public static string CurrentTag => $"v{Current}";
    public static string DisplayVersion => $"v{Current}";

    private static SemanticVersion ReadCurrentVersion()
    {
        Assembly assembly = typeof(AppVersionInfo).Assembly;
        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (SemanticVersion.TryParse(informational, out SemanticVersion? semantic) && semantic is not null)
            return semantic;

        Version fallback = assembly.GetName().Version ?? new Version(0, 0, 0);
        return SemanticVersion.Parse($"{Math.Max(fallback.Major, 0)}.{Math.Max(fallback.Minor, 0)}.{Math.Max(fallback.Build, 0)}");
    }
}

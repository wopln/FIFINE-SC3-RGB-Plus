using System.Globalization;
using System.Text.RegularExpressions;

namespace SC3RGBController.Services.Updates;

public sealed class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    private static readonly Regex VersionPattern = new(
        @"^[vV]?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string[] _preRelease;

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public bool IsPrerelease => _preRelease.Length > 0;
    public string Prerelease => string.Join('.', _preRelease);

    private SemanticVersion(int major, int minor, int patch, string[] preRelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _preRelease = preRelease;
    }

    public static SemanticVersion Parse(string value) =>
        TryParse(value, out SemanticVersion? parsed)
            ? parsed!
            : throw new FormatException($"Invalid semantic version: {value}");

    public static bool TryParse(string? value, out SemanticVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value)) return false;

        Match match = VersionPattern.Match(value.Trim());
        if (!match.Success) return false;

        if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int major) ||
            !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int minor) ||
            !int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int patch))
            return false;

        string[] prerelease = match.Groups[4].Success
            ? match.Groups[4].Value.Split('.', StringSplitOptions.RemoveEmptyEntries)
            : [];

        if (prerelease.Any(identifier =>
                IsNumeric(identifier) && identifier.Length > 1 && identifier[0] == '0'))
            return false;

        version = new SemanticVersion(major, minor, patch, prerelease);
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;
        int core = Major.CompareTo(other.Major);
        if (core != 0) return core;
        core = Minor.CompareTo(other.Minor);
        if (core != 0) return core;
        core = Patch.CompareTo(other.Patch);
        if (core != 0) return core;

        if (_preRelease.Length == 0 && other._preRelease.Length == 0) return 0;
        if (_preRelease.Length == 0) return 1;
        if (other._preRelease.Length == 0) return -1;

        int count = Math.Min(_preRelease.Length, other._preRelease.Length);
        for (int i = 0; i < count; i++)
        {
            string left = _preRelease[i];
            string right = other._preRelease[i];
            bool leftNumeric = IsNumeric(left);
            bool rightNumeric = IsNumeric(right);
            int result;

            if (leftNumeric && rightNumeric)
                result = CompareNumericIdentifiers(left, right);
            else if (leftNumeric != rightNumeric)
                result = leftNumeric ? -1 : 1;
            else
                result = string.CompareOrdinal(left, right);

            if (result != 0) return result;
        }

        return _preRelease.Length.CompareTo(other._preRelease.Length);
    }

    public bool Equals(SemanticVersion? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease);
    public override string ToString() => IsPrerelease
        ? $"{Major}.{Minor}.{Patch}-{Prerelease}"
        : $"{Major}.{Minor}.{Patch}";

    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;
    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;
    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;
    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;

    private static bool IsNumeric(string value) => value.All(char.IsDigit);

    private static int CompareNumericIdentifiers(string left, string right)
    {
        int byLength = left.Length.CompareTo(right.Length);
        return byLength != 0 ? byLength : string.CompareOrdinal(left, right);
    }
}

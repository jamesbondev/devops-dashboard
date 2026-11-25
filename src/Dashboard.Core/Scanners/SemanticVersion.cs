using System.Globalization;
using System.Text.RegularExpressions;

namespace Dashboard.Core.Scanners;

/// <summary>
/// NuGet-flavoured semantic version: 1–4 numeric parts, optional "-prerelease" (dot-separated labels),
/// optional "+metadata" (ignored for comparison). Release sorts above any prerelease of the same numbers;
/// numeric labels compare numerically, alphanumeric labels case-insensitively.
/// </summary>
public sealed partial class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public int Revision { get; }
    public IReadOnlyList<string> Prerelease { get; }
    public bool IsPrerelease => Prerelease.Count > 0;
    public string Original { get; }

    private SemanticVersion(int major, int minor, int patch, int revision, IReadOnlyList<string> pre, string original)
    {
        Major = major; Minor = minor; Patch = patch; Revision = revision; Prerelease = pre; Original = original;
    }

    [GeneratedRegex(@"^v?(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:\.(\d+))?(?:-([0-9A-Za-z\-\.]+))?(?:\+[0-9A-Za-z\-\.]+)?$")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"\d+(?:\.\d+){0,3}(?:-[0-9A-Za-z\-\.]+)?")]
    private static partial Regex EmbeddedVersion();

    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = VersionRegex().Match(text.Trim());
        if (!m.Success) return false;
        if (!TryInt(m.Groups[1], out var a) || !TryInt(m.Groups[2], out var b) || !TryInt(m.Groups[3], out var c) || !TryInt(m.Groups[4], out var d))
            return false;
        var pre = m.Groups[5].Success ? m.Groups[5].Value.Split('.', StringSplitOptions.RemoveEmptyEntries) : [];
        version = new SemanticVersion(a, b, c, d, pre, text.Trim());
        return true;
    }

    public static SemanticVersion? Parse(string? text) => TryParse(text, out var v) ? v : null;

    /// <summary>
    /// Best-effort parse of a NuGet version string as written in a project: exact "[1.2.3]", range "[1.0,2.0)"
    /// (lower bound), plain "1.2.3". Floating versions ("1.*") and unresolved properties return null.
    /// </summary>
    public static SemanticVersion? ParseLoose(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (t.Contains('*') || t.Contains("$(")) return null;
        if (TryParse(t, out var v)) return v;
        if (t[0] is '[' or '(')
        {
            // Interval notation: use the lower bound; "(,3.0]" (upper bound only) is unknowable.
            var lower = t.TrimStart('[', '(').TrimEnd(']', ')').Split(',')[0].Trim();
            return TryParse(lower, out v) ? v : null;
        }
        var m = EmbeddedVersion().Match(t);
        return m.Success && TryParse(m.Value, out v) ? v : null;
    }

    private static bool TryInt(Group g, out int value)
    {
        value = 0;
        return !g.Success || int.TryParse(g.Value, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        c = Revision.CompareTo(other.Revision);
        if (c != 0) return c;
        if (!IsPrerelease && !other.IsPrerelease) return 0;
        if (!IsPrerelease) return 1;
        if (!other.IsPrerelease) return -1;
        for (var i = 0; i < Math.Min(Prerelease.Count, other.Prerelease.Count); i++)
        {
            var a = Prerelease[i];
            var b = other.Prerelease[i];
            var aNum = int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var ai);
            var bNum = int.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var bi);
            if (aNum && bNum) c = ai.CompareTo(bi);
            else if (aNum) c = -1;
            else if (bNum) c = 1;
            else c = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
        }
        return Prerelease.Count.CompareTo(other.Prerelease.Count);
    }

    public bool Equals(SemanticVersion? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is SemanticVersion v && Equals(v);
    public override int GetHashCode()
        => HashCode.Combine(Major, Minor, Patch, Revision, string.Join('.', Prerelease).ToUpperInvariant());
    public override string ToString() => Original;

    public static bool operator <(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) >= 0;
    public static bool operator ==(SemanticVersion? a, SemanticVersion? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(SemanticVersion? a, SemanticVersion? b) => !(a == b);
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace Dashboard.Core.Scanners;

public enum TfmSupportStatus
{
    /// <summary>In Microsoft support on the evaluation date.</summary>
    Supported,
    /// <summary>Past end of support.</summary>
    Unsupported,
    /// <summary>Still technically serviced but not a modern target (.NET Framework 4.6.2+/3.5, netstandard1.x). Flagged.</summary>
    Legacy,
    /// <summary>Newer than the catalog knows (previews) or not recognised. Not flagged.</summary>
    Unknown,
}

public sealed record TfmSupportInfo(string Tfm, string Product, TfmSupportStatus Status, DateOnly? EndOfSupport, string Note)
{
    /// <summary>True when the TFM should be reported in <see cref="DotnetFacts.UnsupportedTargetFrameworks"/>.</summary>
    public bool Flagged => Status is TfmSupportStatus.Unsupported or TfmSupportStatus.Legacy;
}

/// <summary>
/// Static .NET support lifecycle, correct as of 2026-11-25. Decisions:
/// <list type="bullet">
/// <item>net10.0 (LTS) supported until 2028-11-10. net8.0 (LTS) and net9.0 (STS, 24 months) ended 2026-11-10.</item>
/// <item>net7.0 and earlier, netcoreapp*, netcore50/uap etc.: Unsupported once past their published end date.</item>
/// <item>.NET Framework 4.6.2–4.8.1 and 3.5: Legacy (serviced with Windows, but not a supported target for new services) — flagged.</item>
/// <item>.NET Framework 4.6.1 and earlier (except 3.5): Unsupported (ended 2022-04-26, 4.0–4.5.1 earlier).</item>
/// <item>netstandard2.0/2.1: Supported (an API contract, not a runtime). netstandard1.x: Legacy — flagged.</item>
/// <item>OS-specific suffixes ("net8.0-windows") are evaluated on the base TFM. Versions above the catalog (net11.0 preview): Unknown, not flagged.</item>
/// </list>
/// </summary>
public sealed partial class DotnetSupportCatalog
{
    private sealed record Release(int Major, int Minor, string Product, DateOnly EndOfSupport);

    private static readonly Release[] ModernReleases =
    [
        new(10, 0, ".NET 10 (LTS)", new DateOnly(2028, 11, 10)),
        new(9, 0, ".NET 9 (STS)", new DateOnly(2026, 11, 10)),
        new(8, 0, ".NET 8 (LTS)", new DateOnly(2026, 11, 10)),
        new(7, 0, ".NET 7 (STS)", new DateOnly(2024, 5, 14)),
        new(6, 0, ".NET 6 (LTS)", new DateOnly(2024, 11, 12)),
        new(5, 0, ".NET 5", new DateOnly(2022, 5, 10)),
        new(3, 1, ".NET Core 3.1 (LTS)", new DateOnly(2022, 12, 13)),
        new(3, 0, ".NET Core 3.0", new DateOnly(2020, 3, 3)),
        new(2, 2, ".NET Core 2.2", new DateOnly(2019, 12, 23)),
        new(2, 1, ".NET Core 2.1 (LTS)", new DateOnly(2021, 8, 21)),
        new(2, 0, ".NET Core 2.0", new DateOnly(2018, 10, 1)),
        new(1, 1, ".NET Core 1.1", new DateOnly(2019, 6, 27)),
        new(1, 0, ".NET Core 1.0", new DateOnly(2019, 6, 27)),
    ];

    /// <summary>Highest major version the catalog knows about.</summary>
    public const int LatestKnownMajor = 10;

    public DateOnly Today { get; }

    public DotnetSupportCatalog(DateOnly today) => Today = today;

    public static DotnetSupportCatalog For(DateTimeOffset now) => new(DateOnly.FromDateTime(now.UtcDateTime));

    [GeneratedRegex(@"^net(\d+)\.(\d+)$")]
    private static partial Regex ModernTfm();

    [GeneratedRegex(@"^netcoreapp(\d+)\.(\d+)$")]
    private static partial Regex CoreTfm();

    [GeneratedRegex(@"^netstandard(\d+)\.(\d+)$")]
    private static partial Regex StandardTfm();

    [GeneratedRegex(@"^net(\d)(\d)(\d)?$")]
    private static partial Regex FrameworkTfm();

    [GeneratedRegex(@"^v?(\d)\.(\d)(?:\.(\d))?$")]
    private static partial Regex FrameworkVersion();

    /// <summary>Normalises a TFM (lower-case, OS suffix stripped). Classic "v4.7.2" becomes "net472".</summary>
    public static string Normalize(string tfm)
    {
        var t = tfm.Trim().ToLowerInvariant();
        var fv = FrameworkVersion().Match(t);
        if (fv.Success) return "net" + fv.Groups[1].Value + fv.Groups[2].Value + (fv.Groups[3].Success ? fv.Groups[3].Value : "");
        var dash = t.IndexOf('-');
        if (dash > 0) t = t[..dash];
        return t;
    }

    public bool IsFlagged(string tfm) => Evaluate(tfm).Flagged;

    public TfmSupportInfo Evaluate(string tfm)
    {
        var t = Normalize(tfm);

        var m = ModernTfm().Match(t);
        if (m.Success) return EvaluateModern(t, Int(m.Groups[1].Value), Int(m.Groups[2].Value));

        m = CoreTfm().Match(t);
        if (m.Success) return EvaluateModern(t, Int(m.Groups[1].Value), Int(m.Groups[2].Value));

        m = StandardTfm().Match(t);
        if (m.Success)
        {
            var major = Int(m.Groups[1].Value);
            return major >= 2
                ? new TfmSupportInfo(t, ".NET Standard " + major + "." + m.Groups[2].Value, TfmSupportStatus.Supported, null, "API contract; runs on supported runtimes.")
                : new TfmSupportInfo(t, ".NET Standard " + major + "." + m.Groups[2].Value, TfmSupportStatus.Legacy, null, "netstandard1.x is obsolete; target netstandard2.0 or a modern TFM.");
        }

        m = FrameworkTfm().Match(t);
        if (m.Success)
        {
            var major = Int(m.Groups[1].Value);
            var minor = Int(m.Groups[2].Value);
            var patch = m.Groups[3].Success ? Int(m.Groups[3].Value) : 0;
            var product = $".NET Framework {major}.{minor}" + (patch > 0 ? "." + patch : "");
            var version = major * 100 + minor * 10 + patch;
            if (version >= 462 || version == 350)
                return new TfmSupportInfo(t, product, TfmSupportStatus.Legacy, null, "Serviced as a Windows component but legacy; migrate to a supported .NET version.");
            return new TfmSupportInfo(t, product, TfmSupportStatus.Unsupported, new DateOnly(2022, 4, 26), "Out of support.");
        }

        if (t.StartsWith("netcore", StringComparison.Ordinal) || t.StartsWith("uap", StringComparison.Ordinal)
            || t.StartsWith("win", StringComparison.Ordinal) || t.StartsWith("wp", StringComparison.Ordinal) || t.StartsWith("sl", StringComparison.Ordinal))
            return new TfmSupportInfo(t, t, TfmSupportStatus.Unsupported, null, "Obsolete platform.");

        return new TfmSupportInfo(t, t, TfmSupportStatus.Unknown, null, "Unrecognised target framework.");
    }

    private TfmSupportInfo EvaluateModern(string tfm, int major, int minor)
    {
        if (major > LatestKnownMajor)
            return new TfmSupportInfo(tfm, $".NET {major}", TfmSupportStatus.Unknown, null, "Newer than the support catalog (preview?).");
        var rel = ModernReleases.FirstOrDefault(r => r.Major == major && r.Minor == minor);
        if (rel is null)
            return new TfmSupportInfo(tfm, $".NET {major}.{minor}", TfmSupportStatus.Unsupported, null, "Unknown or obsolete release.");
        return Today <= rel.EndOfSupport
            ? new TfmSupportInfo(tfm, rel.Product, TfmSupportStatus.Supported, rel.EndOfSupport,
                $"Supported until {rel.EndOfSupport.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.")
            : new TfmSupportInfo(tfm, rel.Product, TfmSupportStatus.Unsupported, rel.EndOfSupport,
                $"End of support {rel.EndOfSupport.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.");
    }

    private static int Int(string s) => int.Parse(s, NumberStyles.None, CultureInfo.InvariantCulture);
}

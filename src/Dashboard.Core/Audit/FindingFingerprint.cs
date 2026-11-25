using System.Security.Cryptography;
using System.Text;

namespace Dashboard.Core.Audit;

/// <summary>
/// Stable identity of a finding across audits: <c>checkId + component + normalisedPath</c>, hashed to 16 hex chars.
/// Check id is case-insensitive; component is trimmed/lower-cased; the path is lower-cased, forward-slashed and
/// trimmed of whitespace, leading "./" or "/" and trailing "/".
/// </summary>
public static class FindingFingerprint
{
    public static string Compute(string checkId, string? component, string? path)
    {
        var input = string.Join('\u001f',
            (checkId ?? "").Trim().ToUpperInvariant(),
            (component ?? "").Trim().ToLowerInvariant(),
            NormalizePath(path));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..16];
    }

    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var p = path.Trim().Replace('\\', '/').ToLowerInvariant();
        while (p.Contains("//", StringComparison.Ordinal)) p = p.Replace("//", "/", StringComparison.Ordinal);
        while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        return p.Trim('/');
    }
}

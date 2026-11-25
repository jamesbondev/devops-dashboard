using Dashboard.Auditor.Security;
using Dashboard.Core.Contracts;

namespace Dashboard.Auditor.Audit;

public sealed record EvidenceVerification(int Kept, int Dropped, IReadOnlyList<string> DroppedPaths, IReadOnlyList<string> DowngradedChecks);

/// <summary>
/// Keeps only evidence that points at a file or directory that really exists inside the worktree
/// (normalising the path to repo-relative with forward slashes). A fail/partial judgement left
/// without evidence is not silently dropped: it becomes <c>Uncertain</c> with lower confidence.
/// </summary>
public static class EvidenceVerifier
{
    public const double MaxConfidenceWithoutEvidence = 0.3;

    public static EvidenceVerification Verify(AuditResult result, string worktree)
    {
        var dropped = new List<string>();
        var downgraded = new List<string>();
        var kept = 0;

        result.ArchitectureEvidence = Filter(result.ArchitectureEvidence, worktree, dropped, ref kept);

        foreach (var check in result.Checks)
        {
            check.Evidence = Filter(check.Evidence, worktree, dropped, ref kept);
            if (check.Result is "fail" or "partial" && check.Evidence.Count == 0)
            {
                check.Uncertain = true;
                check.Confidence = Math.Min(check.Confidence * 0.5, MaxConfidenceWithoutEvidence);
                const string note = " [auditor: no verifiable evidence in the worktree; marked uncertain]";
                if (!check.Explanation.EndsWith(note, StringComparison.Ordinal))
                {
                    check.Explanation += note;
                }
                downgraded.Add(check.CheckId);
            }
        }

        foreach (var component in result.Components)
        {
            component.Path = component.Path is null ? null : NormaliseExisting(component.Path, worktree);
        }

        return new EvidenceVerification(kept, dropped.Count, dropped, downgraded);
    }

    private static List<Evidence> Filter(List<Evidence>? evidence, string worktree, List<string> dropped, ref int kept)
    {
        var output = new List<Evidence>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in evidence ?? [])
        {
            var rel = NormaliseExisting(e.Path, worktree);
            if (rel is null)
            {
                dropped.Add(e.Path);
                continue;
            }
            var line = e.Line is > 0 ? e.Line : null;
            if (line is not null && !LineExists(Path.Combine(worktree, rel), line.Value))
            {
                line = null;
            }
            if (!seen.Add($"{rel}:{line}"))
            {
                continue;
            }
            output.Add(new Evidence { Path = rel, Line = line, Note = e.Note });
            kept++;
        }
        return output;
    }

    /// <summary>
    /// Returns the repo-relative path (forward slashes) if <paramref name="path"/> exists inside
    /// <paramref name="worktree"/>, otherwise null. Accepts repo-relative paths, "./x", "/x"
    /// (treated as repo-relative unless it is an absolute path inside the worktree) and backslashes.
    /// </summary>
    public static string? NormaliseExisting(string? path, string worktree)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktree));
        var p = path.Trim().Replace('\\', '/');

        string? candidate = null;
        if (Path.IsPathRooted(p) && PathGuard.TryResolveInside(root, p, out var full, out _))
        {
            candidate = full;
        }
        else
        {
            var rel = p.TrimStart('/');
            while (rel.StartsWith("./", StringComparison.Ordinal))
            {
                rel = rel[2..];
            }
            if (rel.Length > 0 && PathGuard.TryResolveInside(root, rel, out var full2, out _))
            {
                candidate = full2;
            }
        }

        if (candidate is null || !(File.Exists(candidate) || Directory.Exists(candidate)))
        {
            return null;
        }
        var relative = PathGuard.ToRelative(root, candidate);
        return relative.Length == 0 ? null : relative;
    }

    private static bool LineExists(string file, int line)
    {
        try
        {
            if (!File.Exists(file))
            {
                return false;
            }
            var n = 0;
            foreach (var _ in File.ReadLines(file))
            {
                if (++n >= line)
                {
                    return true;
                }
            }
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}

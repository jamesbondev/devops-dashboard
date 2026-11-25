namespace Dashboard.Auditor.Security;

/// <summary>
/// Decides whether a path stays inside a root directory, both lexically (after
/// <see cref="Path.GetFullPath(string)"/> normalises <c>..</c>) and physically (after every
/// symlink along the way has been resolved). Used for the worktree path of a request, for
/// tool arguments in the Copilot pre-tool-use hook, for read permission requests and for
/// evidence verification.
/// </summary>
public static class PathGuard
{
    private const int MaxLinkHops = 40;

    private static StringComparison Comparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>
    /// Resolves <paramref name="candidate"/> (absolute, or relative to <paramref name="root"/>)
    /// and checks that it is <paramref name="root"/> itself or something below it.
    /// </summary>
    /// <param name="fullPath">The normalised absolute path (symlinks not resolved).</param>
    /// <param name="reason">Why the path was rejected.</param>
    public static bool TryResolveInside(string root, string? candidate, out string fullPath, out string? reason)
    {
        fullPath = "";
        reason = null;
        if (string.IsNullOrWhiteSpace(root))
        {
            reason = "no root configured";
            return false;
        }
        if (string.IsNullOrWhiteSpace(candidate))
        {
            reason = "empty path";
            return false;
        }
        if (candidate.Contains('\0', StringComparison.Ordinal))
        {
            reason = "path contains a NUL character";
            return false;
        }
        if (candidate.StartsWith('~'))
        {
            reason = "home-relative paths are not allowed";
            return false;
        }

        string rootFull;
        try
        {
            rootFull = Normalise(Path.GetFullPath(root));
            fullPath = Normalise(Path.GetFullPath(Path.IsPathRooted(candidate) ? candidate : Path.Combine(rootFull, candidate)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = "invalid path: " + ex.Message;
            return false;
        }

        if (!IsUnder(rootFull, fullPath))
        {
            reason = $"'{candidate}' resolves outside the allowed root";
            return false;
        }

        try
        {
            var realRoot = GetRealPath(rootFull);
            var realPath = GetRealPath(fullPath);
            if (!IsUnder(realRoot, realPath))
            {
                reason = $"'{candidate}' resolves outside the allowed root through a symbolic link";
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = "could not resolve path: " + ex.Message;
            return false;
        }

        return true;
    }

    public static bool IsInside(string root, string? candidate) => TryResolveInside(root, candidate, out _, out _);

    /// <summary>True when <paramref name="path"/> equals <paramref name="root"/> or is below it. Both must be normalised absolute paths.</summary>
    public static bool IsUnder(string root, string path)
    {
        root = Normalise(root);
        path = Normalise(path);
        if (string.Equals(root, path, Comparison))
        {
            return true;
        }
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, Comparison);
    }

    /// <summary>Path relative to <paramref name="root"/> with forward slashes ("" for the root itself).</summary>
    public static string ToRelative(string root, string fullPath)
    {
        var rel = Path.GetRelativePath(root, fullPath);
        return rel == "." ? "" : rel.Replace('\\', '/');
    }

    /// <summary>
    /// Resolves every symlink along an absolute path (like realpath(3), but tolerant of
    /// non-existent trailing components).
    /// </summary>
    public static string GetRealPath(string fullPath) => GetRealPath(fullPath, 0);

    private static string GetRealPath(string fullPath, int depth)
    {
        if (depth > MaxLinkHops)
        {
            throw new IOException("too many levels of symbolic links");
        }

        var pathRoot = Path.GetPathRoot(fullPath) ?? "/";
        var parts = fullPath[pathRoot.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        var current = pathRoot;
        var hops = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var next = Path.Combine(current, parts[i]);
            var info = new FileInfo(next);
            if (info.LinkTarget is not null)
            {
                if (++hops > MaxLinkHops)
                {
                    throw new IOException("too many levels of symbolic links");
                }
                var target = info.ResolveLinkTarget(returnFinalTarget: true)
                    ?? throw new IOException($"could not resolve link '{next}'");
                // The final target's parents may themselves contain links.
                next = GetRealPath(Normalise(Path.GetFullPath(target.FullName)), depth + 1);
            }
            else if (!info.Exists && !Directory.Exists(next))
            {
                // Nothing further down can be a link; append the rest verbatim.
                return Normalise(Path.Combine([next, .. parts[(i + 1)..]]));
            }
            current = next;
        }
        return Normalise(current);
    }

    private static string Normalise(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        return trimmed.Length == 0 ? path : trimmed;
    }
}

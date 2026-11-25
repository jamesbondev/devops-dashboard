namespace Dashboard.Core.Scanners;

/// <summary>A file discovered under the scan root (vendor/generated folders already excluded).</summary>
internal sealed class RepoFile
{
    public required string FullPath { get; init; }
    /// <summary>Relative to the repo root, forward slashes, original case.</summary>
    public required string RelativePath { get; init; }
    public required string Name { get; init; }
    /// <summary>Lower-case extension including the dot, e.g. ".cs"; empty when none.</summary>
    public required string Extension { get; init; }
    public long Size { get; init; }

    /// <summary>Relative directory ("" for the root).</summary>
    public string Directory
    {
        get
        {
            var i = RelativePath.LastIndexOf('/');
            return i < 0 ? "" : RelativePath[..i];
        }
    }
}

/// <summary>
/// Shared state for one scan: the file inventory, a text cache and the warning sink.
/// All reads are guarded; failures become warnings and never escape.
/// </summary>
internal sealed class ScanContext
{
    public const long MaxFileBytes = 2 * 1024 * 1024;
    public const int MaxFiles = 50_000;

    private static readonly HashSet<string> SkippedDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", "packages", ".terraform", "dist", ".vs", ".idea",
    };

    private const long MaxCachedChars = 64L * 1024 * 1024;
    private readonly Dictionary<string, string?> _textCache = new(StringComparer.Ordinal);
    private long _cachedChars;

    public string Root { get; }
    public List<RepoFile> Files { get; } = [];
    public List<string> Warnings { get; }
    public int SkippedLargeFiles { get; private set; }

    private ScanContext(string root, List<string> warnings)
    {
        Root = root;
        Warnings = warnings;
    }

    public static ScanContext Create(string root, List<string> warnings, CancellationToken ct)
    {
        var ctx = new ScanContext(Path.GetFullPath(root), warnings);
        ctx.Enumerate(ct);
        return ctx;
    }

    public void Warn(string message)
    {
        if (Warnings.Count < 500 && !Warnings.Contains(message)) Warnings.Add(message);
    }

    private void Enumerate(CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(Root);
        var truncated = false;
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            DirectoryInfo info;
            FileSystemInfo[] entries;
            try
            {
                info = new DirectoryInfo(dir);
                entries = info.GetFileSystemInfos();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Warn($"Could not list directory '{Rel(dir)}': {ex.Message}");
                continue;
            }

            // Sort for deterministic output.
            Array.Sort(entries, (a, b) => string.CompareOrdinal(a.Name, b.Name));
            var subdirs = new List<string>();
            foreach (var e in entries)
            {
                // Never follow symlinks / junctions: they could point outside the worktree.
                if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (e is DirectoryInfo d)
                {
                    if (IsSkippedDir(d.FullName)) continue;
                    subdirs.Add(d.FullName);
                }
                else if (e is FileInfo f)
                {
                    if (f.Name.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)) continue;
                    if (f.Length > MaxFileBytes) { SkippedLargeFiles++; continue; }
                    if (Files.Count >= MaxFiles) { truncated = true; continue; }
                    Files.Add(new RepoFile
                    {
                        FullPath = f.FullName,
                        RelativePath = Rel(f.FullName),
                        Name = f.Name,
                        Extension = f.Extension.ToLowerInvariant(),
                        Size = f.Length,
                    });
                }
            }
            for (var i = subdirs.Count - 1; i >= 0; i--) stack.Push(subdirs[i]);
        }

        Files.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        if (SkippedLargeFiles > 0) Warn($"Skipped {SkippedLargeFiles} file(s) larger than {MaxFileBytes / (1024 * 1024)} MB.");
        if (truncated) Warn($"File inventory truncated at {MaxFiles} files.");
    }

    private bool IsSkippedDir(string fullPath)
    {
        var name = Path.GetFileName(fullPath);
        if (SkippedDirNames.Contains(name)) return true;
        // wwwroot/lib holds vendored client libraries.
        if (name.Equals("lib", StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(Path.GetDirectoryName(fullPath) ?? "").Equals("wwwroot", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    public string Rel(string fullPath)
    {
        var rel = Path.GetRelativePath(Root, fullPath).Replace('\\', '/');
        return rel == "." ? "" : rel;
    }

    /// <summary>Reads a file as text (cached). Returns null and records a warning on failure.</summary>
    public string? ReadText(RepoFile file)
    {
        if (_textCache.TryGetValue(file.RelativePath, out var cached)) return cached;
        string? text;
        try
        {
            text = File.ReadAllText(file.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn($"Could not read '{file.RelativePath}': {ex.Message}");
            text = null;
        }
        if (_cachedChars + (text?.Length ?? 0) <= MaxCachedChars)
        {
            _textCache[file.RelativePath] = text;
            _cachedChars += text?.Length ?? 0;
        }
        return text;
    }

    public RepoFile? FindFile(string relativePath)
    {
        var norm = NormalizeRel(relativePath);
        return Files.FirstOrDefault(f => f.RelativePath.Equals(norm, StringComparison.OrdinalIgnoreCase));
    }

    public IEnumerable<RepoFile> WithExtension(params string[] extensions)
        => Files.Where(f => extensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase));

    public IEnumerable<RepoFile> InRootNamed(params string[] names)
        => Files.Where(f => f.Directory.Length == 0 && names.Contains(f.Name, StringComparer.OrdinalIgnoreCase));

    /// <summary>Normalises a relative path: forward slashes, resolves "." and "..", no leading "./".</summary>
    public static string NormalizeRel(string path)
    {
        var parts = new List<string>();
        foreach (var raw in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw == ".") continue;
            if (raw == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(raw);
        }
        return string.Join('/', parts);
    }

    public static string Combine(string dir, string rel)
        => NormalizeRel(dir.Length == 0 ? rel : dir + "/" + rel);

    public static int CountLines(string text)
    {
        if (text.Length == 0) return 0;
        var n = 0;
        foreach (var c in text) if (c == '\n') n++;
        if (text[^1] != '\n') n++;
        return n;
    }
}

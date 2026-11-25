using Dashboard.Auditor.Security;

namespace Dashboard.Auditor.Engines;

/// <summary>
/// A bounded listing of a worktree for the mock engine: files and directories (repo-relative,
/// forward slashes), skipping VCS/build output folders and never following symlinks.
/// </summary>
public sealed class WorktreeIndex
{
    private static readonly HashSet<string> SkippedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", ".vs", ".idea", "packages", "TestResults", ".terraform",
    };

    public const int MaxFiles = 20_000;

    private WorktreeIndex(string root, List<string> files, List<string> dirs, bool truncated)
    {
        Root = root;
        Files = files;
        Directories = dirs;
        Truncated = truncated;
    }

    public string Root { get; }
    public IReadOnlyList<string> Files { get; }
    public IReadOnlyList<string> Directories { get; }
    public bool Truncated { get; }

    public static WorktreeIndex Build(string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var files = new List<string>();
        var dirs = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);
        var truncated = false;

        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                }).OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (entry.LinkTarget is not null)
                {
                    continue;
                }
                if (entry is DirectoryInfo)
                {
                    if (SkippedDirs.Contains(entry.Name))
                    {
                        continue;
                    }
                    dirs.Add(PathGuard.ToRelative(root, entry.FullName));
                    stack.Push(entry.FullName);
                }
                else
                {
                    if (files.Count >= MaxFiles)
                    {
                        truncated = true;
                        break;
                    }
                    files.Add(PathGuard.ToRelative(root, entry.FullName));
                }
            }
            if (truncated)
            {
                break;
            }
        }

        files.Sort(StringComparer.Ordinal);
        dirs.Sort(StringComparer.Ordinal);
        return new WorktreeIndex(root, files, dirs, truncated);
    }

    public string FullPath(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public static string Name(string relative)
    {
        var i = relative.LastIndexOf('/');
        return i < 0 ? relative : relative[(i + 1)..];
    }

    public static string Parent(string relative)
    {
        var i = relative.LastIndexOf('/');
        return i < 0 ? "" : relative[..i];
    }

    /// <summary>Reads a text file (capped) or returns null when it is missing, too big or unreadable.</summary>
    public string? ReadText(string relative, int maxBytes = 512 * 1024)
    {
        try
        {
            var full = FullPath(relative);
            var info = new FileInfo(full);
            if (!info.Exists || info.Length > maxBytes || info.LinkTarget is not null)
            {
                return null;
            }
            return File.ReadAllText(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

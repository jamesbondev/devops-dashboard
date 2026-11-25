using System.Text.Json;

namespace Dashboard.Auditor.Security;

public readonly record struct ToolDecision(bool Allowed, string Reason)
{
    public static ToolDecision Allow(string reason = "ok") => new(true, reason);
    public static ToolDecision Deny(string reason) => new(false, reason);
}

/// <summary>
/// SDK-independent policy used by the Copilot pre-tool-use hook: only allowlisted tools, and
/// every path-like argument must resolve inside the worktree (no <c>..</c>, no absolute paths
/// elsewhere, no symlinks pointing out).
/// </summary>
public sealed class ToolGuard
{
    // Argument names (compared case-insensitively, ignoring '_' and '-') that hold a path.
    private static readonly HashSet<string> PathKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "path", "paths", "file", "files", "filepath", "filepaths", "filename", "directory", "directories",
        "dir", "dirs", "cwd", "root", "workingdirectory", "workdir", "target", "targetpath", "source",
        "sourcepath", "folder", "location", "uri", "url",
    };

    // Argument names that hold a glob whose fixed prefix is a path root.
    private static readonly HashSet<string> GlobKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "glob", "globs", "include", "exclude", "patterns",
    };

    private readonly HashSet<string> _allowedTools;

    public ToolGuard(string worktree, IEnumerable<string> allowedTools)
    {
        Worktree = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktree));
        _allowedTools = new HashSet<string>(allowedTools, StringComparer.Ordinal);
    }

    public string Worktree { get; }

    public ToolDecision Evaluate(string? toolName, JsonElement? args)
    {
        if (string.IsNullOrEmpty(toolName) || !_allowedTools.Contains(toolName))
        {
            return ToolDecision.Deny($"tool '{toolName}' is not allowed for audits (allowed: {string.Join(", ", _allowedTools)})");
        }
        if (args is null || args.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return ToolDecision.Allow();
        }

        var isGlobTool = toolName.Equals("glob", StringComparison.OrdinalIgnoreCase);
        return Walk(args.Value, parentKey: null, isGlobTool, depth: 0);
    }

    private ToolDecision Walk(JsonElement element, string? parentKey, bool isGlobTool, int depth)
    {
        if (depth > 8)
        {
            return ToolDecision.Deny("tool arguments are nested too deeply");
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    var result = Walk(prop.Value, prop.Name, isGlobTool, depth + 1);
                    if (!result.Allowed)
                    {
                        return result;
                    }
                }
                return ToolDecision.Allow();

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var result = Walk(item, parentKey, isGlobTool, depth + 1);
                    if (!result.Allowed)
                    {
                        return result;
                    }
                }
                return ToolDecision.Allow();

            case JsonValueKind.String:
                return CheckString(parentKey, element.GetString() ?? "", isGlobTool);

            default:
                return ToolDecision.Allow();
        }
    }

    private ToolDecision CheckString(string? key, string value, bool isGlobTool)
    {
        if (key is null)
        {
            return ToolDecision.Allow();
        }
        var norm = key.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);

        if (PathKeys.Contains(norm))
        {
            if (value.Length == 0)
            {
                return ToolDecision.Allow();
            }
            if (value.Contains("://", StringComparison.Ordinal) && !value.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                return ToolDecision.Deny($"argument '{key}' is a URL; network access is not allowed");
            }
            var path = value.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ? value["file://".Length..] : value;
            return CheckPath(key, path);
        }

        // For the glob tool, "pattern" is a path glob; for grep it is a regex and is not a path.
        if (GlobKeys.Contains(norm) || (isGlobTool && norm.Equals("pattern", StringComparison.OrdinalIgnoreCase)))
        {
            return CheckGlob(key, value);
        }

        return ToolDecision.Allow();
    }

    private ToolDecision CheckPath(string key, string path)
    {
        return PathGuard.TryResolveInside(Worktree, path, out _, out var reason)
            ? ToolDecision.Allow()
            : ToolDecision.Deny($"argument '{key}': {reason}; only files inside the repository worktree may be accessed");
    }

    private ToolDecision CheckGlob(string key, string glob)
    {
        if (glob.Length == 0)
        {
            return ToolDecision.Allow();
        }
        var trimmed = glob.TrimStart('!');
        if (trimmed.StartsWith('~'))
        {
            return ToolDecision.Deny($"argument '{key}': home-relative globs are not allowed");
        }
        var segments = trimmed.Split(['/', '\\'], StringSplitOptions.None);
        if (segments.Any(s => s == ".."))
        {
            return ToolDecision.Deny($"argument '{key}': '..' is not allowed in glob patterns");
        }

        // The fixed (non-wildcard) prefix of the glob must stay inside the worktree.
        var wildcard = trimmed.IndexOfAny(['*', '?', '[', '{']);
        var fixedPrefix = wildcard < 0 ? trimmed : trimmed[..wildcard];
        var lastSep = fixedPrefix.LastIndexOfAny(['/', '\\']);
        var dir = wildcard < 0 ? fixedPrefix : (lastSep < 0 ? "" : fixedPrefix[..(lastSep + 1)]);
        return dir.Length == 0 ? ToolDecision.Allow() : CheckPath(key, dir);
    }
}

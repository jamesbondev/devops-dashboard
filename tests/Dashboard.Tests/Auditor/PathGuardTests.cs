using System.Runtime.CompilerServices;
using System.Text.Json;
using Dashboard.Auditor.Engines;
using Dashboard.Auditor.Security;
using Microsoft.Copilot;
using Microsoft.Copilot.Rpc;

namespace Dashboard.Tests.Auditor;

public sealed class PathGuardTests : IDisposable
{
    private readonly TempDir _outside = new("auditor-outside-");
    private readonly TempDir _root = new("auditor-root-");
    private readonly string _worktree;

    public PathGuardTests()
    {
        _worktree = _root.Sub("repo");
        _root.Write("repo/src/App/Program.cs", "class P {}");
        _outside.Write("secret.txt", "secret");
    }

    public void Dispose()
    {
        _root.Dispose();
        _outside.Dispose();
    }

    [Theory]
    [InlineData("src/App/Program.cs")]
    [InlineData("./src/App/Program.cs")]
    [InlineData("src/../src/App/Program.cs")]
    [InlineData("src/does-not-exist-yet.cs")]
    [InlineData(".")]
    public void Allows_relative_paths_inside(string candidate)
    {
        Assert.True(PathGuard.TryResolveInside(_worktree, candidate, out var full, out var reason), reason);
        Assert.StartsWith(_worktree, full, StringComparison.Ordinal);
    }

    [Fact]
    public void Allows_absolute_path_inside()
    {
        Assert.True(PathGuard.IsInside(_worktree, Path.Combine(_worktree, "src", "App", "Program.cs")));
    }

    [Theory]
    [InlineData("../")]
    [InlineData("..")]
    [InlineData("src/../../")]
    [InlineData("src/../../../etc/passwd")]
    [InlineData("~/.ssh/id_rsa")]
    [InlineData("")]
    public void Rejects_traversal_and_home(string candidate)
    {
        Assert.False(PathGuard.TryResolveInside(_worktree, candidate, out _, out var reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void Rejects_sibling_with_common_prefix()
    {
        var sibling = _root.Sub("repo-evil");
        Assert.False(PathGuard.IsInside(_worktree, sibling));
        Assert.False(PathGuard.IsInside(_worktree, "../repo-evil/x"));
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("/")]
    public void Rejects_absolute_outside(string candidate)
    {
        Assert.False(PathGuard.IsInside(_worktree, candidate));
    }

    [Fact]
    public void Rejects_symlinked_file_pointing_outside()
    {
        var link = Path.Combine(_worktree, "innocent.txt");
        File.CreateSymbolicLink(link, Path.Combine(_outside.Path, "secret.txt"));

        Assert.False(PathGuard.TryResolveInside(_worktree, "innocent.txt", out _, out var reason));
        Assert.Contains("symbolic link", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_path_through_symlinked_directory_pointing_outside()
    {
        Directory.CreateSymbolicLink(Path.Combine(_worktree, "linked"), _outside.Path);

        Assert.False(PathGuard.IsInside(_worktree, "linked/secret.txt"));
        Assert.False(PathGuard.IsInside(_worktree, "linked"));
        Assert.False(PathGuard.IsInside(_worktree, "linked/not-there/deeper.txt"));
    }

    [Fact]
    public void Rejects_dangling_symlink_pointing_outside()
    {
        File.CreateSymbolicLink(Path.Combine(_worktree, "dangling"), "/nonexistent-auditor-target/x");
        Assert.False(PathGuard.IsInside(_worktree, "dangling"));
    }

    [Fact]
    public void Allows_symlink_that_stays_inside()
    {
        File.CreateSymbolicLink(Path.Combine(_worktree, "alias.cs"), Path.Combine(_worktree, "src", "App", "Program.cs"));
        Directory.CreateSymbolicLink(Path.Combine(_worktree, "app"), "src/App");

        Assert.True(PathGuard.IsInside(_worktree, "alias.cs"));
        Assert.True(PathGuard.IsInside(_worktree, "app/Program.cs"));
    }

    [Fact]
    public void Works_when_the_root_itself_is_reached_through_a_symlink()
    {
        var linkedRoot = Path.Combine(_outside.Path, "root-link");
        Directory.CreateSymbolicLink(linkedRoot, _worktree);

        Assert.True(PathGuard.IsInside(linkedRoot, "src/App/Program.cs"));
        Assert.False(PathGuard.IsInside(linkedRoot, "../secret.txt"));
    }

    // ---------- ToolGuard (pre-tool-use hook policy) ----------

    private ToolGuard Guard() => new(_worktree, ["view", "glob", "grep"]);

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Theory]
    [InlineData("bash", """{"command":"cat /etc/passwd"}""")]
    [InlineData("edit", """{"path":"src/App/Program.cs"}""")]
    [InlineData("web_fetch", """{"url":"https://example.com"}""")]
    [InlineData(null, "{}")]
    public void ToolGuard_denies_tools_not_in_allowlist(string? tool, string args)
    {
        var d = Guard().Evaluate(tool, Args(args));
        Assert.False(d.Allowed);
        Assert.Contains("not allowed", d.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("view", """{"path":"src/App/Program.cs"}""")]
    [InlineData("view", """{"path":"src/App/Program.cs","view_range":[1,10]}""")]
    [InlineData("glob", """{"pattern":"**/*.csproj"}""")]
    [InlineData("glob", """{"pattern":"src/**/*.cs","path":"src"}""")]
    [InlineData("grep", """{"pattern":"\\.\\./|/etc/passwd","paths":["src"],"glob":"*.cs"}""")]
    [InlineData("grep", """{"pattern":"DbContext"}""")]
    public void ToolGuard_allows_reads_inside(string tool, string args)
    {
        var d = Guard().Evaluate(tool, Args(args));
        Assert.True(d.Allowed, d.Reason);
    }

    [Fact]
    public void ToolGuard_allows_absolute_path_inside()
    {
        var json = JsonSerializer.Serialize(new { path = Path.Combine(_worktree, "src") });
        Assert.True(Guard().Evaluate("view", Args(json)).Allowed);
    }

    [Theory]
    [InlineData("view", """{"path":"/etc/passwd"}""")]
    [InlineData("view", """{"path":"../../etc/passwd"}""")]
    [InlineData("view", """{"path":"file:///etc/passwd"}""")]
    [InlineData("view", """{"filePath":"/etc/shadow"}""")]
    [InlineData("view", """{"file_path":"/etc/shadow"}""")]
    [InlineData("grep", """{"pattern":"root","paths":["src","/etc"]}""")]
    [InlineData("grep", """{"pattern":"root","paths":"/etc"}""")]
    [InlineData("grep", """{"pattern":"x","glob":"../**/*.cs"}""")]
    [InlineData("glob", """{"pattern":"/etc/*"}""")]
    [InlineData("glob", """{"pattern":"../**"}""")]
    [InlineData("glob", """{"pattern":"**/*","path":"/"}""")]
    [InlineData("glob", """{"pattern":"~/.ssh/*"}""")]
    [InlineData("glob", """{"pattern":"*","cwd":"/home"}""")]
    [InlineData("view", """{"directory":"/root"}""")]
    [InlineData("view", """{"nested":{"path":"/etc/passwd"}}""")]
    public void ToolGuard_denies_paths_outside(string tool, string args)
    {
        var d = Guard().Evaluate(tool, Args(args));
        Assert.False(d.Allowed, $"expected deny for {tool} {args}");
    }

    [Fact]
    public void ToolGuard_denies_symlink_escape()
    {
        File.CreateSymbolicLink(Path.Combine(_worktree, "README.md"), Path.Combine(_outside.Path, "secret.txt"));
        var d = Guard().Evaluate("view", Args("""{"path":"README.md"}"""));
        Assert.False(d.Allowed);
    }

    // ---------- permission handler ----------

    // SDK request types have many required members; tests only need a few of them.
    private static T Make<T>(Action<T>? init = null) where T : PermissionRequest
    {
        var o = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        init?.Invoke(o);
        return o;
    }

    private static PermissionRequestRead Read(string path, string? resolved) =>
        Make<PermissionRequestRead>(r => { r.Path = path; r.ResolvedPath = resolved!; });

    [Fact]
    public void Permission_handler_approves_reads_inside_only()
    {
        var approve = PermissionDecision.ApproveOnce().Kind;

        Assert.Equal(approve, CopilotAuditEngine.DecidePermission(Read("src/App/Program.cs", Path.Combine(_worktree, "src/App/Program.cs")), _worktree).Kind);
        Assert.Equal(approve, CopilotAuditEngine.DecidePermission(Read("src/App/Program.cs", null), _worktree).Kind);
        Assert.NotEqual(approve, CopilotAuditEngine.DecidePermission(Read("/etc/passwd", "/etc/passwd"), _worktree).Kind);

        // Resolved path wins even when the requested path looks harmless.
        Assert.NotEqual(approve, CopilotAuditEngine.DecidePermission(Read("src/x", "/etc/passwd"), _worktree).Kind);
    }

    [Fact]
    public void Permission_handler_rejects_everything_else()
    {
        var approve = PermissionDecision.ApproveOnce().Kind;
        PermissionRequest[] requests =
        [
            Make<PermissionRequestShell>(r => r.FullCommandText = "ls"),
            Make<PermissionRequestWrite>(r => r.FileName = Path.Combine(_worktree, "x.txt")),
            Make<PermissionRequestUrl>(r => r.Url = "https://example.com"),
            Make<PermissionRequestMcp>(),
            Make<PermissionRequestMemory>(),
            Make<PermissionRequestCustomTool>(),
        ];
        foreach (var r in requests)
        {
            Assert.NotEqual(approve, CopilotAuditEngine.DecidePermission(r, _worktree).Kind);
        }
    }
}

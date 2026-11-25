using System.Diagnostics;
using System.Text;
using Dashboard.Core.Entities;
using Dashboard.Web.Ado;
using Dashboard.Web.Ado.Mock;
using Dashboard.Web.Services;

namespace Dashboard.Web.Audit;

public sealed record PreparedWorktree(string Path, string Sha, string? DiffSummary, IReadOnlyList<string> ChangedFiles);

public interface ICloneManager
{
    Task<PreparedWorktree> PrepareAsync(Repo repo, string sha, string? previousSha, CancellationToken ct);
    Task CleanupAsync(Repo repo, int keep, CancellationToken ct);
}

/// <summary>
/// Persistent partial clones (--filter=blob:none) on the clones volume, plus an immutable git worktree per audited SHA.
/// The PAT is passed via --config-env so it never appears on the command line; hooks and LFS filters are disabled.
/// </summary>
public sealed class GitCloneManager(ConfigService config, IAdoClient ado, ILogger<GitCloneManager> log) : ICloneManager
{
    private string Root => ClonesRoot.Resolve(config);

    public async Task<PreparedWorktree> PrepareAsync(Repo repo, string sha, string? previousSha, CancellationToken ct)
    {
        var baseDir = Path.Combine(Root, Safe(repo.Name));
        var gitDir = Path.Combine(baseDir, "repo");
        var remote = StripUserInfo(repo.RemoteUrl ?? throw new InvalidOperationException($"Repo {repo.Name} has no remote URL"));
        Directory.CreateDirectory(baseDir);
        if (!Directory.Exists(Path.Combine(gitDir, ".git")) && !File.Exists(Path.Combine(gitDir, "HEAD")))
        {
            await GitAsync(baseDir, ct, "clone", "--filter=blob:none", "--no-checkout", remote, "repo");
        }
        else
        {
            await GitAsync(gitDir, ct, "fetch", "--filter=blob:none", "--prune", "origin");
        }

        var wtDir = Path.Combine(baseDir, "wt", sha);
        if (!Directory.Exists(wtDir))
        {
            await GitAsync(gitDir, ct, "worktree", "prune");
            await GitAsync(gitDir, ct, "worktree", "add", "--detach", "--force", wtDir, sha);
        }

        string? diff = null;
        var changed = new List<string>();
        if (previousSha is not null && previousSha != sha)
        {
            try
            {
                var names = await GitAsync(gitDir, ct, "diff", "--name-only", previousSha, sha);
                changed.AddRange(names.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                var stat = await GitAsync(gitDir, ct, "diff", "--stat", "--stat-width=120", previousSha, sha);
                diff = stat.Length > 6000 ? stat[..6000] + "\n…(truncated)" : stat;
            }
            catch (InvalidOperationException ex) { log.LogInformation("Diff {Prev}..{Sha} unavailable for {Repo}: {Error}", previousSha, sha, repo.Name, ex.Message); }
        }
        return new PreparedWorktree(wtDir, sha, diff, changed);
    }

    public async Task CleanupAsync(Repo repo, int keep, CancellationToken ct)
    {
        var baseDir = Path.Combine(Root, Safe(repo.Name));
        var wt = Path.Combine(baseDir, "wt");
        if (!Directory.Exists(wt)) return;
        foreach (var old in new DirectoryInfo(wt).GetDirectories().OrderByDescending(d => d.CreationTimeUtc).Skip(keep))
        {
            try { await GitAsync(Path.Combine(baseDir, "repo"), ct, "worktree", "remove", "--force", old.FullName); }
            catch (InvalidOperationException) { try { old.Delete(true); } catch (IOException) { } }
        }
    }

    private async Task<string> GitAsync(string workingDir, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var header = ado.GitAuthHeader;
        if (header is not null)
        {
            psi.Environment["GIT_AUTH_HEADER"] = "AUTHORIZATION: " + header;
            psi.ArgumentList.Add("--config-env=http.extraheader=GIT_AUTH_HEADER");
        }
        foreach (var c in new[] { "core.hooksPath=/dev/null", "filter.lfs.smudge=", "filter.lfs.process=", "filter.lfs.required=false", "advice.detachedHead=false", "safe.directory=*" })
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(c);
        }
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_LFS_SKIP_SMUDGE"] = "1";

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("git could not be started");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch (InvalidOperationException) { } throw; }
        var err = await stderr;
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args.Take(2))} failed ({p.ExitCode}): {Redact(err)}");
        return await stdout;
    }

    private static string Redact(string s) => s.Length > 600 ? s[..600] : s;

    private static string StripUserInfo(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;
        return new UriBuilder(u) { UserName = "", Password = "" }.Uri.ToString();
    }

    internal static string Safe(string name) => string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
}

/// <summary>Mock mode: writes synthetic repo content per SHA (no git, no network).</summary>
public sealed class MockCloneManager(ConfigService config, MockWorld world) : ICloneManager
{
    public Task<PreparedWorktree> PrepareAsync(Repo repo, string sha, string? previousSha, CancellationToken ct)
    {
        var spec = world.Repos.FirstOrDefault(r => r.Id == repo.Id) ?? throw new InvalidOperationException($"Unknown mock repo {repo.Name}");
        var dir = Path.Combine(ClonesRoot.Resolve(config), GitCloneManager.Safe(repo.Name), "wt", sha);
        if (!Directory.Exists(dir)) MockRepoContent.Write(dir, spec, sha);
        var diff = previousSha is null ? null : $"(mock) {previousSha[..7]}..{sha[..7]}: application code changes";
        return Task.FromResult(new PreparedWorktree(dir, sha, diff, []));
    }

    public Task CleanupAsync(Repo repo, int keep, CancellationToken ct)
    {
        var wt = Path.Combine(ClonesRoot.Resolve(config), GitCloneManager.Safe(repo.Name), "wt");
        if (Directory.Exists(wt))
            foreach (var old in new DirectoryInfo(wt).GetDirectories().OrderByDescending(d => d.CreationTimeUtc).Skip(keep))
                try { old.Delete(true); } catch (IOException) { }
        return Task.CompletedTask;
    }
}

public static class ClonesRoot
{
    private static string? _resolved;

    /// <summary>Configured clones path if writable, else ./data/clones (local `dotnet run`).</summary>
    public static string Resolve(ConfigService config)
    {
        if (_resolved is not null) return _resolved;
        var configured = config.Current.Audit.ClonesPath;
        try
        {
            Directory.CreateDirectory(configured);
            var probe = Path.Combine(configured, ".write-test");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            _resolved = configured;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _resolved = Path.GetFullPath(Path.Combine(Environment.GetEnvironmentVariable("DATA_DIR") ?? "data", "clones"));
            Directory.CreateDirectory(_resolved);
        }
        return _resolved;
    }
}

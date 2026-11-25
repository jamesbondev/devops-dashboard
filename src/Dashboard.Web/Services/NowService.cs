using Dashboard.Core.Configuration;
using Dashboard.Core.Entities;
using Dashboard.Core.Metrics;
using Dashboard.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Dashboard.Web.Services;

public enum AttentionKind { ApprovalForMe = 1, MainRed = 2, ProductionDeployFailed = 3, StuckRelease = 4, StuckPr = 5, PrValidationFailed = 6 }

public sealed record AttentionItem(
    AttentionKind Kind, string RepoId, string RepoName, string Title, string Detail, string? Link, DateTimeOffset Since, double Score, string? Badge = null);

public sealed record RunningItem(Run Run, string RepoName, string PipelineName, string? CurrentStage, string? Environment, bool WaitingApproval);

public sealed record MatrixCell(Deployment? Latest, Deployment? InProgress, bool Drift, int CommitsNotDeployed);

public sealed record MatrixRow(string RepoId, string RepoName, Component? Component, IReadOnlyDictionary<string, MatrixCell> Cells);

public sealed record ShippedItem(DateTimeOffset At, string RepoName, string What, string? Link, string Kind);

public sealed class NowModel
{
    public IReadOnlyList<AttentionItem> Attention { get; init; } = [];
    public IReadOnlyList<RunningItem> Running { get; init; } = [];
    public IReadOnlyList<MatrixRow> Matrix { get; init; } = [];
    public IReadOnlyList<string> Environments { get; init; } = [];
    public IReadOnlyList<ShippedItem> Shipped { get; init; } = [];
    public (int Failures, int Merged, int ProdDeploys, int NewApprovals, DateTimeOffset? Since) SinceLastLooked { get; init; }
    public DateTimeOffset GeneratedAt { get; init; }
}

public sealed class NowService(IServiceScopeFactory scopes, ConfigService config, IClock clock, HealthService health, Sync.SyncSignals signals)
{
    private (long Version, DateTimeOffset At, DateTimeOffset? LastLooked, NowModel Model)? _cache;

    public async Task<NowModel> BuildAsync(DateTimeOffset? lastLooked, CancellationToken ct = default)
    {
        var c = _cache;
        if (c is not null && c.Value.Version == signals.Version && c.Value.LastLooked == lastLooked && clock.UtcNow - c.Value.At < TimeSpan.FromSeconds(10))
            return c.Value.Model;
        var model = await BuildCoreAsync(lastLooked, ct);
        _cache = (signals.Version, clock.UtcNow, lastLooked, model);
        return model;
    }

    private async Task<NowModel> BuildCoreAsync(DateTimeOffset? lastLooked, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
        var cfg = config.Current;
        var wh = config.WorkingHours;
        var now = clock.UtcNow;
        var healths = await health.GetAllAsync(ct);
        var repos = healths.ToDictionary(h => h.Repo.Id, h => h.Repo);
        var muted = healths.Where(h => h.Repo.Muted).Select(h => h.Repo.Id).ToHashSet();
        var repoIds = repos.Keys.ToList();
        var pipelines = await db.Pipelines.AsNoTracking().Where(p => repoIds.Contains(p.RepoId)).ToDictionaryAsync(p => p.Id, ct);
        var attention = new List<AttentionItem>();

        // ① Approvals waiting for me
        var approvals = await db.Approvals.AsNoTracking().Where(a => a.Status == "pending").ToListAsync(ct);
        foreach (var a in approvals.Where(a => a.AssignedToMe && a.RepoId is not null && repos.ContainsKey(a.RepoId)))
        {
            var env = a.EnvironmentName ?? "?";
            var isProd = cfg.Environments.FirstOrDefault(e => e.Name == env)?.Tier == EnvironmentTier.Production;
            var pname = a.PipelineId is { } pid && pipelines.TryGetValue(pid, out var p) ? p.Name : "pipeline";
            attention.Add(new AttentionItem(AttentionKind.ApprovalForMe, a.RepoId!, repos[a.RepoId!].Name,
                $"Approval waiting for you: {repos[a.RepoId!].Name} → {env}",
                $"{pname} · {a.StageName} · {a.EscalationReason ?? a.Instructions ?? "approval required"}",
                a.WebUrl, a.CreatedOn, 1000 + (now - a.CreatedOn).TotalMinutes / 10 + (isProd ? 50 : 0), isProd ? "PROD" : null));
        }

        // ② Main red
        foreach (var h in healths.Where(h => h.OpenRed is not null && !muted.Contains(h.Repo.Id)))
        {
            var red = h.OpenRed!;
            var run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == red.StartRunId, ct);
            var pname = pipelines.TryGetValue(red.PipelineId, out var p) ? p.Name : "pipeline";
            var wall = (now - red.Start).TotalHours;
            attention.Add(new AttentionItem(AttentionKind.MainRed, h.Repo.Id, h.Repo.Name,
                $"main red for {Fmt.Hours(wall)}: {h.Repo.Name}",
                $"{pname} #{run?.BuildNumber} · last change by {run?.RequestedFor ?? "?"} (for context)",
                run?.WebUrl, red.Start, 800 + Math.Min(wall, 72)));
        }

        // ③ Failed Production deployments (recent) and ④ stuck releases
        var recentCutoff = now.AddHours(-cfg.NowView.RecentFailureHours);
        var recentDeps = await db.Deployments.AsNoTracking().Where(d => repoIds.Contains(d.RepoId) && d.StartTime >= now.AddDays(-30)).ToListAsync(ct);
        var depRuns = recentDeps.Select(d => d.RunId).Distinct().ToList();
        var runsById = await db.Runs.AsNoTracking().Where(r => depRuns.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        foreach (var g in recentDeps.Where(d => d.Tier == EnvironmentTier.Production && d.FinishTime >= recentCutoff
                                               && ResultClassifier.Classify(d.Result, cfg.Results) == Outcome.Fail && !muted.Contains(d.RepoId))
                     .GroupBy(d => (d.RunId, d.EnvironmentName)))
        {
            var d = g.First();
            // Skip if a later deployment of the same pipeline to the same env succeeded.
            var recovered = recentDeps.Any(x => x.PipelineId == d.PipelineId && x.EnvironmentName == d.EnvironmentName && x.StartTime > d.StartTime
                                                && ResultClassifier.Classify(x.Result, cfg.Results) == Outcome.Pass);
            if (recovered) continue;
            attention.Add(new AttentionItem(AttentionKind.ProductionDeployFailed, d.RepoId, repos[d.RepoId].Name,
                $"{d.EnvironmentName} deployment failed: {repos[d.RepoId].Name}",
                $"{d.StageName} · {d.Version} · {g.Count()} component(s)", runsById.GetValueOrDefault(d.RunId)?.WebUrl, d.FinishTime ?? d.StartTime!.Value, 700, "PROD"));
        }
        foreach (var d in recentDeps.Where(d => d.FinishTime is null && d.StartTime is not null && d.State == "inProgress" && !muted.Contains(d.RepoId)).GroupBy(d => (d.RunId, d.EnvironmentName)).Select(g => g.First()))
        {
            if (!StuckRelease.IsStuck(d, recentDeps, now, out var minutes, out var usual)) continue;
            attention.Add(new AttentionItem(AttentionKind.StuckRelease, d.RepoId, repos[d.RepoId].Name,
                $"Release slow: {repos[d.RepoId].Name} → {d.EnvironmentName}",
                $"{d.StageName} running {minutes:0} min (usual ≤ {usual:0} min, approval wait excluded)", runsById.GetValueOrDefault(d.RunId)?.WebUrl, d.StartTime!.Value, 600));
        }

        // ⑤ Stuck PRs
        foreach (var h in healths.Where(h => !muted.Contains(h.Repo.Id)))
            foreach (var f in h.Prs.Active.Where(f => f.Stuck))
                attention.Add(new AttentionItem(AttentionKind.StuckPr, h.Repo.Id, h.Repo.Name,
                    $"PR stuck {f.AgeWorkingDays:0.#}d: {h.Repo.Name} !{f.Pr.Id} {f.Pr.Title}",
                    string.Join(" · ", f.StuckReasons) + (f.WaitingOn.Count > 0 ? $" · waiting on {string.Join(", ", f.WaitingOn)}" : ""),
                    f.Pr.WebUrl, f.Pr.PublishedOn ?? f.Pr.CreatedOn, 400 + f.AgeWorkingDays * 10));

        // ⑥ Failed PR validations (latest PR build per active PR failed)
        var activePrIds = healths.SelectMany(h => h.Prs.Active).Where(f => !f.Pr.IsDraft).Select(f => f.Pr.Id).ToHashSet();
        var prRuns = await db.Runs.AsNoTracking().Where(r => repoIds.Contains(r.RepoId) && r.Reason == "pullRequest" && r.QueueTime >= now.AddDays(-14) && r.Status == "completed")
            .ToListAsync(ct);
        foreach (var g in prRuns.GroupBy(r => r.SourceBranch))
        {
            var latest = g.OrderByDescending(r => r.QueueTime).First();
            var prId = ParsePrId(latest.SourceBranch);
            if (prId is null || !activePrIds.Contains(prId.Value) || muted.Contains(latest.RepoId)) continue;
            if (ResultClassifier.Classify(latest.Result, cfg.Results) != Outcome.Fail) continue;
            var pr = healths.SelectMany(h => h.Prs.Active).First(f => f.Pr.Id == prId).Pr;
            attention.Add(new AttentionItem(AttentionKind.PrValidationFailed, latest.RepoId, repos[latest.RepoId].Name,
                $"PR validation failed: {repos[latest.RepoId].Name} !{prId} {pr.Title}", $"build {latest.BuildNumber} · {pr.AuthorName}",
                latest.WebUrl, latest.FinishTime ?? latest.QueueTime, 200));
        }

        // Running / queued
        var active = await db.Runs.AsNoTracking().Where(r => repoIds.Contains(r.RepoId) && r.Status != "completed" && !r.DeletedInAdo).OrderBy(r => r.QueueTime).ToListAsync(ct);
        var activeIds = active.Select(r => r.Id).ToList();
        var activeStages = await db.TimelineRecords.AsNoTracking().Where(t => activeIds.Contains(t.RunId) && t.Type == "Stage" && !t.IsPreviousAttempt).ToListAsync(ct);
        var running = active.Select(r =>
        {
            var stages = activeStages.Where(s => s.RunId == r.Id).OrderBy(s => s.Order).ToList();
            var current = stages.FirstOrDefault(s => s.State == "inProgress") ?? stages.FirstOrDefault(s => s.State == "pending");
            var env = current is null ? null : StageClassifier.MatchEnvironment(current.Name, current.Identifier, cfg.Environments)?.Name;
            var waiting = approvals.Any(a => a.RunId == r.Id);
            return new RunningItem(r, repos.GetValueOrDefault(r.RepoId)?.Name ?? "?", pipelines.GetValueOrDefault(r.PipelineId)?.Name ?? "?", current?.Name, env, waiting);
        }).ToList();

        // Component × environment matrix
        var envs = cfg.Environments.OrderBy(e => e.Order).Select(e => e.Name).ToList();
        var components = await db.Components.AsNoTracking().Where(c => repoIds.Contains(c.RepoId) && !c.Hidden).ToListAsync(ct);
        var allDeps = await db.Deployments.AsNoTracking().Where(d => repoIds.Contains(d.RepoId)).OrderByDescending(d => d.StartTime).Take(20000).ToListAsync(ct);
        var commits = await db.Commits.AsNoTracking().Where(c => repoIds.Contains(c.RepoId) && c.Date >= now.AddDays(-60)).ToListAsync(ct);
        var allRunsForDeps = allDeps.Select(d => d.RunId).Distinct().ToList();
        var depRunTimes = await db.Runs.AsNoTracking().Where(r => allRunsForDeps.Contains(r.Id)).Select(r => new { r.Id, r.QueueTime }).ToDictionaryAsync(r => r.Id, r => r.QueueTime, ct);
        var matrix = new List<MatrixRow>();
        foreach (var repo in repos.Values.Where(r => !r.Muted).OrderBy(r => r.Name))
        {
            var comps = components.Where(c => c.RepoId == repo.Id).OrderBy(c => c.Kind).ThenBy(c => c.Name).Cast<Component?>().ToList();
            var repoDeps = allDeps.Where(d => d.RepoId == repo.Id).ToList();
            if (repoDeps.Count == 0) continue;
            if (comps.Count == 0) comps.Add(null);
            foreach (var comp in comps)
            {
                var cells = new Dictionary<string, MatrixCell>();
                var compDeps = repoDeps.Where(d => comp is null ? true : d.ComponentId == comp.Id || d.ComponentId == null).ToList();
                foreach (var env in envs)
                {
                    var envDeps = compDeps.Where(d => d.EnvironmentName == env).ToList();
                    var latest = envDeps.Where(d => ResultClassifier.Classify(d.Result, cfg.Results) == Outcome.Pass).OrderByDescending(d => d.FinishTime).FirstOrDefault();
                    var inProgress = envDeps.FirstOrDefault(d => d.FinishTime is null && d.State is "inProgress" or "pending");
                    var notDeployed = latest is null ? 0 : commits.Count(c => c.RepoId == repo.Id && depRunTimes.TryGetValue(latest.RunId, out var q) && c.Date > q);
                    cells[env] = new MatrixCell(latest, inProgress, false, notDeployed);
                }
                // Drift: Production environments on different versions
                var prodVersions = cfg.Environments.Where(e => e.Tier == EnvironmentTier.Production).Select(e => cells.GetValueOrDefault(e.Name)?.Latest?.Version).Where(v => v is not null).Distinct().Count();
                if (prodVersions > 1)
                    foreach (var e in cfg.Environments.Where(e => e.Tier == EnvironmentTier.Production))
                        if (cells.TryGetValue(e.Name, out var cell)) cells[e.Name] = cell with { Drift = true };
                matrix.Add(new MatrixRow(repo.Id, repo.Name, comp, cells));
            }
        }

        // Shipped
        var shippedSince = now.AddHours(-cfg.NowView.ShippedHours);
        var shipped = new List<ShippedItem>();
        var merged = await db.PullRequests.AsNoTracking().Where(p => repoIds.Contains(p.RepoId) && p.Status == "completed" && p.ClosedOn >= shippedSince).ToListAsync(ct);
        shipped.AddRange(merged.Select(p => new ShippedItem(p.ClosedOn!.Value, repos[p.RepoId].Name, $"merged !{p.Id} {p.Title}", p.WebUrl, "merge")));
        shipped.AddRange(recentDeps.Where(d => d.Tier == EnvironmentTier.Production && d.FinishTime >= shippedSince && ResultClassifier.Classify(d.Result, cfg.Results) == Outcome.Pass)
            .GroupBy(d => (d.RunId, d.EnvironmentName)).Select(g => g.First())
            .Select(d => new ShippedItem(d.FinishTime!.Value, repos[d.RepoId].Name, $"deployed {d.Version} to {d.EnvironmentName}", runsById.GetValueOrDefault(d.RunId)?.WebUrl, "deploy")));

        // Since last looked
        var since = lastLooked ?? now.AddHours(-24);
        var failuresSince = await db.Runs.AsNoTracking().CountAsync(r => repoIds.Contains(r.RepoId) && r.Reason != "pullRequest" && r.FinishTime >= since && (r.Result == "failed" || r.Result == "partiallySucceeded"), ct);
        var mergedSince = await db.PullRequests.AsNoTracking().CountAsync(p => repoIds.Contains(p.RepoId) && p.Status == "completed" && p.ClosedOn >= since, ct);
        var prodSince = recentDeps.Where(d => d.Tier == EnvironmentTier.Production && d.FinishTime >= since && ResultClassifier.Classify(d.Result, cfg.Results) == Outcome.Pass)
            .Select(d => (d.RunId, d.EnvironmentName)).Distinct().Count();
        var newApprovals = approvals.Count(a => a.AssignedToMe && a.CreatedOn >= since);

        return new NowModel
        {
            Attention = attention.OrderBy(a => a.Kind).ThenByDescending(a => a.Score).ToList(),
            Running = running,
            Matrix = matrix,
            Environments = envs,
            Shipped = shipped.OrderByDescending(s => s.At).Take(40).ToList(),
            SinceLastLooked = (failuresSince, mergedSince, prodSince, newApprovals, lastLooked),
            GeneratedAt = now,
        };
    }

    private static int? ParsePrId(string? sourceBranch) =>
        sourceBranch is not null && sourceBranch.StartsWith("refs/pull/", StringComparison.Ordinal)
        && int.TryParse(sourceBranch.Split('/')[2], out var id) ? id : null;
}

public static class Fmt
{
    public static string Hours(double h) => h < 1 ? $"{h * 60:0}m" : h < 48 ? $"{h:0.#}h" : $"{h / 24:0.#}d";
    public static string Ago(DateTimeOffset t, DateTimeOffset now)
    {
        var d = now - t;
        return d.TotalSeconds < 60 ? "just now" : d.TotalMinutes < 60 ? $"{d.TotalMinutes:0}m ago" : d.TotalHours < 48 ? $"{d.TotalHours:0}h ago" : $"{d.TotalDays:0}d ago";
    }
    public static string Pct(double? v) => v is null ? "—" : $"{v:P0}";
    public static string Pct1(double? v) => v is null ? "—" : $"{v:P1}";
    public static string Num(double? v, string format = "0.#") => v is null ? "—" : v.Value.ToString(format);
    public static string ShortSha(string? sha) => sha is null ? "—" : sha.Length > 8 ? sha[..8] : sha;
}

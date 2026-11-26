using System.Text;
using Dashboard.Core.Entities;
using Dashboard.Web.Data;
using Dashboard.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Dashboard.Web;

public static class Endpoints
{
    public static void MapDashboardEndpoints(this WebApplication app)
    {
        app.MapGet("/health", async (DashboardDbContext db, Sync.SyncSignals signals) =>
        {
            var ok = await db.Database.CanConnectAsync();
            var hot = await db.SyncCursors.AsNoTracking().FirstOrDefaultAsync(c => c.Key == "hot");
            return ok ? Results.Ok(new { status = "ok", lastHotSync = hot?.LastSuccessAt, running = signals.Running })
                      : Results.Problem("database unavailable");
        });

        // Diagnostics: deployments derived for a repo, and how each recent run's stages map to environments.
        app.MapGet("/debug/deploys/{name}", async (string name, DashboardDbContext db, ConfigService config, NowService now, CancellationToken ct) =>
        {
            var cfg = config.Current;
            var repo = await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Name == name, ct);
            if (repo is null) return Results.NotFound($"no repo '{name}'");
            var sb = new StringBuilder();
            var deps = await db.Deployments.AsNoTracking().Where(d => d.RepoId == repo.Id).OrderByDescending(d => d.StartTime).ToListAsync(ct);
            var comps = await db.Components.AsNoTracking().Where(c => c.RepoId == repo.Id).ToListAsync(ct);
            sb.AppendLine($"repo {repo.Name} muted={repo.Muted} assigned={repo.Assigned}  components: {string.Join(", ", comps.Select(c => $"{c.Id}:{c.Name}({c.Kind}{(c.Hidden ? ",hidden" : "")})"))}");
            sb.AppendLine($"configured envs: {string.Join(", ", cfg.Environments.OrderBy(e => e.Order).Select(e => e.Name))}");
            sb.AppendLine($"stored deployments: {deps.Count}; by env: {string.Join(", ", deps.GroupBy(d => d.EnvironmentName).Select(g => $"{g.Key}={g.Count()}"))}");
            foreach (var d in deps.Take(15))
                sb.AppendLine($"  run {d.RunId} {d.EnvironmentName} comp={d.ComponentId?.ToString() ?? "-"} stage='{d.StageName}' state={d.State} result={d.Result} start={d.StartTime:u} finish={d.FinishTime:u} v={d.Version}");
            try
            {
                var model = await now.BuildAsync(null);
                foreach (var row in model.Matrix.Where(r => r.RepoId == repo.Id))
                    sb.AppendLine($"matrix row comp={row.Component?.Name ?? "-"}: " + string.Join(" | ", row.Cells.Select(c => $"{c.Key}={(c.Value.Latest?.Version ?? (c.Value.InProgress is null ? "—" : "running"))}")));
                if (!model.Matrix.Any(r => r.RepoId == repo.Id)) sb.AppendLine("matrix: no row for this repo");
            }
            catch (Exception ex) { sb.AppendLine($"matrix build failed: {ex.GetType().Name}: {ex.Message}"); }
            sb.AppendLine();
            var pipelines = await db.Pipelines.AsNoTracking().Where(p => p.RepoId == repo.Id).ToDictionaryAsync(p => p.Id, ct);
            var runs = await db.Runs.AsNoTracking().Where(r => r.RepoId == repo.Id && r.Reason != "pullRequest").OrderByDescending(r => r.QueueTime).Take(40).ToListAsync(ct);
            foreach (var r in runs.Where(r => pipelines.TryGetValue(r.PipelineId, out var p) && Core.Metrics.StageClassifier.AnyMatch(cfg.Pipelines.ReleaseNamePatterns, p.Name)).Take(8)
                         .DefaultIfEmpty().Where(r => r is not null).Concat(runs.Take(3)).DistinctBy(r => r!.Id))
            {
                var recs = await db.TimelineRecords.AsNoTracking().Where(t => t.RunId == r!.Id && t.Type == "Stage").OrderBy(t => t.Order).ToListAsync(ct);
                sb.AppendLine($"run {r!.Id} {pipelines.GetValueOrDefault(r.PipelineId)?.Name} #{r.BuildNumber} {r.QueueTime:u} {r.Status}/{r.Result} stages={recs.Count} deps={deps.Count(d => d.RunId == r.Id)}");
                foreach (var t in recs.Where(t => !t.IsPreviousAttempt))
                    sb.AppendLine($"    '{t.Name}' id={t.Identifier} state={t.State} result={t.Result} → {Core.Metrics.StageClassifier.MatchEnvironment(t.Name, t.Identifier, cfg.Environments)?.Name ?? "(no env)"}");
            }
            return Results.Text(sb.ToString(), "text/plain; charset=utf-8");
        });

        // Diagnostics: a "running" run as stored vs. what ADO returns now (explains stuck Running & queued rows).
        app.MapGet("/debug/running", async (DashboardDbContext db, Ado.IAdoClient ado, CancellationToken ct) =>
        {
            var sb = new StringBuilder();
            var stored = await db.Runs.AsNoTracking().Where(r => r.Status != "completed").OrderBy(r => r.QueueTime).ToListAsync(ct);
            var repos = await db.Repos.AsNoTracking().ToDictionaryAsync(r => r.Id, ct);
            List<Core.Entities.Run> active;
            try { active = (await ado.ListActiveRunsAsync(ct)).ToList(); }
            catch (Exception ex) { active = []; sb.AppendLine($"ListActiveRuns failed: {ex.GetType().Name}: {ex.Message}"); }
            var activeById = active.ToDictionary(r => r.Id);
            sb.AppendLine($"stored non-completed runs: {stored.Count}; ADO active runs (project): {active.Count}").AppendLine();
            foreach (var r in stored)
            {
                var repo = repos.GetValueOrDefault(r.RepoId);
                sb.AppendLine($"run {r.Id} #{r.BuildNumber} repo={repo?.Name ?? r.RepoId} assigned={repo?.Assigned} queued={r.QueueTime:u} stored status={r.Status} result={r.Result} deletedInAdo={r.DeletedInAdo}");
                sb.AppendLine($"    in ADO active list: {(activeById.TryGetValue(r.Id, out var a) ? $"yes (status={a.Status})" : "no")}");
                try
                {
                    var fresh = await ado.GetRunAsync(r.Id, ct);
                    sb.AppendLine(fresh is null ? "    GET by id: not found (404)" : $"    GET by id: status={fresh.Status} result={fresh.Result} finished={fresh.FinishTime:u}");
                }
                catch (Exception ex) { sb.AppendLine($"    GET by id failed: {ex.GetType().Name}: {ex.Message}"); }
            }
            var hot = await db.SyncCursors.AsNoTracking().FirstOrDefaultAsync(c => c.Key == "hot", ct);
            sb.AppendLine().AppendLine($"hot sync: last success {hot?.LastSuccessAt:u}; last error: {hot?.LastError ?? "none"}");
            return Results.Text(sb.ToString(), "text/plain; charset=utf-8");
        });

        // Diagnostics: how each recent default-branch run of a repo is rated (explains "main red").
        app.MapGet("/debug/red/{name}", async (string name, DashboardDbContext db, ConfigService config, HealthService health) =>
        {
            var cfg = config.Current;
            var repo = await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Name == name);
            if (repo is null) return Results.NotFound($"no repo '{name}'");
            var calc = new Core.Metrics.BuildMetricsCalculator(cfg);
            var sb = new StringBuilder();
            var h = (await health.GetAllAsync()).FirstOrDefault(x => x.Repo.Id == repo.Id);
            sb.AppendLine($"repo {repo.Name}  defaultBranch={repo.DefaultBranch ?? "(null → refs/heads/main)"}  assigned={repo.Assigned}");
            sb.AppendLine($"openRed: {(h?.OpenRed is { } o ? $"pipeline {o.PipelineId} since {o.Start:u} (run {o.StartRunId})" : "none")}");
            sb.AppendLine($"release pipeline ids: {string.Join(",", h?.ReleasePipelineIds ?? new HashSet<int>())}").AppendLine();
            var pipelines = await db.Pipelines.AsNoTracking().Where(p => p.RepoId == repo.Id).ToListAsync();
            var branches = await db.Runs.AsNoTracking().Where(r => r.RepoId == repo.Id).GroupBy(r => r.SourceBranch)
                .Select(g => new { g.Key, Count = g.Count(), Last = g.Max(r => r.QueueTime) }).ToListAsync();
            sb.AppendLine("runs by branch: " + string.Join("; ", branches.OrderByDescending(b => b.Count).Take(8).Select(b => $"{b.Key}={b.Count} (last {b.Last:u})"))).AppendLine();
            foreach (var p in pipelines)
            {
                sb.AppendLine($"## pipeline {p.Id} '{p.Name}'");
                var runs = await db.Runs.AsNoTracking().Where(r => r.PipelineId == p.Id && r.SourceBranch == (repo.DefaultBranch ?? "refs/heads/main"))
                    .OrderByDescending(r => r.QueueTime).Take(12).ToListAsync();
                foreach (var r in runs)
                {
                    var recs = await db.TimelineRecords.AsNoTracking().Where(t => t.RunId == r.Id && (t.Type == "Stage" || t.Type == "Job" || t.Type == "Phase" || t.Type.StartsWith("Checkpoint"))).ToListAsync();
                    var ev = calc.EvaluateRun(r, recs);
                    var stages = recs.Where(t => t.Type == "Stage").OrderBy(t => t.Order).Select(t =>
                        $"{t.Name}[a{t.Attempt}{(t.IsPreviousAttempt ? " prev" : "")}]={t.Result ?? t.State}{(Core.Metrics.StageClassifier.IsBuildStage(t.Name, t.Identifier, cfg, false) ? "" : " (not build)")}");
                    sb.AppendLine($"  {r.QueueTime:u} #{r.BuildNumber} id={r.Id} reason={r.Reason} status={r.Status} result={r.Result} → {ev.Final}  attempts=[{string.Join(",", ev.Attempts.Select(a => $"{a.Outcome}@{a.Time:u}"))}]");
                    sb.AppendLine($"      stages: {(recs.Count == 0 ? "(no timeline synced)" : string.Join(" | ", stages))}");
                }
                sb.AppendLine();
            }
            return Results.Text(sb.ToString(), "text/plain; charset=utf-8");
        });

        app.MapGet("/export/health.md", async (HealthService health, IClock clock) =>
        {
            var all = await health.GetAllAsync();
            var sb = new StringBuilder();
            sb.AppendLine($"# Repo health — {clock.UtcNow:yyyy-MM-dd}").AppendLine();
            sb.AppendLine("| Repo | Tier | Main % | Flaky % | 1st review (wh) | Oldest PR (d) | Last Production | Next-tier gaps |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|");
            foreach (var h in all.OrderBy(h => Array.IndexOf(Core.Rules.TierResult.Order, h.Tier.Tier)).ThenBy(h => h.Repo.Name))
                sb.AppendLine($"| {h.Repo.Name} | {h.Tier.Tier}{(h.Tier.Provisional ? "*" : "")} | {Fmt.Pct(h.MainSuccessRate)} | {Fmt.Pct1(h.FlakyRunRate)} | {Fmt.Num(h.Prs.FirstReviewHours)} | {Fmt.Num(h.Prs.OldestActiveWorkingDays)} | {h.LastProductionDeploy?.FinishTime?.ToString("yyyy-MM-dd") ?? "never"} | {string.Join(", ", h.Tier.NextTierGaps.Select(g => g.RuleId))} |");
            return Results.Text(sb.ToString(), "text/markdown; charset=utf-8");
        });

        app.MapGet("/export/repo/{name}.md", async (string name, RepoDetailService detail, IClock clock) =>
        {
            var d = await detail.GetAsync(name);
            if (d is null) return Results.NotFound();
            var h = d.Health;
            var sb = new StringBuilder();
            sb.AppendLine($"# {h.Repo.Name} — {clock.UtcNow:yyyy-MM-dd}").AppendLine();
            sb.AppendLine($"**Tier:** {h.Tier.Tier}{(h.Tier.Provisional ? " (provisional)" : "")}  ").AppendLine($"**Components:** {string.Join(", ", h.Components.Select(c => c.Name))}").AppendLine();
            if (h.Tier.NextTier is { } nt)
            {
                sb.AppendLine($"## Gap to {nt}").AppendLine();
                foreach (var g in h.Tier.NextTierGaps) sb.AppendLine($"- `{g.RuleId}` {g.Title}: {g.Display}");
                sb.AppendLine();
            }
            sb.AppendLine("## Metrics").AppendLine();
            sb.AppendLine($"- Main success rate: {Fmt.Pct(h.MainSuccessRate)} (n={h.CompletedBuildRuns})");
            sb.AppendLine($"- Flaky run rate: {Fmt.Pct1(h.FlakyRunRate)}, wasted {h.WastedMinutes:0} min");
            sb.AppendLine($"- Time to first review: {Fmt.Num(h.Prs.FirstReviewHours)} wh ({h.Prs.FirstReviewLabel}); time to merge {Fmt.Num(h.Prs.MergeHours)} wh");
            sb.AppendLine($"- Deploys/week to Production: {h.Delivery.DeploysPerWeek:0.#}; lead time {Fmt.Hours(h.Delivery.MedianLeadTimeHours ?? 0)}");
            sb.AppendLine().AppendLine("## Open audit findings").AppendLine();
            foreach (var f in d.Findings.Where(f => f.State?.Status is FindingStatus.New or FindingStatus.Accepted))
                sb.AppendLine($"- **{f.Finding.CheckId}** ({f.Finding.Severity}) {f.Finding.Explanation} {(f.Finding.Recommendation is { } r ? "— " + r : "")}");
            return Results.Text(sb.ToString(), "text/markdown; charset=utf-8");
        });

        app.MapGet("/export/findings.md", async (FindingsService findings, IClock clock) =>
            Results.Text(FindingsService.ExportBacklogMarkdown(await findings.BacklogAsync(), clock.UtcNow), "text/markdown; charset=utf-8"));

        app.MapGet("/export/weekly.md", async (WeeklyService weekly) => Results.Text(await weekly.DigestAsync(), "text/markdown; charset=utf-8"));

        app.MapGet("/audit/{id}/transcript", async (string id, DashboardDbContext db) =>
        {
            var a = await db.AuditRuns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            return a is null ? Results.NotFound() : Results.Text(a.Transcript ?? "(no transcript)", "text/plain; charset=utf-8");
        });
    }
}

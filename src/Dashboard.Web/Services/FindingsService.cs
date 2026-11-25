using System.Text;
using Dashboard.Core.Contracts;
using Dashboard.Core.Entities;
using Dashboard.Core.Templates;
using Dashboard.Web.Audit;
using Dashboard.Web.Data;
using Dashboard.Web.Sync;
using Microsoft.EntityFrameworkCore;

namespace Dashboard.Web.Services;

public sealed record FleetTheme(string CheckId, string Title, string Severity, int Repos, int Findings, IReadOnlyList<string> RepoNames);

public sealed record BacklogRow(FindingState State, Finding? Latest, string RepoName);

public sealed class FindingsService(IServiceScopeFactory scopes, IClock clock, SyncSignals signals, HealthService health, AuditorClient auditor, ConfigService config)
{
    public async Task SetStatusAsync(string fingerprint, FindingStatus status, string? reason)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
        var s = await db.FindingStates.FindAsync(fingerprint);
        if (s is null) return;
        s.Status = status;
        s.Reason = string.IsNullOrWhiteSpace(reason) ? s.Reason : reason.Trim();
        if (status == FindingStatus.Done) s.ResolvedAt = clock.UtcNow;
        await db.SaveChangesAsync();
        health.Invalidate();
        signals.NotifyChanged();
    }

    public async Task EnqueueAuditAsync(string repoId, string trigger = "manual")
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
        if (await db.Jobs.AnyAsync(j => j.Type == "audit" && j.Payload == repoId && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running))) return;
        db.Jobs.Add(new Job { Type = "audit", Payload = repoId, Summary = trigger, Status = JobStatus.Queued, CreatedAt = clock.UtcNow, RunAfter = clock.UtcNow });
        await db.SaveChangesAsync();
        signals.NotifyChanged();
    }

    public async Task<IReadOnlyList<BacklogRow>> BacklogAsync()
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
        var repos = await db.Repos.AsNoTracking().Where(r => r.Assigned).ToDictionaryAsync(r => r.Id, r => r.Name);
        var states = await db.FindingStates.AsNoTracking().Where(s => repos.Keys.Contains(s.RepoId)).ToListAsync();
        var ids = states.Where(s => s.LatestFindingId != null).Select(s => s.LatestFindingId!.Value).ToList();
        var findings = new Dictionary<int, Finding>();
        foreach (var chunk in ids.Chunk(800))
        {
            var c = chunk.ToList();
            foreach (var f in await db.Findings.AsNoTracking().Where(f => c.Contains(f.Id)).ToListAsync()) findings[f.Id] = f;
        }
        return states.Select(s => new BacklogRow(s, s.LatestFindingId is { } id ? findings.GetValueOrDefault(id) : null, repos.GetValueOrDefault(s.RepoId, "?")))
            .OrderBy(r => r.State.Status).ThenBy(r => RepoDetailService.SeverityRank(r.State.Severity)).ThenBy(r => r.RepoName).ToList();
    }

    public static IReadOnlyList<FleetTheme> Themes(IReadOnlyList<BacklogRow> rows) =>
        rows.Where(r => r.State.Status is FindingStatus.New or FindingStatus.Accepted)
            .GroupBy(r => r.State.CheckId)
            .Select(g => new FleetTheme(g.Key, g.Select(x => x.Latest?.Title).FirstOrDefault(t => t is not null) ?? g.Key,
                g.Select(x => x.State.Severity).OrderBy(RepoDetailService.SeverityRank).First(),
                g.Select(x => x.RepoName).Distinct().Count(), g.Count(), g.Select(x => x.RepoName).Distinct().Order().ToList()))
            .OrderByDescending(t => t.Repos).ThenBy(t => RepoDetailService.SeverityRank(t.Severity)).ToList();

    public async Task<string> SummariseTemplateChangesAsync(RepoDetail d)
    {
        var sb = new StringBuilder();
        foreach (var t in d.Health.Templates)
        {
            var branch = TemplateComplianceEvaluator.BranchName(t.Ref);
            var history = d.TemplateCommits.Where(c => c.Branch.Equals(branch, StringComparison.OrdinalIgnoreCase)).OrderByDescending(c => c.Date).ToList();
            var idx = t.Sha is null ? -1 : history.FindIndex(c => c.Sha == t.Sha);
            var pending = idx < 0 ? history.Take(10).ToList() : history.Take(idx).ToList();
            sb.AppendLine($"Pipeline {t.PipelineName} on template branch '{branch}', last run used {Dashboard.Web.Services.Fmt.ShortSha(t.Sha)}; {pending.Count} newer template commit(s):");
            foreach (var c in pending) sb.AppendLine($"- {c.Date:yyyy-MM-dd} {c.Message}");
        }
        if (sb.Length == 0) return "No pipelines with template usage.";
        var resp = await auditor.TextAsync(new TextTaskRequest { Kind = "template-summary", Input = sb.ToString(), Model = config.Current.Audit.Model }, CancellationToken.None);
        return resp.Text + $"\n\n— {resp.Engine} {resp.Model}";
    }

    public static string ExportBacklogMarkdown(IReadOnlyList<BacklogRow> rows, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Audit findings — {now:yyyy-MM-dd}").AppendLine();
        var open = rows.Where(r => r.State.Status is FindingStatus.New or FindingStatus.Accepted).ToList();
        sb.AppendLine("## Themes across repos").AppendLine();
        foreach (var t in Themes(rows)) sb.AppendLine($"- **{t.CheckId}** {t.Title} ({t.Severity}) — {t.Repos} repos: {string.Join(", ", t.RepoNames)}");
        sb.AppendLine().AppendLine("## Open findings").AppendLine();
        sb.AppendLine("| Repo | Check | Severity | Status | Finding | Recommendation |").AppendLine("|---|---|---|---|---|---|");
        foreach (var r in open)
            sb.AppendLine($"| {r.RepoName} | {r.State.CheckId} | {r.State.Severity} | {r.State.Status} | {Esc(r.Latest?.Explanation)} | {Esc(r.Latest?.Recommendation)} |");
        return sb.ToString();
    }

    private static string Esc(string? s) => (s ?? "").Replace("|", "\\|").Replace("\n", " ");
}

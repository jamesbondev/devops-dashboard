using Dashboard.Core.Entities;
using Dashboard.Core.Metrics;
using Dashboard.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Dashboard.Web.Services;

public sealed record RunRow(Run Run, string PipelineName, Outcome BuildOutcome, IReadOnlyList<TimelineRecord> Stages, int FlakyJobs, string? FirstError, double? Minutes);

public sealed record FindingRow(Finding Finding, FindingState? State);

public sealed class RepoDetail
{
    public required RepoHealth Health { get; init; }
    public IReadOnlyList<RunRow> Runs { get; init; } = [];
    public IReadOnlyList<PrFacts> RecentPrs { get; init; } = [];
    public IReadOnlyList<Deployment> Deployments { get; init; } = [];
    public IReadOnlyList<TemplateCommit> TemplateCommits { get; init; } = [];
    public IReadOnlyList<AuditRun> Audits { get; init; } = [];
    public IReadOnlyList<FindingRow> Findings { get; init; } = [];
    public IReadOnlyList<BranchPolicy> Policies { get; init; } = [];
    public IReadOnlyList<TestFlake> TestFlakes { get; init; } = [];
    public IReadOnlyList<(string Fingerprint, string Message, int Count, DateTimeOffset Last)> TopFailures { get; init; } = [];
}

public sealed class RepoDetailService(IServiceScopeFactory scopes, ConfigService config, IClock clock, HealthService health)
{
    public async Task<RepoDetail?> GetAsync(string repoName, CancellationToken ct = default)
    {
        var all = await health.GetAllAsync(ct);
        var h = all.FirstOrDefault(x => x.Repo.Name.Equals(repoName, StringComparison.OrdinalIgnoreCase));
        if (h is null) return null;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
        var cfg = config.Current;
        var now = clock.UtcNow;
        var since = now.AddDays(-cfg.Metrics.WindowDays);
        var repoId = h.Repo.Id;

        var runs = await db.Runs.AsNoTracking().Where(r => r.RepoId == repoId && r.QueueTime >= since).OrderByDescending(r => r.QueueTime).Take(300).ToListAsync(ct);
        var ids = runs.Select(r => r.Id).ToList();
        var records = await db.TimelineRecords.AsNoTracking().Where(t => ids.Contains(t.RunId)).ToListAsync(ct);
        var byRun = records.GroupBy(r => r.RunId).ToDictionary(g => g.Key, g => (IReadOnlyList<TimelineRecord>)g.ToList());
        var calc = new BuildMetricsCalculator(cfg);
        var names = h.Pipelines.ToDictionary(p => p.Id, p => p.Name);
        var rows = runs.Select(r =>
        {
            var recs = byRun.GetValueOrDefault(r.Id) ?? [];
            var o = calc.EvaluateRun(r, recs);
            var stages = recs.Where(x => x.Type == "Stage" && !x.IsPreviousAttempt).OrderBy(x => x.Order).ToList();
            var err = recs.Where(x => x.ErrorMessage is not null).OrderBy(x => x.StartTime).Select(x => x.ErrorMessage).FirstOrDefault();
            double? minutes = r.StartTime is not null && r.FinishTime is not null ? (r.FinishTime.Value - r.StartTime.Value).TotalMinutes : null;
            return new RunRow(r, names.GetValueOrDefault(r.PipelineId, "?"), o.Final, stages, o.FlakyJobs.Count, err, minutes);
        }).ToList();

        var prCalc = new PrMetricsCalculator(cfg, config.WorkingHours);
        var prs = await db.PullRequests.AsNoTracking().Where(p => p.RepoId == repoId && (p.Status == "active" || p.ClosedOn >= since)).OrderByDescending(p => p.CreatedOn).Take(200).ToListAsync(ct);
        var prIds = prs.Select(p => p.Id).ToList();
        var events = await db.PrEvents.AsNoTracking().Where(e => prIds.Contains(e.PullRequestId)).ToListAsync(ct);
        var prFacts = prs.Select(p => prCalc.Evaluate(p, events.Where(e => e.PullRequestId == p.Id).ToList(), now)).ToList();

        var deployments = await db.Deployments.AsNoTracking().Where(d => d.RepoId == repoId && d.StartTime >= now.AddDays(-60)).OrderByDescending(d => d.StartTime).Take(400).ToListAsync(ct);
        var tplCommits = await db.TemplateCommits.AsNoTracking().Where(c => c.Date >= now.AddDays(-90)).OrderByDescending(c => c.Date).ToListAsync(ct);
        var audits = await db.AuditRuns.AsNoTracking().Where(a => a.RepoId == repoId).OrderByDescending(a => a.CreatedAt).Take(20)
            .Select(a => new AuditRun { Id = a.Id, RepoId = a.RepoId, Sha = a.Sha, Model = a.Model, Engine = a.Engine, RubricVersion = a.RubricVersion, PromptHash = a.PromptHash, Votes = a.Votes, Status = a.Status, Trigger = a.Trigger, CreatedAt = a.CreatedAt, StartedAt = a.StartedAt, CompletedAt = a.CompletedAt, DurationMs = a.DurationMs, Error = a.Error, Summary = a.Summary, ArchitectureStyle = a.ArchitectureStyle, IsBaseline = a.IsBaseline, ResultJson = a.ResultJson })
            .ToListAsync(ct);
        var latestOk = audits.FirstOrDefault(a => a.Status == AuditStatus.Succeeded);
        var findings = latestOk is null ? [] : await db.Findings.AsNoTracking().Where(f => f.AuditRunId == latestOk.Id).ToListAsync(ct);
        var states = await db.FindingStates.AsNoTracking().Where(s => s.RepoId == repoId).ToDictionaryAsync(s => s.Fingerprint, ct);
        var policies = await db.BranchPolicies.AsNoTracking().Where(p => p.RepoId == repoId).ToListAsync(ct);
        var flakes = await db.TestFlakes.AsNoTracking().Where(t => t.RepoId == repoId && t.Date >= since).ToListAsync(ct);
        var failures = records.Where(r => r.ErrorFingerprint is not null).GroupBy(r => r.ErrorFingerprint!)
            .Select(g => (g.Key, g.First().ErrorMessage ?? "", g.Select(x => x.RunId).Distinct().Count(), g.Max(x => x.FinishTime ?? x.StartTime ?? DateTimeOffset.MinValue)))
            .OrderByDescending(x => x.Item3).Take(10).ToList();

        return new RepoDetail
        {
            Health = h,
            Runs = rows,
            RecentPrs = prFacts,
            Deployments = deployments,
            TemplateCommits = tplCommits,
            Audits = audits,
            Findings = findings.Select(f => new FindingRow(f, states.GetValueOrDefault(f.Fingerprint))).OrderBy(f => f.Finding.Result == CheckResult.Pass).ThenBy(f => SeverityRank(f.Finding.Severity)).ToList(),
            Policies = policies,
            TestFlakes = flakes,
            TopFailures = failures,
        };
    }

    public static int SeverityRank(string s) => s switch { "high" => 0, "medium" => 1, _ => 2 };
}

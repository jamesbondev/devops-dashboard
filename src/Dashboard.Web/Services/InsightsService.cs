using System.Text;
using Dashboard.Core.Scanners;
using Dashboard.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Dashboard.Web.Services;

public sealed record FailureCluster(string Fingerprint, string SampleMessage, int Repos, int Runs, IReadOnlyList<string> RepoNames, DateTimeOffset FirstSeen, DateTimeOffset LastSeen);

public sealed record QuarantineCandidate(string RepoName, string Stage, string Job, int Occurrences, double WastedMinutes);

public sealed record Outlier(string Fact, string Repo, string RepoValue, string MajorityValue, double MajorityShare, int ApplicableRepos);

public sealed class InsightsModel
{
    public IReadOnlyList<FailureCluster> CrossRepoFailures { get; init; } = [];
    public IReadOnlyList<QuarantineCandidate> Quarantine { get; init; } = [];
    public IReadOnlyList<(string Repo, double WastedMinutes)> WastedByRepo { get; init; } = [];
    public IReadOnlyList<Outlier> Outliers { get; init; } = [];
    public IReadOnlyList<(DateTimeOffset At, string Branch, string Message, double? FailRateBefore, double? FailRateAfter)> TemplateReleases { get; init; } = [];
}

/// <summary>Phase 3 insights: cross-repo failure fingerprints, flakiness cost, fleet outliers, template release impact.</summary>
public sealed class InsightsService(IServiceScopeFactory scopes, ConfigService config, IClock clock, HealthService health)
{
    public async Task<InsightsModel> BuildAsync(CancellationToken ct = default)
    {
        var healths = await health.GetAllAsync(ct);
        var names = healths.ToDictionary(h => h.Repo.Id, h => h.Repo.Name);
        var ids = names.Keys.ToList();
        var now = clock.UtcNow;
        var since = now.AddDays(-config.Current.Metrics.WindowDays);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();

        var runs = await db.Runs.AsNoTracking().Where(r => ids.Contains(r.RepoId) && r.QueueTime >= since).Select(r => new { r.Id, r.RepoId, r.QueueTime, r.Result, r.Reason }).ToListAsync(ct);
        var runIds = runs.Select(r => r.Id).ToList();
        var repoOfRun = runs.ToDictionary(r => r.Id, r => r.RepoId);
        var errors = new List<(int RunId, string Fp, string Msg, DateTimeOffset At)>();
        foreach (var chunk in runIds.Chunk(800))
        {
            var c = chunk.ToList();
            errors.AddRange((await db.TimelineRecords.AsNoTracking().Where(t => c.Contains(t.RunId) && t.ErrorFingerprint != null)
                    .Select(t => new { t.RunId, t.ErrorFingerprint, t.ErrorMessage, t.FinishTime, t.StartTime }).ToListAsync(ct))
                .Select(t => (t.RunId, t.ErrorFingerprint!, t.ErrorMessage ?? "", t.FinishTime ?? t.StartTime ?? now)));
        }
        var clusters = errors.GroupBy(e => e.Fp).Select(g =>
        {
            var repos = g.Select(e => repoOfRun[e.RunId]).Distinct().ToList();
            return new FailureCluster(g.Key, g.First().Msg, repos.Count, g.Select(e => e.RunId).Distinct().Count(),
                repos.Select(r => names.GetValueOrDefault(r, "?")).Order().ToList(), g.Min(e => e.At), g.Max(e => e.At));
        }).Where(c => c.Repos >= 2).OrderByDescending(c => c.Repos).ThenByDescending(c => c.Runs).ToList();

        var quarantine = healths.SelectMany(h => h.FlakyJobs.Select(f => (h.Repo.Name, f)))
            .GroupBy(x => (x.Name, x.f.StageName, x.f.JobName))
            .Select(g => new QuarantineCandidate(g.Key.Name, g.Key.StageName, g.Key.JobName, g.Count(), g.Sum(x => x.f.WastedMinutes)))
            .Where(q => q.Occurrences >= 2).OrderByDescending(q => q.WastedMinutes).ToList();
        var wasted = healths.Select(h => (h.Repo.Name, h.WastedMinutes)).Where(x => x.WastedMinutes > 0).OrderByDescending(x => x.WastedMinutes).ToList();

        var facts = healths.Where(h => h.Facts is not null).ToDictionary(h => h.Repo.Name, h => h.Facts!);
        var outliers = ComputeOutliers(facts);

        // Template release impact: main-branch failure rate across the fleet in the 3 days before vs after each main template commit.
        var tpl = await db.TemplateCommits.AsNoTracking().Where(c => c.Date >= now.AddDays(-60)).OrderByDescending(c => c.Date).ToListAsync(ct);
        var ci = runs.Where(r => r.Reason != "pullRequest" && r.Result != null && r.Result != "canceled").ToList();
        double? Rate(DateTimeOffset from, DateTimeOffset to)
        {
            var w = ci.Where(r => r.QueueTime >= from && r.QueueTime < to).ToList();
            return w.Count < 5 ? null : (double)w.Count(r => r.Result is "failed" or "partiallySucceeded") / w.Count;
        }
        var releases = tpl.Select(c => (c.Date, c.Branch, c.Message ?? "", Rate(c.Date.AddDays(-3), c.Date), Rate(c.Date, c.Date.AddDays(3)))).ToList();

        return new InsightsModel { CrossRepoFailures = clusters, Quarantine = quarantine, WastedByRepo = wasted, Outliers = outliers, TemplateReleases = releases };
    }

    private static IReadOnlyList<Outlier> ComputeOutliers(IReadOnlyDictionary<string, RepoFacts> facts)
    {
        try
        {
            return Dashboard.Core.Audit.FleetOutliers.Compute(facts)
                .Select(o => new Outlier(o.Fact, o.Repo, o.RepoValue ? "yes" : "no", o.MajorityValue ? "yes" : "no", o.MajorityShare, o.ApplicableRepos))
                .ToList();
        }
        catch (Exception) { return []; }
    }

    public static string PlatformNote(FailureCluster c, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Same pipeline failure across {c.Repos} repos").AppendLine();
        sb.AppendLine($"Seen in **{c.Runs} runs** across **{c.Repos} repos** between {c.FirstSeen:yyyy-MM-dd HH:mm} and {c.LastSeen:yyyy-MM-dd HH:mm} UTC.").AppendLine();
        sb.AppendLine("```").AppendLine(c.SampleMessage).AppendLine("```").AppendLine();
        sb.AppendLine($"Repos: {string.Join(", ", c.RepoNames)}").AppendLine();
        sb.AppendLine("Because the same normalised error appears in unrelated repos, this looks like a shared template, agent pool or feed issue rather than a code change.");
        sb.AppendLine().AppendLine($"_Generated by the team DevOps dashboard on {now:yyyy-MM-dd}._");
        return sb.ToString();
    }
}

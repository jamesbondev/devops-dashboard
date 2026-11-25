using System.Text.RegularExpressions;
using Dashboard.Core.Configuration;
using Dashboard.Core.Entities;
using Dashboard.Core.Time;

namespace Dashboard.Core.Metrics;

/// <summary>
/// Derives deployments from timeline stages (no vso.environment_manage needed): a stage whose name/identifier
/// matches an environment's stage patterns is a deployment to that environment. Components are resolved from
/// config deploy rules, then job-name heuristics (skaffold deploys every AKS component; dacpac jobs deploy DBs).
/// </summary>
public sealed class DeploymentDeriver(DashboardConfig config)
{
    private static readonly Regex DbJob = new(@"dacpac|database|(^|[_\-\s])db($|[_\-\s])|sql", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public List<Deployment> Derive(Run run, string repoName, string pipelineName, IReadOnlyList<TimelineRecord> records, IReadOnlyList<Component> components)
    {
        var result = new List<Deployment>();
        var visible = components.Where(c => !c.Hidden).ToList();
        var stages = records.Where(r => r.Type == "Stage" && !r.IsPreviousAttempt).ToList();
        foreach (var stage in stages)
        {
            var env = StageClassifier.MatchEnvironment(stage.Name, stage.Identifier, config.Environments);
            if (env is null) continue;
            if (ResultClassifier.Classify(stage.Result, config.Results) == Outcome.Excluded && stage.State != "inProgress" && stage.State != "pending")
                continue; // skipped / cancelled before starting: not a deployment

            var children = Descendants(records, stage.RecordId).ToList();
            var jobs = children.Where(r => r.Type == "Job" && !r.IsPreviousAttempt).ToList();
            var wait = children.Where(r => r.Type.StartsWith("Checkpoint", StringComparison.OrdinalIgnoreCase) && r.StartTime is not null)
                .Sum(r => ((r.FinishTime ?? DateTimeOffset.UtcNow) - r.StartTime!.Value).TotalSeconds);

            var targets = ResolveComponents(repoName, pipelineName, stage, jobs, visible);
            foreach (var (componentId, jobResult, jobStart, jobFinish) in targets)
            {
                result.Add(new Deployment
                {
                    RunId = run.Id,
                    RepoId = run.RepoId,
                    PipelineId = run.PipelineId,
                    ComponentId = componentId,
                    EnvironmentName = env.Name,
                    Tier = env.Tier,
                    StageName = stage.Name,
                    StageRecordId = stage.RecordId,
                    Attempt = stage.Attempt,
                    Result = jobResult ?? stage.Result,
                    State = stage.State,
                    StartTime = jobStart ?? stage.StartTime,
                    FinishTime = jobFinish ?? stage.FinishTime,
                    WaitSeconds = wait,
                    Version = run.BuildNumber,
                    SourceVersion = run.SourceVersion,
                    TemplateRef = run.TemplateRef,
                });
            }
        }
        return result;
    }

    private List<(int? ComponentId, string? Result, DateTimeOffset? Start, DateTimeOffset? Finish)> ResolveComponents(
        string repoName, string pipelineName, TimelineRecord stage, List<TimelineRecord> jobs, List<Component> components)
    {
        var map = new Dictionary<int, (string? Result, DateTimeOffset? Start, DateTimeOffset? Finish)>();

        // 1. Config deploy rules (stage/job patterns → component name).
        foreach (var rule in config.Components.DeployRules)
        {
            if (rule.Repo is not null && !Regex.IsMatch(repoName, rule.Repo, RegexOptions.IgnoreCase)) continue;
            if (rule.PipelinePattern is not null && !Regex.IsMatch(pipelineName, rule.PipelinePattern, RegexOptions.IgnoreCase)) continue;
            if (rule.StagePattern is not null && !Regex.IsMatch(stage.Name, rule.StagePattern, RegexOptions.IgnoreCase)) continue;
            var comp = components.FirstOrDefault(c => c.Name.Equals(rule.Component, StringComparison.OrdinalIgnoreCase));
            if (comp is null) continue;
            if (rule.JobPattern is null) { map[comp.Id] = (null, null, null); continue; }
            foreach (var j in jobs.Where(j => Regex.IsMatch(j.Name, rule.JobPattern, RegexOptions.IgnoreCase)))
                map[comp.Id] = (j.Result, j.StartTime, j.FinishTime);
        }
        if (map.Count > 0) return map.Select(kv => ((int?)kv.Key, kv.Value.Result, kv.Value.Start, kv.Value.Finish)).ToList();

        // 2. Job name contains a component name (normalised).
        foreach (var j in jobs)
            foreach (var c in components)
                if (Norm(j.Name).Contains(Norm(c.Name), StringComparison.Ordinal) || (j.Identifier is not null && Norm(j.Identifier).Contains(Norm(c.Name), StringComparison.Ordinal)))
                    map[c.Id] = (j.Result, j.StartTime, j.FinishTime);
        if (map.Count > 0) return map.Select(kv => ((int?)kv.Key, kv.Value.Result, kv.Value.Start, kv.Value.Finish)).ToList();

        // 3. Heuristic: DB-looking jobs deploy Database components; other jobs (skaffold) deploy all AKS components.
        var dbComps = components.Where(c => c.Kind == ComponentKind.Database).ToList();
        var aksComps = components.Where(c => c.Kind == ComponentKind.AksService).ToList();
        foreach (var j in jobs)
        {
            var targets = DbJob.IsMatch(j.Name) ? dbComps : aksComps;
            foreach (var c in targets) map[c.Id] = (j.Result, j.StartTime, j.FinishTime);
        }
        if (map.Count > 0) return map.Select(kv => ((int?)kv.Key, kv.Value.Result, kv.Value.Start, kv.Value.Finish)).ToList();

        // 4. Single component, or unattributed (repo-level).
        if (components.Count == 1) return [(components[0].Id, null, null, null)];
        return [(null, null, null, null)];
    }

    private static IEnumerable<TimelineRecord> Descendants(IReadOnlyList<TimelineRecord> records, string id)
    {
        foreach (var child in records.Where(r => r.ParentId == id))
        {
            yield return child;
            foreach (var d in Descendants(records, child.RecordId)) yield return d;
        }
    }

    private static string Norm(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}

public sealed record LeadTimeSample(int PullRequestId, double TotalHours, double ReviewHours, double MergeToDeployHours, double ApprovalWaitHours, DateTimeOffset DeployedAt);

public sealed class DeliveryStats
{
    public double DeploysPerWeek { get; init; }
    public int ProductionDeployments { get; init; }
    public int FailedProductionDeployments { get; init; }
    public double? ChangeFailureRateProxy => ProductionDeployments == 0 ? null : (double)FailedProductionDeployments / ProductionDeployments;
    public double? MedianLeadTimeHours { get; init; }
    public double? MedianReviewHours { get; init; }
    public double? MedianMergeToDeployHours { get; init; }
    public double? MedianApprovalWaitHours { get; init; }
    public IReadOnlyList<LeadTimeSample> Samples { get; init; } = [];
}

/// <summary>Phase 3 delivery metrics. "Production" = Production tier (sim AND prod).</summary>
public sealed class DeliveryMetricsCalculator(DashboardConfig config)
{
    public DeliveryStats Compute(IReadOnlyList<Deployment> deployments, IReadOnlyList<Run> runs,
        IReadOnlyList<(PullRequest Pr, DateTimeOffset? FirstReviewAt)> completedPrs, DateTimeOffset windowStart, DateTimeOffset now)
    {
        var prod = deployments.Where(d => d.Tier == EnvironmentTier.Production && d.StartTime >= windowStart).ToList();
        var finished = prod.Where(d => ResultClassifier.Classify(d.Result, config.Results) is Outcome.Pass or Outcome.Fail).ToList();
        var ok = finished.Where(d => ResultClassifier.Classify(d.Result, config.Results) == Outcome.Pass).ToList();
        var distinct = ok.Select(d => (d.ComponentId, d.Version)).Distinct().Count();
        var weeks = Math.Max(1, (now - windowStart).TotalDays / 7);

        var runById = runs.ToDictionary(r => r.Id);
        var samples = new List<LeadTimeSample>();
        foreach (var (pr, firstReview) in completedPrs.Where(p => p.Pr.ClosedOn is not null))
        {
            var merged = pr.ClosedOn!.Value;
            var first = ok.Where(d => runById.TryGetValue(d.RunId, out var r) && r.QueueTime >= merged && !r.IsPullRequest
                                      && d.RepoId == pr.RepoId && d.FinishTime is not null)
                .OrderBy(d => d.FinishTime).FirstOrDefault();
            if (first is null) continue;
            var start = pr.PublishedOn ?? pr.CreatedOn;
            samples.Add(new LeadTimeSample(pr.Id,
                (first.FinishTime!.Value - start).TotalHours,
                ((firstReview ?? merged) - start).TotalHours,
                (first.FinishTime.Value - merged).TotalHours - first.WaitSeconds / 3600,
                first.WaitSeconds / 3600,
                first.FinishTime.Value));
        }

        return new DeliveryStats
        {
            DeploysPerWeek = distinct / weeks,
            ProductionDeployments = finished.Count,
            FailedProductionDeployments = finished.Count - ok.Count,
            MedianLeadTimeHours = Stats.Median(samples.Select(s => s.TotalHours).ToList()),
            MedianReviewHours = Stats.Median(samples.Select(s => s.ReviewHours).ToList()),
            MedianMergeToDeployHours = Stats.Median(samples.Select(s => s.MergeToDeployHours).ToList()),
            MedianApprovalWaitHours = Stats.Median(samples.Select(s => s.ApprovalWaitHours).ToList()),
            Samples = samples,
        };
    }
}

/// <summary>A release stage running much longer than usual, excluding approval/checkpoint wait.</summary>
public static class StuckRelease
{
    public static bool IsStuck(Deployment running, IReadOnlyList<Deployment> history, DateTimeOffset now, out double minutes, out double usual)
    {
        minutes = 0; usual = 0;
        if (running.StartTime is null || running.FinishTime is not null) return false;
        minutes = (now - running.StartTime.Value).TotalMinutes - running.WaitSeconds / 60;
        var past = history.Where(h => h.EnvironmentName == running.EnvironmentName && h.PipelineId == running.PipelineId
                                      && h.StartTime is not null && h.FinishTime is not null)
            .Select(h => (h.FinishTime!.Value - h.StartTime!.Value).TotalMinutes - h.WaitSeconds / 60).Where(m => m > 0).ToList();
        if (past.Count < 3) { usual = 30; return minutes > 60; }
        usual = (past.Count >= 20 ? Stats.Percentile(past, 0.95) : past.Max()) ?? 30;
        return minutes > Math.Max(usual * 1.5, usual + 10);
    }
}

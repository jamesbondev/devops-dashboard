using Dashboard.Core.Configuration;
using Dashboard.Core.Entities;
using Dashboard.Core.Time;

namespace Dashboard.Core.Metrics;

/// <summary>Outcome of a run's build stages at one attempt.</summary>
public sealed record AttemptOutcome(int RunId, int Attempt, Outcome Outcome, DateTimeOffset Time);

public sealed record RunBuildOutcome(
    Run Run,
    Outcome Final,
    IReadOnlyList<AttemptOutcome> Attempts,
    TimeSpan? BuildDuration,
    IReadOnlyList<FlakyJob> FlakyJobs,
    double WastedMinutes);

public sealed record FlakyJob(int RunId, string StageName, string JobName, int FailedAttempts, double WastedMinutes);

public sealed record RedInterval(int PipelineId, DateTimeOffset Start, DateTimeOffset? End, int StartRunId, int? EndRunId)
{
    public bool Open => End is null;
}

public sealed class PipelineBuildStats
{
    public int PipelineId { get; init; }
    public int CompletedRuns { get; init; }
    public int PassedRuns { get; init; }
    public int FailedRuns { get; init; }
    public double? SuccessRate => PassedRuns + FailedRuns == 0 ? null : (double)PassedRuns / (PassedRuns + FailedRuns);
    public int RunsWithFlakyJob { get; init; }
    public double? FlakyRunRate => CompletedRuns == 0 ? null : (double)RunsWithFlakyJob / CompletedRuns;
    public double WastedMinutes { get; init; }
    public IReadOnlyList<RedInterval> RedIntervals { get; init; } = [];
    public RedInterval? OpenRed => RedIntervals.LastOrDefault(r => r.Open);
    public double? MedianBuildMinutes { get; init; }
    public double? P95BuildMinutes { get; init; }
    public IReadOnlyList<FlakyJob> FlakyJobs { get; init; } = [];
    public int SameShaFlakes { get; init; }
    public IReadOnlyList<RunBuildOutcome> Outcomes { get; init; } = [];
}

/// <summary>
/// Computes build stability and flakiness from raw runs + timeline records.
/// Only build stages count (stages not matching any environment pattern and without checkpoints),
/// because CI and CD share the same YAML pipelines.
/// </summary>
public sealed class BuildMetricsCalculator(DashboardConfig config)
{
    public RunBuildOutcome EvaluateRun(Run run, IReadOnlyList<TimelineRecord> records)
    {
        var stages = records.Where(r => r.Type == "Stage").ToList();
        if (stages.Count == 0)
        {
            // No timeline: fall back to the run result (single attempt).
            var o = run.Status == "completed" ? ResultClassifier.Classify(run.Result, config.Results) : Outcome.Pending;
            var t = run.FinishTime ?? run.QueueTime;
            TimeSpan? dur = run.StartTime is not null && run.FinishTime is not null ? run.FinishTime - run.StartTime : null;
            return new RunBuildOutcome(run, o, [new AttemptOutcome(run.Id, 1, o, t)], dur, [], 0);
        }

        var checkpointParents = records.Where(r => r.Type.StartsWith("Checkpoint", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.ParentId).Where(p => p is not null).ToHashSet();
        bool IsBuild(TimelineRecord s) => StageClassifier.IsBuildStage(s.Name, s.Identifier, config.Environments,
            checkpointParents.Contains(s.RecordId));

        var buildStages = stages.Where(IsBuild).ToList();
        if (buildStages.Count == 0)
            return new RunBuildOutcome(run, Outcome.Excluded, [], null, [], 0);

        // Group stage records by logical stage (identifier/name), each with its attempts.
        var byStage = buildStages.GroupBy(s => s.Identifier ?? s.Name).ToList();
        var maxAttempt = buildStages.Max(s => s.Attempt);
        var attempts = new List<AttemptOutcome>();
        for (var a = 1; a <= maxAttempt; a++)
        {
            var effective = byStage.Select(g => g.Where(s => s.Attempt <= a).OrderByDescending(s => s.Attempt).FirstOrDefault())
                .Where(s => s is not null).Cast<TimelineRecord>().ToList();
            var thisAttempt = buildStages.Where(s => s.Attempt == a).ToList();
            if (thisAttempt.Count == 0) continue;
            var outcomes = effective.Select(s => ResultClassifier.Classify(s.Result, config.Results)).ToList();
            var outcome = outcomes.Contains(Outcome.Fail) ? Outcome.Fail
                : outcomes.Contains(Outcome.Pending) ? Outcome.Pending
                : outcomes.All(o => o == Outcome.Excluded) ? Outcome.Excluded
                : Outcome.Pass;
            var time = thisAttempt.Select(s => s.FinishTime ?? s.StartTime).Where(x => x is not null).DefaultIfEmpty(run.FinishTime ?? run.QueueTime).Max()!.Value;
            attempts.Add(new AttemptOutcome(run.Id, a, outcome, time));
        }

        var final = attempts.Count == 0 ? Outcome.Excluded : attempts[^1].Outcome;

        // Duration of the final attempt's build stages.
        var finalStages = byStage.Select(g => g.OrderByDescending(s => s.Attempt).First()).ToList();
        var starts = finalStages.Where(s => s.StartTime is not null).Select(s => s.StartTime!.Value).ToList();
        var ends = finalStages.Where(s => s.FinishTime is not null).Select(s => s.FinishTime!.Value).ToList();
        TimeSpan? duration = starts.Count > 0 && ends.Count > 0 ? ends.Max() - starts.Min() : null;

        // Flaky jobs: final attempt passed AND a previous attempt of the same job failed.
        var buildStageIds = buildStages.Select(s => s.RecordId).ToHashSet();
        var phaseToStage = records.Where(r => r.Type == "Phase" && r.ParentId is not null && buildStageIds.Contains(r.ParentId))
            .ToDictionary(r => r.RecordId, r => r.ParentId!);
        string? StageOf(TimelineRecord job) =>
            job.ParentId is null ? null : buildStageIds.Contains(job.ParentId) ? job.ParentId : phaseToStage.GetValueOrDefault(job.ParentId);
        var jobs = records.Where(r => r.Type == "Job" && StageOf(r) is not null).ToList();
        var stageNames = buildStages.ToDictionary(s => s.RecordId, s => s.Name);
        var flaky = new List<FlakyJob>();
        double wasted = 0;
        foreach (var g in jobs.GroupBy(j => j.Identifier ?? j.Name))
        {
            var latest = g.OrderByDescending(j => j.Attempt).ThenBy(j => j.IsPreviousAttempt).First();
            var failedPrev = g.Where(j => j.Attempt < latest.Attempt && ResultClassifier.Classify(j.Result, config.Results) == Outcome.Fail).ToList();
            if (failedPrev.Count == 0) continue;
            var minutes = failedPrev.Sum(j => j.StartTime is not null && j.FinishTime is not null ? (j.FinishTime.Value - j.StartTime.Value).TotalMinutes : 0);
            if (ResultClassifier.Classify(latest.Result, config.Results) == Outcome.Pass)
            {
                flaky.Add(new FlakyJob(run.Id, stageNames.GetValueOrDefault(StageOf(latest)!, "?"), latest.Name, failedPrev.Count, minutes));
                wasted += minutes;
            }
        }

        return new RunBuildOutcome(run, final, attempts, duration, flaky, wasted);
    }

    /// <summary>
    /// Stats for one pipeline's default-branch, non-PR runs in [windowStart, now].
    /// Runs before windowStart are used only to seed whether the pipeline was red at window start.
    /// </summary>
    public PipelineBuildStats ForPipeline(int pipelineId, IReadOnlyList<(Run Run, IReadOnlyList<TimelineRecord> Records)> runs,
        DateTimeOffset windowStart, DateTimeOffset now)
    {
        var outcomes = runs.Where(r => !r.Run.IsPullRequest).OrderBy(r => r.Run.QueueTime)
            .Select(r => EvaluateRun(r.Run, r.Records)).ToList();
        var inWindow = outcomes.Where(o => o.Run.QueueTime >= windowStart && o.Run.QueueTime <= now).ToList();
        var completed = inWindow.Where(o => o.Final is Outcome.Pass or Outcome.Fail).ToList();

        // Red intervals over attempt events, ordered by time.
        var events = outcomes.SelectMany(o => o.Attempts).Where(a => a.Outcome is Outcome.Pass or Outcome.Fail)
            .OrderBy(a => a.Time).ThenBy(a => a.RunId).ThenBy(a => a.Attempt).ToList();
        var intervals = new List<RedInterval>();
        RedInterval? open = null;
        foreach (var e in events)
        {
            if (e.Outcome == Outcome.Fail && open is null)
                open = new RedInterval(pipelineId, e.Time, null, e.RunId, null);
            else if (e.Outcome == Outcome.Pass && open is not null)
            {
                intervals.Add(open with { End = e.Time, EndRunId = e.RunId });
                open = null;
            }
        }
        if (open is not null) intervals.Add(open);
        var windowIntervals = intervals.Where(i => (i.End ?? now) >= windowStart)
            .Select(i => i.Start < windowStart ? i with { Start = windowStart } : i).ToList();

        // Same-SHA weak flake: a failed run followed later by a passing run on the same SHA + template SHA.
        var sameSha = 0;
        foreach (var g in completed.GroupBy(o => (o.Run.SourceVersion, o.Run.TemplateSha)))
        {
            var ordered = g.OrderBy(o => o.Run.QueueTime).ToList();
            for (var i = 0; i < ordered.Count - 1; i++)
                if (ordered[i].Final == Outcome.Fail && ordered.Skip(i + 1).Any(o => o.Final == Outcome.Pass)) { sameSha++; break; }
        }
        var sameShaWasted = completed.GroupBy(o => (o.Run.SourceVersion, o.Run.TemplateSha))
            .Where(g => g.Any(o => o.Final == Outcome.Pass))
            .SelectMany(g => g.Where(o => o.Final == Outcome.Fail && g.Any(p => p.Final == Outcome.Pass && p.Run.QueueTime > o.Run.QueueTime)))
            .Sum(o => o.BuildDuration?.TotalMinutes ?? 0);

        var durations = completed.Where(o => o.BuildDuration is not null).Select(o => o.BuildDuration!.Value.TotalMinutes).ToList();
        return new PipelineBuildStats
        {
            PipelineId = pipelineId,
            CompletedRuns = completed.Count,
            PassedRuns = completed.Count(o => o.Final == Outcome.Pass),
            FailedRuns = completed.Count(o => o.Final == Outcome.Fail),
            RunsWithFlakyJob = completed.Count(o => o.FlakyJobs.Count > 0),
            WastedMinutes = completed.Sum(o => o.WastedMinutes) + sameShaWasted,
            RedIntervals = windowIntervals,
            MedianBuildMinutes = Stats.Median(durations),
            P95BuildMinutes = durations.Count >= config.Metrics.PercentileMinSample ? Stats.Percentile(durations, 0.95) : null,
            FlakyJobs = completed.SelectMany(o => o.FlakyJobs).ToList(),
            SameShaFlakes = sameSha,
            Outcomes = inWindow,
        };
    }

    public static double RedWorkingHours(RedInterval i, WorkingHours wh, DateTimeOffset now) => wh.Between(i.Start, i.End ?? now);
    public static double RedWallHours(RedInterval i, DateTimeOffset now) => ((i.End ?? now) - i.Start).TotalHours;
}

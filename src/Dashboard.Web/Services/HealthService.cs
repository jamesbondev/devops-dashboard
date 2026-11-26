using System.Text.Json;
using Dashboard.Core.Configuration;
using Dashboard.Core.Entities;
using Dashboard.Core.Metrics;
using Dashboard.Core.Rules;
using Dashboard.Core.Scanners;
using Dashboard.Core.Templates;
using Dashboard.Web.Ado;
using Dashboard.Web.Data;
using Dashboard.Web.Sync;
using Microsoft.EntityFrameworkCore;

namespace Dashboard.Web.Services;

public sealed class RepoHealth
{
    public required Repo Repo { get; init; }
    public IReadOnlyList<Pipeline> Pipelines { get; init; } = [];
    public IReadOnlyList<Component> Components { get; init; } = [];
    public IReadOnlyList<PipelineBuildStats> PipelineStats { get; init; } = [];
    public PrRepoStats Prs { get; init; } = new();
    public IReadOnlyList<PipelineTemplateStatus> Templates { get; init; } = [];
    public required TierResult Tier { get; init; }
    public string? PreviousTier { get; init; }
    public double? MainSuccessRate { get; init; }
    public int CompletedBuildRuns { get; init; }
    public double? FlakyRunRate { get; init; }
    public double WastedMinutes { get; init; }
    public RedInterval? OpenRed { get; init; }
    public double? OpenRedWorkingHours { get; init; }
    public double? LongestRedWorkingHours { get; init; }
    public double RedWallHours30d { get; init; }
    public double? MedianBuildMinutes { get; init; }
    public Deployment? LastProductionDeploy { get; init; }
    public DeliveryStats Delivery { get; init; } = new();
    public bool Dormant { get; init; }
    public RepoFacts? Facts { get; init; }
    public AuditRun? LatestAudit { get; init; }
    public int OpenFindings { get; init; }
    public double? SuccessRateTrend { get; init; }  // delta vs 4 weeks ago
    public IReadOnlyList<(DateOnly Week, double? SuccessRate, double? FirstReviewHours, double FlakyRate)> Weekly { get; init; } = [];
    public IReadOnlyList<FlakyJob> FlakyJobs { get; init; } = [];
    public bool HasMinReviewerPolicy { get; init; }
    public bool HasBuildValidationPolicy { get; init; }
    public IReadOnlySet<int> ReleasePipelineIds { get; init; } = new HashSet<int>();
}

/// <summary>Computes per-repo health from raw rows (never accumulated). Cached per data version.</summary>
public sealed class HealthService(IServiceScopeFactory scopes, ConfigService config, IClock clock, SyncSignals signals, ILogger<HealthService> log)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private (long Version, string ConfigHash, DateTimeOffset At, IReadOnlyList<RepoHealth> Data)? _cache;

    public async Task<IReadOnlyList<RepoHealth>> GetAllAsync(CancellationToken ct = default)
    {
        var c = _cache;
        if (c is not null && c.Value.Version == signals.Version && c.Value.ConfigHash == config.Hash && clock.UtcNow - c.Value.At < TimeSpan.FromMinutes(5))
            return c.Value.Data;
        await _lock.WaitAsync(ct);
        try
        {
            c = _cache;
            if (c is not null && c.Value.Version == signals.Version && c.Value.ConfigHash == config.Hash && clock.UtcNow - c.Value.At < TimeSpan.FromMinutes(5))
                return c.Value.Data;
            var version = signals.Version;
            var data = await ComputeAsync(null, ct);
            _cache = (version, config.Hash, clock.UtcNow, data);
            return data;
        }
        finally { _lock.Release(); }
    }

    public async Task<RepoHealth?> GetAsync(string repoId, CancellationToken ct = default) =>
        (await GetAllAsync(ct)).FirstOrDefault(r => r.Repo.Id == repoId);

    public void Invalidate() => _cache = null;

    private async Task<IReadOnlyList<RepoHealth>> ComputeAsync(string? onlyRepo, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
        var cfg = config.Current;
        var wh = config.WorkingHours;
        var now = clock.UtcNow;
        var windowStart = now.AddDays(-cfg.Metrics.WindowDays);
        var historyStart = windowStart.AddDays(-14); // seed red state before window

        var repos = await db.Repos.AsNoTracking().Where(r => r.Assigned && (onlyRepo == null || r.Id == onlyRepo)).OrderBy(r => r.Name).ToListAsync(ct);
        var repoIds = repos.Select(r => r.Id).ToList();
        var pipelines = await db.Pipelines.AsNoTracking().Where(p => repoIds.Contains(p.RepoId)).ToListAsync(ct);
        var components = await db.Components.AsNoTracking().Where(c => repoIds.Contains(c.RepoId)).ToListAsync(ct);
        var runs = await db.Runs.AsNoTracking().Where(r => repoIds.Contains(r.RepoId) && r.QueueTime >= historyStart && !r.DeletedInAdo)
            .Select(r => new Run
            {
                Id = r.Id, PipelineId = r.PipelineId, RepoId = r.RepoId, BuildNumber = r.BuildNumber, SourceBranch = r.SourceBranch, SourceVersion = r.SourceVersion,
                Reason = r.Reason, Status = r.Status, Result = r.Result, QueueTime = r.QueueTime, StartTime = r.StartTime, FinishTime = r.FinishTime,
                WebUrl = r.WebUrl, TemplateRef = r.TemplateRef, TemplateSha = r.TemplateSha, RequestedFor = r.RequestedFor,
            }).ToListAsync(ct);
        var runIds = runs.Where(r => !r.IsPullRequest).Select(r => r.Id).ToList();
        var records = new List<TimelineRecord>();
        foreach (var chunk in runIds.Chunk(800))
        {
            var ids = chunk.ToList();
            records.AddRange(await db.TimelineRecords.AsNoTracking().Where(t => ids.Contains(t.RunId) && (t.Type == "Stage" || t.Type == "Job" || t.Type == "Phase" || t.Type.StartsWith("Checkpoint")))
                .Select(t => new TimelineRecord { RunId = t.RunId, RecordId = t.RecordId, ParentId = t.ParentId, Type = t.Type, Name = t.Name, Identifier = t.Identifier, Attempt = t.Attempt, IsPreviousAttempt = t.IsPreviousAttempt, Result = t.Result, State = t.State, StartTime = t.StartTime, FinishTime = t.FinishTime, Order = t.Order })
                .ToListAsync(ct));
        }
        var recordsByRun = records.GroupBy(r => r.RunId).ToDictionary(g => g.Key, g => (IReadOnlyList<TimelineRecord>)g.ToList());
        var prStart = now.AddDays(-Math.Max(cfg.Metrics.WindowDays, 60));
        var prs = await db.PullRequests.AsNoTracking().Where(p => repoIds.Contains(p.RepoId) && (p.Status == "active" || p.ClosedOn >= prStart))
            .Select(p => new PullRequest
            {
                Id = p.Id, RepoId = p.RepoId, Title = p.Title, Status = p.Status, IsDraft = p.IsDraft, AuthorId = p.AuthorId, AuthorName = p.AuthorName, AuthorIsBot = p.AuthorIsBot,
                CreatedOn = p.CreatedOn, PublishedOn = p.PublishedOn, ClosedOn = p.ClosedOn, MergeStatus = p.MergeStatus, PolicyStatus = p.PolicyStatus,
                ReviewersJson = p.ReviewersJson, WebUrl = p.WebUrl, FilesChanged = p.FilesChanged, LastMergeCommit = p.LastMergeCommit, TargetBranch = p.TargetBranch,
            }).ToListAsync(ct);
        var prIds = prs.Select(p => p.Id).ToList();
        var events = new List<PrEvent>();
        foreach (var chunk in prIds.Chunk(800)) { var ids = chunk.ToList(); events.AddRange(await db.PrEvents.AsNoTracking().Where(e => ids.Contains(e.PullRequestId)).ToListAsync(ct)); }
        var eventsByPr = events.GroupBy(e => e.PullRequestId).ToDictionary(g => g.Key, g => (IReadOnlyList<PrEvent>)g.ToList());
        var policies = await db.BranchPolicies.AsNoTracking().Where(p => repoIds.Contains(p.RepoId)).ToListAsync(ct);
        var policiesSynced = await db.SyncCursors.AsNoTracking().AnyAsync(c => c.Key == "policies" && c.LastSuccessAt != null, ct);
        var deployments = await db.Deployments.AsNoTracking().Where(d => repoIds.Contains(d.RepoId) && d.StartTime >= now.AddDays(-120)).ToListAsync(ct);
        var templateHistory = await db.TemplateCommits.AsNoTracking().ToListAsync(ct);
        var commitsInWindow = await db.Commits.AsNoTracking().Where(c => repoIds.Contains(c.RepoId) && c.Date >= windowStart).GroupBy(c => c.RepoId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var latestFacts = await db.RepoFacts.AsNoTracking().Where(f => repoIds.Contains(f.RepoId)).GroupBy(f => f.RepoId)
            .Select(g => g.OrderByDescending(f => f.CreatedAt).First()).ToListAsync(ct);
        var factsByRepo = latestFacts.ToDictionary(f => f.RepoId, f => Parse(f.FactsJson));
        var audits = await db.AuditRuns.AsNoTracking().Where(a => repoIds.Contains(a.RepoId) && a.Status == AuditStatus.Succeeded)
            .GroupBy(a => a.RepoId).Select(g => g.OrderByDescending(a => a.CompletedAt).First()).ToListAsync(ct);
        var auditByRepo = audits.ToDictionary(a => a.RepoId);
        var openStates = await db.FindingStates.AsNoTracking().Where(s => repoIds.Contains(s.RepoId) && (s.Status == FindingStatus.New || s.Status == FindingStatus.Accepted)).ToListAsync(ct);
        var factChecks = FactCheckIds();
        var snapshots = await db.HealthSnapshots.AsNoTracking().Where(s => repoIds.Contains(s.RepoId) && s.Date <= DateOnly.FromDateTime(now.AddDays(-25).UtcDateTime))
            .GroupBy(s => s.RepoId).Select(g => g.OrderByDescending(s => s.Date).First()).ToListAsync(ct);
        var snapshotByRepo = snapshots.ToDictionary(s => s.RepoId);

        var build = new BuildMetricsCalculator(cfg);
        var prCalc = new PrMetricsCalculator(cfg, wh);
        var engine = new RuleEngine(cfg);
        var tplEval = new TemplateComplianceEvaluator(cfg);
        var delivery = new DeliveryMetricsCalculator(cfg);
        var result = new List<RepoHealth>();

        foreach (var repo in repos)
        {
            try
            {
                var defaultBranch = repo.DefaultBranch ?? "refs/heads/main";
                var repoPipelines = pipelines.Where(p => p.RepoId == repo.Id).ToList();
                var repoRuns = runs.Where(r => r.RepoId == repo.Id).ToList();
                var mainRuns = repoRuns.Where(r => !r.IsPullRequest && r.SourceBranch == defaultBranch && r.Status == "completed").ToList();
                // Release pipelines (separate from CI) are excluded from build stability; their runs feed deployments.
                var releaseIds = repoPipelines.Where(p => PipelineClassifier.IsRelease(p.Name,
                        repoRuns.Where(r => r.PipelineId == p.Id).Select(r => (r.IsPullRequest,
                            (IReadOnlyList<string>)(recordsByRun.GetValueOrDefault(r.Id) ?? []).Where(t => t.Type == "Stage").Select(t => t.Name).ToList())).ToList(), cfg))
                    .Select(p => p.Id).ToHashSet();
                var buildPipelines = repoPipelines.Where(p => !releaseIds.Contains(p.Id)).ToList();
                var ciRuns = mainRuns.Where(r => !releaseIds.Contains(r.PipelineId)).ToList();
                var stats = buildPipelines.Select(p => build.ForPipeline(p.Id,
                    mainRuns.Where(r => r.PipelineId == p.Id).Select(r => (r, recordsByRun.GetValueOrDefault(r.Id) ?? (IReadOnlyList<TimelineRecord>)[])).ToList(),
                    windowStart, now)).ToList();
                var passed = stats.Sum(s => s.PassedRuns);
                var failed = stats.Sum(s => s.FailedRuns);
                var completed = stats.Sum(s => s.CompletedRuns);
                var openRed = BuildMetricsCalculator.RepoOpenRed(stats);
                var longestRed = stats.SelectMany(s => s.RedIntervals).Select(i => (double?)BuildMetricsCalculator.RedWorkingHours(i, wh, now)).DefaultIfEmpty(null).Max();
                var redWall = stats.SelectMany(s => s.RedIntervals).Sum(i => BuildMetricsCalculator.RedWallHours(i, now));
                var repoPrs = prs.Where(p => p.RepoId == repo.Id).Select(p => (p, eventsByPr.GetValueOrDefault(p.Id) ?? (IReadOnlyList<PrEvent>)[])).ToList();
                var prStats = prCalc.ForRepo(repoPrs, windowStart, now);

                var repoDeps = deployments.Where(d => d.RepoId == repo.Id).ToList();
                var lastProd = repoDeps.Where(d => d.Tier == EnvironmentTier.Production && ResultClassifier.Classify(d.Result, cfg.Results) == Outcome.Pass)
                    .OrderByDescending(d => d.FinishTime).FirstOrDefault();

                var facts = factsByRepo.GetValueOrDefault(repo.Id);
                var templates = repoPipelines.Select(p =>
                {
                    var latest = mainRuns.Where(r => r.PipelineId == p.Id).OrderByDescending(r => r.QueueTime).FirstOrDefault();
                    var yaml = facts?.Pipelines.FirstOrDefault(f => p.YamlPath is not null && f.Path.TrimStart('/').Equals(p.YamlPath.TrimStart('/'), StringComparison.OrdinalIgnoreCase));
                    var prodFromLatest = latest is not null && repoDeps.Any(d => d.RunId == latest.Id && d.Tier == EnvironmentTier.Production);
                    return tplEval.Evaluate(p, latest, yaml, templateHistory, prodFromLatest, now);
                }).ToList();

                var repoPolicies = policies.Where(p => p.RepoId == repo.Id && p.IsEnabled && p.IsBlocking && (p.RefName == null || p.RefName == defaultBranch)).ToList();
                var hasMin = repoPolicies.Any(p => p.TypeName.Contains("Minimum number of reviewers", StringComparison.OrdinalIgnoreCase) && (p.MinimumApproverCount ?? 1) >= 1);
                var hasBuild = repoPolicies.Any(p => p.TypeName.Equals("Build", StringComparison.OrdinalIgnoreCase));

                var dormant = !commitsInWindow.ContainsKey(repo.Id) && !repoRuns.Any(r => r.QueueTime >= windowStart);
                var audit = auditByRepo.GetValueOrDefault(repo.Id);
                var openForRepo = openStates.Where(s => s.RepoId == repo.Id).ToList();
                var input = new RepoHealthInput
                {
                    Type = repo.Type,
                    Dormant = dormant,
                    PipelineCount = buildPipelines.Count,
                    DefaultBranchCiRuns = ciRuns.Count(r => r.QueueTime >= windowStart),
                    CompletedBuildRuns = passed + failed,
                    MainSuccessRate = passed + failed == 0 ? null : (double)passed / (passed + failed),
                    LongestRedWorkingHours = longestRed,
                    FlakyRunRate = completed == 0 ? null : (double)stats.Sum(s => s.RunsWithFlakyJob) / completed,
                    FirstReviewHours = prStats.FirstReviewHours,
                    FirstReviewLabel = prStats.FirstReviewLabel,
                    ReviewedPrs = prStats.ReviewedInWindow,
                    OldestActivePrWorkingDays = prStats.OldestActiveWorkingDays,
                    ActivePrs = prStats.ActiveCount,
                    HasMinReviewerPolicy = policiesSynced ? hasMin : null,
                    HasBuildValidationPolicy = policiesSynced ? hasBuild : null,
                    Templates = templates,
                    DaysSinceProductionDeploy = lastProd?.FinishTime is { } f ? (now - f).TotalDays : null,
                    FactsAvailable = facts is not null,
                    HasReadme = facts?.Docs.HasReadme ?? false,
                    UnsupportedFrameworks = facts?.Dotnet.UnsupportedTargetFrameworks.Count ?? 0,
                    EveryDeployableHasTests = facts is null ? null : DeployablesHaveTests(facts),
                    OpenHighSeverityFactFindings = openForRepo.Count(s => s.Severity == "high" && factChecks.Contains(s.CheckId)),
                    AuditAvailable = audit is not null,
                };
                var tier = engine.Evaluate(input);

                var completedPrs = repoPrs.Where(p => p.p.Status == "completed").Select(p => (p.p, prCalc.Evaluate(p.p, p.Item2, now).FirstReviewAt)).ToList();
                var deliveryStats = delivery.Compute(repoDeps, repoRuns, completedPrs, windowStart, now);

                var weekly = Enumerable.Range(0, 12).Select(i =>
                {
                    var wStart = now.AddDays(-7 * (12 - i));
                    var wEnd = wStart.AddDays(7);
                    var wk = ciRuns.Where(r => r.QueueTime >= wStart && r.QueueTime < wEnd)
                        .Select(r => build.EvaluateRun(r, recordsByRun.GetValueOrDefault(r.Id) ?? [])).Where(o => o.Final is Outcome.Pass or Outcome.Fail).ToList();
                    var rv = repoPrs.Select(p => prCalc.Evaluate(p.p, p.Item2, now)).Where(f => f.FirstReviewAt >= wStart && f.FirstReviewAt < wEnd && !f.Pr.AuthorIsBot)
                        .Select(f => f.FirstReviewWorkingHours!.Value).ToList();
                    return (DateOnly.FromDateTime(wStart.UtcDateTime),
                        wk.Count == 0 ? (double?)null : (double)wk.Count(o => o.Final == Outcome.Pass) / wk.Count,
                        Stats.Median(rv),
                        wk.Count == 0 ? 0 : (double)wk.Count(o => o.FlakyJobs.Count > 0) / wk.Count);
                }).ToList();

                var snap = snapshotByRepo.GetValueOrDefault(repo.Id);
                result.Add(new RepoHealth
                {
                    Repo = repo,
                    Pipelines = repoPipelines,
                    Components = components.Where(c => c.RepoId == repo.Id && !c.Hidden).OrderBy(c => c.Kind).ThenBy(c => c.Name).ToList(),
                    PipelineStats = stats,
                    Prs = prStats,
                    Templates = templates,
                    Tier = tier,
                    PreviousTier = snap?.Tier,
                    MainSuccessRate = input.MainSuccessRate,
                    CompletedBuildRuns = input.CompletedBuildRuns,
                    FlakyRunRate = input.FlakyRunRate,
                    WastedMinutes = stats.Sum(s => s.WastedMinutes),
                    OpenRed = openRed,
                    OpenRedWorkingHours = openRed is null ? null : BuildMetricsCalculator.RedWorkingHours(openRed, wh, now),
                    LongestRedWorkingHours = longestRed,
                    RedWallHours30d = redWall,
                    MedianBuildMinutes = stats.Select(s => s.MedianBuildMinutes).Where(m => m is not null).Select(m => m!.Value).DefaultIfEmpty().Average() is var avg && avg > 0 ? avg : null,
                    LastProductionDeploy = lastProd,
                    Delivery = deliveryStats,
                    Dormant = dormant,
                    Facts = facts,
                    LatestAudit = audit,
                    OpenFindings = openForRepo.Count,
                    SuccessRateTrend = snap?.MainSuccessRate is { } prev && input.MainSuccessRate is { } cur ? cur - prev : null,
                    Weekly = weekly,
                    FlakyJobs = stats.SelectMany(s => s.FlakyJobs).ToList(),
                    HasMinReviewerPolicy = hasMin,
                    HasBuildValidationPolicy = hasBuild,
                    ReleasePipelineIds = releaseIds,
                });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Health computation failed for {Repo}", repo.Name);
            }
        }
        return result;
    }

    private HashSet<string> FactCheckIds()
    {
        try
        {
            var rubric = Core.Audit.RubricLoader.Load(config.ResolvePath(config.Current.Audit.RubricPath));
            return rubric.Checks.Where(c => c.Kind == "fact").Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or YamlDotNet.Core.YamlException)
        {
            return ["NET-001", "NET-002", "K8S-001", "K8S-002", "DOCK-001", "TEST-001"];
        }
    }

    public static bool? DeployablesHaveTests(RepoFacts facts)
    {
        var deployables = facts.Dotnet.Projects.Where(p => p.Kind is "Web" or "Worker" or "Functions").ToList();
        if (deployables.Count == 0) return null;
        var tests = facts.Dotnet.Projects.Where(p => p.IsTestProject).ToList();
        return deployables.All(d => tests.Any(t =>
            t.ProjectReferences.Any(r => r.Equals(d.Name, StringComparison.OrdinalIgnoreCase))
            || t.Name.StartsWith(d.Name + ".", StringComparison.OrdinalIgnoreCase)
            || t.Name.Equals(d.Name + "Tests", StringComparison.OrdinalIgnoreCase)));
    }

    public static RepoFacts? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<RepoFacts>(json, JsonDefaults.Web); }
        catch (JsonException) { return null; }
    }

    /// <summary>Daily snapshot of tiers and key values (for trends). Idempotent per local date.</summary>
    public async Task<string> SnapshotAsync(CancellationToken ct)
    {
        Invalidate();
        var all = await GetAllAsync(ct);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
        var today = config.WorkingHours.LocalDate(clock.UtcNow);
        foreach (var h in all)
        {
            await db.HealthSnapshots.Where(s => s.RepoId == h.Repo.Id && s.Date == today).ExecuteDeleteAsync(ct);
            await db.RuleResults.Where(s => s.RepoId == h.Repo.Id && s.Date == today).ExecuteDeleteAsync(ct);
            db.HealthSnapshots.Add(new HealthSnapshot
            {
                RepoId = h.Repo.Id, Date = today, Tier = h.Tier.Tier, Provisional = h.Tier.Provisional, ConfigHash = config.Hash,
                MainSuccessRate = h.MainSuccessRate, FlakyRunRate = h.FlakyRunRate, FirstReviewHours = h.Prs.FirstReviewHours, RedHours = h.LongestRedWorkingHours,
            });
            foreach (var o in h.Tier.Outcomes)
                db.RuleResults.Add(new RuleResult { RepoId = h.Repo.Id, Date = today, RuleId = o.RuleId, ConfigHash = config.Hash, Value = o.Value, Display = o.Display, Status = o.Status, SampleSize = o.SampleSize });
        }
        await db.SaveChangesAsync(ct);
        return $"{all.Count} health snapshots";
    }
}

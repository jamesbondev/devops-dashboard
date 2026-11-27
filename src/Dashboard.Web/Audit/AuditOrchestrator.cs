using System.Text.Json;
using Dashboard.Core.Audit;
using Dashboard.Core.Contracts;
using Dashboard.Core.Entities;
using Dashboard.Core.Scanners;
using Dashboard.Web.Ado;
using Dashboard.Web.Data;
using Dashboard.Web.Services;
using Dashboard.Web.Sync;
using Microsoft.EntityFrameworkCore;

namespace Dashboard.Web.Audit;

/// <summary>
/// Runs audits one at a time: worktree → static scan (facts + components) → fact checks → Copilot judgement checks via
/// the auditor → findings with stable fingerprints → FindingState tracking. Falls back to facts-only when the auditor
/// is unavailable. Also auto-queues audits when a repo's default branch moves (rate-limited by config).
/// </summary>
public sealed class AuditOrchestrator(
    IServiceScopeFactory scopes,
    ConfigService config,
    IClock clock,
    SyncSignals signals,
    ICloneManager clones,
    RepoScanner scanner,
    AuditorClient auditor,
    HealthService health,
    ILogger<AuditOrchestrator> log) : BackgroundService
{
    public string? Current { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await QueueTriggeredAsync(stoppingToken);
                while (await NextJobAsync(stoppingToken) is { } job)
                    await RunJobAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Audit loop failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task QueueTriggeredAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
        var now = clock.UtcNow;
        var cfg = config.Current.Audit;
        var repos = await db.Repos.Where(r => r.Assigned && !r.IsDisabled && r.HeadSha != null).ToListAsync(ct);
        var latest = await db.AuditRuns.AsNoTracking().Where(a => a.Status != AuditStatus.Failed || a.CreatedAt > now.AddHours(-6))
            .GroupBy(a => a.RepoId).Select(g => g.OrderByDescending(a => a.CreatedAt).First()).ToListAsync(ct);
        var byRepo = latest.ToDictionary(a => a.RepoId);
        var queued = (await db.Jobs.Where(j => j.Type == "audit" && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running)).Select(j => j.Payload).ToListAsync(ct)).ToHashSet();
        var auditorReady = (await auditor.StatusAsync(ct)).Ready;
        foreach (var repo in repos)
        {
            if (queued.Contains(repo.Id)) continue;
            var last = byRepo.GetValueOrDefault(repo.Id);
            string? trigger = null;
            if (last is null) trigger = "first";
            else if (last.Sha != repo.HeadSha && now - last.CreatedAt >= TimeSpan.FromHours(cfg.MinHoursBetweenAudits)) trigger = "head-moved";
            else if (last.Engine == "facts-only" && auditorReady && cfg.Enabled && now - last.CreatedAt >= TimeSpan.FromMinutes(30)) trigger = "auditor-available";
            if (trigger is null) continue;
            db.Jobs.Add(new Job { Type = "audit", Payload = repo.Id, Summary = trigger, Status = JobStatus.Queued, CreatedAt = now, RunAfter = now });
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<Job?> NextJobAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
        var now = clock.UtcNow;
        return await db.Jobs.AsNoTracking().Where(j => j.Type == "audit" && j.Status == JobStatus.Queued && j.RunAfter <= now).OrderBy(j => j.CreatedAt).FirstOrDefaultAsync(ct);
    }

    private async Task RunJobAsync(Job queued, CancellationToken ct)
    {
        var started = clock.UtcNow;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
        var job = await db.Jobs.FindAsync([queued.Id], ct);
        if (job is null) return;
        job.Status = JobStatus.Running; job.StartedAt = started; job.Attempts++;
        await db.SaveChangesAsync(ct);
        var repo = await db.Repos.FindAsync([job.Payload], ct);
        try
        {
            if (repo is null || repo.HeadSha is null) throw new InvalidOperationException("Repo not found or default branch head unknown yet (wait for the warm sync).");
            Current = repo.Name;
            signals.NotifyChanged();
            var summary = await AuditAsync(db, repo, job.Summary ?? "manual", ct);
            job.Status = JobStatus.Succeeded;
            job.Summary = summary;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Audit of {Repo} failed", repo?.Name);
            job.Status = JobStatus.Failed;
            job.Error = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
        }
        finally
        {
            Current = null;
            job.CompletedAt = clock.UtcNow;
            job.DurationMs = (long)(job.CompletedAt.Value - started).TotalMilliseconds;
            await db.SaveChangesAsync(CancellationToken.None);
            health.Invalidate();
            signals.NotifyChanged();
        }
    }

    private async Task<string> AuditAsync(DashboardDbContext db, Repo repo, string trigger, CancellationToken ct)
    {
        var cfg = config.Current;
        var now = clock.UtcNow;
        var sha = repo.HeadSha!;
        var previous = await db.AuditRuns.AsNoTracking().Where(a => a.RepoId == repo.Id && a.Status == AuditStatus.Succeeded)
            .OrderByDescending(a => a.CompletedAt).FirstOrDefaultAsync(ct);

        var run = new AuditRun
        {
            Id = Guid.NewGuid().ToString("n"), RepoId = repo.Id, Sha = sha, Status = AuditStatus.Running, Trigger = trigger,
            CreatedAt = now, StartedAt = now, Votes = cfg.Audit.Votes, Engine = "facts-only",
        };
        db.AuditRuns.Add(run);
        await db.SaveChangesAsync(ct);

        try
        {
            // 1. Worktree + static scan
            var wt = await clones.PrepareAsync(repo, sha, previous?.Sha, ct);
            var facts = await scanner.ScanAsync(wt.Path, sha, ct);
            var factsJson = JsonSerializer.Serialize(facts, JsonDefaults.Web);
            db.RepoFacts.Add(new RepoFactsRecord { RepoId = repo.Id, Sha = sha, ScannerVersion = RepoScanner.Version, CreatedAt = clock.UtcNow, FactsJson = factsJson });
            var componentsChanged = await SyncComponentsAsync(db, repo, facts, ct);
            await db.SaveChangesAsync(ct);

            // 2. Rubric + fact checks
            var rubric = RubricLoader.Load(config.ResolvePath(cfg.Audit.RubricPath));
            run.RubricVersion = $"{rubric.Version}-{rubric.Hash[..Math.Min(8, rubric.Hash.Length)]}";
            var outcomes = new List<(CheckOutcome Outcome, AuditCheck Check)>();
            var checksById = rubric.Checks.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
            var isDocs = Dashboard.Core.Configuration.RepoTypes.Resolve(repo.Name, repo.Type, cfg) == RepoType.Docs;
            foreach (var o in isDocs ? [] : FactCheckEvaluator.Evaluate(facts, rubric.FactChecks))
                if (checksById.TryGetValue(o.CheckId, out var c)) outcomes.Add((o, c));

            // 3. Judgement checks via the auditor (Copilot)
            AuditResult result = new() { Summary = "Facts-only audit (auditor unavailable).", AzureDependencies = facts.AzureServices };
            var judgement = isDocs ? [] : rubric.JudgementChecks.ToList();
            if (isDocs) result.Summary = "Docs / config repository: code checks don't apply, so no audit checks were run.";
            var status = cfg.Audit.Enabled ? await auditor.StatusAsync(ct) : new AuditorStatus { Ready = false, Error = "audit.enabled is false" };
            if (status.Ready && judgement.Count > 0)
            {
                var teamStandards = LoadTeamStandards(config.ResolvePath(cfg.Audit.RubricPath));
                var started = await auditor.StartAsync(new AuditRequest
                {
                    AuditId = run.Id, RepoName = repo.Name, RepoType = Dashboard.Core.Configuration.RepoTypes.Resolve(repo.Name, repo.Type, cfg).ToString(), Sha = sha, WorktreePath = wt.Path, FactsJson = factsJson, Checks = judgement,
                    DiffSummary = wt.DiffSummary, Model = cfg.Audit.Model, ReasoningEffort = cfg.Audit.ReasoningEffort,
                    TimeoutMinutes = cfg.Audit.TimeoutMinutes, Votes = cfg.Audit.Votes, TeamStandards = teamStandards,
                }, ct);
                var deadline = clock.UtcNow.AddMinutes(cfg.Audit.TimeoutMinutes * Math.Max(1, cfg.Audit.Votes) + 5);
                var response = started;
                while (response.Status is "queued" or "running")
                {
                    if (clock.UtcNow > deadline) throw new TimeoutException($"Auditor did not finish within {cfg.Audit.TimeoutMinutes} min.");
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                    response = await auditor.GetAsync(run.Id, ct) ?? throw new InvalidOperationException("Auditor lost the audit job (restarted?).");
                }
                if (response.Status != "succeeded" || response.Result is null)
                    throw new InvalidOperationException("Auditor failed: " + (response.Error ?? "no result"));
                result = response.Result;
                run.Engine = response.Engine ?? "copilot";
                run.Model = response.Model;
                run.PromptHash = response.PromptHash ?? "";
                run.Transcript = response.Transcript;
                foreach (var o in result.Checks)
                    if (checksById.TryGetValue(o.CheckId, out var c) && c.Kind != "fact") outcomes.Add((o, c));
            }
            else if (!status.Ready)
            {
                run.Transcript = $"Auditor not used: {status.Error ?? status.Engine}";
            }

            // 4. Findings
            var findings = outcomes.Select(x =>
            {
                var path = x.Outcome.Evidence.FirstOrDefault()?.Path;
                return new Finding
                {
                    AuditRunId = run.Id, RepoId = repo.Id, CheckId = x.Check.Id, Component = x.Outcome.Component, Path = path,
                    // Scoped to the repo: the same check/component/path in two repos must be two findings.
                    Fingerprint = FindingFingerprint.Compute(x.Check.Id, $"{repo.Id}/{x.Outcome.Component}", x.Check.Kind == "fact" ? path : null),
                    Result = x.Outcome.Result.ToLowerInvariant() switch { "pass" => CheckResult.Pass, "partial" => CheckResult.Partial, "fail" => CheckResult.Fail, _ => CheckResult.NotApplicable },
                    Severity = x.Check.Severity, Kind = x.Check.Kind, Title = x.Check.Title, Explanation = x.Outcome.Explanation,
                    Recommendation = x.Outcome.Recommendation, EvidenceJson = JsonSerializer.Serialize(x.Outcome.Evidence, JsonDefaults.Web),
                    Confidence = x.Outcome.Confidence, Uncertain = x.Outcome.Uncertain,
                };
            }).ToList();
            db.Findings.AddRange(findings);
            await db.SaveChangesAsync(ct);

            // 5. Baseline: model/rubric/prompt changed → don't auto-resolve findings that merely disappeared.
            run.IsBaseline = previous is null || previous.Model != run.Model || previous.RubricVersion != run.RubricVersion || previous.PromptHash != run.PromptHash;
            var existing = await db.FindingStates.Where(s => s.RepoId == repo.Id).ToListAsync(ct);
            var current = findings.Where(f => f.Result is CheckResult.Fail or CheckResult.Partial)
                .Select(f => (f.Fingerprint, f.CheckId, f.Severity, f.Id)).ToList();
            var tracked = FindingTracker.Apply(existing, current, repo.Id, clock.UtcNow);
            var existingByFp = existing.ToDictionary(e => e.Fingerprint);
            var judgementIds = judgement.Select(j => j.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var u in tracked.Upserts)
            {
                if (existingByFp.TryGetValue(u.Fingerprint, out var e))
                {
                    // Facts-only runs can't speak to judgement checks; baselines don't resolve on absence.
                    var resolving = u.Status == FindingStatus.Done && e.Status != FindingStatus.Done;
                    if (resolving && (run.IsBaseline || (run.Engine == "facts-only" && judgementIds.Contains(e.CheckId)))) continue;
                    db.Entry(e).CurrentValues.SetValues(u);
                }
                else db.FindingStates.Add(u);
            }

            run.Status = AuditStatus.Succeeded;
            run.CompletedAt = clock.UtcNow;
            run.DurationMs = (long)(run.CompletedAt.Value - run.StartedAt!.Value).TotalMilliseconds;
            run.Summary = result.Summary;
            run.ArchitectureStyle = result.ArchitectureStyle;
            result.Checks = outcomes.Select(o => o.Outcome).ToList();
            if (result.AzureDependencies.Count == 0) result.AzureDependencies = facts.AzureServices;
            run.ResultJson = JsonSerializer.Serialize(result, JsonDefaults.Web);
            await db.SaveChangesAsync(ct);

            if (componentsChanged)
                db.Jobs.Add(new Job { Type = "rederive", Payload = repo.Id, Status = JobStatus.Queued, CreatedAt = clock.UtcNow, RunAfter = clock.UtcNow });
            await db.SaveChangesAsync(ct);
            await clones.CleanupAsync(repo, keep: 3, ct);
            signals.Wake();
            return $"{repo.Name}@{sha[..7]}: {run.Engine}, {findings.Count(f => f.Result is CheckResult.Fail or CheckResult.Partial)} failing/partial, {tracked.NewCount} new, {tracked.ResolvedCount} resolved";
        }
        catch (Exception ex)
        {
            run.Status = AuditStatus.Failed;
            run.Error = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
            run.CompletedAt = clock.UtcNow;
            db.ChangeTracker.Entries().Where(e => e.Entity is not AuditRun && e.State is EntityState.Added or EntityState.Modified).ToList().ForEach(e => e.State = EntityState.Detached);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>Components = scanner hints (skaffold artifacts, .sqlproj) + config overrides. Returns true if the set changed.</summary>
    private async Task<bool> SyncComponentsAsync(DashboardDbContext db, Repo repo, RepoFacts facts, CancellationToken ct)
    {
        var existing = await db.Components.Where(c => c.RepoId == repo.Id).ToListAsync(ct);
        var desired = facts.Components.Select(h => (h.Name, Kind: Enum.TryParse<ComponentKind>(h.Kind, true, out var k) ? k : ComponentKind.Other, Image: h.Image, Path: (string?)h.Path, Source: ComponentSource.Discovered, Hidden: false)).ToList();
        foreach (var o in config.Current.Components.Overrides.Where(o => o.Repo.Equals(repo.Name, StringComparison.OrdinalIgnoreCase)))
        {
            desired.RemoveAll(d => d.Name.Equals(o.Name, StringComparison.OrdinalIgnoreCase));
            desired.Add((o.Name, Enum.TryParse<ComponentKind>(o.Kind, true, out var k) ? k : ComponentKind.AksService, null, null, ComponentSource.Config, o.Hidden));
        }
        var changed = false;
        foreach (var d in desired.GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()))
        {
            var e = existing.FirstOrDefault(x => x.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase));
            if (e is null)
            {
                db.Components.Add(new Component { RepoId = repo.Id, Name = d.Name, Kind = d.Kind, Source = d.Source, ImageName = d.Image, SkaffoldArtifact = d.Image, SourcePath = d.Path, Hidden = d.Hidden, LastSeenAt = clock.UtcNow });
                changed = true;
            }
            else
            {
                if (e.Kind != d.Kind || e.Hidden != d.Hidden) changed = true;
                e.Kind = d.Kind; e.Source = d.Source; e.ImageName = d.Image ?? e.ImageName; e.SourcePath = d.Path ?? e.SourcePath; e.Hidden = d.Hidden; e.LastSeenAt = clock.UtcNow;
            }
        }
        foreach (var gone in existing.Where(e => !desired.Any(d => d.Name.Equals(e.Name, StringComparison.OrdinalIgnoreCase))))
        {
            gone.Hidden = true;
            changed = true;
        }
        return changed;
    }

    private static string? LoadTeamStandards(string rubricRoot)
    {
        var dir = Path.Combine(rubricRoot, "team");
        if (!Directory.Exists(dir)) return null;
        var md = Directory.GetFiles(dir, "*.md").OrderBy(f => f).Select(File.ReadAllText).ToList();
        return md.Count == 0 ? null : string.Join("\n\n", md);
    }
}

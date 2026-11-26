using System.Text.Json;
using Dashboard.Core.Configuration;
using Dashboard.Core.Entities;
using Dashboard.Core.Metrics;
using Dashboard.Web.Ado;
using Dashboard.Web.Ado.Mock;
using Dashboard.Web.Data;
using Dashboard.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Dashboard.Web.Sync;

/// <summary>
/// Pulls data from ADO into SQLite. All metric inputs are stored raw; derived tables (deployments) are rebuilt per run.
/// Scoped: one instance per sync pass.
/// </summary>
public sealed class SyncService(
    DashboardDbContext db,
    IAdoClient ado,
    ConfigService config,
    IClock clock,
    IdentityCache identity,
    ILogger<SyncService> log)
{
    private DashboardConfig Cfg => config.Current;
    private DateTimeOffset Now => clock.UtcNow;

    // ------------------------------------------------------------------ cursors

    private async Task<SyncCursor> CursorAsync(string key)
    {
        var c = await db.SyncCursors.FindAsync(key);
        if (c is null) { c = new SyncCursor { Key = key }; db.SyncCursors.Add(c); }
        return c;
    }

    public async Task MarkAsync(string key, Exception? error = null, DateTimeOffset? watermark = null)
    {
        var c = await CursorAsync(key);
        if (error is null) { c.LastSuccessAt = Now; if (watermark is not null) c.Watermark = watermark; }
        else { c.LastErrorAt = Now; c.LastError = error.Message.Length > 500 ? error.Message[..500] : error.Message; }
        await db.SaveChangesAsync();
    }

    private void Detach<T>(Func<T, bool> predicate) where T : class
    {
        foreach (var e in db.ChangeTracker.Entries<T>().Where(e => predicate(e.Entity)).ToList()) e.State = EntityState.Detached;
    }

    private async Task<List<Repo>> AssignedReposAsync(CancellationToken ct) =>
        await db.Repos.Where(r => r.Assigned && !r.IsDisabled).ToListAsync(ct);

    // ------------------------------------------------------------------ daily

    public async Task<string> DiscoverReposAsync(CancellationToken ct)
    {
        var repos = await ado.ListRepositoriesAsync(ct);
        var existing = await db.Repos.ToDictionaryAsync(r => r.Id, ct);
        var firstRun = existing.Count == 0;
        foreach (var r in repos)
        {
            if (existing.TryGetValue(r.Id, out var e))
            {
                e.Name = r.Name; e.ProjectName = r.ProjectName; e.DefaultBranch = r.DefaultBranch; e.WebUrl = r.WebUrl; e.RemoteUrl = r.RemoteUrl;
                e.SizeBytes = r.SizeBytes; e.IsDisabled = r.IsDisabled; e.LastSeenAt = r.LastSeenAt;
            }
            else
            {
                // Mock mode: pre-assign the synthetic team's repos on first discovery so the demo is populated.
                if (firstRun && ado.IsMock && Environment.GetEnvironmentVariable("MOCK_AUTO_ASSIGN") != "false")
                {
                    var spec = MockSpec(r.Name);
                    if (spec is { TeamOwned: true }) { r.Assigned = true; r.AssignedAt = Now; r.Type = spec.Type; }
                }
                db.Repos.Add(r);
            }
        }
        await db.SaveChangesAsync(ct);
        return $"{repos.Count} repos";
    }

    private static MockWorld.MockRepo? MockSpec(string name) => MockSpecs.Value.FirstOrDefault(r => r.Name == name);
    private static readonly Lazy<IReadOnlyList<MockWorld.MockRepo>> MockSpecs = new(() => new MockWorld().Repos);

    public async Task<string> SyncPipelinesAsync(CancellationToken ct)
    {
        var count = 0;
        foreach (var repo in await AssignedReposAsync(ct))
        {
            var pipelines = await ado.ListPipelinesAsync(repo.Id, ct);
            var existing = await db.Pipelines.Where(p => p.RepoId == repo.Id).ToDictionaryAsync(p => p.Id, ct);
            foreach (var p in pipelines)
            {
                if (existing.TryGetValue(p.Id, out var e)) { e.Name = p.Name; e.Folder = p.Folder; e.YamlPath = p.YamlPath; e.WebUrl = p.WebUrl; e.LastSeenAt = p.LastSeenAt; }
                else db.Pipelines.Add(p);
                count++;
            }
            var seen = pipelines.Select(p => p.Id).ToHashSet();
            db.Pipelines.RemoveRange(existing.Values.Where(e => !seen.Contains(e.Id)));
        }
        await db.SaveChangesAsync(ct);
        return $"{count} pipelines";
    }

    public async Task<string> SyncPoliciesAsync(CancellationToken ct)
    {
        var policies = await ado.ListPoliciesAsync(ct);
        Detach<BranchPolicy>(_ => true);
        await db.BranchPolicies.ExecuteDeleteAsync(ct);
        db.BranchPolicies.AddRange(policies.GroupBy(p => p.Id).Select(g => g.First()));
        await db.SaveChangesAsync(ct);
        await MarkAsync("policies");
        return $"{policies.Count} policies";
    }

    public async Task<string> SyncEnvironmentChecksAsync(CancellationToken ct)
    {
        var total = 0;
        var me = await identity.GetAsync(ado, ct);
        // Deployment environments (optional adoId) plus gate-only environments (e.g. the Approval Gate 2 environment).
        var targets = Cfg.Environments.Select(e => (e.Name, Id: e.AdoId ?? (ado.IsMock ? e.Order + 1 : (int?)null)))
            .Concat(Cfg.Pipelines.GateEnvironments.Select(g => (g.Name, Id: (int?)g.AdoId)));
        foreach (var env in targets)
        {
            var id = env.Id;
            if (id is null) continue;
            try
            {
                var checks = await ado.ListEnvironmentChecksAsync(id.Value, env.Name, ct);
                foreach (var c in checks)
                {
                    var ids = ApproverIds(c.SettingsJson);
                    c.RequiresMe = c.RequiresMe || ids.Any(i => i.Equals(me.Id, StringComparison.OrdinalIgnoreCase) || me.GroupIds.Contains(i));
                }
                Detach<EnvironmentCheck>(x => x.EnvironmentName == env.Name);
                await db.EnvironmentChecks.Where(x => x.EnvironmentName == env.Name).ExecuteDeleteAsync(ct);
                db.EnvironmentChecks.AddRange(checks);
                total += checks.Count;
            }
            catch (HttpRequestException ex)
            {
                log.LogWarning("Could not read checks for environment {Env} ({Id}): {Error}", env.Name, id, ex.Message);
            }
        }
        await db.SaveChangesAsync(ct);
        await MarkAsync("environmentChecks");
        return $"{total} checks";
    }

    private static List<string> ApproverIds(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("approverIds", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Select(x => x.GetString()).Where(x => x is not null).Cast<string>().ToList() : [];
        }
        catch (JsonException) { return []; }
    }

    public async Task<string> SyncTemplateCommitsAsync(CancellationToken ct)
    {
        var branches = Cfg.Templates.CompliantBranches.Concat(Cfg.Templates.ExperimentalBranches).Distinct(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        var errors = new List<string>();
        foreach (var branch in branches)
        {
            var cursor = await CursorAsync("templates:" + branch);
            try
            {
                var since = cursor.Watermark?.AddDays(-2) ?? Now.AddDays(-180);
                var commits = await ado.ListTemplateCommitsAsync(branch, since, ct);
                var existing = (await db.TemplateCommits.Where(c => c.Branch == branch).Select(c => c.Sha).ToListAsync(ct)).ToHashSet();
                foreach (var c in commits.Where(c => !existing.Contains(c.Sha))) { db.TemplateCommits.Add(c); count++; }
                cursor.Watermark = Now; cursor.LastSuccessAt = Now;
            }
            catch (HttpRequestException ex)
            {
                // Wrong repo/project or a branch that doesn't exist (e.g. no `next`): record it and carry on.
                var msg = $"Template repo '{Cfg.Templates.Project}/{Cfg.Templates.Repository}' branch '{branch}': {ex.Message}. Check templates.project/repository/branches in config/dashboard.yaml.";
                log.LogWarning("{Message}", msg);
                cursor.LastErrorAt = Now;
                cursor.LastError = msg.Length > 500 ? msg[..500] : msg;
                errors.Add($"{branch}: {(ex.StatusCode is { } sc ? ((int)sc).ToString() : ex.Message)}");
            }
        }
        await db.SaveChangesAsync(ct);
        return $"{count} new template commits" + (errors.Count > 0 ? $" (failed: {string.Join(", ", errors)} — see Jobs)" : "");
    }

    // ------------------------------------------------------------------ hot

    public async Task<string> HotAsync(CancellationToken ct)
    {
        var assigned = (await AssignedReposAsync(ct)).ToDictionary(r => r.Id);
        var me = await identity.GetAsync(ado, ct);

        // Running / queued builds
        var active = (await ado.ListActiveRunsAsync(ct)).Where(r => assigned.ContainsKey(r.RepoId)).ToList();
        var activeIds = active.Select(r => r.Id).ToHashSet();
        foreach (var run in active)
        {
            await UpsertRunAsync(run, ct);
            await RefreshTimelineAsync(run, assigned[run.RepoId], ct, force: true);
        }
        // Builds we thought were running but aren't in the active list any more → refresh in place.
        var stale = await db.Runs.Where(r => r.Status != "completed" && !r.DeletedInAdo).ToListAsync(ct);
        foreach (var r in stale.Where(r => !activeIds.Contains(r.Id) && assigned.ContainsKey(r.RepoId)))
        {
            // Direct GET by id: a list query from the run's queue time can miss very old runs (e.g. one queued 900 days ago).
            var fresh = await ado.GetRunAsync(r.Id, ct);
            if (fresh is null) { r.Status = "completed"; r.Result ??= "canceled"; r.DeletedInAdo = true; continue; }
            await UpsertRunAsync(fresh, ct);
            await RefreshTimelineAsync(fresh, assigned[fresh.RepoId], ct, force: true);
        }

        // Active PRs
        var prs = (await ado.ListActivePullRequestsAsync(ct)).Where(p => assigned.ContainsKey(p.RepoId)).ToList();
        var activePrIds = prs.Select(p => p.Id).ToHashSet();
        foreach (var pr in prs) await UpsertPrAsync(pr, ct);
        var droppedPrs = await db.PullRequests.Where(p => p.Status == "active").ToListAsync(ct);
        foreach (var p in droppedPrs.Where(p => !activePrIds.Contains(p.Id) && assigned.ContainsKey(p.RepoId)))
        {
            var fresh = await ado.GetPullRequestAsync(p.RepoId, p.Id, ct);
            if (fresh is null) { p.Status = "abandoned"; p.ClosedOn ??= Now; continue; }
            await UpsertPrAsync(fresh, ct);
            p.ThreadsFetched = false;
        }
        await db.SaveChangesAsync(ct);

        // Pending approvals
        var approvals = await ado.ListPendingApprovalsAsync(ct);
        var pendingIds = approvals.Select(a => a.Id).ToHashSet();
        foreach (var a in approvals)
        {
            var run = a.RunId is null ? null : await db.Runs.FindAsync([a.RunId.Value], ct);
            a.RepoId ??= run?.RepoId;
            a.PipelineId ??= run?.PipelineId;
            if (a.RepoId is not null && !assigned.ContainsKey(a.RepoId)) continue;
            if (a.RunId is not null && (a.StageName is null || a.EnvironmentName is null))
            {
                // Link via the timeline: Checkpoint.Approval record id == approval id → parent checkpoint → stage.
                var recs = await db.TimelineRecords.AsNoTracking().Where(t => t.RunId == a.RunId).ToListAsync(ct);
                var rec = recs.FirstOrDefault(t => t.RecordId.Equals(a.Id, StringComparison.OrdinalIgnoreCase));
                var stage = rec;
                while (stage is not null && stage.Type != "Stage") stage = recs.FirstOrDefault(t => t.RecordId == stage.ParentId);
                stage ??= recs.Where(t => t.Type == "Stage" && t.State is "inProgress" or "pending").OrderBy(t => t.Order).FirstOrDefault();
                if (stage is not null)
                {
                    a.StageName ??= stage.Name;
                    // A gate stage (e.g. "Approval Gate 2") isn't an environment: report the next deploy stage's environment.
                    a.EnvironmentName ??= StageClassifier.MatchEnvironment(stage.Name, stage.Identifier, Cfg.Environments)?.Name
                        ?? recs.Where(t => t.Type == "Stage" && t.Order > stage.Order).OrderBy(t => t.Order)
                            .Select(t => StageClassifier.MatchEnvironment(t.Name, t.Identifier, Cfg.Environments)?.Name).FirstOrDefault(n => n is not null);
                }
            }
            a.AssignedToMe = AssignedToMe(a.ApproversJson, me);
            var existing = await db.Approvals.FindAsync([a.Id], ct);
            if (existing is null) db.Approvals.Add(a);
            else db.Entry(existing).CurrentValues.SetValues(a);
        }
        foreach (var gone in await db.Approvals.Where(a => a.Status == "pending").ToListAsync(ct))
            if (!pendingIds.Contains(gone.Id)) { gone.Status = "completed"; gone.LastModifiedOn = Now; }
        await db.SaveChangesAsync(ct);
        await MarkAsync("hot");
        return $"{active.Count} running, {prs.Count} active PRs, {approvals.Count} pending approvals";
    }

    private static bool AssignedToMe(string approversJson, AdoIdentity me)
    {
        try
        {
            using var doc = JsonDocument.Parse(approversJson);
            foreach (var a in doc.RootElement.EnumerateArray())
            {
                var id = a.TryGetProperty("id", out var v) ? v.GetString() : null;
                var status = a.TryGetProperty("status", out var s) ? s.GetString() : null;
                if (id is null || status is "approved" or "rejected" or "skipped") continue;
                if (id.Equals(me.Id, StringComparison.OrdinalIgnoreCase) || me.GroupIds.Contains(id)) return true;
            }
        }
        catch (JsonException) { }
        return false;
    }

    // ------------------------------------------------------------------ warm

    public async Task<string> WarmAsync(CancellationToken ct, string? onlyRepoId = null)
    {
        var repos = (await AssignedReposAsync(ct)).Where(r => onlyRepoId is null || r.Id == onlyRepoId).ToList();
        if (onlyRepoId is null) await RederiveIfConfigChangedAsync(repos, ct);
        // Least-synced repos first so a limited timeline budget spreads fairly.
        var cursors = await db.SyncCursors.AsNoTracking().Where(c => c.Key.StartsWith("runs:")).ToDictionaryAsync(c => c.Key, c => c.Watermark, ct);
        repos = repos.OrderBy(r => cursors.GetValueOrDefault("runs:" + r.Id) ?? DateTimeOffset.MinValue).ToList();
        int runs = 0, prs = 0, commits = 0;
        // The mock has no rate limit, so it gets a much larger budget to keep local runs quick.
        var factor = ado.IsMock ? 20 : 1;
        var budget = new TimelineBudget(TimelineBudgetPerPass * factor);
        var threadBudget = new TimelineBudget(ThreadBudgetPerPass * factor);
        var projectId = await ProjectIdAsync(ct);
        foreach (var repo in repos)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                runs += await SyncRunsAsync(repo, budget, ct);
                prs += await SyncPrsAsync(repo, null, projectId, ct, threadBudget);
                commits += await SyncCommitsAsync(repo, ct);
                await MarkAsync("warm:" + repo.Id);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException { InnerException: TimeoutException })
            {
                log.LogWarning("Warm sync failed for {Repo}: {Error}", repo.Name, ex.Message);
                await MarkAsync("warm:" + repo.Id, ex);
            }
        }
        if (onlyRepoId is null) await MarkAsync("warm");
        Backlog = budget.Exhausted || threadBudget.Exhausted;
        return $"{runs} runs, {prs} PRs, {commits} commits updated" + (Backlog ? " (API budget reached; continuing next pass)" : "");
    }

    public async Task<string> BackfillAsync(string repoId, CancellationToken ct)
    {
        var repo = await db.Repos.FindAsync([repoId], ct) ?? throw new InvalidOperationException($"Repo {repoId} not found");
        var pipelines = await ado.ListPipelinesAsync(repo.Id, ct);
        foreach (var p in pipelines)
            if (await db.Pipelines.FindAsync([p.Id], ct) is null) db.Pipelines.Add(p);
        await db.SaveChangesAsync(ct);
        // Runs are backfilled by the budgeted warm sync (cursor starts at sync.backfillBuildDays).
        var runs = 0;
        var prs = await SyncPrsAsync(repo, Now.AddDays(-Cfg.Sync.BackfillPrDays), await ProjectIdAsync(ct), ct, new TimelineBudget(0));
        var commits = await SyncCommitsAsync(repo, ct, Now.AddDays(-Cfg.Sync.BackfillPrDays));
        repo.BackfilledAt = Now;
        await db.SaveChangesAsync(ct);
        return $"{repo.Name}: {runs} runs, {prs} PRs, {commits} commits";
    }

    private async Task<string?> ProjectIdAsync(CancellationToken ct)
    {
        var c = await CursorAsync("projectId");
        if (c.Value is null)
        {
            try { c.Value = await ado.GetProjectIdAsync(ct); await db.SaveChangesAsync(ct); }
            catch (HttpRequestException ex) { log.LogWarning("Project id lookup failed: {Error}", ex.Message); }
        }
        return c.Value;
    }

    /// <summary>Max timeline fetches per warm pass (each is 1–2 API calls); the rest continues on the next pass.</summary>
    public const int TimelineBudgetPerPass = 300;

    /// <summary>Max PR thread fetches per warm pass.</summary>
    public const int ThreadBudgetPerPass = 300;

    /// <summary>True when the last warm pass stopped because the timeline budget ran out.</summary>
    public bool Backlog { get; private set; }

    public sealed class TimelineBudget(int max)
    {
        private int _used;
        public bool TryTake() { if (_used >= max) return false; _used++; return true; }
        public bool Exhausted => _used >= max;
    }

    private async Task<int> SyncRunsAsync(Repo repo, TimelineBudget budget, CancellationToken ct)
    {
        var pipelineIds = await db.Pipelines.Where(p => p.RepoId == repo.Id).Select(p => p.Id).ToListAsync(ct);
        if (pipelineIds.Count == 0) return 0;
        var cursor = await CursorAsync("runs:" + repo.Id);
        var min = cursor.Watermark?.AddMinutes(-Cfg.Sync.OverlapMinutes) ?? Now.AddDays(-Cfg.Sync.BackfillBuildDays);
        var runs = await ado.ListRunsAsync(new RunQuery { DefinitionIds = pipelineIds, MinTime = min, StatusFilter = "completed" }, ct);
        var updated = 0;
        DateTimeOffset? processedUntil = null;
        foreach (var run in runs.OrderBy(r => r.FinishTime))
        {
            var changed = await UpsertRunAsync(run, ct);
            var stored = await db.Runs.FindAsync([run.Id], ct);
            var needsTimeline = changed || stored is { TimelineFetched: false } || stored?.TimelineFetchedFor != run.FinishTime;
            if (needsTimeline && run.IsPullRequest)
            {
                // PR validation metrics only need the result; skip the timeline to save API calls.
                if (stored is not null) { stored.TimelineFetched = true; stored.TimelineFetchedFor = run.FinishTime; }
            }
            else if (needsTimeline)
            {
                if (!budget.TryTake()) break;
                await RefreshTimelineAsync(run, repo, ct, force: false);
                updated++;
            }
            processedUntil = run.FinishTime ?? processedUntil;
        }
        if (processedUntil is not null && (cursor.Watermark is null || processedUntil > cursor.Watermark)) cursor.Watermark = processedUntil;
        await db.SaveChangesAsync(ct);
        return updated;
    }

    /// <summary>Returns true if the run is new or its status/result/finish time changed (e.g. a stage retry).</summary>
    private async Task<bool> UpsertRunAsync(Run run, CancellationToken ct)
    {
        var e = await db.Runs.FindAsync([run.Id], ct);
        if (e is null)
        {
            if (run.RepoId.Length == 0) return false;
            db.Runs.Add(run);
            return true;
        }
        var changed = e.Status != run.Status || e.Result != run.Result || e.FinishTime != run.FinishTime;
        e.Status = run.Status; e.Result = run.Result; e.StartTime = run.StartTime; e.FinishTime = run.FinishTime;
        e.BuildNumber = run.BuildNumber; e.SourceBranch = run.SourceBranch; e.SourceVersion = run.SourceVersion; e.Reason = run.Reason;
        e.RequestedFor = run.RequestedFor; e.WebUrl = run.WebUrl; e.RawJson = run.RawJson ?? e.RawJson; e.LastSyncedAt = Now;
        if (run.TemplateRef is not null) { e.TemplateRef = run.TemplateRef; e.TemplateSha = run.TemplateSha; e.TemplateAlias = run.TemplateAlias; }
        return changed;
    }

    private async Task RefreshTimelineAsync(Run run, Repo repo, CancellationToken ct, bool force)
    {
        var records = await ado.GetTimelineAsync(run.Id, ct);
        Detach<TimelineRecord>(t => t.RunId == run.Id);
        if ((await db.Runs.AsNoTracking().Where(r => r.Id == run.Id).Select(r => r.TimelineFetched).FirstOrDefaultAsync(ct)))
            await db.TimelineRecords.Where(t => t.RunId == run.Id).ExecuteDeleteAsync(ct);
        foreach (var r in records.GroupBy(r => r.RecordId).Select(g => g.First())) db.TimelineRecords.Add(r);

        var stored = await db.Runs.FindAsync([run.Id], ct) ?? run;
        stored.TimelineFetched = true;
        stored.TimelineFetchedFor = run.FinishTime;

        // Template resource actually used (covers nested templates). Only recent default-branch runs matter for compliance.
        var recentMain = !run.IsPullRequest && run.QueueTime >= Now.AddDays(-Cfg.Metrics.WindowDays);
        if (recentMain && (stored.TemplateSha is null || force))
        {
            try
            {
                var resources = await ado.GetRunResourcesAsync(run.PipelineId, run.Id, ct);
                var evaluator = new Core.Templates.TemplateComplianceEvaluator(Cfg);
                var tpl = resources.FirstOrDefault(r => evaluator.IsTemplateRepo(r.Alias, r.RepositoryName));
                if (tpl is not null) { stored.TemplateRef = tpl.RefName; stored.TemplateSha = tpl.Version; stored.TemplateAlias = tpl.Alias; }
            }
            catch (HttpRequestException ex) { log.LogDebug("Run resources unavailable for {Run}: {Error}", run.Id, ex.Message); }
        }

        // Flaky tests: only when a job was retried or a test-ish stage failed (keeps API calls down).
        if (run.Status == "completed" && records.Any(r => r.Type == "Job" && r.Attempt > 1))
        {
            try
            {
                var flakes = await ado.ListTestFlakesAsync(run.Id, run.RepoId, ct);
                Detach<TestFlake>(t => t.RunId == run.Id);
                await db.TestFlakes.Where(t => t.RunId == run.Id).ExecuteDeleteAsync(ct);
                db.TestFlakes.AddRange(flakes);
            }
            catch (HttpRequestException ex) { log.LogDebug("Test results unavailable for {Run}: {Error}", run.Id, ex.Message); }
        }

        // Derived deployments (rebuilt from the timeline each time).
        await DeriveDeploymentsAsync(stored, repo, records, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeriveDeploymentsAsync(Run run, Repo repo, IReadOnlyList<TimelineRecord> records, CancellationToken ct)
    {
        var components = await db.Components.Where(c => c.RepoId == repo.Id).ToListAsync(ct);
        var pipelineName = (await db.Pipelines.FindAsync([run.PipelineId], ct))?.Name ?? "";
        var deps = new DeploymentDeriver(Cfg).Derive(run, repo.Name, pipelineName, records, components);
        Detach<Deployment>(d => d.RunId == run.Id);
        await db.Deployments.Where(d => d.RunId == run.Id).ExecuteDeleteAsync(ct);
        db.Deployments.AddRange(deps);
    }

    /// <summary>
    /// Stored deployments are derived from timelines with the config at the time. When the environment / pipeline /
    /// component rules change, re-derive everything once so old rows (e.g. under renamed environments) don't linger.
    /// </summary>
    public async Task RederiveIfConfigChangedAsync(IReadOnlyList<Repo> repos, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new { Cfg.Environments, Cfg.Pipelines, Cfg.Components, v = 1 });
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)))[..16];
        var cursor = await CursorAsync("derive-config");
        if (cursor.Value == hash) return;
        foreach (var repo in repos) await RederiveRepoAsync(repo.Id, ct);
        cursor.Value = hash;
        cursor.LastSuccessAt = Now;
        await db.SaveChangesAsync(ct);
        log.LogInformation("Deployment rules changed; re-derived deployments for {Count} repos", repos.Count);
    }

    /// <summary>Re-derive all deployments for a repo (after components or environment config change).</summary>
    public async Task<int> RederiveRepoAsync(string repoId, CancellationToken ct)
    {
        var repo = await db.Repos.FindAsync([repoId], ct);
        if (repo is null) return 0;
        var runs = await db.Runs.Where(r => r.RepoId == repoId && !r.Reason.Equals("pullRequest")).ToListAsync(ct);
        var runIds = runs.Select(r => r.Id).ToList();
        var all = await db.TimelineRecords.AsNoTracking().Where(t => runIds.Contains(t.RunId)).ToListAsync(ct);
        var byRun = all.GroupBy(t => t.RunId).ToDictionary(g => g.Key, g => (IReadOnlyList<TimelineRecord>)g.ToList());
        foreach (var run in runs)
            if (byRun.TryGetValue(run.Id, out var recs)) await DeriveDeploymentsAsync(run, repo, recs, ct);
        await db.SaveChangesAsync(ct);
        return runs.Count;
    }

    private async Task<int> SyncPrsAsync(Repo repo, DateTimeOffset? since, string? projectId, CancellationToken ct, TimelineBudget threadBudget)
    {
        var cursor = await CursorAsync("prs:" + repo.Id);
        var min = since ?? (cursor.Watermark?.AddMinutes(-Cfg.Sync.OverlapMinutes) ?? Now.AddDays(-Cfg.Sync.BackfillPrDays));
        var closed = await ado.ListClosedPullRequestsAsync(repo.Id, min, ct);
        foreach (var pr in closed) await UpsertPrAsync(pr, ct);
        await db.SaveChangesAsync(ct);

        // Threads/events: closed PRs not yet fetched, and active PRs whose events are older than the warm interval.
        var staleBefore = Now.AddMinutes(-Cfg.Sync.WarmMinutes + 1);
        var needEvents = await db.PullRequests
            .Where(p => p.RepoId == repo.Id && (!p.ThreadsFetched || (p.Status == "active" && p.LastSyncedAt < staleBefore)))
            .ToListAsync(ct);
        // Active PRs first, then most recently closed; threads for older PRs follow on later passes.
        needEvents = needEvents.OrderByDescending(p => p.Status == "active").ThenByDescending(p => p.ClosedOn ?? p.CreatedOn).ToList();
        var processed = 0;
        foreach (var pr in needEvents)
        {
            if (!threadBudget.TryTake()) break;
            processed++;
            var result = await ado.GetPullRequestEventsAsync(repo.Id, pr.Id, pr.AuthorId, ct);
            var bots = new Core.Metrics.PrMetricsCalculator(Cfg, config.WorkingHours);
            Detach<PrEvent>(e => e.PullRequestId == pr.Id);
            await db.PrEvents.Where(e => e.PullRequestId == pr.Id).ExecuteDeleteAsync(ct);
            foreach (var e in result.Events)
            {
                e.IsBot = e.IsBot || bots.IsBot(e.ActorName, e.ActorId);
                e.IsAuthor = e.IsAuthor || (e.ActorId is not null && e.ActorId == pr.AuthorId);
                db.PrEvents.Add(e);
            }
            pr.PublishedOn = result.PublishedOn ?? pr.PublishedOn;
            pr.AuthorIsBot = pr.AuthorIsBot || bots.IsBot(pr.AuthorName, pr.AuthorId);
            if (pr.Status == "active" && projectId is not null && !ado.IsMock)
            {
                try { pr.PolicyStatus = await ado.GetPolicyStatusAsync(projectId, pr.Id, ct); }
                catch (HttpRequestException ex) { log.LogDebug("Policy status unavailable for PR {Pr}: {Error}", pr.Id, ex.Message); }
            }
            pr.ThreadsFetched = true;
            pr.LastSyncedAt = Now;
        }
        var maxClosed = closed.Select(p => p.ClosedOn).Where(c => c is not null).DefaultIfEmpty(null).Max();
        if (maxClosed is not null && (cursor.Watermark is null || maxClosed > cursor.Watermark)) cursor.Watermark = maxClosed;
        await db.SaveChangesAsync(ct);
        return closed.Count + processed;
    }

    private async Task UpsertPrAsync(PullRequest pr, CancellationToken ct)
    {
        var e = await db.PullRequests.FindAsync([pr.Id], ct);
        if (e is null)
        {
            pr.AuthorIsBot = pr.AuthorIsBot || new PrMetricsCalculator(Cfg, config.WorkingHours).IsBot(pr.AuthorName, pr.AuthorId);
            db.PullRequests.Add(pr);
            return;
        }
        var statusChanged = e.Status != pr.Status;
        e.Title = pr.Title; e.Status = pr.Status; e.IsDraft = pr.IsDraft; e.ClosedOn = pr.ClosedOn; e.MergeStatus = pr.MergeStatus;
        e.LastMergeCommit = pr.LastMergeCommit ?? e.LastMergeCommit; e.ReviewersJson = pr.ReviewersJson; e.WebUrl = pr.WebUrl ?? e.WebUrl;
        e.RawJson = pr.RawJson ?? e.RawJson; e.FilesChanged = pr.FilesChanged ?? e.FilesChanged;
        if (pr.PolicyStatus is not null) e.PolicyStatus = pr.PolicyStatus;
        if (statusChanged) e.ThreadsFetched = false;
    }

    private async Task<int> SyncCommitsAsync(Repo repo, CancellationToken ct, DateTimeOffset? since = null)
    {
        if (repo.DefaultBranch is null) return 0;
        var head = await ado.GetBranchHeadAsync(repo.Id, repo.DefaultBranch, ct);
        repo.HeadCheckedAt = Now;
        if (head is null || (head == repo.HeadSha && since is null)) { await db.SaveChangesAsync(ct); return 0; }
        var latest = await db.Commits.Where(c => c.RepoId == repo.Id).OrderByDescending(c => c.Date).Select(c => (DateTimeOffset?)c.Date).FirstOrDefaultAsync(ct);
        var from = since ?? latest?.AddDays(-1) ?? Now.AddDays(-Cfg.Sync.BackfillPrDays);
        var commits = await ado.ListCommitsAsync(repo.Id, repo.DefaultBranch, from, ct);
        var known = (await db.Commits.Where(c => c.RepoId == repo.Id && c.Date >= from).Select(c => c.Sha).ToListAsync(ct)).ToHashSet();
        var added = 0;
        foreach (var c in commits.Where(c => !known.Contains(c.Sha))) { db.Commits.Add(c); added++; }
        repo.HeadSha = head;
        await db.SaveChangesAsync(ct);
        return added;
    }
}

/// <summary>Caches the authenticated identity (and group ids) for an hour.</summary>
public sealed class IdentityCache
{
    private AdoIdentity? _me;
    private DateTimeOffset _at;
    public AdoIdentity? Current => _me;

    public async Task<AdoIdentity> GetAsync(IAdoClient ado, CancellationToken ct)
    {
        if (_me is not null && DateTimeOffset.UtcNow - _at < TimeSpan.FromHours(1)) return _me;
        _me = await ado.GetMeAsync(ct);
        _at = DateTimeOffset.UtcNow;
        return _me;
    }
}

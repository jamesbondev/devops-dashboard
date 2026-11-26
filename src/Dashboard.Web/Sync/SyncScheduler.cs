using Dashboard.Core.Entities;
using Dashboard.Web.Ado;
using Dashboard.Web.Data;
using Dashboard.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Dashboard.Web.Sync;

/// <summary>Wakes the scheduler, serialises writers, and tells the UI that data changed.</summary>
public sealed class SyncSignals
{
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public event Action? DataChanged;
    public long Version { get; private set; }
    public string? Running { get; set; }
    public bool Paused { get; set; }
    public bool WarmBacklog { get; set; }

    public void Wake() => _wake.Release();
    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => _wake.WaitAsync(timeout, ct);
    public void NotifyChanged() { Version++; DataChanged?.Invoke(); }
}

public sealed class SyncScheduler(
    IServiceScopeFactory scopes,
    ConfigService config,
    IClock clock,
    AdoApiStats stats,
    SyncSignals signals,
    ILogger<SyncScheduler> log) : BackgroundService
{
    private DateTimeOffset _lastHot = DateTimeOffset.MinValue;
    private DateTimeOffset _lastWarm = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!signals.Paused) await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Scheduler tick failed"); }
            try { await signals.WaitAsync(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var cfg = config.Current;

        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
            var hasRepos = await db.Repos.AnyAsync(ct);
            var daily = await db.SyncCursors.FindAsync(["daily"], ct);
            var dueDaily = !hasRepos || daily?.LastSuccessAt is null
                           || daily.LastSuccessAt < config.WorkingHours.PreviousLocalHour(now, cfg.Sync.DailyHour);
            if (dueDaily && (daily?.LastErrorAt is null || now - daily.LastErrorAt > TimeSpan.FromMinutes(10)))
                await RunAsync("daily", null, (s, c) => DailyAsync(s, c), ct);
        }

        if (now - _lastHot >= TimeSpan.FromSeconds(cfg.Sync.HotSeconds))
        {
            _lastHot = now;
            await RunAsync("hot", null, (s, c) => s.GetRequiredService<SyncService>().HotAsync(c), ct, record: false);
        }

        var warmDue = now - _lastWarm >= TimeSpan.FromMinutes(cfg.Sync.WarmMinutes)
                      || (signals.WarmBacklog && now - _lastWarm >= TimeSpan.FromSeconds(60));
        if (warmDue)
        {
            _lastWarm = now;
            await RunAsync("warm", null, async (s, c) =>
            {
                var sync = s.GetRequiredService<SyncService>();
                var summary = await sync.WarmAsync(c);
                signals.WarmBacklog = sync.Backlog;
                return summary;
            }, ct);
        }

        // Queued jobs (backfill, single-repo warm, re-derive, manual triggers)
        while (true)
        {
            Job? job;
            await using (var scope = scopes.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
                var t = clock.UtcNow;
                job = await db.Jobs.Where(j => j.Status == JobStatus.Queued && j.RunAfter <= t && !j.Type.StartsWith("audit"))
                    .OrderBy(j => j.RunAfter).FirstOrDefaultAsync(ct);
            }
            if (job is null) break;
            await RunQueuedAsync(job, ct);
        }
    }

    private async Task<string> DailyAsync(IServiceProvider s, CancellationToken ct)
    {
        var sync = s.GetRequiredService<SyncService>();
        // Repo discovery must succeed; the other steps are independent so one bad step (e.g. a misconfigured
        // template repo) doesn't stop policies, checks, backfills and snapshots.
        var parts = new List<string> { await sync.DiscoverReposAsync(ct) };
        async Task Step(string name, Func<Task<string>> step)
        {
            try { parts.Add(await step()); }
            catch (Exception ex) when (ex is not (OperationCanceledException or AdoAuthException))
            {
                log.LogWarning(ex, "Daily step {Step} failed", name);
                parts.Add($"{name} FAILED: {ex.Message}");
                await sync.MarkAsync("daily:" + name, ex);
            }
        }
        await Step("pipelines", () => sync.SyncPipelinesAsync(ct));
        await Step("policies", () => sync.SyncPoliciesAsync(ct));
        await Step("environmentChecks", () => sync.SyncEnvironmentChecksAsync(ct));
        await Step("templates", () => sync.SyncTemplateCommitsAsync(ct));
        var db = s.GetRequiredService<DashboardDbContext>();
        var now = clock.UtcNow;
        foreach (var repo in await db.Repos.Where(r => r.Assigned && r.BackfilledAt == null && !r.IsDisabled).ToListAsync(ct))
            if (!await db.Jobs.AnyAsync(j => j.Type == "backfill" && j.Payload == repo.Id && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running), ct))
                db.Jobs.Add(new Job { Type = "backfill", Payload = repo.Id, Status = JobStatus.Queued, CreatedAt = now, RunAfter = now });
        await db.SaveChangesAsync(ct);
        await sync.MarkAsync("daily");

        var health = s.GetRequiredService<HealthService>();
        await Step("snapshot", () => health.SnapshotAsync(ct));

        // Prune job history older than 14 days.
        var cutoff = now.AddDays(-14);
        await db.Jobs.Where(j => j.CreatedAt < cutoff && j.Status != JobStatus.Queued).ExecuteDeleteAsync(ct);
        return string.Join("; ", parts);
    }

    private async Task RunQueuedAsync(Job job, CancellationToken ct)
    {
        Func<IServiceProvider, CancellationToken, Task<string>> work = job.Type switch
        {
            "backfill" => (s, c) => s.GetRequiredService<SyncService>().BackfillAsync(job.Payload!, c),
            "warm-repo" => (s, c) => s.GetRequiredService<SyncService>().WarmAsync(c, job.Payload),
            "rederive" => async (s, c) => $"{await s.GetRequiredService<SyncService>().RederiveRepoAsync(job.Payload!, c)} runs re-derived",
            "hot" => (s, c) => s.GetRequiredService<SyncService>().HotAsync(c),
            "warm" => (s, c) => s.GetRequiredService<SyncService>().WarmAsync(c),
            "daily" => DailyAsync,
            "snapshot" => (s, c) => s.GetRequiredService<HealthService>().SnapshotAsync(c),
            _ => (_, _) => Task.FromResult($"Unknown job type {job.Type}"),
        };
        await RunAsync(job.Type, job.Payload, work, ct, existingJobId: job.Id);
    }

    private async Task RunAsync(string type, string? payload, Func<IServiceProvider, CancellationToken, Task<string>> work, CancellationToken ct,
        bool record = true, int? existingJobId = null)
    {
        await signals.Gate.WaitAsync(ct);
        signals.Running = type;
        var callsBefore = stats.Calls;
        var tstuBefore = stats.Tstu;
        var started = clock.UtcNow;
        Job? job = null;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
            if (existingJobId is not null) job = await db.Jobs.FindAsync([existingJobId.Value], ct);
            else if (record)
            {
                job = new Job { Type = type, Payload = payload, CreatedAt = started, RunAfter = started };
                db.Jobs.Add(job);
            }
            if (job is not null) { job.Status = JobStatus.Running; job.StartedAt = started; job.Attempts++; await db.SaveChangesAsync(ct); }

            try
            {
                var summary = await work(scope.ServiceProvider, ct);
                if (job is not null) { job.Status = JobStatus.Succeeded; job.Summary = summary; }
                log.LogInformation("Job {Type} {Payload} done: {Summary}", type, payload, summary);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log.LogWarning(ex, "Job {Type} {Payload} failed", type, payload);
                if (job is not null)
                {
                    job.Status = JobStatus.Failed;
                    job.Error = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
                    if (job.Attempts < 3 && ex is not AdoAuthException && existingJobId is not null)
                    {
                        job.Status = JobStatus.Queued;
                        job.RunAfter = clock.UtcNow.AddMinutes(5 * job.Attempts);
                    }
                }
                db.ChangeTracker.Clear();
                if (job is not null) db.Jobs.Update(job);
                try
                {
                    var cursor = await db.SyncCursors.FindAsync([type], ct) ?? db.SyncCursors.Add(new Core.Entities.SyncCursor { Key = type }).Entity;
                    cursor.LastErrorAt = clock.UtcNow;
                    cursor.LastError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                }
                catch (Exception inner) { log.LogDebug(inner, "Could not record cursor error"); }
            }
            if (job is not null)
            {
                job.CompletedAt = clock.UtcNow;
                job.DurationMs = (long)(job.CompletedAt.Value - started).TotalMilliseconds;
                job.ApiCalls = (int)(stats.Calls - callsBefore);
                job.TstuUsed = stats.Tstu - tstuBefore;
            }
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            signals.Running = null;
            signals.Gate.Release();
            signals.NotifyChanged();
        }
    }
}

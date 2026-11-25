using Dashboard.Core.Entities;
using Dashboard.Web.Ado;
using Dashboard.Web.Data;
using Dashboard.Web.Sync;
using Microsoft.EntityFrameworkCore;

namespace Dashboard.Web.Services;

/// <summary>Repo picker and per-repo settings. Assignment queues a backfill job.</summary>
public sealed class RepoAdminService(IServiceScopeFactory scopes, IClock clock, SyncSignals signals, HealthService health)
{
    public async Task<List<Repo>> SearchAsync(string? text, bool assignedOnly = false)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
        var q = db.Repos.AsNoTracking().AsQueryable();
        if (assignedOnly) q = q.Where(r => r.Assigned);
        var list = await q.OrderByDescending(r => r.Assigned).ThenBy(r => r.Name).ToListAsync();
        return string.IsNullOrWhiteSpace(text) ? list : list.Where(r => r.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public async Task SetAssignedAsync(string repoId, bool assigned)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
        var r = await db.Repos.FindAsync(repoId);
        if (r is null) return;
        r.Assigned = assigned;
        if (assigned)
        {
            r.AssignedAt = clock.UtcNow;
            if (r.BackfilledAt is null)
                db.Jobs.Add(new Job { Type = "backfill", Payload = r.Id, Status = JobStatus.Queued, CreatedAt = clock.UtcNow, RunAfter = clock.UtcNow });
        }
        await db.SaveChangesAsync();
        Changed();
    }

    public Task SetPinnedAsync(string repoId, bool v) => Update(repoId, r => r.Pinned = v);
    public Task SetMutedAsync(string repoId, bool v) => Update(repoId, r => r.Muted = v);
    public Task SetTypeAsync(string repoId, RepoType t) => Update(repoId, r => r.Type = t);

    public async Task EnqueueAsync(string type, string? payload = null)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
        if (await db.Jobs.AnyAsync(j => j.Type == type && j.Payload == payload && j.Status == JobStatus.Queued)) { signals.Wake(); return; }
        db.Jobs.Add(new Job { Type = type, Payload = payload, Status = JobStatus.Queued, CreatedAt = clock.UtcNow, RunAfter = clock.UtcNow });
        await db.SaveChangesAsync();
        signals.Wake();
    }

    private async Task Update(string repoId, Action<Repo> change)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
        var r = await db.Repos.FindAsync(repoId);
        if (r is null) return;
        change(r);
        await db.SaveChangesAsync();
        Changed();
    }

    private void Changed()
    {
        health.Invalidate();
        signals.NotifyChanged();
        signals.Wake();
    }
}

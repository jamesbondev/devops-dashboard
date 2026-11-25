using Dashboard.Web.Ado;
using Dashboard.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Dashboard.Web.Services;

public sealed record StatusSnapshot(
    bool Mock, bool MissingPat, bool AuthFailed, bool Throttled, IReadOnlyList<string> ConfigErrors,
    DateTimeOffset? LastHot, DateTimeOffset? LastWarm, DateTimeOffset? LastDaily, long ApiCalls, double Tstu,
    IReadOnlyList<string> RepoNames, IReadOnlyDictionary<string, (DateTimeOffset? Success, DateTimeOffset? ErrorAt, string? Error)> Cursors);

/// <summary>Freshness, auth and config status for banners and the freshness indicator.</summary>
public sealed class StatusService(IServiceScopeFactory scopes, ConfigService config, IAdoClient ado, AdoApiStats stats, IConfiguration env, IClock clock)
{
    private StatusSnapshot? _cached;
    private DateTimeOffset _at;

    public async Task<StatusSnapshot> GetAsync()
    {
        if (_cached is not null && clock.UtcNow - _at < TimeSpan.FromSeconds(5)) return _cached;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
        var cursors = await db.SyncCursors.AsNoTracking().ToListAsync();
        var names = await db.Repos.AsNoTracking().Where(r => r.Assigned).OrderBy(r => r.Name).Select(r => r.Name).ToListAsync();
        DateTimeOffset? Success(string key) => cursors.FirstOrDefault(c => c.Key == key)?.LastSuccessAt;
        _cached = new StatusSnapshot(
            ado.IsMock,
            !ado.IsMock && string.IsNullOrWhiteSpace(env["ADO_PAT"]),
            stats.AuthFailed,
            stats.LastDelayAt is { } d && clock.UtcNow - d < TimeSpan.FromMinutes(10),
            config.LastLoadErrors,
            Success("hot"), Success("warm"), Success("daily"), stats.Calls, stats.Tstu, names,
            cursors.ToDictionary(c => c.Key, c => (c.LastSuccessAt, c.LastErrorAt, c.LastError)));
        _at = clock.UtcNow;
        return _cached;
    }
}

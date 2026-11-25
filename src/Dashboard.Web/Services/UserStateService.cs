using System.Globalization;
using Dashboard.Core.Entities;
using Dashboard.Web.Data;

namespace Dashboard.Web.Services;

/// <summary>Tiny key/value store for per-user conveniences (single user), e.g. the "since you last looked" cursor.</summary>
public sealed class UserStateService(IServiceScopeFactory scopes)
{
    public async Task<string?> GetAsync(string key)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
        return (await db.UserStates.FindAsync(key))?.Value;
    }

    public async Task SetAsync(string key, string value)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
        var e = await db.UserStates.FindAsync(key);
        if (e is null) db.UserStates.Add(new UserState { Key = key, Value = value });
        else e.Value = value;
        await db.SaveChangesAsync();
    }

    public async Task<DateTimeOffset?> GetDateAsync(string key) =>
        DateTimeOffset.TryParse(await GetAsync(key), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    public Task SetDateAsync(string key, DateTimeOffset value) => SetAsync(key, value.ToString("O", CultureInfo.InvariantCulture));
}

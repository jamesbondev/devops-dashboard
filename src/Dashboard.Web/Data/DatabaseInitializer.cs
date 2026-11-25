using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Dashboard.Web.Data;

public static class DatabaseInitializer
{
    public static string DataDirectory(IConfiguration env, IHostEnvironment host) =>
        env["DATA_DIR"] is { Length: > 0 } d ? d : Path.Combine(host.ContentRootPath, "data");

    public static string ConnectionString(string dataDir) =>
        new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataDir, "dashboard.db"), DefaultTimeout = 30, Cache = SqliteCacheMode.Private, Pooling = true }.ToString();

    /// <summary>
    /// Back up (VACUUM INTO) before applying pending migrations, keep the last 7 backups, then migrate and enable WAL.
    /// Copying a live WAL database file is not a safe backup; VACUUM INTO is.
    /// </summary>
    public static async Task InitialiseAsync(IServiceProvider services, string dataDir, ILogger log, CancellationToken ct = default)
    {
        Directory.CreateDirectory(dataDir);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
        var dbFile = Path.Combine(dataDir, "dashboard.db");
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (File.Exists(dbFile) && new FileInfo(dbFile).Length > 0 && pending.Count > 0)
        {
            var backups = Path.Combine(dataDir, "backups");
            Directory.CreateDirectory(backups);
            var target = Path.Combine(backups, $"dashboard-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");
            log.LogInformation("Backing up database to {Target} before {Count} migration(s)", target, pending.Count);
            await db.Database.ExecuteSqlRawAsync("VACUUM INTO {0}", [target], ct);
            foreach (var old in new DirectoryInfo(backups).GetFiles("dashboard-*.db").OrderByDescending(f => f.Name).Skip(7))
                old.Delete();
        }
        await db.Database.MigrateAsync(ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;", ct);
    }

    /// <summary>On-demand backup (Settings page).</summary>
    public static async Task<string> BackupNowAsync(DashboardDbContext db, string dataDir, CancellationToken ct)
    {
        var backups = Path.Combine(dataDir, "backups");
        Directory.CreateDirectory(backups);
        var target = Path.Combine(backups, $"dashboard-manual-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");
        await db.Database.ExecuteSqlRawAsync("VACUUM INTO {0}", [target], ct);
        return target;
    }
}

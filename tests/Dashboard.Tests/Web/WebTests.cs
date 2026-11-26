using Dashboard.Core.Audit;
using Dashboard.Core.Entities;
using Dashboard.Core.Scanners;
using Dashboard.Web;
using Dashboard.Web.Ado;
using Dashboard.Web.Ado.Mock;
using Dashboard.Web.Data;
using Dashboard.Web.Services;
using Dashboard.Web.Sync;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Hosting;

namespace Dashboard.Tests.Web;

public class MockWorldTests
{
    [Fact]
    public void Ids_are_stable_as_history_grows()
    {
        var t1 = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var t2 = t1.AddDays(40);
        var a = new MockWorld();
        var b = new MockWorld();
        var early = a.RunsAt(t1).ToDictionary(r => r.Run.Id, r => (r.Run.QueueTime, r.Run.SourceVersion));
        var later = b.RunsAt(t2).ToDictionary(r => r.Run.Id, r => (r.Run.QueueTime, r.Run.SourceVersion));
        Assert.NotEmpty(early);
        foreach (var (id, v) in early)
        {
            Assert.True(later.TryGetValue(id, out var lv), $"run {id} disappeared");
            Assert.Equal(v, lv);
        }
        var prsEarly = a.PrsAt(t1).Select(p => p.Pr.Id).ToHashSet();
        Assert.Subset(b.PrsAt(t2).Select(p => p.Pr.Id).ToHashSet(), prsEarly);
    }

    [Fact]
    public void Snapshot_shows_in_progress_runs_and_pending_stages()
    {
        var w = new MockWorld();
        var now = new DateTimeOffset(2026, 10, 14, 11, 0, 0, TimeSpan.Zero);
        var running = w.RunsAt(now).Where(r => r.End > now).ToList();
        Assert.NotEmpty(running);
        var (run, timeline) = w.Snapshot(running[0], now);
        Assert.NotEqual("completed", run.Status);
        Assert.Null(run.Result);
        Assert.DoesNotContain(timeline, t => t.FinishTime > now);
    }
}

public class MockContentScanTests
{
    [Fact]
    public async Task Scanner_discovers_skaffold_and_sqlproj_components_in_mock_repos()
    {
        var world = new MockWorld();
        var orders = world.Repos.First(r => r.Name == "orders");
        var dir = Path.Combine(Path.GetTempPath(), "dash-mock-" + Guid.NewGuid().ToString("n"));
        try
        {
            MockRepoContent.Write(dir, orders, new string('a', 40));
            var facts = await new RepoScanner(OfflinePackageMetadataProvider.Instance).ScanAsync(dir, new string('a', 40), CancellationToken.None);
            var names = facts.Components.Select(c => c.Name).Order().ToList();
            Assert.Contains("orders-api", names);
            Assert.Contains("orders-listener", names);
            Assert.Contains(facts.Components, c => c.Kind == "Database");
            Assert.True(facts.Messaging.UsesServiceBus);
            Assert.True(facts.Docs.HasReadme);
            Assert.Contains(facts.Pipelines, p => p.UsesExtends);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Legacy_mock_repo_fails_fact_checks()
    {
        var world = new MockWorld();
        var legacy = world.Repos.First(r => r.Legacy && r.Name == "legacy-crm-sync");
        var dir = Path.Combine(Path.GetTempPath(), "dash-mock-" + Guid.NewGuid().ToString("n"));
        try
        {
            MockRepoContent.Write(dir, legacy, new string('b', 40));
            var facts = await new RepoScanner(OfflinePackageMetadataProvider.Instance).ScanAsync(dir, new string('b', 40), CancellationToken.None);
            Assert.NotEmpty(facts.Dotnet.UnsupportedTargetFrameworks);
            var rubric = RubricLoader.Load(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../audit/rubric")));
            var outcomes = FactCheckEvaluator.Evaluate(facts, rubric.FactChecks);
            Assert.Contains(outcomes, o => o.CheckId == "NET-001" && o.Result == "fail");
            Assert.Contains(outcomes, o => o.CheckId == "DOCK-001" && o.Result is "fail" or "partial");
        }
        finally { Directory.Delete(dir, true); }
    }
}

public sealed class FixedClock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow { get; set; } = now; }

public class SyncServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dash-sync-" + Guid.NewGuid().ToString("n"));

    [Fact]
    public async Task Daily_hot_and_warm_sync_against_the_mock_populate_the_database()
    {
        Directory.CreateDirectory(_dir);
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 14, 11, 0, 0, TimeSpan.Zero));
        var options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(DatabaseInitializer.ConnectionString(_dir)).Options;
        await using var db = new DashboardDbContext(options);
        await db.Database.MigrateAsync();
        var env = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DASHBOARD_CONFIG"] = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../config/dashboard.yaml")),
        }).Build();
        var config = new ConfigService(NullLogger<ConfigService>.Instance, env, new FakeHost());
        var stats = new AdoApiStats();
        var ado = new MockAdoClient(new MockWorld(), clock, stats);
        var sync = new SyncService(db, ado, config, clock, new IdentityCache(), NullLogger<SyncService>.Instance);

        await sync.DiscoverReposAsync(CancellationToken.None);
        Assert.Equal(30, await db.Repos.CountAsync(r => r.Assigned));
        await sync.SyncPipelinesAsync(CancellationToken.None);
        await sync.SyncPoliciesAsync(CancellationToken.None);
        await sync.SyncEnvironmentChecksAsync(CancellationToken.None);
        Assert.True(await db.EnvironmentChecks.AnyAsync(c => c.RequiresMe));
        await sync.HotAsync(CancellationToken.None);
        var passes = 0;
        do { await sync.WarmAsync(CancellationToken.None); passes++; } while (sync.Backlog && passes < 100);
        Assert.False(sync.Backlog, "budgeted warm passes should catch up");

        Assert.True(await db.Runs.CountAsync() > 100);
        Assert.True(await db.TimelineRecords.AnyAsync(t => t.IsPreviousAttempt), "retries should produce previous-attempt records");
        Assert.True(await db.Deployments.AnyAsync(d => d.EnvironmentName == "prod-green" || d.EnvironmentName == "prod-eu"));
        Assert.True(await db.Deployments.AnyAsync(d => d.EnvironmentName == "team" && d.StageName == "testlemur deploy"));
        Assert.True(await db.Pipelines.AnyAsync(p => p.Name.EndsWith("-release")));
        Assert.True(await db.PrEvents.AnyAsync());
        Assert.True(await db.Runs.AnyAsync(r => r.TemplateRef == "refs/heads/next"));
        Assert.True(stats.Calls > 0);

        // Second warm pass is incremental: nothing new without time passing.
        var summary = await sync.WarmAsync(CancellationToken.None);
        Assert.Contains("0 runs", summary);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private sealed class FakeHost : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Dashboard.Web";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}

public class AppBootTests : IClassFixture<AppBootTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<WebEntryPoint>
    {
        public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "dash-app-" + Guid.NewGuid().ToString("n"));

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            Environment.SetEnvironmentVariable("DATA_DIR", DataDir);
            Environment.SetEnvironmentVariable("CLONES_PATH", Path.Combine(DataDir, "clones"));
            Environment.SetEnvironmentVariable("DASHBOARD_CONFIG", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../config/dashboard.yaml")));
            Environment.SetEnvironmentVariable("AUDITOR_URL", "http://127.0.0.1:1");
            builder.UseEnvironment("Development");
        }
    }

    private readonly Factory _factory;
    public AppBootTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task App_boots_in_mock_mode_syncs_and_serves_pages()
    {
        var client = _factory.CreateClient();
        var health = await client.GetAsync("/health");
        Assert.True(health.IsSuccessStatusCode);

        // Wait for the first daily + hot pass.
        var signals = _factory.Services.GetRequiredService<SyncSignals>();
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
            if (await db.SyncCursors.AnyAsync(c => c.Key == "hot" && c.LastSuccessAt != null)) break;
            await Task.Delay(1000);
        }

        foreach (var url in new[] { "/", "/repos", "/repo/orders", "/repo/orders?tab=pipelines", "/gates", "/findings", "/insights", "/weekly", "/jobs", "/settings", "/export/health.md" })
        {
            var resp = await client.GetAsync(url);
            Assert.True(resp.IsSuccessStatusCode, $"{url} → {(int)resp.StatusCode}");
        }
        var now = await client.GetStringAsync("/");
        Assert.Contains("Needs attention", now);
        Assert.Contains("MOCK DATA", now);
    }
}

public class MockGateTests
{
    [Fact]
    public void Mock_release_blocked_by_release_minder_shows_pending_check_mid_wait()
    {
        var w = new MockWorld();
        var horizon = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var blocked = w.RunsAt(horizon).First(r => r.Timeline.Any(t => t.Name == "Release Minder" && (t.FinishTime - t.StartTime) > TimeSpan.FromMinutes(20)));
        var minder = blocked.Timeline.First(t => t.Name == "Release Minder");
        var mid = minder.StartTime!.Value + (minder.FinishTime!.Value - minder.StartTime.Value) / 2;
        var (run, timeline) = w.Snapshot(blocked, mid);
        Assert.Equal("inProgress", run.Status);
        var gate = timeline.Single(t => t.Type == "Stage" && t.Name == "Automated Approval Gate 2");
        Assert.Equal("inProgress", gate.State);
        var pending = Dashboard.Web.Services.NowService.PendingChecks(timeline, gate.RecordId);
        Assert.Contains(("Release Minder", "check"), pending);
        Assert.DoesNotContain(pending, p => p.Name == "Release Governance API");
    }
}

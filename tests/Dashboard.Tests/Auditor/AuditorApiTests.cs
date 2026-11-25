using System.Net;
using System.Net.Http.Json;
using Dashboard.Auditor;
using Dashboard.Auditor.Jobs;
using Dashboard.Core.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Dashboard.Tests.Auditor;

public sealed class AuditorApiTests : IDisposable
{
    private const string Token = "test-token";
    private readonly TempDir _root = new("auditor-api-");
    private readonly WebApplicationFactory<AuditorEntryPoint> _factory;

    public AuditorApiTests()
    {
        _root.Write("x/src/Shop.Domain/Shop.Domain.csproj",
            "<Project><ItemGroup><PackageReference Include=\"Dapper\" /></ItemGroup></Project>");
        _root.Write("x/README.md", "# x");
        _factory = Create(("AUDITOR_ENGINE", "mock"), ("AUDITOR_TOKEN", Token));
    }

    public void Dispose()
    {
        _factory.Dispose();
        _root.Dispose();
    }

    private WebApplicationFactory<AuditorEntryPoint> Create(params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<AuditorEntryPoint>().WithWebHostBuilder(b =>
        {
            b.UseSetting("AUDITOR_ALLOWED_ROOT", _root.Path);
            b.UseSetting("AUDITOR_MOCK_DELAY_MS", "0");
            b.UseSetting("COPILOT_HOME", Path.Combine(_root.Path, ".copilot-home"));
            foreach (var (k, v) in settings)
            {
                b.UseSetting(k, v);
            }
        });

    private HttpClient Client(string? token = Token)
    {
        var c = _factory.CreateClient();
        if (token is not null)
        {
            c.DefaultRequestHeaders.Add("X-Auditor-Token", token);
        }
        return c;
    }

    private AuditRequest Request(string worktree, string id = "audit-1") => new()
    {
        AuditId = id,
        RepoName = "x",
        Sha = "abc123",
        WorktreePath = worktree,
        Checks = [new AuditCheck { Id = "ARCH-002", Title = "Dependency direction" }],
    };

    [Fact]
    public async Task Health_needs_no_token()
    {
        var r = await Client(token: null).GetAsync(new Uri("/health", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("ok", await r.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    [InlineData("test-token ")]
    public async Task Other_endpoints_require_the_token(string? token)
    {
        var c = Client(token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync(new Uri("/status", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync(new Uri("/audits/whatever", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/audits", Request(Path.Combine(_root.Path, "x")))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/text", new TextTaskRequest { Kind = "weekly-brief" })).StatusCode);
    }

    [Fact]
    public async Task Status_reports_mock_engine()
    {
        var s = await Client().GetFromJsonAsync<AuditorStatus>("/status");
        Assert.NotNull(s);
        Assert.Equal("mock", s.Engine);
        Assert.True(s.Ready);
        Assert.Equal("mock-heuristic-v1", s.SelectedModel);
        Assert.Contains("Microsoft.Copilot.SDK", s.SdkVersion, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audit_round_trip()
    {
        var c = Client();
        var post = await c.PostAsJsonAsync("/audits", Request(Path.Combine(_root.Path, "x")));
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
        Assert.Equal("/audits/audit-1", post.Headers.Location?.OriginalString);
        var accepted = await post.Content.ReadFromJsonAsync<AuditStatusResponse>();
        Assert.Equal("audit-1", accepted!.AuditId);

        // JSON is camelCase on the wire.
        var raw = await c.GetStringAsync(new Uri("/audits/audit-1", UriKind.Relative));
        Assert.Contains("\"auditId\":\"audit-1\"", raw, StringComparison.Ordinal);

        var done = await PollAsync(c, "audit-1");
        Assert.Equal("succeeded", done.Status);
        Assert.Equal("mock", done.Engine);
        Assert.Equal("mock-heuristic-v1", done.Model);
        Assert.NotNull(done.PromptHash);
        Assert.NotNull(done.StartedAt);
        Assert.NotNull(done.CompletedAt);
        var check = Assert.Single(done.Result!.Checks);
        Assert.Equal("fail", check.Result);
        Assert.Equal("src/Shop.Domain/Shop.Domain.csproj", check.Evidence.Single().Path);

        // Re-posting the same id is idempotent.
        var again = await c.PostAsJsonAsync("/audits", Request(Path.Combine(_root.Path, "x")));
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        Assert.Equal("succeeded", (await again.Content.ReadFromJsonAsync<AuditStatusResponse>())!.Status);
    }

    [Fact]
    public async Task Audits_run_one_at_a_time_in_order()
    {
        var c = Client();
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Accepted, (await c.PostAsJsonAsync("/audits", Request(Path.Combine(_root.Path, "x"), $"q{i}"))).StatusCode);
        }
        var results = new List<AuditStatusResponse>();
        for (var i = 0; i < 3; i++)
        {
            results.Add(await PollAsync(c, $"q{i}"));
        }
        Assert.All(results, r => Assert.Equal("succeeded", r.Status));
        for (var i = 1; i < 3; i++)
        {
            Assert.True(results[i].StartedAt >= results[i - 1].CompletedAt, "audits overlapped");
        }
    }

    [Theory]
    [InlineData("/etc")]
    [InlineData("/")]
    [InlineData("{root}/../")]
    [InlineData("{root}/x/../../etc")]
    [InlineData("{root}")]
    [InlineData("{root}/missing")]
    [InlineData("")]
    public async Task Rejects_worktrees_outside_or_missing(string worktree)
    {
        var path = worktree.Replace("{root}", _root.Path, StringComparison.Ordinal);
        var r = await Client().PostAsJsonAsync("/audits", Request(path));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("worktreePath", await r.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejects_worktree_symlinked_out_of_root()
    {
        using var outside = new TempDir();
        Directory.CreateSymbolicLink(Path.Combine(_root.Path, "evil"), outside.Path);
        var r = await Client().PostAsJsonAsync("/audits", Request(Path.Combine(_root.Path, "evil")));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Rejects_bad_ids_and_duplicate_checks()
    {
        var c = Client();
        var badId = Request(Path.Combine(_root.Path, "x"), "has spaces/slash");
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/audits", badId)).StatusCode);

        var dup = Request(Path.Combine(_root.Path, "x"), "dup");
        dup.Checks.Add(new AuditCheck { Id = "arch-002" });
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/audits", dup)).StatusCode);
    }

    [Fact]
    public async Task Generates_an_id_when_missing()
    {
        var r = await Client().PostAsJsonAsync("/audits", Request(Path.Combine(_root.Path, "x"), ""));
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<AuditStatusResponse>();
        Assert.False(string.IsNullOrEmpty(body!.AuditId));
    }

    [Fact]
    public async Task Unknown_audit_is_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Client().GetAsync(new Uri("/audits/nope", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task Text_tasks()
    {
        var c = Client();
        var ok = await c.PostAsJsonAsync("/text", new TextTaskRequest { Kind = "weekly-brief", Input = "3 deploys\nmain red for 2h" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = await ok.Content.ReadFromJsonAsync<TextTaskResponse>();
        Assert.Equal("mock", body!.Engine);
        Assert.Contains("main red for 2h", body.Text, StringComparison.Ordinal);

        var bad = await c.PostAsJsonAsync("/text", new TextTaskRequest { Kind = "poem" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Mock_mode_defaults_to_dev_token()
    {
        using var f = Create(("AUDITOR_ENGINE", "mock"), ("AUDITOR_TOKEN", ""));
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Auditor-Token", AuditorOptions.DevToken);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync(new Uri("/status", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public void Copilot_mode_refuses_to_start_without_token()
    {
        using var f = Create(("AUDITOR_ENGINE", "copilot"), ("AUDITOR_TOKEN", ""));
        var ex = Assert.ThrowsAny<Exception>(() => f.CreateClient());
        Assert.Contains("AUDITOR_TOKEN", ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_engine_refuses_to_start()
    {
        using var f = Create(("AUDITOR_ENGINE", "openai"), ("AUDITOR_TOKEN", "x"));
        Assert.ThrowsAny<Exception>(() => f.CreateClient());
    }

    private static async Task<AuditStatusResponse> PollAsync(HttpClient c, string id)
    {
        for (var i = 0; i < 200; i++)
        {
            var s = await c.GetFromJsonAsync<AuditStatusResponse>($"/audits/{id}");
            if (s!.Status is "succeeded" or "failed")
            {
                return s;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException($"audit {id} did not finish");
    }
}

public sealed class AuditJobStoreTests
{
    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Finished_jobs_expire_after_24_hours()
    {
        var time = new FakeTime();
        var store = new AuditJobStore(new AuditorOptions(), time);
        var (job, created) = store.Enqueue(new AuditRequest { AuditId = "a" });
        Assert.True(created);
        Assert.False(store.Enqueue(new AuditRequest { AuditId = "a" }).Created);

        store.MarkRunning(job, "mock");
        store.MarkSucceeded(job, new AuditResult(), "t", "m", "h");

        time.Now += TimeSpan.FromHours(23);
        Assert.Equal(0, store.PurgeExpired());
        Assert.NotNull(store.Get("a"));

        time.Now += TimeSpan.FromHours(1);
        Assert.Equal(1, store.PurgeExpired());
        Assert.Null(store.Get("a"));
    }

    [Fact]
    public void Running_jobs_are_kept_past_retention()
    {
        var time = new FakeTime();
        var store = new AuditJobStore(new AuditorOptions(), time);
        var (job, _) = store.Enqueue(new AuditRequest { AuditId = "slow" });
        store.MarkRunning(job, "mock");
        time.Now += TimeSpan.FromHours(30);
        Assert.Equal(0, store.PurgeExpired());
        Assert.Equal("running", store.Get("slow")!.ToResponse().Status);
    }
}

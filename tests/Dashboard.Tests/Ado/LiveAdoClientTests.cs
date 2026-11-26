using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Dashboard.Core.Entities;
using Dashboard.Web.Ado;
using Dashboard.Web.Services;
using Dashboard.Web.Sync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dashboard.Tests.Ado;

/// <summary>
/// Parses payloads shaped like the documented Azure DevOps Services responses (api-version 7.1) through a fake
/// HttpMessageHandler, with the real <see cref="ThrottlingHandler"/> in the pipeline.
/// </summary>
public sealed class LiveAdoClientTests : IDisposable
{
    private const string Org = "https://dev.azure.com/contoso";
    private const string Proj = Org + "/Platform";

    public LiveAdoClientTests() => ThrottlingHandler.ResetGate();

    public void Dispose() => ThrottlingHandler.ResetGate();

    // ------------------------------------------------------------------ paging

    [Fact]
    public async Task ListRuns_follows_the_continuation_token_header()
    {
        const string token = "2026-11-20T10:20:00.0000000Z|1202";
        var fake = new FakeAdo()
            .On("/Platform/_apis/build/builds", req => req.RequestUri!.Query.Contains("continuationToken=", StringComparison.Ordinal)
                ? Json(Fixture("builds-page2.json"))
                : Json(Fixture("builds-page1.json"), continuation: token));
        var (client, _) = Create(fake);

        var runs = await client.ListRunsAsync(new RunQuery { DefinitionIds = [42], MinTime = new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero) }, default);

        Assert.Equal([1201, 1202, 1203], runs.Select(r => r.Id));
        Assert.Equal(2, fake.Requests.Count);
        var first = fake.Requests[0].Query;
        Assert.Contains("definitions=42", first);
        Assert.Contains("queryOrder=finishTimeAscending", first);
        Assert.Contains("statusFilter=completed", first);
        Assert.Contains("minTime=2026-11-20T00%3A00%3A00Z", first);
        Assert.Contains("api-version=7.1", first);
        Assert.Contains("continuationToken=" + Uri.EscapeDataString(token), fake.Requests[1].Query);

        var r = runs[0];
        Assert.Equal(42, r.PipelineId);
        Assert.Equal("6b1f0e2a-0000-4000-8000-0000000000b1", r.RepoId);
        Assert.Equal("refs/heads/main", r.SourceBranch);
        Assert.Equal("succeeded", r.Result);
        Assert.Equal("Alice Example", r.RequestedFor);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 9, 12, 44, TimeSpan.Zero).AddTicks(101000), r.FinishTime);
        Assert.EndsWith("_build/results?buildId=1201", r.WebUrl);
        Assert.True(runs[1].IsPullRequest);
        Assert.Equal("partiallySucceeded", runs[1].Result);
    }

    [Fact]
    public async Task ListRuns_for_unfinished_builds_filters_minTime_on_queue_time()
    {
        var fake = new FakeAdo().On("/Platform/_apis/build/builds", _ => Json("""{"count":0,"value":[]}"""));
        var (client, _) = Create(fake);

        await client.ListRunsAsync(new RunQuery { DefinitionIds = [42], MinTime = DateTimeOffset.UtcNow, StatusFilter = "all" }, default);

        // minTime applies to the field named by queryOrder; finishTime would drop builds that have not finished.
        Assert.Contains("queryOrder=queueTimeAscending", fake.Requests.Single().Query);
        Assert.DoesNotContain("statusFilter", fake.Requests.Single().Query);
    }

    // ------------------------------------------------------------------ timeline

    [Fact]
    public async Task Timeline_includes_previous_attempt_records_and_their_children()
    {
        var fake = new FakeAdo()
            .On("/Platform/_apis/build/builds/1203/timeline/b1111111-0000-4000-8000-000000000000", _ => Json(Fixture("timeline-attempt1.json")))
            .On("/Platform/_apis/build/builds/1203/timeline", _ => Json(Fixture("timeline.json")));
        var (client, _) = Create(fake);

        var records = await client.GetTimelineAsync(1203, default);

        Assert.Equal(2, fake.Requests.Count);
        Assert.Equal(5, records.Count(r => !r.IsPreviousAttempt));
        var previous = records.Where(r => r.IsPreviousAttempt).ToList();
        // The referenced job and its task; the unrelated stage in the same attempt timeline is not pulled in.
        Assert.Equal(["c0000000-0000-4000-8000-000000000003", "c0000000-0000-4000-8000-000000000004"], previous.Select(r => r.RecordId).Order());

        var oldJob = previous.Single(r => r.Type == "Job");
        Assert.Equal(1, oldJob.Attempt);
        Assert.Equal("failed", oldJob.Result);
        Assert.Equal("Build.Test.__default", oldJob.Identifier);
        Assert.Equal("Bash exited with code '1'.", oldJob.ErrorMessage);
        Assert.NotNull(oldJob.ErrorFingerprint);
        var oldTask = previous.Single(r => r.Type == "Task");
        Assert.StartsWith("Error: The process", oldTask.ErrorMessage); // first *error* issue, not the warning

        var job = records.Single(r => r.Type == "Job" && !r.IsPreviousAttempt);
        Assert.Equal(2, job.Attempt);
        Assert.Equal("succeeded", job.Result);
        Assert.Equal(1203, job.RunId);
        Assert.Null(records.Single(r => r.Type == "Checkpoint").StartTime);
        Assert.Null(records.Single(r => r.Type == "Task" && !r.IsPreviousAttempt).ErrorMessage); // warnings only
    }

    [Fact]
    public async Task Timeline_for_a_build_without_one_is_empty()
    {
        var fake = new FakeAdo().On("/Platform/_apis/build/builds/77/timeline", _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var (client, _) = Create(fake);
        Assert.Empty(await client.GetTimelineAsync(77, default));
    }

    // ------------------------------------------------------------------ approvals

    [Fact]
    public async Task Pending_approvals_map_steps_to_the_approvers_json_SyncService_reads()
    {
        var fake = new FakeAdo().On("/Platform/_apis/pipelines/approvals", _ => Json(Fixture("approvals-pending.json")));
        var (client, _) = Create(fake);

        var approvals = await client.ListPendingApprovalsAsync(default);

        var q = fake.Requests.Single().Query;
        Assert.Contains("state=pending", q);
        Assert.Contains("$expand=steps", q);
        Assert.Contains("top=500", q);
        Assert.DoesNotContain("$top", q); // Approvals - Query takes `top`, not `$top`

        Assert.Equal(2, approvals.Count);
        var a = approvals[0];
        Assert.Equal("0f3c6a1e-0000-4000-8000-00000000aa01", a.Id);
        Assert.Equal(1203, a.RunId);          // pipeline.owner.id is the build id
        Assert.Equal(42, a.PipelineId);       // pipeline.id is the definition id, as a string
        Assert.Equal("pending", a.Status);
        Assert.Equal(1, a.MinRequiredApprovers);
        Assert.Equal("Check the change ticket before approving prod.", a.Instructions);
        Assert.EndsWith("_build/results?buildId=1203", a.WebUrl);

        using var doc = JsonDocument.Parse(a.ApproversJson);
        var steps = doc.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, steps.Count);
        Assert.Equal("9a8b7c6d-0000-4000-8000-0000000000f1", steps[0].GetProperty("id").GetString());
        Assert.Equal("pending", steps[0].GetProperty("status").GetString());
        Assert.Equal("approved", steps[1].GetProperty("status").GetString());

        // Feed it through SyncService's own rule: a pending step for one of my groups is mine; an approved one is not.
        var viaGroup = new AdoIdentity("me", "Me", new HashSet<string>(["9A8B7C6D-0000-4000-8000-0000000000F1"], StringComparer.OrdinalIgnoreCase));
        var carol = new AdoIdentity("f3d2e4a6-0000-4000-8000-0000000000c3", "Carol", new HashSet<string>());
        var dave = new AdoIdentity("11112222-0000-4000-8000-0000000000d4", "Dave", new HashSet<string>());
        Assert.True(AssignedToMe(a.ApproversJson, viaGroup));
        Assert.False(AssignedToMe(a.ApproversJson, carol));
        Assert.True(AssignedToMe(approvals[1].ApproversJson, dave));
        Assert.False(AssignedToMe(approvals[1].ApproversJson, viaGroup));
    }

    private static bool AssignedToMe(string json, AdoIdentity me)
    {
        var m = typeof(SyncService).GetMethod("AssignedToMe", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("SyncService.AssignedToMe not found");
        return (bool)m.Invoke(null, [json, me])!;
    }

    [Fact]
    public async Task GetMe_expands_group_membership_from_memberOf_descriptors()
    {
        var fake = new FakeAdo()
            .On("/contoso/_apis/connectionData", _ => Json("""
                {"authenticatedUser":{"id":"d1b0c2e4-0000-4000-8000-0000000000a1","descriptor":"Microsoft.IdentityModel.Claims.ClaimsIdentity;72f988bf-0000-4000-8000-000000000000\\alice@contoso.com","subjectDescriptor":"aad.ZDFiMGMy","providerDisplayName":"Alice Example","customDisplayName":null,"isActive":true,"properties":{}},
                 "authorizedUser":{"id":"d1b0c2e4-0000-4000-8000-0000000000a1","providerDisplayName":"Alice Example"},
                 "instanceId":"2c895908-04e0-4952-89fd-54b0046d6288","deploymentId":"00000000-0000-0000-0000-000000000000","deploymentType":"hosted"}
                """))
            .On("/contoso/_apis/identities", req => req.RequestUri!.Query.Contains("identityIds=", StringComparison.Ordinal)
                ? Json("""
                    {"count":1,"value":[{"id":"d1b0c2e4-0000-4000-8000-0000000000a1","descriptor":"Microsoft.IdentityModel.Claims.ClaimsIdentity;72f988bf\\alice@contoso.com",
                     "providerDisplayName":"Alice Example","isActive":true,"isContainer":false,"members":[],
                     "memberOf":["Microsoft.TeamFoundation.Identity;S-1-9-1551374245-1204400969-2402986413-2179408616-3-111","Microsoft.TeamFoundation.Identity;S-1-9-1551374245-1204400969-2402986413-2179408616-3-222"],
                     "memberIds":[],"resourceVersion":2,"metaTypeId":0}]}
                    """)
                : Json("""
                    {"count":2,"value":[
                     {"id":"9a8b7c6d-0000-4000-8000-0000000000f1","descriptor":"Microsoft.TeamFoundation.Identity;S-1-9-1551374245-1204400969-2402986413-2179408616-3-111","providerDisplayName":"[Platform]\\Release Approvers","isContainer":true},
                     {"id":"9a8b7c6d-0000-4000-8000-0000000000f2","descriptor":"Microsoft.TeamFoundation.Identity;S-1-9-1551374245-1204400969-2402986413-2179408616-3-222","providerDisplayName":"[contoso]\\Project Collection Valid Users","isContainer":true}]}
                    """));
        var (client, _) = Create(fake);

        var me = await client.GetMeAsync(default);

        Assert.Equal("d1b0c2e4-0000-4000-8000-0000000000a1", me.Id);
        Assert.Equal("Alice Example", me.DisplayName);
        Assert.Equal(2, me.GroupIds.Count);
        Assert.Contains("9a8b7c6d-0000-4000-8000-0000000000f1", me.GroupIds);
        Assert.All(fake.Requests.Skip(1), r => Assert.Equal("vssps.dev.azure.com", r.Host));
        Assert.Contains("queryMembership=Expanded", fake.Requests[1].Query);
    }

    // ------------------------------------------------------------------ PR threads

    [Fact]
    public async Task Pull_request_threads_yield_votes_comments_pushes_and_published_time()
    {
        var fake = new FakeAdo().On("/Platform/_apis/git/repositories/6b1f0e2a-0000-4000-8000-0000000000b1/pullRequests/77/threads",
            _ => Json(Fixture("pr-threads.json")));
        var (client, _) = Create(fake);

        var result = await client.GetPullRequestEventsAsync("6b1f0e2a-0000-4000-8000-0000000000b1", 77, "e2c1d3f5-0000-4000-8000-0000000000b2", default);

        var ev = result.Events.OrderBy(e => e.Time).ToList();
        // First publish (IsDraftUpdate with CodeReviewIsDraftNowSet = 0), not the re-publish after going back to draft.
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 9, 30, 0, TimeSpan.Zero), result.PublishedOn);

        var votes = ev.Where(e => e.Kind == PrEventKind.Vote).ToList();
        var vote = Assert.Single(votes); // the text reply inside the vote thread is a comment, not a second vote
        Assert.Equal(10, vote.Vote);
        Assert.Equal("f3d2e4a6-0000-4000-8000-0000000000c3", vote.ActorId);
        Assert.Equal("Carol Example", vote.ActorName);
        Assert.False(vote.IsAuthor);

        var comments = ev.Where(e => e.Kind == PrEventKind.Comment).ToList();
        var bot = Assert.Single(comments, c => c.IsBot);
        Assert.Equal("Project Collection Build Service (contoso)", bot.ActorName);
        var human = comments.Where(c => !c.IsBot).ToList();
        // Review comment, author reply, reply inside the vote thread. Deleted comment and deleted thread skipped.
        Assert.Equal(["Carol Example", "Bob Example", "Dave Example"], human.Select(c => c.ActorName));
        Assert.Equal([false, true, false], human.Select(c => c.IsAuthor));

        // The RefUpdate system comment is authored by the TFS service identity; the pusher comes from the properties.
        var push = Assert.Single(ev, e => e.Kind == PrEventKind.Pushed);
        Assert.Equal("e2c1d3f5-0000-4000-8000-0000000000b2", push.ActorId);
        Assert.True(push.IsAuthor);
        Assert.All(ev, e => Assert.Equal(77, e.PullRequestId));
        Assert.Equal(6, ev.Count); // policy-status system thread and draft toggles are not events
    }

    // ------------------------------------------------------------------ test results / run resources

    [Fact]
    public async Task Test_flakes_follow_continuation_and_key_on_the_full_test_identity()
    {
        var fake = new FakeAdo().On("/contoso/Platform/_apis/testresults/resultsbybuild", req =>
            req.RequestUri!.Query.Contains("continuationToken=", StringComparison.Ordinal)
                ? Json(Fixture("testresults-page2.json"))
                : Json(Fixture("testresults-page1.json"), continuation: "b2Zmc2V0PTM="));
        var (client, _) = Create(fake);

        var flakes = await client.ListTestFlakesAsync(1203, "repo", default);

        Assert.Equal(2, fake.Requests.Count);
        Assert.All(fake.Requests, r => Assert.Equal("vstmr.dev.azure.com", r.Host));
        Assert.Contains("buildId=1203", fake.Requests[0].Query);
        Assert.Contains("api-version=7.1-preview.1", fake.Requests[0].Query);
        // Only OrderTests.Constructor_ThrowsOnNull failed and then passed. The same-titled InvoiceTests method and the
        // data-driven cases (rate 5 always failed, rate 7 always passed) are not flaky.
        var flake = Assert.Single(flakes);
        Assert.Equal("Orders.Tests.OrderTests.Constructor_ThrowsOnNull", flake.TestName);
        Assert.Equal(1203, flake.RunId);
        Assert.True(flake.Flaky);
    }

    [Fact]
    public async Task Run_resources_resolve_azure_repos_ids_to_names()
    {
        var fake = new FakeAdo()
            .On("/Platform/_apis/pipelines/42/runs/1203", _ => Json("""
                {"_links":{"web":{"href":"https://dev.azure.com/contoso/Platform/_build/results?buildId=1203"}},
                 "templateParameters":{},"pipeline":{"url":"...","id":42,"revision":7,"name":"orders-service-ci","folder":"\\orders"},
                 "state":"completed","result":"succeeded","createdDate":"2026-11-21T08:00:00Z","finishedDate":"2026-11-21T08:20:00Z",
                 "resources":{"repositories":{
                   "self":{"repository":{"id":"6b1f0e2a-0000-4000-8000-0000000000b1","type":"azureReposGit"},"refName":"refs/heads/main","version":"bb22cc33dd44ee55ff6677889900aabbccddeeff"},
                   "tpl":{"repository":{"id":"7c2e1f3b-0000-4000-8000-0000000000e5","type":"azureReposGit"},"refName":"refs/heads/next","version":"0123456789abcdef0123456789abcdef01234567"},
                   "gh":{"repository":{"fullName":"contoso/tools","connection":{"id":"c0ffee00-0000-4000-8000-000000000000"},"type":"gitHub"},"refName":"refs/heads/main","version":"fedcba9876543210fedcba9876543210fedcba98"}}},
                 "id":1203,"name":"20261121.1"}
                """))
            .On("/cbi/_apis/git/repositories/cbi-yaml-templates", _ => Json("""
                {"id":"7c2e1f3b-0000-4000-8000-0000000000e5","name":"cbi-yaml-templates","project":{"id":"5d1b7a9c-0000-4000-8000-00000000a001","name":"cbi"},"defaultBranch":"refs/heads/main"}
                """));
        var (client, _) = Create(fake);

        var res = await client.GetRunResourcesAsync(42, 1203, default);

        var tpl = res.Single(r => r.Alias == "tpl");
        Assert.Equal("cbi-yaml-templates", tpl.RepositoryName);
        Assert.Equal("refs/heads/next", tpl.RefName);
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", tpl.Version);
        Assert.Equal("contoso/tools", res.Single(r => r.Alias == "gh").RepositoryName);
        Assert.Null(res.Single(r => r.Alias == "self").RepositoryName); // not in the cache and not the template repo
    }

    // ------------------------------------------------------------------ auth

    [Fact]
    public async Task Unauthorized_throws_AdoAuthException_and_flags_stats()
    {
        var fake = new FakeAdo().On("/", _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("", Encoding.UTF8, "text/plain"),
        });
        var (client, stats) = Create(fake);

        await Assert.ThrowsAsync<AdoAuthException>(() => client.GetProjectIdAsync(default));
        Assert.True(stats.AuthFailed);
        Assert.Equal("Basic", fake.Requests.Single().Authorization?.Scheme);
        Assert.Equal(Convert.ToBase64String(Encoding.ASCII.GetBytes(":fake-pat")), fake.Requests.Single().Authorization?.Parameter);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "text/html")]
    [InlineData(HttpStatusCode.NonAuthoritativeInformation, "text/html")]
    [InlineData(HttpStatusCode.OK, "application/octet-stream")]
    public async Task Html_sign_in_page_throws_AdoAuthException(HttpStatusCode status, string mediaType)
    {
        const string signIn = """

            <!DOCTYPE html>
            <html lang="en-US">
            <head><title>Azure DevOps Services | Sign In</title></head>
            <body><form id="signInForm" action="https://app.vssps.visualstudio.com/_signin">...</form></body>
            </html>
            """;
        var fake = new FakeAdo().On("/", _ => new HttpResponseMessage(status) { Content = new StringContent(signIn, Encoding.UTF8, mediaType) });
        var (client, stats) = Create(fake);

        var ex = await Assert.ThrowsAsync<AdoAuthException>(() => client.ListRepositoriesAsync(default));
        Assert.Contains("PAT", ex.Message);
        Assert.True(stats.AuthFailed);
    }

    [Fact]
    public async Task Forbidden_is_an_HttpRequestException_not_an_auth_failure()
    {
        var fake = new FakeAdo().On("/", _ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var (client, stats) = Create(fake);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.ListPoliciesAsync(default));
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.False(stats.AuthFailed);
    }

    // ------------------------------------------------------------------ throttling

    [Fact]
    public async Task Too_many_requests_with_Retry_After_is_retried_after_the_delay()
    {
        var calls = 0;
        var fake = new FakeAdo().On("/contoso/_apis/projects/Platform", _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("""{"message":"TF400733: rate limited"}""", Encoding.UTF8, "application/json") };
                r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
                r.Headers.Add("X-RateLimit-Resource", "Core");
                r.Headers.Add("X-RateLimit-Limit", "200");
                r.Headers.Add("X-RateLimit-Remaining", "0");
                return r;
            }
            var ok = Json("""{"id":"5d1b7a9c-0000-4000-8000-00000000a001","name":"Platform","state":"wellFormed"}""");
            ok.Headers.Add("X-RateLimit-Limit", "200");
            ok.Headers.Add("X-RateLimit-Remaining", "12.5");
            return ok;
        });
        var (client, stats) = Create(fake);
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE"); // "12.5" must still parse as twelve and a half
        try
        {
            var sw = Stopwatch.StartNew();
            var id = await client.GetProjectIdAsync(default);
            sw.Stop();

            Assert.Equal("5d1b7a9c-0000-4000-8000-00000000a001", id);
            Assert.Equal(2, calls);
            Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(900), $"waited only {sw.Elapsed}");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"waited {sw.Elapsed}");
            Assert.Equal(2, stats.Calls);
            Assert.Equal(1, stats.Throttled);
            Assert.Equal(12.5, stats.LastRemaining);
            Assert.Equal(200, stats.LastLimit);
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    [Fact]
    public async Task Too_many_requests_gives_up_after_the_retry_limit()
    {
        var fake = new FakeAdo().On("/", _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return r;
        });
        // Retry-After: 0 falls back to exponential backoff; keep it out of this test by capping the wait.
        var max = ThrottlingHandler.MaxWait;
        ThrottlingHandler.MaxWait = TimeSpan.FromMilliseconds(10);
        try
        {
            var (client, _) = Create(fake);
            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetProjectIdAsync(default));
            Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
            Assert.Equal(ThrottlingHandler.MaxAttempts, fake.Requests.Count);
        }
        finally { ThrottlingHandler.MaxWait = max; }
    }

    // ------------------------------------------------------------------ plumbing

    private static (LiveAdoClient Client, AdoApiStats Stats) Create(HttpMessageHandler inner)
    {
        var env = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ADO_PAT"] = "fake-pat",
            ["ADO_MODE"] = "live",
            ["DASHBOARD_CONFIG"] = Path.Combine(RepoRoot, "config", "dashboard.yaml"),
        }).Build();
        var config = new ConfigService(NullLogger<ConfigService>.Instance, env, new FakeHost());
        Assert.False(config.Current.AzureDevOps.IsMock);
        var stats = new AdoApiStats();
        var http = new HttpClient(new ThrottlingHandler(stats, NullLogger<ThrottlingHandler>.Instance) { InnerHandler = inner });
        return (new LiveAdoClient(http, config, stats, NullLogger<LiveAdoClient>.Instance, env), stats);
    }

    private static HttpResponseMessage Json(string body, string? continuation = null)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        r.Content.Headers.ContentType!.Parameters.Add(new System.Net.Http.Headers.NameValueHeaderValue("api-version", "7.1"));
        if (continuation is not null) r.Headers.Add("x-ms-continuationtoken", continuation);
        return r;
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(RepoRoot, "tests", "fixtures", "ado", name));

    private static string RepoRoot
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "config", "dashboard.yaml")) && Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures", "ado")))
                    return dir.FullName;
            throw new DirectoryNotFoundException("Could not locate the repo root above " + AppContext.BaseDirectory);
        }
    }

    private sealed record SeenRequest(string Host, string Path, string Query, System.Net.Http.Headers.AuthenticationHeaderValue? Authorization);

    /// <summary>Routes by longest matching path prefix; unmatched requests get a 404 like ADO.</summary>
    private sealed class FakeAdo : HttpMessageHandler
    {
        private readonly List<(string Path, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _routes = [];
        private readonly List<SeenRequest> _requests = [];

        public IReadOnlyList<SeenRequest> Requests { get { lock (_requests) return _requests.ToList(); } }

        public FakeAdo On(string pathPrefix, Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _routes.Add((pathPrefix, respond));
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method); // read-only client
            var uri = request.RequestUri!;
            lock (_requests) _requests.Add(new SeenRequest(uri.Host, uri.AbsolutePath, uri.Query, request.Headers.Authorization));
            var route = _routes.Where(r => uri.AbsolutePath.EndsWith(r.Path, StringComparison.OrdinalIgnoreCase) || r.Path == "/")
                .OrderByDescending(r => r.Path.Length).FirstOrDefault();
            var response = route.Respond?.Invoke(request)
                           ?? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"not found"}""", Encoding.UTF8, "application/json") };
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed class FakeHost : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Dashboard.Web";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}

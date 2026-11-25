using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dashboard.Core.Entities;
using Dashboard.Core.Metrics;

namespace Dashboard.Web.Ado.Mock;

/// <summary>
/// A deterministic synthetic Azure DevOps organisation. Everything is generated from fixed seeds relative to a
/// fixed epoch, so data is stable across restarts and "grows" in real time (runs finish, approvals wait, PRs age).
/// </summary>
public sealed class MockWorld
{
    public static readonly DateTimeOffset Epoch = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    public const string MeId = "00000000-0000-4000-8000-00000000me01";
    public const string MeName = "Tech Lead (you)";
    public const string ReleaseApproversGroupId = "00000000-0000-4000-8000-0000000grp01";

    public static readonly (string Id, string Name)[] Team =
    [
        ("u-01", "Avery Patel"), ("u-02", "Blake Chen"), ("u-03", "Casey Okafor"), ("u-04", "Devon Smith"),
        ("u-05", "Emery Novak"), ("u-06", "Finley Garcia"), ("u-07", "Harper Ito"), ("u-08", "Jordan Kowalski"),
    ];

    public static readonly string[] SharedErrors =
    [
        "error NU1301: Unable to load the service index for source https://pkgs.dev.azure.com/contoso/_packaging/shared-feed/nuget/v3/index.json.",
        "##[error]The job running on agent Azure Pipelines 12 ran longer than the maximum time of 60 minutes.",
        "##[error]Docker build failed: failed to fetch anonymous token: unexpected status: 429 Too Many Requests",
    ];

    public static readonly string[] LocalErrors =
    [
        "error CS8602: Dereference of a possibly null reference. [/home/vsts/work/1/s/src/Api/Program.cs]",
        "Test run failed: 1 test(s) failed: IntegrationTests.Should_publish_event_when_order_created",
        "##[error]Bash exited with code '1'.",
        "error MSB3073: The command \"dotnet format --verify-no-changes\" exited with code 2.",
        "Test run failed: 2 test(s) failed: RepositoryTests.Get_returns_null_for_unknown_id",
    ];

    public static readonly string[] DeployErrors =
    [
        "Error: UPGRADE FAILED: timed out waiting for the condition (deployment rollout)",
        "##[error]ImagePullBackOff: back-off pulling image",
        "*** An error occurred during deployment plan generation. Deployment cannot continue. (dacpac)",
    ];

    public sealed record MockComponent(string Name, ComponentKind Kind);

    public sealed record MockRepo(
        int Index, string Id, string Name, RepoType Type, bool TeamOwned, MockComponent[] Components,
        double FailRate, double FlakeShare, string TemplateBranch, bool MinReviewers, bool BuildPolicy,
        double PrsPerDay, bool Dormant, string Tfm, bool Legacy, bool DeploysToProd)
    {
        public int CiPipelineId => 100 + Index;
        public int? NightlyPipelineId => Index % 4 == 0 ? 300 + Index : null;
    }

    public sealed class MockRun
    {
        public required Run Run { get; init; }
        public required List<TimelineRecord> Timeline { get; init; }
        public List<(string ApprovalId, string StageName, string Env, DateTimeOffset Created, DateTimeOffset Done, bool Mine, string? Reason)> Approvals { get; } = [];
        public List<string> FlakyTests { get; } = [];
        public DateTimeOffset End { get; set; }
    }

    public sealed class MockPr
    {
        public required PullRequest Pr { get; init; }
        public required List<PrEvent> Events { get; init; }
        public DateTimeOffset? PublishedOn { get; init; }
        public DateTimeOffset? CompletesAt { get; init; }
        public bool Abandons { get; init; }
        public string MergeSha { get; init; } = "";
        public List<(DateTimeOffset At, int Vote, string ReviewerId)> VoteTimes { get; } = [];
    }

    public IReadOnlyList<MockRepo> Repos { get; }
    private readonly object _lock = new();
    private DateTimeOffset _generatedUntil = DateTimeOffset.MinValue;
    private List<MockPr> _prs = [];
    private List<MockRun> _runs = [];
    private List<TemplateCommit> _templateCommits = [];
    private Dictionary<int, MockRun> _runById = [];
    private Dictionary<int, MockPr> _prById = [];
    private Dictionary<string, List<MockPr>> _prsByRepo = [];

    public MockWorld()
    {
        Repos = BuildRepos();
    }

    // -------------------------------------------------------------- repos

    private static List<MockRepo> BuildRepos()
    {
        var specs = new (string Name, RepoType Type, bool Team, string Comps, string Template, string Tfm, bool Legacy, bool Dormant)[]
        {
            ("orders", RepoType.Service, true, "api,listener,db", "main", "net10.0", false, false),
            ("payments", RepoType.Service, true, "api,listener,db", "main", "net10.0", false, false),
            ("billing", RepoType.Service, true, "api,db", "main", "net10.0", false, false),
            ("invoicing", RepoType.Service, true, "api,listener", "main", "net8.0", false, false),
            ("customer-profile", RepoType.Service, true, "api,db", "main", "net10.0", false, false),
            ("notifications", RepoType.Service, true, "api,listener", "next", "net10.0", false, false),
            ("inventory", RepoType.Service, true, "api,listener,db", "main", "net10.0", false, false),
            ("pricing", RepoType.Service, true, "api", "main", "net10.0", false, false),
            ("catalogue", RepoType.Service, true, "api,db", "main", "net8.0", false, false),
            ("search-indexer", RepoType.Service, true, "listener", "main", "net10.0", false, false),
            ("shipping", RepoType.Service, true, "api,listener,db", "main", "net10.0", false, false),
            ("returns", RepoType.Service, true, "api,db", "feature/new-stages", "net10.0", false, false),
            ("loyalty", RepoType.Service, true, "api", "main", "net10.0", false, false),
            ("ledger", RepoType.Service, true, "api,listener,db", "main", "net10.0", false, false),
            ("fraud-checks", RepoType.Service, true, "listener", "main", "net10.0", false, false),
            ("kyc", RepoType.Service, true, "api,db", "main", "net9.0", false, false),
            ("partner-api", RepoType.Service, true, "api", "main", "net10.0", false, false),
            ("webhooks", RepoType.Service, true, "api,listener", "main", "net10.0", false, false),
            ("scheduler", RepoType.Service, true, "listener", "main", "net10.0", false, false),
            ("email-gateway", RepoType.Service, true, "listener", "main", "net10.0", false, false),
            ("sms-gateway", RepoType.Service, true, "listener", "main", "net10.0", false, true),
            ("document-store", RepoType.Service, true, "api,db", "main", "net10.0", false, false),
            ("data-export", RepoType.Service, true, "listener,db", "main", "net8.0", false, false),
            ("reporting", RepoType.Service, true, "api,db", "main", "net10.0", false, false),
            ("legacy-crm-sync", RepoType.Service, true, "listener,db", "main", "net6.0", true, false),
            ("shared-messaging", RepoType.Library, true, "", "main", "net10.0", false, false),
            ("shared-observability", RepoType.Library, true, "", "main", "net10.0", false, false),
            ("infra-core", RepoType.Infra, true, "", "main", "", false, false),
            ("hiera-data", RepoType.Infra, true, "", "main", "", false, false),
            ("feature-flags", RepoType.Service, true, "api", "main", "net10.0", false, false),
            // Other teams' repos (visible in the picker, not assigned by default).
            ("pipeline-templates", RepoType.Library, false, "", "main", "", false, false),
            ("frontend-portal", RepoType.Service, false, "api", "main", "net10.0", false, false),
            ("mobile-bff", RepoType.Service, false, "api", "main", "net10.0", false, false),
            ("analytics-etl", RepoType.Service, false, "listener", "main", "net8.0", false, false),
            ("identity-bridge", RepoType.Service, false, "api", "main", "net10.0", false, false),
            ("dev-portal", RepoType.Service, false, "api", "main", "net10.0", false, false),
            ("terraform-modules", RepoType.Infra, false, "", "main", "", false, false),
            ("platform-tools", RepoType.Library, false, "", "main", "net10.0", false, false),
            ("chaos-tests", RepoType.Library, false, "", "main", "net10.0", false, true),
            ("archived-monolith", RepoType.Service, false, "api,db", "main", "net48", true, true),
        };

        var list = new List<MockRepo>();
        for (var i = 0; i < specs.Length; i++)
        {
            var s = specs[i];
            var rng = Rng("repo", s.Name);
            var comps = s.Comps.Length == 0 ? [] : s.Comps.Split(',').Select(c => c switch
            {
                "db" => new MockComponent(Pascal(s.Name) + ".Database", ComponentKind.Database),
                "listener" => new MockComponent(s.Name + "-listener", ComponentKind.AksService),
                _ => new MockComponent(s.Name + "-api", ComponentKind.AksService),
            }).ToArray();
            var fail = s.Name switch { "legacy-crm-sync" => 0.25, "notifications" => 0.14, "kyc" => 0.12, _ => 0.03 + rng.NextDouble() * 0.07 };
            var flake = s.Name switch { "inventory" => 0.8, "payments" => 0.5, "search-indexer" => 0.6, _ => rng.NextDouble() * 0.35 };
            list.Add(new MockRepo(i, StableGuid("repo:" + s.Name), s.Name, s.Type, s.Team, comps, fail, flake, s.Template,
                MinReviewers: s.Name is not ("legacy-crm-sync" or "hiera-data"),
                BuildPolicy: s.Name is not ("legacy-crm-sync" or "feature-flags"),
                PrsPerDay: s.Dormant ? 0 : s.Type == RepoType.Infra ? 0.3 : 0.6 + rng.NextDouble() * 0.9,
                Dormant: s.Dormant, Tfm: s.Tfm, Legacy: s.Legacy,
                DeploysToProd: s.Type == RepoType.Service));
        }
        return list;
    }

    // -------------------------------------------------------------- generation

    /// <summary>Ensures history is generated up to (now + 1 day) so in-flight items exist.</summary>
    public void EnsureGenerated(DateTimeOffset now)
    {
        lock (_lock)
        {
            if (now < _generatedUntil) return;
            var until = now.AddHours(36);
            _templateCommits = GenerateTemplateCommits(until);
            _prs = [];
            _runs = [];
            foreach (var repo in Repos) GenerateRepo(repo, until);
            _runById = _runs.ToDictionary(r => r.Run.Id);
            _prById = _prs.ToDictionary(p => p.Pr.Id);
            _prsByRepo = _prs.GroupBy(p => p.Pr.RepoId).ToDictionary(g => g.Key, g => g.ToList());
            _generatedUntil = now.AddHours(6);
        }
    }

    private static List<TemplateCommit> GenerateTemplateCommits(DateTimeOffset until)
    {
        var list = new List<TemplateCommit>();
        foreach (var (branch, everyDays) in new[] { ("main", 5.0), ("next", 2.0) })
        {
            var rng = Rng("template", branch);
            var t = Epoch.AddDays(-30);
            var n = 0;
            while (t < until)
            {
                list.Add(new TemplateCommit
                {
                    Sha = Sha($"tpl:{branch}:{n}"), Branch = branch, Date = t, Author = "Platform Team",
                    Message = branch == "main" ? TemplateMessages[n % TemplateMessages.Length] : $"Experiment: {TemplateMessages[(n + 3) % TemplateMessages.Length]}",
                });
                n++;
                t = t.AddDays(everyDays * (0.6 + rng.NextDouble() * 0.8));
            }
        }
        return list;
    }

    private static readonly string[] TemplateMessages =
    [
        "Add SBOM generation to build stage", "Bump skaffold to 2.14", "Add dead-letter monitor gate to prod stage",
        "Cache NuGet packages per lockfile", "Require dacpac drift report before prod", "Pin dotnet SDK 10.0.1xx",
        "Add deployment window check for sim/prod", "Split test stage into unit/integration jobs", "Publish code coverage",
    ];

    private void GenerateRepo(MockRepo repo, DateTimeOffset until)
    {
        var rng = Rng("prs", repo.Name);
        var prSeq = 0;
        var day = Epoch;
        var dormantFrom = repo.Dormant ? Epoch.AddDays(25) : DateTimeOffset.MaxValue;
        var activeTimes = new List<(DateTimeOffset Merge, string Sha, int PrId)>();
        while (day < until)
        {
            if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && day < dormantFrom)
            {
                var expected = repo.PrsPerDay;
                var count = (int)expected + (rng.NextDouble() < expected - (int)expected ? 1 : 0);
                for (var k = 0; k < count; k++)
                {
                    var created = day.AddHours(8 + rng.NextDouble() * 8);
                    if (created > until) break;
                    var pr = GeneratePr(repo, ++prSeq, created, rng);
                    _prs.Add(pr);
                    // PR validation run(s)
                    GenerateRun(repo, repo.CiPipelineId, "pullRequest", created.AddMinutes(2), Sha($"{repo.Name}:pr{pr.Pr.Id}:1"), pr.Pr.Id, repo.CiPipelineId * 100000 + prSeq * 2);
                    if (rng.NextDouble() < 0.4)
                        GenerateRun(repo, repo.CiPipelineId, "pullRequest", created.AddHours(1 + rng.NextDouble() * 20), Sha($"{repo.Name}:pr{pr.Pr.Id}:2"), pr.Pr.Id, repo.CiPipelineId * 100000 + prSeq * 2 + 1);
                    if (pr.CompletesAt is { } done && !pr.Abandons)
                        activeTimes.Add((done, pr.MergeSha, pr.Pr.Id));
                }
            }
            day = day.AddDays(1);
        }

        // CI/CD runs on each merge.
        foreach (var (merge, sha, prId) in activeTimes.OrderBy(x => x.Merge))
            GenerateRun(repo, repo.CiPipelineId, "individualCI", merge.AddMinutes(1), sha, null, repo.CiPipelineId * 100000 + 50000 + (prId - 1000 - repo.Index * 2000));

        // Nightly scheduled run on some repos (separate pipeline, build only).
        if (repo.NightlyPipelineId is { } nightly && !repo.Dormant)
            for (var d = Epoch; d < until; d = d.AddDays(1))
                if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                    GenerateRun(repo, nightly, "schedule", d.AddHours(1), Sha($"{repo.Name}:nightly:{d:yyyyMMdd}"), null, nightly * 100000 + (int)(d - Epoch).TotalDays + 1);
    }

    private MockPr GeneratePr(MockRepo repo, int seq, DateTimeOffset created, Random rng)
    {
        var id = 1000 + repo.Index * 2000 + seq;
        var bot = rng.NextDouble() < 0.06;
        var author = bot ? ("u-bot", "renovate[bot]") : Team[rng.Next(Team.Length)];
        var draft = !bot && rng.NextDouble() < 0.12;
        DateTimeOffset? published = draft ? created.AddHours(2 + rng.NextDouble() * 30) : null;
        var start = published ?? created;
        var reviewers = Team.Where(t => t.Id != author.Item1).OrderBy(_ => rng.Next()).Take(2).ToList();
        var slowReview = rng.NextDouble() < 0.12;
        var firstReview = start.AddHours(slowReview ? 30 + rng.NextDouble() * 90 : 0.3 + rng.NextDouble() * 10);
        var events = new List<PrEvent>();
        var mpr = new MockPr
        {
            Pr = new PullRequest(), // placeholder; replaced below
            Events = events,
        };
        var votes = new List<(DateTimeOffset, int, string)>();
        events.Add(new PrEvent { PullRequestId = id, ActorId = reviewers[0].Id, ActorName = reviewers[0].Name, Kind = PrEventKind.Comment, Time = firstReview });
        var approveAt = firstReview.AddHours(rng.NextDouble() * 6);
        var waitingForAuthor = rng.NextDouble() < 0.15;
        if (waitingForAuthor)
        {
            votes.Add((firstReview.AddMinutes(5), -5, reviewers[0].Id));
            events.Add(new PrEvent { PullRequestId = id, ActorId = author.Item1, ActorName = author.Item2, IsAuthor = true, Kind = PrEventKind.Pushed, Time = approveAt.AddHours(2) });
            approveAt = approveAt.AddHours(4 + rng.NextDouble() * 20);
        }
        votes.Add((approveAt, 10, reviewers[0].Id));
        if (rng.NextDouble() < 0.6) votes.Add((approveAt.AddHours(rng.NextDouble() * 4), rng.NextDouble() < 0.8 ? 10 : 5, reviewers[1].Id));
        foreach (var (at, vote, rid) in votes)
            events.Add(new PrEvent { PullRequestId = id, ActorId = rid, ActorName = Team.First(t => t.Id == rid).Name, Kind = PrEventKind.Vote, Vote = vote, Time = at });
        if (bot)
            events.Insert(0, new PrEvent { PullRequestId = id, ActorId = "svc", ActorName = "Project Collection Build Service", IsBot = true, Kind = PrEventKind.Comment, Time = created.AddMinutes(3) });

        var abandons = rng.NextDouble() < 0.05;
        // A few PRs stall and are eventually abandoned (stale-PR signal), the rest merge after approval.
        var stalls = rng.NextDouble() < 0.04;
        if (stalls) abandons = true;
        DateTimeOffset? completes = stalls ? created.AddDays(20 + rng.NextDouble() * 20) : votes.Max(v => v.Item1).AddHours(0.2 + rng.NextDouble() * 8);
        var title = PrTitles[rng.Next(PrTitles.Length)] + (bot ? " (dependency update)" : "");
        var pr = new PullRequest
        {
            Id = id,
            RepoId = repo.Id,
            Title = bot ? $"Update dependency {Packages[rng.Next(Packages.Length)]}" : title,
            AuthorId = author.Item1,
            AuthorName = author.Item2,
            AuthorIsBot = bot,
            CreatedOn = created,
            IsDraft = draft,
            SourceBranch = $"refs/heads/feature/{seq}",
            TargetBranch = "refs/heads/main",
            WebUrl = $"https://dev.azure.com/contoso/Platform/_git/{repo.Name}/pullrequest/{id}",
            FilesChanged = 1 + rng.Next(30),
            ReviewersJson = "[]",
            MergeStatus = rng.NextDouble() < 0.05 ? "conflicts" : "succeeded",
            PolicyStatus = rng.NextDouble() < 0.1 ? "rejected" : "approved",
        };
        var result = new MockPr
        {
            Pr = pr, Events = events, PublishedOn = published, CompletesAt = completes, Abandons = abandons,
            MergeSha = Sha($"{repo.Name}:merge:{id}"),
        };
        result.VoteTimes.AddRange(votes);
        var reviewerJson = reviewers.Select((r, i) => new ReviewerInfo { Id = r.Id, DisplayName = r.Name, IsRequired = i == 0 }).ToList();
        pr.ReviewersJson = JsonSerializer.Serialize(reviewerJson, JsonDefaults.Web);
        return result;
    }

    private static readonly string[] PrTitles =
    [
        "Add idempotency key to message handler", "Fix null reference in mapping", "Upgrade to Azure.Messaging.ServiceBus 7.20",
        "Add readiness probe endpoint", "Refactor repository to use Dapper", "Add pagination to list endpoint",
        "Handle dead-lettered messages", "Tidy up configuration options", "Add integration tests for checkout",
        "Bump Microsoft.Data.SqlClient", "Add structured logging to listener", "Migrate to central package management",
        "Add retry policy for outbound HTTP", "Remove unused feature flag", "Split read and write models",
    ];

    private static readonly string[] Packages = ["Azure.Identity", "Serilog.AspNetCore", "Polly", "xunit", "FluentValidation", "Dapper"];

    private void GenerateRun(MockRepo repo, int pipelineId, string reason, DateTimeOffset queued, string sha, int? prId, int id)
    {
        var seq = id % 100000;
        var rng = Rng("run", id.ToString());
        var isPr = reason == "pullRequest";
        var isNightly = reason == "schedule";
        var tpl = repo.TemplateBranch;
        var tplSha = _templateCommits.Where(c => c.Branch == (tpl is "main" or "next" ? tpl : "main") && c.Date <= queued)
            .OrderByDescending(c => c.Date).FirstOrDefault()?.Sha ?? Sha("tpl:none");
        if (tpl is not ("main" or "next")) tplSha = Sha($"tpl:{tpl}");

        var run = new Run
        {
            Id = id, PipelineId = pipelineId, RepoId = repo.Id, BuildNumber = $"{queued:yyyyMMdd}.{seq % 100}",
            SourceBranch = isPr ? $"refs/pull/{prId}/merge" : "refs/heads/main", SourceVersion = sha, Reason = reason,
            QueueTime = queued, RequestedFor = isPr || reason == "individualCI" ? Team[rng.Next(Team.Length)].Name : "Scheduled",
            WebUrl = $"https://dev.azure.com/contoso/Platform/_build/results?buildId={id}",
            TemplateRef = $"refs/heads/{tpl}", TemplateSha = tplSha, TemplateAlias = "templates",
        };
        var timeline = new List<TimelineRecord>();
        var mrun = new MockRun { Run = run, Timeline = timeline };
        var t = queued.AddSeconds(20 + rng.Next(120));
        run.StartTime = t;
        var order = 0;

        // Build + Test (build stages). Possible failure, possibly retried (flaky).
        var buildFails = rng.NextDouble() < repo.FailRate;
        var failStage = rng.NextDouble() < 0.5 ? "Build" : "Test";
        var flaky = !isPr && (buildFails ? rng.NextDouble() < repo.FlakeShare : rng.NextDouble() < repo.FlakeShare * 0.12);
        if (flaky) buildFails = true;
        var useShared = rng.NextDouble() < 0.3;
        var error = useShared ? SharedErrors[rng.Next(SharedErrors.Length)] : $"[{repo.Name}] " + LocalErrors[rng.Next(LocalErrors.Length)].Replace("IntegrationTests", Pascal(repo.Name) + "Tests");
        var buildFailedFinal = false;
        foreach (var stageName in new[] { "Build", "Test" })
        {
            var dur = stageName == "Build" ? 4 + rng.NextDouble() * 8 : 3 + rng.NextDouble() * 6;
            var stageFails = buildFails && stageName == failStage;
            if (buildFailedFinal) { AddStage(timeline, id, stageName, ++order, null, null, "skipped", 1, false, stageName == "Build" ? "Compile" : "UnitTests"); continue; }
            if (stageFails && flaky)
            {
                // attempt 1 failed (previous attempt), attempt 2 succeeded after a manual retry
                AddStage(timeline, id, stageName, ++order, t, t.AddMinutes(dur), "failed", 1, true, stageName == "Build" ? "Compile" : "UnitTests", error);
                var retryAt = t.AddMinutes(dur + 8 + rng.NextDouble() * 40);
                AddStage(timeline, id, stageName, order, retryAt, retryAt.AddMinutes(dur), "succeeded", 2, false, stageName == "Build" ? "Compile" : "UnitTests");
                if (stageName == "Test") mrun.FlakyTests.Add($"IntegrationTests.{PrTitles[rng.Next(PrTitles.Length)].Replace(' ', '_')}");
                t = retryAt.AddMinutes(dur);
            }
            else if (stageFails)
            {
                AddStage(timeline, id, stageName, ++order, t, t.AddMinutes(dur), "failed", 1, false, stageName == "Build" ? "Compile" : "UnitTests", error);
                t = t.AddMinutes(dur);
                buildFailedFinal = true;
            }
            else
            {
                var res = rng.NextDouble() < 0.005 ? "succeededWithIssues" : "succeeded";
                AddStage(timeline, id, stageName, ++order, t, t.AddMinutes(dur), res, 1, false, stageName == "Build" ? "Compile" : "UnitTests",
                    res == "succeededWithIssues" ? $"[{repo.Name}] Code coverage below threshold (continueOnError)" : null);
                if (res == "succeededWithIssues") buildFailedFinal = true;
                t = t.AddMinutes(dur);
            }
        }

        // Deployment stages for CI runs on main of service repos.
        if (!isPr && !isNightly && repo.Type == RepoType.Service && repo.Components.Length > 0)
        {
            var goesToSim = rng.NextDouble() < 0.45;
            var goesToProd = goesToSim && rng.NextDouble() < 0.75;
            foreach (var (env, stage) in new[] { ("team", "Deploy_team"), ("staging", "Deploy_staging"), ("sim", "Deploy_sim"), ("prod", "Deploy_prod") })
            {
                var reach = !buildFailedFinal && env switch { "sim" => goesToSim, "prod" => goesToProd, _ => true };
                if (!reach) { AddDeployStage(mrun, repo, stage, ++order, null, "skipped", rng, env, ref t); continue; }
                var failed = AddDeployStage(mrun, repo, stage, ++order, t, null, rng, env, ref t);
                if (failed) buildFailedFinal = true;
            }
        }

        mrun.End = t;
        run.FinishTime = t;
        run.Result = timeline.Any(r => r.Type == "Stage" && !r.IsPreviousAttempt && r.Result == "failed") ? "failed"
            : timeline.Any(r => r.Type == "Stage" && !r.IsPreviousAttempt && r.Result is "succeededWithIssues") ? "partiallySucceeded" : "succeeded";
        run.Status = "completed";
        _runs.Add(mrun);
    }

    private static void AddStage(List<TimelineRecord> tl, int runId, string name, int order, DateTimeOffset? start, DateTimeOffset? end,
        string result, int attempt, bool previous, string jobName, string? error = null)
    {
        var stageId = StableGuid($"{runId}:{name}:stage:{attempt}");
        var phaseId = StableGuid($"{runId}:{name}:phase:{attempt}");
        var jobId = StableGuid($"{runId}:{name}:job:{attempt}");
        tl.Add(new TimelineRecord { RunId = runId, RecordId = stageId, Type = "Stage", Name = name, Identifier = name, Attempt = attempt, IsPreviousAttempt = previous, Result = result, State = "completed", StartTime = start, FinishTime = end, Order = order });
        tl.Add(new TimelineRecord { RunId = runId, RecordId = phaseId, ParentId = stageId, Type = "Phase", Name = jobName, Identifier = $"{name}.{jobName}", Attempt = attempt, IsPreviousAttempt = previous, Result = result, State = "completed", StartTime = start, FinishTime = end });
        tl.Add(new TimelineRecord
        {
            RunId = runId, RecordId = jobId, ParentId = phaseId, Type = "Job", Name = jobName, Identifier = $"{name}.{jobName}.__default", Attempt = attempt, IsPreviousAttempt = previous,
            Result = result, State = "completed", StartTime = start, FinishTime = end,
            ErrorMessage = error, ErrorFingerprint = error is null ? null : FailureFingerprint.Compute(error),
        });
    }

    /// <summary>Returns true if the stage failed.</summary>
    private bool AddDeployStage(MockRun mrun, MockRepo repo, string stage, int order, DateTimeOffset? start, string? forcedResult, Random rng, string env, ref DateTimeOffset t)
    {
        var runId = mrun.Run.Id;
        var tl = mrun.Timeline;
        var stageId = StableGuid($"{runId}:{stage}:stage:1");
        if (forcedResult == "skipped" || start is null)
        {
            tl.Add(new TimelineRecord { RunId = runId, RecordId = stageId, Type = "Stage", Name = stage, Identifier = stage, Attempt = 1, Result = "skipped", State = "completed", Order = order });
            return false;
        }
        var s = start.Value;
        var cursor = s;
        // Approval checkpoint on sim/prod
        if (env is "sim" or "prod")
        {
            var ckId = StableGuid($"{runId}:{stage}:checkpoint");
            var approvalId = StableGuid($"{runId}:{stage}:approval");
            var escalate = rng.NextDouble() < 0.12;
            string? reason = escalate ? EscalationReasons[rng.Next(EscalationReasons.Length)] : null;
            var wait = escalate ? 0.5 + rng.NextDouble() * 6 : 0.1 + rng.NextDouble() * (env == "prod" ? 5 : 2);
            var done = cursor.AddHours(wait);
            tl.Add(new TimelineRecord { RunId = runId, RecordId = ckId, ParentId = stageId, Type = "Checkpoint", Name = "Checkpoint", Attempt = 1, Result = "succeeded", State = "completed", StartTime = cursor, FinishTime = done });
            tl.Add(new TimelineRecord { RunId = runId, RecordId = approvalId, ParentId = ckId, Type = "Checkpoint.Approval", Name = escalate ? "Tech lead approval" : "Release approval", Attempt = 1, Result = "succeeded", State = "completed", StartTime = cursor, FinishTime = done });
            mrun.Approvals.Add((approvalId, stage, env, cursor, done, escalate, reason));
            cursor = done;
        }
        var deployFails = rng.NextDouble() < (repo.Legacy ? 0.08 : 0.025);
        var phaseId = StableGuid($"{runId}:{stage}:phase:1");
        tl.Add(new TimelineRecord { RunId = runId, RecordId = phaseId, ParentId = stageId, Type = "Phase", Name = "Deploy", Identifier = $"{stage}.Deploy", Attempt = 1, Result = "succeeded", State = "completed", StartTime = cursor });
        var hasDb = repo.Components.Any(c => c.Kind == ComponentKind.Database);
        var jobs = new List<string> { "SkaffoldDeploy" };
        if (hasDb) jobs.Insert(0, "DeployDacpac");
        var failJob = deployFails ? jobs[rng.Next(jobs.Count)] : null;
        foreach (var job in jobs)
        {
            var dur = job == "DeployDacpac" ? 2 + rng.NextDouble() * 4 : 3 + rng.NextDouble() * 6;
            var fails = job == failJob;
            var err = fails ? (job == "DeployDacpac" ? $"[{repo.Name}] " + DeployErrors[2] : rng.NextDouble() < 0.5 ? DeployErrors[1] : $"[{repo.Name}] " + DeployErrors[0]) : null;
            tl.Add(new TimelineRecord
            {
                RunId = runId, RecordId = StableGuid($"{runId}:{stage}:{job}"), ParentId = phaseId, Type = "Job", Name = job, Identifier = $"{stage}.Deploy.{job}",
                Attempt = 1, Result = fails ? "failed" : "succeeded", State = "completed", StartTime = cursor, FinishTime = cursor.AddMinutes(dur),
                ErrorMessage = err, ErrorFingerprint = err is null ? null : FailureFingerprint.Compute(err),
            });
            cursor = cursor.AddMinutes(dur);
            if (fails) break;
        }
        tl.Add(new TimelineRecord { RunId = runId, RecordId = stageId, Type = "Stage", Name = stage, Identifier = stage, Attempt = 1, Result = deployFails ? "failed" : "succeeded", State = "completed", StartTime = s, FinishTime = cursor, Order = order });
        t = cursor.AddMinutes(0.5);
        return deployFails;
    }

    private static readonly string[] EscalationReasons =
    [
        "Outside deployment window — tech lead approval required",
        "Service Bus dead-letter count above threshold on target namespace",
        "Ongoing incident INC-2041 on Production — tech lead approval required",
    ];

    // -------------------------------------------------------------- views at time "now"

    public IReadOnlyList<MockRun> RunsAt(DateTimeOffset now)
    {
        EnsureGenerated(now);
        lock (_lock) return _runs.Where(r => r.Run.QueueTime <= now).ToList();
    }

    public IReadOnlyList<MockPr> PrsAt(DateTimeOffset now)
    {
        EnsureGenerated(now);
        lock (_lock) return _prs.Where(p => p.Pr.CreatedOn <= now).ToList();
    }

    public MockRun? FindRun(int id, DateTimeOffset now)
    {
        EnsureGenerated(now);
        lock (_lock) return _runById.TryGetValue(id, out var r) && r.Run.QueueTime <= now ? r : null;
    }

    public MockPr? FindPr(int id, DateTimeOffset now)
    {
        EnsureGenerated(now);
        lock (_lock) return _prById.TryGetValue(id, out var p) && p.Pr.CreatedOn <= now ? p : null;
    }

    public IReadOnlyList<MockPr> PrsForRepo(string repoId, DateTimeOffset now)
    {
        EnsureGenerated(now);
        lock (_lock) return _prsByRepo.TryGetValue(repoId, out var l) ? l.Where(p => p.Pr.CreatedOn <= now).ToList() : [];
    }

    public IReadOnlyList<TemplateCommit> TemplateCommitsAt(DateTimeOffset now)
    {
        EnsureGenerated(now);
        lock (_lock) return _templateCommits.Where(c => c.Date <= now).ToList();
    }

    /// <summary>Snapshot of a run as it would look at <paramref name="now"/> (in progress / pending approval / done).</summary>
    public (Run Run, List<TimelineRecord> Timeline) Snapshot(MockRun m, DateTimeOffset now)
    {
        var run = Clone(m.Run);
        var records = m.Timeline.Select(Clone).ToList();
        if (m.End <= now) return (run, records);
        run.Status = run.StartTime <= now ? "inProgress" : "notStarted";
        run.Result = null;
        run.FinishTime = null;
        foreach (var r in records)
        {
            if (r.StartTime is null) { if (r.Result == "skipped") { r.Result = null; r.State = "pending"; } continue; }
            if (r.FinishTime <= now) continue;
            if (r.StartTime <= now) { r.State = "inProgress"; r.Result = null; r.FinishTime = null; }
            else { r.State = "pending"; r.Result = null; r.StartTime = null; r.FinishTime = null; }
        }
        return (run, records);
    }

    public Run RunSnapshot(MockRun m, DateTimeOffset now)
    {
        var run = Clone(m.Run);
        if (m.End <= now) return run;
        run.Status = run.StartTime <= now ? "inProgress" : "notStarted";
        run.Result = null;
        run.FinishTime = null;
        return run;
    }

    public PullRequest PrSnapshot(MockPr m, DateTimeOffset now, out List<PrEvent> events)
    {
        var pr = Clone(m.Pr);
        events = m.Events.Where(e => e.Time <= now).ToList();
        pr.IsDraft = m.Pr.IsDraft && (m.PublishedOn is null || m.PublishedOn > now);
        if (m.CompletesAt is { } done && done <= now)
        {
            pr.Status = m.Abandons ? "abandoned" : "completed";
            pr.ClosedOn = done;
            pr.LastMergeCommit = m.MergeSha;
            pr.MergeStatus = "succeeded";
            pr.PolicyStatus = "approved";
        }
        else pr.Status = "active";
        var reviewers = JsonSerializer.Deserialize<List<ReviewerInfo>>(m.Pr.ReviewersJson, JsonDefaults.Web) ?? [];
        foreach (var r in reviewers)
            r.Vote = m.VoteTimes.Where(v => v.ReviewerId == r.Id && v.At <= now).OrderBy(v => v.At).Select(v => v.Vote).LastOrDefault();
        pr.ReviewersJson = JsonSerializer.Serialize(reviewers, JsonDefaults.Web);
        if (pr.Status == "active" && pr.PolicyStatus == "approved" && events.All(e => e.Kind != PrEventKind.Vote)) pr.PolicyStatus = "running";
        return pr;
    }

    // -------------------------------------------------------------- helpers

    private static Run Clone(Run r) => new()
    {
        Id = r.Id, PipelineId = r.PipelineId, RepoId = r.RepoId, BuildNumber = r.BuildNumber, SourceBranch = r.SourceBranch, SourceVersion = r.SourceVersion,
        Reason = r.Reason, Status = r.Status, Result = r.Result, QueueTime = r.QueueTime, StartTime = r.StartTime, FinishTime = r.FinishTime,
        RequestedFor = r.RequestedFor, WebUrl = r.WebUrl, TemplateRef = r.TemplateRef, TemplateSha = r.TemplateSha, TemplateAlias = r.TemplateAlias,
        LastSyncedAt = DateTimeOffset.UtcNow,
    };

    private static TimelineRecord Clone(TimelineRecord r) => new()
    {
        RunId = r.RunId, RecordId = r.RecordId, ParentId = r.ParentId, Type = r.Type, Name = r.Name, Identifier = r.Identifier, Attempt = r.Attempt,
        IsPreviousAttempt = r.IsPreviousAttempt, State = r.State, Result = r.Result, StartTime = r.StartTime, FinishTime = r.FinishTime, Order = r.Order,
        ErrorMessage = r.ErrorMessage, ErrorFingerprint = r.ErrorFingerprint,
    };

    private static PullRequest Clone(PullRequest p) => new()
    {
        Id = p.Id, RepoId = p.RepoId, Title = p.Title, Status = p.Status, IsDraft = p.IsDraft, AuthorId = p.AuthorId, AuthorName = p.AuthorName,
        AuthorIsBot = p.AuthorIsBot, CreatedOn = p.CreatedOn, PublishedOn = p.PublishedOn, ClosedOn = p.ClosedOn, MergeStatus = p.MergeStatus,
        SourceBranch = p.SourceBranch, TargetBranch = p.TargetBranch, LastMergeCommit = p.LastMergeCommit, FilesChanged = p.FilesChanged,
        PolicyStatus = p.PolicyStatus, ReviewersJson = p.ReviewersJson, WebUrl = p.WebUrl, LastSyncedAt = DateTimeOffset.UtcNow,
    };

    public static Random Rng(string kind, string key) => new(StableInt($"{kind}:{key}"));

    public static int StableInt(string s)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return BitConverter.ToInt32(h, 0) & int.MaxValue;
    }

    public static string StableGuid(string s)
    {
        var h = MD5.HashData(Encoding.UTF8.GetBytes(s));
        return new Guid(h).ToString();
    }

    public static string Sha(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private static string Pascal(string kebab) => string.Concat(kebab.Split('-').Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
}

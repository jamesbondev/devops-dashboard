using System.Text.Json;
using Dashboard.Core.Entities;
using Dashboard.Web.Services;

namespace Dashboard.Web.Ado.Mock;

/// <summary>Serves the synthetic <see cref="MockWorld"/> through the same interface as the live client.</summary>
public sealed class MockAdoClient(MockWorld world, IClock clock, AdoApiStats stats) : IAdoClient
{
    public bool IsMock => true;
    public string? GitAuthHeader => null;
    private DateTimeOffset Now => clock.UtcNow;

    private T Call<T>(T value) { stats.RecordCall(); stats.RecordTstu(0.05); return value; }

    public Task<AdoIdentity> GetMeAsync(CancellationToken ct) =>
        Task.FromResult(Call(new AdoIdentity(MockWorld.MeId, MockWorld.MeName, new HashSet<string> { MockWorld.GateApproversGroupId })));

    public Task<string?> GetProjectIdAsync(CancellationToken ct) => Task.FromResult<string?>(Call("mock-project-0001"));

    public Task<IReadOnlyList<Repo>> ListRepositoriesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Repo>>(Call(world.Repos.Select(r => new Repo
        {
            Id = r.Id, Name = r.Name, ProjectName = "Platform", DefaultBranch = "refs/heads/main",
            WebUrl = $"https://dev.azure.com/contoso/Platform/_git/{r.Name}", RemoteUrl = $"https://contoso@dev.azure.com/contoso/Platform/_git/{r.Name}",
            SizeBytes = 200_000 + MockWorld.StableInt(r.Name) % 40_000_000, IsDisabled = r.Name == "archived-monolith", LastSeenAt = Now,
        }).ToList()));

    public Task<IReadOnlyList<Pipeline>> ListPipelinesAsync(string repoId, CancellationToken ct)
    {
        var r = world.Repos.First(x => x.Id == repoId);
        var list = new List<Pipeline>
        {
            new() { Id = r.CiPipelineId, RepoId = repoId, Name = $"{r.Name}-ci", Folder = "\\" + r.Name, YamlPath = "azure-pipelines.yml", WebUrl = $"https://dev.azure.com/contoso/Platform/_build?definitionId={r.CiPipelineId}", LastSeenAt = Now },
        };
        if (r.ReleasePipelineId is { } rel)
            list.Add(new Pipeline { Id = rel, RepoId = repoId, Name = $"{r.Name}-release", Folder = "\\" + r.Name, YamlPath = "release.yml", WebUrl = $"https://dev.azure.com/contoso/Platform/_build?definitionId={rel}", LastSeenAt = Now });
        if (r.NightlyPipelineId is { } n)
            list.Add(new Pipeline { Id = n, RepoId = repoId, Name = $"{r.Name}-nightly", Folder = "\\" + r.Name, YamlPath = "pipelines/nightly.yml", WebUrl = $"https://dev.azure.com/contoso/Platform/_build?definitionId={n}", LastSeenAt = Now });
        return Task.FromResult<IReadOnlyList<Pipeline>>(Call(list));
    }

    public Task<IReadOnlyList<Run>> ListRunsAsync(RunQuery q, CancellationToken ct)
    {
        var now = Now;
        var defs = q.DefinitionIds.ToHashSet();
        var runs = world.RunsAt(now).Where(m => defs.Contains(m.Run.PipelineId)).Select(m => world.RunSnapshot(m, now))
            .Where(r => q.StatusFilter switch
            {
                "completed" => r.Status == "completed",
                "all" => true,
                _ => r.Status != "completed",
            })
            .Where(r => q.MinTime is null || (r.FinishTime ?? r.QueueTime) >= q.MinTime)
            .OrderBy(r => r.FinishTime ?? r.QueueTime).Take(q.Max).ToList();
        return Task.FromResult<IReadOnlyList<Run>>(Call(runs));
    }

    public Task<IReadOnlyList<Run>> ListActiveRunsAsync(CancellationToken ct)
    {
        var now = Now;
        var runs = world.RunsAt(now).Where(m => m.End > now).Select(m => world.RunSnapshot(m, now)).ToList();
        return Task.FromResult<IReadOnlyList<Run>>(Call(runs));
    }

    public Task<Run?> GetRunAsync(int buildId, CancellationToken ct)
    {
        var now = Now;
        var m = world.FindRun(buildId, now);
        return Task.FromResult(Call(m is null ? null : world.RunSnapshot(m, now)));
    }

    public Task<IReadOnlyList<TimelineRecord>> GetTimelineAsync(int buildId, CancellationToken ct)
    {
        var now = Now;
        var m = world.FindRun(buildId, now);
        return Task.FromResult<IReadOnlyList<TimelineRecord>>(Call(m is null ? [] : world.Snapshot(m, now).Timeline));
    }

    public Task<IReadOnlyList<RunResource>> GetRunResourcesAsync(int pipelineId, int runId, CancellationToken ct)
    {
        var m = world.FindRun(runId, Now);
        IReadOnlyList<RunResource> res = m is null ? [] :
        [
            new RunResource("self", m.Run.RepoId, "refs/heads/main", m.Run.SourceVersion),
            new RunResource("templates", "cbi/cbi-yaml-templates", m.Run.TemplateRef, m.Run.TemplateSha),
        ];
        return Task.FromResult(Call(res));
    }

    public Task<IReadOnlyList<PullRequest>> ListActivePullRequestsAsync(CancellationToken ct)
    {
        var now = Now;
        var list = world.PrsAt(now).Select(p => world.PrSnapshot(p, now, out _)).Where(p => p.Status == "active").ToList();
        return Task.FromResult<IReadOnlyList<PullRequest>>(Call(list));
    }

    public Task<IReadOnlyList<PullRequest>> ListClosedPullRequestsAsync(string repoId, DateTimeOffset since, CancellationToken ct)
    {
        var now = Now;
        var list = world.PrsForRepo(repoId, now).Select(p => world.PrSnapshot(p, now, out _))
            .Where(p => p.Status != "active" && p.ClosedOn >= since).ToList();
        return Task.FromResult<IReadOnlyList<PullRequest>>(Call(list));
    }

    public Task<PullRequest?> GetPullRequestAsync(string repoId, int pullRequestId, CancellationToken ct)
    {
        var now = Now;
        var m = world.FindPr(pullRequestId, now);
        return Task.FromResult(Call(m is null ? null : world.PrSnapshot(m, now, out _)));
    }

    public Task<PrEventsResult> GetPullRequestEventsAsync(string repoId, int pullRequestId, string? authorId, CancellationToken ct)
    {
        var now = Now;
        var m = world.FindPr(pullRequestId, now);
        if (m is null) return Task.FromResult(Call(new PrEventsResult([], null)));
        world.PrSnapshot(m, now, out var events);
        var published = m.PublishedOn is { } p && p <= now ? p : (DateTimeOffset?)null;
        return Task.FromResult(Call(new PrEventsResult(events.Select(e => new PrEvent
        {
            PullRequestId = e.PullRequestId, ActorId = e.ActorId, ActorName = e.ActorName, IsAuthor = e.IsAuthor, IsBot = e.IsBot,
            Kind = e.Kind, Vote = e.Vote, Time = e.Time,
        }).ToList(), published)));
    }

    public Task<string?> GetPolicyStatusAsync(string projectId, int pullRequestId, CancellationToken ct)
    {
        var now = Now;
        var m = world.FindPr(pullRequestId, now);
        return Task.FromResult(Call(m is null ? null : world.PrSnapshot(m, now, out _).PolicyStatus));
    }

    public Task<string?> GetBranchHeadAsync(string repoId, string branch, CancellationToken ct)
    {
        var now = Now;
        var head = world.PrsForRepo(repoId, now).Where(p => p.CompletesAt <= now && !p.Abandons)
            .OrderByDescending(p => p.CompletesAt).Select(p => p.MergeSha).FirstOrDefault();
        return Task.FromResult<string?>(Call(head ?? MockWorld.Sha("initial:" + repoId)));
    }

    public Task<IReadOnlyList<Commit>> ListCommitsAsync(string repoId, string branch, DateTimeOffset since, CancellationToken ct)
    {
        var now = Now;
        var list = world.PrsForRepo(repoId, now).Where(p => p.CompletesAt <= now && p.CompletesAt >= since && !p.Abandons)
            .Select(p => new Commit { RepoId = repoId, Sha = p.MergeSha, Author = p.Pr.AuthorName, Date = p.CompletesAt!.Value, Message = $"Merged PR {p.Pr.Id}: {p.Pr.Title}" })
            .ToList();
        return Task.FromResult<IReadOnlyList<Commit>>(Call(list));
    }

    public Task<IReadOnlyList<BranchPolicy>> ListPoliciesAsync(CancellationToken ct)
    {
        var list = new List<BranchPolicy>();
        foreach (var r in world.Repos)
        {
            if (r.MinReviewers)
                list.Add(new BranchPolicy { Id = 10_000 + r.Index, RepoId = r.Id, TypeName = "Minimum number of reviewers", IsEnabled = true, IsBlocking = true, RefName = "refs/heads/main", MinimumApproverCount = r.Index % 3 == 0 ? 2 : 1 });
            if (r.BuildPolicy)
                list.Add(new BranchPolicy { Id = 20_000 + r.Index, RepoId = r.Id, TypeName = "Build", IsEnabled = true, IsBlocking = true, RefName = "refs/heads/main", BuildDefinitionId = r.CiPipelineId });
            list.Add(new BranchPolicy { Id = 30_000 + r.Index, RepoId = r.Id, TypeName = "Comment requirements", IsEnabled = r.Index % 2 == 0, IsBlocking = true, RefName = "refs/heads/main" });
        }
        return Task.FromResult<IReadOnlyList<BranchPolicy>>(Call(list));
    }

    public Task<IReadOnlyList<Approval>> ListPendingApprovalsAsync(CancellationToken ct)
    {
        var now = Now;
        var list = new List<Approval>();
        foreach (var m in world.RunsAt(now).Where(r => r.End > now))
            foreach (var a in m.Approvals.Where(a => a.Created <= now && a.Done > now))
            {
                // Gate escalations go to an approver group you're a member of (exercises group-membership matching).
                var approvers = a.Mine
                    ? new object[] { new { id = MockWorld.GateApproversGroupId, displayName = MockWorld.GateApproversGroupName, status = "pending" } }
                    : [new { id = MockWorld.ReleaseApproversGroupId, displayName = "[cbi]\\Release Approvers", status = "pending" }];
                list.Add(new Approval
                {
                    Id = a.ApprovalId, RunId = m.Run.Id, PipelineId = m.Run.PipelineId, RepoId = m.Run.RepoId, StageName = a.StageName, EnvironmentName = string.IsNullOrEmpty(a.Env) ? null : a.Env,
                    Status = "pending", CreatedOn = a.Created, ApproversJson = JsonSerializer.Serialize(approvers, JsonDefaults.Web),
                    Instructions = a.Reason ?? $"Approve deployment to {a.Env}", MinRequiredApprovers = 1, WebUrl = m.Run.WebUrl, LastSyncedAt = now,
                    EscalationReason = a.Reason,
                });
            }
        return Task.FromResult<IReadOnlyList<Approval>>(Call(list));
    }

    public Task<IReadOnlyList<EnvironmentCheck>> ListEnvironmentChecksAsync(int environmentId, string environmentName, CancellationToken ct)
    {
        var now = Now;
        var gate = JsonSerializer.Serialize(new { approverIds = new[] { MockWorld.GateApproversGroupId } }, JsonDefaults.Web);
        var group = JsonSerializer.Serialize(new { approverIds = new[] { MockWorld.ReleaseApproversGroupId } }, JsonDefaults.Web);
        EnvironmentCheck C(string id, string type, string name, string approvers = "", bool me = false, string settings = "{}") => new()
        {
            EnvironmentName = environmentName, EnvironmentAdoId = environmentId, CheckId = $"{environmentId}{id}", CheckType = type, DisplayName = name,
            ApproverNames = approvers, RequiresMe = me, SettingsJson = settings, LastSeenAt = now,
        };
        IReadOnlyList<EnvironmentCheck> list = environmentName switch
        {
            var e when e.StartsWith("sim", StringComparison.Ordinal) || e.StartsWith("prod", StringComparison.Ordinal) =>
            [
                C("01", "Approval", "Release approval", "[cbi]\\Release Approvers", settings: group),
                C("02", "Task Check", "Business hours (deployment window)"),
                C("03", "Task Check", "Invoke REST API: incident status"),
                C("04", "Task Check", "Query Azure Monitor: Service Bus dead letters"),
                C("05", "Approval", "Approval Gate 2 escalation", MockWorld.GateApproversGroupName, settings: gate),
            ],
            var e when e.StartsWith("staging", StringComparison.Ordinal) => [C("01", "Task Check", "Business hours (deployment window)")],
            var e when e.Contains("approval gate", StringComparison.OrdinalIgnoreCase) =>
            [
                C("01", "Approval", "All approvers must approve", MockWorld.GateApproversGroupName, settings: gate),
                C("02", "Task Check", "Release Minder"),
                C("03", "Task Check", "Release Governance API"),
            ],
            _ => [],
        };
        return Task.FromResult(Call(list));
    }

    public Task<IReadOnlyList<TemplateCommit>> ListTemplateCommitsAsync(string branch, DateTimeOffset since, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TemplateCommit>>(Call(world.TemplateCommitsAt(Now).Where(c => c.Branch == branch && c.Date >= since).ToList()));

    public Task<IReadOnlyList<TestFlake>> ListTestFlakesAsync(int buildId, string repoId, CancellationToken ct)
    {
        var m = world.FindRun(buildId, Now);
        IReadOnlyList<TestFlake> list = m is null ? [] : m.FlakyTests.Select(t => new TestFlake
        {
            RunId = buildId, RepoId = repoId, TestName = t, Outcome = "Passed", Flaky = true, Date = m.Run.FinishTime ?? m.Run.QueueTime,
        }).ToList();
        return Task.FromResult(Call(list));
    }

    public Task<IReadOnlyList<ProbeResult>> ProbeAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ProbeResult>>(
    [
        new("Identity", "(any)", true, "mock"), new("Project", "vso.project", true, "mock"), new("Repositories", "vso.code", true, "mock"),
        new("Pull requests", "vso.code", true, "mock"), new("Policies", "vso.code", true, "mock"), new("Builds", "vso.build", true, "mock"),
        new("Approvals", "vso.build", true, "mock"), new("Environment checks", "vso.build", true, "mock"), new("Group membership", "vso.identity", true, "mock"),
        new("Test results", "vso.test", true, "mock"), new("Template repo", "vso.code", true, "mock"),
    ]);
}

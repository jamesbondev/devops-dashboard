using Dashboard.Core.Entities;

namespace Dashboard.Web.Ado;

public sealed record AdoIdentity(string Id, string DisplayName, IReadOnlySet<string> GroupIds);

public sealed record RunResource(string Alias, string? RepositoryName, string? RefName, string? Version);

public sealed record PrEventsResult(IReadOnlyList<PrEvent> Events, DateTimeOffset? PublishedOn);

public sealed record ProbeResult(string Area, string Scope, bool Ok, string Detail);

public sealed class RunQuery
{
    public IReadOnlyList<int> DefinitionIds { get; init; } = [];
    public DateTimeOffset? MinTime { get; init; }
    /// <summary>completed | inProgress,notStarted | all</summary>
    public string StatusFilter { get; init; } = "completed";
    public int Max { get; init; } = 2000;
}

/// <summary>
/// Read-only Azure DevOps access. Implementations map ADO JSON straight to persistence entities
/// (keeping raw JSON where useful). <see cref="LiveAdoClient"/> talks REST; <see cref="Mock.MockAdoClient"/>
/// serves a deterministic synthetic organisation.
/// </summary>
public interface IAdoClient
{
    bool IsMock { get; }
    Task<AdoIdentity> GetMeAsync(CancellationToken ct);
    Task<string?> GetProjectIdAsync(CancellationToken ct);
    Task<IReadOnlyList<Repo>> ListRepositoriesAsync(CancellationToken ct);
    Task<IReadOnlyList<Pipeline>> ListPipelinesAsync(string repoId, CancellationToken ct);
    Task<IReadOnlyList<Run>> ListRunsAsync(RunQuery query, CancellationToken ct);
    /// <summary>Project-wide running/queued builds.</summary>
    Task<IReadOnlyList<Run>> ListActiveRunsAsync(CancellationToken ct);
    /// <summary>Timeline incl. previous-attempt records (IsPreviousAttempt = true).</summary>
    Task<IReadOnlyList<TimelineRecord>> GetTimelineAsync(int buildId, CancellationToken ct);
    Task<IReadOnlyList<RunResource>> GetRunResourcesAsync(int pipelineId, int runId, CancellationToken ct);
    Task<IReadOnlyList<PullRequest>> ListActivePullRequestsAsync(CancellationToken ct);
    Task<IReadOnlyList<PullRequest>> ListClosedPullRequestsAsync(string repoId, DateTimeOffset since, CancellationToken ct);
    Task<PullRequest?> GetPullRequestAsync(string repoId, int pullRequestId, CancellationToken ct);
    Task<PrEventsResult> GetPullRequestEventsAsync(string repoId, int pullRequestId, string? authorId, CancellationToken ct);
    Task<string?> GetPolicyStatusAsync(string projectId, int pullRequestId, CancellationToken ct);
    Task<string?> GetBranchHeadAsync(string repoId, string branch, CancellationToken ct);
    Task<IReadOnlyList<Commit>> ListCommitsAsync(string repoId, string branch, DateTimeOffset since, CancellationToken ct);
    Task<IReadOnlyList<BranchPolicy>> ListPoliciesAsync(CancellationToken ct);
    Task<IReadOnlyList<Approval>> ListPendingApprovalsAsync(CancellationToken ct);
    Task<IReadOnlyList<EnvironmentCheck>> ListEnvironmentChecksAsync(int environmentId, string environmentName, CancellationToken ct);
    Task<IReadOnlyList<TemplateCommit>> ListTemplateCommitsAsync(string branch, DateTimeOffset since, CancellationToken ct);
    Task<IReadOnlyList<TestFlake>> ListTestFlakesAsync(int buildId, string repoId, CancellationToken ct);
    Task<IReadOnlyList<ProbeResult>> ProbeAsync(CancellationToken ct);
    /// <summary>HTTP header value for git over HTTPS ("Basic ..."); null in mock mode.</summary>
    string? GitAuthHeader { get; }
}

/// <summary>Per-process API call statistics (calls and TSTU usage from X-RateLimit headers).</summary>
public sealed class AdoApiStats
{
    private long _calls;
    private long _throttled;
    private double _tstu;
    public long Calls => Interlocked.Read(ref _calls);
    public long Throttled => Interlocked.Read(ref _throttled);
    public double Tstu { get { lock (this) return _tstu; } }
    public double? LastRemaining { get; private set; }
    public double? LastLimit { get; private set; }
    public DateTimeOffset? LastDelayAt { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset? LastErrorAt { get; private set; }
    public bool AuthFailed { get; private set; }

    public void RecordCall() => Interlocked.Increment(ref _calls);
    public void RecordTstu(double cost) { lock (this) _tstu += cost; }
    public void RecordRemaining(double? remaining, double? limit) { LastRemaining = remaining; LastLimit = limit; }
    public void RecordThrottle() { Interlocked.Increment(ref _throttled); LastDelayAt = DateTimeOffset.UtcNow; }
    public void RecordError(string error, bool auth)
    {
        LastError = error; LastErrorAt = DateTimeOffset.UtcNow;
        if (auth) AuthFailed = true;
    }
    public void RecordSuccess() => AuthFailed = false;
}

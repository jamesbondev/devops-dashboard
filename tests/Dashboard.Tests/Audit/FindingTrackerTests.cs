using Dashboard.Core.Audit;
using Dashboard.Core.Entities;

namespace Dashboard.Tests.Audit;

public class FindingFingerprintTests
{
    [Fact]
    public void Stable_and_short()
    {
        var a = FindingFingerprint.Compute("K8S-001", "orders-api", "k8s/orders-api.yaml");
        Assert.Equal(16, a.Length);
        Assert.Matches("^[0-9a-f]{16}$", a);
        Assert.Equal(a, FindingFingerprint.Compute("K8S-001", "orders-api", "k8s/orders-api.yaml"));
        // Known value: guards against accidental algorithm changes (which would orphan every FindingState).
        Assert.Equal("0010044ffc579ac8", a);
        Assert.Equal(a, FindingFingerprint.Compute("k8s-001", " Orders-API ", "  .\\K8S\\Orders-Api.yaml "));
    }

    [Theory]
    [InlineData("src/App/Dockerfile", "SRC\\App\\Dockerfile")]
    [InlineData("src/App/Dockerfile", "./src/App/Dockerfile")]
    [InlineData("src/App/Dockerfile", "/src//App/Dockerfile/")]
    [InlineData("src/App/Dockerfile", " src/app/dockerfile ")]
    public void Path_normalisation(string a, string b)
        => Assert.Equal(FindingFingerprint.Compute("DOCK-001", null, a), FindingFingerprint.Compute("DOCK-001", null, b));

    [Fact]
    public void Distinguishes_inputs()
    {
        var baseFp = FindingFingerprint.Compute("DOCK-001", "api", "a/Dockerfile");
        Assert.NotEqual(baseFp, FindingFingerprint.Compute("DOCK-002", "api", "a/Dockerfile"));
        Assert.NotEqual(baseFp, FindingFingerprint.Compute("DOCK-001", "web", "a/Dockerfile"));
        Assert.NotEqual(baseFp, FindingFingerprint.Compute("DOCK-001", "api", "b/Dockerfile"));
        Assert.NotEqual(baseFp, FindingFingerprint.Compute("DOCK-001", null, "a/Dockerfile"));
        // Separator prevents ambiguity between component and path.
        Assert.NotEqual(FindingFingerprint.Compute("X", "a", "b"), FindingFingerprint.Compute("X", "a\u001fb", null));
        Assert.Equal(FindingFingerprint.Compute("X", null, null), FindingFingerprint.Compute("X", "", "  "));
    }
}

public class FindingTrackerTests
{
    private const string Repo = "repo-1";
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    private static FindingState State(string fp, FindingStatus status, DateTimeOffset? resolved = null, string? reason = null) => new()
    {
        Fingerprint = fp,
        RepoId = Repo,
        CheckId = "NET-001",
        Status = status,
        Reason = reason,
        FirstSeen = T0,
        LastSeen = T0,
        ResolvedAt = resolved,
        LatestFindingId = 1,
        Severity = "high",
    };

    private static (string, string, string, int) Cur(string fp, int id = 42, string severity = "medium") => (fp, "NET-001", severity, id);

    [Fact]
    public void Unseen_fingerprint_becomes_new()
    {
        var r = FindingTracker.Apply([], [Cur("a")], Repo, Now);
        var s = Assert.Single(r.Upserts);
        Assert.Equal(FindingStatus.New, s.Status);
        Assert.Equal(Now, s.FirstSeen);
        Assert.Equal(Now, s.LastSeen);
        Assert.Equal(42, s.LatestFindingId);
        Assert.Equal(Repo, s.RepoId);
        Assert.Equal("medium", s.Severity);
        Assert.Equal((1, 0, 0), (r.NewCount, r.ResolvedCount, r.ReopenedCount));
    }

    [Theory]
    [InlineData(FindingStatus.New)]
    [InlineData(FindingStatus.Accepted)]
    [InlineData(FindingStatus.Dismissed)]
    public void Seen_fingerprint_updates_last_seen_and_keeps_status(FindingStatus status)
    {
        var existing = State("a", status, reason: status == FindingStatus.Dismissed ? "false positive" : null);
        var r = FindingTracker.Apply([existing], [Cur("a", 99)], Repo, Now);
        var s = Assert.Single(r.Upserts);
        Assert.Equal(status, s.Status);
        Assert.Equal(T0, s.FirstSeen);
        Assert.Equal(Now, s.LastSeen);
        Assert.Equal(99, s.LatestFindingId);
        Assert.Equal(existing.Reason, s.Reason);
        Assert.Null(s.ResolvedAt);
        Assert.Equal((0, 0, 0), (r.NewCount, r.ResolvedCount, r.ReopenedCount));
    }

    [Theory]
    [InlineData(FindingStatus.New)]
    [InlineData(FindingStatus.Accepted)]
    public void Absent_new_or_accepted_becomes_done(FindingStatus status)
    {
        var r = FindingTracker.Apply([State("a", status)], [], Repo, Now);
        var s = Assert.Single(r.Upserts);
        Assert.Equal(FindingStatus.Done, s.Status);
        Assert.Equal(Now, s.ResolvedAt);
        Assert.Equal(T0, s.LastSeen);
        Assert.Equal(1, r.ResolvedCount);
    }

    [Fact]
    public void Done_that_reappears_is_reopened_as_new()
    {
        var r = FindingTracker.Apply([State("a", FindingStatus.Done, resolved: T0)], [Cur("a")], Repo, Now);
        var s = Assert.Single(r.Upserts);
        Assert.Equal(FindingStatus.New, s.Status);
        Assert.Null(s.ResolvedAt);
        Assert.Equal(T0, s.FirstSeen);
        Assert.Equal(Now, s.LastSeen);
        Assert.Equal((0, 0, 1), (r.NewCount, r.ResolvedCount, r.ReopenedCount));
    }

    [Fact]
    public void Dismissed_stays_dismissed_when_absent()
    {
        var r = FindingTracker.Apply([State("a", FindingStatus.Dismissed, reason: "won't fix")], [], Repo, Now);
        Assert.Empty(r.Upserts);
        Assert.Equal((0, 0, 0), (r.NewCount, r.ResolvedCount, r.ReopenedCount));
    }

    [Fact]
    public void Done_stays_done_when_absent()
    {
        var r = FindingTracker.Apply([State("a", FindingStatus.Done, resolved: T0)], [], Repo, Now);
        Assert.Empty(r.Upserts);
    }

    [Fact]
    public void Mixed_scenario_and_purity()
    {
        var existing = new List<FindingState>
        {
            State("keep", FindingStatus.Accepted),
            State("gone", FindingStatus.New),
            State("back", FindingStatus.Done, resolved: T0),
            State("muted", FindingStatus.Dismissed, reason: "n/a"),
            Reassign(State("other-repo", FindingStatus.New), "repo-2"),
        };
        var snapshot = System.Text.Json.JsonSerializer.Serialize(existing);

        var r = FindingTracker.Apply(existing, [Cur("keep"), Cur("back"), Cur("fresh"), Cur("fresh", 7)], Repo, Now);

        Assert.Equal(snapshot, System.Text.Json.JsonSerializer.Serialize(existing));   // inputs untouched
        var byFp = r.Upserts.ToDictionary(s => s.Fingerprint);
        Assert.Equal(["keep", "back", "fresh", "gone"], r.Upserts.Select(s => s.Fingerprint));
        Assert.Equal(FindingStatus.Accepted, byFp["keep"].Status);
        Assert.Equal(FindingStatus.New, byFp["back"].Status);
        Assert.Equal(FindingStatus.New, byFp["fresh"].Status);
        Assert.Equal(42, byFp["fresh"].LatestFindingId);                                  // duplicate fingerprint: first wins
        Assert.Equal(FindingStatus.Done, byFp["gone"].Status);
        Assert.False(byFp.ContainsKey("muted"));
        Assert.False(byFp.ContainsKey("other-repo"));                                      // other repos ignored
        Assert.Equal((1, 1, 1), (r.NewCount, r.ResolvedCount, r.ReopenedCount));

        // Same inputs → same outputs.
        var again = FindingTracker.Apply(existing, [Cur("keep"), Cur("back"), Cur("fresh"), Cur("fresh", 7)], Repo, Now);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(r.Upserts), System.Text.Json.JsonSerializer.Serialize(again.Upserts));
    }

    private static FindingState Reassign(FindingState s, string repo)
    {
        s.RepoId = repo;
        return s;
    }
}

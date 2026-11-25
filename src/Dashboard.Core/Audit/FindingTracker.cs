using Dashboard.Core.Entities;

namespace Dashboard.Core.Audit;

public sealed class TrackerResult
{
    /// <summary>States to insert or update (new copies; inputs are never mutated). Unchanged states are omitted.</summary>
    public List<FindingState> Upserts { get; init; } = [];
    /// <summary>Fingerprints seen for the first time.</summary>
    public int NewCount { get; init; }
    /// <summary>New/Accepted states that are no longer reported and became Done.</summary>
    public int ResolvedCount { get; init; }
    /// <summary>Done states that reappeared and went back to New (not included in <see cref="NewCount"/>).</summary>
    public int ReopenedCount { get; init; }
}

/// <summary>
/// Pure state machine for finding lifecycle across audits of one repo:
/// <list type="bullet">
/// <item>unseen fingerprint → New;</item>
/// <item>seen → LastSeen/LatestFindingId/Severity refreshed, status kept (Accepted/Dismissed stay);</item>
/// <item>absent and New/Accepted → Done with ResolvedAt;</item>
/// <item>Done and present again → New (reopened, ResolvedAt cleared);</item>
/// <item>Dismissed stays Dismissed whether present or not.</item>
/// </list>
/// </summary>
public static class FindingTracker
{
    public static TrackerResult Apply(
        IReadOnlyList<FindingState> existing,
        IReadOnlyList<(string Fingerprint, string CheckId, string Severity, int FindingId)> currentFailingOrPartial,
        string repoId,
        DateTimeOffset now)
    {
        var byFingerprint = new Dictionary<string, FindingState>(StringComparer.Ordinal);
        foreach (var s in existing)
            if (string.Equals(s.RepoId, repoId, StringComparison.Ordinal)) byFingerprint.TryAdd(s.Fingerprint, s);

        var upserts = new List<FindingState>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int newCount = 0, reopened = 0, resolved = 0;

        foreach (var cur in currentFailingOrPartial)
        {
            if (!seen.Add(cur.Fingerprint)) continue;   // duplicate fingerprint in one audit: first wins
            if (!byFingerprint.TryGetValue(cur.Fingerprint, out var prev))
            {
                newCount++;
                upserts.Add(new FindingState
                {
                    Fingerprint = cur.Fingerprint,
                    RepoId = repoId,
                    CheckId = cur.CheckId,
                    Status = FindingStatus.New,
                    FirstSeen = now,
                    LastSeen = now,
                    LatestFindingId = cur.FindingId,
                    Severity = cur.Severity,
                });
                continue;
            }

            var next = Copy(prev);
            next.LastSeen = now;
            next.LatestFindingId = cur.FindingId;
            next.Severity = cur.Severity;
            next.CheckId = cur.CheckId;
            if (prev.Status == FindingStatus.Done)
            {
                reopened++;
                next.Status = FindingStatus.New;
                next.ResolvedAt = null;
            }
            upserts.Add(next);
        }

        foreach (var prev in byFingerprint.Values.OrderBy(s => s.Fingerprint, StringComparer.Ordinal))
        {
            if (seen.Contains(prev.Fingerprint)) continue;
            if (prev.Status is FindingStatus.New or FindingStatus.Accepted)
            {
                var next = Copy(prev);
                next.Status = FindingStatus.Done;
                next.ResolvedAt = now;
                upserts.Add(next);
                resolved++;
            }
            // Done stays Done; Dismissed stays Dismissed: nothing to write.
        }

        return new TrackerResult { Upserts = upserts, NewCount = newCount, ResolvedCount = resolved, ReopenedCount = reopened };
    }

    private static FindingState Copy(FindingState s) => new()
    {
        Fingerprint = s.Fingerprint,
        RepoId = s.RepoId,
        CheckId = s.CheckId,
        Status = s.Status,
        Reason = s.Reason,
        FirstSeen = s.FirstSeen,
        LastSeen = s.LastSeen,
        ResolvedAt = s.ResolvedAt,
        LatestFindingId = s.LatestFindingId,
        Severity = s.Severity,
    };
}

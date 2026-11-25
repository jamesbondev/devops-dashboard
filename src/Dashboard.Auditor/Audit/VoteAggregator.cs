using Dashboard.Core.Contracts;

namespace Dashboard.Auditor.Audit;

/// <summary>
/// Combines N independent audit runs. Each check takes the majority result; any disagreement
/// marks it <c>Uncertain</c>. Explanation/evidence come from the most confident majority member.
/// </summary>
public static class VoteAggregator
{
    // Tie-break order: the more severe outcome wins a tie (and is still marked uncertain).
    private static readonly string[] Severity = ["fail", "partial", "pass", "na"];

    public static AuditResult Aggregate(IReadOnlyList<AuditResult> votes, IReadOnlyList<string> checkIds)
    {
        ArgumentOutOfRangeException.ThrowIfZero(votes.Count);
        if (votes.Count == 1)
        {
            return votes[0];
        }

        var n = votes.Count;

        // Architecture: majority style; rationale/evidence/summary from the first vote with that style.
        var styleGroups = votes
            .Select((v, i) => (Vote: v, Index: i))
            .GroupBy(x => x.Vote.ArchitectureStyle, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Min(x => x.Index))
            .ToList();
        var styleWinner = styleGroups[0].OrderBy(x => x.Index).First().Vote;

        var result = new AuditResult
        {
            ArchitectureStyle = styleWinner.ArchitectureStyle,
            ArchitectureRationale = styleWinner.ArchitectureRationale,
            ArchitectureEvidence = styleWinner.ArchitectureEvidence,
            Summary = styleWinner.Summary,
            AzureDependencies = votes.SelectMany(v => v.AzureDependencies)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Components = votes.SelectMany(v => v.Components)
                .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList(),
        };
        if (styleGroups.Count > 1)
        {
            result.ArchitectureRationale +=
                $" [auditor: {styleGroups[0].Count()}/{n} votes agreed on '{styleWinner.ArchitectureStyle}'; others: " +
                string.Join(", ", styleGroups.Skip(1).Select(g => $"{g.Key}×{g.Count()}")) + "]";
        }

        foreach (var checkId in checkIds)
        {
            var outcomes = votes
                .Select(v => v.Checks.FirstOrDefault(c => string.Equals(c.CheckId, checkId, StringComparison.OrdinalIgnoreCase)))
                .Select(c => c ?? new CheckOutcome { CheckId = checkId, Result = "na", Explanation = "no outcome returned", Confidence = 0, Uncertain = true })
                .ToList();
            result.Checks.Add(AggregateCheck(checkId, outcomes));
        }

        return result;
    }

    public static CheckOutcome AggregateCheck(string checkId, IReadOnlyList<CheckOutcome> outcomes)
    {
        var n = outcomes.Count;
        var groups = outcomes
            .GroupBy(o => o.Result.ToLowerInvariant())
            .OrderByDescending(g => g.Count())
            .ThenBy(g => SeverityRank(g.Key))
            .ToList();

        var winner = groups[0];
        var tie = groups.Count > 1 && groups[1].Count() == winner.Count();
        var representative = winner.OrderByDescending(o => o.Confidence).First();
        var agreement = (double)winner.Count() / n;
        var disagree = groups.Count > 1;

        var explanation = representative.Explanation;
        if (disagree)
        {
            explanation += $" [auditor: votes split {string.Join(", ", groups.Select(g => $"{g.Key}×{g.Count()}"))}{(tie ? "; tie resolved to the more severe outcome" : "")}]";
        }

        return new CheckOutcome
        {
            CheckId = checkId,
            Result = winner.Key,
            Component = representative.Component,
            Explanation = explanation,
            Recommendation = representative.Recommendation,
            Evidence = representative.Evidence,
            Confidence = Math.Round(winner.Average(o => o.Confidence) * agreement, 3),
            Uncertain = disagree || winner.Any(o => o.Uncertain),
        };
    }

    private static int SeverityRank(string result)
    {
        var i = Array.IndexOf(Severity, result);
        return i < 0 ? Severity.Length : i;
    }
}

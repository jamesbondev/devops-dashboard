using Dashboard.Core.Configuration;
using Dashboard.Core.Entities;
using Dashboard.Core.Templates;

namespace Dashboard.Core.Rules;

/// <summary>Everything the rule engine needs about one repo, already computed from raw data.</summary>
public sealed class RepoHealthInput
{
    public RepoType Type { get; init; }
    public bool Dormant { get; init; }
    public int PipelineCount { get; init; }
    public int DefaultBranchCiRuns { get; init; }           // non-PR runs in window, any outcome
    public int CompletedBuildRuns { get; init; }            // pass + fail
    public double? MainSuccessRate { get; init; }
    public double? LongestRedWorkingHours { get; init; }
    public double? FlakyRunRate { get; init; }
    public double? FirstReviewHours { get; init; }
    public string FirstReviewLabel { get; init; } = "median";
    public int ReviewedPrs { get; init; }
    public double? OldestActivePrWorkingDays { get; init; }
    public int ActivePrs { get; init; }
    public bool? HasMinReviewerPolicy { get; init; }        // null = policies not synced
    public bool? HasBuildValidationPolicy { get; init; }
    public IReadOnlyList<PipelineTemplateStatus> Templates { get; init; } = [];
    public double? DaysSinceProductionDeploy { get; init; } // null = never (in synced history)
    public bool FactsAvailable { get; init; }
    public bool HasReadme { get; init; }
    public int UnsupportedFrameworks { get; init; }
    public bool? EveryDeployableHasTests { get; init; }     // null = no deployables
    public int OpenHighSeverityFactFindings { get; init; }
    public bool AuditAvailable { get; init; }
}

public sealed record RuleOutcome(string RuleId, string Tier, RuleStatus Status, double? Value, string Display, int SampleSize, string Title);

public sealed record TierResult(string Tier, bool Provisional, IReadOnlyList<RuleOutcome> Outcomes, IReadOnlyList<RuleOutcome> NextTierGaps, string? NextTier)
{
    public static readonly string[] Order = ["None", "Bronze", "Silver", "Gold"];
}

public sealed class RuleEngine(DashboardConfig config)
{
    private static readonly HashSet<string> ActivityRules = ["BLD-001", "BLD-002", "BLD-003", "FLK-001", "PR-001", "PR-002", "DEP-001", "PIPE-001"];

    public TierResult Evaluate(RepoHealthInput input)
    {
        var outcomes = new List<RuleOutcome>();
        foreach (var rule in config.Rules.Where(r => r.Enabled))
        {
            if (!RuleCatalog.Known.TryGetValue(rule.Id, out var info)) continue;
            if (rule.RepoTypes.Count > 0 && !rule.RepoTypes.Contains(input.Type.ToString(), StringComparer.OrdinalIgnoreCase))
            {
                outcomes.Add(new RuleOutcome(rule.Id, rule.Tier, RuleStatus.NotApplicable, null, $"n/a for {input.Type}", 0, info.Title));
                continue;
            }
            if (input.Type == RepoType.Docs && !config.Repos.DocsRules.Contains(rule.Id, StringComparer.OrdinalIgnoreCase))
            {
                outcomes.Add(new RuleOutcome(rule.Id, rule.Tier, RuleStatus.NotApplicable, null, "n/a for docs / config repos", 0, info.Title));
                continue;
            }
            if (input.Dormant && ActivityRules.Contains(rule.Id) && rule.Id != "PIPE-001")
            {
                outcomes.Add(new RuleOutcome(rule.Id, rule.Tier, RuleStatus.Dormant, null, "dormant", 0, info.Title));
                continue;
            }
            outcomes.Add(EvaluateRule(rule, info, input));
        }
        return ComputeTier(outcomes);
    }

    private RuleOutcome EvaluateRule(RuleConfig rule, RuleInfo info, RepoHealthInput i)
    {
        var m = config.Metrics;
        RuleOutcome R(RuleStatus s, double? v, string d, int n = 0) => new(rule.Id, rule.Tier, s, v, d, n, info.Title);
        RuleOutcome Insufficient(string why, int n = 0) => R(RuleStatus.Insufficient, null, why, n);

        switch (rule.Id)
        {
            case "PIPE-001":
                return i.PipelineCount == 0 ? R(RuleStatus.Fail, 0, "no pipelines")
                    : i.DefaultBranchCiRuns > 0 ? R(RuleStatus.Pass, i.DefaultBranchCiRuns, $"{i.DefaultBranchCiRuns} CI runs", i.DefaultBranchCiRuns)
                    : i.Dormant ? R(RuleStatus.Dormant, 0, "dormant") : R(RuleStatus.Fail, 0, "no default-branch CI runs");
            case "POL-001":
                return i.HasMinReviewerPolicy is null ? Insufficient("policies not synced")
                    : R(i.HasMinReviewerPolicy.Value ? RuleStatus.Pass : RuleStatus.Fail, null, i.HasMinReviewerPolicy.Value ? "required" : "missing");
            case "POL-002":
                return i.HasBuildValidationPolicy is null ? Insufficient("policies not synced")
                    : R(i.HasBuildValidationPolicy.Value ? RuleStatus.Pass : RuleStatus.Fail, null, i.HasBuildValidationPolicy.Value ? "required" : "missing");
            case "TPL-001":
            {
                var known = i.Templates.Where(t => t.RefStatus != TemplateRefStatus.Unknown).ToList();
                if (known.Count == 0) return Insufficient("no pipeline data");
                var bad = known.Count(t => !t.UsesTemplates);
                return R(bad == 0 ? RuleStatus.Pass : RuleStatus.Fail, bad, bad == 0 ? "all pipelines" : $"{bad} pipeline(s) without templates", known.Count);
            }
            case "TPL-002":
            {
                var known = i.Templates.Where(t => t.RefStatus is not (TemplateRefStatus.Unknown or TemplateRefStatus.NoTemplate)).ToList();
                if (known.Count == 0) return Insufficient("no template refs");
                var bad = known.Count(t => t.RefStatus == TemplateRefStatus.NonCompliant);
                return R(bad == 0 ? RuleStatus.Pass : RuleStatus.Fail, bad, bad == 0 ? "main/next" : $"{bad} on other refs", known.Count);
            }
            case "BLD-001":
            case "BLD-003":
            {
                if (i.CompletedBuildRuns < m.MinRuns || i.MainSuccessRate is null) return Insufficient($"{i.CompletedBuildRuns}/{m.MinRuns} runs", i.CompletedBuildRuns);
                var t = rule.Threshold ?? (rule.Id == "BLD-001" ? 0.9 : 0.95);
                return R(i.MainSuccessRate >= t ? RuleStatus.Pass : RuleStatus.Fail, i.MainSuccessRate, $"{i.MainSuccessRate:P0} (≥{t:P0})", i.CompletedBuildRuns);
            }
            case "BLD-002":
            {
                if (i.CompletedBuildRuns < m.MinRuns) return Insufficient($"{i.CompletedBuildRuns}/{m.MinRuns} runs", i.CompletedBuildRuns);
                var t = rule.Threshold ?? 8;
                var v = i.LongestRedWorkingHours ?? 0;
                return R(v <= t ? RuleStatus.Pass : RuleStatus.Fail, v, $"longest red {v:0.#}h (≤{t:0}h)", i.CompletedBuildRuns);
            }
            case "FLK-001":
            {
                if (i.CompletedBuildRuns < m.MinRuns || i.FlakyRunRate is null) return Insufficient($"{i.CompletedBuildRuns}/{m.MinRuns} runs", i.CompletedBuildRuns);
                var t = rule.Threshold ?? 0.05;
                return R(i.FlakyRunRate < t ? RuleStatus.Pass : RuleStatus.Fail, i.FlakyRunRate, $"{i.FlakyRunRate:P1} (<{t:P0})", i.CompletedBuildRuns);
            }
            case "PR-001":
            {
                if (i.ReviewedPrs < m.MinPrs || i.FirstReviewHours is null) return Insufficient($"{i.ReviewedPrs}/{m.MinPrs} PRs", i.ReviewedPrs);
                var t = rule.Threshold ?? 16;
                return R(i.FirstReviewHours < t ? RuleStatus.Pass : RuleStatus.Fail, i.FirstReviewHours, $"{i.FirstReviewLabel} {i.FirstReviewHours:0.#}h (<{t:0}h)", i.ReviewedPrs);
            }
            case "PR-002":
            {
                var t = rule.Threshold ?? 5;
                var v = i.OldestActivePrWorkingDays ?? 0;
                return R(v <= t ? RuleStatus.Pass : RuleStatus.Fail, v, i.ActivePrs == 0 ? "no active PRs" : $"oldest {v:0.#}d (≤{t:0}d)", i.ActivePrs);
            }
            case "DEP-001":
            {
                var t = rule.Threshold ?? 30;
                if (i.DaysSinceProductionDeploy is null) return R(RuleStatus.Fail, null, "no Production deployment in history");
                return R(i.DaysSinceProductionDeploy <= t ? RuleStatus.Pass : RuleStatus.Fail, i.DaysSinceProductionDeploy, $"{i.DaysSinceProductionDeploy:0}d ago (≤{t:0}d)");
            }
            case "DOC-001":
                return !i.FactsAvailable ? Insufficient("not scanned yet") : R(i.HasReadme ? RuleStatus.Pass : RuleStatus.Fail, null, i.HasReadme ? "present" : "missing");
            case "NET-001":
                return !i.FactsAvailable ? Insufficient("not scanned yet")
                    : R(i.UnsupportedFrameworks == 0 ? RuleStatus.Pass : RuleStatus.Fail, i.UnsupportedFrameworks, i.UnsupportedFrameworks == 0 ? "all supported" : $"{i.UnsupportedFrameworks} unsupported TFM(s)");
            case "TST-001":
                return !i.FactsAvailable ? Insufficient("not scanned yet")
                    : i.EveryDeployableHasTests is null ? R(RuleStatus.NotApplicable, null, "no deployables")
                    : R(i.EveryDeployableHasTests.Value ? RuleStatus.Pass : RuleStatus.Fail, null, i.EveryDeployableHasTests.Value ? "covered" : "deployable(s) without tests");
            case "AUD-001":
                return !i.AuditAvailable ? Insufficient("not audited yet")
                    : R(i.OpenHighSeverityFactFindings == 0 ? RuleStatus.Pass : RuleStatus.Fail, i.OpenHighSeverityFactFindings, i.OpenHighSeverityFactFindings == 0 ? "none open" : $"{i.OpenHighSeverityFactFindings} open");
            default:
                return R(RuleStatus.NotApplicable, null, "unknown rule");
        }
    }

    /// <summary>
    /// Cumulative gates: highest tier whose rules all pass (NotApplicable/Dormant are neutral).
    /// Insufficient never passes; if it is what blocks the next tier, the tier is marked provisional.
    /// </summary>
    public static TierResult ComputeTier(IReadOnlyList<RuleOutcome> outcomes)
    {
        var achieved = "None";
        var provisional = false;
        string? nextTier = null;
        IReadOnlyList<RuleOutcome> gaps = [];
        foreach (var tier in new[] { "Bronze", "Silver", "Gold" })
        {
            var rules = outcomes.Where(o => o.Tier == tier && o.Status is not (RuleStatus.NotApplicable or RuleStatus.Dormant)).ToList();
            var blocking = rules.Where(o => o.Status != RuleStatus.Pass).ToList();
            if (blocking.Count == 0) { achieved = tier; continue; }
            nextTier = tier;
            gaps = blocking;
            provisional = blocking.All(b => b.Status == RuleStatus.Insufficient);
            break;
        }
        return new TierResult(achieved, provisional, outcomes, gaps, nextTier);
    }
}

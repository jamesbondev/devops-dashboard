namespace Dashboard.Core.Rules;

public sealed record RuleInfo(string Id, string Title, string Signal, string Formula, string Phase);

/// <summary>All rule ids the engine knows how to evaluate. Thresholds/tiers come from config.</summary>
public static class RuleCatalog
{
    public static readonly IReadOnlyDictionary<string, RuleInfo> Known = new[]
    {
        new RuleInfo("PIPE-001", "Has a CI pipeline on the default branch", "pipelines", "≥1 pipeline with a non-PR run on the default branch in the window", "1"),
        new RuleInfo("POL-001", "Default branch requires ≥1 reviewer", "policy", "Enabled, blocking 'Minimum number of reviewers' policy on the default branch with count ≥ 1", "1"),
        new RuleInfo("POL-002", "Build validation policy on the default branch", "policy", "Enabled, blocking 'Build' policy on the default branch", "1"),
        new RuleInfo("TPL-001", "Pipelines extend the shared templates", "templates", "Every pipeline's latest run used the template repo (resources) or its YAML uses extends:", "1"),
        new RuleInfo("TPL-002", "Template ref is a compliant or experimental branch", "templates", "Latest default-branch run's template ref ∈ compliant ∪ experimental branches", "1"),
        new RuleInfo("BLD-001", "Main build success rate", "build", "Final build-stage outcome pass ÷ (pass + fail), default-branch non-PR runs, window", "1"),
        new RuleInfo("BLD-002", "No long red interval on main", "build", "Longest red interval (working hours) in the window ≤ threshold", "1"),
        new RuleInfo("BLD-003", "Main build success rate (Gold)", "build", "Same as BLD-001 with the Gold threshold", "1"),
        new RuleInfo("FLK-001", "Flaky run rate", "flaky", "Default-branch runs with ≥1 flaky job ÷ completed default-branch runs", "1"),
        new RuleInfo("PR-001", "Time to first review", "pr", "Median (p85 when n ≥ 20) working hours from publish to first human non-author vote/comment", "1"),
        new RuleInfo("PR-002", "No old active PRs", "pr", "No active, non-draft PR older than threshold working days", "1"),
        new RuleInfo("DEP-001", "Deployed to Production recently", "deploy", "A successful deployment to a Production-tier environment within threshold days", "1"),
        new RuleInfo("DOC-001", "README present", "audit", "Fact scanner found a README at the repo root", "2"),
        new RuleInfo("NET-001", "Supported .NET target frameworks", "audit", "No project targets an out-of-support .NET version", "2"),
        new RuleInfo("TST-001", "Test projects for each deployable", "audit", "Every deployable .NET component has at least one test project", "2"),
        new RuleInfo("AUD-001", "No open high-severity fact finding", "audit", "No New/Accepted high-severity finding from a fact-based check", "2"),
    }.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
}

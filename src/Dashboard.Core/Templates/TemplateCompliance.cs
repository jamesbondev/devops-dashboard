using System.Text.RegularExpressions;
using Dashboard.Core.Configuration;
using Dashboard.Core.Entities;
using Dashboard.Core.Scanners;

namespace Dashboard.Core.Templates;

public enum TemplateRefStatus { Compliant, Experimental, NonCompliant, NoTemplate, Unknown }

public sealed class PipelineTemplateStatus
{
    public int PipelineId { get; init; }
    public string PipelineName { get; init; } = "";
    public TemplateRefStatus RefStatus { get; init; }
    public string? Ref { get; init; }
    public string? Sha { get; init; }
    public bool UsesTemplates { get; init; }
    public bool UsesExtends { get; init; }
    public int InlineStages { get; init; }
    public int InlineJobs { get; init; }
    public int InlineSteps { get; init; }
    public bool InlineBypass => InlineStages + InlineJobs + InlineSteps > 0;
    public bool ExperimentalInProduction { get; init; }
    public int? CommitsBehind { get; init; }
    public double? DaysBehind { get; init; }
    public bool Stale { get; init; }
    public DateTimeOffset? LastRunAt { get; init; }
    public List<string> Notes { get; init; } = [];
}

/// <summary>
/// Template compliance from what actually ran (Runs - Get resources) plus a static YAML parse.
/// Pipelines follow branches (main = compliant, next = experimental), so "behind" means the last run used an
/// older template commit than the branch head (informational, not a rule).
/// </summary>
public sealed class TemplateComplianceEvaluator(DashboardConfig config)
{
    public static string BranchName(string? refName) =>
        refName is null ? "" : refName.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase) ? refName[11..] : refName;

    public PipelineTemplateStatus Evaluate(Pipeline pipeline, Run? latestDefaultBranchRun, PipelineFileFacts? yaml,
        IReadOnlyList<TemplateCommit> templateHistory, bool deployedToProductionFromLatest, DateTimeOffset now)
    {
        var notes = new List<string>();
        var tc = config.Templates;
        var refName = latestDefaultBranchRun?.TemplateRef;
        var sha = latestDefaultBranchRun?.TemplateSha;

        // Fall back to static YAML resources when the run didn't report the template repository.
        if (refName is null && yaml is not null)
        {
            var res = yaml.RepositoryResources.FirstOrDefault(r => IsTemplateRepo(r.Alias, r.Name));
            if (res is not null) { refName = res.Ref ?? "refs/heads/main"; notes.Add("Template ref taken from YAML (run resources unavailable)."); }
        }

        var usesTemplates = refName is not null || (yaml?.UsesExtends ?? false) || (yaml?.TemplateReferences.Any(t => t.Contains('@')) ?? false);
        var branch = BranchName(refName);
        var status = refName is null
            ? (latestDefaultBranchRun is null && yaml is null ? TemplateRefStatus.Unknown : TemplateRefStatus.NoTemplate)
            : tc.CompliantBranches.Contains(branch, StringComparer.OrdinalIgnoreCase) ? TemplateRefStatus.Compliant
            : tc.ExperimentalBranches.Contains(branch, StringComparer.OrdinalIgnoreCase) ? TemplateRefStatus.Experimental
            : TemplateRefStatus.NonCompliant;

        int? behind = null;
        double? daysBehind = null;
        if (sha is not null && branch.Length > 0)
        {
            var history = templateHistory.Where(c => c.Branch.Equals(branch, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(c => c.Date).ToList();
            var idx = history.FindIndex(c => c.Sha.Equals(sha, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                behind = idx;
                daysBehind = idx == 0 ? 0 : (history[0].Date - history[idx].Date).TotalDays;
            }
            else if (history.Count > 0) notes.Add("Template commit not found in synced history (older than history window).");
        }

        if (status == TemplateRefStatus.NonCompliant) notes.Add($"Template ref '{branch}' is not an allowed branch.");
        if (status == TemplateRefStatus.Experimental && deployedToProductionFromLatest)
            notes.Add($"Experimental template branch '{branch}' reached Production.");
        if (yaml is { UsesExtends: false } && usesTemplates) notes.Add("Uses template references but not 'extends:' at the top level.");

        return new PipelineTemplateStatus
        {
            PipelineId = pipeline.Id,
            PipelineName = pipeline.Name,
            RefStatus = status,
            Ref = refName,
            Sha = sha,
            UsesTemplates = usesTemplates,
            UsesExtends = yaml?.UsesExtends ?? refName is not null,
            InlineStages = yaml?.InlineStages ?? 0,
            InlineJobs = yaml?.InlineJobs ?? 0,
            InlineSteps = yaml?.InlineSteps ?? 0,
            ExperimentalInProduction = status == TemplateRefStatus.Experimental && deployedToProductionFromLatest,
            CommitsBehind = behind,
            DaysBehind = daysBehind,
            Stale = daysBehind is not null && daysBehind > tc.StaleInfoDays,
            LastRunAt = latestDefaultBranchRun?.FinishTime ?? latestDefaultBranchRun?.QueueTime,
            Notes = notes,
        };
    }

    public bool IsTemplateRepo(string? alias, string? name) =>
        config.Templates.AliasPatterns.Any(p =>
            (alias is not null && Regex.IsMatch(alias, p, RegexOptions.IgnoreCase))
            || (name is not null && Regex.IsMatch(name, p, RegexOptions.IgnoreCase)))
        || (name is not null && name.EndsWith("/" + config.Templates.Repository, StringComparison.OrdinalIgnoreCase))
        || string.Equals(name, config.Templates.Repository, StringComparison.OrdinalIgnoreCase);
}

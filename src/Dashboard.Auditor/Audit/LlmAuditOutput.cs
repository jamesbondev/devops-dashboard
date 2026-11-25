using System.ComponentModel;
using Dashboard.Core.Contracts;

namespace Dashboard.Auditor.Audit;

// Shape the model is asked to return (structured output). Kept separate from the HTTP contract so
// auditor-owned fields (Uncertain) are not model-controlled and the schema stays strict-friendly.

[Description("Read-only architecture and practices review of one repository.")]
public sealed class LlmAuditOutput
{
    [Description("One of: layered, clean, vertical-slice, hexagonal, microservice, modular-monolith, other, unknown")]
    public string ArchitectureStyle { get; set; } = "unknown";

    [Description("Two to five sentences explaining the architecture style, citing concrete folders/projects.")]
    public string ArchitectureRationale { get; set; } = "";

    public List<LlmEvidence> ArchitectureEvidence { get; set; } = [];

    [Description("Short overall summary (3-6 sentences) of the repository's state.")]
    public string Summary { get; set; } = "";

    [Description("Exactly one outcome per requested check id.")]
    public List<LlmCheckOutcome> Checks { get; set; } = [];

    [Description("Azure services the code depends on, e.g. 'Service Bus', 'Key Vault', 'SQL Database'.")]
    public List<string> AzureDependencies { get; set; } = [];

    public List<LlmComponent> Components { get; set; } = [];
}

public sealed class LlmCheckOutcome
{
    [Description("The requested check id, e.g. ARCH-002")]
    public string CheckId { get; set; } = "";

    [Description("One of: pass, partial, fail, na")]
    public string Result { get; set; } = "na";

    [Description("Component (deployable/project) the outcome applies to, or null for the whole repository.")]
    public string? Component { get; set; }

    public string Explanation { get; set; } = "";

    public string? Recommendation { get; set; }

    [Description("Files you actually opened that support the outcome. Required for fail and partial.")]
    public List<LlmEvidence> Evidence { get; set; } = [];

    [Description("0.0 - 1.0")]
    public double Confidence { get; set; }
}

public sealed class LlmEvidence
{
    [Description("Path relative to the repository root, forward slashes.")]
    public string Path { get; set; } = "";

    [Description("1-based line number, or null.")]
    public int? Line { get; set; }

    public string? Note { get; set; }
}

public sealed class LlmComponent
{
    public string Name { get; set; } = "";

    [Description("e.g. web-api, worker, function, database, library, frontend")]
    public string Kind { get; set; } = "";

    public string? Path { get; set; }
}

public static class AuditOutputValidator
{
    public static readonly string[] ArchitectureStyles =
        ["layered", "clean", "vertical-slice", "hexagonal", "microservice", "modular-monolith", "other", "unknown"];

    public static readonly string[] CheckResults = ["pass", "partial", "fail", "na"];

    /// <summary>Returns human-readable problems (empty when valid) to feed back to the model on retry.</summary>
    public static List<string> Validate(LlmAuditOutput? output, IReadOnlyList<string> checkIds)
    {
        var errors = new List<string>();
        if (output is null)
        {
            errors.Add("The response was empty.");
            return errors;
        }
        if (!ArchitectureStyles.Contains(output.ArchitectureStyle?.Trim().ToLowerInvariant()))
        {
            errors.Add($"architectureStyle '{output.ArchitectureStyle}' is not one of: {string.Join(", ", ArchitectureStyles)}.");
        }
        if (string.IsNullOrWhiteSpace(output.Summary))
        {
            errors.Add("summary is empty.");
        }

        var checks = output.Checks ?? [];
        var returned = checks.GroupBy(c => c.CheckId?.Trim() ?? "", StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var id in checkIds)
        {
            var count = returned.FirstOrDefault(g => g.Key.Equals(id, StringComparison.OrdinalIgnoreCase))?.Count() ?? 0;
            if (count == 0)
            {
                errors.Add($"Missing outcome for check {id}.");
            }
            else if (count > 1)
            {
                errors.Add($"Check {id} has {count} outcomes; return exactly one.");
            }
        }
        foreach (var g in returned.Where(g => !checkIds.Contains(g.Key, StringComparer.OrdinalIgnoreCase)))
        {
            errors.Add($"Unexpected check id '{g.Key}'; only return the requested checks.");
        }
        foreach (var c in checks)
        {
            if (!CheckResults.Contains(c.Result?.Trim().ToLowerInvariant()))
            {
                errors.Add($"Check {c.CheckId}: result '{c.Result}' must be one of pass, partial, fail, na.");
            }
            if (c.Confidence is < 0 or > 1 || double.IsNaN(c.Confidence))
            {
                errors.Add($"Check {c.CheckId}: confidence must be between 0 and 1.");
            }
            if (c.Result?.Trim().ToLowerInvariant() is "fail" or "partial" && (c.Evidence is null || c.Evidence.Count == 0))
            {
                errors.Add($"Check {c.CheckId}: a {c.Result} outcome must cite at least one file as evidence.");
            }
            if (string.IsNullOrWhiteSpace(c.Explanation))
            {
                errors.Add($"Check {c.CheckId}: explanation is empty.");
            }
        }
        return errors;
    }

    /// <summary>
    /// Converts (possibly still invalid after the retry) model output into the contract shape:
    /// bad enum values become na/unknown, missing checks are added as uncertain na, extras are dropped.
    /// </summary>
    public static AuditResult ToResult(LlmAuditOutput output, IReadOnlyList<string> checkIds)
    {
        var style = output.ArchitectureStyle?.Trim().ToLowerInvariant() ?? "unknown";
        var result = new AuditResult
        {
            ArchitectureStyle = ArchitectureStyles.Contains(style) ? style : "unknown",
            ArchitectureRationale = output.ArchitectureRationale ?? "",
            ArchitectureEvidence = Map(output.ArchitectureEvidence),
            Summary = output.Summary ?? "",
            AzureDependencies = (output.AzureDependencies ?? [])
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Components = (output.Components ?? [])
                .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                .Select(c => new ComponentGuess { Name = c.Name.Trim(), Kind = c.Kind ?? "", Path = c.Path })
                .ToList(),
        };

        foreach (var id in checkIds)
        {
            var c = (output.Checks ?? []).FirstOrDefault(x => string.Equals(x.CheckId?.Trim(), id, StringComparison.OrdinalIgnoreCase));
            if (c is null)
            {
                result.Checks.Add(new CheckOutcome
                {
                    CheckId = id,
                    Result = "na",
                    Explanation = "[auditor: the model returned no outcome for this check]",
                    Confidence = 0,
                    Uncertain = true,
                });
                continue;
            }
            var r = c.Result?.Trim().ToLowerInvariant() ?? "na";
            var valid = CheckResults.Contains(r);
            result.Checks.Add(new CheckOutcome
            {
                CheckId = id,
                Result = valid ? r : "na",
                Component = string.IsNullOrWhiteSpace(c.Component) ? null : c.Component,
                Explanation = valid ? c.Explanation ?? "" : $"{c.Explanation} [auditor: invalid result '{c.Result}']",
                Recommendation = c.Recommendation,
                Evidence = Map(c.Evidence),
                Confidence = double.IsNaN(c.Confidence) ? 0 : Math.Clamp(c.Confidence, 0, 1),
                Uncertain = !valid,
            });
        }
        return result;
    }

    private static List<Evidence> Map(List<LlmEvidence>? evidence) =>
        (evidence ?? []).Select(e => new Evidence { Path = e.Path ?? "", Line = e.Line, Note = e.Note }).ToList();
}

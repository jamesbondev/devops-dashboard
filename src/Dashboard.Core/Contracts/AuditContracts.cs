namespace Dashboard.Core.Contracts;

// HTTP contract between the app and the auditor service (JSON, camelCase).
//   POST /audits            AuditRequest      -> 202 AuditStatusResponse
//   GET  /audits/{id}                         -> 200 AuditStatusResponse
//   POST /text              TextTaskRequest   -> 200 TextTaskResponse      (weekly brief, template change summary)
//   GET  /status                              -> 200 AuditorStatus
// Every request must carry header "X-Auditor-Token: <AUDITOR_TOKEN>".

public sealed class AuditCheck
{
    public string Id { get; set; } = "";
    public string Area { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>low | medium | high</summary>
    public string Severity { get; set; } = "medium";
    /// <summary>fact (evaluated in code by the app) | judgement (evaluated by the LLM)</summary>
    public string Kind { get; set; } = "judgement";
    public string Description { get; set; } = "";
    /// <summary>What pass / partial / fail mean for this check.</summary>
    public string Guidance { get; set; } = "";
}

public sealed class AuditRequest
{
    public string AuditId { get; set; } = "";
    public string RepoName { get; set; } = "";
    public string Sha { get; set; } = "";
    /// <summary>Absolute path of the immutable worktree, as mounted inside the auditor container.</summary>
    public string WorktreePath { get; set; } = "";
    /// <summary>RepoFacts serialised as JSON.</summary>
    public string FactsJson { get; set; } = "{}";
    /// <summary>Judgement checks only.</summary>
    public List<AuditCheck> Checks { get; set; } = [];
    public string? DiffSummary { get; set; }
    public string? Model { get; set; }
    public string? ReasoningEffort { get; set; }
    public int TimeoutMinutes { get; set; } = 20;
    public int Votes { get; set; } = 1;
    /// <summary>Optional extra team standards text appended to the system prompt.</summary>
    public string? TeamStandards { get; set; }
}

public sealed class AuditStatusResponse
{
    public string AuditId { get; set; } = "";
    /// <summary>queued | running | succeeded | failed</summary>
    public string Status { get; set; } = "queued";
    public string? Error { get; set; }
    public string? Engine { get; set; }
    public string? Model { get; set; }
    public string? PromptHash { get; set; }
    public AuditResult? Result { get; set; }
    public string? Transcript { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class AuditResult
{
    /// <summary>layered | clean | vertical-slice | hexagonal | microservice | modular-monolith | other | unknown</summary>
    public string ArchitectureStyle { get; set; } = "unknown";
    public string ArchitectureRationale { get; set; } = "";
    public List<Evidence> ArchitectureEvidence { get; set; } = [];
    public string Summary { get; set; } = "";
    public List<CheckOutcome> Checks { get; set; } = [];
    public List<string> AzureDependencies { get; set; } = [];
    public List<ComponentGuess> Components { get; set; } = [];
}

public sealed class CheckOutcome
{
    public string CheckId { get; set; } = "";
    /// <summary>pass | partial | fail | na</summary>
    public string Result { get; set; } = "na";
    public string? Component { get; set; }
    public string Explanation { get; set; } = "";
    public string? Recommendation { get; set; }
    public List<Evidence> Evidence { get; set; } = [];
    public double Confidence { get; set; } = 0.5;
    /// <summary>Set when votes disagreed.</summary>
    public bool Uncertain { get; set; }
}

public sealed class Evidence
{
    /// <summary>Path relative to the repository root, forward slashes.</summary>
    public string Path { get; set; } = "";
    public int? Line { get; set; }
    public string? Note { get; set; }
}

public sealed class ComponentGuess
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? Path { get; set; }
}

public sealed class TextTaskRequest
{
    /// <summary>weekly-brief | template-summary</summary>
    public string Kind { get; set; } = "";
    public string Input { get; set; } = "";
    public string? Model { get; set; }
    public int TimeoutMinutes { get; set; } = 5;
}

public sealed class TextTaskResponse
{
    public string Text { get; set; } = "";
    public string Engine { get; set; } = "";
    public string? Model { get; set; }
}

public sealed class AuditorStatus
{
    /// <summary>mock | copilot</summary>
    public string Engine { get; set; } = "";
    public bool Ready { get; set; }
    public bool Authenticated { get; set; }
    public string? Login { get; set; }
    public List<string> Models { get; set; } = [];
    public string? SelectedModel { get; set; }
    public string? Error { get; set; }
    public string SdkVersion { get; set; } = "";
}

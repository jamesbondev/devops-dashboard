using Dashboard.Core.Configuration;

namespace Dashboard.Core.Entities;

// Persistence model (EF Core, SQLite). All timestamps are UTC DateTimeOffset.
// Raw ADO JSON is kept (gzip) alongside parsed fields so metrics can be re-derived after parser fixes.

/// <summary>E2E and Load are test-only repos: their pipelines test other systems, so failures there are an
/// early warning about environments/apps, not a broken build of the repo itself.</summary>
public enum RepoType { Service, Library, Infra, E2E, Load }

public sealed class Repo
{
    public string Id { get; set; } = "";            // ADO repository GUID
    public string Name { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string? DefaultBranch { get; set; }      // refs/heads/main
    public string? WebUrl { get; set; }
    public string? RemoteUrl { get; set; }
    public long SizeBytes { get; set; }
    public bool IsDisabled { get; set; }
    public bool Assigned { get; set; }
    public RepoType Type { get; set; } = RepoType.Service;
    public bool Pinned { get; set; }
    public bool Muted { get; set; }
    public DateTimeOffset? AssignedAt { get; set; }
    public DateTimeOffset? BackfilledAt { get; set; }
    public string? HeadSha { get; set; }
    public DateTimeOffset? HeadCheckedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

public sealed class Pipeline
{
    public int Id { get; set; }                     // build definition id
    public string RepoId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Folder { get; set; }
    public string? YamlPath { get; set; }
    public string? WebUrl { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

public enum ComponentKind { AksService, Database, Other }
public enum ComponentSource { Discovered, Config }

public sealed class Component
{
    public int Id { get; set; }
    public string RepoId { get; set; } = "";
    public string Name { get; set; } = "";
    public ComponentKind Kind { get; set; }
    public ComponentSource Source { get; set; }
    public string? SkaffoldArtifact { get; set; }
    public string? ImageName { get; set; }
    public string? SourcePath { get; set; }         // skaffold.yaml / .sqlproj path
    public bool Hidden { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

public sealed class Run
{
    public int Id { get; set; }                     // build id
    public int PipelineId { get; set; }
    public string RepoId { get; set; } = "";
    public string? BuildNumber { get; set; }
    public string? SourceBranch { get; set; }
    public string? SourceVersion { get; set; }
    public string Reason { get; set; } = "";        // individualCI, batchedCI, pullRequest, manual, schedule, ...
    public string Status { get; set; } = "";        // notStarted, inProgress, completed, cancelling, postponed
    public string? Result { get; set; }             // succeeded, partiallySucceeded, failed, canceled
    public DateTimeOffset QueueTime { get; set; }
    public DateTimeOffset? StartTime { get; set; }
    public DateTimeOffset? FinishTime { get; set; }
    public string? RequestedFor { get; set; }
    public string? WebUrl { get; set; }
    public string? TemplateRef { get; set; }        // refs/heads/main
    public string? TemplateSha { get; set; }
    public string? TemplateAlias { get; set; }
    public bool TimelineFetched { get; set; }
    public DateTimeOffset? TimelineFetchedFor { get; set; } // FinishTime the timeline corresponds to
    public bool DeletedInAdo { get; set; }
    public byte[]? RawJson { get; set; }
    public DateTimeOffset LastSyncedAt { get; set; }

    public bool IsPullRequest => Reason.Equals("pullRequest", StringComparison.OrdinalIgnoreCase);
}

public sealed class TimelineRecord
{
    public int RunId { get; set; }
    public string RecordId { get; set; } = "";      // GUID; previous attempts have their own ids
    public string? ParentId { get; set; }
    public string Type { get; set; } = "";          // Stage, Phase, Job, Checkpoint, Checkpoint.Approval, Task, ...
    public string Name { get; set; } = "";
    public string? Identifier { get; set; }
    public int Attempt { get; set; } = 1;
    public bool IsPreviousAttempt { get; set; }
    public string? State { get; set; }
    public string? Result { get; set; }             // succeeded, succeededWithIssues, failed, canceled, skipped, abandoned
    public DateTimeOffset? StartTime { get; set; }
    public DateTimeOffset? FinishTime { get; set; }
    public int Order { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ErrorFingerprint { get; set; }
}

public sealed class Deployment
{
    public int Id { get; set; }
    public int RunId { get; set; }
    public string RepoId { get; set; } = "";
    public int PipelineId { get; set; }
    public int? ComponentId { get; set; }
    public string EnvironmentName { get; set; } = "";
    public EnvironmentTier Tier { get; set; }
    public string StageName { get; set; } = "";
    public string StageRecordId { get; set; } = "";
    public int Attempt { get; set; }
    public string? Result { get; set; }
    public string? State { get; set; }
    public DateTimeOffset? StartTime { get; set; }
    public DateTimeOffset? FinishTime { get; set; }
    public double WaitSeconds { get; set; }         // time in checkpoints/approvals
    public string? Version { get; set; }            // build number
    public string? SourceVersion { get; set; }
    public string? TemplateRef { get; set; }
}

public sealed class Approval
{
    public string Id { get; set; } = "";
    public int? RunId { get; set; }
    public int? PipelineId { get; set; }
    public string? RepoId { get; set; }
    public string? StageName { get; set; }
    public string? EnvironmentName { get; set; }
    public string Status { get; set; } = "";        // pending, approved, rejected, canceled, timedOut, skipped
    public DateTimeOffset CreatedOn { get; set; }
    public DateTimeOffset? LastModifiedOn { get; set; }
    public bool AssignedToMe { get; set; }
    public string ApproversJson { get; set; } = "[]";
    public string? Instructions { get; set; }
    public int MinRequiredApprovers { get; set; }
    public string? WebUrl { get; set; }
    public string? EscalationReason { get; set; }
    public DateTimeOffset LastSyncedAt { get; set; }
}

public sealed class EnvironmentCheck
{
    public int Id { get; set; }
    public string EnvironmentName { get; set; } = "";
    public int? EnvironmentAdoId { get; set; }
    public string CheckId { get; set; } = "";
    public string CheckType { get; set; } = "";     // Approval, BusinessHours, InvokeRestAPI, AzureFunction, AzureMonitorAlerts, ...
    public string DisplayName { get; set; } = "";
    public bool RequiresMe { get; set; }
    public string ApproverNames { get; set; } = "";
    public string SettingsJson { get; set; } = "{}";
    public DateTimeOffset LastSeenAt { get; set; }
}

public sealed class PullRequest
{
    public int Id { get; set; }
    public string RepoId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Status { get; set; } = "";        // active, completed, abandoned
    public bool IsDraft { get; set; }
    public string? AuthorId { get; set; }
    public string? AuthorName { get; set; }
    public bool AuthorIsBot { get; set; }
    public DateTimeOffset CreatedOn { get; set; }
    public DateTimeOffset? PublishedOn { get; set; }
    public DateTimeOffset? ClosedOn { get; set; }
    public string? MergeStatus { get; set; }        // succeeded, conflicts, ...
    public string? SourceBranch { get; set; }
    public string? TargetBranch { get; set; }
    public string? LastMergeCommit { get; set; }
    public int? FilesChanged { get; set; }
    public string? PolicyStatus { get; set; }       // approved, rejected, running, queued, null
    public string ReviewersJson { get; set; } = "[]";
    public string? WebUrl { get; set; }
    public bool ThreadsFetched { get; set; }
    public byte[]? RawJson { get; set; }
    public DateTimeOffset LastSyncedAt { get; set; }
}

public enum PrEventKind { Vote, Comment, Published, Completed, Abandoned, Pushed }

public sealed class PrEvent
{
    public int Id { get; set; }
    public int PullRequestId { get; set; }
    public string? ActorId { get; set; }
    public string? ActorName { get; set; }
    public bool IsAuthor { get; set; }
    public bool IsBot { get; set; }
    public PrEventKind Kind { get; set; }
    public int? Vote { get; set; }
    public DateTimeOffset Time { get; set; }
}

public sealed class Commit
{
    public string RepoId { get; set; } = "";
    public string Sha { get; set; } = "";
    public string? Author { get; set; }
    public string? AuthorEmail { get; set; }
    public DateTimeOffset Date { get; set; }
    public string? Message { get; set; }
}

public sealed class BranchPolicy
{
    public int Id { get; set; }
    public string RepoId { get; set; } = "";
    public string TypeName { get; set; } = "";      // "Minimum number of reviewers", "Build", ...
    public bool IsEnabled { get; set; }
    public bool IsBlocking { get; set; }
    public string? RefName { get; set; }
    public int? MinimumApproverCount { get; set; }
    public int? BuildDefinitionId { get; set; }
    public string SettingsJson { get; set; } = "{}";
}

public sealed class TestFlake
{
    public int Id { get; set; }
    public int RunId { get; set; }
    public string RepoId { get; set; } = "";
    public string TestName { get; set; } = "";
    public string Outcome { get; set; } = "";
    public bool Flaky { get; set; }
    public DateTimeOffset Date { get; set; }
}

public sealed class TemplateCommit
{
    public string Sha { get; set; } = "";
    public string Branch { get; set; } = "";
    public DateTimeOffset Date { get; set; }
    public string? Author { get; set; }
    public string? Message { get; set; }
}

public sealed class RepoFactsRecord
{
    public int Id { get; set; }
    public string RepoId { get; set; } = "";
    public string Sha { get; set; } = "";
    public string ScannerVersion { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string FactsJson { get; set; } = "{}";
}

public enum AuditStatus { Queued, Running, Succeeded, Failed }

public sealed class AuditRun
{
    public string Id { get; set; } = "";
    public string RepoId { get; set; } = "";
    public string Sha { get; set; } = "";
    public string? Model { get; set; }
    public string Engine { get; set; } = "";
    public string RubricVersion { get; set; } = "";
    public string PromptHash { get; set; } = "";
    public int Votes { get; set; } = 1;
    public AuditStatus Status { get; set; }
    public string Trigger { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long? DurationMs { get; set; }
    public string? Error { get; set; }
    public string? Summary { get; set; }
    public string? ArchitectureStyle { get; set; }
    public string? ResultJson { get; set; }
    public string? Transcript { get; set; }
    /// <summary>True when compared to the previous comparable run is impossible (model/rubric/prompt changed).</summary>
    public bool IsBaseline { get; set; }
}

public enum CheckResult { Pass, Partial, Fail, NotApplicable }

public sealed class Finding
{
    public int Id { get; set; }
    public string AuditRunId { get; set; } = "";
    public string RepoId { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string CheckId { get; set; } = "";
    public string? Component { get; set; }
    public string? Path { get; set; }
    public CheckResult Result { get; set; }
    public string Severity { get; set; } = "medium"; // low, medium, high
    public string Kind { get; set; } = "fact";       // fact | judgement
    public string Title { get; set; } = "";
    public string Explanation { get; set; } = "";
    public string? Recommendation { get; set; }
    public string EvidenceJson { get; set; } = "[]";
    public double Confidence { get; set; } = 1;
    public bool Uncertain { get; set; }
}

public enum FindingStatus { New, Accepted, Dismissed, Done }

public sealed class FindingState
{
    public string Fingerprint { get; set; } = "";
    public string RepoId { get; set; } = "";
    public string CheckId { get; set; } = "";
    public FindingStatus Status { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public int? LatestFindingId { get; set; }
    public string Severity { get; set; } = "medium";
}

public enum RuleStatus { Pass, Fail, Insufficient, NotApplicable, Dormant }

public sealed class RuleResult
{
    public int Id { get; set; }
    public string RepoId { get; set; } = "";
    public DateOnly Date { get; set; }
    public string RuleId { get; set; } = "";
    public string ConfigHash { get; set; } = "";
    public double? Value { get; set; }
    public string? Display { get; set; }
    public RuleStatus Status { get; set; }
    public int SampleSize { get; set; }
}

public sealed class HealthSnapshot
{
    public int Id { get; set; }
    public string RepoId { get; set; } = "";
    public DateOnly Date { get; set; }
    public string Tier { get; set; } = "None";
    public bool Provisional { get; set; }
    public string ConfigHash { get; set; } = "";
    public double? MainSuccessRate { get; set; }
    public double? FlakyRunRate { get; set; }
    public double? FirstReviewHours { get; set; }
    public double? RedHours { get; set; }
}

public sealed class SyncCursor
{
    public string Key { get; set; } = "";
    public DateTimeOffset? Watermark { get; set; }
    public string? Value { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public DateTimeOffset? LastErrorAt { get; set; }
    public string? LastError { get; set; }
}

public enum JobStatus { Queued, Running, Succeeded, Failed }

public sealed class Job
{
    public int Id { get; set; }
    public string Type { get; set; } = "";
    public string? Payload { get; set; }
    public JobStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset RunAfter { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long? DurationMs { get; set; }
    public int ApiCalls { get; set; }
    public double TstuUsed { get; set; }
    public string? Error { get; set; }
    public string? Summary { get; set; }
}

public sealed class UserState
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

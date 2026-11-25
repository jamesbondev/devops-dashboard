using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dashboard.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Approvals",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    RunId = table.Column<int>(type: "INTEGER", nullable: true),
                    PipelineId = table.Column<int>(type: "INTEGER", nullable: true),
                    RepoId = table.Column<string>(type: "TEXT", nullable: true),
                    StageName = table.Column<string>(type: "TEXT", nullable: true),
                    EnvironmentName = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedOn = table.Column<long>(type: "INTEGER", nullable: false),
                    LastModifiedOn = table.Column<long>(type: "INTEGER", nullable: true),
                    AssignedToMe = table.Column<bool>(type: "INTEGER", nullable: false),
                    ApproversJson = table.Column<string>(type: "TEXT", nullable: false),
                    Instructions = table.Column<string>(type: "TEXT", nullable: true),
                    MinRequiredApprovers = table.Column<int>(type: "INTEGER", nullable: false),
                    WebUrl = table.Column<string>(type: "TEXT", nullable: true),
                    EscalationReason = table.Column<string>(type: "TEXT", nullable: true),
                    LastSyncedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Approvals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditRuns",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    RepoId = table.Column<string>(type: "TEXT", nullable: false),
                    Sha = table.Column<string>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: true),
                    Engine = table.Column<string>(type: "TEXT", nullable: false),
                    RubricVersion = table.Column<string>(type: "TEXT", nullable: false),
                    PromptHash = table.Column<string>(type: "TEXT", nullable: false),
                    Votes = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    Summary = table.Column<string>(type: "TEXT", nullable: true),
                    ArchitectureStyle = table.Column<string>(type: "TEXT", nullable: true),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: true),
                    Transcript = table.Column<string>(type: "TEXT", nullable: true),
                    IsBaseline = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BranchPolicies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    RepoId = table.Column<string>(type: "TEXT", nullable: false),
                    TypeName = table.Column<string>(type: "TEXT", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsBlocking = table.Column<bool>(type: "INTEGER", nullable: false),
                    RefName = table.Column<string>(type: "TEXT", nullable: true),
                    MinimumApproverCount = table.Column<int>(type: "INTEGER", nullable: true),
                    BuildDefinitionId = table.Column<int>(type: "INTEGER", nullable: true),
                    SettingsJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BranchPolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Commits",
                columns: table => new
                {
                    RepoId = table.Column<string>(type: "TEXT", nullable: false),
                    Sha = table.Column<string>(type: "TEXT", nullable: false),
                    Author = table.Column<string>(type: "TEXT", nullable: true),
                    AuthorEmail = table.Column<string>(type: "TEXT", nullable: true),
                    Date = table.Column<long>(type: "INTEGER", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Commits", x => new { x.RepoId, x.Sha });
                });

            migrationBuilder.CreateTable(
                name: "Components",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RepoId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    SkaffoldArtifact = table.Column<string>(type: "TEXT", nullable: true),
                    ImageName = table.Column<string>(type: "TEXT", nullable: true),
                    SourcePath = table.Column<string>(type: "TEXT", nullable: true),
                    Hidden = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Components", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Deployments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunId = table.Column<int>(type: "INTEGER", nullable: false),
                    RepoId = table.Column<string>(type: "TEXT", nullable: false),
                    PipelineId = table.Column<int>(type: "INTEGER", nullable: false),
                    ComponentId = table.Column<int>(type: "INTEGER", nullable: true),
                    EnvironmentName = table.Column<string>(type: "TEXT", nullable: false),
                    Tier = table.Column<int>(type: "INTEGER", nullable: false),
                    StageName = table.Column<string>(type: "TEXT", nullable: false),
                    StageRecordId = table.Column<string>(type: "TEXT", nullable: false),
                    Attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    Result = table.Column<string>(type: "TEXT", nullable: true),
                    State = table.Column<string>(type: "TEXT", nullable: true),
                    StartTime = table.Column<long>(type: "INTEGER", nullable: true),
                    FinishTime = table.Column<long>(type: "INTEGER", nullable: true),
                    WaitSeconds = table.Column<double>(type: "REAL", nullable: false),
                    Version = table.Column<string>(type: "TEXT", nullable: true),
                    SourceVersion = table.Column<string>(type: "TEXT", nullable: true),
                    TemplateRef = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Deployments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EnvironmentChecks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EnvironmentName = table.Column<string>(type: "TEXT", nullable: false),
                    EnvironmentAdoId = table.Column<int>(type: "INTEGER", nullable: true),
                    CheckId = table.Column<string>(type: "TEXT", nullable: false),
                    CheckType = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    RequiresMe = table.Column<bool>(type: "INTEGER", nullable: false),
                    ApproverNames = table.Column<string>(type: "TEXT", nullable: false),
                    SettingsJson = table.Column<string>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnvironmentChecks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Findings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AuditRunId = table.Column<string>(type: "TEXT", nullable: false),
                    RepoId = table.Column<string>(type: "TEXT", nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    CheckId = table.Column<string>(type: "TEXT", nullable: false),
                    Component = table.Column<string>(type: "TEXT", nullable: true),
                    Path = table.Column<string>(type: "TEXT", nullable: true),
                    Result = table.Column<int>(type: "INTEGER", nullable: false),
                    Severity = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Explanation = table.Column<string>(type: "TEXT", nullable: false),
                    Recommendation = table.Column<string>(type: "TEXT", nullable: true),
                    EvidenceJson = table.Column<string>(type: "TEXT", nullable: false),
                    Confidence = table.Column<double>(type: "REAL", nullable: false),
                    Uncertain = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Findings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FindingStates",
                columns: table => new
                {
                    Fingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    RepoId = table.Column<string>(type: "TEXT", nullable: false),
                    CheckId = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: true),
                    FirstSeen = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeen = table.Column<long>(type: "INTEGER", nullable: false),
                    ResolvedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LatestFindingId = table.Column<int>(type: "INTEGER", nullable: true),
                    Severity = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FindingStates", x => x.Fingerprint);
                });

            migrationBuilder.CreateTable(
                name: "HealthSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RepoId = table.Column<string>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Tier = table.Column<string>(type: "TEXT", nullable: false),
                    Provisional = table.Column<bool>(type: "INTEGER", nullable: false),
                    ConfigHash = table.Column<string>(type: "TEXT", nullable: false),
                    MainSuccessRate = table.Column<double>(type: "REAL", nullable: true),
                    FlakyRunRate = table.Column<double>(type: "REAL", nullable: true),
                    FirstReviewHours = table.Column<double>(type: "REAL", nullable: true),
                    RedHours = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HealthSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RunAfter = table.Column<long>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: true),
                    ApiCalls = table.Column<int>(type: "INTEGER", nullable: false),
                    TstuUsed = table.Column<double>(type: "REAL", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    Summary = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Pipelines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    RepoId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Folder = table.Column<string>(type: "TEXT", nullable: true),
                    YamlPath = table.Column<string>(type: "TEXT", nullable: true),
                    WebUrl = table.Column<string>(type: "TEXT", nullable: true),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Pipelines", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PrEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PullRequestId = table.Column<int>(type: "INTEGER", nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", nullable: true),
                    ActorName = table.Column<string>(type: "TEXT", nullable: true),
                    IsAuthor = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsBot = table.Column<bool>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Vote = table.Column<int>(type: "INTEGER", nullable: true),
                    Time = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PullRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    RepoId = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    IsDraft = table.Column<bool>(type: "INTEGER", nullable: false),
                    AuthorId = table.Column<string>(type: "TEXT", nullable: true),
                    AuthorName = table.Column<string>(type: "TEXT", nullable: true),
                    AuthorIsBot = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedOn = table.Column<long>(type: "INTEGER", nullable: false),
                    PublishedOn = table.Column<long>(type: "INTEGER", nullable: true),
                    ClosedOn = table.Column<long>(type: "INTEGER", nullable: true),
                    MergeStatus = table.Column<string>(type: "TEXT", nullable: true),
                    SourceBranch = table.Column<string>(type: "TEXT", nullable: true),
                    TargetBranch = table.Column<string>(type: "TEXT", nullable: true),
                    LastMergeCommit = table.Column<string>(type: "TEXT", nullable: true),
                    FilesChanged = table.Column<int>(type: "INTEGER", nullable: true),
                    PolicyStatus = table.Column<string>(type: "TEXT", nullable: true),
                    ReviewersJson = table.Column<string>(type: "TEXT", nullable: false),
                    WebUrl = table.Column<string>(type: "TEXT", nullable: true),
                    ThreadsFetched = table.Column<bool>(type: "INTEGER", nullable: false),
                    RawJson = table.Column<byte[]>(type: "BLOB", nullable: true),
                    LastSyncedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PullRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RepoFacts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RepoId = table.Column<string>(type: "TEXT", nullable: false),
                    Sha = table.Column<string>(type: "TEXT", nullable: false),
                    ScannerVersion = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    FactsJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepoFacts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Repos",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    ProjectName = table.Column<string>(type: "TEXT", nullable: false),
                    DefaultBranch = table.Column<string>(type: "TEXT", nullable: true),
                    WebUrl = table.Column<string>(type: "TEXT", nullable: true),
                    RemoteUrl = table.Column<string>(type: "TEXT", nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    IsDisabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Assigned = table.Column<bool>(type: "INTEGER", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Pinned = table.Column<bool>(type: "INTEGER", nullable: false),
                    Muted = table.Column<bool>(type: "INTEGER", nullable: false),
                    AssignedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    BackfilledAt = table.Column<long>(type: "INTEGER", nullable: true),
                    HeadSha = table.Column<string>(type: "TEXT", nullable: true),
                    HeadCheckedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Repos", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RuleResults",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RepoId = table.Column<string>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    RuleId = table.Column<string>(type: "TEXT", nullable: false),
                    ConfigHash = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<double>(type: "REAL", nullable: true),
                    Display = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    SampleSize = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuleResults", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Runs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    PipelineId = table.Column<int>(type: "INTEGER", nullable: false),
                    RepoId = table.Column<string>(type: "TEXT", nullable: false),
                    BuildNumber = table.Column<string>(type: "TEXT", nullable: true),
                    SourceBranch = table.Column<string>(type: "TEXT", nullable: true),
                    SourceVersion = table.Column<string>(type: "TEXT", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Result = table.Column<string>(type: "TEXT", nullable: true),
                    QueueTime = table.Column<long>(type: "INTEGER", nullable: false),
                    StartTime = table.Column<long>(type: "INTEGER", nullable: true),
                    FinishTime = table.Column<long>(type: "INTEGER", nullable: true),
                    RequestedFor = table.Column<string>(type: "TEXT", nullable: true),
                    WebUrl = table.Column<string>(type: "TEXT", nullable: true),
                    TemplateRef = table.Column<string>(type: "TEXT", nullable: true),
                    TemplateSha = table.Column<string>(type: "TEXT", nullable: true),
                    TemplateAlias = table.Column<string>(type: "TEXT", nullable: true),
                    TimelineFetched = table.Column<bool>(type: "INTEGER", nullable: false),
                    TimelineFetchedFor = table.Column<long>(type: "INTEGER", nullable: true),
                    DeletedInAdo = table.Column<bool>(type: "INTEGER", nullable: false),
                    RawJson = table.Column<byte[]>(type: "BLOB", nullable: true),
                    LastSyncedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Runs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncCursors",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Watermark = table.Column<long>(type: "INTEGER", nullable: true),
                    Value = table.Column<string>(type: "TEXT", nullable: true),
                    LastSuccessAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastErrorAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncCursors", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "TemplateCommits",
                columns: table => new
                {
                    Sha = table.Column<string>(type: "TEXT", nullable: false),
                    Branch = table.Column<string>(type: "TEXT", nullable: false),
                    Date = table.Column<long>(type: "INTEGER", nullable: false),
                    Author = table.Column<string>(type: "TEXT", nullable: true),
                    Message = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TemplateCommits", x => new { x.Branch, x.Sha });
                });

            migrationBuilder.CreateTable(
                name: "TestFlakes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunId = table.Column<int>(type: "INTEGER", nullable: false),
                    RepoId = table.Column<string>(type: "TEXT", nullable: false),
                    TestName = table.Column<string>(type: "TEXT", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false),
                    Flaky = table.Column<bool>(type: "INTEGER", nullable: false),
                    Date = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TestFlakes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TimelineRecords",
                columns: table => new
                {
                    RunId = table.Column<int>(type: "INTEGER", nullable: false),
                    RecordId = table.Column<string>(type: "TEXT", nullable: false),
                    ParentId = table.Column<string>(type: "TEXT", nullable: true),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Identifier = table.Column<string>(type: "TEXT", nullable: true),
                    Attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    IsPreviousAttempt = table.Column<bool>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: true),
                    Result = table.Column<string>(type: "TEXT", nullable: true),
                    StartTime = table.Column<long>(type: "INTEGER", nullable: true),
                    FinishTime = table.Column<long>(type: "INTEGER", nullable: true),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorFingerprint = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimelineRecords", x => new { x.RunId, x.RecordId });
                });

            migrationBuilder.CreateTable(
                name: "UserStates",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserStates", x => x.Key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Approvals_Status",
                table: "Approvals",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_AuditRuns_RepoId_CreatedAt",
                table: "AuditRuns",
                columns: new[] { "RepoId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditRuns_Status",
                table: "AuditRuns",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_BranchPolicies_RepoId",
                table: "BranchPolicies",
                column: "RepoId");

            migrationBuilder.CreateIndex(
                name: "IX_Commits_RepoId_Date",
                table: "Commits",
                columns: new[] { "RepoId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_Components_RepoId_Name",
                table: "Components",
                columns: new[] { "RepoId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Deployments_RepoId_EnvironmentName_StartTime",
                table: "Deployments",
                columns: new[] { "RepoId", "EnvironmentName", "StartTime" });

            migrationBuilder.CreateIndex(
                name: "IX_Deployments_RunId",
                table: "Deployments",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_EnvironmentChecks_EnvironmentName",
                table: "EnvironmentChecks",
                column: "EnvironmentName");

            migrationBuilder.CreateIndex(
                name: "IX_Findings_AuditRunId",
                table: "Findings",
                column: "AuditRunId");

            migrationBuilder.CreateIndex(
                name: "IX_Findings_Fingerprint",
                table: "Findings",
                column: "Fingerprint");

            migrationBuilder.CreateIndex(
                name: "IX_FindingStates_RepoId_Status",
                table: "FindingStates",
                columns: new[] { "RepoId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_HealthSnapshots_RepoId_Date",
                table: "HealthSnapshots",
                columns: new[] { "RepoId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_CreatedAt",
                table: "Jobs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Status_RunAfter",
                table: "Jobs",
                columns: new[] { "Status", "RunAfter" });

            migrationBuilder.CreateIndex(
                name: "IX_Pipelines_RepoId",
                table: "Pipelines",
                column: "RepoId");

            migrationBuilder.CreateIndex(
                name: "IX_PrEvents_PullRequestId",
                table: "PrEvents",
                column: "PullRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_PullRequests_ClosedOn",
                table: "PullRequests",
                column: "ClosedOn");

            migrationBuilder.CreateIndex(
                name: "IX_PullRequests_RepoId_Status",
                table: "PullRequests",
                columns: new[] { "RepoId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_RepoFacts_RepoId_CreatedAt",
                table: "RepoFacts",
                columns: new[] { "RepoId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Repos_Assigned",
                table: "Repos",
                column: "Assigned");

            migrationBuilder.CreateIndex(
                name: "IX_Repos_Name",
                table: "Repos",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_RuleResults_RepoId_Date_RuleId",
                table: "RuleResults",
                columns: new[] { "RepoId", "Date", "RuleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Runs_PipelineId_QueueTime",
                table: "Runs",
                columns: new[] { "PipelineId", "QueueTime" });

            migrationBuilder.CreateIndex(
                name: "IX_Runs_RepoId_QueueTime",
                table: "Runs",
                columns: new[] { "RepoId", "QueueTime" });

            migrationBuilder.CreateIndex(
                name: "IX_Runs_Status",
                table: "Runs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_TestFlakes_RepoId_Date",
                table: "TestFlakes",
                columns: new[] { "RepoId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_TimelineRecords_ErrorFingerprint",
                table: "TimelineRecords",
                column: "ErrorFingerprint");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Approvals");

            migrationBuilder.DropTable(
                name: "AuditRuns");

            migrationBuilder.DropTable(
                name: "BranchPolicies");

            migrationBuilder.DropTable(
                name: "Commits");

            migrationBuilder.DropTable(
                name: "Components");

            migrationBuilder.DropTable(
                name: "Deployments");

            migrationBuilder.DropTable(
                name: "EnvironmentChecks");

            migrationBuilder.DropTable(
                name: "Findings");

            migrationBuilder.DropTable(
                name: "FindingStates");

            migrationBuilder.DropTable(
                name: "HealthSnapshots");

            migrationBuilder.DropTable(
                name: "Jobs");

            migrationBuilder.DropTable(
                name: "Pipelines");

            migrationBuilder.DropTable(
                name: "PrEvents");

            migrationBuilder.DropTable(
                name: "PullRequests");

            migrationBuilder.DropTable(
                name: "RepoFacts");

            migrationBuilder.DropTable(
                name: "Repos");

            migrationBuilder.DropTable(
                name: "RuleResults");

            migrationBuilder.DropTable(
                name: "Runs");

            migrationBuilder.DropTable(
                name: "SyncCursors");

            migrationBuilder.DropTable(
                name: "TemplateCommits");

            migrationBuilder.DropTable(
                name: "TestFlakes");

            migrationBuilder.DropTable(
                name: "TimelineRecords");

            migrationBuilder.DropTable(
                name: "UserStates");
        }
    }
}

namespace Dashboard.Core.Configuration;

/// <summary>Root of config/dashboard.yaml. All property names are camelCase in YAML.</summary>
public sealed class DashboardConfig
{
    public AzureDevOpsConfig AzureDevOps { get; set; } = new();
    public TimeConfig Time { get; set; } = new();
    public List<EnvironmentConfig> Environments { get; set; } = [];
    /// <summary>Tier for any environment not listed in <see cref="Environments"/>.</summary>
    public EnvironmentTier DefaultEnvironmentTier { get; set; } = EnvironmentTier.Team;
    public TemplateConfig Templates { get; set; } = new();
    public List<string> BotIdentities { get; set; } = [];
    public SyncConfig Sync { get; set; } = new();
    public ResultConfig Results { get; set; } = new();
    public MetricsConfig Metrics { get; set; } = new();
    public List<RuleConfig> Rules { get; set; } = [];
    public ComponentsConfig Components { get; set; } = new();
    public AuditConfig Audit { get; set; } = new();
    public NowViewConfig NowView { get; set; } = new();
    public NotificationsConfig Notifications { get; set; } = new();
}

public sealed class AzureDevOpsConfig
{
    /// <summary>"mock" (synthetic data, no network) or "live".</summary>
    public string Mode { get; set; } = "mock";
    public string Organization { get; set; } = "contoso";
    public string Project { get; set; } = "Platform";
    public string ApiVersion { get; set; } = "7.1";
    /// <summary>Base URL override, mainly for tests. Defaults to https://dev.azure.com.</summary>
    public string BaseUrl { get; set; } = "https://dev.azure.com";
    public bool IsMock => string.Equals(Mode, "mock", StringComparison.OrdinalIgnoreCase);
}

public sealed class TimeConfig
{
    /// <summary>IANA time zone id, e.g. Europe/London.</summary>
    public string TimeZone { get; set; } = "Europe/London";
    public string WorkdayStart { get; set; } = "09:00";
    public string WorkdayEnd { get; set; } = "17:30";
    public List<DayOfWeek> WorkDays { get; set; } =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
}

public enum EnvironmentTier { Team, Staging, Production }

public sealed class EnvironmentConfig
{
    public string Name { get; set; } = "";
    public EnvironmentTier Tier { get; set; }
    /// <summary>Regexes (case-insensitive) matched against timeline stage names/identifiers that deploy to this environment.</summary>
    public List<string> StagePatterns { get; set; } = [];
    /// <summary>Optional ADO environment id; needed only for reading check configurations.</summary>
    public int? AdoId { get; set; }
    public int Order { get; set; }
}

public sealed class TemplateConfig
{
    public string Project { get; set; } = "Platform";
    public string Repository { get; set; } = "pipeline-templates";
    /// <summary>Regexes matched against resources.repositories aliases or repository names that identify the shared templates.</summary>
    public List<string> AliasPatterns { get; set; } = ["^templates?$", "pipeline-templates"];
    public List<string> CompliantBranches { get; set; } = ["main"];
    public List<string> ExperimentalBranches { get; set; } = ["next"];
    /// <summary>Informational staleness threshold (days behind branch head).</summary>
    public int StaleInfoDays { get; set; } = 14;
}

public sealed class SyncConfig
{
    public int HotSeconds { get; set; } = 60;
    public int WarmMinutes { get; set; } = 15;
    /// <summary>Local hour (in Time.TimeZone) for the daily job; runs on startup if overdue.</summary>
    public int DailyHour { get; set; } = 2;
    public int BackfillPrDays { get; set; } = 180;
    public int BackfillBuildDays { get; set; } = 365;
    public int OverlapMinutes { get; set; } = 60;
}

public sealed class ResultConfig
{
    /// <summary>Timeline "succeededWithIssues" / build "partiallySucceeded" count as failures ("a failed step = failed").</summary>
    public bool SucceededWithIssuesIsFailure { get; set; } = true;
}

public sealed class MetricsConfig
{
    public int WindowDays { get; set; } = 30;
    public int MinRuns { get; set; } = 10;
    public int MinPrs { get; set; } = 5;
    /// <summary>Use p85 only when the sample has at least this many values; median otherwise.</summary>
    public int PercentileMinSample { get; set; } = 20;
    public double StuckPrIdleWorkingHours { get; set; } = 8;
    public double StalePrWorkingDays { get; set; } = 3;
    public int DormantDays { get; set; } = 30;
}

public sealed class RuleConfig
{
    public string Id { get; set; } = "";
    public string Tier { get; set; } = "Bronze";
    public bool Enabled { get; set; } = true;
    public double? Threshold { get; set; }
    /// <summary>Repo types this rule applies to; empty = all.</summary>
    public List<string> RepoTypes { get; set; } = [];
    public string? Description { get; set; }
}

public sealed class ComponentsConfig
{
    public List<ComponentOverride> Overrides { get; set; } = [];
    public List<DeployRuleConfig> DeployRules { get; set; } = [];
}

public sealed class ComponentOverride
{
    public string Repo { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "AksService";
    public bool Hidden { get; set; }
}

public sealed class DeployRuleConfig
{
    /// <summary>Repo name regex (optional).</summary>
    public string? Repo { get; set; }
    public string? PipelinePattern { get; set; }
    public string? StagePattern { get; set; }
    public string? JobPattern { get; set; }
    public string Component { get; set; } = "";
}

public sealed class AuditConfig
{
    public bool Enabled { get; set; } = true;
    public string AuditorUrl { get; set; } = "http://auditor:8081";
    /// <summary>Preferred model id; falls back to the strongest available.</summary>
    public string? Model { get; set; } = "claude-opus-5.5";
    public string ReasoningEffort { get; set; } = "high";
    public int TimeoutMinutes { get; set; } = 20;
    /// <summary>1 = single run; 3 = majority vote over judgement checks.</summary>
    public int Votes { get; set; } = 1;
    public int MinHoursBetweenAudits { get; set; } = 24;
    public List<string> TriggerGlobs { get; set; } =
        ["**/*.csproj", "**/*.sln", "**/*.slnx", "**/skaffold.yaml", "**/azure-pipelines*.yml", "**/*.pipeline.yml", "**/Dockerfile", "**/*.tf", "**/Chart.yaml", "**/Directory.Packages.props"];
    public string RubricPath { get; set; } = "audit/rubric";
    public List<string> GoldenRepos { get; set; } = [];
    public string ClonesPath { get; set; } = "/clones";
}

public sealed class NowViewConfig
{
    public List<string> Panels { get; set; } =
        ["attention", "matrix", "running", "shipped", "sinceLastLooked"];
    public int ShippedHours { get; set; } = 24;
    public int RecentFailureHours { get; set; } = 24;
}

public sealed class NotificationsConfig
{
    public bool Enabled { get; set; } = true;
    public bool ApprovalWaitingForMe { get; set; } = true;
    public bool ProductionDeployFailed { get; set; } = true;
    public double MainRedHours { get; set; } = 1;
}

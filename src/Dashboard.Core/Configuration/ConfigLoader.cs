using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Dashboard.Core.Configuration;

public sealed record ConfigLoadResult(DashboardConfig Config, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings, string Hash)
{
    public bool IsValid => Errors.Count == 0;
}

public static class ConfigLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static ConfigLoadResult LoadFile(string path)
    {
        if (!File.Exists(path))
        {
            var def = Defaults.Create();
            return new ConfigLoadResult(def, [], [$"Config file '{path}' not found; using built-in defaults (mock mode)."], Hash(""));
        }
        return Parse(File.ReadAllText(path));
    }

    public static ConfigLoadResult Parse(string yaml)
    {
        DashboardConfig config;
        try
        {
            config = string.IsNullOrWhiteSpace(yaml) ? new DashboardConfig() : Deserializer.Deserialize<DashboardConfig>(yaml) ?? new DashboardConfig();
        }
        catch (Exception ex)
        {
            return new ConfigLoadResult(Defaults.Create(), [$"YAML parse error: {ex.Message}"], [], Hash(yaml));
        }

        if (config.Environments.Count == 0) config.Environments = Defaults.Environments();
        if (config.Rules.Count == 0) config.Rules = Defaults.Rules();

        var (errors, warnings) = Validate(config);
        return new ConfigLoadResult(config, errors, warnings, Hash(yaml));
    }

    public static (List<string> Errors, List<string> Warnings) Validate(DashboardConfig c)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (c.AzureDevOps.Mode is not ("mock" or "live"))
            errors.Add($"azureDevOps.mode must be 'mock' or 'live' (was '{c.AzureDevOps.Mode}').");
        if (!c.AzureDevOps.IsMock)
        {
            if (string.IsNullOrWhiteSpace(c.AzureDevOps.Organization)) errors.Add("azureDevOps.organization is required in live mode.");
            if (string.IsNullOrWhiteSpace(c.AzureDevOps.Project)) errors.Add("azureDevOps.project is required in live mode.");
        }

        try { TimeZoneInfo.FindSystemTimeZoneById(c.Time.TimeZone); }
        catch (Exception) { errors.Add($"time.timeZone '{c.Time.TimeZone}' is not a known IANA time zone."); }
        if (!TimeOnly.TryParse(c.Time.WorkdayStart, out var s) || !TimeOnly.TryParse(c.Time.WorkdayEnd, out var e) || e <= s)
            errors.Add("time.workdayStart/workdayEnd must be HH:mm with end after start.");
        if (c.Time.WorkDays.Count == 0) errors.Add("time.workDays must contain at least one day.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var env in c.Environments)
        {
            if (string.IsNullOrWhiteSpace(env.Name)) { errors.Add("environments[].name is required."); continue; }
            if (!names.Add(env.Name)) errors.Add($"Duplicate environment '{env.Name}'.");
            if (env.StagePatterns.Count == 0) warnings.Add($"Environment '{env.Name}' has no stagePatterns; no deployments will be attributed to it.");
            foreach (var p in env.StagePatterns) CheckRegex(p, $"environments[{env.Name}].stagePatterns", errors);
        }
        if (!c.Environments.Any(x => x.Tier == EnvironmentTier.Production))
            warnings.Add("No environment has tier Production; delivery metrics will be empty.");

        foreach (var p in c.Templates.AliasPatterns) CheckRegex(p, "templates.aliasPatterns", errors);
        foreach (var p in c.Pipelines.ReleaseNamePatterns) CheckRegex(p, "pipelines.releaseNamePatterns", errors);
        foreach (var p in c.Pipelines.GateStagePatterns) CheckRegex(p, "pipelines.gateStagePatterns", errors);
        foreach (var g in c.Pipelines.GateEnvironments)
        {
            if (string.IsNullOrWhiteSpace(g.Name)) errors.Add("pipelines.gateEnvironments[].name is required.");
            if (g.AdoId <= 0) errors.Add($"pipelines.gateEnvironments[{g.Name}].adoId must be the environment id from its URL (…/_environments/<id>).");
            if (c.Environments.Any(e => e.Name.Equals(g.Name, StringComparison.OrdinalIgnoreCase)))
                errors.Add($"Gate environment '{g.Name}' clashes with a deployment environment name.");
        }
        foreach (var p in c.Components.AksJobPatterns.Concat(c.Components.DatabaseJobPatterns)) CheckRegex(p, "components job patterns", errors);
        foreach (var r in c.Components.DeployRules)
        {
            if (string.IsNullOrWhiteSpace(r.Component)) errors.Add("components.deployRules[].component is required.");
            foreach (var p in new[] { r.Repo, r.PipelinePattern, r.StagePattern, r.JobPattern }.Where(x => x is not null))
                CheckRegex(p!, "components.deployRules", errors);
        }

        var ruleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in c.Rules)
        {
            if (!ruleIds.Add(r.Id)) errors.Add($"Duplicate rule id '{r.Id}'.");
            if (r.Tier is not ("Bronze" or "Silver" or "Gold")) errors.Add($"Rule {r.Id}: tier must be Bronze, Silver or Gold.");
            if (!Dashboard.Core.Rules.RuleCatalog.Known.ContainsKey(r.Id)) warnings.Add($"Rule {r.Id} is not a known rule id and will be ignored.");
        }

        if (c.Sync.HotSeconds < 15) errors.Add("sync.hotSeconds must be >= 15 (TSTU budget).");
        if (c.Sync.WarmMinutes < 1) errors.Add("sync.warmMinutes must be >= 1.");
        if (c.Sync.DailyHour is < 0 or > 23) errors.Add("sync.dailyHour must be 0-23.");
        if (c.Audit.Votes is not (1 or 3 or 5)) errors.Add("audit.votes must be 1, 3 or 5.");
        if (c.Audit.TimeoutMinutes < 1) errors.Add("audit.timeoutMinutes must be >= 1.");
        if (c.Metrics.WindowDays < 7) errors.Add("metrics.windowDays must be >= 7.");

        return (errors, warnings);
    }

    private static void CheckRegex(string pattern, string where, List<string> errors)
    {
        try { _ = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)); }
        catch (ArgumentException ex) { errors.Add($"{where}: invalid regex '{pattern}': {ex.Message}"); }
    }

    public static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12].ToLowerInvariant();
}

public static class Defaults
{
    public static DashboardConfig Create() => new()
    {
        Environments = Environments(),
        Rules = Rules(),
    };

    /// <summary>
    /// Stage naming used by the shared release template: team envs are "test{name} deploy" (testlemur, testfox2, …);
    /// staging/sim/prod are per region: green (UK) and eu, e.g. "staginggreen deploy", "prodeu deploy".
    /// </summary>
    public static List<EnvironmentConfig> Environments() =>
    [
        new() { Name = "team", Tier = EnvironmentTier.Team, StagePatterns = [@"^test[a-z0-9]+[ _-]?(deploy|apply)$"], Order = 0 },
        new() { Name = "staging-green", Tier = EnvironmentTier.Staging, Region = "green", StagePatterns = [@"^staging[ _-]?green[ _-]?(deploy|apply)$"], Order = 1 },
        new() { Name = "staging-eu", Tier = EnvironmentTier.Staging, Region = "eu", StagePatterns = [@"^staging[ _-]?eu[ _-]?(deploy|apply)$"], Order = 2 },
        new() { Name = "sim-green", Tier = EnvironmentTier.Production, Region = "green", StagePatterns = [@"^sim[ _-]?green[ _-]?(deploy|apply)$"], Order = 3 },
        new() { Name = "sim-eu", Tier = EnvironmentTier.Production, Region = "eu", StagePatterns = [@"^sim[ _-]?eu[ _-]?(deploy|apply)$"], Order = 4 },
        new() { Name = "prod-green", Tier = EnvironmentTier.Production, Region = "green", StagePatterns = [@"^prod[ _-]?green[ _-]?(deploy|apply)$"], Order = 5 },
        new() { Name = "prod-eu", Tier = EnvironmentTier.Production, Region = "eu", StagePatterns = [@"^prod[ _-]?eu[ _-]?(deploy|apply)$"], Order = 6 },
    ];

    public static List<RuleConfig> Rules() =>
    [
        new() { Id = "PIPE-001", Tier = "Bronze" },
        new() { Id = "POL-001", Tier = "Bronze" },
        new() { Id = "POL-002", Tier = "Bronze" },
        new() { Id = "TPL-001", Tier = "Bronze" },
        new() { Id = "DOC-001", Tier = "Bronze" },
        new() { Id = "NET-001", Tier = "Bronze" },
        new() { Id = "BLD-001", Tier = "Silver", Threshold = 0.90 },
        new() { Id = "BLD-002", Tier = "Silver", Threshold = 8 },
        new() { Id = "FLK-001", Tier = "Silver", Threshold = 0.05 },
        new() { Id = "PR-001", Tier = "Silver", Threshold = 16 },
        new() { Id = "TPL-002", Tier = "Silver" },
        new() { Id = "BLD-003", Tier = "Gold", Threshold = 0.95 },
        new() { Id = "PR-002", Tier = "Gold", Threshold = 5 },
        new() { Id = "DEP-001", Tier = "Gold", Threshold = 30, RepoTypes = ["Service"] },
        new() { Id = "TST-001", Tier = "Gold" },
        new() { Id = "AUD-001", Tier = "Gold" },
    ];
}

using Dashboard.Core.Configuration;
using Dashboard.Core.Time;

namespace Dashboard.Web.Services;

/// <summary>Loads config/dashboard.yaml, validates it, and keeps the last valid config on reload errors.</summary>
public sealed class ConfigService
{
    private readonly ILogger<ConfigService> _log;
    private readonly IConfiguration _env;
    private ConfigLoadResult _current;

    public ConfigService(ILogger<ConfigService> log, IConfiguration env, IHostEnvironment host)
    {
        _log = log;
        _env = env;
        Path = env["DASHBOARD_CONFIG"] ?? ResolveDefault(host.ContentRootPath);
        _current = ConfigLoader.LoadFile(Path);
        ApplyEnvOverrides(_current.Config);
        LastLoadErrors = _current.Errors;
        LastLoadWarnings = _current.Warnings;
        if (!_current.IsValid)
        {
            _log.LogError("Config {Path} is invalid: {Errors}. Using defaults.", Path, string.Join("; ", _current.Errors));
            var fallback = Defaults.Create();
            ApplyEnvOverrides(fallback);
            _current = new ConfigLoadResult(fallback, [], _current.Warnings, "defaults");
        }
        WorkingHours = new WorkingHours(_current.Config.Time);
        LoadedAt = DateTimeOffset.UtcNow;
    }

    public string Path { get; }
    public DashboardConfig Current => _current.Config;
    public string Hash => _current.Hash;
    public WorkingHours WorkingHours { get; private set; }
    public DateTimeOffset LoadedAt { get; private set; }
    public IReadOnlyList<string> LastLoadErrors { get; private set; }
    public IReadOnlyList<string> LastLoadWarnings { get; private set; }
    public event Action? Changed;

    public ConfigLoadResult Reload()
    {
        var result = ConfigLoader.LoadFile(Path);
        ApplyEnvOverrides(result.Config);
        LastLoadErrors = result.Errors;
        LastLoadWarnings = result.Warnings;
        if (!result.IsValid)
        {
            _log.LogWarning("Config reload rejected: {Errors}", string.Join("; ", result.Errors));
            return result;
        }
        _current = result;
        WorkingHours = new WorkingHours(result.Config.Time);
        LoadedAt = DateTimeOffset.UtcNow;
        _log.LogInformation("Config reloaded ({Hash})", result.Hash);
        Changed?.Invoke();
        return result;
    }

    /// <summary>Environment variables win over the YAML for deployment-specific values.</summary>
    private void ApplyEnvOverrides(DashboardConfig c)
    {
        if (_env["ADO_MODE"] is { Length: > 0 } mode) c.AzureDevOps.Mode = mode;
        if (_env["ADO_ORGANIZATION"] is { Length: > 0 } org) c.AzureDevOps.Organization = org;
        if (_env["ADO_PROJECT"] is { Length: > 0 } proj) c.AzureDevOps.Project = proj;
        if (_env["ADO_BASE_URL"] is { Length: > 0 } url) c.AzureDevOps.BaseUrl = url;
        if (_env["AUDITOR_URL"] is { Length: > 0 } aud) c.Audit.AuditorUrl = aud;
        if (_env["CLONES_PATH"] is { Length: > 0 } clones) c.Audit.ClonesPath = clones;
        if (_env["RUBRIC_PATH"] is { Length: > 0 } rubric) c.Audit.RubricPath = rubric;
        if (_env["TZ_OVERRIDE"] is { Length: > 0 } tz) c.Time.TimeZone = tz;
    }

    private static string ResolveDefault(string contentRoot)
    {
        foreach (var candidate in new[]
                 {
                     System.IO.Path.Combine(contentRoot, "config", "dashboard.yaml"),
                     System.IO.Path.Combine(contentRoot, "..", "..", "config", "dashboard.yaml"),
                     "/app/config/dashboard.yaml",
                 })
            if (File.Exists(candidate)) return System.IO.Path.GetFullPath(candidate);
        return System.IO.Path.Combine(contentRoot, "config", "dashboard.yaml");
    }

    /// <summary>Resolve a config-relative path (e.g. audit.rubricPath) against the repo/app root.</summary>
    public string ResolvePath(string relativeOrAbsolute)
    {
        if (System.IO.Path.IsPathRooted(relativeOrAbsolute)) return relativeOrAbsolute;
        var configDir = System.IO.Path.GetDirectoryName(Path) ?? ".";
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(configDir, "..", relativeOrAbsolute));
    }
}

public interface IClock { DateTimeOffset UtcNow { get; } }

public sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }

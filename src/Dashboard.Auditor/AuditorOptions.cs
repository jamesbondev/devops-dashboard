namespace Dashboard.Auditor;

/// <summary>
/// Auditor configuration, read from environment variables (via IConfiguration, so tests can
/// override with UseSetting). See the table in the slice brief / docs/plan.md §9.
/// </summary>
public sealed class AuditorOptions
{
    public const string DevToken = "dev-token";

    /// <summary>mock | copilot</summary>
    public string Engine { get; init; } = "mock";
    public string Token { get; init; } = "";
    public bool TokenWasDefaulted { get; init; }
    public string AllowedRoot { get; init; } = "/clones";
    public string? GitHubToken { get; init; }
    public string CopilotHome { get; init; } = "/copilot-home";
    public IReadOnlyList<string> Tools { get; init; } = ["view", "glob", "grep"];
    public string? DefaultModel { get; init; }
    public int MockDelayMs { get; init; } = 1500;
    public TimeSpan JobRetention { get; init; } = TimeSpan.FromHours(24);

    public bool IsMock => string.Equals(Engine, "mock", StringComparison.OrdinalIgnoreCase);
    public bool IsCopilot => string.Equals(Engine, "copilot", StringComparison.OrdinalIgnoreCase);

    public static AuditorOptions FromConfiguration(IConfiguration config)
    {
        static string? Get(IConfiguration c, string key)
        {
            var v = c[key];
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }

        var engine = (Get(config, "AUDITOR_ENGINE") ?? "mock").ToLowerInvariant();
        if (engine is not ("mock" or "copilot"))
        {
            throw new InvalidOperationException($"AUDITOR_ENGINE must be 'mock' or 'copilot' (got '{engine}').");
        }

        var token = Get(config, "AUDITOR_TOKEN");
        var defaulted = false;
        if (token is null)
        {
            if (engine == "copilot")
            {
                throw new InvalidOperationException("AUDITOR_TOKEN must be set when AUDITOR_ENGINE=copilot.");
            }
            token = DevToken;
            defaulted = true;
        }

        var tools = (Get(config, "AUDITOR_TOOLS") ?? "view,glob,grep")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var delay = int.TryParse(Get(config, "AUDITOR_MOCK_DELAY_MS"), out var d) && d >= 0 ? d : 1500;

        var root = Get(config, "AUDITOR_ALLOWED_ROOT") ?? "/clones";
        return new AuditorOptions
        {
            Engine = engine,
            Token = token,
            TokenWasDefaulted = defaulted,
            AllowedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
            GitHubToken = Get(config, "COPILOT_GITHUB_TOKEN"),
            CopilotHome = Get(config, "COPILOT_HOME") ?? "/copilot-home",
            Tools = tools,
            DefaultModel = Get(config, "AUDITOR_DEFAULT_MODEL"),
            MockDelayMs = delay,
        };
    }
}

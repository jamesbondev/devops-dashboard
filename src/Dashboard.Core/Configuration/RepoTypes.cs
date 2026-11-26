using Dashboard.Core.Entities;
using Dashboard.Core.Metrics;

namespace Dashboard.Core.Configuration;

public static class RepoTypes
{
    /// <summary>
    /// Effective repo type: repos.types in config, then a type chosen on the repo page (anything other than the
    /// default Service), then the e2e / load name patterns, else Service.
    /// </summary>
    public static RepoType Resolve(string repoName, RepoType stored, DashboardConfig config)
    {
        var explicitType = config.Repos.Types.FirstOrDefault(t => t.Name.Equals(repoName, StringComparison.OrdinalIgnoreCase));
        if (explicitType is not null && Enum.TryParse<RepoType>(explicitType.Type, ignoreCase: true, out var t)) return t;
        if (stored != RepoType.Service) return stored;
        if (StageClassifier.AnyMatch(config.Repos.E2EPatterns, repoName)) return RepoType.E2E;
        if (StageClassifier.AnyMatch(config.Repos.LoadPatterns, repoName)) return RepoType.Load;
        return RepoType.Service;
    }

    public static bool IsTestRepo(RepoType type) => type is RepoType.E2E or RepoType.Load;

    public static string Label(RepoType type) => type switch { RepoType.E2E => "E2E tests", RepoType.Load => "Load tests", _ => type.ToString() };
}

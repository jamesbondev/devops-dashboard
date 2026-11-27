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
        if (StageClassifier.AnyMatch(config.Repos.NapPatterns, repoName)) return RepoType.Nap;
        return RepoType.Service;
    }

    /// <summary>Docs / config repos (documentation, access-request config, ...): no code, so only repos.docsRules apply and no code audit runs.</summary>
    public static bool IsDocs(RepoType type) => type == RepoType.Docs;

    public static bool IsTestRepo(RepoType type) => type is RepoType.E2E or RepoType.Load;

    public static string Label(RepoType type) => type switch { RepoType.E2E => "E2E tests", RepoType.Load => "Load tests", RepoType.Docs => "Docs / config", RepoType.Nap => "NAP (scripts)", _ => type.ToString() };
}

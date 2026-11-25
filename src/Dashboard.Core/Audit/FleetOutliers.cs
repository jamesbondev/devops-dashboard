using Dashboard.Core.Scanners;

namespace Dashboard.Core.Audit;

public sealed record FleetOutlier(
    string Fact,
    string Repo,
    bool RepoValue,
    bool MajorityValue,
    double MajorityShare,
    int ApplicableRepos);

/// <summary>
/// "Most repos do X, this one doesn't": boolean facts compared across the fleet. A fact only produces outliers when
/// at least <see cref="MinApplicableRepos"/> repos are applicable and one value holds in at least
/// <c>minMajorityShare</c> of them.
/// </summary>
public static class FleetOutliers
{
    public const int MinApplicableRepos = 3;

    public static class Facts
    {
        public const string CentralPackageManagement = "CentralPackageManagement";
        public const string NullableEverywhere = "NullableEverywhere";
        public const string AnalyzersEnabled = "AnalyzersEnabled";
        public const string TreatWarningsAsErrors = "TreatWarningsAsErrors";
        public const string DockerfilesNonRoot = "DockerfilesNonRoot";
        public const string DockerBaseImagesPinned = "DockerBaseImagesPinned";
        public const string K8sProbes = "K8sProbes";
        public const string K8sResourceLimits = "K8sResourceLimits";
        public const string HasReadme = "HasReadme";
        public const string HasCodeowners = "HasCodeowners";
        public const string HasEditorConfig = "HasEditorConfig";
        public const string TerraformLockFile = "TerraformLockFile";
        public const string HasTestProject = "HasTestProject";
    }

    /// <summary>Fact name → extractor; null means "not applicable to this repo".</summary>
    public static readonly IReadOnlyList<(string Fact, Func<RepoFacts, bool?> Extract)> Extractors =
    [
        (Facts.CentralPackageManagement, f => CodeProjects(f).Count == 0 ? null : f.Dotnet.CentralPackageManagement),
        (Facts.NullableEverywhere, f => CodeProjects(f) is { Count: > 0 } p ? p.All(x => x.Nullable == true) : null),
        (Facts.AnalyzersEnabled, f => CodeProjects(f) is { Count: > 0 } p ? p.All(x => x.AnalyzersEnabled) : null),
        (Facts.TreatWarningsAsErrors, f => CodeProjects(f) is { Count: > 0 } p ? p.All(x => x.TreatWarningsAsErrors == true) : null),
        (Facts.DockerfilesNonRoot, f => f.Deploy.Dockerfiles.Count == 0 ? null : f.Deploy.Dockerfiles.All(d => d.NonRootUser)),
        (Facts.DockerBaseImagesPinned, f => f.Deploy.Dockerfiles.Count == 0 ? null : f.Deploy.Dockerfiles.All(d => d.BaseImagesPinned)),
        (Facts.K8sProbes, f => LongRunning(f) is { Count: > 0 } w
            ? w.All(x => x.Containers.All(c => c.HasLivenessProbe && c.HasReadinessProbe)) : null),
        (Facts.K8sResourceLimits, f => f.Deploy.Workloads.Count == 0 ? null : f.Deploy.Workloads.All(x => x.Containers.All(c => c.HasLimits))),
        (Facts.HasReadme, f => f.Docs.HasReadme),
        (Facts.HasCodeowners, f => f.Docs.HasCodeowners),
        (Facts.HasEditorConfig, f => f.Docs.HasEditorConfig),
        (Facts.TerraformLockFile, f => f.Terraform.Directories.Count == 0 ? null : f.Terraform.HasLockFile),
        (Facts.HasTestProject, f => CodeProjects(f).Count == 0 ? null : f.Dotnet.Projects.Any(p => p.IsTestProject)),
    ];

    public static List<FleetOutlier> Compute(IReadOnlyDictionary<string, RepoFacts> facts, double minMajorityShare = 0.6)
    {
        var result = new List<FleetOutlier>();
        foreach (var (fact, extract) in Extractors)
        {
            var values = new List<(string Repo, bool Value)>();
            foreach (var (repo, f) in facts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var v = extract(f);
                if (v.HasValue) values.Add((repo, v.Value));
            }
            if (values.Count < MinApplicableRepos) continue;

            var trueCount = values.Count(v => v.Value);
            var falseCount = values.Count - trueCount;
            if (trueCount == falseCount) continue;   // no majority
            var majority = trueCount > falseCount;
            var share = (double)Math.Max(trueCount, falseCount) / values.Count;
            if (share < minMajorityShare) continue;

            foreach (var (repo, value) in values.Where(v => v.Value != majority))
                result.Add(new FleetOutlier(fact, repo, value, majority, Math.Round(share, 4), values.Count));
        }
        return result;
    }

    private static List<DotnetProject> CodeProjects(RepoFacts f)
        => f.Dotnet.Projects.Where(p => p.Kind is not "Database" and not "Test").ToList();

    private static List<K8sWorkload> LongRunning(RepoFacts f)
        => f.Deploy.Workloads.Where(w => w.Kind is "Deployment" or "StatefulSet" or "DaemonSet").ToList();
}

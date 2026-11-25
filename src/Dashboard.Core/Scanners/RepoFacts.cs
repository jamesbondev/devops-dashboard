namespace Dashboard.Core.Scanners;

/// <summary>
/// Deterministic facts about a repository at a commit, produced by static parsing only
/// (never restore/build/terraform init). Serialised to JSON (camelCase) and stored per SHA.
/// </summary>
public sealed class RepoFacts
{
    public string ScannerVersion { get; set; } = "";
    public string Sha { get; set; } = "";
    public DateTimeOffset ScannedAt { get; set; }

    public LanguageFacts Languages { get; set; } = new();
    public DotnetFacts Dotnet { get; set; } = new();
    public DataFacts Data { get; set; } = new();
    public MessagingFacts Messaging { get; set; } = new();
    public DeployFacts Deploy { get; set; } = new();
    public TerraformFacts Terraform { get; set; } = new();
    public HieraFacts Hiera { get; set; } = new();
    public List<PipelineFileFacts> Pipelines { get; set; } = [];
    public DocsFacts Docs { get; set; } = new();
    public List<ComponentHint> Components { get; set; } = [];
    /// <summary>Azure services inferred from packages/terraform (e.g. "Service Bus", "Key Vault", "SQL Database").</summary>
    public List<string> AzureServices { get; set; } = [];
    public List<string> ScanWarnings { get; set; } = [];
}

public sealed class LanguageFacts
{
    /// <summary>Language name → file count (generated/vendor folders excluded).</summary>
    public Dictionary<string, int> FileCounts { get; set; } = [];
    /// <summary>Language name → line count.</summary>
    public Dictionary<string, long> LineCounts { get; set; } = [];
    public string? Primary { get; set; }
}

public sealed class DotnetFacts
{
    public List<string> Solutions { get; set; } = [];             // .sln / .slnx paths
    public List<DotnetProject> Projects { get; set; } = [];
    public bool CentralPackageManagement { get; set; }            // Directory.Packages.props with ManagePackageVersionsCentrally
    public bool HasDirectoryBuildProps { get; set; }
    public bool HasGlobalJson { get; set; }
    public string? SdkVersion { get; set; }                       // from global.json
    public List<string> TargetFrameworks { get; set; } = [];      // distinct across projects
    public List<string> UnsupportedTargetFrameworks { get; set; } = [];
    public List<PackageVersionInfo> Packages { get; set; } = [];  // distinct id+version
    public int OutdatedPackageCount { get; set; }
    public bool PackageLookupPerformed { get; set; }
}

public sealed class DotnetProject
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Microsoft.NET.Sdk, Microsoft.NET.Sdk.Web, Microsoft.NET.Sdk.Worker, Microsoft.NET.Sdk.Functions, MSBuild.Sdk.SqlProj, ...</summary>
    public string Sdk { get; set; } = "";
    /// <summary>Web, Worker, Functions, Library, Test, Console, Database</summary>
    public string Kind { get; set; } = "Library";
    public List<string> TargetFrameworks { get; set; } = [];
    public bool? Nullable { get; set; }
    public bool? TreatWarningsAsErrors { get; set; }
    public bool AnalyzersEnabled { get; set; }
    public bool IsTestProject { get; set; }
    public string? TestFramework { get; set; }                    // xunit, nunit, mstest
    public List<string> ProjectReferences { get; set; } = [];     // project names
    public List<PackageRef> PackageReferences { get; set; } = [];
}

public sealed class PackageRef
{
    public string Id { get; set; } = "";
    public string? Version { get; set; }                          // null when centrally managed and unresolved
}

public sealed class PackageVersionInfo
{
    public string Id { get; set; } = "";
    public string? Version { get; set; }
    public string? Latest { get; set; }
    public bool Outdated { get; set; }
    public int MajorVersionsBehind { get; set; }
}

public sealed class DataFacts
{
    public bool UsesEfCore { get; set; }
    public bool UsesDapper { get; set; }
    public bool UsesSqlClient { get; set; }
    public bool UsesDbUp { get; set; }
    public bool UsesFlyway { get; set; }
    public List<string> SqlProjects { get; set; } = [];           // .sqlproj paths
    public List<string> MigrationFolders { get; set; } = [];
    public List<string> DbContexts { get; set; } = [];            // class names
    /// <summary>"sqlserver", "postgres", "cosmos", "sqlite", ...</summary>
    public List<string> DatabaseEngines { get; set; } = [];
    /// <summary>Project names containing data access code (DbContext / Dapper usage).</summary>
    public List<string> DataAccessProjects { get; set; } = [];
}

public sealed class MessagingFacts
{
    public bool UsesServiceBus { get; set; }
    public bool UsesMassTransit { get; set; }
    public bool UsesNServiceBus { get; set; }
    public List<string> QueueOrTopicNames { get; set; } = [];
    public bool DeadLetterHandlingFound { get; set; }
    public List<string> ConsumerFiles { get; set; } = [];
}

public sealed class DeployFacts
{
    public List<SkaffoldFile> Skaffold { get; set; } = [];
    public List<DockerfileFacts> Dockerfiles { get; set; } = [];
    public List<K8sWorkload> Workloads { get; set; } = [];
    public List<string> HelmCharts { get; set; } = [];
    public bool UsesDacpac { get; set; }
}

public sealed class SkaffoldFile
{
    public string Path { get; set; } = "";
    public string? ConfigName { get; set; }
    public List<SkaffoldArtifact> Artifacts { get; set; } = [];
    public List<string> Profiles { get; set; } = [];
    /// <summary>helm | kubectl | kustomize</summary>
    public string? Deployer { get; set; }
    public List<string> Manifests { get; set; } = [];
    public List<string> Requires { get; set; } = [];
}

public sealed class SkaffoldArtifact
{
    public string Image { get; set; } = "";
    public string? Context { get; set; }
    public string? Dockerfile { get; set; }
}

public sealed class DockerfileFacts
{
    public string Path { get; set; } = "";
    public List<string> BaseImages { get; set; } = [];
    public bool MultiStage { get; set; }
    public bool BaseImagesPinned { get; set; }                    // no :latest / untagged; digest or explicit tag
    public bool NonRootUser { get; set; }
    public bool HasHealthcheck { get; set; }
}

public sealed class K8sWorkload
{
    public string Path { get; set; } = "";
    public string Kind { get; set; } = "";                        // Deployment, StatefulSet, CronJob, Job
    public string Name { get; set; } = "";
    public List<K8sContainer> Containers { get; set; } = [];
    public int? Replicas { get; set; }
    /// <summary>True when values come from a Helm template we could not fully evaluate.</summary>
    public bool Templated { get; set; }
}

public sealed class K8sContainer
{
    public string Name { get; set; } = "";
    public string? Image { get; set; }
    public bool HasRequests { get; set; }
    public bool HasLimits { get; set; }
    public bool HasLivenessProbe { get; set; }
    public bool HasReadinessProbe { get; set; }
}

public sealed class TerraformFacts
{
    public List<string> Directories { get; set; } = [];
    public string? RequiredVersion { get; set; }
    public Dictionary<string, string> Providers { get; set; } = [];  // name → version constraint
    public string? AzurermVersion { get; set; }
    public string? LatestAzurermVersion { get; set; }
    public string? BackendType { get; set; }                         // azurerm, local, ...
    public List<TerraformModuleRef> Modules { get; set; } = [];
    public bool HasLockFile { get; set; }
    public List<string> ResourceTypes { get; set; } = [];
}

public sealed class TerraformModuleRef
{
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public string? Version { get; set; }
    public bool Pinned { get; set; }
}

public sealed class HieraFacts
{
    public bool Present { get; set; }
    public List<string> Files { get; set; } = [];
    public List<string> HierarchyLevels { get; set; } = [];
    public int KeyCount { get; set; }
}

public sealed class PipelineFileFacts
{
    public string Path { get; set; } = "";
    public bool UsesExtends { get; set; }
    public string? ExtendsTemplate { get; set; }                  // e.g. "templates/service.yml@templates"
    public List<PipelineRepoResource> RepositoryResources { get; set; } = [];
    public int InlineStages { get; set; }
    public int InlineJobs { get; set; }
    public int InlineSteps { get; set; }
    public List<string> Environments { get; set; } = [];
    public List<string> TemplateReferences { get; set; } = [];    // every "template:" reference
}

public sealed class PipelineRepoResource
{
    public string Alias { get; set; } = "";
    public string? Name { get; set; }                             // Project/Repo
    public string? Ref { get; set; }                              // refs/heads/main
    public string? Type { get; set; }
}

public sealed class DocsFacts
{
    public bool HasReadme { get; set; }
    public string? ReadmePath { get; set; }
    public int ReadmeLines { get; set; }
    public bool ReadmeMentionsRun { get; set; }
    public bool ReadmeMentionsDeploy { get; set; }
    public bool HasCodeowners { get; set; }
    public bool HasEditorConfig { get; set; }
    public bool HasGitattributes { get; set; }
}

public sealed class ComponentHint
{
    public string Name { get; set; } = "";
    /// <summary>AksService | Database | Other</summary>
    public string Kind { get; set; } = "AksService";
    /// <summary>skaffold | sqlproj | csproj</summary>
    public string Source { get; set; } = "";
    public string? Image { get; set; }
    public string Path { get; set; } = "";
}

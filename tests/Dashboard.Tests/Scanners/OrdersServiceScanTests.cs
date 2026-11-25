using Dashboard.Core.Scanners;

namespace Dashboard.Tests.Scanners;

/// <summary>Fixture (a): clean-architecture .NET service with API + queue listener.</summary>
public class OrdersServiceScanTests
{
    private static RepoFacts F => Fixtures.Facts(Fixtures.Orders);

    [Fact]
    public void Metadata_is_set()
    {
        Assert.Equal(RepoScanner.Version, F.ScannerVersion);
        Assert.Equal("abc123", F.Sha);
        Assert.Equal(Fixtures.Today, F.ScannedAt);
        Assert.Empty(F.ScanWarnings);
    }

    [Fact]
    public void Languages_count_files_and_lines_and_skip_designer_files()
    {
        Assert.Equal("C#", F.Languages.Primary);
        Assert.Equal(11, F.Languages.FileCounts["C#"]);   // Initial.Designer.cs excluded
        Assert.Equal(2, F.Languages.FileCounts["Dockerfile"]);
        Assert.True(F.Languages.LineCounts["C#"] > 100);
        Assert.Equal(1, F.Languages.FileCounts["SQL"]);
    }

    [Fact]
    public void Dotnet_solution_projects_and_kinds()
    {
        var d = F.Dotnet;
        Assert.Equal(["Orders.slnx"], d.Solutions);
        Assert.Equal(9, d.Projects.Count);
        var kinds = d.Projects.ToDictionary(p => p.Name, p => p.Kind);
        Assert.Equal("Web", kinds["Orders.Api"]);
        Assert.Equal("Worker", kinds["Orders.QueueListener"]);
        Assert.Equal("Library", kinds["Orders.Application"]);
        Assert.Equal("Library", kinds["Orders.Domain"]);
        Assert.Equal("Library", kinds["Orders.Infrastructure"]);
        Assert.Equal("Test", kinds["Orders.Api.Tests"]);
        Assert.Equal("Test", kinds["Orders.Application.Tests"]);
        Assert.Equal("Database", kinds["Orders.Database"]);

        var api = d.Projects.Single(p => p.Name == "Orders.Api");
        Assert.Equal("Microsoft.NET.Sdk.Web", api.Sdk);
        Assert.Equal("src/Orders.Api/Orders.Api.csproj", api.Path);
        Assert.Equal(["Orders.Infrastructure", "Orders.Application"], api.ProjectReferences);
        Assert.Equal("Microsoft.Build.Sql", d.Projects.Single(p => p.Name == "Orders.Database").Sdk);
    }

    [Fact]
    public void Directory_build_props_is_inherited()
    {
        foreach (var p in F.Dotnet.Projects.Where(p => p.Kind != "Database"))
        {
            Assert.Equal(["net10.0"], p.TargetFrameworks);
            Assert.True(p.Nullable);
            Assert.True(p.TreatWarningsAsErrors);
            Assert.True(p.AnalyzersEnabled);
        }
        Assert.True(F.Dotnet.HasDirectoryBuildProps);
        Assert.Equal(["net10.0"], F.Dotnet.TargetFrameworks);
        Assert.Empty(F.Dotnet.UnsupportedTargetFrameworks);
    }

    [Fact]
    public void Central_package_management_resolves_versions()
    {
        Assert.True(F.Dotnet.CentralPackageManagement);
        Assert.Equal("Directory.Packages.props", F.Dotnet.DirectoryPackagesPropsPath);
        Assert.All(F.Dotnet.Projects.Where(p => p.Kind != "Database"), p => Assert.True(p.CentralPackageManagement));
        var infra = F.Dotnet.Projects.Single(p => p.Name == "Orders.Infrastructure");
        Assert.Equal("10.0.0", infra.PackageReferences.Single(r => r.Id == "Microsoft.EntityFrameworkCore.SqlServer").Version);
        Assert.Equal("7.18.1", infra.PackageReferences.Single(r => r.Id == "Azure.Messaging.ServiceBus").Version);
    }

    [Fact]
    public void Global_json_with_comments_is_read()
    {
        Assert.True(F.Dotnet.HasGlobalJson);
        Assert.Equal("10.0.100", F.Dotnet.SdkVersion);
    }

    [Fact]
    public void Test_projects_and_frameworks()
    {
        var tests = F.Dotnet.Projects.Where(p => p.IsTestProject).OrderBy(p => p.Name).ToList();
        Assert.Equal(["Orders.Api.Tests", "Orders.Application.Tests", "Orders.QueueListener.Tests"], tests.Select(t => t.Name));
        Assert.All(tests, t => Assert.Equal("xunit", t.TestFramework));
        Assert.Equal(["Orders.Api"], tests[0].ProjectReferences);
    }

    [Fact]
    public void Packages_are_distinct_with_staleness_from_provider()
    {
        var pkgs = F.Dotnet.Packages;
        Assert.True(F.Dotnet.PackageLookupPerformed);
        var sb = pkgs.Single(p => p.Id == "Azure.Messaging.ServiceBus");
        Assert.Equal("7.18.1", sb.Version);
        Assert.Equal("7.20.1", sb.Latest);
        Assert.True(sb.Outdated);
        Assert.Equal(0, sb.MajorVersionsBehind);
        Assert.Single(pkgs, p => p.Id == "xunit");   // used by 3 test projects, one version
        var identity = pkgs.Single(p => p.Id == "Azure.Identity");
        Assert.Null(identity.Latest);                 // provider doesn't know it
        Assert.False(identity.Outdated);
        Assert.Equal(pkgs.Count(p => p.Outdated), F.Dotnet.OutdatedPackageCount);
    }

    [Fact]
    public void Data_facts()
    {
        var d = F.Data;
        Assert.True(d.UsesEfCore);
        Assert.False(d.UsesDapper);
        Assert.False(d.UsesSqlClient);
        Assert.Equal(["database/Orders.Database/Orders.Database.sqlproj"], d.SqlProjects);
        Assert.Equal(["src/Orders.Infrastructure/Migrations"], d.MigrationFolders);
        Assert.Equal(["OrdersDbContext"], d.DbContexts);   // decoy in *.Designer.cs ignored
        Assert.Equal(["sqlserver"], d.DatabaseEngines);
        Assert.Equal(["Orders.Infrastructure"], d.DataAccessProjects);
    }

    [Fact]
    public void Messaging_facts()
    {
        var m = F.Messaging;
        Assert.True(m.UsesServiceBus);
        Assert.False(m.UsesMassTransit);
        Assert.Equal(["order-events", "orders-created"], m.QueueOrTopicNames);
        Assert.True(m.DeadLetterHandlingFound);
        Assert.Equal(["src/Orders.QueueListener/OrderCreatedWorker.cs"], m.ConsumerFiles);
    }

    [Fact]
    public void Skaffold_multi_document()
    {
        Assert.Equal(2, F.Deploy.Skaffold.Count);
        var main = F.Deploy.Skaffold[0];
        Assert.Equal("skaffold.yaml", main.Path);
        Assert.Equal("orders", main.ConfigName);
        Assert.Equal(2, main.Artifacts.Count);
        Assert.Equal("ordersacr.azurecr.io/orders/orders-api", main.Artifacts[0].Image);
        Assert.Equal(".", main.Artifacts[0].Context);
        Assert.Equal("src/Orders.Api/Dockerfile", main.Artifacts[0].Dockerfile);
        Assert.Equal(["dev", "prod"], main.Profiles);
        Assert.Equal("kubectl", main.Deployer);
        Assert.Equal(["k8s/orders-api.yaml", "k8s/orders-queue-listener.yaml", "k8s/*.yaml"], main.Manifests);

        var shared = F.Deploy.Skaffold[1];
        Assert.Equal("orders-shared", shared.ConfigName);
        Assert.Equal(["../platform/skaffold.yaml"], shared.Requires);
        Assert.Empty(shared.Artifacts);
    }

    [Fact]
    public void Dockerfiles_multi_stage_pinned_non_root()
    {
        var api = F.Deploy.Dockerfiles.Single(d => d.Path == "src/Orders.Api/Dockerfile");
        Assert.Equal(["mcr.microsoft.com/dotnet/sdk:10.0", "mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled"], api.BaseImages);
        Assert.True(api.MultiStage);
        Assert.True(api.BaseImagesPinned);
        Assert.True(api.NonRootUser);
        Assert.False(api.HasHealthcheck);

        var listener = F.Deploy.Dockerfiles.Single(d => d.Path == "src/Orders.QueueListener/Dockerfile");
        Assert.True(listener.BaseImagesPinned);   // digest
        Assert.True(listener.NonRootUser);
    }

    [Fact]
    public void K8s_workloads()
    {
        var w = F.Deploy.Workloads.OrderBy(x => x.Name).ToList();
        Assert.Equal(2, w.Count);   // the Service is not a workload
        var api = w[0];
        Assert.Equal("orders-api", api.Name);
        Assert.Equal("Deployment", api.Kind);
        Assert.Equal(3, api.Replicas);
        Assert.False(api.Templated);
        var c = Assert.Single(api.Containers);
        Assert.True(c.HasRequests && c.HasLimits && c.HasLivenessProbe && c.HasReadinessProbe);
        Assert.Equal("ordersacr.azurecr.io/orders/orders-api", c.Image);

        var listener = Assert.Single(w[1].Containers);
        Assert.True(listener.HasRequests);
        Assert.False(listener.HasLimits);
        Assert.True(listener.HasLivenessProbe);
        Assert.False(listener.HasReadinessProbe);
    }

    [Fact]
    public void Dacpac_and_no_helm()
    {
        Assert.True(F.Deploy.UsesDacpac);
        Assert.Empty(F.Deploy.HelmCharts);
    }

    [Fact]
    public void Pipeline_extends_templates_repo_on_main()
    {
        var p = Assert.Single(F.Pipelines);
        Assert.Equal("azure-pipelines.yml", p.Path);
        Assert.True(p.UsesExtends);
        Assert.Equal("templates/service.yml@templates", p.ExtendsTemplate);
        var r = Assert.Single(p.RepositoryResources);
        Assert.Equal("templates", r.Alias);
        Assert.Equal("Platform/pipeline-templates", r.Name);
        Assert.Equal("refs/heads/main", r.Ref);
        Assert.Equal("git", r.Type);
        Assert.Equal(0, p.InlineStages);
        Assert.Equal(0, p.InlineJobs);
        Assert.Equal(0, p.InlineSteps);
        Assert.Equal(["orders-dev", "orders-prod"], p.Environments);
        Assert.Equal(["templates/service.yml@templates"], p.TemplateReferences);
    }

    [Fact]
    public void Docs_facts()
    {
        var d = F.Docs;
        Assert.True(d.HasReadme);
        Assert.Equal("README.md", d.ReadmePath);
        Assert.True(d.ReadmeLines > 10);
        Assert.True(d.ReadmeMentionsRun);
        Assert.True(d.ReadmeMentionsDeploy);
        Assert.True(d.HasCodeowners);
        Assert.True(d.HasEditorConfig);
        Assert.True(d.HasGitattributes);
    }

    [Fact]
    public void Components_from_skaffold_and_sqlproj()
    {
        var c = F.Components;
        Assert.Equal(3, c.Count);
        var api = c.Single(x => x.Name == "orders-api");
        Assert.Equal("AksService", api.Kind);
        Assert.Equal("skaffold", api.Source);
        Assert.Equal("ordersacr.azurecr.io/orders/orders-api", api.Image);
        Assert.Equal("skaffold.yaml", api.Path);
        Assert.Contains(c, x => x.Name == "orders-queue-listener" && x.Kind == "AksService");
        var db = c.Single(x => x.Kind == "Database");
        Assert.Equal("Orders.Database", db.Name);
        Assert.Equal("sqlproj", db.Source);
    }

    [Fact]
    public void Azure_services_inferred_from_packages()
        => Assert.Equal(["Azure Identity", "Key Vault", "SQL Database", "Service Bus"], F.AzureServices);

    [Fact]
    public void No_terraform_or_hiera()
    {
        Assert.Empty(F.Terraform.Directories);
        Assert.Null(F.Terraform.BackendType);
        Assert.False(F.Terraform.HasLockFile);
        Assert.False(F.Hiera.Present);
    }
}

using Dashboard.Core.Scanners;

namespace Dashboard.Tests.Scanners;

/// <summary>Fixture (b): net6.0, no CPM, :latest base image, root, no probes, inline pipeline, no tests, no README.</summary>
public class LegacyBillingScanTests
{
    private static RepoFacts F => Fixtures.Facts(Fixtures.Legacy);

    [Fact]
    public void Scans_without_warnings() => Assert.Empty(F.ScanWarnings);

    [Fact]
    public void Projects_and_target_frameworks()
    {
        var d = F.Dotnet;
        Assert.Equal(["Billing.sln"], d.Solutions);
        Assert.Equal(2, d.Projects.Count);
        var web = d.Projects.Single(p => p.Name == "Billing.Web");
        Assert.Equal("Web", web.Kind);
        Assert.Equal(["net6.0"], web.TargetFrameworks);
        Assert.Null(web.Nullable);
        Assert.Null(web.TreatWarningsAsErrors);
        Assert.False(web.AnalyzersEnabled);
        Assert.Equal(["Billing.Core"], web.ProjectReferences);

        var core = d.Projects.Single(p => p.Name == "Billing.Core");
        Assert.Equal("Library", core.Kind);
        Assert.Equal(["net6.0", "net48"], core.TargetFrameworks);   // TargetFrameworks (plural) split
        Assert.False(core.Nullable);                                  // explicitly disabled

        Assert.Equal(["net48", "net6.0"], d.TargetFrameworks);
        Assert.Equal(["net48", "net6.0"], d.UnsupportedTargetFrameworks);
    }

    [Fact]
    public void No_cpm_and_inline_versions_including_child_element()
    {
        Assert.False(F.Dotnet.CentralPackageManagement);
        Assert.Null(F.Dotnet.DirectoryPackagesPropsPath);
        Assert.False(F.Dotnet.HasDirectoryBuildProps);
        Assert.False(F.Dotnet.HasGlobalJson);
        var web = F.Dotnet.Projects.Single(p => p.Name == "Billing.Web");
        Assert.Equal("6.2.3", web.PackageReferences.Single(p => p.Id == "Swashbuckle.AspNetCore").Version);
    }

    [Fact]
    public void Package_majors_behind()
    {
        var nj = F.Dotnet.Packages.Single(p => p.Id == "Newtonsoft.Json");   // same version in both projects → one entry
        Assert.True(nj.Outdated);
        Assert.Equal(1, nj.MajorVersionsBehind);
        var sw = F.Dotnet.Packages.Single(p => p.Id == "Swashbuckle.AspNetCore");
        Assert.Equal(3, sw.MajorVersionsBehind);
        var asb = F.Dotnet.Packages.Single(p => p.Id == "Microsoft.Azure.ServiceBus");
        Assert.False(asb.Outdated);   // already latest
    }

    [Fact]
    public void Data_dapper_and_sqlclient()
    {
        var d = F.Data;
        Assert.True(d.UsesDapper);
        Assert.True(d.UsesSqlClient);
        Assert.False(d.UsesEfCore);
        Assert.Empty(d.DbContexts);
        Assert.Empty(d.SqlProjects);
        Assert.Equal(["sqlserver"], d.DatabaseEngines);
        Assert.Equal(["Billing.Web"], d.DataAccessProjects);
    }

    [Fact]
    public void Messaging_legacy_service_bus_and_json_with_comments()
    {
        var m = F.Messaging;
        Assert.True(m.UsesServiceBus);
        Assert.Equal(["invoices", "payments"], m.QueueOrTopicNames);
        Assert.False(m.DeadLetterHandlingFound);
        Assert.Equal(["src/Billing.Web/Messaging/InvoiceListener.cs"], m.ConsumerFiles);
    }

    [Fact]
    public void Dockerfile_latest_single_stage_root()
    {
        var df = Assert.Single(F.Deploy.Dockerfiles);
        Assert.Equal(["mcr.microsoft.com/dotnet/aspnet:latest"], df.BaseImages);
        Assert.False(df.MultiStage);
        Assert.False(df.BaseImagesPinned);
        Assert.False(df.NonRootUser);
    }

    [Fact]
    public void Helm_template_is_parsed_best_effort()
    {
        Assert.Equal(["chart/billing-web/Chart.yaml"], F.Deploy.HelmCharts);
        var w = Assert.Single(F.Deploy.Workloads);
        Assert.True(w.Templated);
        Assert.Equal("Deployment", w.Kind);
        Assert.Equal("billing-web", w.Name);          // templated name → chart name
        Assert.Null(w.Replicas);                      // {{ .Values.replicaCount }}
        var c = Assert.Single(w.Containers);
        Assert.Equal("billing-web", c.Name);
        Assert.Null(c.Image);
        Assert.False(c.HasRequests);                  // values.yaml: resources: {}
        Assert.False(c.HasLimits);
        Assert.False(c.HasLivenessProbe);
        Assert.False(c.HasReadinessProbe);
        Assert.False(F.Deploy.UsesDacpac);
        Assert.Empty(F.Deploy.Skaffold);
    }

    [Fact]
    public void Inline_pipeline_counts()
    {
        var p = Assert.Single(F.Pipelines);
        Assert.False(p.UsesExtends);
        Assert.Null(p.ExtendsTemplate);
        Assert.Empty(p.RepositoryResources);
        Assert.Equal(2, p.InlineStages);
        Assert.Equal(2, p.InlineJobs);
        Assert.Equal(5, p.InlineSteps);               // 4 build steps + 1 deploy step; the template step is excluded
        Assert.Equal(["billing-prod"], p.Environments);
        Assert.Equal(["steps/notify.yml"], p.TemplateReferences);
    }

    [Fact]
    public void No_docs_and_no_tests()
    {
        Assert.False(F.Docs.HasReadme);
        Assert.Null(F.Docs.ReadmePath);
        Assert.False(F.Docs.HasCodeowners);
        Assert.False(F.Docs.HasEditorConfig);
        Assert.DoesNotContain(F.Dotnet.Projects, p => p.IsTestProject);
    }

    [Fact]
    public void Components_fall_back_to_csproj()
    {
        var c = Assert.Single(F.Components);
        Assert.Equal("Billing.Web", c.Name);
        Assert.Equal("AksService", c.Kind);
        Assert.Equal("csproj", c.Source);
        Assert.Equal("src/Billing.Web/Billing.Web.csproj", c.Path);
        Assert.Equal(["SQL Database", "Service Bus"], F.AzureServices);
    }
}

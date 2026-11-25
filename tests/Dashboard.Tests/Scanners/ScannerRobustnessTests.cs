using Dashboard.Core.Scanners;

namespace Dashboard.Tests.Scanners;

public class ScannerRobustnessTests
{
    [Fact]
    public async Task Skips_vendor_and_generated_folders()
    {
        using var repo = new TempRepo()
            .Write("src/App/Program.cs", "class P {}\n")
            .Write("src/App/bin/Debug/Gen.cs", "class G {}\n")
            .Write("src/App/obj/Gen.cs", "class G {}\n")
            .Write("web/node_modules/lib/index.js", "x\n")
            .Write("packages/Foo/Foo.cs", "class F {}\n")
            .Write("infra/.terraform/modules/m/main.tf", "resource \"azurerm_x\" \"y\" {}\n")
            .Write("ui/dist/app.js", "x\n")
            .Write("src/Web/wwwroot/lib/jquery.js", "x\n")
            .Write("src/Web/wwwroot/site.js", "x\n")
            .Write("src/App/Migrations/X.Designer.cs", "class D : DbContext {}\n");
        var f = await repo.ScanAsync();
        Assert.Equal(1, f.Languages.FileCounts["C#"]);
        Assert.Equal(1, f.Languages.FileCounts["JavaScript"]);
        Assert.Empty(f.Terraform.Directories);
        Assert.Empty(f.Data.DbContexts);
    }

    [Fact]
    public async Task Skips_files_over_two_megabytes_with_warning()
    {
        using var repo = new TempRepo()
            .Write("big.cs", new string('a', 2 * 1024 * 1024 + 10))
            .Write("small.cs", "class S {}\n");
        var f = await repo.ScanAsync();
        Assert.Equal(1, f.Languages.FileCounts["C#"]);
        Assert.Contains(f.ScanWarnings, w => w.Contains("larger than 2 MB", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Malformed_files_produce_warnings_not_exceptions()
    {
        using var repo = new TempRepo()
            .Write("src/Bad/Bad.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>")
            .Write("src/Evil/Evil.csproj", "<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><Project>&e;</Project>")
            .Write("global.json", "{ not json")
            .Write("skaffold.yaml", "apiVersion: skaffold/v4\nbuild: [unclosed\n")
            .Write("k8s/deploy.yaml", "kind: Deployment\nspec: {template: [\n")
            .Write("Dockerfile", "RUN echo no from\n")
            .Write("appsettings.json", "{ \"Queue\": ")
            .Write("hiera.yaml", "hierarchy: - : -\n  :")
            .Write("main.tf", "module \"x\" { source = \"./m\"\n")
            .Write("azure-pipelines.yml", "trigger: [\n");
        var f = await repo.ScanAsync();
        Assert.NotEmpty(f.ScanWarnings);
        Assert.Contains(f.ScanWarnings, w => w.Contains("Bad.csproj", StringComparison.Ordinal));
        Assert.Contains(f.ScanWarnings, w => w.Contains("Evil.csproj", StringComparison.Ordinal));   // DTDs are rejected
        Assert.Contains(f.ScanWarnings, w => w.Contains("global.json", StringComparison.Ordinal));
        Assert.Empty(f.Dotnet.Projects);
        Assert.True(f.Dotnet.HasGlobalJson);
        Assert.Null(f.Dotnet.SdkVersion);
        Assert.Empty(f.Pipelines);
    }

    [Fact]
    public async Task Missing_root_returns_warning()
    {
        var f = await Fixtures.Scanner().ScanAsync(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid()), "s", CancellationToken.None);
        Assert.Single(f.ScanWarnings);
        Assert.Equal(RepoScanner.Version, f.ScannerVersion);
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        using var repo = new TempRepo().Write("a.cs", "class A {}\n");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Fixtures.Scanner().ScanAsync(repo.Root, "s", cts.Token));
    }

    [Fact]
    public async Task Provider_failures_degrade_gracefully()
    {
        using var repo = new TempRepo().Write("A/A.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>" +
            "<ItemGroup><PackageReference Include=\"Foo\" Version=\"1.0.0\" /></ItemGroup></Project>");
        var f = await repo.ScanAsync(new ThrowingProvider());
        var pkg = Assert.Single(f.Dotnet.Packages);
        Assert.Null(pkg.Latest);
        Assert.False(f.Dotnet.PackageLookupPerformed);
    }

    private sealed class ThrowingProvider : IPackageMetadataProvider
    {
        public Task<string?> GetLatestVersionAsync(string packageId, CancellationToken ct) => throw new HttpRequestException("offline");
        public Task<string?> GetLatestTerraformProviderVersionAsync(string providerNamespace, string providerType, CancellationToken ct) => throw new HttpRequestException("offline");
    }

    [Fact]
    public async Task Helm_values_supply_injected_resources_and_probes()
    {
        using var repo = new TempRepo()
            .Write("charts/api/Chart.yaml", "apiVersion: v2\nname: api\nversion: 1.0.0\n")
            .Write("charts/api/values.yaml", """
                replicaCount: 2
                resources:
                  requests: { cpu: 100m, memory: 128Mi }
                  limits: { memory: 256Mi }
                livenessProbe:
                  httpGet: { path: /health/live, port: http }
                readinessProbe:
                  httpGet: { path: /health/ready, port: http }
                """)
            .Write("charts/api/templates/deployment.yaml", """
                apiVersion: apps/v1
                kind: Deployment
                metadata:
                  name: {{ include "api.fullname" . }}
                spec:
                  {{- if not .Values.autoscaling.enabled }}
                  replicas: {{ .Values.replicaCount }}
                  {{- end }}
                  template:
                    spec:
                      containers:
                        - name: {{ .Chart.Name }}
                          image: "{{ .Values.image.repository }}:{{ .Values.image.tag | default .Chart.AppVersion }}"
                          livenessProbe:
                            {{- toYaml .Values.livenessProbe | nindent 12 }}
                          readinessProbe:
                            {{- toYaml .Values.readinessProbe | nindent 12 }}
                          {{- with .Values.resources }}
                          resources:
                            {{- toYaml . | nindent 12 }}
                          {{- end }}
                ---
                apiVersion: batch/v1
                kind: CronJob
                metadata:
                  name: cleanup
                spec:
                  jobTemplate:
                    spec:
                      template:
                        spec:
                          containers:
                            - name: cleanup
                              image: busybox:1.36
                """);
        var f = await repo.ScanAsync();
        Assert.Equal(2, f.Deploy.Workloads.Count);
        var w = f.Deploy.Workloads.Single(x => x.Kind == "Deployment");
        Assert.True(w.Templated);
        Assert.Equal("api", w.Name);
        var c = Assert.Single(w.Containers);
        Assert.True(c.HasRequests);
        Assert.True(c.HasLimits);
        Assert.True(c.HasLivenessProbe);
        Assert.True(c.HasReadinessProbe);
        var cron = f.Deploy.Workloads.Single(x => x.Kind == "CronJob");
        Assert.Equal("cleanup", cron.Name);
        Assert.Equal("busybox:1.36", Assert.Single(cron.Containers).Image);
    }

    [Fact]
    public async Task Dockerfile_variants()
    {
        using var repo = new TempRepo()
            .Write("a/Dockerfile", "FROM --platform=$BUILDPLATFORM node:20-alpine AS build\nFROM build AS test\nFROM nginx\nUSER root\nHEALTHCHECK CMD curl -f http://localhost/\n")
            .Write("b/api.Dockerfile", "ARG BASE=mcr.microsoft.com/dotnet/aspnet:10.0\nFROM ${BASE}\nUSER 1000:1000\n")
            .Write("c/Dockerfile", "FROM registry.local:5000/team/app\nUSER 0\n");
        var f = await repo.ScanAsync();
        var a = f.Deploy.Dockerfiles.Single(d => d.Path == "a/Dockerfile");
        Assert.Equal(["node:20-alpine", "nginx"], a.BaseImages);   // "build" stage reference excluded
        Assert.True(a.MultiStage);
        Assert.False(a.BaseImagesPinned);                           // nginx untagged
        Assert.False(a.NonRootUser);
        Assert.True(a.HasHealthcheck);
        var b = f.Deploy.Dockerfiles.Single(d => d.Path == "b/api.Dockerfile");
        Assert.Equal(["mcr.microsoft.com/dotnet/aspnet:10.0"], b.BaseImages);
        Assert.True(b.BaseImagesPinned);
        Assert.True(b.NonRootUser);
        var c = f.Deploy.Dockerfiles.Single(d => d.Path == "c/Dockerfile");
        Assert.False(c.BaseImagesPinned);                           // registry port is not a tag
        Assert.False(c.NonRootUser);
    }

    [Fact]
    public async Task Hiera_default_data_dir_and_v3_hierarchy()
    {
        using var repo = new TempRepo()
            .Write("hiera.yaml", ":hierarchy:\n  - \"nodes/%{::fqdn}\"\n  - common\n")
            .Write("data/common.yaml", "a: 1\nb: 2\n")
            .Write("data/nodes/x.json", "{\"c\": 3}")
            .Write("other/data/ignored.yaml", "z: 1\n");
        var f = await repo.ScanAsync();
        Assert.True(f.Hiera.Present);
        Assert.Equal(["nodes/%{::fqdn}", "common"], f.Hiera.HierarchyLevels);
        Assert.Equal(3, f.Hiera.KeyCount);
        Assert.DoesNotContain("other/data/ignored.yaml", f.Hiera.Files);
    }

    [Fact]
    public async Task Csproj_property_substitution_conditions_and_functions_kind()
    {
        using var repo = new TempRepo()
            .Write("Directory.Build.props", "<Project><PropertyGroup><DefaultTfm>net8.0</DefaultTfm><Nullable>enable</Nullable></PropertyGroup></Project>")
            .Write("src/Directory.Build.props",
                "<Project><Import Project=\"$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))\" />" +
                "<PropertyGroup><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>" +
                "<ItemGroup><PackageReference Include=\"StyleCop.Analyzers\" Version=\"1.1.118\" /></ItemGroup></Project>")
            .Write("src/Fn/Fn.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>$(DefaultTfm)</TargetFramework><AzureFunctionsVersion>v4</AzureFunctionsVersion><OutputType>Exe</OutputType></PropertyGroup>" +
                "<PropertyGroup Condition=\"'$(Configuration)'=='Release'\"><TreatWarningsAsErrors>false</TreatWarningsAsErrors></PropertyGroup>" +
                "<ItemGroup><PackageReference Include=\"Microsoft.Azure.Functions.Worker\" Version=\"2.0.0\" /></ItemGroup></Project>")
            .Write("src/Tool/Tool.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework></PropertyGroup></Project>")
            .Write("legacy/Old.csproj", "<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"><PropertyGroup><TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion><OutputType>Library</OutputType></PropertyGroup></Project>");
        var f = await repo.ScanAsync();
        var fn = f.Dotnet.Projects.Single(p => p.Name == "Fn");
        Assert.Equal("Functions", fn.Kind);
        Assert.Equal(["net8.0"], fn.TargetFrameworks);
        Assert.True(fn.Nullable);                 // from the outer Directory.Build.props via the import chain
        Assert.True(fn.TreatWarningsAsErrors);    // unconditional value beats the conditional override
        Assert.True(fn.AnalyzersEnabled);         // StyleCop from Directory.Build.props
        Assert.Contains(fn.PackageReferences, p => p.Id == "StyleCop.Analyzers");

        var tool = f.Dotnet.Projects.Single(p => p.Name == "Tool");
        Assert.Equal("Console", tool.Kind);
        Assert.Equal(["net10.0-windows"], tool.TargetFrameworks);

        var old = f.Dotnet.Projects.Single(p => p.Name == "Old");
        Assert.Equal("Classic", old.Sdk);
        Assert.Equal(["net472"], old.TargetFrameworks);

        Assert.Equal(["net10.0-windows", "net472", "net8.0"], f.Dotnet.TargetFrameworks);
        Assert.Equal(["net472", "net8.0"], f.Dotnet.UnsupportedTargetFrameworks);   // net8.0 ended 2026-11-10
        Assert.Equal("Fn", Assert.Single(f.Components).Name);
        Assert.Equal("Other", f.Components[0].Kind);
    }

    [Fact]
    public async Task Masstransit_endpoints_and_classic_sqlproj()
    {
        using var repo = new TempRepo()
            .Write("Svc/Svc.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Worker\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>" +
                "<ItemGroup><PackageReference Include=\"MassTransit.Azure.ServiceBus.Core\" Version=\"8.3.0\" /><PackageReference Include=\"Npgsql.EntityFrameworkCore.PostgreSQL\" Version=\"10.0.0\" /></ItemGroup></Project>")
            .Write("Svc/Consumers.cs", """
                public class OrderConsumer : IConsumer<OrderSubmitted> { }
                public class Setup {
                    void Configure(IServiceBusBusFactoryConfigurator cfg) {
                        cfg.ReceiveEndpoint("order-submitted", e => { e.UseMessageRetry(r => r.Interval(3, 1000)); });
                        cfg.SubscriptionEndpoint("billing-sub", "order-events", e => { });
                    }
                }
                public class AppDb(DbContextOptions<AppDb> o) : IdentityDbContext<User>(o) { }
                """)
            .Write("Db/Legacy.sqlproj", "<Project DefaultTargets=\"Build\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"><Import Project=\"$(SQLDBExtensionsRefPath)\\Microsoft.Data.Tools.Schema.SqlTasks.targets\" /></Project>");
        var f = await repo.ScanAsync();
        Assert.True(f.Messaging.UsesMassTransit);
        Assert.True(f.Messaging.UsesServiceBus);
        Assert.Equal(["billing-sub", "order-events", "order-submitted"], f.Messaging.QueueOrTopicNames);
        Assert.True(f.Messaging.DeadLetterHandlingFound);   // explicit retry policy
        Assert.Equal(["Svc/Consumers.cs"], f.Messaging.ConsumerFiles);
        Assert.Equal(["AppDb"], f.Data.DbContexts);
        Assert.Contains("postgres", f.Data.DatabaseEngines);
        Assert.Contains("sqlserver", f.Data.DatabaseEngines);   // from the sqlproj
        var db = f.Dotnet.Projects.Single(p => p.Name == "Legacy");
        Assert.Equal("Database", db.Kind);
        Assert.Equal("Microsoft.Data.Tools.Schema.SqlTasks", db.Sdk);
        Assert.Equal(["Db/Legacy.sqlproj"], f.Data.SqlProjects);
        Assert.True(f.Deploy.UsesDacpac);
    }

    [Fact]
    public async Task Pipeline_detected_by_top_level_keys_and_dacpac_task()
    {
        using var repo = new TempRepo()
            .Write("build/ci.yml", """
                stages:
                  - template: stages/build.yml@templates
                  - stage: Db
                    jobs:
                      - job: Deploy
                        steps:
                          - task: SqlAzureDacpacDeployment@1
                            inputs: { DacpacFile: db.dacpac }
                          - checkout: self
                  - stage: Prod
                    jobs:
                      - deployment: Prod
                        environment:
                          name: prod-env
                          resourceType: Kubernetes
                        strategy:
                          runOnce:
                            deploy:
                              steps:
                                - script: echo hi
                """)
            .Write("config/settings.yml", "foo: bar\n");
        var f = await repo.ScanAsync();
        var p = Assert.Single(f.Pipelines);
        Assert.Equal("build/ci.yml", p.Path);
        Assert.Equal(2, p.InlineStages);
        Assert.Equal(2, p.InlineJobs);
        Assert.Equal(3, p.InlineSteps);
        Assert.Equal(["prod-env"], p.Environments);
        Assert.Equal(["stages/build.yml@templates"], p.TemplateReferences);
        Assert.True(f.Deploy.UsesDacpac);
    }
}

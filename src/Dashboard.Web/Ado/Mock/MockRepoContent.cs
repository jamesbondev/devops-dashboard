using Dashboard.Core.Entities;

namespace Dashboard.Web.Ado.Mock;

/// <summary>Writes a plausible repository tree for a synthetic repo so scanners and audits have something real to read.</summary>
public static class MockRepoContent
{
    public static void Write(string root, MockWorld.MockRepo repo, string sha)
    {
        Directory.CreateDirectory(root);
        var pascal = string.Concat(repo.Name.Split('-').Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
        var legacy = repo.Legacy;
        var cpm = !legacy && repo.Index % 6 != 3;
        var probes = !legacy && repo.Index % 7 != 2;
        var limits = !legacy && repo.Index % 5 != 4;
        var nonRoot = !legacy;
        var testsForListener = repo.Index % 4 != 1;
        var tfm = string.IsNullOrEmpty(repo.Tfm) ? "net10.0" : repo.Tfm;

        void F(string rel, string content)
        {
            var path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content.Replace("\r\n", "\n"));
        }

        if (!legacy)
            F("README.md", $"# {repo.Name}\n\nOwned by the team. {sha[..7]}\n\n## Run locally\n\n`dotnet run --project src/{pascal}.Api`\n\n## Build\n\n`dotnet build`\n\n## Deploy\n\nDeployed via the shared pipeline templates (`extends`) and skaffold to AKS.\n");
        F(".editorconfig", "root = true\n[*.cs]\nindent_style = space\n");
        if (repo.Index % 3 != 0) F("CODEOWNERS", $"* @team-{repo.Name}\n");

        if (repo.Type == RepoType.Infra)
        {
            if (repo.Name.Contains("hiera"))
            {
                F("hiera.yaml", "---\nversion: 5\nhierarchy:\n  - name: \"Per-node\"\n    path: \"nodes/%{trusted.certname}.yaml\"\n  - name: \"Per-environment\"\n    path: \"environments/%{environment}.yaml\"\n  - name: \"Common\"\n    path: \"common.yaml\"\n");
                F("hieradata/common.yaml", "---\nprofile::base::ntp_servers:\n  - 0.pool.ntp.org\nprofile::app::log_level: info\nprofile::app::servicebus_namespace: sb-platform\n");
                F("hieradata/environments/prod.yaml", "---\nprofile::app::log_level: warn\n");
            }
            else
            {
                F("terraform/main.tf", "terraform {\n  required_version = \">= 1.9.0\"\n  required_providers {\n    azurerm = {\n      source  = \"hashicorp/azurerm\"\n      version = \"~> 3.110\"\n    }\n  }\n  backend \"azurerm\" {}\n}\n\nprovider \"azurerm\" {\n  features {}\n}\n\nresource \"azurerm_servicebus_namespace\" \"sb\" {\n  name                = \"sb-platform\"\n  location            = \"uksouth\"\n  resource_group_name = \"rg-platform\"\n  sku                 = \"Standard\"\n}\n\nresource \"azurerm_mssql_server\" \"sql\" {\n  name                = \"sql-platform\"\n  resource_group_name = \"rg-platform\"\n  location            = \"uksouth\"\n  version             = \"12.0\"\n}\n\nmodule \"naming\" {\n  source = \"git::https://dev.azure.com/contoso/Platform/_git/terraform-modules//naming?ref=v2.3.0\"\n}\n\nmodule \"kv\" {\n  source = \"git::https://dev.azure.com/contoso/Platform/_git/terraform-modules//keyvault\"\n}\n");
                F("terraform/.terraform.lock.hcl", "provider \"registry.terraform.io/hashicorp/azurerm\" {\n  version = \"3.110.0\"\n}\n");
            }
            Pipeline(F, repo, deploy: false);
            return;
        }

        var projects = new List<string>();
        var csprojProps = legacy ? "" : "    <Nullable>enable</Nullable>\n    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>\n    <EnableNETAnalyzers>true</EnableNETAnalyzers>\n";
        string Ver(string v) => cpm ? "" : $" Version=\"{v}\"";

        if (cpm)
        {
            F("Directory.Packages.props", "<Project>\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n  <ItemGroup>\n" +
              "    <PackageVersion Include=\"Microsoft.EntityFrameworkCore.SqlServer\" Version=\"10.0.0\" />\n    <PackageVersion Include=\"Azure.Messaging.ServiceBus\" Version=\"7.18.0\" />\n" +
              "    <PackageVersion Include=\"Azure.Identity\" Version=\"1.13.0\" />\n    <PackageVersion Include=\"Dapper\" Version=\"2.1.35\" />\n    <PackageVersion Include=\"xunit\" Version=\"2.9.3\" />\n    <PackageVersion Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.14.1\" />\n  </ItemGroup>\n</Project>\n");
        }
        F("Directory.Build.props", $"<Project>\n  <PropertyGroup>\n    <TargetFramework>{tfm}</TargetFramework>\n{csprojProps}  </PropertyGroup>\n</Project>\n");

        // Domain / Application / Infrastructure layering for most; legacy is a flat project.
        var clean = !legacy && repo.Index % 3 != 2;
        if (repo.Type == RepoType.Library)
        {
            F($"src/{pascal}/{pascal}.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <PackageReference Include=\"Azure.Messaging.ServiceBus\"{Ver("7.18.0")} />\n  </ItemGroup>\n</Project>\n");
            F($"src/{pascal}/MessagePublisher.cs", "namespace Lib;\npublic sealed class MessagePublisher { }\n");
            F($"tests/{pascal}.Tests/{pascal}.Tests.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <PackageReference Include=\"xunit\"{Ver("2.9.3")} />\n    <ProjectReference Include=\"../../src/{pascal}/{pascal}.csproj\" />\n  </ItemGroup>\n</Project>\n");
            projects.AddRange([$"src/{pascal}/{pascal}.csproj", $"tests/{pascal}.Tests/{pascal}.Tests.csproj"]);
            F($"{pascal}.slnx", Slnx(projects));
            Pipeline(F, repo, deploy: false);
            return;
        }

        if (clean)
        {
            F($"src/{pascal}.Domain/{pascal}.Domain.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n</Project>\n");
            F($"src/{pascal}.Domain/Order.cs", "namespace Domain;\npublic sealed record Order(Guid Id, decimal Total);\n");
            F($"src/{pascal}.Application/{pascal}.Application.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <ProjectReference Include=\"../{pascal}.Domain/{pascal}.Domain.csproj\" />\n  </ItemGroup>\n</Project>\n");
            F($"src/{pascal}.Application/Handlers/CreateOrderHandler.cs", "namespace Application.Handlers;\npublic sealed class CreateOrderHandler { }\n");
            F($"src/{pascal}.Infrastructure/{pascal}.Infrastructure.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <PackageReference Include=\"Microsoft.EntityFrameworkCore.SqlServer\"{Ver("10.0.0")} />\n    <PackageReference Include=\"Azure.Identity\"{Ver("1.13.0")} />\n    <ProjectReference Include=\"../{pascal}.Application/{pascal}.Application.csproj\" />\n  </ItemGroup>\n</Project>\n");
            F($"src/{pascal}.Infrastructure/Persistence/{pascal}DbContext.cs", $"using Microsoft.EntityFrameworkCore;\nnamespace Infrastructure.Persistence;\npublic sealed class {pascal}DbContext(DbContextOptions<{pascal}DbContext> o) : DbContext(o) {{ }}\n");
            F($"src/{pascal}.Infrastructure/Persistence/Migrations/20260101000000_Initial.cs", "namespace Infrastructure.Persistence.Migrations;\npublic partial class Initial { }\n");
            F($"src/{pascal}.Infrastructure/Persistence/Migrations/{pascal}DbContextModelSnapshot.cs", "namespace Infrastructure.Persistence.Migrations;\npublic partial class Snapshot { }\n");
            projects.AddRange([$"src/{pascal}.Domain/{pascal}.Domain.csproj", $"src/{pascal}.Application/{pascal}.Application.csproj", $"src/{pascal}.Infrastructure/{pascal}.Infrastructure.csproj"]);
        }

        foreach (var c in repo.Components)
        {
            if (c.Kind == ComponentKind.Database)
            {
                F($"database/{c.Name}/{c.Name}.sqlproj", "<Project Sdk=\"MSBuild.Sdk.SqlProj/3.0.0\">\n  <PropertyGroup>\n    <SqlServerVersion>SqlAzure</SqlServerVersion>\n  </PropertyGroup>\n</Project>\n");
                F($"database/{c.Name}/Tables/Orders.sql", "CREATE TABLE [dbo].[Orders] ([Id] UNIQUEIDENTIFIER NOT NULL PRIMARY KEY, [Total] DECIMAL(18,2) NOT NULL);\n");
                projects.Add($"database/{c.Name}/{c.Name}.sqlproj");
                continue;
            }
            var isListener = c.Name.EndsWith("-listener", StringComparison.Ordinal);
            var proj = isListener ? $"{pascal}.QueueListener" : $"{pascal}.Api";
            var sdk = isListener ? "Microsoft.NET.Sdk.Worker" : "Microsoft.NET.Sdk.Web";
            var refs = clean ? $"    <ProjectReference Include=\"../{pascal}.Infrastructure/{pascal}.Infrastructure.csproj\" />\n" : "";
            var pkgs = isListener ? $"    <PackageReference Include=\"Azure.Messaging.ServiceBus\"{Ver("7.18.0")} />\n" : (clean ? "" : $"    <PackageReference Include=\"Dapper\"{Ver("2.1.35")} />\n");
            F($"src/{proj}/{proj}.csproj", $"<Project Sdk=\"{sdk}\">\n  <ItemGroup>\n{pkgs}{refs}  </ItemGroup>\n</Project>\n");
            if (isListener)
            {
                var dlq = !legacy && repo.Index % 2 == 0;
                F($"src/{proj}/OrderCreatedConsumer.cs",
                    "using Azure.Messaging.ServiceBus;\nnamespace Listener;\npublic sealed class OrderCreatedConsumer(ServiceBusClient client)\n{\n" +
                    $"    private readonly ServiceBusProcessor _processor = client.CreateProcessor(\"{repo.Name}-events\", new ServiceBusProcessorOptions {{ MaxConcurrentCalls = 4 }});\n" +
                    (dlq ? "    // Poison messages: after MaxDeliveryCount the message is dead-lettered explicitly with a reason.\n    public static Task DeadLetter(ProcessMessageEventArgs a) => a.DeadLetterMessageAsync(a.Message, \"poison\");\n" : "") +
                    "}\n");
                F($"src/{proj}/appsettings.json", $"{{\n  \"ServiceBus\": {{ \"Queue\": \"{repo.Name}-events\" }}\n}}\n");
            }
            else
            {
                F($"src/{proj}/Program.cs", "var builder = WebApplication.CreateBuilder(args);\nvar app = builder.Build();\n" + (probes ? "app.MapGet(\"/health/live\", () => \"ok\");\napp.MapGet(\"/health/ready\", () => \"ok\");\n" : "") + "app.Run();\n");
                F($"src/{proj}/Controllers/OrdersController.cs", "namespace Api.Controllers;\npublic sealed class OrdersController { }\n");
                if (!clean) F($"src/{proj}/Repositories/OrderRepository.cs", "using Dapper;\nnamespace Api.Repositories;\npublic sealed class OrderRepository { }\n");
            }
            F($"src/{proj}/Dockerfile",
                (legacy ? "FROM mcr.microsoft.com/dotnet/aspnet:latest\nCOPY . /app\nWORKDIR /app\n" :
                    $"FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build\nWORKDIR /src\nCOPY . .\nRUN dotnet publish src/{proj} -c Release -o /out\n\nFROM mcr.microsoft.com/dotnet/aspnet:10.0\nWORKDIR /app\nCOPY --from=build /out .\n") +
                (nonRoot ? "USER app\n" : "") + $"ENTRYPOINT [\"dotnet\", \"{proj}.dll\"]\n");
            F($"deploy/k8s/{c.Name}.yaml",
                $"apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: {c.Name}\nspec:\n  replicas: 2\n  template:\n    spec:\n      containers:\n        - name: {c.Name}\n          image: acrplatform.azurecr.io/{c.Name}\n" +
                (limits ? "          resources:\n            requests:\n              cpu: 100m\n              memory: 256Mi\n            limits:\n              memory: 512Mi\n" : "") +
                (probes && !isListener ? "          livenessProbe:\n            httpGet:\n              path: /health/live\n              port: 8080\n          readinessProbe:\n            httpGet:\n              path: /health/ready\n              port: 8080\n" : "") +
                (probes && isListener ? "          livenessProbe:\n            exec:\n              command: [\"/bin/sh\", \"-c\", \"test -f /tmp/alive\"]\n          readinessProbe:\n            exec:\n              command: [\"/bin/sh\", \"-c\", \"test -f /tmp/ready\"]\n" : ""));
            projects.Add($"src/{proj}/{proj}.csproj");
            if (!legacy && (!isListener || testsForListener))
            {
                F($"tests/{proj}.Tests/{proj}.Tests.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <PackageReference Include=\"xunit\"{Ver("2.9.3")} />\n    <PackageReference Include=\"Microsoft.NET.Test.Sdk\"{Ver("17.14.1")} />\n    <ProjectReference Include=\"../../src/{proj}/{proj}.csproj\" />\n  </ItemGroup>\n</Project>\n");
                F($"tests/{proj}.Tests/SmokeTests.cs", "public class SmokeTests { [Xunit.Fact] public void Works() { } }\n");
                projects.Add($"tests/{proj}.Tests/{proj}.Tests.csproj");
            }
        }

        var aks = repo.Components.Where(c => c.Kind == ComponentKind.AksService).ToList();
        if (aks.Count > 0)
        {
            var artifacts = string.Concat(aks.Select(c =>
            {
                var proj = c.Name.EndsWith("-listener", StringComparison.Ordinal) ? $"{pascal}.QueueListener" : $"{pascal}.Api";
                return $"    - image: acrplatform.azurecr.io/{c.Name}\n      context: .\n      docker:\n        dockerfile: src/{proj}/Dockerfile\n";
            }));
            F("skaffold.yaml", $"apiVersion: skaffold/v4beta11\nkind: Config\nmetadata:\n  name: {repo.Name}\nbuild:\n  artifacts:\n{artifacts}manifests:\n  rawYaml:\n" +
                string.Concat(aks.Select(c => $"    - deploy/k8s/{c.Name}.yaml\n")) + "deploy:\n  kubectl: {}\nprofiles:\n  - name: staging\n  - name: sim\n  - name: prod\n");
        }
        F($"{pascal}.slnx", Slnx(projects));
        Pipeline(F, repo, deploy: true);
    }

    private static string Slnx(IEnumerable<string> projects) =>
        "<Solution>\n" + string.Concat(projects.Where(p => p.EndsWith(".csproj", StringComparison.Ordinal)).Select(p => $"  <Project Path=\"{p}\" />\n")) + "</Solution>\n";

    private static void Pipeline(Action<string, string> f, MockWorld.MockRepo repo, bool deploy)
    {
        var refName = $"refs/heads/{repo.TemplateBranch}";
        if (repo.Legacy)
        {
            f("azure-pipelines.yml", "trigger:\n  - main\npool:\n  vmImage: ubuntu-latest\nstages:\n  - stage: Build\n    jobs:\n      - job: Compile\n        steps:\n          - script: dotnet build\n          - script: dotnet test\n  - stage: Deploy_prod\n    jobs:\n      - deployment: Deploy\n        environment: prod\n        strategy:\n          runOnce:\n            deploy:\n              steps:\n                - script: echo deploy\n");
            return;
        }
        var resources = $"resources:\n  repositories:\n    - repository: templates\n      type: git\n      name: cbi/cbi-yaml-templates\n      ref: {refName}\n";
        f("azure-pipelines.yml", "trigger:\n  - main\n" + resources + $"extends:\n  template: v3/ci_build_v4.yaml@templates\n  parameters:\n    serviceName: {repo.Name}\n");
        if (deploy && repo.ReleasePipelineId is not null)
            f("release.yml", "trigger: none\n" + resources + $"extends:\n  template: v3/software_release_v5.yaml@templates\n  parameters:\n    serviceName: {repo.Name}\n    regions: [green, eu]\n    teamEnvironment: lemur\n");
        if (repo.NightlyPipelineId is not null)
            f("pipelines/nightly.yml", $"schedules:\n  - cron: \"0 1 * * 1-5\"\n    branches:\n      include: [main]\nresources:\n  repositories:\n    - repository: templates\n      type: git\n      name: cbi/cbi-yaml-templates\n      ref: {refName}\nextends:\n  template: v3/nightly_v2.yaml@templates\n");
    }
}

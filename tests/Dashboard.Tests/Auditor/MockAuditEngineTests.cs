using System.Text.Json;
using Dashboard.Auditor;
using Dashboard.Auditor.Engines;
using Dashboard.Core.Contracts;
using Dashboard.Core.Scanners;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dashboard.Tests.Auditor;

public sealed class MockAuditEngineTests : IDisposable
{
    private readonly TempDir _root = new("auditor-mock-");

    public void Dispose() => _root.Dispose();

    private MockAuditEngine Engine() => new(
        new AuditorOptions { AllowedRoot = _root.Path, MockDelayMs = 0 },
        NullLogger<MockAuditEngine>.Instance);

    private static List<AuditCheck> Checks(params string[] ids) =>
        ids.Select(id => new AuditCheck { Id = id, Title = id }).ToList();

    private AuditRequest Request(string repo, string factsJson = "{}", params string[] checks) => new()
    {
        AuditId = "t-" + repo,
        RepoName = repo,
        Sha = "0123456789abcdef",
        WorktreePath = Path.Combine(_root.Path, repo),
        FactsJson = factsJson,
        Checks = Checks(checks),
    };

    private void CleanRepo()
    {
        _root.Write("clean/Shop.slnx", "<Solution />");
        _root.Write("clean/README.md", "# Shop");
        _root.Write("clean/src/Shop.Domain/Shop.Domain.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.EntityFrameworkCore" />
                <ProjectReference Include="..\Shop.Infrastructure\Shop.Infrastructure.csproj" />
              </ItemGroup>
            </Project>
            """);
        _root.Write("clean/src/Shop.Domain/Order.cs", "namespace Shop.Domain; public class Order {}");
        _root.Write("clean/src/Shop.Application/Shop.Application.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        _root.Write("clean/src/Shop.Infrastructure/Shop.Infrastructure.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        _root.Write("clean/src/Shop.Infrastructure/ShopDbContext.cs", """
            using Microsoft.EntityFrameworkCore;
            namespace Shop.Infrastructure;
            public class ShopDbContext : DbContext { }
            """);
        _root.Write("clean/src/Shop.Api/Shop.Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");
        _root.Write("clean/src/Shop.Api/Reports.cs", """
            using Dapper;
            namespace Shop.Api;
            public class Reports { }
            """);
        _root.Write("clean/src/Shop.Worker/Shop.Worker.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Worker\" />");
        _root.Write("clean/src/Shop.Worker/OrderConsumer.cs", """
            using Azure.Messaging.ServiceBus;
            public class OrderConsumer
            {
                private ServiceBusProcessor _processor = null!;
                // Messages that fail MaxDeliveryCount times are moved to the dead-letter queue.
                public void Handle(ProcessMessageEventArgs args) => args.DeadLetterMessageAsync(args.Message);
            }
            """);
        _root.Write("clean/src/Shop.Worker/appsettings.json", """{ "ServiceBus": { "RetryOptions": { "MaxRetries": 5 } } }""");
    }

    [Fact]
    public async Task Detects_clean_architecture_with_real_evidence()
    {
        CleanRepo();
        var facts = JsonSerializer.Serialize(new RepoFacts
        {
            AzureServices = ["Service Bus", "SQL Database"],
            Components = [new ComponentHint { Name = "shop-api", Kind = "AksService", Path = "src/Shop.Api/Shop.Api.csproj" }],
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var r = await Engine().RunAuditAsync(Request("clean", facts, "ARCH-001", "ARCH-002", "DATA-001", "MSG-001", "TEST-999"), CancellationToken.None);

        Assert.Equal(MockAuditEngine.ModelName, r.Model);
        Assert.StartsWith("sha256:", r.PromptHash, StringComparison.Ordinal);
        Assert.Equal("clean", r.Result.ArchitectureStyle);
        Assert.Equal(3, r.Result.ArchitectureEvidence.Count);
        Assert.Equal(["Service Bus", "SQL Database"], r.Result.AzureDependencies);
        Assert.Equal("shop-api", Assert.Single(r.Result.Components).Name);
        Assert.Equal(5, r.Result.Checks.Count);

        var arch002 = r.Result.Checks.Single(c => c.CheckId == "ARCH-002");
        Assert.Equal("fail", arch002.Result);
        Assert.All(arch002.Evidence, e => Assert.Equal("src/Shop.Domain/Shop.Domain.csproj", e.Path));
        Assert.Equal(2, arch002.Evidence.Count);
        Assert.All(arch002.Evidence, e => Assert.NotNull(e.Line));

        var data = r.Result.Checks.Single(c => c.CheckId == "DATA-001");
        Assert.Equal("partial", data.Result); // EF in Infrastructure, Dapper in Api
        Assert.Contains(data.Evidence, e => e.Path == "src/Shop.Infrastructure/ShopDbContext.cs");

        var msg = r.Result.Checks.Single(c => c.CheckId == "MSG-001");
        Assert.Equal("pass", msg.Result);

        var other = r.Result.Checks.Single(c => c.CheckId == "TEST-999");
        Assert.Contains(other.Result, new[] { "pass", "partial" });

        // Every cited path really exists inside the worktree.
        var all = r.Result.ArchitectureEvidence.Concat(r.Result.Checks.SelectMany(c => c.Evidence));
        Assert.All(all, e => Assert.True(File.Exists(Path.Combine(_root.Path, "clean", e.Path)) || Directory.Exists(Path.Combine(_root.Path, "clean", e.Path)), e.Path));
        Assert.Contains("[mock]", r.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Is_deterministic()
    {
        CleanRepo();
        var engine = Engine();
        var a = await engine.RunAuditAsync(Request("clean", "{}", "ARCH-002", "DATA-001", "OTHER-1"), CancellationToken.None);
        var b = await engine.RunAuditAsync(Request("clean", "{}", "ARCH-002", "DATA-001", "OTHER-1"), CancellationToken.None);
        Assert.Equal(JsonSerializer.Serialize(a.Result), JsonSerializer.Serialize(b.Result));
        Assert.Equal(a.PromptHash, b.PromptHash);
    }

    [Fact]
    public async Task Detects_layered_and_vertical_slice_and_other()
    {
        _root.Sub("layered/Web/Controllers");
        _root.Sub("layered/Web/Services");
        _root.Write("layered/Web/Repositories/OrderRepository.cs", "class R {}");
        _root.Write("slices/src/Features/Orders/CreateOrder.cs", "class C {}");
        _root.Write("plain/Program.cs", "class P {}");

        var engine = Engine();
        Assert.Equal("layered", (await engine.RunAuditAsync(Request("layered"), CancellationToken.None)).Result.ArchitectureStyle);
        var slices = await engine.RunAuditAsync(Request("slices"), CancellationToken.None);
        Assert.Equal("vertical-slice", slices.Result.ArchitectureStyle);
        Assert.Contains(slices.Result.ArchitectureEvidence, e => e.Path == "src/Features");
        Assert.Equal("other", (await engine.RunAuditAsync(Request("plain"), CancellationToken.None)).Result.ArchitectureStyle);
    }

    [Fact]
    public async Task Clean_domain_passes_and_missing_messaging_is_na()
    {
        _root.Write("ok/src/Acme.Domain/Acme.Domain.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"MediatR.Contracts\" /></ItemGroup></Project>");
        _root.Write("ok/src/Acme.Application/Acme.Application.csproj", "<Project />");
        _root.Write("ok/src/Acme.Infrastructure/Acme.Infrastructure.csproj", "<Project />");

        var r = await Engine().RunAuditAsync(Request("ok", "{}", "ARCH-002", "MSG-001", "DATA-001"), CancellationToken.None);
        Assert.Equal("pass", r.Result.Checks.Single(c => c.CheckId == "ARCH-002").Result);
        Assert.Equal("na", r.Result.Checks.Single(c => c.CheckId == "MSG-001").Result);
        Assert.Equal("na", r.Result.Checks.Single(c => c.CheckId == "DATA-001").Result);
    }

    [Fact]
    public async Task Messaging_without_retry_or_dead_letter_fails()
    {
        _root.Write("msg/Worker/Consumer.cs", "public class C { ServiceBusClient _c; }");
        var r = await Engine().RunAuditAsync(Request("msg", "{}", "MSG-001"), CancellationToken.None);
        var msg = r.Result.Checks.Single();
        Assert.Equal("fail", msg.Result);
        Assert.Equal("Worker/Consumer.cs", msg.Evidence.Single().Path);
    }

    [Fact]
    public async Task Ignores_symlinks_out_of_the_worktree()
    {
        using var outside = new TempDir();
        outside.Write("Features/Secret/Leak.cs", "class L {}");
        _root.Write("linky/Program.cs", "class P {}");
        Directory.CreateSymbolicLink(Path.Combine(_root.Path, "linky", "src"), outside.Path);

        var r = await Engine().RunAuditAsync(Request("linky"), CancellationToken.None);
        Assert.Equal("other", r.Result.ArchitectureStyle);
        Assert.DoesNotContain(r.Result.ArchitectureEvidence, e => e.Path.StartsWith("src", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rejects_worktree_outside_allowed_root()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Engine().RunAuditAsync(new AuditRequest { WorktreePath = "/etc" }, CancellationToken.None));
    }

    [Theory]
    [InlineData("weekly-brief", "Weekly brief")]
    [InlineData("template-summary", "Template change summary")]
    public async Task Text_tasks_format_input(string kind, string heading)
    {
        var r = await Engine().RunTextAsync(new TextTaskRequest { Kind = kind, Input = "diff --git a/x.yml b/x.yml\n+++ b/x.yml\n+added\n-removed\nmain is red" }, CancellationToken.None);
        Assert.StartsWith(heading, r.Text, StringComparison.Ordinal);
        Assert.Equal("mock", r.Engine);
        Assert.Equal(MockAuditEngine.ModelName, r.Model);
    }
}

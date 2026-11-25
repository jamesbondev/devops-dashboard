using Dashboard.Core.Audit;
using Dashboard.Core.Contracts;
using Dashboard.Core.Scanners;
using Dashboard.Tests.Scanners;

namespace Dashboard.Tests.Audit;

public class FactCheckEvaluatorTests
{
    private static readonly List<AuditCheck> FactChecks = RubricLoader.Load(RubricLoaderTests.RubricRoot).FactChecks.ToList();

    private static List<CheckOutcome> Eval(string fixture) => FactCheckEvaluator.Evaluate(Fixtures.Facts(fixture), FactChecks);

    private static Dictionary<(string, string?), string> Results(List<CheckOutcome> outcomes)
        => outcomes.ToDictionary(o => (o.CheckId, o.Component), o => o.Result);

    [Fact]
    public void Orders_service_outcomes()
    {
        var outcomes = Eval(Fixtures.Orders);
        var r = Results(outcomes);
        Assert.Equal("pass", r[("NET-001", null)]);
        Assert.Equal("pass", r[("NET-002", null)]);
        Assert.Equal("pass", r[("K8S-001", "orders-api")]);
        Assert.Equal("partial", r[("K8S-001", "orders-queue-listener")]);   // requests, no limits
        Assert.Equal("pass", r[("K8S-002", "orders-api")]);
        Assert.Equal("partial", r[("K8S-002", "orders-queue-listener")]);   // liveness only
        Assert.Equal("pass", r[("DOCK-001", "orders-api")]);                 // component mapped via skaffold artifact
        Assert.Equal("pass", r[("DOCK-001", "orders-queue-listener")]);
        Assert.Equal("pass", r[("TEST-001", "Orders.Api")]);
        Assert.Equal("pass", r[("TEST-001", "Orders.QueueListener")]);
        Assert.Equal(10, outcomes.Count);

        Assert.All(outcomes, o =>
        {
            Assert.Equal(1, o.Confidence);
            Assert.False(string.IsNullOrWhiteSpace(o.Explanation));
            Assert.NotEmpty(o.Evidence);
            Assert.All(o.Evidence, e => Assert.True(File.Exists(Path.Combine(Fixtures.Path(Fixtures.Orders), e.Path)), $"missing evidence file {e.Path}"));
        });

        var listenerLimits = outcomes.Single(o => o.CheckId == "K8S-001" && o.Component == "orders-queue-listener");
        Assert.Contains("limits", listenerLimits.Explanation, StringComparison.Ordinal);
        Assert.NotNull(listenerLimits.Recommendation);
        Assert.Equal("k8s/orders-queue-listener.yaml", Assert.Single(listenerLimits.Evidence).Path);
        Assert.Null(outcomes.Single(o => o.CheckId == "NET-001").Recommendation);
    }

    [Fact]
    public void Legacy_outcomes()
    {
        var outcomes = Eval(Fixtures.Legacy);
        var r = Results(outcomes);
        Assert.Equal("fail", r[("NET-001", null)]);
        Assert.Equal("fail", r[("NET-002", null)]);
        Assert.Equal("fail", r[("K8S-001", "billing-web")]);
        Assert.Equal("fail", r[("K8S-002", "billing-web")]);
        Assert.Equal("fail", r[("DOCK-001", "Billing.Web")]);
        Assert.Equal("fail", r[("TEST-001", "Billing.Web")]);

        var net = outcomes.Single(o => o.CheckId == "NET-001");
        Assert.Contains("net6.0", net.Explanation, StringComparison.Ordinal);
        Assert.Contains("net10.0", net.Recommendation, StringComparison.Ordinal);
        Assert.Equal(["src/Billing.Core/Billing.Core.csproj", "src/Billing.Web/Billing.Web.csproj"], net.Evidence.Select(e => e.Path));

        var dock = outcomes.Single(o => o.CheckId == "DOCK-001");
        Assert.Contains(":latest", dock.Explanation, StringComparison.Ordinal);
        Assert.Contains("root", dock.Explanation, StringComparison.Ordinal);
        Assert.All(outcomes, o => Assert.All(o.Evidence, e => Assert.True(File.Exists(Path.Combine(Fixtures.Path(Fixtures.Legacy), e.Path)))));
    }

    [Fact]
    public void Infra_repo_is_not_applicable_everywhere()
    {
        var outcomes = Eval(Fixtures.Infra);
        Assert.Equal(6, outcomes.Count);
        Assert.All(outcomes, o => Assert.Equal("na", o.Result));
    }

    [Fact]
    public void Evaluation_is_deterministic()
    {
        var a = System.Text.Json.JsonSerializer.Serialize(Eval(Fixtures.Orders));
        var b = System.Text.Json.JsonSerializer.Serialize(Eval(Fixtures.Orders));
        Assert.Equal(a, b);
    }

    [Fact]
    public void Unknown_fact_check_is_na()
    {
        var o = Assert.Single(FactCheckEvaluator.Evaluate(new RepoFacts(), [new AuditCheck { Id = "SEC-999", Kind = "fact" }]));
        Assert.Equal("SEC-999", o.CheckId);
        Assert.Equal("na", o.Result);
    }

    [Fact]
    public void Partial_cases_for_net_and_tests()
    {
        var facts = new RepoFacts();
        facts.Dotnet.UnsupportedTargetFrameworks = ["net48"];
        facts.Dotnet.Projects =
        [
            new DotnetProject { Name = "Lib", Path = "Lib/Lib.csproj", TargetFrameworks = ["net10.0", "net48"], CentralPackageManagement = true },
            new DotnetProject { Name = "Api", Path = "Api/Api.csproj", Kind = "Web", TargetFrameworks = ["net10.0"], ProjectReferences = ["Lib"] },
            new DotnetProject { Name = "Lib.Tests", Path = "Lib.Tests/Lib.Tests.csproj", Kind = "Test", IsTestProject = true, TargetFrameworks = ["net10.0"], ProjectReferences = ["Lib"], CentralPackageManagement = true },
        ];
        var checks = new[] { "NET-001", "NET-002", "TEST-001" }.Select(id => new AuditCheck { Id = id, Kind = "fact" });
        var r = Results(FactCheckEvaluator.Evaluate(facts, checks));
        Assert.Equal("partial", r[("NET-001", null)]);       // multi-targets an unsupported TFM alongside net10.0
        Assert.Equal("partial", r[("NET-002", null)]);       // CPM covers 2 of 3
        Assert.Equal("partial", r[("TEST-001", "Api")]);     // only its dependency is tested
    }

    [Fact]
    public void Dockerfile_partial_and_jobs_only_probes_na()
    {
        var facts = new RepoFacts();
        facts.Deploy.Dockerfiles = [new DockerfileFacts { Path = "Dockerfile", BaseImages = ["nginx:1.27"], MultiStage = false, BaseImagesPinned = true, NonRootUser = true }];
        facts.Deploy.Workloads = [new K8sWorkload { Path = "job.yaml", Kind = "CronJob", Name = "nightly", Containers = [new K8sContainer { Name = "c" }] }];
        var outcomes = FactCheckEvaluator.Evaluate(facts, new[] { "DOCK-001", "K8S-002" }.Select(id => new AuditCheck { Id = id, Kind = "fact" }));
        var dock = outcomes.Single(o => o.CheckId == "DOCK-001");
        Assert.Equal("partial", dock.Result);
        Assert.Null(dock.Component);   // root Dockerfile
        Assert.Contains("single-stage", dock.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("na", outcomes.Single(o => o.CheckId == "K8S-002").Result);
    }
}

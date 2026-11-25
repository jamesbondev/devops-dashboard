using System.Net;
using System.Text;
using Dashboard.Core.Scanners;

namespace Dashboard.Tests.Scanners;

public class SemanticVersionTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.1")]
    [InlineData("1.9.0", "1.10.0")]
    [InlineData("1.0.0-beta", "1.0.0")]
    [InlineData("1.0.0-alpha", "1.0.0-beta")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.10")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-rc.1", "1.0.0-rc.1.1")]
    [InlineData("1.0", "1.0.0.1")]
    [InlineData("4.8.5", "10.0.0")]
    public void Ordering(string lower, string higher)
    {
        var a = SemanticVersion.Parse(lower)!;
        var b = SemanticVersion.Parse(higher)!;
        Assert.True(a < b);
        Assert.True(b > a);
    }

    [Fact]
    public void Equality_ignores_metadata_and_trailing_zeros()
    {
        Assert.Equal(SemanticVersion.Parse("1.0.0+abc"), SemanticVersion.Parse("1.0"));
        Assert.Equal(SemanticVersion.Parse("1.0.0-RC.1"), SemanticVersion.Parse("1.0.0-rc.1"));
    }

    [Theory]
    [InlineData("[1.2.3]", "1.2.3")]
    [InlineData("[1.0,2.0)", "1.0")]
    [InlineData("(, 3.0]", null)]
    [InlineData("1.*", null)]
    [InlineData("$(EfVersion)", null)]
    [InlineData("6.0.0-preview.7.21377.19", "6.0.0-preview.7.21377.19")]
    public void Loose_parse(string input, string? expected)
        => Assert.Equal(expected, SemanticVersion.ParseLoose(input)?.Original);

    [Theory]
    [InlineData("12.0.3", "13.0.3", true, 1)]
    [InlineData("13.0.3", "13.0.3", false, 0)]
    [InlineData("10.0.0-rc.2", "10.0.0", true, 0)]
    [InlineData("11.0.0-preview.1", "10.0.5", false, 0)]
    [InlineData("1.*", "3.0.0", false, 0)]
    public void Staleness(string current, string latest, bool outdated, int majors)
    {
        var p = new PackageVersionInfo { Id = "X", Version = current, Latest = latest };
        RepoScanner.ApplyStaleness(p);
        Assert.Equal(outdated, p.Outdated);
        Assert.Equal(majors, p.MajorVersionsBehind);
    }
}

public class PackageMetadataProviderTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Nuget_picks_latest_stable_lowercases_id_and_caches()
    {
        var handler = new StubHandler(_ => Json("""{"versions":["1.0.0","2.0.0","2.1.0-beta.1","1.10.0"]}"""));
        using var http = new HttpClient(handler);
        var p = new NuGetPackageMetadataProvider(http);
        Assert.Equal("2.0.0", await p.GetLatestVersionAsync("Newtonsoft.Json", CancellationToken.None));
        Assert.Equal("2.0.0", await p.GetLatestVersionAsync("newtonsoft.json", CancellationToken.None));
        var call = Assert.Single(handler.Calls);
        Assert.Equal("https://api.nuget.org/v3-flatcontainer/newtonsoft.json/index.json", call.ToString());
    }

    [Fact]
    public async Task Nuget_404_and_bad_json_return_null()
    {
        using var http404 = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        Assert.Null(await new NuGetPackageMetadataProvider(http404).GetLatestVersionAsync("nope", CancellationToken.None));
        using var httpBad = new HttpClient(new StubHandler(_ => Json("<html>")));
        Assert.Null(await new NuGetPackageMetadataProvider(httpBad).GetLatestVersionAsync("bad", CancellationToken.None));
    }

    [Fact]
    public async Task Nuget_goes_offline_after_network_failure()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("no network"));
        using var http = new HttpClient(handler);
        var p = new NuGetPackageMetadataProvider(http);
        Assert.Null(await p.GetLatestVersionAsync("a", CancellationToken.None));
        Assert.True(p.IsOffline);
        Assert.Null(await p.GetLatestVersionAsync("b", CancellationToken.None));
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Terraform_registry_reads_version()
    {
        var handler = new StubHandler(_ => Json("""{"id":"hashicorp/azurerm/4.50.0","version":"4.50.0","versions":["4.49.0","4.50.0"]}"""));
        using var http = new HttpClient(handler);
        var p = new TerraformRegistryMetadataProvider(http);
        Assert.Equal("4.50.0", await p.GetLatestTerraformProviderVersionAsync("hashicorp", "azurerm", CancellationToken.None));
        Assert.Equal("https://registry.terraform.io/v1/providers/hashicorp/azurerm", Assert.Single(handler.Calls).ToString());
        Assert.Null(await p.GetLatestVersionAsync("x", CancellationToken.None));
    }

    [Fact]
    public async Task Composite_and_offline()
    {
        var offline = OfflinePackageMetadataProvider.Instance;
        Assert.Null(await offline.GetLatestVersionAsync("x", CancellationToken.None));
        Assert.Null(await offline.GetLatestTerraformProviderVersionAsync("hashicorp", "azurerm", CancellationToken.None));
        var composite = new CompositePackageMetadataProvider(offline, FakePackageMetadataProvider.Default());
        Assert.Equal("13.0.3", await composite.GetLatestVersionAsync("Newtonsoft.Json", CancellationToken.None));
        Assert.Equal("4.50.0", await composite.GetLatestTerraformProviderVersionAsync("hashicorp", "azurerm", CancellationToken.None));
    }
}

using System.Collections.Concurrent;
using Dashboard.Core.Scanners;

namespace Dashboard.Tests.Scanners;

/// <summary>Offline package metadata with canned answers.</summary>
public sealed class FakePackageMetadataProvider : IPackageMetadataProvider
{
    private readonly Dictionary<string, string> _latest;
    private readonly string? _azurerm;
    public ConcurrentBag<string> Requested { get; } = [];

    public FakePackageMetadataProvider(Dictionary<string, string>? latest = null, string? azurerm = null)
    {
        _latest = new Dictionary<string, string>(latest ?? [], StringComparer.OrdinalIgnoreCase);
        _azurerm = azurerm;
    }

    public static FakePackageMetadataProvider Default() => new(new()
    {
        ["Azure.Messaging.ServiceBus"] = "7.20.1",
        ["Microsoft.EntityFrameworkCore.SqlServer"] = "10.0.15",
        ["Newtonsoft.Json"] = "13.0.3",
        ["Dapper"] = "2.1.66",
        ["System.Data.SqlClient"] = "4.9.0",
        ["Microsoft.Azure.ServiceBus"] = "5.2.0",
        ["Swashbuckle.AspNetCore"] = "9.0.6",
        ["xunit"] = "2.9.3",
    }, azurerm: "4.50.0");

    public Task<string?> GetLatestVersionAsync(string packageId, CancellationToken ct)
    {
        Requested.Add(packageId);
        return Task.FromResult(_latest.TryGetValue(packageId, out var v) ? v : null);
    }

    public Task<string?> GetLatestTerraformProviderVersionAsync(string providerNamespace, string providerType, CancellationToken ct)
        => Task.FromResult(providerNamespace == "hashicorp" && providerType == "azurerm" ? _azurerm : null);
}

public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

public static class Fixtures
{
    public static readonly DateTimeOffset Today = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    private static readonly ConcurrentDictionary<string, Lazy<RepoFacts>> Cache = new();

    public static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (Directory.Exists(System.IO.Path.Combine(dir.FullName, "tests", "fixtures", "repos"))) return dir.FullName;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException("Could not locate tests/fixtures/repos above " + AppContext.BaseDirectory);
        }
    }

    public static string Path(string name) => System.IO.Path.Combine(RepoRoot, "tests", "fixtures", "repos", name);

    public static RepoScanner Scanner(IPackageMetadataProvider? provider = null)
        => new(provider ?? FakePackageMetadataProvider.Default(), new FixedTimeProvider(Today));

    /// <summary>Facts for a fixture repo, scanned once per test run with the default fake provider.</summary>
    public static RepoFacts Facts(string name)
        => Cache.GetOrAdd(name, n => new Lazy<RepoFacts>(() =>
            Scanner().ScanAsync(Path(n), "abc123", CancellationToken.None).GetAwaiter().GetResult())).Value;

    public const string Orders = "orders-service";
    public const string Legacy = "legacy-billing";
    public const string Infra = "platform-infra";
}

/// <summary>A throwaway repo directory for edge-case tests.</summary>
public sealed class TempRepo : IDisposable
{
    public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dash-scan-" + Guid.NewGuid().ToString("N"));

    public TempRepo() => Directory.CreateDirectory(Root);

    public TempRepo Write(string relativePath, string content)
    {
        var full = System.IO.Path.Combine(Root, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return this;
    }

    public Task<RepoFacts> ScanAsync(IPackageMetadataProvider? provider = null)
        => Fixtures.Scanner(provider ?? OfflinePackageMetadataProvider.Instance).ScanAsync(Root, "sha", CancellationToken.None);

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

using System.Collections.Concurrent;
using System.Text.Json;

namespace Dashboard.Core.Scanners;

/// <summary>Looks up the latest published versions of packages/providers. Implementations must never throw
/// for network/data problems; they return null instead (cancellation may still throw).</summary>
public interface IPackageMetadataProvider
{
    /// <summary>Latest stable NuGet version of <paramref name="packageId"/>, or null when unknown/offline.</summary>
    Task<string?> GetLatestVersionAsync(string packageId, CancellationToken ct);

    /// <summary>Latest version of a Terraform provider (e.g. "hashicorp", "azurerm"), or null when unknown/offline.</summary>
    Task<string?> GetLatestTerraformProviderVersionAsync(string providerNamespace, string providerType, CancellationToken ct);
}

/// <summary>Never looks anything up. Use for tests and offline scans.</summary>
public sealed class OfflinePackageMetadataProvider : IPackageMetadataProvider
{
    public static readonly OfflinePackageMetadataProvider Instance = new();

    public Task<string?> GetLatestVersionAsync(string packageId, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<string?> GetLatestTerraformProviderVersionAsync(string providerNamespace, string providerType, CancellationToken ct)
        => Task.FromResult<string?>(null);
}

/// <summary>
/// NuGet flat-container lookups (<c>https://api.nuget.org/v3-flatcontainer/{id}/index.json</c>).
/// Picks the highest stable version, caches per instance and stops calling after the first network failure.
/// </summary>
public sealed class NuGetPackageMetadataProvider : IPackageMetadataProvider
{
    public const string DefaultBaseUrl = "https://api.nuget.org/v3-flatcontainer/";

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _offline;

    public NuGetPackageMetadataProvider(HttpClient http, string? baseUrl = null)
    {
        _http = http;
        _baseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/') + "/";
    }

    /// <summary>True once a network failure has been seen; further lookups return null immediately.</summary>
    public bool IsOffline => _offline;

    public Task<string?> GetLatestVersionAsync(string packageId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(packageId) || _offline) return Task.FromResult<string?>(null);
        return _cache.GetOrAdd(packageId.Trim(), id => new Lazy<Task<string?>>(() => FetchAsync(id, ct))).Value;
    }

    public Task<string?> GetLatestTerraformProviderVersionAsync(string providerNamespace, string providerType, CancellationToken ct)
        => Task.FromResult<string?>(null);

    private async Task<string?> FetchAsync(string id, CancellationToken ct)
    {
        try
        {
            var url = $"{_baseUrl}{Uri.EscapeDataString(id.ToLowerInvariant())}/index.json";
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array) return null;
            return PickLatestStable(versions.EnumerateArray().Select(v => v.GetString()));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _cache.TryRemove(id, out _);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            _offline = true;   // network down or timing out: degrade for the rest of this instance's life
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string? PickLatestStable(IEnumerable<string?> versions)
    {
        SemanticVersion? best = null;
        foreach (var s in versions)
        {
            if (!SemanticVersion.TryParse(s, out var v) || v.IsPrerelease) continue;
            if (best is null || v > best) best = v;
        }
        return best?.Original;
    }
}

/// <summary>
/// Terraform Registry lookups (<c>https://registry.terraform.io/v1/providers/{ns}/{type}</c>), used for the latest
/// azurerm version. Optional and graceful: any failure returns null.
/// </summary>
public sealed class TerraformRegistryMetadataProvider : IPackageMetadataProvider
{
    public const string DefaultBaseUrl = "https://registry.terraform.io/v1/providers/";

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _offline;

    public TerraformRegistryMetadataProvider(HttpClient http, string? baseUrl = null)
    {
        _http = http;
        _baseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/') + "/";
    }

    public Task<string?> GetLatestVersionAsync(string packageId, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<string?> GetLatestTerraformProviderVersionAsync(string providerNamespace, string providerType, CancellationToken ct)
    {
        if (_offline || string.IsNullOrWhiteSpace(providerNamespace) || string.IsNullOrWhiteSpace(providerType))
            return Task.FromResult<string?>(null);
        var key = $"{providerNamespace.Trim()}/{providerType.Trim()}";
        return _cache.GetOrAdd(key, k => new Lazy<Task<string?>>(() => FetchAsync(k, ct))).Value;
    }

    private async Task<string?> FetchAsync(string key, CancellationToken ct)
    {
        try
        {
            var parts = key.Split('/');
            var url = $"{_baseUrl}{Uri.EscapeDataString(parts[0].ToLowerInvariant())}/{Uri.EscapeDataString(parts[1].ToLowerInvariant())}";
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String) return v.GetString();
            if (doc.RootElement.TryGetProperty("versions", out var vs) && vs.ValueKind == JsonValueKind.Array)
                return NuGetPackageMetadataProvider.PickLatestStable(vs.EnumerateArray().Select(x => x.GetString()));
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _cache.TryRemove(key, out _);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            _offline = true;
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Asks each provider in order and returns the first non-null answer (e.g. NuGet + Terraform Registry).</summary>
public sealed class CompositePackageMetadataProvider(params IPackageMetadataProvider[] providers) : IPackageMetadataProvider
{
    public async Task<string?> GetLatestVersionAsync(string packageId, CancellationToken ct)
    {
        foreach (var p in providers)
        {
            var v = await p.GetLatestVersionAsync(packageId, ct).ConfigureAwait(false);
            if (v is not null) return v;
        }
        return null;
    }

    public async Task<string?> GetLatestTerraformProviderVersionAsync(string providerNamespace, string providerType, CancellationToken ct)
    {
        foreach (var p in providers)
        {
            var v = await p.GetLatestTerraformProviderVersionAsync(providerNamespace, providerType, ct).ConfigureAwait(false);
            if (v is not null) return v;
        }
        return null;
    }
}

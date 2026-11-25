namespace Dashboard.Core.Scanners;

/// <summary>
/// Produces <see cref="RepoFacts"/> for a checked-out worktree by static parsing only: nothing from the repository
/// is executed (no dotnet/terraform/npm). Vendor/generated folders (bin, obj, node_modules, .git, packages,
/// .terraform, dist, wwwroot/lib), EF <c>*.Designer.cs</c> files and files over 2 MB are skipped. Malformed files
/// produce <see cref="RepoFacts.ScanWarnings"/> rather than exceptions; only cancellation throws.
/// </summary>
public sealed class RepoScanner
{
    /// <summary>Stored as <see cref="RepoFacts.ScannerVersion"/>; bump when facts change meaning.</summary>
    public const string Version = "1.0.0";

    private const int MaxConcurrentLookups = 6;

    private readonly IPackageMetadataProvider _packages;
    private readonly TimeProvider _time;

    public RepoScanner(IPackageMetadataProvider packageMetadataProvider, TimeProvider? timeProvider = null)
    {
        _packages = packageMetadataProvider ?? OfflinePackageMetadataProvider.Instance;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<RepoFacts> ScanAsync(string rootPath, string sha, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var facts = new RepoFacts { ScannerVersion = Version, Sha = sha ?? "", ScannedAt = now };
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            facts.ScanWarnings.Add($"Repository root '{rootPath}' does not exist.");
            return facts;
        }

        var catalog = DotnetSupportCatalog.For(now);
        var ctx = ScanContext.Create(rootPath, facts.ScanWarnings, ct);

        Run(ctx, "languages", () => LanguageScanner.Scan(ctx, facts), ct);
        Run(ctx, ".NET", () => DotnetScanner.Scan(ctx, facts, catalog), ct);
        var projects = new ProjectIndex(facts.Dotnet.Projects);
        Run(ctx, "data", () => DataScanner.Scan(ctx, facts, projects), ct);
        Run(ctx, "messaging", () => MessagingScanner.Scan(ctx, facts), ct);
        Run(ctx, "pipelines", () => PipelineScanner.Scan(ctx, facts), ct);
        Run(ctx, "deploy", () => DeployScanner.Scan(ctx, facts), ct);
        Run(ctx, "hiera", () => HieraScanner.Scan(ctx, facts), ct);
        Run(ctx, "docs", () => DocsScanner.Scan(ctx, facts), ct);

        try { await TerraformScanner.ScanAsync(ctx, facts, _packages, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { ctx.Warn($"terraform scanner failed: {ex.Message}"); }

        Run(ctx, "inference", () => InferenceScanner.Infer(facts), ct);

        try { await ResolvePackagesAsync(facts, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { ctx.Warn($"Package lookup failed: {ex.Message}"); }

        return facts;
    }

    private static void Run(ScanContext ctx, string area, Action action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { action(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ctx.Warn($"{area} scanner failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task ResolvePackagesAsync(RepoFacts facts, CancellationToken ct)
    {
        var packages = facts.Dotnet.Packages;
        if (packages.Count == 0) return;
        var ids = packages.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var latest = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        using var gate = new SemaphoreSlim(MaxConcurrentLookups);
        var tasks = ids.Select(async id =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                string? v;
                try { v = await _packages.GetLatestVersionAsync(id, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { v = null; }
                return (id, v);
            }
            finally { gate.Release(); }
        }).ToList();
        foreach (var (id, v) in await Task.WhenAll(tasks).ConfigureAwait(false)) latest[id] = v;

        foreach (var p in packages)
        {
            p.Latest = latest.GetValueOrDefault(p.Id);
            ApplyStaleness(p);
        }
        facts.Dotnet.PackageLookupPerformed = packages.Any(p => p.Latest is not null);
        facts.Dotnet.OutdatedPackageCount = packages.Count(p => p.Outdated);
    }

    internal static void ApplyStaleness(PackageVersionInfo p)
    {
        p.Outdated = false;
        p.MajorVersionsBehind = 0;
        var current = SemanticVersion.ParseLoose(p.Version);
        var latest = SemanticVersion.Parse(p.Latest);
        if (current is null || latest is null) return;
        if (latest > current)
        {
            p.Outdated = true;
            p.MajorVersionsBehind = Math.Max(0, latest.Major - current.Major);
        }
    }
}

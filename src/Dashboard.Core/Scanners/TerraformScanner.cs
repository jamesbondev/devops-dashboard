using System.Text;
using System.Text.RegularExpressions;

namespace Dashboard.Core.Scanners;

/// <summary>HCL-lite: comment stripping, brace matching and regexes. Good enough for the attributes we need.</summary>
internal static partial class TerraformScanner
{
    [GeneratedRegex(@"\brequired_version\s*=\s*""([^""]*)""")]
    private static partial Regex RequiredVersion();

    [GeneratedRegex(@"\brequired_providers\s*\{")]
    private static partial Regex RequiredProvidersBlock();

    [GeneratedRegex(@"(?m)^\s*([A-Za-z0-9_\-]+)\s*=\s*(\{|""([^""]*)"")")]
    private static partial Regex ProviderEntry();

    [GeneratedRegex(@"\bversion\s*=\s*""([^""]*)""")]
    private static partial Regex VersionAttr();

    [GeneratedRegex(@"\bsource\s*=\s*""([^""]*)""")]
    private static partial Regex SourceAttr();

    [GeneratedRegex(@"\bbackend\s+""([^""]+)""\s*\{")]
    private static partial Regex Backend();

    [GeneratedRegex(@"(?m)^\s*cloud\s*\{")]
    private static partial Regex CloudBlock();

    [GeneratedRegex(@"\bmodule\s+""([^""]+)""\s*\{")]
    private static partial Regex ModuleBlock();

    [GeneratedRegex(@"\bresource\s+""([A-Za-z0-9_]+)""\s+""[^""]+""\s*\{")]
    private static partial Regex ResourceBlock();

    [GeneratedRegex(@"\bprovider\s+""azurerm""\s*\{")]
    private static partial Regex AzurermProviderBlock();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_\-]*/[A-Za-z0-9_\-]+/[A-Za-z0-9_\-]+$")]
    private static partial Regex RegistrySource();

    public static async Task ScanAsync(ScanContext ctx, RepoFacts facts, IPackageMetadataProvider provider, CancellationToken ct)
    {
        var tf = facts.Terraform;
        var files = ctx.WithExtension(".tf").ToList();
        tf.HasLockFile = ctx.Files.Any(f => f.Name.Equals(".terraform.lock.hcl", StringComparison.OrdinalIgnoreCase));
        if (files.Count == 0) return;

        tf.Directories = files.Select(f => f.Directory).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var resources = new SortedSet<string>(StringComparer.Ordinal);
        // Root-most directories first so the "main" configuration wins for single-valued facts.
        foreach (var f in files.OrderBy(f => f.RelativePath.Count(c => c == '/')).ThenBy(f => f.RelativePath, StringComparer.Ordinal))
        {
            var raw = ctx.ReadText(f);
            if (raw is null) continue;
            var text = StripComments(raw);

            if (tf.RequiredVersion is null && RequiredVersion().Match(text) is { Success: true } rv) tf.RequiredVersion = rv.Groups[1].Value;

            foreach (Match m in RequiredProvidersBlock().Matches(text))
            {
                var body = BlockBody(text, m.Index + m.Length - 1);
                if (body is null) { ctx.Warn($"Unbalanced braces in '{f.RelativePath}'."); continue; }
                ParseProviders(body, tf);
            }

            if (AzurermProviderBlock().Match(text) is { Success: true } ap && BlockBody(text, ap.Index + ap.Length - 1) is { } apBody
                && VersionAttr().Match(apBody) is { Success: true } apv)
                tf.Providers.TryAdd("azurerm", apv.Groups[1].Value);

            if (tf.BackendType is null)
            {
                if (Backend().Match(text) is { Success: true } b) tf.BackendType = b.Groups[1].Value;
                else if (CloudBlock().IsMatch(text)) tf.BackendType = "cloud";
            }

            foreach (Match m in ModuleBlock().Matches(text))
            {
                var body = BlockBody(text, m.Index + m.Length - 1) ?? "";
                var source = TopLevelAttr(body, SourceAttr()) ?? "";
                var version = TopLevelAttr(body, VersionAttr());
                tf.Modules.Add(new TerraformModuleRef
                {
                    Name = m.Groups[1].Value,
                    Source = source,
                    Version = version,
                    Pinned = IsModulePinned(source, version),
                });
            }

            foreach (Match m in ResourceBlock().Matches(text)) resources.Add(m.Groups[1].Value);
        }

        tf.ResourceTypes = resources.ToList();
        tf.BackendType ??= "local";
        tf.Providers = tf.Providers.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary();
        if (tf.Providers.TryGetValue("azurerm", out var az)) tf.AzurermVersion = az.Length == 0 ? null : az;

        if (tf.Providers.ContainsKey("azurerm") || resources.Any(r => r.StartsWith("azurerm_", StringComparison.Ordinal)))
        {
            try
            {
                tf.LatestAzurermVersion = await provider.GetLatestTerraformProviderVersionAsync("hashicorp", "azurerm", ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ctx.Warn($"Terraform registry lookup failed: {ex.Message}");
            }
        }
    }

    /// <summary>Local paths are versioned with the repo (pinned). Otherwise needs a version, a git ?ref= or a registry version.</summary>
    internal static bool IsModulePinned(string source, string? version)
    {
        if (source.StartsWith("./", StringComparison.Ordinal) || source.StartsWith("../", StringComparison.Ordinal)) return true;
        if (!string.IsNullOrWhiteSpace(version)) return true;
        if (source.Contains("?ref=", StringComparison.OrdinalIgnoreCase) || source.Contains("&ref=", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static void ParseProviders(string body, TerraformFacts tf)
    {
        var i = 0;
        while (i < body.Length)
        {
            var m = ProviderEntry().Match(body, i);
            if (!m.Success) break;
            var name = m.Groups[1].Value;
            if (m.Groups[2].Value == "{")
            {
                var open = m.Groups[2].Index;
                var inner = BlockBody(body, open) ?? "";
                var version = VersionAttr().Match(inner);
                tf.Providers.TryAdd(name, version.Success ? version.Groups[1].Value : "");
                i = open + inner.Length + 2;
            }
            else
            {
                tf.Providers.TryAdd(name, m.Groups[3].Value);   // legacy: azurerm = "~> 2.0"
                i = m.Index + m.Length;
            }
        }
    }

    /// <summary>Attribute at nesting depth 0 of a block body (ignores nested blocks).</summary>
    private static string? TopLevelAttr(string body, Regex attr)
    {
        var depth = 0;
        var top = new StringBuilder();
        foreach (var c in body)
        {
            if (c == '{') depth++;
            else if (c == '}') depth--;
            else if (depth == 0) top.Append(c);
        }
        var m = attr.Match(top.ToString());
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Text between the brace at <paramref name="openIndex"/> and its match, or null when unbalanced.</summary>
    internal static string? BlockBody(string text, int openIndex)
    {
        if (openIndex < 0 || openIndex >= text.Length || text[openIndex] != '{') return null;
        var depth = 0;
        var inString = false;
        for (var i = openIndex; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\') { i++; continue; }
                if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; continue; }
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return text.Substring(openIndex + 1, i - openIndex - 1);
            }
        }
        return null;
    }

    /// <summary>Removes #, // and /* */ comments outside of strings.</summary>
    internal static string StripComments(string text)
    {
        var sb = new StringBuilder(text.Length);
        var inString = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < text.Length) { sb.Append(text[++i]); continue; }
                if (c == '"' || c == '\n') inString = false;
                continue;
            }
            if (c == '"') { inString = true; sb.Append(c); continue; }
            if (c == '#' || (c == '/' && i + 1 < text.Length && text[i + 1] == '/'))
            {
                while (i < text.Length && text[i] != '\n') i++;
                if (i < text.Length) sb.Append('\n');
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/')) { if (text[i] == '\n') sb.Append('\n'); i++; }
                i++;
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    internal static bool IsRegistrySource(string source) => RegistrySource().IsMatch(source);
}

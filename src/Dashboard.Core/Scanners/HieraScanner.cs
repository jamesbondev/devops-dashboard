using System.Text.Json;
using YamlDotNet.RepresentationModel;

namespace Dashboard.Core.Scanners;

/// <summary>
/// Puppet Hiera: hiera.yaml (v5 <c>hierarchy[].name</c>, v3 <c>:hierarchy:</c> strings) and data files under
/// any <c>hieradata/</c> directory or the configured/default <c>datadir</c> ("data") next to a hiera.yaml.
/// <see cref="HieraFacts.Files"/> lists hiera.yaml configs first, then data files.
/// </summary>
internal static class HieraScanner
{
    private static readonly string[] DataExtensions = [".yaml", ".yml", ".json", ".eyaml"];

    public static void Scan(ScanContext ctx, RepoFacts facts)
    {
        var h = facts.Hiera;
        var configs = ctx.Files.Where(f => f.Name.Equals("hiera.yaml", StringComparison.OrdinalIgnoreCase)).ToList();
        var dataRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var cfg in configs)
        {
            var text = ctx.ReadText(cfg);
            if (text is null) continue;
            var root = YamlUtil.LoadFirst(text, cfg.RelativePath, ctx);
            var datadir = root.Map("defaults").Str("datadir") ?? root.Map(":yaml").Str(":datadir");
            dataRoots.Add(ScanContext.Combine(cfg.Directory, string.IsNullOrWhiteSpace(datadir) || Path.IsPathRooted(datadir) ? "data" : datadir));

            foreach (var level in root.Seq("hierarchy"))
            {
                var name = level.Str("name") ?? level.Scalar();
                if (!string.IsNullOrWhiteSpace(name) && !h.HierarchyLevels.Contains(name)) h.HierarchyLevels.Add(name);
            }
            foreach (var level in root.Seq(":hierarchy"))
                if (level.Scalar() is { } s && !h.HierarchyLevels.Contains(s)) h.HierarchyLevels.Add(s);
        }

        var dataFiles = ctx.Files.Where(f => DataExtensions.Contains(f.Extension)
                && !f.Name.Equals("hiera.yaml", StringComparison.OrdinalIgnoreCase)
                && (HasSegment(f.Directory, "hieradata") || dataRoots.Any(r => f.RelativePath.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase))))
            .ToList();

        h.Present = configs.Count > 0 || dataFiles.Count > 0;
        h.Files = configs.Select(c => c.RelativePath).Concat(dataFiles.Select(d => d.RelativePath)).ToList();

        var keys = 0;
        foreach (var f in dataFiles)
        {
            var text = ctx.ReadText(f);
            if (text is null) continue;
            if (f.Extension == ".json")
            {
                try
                {
                    using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                    if (doc.RootElement.ValueKind == JsonValueKind.Object) keys += doc.RootElement.EnumerateObject().Count();
                }
                catch (JsonException ex) { ctx.Warn($"Malformed JSON in '{f.RelativePath}': {ex.Message}"); }
            }
            else if (YamlUtil.LoadFirst(text, f.RelativePath, ctx) is YamlMappingNode map)
            {
                keys += map.Children.Count;
            }
        }
        h.KeyCount = keys;
    }

    private static bool HasSegment(string dir, string segment)
        => dir.Split('/').Any(s => s.Equals(segment, StringComparison.OrdinalIgnoreCase));
}

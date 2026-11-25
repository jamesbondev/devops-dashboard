using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Dashboard.Core.Scanners;

internal static partial class DeployScanner
{
    private const string TemplatedMarker = "__TEMPLATED__";

    private static readonly HashSet<string> WorkloadKinds = new(StringComparer.Ordinal)
    {
        "Deployment", "StatefulSet", "DaemonSet", "Job", "CronJob",
    };

    [GeneratedRegex(@"\{\{/\*.*?\*/\}\}", RegexOptions.Singleline)]
    private static partial Regex HelmComment();

    [GeneratedRegex(@"\{\{.*?\}\}")]
    private static partial Regex HelmExpression();

    [GeneratedRegex(@"^\s*kind\s*:\s*[""']?(Deployment|StatefulSet|DaemonSet|Job|CronJob)[""']?\s*$", RegexOptions.Multiline)]
    private static partial Regex WorkloadKindLine();

    [GeneratedRegex(@"\$\{?([A-Za-z_][A-Za-z0-9_]*)(?::-([^}]*))?\}?")]
    private static partial Regex DockerVariable();

    public static bool IsDockerfile(RepoFile f)
        => f.Name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)
            || f.Name.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase)
            || f.Name.EndsWith(".Dockerfile", StringComparison.OrdinalIgnoreCase)
            || f.Name.EndsWith(".dockerfile", StringComparison.OrdinalIgnoreCase)
            || f.Name.Equals("Containerfile", StringComparison.OrdinalIgnoreCase);

    public static bool IsSkaffold(RepoFile f)
        => f.Name.StartsWith("skaffold", StringComparison.OrdinalIgnoreCase) && f.Extension is ".yaml" or ".yml";

    public static void Scan(ScanContext ctx, RepoFacts facts)
    {
        var deploy = facts.Deploy;
        foreach (var f in ctx.Files.Where(IsSkaffold)) ScanSkaffold(ctx, f, deploy);
        foreach (var f in ctx.Files.Where(IsDockerfile)) ScanDockerfile(ctx, f, deploy);

        var charts = ctx.Files.Where(f => f.Name.Equals("Chart.yaml", StringComparison.OrdinalIgnoreCase)).ToList();
        deploy.HelmCharts = charts.Select(c => c.RelativePath).ToList();

        foreach (var f in ctx.WithExtension(".yaml", ".yml"))
        {
            if (IsSkaffold(f)) continue;
            var text = ctx.ReadText(f);
            if (text is null || !WorkloadKindLine().IsMatch(text)) continue;
            var chart = charts.Where(c => c.Directory.Length == 0 || f.RelativePath.StartsWith(c.Directory + "/", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(c => c.Directory.Length).FirstOrDefault();
            ScanManifest(ctx, f, text, chart, deploy);
        }

        deploy.UsesDacpac = facts.Dotnet.Projects.Any(p => p.Kind == "Database")
            || ctx.WithExtension(".sqlproj").Any()
            || facts.Pipelines.Select(p => ctx.FindFile(p.Path)).OfType<RepoFile>()
                .Any(f => ctx.ReadText(f) is { } t && (t.Contains("SqlAzureDacpacDeployment", StringComparison.OrdinalIgnoreCase)
                    || t.Contains("SqlPackage", StringComparison.OrdinalIgnoreCase)));
    }

    // ---------------------------------------------------------------- skaffold

    private static void ScanSkaffold(ScanContext ctx, RepoFile f, DeployFacts deploy)
    {
        var text = ctx.ReadText(f);
        if (text is null) return;
        foreach (var doc in YamlUtil.LoadDocuments(text, f.RelativePath, ctx))
        {
            if (doc is not YamlMappingNode) continue;
            var api = doc.Str("apiVersion");
            if (api is not null && !api.StartsWith("skaffold/", StringComparison.OrdinalIgnoreCase)) continue;
            if (api is null && doc.Get("build") is null && doc.Get("deploy") is null && doc.Get("manifests") is null && doc.Get("requires") is null) continue;

            var s = new SkaffoldFile { Path = f.RelativePath, ConfigName = doc.Map("metadata").Str("name") };
            AddArtifacts(doc.Map("build"), s);
            foreach (var p in doc.Seq("profiles"))
            {
                if (p.Str("name") is { } pn) s.Profiles.Add(pn);
                AddArtifacts(p.Map("build"), s);
            }

            s.Deployer = DetectDeployer(doc) ?? doc.Seq("profiles").Select(DetectDeployer).FirstOrDefault(x => x is not null);
            AddManifests(doc, s);
            foreach (var p in doc.Seq("profiles")) AddManifests(p, s);
            s.Manifests = s.Manifests.Distinct(StringComparer.Ordinal).ToList();

            foreach (var r in doc.Seq("requires"))
            {
                var path = r.Str("path");
                if (path is not null) s.Requires.Add(path);
                if (r.Get("git") is YamlMappingNode git && git.Str("repo") is { } repo) s.Requires.Add(repo + (git.Str("path") is { } gp ? "//" + gp : ""));
            }
            deploy.Skaffold.Add(s);
        }
    }

    private static void AddArtifacts(YamlMappingNode? build, SkaffoldFile s)
    {
        foreach (var a in build.Seq("artifacts"))
        {
            var image = a.Str("image");
            if (string.IsNullOrWhiteSpace(image) || s.Artifacts.Any(x => x.Image == image)) continue;
            s.Artifacts.Add(new SkaffoldArtifact
            {
                Image = image,
                Context = a.Str("context"),
                Dockerfile = a.Map("docker").Str("dockerfile"),
            });
        }
    }

    private static string? DetectDeployer(YamlNode doc)
    {
        var d = doc.Map("deploy");
        if (d.Get("helm") is not null) return "helm";
        if (d.Get("kustomize") is not null) return "kustomize";
        if (d.Get("kubectl") is not null) return "kubectl";
        if (d.Get("kpt") is not null) return "kpt";
        var m = doc.Map("manifests");
        if (m.Get("helm") is not null) return "helm";
        if (m.Get("kustomize") is not null) return "kustomize";
        if (m.Get("rawYaml") is not null) return "kubectl";   // skaffold v2 default deployer for rawYaml
        return null;
    }

    private static void AddManifests(YamlNode doc, SkaffoldFile s)
    {
        var m = doc.Map("manifests");
        foreach (var r in m.Seq("rawYaml")) if (r.Scalar() is { } v) s.Manifests.Add(v);
        foreach (var r in m.Map("kustomize").Seq("paths")) if (r.Scalar() is { } v) s.Manifests.Add(v);
        foreach (var r in m.Map("helm").Seq("releases")) if (r.Str("chartPath") is { } v) s.Manifests.Add(v);
        var d = doc.Map("deploy");
        foreach (var r in d.Map("kubectl").Seq("manifests")) if (r.Scalar() is { } v) s.Manifests.Add(v);
        foreach (var r in d.Map("kustomize").Seq("paths")) if (r.Scalar() is { } v) s.Manifests.Add(v);
        foreach (var r in d.Map("helm").Seq("releases")) if (r.Str("chartPath") is { } v) s.Manifests.Add(v);
    }

    // ---------------------------------------------------------------- Dockerfile

    private static void ScanDockerfile(ScanContext ctx, RepoFile f, DeployFacts deploy)
    {
        var text = ctx.ReadText(f);
        if (text is null) return;
        var facts = new DockerfileFacts { Path = f.RelativePath };
        var args = new Dictionary<string, string>(StringComparer.Ordinal);
        var stages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fromCount = 0;
        string? lastUser = null;   // USER of the current (eventually final) stage
        var allPinned = true;

        foreach (var instruction in LogicalLines(text))
        {
            var sp = instruction.IndexOfAny([' ', '\t']);
            var op = (sp < 0 ? instruction : instruction[..sp]).ToUpperInvariant();
            var rest = sp < 0 ? "" : instruction[(sp + 1)..].Trim();
            switch (op)
            {
                case "ARG" when fromCount == 0:
                    var eq = rest.IndexOf('=');
                    if (eq > 0) args[rest[..eq].Trim()] = rest[(eq + 1)..].Trim().Trim('"');
                    break;
                case "FROM":
                    fromCount++;
                    lastUser = null;
                    var tokens = rest.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Where(t => !t.StartsWith("--", StringComparison.Ordinal)).ToList();
                    if (tokens.Count == 0) break;
                    var image = ExpandArgs(tokens[0], args);
                    var isStageRef = stages.Contains(image);   // FROM <earlier stage>
                    if (tokens.Count >= 3 && tokens[1].Equals("AS", StringComparison.OrdinalIgnoreCase)) stages.Add(tokens[2]);
                    if (isStageRef || image.Equals("scratch", StringComparison.OrdinalIgnoreCase)) break;
                    facts.BaseImages.Add(image);
                    if (!IsPinned(image)) allPinned = false;
                    break;
                case "USER":
                    lastUser = rest.Trim();
                    break;
                case "HEALTHCHECK":
                    if (!rest.Trim().Equals("NONE", StringComparison.OrdinalIgnoreCase)) facts.HasHealthcheck = true;
                    break;
            }
        }

        facts.BaseImages = facts.BaseImages.Distinct(StringComparer.Ordinal).ToList();
        facts.MultiStage = fromCount >= 2;
        facts.BaseImagesPinned = facts.BaseImages.Count > 0 && allPinned;
        facts.NonRootUser = lastUser is not null && !IsRootUser(lastUser);
        if (fromCount == 0) ctx.Warn($"Dockerfile '{f.RelativePath}' has no FROM instruction.");
        deploy.Dockerfiles.Add(facts);
    }

    private static IEnumerable<string> LogicalLines(string text)
    {
        var buffer = new System.Text.StringBuilder();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.TrimStart();
            if (buffer.Length == 0 && (trimmed.Length == 0 || trimmed.StartsWith('#'))) continue;
            if (buffer.Length > 0 && trimmed.StartsWith('#')) continue;   // comments inside continuations
            if (line.EndsWith('\\'))
            {
                buffer.Append(line[..^1]).Append(' ');
                continue;
            }
            buffer.Append(line);
            yield return buffer.ToString().Trim();
            buffer.Clear();
        }
        if (buffer.Length > 0) yield return buffer.ToString().Trim();
    }

    private static string ExpandArgs(string token, Dictionary<string, string> args)
        => DockerVariable().Replace(token, m =>
            args.TryGetValue(m.Groups[1].Value, out var v) && v.Length > 0 ? v
            : m.Groups[2].Success ? m.Groups[2].Value : m.Value);

    /// <summary>Pinned = has a digest, or an explicit tag other than "latest". Unresolved variables in the tag count as pinned.</summary>
    internal static bool IsPinned(string image)
    {
        if (image.Contains("@sha256:", StringComparison.OrdinalIgnoreCase)) return true;
        var lastSlash = image.LastIndexOf('/');
        var colon = image.IndexOf(':', lastSlash + 1);
        if (colon < 0) return false;
        var tag = image[(colon + 1)..];
        return tag.Length > 0 && !tag.Equals("latest", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRootUser(string user)
    {
        var u = user.Split(':')[0].Trim().Trim('"');
        return u.Equals("root", StringComparison.OrdinalIgnoreCase) || u == "0";
    }

    // ---------------------------------------------------------------- Kubernetes / Helm

    private static void ScanManifest(ScanContext ctx, RepoFile f, string text, RepoFile? chart, DeployFacts deploy)
    {
        var templated = text.Contains("{{", StringComparison.Ordinal);
        var content = templated ? StripHelm(text) : text;
        YamlNode? values = null;
        string? chartName = null;
        if (templated && chart is not null)
        {
            if (ctx.ReadText(chart) is { } chartText) chartName = YamlUtil.LoadFirst(chartText, chart.RelativePath, ctx).Str("name");
            var valuesFile = ctx.FindFile(ScanContext.Combine(chart.Directory, "values.yaml"));
            if (valuesFile is not null && ctx.ReadText(valuesFile) is { } vt) values = YamlUtil.LoadFirst(vt, valuesFile.RelativePath, ctx);
        }

        foreach (var doc in YamlUtil.LoadDocuments(content, f.RelativePath, ctx))
        {
            var kind = doc.Str("kind");
            if (kind is null || !WorkloadKinds.Contains(kind)) continue;
            var name = doc.Map("metadata").Str("name") ?? "";
            if (name.Length == 0 || name.Contains(TemplatedMarker, StringComparison.Ordinal))
                name = chartName ?? Path.GetFileNameWithoutExtension(f.Name);

            var spec = doc.Map("spec");
            var podSpec = kind == "CronJob"
                ? spec.Map("jobTemplate").Map("spec").Map("template").Map("spec")
                : spec.Map("template").Map("spec");

            var w = new K8sWorkload { Path = f.RelativePath, Kind = kind, Name = name, Templated = templated };
            if (spec.Str("replicas") is { } r && int.TryParse(r, out var replicas)) w.Replicas = replicas;

            foreach (var c in podSpec.Seq("containers"))
            {
                var resources = c.Get("resources");
                var container = new K8sContainer
                {
                    Name = c.Str("name") is { } cn && !cn.Contains(TemplatedMarker, StringComparison.Ordinal) ? cn : (chartName ?? name),
                    Image = c.Str("image"),
                    HasRequests = resources.Get("requests").IsNonEmptyMap(),
                    HasLimits = resources.Get("limits").IsNonEmptyMap(),
                    HasLivenessProbe = c.Get("livenessProbe").IsNonEmptyMap(),
                    HasReadinessProbe = c.Get("readinessProbe").IsNonEmptyMap(),
                };
                if (templated && values is not null)
                {
                    // Values injected via toYaml: fall back to the chart's values.yaml.
                    if (IsTemplatedSlot(c, "resources"))
                    {
                        container.HasRequests |= ValuesHas(values, "resources", "requests");
                        container.HasLimits |= ValuesHas(values, "resources", "limits");
                    }
                    if (IsTemplatedSlot(c, "livenessProbe")) container.HasLivenessProbe |= ValuesHas(values, "livenessProbe", null);
                    if (IsTemplatedSlot(c, "readinessProbe")) container.HasReadinessProbe |= ValuesHas(values, "readinessProbe", null);
                }
                if (container.Image?.Contains(TemplatedMarker, StringComparison.Ordinal) == true) container.Image = null;
                w.Containers.Add(container);
            }
            deploy.Workloads.Add(w);
        }
    }

    /// <summary>True when the key is absent/null or a templated scalar (e.g. "resources: {{ toYaml ... }}").</summary>
    private static bool IsTemplatedSlot(YamlNode container, string key)
    {
        var node = container.Get(key);
        if (node is null) return true;
        return node.IsNullish() || (node.Scalar()?.Contains(TemplatedMarker, StringComparison.Ordinal) ?? false);
    }

    private static bool ValuesHas(YamlNode values, string key, string? child)
    {
        foreach (var (k, v) in YamlUtil.WalkPairs(values))
        {
            if (!k.Equals(key, StringComparison.Ordinal)) continue;
            if (child is null ? v.IsNonEmptyMap() : v.Get(child).IsNonEmptyMap()) return true;
        }
        return false;
    }

    /// <summary>
    /// Best-effort removal of Go-template syntax so the YAML parses: drops lines that are only template actions
    /// and replaces inline expressions with a marker.
    /// </summary>
    internal static string StripHelm(string text)
    {
        var noComments = HelmComment().Replace(text, "");
        var sb = new System.Text.StringBuilder();
        foreach (var raw in noComments.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.StartsWith("{{", StringComparison.Ordinal) && trimmed.EndsWith("}}", StringComparison.Ordinal)
                && HelmExpression().Replace(trimmed, "").Trim().Length == 0)
                continue;
            sb.Append(HelmExpression().Replace(line, TemplatedMarker)).Append('\n');
        }
        return sb.ToString();
    }
}

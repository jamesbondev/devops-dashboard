using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Dashboard.Core.Scanners;

/// <summary>
/// Azure DevOps YAML pipelines. Candidates: azure-pipelines*.yml, *.pipeline.yml, anything under .azuredevops/ or
/// pipelines/, or any YAML whose top level has trigger:/stages:/extends:. Inline counts only cover the top-level
/// definition (outside extends); template references are excluded from inline counts.
/// </summary>
internal static partial class PipelineScanner
{
    [GeneratedRegex(@"^(trigger|stages|extends)\s*:", RegexOptions.Multiline)]
    private static partial Regex TopLevelPipelineKey();

    public static void Scan(ScanContext ctx, RepoFacts facts)
    {
        foreach (var f in ctx.WithExtension(".yml", ".yaml"))
        {
            if (DeployScanner.IsSkaffold(f) || f.Name.Equals("Chart.yaml", StringComparison.OrdinalIgnoreCase)
                || f.Name.Equals("hiera.yaml", StringComparison.OrdinalIgnoreCase)) continue;
            var byPath = IsPipelinePath(f);
            var text = ctx.ReadText(f);
            if (text is null) continue;
            if (!byPath && !TopLevelPipelineKey().IsMatch(text)) continue;

            var root = YamlUtil.LoadFirst(text, f.RelativePath, ctx);
            if (root is not YamlMappingNode map) continue;
            // Kubernetes/compose/etc. documents are not pipelines even under pipelines/.
            if (map.Get("apiVersion") is not null || map.Get("kind") is not null && map.Get("stages") is null) continue;
            facts.Pipelines.Add(Parse(f.RelativePath, map));
        }
    }

    private static bool IsPipelinePath(RepoFile f)
    {
        var name = f.Name.ToLowerInvariant();
        if (name.StartsWith("azure-pipelines", StringComparison.Ordinal)) return true;
        if (name.EndsWith(".pipeline.yml", StringComparison.Ordinal) || name.EndsWith(".pipeline.yaml", StringComparison.Ordinal)) return true;
        var segments = f.Directory.Split('/');
        return segments.Any(s => s.Equals(".azuredevops", StringComparison.OrdinalIgnoreCase) || s.Equals("pipelines", StringComparison.OrdinalIgnoreCase));
    }

    internal static PipelineFileFacts Parse(string path, YamlMappingNode root)
    {
        var p = new PipelineFileFacts { Path = path };

        if (root.Get("extends") is YamlMappingNode ext)
        {
            p.UsesExtends = true;
            p.ExtendsTemplate = ext.Str("template");
        }
        else if (root.Get("extends") is not null)
        {
            p.UsesExtends = true;
        }

        foreach (var r in root.Map("resources").Seq("repositories"))
        {
            var alias = r.Str("repository");
            if (string.IsNullOrWhiteSpace(alias)) continue;
            p.RepositoryResources.Add(new PipelineRepoResource { Alias = alias, Name = r.Str("name"), Ref = r.Str("ref"), Type = r.Str("type") });
        }

        // Inline definition (outside extends).
        foreach (var stage in root.Seq("stages"))
        {
            if (IsTemplateRef(stage)) continue;
            p.InlineStages++;
            CountJobs(stage.Seq("jobs"), p);
        }
        CountJobs(root.Seq("jobs"), p);
        p.InlineSteps += CountSteps(root.Seq("steps"));

        var envs = new List<string>();
        var templates = new List<string>();
        foreach (var (key, value) in YamlUtil.WalkPairs(root))
        {
            if (key == "environment")
            {
                var env = value.Scalar() ?? value.Str("name");
                if (!string.IsNullOrWhiteSpace(env) && !env.Contains("${{", StringComparison.Ordinal) && !env.Contains("$(", StringComparison.Ordinal)
                    && !envs.Contains(env, StringComparer.OrdinalIgnoreCase))
                    envs.Add(env);
            }
            else if (key == "template" && value.Scalar() is { } t && !templates.Contains(t, StringComparer.Ordinal))
            {
                templates.Add(t);
            }
        }
        p.Environments = envs;
        p.TemplateReferences = templates;
        return p;
    }

    private static void CountJobs(IEnumerable<YamlNode> jobs, PipelineFileFacts p)
    {
        foreach (var job in jobs)
        {
            if (IsTemplateRef(job)) continue;
            p.InlineJobs++;
            p.InlineSteps += CountSteps(job.Seq("steps"));
            // Deployment jobs: strategy.<runOnce|rolling|canary>.<deploy|preDeploy|...>.steps
            if (job.Get("strategy") is YamlMappingNode strategy)
                foreach (var s in strategy.Children.Values.OfType<YamlMappingNode>())
                    foreach (var hook in s.Children.Values.OfType<YamlMappingNode>())
                        p.InlineSteps += CountSteps(hook.Seq("steps"));
                    // routeTraffic/postRouteTraffic/on.failure nest one level deeper; ignored on purpose.
        }
    }

    private static int CountSteps(IEnumerable<YamlNode> steps) => steps.Count(s => !IsTemplateRef(s));

    private static bool IsTemplateRef(YamlNode node) => node is YamlMappingNode m && m.Get("template") is not null;
}

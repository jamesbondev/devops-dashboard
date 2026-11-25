using Dashboard.Core.Contracts;
using Dashboard.Core.Scanners;

namespace Dashboard.Core.Audit;

/// <summary>
/// Deterministic evaluation of <c>kind: fact</c> checks from <see cref="RepoFacts"/> alone (Confidence = 1).
/// Per-component outcomes where natural: K8S-* per workload, DOCK-001 per Dockerfile, TEST-001 per deployable
/// project; NET-001/NET-002 one outcome per check. Unknown fact check ids yield a single "na" outcome.
/// </summary>
public static class FactCheckEvaluator
{
    public const string Pass = "pass";
    public const string Partial = "partial";
    public const string Fail = "fail";
    public const string NotApplicable = "na";

    private static readonly string[] DeployableKinds = ["Web", "Worker", "Functions", "Console"];

    public static List<CheckOutcome> Evaluate(RepoFacts facts, IEnumerable<AuditCheck> factChecks)
    {
        var outcomes = new List<CheckOutcome>();
        foreach (var check in factChecks)
        {
            var id = check.Id.Trim().ToUpperInvariant();
            var produced = id switch
            {
                "NET-001" => EvaluateNet001(facts),
                "NET-002" => EvaluateNet002(facts),
                "K8S-001" => EvaluateK8s001(facts),
                "K8S-002" => EvaluateK8s002(facts),
                "DOCK-001" => EvaluateDock001(facts),
                "TEST-001" => EvaluateTest001(facts),
                _ => [Outcome(NotApplicable, null, $"No deterministic evaluator exists for fact check '{check.Id}'.", null)],
            };
            foreach (var o in produced) o.CheckId = check.Id;
            outcomes.AddRange(produced);
        }
        return outcomes;
    }

    // ------------------------------------------------------------------ NET-001

    private static List<CheckOutcome> EvaluateNet001(RepoFacts facts)
    {
        var projects = facts.Dotnet.Projects.Where(p => p.Kind != "Database" && p.TargetFrameworks.Count > 0).ToList();
        if (projects.Count == 0)
            return [Outcome(NotApplicable, null, "No .NET projects with a target framework were found.", null)];

        var flagged = new HashSet<string>(facts.Dotnet.UnsupportedTargetFrameworks, StringComparer.OrdinalIgnoreCase);
        bool IsFlagged(string tfm) => flagged.Contains(tfm) || flagged.Contains(DotnetSupportCatalog.Normalize(tfm));

        var affected = projects.Where(p => p.TargetFrameworks.Any(IsFlagged)).OrderBy(p => p.Path, StringComparer.Ordinal).ToList();
        if (affected.Count == 0)
            return [Outcome(Pass, null,
                $"All {projects.Count} .NET project(s) target supported frameworks ({string.Join(", ", facts.Dotnet.TargetFrameworks)}).",
                null, projects.Take(5).Select(p => Ev(p.Path, string.Join(";", p.TargetFrameworks))))];

        var onlyUnsupported = affected.Where(p => p.TargetFrameworks.All(IsFlagged)).ToList();
        var result = onlyUnsupported.Count > 0 ? Fail : Partial;
        var detail = string.Join("; ", affected.Select(p => $"{p.Name}: {string.Join(", ", p.TargetFrameworks.Where(IsFlagged))}"));
        var explanation = result == Fail
            ? $"{onlyUnsupported.Count} of {projects.Count} project(s) target only out-of-support or legacy frameworks ({detail})."
            : $"Every project has a supported target, but {affected.Count} project(s) still multi-target unsupported frameworks ({detail}).";
        return [Outcome(result, null, explanation,
            "Retarget to net10.0 (LTS, supported until 2028-11-10); drop unsupported targets from TargetFrameworks.",
            affected.Select(p => Ev(p.Path, string.Join(";", p.TargetFrameworks))))];
    }

    // ------------------------------------------------------------------ NET-002

    private static List<CheckOutcome> EvaluateNet002(RepoFacts facts)
    {
        var projects = facts.Dotnet.Projects.Where(p => p.Kind != "Database").ToList();
        if (projects.Count == 0)
            return [Outcome(NotApplicable, null, "No .NET projects were found.", null)];

        var managed = projects.Where(p => p.CentralPackageManagement).ToList();
        var unmanaged = projects.Where(p => !p.CentralPackageManagement).OrderBy(p => p.Path, StringComparer.Ordinal).ToList();
        if (unmanaged.Count == 0)
            return [Outcome(Pass, null, $"Central package management is enabled for all {projects.Count} project(s).", null,
                facts.Dotnet.DirectoryPackagesPropsPath is { } cpmPath ? [Ev(cpmPath, "ManagePackageVersionsCentrally=true")] : null)];
        if (managed.Count > 0)
            return [Outcome(Partial, null,
                $"Central package management covers {managed.Count} of {projects.Count} project(s); not covered: {string.Join(", ", unmanaged.Select(p => p.Name))}.",
                "Move the remaining projects under the Directory.Packages.props (or remove per-project opt-outs) so versions are defined once.",
                unmanaged.Select(p => Ev(p.Path, "not centrally managed")))];
        if (projects.Count == 1)
            return [Outcome(Partial, null,
                "Single project without central package management; low impact today, but versions will drift once more projects are added.",
                "Add a Directory.Packages.props with ManagePackageVersionsCentrally=true.",
                unmanaged.Select(p => Ev(p.Path, "inline package versions")))];
        return [Outcome(Fail, null,
            $"None of the {projects.Count} project(s) use central package management; package versions are declared per project.",
            "Add a Directory.Packages.props at the repo root with ManagePackageVersionsCentrally=true and move versions into PackageVersion items.",
            unmanaged.Select(p => Ev(p.Path, "inline package versions")))];
    }

    // ------------------------------------------------------------------ K8S-001

    private static List<CheckOutcome> EvaluateK8s001(RepoFacts facts)
    {
        var workloads = facts.Deploy.Workloads.Where(w => w.Containers.Count > 0).ToList();
        if (workloads.Count == 0)
            return [Outcome(NotApplicable, null, "No Kubernetes workload manifests (Deployment/StatefulSet/DaemonSet/Job/CronJob) were found.", null)];

        var outcomes = new List<CheckOutcome>();
        foreach (var w in Ordered(workloads))
        {
            var both = w.Containers.Count(c => c.HasRequests && c.HasLimits);
            var none = w.Containers.Count(c => !c.HasRequests && !c.HasLimits);
            var templatedNote = w.Templated ? " (Helm template; values.yaml consulted where resources are injected)" : "";
            var missing = w.Containers.Where(c => !(c.HasRequests && c.HasLimits))
                .Select(c => $"{c.Name}: {(c.HasRequests ? "" : "requests ")}{(c.HasLimits ? "" : "limits")}".Trim()).ToList();
            string result, explanation;
            if (both == w.Containers.Count)
            {
                result = Pass;
                explanation = $"{w.Kind} '{w.Name}': all {w.Containers.Count} container(s) set resource requests and limits{templatedNote}.";
            }
            else if (none == w.Containers.Count)
            {
                result = Fail;
                explanation = $"{w.Kind} '{w.Name}': no container sets resource requests or limits{templatedNote}.";
            }
            else
            {
                result = Partial;
                explanation = $"{w.Kind} '{w.Name}': missing {string.Join("; ", missing)}{templatedNote}.";
            }
            outcomes.Add(Outcome(result, w.Name, explanation,
                result == Pass ? null : "Set resources.requests (cpu, memory) and resources.limits (at least memory) on every container.",
                [Ev(w.Path, $"{w.Kind} {w.Name}")]));
        }
        return outcomes;
    }

    // ------------------------------------------------------------------ K8S-002

    private static List<CheckOutcome> EvaluateK8s002(RepoFacts facts)
    {
        var workloads = facts.Deploy.Workloads
            .Where(w => w.Kind is "Deployment" or "StatefulSet" or "DaemonSet" && w.Containers.Count > 0).ToList();
        if (workloads.Count == 0)
            return [Outcome(NotApplicable, null,
                facts.Deploy.Workloads.Count > 0
                    ? "Only run-to-completion workloads (Job/CronJob) were found; probes do not apply."
                    : "No long-running Kubernetes workloads were found.", null)];

        var outcomes = new List<CheckOutcome>();
        foreach (var w in Ordered(workloads))
        {
            var both = w.Containers.Count(c => c.HasLivenessProbe && c.HasReadinessProbe);
            var none = w.Containers.Count(c => !c.HasLivenessProbe && !c.HasReadinessProbe);
            var missing = w.Containers.Where(c => !(c.HasLivenessProbe && c.HasReadinessProbe))
                .Select(c => $"{c.Name}: {(c.HasLivenessProbe ? "" : "liveness ")}{(c.HasReadinessProbe ? "" : "readiness")}".Trim()).ToList();
            var templatedNote = w.Templated ? " (Helm template; values.yaml consulted where probes are injected)" : "";
            string result, explanation;
            if (both == w.Containers.Count)
            {
                result = Pass;
                explanation = $"{w.Kind} '{w.Name}': every container defines liveness and readiness probes{templatedNote}.";
            }
            else if (none == w.Containers.Count)
            {
                result = Fail;
                explanation = $"{w.Kind} '{w.Name}': no liveness or readiness probes{templatedNote}.";
            }
            else
            {
                result = Partial;
                explanation = $"{w.Kind} '{w.Name}': missing {string.Join("; ", missing)} probe(s){templatedNote}.";
            }
            outcomes.Add(Outcome(result, w.Name, explanation,
                result == Pass ? null : "Add readinessProbe and livenessProbe (e.g. httpGet on /health/ready and /health/live) to every container.",
                [Ev(w.Path, $"{w.Kind} {w.Name}")]));
        }
        return outcomes;
    }

    // ------------------------------------------------------------------ DOCK-001

    private static List<CheckOutcome> EvaluateDock001(RepoFacts facts)
    {
        if (facts.Deploy.Dockerfiles.Count == 0)
            return [Outcome(NotApplicable, null, "No Dockerfiles were found.", null)];

        var outcomes = new List<CheckOutcome>();
        foreach (var d in facts.Deploy.Dockerfiles.OrderBy(d => d.Path, StringComparer.Ordinal))
        {
            var problems = new List<string>();
            var fixes = new List<string>();
            if (!d.MultiStage) { problems.Add("single-stage build"); fixes.Add("use a multi-stage build (sdk → runtime image)"); }
            if (!d.BaseImagesPinned)
            {
                var unpinned = d.BaseImages.Where(i => !DeployScanner.IsPinned(i)).ToList();
                problems.Add(unpinned.Count > 0 ? $"unpinned base image(s) {string.Join(", ", unpinned)}" : "no pinned base image");
                fixes.Add("pin base images to an explicit version tag or digest (never :latest)");
            }
            if (!d.NonRootUser) { problems.Add("runs as root (no non-root USER in the final stage)"); fixes.Add("add USER $APP_UID (or another non-root user) to the final stage"); }

            var result = problems.Count == 0 ? Pass : problems.Count == 3 ? Fail : Partial;
            var health = d.HasHealthcheck ? " Has HEALTHCHECK." : "";
            var explanation = result == Pass
                ? $"Multi-stage build, pinned base images ({string.Join(", ", d.BaseImages)}) and a non-root user.{health}"
                : $"{Capitalize(string.Join("; ", problems))}.{health}";
            outcomes.Add(Outcome(result, DockerComponent(facts, d.Path), explanation,
                result == Pass ? null : Capitalize(string.Join("; ", fixes)) + ".",
                [Ev(d.Path, d.BaseImages.Count > 0 ? "FROM " + string.Join(", ", d.BaseImages) : null)]));
        }
        return outcomes;
    }

    private static string? DockerComponent(RepoFacts facts, string dockerfilePath)
    {
        foreach (var s in facts.Deploy.Skaffold)
        {
            var dir = s.Path.Contains('/') ? s.Path[..s.Path.LastIndexOf('/')] : "";
            foreach (var a in s.Artifacts)
            {
                var context = ScanContextPath(dir, a.Context ?? ".");
                var df = ScanContextPath(context, a.Dockerfile ?? "Dockerfile");
                if (df.Equals(dockerfilePath, StringComparison.OrdinalIgnoreCase)) return InferenceScanner.ImageShortName(a.Image);
            }
        }
        var i = dockerfilePath.LastIndexOf('/');
        if (i < 0) return null;
        var parent = dockerfilePath[..i];
        return parent[(parent.LastIndexOf('/') + 1)..];
    }

    private static string ScanContextPath(string dir, string rel) => ScanContext.Combine(dir, rel);

    // ------------------------------------------------------------------ TEST-001

    private static List<CheckOutcome> EvaluateTest001(RepoFacts facts)
    {
        var projects = facts.Dotnet.Projects;
        var deployables = projects.Where(p => DeployableKinds.Contains(p.Kind)).OrderBy(p => p.Path, StringComparer.Ordinal).ToList();
        if (deployables.Count == 0)
            return [Outcome(NotApplicable, null, "No deployable .NET projects (Web/Worker/Functions/Console) were found.", null)];

        var tests = projects.Where(p => p.IsTestProject).ToList();
        var byName = projects.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var outcomes = new List<CheckOutcome>();
        foreach (var d in deployables)
        {
            var direct = tests.Where(t => t.ProjectReferences.Contains(d.Name, StringComparer.OrdinalIgnoreCase)
                || t.Name.StartsWith(d.Name + ".", StringComparison.OrdinalIgnoreCase)).ToList();
            if (direct.Count > 0)
            {
                outcomes.Add(Outcome(Pass, d.Name, $"'{d.Name}' is covered by {string.Join(", ", direct.Select(t => t.Name))}.", null,
                    [Ev(d.Path, d.Kind), .. direct.Select(t => Ev(t.Path, t.TestFramework))]));
                continue;
            }

            // Indirect: a test project references something in the deployable's (in-repo) dependency closure.
            var closure = Closure(d, byName);
            var indirect = tests.Where(t => t.ProjectReferences.Any(closure.Contains)).ToList();
            if (indirect.Count > 0)
            {
                outcomes.Add(Outcome(Partial, d.Name,
                    $"No test project targets '{d.Name}' directly; {string.Join(", ", indirect.Select(t => t.Name))} cover(s) its dependencies only.",
                    $"Add a {d.Name}.Tests project (e.g. WebApplicationFactory / host-level tests) that references {d.Name}.",
                    [Ev(d.Path, d.Kind), .. indirect.Select(t => Ev(t.Path, t.TestFramework))]));
                continue;
            }

            outcomes.Add(Outcome(Fail, d.Name,
                tests.Count == 0 ? $"The repository has no test projects; '{d.Name}' is untested." : $"No test project references '{d.Name}' or its dependencies.",
                $"Add a {d.Name}.Tests project referencing {d.Name}.",
                [Ev(d.Path, d.Kind)]));
        }
        return outcomes;
    }

    private static HashSet<string> Closure(DotnetProject root, Dictionary<string, DotnetProject> byName)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>(root.ProjectReferences);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (!seen.Add(n)) continue;
            if (byName.TryGetValue(n, out var p)) foreach (var r in p.ProjectReferences) stack.Push(r);
        }
        return seen;
    }

    // ------------------------------------------------------------------ helpers

    private static IEnumerable<K8sWorkload> Ordered(IEnumerable<K8sWorkload> w)
        => w.OrderBy(x => x.Path, StringComparer.Ordinal).ThenBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Name, StringComparer.Ordinal);

    private static CheckOutcome Outcome(string result, string? component, string explanation, string? recommendation, IEnumerable<Evidence>? evidence = null)
        => new()
        {
            Result = result,
            Component = component,
            Explanation = explanation,
            Recommendation = recommendation,
            Evidence = evidence?.ToList() ?? [],
            Confidence = 1,
            Uncertain = false,
        };

    private static Evidence Ev(string path, string? note) => new() { Path = path, Note = note };

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

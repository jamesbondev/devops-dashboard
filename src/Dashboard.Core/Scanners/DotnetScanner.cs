using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Dashboard.Core.Scanners;

/// <summary>
/// Static MSBuild parsing: solutions, project files, Directory.Build.props inheritance, central package management
/// and global.json. Conditions are ignored (last unconditional value wins, else the first conditional one);
/// simple <c>$(Property)</c> references are substituted from known properties.
/// </summary>
internal static partial class DotnetScanner
{
    private static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj", ".sqlproj"];

    private static readonly string[] AnalyzerPackagePrefixes =
    [
        "Microsoft.CodeAnalysis.NetAnalyzers", "Microsoft.CodeAnalysis.FxCopAnalyzers", "StyleCop.Analyzers", "SonarAnalyzer.",
        "Roslynator.Analyzers", "Meziantou.Analyzer", "Microsoft.VisualStudio.Threading.Analyzers", "AsyncFixer",
        "IDisposableAnalyzers", "ErrorProne.NET", "SecurityCodeScan",
    ];

    [GeneratedRegex(@"\$\(([A-Za-z_][A-Za-z0-9_\-]*)\)")]
    private static partial Regex PropertyRef();

    [GeneratedRegex(@"\.(Unit|Integration|Functional|Acceptance|Component|Contract|E2E|Api|Architecture)?Tests?$", RegexOptions.IgnoreCase)]
    private static partial Regex TestNamePattern();

    private sealed class MsBuildFile
    {
        public Dictionary<string, string> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<PackageRef> PackageReferences { get; } = [];
        public List<string> ProjectReferences { get; } = [];
        public Dictionary<string, string> PackageVersions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<PackageRef> GlobalPackageReferences { get; } = [];
        public string? Sdk { get; set; }
        public bool ImportsParent { get; set; }
        public bool ClassicSqlProj { get; set; }
        public bool Parsed { get; set; }
    }

    public static void Scan(ScanContext ctx, RepoFacts facts, DotnetSupportCatalog catalog)
    {
        var d = facts.Dotnet;
        d.Solutions = ctx.WithExtension(".sln", ".slnx").Select(f => f.RelativePath).ToList();

        var propsFiles = ctx.Files.Where(f => f.Name.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => f.Directory, f => Parse(ctx, f), StringComparer.OrdinalIgnoreCase);
        var cpmFiles = ctx.Files.Where(f => f.Name.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => f.Directory, f => Parse(ctx, f), StringComparer.OrdinalIgnoreCase);
        d.HasDirectoryBuildProps = propsFiles.Count > 0;
        d.DirectoryPackagesPropsPath = ctx.Files.Where(f => f.Name.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.RelativePath.Count(c => c == '/')).Select(f => f.RelativePath).FirstOrDefault();

        ScanGlobalJson(ctx, d);

        foreach (var file in ctx.WithExtension(ProjectExtensions))
        {
            var proj = Parse(ctx, file);
            if (!proj.Parsed) continue;
            d.Projects.Add(BuildProject(file, proj, propsFiles, cpmFiles));
        }

        d.CentralPackageManagement = d.Projects.Any(p => p.CentralPackageManagement)
            || cpmFiles.Values.Any(c => IsTrue(c.Properties.GetValueOrDefault("ManagePackageVersionsCentrally")));

        var codeProjects = d.Projects.Where(p => p.Kind != "Database").ToList();
        d.TargetFrameworks = codeProjects.SelectMany(p => p.TargetFrameworks)
            .Where(t => !t.Contains("$(", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        d.UnsupportedTargetFrameworks = d.TargetFrameworks.Where(catalog.IsFlagged).ToList();

        d.Packages = d.Projects.SelectMany(p => p.PackageReferences)
            .GroupBy(p => (p.Id.ToUpperInvariant(), p.Version))
            .Select(g => new PackageVersionInfo { Id = g.First().Id, Version = g.Key.Version })
            .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Version, StringComparer.Ordinal)
            .ToList();
    }

    private static void ScanGlobalJson(ScanContext ctx, DotnetFacts d)
    {
        var gj = ctx.InRootNamed("global.json").FirstOrDefault();
        if (gj is null) return;
        d.HasGlobalJson = true;
        var text = ctx.ReadText(gj);
        if (text is null) return;
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("sdk", out var sdk) && sdk.ValueKind == JsonValueKind.Object
                && sdk.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
                d.SdkVersion = v.GetString();
        }
        catch (JsonException ex)
        {
            ctx.Warn($"Malformed global.json: {ex.Message}");
        }
    }

    private static DotnetProject BuildProject(RepoFile file, MsBuildFile proj,
        Dictionary<string, MsBuildFile> propsFiles, Dictionary<string, MsBuildFile> cpmFiles)
    {
        // Directory.Build.props chain: nearest first; continue upward only when it imports its parent.
        var chain = new List<MsBuildFile>();
        foreach (var dir in Ancestors(file.Directory))
        {
            if (!propsFiles.TryGetValue(dir, out var props)) continue;
            chain.Add(props);
            if (!props.ImportsParent) break;
        }
        chain.Reverse(); // outermost first so nearer files override

        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in chain) foreach (var kv in p.Properties) properties[kv.Key] = kv.Value;
        foreach (var kv in proj.Properties) properties[kv.Key] = kv.Value;
        properties["MSBuildProjectName"] = Path.GetFileNameWithoutExtension(file.Name);
        Substitute(properties);

        MsBuildFile? cpm = null;
        foreach (var dir in Ancestors(file.Directory))
            if (cpmFiles.TryGetValue(dir, out cpm)) break;
        var cpmEnabled = IsTrue(properties.GetValueOrDefault("ManagePackageVersionsCentrally"))
            || (cpm is not null && IsTrue(cpm.Properties.GetValueOrDefault("ManagePackageVersionsCentrally"))
                && !IsFalse(properties.GetValueOrDefault("ManagePackageVersionsCentrally")));

        var packages = new List<PackageRef>();
        void AddPackages(IEnumerable<PackageRef> refs)
        {
            foreach (var r in refs)
            {
                var version = r.Version is null ? null : SubstituteValue(r.Version, properties);
                if (version is null && cpm is not null && cpm.PackageVersions.TryGetValue(r.Id, out var central))
                    version = SubstituteValue(central, properties);
                var existing = packages.FindIndex(p => p.Id.Equals(r.Id, StringComparison.OrdinalIgnoreCase));
                var pr = new PackageRef { Id = r.Id, Version = version };
                if (existing >= 0) packages[existing] = pr; else packages.Add(pr);
            }
        }
        foreach (var p in chain) AddPackages(p.PackageReferences);
        if (cpmEnabled && cpm is not null) AddPackages(cpm.GlobalPackageReferences);
        AddPackages(proj.PackageReferences);

        var sdk = proj.Sdk ?? (proj.ClassicSqlProj ? "Microsoft.Data.Tools.Schema.SqlTasks" : "Classic");
        var name = Path.GetFileNameWithoutExtension(file.Name);
        var tfms = ReadTfms(properties);

        var isDatabase = file.Extension == ".sqlproj"
            || sdk.StartsWith("MSBuild.Sdk.SqlProj", StringComparison.OrdinalIgnoreCase)
            || sdk.StartsWith("Microsoft.Build.Sql", StringComparison.OrdinalIgnoreCase);

        var testFramework = DetectTestFramework(packages, sdk);
        var isTest = !isDatabase && (IsTrue(properties.GetValueOrDefault("IsTestProject"))
            || (!IsFalse(properties.GetValueOrDefault("IsTestProject"))
                && (testFramework is not null || HasPackage(packages, "Microsoft.NET.Test.Sdk") || TestNamePattern().IsMatch(name))));

        var analyzers = IsTrue(properties.GetValueOrDefault("EnableNETAnalyzers"))
            || (properties.TryGetValue("AnalysisLevel", out var al) && !string.IsNullOrWhiteSpace(al) && !al.Equals("none", StringComparison.OrdinalIgnoreCase))
            || (properties.TryGetValue("AnalysisMode", out var am) && !string.IsNullOrWhiteSpace(am) && !am.Equals("None", StringComparison.OrdinalIgnoreCase))
            || packages.Any(p => AnalyzerPackagePrefixes.Any(a => p.Id.StartsWith(a, StringComparison.OrdinalIgnoreCase)) || p.Id.EndsWith(".Analyzers", StringComparison.OrdinalIgnoreCase));
        if (IsFalse(properties.GetValueOrDefault("EnableNETAnalyzers")) && !packages.Any(p => AnalyzerPackagePrefixes.Any(a => p.Id.StartsWith(a, StringComparison.OrdinalIgnoreCase))))
            analyzers = false;

        return new DotnetProject
        {
            Path = file.RelativePath,
            Name = name,
            Sdk = sdk,
            Kind = DetectKind(isDatabase, isTest, sdk, properties, packages),
            TargetFrameworks = isDatabase && proj.ClassicSqlProj ? [] : tfms,
            Nullable = ParseNullable(properties.GetValueOrDefault("Nullable")),
            TreatWarningsAsErrors = ParseBool(properties.GetValueOrDefault("TreatWarningsAsErrors")),
            AnalyzersEnabled = analyzers,
            IsTestProject = isTest,
            TestFramework = isTest ? testFramework : null,
            ProjectReferences = proj.ProjectReferences.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            PackageReferences = packages.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList(),
            CentralPackageManagement = cpmEnabled,
        };
    }

    private static List<string> ReadTfms(Dictionary<string, string> props)
    {
        string? raw = null;
        if (props.TryGetValue("TargetFrameworks", out var multi) && !string.IsNullOrWhiteSpace(multi)) raw = multi;
        else if (props.TryGetValue("TargetFramework", out var single) && !string.IsNullOrWhiteSpace(single)) raw = single;
        else if (props.TryGetValue("TargetFrameworkVersion", out var classic) && !string.IsNullOrWhiteSpace(classic))
            raw = DotnetSupportCatalog.Normalize(classic);
        if (raw is null) return [];
        return raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant()).Distinct().ToList();
    }

    private static string DetectKind(bool isDatabase, bool isTest, string sdk, Dictionary<string, string> props, List<PackageRef> packages)
    {
        if (isDatabase) return "Database";
        if (isTest) return "Test";
        if (sdk.StartsWith("Microsoft.NET.Sdk.Functions", StringComparison.OrdinalIgnoreCase)
            || props.ContainsKey("AzureFunctionsVersion")
            || packages.Any(p => p.Id.StartsWith("Microsoft.Azure.Functions.Worker", StringComparison.OrdinalIgnoreCase)
                || p.Id.Equals("Microsoft.NET.Sdk.Functions", StringComparison.OrdinalIgnoreCase)))
            return "Functions";
        if (sdk.StartsWith("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase)
            || sdk.StartsWith("Microsoft.NET.Sdk.BlazorWebAssembly", StringComparison.OrdinalIgnoreCase)
            || (sdk.StartsWith("Microsoft.NET.Sdk.Razor", StringComparison.OrdinalIgnoreCase) && IsExe(props)))
            return "Web";
        if (sdk.StartsWith("Microsoft.NET.Sdk.Worker", StringComparison.OrdinalIgnoreCase)) return "Worker";
        if (IsExe(props))
        {
            return packages.Any(p => p.Id.StartsWith("Microsoft.Extensions.Hosting", StringComparison.OrdinalIgnoreCase))
                ? "Worker" : "Console";
        }
        return "Library";
    }

    private static bool IsExe(Dictionary<string, string> props)
        => props.TryGetValue("OutputType", out var ot) && (ot.Equals("Exe", StringComparison.OrdinalIgnoreCase) || ot.Equals("WinExe", StringComparison.OrdinalIgnoreCase));

    private static string? DetectTestFramework(List<PackageRef> packages, string sdk)
    {
        if (sdk.StartsWith("MSTest.Sdk", StringComparison.OrdinalIgnoreCase)) return "mstest";
        if (packages.Any(p => p.Id.Equals("xunit", StringComparison.OrdinalIgnoreCase) || p.Id.StartsWith("xunit.v3", StringComparison.OrdinalIgnoreCase)
            || p.Id.Equals("xunit.core", StringComparison.OrdinalIgnoreCase))) return "xunit";
        if (packages.Any(p => p.Id.Equals("NUnit", StringComparison.OrdinalIgnoreCase))) return "nunit";
        if (packages.Any(p => p.Id.StartsWith("MSTest", StringComparison.OrdinalIgnoreCase))) return "mstest";
        if (packages.Any(p => p.Id.Equals("TUnit", StringComparison.OrdinalIgnoreCase))) return "tunit";
        return null;
    }

    private static bool HasPackage(List<PackageRef> packages, string id) => packages.Any(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> Ancestors(string dir)
    {
        var d = dir;
        while (true)
        {
            yield return d;
            if (d.Length == 0) yield break;
            var i = d.LastIndexOf('/');
            d = i < 0 ? "" : d[..i];
        }
    }

    private static MsBuildFile Parse(ScanContext ctx, RepoFile file)
    {
        var result = new MsBuildFile();
        var text = ctx.ReadText(file);
        if (text is null) return result;
        XDocument doc;
        try
        {
            doc = XDocument.Parse(text, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            ctx.Warn($"Malformed MSBuild file '{file.RelativePath}': {ex.Message}");
            return result;
        }
        var root = doc.Root;
        if (root is null || root.Name.LocalName != "Project") return result;
        result.Parsed = true;

        var sdkAttr = (string?)root.Attribute("Sdk");
        var sdkEl = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Sdk")?.Attribute("Name")?.Value
            ?? root.Elements().FirstOrDefault(e => e.Name.LocalName == "Import" && e.Attribute("Sdk") is not null)?.Attribute("Sdk")?.Value;
        var sdk = sdkAttr ?? sdkEl;
        if (!string.IsNullOrWhiteSpace(sdk))
        {
            sdk = sdk.Split(';')[0].Trim();
            var slash = sdk.IndexOf('/');
            result.Sdk = slash > 0 ? sdk[..slash] : sdk;
        }
        result.ClassicSqlProj = file.Extension == ".sqlproj" && result.Sdk is null;

        var conditional = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in root.Descendants().Where(e => e.Name.LocalName == "PropertyGroup"))
        {
            var groupConditional = group.Attribute("Condition") is not null;
            foreach (var prop in group.Elements())
            {
                var value = prop.Value.Trim();
                var isConditional = groupConditional || prop.Attribute("Condition") is not null;
                if (isConditional) conditional.TryAdd(prop.Name.LocalName, value);
                else result.Properties[prop.Name.LocalName] = value;
            }
        }
        foreach (var kv in conditional) result.Properties.TryAdd(kv.Key, kv.Value);

        foreach (var item in root.Descendants().Where(e => e.Parent?.Name.LocalName == "ItemGroup"))
        {
            var include = ((string?)item.Attribute("Include"))?.Trim();
            switch (item.Name.LocalName)
            {
                case "PackageReference" when !string.IsNullOrEmpty(include):
                    var version = (string?)item.Attribute("VersionOverride") ?? (string?)item.Attribute("Version")
                        ?? item.Elements().FirstOrDefault(e => e.Name.LocalName is "VersionOverride" or "Version")?.Value;
                    result.PackageReferences.Add(new PackageRef { Id = include, Version = string.IsNullOrWhiteSpace(version) ? null : version.Trim() });
                    break;
                case "ProjectReference" when !string.IsNullOrEmpty(include):
                    var name = Path.GetFileNameWithoutExtension(include.Replace('\\', '/').Split('/')[^1]);
                    if (!string.IsNullOrEmpty(name)) result.ProjectReferences.Add(name);
                    break;
                case "PackageVersion" when !string.IsNullOrEmpty(include):
                    var pv = (string?)item.Attribute("Version") ?? item.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value;
                    if (!string.IsNullOrWhiteSpace(pv)) result.PackageVersions[include] = pv.Trim();
                    break;
                case "GlobalPackageReference" when !string.IsNullOrEmpty(include):
                    result.GlobalPackageReferences.Add(new PackageRef { Id = include, Version = ((string?)item.Attribute("Version"))?.Trim() });
                    break;
            }
        }

        result.ImportsParent = root.Elements().Any(e => e.Name.LocalName == "Import"
            && ((string?)e.Attribute("Project") ?? "").Contains("Directory.Build.props", StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static void Substitute(Dictionary<string, string> props)
    {
        foreach (var key in props.Keys.ToList())
            props[key] = SubstituteValue(props[key], props);
    }

    private static string SubstituteValue(string value, Dictionary<string, string> props)
    {
        var v = value;
        for (var i = 0; i < 5 && v.Contains("$(", StringComparison.Ordinal); i++)
        {
            var next = PropertyRef().Replace(v, m =>
                props.TryGetValue(m.Groups[1].Value, out var r) && !r.Contains("$(" + m.Groups[1].Value + ")", StringComparison.OrdinalIgnoreCase)
                    ? r : m.Value);
            if (next == v) break;
            v = next;
        }
        return v;
    }

    private static bool IsTrue(string? v) => v is not null && v.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    private static bool IsFalse(string? v) => v is not null && v.Trim().Equals("false", StringComparison.OrdinalIgnoreCase);
    private static bool? ParseBool(string? v) => IsTrue(v) ? true : IsFalse(v) ? false : null;

    /// <summary>"enable" → true; "disable", "warnings", "annotations" → false (not fully enabled); unset → null.</summary>
    private static bool? ParseNullable(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        return v.Trim().Equals("enable", StringComparison.OrdinalIgnoreCase) || v.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }
}

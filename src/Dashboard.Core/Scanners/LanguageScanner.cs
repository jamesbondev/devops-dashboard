namespace Dashboard.Core.Scanners;

internal static class LanguageScanner
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "C#", [".csx"] = "C#", [".fs"] = "F#", [".fsx"] = "F#", [".vb"] = "Visual Basic",
        [".ts"] = "TypeScript", [".tsx"] = "TypeScript", [".js"] = "JavaScript", [".jsx"] = "JavaScript",
        [".mjs"] = "JavaScript", [".cjs"] = "JavaScript", [".py"] = "Python", [".go"] = "Go", [".java"] = "Java",
        [".kt"] = "Kotlin", [".rb"] = "Ruby", [".rs"] = "Rust", [".php"] = "PHP", [".pp"] = "Puppet",
        [".tf"] = "Terraform", [".tfvars"] = "Terraform", [".hcl"] = "HCL", [".bicep"] = "Bicep",
        [".sql"] = "SQL", [".ps1"] = "PowerShell", [".psm1"] = "PowerShell", [".psd1"] = "PowerShell",
        [".sh"] = "Shell", [".bash"] = "Shell", [".razor"] = "Razor", [".cshtml"] = "Razor",
        [".html"] = "HTML", [".htm"] = "HTML", [".css"] = "CSS", [".scss"] = "SCSS", [".less"] = "Less",
        [".yml"] = "YAML", [".yaml"] = "YAML", [".json"] = "JSON", [".xml"] = "XML", [".md"] = "Markdown",
        [".erb"] = "ERB", [".groovy"] = "Groovy",
    };

    /// <summary>Data/markup languages never chosen as the primary language (unless nothing else exists).</summary>
    private static readonly HashSet<string> NonPrimary = new(StringComparer.OrdinalIgnoreCase)
    {
        "YAML", "JSON", "XML", "Markdown", "HTML", "CSS", "SCSS", "Less",
    };

    public static void Scan(ScanContext ctx, RepoFacts facts)
    {
        var files = new Dictionary<string, int>(StringComparer.Ordinal);
        var lines = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var f in ctx.Files)
        {
            var lang = Classify(f);
            if (lang is null) continue;
            var text = ctx.ReadText(f);
            if (text is null) continue;
            files[lang] = files.GetValueOrDefault(lang) + 1;
            lines[lang] = lines.GetValueOrDefault(lang) + ScanContext.CountLines(text);
        }
        facts.Languages.FileCounts = files.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary();
        facts.Languages.LineCounts = lines.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary();
        facts.Languages.Primary = lines.Where(k => !NonPrimary.Contains(k.Key))
            .OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal)
            .Select(k => k.Key).FirstOrDefault();
    }

    private static string? Classify(RepoFile f)
    {
        if (f.Name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) || DeployScanner.IsDockerfile(f)) return "Dockerfile";
        return ByExtension.GetValueOrDefault(f.Extension);
    }
}

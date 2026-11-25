using System.Text.RegularExpressions;

namespace Dashboard.Core.Scanners;

internal static partial class DocsScanner
{
    [GeneratedRegex(@"(?im)\b(dotnet\s+(run|build|test|watch)|docker[\s-]compose|docker\s+(run|build)|skaffold\s+dev|npm\s+(run|start|install|ci)|yarn\s+(start|build)|make\s+\w+|getting\s+started|run(ning)?\s+(it\s+)?locally|how\s+to\s+(run|build)|local\s+development|prerequisites)\b|^#+\s*(build|run|running|usage|development|quick\s*start)\b")]
    private static partial Regex RunKeywords();

    [GeneratedRegex(@"(?im)\b(deploy(s|ed|ing|ment|ments)?|release\s+process|helm\s+(install|upgrade)|kubectl\s+apply|skaffold\s+(run|deploy)|terraform\s+apply|rollout)\b")]
    private static partial Regex DeployKeywords();

    private static readonly string[] CodeownersDirs = ["", ".github", "docs", ".azuredevops"];

    public static void Scan(ScanContext ctx, RepoFacts facts)
    {
        var d = facts.Docs;
        var readme = ctx.Files.Where(f => f.Directory.Length == 0 && f.Name.StartsWith("README", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Extension == ".md" ? 0 : 1).ThenBy(f => f.Name, StringComparer.Ordinal).FirstOrDefault();
        if (readme is not null)
        {
            d.HasReadme = true;
            d.ReadmePath = readme.RelativePath;
            var text = ctx.ReadText(readme) ?? "";
            d.ReadmeLines = ScanContext.CountLines(text);
            d.ReadmeMentionsRun = RunKeywords().IsMatch(text);
            d.ReadmeMentionsDeploy = DeployKeywords().IsMatch(text);
        }
        d.HasCodeowners = ctx.Files.Any(f => f.Name.Equals("CODEOWNERS", StringComparison.OrdinalIgnoreCase)
            && CodeownersDirs.Contains(f.Directory, StringComparer.OrdinalIgnoreCase));
        d.HasEditorConfig = ctx.InRootNamed(".editorconfig").Any();
        d.HasGitattributes = ctx.InRootNamed(".gitattributes").Any();
    }
}

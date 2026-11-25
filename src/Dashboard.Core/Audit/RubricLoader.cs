using System.Security.Cryptography;
using System.Text;
using Dashboard.Core.Contracts;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Dashboard.Core.Audit;

public sealed class Rubric
{
    /// <summary>Version of the base rubric file (e.g. "v1").</summary>
    public string Version { get; set; } = "";
    public List<AuditCheck> Checks { get; set; } = [];
    /// <summary>SHA-256 (hex, first 16 chars) over the base and team files; changes when any check text changes.</summary>
    public string Hash { get; set; } = "";
    /// <summary>Problems in team files (skipped entries / files). The base rubric throws instead.</summary>
    public List<string> Warnings { get; set; } = [];

    public IEnumerable<AuditCheck> FactChecks => Checks.Where(c => c.Kind.Equals("fact", StringComparison.OrdinalIgnoreCase));
    public IEnumerable<AuditCheck> JudgementChecks => Checks.Where(c => c.Kind.Equals("judgement", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Loads <c>{root}/{version}/checks.yaml</c> plus any <c>{root}/team/*.yaml|*.yml</c> (same schema, appended in
/// file-name order). Base rubric problems throw <see cref="InvalidDataException"/>; team problems become warnings.
/// </summary>
public static class RubricLoader
{
    public const string DefaultVersion = "v1";

    private static readonly string[] Severities = ["low", "medium", "high"];
    private static readonly string[] Kinds = ["fact", "judgement"];

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private sealed class RubricFile
    {
        public string? Version { get; set; }
        public List<AuditCheck>? Checks { get; set; }
    }

    public static Rubric Load(string rubricRoot) => Load(rubricRoot, DefaultVersion);

    public static Rubric Load(string rubricRoot, string version)
    {
        var basePath = Path.Combine(rubricRoot, version, "checks.yaml");
        if (!File.Exists(basePath)) throw new FileNotFoundException($"Rubric file not found: {basePath}", basePath);

        var hashInput = new StringBuilder();
        var baseText = File.ReadAllText(basePath);
        hashInput.Append(baseText);
        var rubric = Parse(baseText, basePath);

        var teamDir = Path.Combine(rubricRoot, "team");
        if (Directory.Exists(teamDir))
        {
            var teamFiles = Directory.EnumerateFiles(teamDir)
                .Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal);
            foreach (var file in teamFiles)
            {
                var text = File.ReadAllText(file);
                hashInput.Append('\n').Append(Path.GetFileName(file)).Append('\n').Append(text);
                RubricFile? parsed;
                try { parsed = Deserializer.Deserialize<RubricFile>(text); }
                catch (Exception ex) when (ex is YamlDotNet.Core.YamlException)
                {
                    rubric.Warnings.Add($"{Path.GetFileName(file)}: YAML error: {ex.Message}");
                    continue;
                }
                foreach (var check in parsed?.Checks ?? [])
                {
                    var error = Validate(check);
                    if (error is not null) { rubric.Warnings.Add($"{Path.GetFileName(file)}: {error}; skipped."); continue; }
                    if (rubric.Checks.Any(c => c.Id.Equals(check.Id, StringComparison.OrdinalIgnoreCase)))
                    {
                        rubric.Warnings.Add($"{Path.GetFileName(file)}: duplicate check id '{check.Id}'; skipped.");
                        continue;
                    }
                    rubric.Checks.Add(Normalize(check));
                }
            }
        }

        rubric.Hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput.ToString().Replace("\r\n", "\n", StringComparison.Ordinal))))[..16];
        return rubric;
    }

    /// <summary>Parses a single rubric document (base schema). Throws <see cref="InvalidDataException"/> when invalid.</summary>
    public static Rubric Parse(string yaml, string? sourceName = null)
    {
        var name = sourceName ?? "rubric";
        RubricFile? file;
        try { file = Deserializer.Deserialize<RubricFile>(yaml); }
        catch (YamlDotNet.Core.YamlException ex) { throw new InvalidDataException($"{name}: YAML error: {ex.Message}", ex); }
        if (file is null || string.IsNullOrWhiteSpace(file.Version)) throw new InvalidDataException($"{name}: missing 'version'.");
        if (file.Checks is null || file.Checks.Count == 0) throw new InvalidDataException($"{name}: no checks.");

        var rubric = new Rubric { Version = file.Version.Trim() };
        foreach (var check in file.Checks)
        {
            var error = Validate(check);
            if (error is not null) throw new InvalidDataException($"{name}: {error}.");
            if (rubric.Checks.Any(c => c.Id.Equals(check.Id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"{name}: duplicate check id '{check.Id}'.");
            rubric.Checks.Add(Normalize(check));
        }
        return rubric;
    }

    private static string? Validate(AuditCheck? c)
    {
        if (c is null) return "empty check entry";
        if (string.IsNullOrWhiteSpace(c.Id)) return "check without id";
        if (string.IsNullOrWhiteSpace(c.Title)) return $"check '{c.Id}' has no title";
        if (!Kinds.Contains(c.Kind?.Trim().ToLowerInvariant())) return $"check '{c.Id}' has invalid kind '{c.Kind}' (fact|judgement)";
        if (!Severities.Contains(c.Severity?.Trim().ToLowerInvariant())) return $"check '{c.Id}' has invalid severity '{c.Severity}' (low|medium|high)";
        return null;
    }

    private static AuditCheck Normalize(AuditCheck c) => new()
    {
        Id = c.Id.Trim(),
        Area = c.Area?.Trim() ?? "",
        Title = c.Title.Trim(),
        Severity = c.Severity.Trim().ToLowerInvariant(),
        Kind = c.Kind.Trim().ToLowerInvariant(),
        Description = c.Description?.Trim() ?? "",
        Guidance = c.Guidance?.Trim() ?? "",
    };
}

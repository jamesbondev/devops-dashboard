using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Dashboard.Auditor.Audit;
using Dashboard.Auditor.Security;
using Dashboard.Core.Contracts;
using Dashboard.Core.Scanners;

namespace Dashboard.Auditor.Engines;

/// <summary>
/// Deterministic, offline engine: folder/project-name heuristics plus a few keyword checks.
/// Only paths that exist in the worktree are ever cited.
/// </summary>
public sealed partial class MockAuditEngine(AuditorOptions options, ILogger<MockAuditEngine> logger) : IAuditEngine
{
    public const string ModelName = "mock-heuristic-v1";
    private const int MaxEvidence = 5;

    private static readonly JsonSerializerOptions FactsJson = new(JsonSerializerDefaults.Web);

    private static readonly string[] InfrastructurePackagePrefixes =
    [
        "Microsoft.EntityFrameworkCore", "Dapper", "Microsoft.Data.SqlClient", "System.Data.SqlClient", "Npgsql",
        "Azure.", "Microsoft.Azure.", "MassTransit", "NServiceBus", "StackExchange.Redis", "RabbitMQ.Client",
        "Confluent.Kafka", "MongoDB.Driver", "Microsoft.AspNetCore.", "Polly",
    ];

    private static readonly string[] InfrastructureProjectTokens =
        ["Infrastructure", "Infra", "Persistence", "Data", "DataAccess", "Api", "Web", "Repositories"];

    public string Name => "mock";

    public Task<AuditorStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new AuditorStatus
        {
            Engine = Name,
            Ready = true,
            Authenticated = true,
            Login = "mock",
            Models = [ModelName],
            SelectedModel = ModelName,
            SdkVersion = SdkInfo.Version,
        });

    public async Task<AuditEngineResult> RunAuditAsync(AuditRequest request, CancellationToken cancellationToken)
    {
        if (!PathGuard.TryResolveInside(options.AllowedRoot, request.WorktreePath, out var worktree, out var reason))
        {
            throw new InvalidOperationException($"worktree rejected: {reason}");
        }
        if (!Directory.Exists(worktree))
        {
            throw new DirectoryNotFoundException($"worktree '{request.WorktreePath}' does not exist");
        }

        if (options.MockDelayMs > 0)
        {
            await Task.Delay(options.MockDelayMs, cancellationToken);
        }

        var transcript = new StringBuilder();
        transcript.AppendLine($"[mock] engine={Name} model={ModelName} repo={request.RepoName} sha={request.Sha}");

        var facts = ParseFacts(request.FactsJson, transcript);
        var index = WorktreeIndex.Build(worktree);
        transcript.AppendLine($"[mock] indexed {index.Files.Count} files and {index.Directories.Count} directories{(index.Truncated ? " (truncated)" : "")}");
        cancellationToken.ThrowIfCancellationRequested();

        var projects = index.Files.Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).ToList();
        var (style, rationale, archEvidence) = DetectArchitecture(index, projects);
        transcript.AppendLine($"[mock] architecture: {style} ({archEvidence.Count} evidence paths)");

        var result = new AuditResult
        {
            ArchitectureStyle = style,
            ArchitectureRationale = rationale,
            ArchitectureEvidence = archEvidence,
            AzureDependencies = facts.AzureServices.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Components = facts.Components
                .Select(c => new ComponentGuess { Name = c.Name, Kind = c.Kind, Path = string.IsNullOrEmpty(c.Path) ? null : c.Path })
                .ToList(),
        };

        foreach (var check in request.Checks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = check.Id.ToUpperInvariant() switch
            {
                "ARCH-001" => CheckArchitectureStyle(check, style, archEvidence, index),
                "ARCH-002" => CheckDependencyDirection(check, index, projects, facts),
                "DATA-001" => CheckDataAccess(check, index, projects, facts),
                "MSG-001" => CheckMessaging(check, index, facts),
                _ => Placeholder(check, request.Sha, index, projects),
            };
            transcript.AppendLine($"[mock] {outcome.CheckId}: {outcome.Result} (confidence {outcome.Confidence:0.00}, {outcome.Evidence.Count} evidence)");
            result.Checks.Add(outcome);
        }

        var verification = EvidenceVerifier.Verify(result, worktree);
        if (verification.Dropped > 0)
        {
            transcript.AppendLine($"[mock] dropped {verification.Dropped} unverifiable evidence paths");
        }

        var failing = result.Checks.Count(c => c.Result == "fail");
        var partial = result.Checks.Count(c => c.Result == "partial");
        result.Summary =
            $"Mock heuristic audit of {request.RepoName}: {projects.Count} .NET project(s), architecture looks '{style}'. " +
            $"{result.Checks.Count} judgement check(s) evaluated: {failing} fail, {partial} partial. " +
            "Outcomes come from folder/project names and keyword scans, not from an LLM.";

        logger.LogInformation("Mock audit {AuditId}: style={Style}, checks={Checks}", request.AuditId, style, result.Checks.Count);
        return new AuditEngineResult(result, transcript.ToString(), ModelName, AuditPrompt.ComputeHash(ModelName, request));
    }

    public Task<TextTaskResponse> RunTextAsync(TextTaskRequest request, CancellationToken cancellationToken)
    {
        var lines = (request.Input ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var nonEmpty = lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).ToList();
        var sb = new StringBuilder();
        switch (request.Kind)
        {
            case "weekly-brief":
                sb.AppendLine("Weekly brief (mock engine)");
                sb.AppendLine();
                sb.AppendLine($"Input: {nonEmpty.Count} non-empty line(s), {request.Input?.Length ?? 0} characters.");
                sb.AppendLine();
                sb.AppendLine("Highlights:");
                foreach (var l in nonEmpty.Take(10))
                {
                    sb.Append("- ").AppendLine(l.Length > 160 ? l[..160] + "…" : l);
                }
                if (nonEmpty.Count > 10)
                {
                    sb.AppendLine($"- …and {nonEmpty.Count - 10} more line(s).");
                }
                break;
            case "template-summary":
                var added = lines.Count(l => l.StartsWith('+') && !l.StartsWith("+++", StringComparison.Ordinal));
                var removed = lines.Count(l => l.StartsWith('-') && !l.StartsWith("---", StringComparison.Ordinal));
                var files = lines.Where(l => l.StartsWith("+++ ", StringComparison.Ordinal) || l.StartsWith("diff --git", StringComparison.Ordinal))
                    .Select(l => l.Split(' ').Last().TrimStart('a', 'b').TrimStart('/'))
                    .Distinct()
                    .ToList();
                sb.AppendLine("Template change summary (mock engine)");
                sb.AppendLine();
                sb.AppendLine($"{added} line(s) added, {removed} line(s) removed across {Math.Max(files.Count, nonEmpty.Count > 0 ? 1 : 0)} file(s).");
                foreach (var f in files.Take(20))
                {
                    sb.Append("- ").AppendLine(f);
                }
                break;
            default:
                throw new ArgumentException($"unknown text task kind '{request.Kind}'");
        }
        return Task.FromResult(new TextTaskResponse { Text = sb.ToString().TrimEnd(), Engine = Name, Model = ModelName });
    }

    private static RepoFacts ParseFacts(string? json, StringBuilder transcript)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new RepoFacts();
        }
        try
        {
            return JsonSerializer.Deserialize<RepoFacts>(json, FactsJson) ?? new RepoFacts();
        }
        catch (JsonException ex)
        {
            transcript.AppendLine($"[mock] facts JSON could not be parsed ({ex.Message}); continuing without facts");
            return new RepoFacts();
        }
    }

    // ---------- architecture ----------

    internal static (string Style, string Rationale, List<Evidence> Evidence) DetectArchitecture(WorktreeIndex index, IReadOnlyList<string> projects)
    {
        string? Locate(params string[] names)
        {
            foreach (var name in names)
            {
                var proj = projects.FirstOrDefault(p => Tokens(Path.GetFileNameWithoutExtension(WorktreeIndex.Name(p)))
                    .Contains(name, StringComparer.OrdinalIgnoreCase));
                if (proj is not null)
                {
                    return proj;
                }
                var dir = index.Directories.FirstOrDefault(d => WorktreeIndex.Name(d).Equals(name, StringComparison.OrdinalIgnoreCase));
                if (dir is not null)
                {
                    return dir;
                }
            }
            return null;
        }

        var domain = Locate("Domain");
        var application = Locate("Application", "UseCases");
        var infrastructure = Locate("Infrastructure", "Infra", "Persistence");
        if (domain is not null && application is not null && infrastructure is not null)
        {
            return ("clean",
                "Separate Domain, Application and Infrastructure projects/folders indicate a Clean Architecture (onion) layout.",
                [Ev(domain, "domain layer"), Ev(application, "application layer"), Ev(infrastructure, "infrastructure layer")]);
        }

        var features = index.Directories.FirstOrDefault(d => WorktreeIndex.Name(d) is "Features" or "features" or "Slices");
        if (features is not null)
        {
            var sample = index.Directories.Where(d => WorktreeIndex.Parent(d) == features).Take(3).Select(d => Ev(d, "feature slice"));
            return ("vertical-slice",
                "Code is grouped by feature under a Features folder rather than by technical layer.",
                [Ev(features, "feature folders"), .. sample]);
        }

        var controllers = DirNamed(index, "Controllers");
        var services = DirNamed(index, "Services");
        var repositories = DirNamed(index, "Repositories") ?? DirNamed(index, "Repository");
        if (controllers is not null && services is not null && repositories is not null)
        {
            return ("layered",
                "Controllers, Services and Repositories folders indicate a classic n-tier layered design.",
                [Ev(controllers, "presentation layer"), Ev(services, "business layer"), Ev(repositories, "data layer")]);
        }

        var evidence = index.Files
            .Where(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            .Concat(projects)
            .Take(3)
            .Select(f => Ev(f, "project structure"))
            .ToList();
        return ("other", "No recognisable clean, layered or vertical-slice folder/project naming was found.", evidence);
    }

    private static string? DirNamed(WorktreeIndex index, string name) =>
        index.Directories.FirstOrDefault(d => WorktreeIndex.Name(d).Equals(name, StringComparison.OrdinalIgnoreCase));

    private static CheckOutcome CheckArchitectureStyle(AuditCheck check, string style, List<Evidence> evidence, WorktreeIndex index)
    {
        var known = style != "other";
        return new CheckOutcome
        {
            CheckId = check.Id,
            Result = known ? "pass" : "partial",
            Explanation = known
                ? $"The repository follows a recognisable '{style}' structure."
                : "No recognisable architecture style from folder and project names.",
            Recommendation = known ? null : "Document the intended architecture and align folder/project names with it.",
            Evidence = evidence.Count > 0 ? [.. evidence] : FirstExisting(index, "README.md"),
            Confidence = known ? 0.7 : 0.4,
        };
    }

    // ---------- ARCH-002 dependency direction ----------

    private static CheckOutcome CheckDependencyDirection(AuditCheck check, WorktreeIndex index, IReadOnlyList<string> projects, RepoFacts facts)
    {
        var domainProjects = projects
            .Where(p => Tokens(Path.GetFileNameWithoutExtension(WorktreeIndex.Name(p))).Contains("Domain", StringComparer.OrdinalIgnoreCase)
                        || WorktreeIndex.Parent(p).Split('/').Contains("Domain", StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (domainProjects.Count == 0)
        {
            return new CheckOutcome
            {
                CheckId = check.Id,
                Result = "na",
                Explanation = "No Domain project found, so dependency direction cannot be assessed.",
                Confidence = 0.5,
            };
        }

        var violations = new List<Evidence>();
        foreach (var project in domainProjects)
        {
            var text = index.ReadText(project);
            if (text is null)
            {
                continue;
            }
            foreach (var (include, kind, line) in ReadReferences(text))
            {
                if (kind == "PackageReference" && InfrastructurePackagePrefixes.Any(p => include.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                {
                    violations.Add(new Evidence { Path = project, Line = line, Note = $"Domain references infrastructure package {include}" });
                }
                else if (kind == "ProjectReference")
                {
                    var refName = Path.GetFileNameWithoutExtension(include.Replace('\\', '/').Split('/').Last());
                    if (Tokens(refName).Intersect(InfrastructureProjectTokens, StringComparer.OrdinalIgnoreCase).Any())
                    {
                        violations.Add(new Evidence { Path = project, Line = line, Note = $"Domain references project {refName}" });
                    }
                }
            }
        }

        // Fall back to scanner facts for projects whose csproj could not be read.
        foreach (var p in facts.Dotnet.Projects.Where(p => Tokens(p.Name).Contains("Domain", StringComparer.OrdinalIgnoreCase)))
        {
            if (violations.Any(v => v.Path.Equals(p.Path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            foreach (var pkg in p.PackageReferences.Where(r => InfrastructurePackagePrefixes.Any(x => r.Id.StartsWith(x, StringComparison.OrdinalIgnoreCase))))
            {
                violations.Add(new Evidence { Path = p.Path, Note = $"Domain references infrastructure package {pkg.Id} (from scanner facts)" });
            }
        }

        if (violations.Count > 0)
        {
            return new CheckOutcome
            {
                CheckId = check.Id,
                Result = "fail",
                Component = Path.GetFileNameWithoutExtension(WorktreeIndex.Name(domainProjects[0])),
                Explanation = $"{violations.Count} infrastructure dependenc{(violations.Count == 1 ? "y" : "ies")} found in Domain project(s); dependencies should point inwards.",
                Recommendation = "Move persistence/messaging/cloud SDK references to Infrastructure and expose abstractions from Domain/Application.",
                Evidence = violations.Take(MaxEvidence).ToList(),
                Confidence = 0.75,
            };
        }
        return new CheckOutcome
        {
            CheckId = check.Id,
            Result = "pass",
            Explanation = "Domain project(s) reference no infrastructure packages or projects.",
            Evidence = domainProjects.Take(MaxEvidence).Select(p => Ev(p, "domain project references checked")).ToList(),
            Confidence = 0.65,
        };
    }

    internal static IEnumerable<(string Include, string Kind, int? Line)> ReadReferences(string csproj)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(csproj, LoadOptions.SetLineInfo);
        }
        catch (System.Xml.XmlException)
        {
            yield break;
        }
        foreach (var el in doc.Descendants().Where(e => e.Name.LocalName is "PackageReference" or "ProjectReference"))
        {
            var include = (string?)el.Attribute("Include") ?? (string?)el.Attribute("Update");
            if (string.IsNullOrWhiteSpace(include))
            {
                continue;
            }
            var li = (System.Xml.IXmlLineInfo)el;
            yield return (include, el.Name.LocalName, li.HasLineInfo() ? li.LineNumber : null);
        }
    }

    // ---------- DATA-001 data access consistency ----------

    [GeneratedRegex(@"class\s+\w+\s*(?:\([^)]*\))?\s*:\s*[\w.]*DbContext\b|\bAddDbContext(?:Pool|Factory)?<|\bDbSet<")]
    private static partial Regex EfUsage();

    [GeneratedRegex(@"^\s*using\s+Dapper\s*;", RegexOptions.Multiline)]
    private static partial Regex DapperUsage();

    private static CheckOutcome CheckDataAccess(AuditCheck check, WorktreeIndex index, IReadOnlyList<string> projects, RepoFacts facts)
    {
        var projectDirs = projects.Select(p => (Dir: WorktreeIndex.Parent(p), Project: p))
            .OrderByDescending(x => x.Dir.Length)
            .ToList();
        var byProject = new Dictionary<string, (bool Ef, bool Dapper, List<Evidence> Evidence)>(StringComparer.Ordinal);

        foreach (var file in index.Files.Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            var text = index.ReadText(file);
            if (text is null)
            {
                continue;
            }
            var ef = EfUsage().Match(text);
            var dapper = DapperUsage().Match(text);
            if (!ef.Success && !dapper.Success)
            {
                continue;
            }
            var owner = projectDirs.FirstOrDefault(x => x.Dir.Length == 0 || file.StartsWith(x.Dir + "/", StringComparison.Ordinal)).Project ?? "(no project)";
            var entry = byProject.TryGetValue(owner, out var e) ? e : (Ef: false, Dapper: false, Evidence: new List<Evidence>());
            var match = ef.Success ? ef : dapper;
            entry.Evidence.Add(new Evidence
            {
                Path = file,
                Line = LineOf(text, match.Index),
                Note = ef.Success ? "EF Core DbContext usage" : "Dapper usage",
            });
            byProject[owner] = (entry.Ef || ef.Success, entry.Dapper || dapper.Success, entry.Evidence);
        }

        // Scanner facts may know about data-access projects we could not attribute.
        var factProjects = facts.Data.DataAccessProjects.Where(n => !byProject.Keys.Any(k => k.Contains(n, StringComparison.OrdinalIgnoreCase))).ToList();
        var count = byProject.Count + factProjects.Count;
        var evidence = byProject.Values.SelectMany(v => v.Evidence.Take(2)).Take(MaxEvidence).ToList();
        var mixed = byProject.Values.Any(v => v.Ef) && byProject.Values.Any(v => v.Dapper);
        var names = byProject.Keys.Select(k => Path.GetFileNameWithoutExtension(WorktreeIndex.Name(k))).Concat(factProjects).ToList();

        if (count == 0)
        {
            return new CheckOutcome
            {
                CheckId = check.Id,
                Result = "na",
                Explanation = "No EF Core DbContext or Dapper usage found.",
                Confidence = 0.6,
            };
        }
        var result = count switch
        {
            1 when !mixed => "pass",
            1 or 2 => "partial",
            _ => "fail",
        };
        return new CheckOutcome
        {
            CheckId = check.Id,
            Result = result,
            Component = count == 1 ? names[0] : null,
            Explanation = $"Data access code found in {count} project(s): {string.Join(", ", names)}{(mixed ? "; both EF Core and Dapper are used" : "")}.",
            Recommendation = result == "pass" ? null : "Concentrate data access in one infrastructure/persistence project behind repository or query abstractions.",
            Evidence = evidence,
            Confidence = result == "pass" ? 0.65 : 0.6,
        };
    }

    // ---------- MSG-001 retries and dead-lettering ----------

    [GeneratedRegex(@"dead[-_ ]?letter|DeadLetter", RegexOptions.IgnoreCase)]
    private static partial Regex DeadLetter();

    [GeneratedRegex(@"\bretry|MaxDeliveryCount|max_delivery_count|UseMessageRetry|RetryPolicy|ServiceBusRetryOptions|\bPolly\b", RegexOptions.IgnoreCase)]
    private static partial Regex Retry();

    [GeneratedRegex(@"ServiceBus(Client|Processor|Sender|Receiver)|IConsumer<|MassTransit|NServiceBus|azurerm_servicebus", RegexOptions.None)]
    private static partial Regex MessagingUsage();

    private static readonly string[] MessagingScanExtensions = [".cs", ".json", ".yaml", ".yml", ".tf", ".bicep"];

    private static CheckOutcome CheckMessaging(AuditCheck check, WorktreeIndex index, RepoFacts facts)
    {
        var deadLetter = new List<Evidence>();
        var retry = new List<Evidence>();
        var usage = new List<Evidence>();
        foreach (var file in index.Files.Where(f => MessagingScanExtensions.Any(x => f.EndsWith(x, StringComparison.OrdinalIgnoreCase))))
        {
            if (file.EndsWith("packages.lock.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var text = index.ReadText(file);
            if (text is null)
            {
                continue;
            }
            Collect(DeadLetter(), text, file, "dead-letter handling", deadLetter);
            Collect(Retry(), text, file, "retry configuration", retry);
            Collect(MessagingUsage(), text, file, "messaging usage", usage);
        }

        var usesMessaging = facts.Messaging.UsesServiceBus || facts.Messaging.UsesMassTransit || facts.Messaging.UsesNServiceBus || usage.Count > 0;
        if (!usesMessaging)
        {
            return new CheckOutcome
            {
                CheckId = check.Id,
                Result = "na",
                Explanation = "No message broker usage found.",
                Confidence = 0.6,
            };
        }

        var hasDl = deadLetter.Count > 0 || facts.Messaging.DeadLetterHandlingFound;
        var hasRetry = retry.Count > 0;
        var (result, explanation) = (hasDl, hasRetry) switch
        {
            (true, true) => ("pass", "Both retry configuration and dead-letter handling are present."),
            (true, false) => ("partial", "Dead-letter handling is present but no explicit retry configuration was found."),
            (false, true) => ("partial", "Retry configuration is present but no dead-letter handling was found."),
            _ => ("fail", "Messaging is used but neither retries nor dead-letter handling are explicit."),
        };
        return new CheckOutcome
        {
            CheckId = check.Id,
            Result = result,
            Explanation = explanation,
            Recommendation = result == "pass" ? null : "Configure retry policies explicitly and process (or alert on) dead-lettered messages.",
            Evidence = deadLetter.Take(2).Concat(retry.Take(2)).Concat(usage.Take(MaxEvidence)).Take(MaxEvidence).ToList(),
            Confidence = result == "pass" ? 0.6 : 0.55,
        };
    }

    private static void Collect(Regex regex, string text, string file, string note, List<Evidence> into)
    {
        if (into.Count >= MaxEvidence)
        {
            return;
        }
        var m = regex.Match(text);
        if (m.Success)
        {
            into.Add(new Evidence { Path = file, Line = LineOf(text, m.Index), Note = note });
        }
    }

    // ---------- other checks ----------

    private static CheckOutcome Placeholder(AuditCheck check, string sha, WorktreeIndex index, IReadOnlyList<string> projects)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(check.Id + "|" + sha));
        var pass = (hash[0] & 1) == 0;
        var evidence = FirstExisting(index, "README.md", "readme.md");
        if (evidence.Count == 0 && projects.Count > 0)
        {
            evidence = [Ev(projects[0], "project file")];
        }
        return new CheckOutcome
        {
            CheckId = check.Id,
            Result = pass ? "pass" : "partial",
            Explanation = $"Mock heuristic: no rule for {check.Id}; this is a deterministic placeholder outcome.",
            Recommendation = pass ? null : $"Review '{check.Title}' manually.",
            Evidence = evidence,
            Confidence = 0.3,
        };
    }

    // ---------- helpers ----------

    private static List<Evidence> FirstExisting(WorktreeIndex index, params string[] candidates)
    {
        var hit = candidates.FirstOrDefault(c => index.Files.Contains(c, StringComparer.Ordinal));
        return hit is null ? [] : [Ev(hit, null)];
    }

    private static Evidence Ev(string path, string? note) => new() { Path = path, Note = note };

    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }
        return line;
    }

    internal static string[] Tokens(string name) =>
        name.Split(['.', '-', '_', ' '], StringSplitOptions.RemoveEmptyEntries);
}

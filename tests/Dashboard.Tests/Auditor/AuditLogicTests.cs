using Dashboard.Auditor.Audit;
using Dashboard.Core.Contracts;

namespace Dashboard.Tests.Auditor;

public sealed class VoteAggregatorTests
{
    private static AuditResult Vote(string style, params (string Id, string Result, double Confidence)[] checks) => new()
    {
        ArchitectureStyle = style,
        ArchitectureRationale = $"{style} rationale",
        Summary = $"{style} summary",
        AzureDependencies = ["Key Vault"],
        Components = [new ComponentGuess { Name = "api", Kind = "web-api" }],
        Checks = checks.Select(c => new CheckOutcome
        {
            CheckId = c.Id,
            Result = c.Result,
            Confidence = c.Confidence,
            Explanation = $"{c.Result} because",
            Evidence = [new Evidence { Path = $"{c.Result}.cs" }],
        }).ToList(),
    };

    [Fact]
    public void Single_vote_is_returned_unchanged()
    {
        var v = Vote("clean", ("A", "pass", 0.9));
        Assert.Same(v, VoteAggregator.Aggregate([v], ["A"]));
        Assert.False(v.Checks[0].Uncertain);
    }

    [Fact]
    public void Unanimous_votes_are_certain()
    {
        var r = VoteAggregator.Aggregate(
            [Vote("clean", ("A", "fail", 0.8)), Vote("clean", ("A", "fail", 0.6)), Vote("clean", ("A", "fail", 0.7))],
            ["A"]);
        var a = Assert.Single(r.Checks);
        Assert.Equal("fail", a.Result);
        Assert.False(a.Uncertain);
        Assert.Equal(0.7, a.Confidence, 3);
    }

    [Fact]
    public void Majority_wins_and_disagreement_is_uncertain()
    {
        var r = VoteAggregator.Aggregate(
            [
                Vote("clean", ("A", "pass", 0.9), ("B", "fail", 0.5)),
                Vote("layered", ("A", "pass", 0.7), ("B", "fail", 0.9)),
                Vote("clean", ("A", "partial", 0.8), ("B", "fail", 0.7)),
            ],
            ["A", "B"]);

        var a = r.Checks.Single(c => c.CheckId == "A");
        Assert.Equal("pass", a.Result);
        Assert.True(a.Uncertain);
        Assert.Equal(0.8 * 2 / 3, a.Confidence, 3);
        Assert.Equal("pass.cs", a.Evidence.Single().Path);
        Assert.Contains("votes split", a.Explanation, StringComparison.Ordinal);

        var b = r.Checks.Single(c => c.CheckId == "B");
        Assert.Equal("fail", b.Result);
        Assert.False(b.Uncertain);

        Assert.Equal("clean", r.ArchitectureStyle);
        Assert.StartsWith("clean rationale", r.ArchitectureRationale, StringComparison.Ordinal);
        Assert.Equal(["Key Vault"], r.AzureDependencies);
        Assert.Single(r.Components);
    }

    [Fact]
    public void Tie_resolves_to_more_severe_outcome_and_is_uncertain()
    {
        var r = VoteAggregator.Aggregate([Vote("clean", ("A", "pass", 0.9)), Vote("clean", ("A", "fail", 0.6))], ["A"]);
        var a = r.Checks.Single();
        Assert.Equal("fail", a.Result);
        Assert.True(a.Uncertain);
        Assert.Contains("tie", a.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_check_in_a_vote_counts_as_uncertain_na()
    {
        var r = VoteAggregator.Aggregate([Vote("clean", ("A", "pass", 0.9)), Vote("clean")], ["A"]);
        Assert.True(r.Checks.Single().Uncertain);
    }
}

public sealed class EvidenceVerifierTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly TempDir _outside = new();

    public EvidenceVerifierTests()
    {
        _dir.Write("src/Domain/Order.cs", "line1\nline2\nline3\n");
        _dir.Write("README.md", "# hi");
        _outside.Write("secret.txt", "x");
    }

    public void Dispose()
    {
        _dir.Dispose();
        _outside.Dispose();
    }

    [Fact]
    public void Keeps_existing_paths_and_normalises_them()
    {
        var result = new AuditResult
        {
            ArchitectureEvidence =
            [
                new Evidence { Path = "./src/Domain/Order.cs", Line = 2 },
                new Evidence { Path = "src\\Domain" },
                new Evidence { Path = "/README.md" },
                new Evidence { Path = Path.Combine(_dir.Path, "src/Domain/Order.cs"), Line = 99 },
            ],
        };
        var v = EvidenceVerifier.Verify(result, _dir.Path);

        Assert.Equal(0, v.Dropped);
        Assert.Equal(["src/Domain/Order.cs", "src/Domain", "README.md", "src/Domain/Order.cs"], result.ArchitectureEvidence.Select(e => e.Path));
        Assert.Equal(2, result.ArchitectureEvidence[0].Line);
        Assert.Null(result.ArchitectureEvidence[3].Line); // line beyond EOF is cleared
    }

    [Fact]
    public void Drops_missing_outside_and_symlinked_out_paths()
    {
        File.CreateSymbolicLink(Path.Combine(_dir.Path, "link.txt"), Path.Combine(_outside.Path, "secret.txt"));
        var check = new CheckOutcome
        {
            CheckId = "X",
            Result = "pass",
            Confidence = 0.9,
            Evidence =
            [
                new Evidence { Path = "src/Missing.cs" },
                new Evidence { Path = "../" + Path.GetFileName(_outside.Path) + "/secret.txt" },
                new Evidence { Path = Path.Combine(_outside.Path, "secret.txt") },
                new Evidence { Path = "link.txt" },
                new Evidence { Path = "" },
                new Evidence { Path = "README.md" },
            ],
        };
        var result = new AuditResult { Checks = [check] };
        var v = EvidenceVerifier.Verify(result, _dir.Path);

        Assert.Equal(5, v.Dropped);
        Assert.Equal("README.md", Assert.Single(check.Evidence).Path);
        Assert.False(check.Uncertain);
        Assert.Equal(0.9, check.Confidence);
    }

    [Theory]
    [InlineData("fail")]
    [InlineData("partial")]
    public void Negative_outcome_without_evidence_becomes_uncertain(string outcome)
    {
        var check = new CheckOutcome
        {
            CheckId = "ARCH-002",
            Result = outcome,
            Confidence = 0.9,
            Explanation = "Domain references EF",
            Evidence = [new Evidence { Path = "src/Domain/Domain.csproj" }],
        };
        var result = new AuditResult { Checks = [check] };
        var v = EvidenceVerifier.Verify(result, _dir.Path);

        Assert.Equal(outcome, check.Result); // not dropped
        Assert.True(check.Uncertain);
        Assert.True(check.Confidence <= EvidenceVerifier.MaxConfidenceWithoutEvidence);
        Assert.Contains("ARCH-002", v.DowngradedChecks);
        Assert.Empty(check.Evidence);
    }

    [Fact]
    public void Pass_or_na_without_evidence_is_left_alone()
    {
        var check = new CheckOutcome { CheckId = "A", Result = "na", Confidence = 0.6 };
        EvidenceVerifier.Verify(new AuditResult { Checks = [check] }, _dir.Path);
        Assert.False(check.Uncertain);
        Assert.Equal(0.6, check.Confidence);
    }
}

public sealed class AuditOutputValidatorTests
{
    private static LlmAuditOutput Valid() => new()
    {
        ArchitectureStyle = "clean",
        Summary = "ok",
        Checks =
        [
            new LlmCheckOutcome { CheckId = "ARCH-002", Result = "fail", Explanation = "x", Confidence = 0.8, Evidence = [new LlmCheckEvidence { Path = "a.cs" }] },
            new LlmCheckOutcome { CheckId = "MSG-001", Result = "na", Explanation = "no messaging", Confidence = 0.5 },
        ],
    };

    [Fact]
    public void Valid_output_has_no_errors()
    {
        Assert.Empty(AuditOutputValidator.Validate(Valid(), ["ARCH-002", "MSG-001"]));
    }

    [Fact]
    public void Reports_missing_duplicate_unknown_and_invalid_values()
    {
        var o = Valid();
        o.ArchitectureStyle = "spaghetti";
        o.Checks.Add(new LlmCheckOutcome { CheckId = "MSG-001", Result = "maybe", Explanation = "", Confidence = 2 });
        o.Checks.Add(new LlmCheckOutcome { CheckId = "EXTRA-1", Result = "partial", Explanation = "x", Confidence = 0.5 });

        var errors = AuditOutputValidator.Validate(o, ["ARCH-002", "MSG-001", "DATA-001"]);
        Assert.Contains(errors, e => e.Contains("architectureStyle", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("Missing outcome for check DATA-001", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("MSG-001 has 2 outcomes", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("EXTRA-1", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("'maybe'", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("confidence", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("must cite at least one file", StringComparison.Ordinal));
    }

    [Fact]
    public void ToResult_normalises_invalid_output()
    {
        var o = Valid();
        o.ArchitectureStyle = "Spaghetti";
        o.Checks[1].Result = "maybe";
        var r = AuditOutputValidator.ToResult(o, ["ARCH-002", "MSG-001", "DATA-001"]);

        Assert.Equal("unknown", r.ArchitectureStyle);
        Assert.Equal(["ARCH-002", "MSG-001", "DATA-001"], r.Checks.Select(c => c.CheckId));
        Assert.False(r.Checks[0].Uncertain);
        Assert.Equal("na", r.Checks[1].Result);
        Assert.True(r.Checks[1].Uncertain);
        Assert.Equal("na", r.Checks[2].Result);
        Assert.True(r.Checks[2].Uncertain);
    }
}

public sealed class AuditPromptTests
{
    private static AuditRequest Request(string facts = "{}") => new()
    {
        AuditId = "a1",
        RepoName = "repo",
        Sha = "abc",
        FactsJson = facts,
        Checks =
        [
            new AuditCheck { Id = "ARCH-002", Title = "Dependency direction", Guidance = "fail if Domain references EF" },
            new AuditCheck { Id = "MSG-001", Title = "Retries", Guidance = "pass if explicit" },
        ],
    };

    [Fact]
    public void Hash_ignores_facts_but_tracks_rubric()
    {
        var a = AuditPrompt.ComputeHash(AuditPrompt.SystemTemplate, Request("""{"sha":"1"}"""));
        var b = AuditPrompt.ComputeHash(AuditPrompt.SystemTemplate, Request("""{"sha":"2","big":[1,2,3]}"""));
        Assert.Equal(a, b);
        Assert.StartsWith("sha256:", a, StringComparison.Ordinal);

        var changed = Request();
        changed.Checks[0].Guidance = "different rubric";
        Assert.NotEqual(a, AuditPrompt.ComputeHash(AuditPrompt.SystemTemplate, changed));

        var standards = Request();
        standards.TeamStandards = "Use MediatR";
        Assert.NotEqual(a, AuditPrompt.ComputeHash(AuditPrompt.SystemTemplate, standards));
    }

    [Fact]
    public void System_message_contains_checks_facts_and_optional_sections()
    {
        var r = Request("""{"dotnet":{"centralPackageManagement":true}}""");
        r.DiffSummary = "M src/Foo.cs";
        var msg = AuditPrompt.BuildSystemMessage(r);
        Assert.Contains("ARCH-002", msg, StringComparison.Ordinal);
        Assert.Contains("fail if Domain references EF", msg, StringComparison.Ordinal);
        Assert.Contains("centralPackageManagement", msg, StringComparison.Ordinal);
        Assert.Contains("M src/Foo.cs", msg, StringComparison.Ordinal);
        Assert.DoesNotContain("<team-standards>", msg, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void Facts_truncation_drops_bulky_parts_first()
    {
        var packages = string.Join(",", Enumerable.Range(0, 3000).Select(i => $$"""{"id":"Pkg{{i}}","version":"1.0.{{i}}","outdated":{{(i % 1000 == 0 ? "true" : "false")}}}"""));
        var facts = $$"""{"sha":"abc","dotnet":{"packages":[{{packages}}],"centralPackageManagement":true},"scanWarnings":["w"]}""";
        var t = AuditPrompt.TruncateFacts(facts, 10_000);

        Assert.True(t.Length <= 10_000, $"length {t.Length}");
        Assert.Contains("centralPackageManagement", t, StringComparison.Ordinal);
        Assert.Contains("Pkg1000", t, StringComparison.Ordinal);
        Assert.DoesNotContain("Pkg1001", t, StringComparison.Ordinal);
        Assert.DoesNotContain("scanWarnings", t, StringComparison.Ordinal);

        var hard = AuditPrompt.TruncateFacts(new string('x', 500), 100);
        Assert.Contains("truncated", hard, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("gpt-5", null, "gpt-5")]
    [InlineData("GPT-5", null, "gpt-5")]
    [InlineData("missing", "claude-sonnet-4.5", "claude-sonnet-4.5")]
    [InlineData(null, "missing", "claude-opus-4.5")]
    [InlineData(null, null, "claude-opus-4.5")]
    public void Model_selection_prefers_request_then_default_then_opus(string? requested, string? fallback, string expected)
    {
        string[] models = ["gpt-5", "claude-sonnet-4.5", "claude-opus-4.5", "gpt-5-mini"];
        Assert.Equal(expected, ModelSelector.Choose(models, requested, fallback));
    }

    [Fact]
    public void Model_selection_falls_back_to_claude_then_first()
    {
        Assert.Equal("claude-sonnet-4.5", ModelSelector.Choose(["gpt-5", "claude-sonnet-4.5"], null, null));
        Assert.Equal("gpt-5", ModelSelector.Choose(["gpt-5", "o4"], null, null));
        Assert.Null(ModelSelector.Choose([], "gpt-5", null));
    }
}

public sealed class ErrorTextTests
{
    [Fact]
    public void Summarises_rpc_http_failures()
    {
        var ex = new InvalidOperationException(
            "Request models.list failed with message: GenericFailure, {\"kind\":\"http\",\"status\":403,\"statusText\":\"Forbidden\",\"body\":\"unauthorized: not authorized to use this Copilot feature\\n\",\"headers\":[{\"name\":\"date\",\"value\":\"x\"}]}");
        Assert.Equal(
            "Request models.list failed with message: GenericFailure: HTTP 403 unauthorized: not authorized to use this Copilot feature",
            Dashboard.Auditor.Engines.ErrorText.Summarise(ex));
    }

    [Fact]
    public void Caps_long_messages()
    {
        var s = Dashboard.Auditor.Engines.ErrorText.Summarise(new InvalidOperationException(new string('x', 2000)));
        Assert.True(s.Length <= 501);
    }
}

public sealed class AuditSchemaTests
{
    [Fact]
    public void Output_schema_has_no_refs()
    {
        var schema = System.Text.Json.Schema.JsonSchemaExporter.GetJsonSchemaAsNode(
            System.Text.Json.JsonSerializerOptions.Web, typeof(LlmAuditOutput)).ToJsonString();
        Assert.DoesNotContain("$ref", schema);
    }
}

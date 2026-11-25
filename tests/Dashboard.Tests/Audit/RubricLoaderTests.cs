using Dashboard.Core.Audit;
using Dashboard.Tests.Scanners;

namespace Dashboard.Tests.Audit;

public class RubricLoaderTests
{
    public static string RubricRoot => Path.Combine(Fixtures.RepoRoot, "audit", "rubric");

    [Fact]
    public void Loads_v1_with_exactly_ten_checks()
    {
        var r = RubricLoader.Load(RubricRoot);
        Assert.Equal("v1", r.Version);
        Assert.Equal(
            ["ARCH-001", "ARCH-002", "NET-001", "NET-002", "K8S-001", "K8S-002", "DOCK-001", "DATA-001", "MSG-001", "TEST-001"],
            r.Checks.Select(c => c.Id));
        Assert.Equal(["NET-001", "NET-002", "K8S-001", "K8S-002", "DOCK-001", "TEST-001"], r.FactChecks.Select(c => c.Id));
        Assert.Equal(["ARCH-001", "ARCH-002", "DATA-001", "MSG-001"], r.JudgementChecks.Select(c => c.Id));
        Assert.All(r.Checks, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Title));
            Assert.False(string.IsNullOrWhiteSpace(c.Area));
            Assert.False(string.IsNullOrWhiteSpace(c.Description));
            Assert.Contains("pass:", c.Guidance, StringComparison.Ordinal);
            Assert.Contains("partial:", c.Guidance, StringComparison.Ordinal);
            Assert.Contains("fail:", c.Guidance, StringComparison.Ordinal);
            Assert.Contains(c.Severity, new[] { "low", "medium", "high" });
        });
        Assert.Equal(16, r.Hash.Length);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Appends_team_checks_and_reports_bad_entries()
    {
        var root = Path.Combine(Path.GetTempPath(), "rubric-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "v1"));
            Directory.CreateDirectory(Path.Combine(root, "team"));
            File.Copy(Path.Combine(RubricRoot, "v1", "checks.yaml"), Path.Combine(root, "v1", "checks.yaml"));
            var baseline = RubricLoader.Load(root);

            File.WriteAllText(Path.Combine(root, "team", "b-logging.yaml"), """
                version: "team"
                checks:
                  - id: TEAM-LOG-001
                    area: observability
                    title: Structured logging
                    severity: LOW
                    kind: judgement
                    description: Use structured logging.
                    guidance: "pass: ... partial: ... fail: ..."
                  - id: NET-001
                    title: Duplicate
                    severity: high
                    kind: fact
                  - id: TEAM-BAD
                    title: Bad kind
                    severity: medium
                    kind: opinion
                """);
            File.WriteAllText(Path.Combine(root, "team", "a-broken.yml"), "checks: [ {id: ");
            File.WriteAllText(Path.Combine(root, "team", "notes.txt"), "ignored");

            var r = RubricLoader.Load(root);
            Assert.Equal("v1", r.Version);
            Assert.Equal(11, r.Checks.Count);
            var team = r.Checks[^1];
            Assert.Equal("TEAM-LOG-001", team.Id);
            Assert.Equal("low", team.Severity);   // normalised
            Assert.Equal(3, r.Warnings.Count);
            Assert.Contains(r.Warnings, w => w.Contains("a-broken.yml", StringComparison.Ordinal));
            Assert.Contains(r.Warnings, w => w.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(r.Warnings, w => w.Contains("TEAM-BAD", StringComparison.Ordinal));
            Assert.NotEqual(baseline.Hash, r.Hash);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Invalid_base_rubric_throws()
    {
        Assert.Throws<InvalidDataException>(() => RubricLoader.Parse("checks: []"));
        Assert.Throws<InvalidDataException>(() => RubricLoader.Parse("version: v1\nchecks:\n  - id: X\n    title: t\n    severity: urgent\n    kind: fact\n"));
        Assert.Throws<InvalidDataException>(() => RubricLoader.Parse("version: v1\nchecks:\n  - id: X\n    title: t\n    severity: low\n    kind: fact\n  - id: x\n    title: t\n    severity: low\n    kind: fact\n"));
        Assert.Throws<FileNotFoundException>(() => RubricLoader.Load(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid())));
    }
}

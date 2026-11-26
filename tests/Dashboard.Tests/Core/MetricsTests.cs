using Dashboard.Core.Configuration;
using Dashboard.Core.Entities;
using Dashboard.Core.Metrics;
using Dashboard.Core.Rules;
using Dashboard.Core.Templates;
using Dashboard.Core.Time;

namespace Dashboard.Tests.Core;

internal static class B
{
    public static readonly DateTimeOffset T0 = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero); // Monday
    public static DashboardConfig Config() => Defaults.Create();

    public static Run Run(int id, DateTimeOffset queued, string result = "succeeded", string reason = "individualCI", string sha = "abc", int pipeline = 1) => new()
    {
        Id = id, PipelineId = pipeline, RepoId = "r1", Reason = reason, Status = "completed", Result = result,
        QueueTime = queued, StartTime = queued, FinishTime = queued.AddMinutes(10), SourceVersion = sha, BuildNumber = $"1.0.{id}",
        SourceBranch = "refs/heads/main",
    };

    public static TimelineRecord Stage(int run, string id, string name, string? result, int attempt = 1, bool prev = false, DateTimeOffset? start = null, double minutes = 5) => new()
    {
        RunId = run, RecordId = id, Type = "Stage", Name = name, Identifier = name, Result = result, Attempt = attempt, IsPreviousAttempt = prev,
        State = "completed", StartTime = start ?? T0, FinishTime = (start ?? T0).AddMinutes(minutes),
    };

    public static TimelineRecord Job(int run, string id, string parent, string name, string? result, int attempt = 1, bool prev = false, DateTimeOffset? start = null, double minutes = 4) => new()
    {
        RunId = run, RecordId = id, ParentId = parent, Type = "Job", Name = name, Identifier = $"Build.{name}", Result = result, Attempt = attempt,
        IsPreviousAttempt = prev, State = "completed", StartTime = start ?? T0, FinishTime = (start ?? T0).AddMinutes(minutes),
    };
}

public class WorkingHoursTests
{
    private static WorkingHours London() => new(new TimeConfig { TimeZone = "Europe/London", WorkdayStart = "09:00", WorkdayEnd = "17:00" });

    [Fact]
    public void Same_day_inside_hours()
    {
        var wh = London();
        var from = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
        Assert.Equal(3, wh.Between(from, from.AddHours(3)), 3);
    }

    [Fact]
    public void Weekend_is_skipped()
    {
        var wh = London();
        var fri = new DateTimeOffset(2026, 11, 6, 16, 0, 0, TimeSpan.Zero);
        var mon = new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
        Assert.Equal(2, wh.Between(fri, mon), 3);
    }

    [Fact]
    public void Dst_change_keeps_full_working_day()
    {
        var wh = London();
        // Clocks go back on Sunday 2026-10-25; Monday is GMT (UTC+0), the Friday before is BST (UTC+1).
        var friStart = new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero); // 09:00 BST
        var monEnd = new DateTimeOffset(2026, 10, 26, 17, 0, 0, TimeSpan.Zero);  // 17:00 GMT
        Assert.Equal(16, wh.Between(friStart, monEnd), 3);
    }
}

public class BuildMetricsTests
{
    private readonly BuildMetricsCalculator _calc = new(B.Config());

    [Fact]
    public void SucceededWithIssues_counts_as_failure_and_skipped_is_excluded()
    {
        var run = B.Run(1, B.T0, "partiallySucceeded");
        var records = new[] { B.Stage(1, "s1", "Build", "succeededWithIssues"), B.Stage(1, "s2", "Test", "skipped") };
        Assert.Equal(Outcome.Fail, _calc.EvaluateRun(run, records).Final);

        var run2 = B.Run(2, B.T0);
        var records2 = new[] { B.Stage(2, "s1", "Build", "succeeded"), B.Stage(2, "s2", "Lint", "skipped") };
        Assert.Equal(Outcome.Pass, _calc.EvaluateRun(run2, records2).Final);
    }

    [Fact]
    public void Deploy_stage_failure_does_not_affect_build_outcome()
    {
        var run = B.Run(1, B.T0, "failed");
        var records = new[] { B.Stage(1, "s1", "Build", "succeeded"), B.Stage(1, "s2", "prodgreen deploy", "failed") };
        Assert.Equal(Outcome.Pass, _calc.EvaluateRun(run, records).Final);
    }

    [Fact]
    public void Retried_job_that_failed_then_passed_is_flaky_and_creates_red_time()
    {
        var t = B.T0;
        var run = B.Run(1, t);
        var records = new[]
        {
            B.Stage(1, "s1-a1", "Build", "failed", attempt: 1, prev: true, start: t, minutes: 5),
            B.Job(1, "j1-a1", "s1-a1", "Compile", "failed", attempt: 1, prev: true, start: t, minutes: 5),
            B.Stage(1, "s1", "Build", "succeeded", attempt: 2, start: t.AddMinutes(20), minutes: 5),
            B.Job(1, "j1", "s1", "Compile", "succeeded", attempt: 2, start: t.AddMinutes(20), minutes: 5),
        };
        var outcome = _calc.EvaluateRun(run, records);
        Assert.Equal(Outcome.Pass, outcome.Final);
        Assert.Single(outcome.FlakyJobs);
        Assert.Equal(5, outcome.WastedMinutes, 3);
        Assert.Equal(2, outcome.Attempts.Count);

        var stats = _calc.ForPipeline(1, [(run, records)], t.AddDays(-30), t.AddDays(1));
        var red = Assert.Single(stats.RedIntervals);
        Assert.Equal(t.AddMinutes(5), red.Start);
        Assert.Equal(t.AddMinutes(25), red.End);
        Assert.Equal(1, stats.RunsWithFlakyJob);
    }

    [Fact]
    public void Whole_stage_rerun_of_passing_jobs_is_not_flaky()
    {
        var t = B.T0;
        var run = B.Run(1, t);
        var records = new[]
        {
            B.Stage(1, "s1-a1", "Build", "succeeded", attempt: 1, prev: true, start: t),
            B.Job(1, "j1-a1", "s1-a1", "Compile", "succeeded", attempt: 1, prev: true, start: t),
            B.Stage(1, "s1", "Build", "succeeded", attempt: 2, start: t.AddMinutes(20)),
            B.Job(1, "j1", "s1", "Compile", "succeeded", attempt: 2, start: t.AddMinutes(20)),
        };
        Assert.Empty(_calc.EvaluateRun(run, records).FlakyJobs);
    }

    [Fact]
    public void Red_interval_spans_runs_and_success_rate_excludes_pr_and_cancelled()
    {
        var t = B.T0;
        var runs = new List<(Run, IReadOnlyList<TimelineRecord>)>
        {
            (B.Run(1, t), [B.Stage(1, "a", "Build", "succeeded", start: t)]),
            (B.Run(2, t.AddHours(1), "failed"), [B.Stage(2, "b", "Build", "failed", start: t.AddHours(1))]),
            (B.Run(3, t.AddHours(2), "canceled"), [B.Stage(3, "c", "Build", "canceled", start: t.AddHours(2))]),
            (B.Run(4, t.AddHours(3), "failed", reason: "pullRequest"), [B.Stage(4, "d", "Build", "failed", start: t.AddHours(3))]),
            (B.Run(5, t.AddHours(4)), [B.Stage(5, "e", "Build", "succeeded", start: t.AddHours(4))]),
        };
        var stats = _calc.ForPipeline(1, runs, t.AddDays(-1), t.AddDays(1));
        Assert.Equal(3, stats.CompletedRuns);
        Assert.Equal(2.0 / 3, stats.SuccessRate!.Value, 3);
        var red = Assert.Single(stats.RedIntervals);
        Assert.Equal(t.AddHours(1).AddMinutes(5), red.Start);
        Assert.Equal(t.AddHours(4).AddMinutes(5), red.End);
    }

    [Fact]
    public void Open_red_interval_when_latest_failed()
    {
        var t = B.T0;
        var runs = new List<(Run, IReadOnlyList<TimelineRecord>)>
        {
            (B.Run(1, t, "failed"), [B.Stage(1, "a", "Build", "failed", start: t)]),
        };
        var stats = _calc.ForPipeline(1, runs, t.AddDays(-1), t.AddDays(1));
        Assert.NotNull(stats.OpenRed);
    }
}

public class StatsTests
{
    [Fact]
    public void Typical_uses_median_below_threshold()
    {
        var (v, label) = Stats.Typical([1, 2, 3, 100], 20);
        Assert.Equal("median", label);
        Assert.Equal(2.5, v);
    }

    [Fact]
    public void Typical_uses_p85_at_threshold()
    {
        var values = Enumerable.Range(1, 20).Select(i => (double)i).ToList();
        var (v, label) = Stats.Typical(values, 20);
        Assert.Equal("p85", label);
        Assert.Equal(17.15, v!.Value, 2);
    }
}

public class PrMetricsTests
{
    private static PrMetricsCalculator Calc() => new(B.Config(), new WorkingHours(new TimeConfig { TimeZone = "UTC" }));

    [Fact]
    public void First_review_ignores_author_bots_and_starts_at_publish()
    {
        var pr = new PullRequest { Id = 1, RepoId = "r1", Status = "completed", CreatedOn = B.T0, PublishedOn = B.T0.AddHours(2), ClosedOn = B.T0.AddHours(6), AuthorName = "Ann" };
        var events = new List<PrEvent>
        {
            new() { Kind = PrEventKind.Comment, IsAuthor = true, Time = B.T0.AddHours(2.5) },
            new() { Kind = PrEventKind.Vote, IsBot = true, Time = B.T0.AddHours(3) },
            new() { Kind = PrEventKind.Vote, Time = B.T0.AddHours(4), Vote = 10 },
        };
        var f = Calc().Evaluate(pr, events, B.T0.AddDays(1));
        Assert.Equal(2, f.FirstReviewWorkingHours!.Value, 3);
        Assert.Equal(4, f.MergeWorkingHours!.Value, 3);
    }

    [Fact]
    public void Stuck_when_no_review_and_waiting_on_required_reviewers()
    {
        var pr = new PullRequest
        {
            Id = 1, RepoId = "r1", Status = "active", CreatedOn = B.T0, AuthorName = "Ann",
            ReviewersJson = """[{"displayName":"Bob","vote":0,"isRequired":true},{"displayName":"Cy","vote":-5}]""",
        };
        var f = Calc().Evaluate(pr, [], B.T0.AddDays(3));
        Assert.True(f.Stuck);
        Assert.Contains("Bob", f.WaitingOn);
        Assert.Contains("Ann (author)", f.WaitingOn);
    }

    [Fact]
    public void Drafts_are_never_stuck()
    {
        var pr = new PullRequest { Id = 1, RepoId = "r1", Status = "active", IsDraft = true, CreatedOn = B.T0 };
        Assert.False(Calc().Evaluate(pr, [], B.T0.AddDays(10)).Stuck);
    }
}

public class RuleEngineTests
{
    private static RuleEngine Engine() => new(B.Config());

    private static RepoHealthInput Healthy() => new()
    {
        Type = RepoType.Service, PipelineCount = 1, DefaultBranchCiRuns = 30, CompletedBuildRuns = 30, MainSuccessRate = 0.97,
        LongestRedWorkingHours = 2, FlakyRunRate = 0.01, FirstReviewHours = 3, ReviewedPrs = 10, OldestActivePrWorkingDays = 1, ActivePrs = 2,
        HasMinReviewerPolicy = true, HasBuildValidationPolicy = true, DaysSinceProductionDeploy = 3, FactsAvailable = true, HasReadme = true,
        EveryDeployableHasTests = true, AuditAvailable = true,
        Templates = [new PipelineTemplateStatus { PipelineId = 1, RefStatus = TemplateRefStatus.Compliant, UsesTemplates = true }],
    };

    [Fact]
    public void Healthy_repo_is_gold()
    {
        var r = Engine().Evaluate(Healthy());
        Assert.Equal("Gold", r.Tier);
        Assert.Null(r.NextTier);
    }

    [Fact]
    public void Insufficient_data_never_passes_and_marks_provisional()
    {
        var input = Healthy();
        var r = Engine().Evaluate(new RepoHealthInput
        {
            Type = input.Type, PipelineCount = 1, DefaultBranchCiRuns = 3, CompletedBuildRuns = 3, MainSuccessRate = 1, FlakyRunRate = 0,
            HasMinReviewerPolicy = true, HasBuildValidationPolicy = true, FactsAvailable = true, HasReadme = true, AuditAvailable = true,
            EveryDeployableHasTests = true, Templates = input.Templates, ReviewedPrs = 10, FirstReviewHours = 2, DaysSinceProductionDeploy = 1,
        });
        Assert.Equal("Bronze", r.Tier);
        Assert.True(r.Provisional);
        Assert.Equal("Silver", r.NextTier);
        Assert.All(r.NextTierGaps, g => Assert.Equal(RuleStatus.Insufficient, g.Status));
    }

    [Fact]
    public void Failing_bronze_rule_gives_none_with_gap()
    {
        var h = Healthy();
        var r = Engine().Evaluate(new RepoHealthInput
        {
            Type = h.Type, PipelineCount = 1, DefaultBranchCiRuns = 30, CompletedBuildRuns = 30, MainSuccessRate = 0.97, FlakyRunRate = 0,
            HasMinReviewerPolicy = false, HasBuildValidationPolicy = true, FactsAvailable = true, HasReadme = true, Templates = h.Templates,
        });
        Assert.Equal("None", r.Tier);
        Assert.Contains(r.NextTierGaps, g => g.RuleId == "POL-001");
        Assert.False(r.Provisional);
    }

    [Fact]
    public void Library_skips_service_only_rules()
    {
        var h = Healthy();
        var r = Engine().Evaluate(new RepoHealthInput
        {
            Type = RepoType.Library, PipelineCount = 1, DefaultBranchCiRuns = 30, CompletedBuildRuns = 30, MainSuccessRate = 0.99, LongestRedWorkingHours = 0,
            FlakyRunRate = 0, FirstReviewHours = 2, ReviewedPrs = 10, HasMinReviewerPolicy = true, HasBuildValidationPolicy = true,
            DaysSinceProductionDeploy = null, FactsAvailable = true, HasReadme = true, EveryDeployableHasTests = null, AuditAvailable = true, Templates = h.Templates,
        });
        Assert.Equal("Gold", r.Tier);
        Assert.Contains(r.Outcomes, o => o.RuleId == "DEP-001" && o.Status == RuleStatus.NotApplicable);
    }
}

public class DeploymentDeriverTests
{
    [Fact]
    public void Skaffold_job_deploys_aks_components_dacpac_deploys_db_and_infra_jobs_are_ignored()
    {
        var cfg = B.Config();
        var run = B.Run(9, B.T0);
        var comps = new List<Component>
        {
            new() { Id = 1, Name = "interest-api", Kind = ComponentKind.AksService },
            new() { Id = 2, Name = "interest-listener", Kind = ComponentKind.AksService },
            new() { Id = 3, Name = "Interest.Database", Kind = ComponentKind.Database },
        };
        var records = new List<TimelineRecord>
        {
            B.Stage(9, "gate", "Approval Gate 2", "succeeded", start: B.T0.AddMinutes(-60), minutes: 45),
            B.Stage(9, "st", "prodgreen deploy", "succeeded"),
            new() { RunId = 9, RecordId = "ph", ParentId = "st", Type = "Phase", Name = "deploy" },
            B.Job(9, "j0", "ph", "pre_apply", "succeeded"),
            B.Job(9, "j1", "ph", "terraform_deploy", "succeeded"),
            B.Job(9, "j2", "ph", "terraform_servicebus_namespace_deploy", "succeeded"),
            B.Job(9, "j3", "ph", "mssql_managed_identity_deploy", "succeeded"),
            B.Job(9, "j4", "ph", "dacpac_apply", "failed"),
            B.Job(9, "j5", "ph", "skaffold_deploy", "succeeded"),
        };
        var deps = new DeploymentDeriver(cfg).Derive(run, "interest", "interest-release", records, comps);
        Assert.Equal(3, deps.Count);
        Assert.All(deps, d => Assert.Equal("prod-green", d.EnvironmentName));
        Assert.All(deps, d => Assert.Equal(45 * 60, d.WaitSeconds, 1)); // gate time counts as approval wait
        Assert.Equal("failed", deps.Single(d => d.ComponentId == 3).Result);
        Assert.All(deps.Where(d => d.ComponentId != 3), d => Assert.Equal("succeeded", d.Result));
    }

    [Fact]
    public void Terraform_only_deploy_is_repo_level()
    {
        var records = new List<TimelineRecord>
        {
            B.Stage(1, "st", "simeu deploy", "succeeded"),
            new() { RunId = 1, RecordId = "ph", ParentId = "st", Type = "Phase", Name = "deploy" },
            B.Job(1, "j1", "ph", "terraform_deploy", "succeeded"),
        };
        var dep = Assert.Single(new DeploymentDeriver(B.Config()).Derive(B.Run(1, B.T0), "infra", "infra-release", records,
            [new Component { Id = 1, Name = "x-api", Kind = ComponentKind.AksService }]));
        Assert.Null(dep.ComponentId);
        Assert.Equal("sim-eu", dep.EnvironmentName);
    }

    [Theory]
    [InlineData("testlemur deploy", "team")]
    [InlineData("testfox2 deploy", "team")]
    [InlineData("staginggreen deploy", "staging-green")]
    [InlineData("stagingeu deploy", "staging-eu")]
    [InlineData("simgreen deploy", "sim-green")]
    [InlineData("prodgreendeploy", "prod-green")]
    [InlineData("prodeu deploy", "prod-eu")]
    [InlineData("tests_cibuild", null)]
    [InlineData("Terraform CI Build and Test", null)]
    [InlineData("Approval Gate 2", null)]
    [InlineData("software release 3.4", null)]
    public void Stage_names_map_to_environments(string stage, string? env) =>
        Assert.Equal(env, StageClassifier.MatchEnvironment(stage, stage, B.Config().Environments)?.Name);

    [Fact]
    public void Release_pipelines_are_detected_by_name_or_shape()
    {
        var cfg = B.Config();
        IReadOnlyList<string> deploy = ["software release 3.4", "Approval Gate 2", "testlemur deploy", "prodeu deploy"];
        IReadOnlyList<string> ci = ["tests_cibuild", "skaffold_cibuild", "artifact", "Trigger Releases"];
        Assert.True(PipelineClassifier.IsRelease("interest-release", [], cfg));
        Assert.True(PipelineClassifier.IsRelease("interest-cd", [(false, deploy), (false, deploy), (false, deploy)], cfg));
        Assert.False(PipelineClassifier.IsRelease("interest-ci", [(false, ci), (true, ci), (false, ci)], cfg));
        Assert.False(StageClassifier.IsBuildStage("Approval Gate 2", null, cfg, hasCheckpoints: false));
        Assert.True(StageClassifier.IsBuildStage("Trigger Releases", null, cfg, hasCheckpoints: false));
    }

    [Fact]
    public void Build_stage_is_not_a_deployment()
    {
        var deps = new DeploymentDeriver(B.Config()).Derive(B.Run(1, B.T0), "x", "x", [B.Stage(1, "s", "Build", "succeeded")], []);
        Assert.Empty(deps);
    }
}

public class TemplateComplianceTests
{
    [Fact]
    public void Main_is_compliant_next_experimental_other_noncompliant_and_staleness_computed()
    {
        var eval = new TemplateComplianceEvaluator(B.Config());
        var p = new Pipeline { Id = 1, Name = "ci" };
        var history = new List<TemplateCommit>
        {
            new() { Sha = "c3", Branch = "main", Date = B.T0 },
            new() { Sha = "c2", Branch = "main", Date = B.T0.AddDays(-10) },
            new() { Sha = "c1", Branch = "main", Date = B.T0.AddDays(-20) },
        };
        var run = B.Run(1, B.T0);
        run.TemplateRef = "refs/heads/main"; run.TemplateSha = "c1";
        var s = eval.Evaluate(p, run, null, history, false, B.T0);
        Assert.Equal(TemplateRefStatus.Compliant, s.RefStatus);
        Assert.Equal(2, s.CommitsBehind);
        Assert.True(s.Stale);

        run.TemplateRef = "refs/heads/next";
        Assert.True(eval.Evaluate(p, run, null, history, true, B.T0).ExperimentalInProduction);

        run.TemplateRef = "refs/heads/feature/x";
        Assert.Equal(TemplateRefStatus.NonCompliant, eval.Evaluate(p, run, null, history, false, B.T0).RefStatus);
    }
}

public class FailureFingerprintTests
{
    [Fact]
    public void Same_error_with_different_ids_and_paths_matches()
    {
        var a = FailureFingerprint.Compute("Error NU1301: Unable to load the service index for source https://pkgs.dev.azure.com/x/_packaging/feed/nuget/v3/index.json at /home/vsts/work/1/s/src/App.csproj (attempt 3)");
        var b = FailureFingerprint.Compute("Error NU1301: Unable to load the service index for source https://pkgs.dev.azure.com/y/_packaging/other/nuget/v3/index.json at /home/vsts/work/7/s/src/Other.csproj (attempt 1)");
        Assert.Equal(a, b);
    }
}

public class ConfigLoaderTests
{
    [Fact]
    public void Default_config_file_is_valid()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "../../../../../config/dashboard.yaml");
        var result = ConfigLoader.LoadFile(Path.GetFullPath(path));
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Contains(result.Config.Environments, e => e.Name == "sim-green" && e.Tier == EnvironmentTier.Production && e.Region == "green");
    }

    [Fact]
    public void Invalid_values_are_reported()
    {
        var r = ConfigLoader.Parse("azureDevOps:\n  mode: weird\ntime:\n  timeZone: Mars/Base\nsync:\n  hotSeconds: 1\n");
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("mode"));
        Assert.Contains(r.Errors, e => e.Contains("timeZone"));
        Assert.Contains(r.Errors, e => e.Contains("hotSeconds"));
    }
}

public class GateEnvironmentConfigTests
{
    [Fact]
    public void Gate_environments_parse_and_validate()
    {
        var ok = ConfigLoader.Parse("pipelines:\n  gateEnvironments:\n    - { name: approval-gate, adoId: 42 }\n");
        Assert.True(ok.IsValid, string.Join("; ", ok.Errors));
        Assert.Equal(42, Assert.Single(ok.Config.Pipelines.GateEnvironments).AdoId);

        var bad = ConfigLoader.Parse("pipelines:\n  gateEnvironments:\n    - { name: approval-gate }\n");
        Assert.Contains(bad.Errors, e => e.Contains("adoId"));
    }
}

using System.Text.Json;
using Dashboard.Core.Configuration;
using Dashboard.Core.Entities;
using Dashboard.Core.Time;

namespace Dashboard.Core.Metrics;

public sealed class ReviewerInfo
{
    public string? Id { get; set; }
    public string? DisplayName { get; set; }
    public int Vote { get; set; }
    public bool IsRequired { get; set; }
    public bool IsContainer { get; set; }
}

public sealed record PrFacts(
    PullRequest Pr,
    double? FirstReviewWorkingHours,
    double? MergeWorkingHours,
    double AgeWorkingDays,
    bool Stuck,
    IReadOnlyList<string> StuckReasons,
    IReadOnlyList<string> WaitingOn,
    DateTimeOffset? FirstReviewAt);

public sealed class PrRepoStats
{
    public int CompletedInWindow { get; init; }
    public int ReviewedInWindow { get; init; }
    public double? FirstReviewHours { get; init; }
    public string FirstReviewLabel { get; init; } = "median";
    public double? MergeHours { get; init; }
    public string MergeLabel { get; init; } = "median";
    public int ActiveCount { get; init; }
    public int StuckCount { get; init; }
    public double? OldestActiveWorkingDays { get; init; }
    public IReadOnlyList<PrFacts> Active { get; init; } = [];
}

public sealed class PrMetricsCalculator(DashboardConfig config, WorkingHours wh)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<ReviewerInfo> Reviewers(PullRequest pr)
    {
        try { return JsonSerializer.Deserialize<List<ReviewerInfo>>(pr.ReviewersJson, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    public bool IsBot(string? name, string? id) =>
        (name is not null && (config.BotIdentities.Any(b => name.Contains(b, StringComparison.OrdinalIgnoreCase))
                              || name.Contains("Build Service", StringComparison.OrdinalIgnoreCase)
                              || name.Contains("[bot]", StringComparison.OrdinalIgnoreCase)))
        || (id is not null && config.BotIdentities.Contains(id, StringComparer.OrdinalIgnoreCase));

    public PrFacts Evaluate(PullRequest pr, IReadOnlyList<PrEvent> events, DateTimeOffset now)
    {
        var start = pr.PublishedOn ?? pr.CreatedOn;
        var firstReview = events
            .Where(e => !e.IsAuthor && !e.IsBot && e.Kind is PrEventKind.Vote or PrEventKind.Comment && e.Time >= start)
            .OrderBy(e => e.Time).FirstOrDefault();
        double? ttfr = firstReview is null ? null : wh.Between(start, firstReview.Time);
        double? ttm = pr.Status == "completed" && pr.ClosedOn is not null ? wh.Between(start, pr.ClosedOn.Value) : null;
        var age = pr.Status == "active" ? wh.WorkingDaysBetween(start, now) : 0;

        var reasons = new List<string>();
        var waiting = new List<string>();
        if (pr.Status == "active" && !pr.IsDraft)
        {
            var lastHuman = events.Where(e => !e.IsAuthor && !e.IsBot && e.Kind is PrEventKind.Vote or PrEventKind.Comment)
                .Select(e => (DateTimeOffset?)e.Time).DefaultIfEmpty(null).Max();
            var idleSince = lastHuman ?? start;
            var idle = wh.Between(idleSince, now);
            if (idle > config.Metrics.StuckPrIdleWorkingHours)
                reasons.Add(lastHuman is null ? $"no review for {idle:0}h" : $"no review activity for {idle:0}h");
            if (string.Equals(pr.PolicyStatus, "rejected", StringComparison.OrdinalIgnoreCase)) reasons.Add("policy failing");
            if (string.Equals(pr.MergeStatus, "conflicts", StringComparison.OrdinalIgnoreCase)) reasons.Add("merge conflict");

            var reviewers = Reviewers(pr);
            waiting.AddRange(reviewers.Where(r => r.IsRequired && r.Vote == 0).Select(r => r.DisplayName ?? r.Id ?? "?"));
            if (reviewers.Any(r => r.Vote == -5)) waiting.Add($"{pr.AuthorName} (author)");
        }
        return new PrFacts(pr, ttfr, ttm, age, reasons.Count > 0, reasons, waiting, firstReview?.Time);
    }

    public PrRepoStats ForRepo(IReadOnlyList<(PullRequest Pr, IReadOnlyList<PrEvent> Events)> prs, DateTimeOffset windowStart, DateTimeOffset now)
    {
        var human = prs.Where(p => !p.Pr.AuthorIsBot).ToList();
        var facts = human.Select(p => Evaluate(p.Pr, p.Events, now)).ToList();
        var reviewed = facts.Where(f => f.FirstReviewAt is not null && f.FirstReviewAt >= windowStart && !f.Pr.IsDraft)
            .Select(f => f.FirstReviewWorkingHours!.Value).ToList();
        var merged = facts.Where(f => f.MergeWorkingHours is not null && f.Pr.ClosedOn >= windowStart).Select(f => f.MergeWorkingHours!.Value).ToList();
        var (ttfr, ttfrLabel) = Stats.Typical(reviewed, config.Metrics.PercentileMinSample);
        var (ttm, ttmLabel) = Stats.Typical(merged, config.Metrics.PercentileMinSample);
        var active = facts.Where(f => f.Pr.Status == "active").OrderByDescending(f => f.AgeWorkingDays).ToList();
        return new PrRepoStats
        {
            CompletedInWindow = merged.Count,
            ReviewedInWindow = reviewed.Count,
            FirstReviewHours = ttfr,
            FirstReviewLabel = ttfrLabel,
            MergeHours = ttm,
            MergeLabel = ttmLabel,
            ActiveCount = active.Count(f => !f.Pr.IsDraft),
            StuckCount = active.Count(f => f.Stuck),
            OldestActiveWorkingDays = active.Where(f => !f.Pr.IsDraft).Select(f => (double?)f.AgeWorkingDays).DefaultIfEmpty(null).Max(),
            Active = active,
        };
    }
}

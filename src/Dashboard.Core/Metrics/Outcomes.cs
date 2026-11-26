using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dashboard.Core.Configuration;

namespace Dashboard.Core.Metrics;

public enum Outcome { Pass, Fail, Excluded, Pending }

public static class ResultClassifier
{
    /// <summary>Classify an ADO build or timeline result string.</summary>
    public static Outcome Classify(string? result, ResultConfig config)
    {
        if (string.IsNullOrEmpty(result)) return Outcome.Pending;
        return result.ToLowerInvariant() switch
        {
            "succeeded" => Outcome.Pass,
            "succeededwithissues" or "partiallysucceeded" => config.SucceededWithIssuesIsFailure ? Outcome.Fail : Outcome.Pass,
            "failed" => Outcome.Fail,
            "canceled" or "cancelled" or "abandoned" or "skipped" or "none" => Outcome.Excluded,
            _ => Outcome.Excluded,
        };
    }
}

public static class Stats
{
    public static double? Median(IReadOnlyCollection<double> values) => Percentile(values, 0.5);

    /// <summary>Linear-interpolated percentile; null for empty input.</summary>
    public static double? Percentile(IReadOnlyCollection<double> values, double p)
    {
        if (values.Count == 0) return null;
        var sorted = values.OrderBy(v => v).ToArray();
        var rank = p * (sorted.Length - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    /// <summary>Median when n &lt; minSample, else p85. Returns (value, label).</summary>
    public static (double? Value, string Label) Typical(IReadOnlyCollection<double> values, int minSample)
        => values.Count >= minSample ? (Percentile(values, 0.85), "p85") : (Median(values), "median");
}

public static class StageClassifier
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> Cache = new();

    public static Regex Rx(string pattern) =>
        Cache.GetOrAdd(pattern, p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)));

    public static bool AnyMatch(IEnumerable<string> patterns, params string?[] values) =>
        patterns.Any(p => values.Any(v => v is not null && Rx(p).IsMatch(v.Trim())));

    public static EnvironmentConfig? MatchEnvironment(string? stageName, string? identifier, IEnumerable<EnvironmentConfig> envs)
    {
        foreach (var env in envs.OrderByDescending(e => e.Order))
            if (AnyMatch(env.StagePatterns, stageName, identifier)) return env;
        return null;
    }

    public static bool IsGate(string? stageName, string? identifier, PipelinesConfig pipelines) =>
        AnyMatch(pipelines.GateStagePatterns, stageName, identifier);

    public static bool IsBuildStage(string? stageName, string? identifier, IEnumerable<EnvironmentConfig> envs, bool hasCheckpoints) =>
        !hasCheckpoints && MatchEnvironment(stageName, identifier, envs) is null;

    public static bool IsBuildStage(string? stageName, string? identifier, DashboardConfig config, bool hasCheckpoints) =>
        IsBuildStage(stageName, identifier, config.Environments, hasCheckpoints) && !IsGate(stageName, identifier, config.Pipelines);
}

/// <summary>
/// CI and release are usually separate YAML pipelines. Release pipelines are excluded from build stability
/// (their gate/setup stages aren't builds). A pipeline is a release pipeline if its name matches
/// pipelines.releaseNamePatterns, or if it never runs for PRs and every run with a timeline has a deploy stage.
/// </summary>
public static class PipelineClassifier
{
    public static bool IsRelease(string pipelineName, IReadOnlyCollection<(bool IsPullRequest, IReadOnlyList<string> StageNames)> runs, DashboardConfig config)
    {
        if (StageClassifier.AnyMatch(config.Pipelines.ReleaseNamePatterns, pipelineName)) return true;
        if (runs.Any(r => r.IsPullRequest)) return false;
        var withStages = runs.Where(r => r.StageNames.Count > 0).ToList();
        return withStages.Count >= 3
               && withStages.All(r => r.StageNames.Any(n => StageClassifier.MatchEnvironment(n, n, config.Environments) is not null));
    }
}

public static class FailureFingerprint
{
    private static readonly Regex Guid = new(@"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Sha = new(@"\b[0-9a-f]{12,40}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Url = new(@"https?://\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WinPath = new(@"[a-z]:\\[^\s'""]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UnixPath = new(@"(/[\w.\-]+){2,}", RegexOptions.Compiled);
    private static readonly Regex Timestamp = new(@"\d{4}-\d{2}-\d{2}[t ]\d{2}:\d{2}(:\d{2}(\.\d+)?)?z?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Number = new(@"\b\d+(\.\d+)?\b", RegexOptions.Compiled);
    private static readonly Regex Space = new(@"\s+", RegexOptions.Compiled);

    public static string Normalise(string message)
    {
        var s = message.ToLowerInvariant();
        s = Url.Replace(s, "<url>");
        s = Guid.Replace(s, "<guid>");
        s = Timestamp.Replace(s, "<ts>");
        s = WinPath.Replace(s, "<path>");
        s = UnixPath.Replace(s, "<path>");
        s = Sha.Replace(s, "<sha>");
        s = Number.Replace(s, "<n>");
        s = Space.Replace(s, " ").Trim();
        return s.Length > 240 ? s[..240] : s;
    }

    public static string Compute(string message) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalise(message))))[..16].ToLowerInvariant();
}

using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dashboard.Core.Configuration;
using Dashboard.Core.Entities;
using Dashboard.Core.Metrics;
using Dashboard.Web.Services;

namespace Dashboard.Web.Ado;

/// <summary>Honours Retry-After / X-RateLimit-* and records TSTU usage.</summary>
public sealed class ThrottlingHandler(AdoApiStats stats, ILogger<ThrottlingHandler> log) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            stats.RecordCall();
            var response = await base.SendAsync(request, ct);
            if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var rem) && double.TryParse(rem.FirstOrDefault(), out var remaining))
            {
                double? limit = response.Headers.TryGetValues("X-RateLimit-Limit", out var lim) && double.TryParse(lim.FirstOrDefault(), out var l) ? l : null;
                stats.RecordRemaining(remaining, limit);
            }
            if (response.Headers.TryGetValues("X-RateLimit-Delay", out var delayVals) && double.TryParse(delayVals.FirstOrDefault(), out var delaySec) && delaySec > 0)
            {
                stats.RecordThrottle();
                log.LogWarning("ADO is delaying requests by {Delay}s (TSTU budget); backing off", delaySec);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(delaySec, 30)), ct);
            }
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable && attempt < 5)
            {
                stats.RecordThrottle();
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt) * 5);
                log.LogWarning("ADO returned {Status}; retrying in {Wait}", (int)response.StatusCode, wait);
                response.Dispose();
                await Task.Delay(wait, ct);
                continue;
            }
            return response;
        }
    }
}

public sealed class AdoAuthException(string message) : Exception(message);

/// <summary>Azure DevOps Services REST client (api-version 7.1, previews where required). Read-only.</summary>
public sealed class LiveAdoClient : IAdoClient
{
    private readonly HttpClient _http;
    private readonly ConfigService _config;
    private readonly AdoApiStats _stats;
    private readonly ILogger<LiveAdoClient> _log;
    private readonly string? _pat;

    public LiveAdoClient(HttpClient http, ConfigService config, AdoApiStats stats, ILogger<LiveAdoClient> log, IConfiguration env)
    {
        _http = http;
        _config = config;
        _stats = stats;
        _log = log;
        _pat = env["ADO_PAT"];
        if (!string.IsNullOrWhiteSpace(_pat))
        {
            var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + _pat));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.Timeout = TimeSpan.FromSeconds(100);
    }

    public bool IsMock => false;
    public string? GitAuthHeader => string.IsNullOrWhiteSpace(_pat) ? null : "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + _pat));

    private AzureDevOpsConfig Ado => _config.Current.AzureDevOps;
    private string Org => $"{Ado.BaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(Ado.Organization)}";
    private string Proj => $"{Org}/{Uri.EscapeDataString(Ado.Project)}";
    private string V(string? preview = null) => "api-version=" + (preview ?? Ado.ApiVersion);
    private string Vssps => Ado.BaseUrl.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase)
        ? $"https://vssps.dev.azure.com/{Uri.EscapeDataString(Ado.Organization)}" : Org;
    private string Vstmr => Ado.BaseUrl.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase)
        ? $"https://vstmr.dev.azure.com/{Uri.EscapeDataString(Ado.Organization)}/{Uri.EscapeDataString(Ado.Project)}" : Proj;

    // ---------- HTTP helpers ----------

    private async Task<(JsonDocument Doc, string? Continuation)> GetAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_pat)) throw new AdoAuthException("ADO_PAT is not set. Add it to .env and restart, or switch azureDevOps.mode to mock.");
        using var resp = await _http.GetAsync(url, ct);
        if (resp.StatusCode is HttpStatusCode.Unauthorized || (resp.StatusCode == HttpStatusCode.Redirect || resp.StatusCode == HttpStatusCode.NonAuthoritativeInformation))
        {
            _stats.RecordError($"401 Unauthorized for {Trim(url)} — PAT expired or invalid", auth: true);
            throw new AdoAuthException("Azure DevOps rejected the PAT (401). It may be expired, revoked or for another organisation.");
        }
        if (resp.StatusCode == HttpStatusCode.Forbidden)
        {
            _stats.RecordError($"403 Forbidden for {Trim(url)} — missing PAT scope?", auth: false);
            throw new HttpRequestException($"403 Forbidden: {Trim(url)} (missing PAT scope or permission)", null, HttpStatusCode.Forbidden);
        }
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _stats.RecordError($"{(int)resp.StatusCode} for {Trim(url)}", auth: false);
            throw new HttpRequestException($"{(int)resp.StatusCode} {resp.ReasonPhrase}: {Trim(url)} {body[..Math.Min(body.Length, 300)]}", null, resp.StatusCode);
        }
        var media = resp.Content.Headers.ContentType?.MediaType;
        if (media is not null && media.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            _stats.RecordError("Received HTML (sign-in page) — PAT invalid", auth: true);
            throw new AdoAuthException("Azure DevOps returned a sign-in page; the PAT is invalid.");
        }
        _stats.RecordSuccess();
        var cont = resp.Headers.TryGetValues("x-ms-continuationtoken", out var vals) ? vals.FirstOrDefault() : null;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return (await JsonDocument.ParseAsync(stream, cancellationToken: ct), cont);
    }

    private async Task<List<JsonElement>> GetValuesAsync(string url, CancellationToken ct, int max = 5000, bool continuation = false)
    {
        var list = new List<JsonElement>();
        string? token = null;
        do
        {
            var u = token is null ? url : url + "&continuationToken=" + Uri.EscapeDataString(token);
            var (doc, cont) = await GetAsync(u, ct);
            using (doc)
            {
                if (doc.RootElement.TryGetProperty("value", out var value))
                    foreach (var v in value.EnumerateArray()) list.Add(v.Clone());
            }
            token = continuation ? cont : null;
        } while (token is not null && list.Count < max);
        return list;
    }

    private async Task<List<JsonElement>> GetSkipPagedAsync(string url, int page, CancellationToken ct, int max = 5000)
    {
        var list = new List<JsonElement>();
        for (var skip = 0; list.Count < max; skip += page)
        {
            var batch = await GetValuesAsync($"{url}&$top={page}&$skip={skip}", ct);
            list.AddRange(batch);
            if (batch.Count < page) break;
        }
        return list;
    }

    private static string Trim(string url) => url.Length > 160 ? url[..160] + "…" : url;
    private static string Iso(DateTimeOffset t) => Uri.EscapeDataString(t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"));

    // ---------- API ----------

    public async Task<AdoIdentity> GetMeAsync(CancellationToken ct)
    {
        var (doc, _) = await GetAsync($"{Org}/_apis/connectionData?{V("7.1-preview")}", ct);
        using (doc)
        {
            var u = doc.RootElement.GetProperty("authenticatedUser");
            var id = u.Str("id") ?? "";
            var name = u.Str("providerDisplayName") ?? u.Str("customDisplayName") ?? "me";
            var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                // Expanded group membership (requires vso.identity); used to treat group-assigned approvals as "mine".
                var (idDoc, _) = await GetAsync($"{Vssps}/_apis/identities?identityIds={id}&queryMembership=Expanded&{V()}", ct);
                using (idDoc)
                {
                    var descriptors = new List<string>();
                    foreach (var ident in idDoc.RootElement.GetProperty("value").EnumerateArray())
                        if (ident.TryGetProperty("memberOf", out var mo))
                            foreach (var d in mo.EnumerateArray()) if (d.GetString() is { } s) descriptors.Add(s);
                    foreach (var chunk in descriptors.Chunk(50))
                    {
                        var (gDoc, _) = await GetAsync($"{Vssps}/_apis/identities?descriptors={Uri.EscapeDataString(string.Join(',', chunk))}&{V()}", ct);
                        using (gDoc)
                            foreach (var g in gDoc.RootElement.GetProperty("value").EnumerateArray())
                                if (g.Str("id") is { } gid) groups.Add(gid);
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or KeyNotFoundException or JsonException)
            {
                _log.LogInformation("Group membership lookup unavailable ({Error}); only direct approval assignments will count as mine", ex.Message);
            }
            return new AdoIdentity(id, name, groups);
        }
    }

    public async Task<string?> GetProjectIdAsync(CancellationToken ct)
    {
        var (doc, _) = await GetAsync($"{Org}/_apis/projects/{Uri.EscapeDataString(Ado.Project)}?{V()}", ct);
        using (doc) return doc.RootElement.Str("id");
    }

    public async Task<IReadOnlyList<Repo>> ListRepositoriesAsync(CancellationToken ct)
    {
        var values = await GetValuesAsync($"{Proj}/_apis/git/repositories?includeHidden=false&{V()}", ct);
        var now = DateTimeOffset.UtcNow;
        return values.Select(v => new Repo
        {
            Id = v.Str("id")!,
            Name = v.Str("name") ?? "",
            ProjectName = v.Obj("project")?.Str("name") ?? Ado.Project,
            DefaultBranch = v.Str("defaultBranch"),
            WebUrl = v.Str("webUrl"),
            RemoteUrl = v.Str("remoteUrl"),
            SizeBytes = v.Long("size") ?? 0,
            IsDisabled = v.Bool("isDisabled") ?? false,
            LastSeenAt = now,
        }).ToList();
    }

    public async Task<IReadOnlyList<Pipeline>> ListPipelinesAsync(string repoId, CancellationToken ct)
    {
        var values = await GetValuesAsync($"{Proj}/_apis/build/definitions?repositoryId={repoId}&repositoryType=TfsGit&includeAllProperties=true&{V()}", ct);
        var now = DateTimeOffset.UtcNow;
        return values.Select(v => new Pipeline
        {
            Id = v.Int("id") ?? 0,
            RepoId = repoId,
            Name = v.Str("name") ?? "",
            Folder = v.Str("path"),
            YamlPath = v.Obj("process")?.Str("yamlFilename"),
            WebUrl = v.Obj("_links")?.Obj("web")?.Str("href"),
            LastSeenAt = now,
        }).ToList();
    }

    public async Task<IReadOnlyList<Run>> ListRunsAsync(RunQuery q, CancellationToken ct)
    {
        var results = new List<Run>();
        foreach (var chunk in q.DefinitionIds.Chunk(20))
        {
            var url = $"{Proj}/_apis/build/builds?definitions={string.Join(',', chunk)}&queryOrder=finishTimeAscending&$top=500&{V()}";
            if (q.StatusFilter != "all") url += "&statusFilter=" + q.StatusFilter;
            if (q.MinTime is not null) url += "&minTime=" + Iso(q.MinTime.Value);
            var values = await GetValuesAsync(url, ct, q.Max, continuation: true);
            results.AddRange(values.Select(MapBuild));
        }
        return results;
    }

    public async Task<IReadOnlyList<Run>> ListActiveRunsAsync(CancellationToken ct)
    {
        var values = await GetValuesAsync($"{Proj}/_apis/build/builds?statusFilter=inProgress,notStarted,cancelling,postponed&$top=500&{V()}", ct);
        return values.Select(MapBuild).ToList();
    }

    private static Run MapBuild(JsonElement v) => new()
    {
        Id = v.Int("id") ?? 0,
        PipelineId = v.Obj("definition")?.Int("id") ?? 0,
        RepoId = v.Obj("repository")?.Str("id") ?? "",
        BuildNumber = v.Str("buildNumber"),
        SourceBranch = v.Str("sourceBranch"),
        SourceVersion = v.Str("sourceVersion"),
        Reason = v.Str("reason") ?? "",
        Status = v.Str("status") ?? "",
        Result = v.Str("result"),
        QueueTime = v.Date("queueTime") ?? DateTimeOffset.UtcNow,
        StartTime = v.Date("startTime"),
        FinishTime = v.Date("finishTime"),
        RequestedFor = v.Obj("requestedFor")?.Str("displayName"),
        WebUrl = v.Obj("_links")?.Obj("web")?.Str("href"),
        RawJson = Gzip(v.GetRawText()),
        LastSyncedAt = DateTimeOffset.UtcNow,
    };

    public async Task<IReadOnlyList<TimelineRecord>> GetTimelineAsync(int buildId, CancellationToken ct)
    {
        var (doc, _) = await GetAsync($"{Proj}/_apis/build/builds/{buildId}/timeline?{V()}", ct);
        var list = new List<TimelineRecord>();
        var previous = new List<(string TimelineId, string RecordId)>();
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("records", out var records)) return list;
            foreach (var r in records.EnumerateArray())
            {
                list.Add(MapRecord(buildId, r, false));
                if (r.TryGetProperty("previousAttempts", out var prev) && prev.ValueKind == JsonValueKind.Array)
                    foreach (var p in prev.EnumerateArray())
                        if (p.Str("timelineId") is { } tid && p.Str("recordId") is { } rid) previous.Add((tid, rid));
            }
        }
        // Previous attempts live in their own timelines; fetch each once and keep only the referenced records + descendants.
        foreach (var g in previous.GroupBy(p => p.TimelineId))
        {
            try
            {
                var (pdoc, _) = await GetAsync($"{Proj}/_apis/build/builds/{buildId}/timeline/{g.Key}?{V()}", ct);
                using (pdoc)
                {
                    if (!pdoc.RootElement.TryGetProperty("records", out var precs)) continue;
                    var all = precs.EnumerateArray().Select(r => MapRecord(buildId, r, true)).ToList();
                    var keep = g.Select(x => x.RecordId).ToHashSet();
                    var changed = true;
                    while (changed)
                    {
                        changed = false;
                        foreach (var rec in all)
                            if (!keep.Contains(rec.RecordId) && rec.ParentId is not null && keep.Contains(rec.ParentId)) { keep.Add(rec.RecordId); changed = true; }
                    }
                    var existing = list.Select(x => x.RecordId).ToHashSet();
                    list.AddRange(all.Where(r => keep.Contains(r.RecordId) && !existing.Contains(r.RecordId)));
                }
            }
            catch (HttpRequestException ex) { _log.LogDebug("Previous-attempt timeline {Tid} for build {Build} unavailable: {Err}", g.Key, buildId, ex.Message); }
        }
        return list;
    }

    private static TimelineRecord MapRecord(int buildId, JsonElement r, bool previousAttempt)
    {
        string? error = null;
        if (r.TryGetProperty("issues", out var issues) && issues.ValueKind == JsonValueKind.Array)
            error = issues.EnumerateArray().Where(i => i.Str("type") == "error").Select(i => i.Str("message")).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));
        return new TimelineRecord
        {
            RunId = buildId,
            RecordId = r.Str("id") ?? Guid.NewGuid().ToString(),
            ParentId = r.Str("parentId"),
            Type = r.Str("type") ?? "",
            Name = r.Str("name") ?? "",
            Identifier = r.Str("identifier") ?? r.Str("refName"),
            Attempt = r.Int("attempt") ?? 1,
            IsPreviousAttempt = previousAttempt,
            State = r.Str("state"),
            Result = r.Str("result"),
            StartTime = r.Date("startTime"),
            FinishTime = r.Date("finishTime"),
            Order = r.Int("order") ?? 0,
            ErrorMessage = error is null ? null : error.Length > 1000 ? error[..1000] : error,
            ErrorFingerprint = error is null ? null : FailureFingerprint.Compute(error),
        };
    }

    public async Task<IReadOnlyList<RunResource>> GetRunResourcesAsync(int pipelineId, int runId, CancellationToken ct)
    {
        var (doc, _) = await GetAsync($"{Proj}/_apis/pipelines/{pipelineId}/runs/{runId}?{V()}", ct);
        using (doc)
        {
            var list = new List<RunResource>();
            if (doc.RootElement.Obj("resources")?.Obj("repositories") is { } repos)
                foreach (var p in repos.EnumerateObject())
                    list.Add(new RunResource(p.Name, p.Value.Obj("repository")?.Str("fullName") ?? p.Value.Obj("repository")?.Str("name"),
                        p.Value.Str("refName"), p.Value.Str("version")));
            return list;
        }
    }

    public async Task<IReadOnlyList<PullRequest>> ListActivePullRequestsAsync(CancellationToken ct)
    {
        var values = await GetSkipPagedAsync($"{Proj}/_apis/git/pullrequests?searchCriteria.status=active&{V()}", 500, ct);
        return values.Select(MapPr).ToList();
    }

    public async Task<IReadOnlyList<PullRequest>> ListClosedPullRequestsAsync(string repoId, DateTimeOffset since, CancellationToken ct)
    {
        var values = await GetSkipPagedAsync(
            $"{Proj}/_apis/git/repositories/{repoId}/pullrequests?searchCriteria.status=all&searchCriteria.queryTimeRangeType=closed&searchCriteria.minTime={Iso(since)}&{V()}", 200, ct);
        return values.Select(MapPr).Where(p => p.Status != "active").ToList();
    }

    public async Task<PullRequest?> GetPullRequestAsync(string repoId, int pullRequestId, CancellationToken ct)
    {
        try
        {
            var (doc, _) = await GetAsync($"{Proj}/_apis/git/repositories/{repoId}/pullrequests/{pullRequestId}?{V()}", ct);
            using (doc) return MapPr(doc.RootElement);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    private static PullRequest MapPr(JsonElement v)
    {
        var reviewers = new List<ReviewerInfo>();
        if (v.TryGetProperty("reviewers", out var rv) && rv.ValueKind == JsonValueKind.Array)
            foreach (var r in rv.EnumerateArray())
                reviewers.Add(new ReviewerInfo { Id = r.Str("id"), DisplayName = r.Str("displayName"), Vote = r.Int("vote") ?? 0, IsRequired = r.Bool("isRequired") ?? false, IsContainer = r.Bool("isContainer") ?? false });
        var repoId = v.Obj("repository")?.Str("id") ?? "";
        var id = v.Int("pullRequestId") ?? 0;
        var web = v.Obj("repository")?.Str("webUrl");
        return new PullRequest
        {
            Id = id,
            RepoId = repoId,
            Title = v.Str("title") ?? "",
            Status = v.Str("status") ?? "",
            IsDraft = v.Bool("isDraft") ?? false,
            AuthorId = v.Obj("createdBy")?.Str("id"),
            AuthorName = v.Obj("createdBy")?.Str("displayName"),
            CreatedOn = v.Date("creationDate") ?? DateTimeOffset.UtcNow,
            ClosedOn = v.Date("closedDate"),
            MergeStatus = v.Str("mergeStatus"),
            SourceBranch = v.Str("sourceRefName"),
            TargetBranch = v.Str("targetRefName"),
            LastMergeCommit = v.Obj("lastMergeCommit")?.Str("commitId"),
            ReviewersJson = JsonSerializer.Serialize(reviewers, JsonDefaults.Web),
            WebUrl = web is null ? null : $"{web}/pullrequest/{id}",
            RawJson = Gzip(v.GetRawText()),
            LastSyncedAt = DateTimeOffset.UtcNow,
        };
    }

    public async Task<PrEventsResult> GetPullRequestEventsAsync(string repoId, int pullRequestId, string? authorId, CancellationToken ct)
    {
        var threads = await GetValuesAsync($"{Proj}/_apis/git/repositories/{repoId}/pullRequests/{pullRequestId}/threads?{V()}", ct);
        var events = new List<PrEvent>();
        DateTimeOffset? published = null;
        foreach (var t in threads)
        {
            var threadType = t.Obj("properties")?.Obj("CodeReviewThreadType")?.Str("$value");
            if (!t.TryGetProperty("comments", out var comments) || comments.ValueKind != JsonValueKind.Array) continue;
            foreach (var c in comments.EnumerateArray())
            {
                var author = c.Obj("author");
                var aid = author?.Str("id");
                var aname = author?.Str("displayName");
                var time = c.Date("publishedDate") ?? t.Date("publishedDate");
                if (time is null) continue;
                var type = c.Str("commentType");
                var isAuthor = aid is not null && string.Equals(aid, authorId, StringComparison.OrdinalIgnoreCase);
                var isBot = aname is not null && (aname.Contains("Build Service", StringComparison.OrdinalIgnoreCase) || aname.Contains("[bot]", StringComparison.OrdinalIgnoreCase)
                                                  || aname.Equals("Azure Pipelines", StringComparison.OrdinalIgnoreCase));
                if (threadType == "VoteUpdate")
                {
                    int? vote = int.TryParse(t.Obj("properties")?.Obj("CodeReviewVoteResult")?.Str("$value"), out var vv) ? vv : null;
                    events.Add(new PrEvent { PullRequestId = pullRequestId, ActorId = aid, ActorName = aname, IsAuthor = isAuthor, IsBot = isBot, Kind = PrEventKind.Vote, Vote = vote, Time = time.Value });
                }
                else if (type == "text")
                    events.Add(new PrEvent { PullRequestId = pullRequestId, ActorId = aid, ActorName = aname, IsAuthor = isAuthor, IsBot = isBot, Kind = PrEventKind.Comment, Time = time.Value });
                else if (type == "system" && (c.Str("content") ?? "").Contains("published", StringComparison.OrdinalIgnoreCase))
                    published ??= time;
                else if (threadType == "RefUpdate")
                    events.Add(new PrEvent { PullRequestId = pullRequestId, ActorId = aid, ActorName = aname, IsAuthor = isAuthor, IsBot = isBot, Kind = PrEventKind.Pushed, Time = time.Value });
            }
        }
        return new PrEventsResult(events, published);
    }

    public async Task<string?> GetPolicyStatusAsync(string projectId, int pullRequestId, CancellationToken ct)
    {
        var artifact = Uri.EscapeDataString($"vstfs:///CodeReview/CodeReviewId/{projectId}/{pullRequestId}");
        var values = await GetValuesAsync($"{Proj}/_apis/policy/evaluations?artifactId={artifact}&{V("7.1-preview.1")}", ct);
        var blocking = values.Where(v => v.Obj("configuration")?.Bool("isBlocking") ?? false).Select(v => v.Str("status")).ToList();
        if (blocking.Count == 0) return null;
        if (blocking.Contains("rejected")) return "rejected";
        if (blocking.Contains("running") || blocking.Contains("queued")) return "running";
        return blocking.All(s => s is "approved" or "notApplicable") ? "approved" : "running";
    }

    public async Task<string?> GetBranchHeadAsync(string repoId, string branch, CancellationToken ct)
    {
        var name = branch.StartsWith("refs/", StringComparison.Ordinal) ? branch[5..] : "heads/" + branch;
        var values = await GetValuesAsync($"{Proj}/_apis/git/repositories/{repoId}/refs?filter={Uri.EscapeDataString(name)}&{V()}", ct);
        return values.FirstOrDefault(v => string.Equals(v.Str("name"), "refs/" + name, StringComparison.OrdinalIgnoreCase)).Str("objectId");
    }

    public async Task<IReadOnlyList<Commit>> ListCommitsAsync(string repoId, string branch, DateTimeOffset since, CancellationToken ct)
    {
        var b = branch.StartsWith("refs/heads/", StringComparison.Ordinal) ? branch[11..] : branch;
        var values = await GetSkipPagedAsync($"{Proj}/_apis/git/repositories/{repoId}/commits?searchCriteria.itemVersion.version={Uri.EscapeDataString(b)}&searchCriteria.fromDate={Iso(since)}&{V()}", 500, ct, 5000);
        return values.Select(v => new Commit
        {
            RepoId = repoId,
            Sha = v.Str("commitId") ?? "",
            Author = v.Obj("author")?.Str("name"),
            AuthorEmail = v.Obj("author")?.Str("email"),
            Date = v.Obj("committer")?.Date("date") ?? v.Obj("author")?.Date("date") ?? DateTimeOffset.UtcNow,
            Message = FirstLine(v.Str("comment")),
        }).ToList();
    }

    public async Task<IReadOnlyList<BranchPolicy>> ListPoliciesAsync(CancellationToken ct)
    {
        var values = await GetValuesAsync($"{Proj}/_apis/policy/configurations?{V()}", ct, continuation: true);
        var list = new List<BranchPolicy>();
        foreach (var v in values)
        {
            var settings = v.Obj("settings");
            if (settings is null || !settings.Value.TryGetProperty("scope", out var scopes) || scopes.ValueKind != JsonValueKind.Array) continue;
            foreach (var s in scopes.EnumerateArray())
            {
                var repoId = s.Str("repositoryId");
                if (repoId is null) continue;
                list.Add(new BranchPolicy
                {
                    Id = (v.Int("id") ?? 0) * 1000 + list.Count(p => p.Id / 1000 == (v.Int("id") ?? 0)),
                    RepoId = repoId,
                    TypeName = v.Obj("type")?.Str("displayName") ?? "",
                    IsEnabled = v.Bool("isEnabled") ?? false,
                    IsBlocking = v.Bool("isBlocking") ?? false,
                    RefName = s.Str("refName"),
                    MinimumApproverCount = settings.Value.Int("minimumApproverCount"),
                    BuildDefinitionId = settings.Value.Int("buildDefinitionId"),
                    SettingsJson = settings.Value.GetRawText(),
                });
            }
        }
        return list;
    }

    public async Task<IReadOnlyList<Approval>> ListPendingApprovalsAsync(CancellationToken ct)
    {
        var values = await GetValuesAsync($"{Proj}/_apis/pipelines/approvals?state=pending&$expand=steps&{V()}", ct);
        var now = DateTimeOffset.UtcNow;
        return values.Select(v =>
        {
            var approvers = new List<object>();
            if (v.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
                foreach (var s in steps.EnumerateArray())
                    approvers.Add(new { id = s.Obj("assignedApprover")?.Str("id"), displayName = s.Obj("assignedApprover")?.Str("displayName"), status = s.Str("status") });
            var pipeline = v.Obj("pipeline");
            return new Approval
            {
                Id = v.Str("id") ?? "",
                RunId = pipeline?.Obj("owner")?.Int("id"),
                PipelineId = pipeline?.Int("id") ?? (int.TryParse(pipeline?.Str("id"), out var pid) ? pid : null),
                Status = v.Str("status") ?? "pending",
                CreatedOn = v.Date("createdOn") ?? now,
                LastModifiedOn = v.Date("lastModifiedOn"),
                ApproversJson = JsonSerializer.Serialize(approvers, JsonDefaults.Web),
                Instructions = v.Str("instructions"),
                MinRequiredApprovers = v.Int("minRequiredApprovers") ?? 1,
                WebUrl = pipeline?.Obj("owner")?.Obj("_links")?.Obj("web")?.Str("href"),
                LastSyncedAt = now,
            };
        }).ToList();
    }

    public async Task<IReadOnlyList<EnvironmentCheck>> ListEnvironmentChecksAsync(int environmentId, string environmentName, CancellationToken ct)
    {
        var values = await GetValuesAsync($"{Proj}/_apis/pipelines/checks/configurations?resourceType=environment&resourceId={environmentId}&$expand=settings&{V("7.1-preview.1")}", ct);
        var now = DateTimeOffset.UtcNow;
        return values.Select(v =>
        {
            var settings = v.Obj("settings");
            var approvers = new List<(string? Id, string? Name)>();
            if (settings?.TryGetProperty("approvers", out var a) == true && a.ValueKind == JsonValueKind.Array)
                foreach (var x in a.EnumerateArray()) approvers.Add((x.Str("id"), x.Str("displayName")));
            return new EnvironmentCheck
            {
                EnvironmentName = environmentName,
                EnvironmentAdoId = environmentId,
                CheckId = v.Int("id")?.ToString() ?? v.Str("id") ?? "",
                CheckType = v.Obj("type")?.Str("name") ?? "",
                DisplayName = settings?.Str("displayName") ?? v.Obj("type")?.Str("name") ?? "",
                ApproverNames = string.Join(", ", approvers.Select(x => x.Name)),
                SettingsJson = JsonSerializer.Serialize(new { approverIds = approvers.Select(x => x.Id), raw = settings?.GetRawText() }, JsonDefaults.Web),
                LastSeenAt = now,
            };
        }).ToList();
    }

    public async Task<IReadOnlyList<TemplateCommit>> ListTemplateCommitsAsync(string branch, DateTimeOffset since, CancellationToken ct)
    {
        var t = _config.Current.Templates;
        var url = $"{Org}/{Uri.EscapeDataString(t.Project)}/_apis/git/repositories/{Uri.EscapeDataString(t.Repository)}/commits?searchCriteria.itemVersion.version={Uri.EscapeDataString(branch)}&searchCriteria.fromDate={Iso(since)}&{V()}";
        var values = await GetSkipPagedAsync(url, 500, ct, 2000);
        return values.Select(v => new TemplateCommit
        {
            Sha = v.Str("commitId") ?? "",
            Branch = branch,
            Date = v.Obj("committer")?.Date("date") ?? DateTimeOffset.UtcNow,
            Author = v.Obj("author")?.Str("name"),
            Message = FirstLine(v.Str("comment")),
        }).ToList();
    }

    public async Task<IReadOnlyList<TestFlake>> ListTestFlakesAsync(int buildId, string repoId, CancellationToken ct)
    {
        var values = await GetValuesAsync($"{Vstmr}/_apis/testresults/resultsbybuild?buildId={buildId}&outcomes=Failed,Passed&$top=5000&{V("7.1-preview.1")}", ct);
        var date = DateTimeOffset.UtcNow;
        return values.GroupBy(v => v.Str("testCaseTitle") ?? v.Str("automatedTestName") ?? "?")
            .Where(g => g.Any(v => v.Str("outcome") == "Failed") && g.Any(v => v.Str("outcome") == "Passed"))
            .Select(g => new TestFlake { RunId = buildId, RepoId = repoId, TestName = g.Key, Outcome = "Passed", Flaky = true, Date = date })
            .ToList();
    }

    public async Task<IReadOnlyList<ProbeResult>> ProbeAsync(CancellationToken ct)
    {
        var probes = new (string Area, string Scope, string Url)[]
        {
            ("Identity", "(any)", $"{Org}/_apis/connectionData?{V("7.1-preview")}"),
            ("Project", "vso.project", $"{Org}/_apis/projects/{Uri.EscapeDataString(Ado.Project)}?{V()}"),
            ("Repositories", "vso.code", $"{Proj}/_apis/git/repositories?$top=1&{V()}"),
            ("Pull requests", "vso.code", $"{Proj}/_apis/git/pullrequests?$top=1&{V()}"),
            ("Policies", "vso.code", $"{Proj}/_apis/policy/configurations?$top=1&{V()}"),
            ("Builds", "vso.build", $"{Proj}/_apis/build/builds?$top=1&{V()}"),
            ("Approvals", "vso.build", $"{Proj}/_apis/pipelines/approvals?state=pending&$top=1&{V()}"),
            ("Environment checks", "vso.build", $"{Proj}/_apis/pipelines/checks/configurations?{V("7.1-preview.1")}"),
            ("Group membership", "vso.identity", $"{Vssps}/_apis/identities?searchFilter=General&filterValue=x&{V()}"),
            ("Test results", "vso.test", $"{Vstmr}/_apis/testresults/resultsbybuild?buildId=1&{V("7.1-preview.1")}"),
            ("Template repo", "vso.code", $"{Org}/{Uri.EscapeDataString(_config.Current.Templates.Project)}/_apis/git/repositories/{Uri.EscapeDataString(_config.Current.Templates.Repository)}?{V()}"),
        };
        var results = new List<ProbeResult>();
        foreach (var (area, scope, url) in probes)
        {
            try { var (d, _) = await GetAsync(url, ct); d.Dispose(); results.Add(new ProbeResult(area, scope, true, "ok")); }
            catch (Exception ex) when (ex is HttpRequestException or AdoAuthException or JsonException or TaskCanceledException)
            {
                var detail = ex is HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.BadRequest } ? "reachable (no data for probe)" : ex.Message;
                results.Add(new ProbeResult(area, scope, ex is HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.BadRequest }, detail));
            }
        }
        return results;
    }

    private static string? FirstLine(string? s) => s?.Split('\n', 2)[0].Trim();

    internal static byte[] Gzip(string text)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest)) gz.Write(Encoding.UTF8.GetBytes(text));
        return ms.ToArray();
    }
}

internal static class JsonExt
{
    public static string? Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        } : null;

    public static int? Int(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var j) ? j : null
            : null;

    public static long? Long(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : null;

    public static bool? Bool(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    public static DateTimeOffset? Date(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), out var d)
            ? d.Year < 1900 ? null : d.ToUniversalTime() : null;

    public static JsonElement? Obj(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    public static JsonElement? Obj(this JsonElement? e, string name) => e is null ? null : e.Value.Obj(name);
    public static string? Str(this JsonElement? e, string name) => e?.Str(name);
    public static int? Int(this JsonElement? e, string name) => e?.Int(name);
    public static bool? Bool(this JsonElement? e, string name) => e?.Bool(name);
    public static DateTimeOffset? Date(this JsonElement? e, string name) => e?.Date(name);
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
}

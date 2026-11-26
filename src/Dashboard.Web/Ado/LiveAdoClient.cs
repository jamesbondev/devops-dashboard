using System.Globalization;
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

/// <summary>
/// Honours Retry-After / X-RateLimit-* (TSTU) and retries 429/502/503/504 with backoff.
/// Microsoft's guidance: after any response carrying Retry-After, wait that long before sending another request,
/// so the wait is a process-wide gate shared by every request (the handler instance itself is pooled/rotated).
/// Each attempt has its own timeout; <see cref="LiveAdoClient"/> sets HttpClient.Timeout as an overall backstop.
/// </summary>
public sealed class ThrottlingHandler(AdoApiStats stats, ILogger<ThrottlingHandler> log) : DelegatingHandler
{
    internal const int MaxAttempts = 5;
    internal static TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(100);
    internal static TimeSpan MaxWait { get; set; } = TimeSpan.FromMinutes(2);
    private static long _notBeforeTicks;

    /// <summary>Test hook: forget any pending back-off.</summary>
    internal static void ResetGate() => Interlocked.Exchange(ref _notBeforeTicks, 0);

    private static void PushGate(TimeSpan wait)
    {
        var until = DateTimeOffset.UtcNow.Add(wait).UtcTicks;
        long cur;
        do { cur = Interlocked.Read(ref _notBeforeTicks); if (cur >= until) return; }
        while (Interlocked.CompareExchange(ref _notBeforeTicks, until, cur) != cur);
    }

    private static async Task WaitGateAsync(CancellationToken ct)
    {
        var wait = new DateTimeOffset(Interlocked.Read(ref _notBeforeTicks), TimeSpan.Zero) - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait < MaxWait ? wait : MaxWait, ct);
    }

    private static double? Header(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var vals)
        && double.TryParse(vals.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static TimeSpan? RetryAfter(HttpResponseMessage r)
    {
        var ra = r.Headers.RetryAfter;
        if (ra?.Delta is { } delta) return delta;
        if (ra?.Date is { } date) return date - DateTimeOffset.UtcNow;
        return null;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await WaitGateAsync(ct);
            stats.RecordCall();
            HttpResponseMessage response;
            using (var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                attemptCts.CancelAfter(AttemptTimeout);
                try
                {
                    response = await base.SendAsync(request, attemptCts.Token);
                    // Buffer the body under the per-attempt timeout (HttpClient would otherwise buffer it without one).
                    await response.Content.LoadIntoBufferAsync(attemptCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Same shape as HttpClient's own timeout, which callers already handle.
                    throw new TaskCanceledException($"ADO request timed out after {AttemptTimeout.TotalSeconds:0}s", new TimeoutException());
                }
            }

            if (Header(response, "X-RateLimit-Remaining") is { } remaining)
                stats.RecordRemaining(remaining, Header(response, "X-RateLimit-Limit"));
            if (Header(response, "X-RateLimit-Delay") is { } delaySec && delaySec > 0)
            {
                // The server already delayed this request: we are over the TSTU budget. Slow down further requests.
                stats.RecordThrottle();
                log.LogWarning("ADO delayed a request by {Delay}s (TSTU budget); backing off", delaySec);
                PushGate(TimeSpan.FromSeconds(Math.Min(delaySec, 30)));
            }
            var retryAfter = RetryAfter(response);
            if (retryAfter is { } ra && ra > TimeSpan.Zero) PushGate(ra < MaxWait ? ra : MaxWait);

            var transient = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout;
            if (transient && attempt < MaxAttempts)
            {
                stats.RecordThrottle();
                var wait = retryAfter is { } w && w > TimeSpan.Zero ? w : TimeSpan.FromSeconds(Math.Pow(2, attempt) * 5);
                if (wait > MaxWait) wait = MaxWait;
                log.LogWarning("ADO returned {Status} for {Url}; retry {Attempt}/{Max} in {Wait}", (int)response.StatusCode, request.RequestUri?.AbsolutePath, attempt, MaxAttempts - 1, wait);
                response.Dispose();
                PushGate(wait);
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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _repoNames = new(StringComparer.OrdinalIgnoreCase);
    private bool _templateRepoResolved;

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
        // Per-attempt timeouts live in ThrottlingHandler; this is the backstop across retries and Retry-After waits.
        _http.Timeout = TimeSpan.FromMinutes(10);
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
        // 203 + HTML and 302 to the sign-in page are how ADO answers an invalid/expired PAT on some endpoints.
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NonAuthoritativeInformation or HttpStatusCode.Redirect or HttpStatusCode.Found)
        {
            _stats.RecordError($"{(int)resp.StatusCode} for {Trim(url)}: PAT expired or invalid", auth: true);
            throw new AdoAuthException($"Azure DevOps rejected the PAT ({(int)resp.StatusCode}). It may be expired, revoked or for another organisation.");
        }
        if (resp.StatusCode == HttpStatusCode.Forbidden)
        {
            _stats.RecordError($"403 Forbidden for {Trim(url)}: missing PAT scope?", auth: false);
            throw new HttpRequestException($"403 Forbidden: {Trim(url)} (missing PAT scope or permission)", null, HttpStatusCode.Forbidden);
        }
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _stats.RecordError($"{(int)resp.StatusCode} for {Trim(url)}", auth: false);
            throw new HttpRequestException($"{(int)resp.StatusCode} {resp.ReasonPhrase}: {Trim(url)} {body[..Math.Min(body.Length, 300)]}", null, resp.StatusCode);
        }
        var media = resp.Content.Headers.ContentType?.MediaType;
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        var start = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) start = 3;
        while (start < bytes.Length && bytes[start] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') start++;
        if ((media is not null && media.Contains("html", StringComparison.OrdinalIgnoreCase)) || (start < bytes.Length && bytes[start] == (byte)'<'))
        {
            _stats.RecordError("Received HTML (sign-in page): PAT invalid", auth: true);
            throw new AdoAuthException("Azure DevOps returned a sign-in page; the PAT is invalid or expired.");
        }
        _stats.RecordSuccess();
        var cont = resp.Headers.TryGetValues("x-ms-continuationtoken", out var vals) ? vals.FirstOrDefault() : null;
        if (string.IsNullOrWhiteSpace(cont)) cont = null;
        // 204 / empty body (e.g. a timeline for a build that has not started) parses as an empty object.
        var doc = start >= bytes.Length ? JsonDocument.Parse("{}") : JsonDocument.Parse(bytes.AsMemory(start));
        return (doc, cont);
    }

    /// <summary>Reads <c>value[]</c>, following <c>x-ms-continuationtoken</c> when <paramref name="continuation"/> is set.</summary>
    private async Task<List<JsonElement>> GetValuesAsync(string url, CancellationToken ct, int max = 5000, bool continuation = false)
    {
        var list = new List<JsonElement>();
        string? token = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            var u = token is null ? url : url + "&continuationToken=" + Uri.EscapeDataString(token);
            var (doc, cont) = await GetAsync(u, ct);
            using (doc)
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
                    foreach (var v in value.EnumerateArray()) list.Add(v.Clone());
            }
            token = continuation ? cont : null;
            // A token the server already gave us would loop forever.
            if (token is not null && !seen.Add(token)) { _log.LogWarning("Repeated continuation token for {Url}; stopping", Trim(url)); break; }
        } while (token is not null && list.Count < max);
        return list;
    }

    /// <summary>
    /// $top/$skip paging. Advances by what the server actually returned and stops on an empty page, so a server-side page cap
    /// smaller than <paramref name="page"/> cannot silently truncate the listing.
    /// </summary>
    private async Task<List<JsonElement>> GetSkipPagedAsync(string url, int page, CancellationToken ct, int max = 5000)
    {
        var list = new List<JsonElement>();
        var skip = 0;
        while (list.Count < max)
        {
            var batch = await GetValuesAsync($"{url}&$top={page}&$skip={skip}", ct);
            list.AddRange(batch);
            if (batch.Count == 0 || batch.Count < Math.Min(page, 100)) break;
            skip += batch.Count;
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
                        if (ident.TryGetProperty("memberOf", out var mo) && mo.ValueKind == JsonValueKind.Array)
                            foreach (var d in mo.EnumerateArray())
                                if (Descriptor(d) is { } s) descriptors.Add(s);
                    // An over-long descriptor list comes back as a bare 404 (observed at ~40), so keep batches small
                    // and let one bad batch cost only its own groups.
                    foreach (var chunk in descriptors.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(20))
                    {
                        try
                        {
                            var (gDoc, _) = await GetAsync($"{Vssps}/_apis/identities?descriptors={Uri.EscapeDataString(string.Join(',', chunk))}&{V()}", ct);
                            using (gDoc)
                                if (gDoc.RootElement.ValueKind == JsonValueKind.Object && gDoc.RootElement.TryGetProperty("value", out var gv) && gv.ValueKind == JsonValueKind.Array)
                                    foreach (var g in gv.EnumerateArray())
                                        if (g.ValueKind == JsonValueKind.Object && g.Str("id") is { } gid) groups.Add(gid);
                        }
                        catch (HttpRequestException ex) { _log.LogInformation("Group lookup for {Count} descriptors failed: {Error}", chunk.Length, ex.Message); }
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or KeyNotFoundException or JsonException or InvalidOperationException)
            {
                _log.LogInformation("Group membership lookup unavailable ({Error}); only direct approval assignments will count as mine", ex.Message);
            }
            return new AdoIdentity(id, name, groups);
        }
    }

    /// <summary>IdentityDescriptor serialises as "type;identifier"; accept the object form too.</summary>
    private static string? Descriptor(JsonElement d) => d.ValueKind switch
    {
        JsonValueKind.String => d.GetString(),
        JsonValueKind.Object when d.Str("identityType") is { } t && d.Str("identifier") is { } i => $"{t};{i}",
        _ => null,
    };

    public async Task<string?> GetProjectIdAsync(CancellationToken ct)
    {
        var (doc, _) = await GetAsync($"{Org}/_apis/projects/{Uri.EscapeDataString(Ado.Project)}?{V()}", ct);
        using (doc) return doc.RootElement.Str("id");
    }

    public async Task<IReadOnlyList<Repo>> ListRepositoriesAsync(CancellationToken ct)
    {
        var values = await GetValuesAsync($"{Proj}/_apis/git/repositories?includeHidden=false&{V()}", ct);
        var now = DateTimeOffset.UtcNow;
        foreach (var v in values)
            if (v.Str("id") is { } rid && v.Str("name") is { } rname) _repoNames[rid] = rname;
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
        var values = await GetValuesAsync($"{Proj}/_apis/build/definitions?repositoryId={repoId}&repositoryType=TfsGit&includeAllProperties=true&{V()}", ct, continuation: true);
        var now = DateTimeOffset.UtcNow;
        // includeAllProperties normally returns the full definition (with process); if a YAML definition comes back
        // without it, fetch that definition on its own (daily tier, assigned repos only).
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i].Obj("process") is not null || values[i].Int("id") is not { } defId) continue;
            try
            {
                var (d, _) = await GetAsync($"{Proj}/_apis/build/definitions/{defId}?{V()}", ct);
                using (d) values[i] = d.RootElement.Clone();
            }
            catch (HttpRequestException ex) { _log.LogDebug("Definition {Id} unavailable: {Error}", defId, ex.Message); }
        }
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
            // minTime filters on the time field implied by queryOrder: finish time for completed builds (the sync
            // cursor is a finish time), queue time otherwise (unfinished builds have no finish time and would be dropped).
            var order = q.StatusFilter == "completed" ? "finishTimeAscending" : "queueTimeAscending";
            var url = $"{Proj}/_apis/build/builds?definitions={string.Join(',', chunk)}&queryOrder={order}&$top=500&{V()}";
            if (q.StatusFilter != "all") url += "&statusFilter=" + q.StatusFilter;
            if (q.MinTime is not null) url += "&minTime=" + Iso(q.MinTime.Value);
            var values = await GetValuesAsync(url, ct, q.Max, continuation: true);
            results.AddRange(values.Select(MapBuild).Where(r => r.Id != 0));
        }
        return results;
    }

    public async Task<IReadOnlyList<Run>> ListActiveRunsAsync(CancellationToken ct)
    {
        var values = await GetValuesAsync($"{Proj}/_apis/build/builds?statusFilter=inProgress,notStarted,cancelling,postponed&queryOrder=queueTimeDescending&$top=500&{V()}", ct, 2000, continuation: true);
        return values.Select(MapBuild).Where(r => r.Id != 0).ToList();
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
        var raw = new List<(string Alias, string? Id, string? Name, string? Ref, string? Version)>();
        using (doc)
        {
            if (doc.RootElement.Obj("resources")?.Obj("repositories") is { } repos)
                foreach (var p in repos.EnumerateObject())
                {
                    var repo = p.Value.Obj("repository");
                    // Azure Repos resources only carry {id, type}; GitHub ones carry fullName.
                    raw.Add((p.Name, repo?.Str("id"), repo?.Str("fullName") ?? repo?.Str("name"), p.Value.Str("refName"), p.Value.Str("version")));
                }
        }
        var list = new List<RunResource>();
        foreach (var r in raw)
        {
            var name = r.Name;
            if (name is null && r.Id is not null) name = await RepoNameAsync(r.Id, ct);
            list.Add(new RunResource(r.Alias, name, r.Ref, r.Version));
        }
        return list;
    }

    /// <summary>Repo id → name from the project listing, plus the (possibly cross-project) template repo, looked up once.</summary>
    private async Task<string?> RepoNameAsync(string repoId, CancellationToken ct)
    {
        if (_repoNames.TryGetValue(repoId, out var n)) return n;
        if (!_templateRepoResolved)
        {
            try
            {
                var t = _config.Current.Templates;
                var (doc, _) = await GetAsync($"{Org}/{Uri.EscapeDataString(t.Project)}/_apis/git/repositories/{Uri.EscapeDataString(t.Repository)}?{V()}", ct);
                using (doc)
                    if (doc.RootElement.Str("id") is { } tid) _repoNames[tid] = doc.RootElement.Str("name") ?? t.Repository;
                _templateRepoResolved = true;
            }
            catch (HttpRequestException ex)
            {
                // Only a definitive answer stops further lookups; transient failures retry on the next run.
                _templateRepoResolved = ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden;
                _log.LogDebug("Template repo lookup failed: {Error}", ex.Message);
            }
            if (_repoNames.TryGetValue(repoId, out n)) return n;
        }
        return null;
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

    public async Task<Run?> GetRunAsync(int buildId, CancellationToken ct)
    {
        try
        {
            var (doc, _) = await GetAsync($"{Proj}/_apis/build/builds/{buildId}?{V()}", ct);
            using (doc) { var r = MapBuild(doc.RootElement); return r.Id == 0 ? null : r; }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
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

    private PullRequest MapPr(JsonElement v)
    {
        var reviewers = new List<ReviewerInfo>();
        if (v.TryGetProperty("reviewers", out var rv) && rv.ValueKind == JsonValueKind.Array)
            foreach (var r in rv.EnumerateArray())
                reviewers.Add(new ReviewerInfo { Id = r.Str("id"), DisplayName = r.Str("displayName"), Vote = r.Int("vote") ?? 0, IsRequired = r.Bool("isRequired") ?? false, IsContainer = r.Bool("isContainer") ?? false });
        var repoId = v.Obj("repository")?.Str("id") ?? "";
        var id = v.Int("pullRequestId") ?? 0;
        // PR payloads carry repository {id, name, url, project}; webUrl is not guaranteed, so build it from the name.
        var web = v.Obj("repository")?.Str("webUrl")
                  ?? (v.Obj("repository")?.Str("name") is { } rn
                      ? $"{Org}/{Uri.EscapeDataString(v.Obj("repository")?.Obj("project")?.Str("name") ?? Ado.Project)}/_git/{Uri.EscapeDataString(rn)}"
                      : null);
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
            if (t.Bool("isDeleted") == true) continue;
            var props = t.Obj("properties");
            var threadType = Prop(props, "CodeReviewThreadType");
            if (!t.TryGetProperty("comments", out var comments) || comments.ValueKind != JsonValueKind.Array) continue;
            foreach (var c in comments.EnumerateArray())
            {
                if (c.Bool("isDeleted") == true) continue;
                var time = c.Date("publishedDate") ?? t.Date("publishedDate");
                if (time is null) continue;
                var type = c.Str("commentType");
                var author = c.Obj("author");
                string? aid = author?.Str("id"), aname = author?.Str("displayName");

                if (type == "system")
                {
                    switch (threadType)
                    {
                        case "VoteUpdate":
                        {
                            var (vid, vname) = SystemActor(t, props, "CodeReviewVotedBy", aid, aname);
                            int? vote = int.TryParse(Prop(props, "CodeReviewVoteResult"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var vv) ? vv : null;
                            events.Add(Event(pullRequestId, vid, vname, authorId, PrEventKind.Vote, time.Value, vote));
                            break;
                        }
                        case "RefUpdate":
                        {
                            var (pid, pname) = SystemActor(t, props, "CodeReviewRefUpdatedBy", aid, aname);
                            events.Add(Event(pullRequestId, pid, pname, authorId, PrEventKind.Pushed, time.Value));
                            break;
                        }
                        case "IsDraftUpdate":
                            // CodeReviewIsDraftNowSet = 1 → made a draft; otherwise → published.
                            if (Prop(props, "CodeReviewIsDraftNowSet") is not ("1" or "true" or "True")) published = Earliest(published, time);
                            break;
                        case null when (c.Str("content") ?? "").Contains("published", StringComparison.OrdinalIgnoreCase):
                            published = Earliest(published, time);
                            break;
                    }
                }
                else if (type is "text" or "codeChange")
                {
                    events.Add(Event(pullRequestId, aid, aname, authorId, PrEventKind.Comment, time.Value));
                }
            }
        }
        return new PrEventsResult(events, published);
    }

    /// <summary>
    /// System comments can be authored by a service identity; the real actor is in the thread properties:
    /// {prefix}Identity indexes the thread's identities map, {prefix}TfId / {prefix}DisplayName are the flat form.
    /// </summary>
    private static (string? Id, string? Name) SystemActor(JsonElement thread, JsonElement? props, string prefix, string? authorId, string? authorName)
    {
        var ident = Prop(props, prefix + "Identity") is { } key ? thread.Obj("identities")?.Obj(key) : null;
        return (ident?.Str("id") ?? Prop(props, prefix + "TfId") ?? authorId,
                ident?.Str("displayName") ?? Prop(props, prefix + "DisplayName") ?? authorName);
    }

    private static DateTimeOffset? Earliest(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a < b ? a : b;

    /// <summary>PR thread properties are {"$type": "...", "$value": ...}; the value may be a string or a number.</summary>
    private static string? Prop(JsonElement? props, string name) =>
        props?.Obj(name)?.Str("$value") ?? props?.Str(name);

    private static PrEvent Event(int prId, string? actorId, string? actorName, string? prAuthorId, PrEventKind kind, DateTimeOffset time, int? vote = null) => new()
    {
        PullRequestId = prId,
        ActorId = actorId,
        ActorName = actorName,
        IsAuthor = actorId is not null && string.Equals(actorId, prAuthorId, StringComparison.OrdinalIgnoreCase),
        IsBot = actorName is not null && (actorName.Contains("Build Service", StringComparison.OrdinalIgnoreCase)
                                         || actorName.Contains("[bot]", StringComparison.OrdinalIgnoreCase)
                                         || actorName.Equals("Azure Pipelines", StringComparison.OrdinalIgnoreCase)
                                         || actorName.Equals("Microsoft.VisualStudio.Services.TFS", StringComparison.OrdinalIgnoreCase)),
        Kind = kind,
        Vote = vote,
        Time = time,
    };

    public async Task<string?> GetPolicyStatusAsync(string projectId, int pullRequestId, CancellationToken ct)
    {
        var artifact = Uri.EscapeDataString($"vstfs:///CodeReview/CodeReviewId/{projectId}/{pullRequestId}");
        var values = await GetValuesAsync($"{Proj}/_apis/policy/evaluations?artifactId={artifact}&{V("7.1-preview.1")}", ct);
        var blocking = values
            .Where(v => (v.Obj("configuration")?.Bool("isBlocking") ?? false)
                        && (v.Obj("configuration")?.Bool("isEnabled") ?? true)
                        && !(v.Obj("configuration")?.Bool("isDeleted") ?? false))
            .Select(v => v.Str("status")).ToList();
        if (blocking.Count == 0) return null;
        if (blocking.Contains("rejected") || blocking.Contains("broken")) return "rejected";
        if (blocking.Contains("running") || blocking.Contains("queued")) return "running";
        return blocking.All(s => s is "approved" or "notApplicable") ? "approved" : "running";
    }

    public async Task<string?> GetBranchHeadAsync(string repoId, string branch, CancellationToken ct)
    {
        var name = branch.StartsWith("refs/", StringComparison.Ordinal) ? branch[5..] : "heads/" + branch;
        var values = await GetValuesAsync($"{Proj}/_apis/git/repositories/{repoId}/refs?filter={Uri.EscapeDataString(name)}&{V()}", ct, continuation: true);
        return values.FirstOrDefault(v => string.Equals(v.Str("name"), "refs/" + name, StringComparison.OrdinalIgnoreCase)).Str("objectId");
    }

    public async Task<IReadOnlyList<Commit>> ListCommitsAsync(string repoId, string branch, DateTimeOffset since, CancellationToken ct)
    {
        var b = branch.StartsWith("refs/heads/", StringComparison.Ordinal) ? branch[11..] : branch;
        var values = await GetSkipPagedAsync($"{Proj}/_apis/git/repositories/{repoId}/commits?searchCriteria.itemVersion.version={Uri.EscapeDataString(b)}&searchCriteria.fromDate={Iso(since)}&{V()}", 500, ct, 5000);
        return values.DistinctBy(v => v.Str("commitId")).Select(v => new Commit
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
        values = values.Where(v => v.Bool("isDeleted") != true).ToList();
        // A scope with repositoryId = null is a cross-repo (project-wide) policy and applies to every repo.
        IReadOnlyList<string>? allRepos = null;
        var projectWide = values.Any(v => v.Obj("settings") is { } st && st.TryGetProperty("scope", out var sc) && sc.ValueKind == JsonValueKind.Array
                                          && sc.EnumerateArray().Any(x => x.Str("repositoryId") is null));
        if (projectWide) allRepos = (await ListRepositoriesAsync(ct)).Select(r => r.Id).ToList();

        var list = new List<BranchPolicy>();
        foreach (var v in values)
        {
            var settings = v.Obj("settings");
            if (settings is null || !settings.Value.TryGetProperty("scope", out var scopes) || scopes.ValueKind != JsonValueKind.Array) continue;
            var policyId = v.Int("id") ?? 0;
            var n = 0;
            foreach (var s in scopes.EnumerateArray())
            {
                IReadOnlyList<string> repoIds = s.Str("repositoryId") is { } rid ? [rid] : allRepos ?? [];
                foreach (var repoId in repoIds)
                {
                    list.Add(new BranchPolicy
                    {
                        Id = policyId * 1000 + n++,
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
            if (n > 1000) _log.LogWarning("Policy {Id} expands to {Count} scopes; synthetic ids overlap the next policy", policyId, n);
        }
        return list;
    }

    public async Task<IReadOnlyList<Approval>> ListPendingApprovalsAsync(CancellationToken ct)
    {
        var values = await GetValuesAsync($"{Proj}/_apis/pipelines/approvals?state=pending&$expand=steps&top=500&{V()}", ct, 2000, continuation: true);
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
        var b = branch.StartsWith("refs/heads/", StringComparison.Ordinal) ? branch[11..] : branch;
        var url = $"{Org}/{Uri.EscapeDataString(t.Project)}/_apis/git/repositories/{Uri.EscapeDataString(t.Repository)}/commits?searchCriteria.itemVersion.version={Uri.EscapeDataString(b)}&searchCriteria.fromDate={Iso(since)}&{V()}";
        var values = await GetSkipPagedAsync(url, 500, ct, 2000);
        return values.DistinctBy(v => v.Str("commitId")).Select(v => new TemplateCommit
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
        var values = await GetValuesAsync($"{Vstmr}/_apis/testresults/resultsbybuild?buildId={buildId}&outcomes=Failed,Passed&$top=1000&{V("7.1-preview.1")}", ct, 20000, continuation: true);
        var date = DateTimeOffset.UtcNow;
        // testCaseTitle alone is often just the method name (NUnit/MSTest), which collides across classes, while
        // automatedTestName alone is shared by every case of a data-driven test. Key on storage + name + title.
        return values
            .Select(v => (Key: $"{v.Str("automatedTestStorage")}|{v.Str("automatedTestName")}|{v.Str("testCaseTitle")}", Name: TestName(v), Outcome: v.Str("outcome")))
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Any(x => string.Equals(x.Outcome, "Failed", StringComparison.OrdinalIgnoreCase))
                        && g.Any(x => string.Equals(x.Outcome, "Passed", StringComparison.OrdinalIgnoreCase)))
            .Select(g => new TestFlake { RunId = buildId, RepoId = repoId, TestName = g.First().Name, Outcome = "Passed", Flaky = true, Date = date })
            .ToList();
    }

    private static string TestName(JsonElement v)
    {
        var fqn = v.Str("automatedTestName");
        var title = v.Str("testCaseTitle");
        if (fqn is null) return title ?? "?";
        if (title is null || title == fqn || fqn.EndsWith("." + title, StringComparison.Ordinal)) return fqn;
        return title.StartsWith(fqn, StringComparison.Ordinal) ? title : $"{fqn} ({title})";
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
            ("Approvals", "vso.build", $"{Proj}/_apis/pipelines/approvals?state=pending&top=1&{V()}"),
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

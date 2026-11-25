using System.Net.Http.Json;
using Dashboard.Core.Contracts;
using Dashboard.Web.Ado;
using Dashboard.Web.Services;

namespace Dashboard.Web.Audit;

/// <summary>HTTP client for the auditor container (see Dashboard.Core.Contracts.AuditContracts).</summary>
public sealed class AuditorClient
{
    private readonly HttpClient _http;
    private readonly ConfigService _config;

    public AuditorClient(HttpClient http, ConfigService config, IConfiguration env)
    {
        _http = http;
        _config = config;
        _http.DefaultRequestHeaders.Add("X-Auditor-Token", env["AUDITOR_TOKEN"] is { Length: > 0 } t ? t : "dev-token");
        _http.Timeout = TimeSpan.FromMinutes(10);
    }

    private Uri U(string path) => new(new Uri(_config.Current.Audit.AuditorUrl.TrimEnd('/') + "/"), path.TrimStart('/'));

    public async Task<AuditorStatus> StatusAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            return await _http.GetFromJsonAsync<AuditorStatus>(U("status"), JsonDefaults.Web, cts.Token) ?? new AuditorStatus { Error = "empty response" };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return new AuditorStatus { Engine = "unreachable", Ready = false, Error = $"{_config.Current.Audit.AuditorUrl}: {ex.Message}" };
        }
    }

    public async Task<AuditStatusResponse> StartAsync(AuditRequest request, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync(U("audits"), request, JsonDefaults.Web, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Auditor rejected audit: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(ct)}");
        return await resp.Content.ReadFromJsonAsync<AuditStatusResponse>(JsonDefaults.Web, ct) ?? throw new HttpRequestException("Empty auditor response");
    }

    public async Task<AuditStatusResponse?> GetAsync(string auditId, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(U($"audits/{Uri.EscapeDataString(auditId)}"), ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<AuditStatusResponse>(JsonDefaults.Web, ct);
    }

    public async Task<TextTaskResponse> TextAsync(TextTaskRequest request, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync(U("text"), request, JsonDefaults.Web, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Auditor text task failed: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(ct)}");
        return await resp.Content.ReadFromJsonAsync<TextTaskResponse>(JsonDefaults.Web, ct) ?? new TextTaskResponse { Text = "" };
    }
}

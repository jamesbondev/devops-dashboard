using System.Text.RegularExpressions;
using Dashboard.Auditor.Engines;
using Dashboard.Auditor.Jobs;
using Dashboard.Auditor.Security;
using Dashboard.Core.Contracts;

namespace Dashboard.Auditor;

public static partial class AuditorEndpoints
{
    public const int MaxVotes = 5;
    public const int MaxTimeoutMinutes = 120;

    [GeneratedRegex("^[A-Za-z0-9._:-]{1,128}$")]
    private static partial Regex AuditIdPattern();

    private static readonly string[] TextKinds = ["weekly-brief", "template-summary"];

    public static void MapAuditorEndpoints(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Text("ok"));

        app.MapGet("/status", (IAuditEngine engine, CancellationToken ct) => engine.GetStatusAsync(ct));

        app.MapPost("/audits", (AuditRequest? request, AuditJobStore store, AuditorOptions options) =>
        {
            if (request is null)
            {
                return Error("request body is required");
            }
            if (string.IsNullOrWhiteSpace(request.AuditId))
            {
                request.AuditId = Guid.NewGuid().ToString("n");
            }
            if (!AuditIdPattern().IsMatch(request.AuditId))
            {
                return Error("auditId must be 1-128 characters of [A-Za-z0-9._:-]");
            }
            if (!PathGuard.TryResolveInside(options.AllowedRoot, request.WorktreePath, out var worktree, out var reason))
            {
                return Error($"worktreePath rejected: {reason} (must be under {options.AllowedRoot})");
            }
            if (PathGuard.IsUnder(worktree, options.AllowedRoot)) // worktree is inside the root, so this means "is the root"
            {
                return Error($"worktreePath must be a directory below {options.AllowedRoot}, not the root itself");
            }
            if (!Directory.Exists(worktree))
            {
                return Error($"worktreePath '{request.WorktreePath}' does not exist or is not a directory");
            }

            var ids = request.Checks.Select(c => c.Id?.Trim() ?? "").ToList();
            if (ids.Any(string.IsNullOrEmpty))
            {
                return Error("every check needs an id");
            }
            var duplicate = ids.GroupBy(i => i, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (duplicate is not null)
            {
                return Error($"duplicate check id '{duplicate.Key}'");
            }

            request.WorktreePath = worktree;
            request.Votes = Math.Clamp(request.Votes, 1, MaxVotes);
            request.TimeoutMinutes = Math.Clamp(request.TimeoutMinutes, 1, MaxTimeoutMinutes);
            request.FactsJson = string.IsNullOrWhiteSpace(request.FactsJson) ? "{}" : request.FactsJson;

            var (job, _) = store.Enqueue(request);
            return Results.Accepted($"/audits/{Uri.EscapeDataString(request.AuditId)}", job.ToResponse());
        });

        app.MapGet("/audits/{id}", (string id, AuditJobStore store) =>
            store.Get(id) is { } job ? Results.Ok(job.ToResponse()) : Results.NotFound(new { error = $"unknown audit '{id}'" }));

        app.MapPost("/text", async (TextTaskRequest? request, IAuditEngine engine, ILoggerFactory loggers, CancellationToken ct) =>
        {
            if (request is null)
            {
                return Error("request body is required");
            }
            if (!TextKinds.Contains(request.Kind))
            {
                return Error($"kind must be one of: {string.Join(", ", TextKinds)}");
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(request.TimeoutMinutes, 1, 30)));
            try
            {
                return Results.Ok(await engine.RunTextAsync(request, timeout.Token));
            }
            catch (Exception ex) when (ex is TimeoutException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                return Results.Json(new { error = "text task timed out" }, statusCode: StatusCodes.Status504GatewayTimeout);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                loggers.CreateLogger("Dashboard.Auditor.Text").LogError(ex, "Text task {Kind} failed", request.Kind);
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });
    }

    private static IResult Error(string message) => Results.BadRequest(new { error = message });
}

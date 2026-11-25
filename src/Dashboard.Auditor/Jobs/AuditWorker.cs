using Dashboard.Auditor.Engines;

namespace Dashboard.Auditor.Jobs;

/// <summary>Runs queued audits one at a time and periodically purges expired jobs.</summary>
public sealed class AuditWorker(
    AuditJobStore store,
    IAuditEngine engine,
    ILogger<AuditWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var purgeTimer = new PeriodicTimer(PurgeInterval);
        _ = PurgeLoopAsync(purgeTimer, stoppingToken);

        try
        {
            await foreach (var id in store.Reader.ReadAllAsync(stoppingToken))
            {
                var job = store.Get(id);
                if (job is null)
                {
                    continue;
                }
                await RunOneAsync(job, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunOneAsync(AuditJob job, CancellationToken stoppingToken)
    {
        var request = job.Request;
        store.MarkRunning(job, engine.Name);
        logger.LogInformation("Audit {AuditId} started for {Repo}@{Sha} ({Checks} checks, {Votes} votes)",
            request.AuditId, request.RepoName, request.Sha, request.Checks.Count, request.Votes);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(request.TimeoutMinutes));
        try
        {
            var result = await engine.RunAuditAsync(request, timeout.Token);
            store.MarkSucceeded(job, result.Result, result.Transcript, result.Model, result.PromptHash);
            logger.LogInformation("Audit {AuditId} succeeded with model {Model}", request.AuditId, result.Model);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            store.MarkFailed(job, $"audit timed out after {request.TimeoutMinutes} minutes");
            logger.LogWarning("Audit {AuditId} timed out", request.AuditId);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            store.MarkFailed(job, "auditor is shutting down");
        }
        catch (Exception ex)
        {
            var transcript = ex is AuditEngineException aee ? aee.Transcript : null;
            store.MarkFailed(job, ex.Message, transcript);
            logger.LogError(ex, "Audit {AuditId} failed", request.AuditId);
        }
    }

    private async Task PurgeLoopAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var removed = store.PurgeExpired();
                if (removed > 0)
                {
                    logger.LogInformation("Purged {Count} expired audit jobs", removed);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}

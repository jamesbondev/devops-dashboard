using System.Collections.Concurrent;
using System.Threading.Channels;
using Dashboard.Core.Contracts;

namespace Dashboard.Auditor.Jobs;

/// <summary>One audit job. All mutation goes through the store so status snapshots stay consistent.</summary>
public sealed class AuditJob
{
    internal readonly Lock Gate = new();

    public required AuditRequest Request { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string Status { get; set; } = "queued";
    public string? Error { get; set; }
    public string? Engine { get; set; }
    public string? Model { get; set; }
    public string? PromptHash { get; set; }
    public AuditResult? Result { get; set; }
    public string? Transcript { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public AuditStatusResponse ToResponse()
    {
        lock (Gate)
        {
            return new AuditStatusResponse
            {
                AuditId = Request.AuditId,
                Status = Status,
                Error = Error,
                Engine = Engine,
                Model = Model,
                PromptHash = PromptHash,
                Result = Result,
                Transcript = Transcript,
                StartedAt = StartedAt,
                CompletedAt = CompletedAt,
            };
        }
    }
}

/// <summary>
/// In-memory job state plus the single-consumer queue. Jobs are dropped
/// <see cref="AuditorOptions.JobRetention"/> after they finish (or after they were created,
/// for jobs that somehow never finish).
/// </summary>
public sealed class AuditJobStore
{
    private readonly ConcurrentDictionary<string, AuditJob> _jobs = new(StringComparer.Ordinal);
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TimeProvider _time;
    private readonly TimeSpan _retention;

    public AuditJobStore(AuditorOptions options, TimeProvider time)
    {
        _time = time;
        _retention = options.JobRetention;
    }

    public ChannelReader<string> Reader => _queue.Reader;

    public int Count => _jobs.Count;

    /// <summary>Adds and enqueues a job. Returns the existing job if the id is already known (idempotent POST).</summary>
    public (AuditJob Job, bool Created) Enqueue(AuditRequest request)
    {
        PurgeExpired();
        var job = new AuditJob { Request = request, CreatedAt = _time.GetUtcNow() };
        var stored = _jobs.GetOrAdd(request.AuditId, job);
        if (!ReferenceEquals(stored, job))
        {
            return (stored, false);
        }
        if (!_queue.Writer.TryWrite(request.AuditId))
        {
            _jobs.TryRemove(request.AuditId, out _);
            throw new InvalidOperationException("audit queue is closed");
        }
        return (job, true);
    }

    public AuditJob? Get(string id) => _jobs.TryGetValue(id, out var job) ? job : null;

    public void MarkRunning(AuditJob job, string engine)
    {
        lock (job.Gate)
        {
            job.Status = "running";
            job.Engine = engine;
            job.StartedAt = _time.GetUtcNow();
        }
    }

    public void MarkSucceeded(AuditJob job, AuditResult result, string transcript, string model, string promptHash)
    {
        lock (job.Gate)
        {
            job.Status = "succeeded";
            job.Result = result;
            job.Transcript = transcript;
            job.Model = model;
            job.PromptHash = promptHash;
            job.CompletedAt = _time.GetUtcNow();
        }
    }

    public void MarkFailed(AuditJob job, string error, string? transcript = null)
    {
        lock (job.Gate)
        {
            job.Status = "failed";
            job.Error = error;
            job.Transcript = transcript ?? job.Transcript;
            job.CompletedAt = _time.GetUtcNow();
        }
    }

    public int PurgeExpired()
    {
        var now = _time.GetUtcNow();
        var removed = 0;
        foreach (var (id, job) in _jobs)
        {
            DateTimeOffset reference;
            lock (job.Gate)
            {
                reference = job.CompletedAt ?? job.CreatedAt;
                if (job.Status is "queued" or "running" && now - job.CreatedAt < _retention * 2)
                {
                    continue;
                }
            }
            if (now - reference >= _retention && _jobs.TryRemove(id, out _))
            {
                removed++;
            }
        }
        return removed;
    }

    public void Complete() => _queue.Writer.TryComplete();
}

using Dashboard.Core.Contracts;

namespace Dashboard.Auditor.Engines;

public sealed record AuditEngineResult(AuditResult Result, string Transcript, string Model, string PromptHash);

public interface IAuditEngine
{
    /// <summary>mock | copilot</summary>
    string Name { get; }

    /// <summary>Never throws for "not logged in" or similar; reports it in the status instead.</summary>
    Task<AuditorStatus> GetStatusAsync(CancellationToken cancellationToken);

    Task<AuditEngineResult> RunAuditAsync(AuditRequest request, CancellationToken cancellationToken);

    Task<TextTaskResponse> RunTextAsync(TextTaskRequest request, CancellationToken cancellationToken);
}

/// <summary>An engine failure that still carries a (partial) transcript worth keeping.</summary>
public sealed class AuditEngineException(string message, string? transcript, Exception? inner = null)
    : Exception(message, inner)
{
    public string? Transcript { get; } = transcript;
}

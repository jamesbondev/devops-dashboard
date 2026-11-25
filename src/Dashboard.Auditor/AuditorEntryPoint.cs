namespace Dashboard.Auditor;

/// <summary>
/// Public marker type for <c>WebApplicationFactory&lt;AuditorEntryPoint&gt;</c>. The top-level
/// <c>Program</c> stays internal so it does not clash with Dashboard.Web's in the test project.
/// </summary>
public sealed class AuditorEntryPoint
{
    private AuditorEntryPoint()
    {
    }
}

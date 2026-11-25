using Dashboard.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Dashboard.Web.Data;

public sealed class DashboardDbContext(DbContextOptions<DashboardDbContext> options) : DbContext(options)
{
    public DbSet<Repo> Repos => Set<Repo>();
    public DbSet<Pipeline> Pipelines => Set<Pipeline>();
    public DbSet<Component> Components => Set<Component>();
    public DbSet<Run> Runs => Set<Run>();
    public DbSet<TimelineRecord> TimelineRecords => Set<TimelineRecord>();
    public DbSet<Deployment> Deployments => Set<Deployment>();
    public DbSet<Approval> Approvals => Set<Approval>();
    public DbSet<EnvironmentCheck> EnvironmentChecks => Set<EnvironmentCheck>();
    public DbSet<PullRequest> PullRequests => Set<PullRequest>();
    public DbSet<PrEvent> PrEvents => Set<PrEvent>();
    public DbSet<Commit> Commits => Set<Commit>();
    public DbSet<BranchPolicy> BranchPolicies => Set<BranchPolicy>();
    public DbSet<TestFlake> TestFlakes => Set<TestFlake>();
    public DbSet<TemplateCommit> TemplateCommits => Set<TemplateCommit>();
    public DbSet<RepoFactsRecord> RepoFacts => Set<RepoFactsRecord>();
    public DbSet<AuditRun> AuditRuns => Set<AuditRun>();
    public DbSet<Finding> Findings => Set<Finding>();
    public DbSet<FindingState> FindingStates => Set<FindingState>();
    public DbSet<RuleResult> RuleResults => Set<RuleResult>();
    public DbSet<HealthSnapshot> HealthSnapshots => Set<HealthSnapshot>();
    public DbSet<SyncCursor> SyncCursors => Set<SyncCursor>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<UserState> UserStates => Set<UserState>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        // SQLite can't order/compare DateTimeOffset natively; store as UTC ticks (long) so queries translate.
        b.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();
        b.Properties<DateTimeOffset?>().HaveConversion<NullableUtcTicksConverter>();
    }

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<Repo>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.Name); e.HasIndex(x => x.Assigned); });
        m.Entity<Pipeline>(e => { e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedNever(); e.HasIndex(x => x.RepoId); });
        m.Entity<Component>(e => { e.HasKey(x => x.Id); e.HasIndex(x => new { x.RepoId, x.Name }).IsUnique(); });
        m.Entity<Run>(e =>
        {
            e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => new { x.PipelineId, x.QueueTime }); e.HasIndex(x => new { x.RepoId, x.QueueTime }); e.HasIndex(x => x.Status);
            e.Ignore(x => x.IsPullRequest);
        });
        m.Entity<TimelineRecord>(e => { e.HasKey(x => new { x.RunId, x.RecordId }); e.HasIndex(x => x.ErrorFingerprint); });
        m.Entity<Deployment>(e => { e.HasKey(x => x.Id); e.HasIndex(x => new { x.RepoId, x.EnvironmentName, x.StartTime }); e.HasIndex(x => x.RunId); });
        m.Entity<Approval>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.Status); });
        m.Entity<EnvironmentCheck>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.EnvironmentName); });
        m.Entity<PullRequest>(e =>
        {
            e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => new { x.RepoId, x.Status }); e.HasIndex(x => x.ClosedOn);
        });
        m.Entity<PrEvent>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.PullRequestId); });
        m.Entity<Commit>(e => { e.HasKey(x => new { x.RepoId, x.Sha }); e.HasIndex(x => new { x.RepoId, x.Date }); });
        m.Entity<BranchPolicy>(e => { e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedNever(); e.HasIndex(x => x.RepoId); });
        m.Entity<TestFlake>(e => { e.HasKey(x => x.Id); e.HasIndex(x => new { x.RepoId, x.Date }); });
        m.Entity<TemplateCommit>(e => { e.HasKey(x => new { x.Branch, x.Sha }); });
        m.Entity<RepoFactsRecord>(e => { e.HasKey(x => x.Id); e.HasIndex(x => new { x.RepoId, x.CreatedAt }); });
        m.Entity<AuditRun>(e => { e.HasKey(x => x.Id); e.HasIndex(x => new { x.RepoId, x.CreatedAt }); e.HasIndex(x => x.Status); });
        m.Entity<Finding>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.AuditRunId); e.HasIndex(x => x.Fingerprint); });
        m.Entity<FindingState>(e => { e.HasKey(x => x.Fingerprint); e.HasIndex(x => new { x.RepoId, x.Status }); });
        m.Entity<RuleResult>(e => { e.HasKey(x => x.Id); e.HasIndex(x => new { x.RepoId, x.Date, x.RuleId }).IsUnique(); });
        m.Entity<HealthSnapshot>(e => { e.HasKey(x => x.Id); e.HasIndex(x => new { x.RepoId, x.Date }).IsUnique(); });
        m.Entity<SyncCursor>(e => e.HasKey(x => x.Key));
        m.Entity<Job>(e => { e.HasKey(x => x.Id); e.HasIndex(x => new { x.Status, x.RunAfter }); e.HasIndex(x => x.CreatedAt); });
        m.Entity<UserState>(e => e.HasKey(x => x.Key));
    }

    private sealed class UtcTicksConverter() : ValueConverter<DateTimeOffset, long>(
        v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));

    private sealed class NullableUtcTicksConverter() : ValueConverter<DateTimeOffset?, long?>(
        v => v.HasValue ? v.Value.UtcTicks : null, v => v.HasValue ? new DateTimeOffset(v.Value, TimeSpan.Zero) : null);
}

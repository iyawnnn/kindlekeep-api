using KindleKeep.Api.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace KindleKeep.Api.Infrastructure.Data;

public class KindleDbContext(DbContextOptions<KindleDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<MonitorTarget> MonitorTargets => Set<MonitorTarget>();
    public DbSet<UptimeLog> UptimeLogs => Set<UptimeLog>();
    public DbSet<SecurityAudit> SecurityAudits => Set<SecurityAudit>();
    public DbSet<AlertIncident> AlertIncidents => Set<AlertIncident>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<StatusPage> StatusPages => Set<StatusPage>();
    public DbSet<StatusPageService> StatusPageServices => Set<StatusPageService>();
    public DbSet<JourneyStep> JourneySteps => Set<JourneyStep>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<MonitorTarget>()
            .HasIndex(m => m.PublicSlug)
            .IsUnique();

        modelBuilder.Entity<StatusPage>()
            .HasIndex(p => p.Slug)
            .IsUnique();

        modelBuilder.Entity<StatusPageService>()
            .HasIndex(s => new { s.StatusPageId, s.MonitorId })
            .IsUnique();

        modelBuilder.Entity<JourneyStep>()
            .HasIndex(s => new { s.MonitorId, s.StepOrder })
            .IsUnique();

        // C# property initializers (`= 10`) aren't picked up as SQL column defaults by EF
        // migrations - without this, ALTER TABLE writes DEFAULT 0, which would make every
        // existing user's incidents escalate instantly instead of after 10 minutes.
        modelBuilder.Entity<User>()
            .Property(u => u.EscalationDelayMinutes)
            .HasDefaultValue(10);
    }
}
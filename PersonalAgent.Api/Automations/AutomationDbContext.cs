using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PersonalAgent.Api.Automations;

internal sealed class AutomationDbContext(DbContextOptions<AutomationDbContext> Options) : DbContext(Options)
{
    public DbSet<AutomationDefinition> Automations => Set<AutomationDefinition>();
    public DbSet<AutomationVersion> Versions => Set<AutomationVersion>();
    public DbSet<AutomationRun> Runs => Set<AutomationRun>();
    public DbSet<AutomationStepExecution> Steps => Set<AutomationStepExecution>();
    public DbSet<AutomationReport> Reports => Set<AutomationReport>();

    protected override void OnModelCreating(ModelBuilder Model)
    {
        Model.HasDefaultSchema("automation");
        Model.Entity<AutomationDefinition>(E =>
        {
            E.ToTable("Definitions"); E.HasKey(X => X.Id);
            E.Property(X => X.Name).HasMaxLength(160);
            E.HasIndex(X => new { X.Status, X.NextRunAt });
            E.HasIndex(X => new { X.SubjectProfileId, X.CreatedAt });
        });
        Model.Entity<AutomationVersion>(E =>
        {
            E.ToTable("Versions"); E.HasKey(X => new { X.AutomationId, X.Version });
            E.HasOne<AutomationDefinition>().WithMany().HasForeignKey(X => X.AutomationId).OnDelete(DeleteBehavior.Restrict);
        });
        Model.Entity<AutomationRun>(E =>
        {
            E.ToTable("Runs"); E.HasKey(X => X.CorrelationId);
            E.Property(X => X.CurrentState).HasMaxLength(64);
            E.HasIndex(X => new { X.AutomationId, X.ScheduledAt });
            E.HasOne<AutomationVersion>().WithMany().HasForeignKey(X => new { X.AutomationId, X.Version }).OnDelete(DeleteBehavior.Restrict);
        });
        Model.Entity<AutomationStepExecution>(E =>
        {
            E.ToTable("Steps"); E.HasKey(X => new { X.RunId, X.Index });
            E.HasOne<AutomationRun>().WithMany().HasForeignKey(X => X.RunId).OnDelete(DeleteBehavior.Restrict);
        });
        Model.Entity<AutomationReport>(E =>
        {
            E.ToTable("Reports"); E.HasKey(X => X.Id);
            E.HasIndex(X => new { X.RunId, X.StepIndex }).IsUnique();
            E.HasOne<AutomationRun>().WithMany().HasForeignKey(X => X.RunId).OnDelete(DeleteBehavior.Restrict);
        });
        Model.AddInboxStateEntity();
        Model.AddOutboxMessageEntity();
        Model.AddOutboxStateEntity();
    }
}

// Tooling only: migrations are generated without loading application secrets or starting its hosts.
internal sealed class AutomationDesignTimeFactory : IDesignTimeDbContextFactory<AutomationDbContext>
{
    public AutomationDbContext CreateDbContext(string[] Args) => new(new DbContextOptionsBuilder<AutomationDbContext>()
        .UseNpgsql("Host=localhost;Database=automation_design", O => O.MigrationsHistoryTable("__EFMigrationsHistory", "automation")).Options);
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Production.Domain;
using Scm.Messaging;

namespace Production.Persistence;

public sealed record ProductionAudit(Guid Id, Guid TaskId, Guid OrderId, Guid DecisionId, string Action, DateTimeOffset OccurredAt);
public sealed class ProductionDbContext(DbContextOptions<ProductionDbContext> options) : DbContext(options)
{
    public DbSet<ProductionTask> Tasks => Set<ProductionTask>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.MapMessages();
        model.Entity<ProductionTask>(e =>
        {
            e.ToTable("production_tasks", t => t.HasCheckConstraint("ck_task_version", "\"AcceptedOrderVersion\" > 0 AND \"AcceptedRevision\" > 0"));
            e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedNever(); e.Property(x => x.FactoryName).HasMaxLength(200);
            e.Property<string>("AcceptedContentHash").HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.OrderId).IsUnique(); e.HasIndex(x => x.DecisionId).IsUnique();
            e.HasIndex(x => new { x.OrderId, x.AcceptedOrderVersion }).IsUnique();
            e.HasIndex(x => new { x.FactoryId, x.CreatedAtUtc });
            e.HasMany(x => x.Lines).WithOne().HasForeignKey("TaskId").OnDelete(DeleteBehavior.Cascade);
            e.Navigation(x => x.Lines).HasField("_lines").UsePropertyAccessMode(PropertyAccessMode.Field);
        });
        model.Entity<ProductionTaskLine>(e =>
        {
            e.ToTable("production_task_lines", t => t.HasCheckConstraint("ck_task_line_quantity", "\"ConfirmedQuantity\" > 0"));
            e.HasKey("TaskId", nameof(ProductionTaskLine.LineId)); e.Property(x => x.LineId).ValueGeneratedNever();
            e.Property(x => x.Style).HasMaxLength(100); e.Property(x => x.Color).HasMaxLength(100); e.Property(x => x.Size).HasMaxLength(30);
            e.HasIndex("TaskId", nameof(ProductionTaskLine.SkuId)).IsUnique();
        });
        model.Entity<ProductionAudit>(e =>
        {
            e.ToTable("business_audit"); e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Action).HasMaxLength(100); e.HasIndex(x => new { x.DecisionId, x.Action }).IsUnique();
            e.HasOne<ProductionTask>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
public sealed class ProductionDesignTimeFactory : IDesignTimeDbContextFactory<ProductionDbContext>
{
    public ProductionDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<ProductionDbContext>()
        .UseNpgsql(Environment.GetEnvironmentVariable("ConnectionStrings__Production") ?? "Host=localhost;Database=scm_production;Username=scm_production").Options);
}

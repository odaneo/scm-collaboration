using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Procurement.Domain;

namespace Procurement.Persistence;

public sealed record Factory(Guid Id, string Name, bool Active = true);
public sealed record Sku(Guid Id, string Style, string Color, string Size, bool Active = true);
public sealed record DemoUser(string Id, string Username, string Role, Guid? FactoryId, string PasswordHash);
public sealed record AuditEntry(Guid Id, Guid OrderId, string Action, string SubjectId, Guid FactoryId,
    DateTimeOffset OccurredAt);
public sealed class HttpRequestResult
{
    public string SubjectId { get; set; } = "";
    public string Operation { get; set; } = "CreateDraft";
    public string Key { get; set; } = "";
    public string Hash { get; set; } = "";
    public string ResponseJson { get; set; } = "";
}

public sealed class ProcurementDbContext(DbContextOptions<ProcurementDbContext> options) : DbContext(options)
{
    public DbSet<PurchaseOrder> Orders => Set<PurchaseOrder>();
    public DbSet<Factory> Factories => Set<Factory>();
    public DbSet<Sku> Skus => Set<Sku>();
    public DbSet<DemoUser> Users => Set<DemoUser>();
    public DbSet<AuditEntry> Audit => Set<AuditEntry>();
    public DbSet<HttpRequestResult> Requests => Set<HttpRequestResult>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Factory>(e =>
        {
            e.ToTable("factories"); e.HasKey(x => x.Id); e.Property(x => x.Name).HasMaxLength(200);
        });
        model.Entity<Sku>(e =>
        {
            e.ToTable("skus"); e.HasKey(x => x.Id);
            e.Property(x => x.Style).HasMaxLength(100); e.Property(x => x.Color).HasMaxLength(100);
            e.Property(x => x.Size).HasMaxLength(30); e.HasIndex(x => new { x.Style, x.Color, x.Size }).IsUnique();
        });
        model.Entity<DemoUser>(e =>
        {
            e.ToTable("demo_users"); e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(100); e.Property(x => x.Username).HasMaxLength(100);
            e.HasIndex(x => x.Username).IsUnique(); e.Property(x => x.Role).HasMaxLength(30);
            e.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<PurchaseOrder>(e =>
        {
            e.ToTable("purchase_orders", t => t.HasCheckConstraint("ck_order_revision", "\"Revision\" > 0"));
            e.HasKey(x => x.Id); e.Property(x => x.Status).HasMaxLength(30);
            e.Property(x => x.CreatedBy).HasMaxLength(100);
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey("OrderId").IsRequired().OnDelete(DeleteBehavior.Cascade);
            e.Navigation(x => x.Lines).HasField("_lines").UsePropertyAccessMode(PropertyAccessMode.Field);
            e.HasIndex(x => new { x.CreatedAt, x.Id });
        });
        model.Entity<PurchaseOrderLine>(e =>
        {
            e.ToTable("order_lines", t => t.HasCheckConstraint("ck_line_quantity", "\"Quantity\" > 0"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Quantity).HasConversion(x => x.Value, x => PieceQuantity.From(x));
            e.HasIndex("OrderId", nameof(PurchaseOrderLine.SkuId)).IsUnique();
            e.HasOne<Sku>().WithMany().HasForeignKey(x => x.SkuId).OnDelete(DeleteBehavior.Restrict);
            e.OwnsOne(x => x.Product, p =>
            {
                p.Property(x => x.Style).HasColumnName("Style").HasMaxLength(100);
                p.Property(x => x.Color).HasColumnName("Color").HasMaxLength(100);
                p.Property(x => x.Size).HasColumnName("Size").HasMaxLength(30);
            });
            e.Navigation(x => x.Product).IsRequired();
        });
        model.Entity<AuditEntry>(e =>
        {
            e.ToTable("business_audit"); e.HasKey(x => x.Id);
            e.Property(x => x.SubjectId).HasMaxLength(100); e.Property(x => x.Action).HasMaxLength(100);
            e.HasIndex(x => new { x.OrderId, x.Action }).IsUnique();
            e.HasOne<PurchaseOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<HttpRequestResult>(e =>
        {
            e.ToTable("http_request_results"); e.HasKey(x => new { x.SubjectId, x.Operation, x.Key });
            e.Property(x => x.SubjectId).HasMaxLength(100); e.Property(x => x.Operation).HasMaxLength(100);
            e.Property(x => x.Key).HasMaxLength(128); e.Property(x => x.Hash).HasMaxLength(64);
        });
    }
}

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ProcurementDbContext>
{
    public ProcurementDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<ProcurementDbContext>()
        .UseNpgsql(Environment.GetEnvironmentVariable("ConnectionStrings__Procurement") ??
            "Host=localhost;Database=scm_procurement;Username=scm_procurement").Options);
}

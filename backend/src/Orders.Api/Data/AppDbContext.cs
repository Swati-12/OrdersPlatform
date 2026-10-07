using Microsoft.EntityFrameworkCore;
using Orders.Api.Entities;

namespace Orders.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<Shipment> Shipments => Set<Shipment>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.HasIndex(u => u.Email).IsUnique();
            e.Property(u => u.Email).HasMaxLength(254);
            e.Property(u => u.Role).HasMaxLength(20);
        });

        b.Entity<Product>(e =>
        {
            e.HasIndex(p => p.Sku).IsUnique();
            e.Property(p => p.Sku).HasMaxLength(64);
            e.Property(p => p.Name).HasMaxLength(200);
            e.Property(p => p.Price).HasPrecision(18, 2);
            e.Property(p => p.Version).IsRowVersion();
        });

        b.Entity<Order>(e =>
        {
            e.Property(o => o.Total).HasPrecision(18, 2);
            e.Property(o => o.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(o => o.IdempotencyKey).HasMaxLength(100);
            e.HasIndex(o => new { o.CustomerId, o.IdempotencyKey }).IsUnique(); // idempotency
            e.HasIndex(o => new { o.CustomerId, o.CreatedAtUtc });
            e.Property(o => o.Version).IsRowVersion(); // Npgsql maps uint row versions to xmin
            e.HasMany(o => o.Items).WithOne().HasForeignKey(i => i.OrderId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<OrderItem>(e =>
        {
            e.Property(i => i.UnitPrice).HasPrecision(18, 2);
            e.Property(i => i.ProductName).HasMaxLength(200);
        });

        b.Entity<Payment>(e =>
        {
            e.Property(p => p.Amount).HasPrecision(18, 2);
            e.Property(p => p.Currency).HasMaxLength(3);
            e.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.ProviderReference).HasMaxLength(100);
            e.Property(p => p.FailureReason).HasMaxLength(500);
            e.HasIndex(p => p.OrderId).IsUnique(); // one payment row per order
            e.HasOne<Order>().WithMany().HasForeignKey(p => p.OrderId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.Property(m => m.Type).HasMaxLength(100);
            e.Property(m => m.Payload).HasColumnType("jsonb");
            e.Property(m => m.LastError).HasMaxLength(1000);
            e.HasIndex(m => new { m.ProcessedAtUtc, m.DeadLettered });
        });

        b.Entity<Product>().HasData(
            new Product { Id = Guid.Parse("11111111-1111-1111-1111-111111111111"), Sku = "KB-001", Name = "Mechanical Keyboard", Price = 89.00m, Stock = 500 },
            new Product { Id = Guid.Parse("22222222-2222-2222-2222-222222222222"), Sku = "MS-002", Name = "Wireless Mouse", Price = 39.50m, Stock = 800 },
            new Product { Id = Guid.Parse("33333333-3333-3333-3333-333333333333"), Sku = "MN-003", Name = "27-inch 4K Monitor", Price = 329.99m, Stock = 120 });
    
    b.Entity<Shipment>(e =>
{
    e.HasKey(s => s.Id);

    e.HasIndex(s => s.OrderId)
        .IsUnique();

    e.HasIndex(s => s.TrackingNumber)
        .IsUnique();

    e.Property(s => s.TrackingNumber)
        .HasMaxLength(100)
        .IsRequired();

    e.Property(s => s.Carrier)
        .HasMaxLength(50)
        .IsRequired();

    e.Property(s => s.Status)
        .HasConversion<string>()
        .HasMaxLength(20);

    e.Property(s => s.CreatedAtUtc)
        .IsRequired();
});

    }
}
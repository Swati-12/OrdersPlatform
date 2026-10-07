using Microsoft.EntityFrameworkCore;
using Orders.Api.Data;
using Orders.Api.Entities;

namespace Orders.Api.Repositories;

public interface IUserRepository
{
    Task<User?> GetByEmailAsync(string email, CancellationToken ct);
    Task<bool> EmailExistsAsync(string email, CancellationToken ct);
    Task AddAsync(User user, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}

public interface IOrderRepository
{
    Task<Order?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<Order?> GetByIdempotencyKeyAsync(Guid customerId, string key, CancellationToken ct);
    Task<(List<Order> Items, int Total)> GetPagedAsync(Guid? customerId, int page, int pageSize, CancellationToken ct);
    Task AddAsync(Order order, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}

public interface IProductRepository
{
    Task<List<Product>> GetAllAsync(CancellationToken ct);
    Task<List<Product>> GetByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct);
}

public interface IPaymentRepository
{
    Task<Payment?> GetByOrderIdAsync(Guid orderId, CancellationToken ct);
    Task AddAsync(Payment payment, CancellationToken ct);
}

public interface IOutboxRepository
{
    Task AddAsync(OutboxMessage message, CancellationToken ct);
    Task<List<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}
public interface IShipmentRepository
{
    Task<Shipment?> GetByIdAsync(Guid id, CancellationToken ct);

    Task<Shipment?> GetByOrderIdAsync(
        Guid orderId,
        CancellationToken ct);

    Task AddAsync(
        Shipment shipment,
        CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}

public class UserRepository(AppDbContext db) : IUserRepository
{
    public Task<User?> GetByEmailAsync(string email, CancellationToken ct) =>
        db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

    public Task<bool> EmailExistsAsync(string email, CancellationToken ct) =>
        db.Users.AnyAsync(u => u.Email == email, ct);

    public async Task AddAsync(User user, CancellationToken ct) => await db.Users.AddAsync(user, ct);
    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}

public class OrderRepository(AppDbContext db) : IOrderRepository
{
    public Task<Order?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == id, ct);

    public Task<Order?> GetByIdempotencyKeyAsync(Guid customerId, string key, CancellationToken ct) =>
        db.Orders.Include(o => o.Items).AsNoTracking()
            .FirstOrDefaultAsync(o => o.CustomerId == customerId && o.IdempotencyKey == key, ct);

    public async Task<(List<Order> Items, int Total)> GetPagedAsync(Guid? customerId, int page, int pageSize, CancellationToken ct)
    {
        var q = db.Orders.AsNoTracking();
        if (customerId is { } id) q = q.Where(o => o.CustomerId == id);

        var total = await q.CountAsync(ct);
        var items = await q.Include(o => o.Items).OrderByDescending(o => o.CreatedAtUtc)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }

    public async Task AddAsync(Order order, CancellationToken ct) => await db.Orders.AddAsync(order, ct);
    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}

public class ProductRepository(AppDbContext db) : IProductRepository
{
    public Task<List<Product>> GetAllAsync(CancellationToken ct) =>
        db.Products.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);

    // Tracked on purpose: the service changes stock in the same unit of work as the order.
    public Task<List<Product>> GetByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct) =>
        db.Products.Where(p => ids.Contains(p.Id)).ToListAsync(ct);
}

public class PaymentRepository(AppDbContext db) : IPaymentRepository
{
    public Task<Payment?> GetByOrderIdAsync(Guid orderId, CancellationToken ct) =>
        db.Payments.FirstOrDefaultAsync(p => p.OrderId == orderId, ct);

    public async Task AddAsync(Payment payment, CancellationToken ct) => await db.Payments.AddAsync(payment, ct);
}

public class OutboxRepository(AppDbContext db) : IOutboxRepository
{
    // Written in the same DbContext (same transaction) as the order/payment change.
    public async Task AddAsync(OutboxMessage message, CancellationToken ct) => await db.OutboxMessages.AddAsync(message, ct);

    // Production with several instances: use FOR UPDATE SKIP LOCKED so two workers never take the same row.
    public Task<List<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken ct) =>
        db.OutboxMessages.Where(m => m.ProcessedAtUtc == null && !m.DeadLettered)
            .OrderBy(m => m.CreatedAtUtc).Take(batchSize).ToListAsync(ct);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}

public class ShipmentRepository(AppDbContext db) : IShipmentRepository
{
    public Task<Shipment?> GetByIdAsync(
        Guid id,
        CancellationToken ct) =>
        db.Shipments
            .FirstOrDefaultAsync(s => s.Id == id, ct);

    public Task<Shipment?> GetByOrderIdAsync(
        Guid orderId,
        CancellationToken ct) =>
        db.Shipments
            .FirstOrDefaultAsync(s => s.OrderId == orderId, ct);

    public async Task AddAsync(
        Shipment shipment,
        CancellationToken ct) =>
        await db.Shipments.AddAsync(shipment, ct);

    public Task SaveChangesAsync(
        CancellationToken ct) =>
        db.SaveChangesAsync(ct);
}
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Orders.Api.Auth;
using Orders.Api.Dtos;
using Orders.Api.Entities;
using Orders.Api.Repositories;

namespace Orders.Api.Services;

public interface IOrderService
{
    Task<(OrderResponse Order, bool Created)> CreateAsync(CreateOrderRequest request, string idempotencyKey, CancellationToken ct);
    Task<OrderResponse> GetAsync(Guid id, CancellationToken ct);
    Task<PagedResult<OrderResponse>> ListAsync(Guid? customerId, int page, int pageSize, CancellationToken ct);
    Task<OrderResponse> UpdateStatusAsync(Guid id, UpdateOrderStatusRequest request, CancellationToken ct); // admin
    Task<OrderResponse> CancelAsync(Guid id, CancellationToken ct);                                          // owner or admin
}

public class OrderService(
    IOrderRepository orders,
    IProductRepository products,
    IOutboxRepository outbox,
    ICurrentUser currentUser,
    IValidator<CreateOrderRequest> createValidator,
    IValidator<UpdateOrderStatusRequest> statusValidator,
    ILogger<OrderService> logger) : IOrderService
{
    private static readonly Dictionary<OrderStatus, OrderStatus[]> Transitions = new()
    {
        [OrderStatus.Pending]   = [OrderStatus.Paid, OrderStatus.Cancelled],
        [OrderStatus.Paid]      = [OrderStatus.Shipped, OrderStatus.Cancelled],
        [OrderStatus.Shipped]   = [OrderStatus.Delivered],
        [OrderStatus.Delivered] = [],
        [OrderStatus.Cancelled] = [],
    };

    public async Task<(OrderResponse, bool)> CreateAsync(CreateOrderRequest request, string idempotencyKey, CancellationToken ct)
    {
        await createValidator.ValidateAndThrowAsync(request, ct);
        var customerId = currentUser.Id; // from the JWT, never from the request body

        var existing = await orders.GetByIdempotencyKeyAsync(customerId, idempotencyKey, ct);
        if (existing is not null) return (Map(existing), false);

        var ids = request.Items.Select(i => i.ProductId).ToList();
        var catalog = (await products.GetByIdsAsync(ids, ct)).ToDictionary(p => p.Id);

        var missing = ids.Where(id => !catalog.ContainsKey(id)).ToList();
        if (missing.Count > 0) throw new NotFoundException($"Unknown product(s): {string.Join(", ", missing)}");

        var order = new Order { Id = Guid.NewGuid(), CustomerId = customerId, IdempotencyKey = idempotencyKey };
        foreach (var line in request.Items)
        {
            var product = catalog[line.ProductId];
            if (product.Stock < line.Quantity)
                throw new BusinessRuleException($"Only {product.Stock} of '{product.Name}' left in stock.");

            product.Stock -= line.Quantity;
            order.Items.Add(new OrderItem
            {
                Id = Guid.NewGuid(), ProductId = product.Id, ProductName = product.Name,
                UnitPrice = product.Price, Quantity = line.Quantity
            });
        }
        order.Total = order.Items.Sum(i => i.UnitPrice * i.Quantity);

        try
        {
            await orders.AddAsync(order, ct);
            await outbox.AddAsync(OutboxMessage.Create("OrderCreated",
                new { orderId = order.Id, customerId, total = order.Total }), ct);
            await orders.SaveChangesAsync(ct); // order + stock decrement + outbox row commit atomically
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("Stock changed while placing the order. Please retry.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            var winner = await orders.GetByIdempotencyKeyAsync(customerId, idempotencyKey, ct)
                         ?? throw new ConflictException("Duplicate order request.");
            return (Map(winner), false);
        }

        logger.LogInformation("Order {OrderId} created for customer {CustomerId}, total {Total}", order.Id, customerId, order.Total);
        return (Map(order), true);
    }

    public async Task<OrderResponse> GetAsync(Guid id, CancellationToken ct) =>
        Map(EnsureAccess(await orders.GetByIdAsync(id, ct), id));

    public async Task<PagedResult<OrderResponse>> ListAsync(Guid? customerId, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        // Customers are always scoped to themselves; admins may filter by customer or see everything.
        var scope = currentUser.IsAdmin ? customerId : currentUser.Id;
        var (items, total) = await orders.GetPagedAsync(scope, page, pageSize, ct);
        return new PagedResult<OrderResponse>(items.Select(Map).ToList(), page, pageSize, total);
    }

    public async Task<OrderResponse> UpdateStatusAsync(Guid id, UpdateOrderStatusRequest request, CancellationToken ct)
    {
        await statusValidator.ValidateAndThrowAsync(request, ct);
        return await ChangeStatusAsync(id, request.Status, ct);
    }

    public Task<OrderResponse> CancelAsync(Guid id, CancellationToken ct) =>
        ChangeStatusAsync(id, OrderStatus.Cancelled, ct);

    private async Task<OrderResponse> ChangeStatusAsync(Guid id, OrderStatus newStatus, CancellationToken ct)
    {
        var order = EnsureAccess(await orders.GetByIdAsync(id, ct), id);

        if (!Transitions[order.Status].Contains(newStatus))
            throw new BusinessRuleException($"Cannot move an order from {order.Status} to {newStatus}.");

        if (newStatus == OrderStatus.Cancelled)
        {
            var catalog = (await products.GetByIdsAsync(order.Items.Select(i => i.ProductId), ct)).ToDictionary(p => p.Id);
            foreach (var item in order.Items)
                if (catalog.TryGetValue(item.ProductId, out var p)) p.Stock += item.Quantity; // release stock
        }

        order.Status = newStatus;
        order.UpdatedAtUtc = DateTime.UtcNow;
        await outbox.AddAsync(OutboxMessage.Create("OrderStatusChanged",
            new { orderId = order.Id, status = newStatus.ToString() }), ct);

        try { await orders.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException("The order was modified by someone else. Reload and retry."); }

        return Map(order);
    }

    /// <summary>Non-owners get 404 (not 403) so order IDs can't be probed.</summary>
    private Order EnsureAccess(Order? order, Guid id)
    {
        if (order is null || (!currentUser.IsAdmin && order.CustomerId != currentUser.Id))
            throw new NotFoundException($"Order {id} was not found.");
        return order;
    }

    internal static OrderResponse Map(Order o) => new(o.Id, o.CustomerId, o.Status.ToString(), o.Total,
        o.CreatedAtUtc, o.UpdatedAtUtc,
        o.Items.Select(i => new OrderItemResponse(i.ProductId, i.ProductName, i.UnitPrice, i.Quantity)).ToList());
}
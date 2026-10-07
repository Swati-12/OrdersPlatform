namespace Orders.Api.Dtos;

// Auth
public record RegisterRequest(string Email, string Password);
public record LoginRequest(string Email, string Password);
public record AuthResponse(string AccessToken, DateTime ExpiresAtUtc, string Role);

// Orders (no CustomerId: it comes from the JWT)
public record OrderItemRequest(Guid ProductId, int Quantity);
public record CreateOrderRequest(List<OrderItemRequest> Items);
public record UpdateOrderStatusRequest(Entities.OrderStatus Status);

public record OrderItemResponse(Guid ProductId, string ProductName, decimal UnitPrice, int Quantity);
public record OrderResponse(Guid Id, Guid CustomerId, string Status, decimal Total,
    DateTime CreatedAtUtc, DateTime UpdatedAtUtc, List<OrderItemResponse> Items);
public record ProductResponse(Guid Id, string Sku, string Name, decimal Price, int Stock);
public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public record PaymentResponse(Guid PaymentId, Guid OrderId, decimal Amount, string Currency,
    string Status, string? ProviderReference, DateTime CreatedAtUtc);

    public record ShipmentResponse(
    Guid Id,
    Guid OrderId,
    string TrackingNumber,
    string Carrier,
    string Status,
    DateTime CreatedAtUtc,
    DateTime? ShippedAtUtc,
    DateTime? DeliveredAtUtc,
    DateTime? EstimatedDeliveryDateUtc);

public record UpdateShipmentStatusRequest(
    Entities.ShipmentStatus Status);
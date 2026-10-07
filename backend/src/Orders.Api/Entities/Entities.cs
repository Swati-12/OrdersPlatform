namespace Orders.Api.Entities;

public static class Roles
{
    public const string Customer = "Customer";
    public const string Admin = "Admin";
}

public enum OrderStatus { Pending = 0, Paid = 1, Shipped = 2, Delivered = 3, Cancelled = 4 }
public enum PaymentStatus { Pending = 0, Succeeded = 1, Failed = 2 }

public class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = default!;        // stored lower-case
    public string PasswordHash { get; set; } = default!; // PBKDF2 via PasswordHasher<User>
    public string Role { get; set; } = Roles.Customer;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class Product
{
    public Guid Id { get; set; }
    public string Sku { get; set; } = default!;
    public string Name { get; set; } = default!;
    public decimal Price { get; set; }
    public int Stock { get; set; }
    public uint Version { get; set; } // xmin: prevents overselling under concurrent updates
}

public class Order
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; } // = User.Id from the JWT "sub" claim
    public OrderStatus Status { get; set; } = OrderStatus.Pending;
    public decimal Total { get; set; }
    public string IdempotencyKey { get; set; } = default!;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public uint Version { get; set; } // PostgreSQL xmin, optimistic concurrency
    public List<OrderItem> Items { get; set; } = new();
}

public class OrderItem
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid ProductId { get; set; }
    public string ProductName { get; set; } = default!; // snapshot at purchase time
    public decimal UnitPrice { get; set; }              // snapshot at purchase time
    public int Quantity { get; set; }
}

public class Payment
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;
    public string? ProviderReference { get; set; }
    public string? FailureReason { get; set; }
    public int Attempts { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class OutboxMessage
{
    public Guid Id { get; set; }
    public string Type { get; set; } = default!;
    public string Payload { get; set; } = default!;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAtUtc { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public bool DeadLettered { get; set; }

    public static OutboxMessage Create(string type, object payload) => new()
    {
        Id = Guid.NewGuid(),
        Type = type,
        Payload = System.Text.Json.JsonSerializer.Serialize(payload)
    };
}

public enum ShipmentStatus
{
    Pending,
    Created,
    Shipped,
    InTransit,
    Delivered,
    Cancelled
}

public class Shipment
{
    public Guid Id { get; set; }

    public Guid OrderId { get; set; }

    public string TrackingNumber { get; set; } = string.Empty;

    public string Carrier { get; set; } = string.Empty;

    public ShipmentStatus Status { get; set; } = ShipmentStatus.Pending;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? ShippedAtUtc { get; set; }

    public DateTime? DeliveredAtUtc { get; set; }

    public DateTime? EstimatedDeliveryDateUtc { get; set; }
}
using Orders.Api.Dtos;
using Orders.Api.Entities;
using Orders.Api.Repositories;
using Orders.Api.Shipping;

namespace Orders.Api.Services;

public interface IShippingService
{
    Task<ShipmentResponse> CreateAsync(
        Guid orderId,
        CancellationToken ct);

    Task<ShipmentResponse> GetByOrderIdAsync(
        Guid orderId,
        CancellationToken ct);

    Task<ShipmentResponse> UpdateStatusAsync(
        Guid orderId,
        ShipmentStatus status,
        CancellationToken ct);
}

public class ShippingService(
    IShipmentRepository shipments,
    IShippingProvider provider,
    IOutboxRepository outbox,
    ILogger<ShippingService> logger) : IShippingService
{
    public async Task<ShipmentResponse> CreateAsync(
        Guid orderId,
        CancellationToken ct)
    {
        // Prevent duplicate shipment creation
        var existing =
            await shipments.GetByOrderIdAsync(orderId, ct);

        if (existing is not null)
        {
            return Map(existing);
        }

        // Call shipping provider
        var result =
            await provider.CreateShipmentAsync(
                new ShippingRequest(orderId),
                ct);

        // Create shipment entity
        var shipment = new Shipment
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            TrackingNumber = result.TrackingNumber,
            Carrier = result.Carrier,
            Status = ShipmentStatus.Created,
            CreatedAtUtc = DateTime.UtcNow,
            EstimatedDeliveryDateUtc =
                result.EstimatedDeliveryDateUtc
        };

        await shipments.AddAsync(shipment, ct);

        // Create outbox event
        await outbox.AddAsync(
            OutboxMessage.Create(
                "ShipmentCreated",
                new
                {
                    shipment.Id,
                    shipment.OrderId,
                    shipment.TrackingNumber,
                    shipment.Carrier,
                    Status = shipment.Status.ToString()
                }),
            ct);

        // Save shipment + outbox event together
        await shipments.SaveChangesAsync(ct);

        logger.LogInformation(
            "Shipment created. ShipmentId={ShipmentId}, OrderId={OrderId}, TrackingNumber={TrackingNumber}",
            shipment.Id,
            shipment.OrderId,
            shipment.TrackingNumber);

        return Map(shipment);
    }

    public async Task<ShipmentResponse> GetByOrderIdAsync(
        Guid orderId,
        CancellationToken ct)
    {
        var shipment =
            await shipments.GetByOrderIdAsync(orderId, ct);

        if (shipment is null)
        {
            throw new NotFoundException(
                $"Shipment for order {orderId} was not found.");
        }

        return Map(shipment);
    }

    public async Task<ShipmentResponse> UpdateStatusAsync(
        Guid orderId,
        ShipmentStatus status,
        CancellationToken ct)
    {
        var shipment =
            await shipments.GetByOrderIdAsync(orderId, ct);

        if (shipment is null)
        {
            throw new NotFoundException(
                $"Shipment for order {orderId} was not found.");
        }

        ValidateTransition(
            shipment.Status,
            status);

        shipment.Status = status;

        if (status == ShipmentStatus.Shipped)
        {
            shipment.ShippedAtUtc = DateTime.UtcNow;
        }

        if (status == ShipmentStatus.Delivered)
        {
            shipment.DeliveredAtUtc = DateTime.UtcNow;
        }

        // Publish status change through Outbox
        await outbox.AddAsync(
            OutboxMessage.Create(
                "ShipmentStatusChanged",
                new
                {
                    shipment.Id,
                    shipment.OrderId,
                    Status = shipment.Status.ToString()
                }),
            ct);

        await shipments.SaveChangesAsync(ct);

        logger.LogInformation(
            "Shipment status updated. ShipmentId={ShipmentId}, OrderId={OrderId}, Status={Status}",
            shipment.Id,
            shipment.OrderId,
            shipment.Status);

        return Map(shipment);
    }

    private static void ValidateTransition(
        ShipmentStatus current,
        ShipmentStatus next)
    {
        var valid = current switch
        {
            ShipmentStatus.Created =>
                next is ShipmentStatus.Shipped
                    or ShipmentStatus.Cancelled,

            ShipmentStatus.Shipped =>
                next == ShipmentStatus.InTransit,

            ShipmentStatus.InTransit =>
                next == ShipmentStatus.Delivered,

            _ => false
        };

        if (!valid)
        {
            throw new ConflictException(
                $"Invalid shipment status transition: {current} -> {next}");
        }
    }

    private static ShipmentResponse Map(
        Shipment shipment)
    {
        return new ShipmentResponse(
            shipment.Id,
            shipment.OrderId,
            shipment.TrackingNumber,
            shipment.Carrier,
            shipment.Status.ToString(),
            shipment.CreatedAtUtc,
            shipment.ShippedAtUtc,
            shipment.DeliveredAtUtc,
            shipment.EstimatedDeliveryDateUtc);
    }
}
namespace Orders.Api.Shipping;

public record ShippingRequest(Guid OrderId);

public record ShippingResult(
    string TrackingNumber,
    string Carrier,
    DateTime EstimatedDeliveryDateUtc);

public interface IShippingProvider
{
    Task<ShippingResult> CreateShipmentAsync(
        ShippingRequest request,
        CancellationToken ct);
}
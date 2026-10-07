namespace Orders.Api.Shipping;

public class MockShippingProvider : IShippingProvider
{
    public Task<ShippingResult> CreateShipmentAsync(
        ShippingRequest request,
        CancellationToken ct)
    {
        var trackingNumber =
            $"TRK-{Guid.NewGuid():N}".ToUpperInvariant();

        var result = new ShippingResult(
            trackingNumber,
            "MockCarrier",
            DateTime.UtcNow.AddDays(5));

        return Task.FromResult(result);
    }
}
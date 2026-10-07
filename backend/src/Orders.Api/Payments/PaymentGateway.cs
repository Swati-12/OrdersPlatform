namespace Orders.Api.Payments;

public record PaymentResult(bool Success, string? Reference, string? Error);

public interface IPaymentGateway
{
    /// <summary>orderId doubles as the provider idempotency key (Stripe: Idempotency-Key header).</summary>
    Task<PaymentResult> ChargeAsync(Guid orderId, decimal amount, string currency, CancellationToken ct);
}

/// <summary>Stand-in for Stripe/PayPal. Set Payments:TransientFailureRate (0..1) to simulate outages.</summary>
public class MockPaymentGateway(IConfiguration config, ILogger<MockPaymentGateway> logger) : IPaymentGateway
{
    private readonly double _transientFailureRate = config.GetValue<double>("Payments:TransientFailureRate");

    public async Task<PaymentResult> ChargeAsync(Guid orderId, decimal amount, string currency, CancellationToken ct)
    {
        await Task.Delay(50, ct);

        if (Random.Shared.NextDouble() < _transientFailureRate)
            throw new HttpRequestException("Simulated gateway timeout (503).");

        if (amount > 5000m)
            return new PaymentResult(false, null, "Amount exceeds the demo limit of 5000.");

        logger.LogInformation("Mock charge of {Amount} {Currency} for order {OrderId}", amount, currency, orderId);
        return new PaymentResult(true, $"mock_{Guid.NewGuid():N}", null);
    }
}
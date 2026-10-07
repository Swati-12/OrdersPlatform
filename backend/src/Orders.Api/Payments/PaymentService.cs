using Microsoft.EntityFrameworkCore;
using Npgsql;
using Orders.Api.Auth;
using Orders.Api.Dtos;
using Orders.Api.Entities;
using Orders.Api.Repositories;
using Orders.Api.Services;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Orders.Api.Payments;

public interface IPaymentService
{
    Task<PaymentResponse> PayAsync(Guid orderId, CancellationToken ct);
}

public class PaymentService(
    IOrderRepository orders,
    IPaymentRepository payments,
    IOutboxRepository outbox,
    IPaymentGateway gateway,
    ResiliencePipeline pipeline,
    ICurrentUser currentUser,
    ILogger<PaymentService> logger) : IPaymentService
{
    public async Task<PaymentResponse> PayAsync(Guid orderId, CancellationToken ct)
    {
        var order = await orders.GetByIdAsync(orderId, ct);
        if (order is null || (!currentUser.IsAdmin && order.CustomerId != currentUser.Id))
            throw new NotFoundException($"Order {orderId} was not found.");

        var payment = await payments.GetByOrderIdAsync(orderId, ct);
        if (payment is { Status: PaymentStatus.Succeeded }) return Map(payment); // idempotent: never charge twice

        if (order.Status != OrderStatus.Pending)
            throw new BusinessRuleException($"Only pending orders can be paid (current status: {order.Status}).");

        if (payment is null)
        {
            payment = new Payment { Id = Guid.NewGuid(), OrderId = order.Id, Amount = order.Total };
            await payments.AddAsync(payment, ct);
        }
        payment.Attempts++;
        payment.UpdatedAtUtc = DateTime.UtcNow;

        // The provider is called OUTSIDE any DB transaction, so a slow gateway never holds locks.
        PaymentResult result;
        try
        {
            result = await pipeline.ExecuteAsync(
                token => new ValueTask<PaymentResult>(gateway.ChargeAsync(order.Id, payment.Amount, payment.Currency, token)), ct);
        }
        catch (BrokenCircuitException)
        {
            // Circuit open: fail fast instead of hammering a provider that is down.
            throw new ServiceUnavailableException("Payment provider is temporarily unavailable. Please try again shortly.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutRejectedException)
        {
            await RecordFailureAsync(order, payment, "Provider unreachable after retries", ct);
            throw new ServiceUnavailableException("Payment could not be completed. Please retry.");
        }

        if (!result.Success)
        {
            await RecordFailureAsync(order, payment, result.Error ?? "Declined", ct);
            throw new BusinessRuleException($"Payment declined: {result.Error}");
        }

        payment.Status = PaymentStatus.Succeeded;
        payment.ProviderReference = result.Reference;
        payment.FailureReason = null;
        order.Status = OrderStatus.Paid;
        order.UpdatedAtUtc = DateTime.UtcNow;
        await outbox.AddAsync(OutboxMessage.Create("OrderPaid",
            new { orderId = order.Id, paymentId = payment.Id, amount = payment.Amount }), ct);

        try { await orders.SaveChangesAsync(ct); } // payment + order status + outbox commit together
        catch (DbUpdateConcurrencyException) { throw new ConflictException("The order changed while paying. Reload and retry."); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        { throw new ConflictException("A payment for this order is already in progress."); }

        logger.LogInformation("Order {OrderId} paid, payment {PaymentId}", order.Id, payment.Id);
        return Map(payment);
    }

    private async Task RecordFailureAsync(Order order, Payment payment, string reason, CancellationToken ct)
    {
        payment.Status = PaymentStatus.Failed;
        payment.FailureReason = reason.Length > 500 ? reason[..500] : reason;
        await outbox.AddAsync(OutboxMessage.Create("PaymentFailed",
            new { orderId = order.Id, paymentId = payment.Id, reason }), ct);
        await orders.SaveChangesAsync(ct);
    }

    private static PaymentResponse Map(Payment p) => new(p.Id, p.OrderId, p.Amount, p.Currency,
        p.Status.ToString(), p.ProviderReference, p.CreatedAtUtc);
}
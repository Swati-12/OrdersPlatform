using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orders.Api.Dtos;
using Orders.Api.Payments;
using Orders.Api.Services;

namespace Orders.Api.Controllers;

[ApiController]
[Authorize] // every action requires a valid JWT unless stated otherwise
[Route("api/orders")]
public class OrdersController(IOrderService service, IPaymentService payments) : ControllerBase
{
    /// <summary>Place an order for the authenticated user. Send a unique Idempotency-Key header.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<OrderResponse>> Create(
        [FromBody] CreateOrderRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100)
            return Problem(title: "Idempotency-Key header is required (max 100 chars).", statusCode: StatusCodes.Status400BadRequest);

        var (order, created) = await service.CreateAsync(request, idempotencyKey, ct);
        return created ? CreatedAtAction(nameof(Get), new { id = order.Id }, order) : Ok(order);
    }

    /// <summary>Owner or admin.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<OrderResponse>> Get(Guid id, CancellationToken ct) =>
        Ok(await service.GetAsync(id, ct));

    /// <summary>Customers see their own orders; admins see all (optionally filtered by customerId).</summary>
    [HttpGet]
    public async Task<ActionResult<PagedResult<OrderResponse>>> List(
        [FromQuery] Guid? customerId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        Ok(await service.ListAsync(customerId, page, pageSize, ct));

    /// <summary>Owner or admin can cancel while Pending/Paid.</summary>
    [HttpPost("{id:guid}/cancel")]
    public async Task<ActionResult<OrderResponse>> Cancel(Guid id, CancellationToken ct) =>
        Ok(await service.CancelAsync(id, ct));

    /// <summary>Pay a pending order (owner or admin). Safe to retry: a succeeded payment is returned as-is.</summary>
    [HttpPost("{id:guid}/pay")]
    public async Task<ActionResult<PaymentResponse>> Pay(Guid id, CancellationToken ct) =>
        Ok(await payments.PayAsync(id, ct));

    /// <summary>Admin only: drive the fulfilment lifecycle (Paid, Shipped, Delivered).</summary>
    [HttpPatch("{id:guid}/status")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<OrderResponse>> UpdateStatus(
        Guid id, [FromBody] UpdateOrderStatusRequest request, CancellationToken ct) =>
        Ok(await service.UpdateStatusAsync(id, request, ct));
}
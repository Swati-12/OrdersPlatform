using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orders.Api.Dtos;
using Orders.Api.Services;

namespace Orders.Api.Controllers;

[ApiController]
[Route("api/orders/{orderId:guid}/shipment")]
[Authorize]
public class ShipmentsController(
    IShippingService shipping) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ShipmentResponse>> Get(
        Guid orderId,
        CancellationToken ct)
    {
        return Ok(
            await shipping.GetByOrderIdAsync(
                orderId,
                ct));
    }

    [HttpPost]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<ShipmentResponse>> Create(
        Guid orderId,
        CancellationToken ct)
    {
        return Ok(
            await shipping.CreateAsync(
                orderId,
                ct));
    }

    [HttpPatch("status")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<ShipmentResponse>> UpdateStatus(
        Guid orderId,
        [FromBody] UpdateShipmentStatusRequest request,
        CancellationToken ct)
    {
        return Ok(
            await shipping.UpdateStatusAsync(
                orderId,
                request.Status,
                ct));
    }
}
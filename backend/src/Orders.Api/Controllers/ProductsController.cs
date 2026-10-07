using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orders.Api.Dtos;
using Orders.Api.Services;

namespace Orders.Api.Controllers;

[ApiController]
[AllowAnonymous] // browsing the catalog needs no login
[Route("api/products")]
public class ProductsController(IProductService service) : ControllerBase
{
    [HttpGet]
    [ResponseCache(Duration = 60)]
    public async Task<ActionResult<IReadOnlyList<ProductResponse>>> List(CancellationToken ct) =>
        Ok(await service.ListAsync(ct));
}
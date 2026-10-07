using Orders.Api.Dtos;
using Orders.Api.Repositories;

namespace Orders.Api.Services;

public interface IProductService
{
    Task<IReadOnlyList<ProductResponse>> ListAsync(CancellationToken ct);
}

public class ProductService(IProductRepository products) : IProductService
{
    public async Task<IReadOnlyList<ProductResponse>> ListAsync(CancellationToken ct) =>
        (await products.GetAllAsync(ct)).Select(p => new ProductResponse(p.Id, p.Sku, p.Name, p.Price, p.Stock)).ToList();
}
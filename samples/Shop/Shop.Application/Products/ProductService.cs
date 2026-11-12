using Shop.Application.Abstractions;
using Shop.Domain;

namespace Shop.Application.Products;

public sealed record ProductDto(int Id, string Name, decimal Price, int Stock);

public sealed record CreateProduct(string Name, decimal Price, int Stock);

public interface IProductService
{
    Task<IReadOnlyList<ProductDto>> ListAsync(CancellationToken ct);
    Task<ProductDto?> GetAsync(int id, CancellationToken ct);
    Task<ProductDto> CreateAsync(CreateProduct input, CancellationToken ct);
}

public sealed class ProductService(IProductRepository products) : IProductService
{
    public async Task<IReadOnlyList<ProductDto>> ListAsync(CancellationToken ct)
    {
        var items = await products.ListAsync(ct);
        return items.Select(ToDto).ToList();
    }

    public async Task<ProductDto?> GetAsync(int id, CancellationToken ct)
    {
        var product = await products.FindAsync(id, ct);
        return product is null ? null : ToDto(product);
    }

    public async Task<ProductDto> CreateAsync(CreateProduct input, CancellationToken ct)
    {
        var product = new Product { Name = input.Name, Price = input.Price, Stock = input.Stock };
        await products.AddAsync(product, ct);
        await products.SaveAsync(ct);
        return ToDto(product);
    }

    private static ProductDto ToDto(Product p) => new(p.Id, p.Name, p.Price, p.Stock);
}

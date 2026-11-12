using Microsoft.EntityFrameworkCore;
using Shop.Application.Abstractions;
using Shop.Domain;

namespace Shop.Infrastructure.Persistence;

public sealed class OrderRepository(ShopDbContext db) : IOrderRepository
{
    public Task<Order?> FindAsync(int id, CancellationToken ct) =>
        db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, ct);

    public async Task AddAsync(Order order, CancellationToken ct) => await db.Orders.AddAsync(order, ct);

    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}

public sealed class ProductRepository(ShopDbContext db) : IProductRepository
{
    public async Task<IReadOnlyList<Product>> ListAsync(CancellationToken ct) =>
        await db.Products.OrderBy(p => p.Name).ToListAsync(ct);

    public Task<Product?> FindAsync(int id, CancellationToken ct) =>
        db.Products.FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task AddAsync(Product product, CancellationToken ct) => await db.Products.AddAsync(product, ct);

    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}

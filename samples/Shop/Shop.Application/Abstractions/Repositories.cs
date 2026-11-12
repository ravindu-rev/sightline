using Shop.Domain;

namespace Shop.Application.Abstractions;

public interface IOrderRepository
{
    Task<Order?> FindAsync(int id, CancellationToken ct);
    Task AddAsync(Order order, CancellationToken ct);
    Task SaveAsync(CancellationToken ct);
}

public interface IProductRepository
{
    Task<IReadOnlyList<Product>> ListAsync(CancellationToken ct);
    Task<Product?> FindAsync(int id, CancellationToken ct);
    Task AddAsync(Product product, CancellationToken ct);
    Task SaveAsync(CancellationToken ct);
}

public interface IPaymentGateway
{
    Task<bool> ChargeAsync(string email, decimal amount, CancellationToken ct);
}

using Shop.Application.Abstractions;
using Shop.Application.Messaging;
using Shop.Domain;

namespace Shop.Application.Orders;

public sealed record PlaceOrder(string CustomerEmail, IReadOnlyList<PlaceOrderLine> Lines) : IRequest<OrderDto>;

public sealed record PlaceOrderLine(int ProductId, int Quantity);

public sealed class PlaceOrderHandler(
    IOrderRepository orders,
    IProductRepository products,
    IPaymentGateway payments) : IRequestHandler<PlaceOrder, OrderDto>
{
    public async Task<OrderDto> Handle(PlaceOrder request, CancellationToken ct)
    {
        var order = new Order { CustomerEmail = request.CustomerEmail, CreatedUtc = DateTime.UtcNow };

        foreach (var line in request.Lines)
        {
            var product = await products.FindAsync(line.ProductId, ct)
                ?? throw new InvalidOperationException($"Product {line.ProductId} not found.");
            product.Reserve(line.Quantity);
            order.Lines.Add(new OrderLine { ProductId = product.Id, Quantity = line.Quantity, UnitPrice = product.Price });
        }

        if (await payments.ChargeAsync(order.CustomerEmail, order.Total, ct))
            order.MarkPaid();

        await orders.AddAsync(order, ct);
        await orders.SaveAsync(ct);
        return OrderDto.From(order);
    }
}

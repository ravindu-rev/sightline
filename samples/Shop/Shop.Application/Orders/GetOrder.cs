using Shop.Application.Abstractions;
using Shop.Application.Messaging;

namespace Shop.Application.Orders;

public sealed record GetOrder(int Id) : IRequest<OrderDto?>;

public sealed class GetOrderHandler(IOrderRepository orders) : IRequestHandler<GetOrder, OrderDto?>
{
    public async Task<OrderDto?> Handle(GetOrder request, CancellationToken ct)
    {
        var order = await orders.FindAsync(request.Id, ct);
        return order is null ? null : OrderDto.From(order);
    }
}

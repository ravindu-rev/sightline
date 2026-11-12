using Shop.Domain;

namespace Shop.Application.Orders;

public sealed record OrderDto(int Id, string CustomerEmail, string Status, decimal Total, IReadOnlyList<OrderLineDto> Lines)
{
    public static OrderDto From(Order order) => new(
        order.Id,
        order.CustomerEmail,
        order.Status.ToString(),
        order.Total,
        order.Lines.Select(l => new OrderLineDto(l.ProductId, l.Quantity, l.UnitPrice)).ToList());
}

public sealed record OrderLineDto(int ProductId, int Quantity, decimal UnitPrice);

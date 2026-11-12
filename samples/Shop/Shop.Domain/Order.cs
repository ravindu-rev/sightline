namespace Shop.Domain;

public class Order
{
    public int Id { get; set; }
    public string CustomerEmail { get; set; } = "";
    public OrderStatus Status { get; private set; } = OrderStatus.Pending;
    public DateTime CreatedUtc { get; set; }
    public List<OrderLine> Lines { get; set; } = [];

    public decimal Total => Lines.Sum(l => l.UnitPrice * l.Quantity);

    public void MarkPaid() => Status = OrderStatus.Paid;

    public void Cancel()
    {
        if (Status == OrderStatus.Shipped)
            throw new InvalidOperationException("Shipped orders cannot be cancelled.");
        Status = OrderStatus.Cancelled;
    }
}

public class OrderLine
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public int ProductId { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}

public enum OrderStatus
{
    Pending,
    Paid,
    Shipped,
    Cancelled
}

namespace Shop.Domain;

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public int Stock { get; set; }

    public void Reserve(int quantity)
    {
        if (quantity > Stock)
            throw new InvalidOperationException($"Only {Stock} of '{Name}' left.");
        Stock -= quantity;
    }
}

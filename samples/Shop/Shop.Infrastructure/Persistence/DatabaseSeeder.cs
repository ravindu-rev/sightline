using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shop.Domain;

namespace Shop.Infrastructure.Persistence;

public static class DatabaseSeeder
{
    public static async Task SeedAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
        if (await db.Products.AnyAsync()) return;

        db.Products.AddRange(
            new Product { Name = "Keyboard", Price = 79.90m, Stock = 25 },
            new Product { Name = "Mouse", Price = 34.50m, Stock = 60 },
            new Product { Name = "Monitor", Price = 249.00m, Stock = 8 });
        await db.SaveChangesAsync();
    }
}

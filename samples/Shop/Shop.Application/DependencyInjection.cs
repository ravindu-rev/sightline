using Microsoft.Extensions.DependencyInjection;
using Shop.Application.Messaging;
using Shop.Application.Orders;
using Shop.Application.Products;

namespace Shop.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<ISender, Sender>();
        services.AddScoped<IRequestHandler<PlaceOrder, OrderDto>, PlaceOrderHandler>();
        services.AddScoped<IRequestHandler<GetOrder, OrderDto?>, GetOrderHandler>();
        services.AddScoped<IProductService, ProductService>();
        return services;
    }
}

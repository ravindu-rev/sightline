using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shop.Application.Abstractions;
using Shop.Infrastructure.Background;
using Shop.Infrastructure.Payments;
using Shop.Infrastructure.Persistence;

namespace Shop.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<ShopDbContext>(o => o.UseInMemoryDatabase("shop"));
        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<IProductRepository, ProductRepository>();

        services.Configure<PaymentOptions>(configuration.GetSection("Payments"));
        services.AddHttpClient<IPaymentGateway, PaymentGatewayClient>((sp, client) =>
        {
            client.BaseAddress = new Uri(sp.GetRequiredService<IOptions<PaymentOptions>>().Value.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(3);
        });

        services.AddHostedService<OutboxWorker>();
        return services;
    }
}

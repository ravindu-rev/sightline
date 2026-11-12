using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shop.Application.Abstractions;

namespace Shop.Infrastructure.Payments;

public sealed class PaymentOptions
{
    public string BaseUrl { get; set; } = "https://payments.example.com/";
    public string Currency { get; set; } = "USD";
}

public sealed class PaymentGatewayClient(
    HttpClient http,
    IOptions<PaymentOptions> options,
    ILogger<PaymentGatewayClient> logger) : IPaymentGateway
{
    public async Task<bool> ChargeAsync(string email, decimal amount, CancellationToken ct)
    {
        try
        {
            var response = await http.PostAsJsonAsync("charges", new { email, amount, currency = options.Value.Currency }, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("Payment gateway unreachable ({Reason}); order stays pending.", ex.Message);
            return false;
        }
    }
}

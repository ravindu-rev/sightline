using Microsoft.Extensions.DependencyInjection;

namespace Shop.Application.Messaging;

public sealed class Sender(IServiceProvider services) : ISender
{
    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default)
    {
        var handlerType = typeof(IRequestHandler<,>).MakeGenericType(request.GetType(), typeof(TResponse));
        dynamic handler = services.GetRequiredService(handlerType);
        return handler.Handle((dynamic)request, ct);
    }
}

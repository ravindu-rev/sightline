using Shop.Application.Abstractions;
using Shop.Application.Messaging;
using Shop.Application.Orders;

namespace Shop.Api.Endpoints;

public static class OrderEndpoints
{
    public static RouteGroupBuilder MapOrderEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/", async (PlaceOrder command, ISender sender, CancellationToken ct) =>
        {
            var order = await sender.Send(command, ct);
            return Results.Created($"/api/orders/{order.Id}", order);
        });

        group.MapGet("/{id:int}", GetById);

        group.MapDelete("/{id:int}", async (int id, IOrderRepository orders, CancellationToken ct) =>
        {
            var order = await orders.FindAsync(id, ct);
            if (order is null) return Results.NotFound();

            order.Cancel();
            await orders.SaveAsync(ct);
            return Results.NoContent();
        });

        return group;
    }

    private static async Task<IResult> GetById(int id, ISender sender, CancellationToken ct)
    {
        var order = await sender.Send(new GetOrder(id), ct);
        return order is null ? Results.NotFound() : Results.Ok(order);
    }
}

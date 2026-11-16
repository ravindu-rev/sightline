using Co.Revington.Sightline.Commands;
using Co.Revington.Sightline.Live;
using Co.Revington.Sightline.Settings;
using MapGraph = Co.Revington.Sightline.Analysis.Graph;

namespace Co.Revington.Sightline.Tests;

/// The samples folder holds the C# Shop and the JavaScript and TypeScript code that calls it.
public sealed class MixedFixture : IAsyncLifetime
{
    public string Samples { get; } = Path.Combine(ShopFixture.FindRepoRoot(), "samples");
    internal MapGraph Map { get; private set; } = null!;
    public Graph View { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var (session, snapshot) = await OneShot.LoadAsync(new SolutionTarget(Samples, null, ProjectFilter.None, Restore: true));
        using (session)
        {
            Map = snapshot.Graph!;
            View = Graph.Parse(snapshot.ToJson(session.StatusJson));
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

/// One map for code in several languages, joined where one calls the other over HTTP.
public sealed class MixedSolutionTests(MixedFixture mixed) : IClassFixture<MixedFixture>
{
    [Fact]
    public void Csharp_and_npm_projects_share_one_map()
    {
        Assert.Equal(9, mixed.View.Names("project").Count);
        Assert.Contains("Shop.Api", mixed.View.Names("project"));
        Assert.Contains("@shop/web", mixed.View.Names("project"));
    }

    [Fact]
    public void A_request_from_the_web_client_runs_on_into_the_csharp_handler_and_its_database()
    {
        var g = mixed.View;
        Assert.True(g.HasEdge("http", "client.placeOrder", "POST /api/orders"));

        var placeOrder = g.Nodes.Single(n => n.Kind == "member" && g.Label(n.Id) == "client.placeOrder");
        var reached = g.Reachable(placeOrder.Id);
        Assert.Contains("PlaceOrderHandler.Handle", reached);
        Assert.Contains("ShopDbContext", reached);
    }

    [Fact]
    public void Requests_reach_the_one_endpoint_they_fit_and_others_stay_outbound()
    {
        var g = mixed.View;
        Assert.True(g.HasEdge("http", "OrdersClient.find", "GET /api/orders/{id:int}"));         // NestJS to C#, address from settings
        Assert.True(g.HasEdge("http", "sync.syncProducts", "GET /api/Products"));                // CommonJS and axios to C#
        Assert.True(g.HasEdge("http", "sync.syncProducts", "POST /api/Products"));
        Assert.True(g.HasEdge("http", "client.trackShipment", "GET /api/shipments/:id"));        // TypeScript to Express
        Assert.True(g.HasEdge("http", "CarrierClient.rate", "HTTP (outbound)"));                  // a host outside the code
        Assert.True(g.HasEdge("http", "PaymentGatewayClient.ChargeAsync", "HTTP (outbound)"));    // C# to a path no endpoint has
    }

    [Fact]
    public async Task Languages_in_the_settings_limit_what_is_read()
    {
        var options = new LoadOptions(ProjectFilter.None, Restore: false, Watch: false, Log: TextWriter.Null, Languages: ["csharp"]);
        using var session = new LiveSession(mixed.Samples, new EventHub(), options);
        var snapshot = await session.LoadOnceAsync(CancellationToken.None);

        Assert.Equal(["Shop.Api", "Shop.Application", "Shop.Domain", "Shop.Infrastructure"],
            snapshot.Graph!.Nodes.Values.Where(n => n.Kind == "project").Select(n => n.Name).Order());
    }
}

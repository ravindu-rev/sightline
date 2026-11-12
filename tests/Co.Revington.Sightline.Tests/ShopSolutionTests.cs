using System.Net;
using System.Net.Http.Json;

namespace Co.Revington.Sightline.Tests;

/// End-to-end checks against the bundled Shop sample: what the analysis finds,
/// what the HTTP API allows, and that live edits reach the graph.
public sealed class ShopSolutionTests(ShopFixture shop) : IClassFixture<ShopFixture>
{
    [Fact]
    public async Task Finds_every_project_and_how_they_reference_each_other()
    {
        var graph = await shop.GetGraphAsync();

        string[] projects = ["Shop.Api", "Shop.Application", "Shop.Domain", "Shop.Infrastructure"];
        Assert.Equal(projects, graph.Names("project"));
        Assert.True(graph.HasEdge("projectRef", "Shop.Api", "Shop.Application"));
        Assert.True(graph.HasEdge("projectRef", "Shop.Infrastructure", "Shop.Application"));
        Assert.True(graph.HasEdge("projectRef", "Shop.Application", "Shop.Domain"));
        Assert.Contains("Microsoft.EntityFrameworkCore.InMemory", graph.Names("package"));
    }

    [Fact]
    public async Task Finds_every_endpoint_with_its_full_route()
    {
        var graph = await shop.GetGraphAsync();

        // Controller actions, minimal API lambdas, a method-group handler,
        // and a route prefix that is passed into an extension method.
        string[] expected =
        [
            "DELETE /api/orders/{id:int}",
            "GET /api/Products",
            "GET /api/Products/{id:int}",
            "GET /api/orders/{id:int}",
            "GET /health",
            "POST /api/Products",
            "POST /api/orders",
        ];
        Assert.Equal(expected.Order(StringComparer.Ordinal), graph.Names("endpoint"));
    }

    [Fact]
    public async Task Reads_dependency_injection_registrations()
    {
        var graph = await shop.GetGraphAsync();

        Assert.Equal(10, graph.Services.Count);
        Assert.Contains(new GraphService("IOrderRepository", "OrderRepository", "scoped"), graph.Services);
        Assert.Contains(new GraphService("IPaymentGateway", "PaymentGatewayClient", "httpclient"), graph.Services);
        Assert.Contains(new GraphService("OutboxWorker", "OutboxWorker", "hosted"), graph.Services);
        Assert.Contains(new GraphService("PaymentOptions", "PaymentOptions", "options"), graph.Services);
    }

    [Fact]
    public async Task Traces_a_request_from_the_route_to_the_database_and_outbound_http()
    {
        var graph = await shop.GetGraphAsync();
        var reached = graph.Reachable(graph.Endpoint("POST /api/orders").Id);

        // The message sent by the endpoint lands in its handler.
        Assert.True(graph.HasEdge("dispatches", "POST /api/orders", "PlaceOrderHandler.Handle"));

        // Calls on interfaces continue in the registered implementation.
        Assert.Contains("OrderRepository.SaveAsync", reached);
        Assert.Contains("PaymentGatewayClient.ChargeAsync", reached);

        // The flow ends at the database and at the payment API.
        Assert.Contains("ShopDbContext", reached);
        Assert.Contains("HTTP (outbound)", reached);
    }

    [Fact]
    public async Task Api_rejects_calls_without_the_page_token_and_files_outside_the_solution()
    {
        var noToken = await shop.Anonymous.GetAsync("/api/graph");
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);

        var outside = Uri.EscapeDataString(typeof(ShopSolutionTests).Assembly.Location);
        var foreignFile = await shop.Http.GetAsync("/api/source?file=" + outside);
        Assert.Equal(HttpStatusCode.NotFound, foreignFile.StatusCode);
    }

    [Fact]
    public async Task Page_carries_the_token_and_a_script_policy()
    {
        var response = await shop.Anonymous.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains($"content=\"{ShopFixture.Token}\"", html);
        Assert.Contains("script-src 'self'", string.Join(' ', response.Headers.GetValues("Content-Security-Policy")));
    }

    [Fact]
    public async Task Unsaved_editor_text_updates_the_graph_and_leaves_the_file_alone()
    {
        var file = Path.Combine(shop.SampleDir, "Shop.Application", "Products", "ProductService.cs");
        var query = "?file=" + Uri.EscapeDataString(file);
        var onDisk = await File.ReadAllTextAsync(file);

        var source = await shop.Http.GetFromJsonAsync<SourceText>("/api/source" + query);
        const string anchor = "var product = await products.FindAsync(id, ct);";
        Assert.Contains(anchor, source!.Text);

        // Type one more call into GetAsync, the way the in-page editor sends it.
        var edited = source.Text.Replace(anchor, anchor + "\n        await products.ListAsync(ct);");
        (await shop.Http.PostAsJsonAsync("/api/overlay", new { file, text = edited })).EnsureSuccessStatusCode();
        try
        {
            await shop.Eventually(g => g.HasEdge("calls", "ProductService.GetAsync", "IProductRepository.ListAsync"));
            Assert.Equal(onDisk, await File.ReadAllTextAsync(file));
        }
        finally
        {
            (await shop.Http.DeleteAsync("/api/overlay" + query)).EnsureSuccessStatusCode();
        }

        // Discarding the unsaved text takes the call out again.
        await shop.Eventually(g => !g.HasEdge("calls", "ProductService.GetAsync", "IProductRepository.ListAsync"));
    }
}

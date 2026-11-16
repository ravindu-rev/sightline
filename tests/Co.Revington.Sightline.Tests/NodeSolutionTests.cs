using System.Diagnostics;
using Co.Revington.Sightline.Analysis;
using Co.Revington.Sightline.Live;

namespace Co.Revington.Sightline.Tests;

/// Loads the JavaScript and TypeScript sample (samples/shop-node) once, as it is in a fresh clone:
/// nothing installed, nothing built. Reading it needs Node.js on the machine.
public sealed class NodeFixture : IAsyncLifetime
{
    readonly CancellationTokenSource _stop = new();
    Task? _loop;

    public string RepoRoot { get; } = ShopFixture.FindRepoRoot();
    public string SampleDir => Path.Combine(RepoRoot, "samples", "shop-node");
    internal LiveSession Session { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Session = new LiveSession(SampleDir, new EventHub());
        _loop = Task.Run(() => Session.RunAsync(_stop.Token));
        var waited = Stopwatch.StartNew();
        while (Session.Status.State == "loading")
        {
            if (waited.Elapsed > TimeSpan.FromMinutes(3)) throw new TimeoutException("The JavaScript sample did not load: " + Session.Status.Message);
            await Task.Delay(100);
        }
        if (Session.Status.State != "ready") throw new InvalidOperationException("The JavaScript sample did not load: " + Session.Status.Message);
    }

    public async Task DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null) await _loop;
        Session.Dispose();
    }

    public Graph GetGraph() => Graph.Parse(Session.GraphJson());

    public async Task<Graph> Eventually(Func<Graph, bool> condition)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            var graph = GetGraph();
            if (condition(graph)) return graph;
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(20), "The graph did not reach the expected state in time.");
            await Task.Delay(100);
        }
    }
}

/// What the TypeScript compiler finds in an npm workspace with Express, NestJS, CommonJS and React.
public sealed class NodeSolutionTests(NodeFixture node) : IClassFixture<NodeFixture>
{
    [Fact]
    public void Finds_every_package_how_they_use_each_other_and_what_they_install()
    {
        var graph = node.GetGraph();

        Assert.Equal(["@shop/admin", "@shop/shared", "@shop/shipping-api", "@shop/web", "@shop/worker"], graph.Names("project"));
        Assert.True(graph.HasEdge("projectRef", "@shop/shipping-api", "@shop/shared"));
        Assert.True(graph.HasEdge("projectRef", "@shop/web", "@shop/shared"));
        Assert.True(graph.HasEdge("packageRef", "@shop/shipping-api", "express"));
        Assert.Contains(graph.Nodes, n => n.Id == "pkg:npm:express");

        var projects = node.Session.Snapshot.Graph!.Nodes.Values.Where(n => n.Kind == "project").ToDictionary(n => n.Name);
        Assert.Equal("TypeScript", projects["@shop/web"].Meta["language"]);
        Assert.Equal("JavaScript", projects["@shop/worker"].Meta["language"]);
        Assert.Equal("ui", projects["@shop/web"].Meta["projectKind"]);
        Assert.Equal("web", projects["@shop/admin"].Meta["projectKind"]);
    }

    [Fact]
    public void Finds_express_and_nest_endpoints_with_the_prefixes_they_are_mounted_under()
    {
        string[] expected =
        [
            "GET /admin/reports", "GET /admin/reports/:id", "GET /api/shipments", "GET /api/shipments/:id", "GET /health",
            "POST /admin/reports", "POST /api/quotes", "POST /api/shipments",
        ];
        Assert.Equal(expected.Order(StringComparer.Ordinal), node.GetGraph().Names("endpoint"));
    }

    [Fact]
    public void Reads_nest_providers_with_their_scopes_and_tokens()
    {
        var graph = node.GetGraph();

        Assert.Contains(new GraphService("ReportsService", "ReportsService", "singleton"), graph.Services);
        Assert.Contains(new GraphService("AuditLog", "AuditLog", "request"), graph.Services);
        Assert.Contains(new GraphService("REPORT_STORE", "InMemoryReportStore", "singleton"), graph.Services);
        Assert.True(graph.HasEdge("injects", "ReportsService", "AuditLog"));
        Assert.Empty(node.Session.Snapshot.Graph!.Problems);
    }

    [Fact]
    public void Traces_a_request_through_an_interface_into_another_package_the_database_and_outbound_http()
    {
        var graph = node.GetGraph();
        var reached = graph.Reachable(graph.Endpoint("POST /api/shipments").Id);

        Assert.Contains("ShippingService.create", reached);
        Assert.Contains("DefaultShippingService.create", reached);       // the call on the interface continues in the class
        Assert.Contains("money.formatMoney", reached);                    // in @shop/shared, found without anything installed
        Assert.Contains("Database", reached);
        Assert.Contains("HTTP (outbound)", reached);

        Assert.True(graph.HasEdge("calls", "Checkout.Checkout", "client.placeOrder"));       // <Checkout /> and its calls
        Assert.True(graph.HasEdge("calls", "App.App", "ProductList.ProductList"));
        Assert.True(graph.HasEdge("calls", "src/index.(top level)", "sync.syncProducts"));   // CommonJS require
    }

    [Fact]
    public void Endpoints_know_what_a_request_carries()
    {
        var nodes = node.Session.Snapshot.Graph!.Nodes.Values;
        RequestShape Shape(string name) => (RequestShape)nodes.Single(n => n.Kind == "endpoint" && n.Name == name).Meta["request"];

        var report = Shape("POST /admin/reports");
        Assert.Equal("CreateReportDto", report.BodyType);
        Assert.Contains("\"title\": \"Sample title\"", report.BodySample);

        var shipment = Shape("POST /api/shipments");
        Assert.Contains("\"orderId\": 1", shipment.BodySample);
        Assert.Contains("\"weightKg\": 1", Shape("POST /api/quotes").BodySample);
        Assert.Equal(("id", "route"), (Shape("GET /api/shipments/:id").Params.Single().Name, Shape("GET /api/shipments/:id").Params.Single().In));
        Assert.Equal(["from", "to"], Shape("GET /admin/reports").Params.Where(p => p.In == "query").Select(p => p.Name));
    }

    [Fact]
    public async Task Unsaved_editor_text_updates_the_graph_and_goes_when_discarded()
    {
        var file = Path.Combine(node.SampleDir, "web", "src", "api", "client.ts");
        var source = (await node.Session.ReadSourceAsync(file))!.Value;
        const string anchor = "export async function trackShipment(id: string) {";
        Assert.Contains(anchor, source.Text);

        node.Session.Enqueue(new OverlayWork(file, source.Text.Replace(anchor, anchor + "\n  await listProducts();")));
        await node.Eventually(g => g.HasEdge("calls", "client.trackShipment", "client.listProducts"));
        Assert.Equal(source.Text, await File.ReadAllTextAsync(file));

        node.Session.Enqueue(new OverlayWork(file, null));
        await node.Eventually(g => !g.HasEdge("calls", "client.trackShipment", "client.listProducts"));
    }
}

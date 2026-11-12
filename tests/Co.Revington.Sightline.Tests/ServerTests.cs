using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Co.Revington.Sightline.Analysis;
using Co.Revington.Sightline.Live;
using Co.Revington.Sightline.Settings;

namespace Co.Revington.Sightline.Tests;

/// The server's other jobs on the Shop sample: opening files in an editor, "Show in map",
/// sending requests to local apps, and what running apps report.
public sealed class ServerTests(ShopFixture shop) : IClassFixture<ShopFixture>
{
    [Fact]
    public async Task Opening_a_file_outside_the_solution_is_refused()
    {
        var outside = typeof(ServerTests).Assembly.Location;
        var response = await shop.Http.PostAsJsonAsync("/api/open", new { file = outside, line = 1 });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Show_in_map_finds_the_method_at_a_line_and_answers_with_a_link()
    {
        var file = Path.Combine(shop.SampleDir, "Shop.Application", "Orders", "PlaceOrder.cs");
        var line = File.ReadAllLines(file).ToList().FindIndex(l => l.Contains("public async Task<OrderDto> Handle")) + 2;

        var answer = await (await shop.Http.PostAsJsonAsync("/api/reveal", new { file, line })).Content.ReadFromJsonAsync<JsonObject>();

        Assert.Equal("Handle", answer!["name"]!.GetValue<string>());
        Assert.StartsWith("#view=flow&id=", answer["hash"]!.GetValue<string>());
        Assert.Contains("PlaceOrderHandler.Handle", Uri.UnescapeDataString(answer["hash"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Requests_only_go_to_apps_on_this_machine()
    {
        var answer = await (await shop.Http.PostAsJsonAsync("/api/try", new { url = "http://example.com/", method = "GET" }))
            .Content.ReadFromJsonAsync<JsonObject>();
        Assert.Contains("only go to an app on this machine", answer!["error"]!.GetValue<string>());

        // A local address that nothing listens on gives a plain answer, not a failure of the page.
        var closed = await (await shop.Http.PostAsJsonAsync("/api/try", new { url = "http://127.0.0.1:9/", method = "GET" }))
            .Content.ReadFromJsonAsync<JsonObject>();
        Assert.Contains("Is the app running?", closed!["error"]!.GetValue<string>());
    }

    [Fact]
    public void Routes_from_the_running_app_match_the_endpoints_read_from_the_code()
    {
        var graph = shop.Session.Snapshot.Graph!;

        Assert.Equal("ep:Shop.Api:GET /api/orders/{id:int}", RuntimeMerge.EndpointFor(graph, "GET", "/api/orders/7", null));
        Assert.Equal("ep:Shop.Api:GET /api/orders/{id:int}", RuntimeMerge.EndpointFor(graph, "GET", "/api/orders/7", "/api/orders/{id:int}"));
        Assert.Equal("ep:Shop.Api:POST /api/orders", RuntimeMerge.EndpointFor(graph, "POST", "/api/orders/", "/api/orders/"));
        Assert.Equal("ep:Shop.Api:GET /api/Products", RuntimeMerge.EndpointFor(graph, "GET", "/api/products", "api/Products"));
        Assert.Null(RuntimeMerge.EndpointFor(graph, "GET", "/nowhere", null));
    }

    [Fact]
    public void Registrations_and_endpoints_only_the_running_app_knows_are_added_to_the_map()
    {
        var shopFacts = shop.Session;
        var projects = new List<ProjectFacts>();
        var docs = new List<DocFacts>();
        var apiProject = new ProjectFacts
        {
            Id = "proj:Api", Name = "Api", AssemblyName = "Api", Path = "Api.csproj", Dir = ".", Kind = "web",
            RoslynId = Microsoft.CodeAnalysis.ProjectId.CreateNewId(),
        };
        projects.Add(apiProject);
        var doc = new DocFacts { File = "Program.cs", Project = "proj:Api" };
        doc.Types.Add(new TypeFact("Api::T:Api.IClock", "IClock", "Api", "interface", "public", null, null, null, [], [], [], 1, 1));
        doc.Types.Add(new TypeFact("Api::T:Api.Clock", "Clock", "Api", "class", "public", null, null, null, [], [], [], 2, 2));
        docs.Add(doc);

        var app = new RuntimeApp(1, "Api", "Development", ["http://localhost:5000"],
            [
                new RuntimeService("Api.IClock", "Api", "IClock", "Api.Clock", "Api", "Clock", "Singleton", false, false),
                new RuntimeService("Microsoft.Extensions.Logging.ILogger`1", "Microsoft.Extensions.Logging.Abstractions", "ILogger<T>", null, null, null, "Singleton", false, false),
            ],
            [new RuntimeEndpoint("/scanned", ["GET"], "HTTP: GET /scanned", null, null, null)],
            DateTime.UtcNow);

        var graph = GraphBuilder.Build(projects, docs, null, [app]);

        var service = Assert.Single(graph.Services);
        Assert.Equal(("IClock", "Clock", "singleton", "running app"), (service.Service, service.Impl, service.Lifetime, service.Method));
        var endpoint = graph.Nodes["ep:Api:GET /scanned"];
        Assert.Equal("only", endpoint.Meta["runtime"]);
        Assert.Contains(graph.Coverage, c => c.Kind == "runtime" && c.Count == 2);
        Assert.NotNull(shopFacts);
    }

    [Fact]
    public void Tours_are_saved_under_a_name_made_from_the_title()
    {
        var root = Directory.CreateTempSubdirectory("sightline-tours-").FullName;
        try
        {
            var tour = JsonNode.Parse("""{ "title": "How an order is placed!", "steps": [ { "view": "flow", "id": "x", "label": "POST /api/orders", "text": "Start here" } ] }""")!.AsObject();
            var id = Tours.Save(root, tour, null);

            Assert.Equal("how-an-order-is-placed", id);
            Assert.True(File.Exists(Path.Combine(root, ".sightline", "tours", id + ".json")));
            var loaded = Assert.Single(Tours.Load(root));
            Assert.Equal("Start here", loaded!["steps"]![0]!["text"]!.GetValue<string>());

            // A name from a request can never point outside the tours folder.
            Assert.False(Tours.Delete(root, "../../secrets"));
            Assert.True(Tours.Delete(root, id));
            Assert.Empty(Tours.Load(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_repository_can_only_name_a_known_editor()
    {
        var dir = Directory.CreateTempSubdirectory("sightline-editor-").FullName;
        try
        {
            var file = Path.Combine(dir, "sightline.json");
            File.WriteAllText(file, """{ "editor": "Rider" }""");
            Assert.Equal("rider", SightlineConfig.Load(file).Editor);

            File.WriteAllText(file, """{ "editor": "calc.exe {file}" }""");
            var error = Assert.Throws<ConfigException>(() => SightlineConfig.Load(file));
            Assert.Contains("SIGHTLINE_EDITOR", error.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

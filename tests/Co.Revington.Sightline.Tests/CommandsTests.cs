using System.Text.Json.Nodes;
using Co.Revington.Sightline.Commands;
using Co.Revington.Sightline.Mcp;
using Co.Revington.Sightline.Settings;

namespace Co.Revington.Sightline.Tests;

/// The commands that report and exit (export, diff) and the tools for AI assistants, on the Shop sample.
public sealed class CommandsTests(ShopFixture shop) : IClassFixture<ShopFixture>
{
    McpTools Tools => new(shop.Session, new PathScrubber(shop.RepoRoot));

    string Call(string tool, JsonObject args) => Tools.Call(tool, args, shop.Session.Snapshot.Graph!);

    [Fact]
    public void The_shop_has_no_problems_and_says_what_it_could_not_follow()
    {
        var graph = shop.Session.Snapshot.Graph!;
        Assert.Empty(graph.Problems);
        var note = Assert.Single(graph.Coverage);
        Assert.Equal("reflection", note.Kind);
        Assert.EndsWith("Sender.cs", note.Sites[0].File);
    }

    [Fact]
    public void Endpoints_know_what_a_request_carries()
    {
        var nodes = shop.Session.Snapshot.Graph!.Nodes.Values;
        var post = nodes.Single(n => n.Kind == "endpoint" && n.Name == "POST /api/orders");
        var request = Assert.IsType<Analysis.RequestShape>(post.Meta["request"]);
        Assert.Equal("PlaceOrder", request.BodyType);
        Assert.Contains("\"customerEmail\": \"user@example.com\"", request.BodySample);
        Assert.Contains("\"productId\": 1", request.BodySample);

        var get = nodes.Single(n => n.Kind == "endpoint" && n.Name == "GET /api/Products/{id:int}");
        var route = Assert.Single(((Analysis.RequestShape)get.Meta["request"]).Params);
        Assert.Equal(("id", "route", "1"), (route.Name, route.In, route.Sample));
    }

    [Fact]
    public void Ai_tools_trace_a_request_through_interfaces_to_the_database()
    {
        var text = Call("trace_flow", new JsonObject { ["target"] = "POST /api/orders" });

        Assert.Contains("sends to PlaceOrderHandler.Handle", text);
        Assert.Contains("IPaymentGateway.ChargeAsync → PaymentGatewayClient.ChargeAsync", text);
        Assert.Contains("database ShopDbContext", text);
        Assert.Contains("samples/Shop/Shop.Application/Orders/PlaceOrder.cs", text);
        Assert.DoesNotContain(shop.RepoRoot, text);
    }

    [Fact]
    public void Ai_tools_answer_who_depends_on_a_type_and_explain_unknown_names()
    {
        var users = Call("who_depends_on", new JsonObject { ["target"] = "IOrderRepository" });
        Assert.Contains("PlaceOrderHandler", users);
        Assert.Contains("DELETE /api/orders/{id:int}", users);

        var missing = Assert.Throws<ToolException>(() => Call("describe_type", new JsonObject { ["name"] = "NoSuchThing" }));
        Assert.Contains("find_symbol", missing.Message);
    }

    [Fact]
    public async Task Export_writes_a_website_without_paths_from_this_machine()
    {
        var dir = Directory.CreateTempSubdirectory("sightline-export-").FullName;
        try
        {
            var target = new SolutionTarget(Path.Combine(shop.SampleDir, "Shop.sln"), null, ProjectFilter.None, Restore: false);
            var code = await Exporter.RunAsync(new ExportCommand(target, Path.Combine(dir, "site"), Path.Combine(dir, "map.json"), true, null, "Shop"));
            Assert.Equal(0, code);

            var data = await File.ReadAllTextAsync(Path.Combine(dir, "site", "data.json"));
            Assert.DoesNotContain(shop.RepoRoot.Replace("\\", "\\\\"), data);
            Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Replace("\\", "\\\\"), data);
            Assert.Contains("\"samples/Shop/Shop.Application/Orders/PlaceOrder.cs\"", data);

            var html = await File.ReadAllTextAsync(Path.Combine(dir, "site", "index.html"));
            Assert.Contains("Content-Security-Policy", html);
            Assert.DoesNotContain("src=\"/", html);
            Assert.Contains("api.js", Directory.GetFiles(Path.Combine(dir, "site", "js")).Select(Path.GetFileName));
            Assert.Contains("const data = await fetch('./data.json')", await File.ReadAllTextAsync(Path.Combine(dir, "site", "js", "api.js")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Diff_reports_endpoints_flows_services_and_problems()
    {
        var before = JsonNode.Parse("""
            {
              "nodes": [
                { "id": "ep:A:GET /a", "kind": "endpoint", "name": "GET /a" },
                { "id": "T:S", "kind": "type", "name": "Service" },
                { "id": "M:S.Run", "kind": "member", "name": "Run", "parent": "T:S" }
              ],
              "edges": [ { "id": "1", "kind": "handles", "source": "ep:A:GET /a", "target": "M:S.Run" } ],
              "services": [ { "id": "s1", "service": "IService", "impl": "Service", "lifetime": "scoped" } ],
              "problems": []
            }
            """)!;
        var after = JsonNode.Parse("""
            {
              "nodes": [
                { "id": "ep:A:GET /a", "kind": "endpoint", "name": "GET /a" },
                { "id": "ep:A:POST /b", "kind": "endpoint", "name": "POST /b", "file": "src/B.cs", "line": 3 },
                { "id": "T:S", "kind": "type", "name": "Service" },
                { "id": "M:S.Run", "kind": "member", "name": "Run", "parent": "T:S" },
                { "id": "ext:db", "kind": "external", "name": "Database" }
              ],
              "edges": [
                { "id": "1", "kind": "handles", "source": "ep:A:GET /a", "target": "M:S.Run" },
                { "id": "2", "kind": "db", "source": "M:S.Run", "target": "ext:db" }
              ],
              "services": [ { "id": "s1", "service": "IService", "impl": "Service", "lifetime": "singleton" } ],
              "problems": [ { "id": "p1", "rule": "captive-dependency", "severity": "error", "title": "A singleton holds on to a scoped service", "message": "..." } ]
            }
            """)!;

        var result = MapDiff.Compare(before, after);

        Assert.Equal(new MapDiff.Change("+", "POST /b", "src/B.cs:3"), Assert.Single(result.Endpoints));
        var flow = Assert.Single(result.Flows);
        Assert.Equal("GET /a", flow.Endpoint);
        Assert.Equal(["Database"], flow.Added);
        Assert.Equal(new MapDiff.Change("~", "IService → Service", "scoped → singleton"), Assert.Single(result.Services));
        Assert.Single(result.NewProblems);

        var markdown = MapDiff.Markdown(result);
        Assert.StartsWith(MapDiff.Marker, markdown);
        Assert.Contains("1 endpoint added", markdown);
        Assert.Contains("`GET /a` now also reaches `Database`", markdown);
    }
}

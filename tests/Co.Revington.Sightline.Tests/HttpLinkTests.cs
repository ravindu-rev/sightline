using Co.Revington.Sightline.Analysis;
using Co.Revington.Sightline.Commands;
using Co.Revington.Sightline.Live;
using Co.Revington.Sightline.Settings;
using Co.Revington.Sightline.Setup;
using MapGraph = Co.Revington.Sightline.Analysis.Graph;

namespace Co.Revington.Sightline.Tests;

/// Which endpoint an HTTP request in the code reaches, in any of the route styles.
public sealed class RoutesTests
{
    [Theory]
    [InlineData("GET", "https://localhost:5001/api/orders?page=2", "GET /api/orders")]
    [InlineData("post", "api/orders", "POST */api/orders")]
    [InlineData("GET", "*/api/orders/*", "GET */api/orders/*")]
    [InlineData("GET", "http://shop-api:8080/api/orders", "GET /api/orders")]
    [InlineData("GET", "/api/orders/order-*", "GET /api/orders/*")]
    [InlineData("GET", "https://payments.example.com/charges", null)]
    [InlineData("GET", "https://*.stripe.com/v1/charges", null)]
    [InlineData("GET", "http://*/api/orders", "GET /api/orders")]
    [InlineData("GET", "*", null)]
    [InlineData("GET", "/*", null)]
    public void Addresses_become_requests_that_can_be_compared(string verb, string url, string? expected)
    {
        var call = Routes.Request(verb, url);
        Assert.Equal(expected, call is null ? null : $"{call.Verb} {call.Path}");
    }

    [Theory]
    [InlineData("GET", "/api/orders/7", "GET", "/api/orders/{id:int}", true)]
    [InlineData("POST", "/api/orders/7", "GET", "/api/orders/{id:int}", false)]
    [InlineData("GET", "/api/shipments/*", "GET", "/api/shipments/:id", true)]
    [InlineData("GET", "/api/orders/7", "GET", "/api/orders/[id]", true)]
    [InlineData("GET", "/files/a/b/c", "GET", "/files/{*path}", true)]
    [InlineData("GET", "/docs", "ANY", "/docs/[[...slug]]", true)]
    [InlineData("GET", "/anything/at/all", "ANY", "{*path}", false)]
    [InlineData("GET", "*/orders", "GET", "/api/orders", true)]
    [InlineData("GET", "/orders", "GET", "/api/orders", false)]
    [InlineData("GET", "/api/orders", "HUB", "/api/orders", false)]
    [InlineData("*", "/api/orders", "POST", "/api/orders", true)]
    [InlineData("GET", "/api/*", "GET", "/api/health", false)]          // a part worked out at run time is no fixed word
    [InlineData("GET", "*/x", "GET", "/api/files/{*path}", false)]      // a catch-all with nothing in common
    [InlineData("GET", "/a/b", "GET", "/{tenant}/{id}", false)]          // no fixed word in common
    public void Requests_fit_routes_of_every_style(string verb, string path, string verbs, string route, bool fits)
    {
        Assert.Equal(fits, Routes.Match(new HttpCall(verb, path), verbs, route) > 0);
    }

    [Fact]
    public void A_request_that_fits_the_whole_route_beats_one_that_fits_its_end()
    {
        var call = new HttpCall("GET", "*/api/orders");
        Assert.True(Routes.Match(call, "GET", "/api/orders") > Routes.Match(call, "GET", "/v2/api/orders"));
    }

    [Fact]
    public void A_request_that_fits_several_endpoints_equally_stays_outbound()
    {
        var doc = new DocFacts { File = "client.ts", Project = "proj:App" };
        doc.Types.Add(new TypeFact("App::T:Client", "Client", "", "class", "public", null, null, null, [], [], [], 1, 9));
        doc.Members.Add(new MemberFact("App::M:Client.Load", "App::T:Client", "Load", "method", "Load()", null, "public", false, false, false, 2, 3));
        doc.Members.Add(new MemberFact("App::M:Client.LoadA", "App::T:Client", "LoadA", "method", "LoadA()", null, "public", false, false, false, 4, 5));
        doc.Endpoints.Add(new EndpointFact("a", "GET", "/a/orders", "", null, "minimal", null, null, "App::T:Client", false, null, 6, 6));
        doc.Endpoints.Add(new EndpointFact("b", "GET", "/b/orders", "", null, "minimal", null, null, "App::T:Client", false, null, 7, 7));
        doc.Refs.Add(new RefFact("App::M:Client.Load", "ext:http", "http", 2, "GET", Request: new HttpCall("GET", "*/orders")));
        doc.Refs.Add(new RefFact("App::M:Client.LoadA", "ext:http", "http", 4, "GET", Request: new HttpCall("GET", "*/a/orders")));
        var project = new ProjectFacts { Id = "proj:App", Name = "App", Path = "App.csproj", Dir = ".", Kind = "web" };

        var graph = GraphBuilder.Build([project], [doc]);

        Assert.Contains(graph.Edges.Values, e => e.Kind == "http" && e.Source == "App::M:Client.Load" && e.Target == "ext:http");
        Assert.Contains(graph.Edges.Values, e => e.Kind == "http" && e.Source == "App::M:Client.LoadA" && e.Target == "ep:App:GET /a/orders");
    }
}

/// A web app that calls its own endpoints with HttpClient: the calls link to them.
public sealed class CsharpHttpFixture : IAsyncLifetime
{
    readonly string _dir = Directory.CreateTempSubdirectory("sightline-http-").FullName;
    internal MapGraph Graph { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        Write("App/Program.cs", """
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddHttpClient<StockClient>();
            var app = builder.Build();
            app.MapGet("/api/stock/{sku}", (string sku) => 3);
            app.MapPost("/api/stock/{sku}/reserve", (string sku) => Results.NoContent());
            app.Run();

            public sealed class StockClient(HttpClient http)
            {
                const string Root = "api/stock";

                public Task<int?> CountAsync(string sku) => http.GetFromJsonAsync<int?>($"{Root}/{sku}");

                public Task ReserveAsync(string sku) => http.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/stock/{sku}/reserve"));

                public Task<string> ElsewhereAsync() => http.GetStringAsync("https://status.example.com/api/stock/1");
            }
            """);

        var (session, snapshot) = await OneShot.LoadAsync(new SolutionTarget(Path.Combine(_dir, "App", "App.csproj"), null, ProjectFilter.None, Restore: true));
        using (session) Graph = snapshot.Graph!;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return Task.CompletedTask;
    }

    void Write(string relative, string text)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}

public sealed class CsharpHttpLinkTests(CsharpHttpFixture app) : IClassFixture<CsharpHttpFixture>
{
    string Target(string member)
    {
        var id = app.Graph.Nodes.Values.Single(n => n.Kind == "member" && n.Name == member).Id;
        return app.Graph.Edges.Values.Single(e => e.Kind == "http" && e.Source == id).Target;
    }

    [Fact]
    public void HttpClient_calls_to_an_endpoint_of_the_code_link_to_it()
    {
        Assert.Equal("ep:App:GET /api/stock/{sku}", Target("CountAsync"));
        Assert.Equal("ep:App:POST /api/stock/{sku}/reserve", Target("ReserveAsync"));
        Assert.Equal("ext:http", Target("ElsewhereAsync"));
    }
}

/// sightline.json "languages", "solution" naming a folder, and init in a folder of npm packages.
public sealed class LanguageSettingsTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("sightline-languages-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    string Write(string relative, string text)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void Languages_name_the_readers_to_use()
    {
        var config = SightlineConfig.Load(Write("sightline.json", """{ "languages": ["csharp", "JavaScript", "typescript"] }"""));
        Assert.Equal(["csharp", "typescript"], config.Languages);

        var error = Assert.Throws<ConfigException>(() => SightlineConfig.Load(Write("sightline.json", """{ "languages": ["cobol"] }""")));
        Assert.Contains("\"languages\" must be", error.Message);
    }

    [Fact]
    public async Task Init_sets_up_a_folder_of_npm_packages_without_naming_a_solution()
    {
        Write("package.json", """{ "name": "web" }""");
        Write("src/index.ts", "export const x = 1;\n");

        Assert.Equal(0, await Init.RunAsync(new InitOptions(_dir, null, VsCode: false, PinTool: false)));

        var config = SightlineConfig.Load(Path.Combine(_dir, "sightline.json"));
        Assert.Null(config.Solution);
        Assert.Equal(_dir, Cli.FindNearestPackageRoot(Path.Combine(_dir, "src")));
    }

    [Fact]
    public async Task A_broken_package_json_is_a_warning_and_the_rest_still_loads()
    {
        Write("Lib/Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Write("Lib/Thing.cs", "namespace Lib; public class Thing { }");
        Write("ui/package.json", "{ \"name\": ");
        Write("ui/index.js", "export const x = 1;\n");

        var (session, snapshot) = await OneShot.LoadAsync(new SolutionTarget(_dir, null, ProjectFilter.None, Restore: true));
        using (session)
        {
            Assert.Equal(["Lib"], snapshot.Graph!.Nodes.Values.Where(n => n.Kind == "project").Select(n => n.Name));
            Assert.Contains("ui/package.json could not be read", snapshot.InfoJson);
        }
    }
}

[CollectionDefinition("Environment", DisableParallelization = true)]
public sealed class EnvironmentCollection;

/// Changes environment variables, so it runs alone.
[Collection("Environment")]
public sealed class NodeMissingTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("sightline-no-node-").FullName;

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SIGHTLINE_NODE", null);
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Without_Node_the_csharp_map_loads_and_says_what_is_missing()
    {
        File.WriteAllText(Path.Combine(_dir, "package.json"), """{ "name": "web" }""");
        File.WriteAllText(Path.Combine(_dir, "index.js"), "export const x = 1;\n");
        Directory.CreateDirectory(Path.Combine(_dir, "Lib"));
        File.WriteAllText(Path.Combine(_dir, "Lib", "Lib.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(_dir, "Lib", "Thing.cs"), "namespace Lib; public class Thing { }");
        Environment.SetEnvironmentVariable("SIGHTLINE_NODE", Path.Combine(_dir, "no-such-node.exe"));

        var (session, snapshot) = await OneShot.LoadAsync(new SolutionTarget(_dir, null, ProjectFilter.None, Restore: true));
        using (session)
        {
            Assert.Equal(["Lib"], snapshot.Graph!.Nodes.Values.Where(n => n.Kind == "project").Select(n => n.Name));
            Assert.Contains("reading them needs Node.js 18 or newer", snapshot.InfoJson);
        }

        // With nothing else to show, the same is an error.
        Directory.Delete(Path.Combine(_dir, "Lib"), recursive: true);
        using var alone = new LiveSession(_dir, new EventHub(), new LoadOptions(ProjectFilter.None, Restore: false, Watch: false, Log: TextWriter.Null));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => alone.LoadOnceAsync(CancellationToken.None));
        Assert.Contains("Node.js", error.Message);
    }
}

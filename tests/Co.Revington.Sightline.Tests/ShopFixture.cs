using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Co.Revington.Sightline.Live;
using Co.Revington.Sightline.Server;

namespace Co.Revington.Sightline.Tests;

/// Loads the bundled Shop sample once and serves it on a local port,
/// so the tests run the real analysis and the real HTTP API.
public sealed class ShopFixture : IAsyncLifetime
{
    public const string Token = "test-token";

    readonly CancellationTokenSource _stop = new();
    WebApplication? _app;
    Task? _loop;

    public string RepoRoot { get; } = FindRepoRoot();
    public string SampleDir => Path.Combine(RepoRoot, "samples", "Shop");

    /// The loaded solution, for tests that look at more than the HTTP API (MCP tools, export).
    internal LiveSession Session { get; private set; } = null!;

    /// Sends the page token, like the UI does.
    public HttpClient Http { get; private set; } = null!;

    /// Sends no token, like any other program on the machine would.
    public HttpClient Anonymous { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var solution = Path.Combine(SampleDir, "Shop.sln");
        await RestoreIfNeeded(solution);

        var hub = new EventHub();
        var session = new LiveSession(solution, hub);
        Session = session;
        _loop = Task.Run(() => session.RunAsync(_stop.Token));

        var waited = Stopwatch.StartNew();
        while (session.Status.State == "loading")
        {
            if (waited.Elapsed > TimeSpan.FromMinutes(4))
                throw new TimeoutException("The sample solution did not finish loading: " + session.Status.Message);
            await Task.Delay(200);
        }
        if (session.Status.State != "ready")
            throw new InvalidOperationException("The sample solution failed to load: " + session.Status.Message);

        var port = FreePort();
        _app = WebApp.Build(session, hub, port, Token, Path.Combine(RepoRoot, "src", "Co.Revington.Sightline", "wwwroot"));
        await _app.StartAsync();

        var address = new Uri($"http://127.0.0.1:{port}");
        Anonymous = new HttpClient { BaseAddress = address };
        Http = new HttpClient { BaseAddress = address };
        Http.DefaultRequestHeaders.Add("X-Sightline-Token", Token);
    }

    public async Task DisposeAsync()
    {
        _stop.Cancel();
        if (_app is not null) await _app.StopAsync();
        if (_loop is not null) await _loop;
        Http?.Dispose();
        Anonymous?.Dispose();
    }

    /// The graph exactly as the page receives it.
    public async Task<Graph> GetGraphAsync() => Graph.Parse(await Http.GetStringAsync("/api/graph"));

    /// Live updates arrive a moment after an edit; poll until the graph shows the change.
    public async Task<Graph> Eventually(Func<Graph, bool> condition)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            var graph = await GetGraphAsync();
            if (condition(graph)) return graph;
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(20), "The graph did not reach the expected state in time.");
            await Task.Delay(100);
        }
    }

    internal static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Co.Revington.Sightline.sln"))) return dir.FullName;
        throw new InvalidOperationException("Could not find Co.Revington.Sightline.sln above " + AppContext.BaseDirectory);
    }

    /// The analysis needs the sample's NuGet references, which only exist after a restore.
    static async Task RestoreIfNeeded(string solution)
    {
        var assets = Path.Combine(Path.GetDirectoryName(solution)!, "Shop.Api", "obj", "project.assets.json");
        if (File.Exists(assets)) return;

        var info = new ProcessStartInfo("dotnet", $"restore \"{solution}\"")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        using var process = Process.Start(info)!;
        var errors = process.StandardError.ReadToEndAsync();
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"dotnet restore failed for the sample solution:\n{output}\n{await errors}");
    }

    static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

public sealed record GraphNode(string Id, string Kind, string Name, string? Parent);

public sealed record GraphEdge(string Kind, string Source, string Target);

public sealed record GraphService(string Service, string? Impl, string Lifetime);

public sealed record GraphStatus(string State, string? Message);

public sealed record SourceText(string File, string Text, bool Dirty);

/// Read-only view of the /api/graph response with a few helpers for assertions.
public sealed class Graph
{
    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    sealed record Payload(GraphStatus Status, List<GraphNode> Nodes, List<GraphEdge> Edges, List<GraphService> Services);

    // Edge kinds a request travels along.
    static readonly HashSet<string> FlowKinds = ["handles", "calls", "dispatches", "impl", "db", "http"];

    readonly Dictionary<string, GraphNode> _byId;

    Graph(Payload payload)
    {
        Status = payload.Status;
        Nodes = payload.Nodes;
        Edges = payload.Edges;
        Services = payload.Services;
        _byId = Nodes.ToDictionary(n => n.Id);
    }

    public GraphStatus Status { get; }
    public IReadOnlyList<GraphNode> Nodes { get; }
    public IReadOnlyList<GraphEdge> Edges { get; }
    public IReadOnlyList<GraphService> Services { get; }

    public static Graph Parse(string json) => new(JsonSerializer.Deserialize<Payload>(json, Web)!);

    /// "Type.Member" for members, the plain name for everything else.
    public string Label(string id)
    {
        var node = _byId[id];
        return node.Kind == "member" && node.Parent is not null && _byId.TryGetValue(node.Parent, out var owner)
            ? $"{owner.Name}.{node.Name}"
            : node.Name;
    }

    public List<string> Names(string kind) =>
        Nodes.Where(n => n.Kind == kind).Select(n => n.Name).Order(StringComparer.Ordinal).ToList();

    public GraphNode Endpoint(string name) => Nodes.Single(n => n.Kind == "endpoint" && n.Name == name);

    public bool HasEdge(string kind, string from, string to) =>
        Edges.Any(e => e.Kind == kind && Label(e.Source) == from && Label(e.Target) == to);

    /// Labels of everything a request can reach from a starting node.
    public HashSet<string> Reachable(string startId)
    {
        var seen = new HashSet<string> { startId };
        var queue = new Queue<string>([startId]);
        while (queue.TryDequeue(out var id))
            foreach (var e in Edges.Where(e => e.Source == id && FlowKinds.Contains(e.Kind)))
                if (seen.Add(e.Target)) queue.Enqueue(e.Target);
        return seen.Select(Label).ToHashSet();
    }
}

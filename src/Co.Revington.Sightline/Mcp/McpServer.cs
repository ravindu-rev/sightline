using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Co.Revington.Sightline.Analysis;
using Co.Revington.Sightline.Commands;
using Co.Revington.Sightline.Live;

namespace Co.Revington.Sightline.Mcp;

/// "sightline mcp": the map for AI assistants, over the Model Context Protocol (JSON-RPC 2.0,
/// one message per line on stdin and stdout). The solution stays loaded and follows edits, so
/// answers match the code as it is now. Everything is read-only.
internal sealed class McpServer
{
    // Newest first. A client that asks for one of these gets it; any other gets the newest.
    static readonly string[] Versions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    readonly LiveSession _session;
    readonly PathScrubber _paths;
    readonly Stream _out;
    readonly SemaphoreSlim _writing = new(1, 1);
    readonly McpTools _tools;

    McpServer(LiveSession session, PathScrubber paths, Stream output)
    {
        _session = session;
        _paths = paths;
        _out = output;
        _tools = new McpTools(session, paths);
    }

    public static async Task<int> RunAsync(McpCommand command)
    {
        // The protocol owns stdout. Anything else that writes to the console goes to stderr instead.
        var stdout = Console.OpenStandardOutput();
        Console.SetOut(Console.Error);

        var target = command.Target;
        var options = new LoadOptions(target.Filter, target.Restore, target.Checks, Watch: true, Log: Console.Error);
        var session = new LiveSession(target.Path, new EventHub(), options);
        using var stop = new CancellationTokenSource();
        var loop = Task.Run(() => session.RunAsync(stop.Token));

        var server = new McpServer(session, new PathScrubber(OneShot.RepoRoot(session.RootDir)), stdout);
        await server.ServeAsync(Console.OpenStandardInput(), stop.Token);

        stop.Cancel();
        await loop;
        return 0;
    }

    async Task ServeAsync(Stream input, CancellationToken ct)
    {
        using var reader = new StreamReader(input, new UTF8Encoding(false));
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonNode? message;
            try
            {
                message = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                await SendAsync(Error(null, -32700, "Parse error: each line must be one JSON-RPC message."));
                continue;
            }

            if (message is JsonArray batch)
            {
                foreach (var item in batch) await HandleAsync(item as JsonObject);
            }
            else
            {
                await HandleAsync(message as JsonObject);
            }
        }
    }

    async Task HandleAsync(JsonObject? message)
    {
        if (message is null)
        {
            await SendAsync(Error(null, -32600, "Invalid request."));
            return;
        }

        var method = message["method"]?.GetValue<string>();
        var id = message["id"]?.DeepClone();
        if (method is null) return;                       // a response to us; we never send requests
        if (id is null) return;                           // notifications need no answer

        try
        {
            JsonNode? result = method switch
            {
                "initialize" => Initialize(message["params"] as JsonObject),
                "ping" => new JsonObject(),
                "tools/list" => new JsonObject { ["tools"] = McpTools.Definitions() },
                "tools/call" => await CallAsync(message["params"] as JsonObject),
                _ => null,
            };
            await SendAsync(result is null
                ? Error(id, -32601, $"Method not found: {method}")
                : new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
        }
        catch (ArgumentException ex)
        {
            await SendAsync(Error(id, -32602, ex.Message));
        }
    }

    JsonObject Initialize(JsonObject? args)
    {
        var asked = args?["protocolVersion"]?.GetValue<string>();
        return new JsonObject
        {
            ["protocolVersion"] = asked is not null && Versions.Contains(asked) ? asked : Versions[0],
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "sightline", ["title"] = "Sightline", ["version"] = ToolInfo.Version },
            ["instructions"] =
                $"Sightline has read the .NET solution {_session.Name} with the C# compiler and keeps up with edits. " +
                "Use it instead of searching files when you need to know how the code fits together: which endpoint runs which " +
                "handler, what a request calls down to the database, which class is registered for an interface, who uses a type, " +
                "and which dependency injection problems exist. Start with solution_overview. File paths are relative to " +
                _paths.Root + ".",
        };
    }

    async Task<JsonObject> CallAsync(JsonObject? args)
    {
        var name = args?["name"]?.GetValue<string>() ?? throw new ArgumentException("tools/call needs a tool name.");
        var arguments = args["arguments"] as JsonObject ?? new JsonObject();

        string text;
        var failed = false;
        try
        {
            var graph = await ReadyGraphAsync();
            text = _tools.Call(name, arguments, graph);
        }
        catch (ToolException ex)
        {
            (text, failed) = (ex.Message, true);
        }

        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = failed,
        };
    }

    /// The current graph, once the solution has loaded.
    async Task<Graph> ReadyGraphAsync()
    {
        for (var waited = 0; waited < 600; waited++)
        {
            if (_session.Snapshot.Graph is { } graph && _session.Status.State != "loading") return graph;
            if (_session.Status.State == "error") throw new ToolException("Sightline could not load the solution: " + _session.Status.Message);
            await Task.Delay(250);
        }
        throw new ToolException($"Sightline is still loading the solution ({_session.Status.Message}). Try again in a moment.");
    }

    static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    async Task SendAsync(JsonObject message)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString(Compact) + "\n");
        await _writing.WaitAsync();
        try
        {
            await _out.WriteAsync(bytes);
            await _out.FlushAsync();
        }
        finally
        {
            _writing.Release();
        }
    }
}

internal sealed class ToolException(string message) : Exception(message);

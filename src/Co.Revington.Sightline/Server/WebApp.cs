using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Co.Revington.Sightline.Analysis;
using Co.Revington.Sightline.Live;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace Co.Revington.Sightline.Server;

/// Companions: apps this map runs beside. ToursRoot: where .sightline/tours lives. Editor: "editor" from sightline.json.
internal sealed record ServerOptions(Companions? Companions = null, string? ToursRoot = null, string? Editor = null);

/// Local web server: serves the UI, the graph, live events and source files.
internal static class WebApp
{
    sealed record SourceBody(string File, string Text);

    sealed record AttachBody(int Pid);

    sealed record OpenBody(string File, int Line, string? Editor);

    sealed record RevealBody(string File, int Line);

    public static WebApplication Build(LiveSession session, EventHub hub, int port, string token, string uiDir, ServerOptions? options = null)
    {
        options ??= new ServerOptions();
        var companions = options.Companions;
        var runtime = new RuntimeRegistry(session, hub);
        // Content root is the tool folder, so the analyzed app's appsettings.json is never picked up.
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.ListenLocalhost(port));
        var app = builder.Build();

        var tokenBytes = Encoding.UTF8.GetBytes(token);
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { $"localhost:{port}", $"127.0.0.1:{port}", $"[::1]:{port}" };

        // Guard: requests must address localhost, and API calls must carry the token from the page.
        // This stops other websites from reading or writing source files through the browser.
        app.Use(async (ctx, next) =>
        {
            if (!hosts.Contains(ctx.Request.Host.Value ?? ""))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                var given = ctx.Request.Headers["X-Sightline-Token"].FirstOrDefault() ?? ctx.Request.Query["token"].FirstOrDefault() ?? "";
                if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), tokenBytes))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
            }
            await next(ctx);
        });

        if (Directory.Exists(uiDir))
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(uiDir),
                OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache",
            });
        }

        app.MapGet("/", async ctx =>
        {
            var index = Path.Combine(uiDir, "index.html");
            if (!File.Exists(index))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                await ctx.Response.WriteAsync("Sightline UI files are missing: " + uiDir);
                return;
            }

            var html = await File.ReadAllTextAsync(index);
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-store";

            // Only the tool's own scripts may run. Names and routes from the analyzed code are shown as text,
            // and this stops them from ever executing even if one slipped through unescaped.
            // Framing is allowed only inside VS Code (its Simple Browser), never by a website.
            ctx.Response.Headers.ContentSecurityPolicy =
                "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
                "connect-src 'self'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'self' vscode-webview:";
            ctx.Response.Headers.XContentTypeOptions = "nosniff";
            await ctx.Response.WriteAsync(html.Replace("__SIGHTLINE_TOKEN__", token));
        });

        app.MapGet("/api/graph", ctx => Json(ctx, session.Snapshot.ToJson(session.StatusJson)));

        // Cheap check for the page: 204 while this server runs, 401 when the page's token is from an earlier run.
        app.MapGet("/api/ping", ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });

        app.MapGet("/api/events", ctx => StreamEvents(ctx, session, hub));

        app.MapGet("/api/source", async ctx =>
        {
            var file = FullPath(ctx.Request.Query["file"]);
            var source = file is null ? null : await session.ReadSourceAsync(file);
            if (source is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await Json(ctx, JsonSerializer.Serialize(new { file, text = source.Value.Text, dirty = source.Value.Dirty }, Snapshot.Json));
        });

        // Unsaved editor text: analyzed live, not written to disk.
        app.MapPost("/api/overlay", async ctx =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<SourceBody>();
            var file = FullPath(body?.File);
            if (body is null || file is null || !session.IsTracked(file))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            session.Enqueue(new OverlayWork(file, body.Text));
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
        });

        app.MapDelete("/api/overlay", ctx =>
        {
            var file = FullPath(ctx.Request.Query["file"]);
            if (file is null || !session.IsTracked(file))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }
            session.Enqueue(new OverlayWork(file, null));
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });

        app.MapPut("/api/source", async ctx =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<SourceBody>();
            var file = FullPath(body?.File);
            var saved = body is not null && file is not null && await session.SaveAsync(file, body.Text);
            ctx.Response.StatusCode = saved ? StatusCodes.Status204NoContent : StatusCodes.Status404NotFound;
        });

        app.MapPost("/api/reload", ctx =>
        {
            session.Enqueue(new ReloadWork());
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });

        // Another app joins this map: keep it open while that app runs. See Companions.
        app.MapPost("/api/attach", async ctx =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<AttachBody>();
            var joined = companions is not null && body is not null && companions.Attach(body.Pid);
            ctx.Response.StatusCode = joined ? StatusCodes.Status204NoContent : StatusCodes.Status400BadRequest;
        });

        // Opens a file of the solution in the developer's editor.
        app.MapPost("/api/open", async ctx =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<OpenBody>();
            var file = FullPath(body?.File);
            if (body is null || file is null || !session.IsTracked(file))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            var result = EditorLauncher.Open(file, body.Line, body.Editor, options.Editor);
            await Json(ctx, JsonSerializer.Serialize(result, Snapshot.Json));
        });

        // "Show in map" from an editor: the node at a file and line, shown in every open map page.
        app.MapPost("/api/reveal", async ctx =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<RevealBody>();
            var file = FullPath(body?.File);
            var node = file is null || session.Snapshot.Graph is not { } graph ? null : NodeAt(graph, file, body!.Line);
            if (node is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            var hash = HashFor(node);
            hub.Publish("reveal", JsonSerializer.Serialize(new { hash }, Snapshot.Json));
            await Json(ctx, JsonSerializer.Serialize(new { id = node.Id, name = node.Name, kind = node.Kind, hash, viewers = hub.Count }, Snapshot.Json));
        });

        // Tours kept in the repository, under .sightline/tours.
        var toursRoot = options.ToursRoot ?? session.RootDir;
        app.MapGet("/api/tours", ctx => Json(ctx, Tours.Load(toursRoot).ToJsonString()));
        app.MapPut("/api/tours", async ctx =>
        {
            var tour = await JsonNode.ParseAsync(ctx.Request.Body) as JsonObject;
            try
            {
                var id = Tours.Save(toursRoot, tour ?? throw new ArgumentException("A tour is a JSON object."), ctx.Request.Query["id"].FirstOrDefault());
                await Json(ctx, JsonSerializer.Serialize(new { id }, Snapshot.Json));
            }
            catch (ArgumentException ex)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync(ex.Message);
            }
        });
        app.MapDelete("/api/tours", ctx =>
        {
            var deleted = Tours.Delete(toursRoot, ctx.Request.Query["id"].FirstOrDefault() ?? "");
            ctx.Response.StatusCode = deleted ? StatusCodes.Status204NoContent : StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });

        // Running apps (Co.Revington.Sightline.Hosting) report their services, endpoints and requests here.
        app.MapPost("/api/runtime", async ctx =>
        {
            var report = await ctx.Request.ReadFromJsonAsync<RuntimeReport>(Snapshot.Json);
            if (report is null || string.IsNullOrWhiteSpace(report.App))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            runtime.Report(report);
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
        });
        app.MapGet("/api/runtime", ctx => Json(ctx, JsonSerializer.Serialize(runtime.Apps(), Snapshot.Json)));

        app.MapPost("/api/trace", async ctx =>
        {
            var batch = await ctx.Request.ReadFromJsonAsync<TraceBatch>(Snapshot.Json);
            if (batch?.Traces is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            runtime.AddTraces(batch);
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
        });
        app.MapGet("/api/traces", ctx => Json(ctx, JsonSerializer.Serialize(runtime.Traces(), Snapshot.Json)));

        // "Try it": a request to a local app, sent from here because the page cannot read other ports.
        app.MapPost("/api/try", async ctx =>
        {
            var request = await ctx.Request.ReadFromJsonAsync<TryRequest>(Snapshot.Json);
            if (request is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            await Json(ctx, JsonSerializer.Serialize(await TryProxy.SendAsync(request, ctx.RequestAborted), Snapshot.Json));
        });

        return app;
    }

    /// The innermost member, endpoint or type that covers a line of a file.
    static Node? NodeAt(Graph graph, string file, int line) =>
        graph.Nodes.Values
            .Where(n => n.Kind is "member" or "type" or "endpoint" && n.File is not null && n.Line is not null
                        && string.Equals(Path.GetFullPath(n.File), file, StringComparison.OrdinalIgnoreCase)
                        && n.Line <= line && (n.EndLine ?? n.Line) >= line)
            .OrderBy(n => (n.EndLine ?? n.Line) - n.Line)
            .ThenBy(n => n.Kind == "endpoint" ? 0 : n.Kind == "member" ? 1 : 2)
            .FirstOrDefault();

    /// The page address that shows a node: endpoints and methods as a flow, anything else in focus on its type.
    static string HashFor(Node node)
    {
        var flow = node.Kind == "endpoint" || node.Meta.GetValueOrDefault("memberKind") is "method" or "ctor";
        var (view, id, sel) = flow ? ("flow", node.Id, null)
            : node.Kind == "member" ? ("focus", node.Parent ?? node.Id, node.Id)
            : ("focus", node.Id, (string?)null);
        return $"#view={view}&id={Uri.EscapeDataString(id)}" + (sel is null ? "" : $"&sel={Uri.EscapeDataString(sel)}");
    }

    static string? FullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path); }
        catch { return null; }
    }

    static Task Json(HttpContext ctx, string json)
    {
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-store";
        return ctx.Response.WriteAsync(json);
    }

    /// Server-sent events: status, patch and reload messages, plus a ping to keep the pipe open.
    static async Task StreamEvents(HttpContext ctx, LiveSession session, EventHub hub)
    {
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        var ct = ctx.RequestAborted;
        var channel = hub.Subscribe();

        try
        {
            await ctx.Response.WriteAsync($"event: hello\ndata: {{\"version\":{session.Snapshot.Version},\"status\":{session.StatusJson}}}\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);

            var reader = channel.Reader;
            var wait = reader.WaitToReadAsync(ct).AsTask();
            while (true)
            {
                var finished = await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(15), ct));
                if (finished != wait)
                {
                    await ctx.Response.WriteAsync(": ping\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                    continue;
                }

                if (!await wait) break;
                while (reader.TryRead(out var message)) await ctx.Response.WriteAsync(message, ct);
                await ctx.Response.Body.FlushAsync(ct);
                wait = reader.WaitToReadAsync(ct).AsTask();
            }
        }
        catch (OperationCanceledException)
        {
            // browser tab closed
        }
        finally
        {
            hub.Unsubscribe(channel);
        }
    }
}

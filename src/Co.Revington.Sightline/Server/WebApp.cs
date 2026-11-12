using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Co.Revington.Sightline.Analysis;
using Co.Revington.Sightline.Live;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace Co.Revington.Sightline.Server;

/// Local web server: serves the UI, the graph, live events and source files.
internal static class WebApp
{
    sealed record SourceBody(string File, string Text);

    public static WebApplication Build(LiveSession session, EventHub hub, int port, string token, string uiDir)
    {
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
            ctx.Response.Headers.ContentSecurityPolicy =
                "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
                "connect-src 'self'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
            ctx.Response.Headers.XContentTypeOptions = "nosniff";
            await ctx.Response.WriteAsync(html.Replace("__SIGHTLINE_TOKEN__", token));
        });

        app.MapGet("/api/graph", ctx => Json(ctx, session.Snapshot.ToJson(session.StatusJson)));

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

        return app;
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

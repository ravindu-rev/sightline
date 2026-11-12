using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Co.Revington.Sightline;
using Co.Revington.Sightline.Commands;
using Co.Revington.Sightline.Live;
using Co.Revington.Sightline.Mcp;
using Co.Revington.Sightline.Server;
using Co.Revington.Sightline.Setup;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

return Cli.Parse(args) switch
{
    ExitCommand exit => exit.Code,
    InitCommand init => await Init.RunAsync(init.Options),
    CheckCommand check => await CheckRunner.RunAsync(check),
    ExportCommand export => await Exporter.RunAsync(export),
    DiffCommand diff => await MapDiff.RunAsync(diff),
    McpCommand mcp => await McpServer.RunAsync(mcp),
    MapCommand map => await OpenMapAsync(map.Options),
    _ => 1,
};

static async Task<int> OpenMapAsync(MapOptions options)
{
    // Started beside an app: when this solution already has a map open, join it instead of opening another.
    // The app reads these two lines, and reports its services, endpoints and requests to the map with the token.
    if (options.AttachPid is int joining && await Instances.JoinAsync(options.Target, joining) is { } open)
    {
        Console.WriteLine($"sightline-token: {open.Token}");
        Console.WriteLine($"sightline-url: {open.Url}");
        return 0;
    }

    var port = FindFreePort(options.Port);
    var token = options.Token ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    var hub = new EventHub();
    var session = new LiveSession(options.Target, hub, new LoadOptions(options.Filter, options.Restore, options.Checks));
    var companions = new Companions(closeWithApps: options.AttachPid is not null, Companions.DefaultGrace);
    var server = new ServerOptions(companions, options.Config?.Dir, options.Config?.Editor);
    var app = WebApp.Build(session, hub, port, token, options.UiDir, server);

    await app.StartAsync();
    Instances.Register(options.Target, port, token);

    var url = $"http://localhost:{port}";
    if (options.AttachPid is int parent)
    {
        companions.Attach(parent);
        Console.WriteLine($"sightline-token: {token}");
        Console.WriteLine($"sightline-url: {url}");
    }

    Console.WriteLine($"Sightline {ToolInfo.Version}");
    Console.WriteLine($"  target  {options.Target}");
    if (options.Config is not null) Console.WriteLine($"  config  {options.Config.File}");
    Console.WriteLine($"  url     {url}");
    if (options.Config is null && options.AttachPid is null)
        Console.WriteLine("  tip     \"sightline init\" saves this setup for everyone who works on the solution");

    var stopping = app.Lifetime.ApplicationStopping;
    var loop = Task.Run(() => session.RunAsync(stopping));
    var watch = Task.Run(() => companions.WatchAsync(app.Lifetime.StopApplication, stopping));

    if (options.Open)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser available: the URL is printed above */ }
    }

    await app.WaitForShutdownAsync();
    Instances.Unregister(options.Target);
    await loop;
    await watch;
    return 0;
}

static int FindFreePort(int preferred)
{
    for (var port = preferred; port < preferred + 50; port++)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return port;
        }
        catch (SocketException)
        {
        }
    }
    return preferred;
}

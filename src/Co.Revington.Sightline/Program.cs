using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Co.Revington.Sightline;
using Co.Revington.Sightline.Live;
using Co.Revington.Sightline.Server;
using Co.Revington.Sightline.Setup;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

return Cli.Parse(args) switch
{
    ExitCommand exit => exit.Code,
    InitCommand init => await Init.RunAsync(init.Options),
    MapCommand map => await OpenMapAsync(map.Options),
    _ => 1,
};

static async Task<int> OpenMapAsync(MapOptions options)
{
    var port = FindFreePort(options.Port);
    var token = options.Token ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    var hub = new EventHub();
    var session = new LiveSession(options.Target, hub, new LoadOptions(options.Filter, options.Restore));
    var app = WebApp.Build(session, hub, port, token, options.UiDir);

    await app.StartAsync();

    var url = $"http://localhost:{port}";
    Console.WriteLine($"Sightline {ToolInfo.Version}");
    Console.WriteLine($"  target  {options.Target}");
    if (options.Config is not null) Console.WriteLine($"  config  {options.Config.File}");
    Console.WriteLine($"  url     {url}");
    if (options.Config is null) Console.WriteLine("  tip     \"sightline init\" saves this setup for everyone who works on the solution");

    var loop = Task.Run(() => session.RunAsync(app.Lifetime.ApplicationStopping));

    if (options.Open)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser available: the URL is printed above */ }
    }

    await app.WaitForShutdownAsync();
    await loop;
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

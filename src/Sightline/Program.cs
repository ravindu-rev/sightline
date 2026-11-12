using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Sightline;
using Sightline.Live;
using Sightline.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

var options = CliOptions.Parse(args);
if (options is null) return 1;

var port = FindFreePort(options.Port);
var token = options.Token ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
var hub = new EventHub();
var session = new LiveSession(options.Target, hub);
var app = WebApp.Build(session, hub, port, token, options.UiDir);

await app.StartAsync();

var url = $"http://localhost:{port}";
Console.WriteLine("Sightline");
Console.WriteLine($"  target  {options.Target}");
Console.WriteLine($"  url     {url}");

var loop = Task.Run(() => session.RunAsync(app.Lifetime.ApplicationStopping));

if (options.Open)
{
    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
    catch { /* no browser available: the URL is printed above */ }
}

await app.WaitForShutdownAsync();
await loop;
return 0;

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

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Co.Revington.Sightline.Tests;

/// Co.Revington.Sightline.Hosting end to end: the package is packed, added to a small web app,
/// and the app is run the way a developer runs it. Slow, so HostingFixture builds everything once.
public sealed partial class HostingPackageTests(HostingFixture hosting) : IClassFixture<HostingFixture>
{
    [Fact]
    public void Adds_no_warnings_to_the_app_even_with_every_analysis_rule_on()
    {
        Assert.DoesNotMatch("Sightline(AutoStart|Hosting|Runtime)\\.cs", hosting.StrictBuildOutput);
    }

    [Fact]
    public async Task A_debug_build_opens_the_map_beside_the_app_and_closes_it_after_the_app_stops()
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };

        using var first = hosting.Start("Debug");
        var line = await first.WaitForLineAsync("Sightline: ", TimeSpan.FromMinutes(2));
        Assert.Contains("the live map of this solution is at http://localhost:", line);
        Assert.Contains("(or open /sightline on this app)", line);
        var map = MapAddress().Match(line).Value;

        // /sightline on the app leads to the map, and the map is there.
        var redirect = await http.GetAsync($"http://127.0.0.1:{first.Port}/sightline");
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal(map, redirect.Headers.Location!.ToString().TrimEnd('/'));
        var page = await http.GetStringAsync(map);
        Assert.Contains("<title>Sightline</title>", page);

        // The running app tells the map its address and endpoints, and reports the requests it handles.
        http.DefaultRequestHeaders.Add("X-Sightline-Token", TokenOf().Match(page).Groups[1].Value);
        var apps = await EventuallyAsync(() => http.GetStringAsync(map + "/api/runtime"), json => json.Contains($"127.0.0.1:{first.Port}"));
        Assert.Contains("\"app\":\"App\"", apps);
        Assert.Equal("hello", await http.GetStringAsync($"http://127.0.0.1:{first.Port}/"));
        // The request is matched to its endpoint once the map has read the solution.
        var traces = await EventuallyAsync(() => http.GetStringAsync(map + "/api/traces"), json => json.Contains("\"endpoint\":\"ep:App:GET /\""));
        Assert.Contains("\"path\":\"/\"", traces);
        await EventuallyAsync(() => http.GetStringAsync(map + "/api/graph"), json => json.Contains("\"runtime\":\"seen\""));

        // A second copy of the app joins the same map instead of opening another.
        using (var second = hosting.Start("Debug"))
            Assert.Contains(map, await second.WaitForLineAsync("Sightline: ", TimeSpan.FromMinutes(1)));

        // The app's output ends as soon as the app stops (the map must not hold it open)...
        Assert.True(await first.StopAsync(TimeSpan.FromSeconds(5)), "The app's output stayed open after the app stopped.");

        // ...and the map closes by itself shortly after the last app is gone.
        var waited = Stopwatch.StartNew();
        while (await RespondsAsync(http, map))
        {
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(45), "The map did not close after the apps stopped.");
            await Task.Delay(500);
        }
    }

    [Fact]
    public async Task A_release_build_gets_nothing()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var app = hosting.Start("Release");
        await app.WaitForLineAsync("Now listening on", TimeSpan.FromMinutes(1));
        await Task.Delay(TimeSpan.FromSeconds(3));

        Assert.DoesNotContain(app.Lines, l => l.StartsWith("Sightline:", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"http://127.0.0.1:{app.Port}/sightline")).StatusCode);
    }

    /// Asks again until the answer satisfies the condition: the app reports a moment after it starts.
    static async Task<string> EventuallyAsync(Func<Task<string>> ask, Func<string, bool> condition)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            var answer = await ask();
            if (condition(answer)) return answer;
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(30), "The map did not receive the report in time. Last answer: " + answer);
            await Task.Delay(300);
        }
    }

    [GeneratedRegex("name=\"sightline-token\" content=\"([^\"]+)\"")]
    private static partial Regex TokenOf();

    static async Task<bool> RespondsAsync(HttpClient http, string url)
    {
        try
        {
            using var response = await http.GetAsync(url);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"http://localhost:\d+")]
    private static partial Regex MapAddress();
}

/// Packs the Hosting package into a private feed, and builds a small web app that uses it,
/// in Debug and in Release. Uses its own NuGet package folder, so nothing leaks into the machine's.
public sealed class HostingFixture : IAsyncLifetime
{
    const string Version = "0.0.0-e2e";

    readonly string _dir = Directory.CreateTempSubdirectory("sightline-hosting-").FullName;
    readonly List<RunningApp> _apps = [];

    string Packages => Path.Combine(_dir, "packages");
    string AppDir => Path.Combine(_dir, "app");

    public string StrictBuildOutput { get; private set; } = "";

    public async Task InitializeAsync()
    {
        var root = ShopFixture.FindRepoRoot();
        var feed = Path.Combine(_dir, "feed");
        var project = Path.Combine(root, "src", "Co.Revington.Sightline.Hosting", "Co.Revington.Sightline.Hosting.csproj");
        await DotnetAsync(root, "pack", project, "-c", "Debug", "-o", feed, $"-p:Version={Version}");

        Directory.CreateDirectory(AppDir);
        File.WriteAllText(Path.Combine(_dir, "nuget.config"),
            $"""<configuration><packageSources><clear /><add key="local" value="{feed}" /></packageSources></configuration>""");
        File.WriteAllText(Path.Combine(_dir, "sightline.json"), """{ "solution": "app/App.csproj" }""");
        File.WriteAllText(Path.Combine(AppDir, "App.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Co.Revington.Sightline.Hosting" Version="{Version}" PrivateAssets="all" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(AppDir, "Program.cs"), """
            var app = WebApplication.CreateBuilder(args).Build();
            app.MapGet("/", () => "hello");
            app.Run();
            """);

        // Every analysis rule on, to catch any warning the package's files would add to someone's build.
        StrictBuildOutput = await DotnetAsync(AppDir, "build", "-c", "Debug", "-p:AnalysisLevel=latest-all",
            "-p:EnforceCodeStyleInBuild=true", "-p:TreatWarningsAsErrors=false", "--no-incremental");
        await DotnetAsync(AppDir, "build", "-c", "Debug");
        await DotnetAsync(AppDir, "build", "-c", "Release");
    }

    public async Task DisposeAsync()
    {
        foreach (var app in _apps) app.Dispose();

        // Give the maps time to close before their folder goes away.
        for (var i = 0; i < 30; i++)
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(1000);
            }
        }
    }

    /// Runs the app's Debug or Release build in Development, on a free port.
    public RunningApp Start(string configuration)
    {
        var port = FreePort();
        var exe = Path.Combine(AppDir, "bin", configuration, "net10.0", OperatingSystem.IsWindows() ? "App.exe" : "App");
        var info = new ProcessStartInfo(exe)
        {
            WorkingDirectory = AppDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        info.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        info.Environment["NUGET_PACKAGES"] = Packages;

        // The starter stays off on build servers; this test runs on one, so it pretends otherwise.
        foreach (var name in new[] { "CI", "GITHUB_ACTIONS", "TF_BUILD" }) info.Environment.Remove(name);

        var app = new RunningApp(Process.Start(info)!, port);
        _apps.Add(app);
        return app;
    }

    async Task<string> DotnetAsync(string dir, params string[] args)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment["NUGET_PACKAGES"] = Packages;
        info.Environment["DOTNET_NOLOGO"] = "1";

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var text = await output + await errors;
        if (process.ExitCode != 0) throw new InvalidOperationException($"dotnet {string.Join(' ', args)} failed:\n{text}");
        return text;
    }

    static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

/// A running copy of the test app, with its output collected line by line.
public sealed class RunningApp : IDisposable
{
    readonly Process _process;
    readonly ConcurrentQueue<string> _lines = new();
    readonly TaskCompletionSource _outputEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RunningApp(Process process, int port)
    {
        _process = process;
        Port = port;
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) _outputEnded.TrySetResult();
            else _lines.Enqueue(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) _lines.Enqueue(e.Data);
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    public int Port { get; }
    public IReadOnlyCollection<string> Lines => _lines;

    /// The first line that starts with the text given, ignoring indentation (ASP.NET Core indents its log messages).
    public async Task<string> WaitForLineAsync(string start, TimeSpan timeout)
    {
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < timeout)
        {
            var line = _lines.FirstOrDefault(l => l.TrimStart().StartsWith(start, StringComparison.Ordinal));
            if (line is not null) return line;
            Assert.False(_process.HasExited, "The app stopped early:\n" + string.Join('\n', _lines));
            await Task.Delay(200);
        }
        throw new TimeoutException($"No line starting with \"{start}\" within {timeout}. Output:\n{string.Join('\n', _lines)}");
    }

    /// Stops the app only (not the map), and says whether its output then ended within the time given.
    public async Task<bool> StopAsync(TimeSpan outputTimeout)
    {
        if (!_process.HasExited) _process.Kill();
        return await Task.WhenAny(_outputEnded.Task, Task.Delay(outputTimeout)) == _outputEnded.Task;
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited) _process.Kill();
        }
        catch (InvalidOperationException)
        {
        }
        _process.Dispose();
    }
}

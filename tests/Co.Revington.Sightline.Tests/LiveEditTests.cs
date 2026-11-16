using System.Diagnostics;
using MapGraph = Co.Revington.Sightline.Analysis.Graph;
using Co.Revington.Sightline.Live;
using Co.Revington.Sightline.Settings;

namespace Co.Revington.Sightline.Tests;

/// What the map does while files change on disk, on a small solution of its own: edits, new and deleted
/// files, a declaration that other projects use, a project left out by the settings, and unsaved text
/// that has to survive a reload.
public sealed class LiveEditTests : IAsyncLifetime
{
    readonly string _dir = Directory.CreateTempSubdirectory("sightline-live-").FullName;
    readonly CancellationTokenSource _stop = new();
    LiveSession _session = null!;
    Task? _loop;

    public async Task InitializeAsync()
    {
        Write("Lib/Lib.csproj", Project());
        Write("Lib/Greeter.cs", "namespace Lib; public class Greeter { public string Hello() => \"hi\"; }");
        Write("Hidden/Hidden.csproj", Project());
        Write("Hidden/Thing.cs", "namespace Hidden; public class Thing { public int Count() => 1; }");
        Write("App/App.csproj", Project("../Lib/Lib.csproj", "../Hidden/Hidden.csproj"));
        Write("App/Runner.cs", """
            namespace App;
            public class Runner
            {
                public void Run()
                {
                    new Lib.Greeter().Hello();
                    new Hidden.Thing().Count();
                }
            }
            """);
        Write("All.slnx", """
            <Solution>
              <Project Path="App/App.csproj" />
              <Project Path="Lib/Lib.csproj" />
              <Project Path="Hidden/Hidden.csproj" />
            </Solution>
            """);

        _session = new LiveSession(Path.Combine(_dir, "All.slnx"), new EventHub(),
            new LoadOptions(new ProjectFilter(_dir, ["Hidden"]), Restore: true));
        _loop = Task.Run(() => _session.RunAsync(_stop.Token));

        var waited = Stopwatch.StartNew();
        while (_session.Status.State == "loading")
        {
            if (waited.Elapsed > TimeSpan.FromMinutes(4)) throw new TimeoutException("The solution did not load: " + _session.Status.Message);
            await Task.Delay(200);
        }
        Assert.Equal("ready", _session.Status.State);
    }

    public async Task DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null) await _loop;
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static string Project(params string[] references) =>
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup>" +
        string.Concat(references.Select(r => $"<ProjectReference Include=\"{r}\" />")) + "</ItemGroup></Project>";

    string Write(string relative, string text)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    MapGraph Now => _session.Snapshot.Graph!;

    static string Label(MapGraph g, string id) =>
        g.Nodes.TryGetValue(id, out var n) && n.Kind == "member" && n.Parent is not null && g.Nodes.TryGetValue(n.Parent, out var owner)
            ? $"{owner.Name}.{n.Name}"
            : g.Nodes.TryGetValue(id, out var node) ? node.Name : id;

    static bool Calls(MapGraph g, string from, string to) =>
        g.Edges.Values.Any(e => e.Kind == "calls" && Label(g, e.Source) == from && Label(g, e.Target) == to);

    static bool HasType(MapGraph g, string name) => g.Nodes.Values.Any(n => n.Kind == "type" && n.Name == name);

    async Task Eventually(Func<MapGraph, bool> condition, string what)
    {
        var waited = Stopwatch.StartNew();
        while (!condition(Now))
        {
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(30), "The map did not show this in time: " + what);
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task The_map_follows_edits_new_and_deleted_files_and_declarations_used_elsewhere()
    {
        Assert.True(Calls(Now, "Runner.Run", "Greeter.Hello"));
        Assert.False(HasType(Now, "Thing"));                       // Hidden is left out by the settings

        // A new method, then a call to it from another project.
        Write("Lib/Greeter.cs", "namespace Lib; public class Greeter { public string Hello() => \"hi\"; public string Bye() => \"bye\"; }");
        await Eventually(g => g.Nodes.Values.Any(n => n.Kind == "member" && Label(g, n.Id) == "Greeter.Bye"), "Greeter.Bye");
        var runner = File.ReadAllText(Path.Combine(_dir, "App", "Runner.cs"));
        Write("App/Runner.cs", runner.Replace("new Lib.Greeter().Hello();", "new Lib.Greeter().Hello();\n        new Lib.Greeter().Bye();"));
        await Eventually(g => Calls(g, "Runner.Run", "Greeter.Bye"), "Runner.Run calls Greeter.Bye");

        // Renaming the method in Lib changes what the unchanged file in App binds to.
        Write("Lib/Greeter.cs", "namespace Lib; public class Greeter { public string Hello() => \"hi\"; public string Farewell() => \"bye\"; }");
        await Eventually(g => !Calls(g, "Runner.Run", "Greeter.Bye") && g.Nodes.Values.Any(n => Label(g, n.Id) == "Greeter.Farewell"),
            "the call to the renamed method is gone");

        // New and deleted files.
        var extra = Write("App/Extra.cs", "namespace App; public class Extra { }");
        await Eventually(g => HasType(g, "Extra"), "the new class Extra");
        File.Delete(extra);
        await Eventually(g => !HasType(g, "Extra"), "the deleted class Extra is gone");

        // An edit in the project that is left out keeps it off the map, and the rest keeps working.
        Write("Hidden/Thing.cs", "namespace Hidden; public class Thing { public int Count() => 2; } public class Other { }");
        Write("App/Runner.cs", File.ReadAllText(Path.Combine(_dir, "App", "Runner.cs")).Replace("new Lib.Greeter().Bye();", "new Lib.Greeter().Farewell();"));
        await Eventually(g => Calls(g, "Runner.Run", "Greeter.Farewell"), "Runner.Run calls Greeter.Farewell");
        Assert.False(HasType(Now, "Thing"));
        Assert.False(HasType(Now, "Other"));
        Assert.Single(Now.Nodes.Values, n => n.Kind == "type" && n.Name == "Runner");
    }

    [Fact]
    public async Task Unsaved_text_survives_a_reload_and_goes_when_discarded()
    {
        var file = Path.Combine(_dir, "Lib", "Greeter.cs");
        _session.Enqueue(new OverlayWork(file, "namespace Lib; public class Greeter { public string Hello() => \"hi\"; public void Unsaved() { Hello(); } }"));
        await Eventually(g => Calls(g, "Greeter.Unsaved", "Greeter.Hello"), "the unsaved method");

        var before = _session.Snapshot.Version;
        _session.Enqueue(new ReloadWork());
        await Eventually(_ => _session.Snapshot.Version > before && _session.Status.State == "ready", "a reload");
        Assert.True(Calls(Now, "Greeter.Unsaved", "Greeter.Hello"));
        Assert.True((await _session.ReadSourceAsync(file))!.Value.Dirty);

        _session.Enqueue(new OverlayWork(file, null));
        await Eventually(g => !Calls(g, "Greeter.Unsaved", "Greeter.Hello"), "the unsaved method is gone");
        Assert.False((await _session.ReadSourceAsync(file))!.Value.Dirty);
    }
}

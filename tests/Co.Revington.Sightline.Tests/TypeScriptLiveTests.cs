using System.Diagnostics;
using Co.Revington.Sightline.Commands;
using Co.Revington.Sightline.Live;
using Co.Revington.Sightline.Settings;
using MapGraph = Co.Revington.Sightline.Analysis.Graph;

namespace Co.Revington.Sightline.Tests;

/// What the map does while TypeScript files change on disk, on a package of its own.
public sealed class TypeScriptLiveTests : IAsyncLifetime
{
    readonly string _dir = Directory.CreateTempSubdirectory("sightline-ts-live-").FullName;
    readonly CancellationTokenSource _stop = new();
    LiveSession _session = null!;
    Task? _loop;

    public async Task InitializeAsync()
    {
        Write("package.json", """{ "name": "live-app", "dependencies": { "express": "^5.1.0" } }""");
        Write("src/greeter.ts", "export class Greeter {\n  hello() { return 'hi'; }\n}\n");
        Write("src/runner.ts", "import { Greeter } from './greeter';\n\nexport function run() {\n  return new Greeter().hello();\n}\n");

        _session = new LiveSession(_dir, new EventHub(), new LoadOptions(ProjectFilter.None, Restore: false, Log: TextWriter.Null));
        _loop = Task.Run(() => _session.RunAsync(_stop.Token));
        var waited = Stopwatch.StartNew();
        while (_session.Status.State == "loading")
        {
            if (waited.Elapsed > TimeSpan.FromMinutes(2)) throw new TimeoutException(_session.Status.Message);
            await Task.Delay(100);
        }
        Assert.Equal("ready", _session.Status.State);
    }

    public async Task DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null) await _loop;
        _session.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

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

    static bool HasMember(MapGraph g, string label) => g.Nodes.Values.Any(n => n.Kind == "member" && Label(g, n.Id) == label);

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
        Assert.True(Calls(Now, "runner.run", "Greeter.hello"));

        Write("src/greeter.ts", "export class Greeter {\n  hello() { return 'hi'; }\n  bye() { return 'bye'; }\n}\n");
        await Eventually(g => HasMember(g, "Greeter.bye"), "the new method");
        Write("src/runner.ts", "import { Greeter } from './greeter';\n\nexport function run() {\n  new Greeter().hello();\n  return new Greeter().bye();\n}\n");
        await Eventually(g => Calls(g, "runner.run", "Greeter.bye"), "runner.run calls Greeter.bye");

        // Renaming the method changes what the unchanged runner.ts calls.
        Write("src/greeter.ts", "export class Greeter {\n  hello() { return 'hi'; }\n  farewell() { return 'bye'; }\n}\n");
        await Eventually(g => !Calls(g, "runner.run", "Greeter.bye") && HasMember(g, "Greeter.farewell"), "the call to the renamed method is gone");

        var extra = Write("src/extra.ts", "export class Extra {}\n");
        await Eventually(g => g.Nodes.Values.Any(n => n.Name == "Extra"), "the new class");
        File.Delete(extra);
        await Eventually(g => !g.Nodes.Values.Any(n => n.Name == "Extra"), "the deleted class is gone");

        // A new dependency in package.json loads the package again.
        Write("package.json", """{ "name": "live-app", "dependencies": { "express": "^5.1.0", "zod": "^4.0.0" } }""");
        await Eventually(g => g.Nodes.ContainsKey("pkg:npm:zod"), "the new package");

        // A package that has no code yet joins the map with its first file.
        var version = _session.Snapshot.Version;
        Write("other/package.json", """{ "name": "other" }""");
        await Eventually(_ => _session.Snapshot.Version > version, "a reload for the new package.json");
        Write("other/src/index.ts", "export class Other {}\n");
        await Eventually(g => g.Nodes.ContainsKey("proj:npm:other") && g.Nodes.Values.Any(n => n.Name == "Other"), "the new package with its first file");
    }

    [Fact]
    public async Task A_package_that_arrives_as_a_whole_folder_joins_the_map()
    {
        // A folder moved in raises one event, for the folder: none for the files in it. On Linux a new folder
        // is only watched once it is reported, so files written into it right away go unreported the same way.
        var staging = Directory.CreateTempSubdirectory("sightline-ts-moved-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(staging, "package.json"), """{ "name": "moved" }""");
            Directory.CreateDirectory(Path.Combine(staging, "src"));
            File.WriteAllText(Path.Combine(staging, "src", "index.ts"), "export class Moved {}\n");
            Directory.Move(staging, Path.Combine(_dir, "moved"));

            await Eventually(g => g.Nodes.ContainsKey("proj:npm:moved") && g.Nodes.Values.Any(n => n.Name == "Moved"), "the package that was moved in");
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    [Fact]
    public async Task A_file_reached_through_a_linked_folder_is_never_read()
    {
        var outside = Directory.CreateTempSubdirectory("sightline-ts-outside-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(outside, "secret.ts"), "export class Secret {}\n");
            var link = Path.Combine(_dir, "src", "linked");
            Assert.True(Links.TryLinkFolder(link, outside), "Could not make a folder link.");

            // Handed to the session directly, as a watcher that followed the link would report it.
            var secret = Path.Combine(link, "secret.ts");
            _session.Enqueue(new FilesWork([secret]));
            Write("src/greeter.ts", "export class Greeter {\n  hello() { return 'hi'; }\n  wave() { return 'o/'; }\n}\n");
            await Eventually(g => HasMember(g, "Greeter.wave"), "an edit made after the linked file");

            Assert.DoesNotContain(Now.Nodes.Values, n => n.Name == "Secret");
            Assert.False(_session.IsTracked(secret));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task Unsaved_text_survives_a_reload_of_its_language_and_goes_when_discarded()
    {
        var file = Path.Combine(_dir, "src", "greeter.ts");
        _session.Enqueue(new OverlayWork(file, "export class Greeter {\n  hello() { return 'hi'; }\n  unsaved() { return this.hello(); }\n}\n"));
        await Eventually(g => Calls(g, "Greeter.unsaved", "Greeter.hello"), "the unsaved method");

        var before = _session.Snapshot.Version;
        _session.Enqueue(new ReloadWork("typescript"));
        await Eventually(_ => _session.Snapshot.Version > before && _session.Status.State == "ready", "a reload");
        Assert.True(Calls(Now, "Greeter.unsaved", "Greeter.hello"));

        _session.Enqueue(new OverlayWork(file, null));
        await Eventually(g => !Calls(g, "Greeter.unsaved", "Greeter.hello"), "the unsaved method is gone");
    }
}

/// A NestJS app with one of each provider mistake, read by "sightline check" the way CI would.
public sealed class NodeProblemsFixture : IAsyncLifetime
{
    readonly string _dir = Directory.CreateTempSubdirectory("sightline-nest-problems-").FullName;
    internal MapGraph Graph { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Write("package.json", """{ "name": "nest-app", "dependencies": { "@nestjs/common": "^11.0.0", "@nestjs/core": "^11.0.0" } }""");
        Write("tsconfig.json", """{ "compilerOptions": { "experimentalDecorators": true, "emitDecoratorMetadata": true } }""");
        Write("src/app.module.ts", """
            import { Module } from '@nestjs/common';
            import { UsersController } from './users';
            import { UsersService, A, B, Leftover, PerRequest, Holder, NeedsRepo, AuthGuard } from './services';

            @Module({
              controllers: [UsersController],
              providers: [UsersService, A, B, Leftover, PerRequest, Holder, NeedsRepo, AuthGuard],
            })
            export class AppModule {}
            """);
        Write("src/users.ts", """
            import { Controller, Get } from '@nestjs/common';
            import { UsersService, Mailer } from './services';

            @Controller('users')
            export class UsersController {
              constructor(private readonly users: UsersService, private readonly mailer: Mailer) {}

              @Get()
              list() { return this.users.all(); }
            }
            """);
        Write("src/services.ts", """
            import { Injectable, Scope, CanActivate } from '@nestjs/common';

            export interface Repo { find(): unknown; }

            @Injectable() export class Mailer {}
            @Injectable() export class UsersService { all() { return []; } }
            @Injectable() export class A { constructor(private readonly b: B) {} }
            @Injectable() export class B { constructor(private readonly a: A) {} }
            @Injectable() export class Leftover {}
            @Injectable({ scope: Scope.REQUEST }) export class PerRequest {}
            @Injectable() export class Holder { constructor(private readonly perRequest: PerRequest) {} }
            @Injectable() export class NeedsRepo { constructor(private readonly repo: Repo) {} }
            @Injectable() export class AuthGuard implements CanActivate { canActivate() { return true; } }
            """);

        var (session, snapshot) = await OneShot.LoadAsync(new SolutionTarget(_dir, null, ProjectFilter.None, Restore: false));
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

public sealed class NodeProblemsTests(NodeProblemsFixture app) : IClassFixture<NodeProblemsFixture>
{
    Analysis.Problem One(string rule, string mentions) =>
        Assert.Single(app.Graph.Problems, p => p.Rule == rule && p.Message.Contains(mentions, StringComparison.Ordinal));

    [Fact]
    public void A_provider_that_no_module_provides_is_reported_the_way_nest_fixes_it()
    {
        var p = One("missing-registration", "Mailer");
        Assert.Equal("warning", p.Severity);
        Assert.Contains("providers: [Mailer]", p.Message);

        var iface = One("missing-registration", "Repo");
        Assert.Contains("@Inject('Repo')", iface.Message);
    }

    [Fact]
    public void Providers_that_need_each_other_are_an_error()
    {
        var p = One("dependency-cycle", "Creating");
        Assert.Equal("error", p.Severity);
        Assert.Contains("forwardRef", p.Message);
    }

    [Fact]
    public void Request_scope_and_framework_providers_are_no_false_alarms()
    {
        // Nest makes Holder request-scoped too, so holding PerRequest is fine.
        Assert.DoesNotContain(app.Graph.Problems, p => p.Rule == "captive-dependency");
        Assert.DoesNotContain(app.Graph.Problems, p => p.Rule == "unused-registration" && p.Message.StartsWith("AuthGuard", StringComparison.Ordinal));
        Assert.Equal("info", One("unused-registration", "Leftover").Severity);
    }
}

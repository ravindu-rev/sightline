using System.Diagnostics;
using Co.Revington.Sightline.Analysis;
using Co.Revington.Sightline.Commands;
using Co.Revington.Sightline.Languages;
using Co.Revington.Sightline.Live;
using Co.Revington.Sightline.Settings;
using MapGraph = Co.Revington.Sightline.Analysis.Graph;

namespace Co.Revington.Sightline.Tests;

/// JavaScript and TypeScript that must not turn into something it is not.
public sealed class TypeScriptEdgeCaseTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("sightline-ts-edges-").FullName;

    public void Dispose()
    {
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

    async Task<MapGraph> LoadAsync()
    {
        var (session, snapshot) = await OneShot.LoadAsync(new SolutionTarget(_dir, null, ProjectFilter.None, Restore: false));
        using (session) return snapshot.Graph!;
    }

    static string Label(MapGraph g, string id) =>
        g.Nodes.TryGetValue(id, out var n) && n.Kind == "member" && n.Parent is not null && g.Nodes.TryGetValue(n.Parent, out var owner)
            ? $"{owner.Name}.{n.Name}"
            : g.Nodes.TryGetValue(id, out var node) ? node.Name : id;

    [Fact]
    public async Task An_api_client_of_the_code_is_not_taken_for_a_router()
    {
        Write("package.json", """{ "name": "client", "dependencies": { "express": "^5.1.0" } }""");
        Write("src/api.ts", """
            export class ApiClient {
              async post(path: string, body: unknown) { return fetch(`/backend${path}`, { method: 'POST', body: JSON.stringify(body) }); }
            }
            export const api = new ApiClient();
            """);
        Write("src/orders.ts", """
            import { api } from './api';
            export async function placeOrder(order: { id: number }) {
              return api.post('/orders', order);
            }
            """);

        var g = await LoadAsync();

        Assert.DoesNotContain(g.Nodes.Values, n => n.Kind == "endpoint");
        Assert.Contains(g.Edges.Values, e => e.Kind == "calls" && Label(g, e.Source) == "orders.placeOrder" && Label(g, e.Target) == "ApiClient.post");
    }

    [Fact]
    public async Task CommonJS_classes_keep_one_id_from_their_file_to_the_code_that_uses_them()
    {
        Write("package.json", """{ "name": "cjs" }""");
        Write("src/store.js", "module.exports = class Store {\n  save(x) { return x; }\n};\n");
        Write("src/cache.js", "exports.Cache = class {\n  get(key) { return key; }\n};\n");
        Write("src/index.js", """
            const Store = require('./store');
            const { Cache } = require('./cache');
            function run() {
              new Store().save(1);
              return new Cache().get('k');
            }
            module.exports = { run };
            """);

        var g = await LoadAsync();

        Assert.Contains(g.Edges.Values, e => e.Kind == "calls" && Label(g, e.Source) == "src/index.run" && Label(g, e.Target) == "Store.save");
        Assert.Contains(g.Edges.Values, e => e.Kind == "calls" && Label(g, e.Source) == "src/index.run" && Label(g, e.Target) == "Cache.get");
    }

    [Fact]
    public async Task A_linked_file_is_never_read_as_code_of_the_repository()
    {
        var outside = Directory.CreateTempSubdirectory("sightline-outside-").FullName;
        try
        {
            var secret = Path.Combine(outside, "secret.txt");
            File.WriteAllText(secret, "export const token = 'do not show';\n");
            Write("package.json", """{ "name": "linked" }""");
            Write("src/real.ts", "export const real = 1;\n");
            try
            {
                File.CreateSymbolicLink(Path.Combine(_dir, "src", "link.ts"), secret);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;                                  // this machine does not allow links; nothing to check
            }

            var (session, snapshot) = await OneShot.LoadAsync(new SolutionTarget(_dir, null, ProjectFilter.None, Restore: false));
            using (session)
            {
                Assert.False(session.IsTracked(Path.Combine(_dir, "src", "link.ts")));
                Assert.True(session.IsTracked(Path.Combine(_dir, "src", "real.ts")));
                Assert.DoesNotContain("do not show", snapshot.ToJson(session.StatusJson));
            }
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }
}

/// A language whose reader fails while following edits: the other languages go on, and it loads again once.
public sealed class SessionRecoveryTests
{
    /// failOnApply: every edit fails, as when the reader process has stopped. failLoadsAfterFirst: it cannot be started again.
    sealed class FakeWorkspace(string language, string dir, bool failOnApply, bool failLoadsAfterFirst = false) : ILanguageWorkspace
    {
        public int Loads;
        string _name = "First";

        public string Language => language;
        public IReadOnlyList<ProjectFacts> Projects { get; } = [new ProjectFacts { Id = $"proj:{language}", Name = language, Path = dir, Dir = dir, Kind = "lib", Language = "TypeScript" }];
        public IEnumerable<DocFacts> Facts
        {
            get
            {
                var doc = new DocFacts { File = Path.Combine(dir, language + ".ts"), Project = $"proj:{language}" };
                doc.Types.Add(new TypeFact($"{language}::{_name}", _name + language, "", "class", "public", null, null, null, [], [], [], 1, 1));
                yield return doc;
            }
        }
        public IReadOnlyList<string> Diagnostics => [];
        public int Excluded => 0;
        public IEnumerable<string> WatchRoots => [];
        public bool SyncPending => false;

        public Task LoadAsync(LoadContext context, CancellationToken ct)
        {
            if (Interlocked.Increment(ref Loads) > 1 && failLoadsAfterFirst) throw new SidecarException("The reader does not start.");
            return Task.CompletedTask;
        }

        public bool Tracks(string path) => path.EndsWith(language + ".ts", StringComparison.Ordinal);
        public FileRole RoleOf(string path) => Tracks(path) ? FileRole.Source : FileRole.None;
        public Task<string?> ReadTextAsync(string path, CancellationToken ct) => Task.FromResult<string?>("");

        public Task<IReadOnlyList<string>> ApplyAsync(IReadOnlyList<FileChange> changes, CancellationToken ct)
        {
            if (failOnApply) throw new SidecarException("The reader stopped.");
            _name = "Second";
            return Task.FromResult<IReadOnlyList<string>>([.. changes.Select(c => c.Path)]);
        }

        public Task<bool> SyncAsync(Func<bool> interrupted, CancellationToken ct) => Task.FromResult(true);
        public void DropSync() { }
        public void Dispose() { }
    }

    [Fact]
    public async Task A_failing_language_does_not_hold_back_the_others_and_loads_again_once()
    {
        var dir = Directory.CreateTempSubdirectory("sightline-recovery-").FullName;
        var good = new FakeWorkspace("good", dir, failOnApply: false);
        var bad = new FakeWorkspace("bad", dir, failOnApply: true, failLoadsAfterFirst: true);
        using var session = new LiveSession(dir, new EventHub(), new LoadOptions(ProjectFilter.None, Restore: false, Watch: false, Log: TextWriter.Null), [good, bad]);
        using var stop = new CancellationTokenSource();
        var loop = Task.Run(() => session.RunAsync(stop.Token));
        try
        {
            await Until(() => session.Status.State == "ready" && bad.Loads == 1);

            session.Enqueue(new FilesWork([Path.Combine(dir, "good.ts"), Path.Combine(dir, "bad.ts")]));
            await Until(() => session.Snapshot.Graph!.Nodes.Values.Any(n => n.Name == "Secondgood"));     // the good language's edit is on the map
            await Until(() => bad.Loads == 2);                                                            // the failing one loads again

            session.Enqueue(new FilesWork([Path.Combine(dir, "bad.ts")]));
            await Task.Delay(1500);
            Assert.Equal(2, bad.Loads);                                                                   // a reload that failed is not tried over and over
            Assert.Equal(1, good.Loads);
        }
        finally
        {
            stop.Cancel();
            await loop;
            Directory.Delete(dir, recursive: true);
        }
    }

    static async Task Until(Func<bool> condition)
    {
        var waited = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(15), "Timed out.");
            await Task.Delay(50);
        }
    }
}

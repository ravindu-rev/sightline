using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Co.Revington.Sightline.Analysis;
using Co.Revington.Sightline.Languages;
using Co.Revington.Sightline.Settings;

namespace Co.Revington.Sightline.Live;

internal abstract record Work;

/// Only: the language to load again ("csharp", "typescript"), or null for all of them.
internal sealed record ReloadWork(string? Only = null) : Work;
internal sealed record FilesWork(IReadOnlyList<string> Paths) : Work;

/// Editor text for a file. Text = null drops the unsaved buffer and goes back to disk.
internal sealed record OverlayWork(string Path, string? Text, bool Saved = false) : Work;

/// What the running apps reported about themselves; see RuntimeMerge.
internal sealed record RuntimeWork(IReadOnlyList<RuntimeApp> Apps) : Work;

internal sealed record StatusInfo(string State, string Message);

/// Filter: projects to leave off the map. Restore: run "dotnet restore" for projects that were never restored.
/// Checks: settings for the problems the map reports. Watch: follow file changes after loading (off for
/// one-shot commands such as "sightline check"). Log: where progress goes (stdout by default).
/// Languages: the languages to read ("csharp", "typescript"), or null for every one found under the target.
/// PackagesFrom: for "diff", where a checkout without installed npm packages borrows them from.
internal sealed record LoadOptions(
    ProjectFilter Filter, bool Restore, CheckOptions? Checks = null, bool Watch = true, TextWriter? Log = null,
    IReadOnlyList<string>? Languages = null, PackageMirror? PackagesFrom = null)
{
    public static readonly LoadOptions Default = new(ProjectFilter.None, Restore: true);
}

/// Owns the code that is loaded, in every language, and keeps the graph in step with edits.
/// All changes run on one loop, so state never needs fine-grained locking.
internal sealed class LiveSession : IDisposable
{
    readonly string _target;
    readonly EventHub _hub;
    readonly LoadOptions _options;
    readonly Channel<Work> _queue = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions { SingleReader = true });
    readonly object _gate = new();
    readonly Timer _reloadTimer;
    readonly HashSet<string> _reloadDue = [];                    // languages to load again; "*" for all
    readonly List<ILanguageWorkspace> _workspaces;
    readonly Dictionary<string, string> _overlays = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _unavailable = [];      // language -> why it is not on the map
    readonly HashSet<string> _recovering = [];                    // languages loaded again after their reader failed

    FileWatcher? _watcher;
    IReadOnlyList<RuntimeApp> _runtime = [];
    bool _loaded;

    volatile Snapshot _snapshot = Snapshot.Empty;
    volatile StatusInfo _status = new("loading", "Starting");

    public LiveSession(string target, EventHub hub, LoadOptions? options = null)
        : this(target, hub, options ?? LoadOptions.Default, LanguageSet.For(target, options?.Languages))
    {
    }

    /// With the workspaces given, for tests.
    internal LiveSession(string target, EventHub hub, LoadOptions options, List<ILanguageWorkspace> workspaces)
    {
        _target = target;
        _hub = hub;
        _options = options;
        RootDir = Directory.Exists(target) ? target : Path.GetDirectoryName(target)!;
        Name = Directory.Exists(target) || IsPackageManifest(target) ? new DirectoryInfo(RootDir).Name : Path.GetFileName(target);
        _workspaces = workspaces;
        _reloadTimer = new Timer(_ => ReloadDue(), null, Timeout.Infinite, Timeout.Infinite);
    }

    static bool IsPackageManifest(string path) => Path.GetFileName(path).Equals("package.json", StringComparison.OrdinalIgnoreCase);

    public string RootDir { get; }
    public string Name { get; }
    public Snapshot Snapshot => _snapshot;
    public StatusInfo Status => _status;
    public string StatusJson => JsonSerializer.Serialize(_status, Snapshot.Json);

    sealed record GraphCache(int Version, string Status, string Json);

    volatile GraphCache? _graphJson;

    /// The whole graph as the page fetches it. Built once per version: on a big solution it is tens of megabytes.
    /// Requests come in on several threads, so the cache is one object, swapped whole.
    public string GraphJson()
    {
        var snapshot = _snapshot;
        var status = StatusJson;
        if (_graphJson is { } cached && cached.Version == snapshot.Version && cached.Status == status) return cached.Json;
        var json = snapshot.ToJson(status);
        _graphJson = new GraphCache(snapshot.Version, status, json);
        return json;
    }

    public void Enqueue(Work work) => _queue.Writer.TryWrite(work);

    /// The workspace whose map holds this file, if any. Any thread.
    ILanguageWorkspace? Owner(string path) => _workspaces.FirstOrDefault(w => w.Tracks(path));

    public bool IsTracked(string path) => Owner(path) is not null;

    public async Task<(string Text, bool Dirty)?> ReadSourceAsync(string path)
    {
        var owner = Owner(path);
        if (owner is null) return null;
        lock (_gate)
        {
            if (_overlays.TryGetValue(path, out var unsaved)) return (unsaved, true);
        }
        var text = await owner.ReadTextAsync(path, CancellationToken.None);
        return text is null ? null : (text, false);
    }

    public async Task<bool> SaveAsync(string path, string text)
    {
        if (!IsTracked(path)) return false;
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(HasBom(path)));
        Enqueue(new OverlayWork(path, text, Saved: true));
        return true;
    }

    static bool HasBom(string path)
    {
        try
        {
            using var s = File.OpenRead(path);
            return s.ReadByte() == 0xEF && s.ReadByte() == 0xBB && s.ReadByte() == 0xBF;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _reloadTimer.Dispose();
        _watcher?.Dispose();
        foreach (var w in _workspaces)
        {
            try { w.Dispose(); }
            catch (Exception ex) { Console.Error.WriteLine($"  error   {ex.Message}"); }
        }
    }

    // ---------------------------------------------------------------- loop

    public async Task RunAsync(CancellationToken ct)
    {
        Enqueue(new ReloadWork());
        try
        {
            while (await _queue.Reader.WaitToReadAsync(ct))
            {
                while (_queue.Reader.TryRead(out var work))
                {
                    work = Coalesce(work);
                    try
                    {
                        switch (work)
                        {
                            case ReloadWork r: await LoadAsync(r.Only, ct); break;
                            case FilesWork f: await ApplyFilesAsync(f.Paths, ct); break;
                            case OverlayWork o: await ApplyOverlayAsync(o, ct); break;
                            case RuntimeWork r:
                                _runtime = r.Apps;
                                if (_loaded) Publish("runtime", [], Stopwatch.StartNew());
                                break;
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        // A failed load is shown as the status (which prints it); anything else is printed here.
                        if (work is ReloadWork) SetStatus("error", ex.Message);
                        else Console.Error.WriteLine($"  error   {ex.Message}");
                    }
                }

                // Idle: refresh files whose meaning may have shifted after a declaration change.
                if (await SyncAsync(ct)) return;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// Drops work that a newer queued item makes pointless (typing fast, repeated reloads).
    Work Coalesce(Work work)
    {
        while (_queue.Reader.TryPeek(out var next))
        {
            Work? merged = (work, next) switch
            {
                (ReloadWork a, ReloadWork b) => new ReloadWork(a.Only == b.Only ? a.Only : null),
                (RuntimeWork, RuntimeWork) => next,
                (OverlayWork { Saved: false } a, OverlayWork b) when SamePath(a.Path, b.Path) => next,
                _ => null,
            };
            if (merged is null) break;
            _queue.Reader.TryRead(out _);
            work = merged;
        }
        return work;
    }

    static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    void SetStatus(string state, string message)
    {
        if (_status.State != state) (_options.Log ?? Console.Out).WriteLine($"  {state,-7} {message}");
        _status = new StatusInfo(state, message);
        _hub.Publish("status", StatusJson);
    }

    /// A project file changed: load that language again once the changes settle.
    void ScheduleReload(string? language)
    {
        lock (_reloadDue) _reloadDue.Add(language ?? "*");
        _reloadTimer.Change(700, Timeout.Infinite);
    }

    void ReloadDue()
    {
        string? only;
        lock (_reloadDue)
        {
            if (_reloadDue.Count == 0) return;
            only = _reloadDue.Count == 1 && !_reloadDue.Contains("*") ? _reloadDue.First() : null;
            _reloadDue.Clear();
        }
        Enqueue(new ReloadWork(only));
    }

    // ---------------------------------------------------------------- load

    async Task LoadAsync(string? only, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var context = new LoadContext(_target, RootDir, Name, _options.Filter, _options.Restore,
            message => SetStatus("loading", message), _options.PackagesFrom);

        foreach (var w in _workspaces)
        {
            if (only is not null && w.Language != only) continue;
            try
            {
                await w.LoadAsync(context, ct);
                _unavailable.Remove(w.Language);
                _recovering.Remove(w.Language);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (CanGoOnWithout(w, ex))
            {
                var message = ex is LanguageUnavailableException ? ex.Message : $"{LanguageSet.Title(w.Language)} could not be read: {ex.Message}";
                if (!_unavailable.TryGetValue(w.Language, out var before) || before != message)
                    (_options.Log ?? Console.Out).WriteLine($"  warning {message}");
                _unavailable[w.Language] = message;
            }
        }

        // Unsaved editor buffers survive a reload.
        foreach (var (path, text) in Overlays())
        {
            if (Owner(path) is not { } owner || (only is not null && owner.Language != only)) continue;
            try
            {
                await owner.ApplyAsync([new FileChange(path, text)], ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Recover(owner, ex);
            }
        }

        var graph = Build();
        _snapshot = Snapshot.From(graph, _snapshot.Version + 1, Info(graph));
        _loaded = true;
        _hub.Publish("reload", $"{{\"version\":{_snapshot.Version}}}");

        var count = (string kind) => graph.Nodes.Values.Count(n => n.Kind == kind);
        var excluded = _workspaces.Sum(w => w.Excluded);
        var left = excluded == 0 ? "" : $", {excluded} left out by {SightlineConfig.FileName}";
        SetStatus("ready", $"{count("project")} projects, {count("type")} types, {count("endpoint")} endpoints in {sw.Elapsed.TotalSeconds:0.0}s{left}");

        if (_options.Watch)
            _watcher ??= new FileWatcher(WatchRoots(), Accepts, () => _workspaces.Any(w => w is not CSharpWorkspace && w.Projects.Count > 0),
                paths => Enqueue(new FilesWork(paths)));
    }

    /// Whether the map can show the rest when one language fails to load. C# never: a solution that does
    /// not open is an error, as it always was. Another language can be left out when something else is on
    /// the map: always when the machine lacks what it needs (Node.js), and otherwise only on the live map,
    /// because a one-shot command such as "check" must not pass while part of the code went unread.
    bool CanGoOnWithout(ILanguageWorkspace failed, Exception ex)
    {
        if (failed is CSharpWorkspace) return false;
        if (!_workspaces.Any(w => w != failed && w.Projects.Count > 0)) return false;
        return ex is LanguageUnavailableException || _options.Watch;
    }

    /// One language failed while following an edit, for example because its reader process stopped. The others
    /// go on; that language is loaded again, once: if that fails too, it says so and waits for the next reload.
    void Recover(ILanguageWorkspace w, Exception ex)
    {
        Console.Error.WriteLine($"  error   {ex.Message}");
        if (w is not CSharpWorkspace && _recovering.Add(w.Language)) ScheduleReload(w.Language);
    }

    List<KeyValuePair<string, string>> Overlays()
    {
        lock (_gate) return [.. _overlays];
    }

    /// Loads once, without following changes. For one-shot commands (check, export, diff).
    public async Task<Snapshot> LoadOnceAsync(CancellationToken ct)
    {
        await LoadAsync(null, ct);
        if (_status.State != "ready") throw new InvalidOperationException(_status.Message);
        return _snapshot;
    }

    Graph Build() =>
        GraphBuilder.Build(_workspaces.SelectMany(w => w.Projects).ToList(), _workspaces.SelectMany(w => w.Facts), _options.Checks, _runtime);

    bool Accepts(string path) => _workspaces.Any(w => w.RoleOf(path) != FileRole.None);

    List<string> WatchRoots()
    {
        static bool Within(string dir, string root) =>
            string.Equals(dir.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || dir.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        var roots = new List<string> { RootDir };
        foreach (var dir in _workspaces.SelectMany(w => w.WatchRoots))
            if (!roots.Any(r => Within(dir, r))) roots.Add(dir);
        return roots;
    }

    object Info(Graph g)
    {
        int Count(string kind) => g.Nodes.Values.Count(n => n.Kind == kind);
        var diagnostics = _workspaces.SelectMany(w => w.Diagnostics)
            .Concat(_workspaces.Where(w => _unavailable.ContainsKey(w.Language)).Select(w => "Warning: " + _unavailable[w.Language]))
            .ToList();
        return new
        {
            name = Name,
            version = ToolInfo.Version,
            path = _target,
            root = RootDir,
            counts = new
            {
                projects = Count("project"), packages = Count("package"), types = Count("type"),
                members = Count("member"), endpoints = Count("endpoint"), services = g.Services.Count,
            },
            diagnostics,
            languages = _workspaces.SelectMany(w => w.Projects).Select(p => p.Language).Distinct().ToList(),
        };
    }

    // ---------------------------------------------------------------- edits

    async Task ApplyFilesAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        if (!_loaded) return;
        var sw = Stopwatch.StartNew();
        var changes = new Dictionary<ILanguageWorkspace, List<FileChange>>();
        var reload = new HashSet<string>();
        var everything = false;

        foreach (var raw in paths)
        {
            if (raw == FileWatcher.Rescan)
            {
                everything = true;
                continue;
            }

            var path = Path.GetFullPath(raw);
            foreach (var w in _workspaces)
            {
                switch (w.RoleOf(path))
                {
                    case FileRole.Project:
                        reload.Add(w.Language);
                        break;
                    case FileRole.Source:
                        bool unsaved;
                        lock (_gate) unsaved = _overlays.ContainsKey(path);
                        if (unsaved) break;                     // the editor buffer wins until saved or reverted
                        if (!changes.TryGetValue(w, out var list)) changes[w] = list = [];
                        list.Add(new FileChange(path, null));
                        break;
                }
            }
        }

        var files = new List<string>();
        foreach (var (w, list) in changes)
        {
            try
            {
                files.AddRange(await w.ApplyAsync(list, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Recover(w, ex);
            }
        }
        if (files.Count > 0) Publish("disk", files.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), sw);

        if (everything) ScheduleReload(null);
        else foreach (var language in reload) ScheduleReload(language);
    }

    async Task ApplyOverlayAsync(OverlayWork o, CancellationToken ct)
    {
        var owner = Owner(o.Path);
        if (owner is null) return;
        var sw = Stopwatch.StartNew();

        lock (_gate)
        {
            if (o.Text is null || o.Saved) _overlays.Remove(o.Path);
            else _overlays[o.Path] = o.Text;
        }

        IReadOnlyList<string> changed;
        try
        {
            changed = await owner.ApplyAsync([new FileChange(o.Path, o.Text)], ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Recover(owner, ex);
            return;
        }
        if (changed.Count > 0) Publish(o.Saved ? "save" : "edit", [o.Path], sw);
    }

    /// Reads again what a declaration change may have shifted, language by language, stopping as soon as
    /// new work arrives. True when the session is shutting down.
    async Task<bool> SyncAsync(CancellationToken ct)
    {
        var pending = _workspaces.Where(w => w.SyncPending).ToList();
        if (pending.Count == 0) return false;

        var sw = Stopwatch.StartNew();
        var done = false;
        foreach (var w in pending)
        {
            try
            {
                if (!await w.SyncAsync(() => _queue.Reader.TryPeek(out _), ct)) break;
                done = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return true;
            }
            catch (Exception ex)
            {
                w.DropSync();
                Recover(w, ex);
            }
        }
        if (done) Publish("sync", [], sw);
        return false;
    }

    void Publish(string reason, IReadOnlyList<string> files, Stopwatch sw)
    {
        var graph = Build();
        var next = Snapshot.From(graph, _snapshot.Version + 1, Info(graph));
        var relative = files.Select(f => Path.GetRelativePath(RootDir, f).Replace('\\', '/'));
        var patch = Snapshot.Diff(_snapshot, next, reason, relative, sw.ElapsedMilliseconds);
        if (patch is null) return;

        _snapshot = next;
        _hub.Publish("patch", patch);
    }
}

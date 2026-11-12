using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Co.Revington.Sightline.Analysis;
using Co.Revington.Sightline.Settings;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace Co.Revington.Sightline.Live;

internal abstract record Work;
internal sealed record ReloadWork : Work;
internal sealed record FilesWork(IReadOnlyList<string> Paths) : Work;

/// Editor text for a file. Text = null drops the unsaved buffer and goes back to disk.
internal sealed record OverlayWork(string Path, string? Text, bool Saved = false) : Work;

internal sealed record StatusInfo(string State, string Message);

/// Filter: projects to leave off the map. Restore: run "dotnet restore" for projects that were never restored.
/// Checks: settings for the problems the map reports. Watch: follow file changes after loading (off for
/// one-shot commands such as "sightline check"). Log: where progress goes (stdout by default).
internal sealed record LoadOptions(ProjectFilter Filter, bool Restore, CheckOptions? Checks = null, bool Watch = true, TextWriter? Log = null)
{
    public static readonly LoadOptions Default = new(ProjectFilter.None, Restore: true);
}

/// Owns the loaded solution and keeps the graph in step with edits.
/// All changes run on one loop, so state never needs fine-grained locking.
internal sealed class LiveSession
{
    static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", ".vs", ".idea", "node_modules" };

    readonly string _target;
    readonly EventHub _hub;
    readonly LoadOptions _options;
    readonly HashSet<string> _restoreTried = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> _restoreProblems = [];
    readonly Channel<Work> _queue = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions { SingleReader = true });
    readonly object _gate = new();
    readonly Timer _reloadTimer;

    MSBuildWorkspace? _workspace;
    Solution? _solution;
    List<ProjectFacts> _projects = [];
    Dictionary<ProjectId, string> _projectNodeIds = [];
    List<string> _diagnostics = [];
    FileWatcher? _watcher;

    readonly Dictionary<DocumentId, DocFacts> _facts = [];
    readonly Dictionary<string, List<DocumentId>> _tracked = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _overlays = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<ProjectId> _pendingSync = [];

    volatile Snapshot _snapshot = Snapshot.Empty;
    volatile StatusInfo _status = new("loading", "Starting");

    public LiveSession(string target, EventHub hub, LoadOptions? options = null)
    {
        _target = target;
        _hub = hub;
        _options = options ?? LoadOptions.Default;
        RootDir = Directory.Exists(target) ? target : Path.GetDirectoryName(target)!;
        Name = Directory.Exists(target) ? new DirectoryInfo(target).Name : Path.GetFileName(target);
        _reloadTimer = new Timer(_ => Enqueue(new ReloadWork()), null, Timeout.Infinite, Timeout.Infinite);
    }

    public string RootDir { get; }
    public string Name { get; }
    public Snapshot Snapshot => _snapshot;
    public StatusInfo Status => _status;
    public string StatusJson => JsonSerializer.Serialize(_status, Snapshot.Json);

    public void Enqueue(Work work) => _queue.Writer.TryWrite(work);

    public bool IsTracked(string path)
    {
        lock (_gate) return _tracked.ContainsKey(path);
    }

    public async Task<(string Text, bool Dirty)?> ReadSourceAsync(string path)
    {
        Solution? solution;
        DocumentId id;
        bool dirty;
        lock (_gate)
        {
            if (!_tracked.TryGetValue(path, out var ids)) return null;
            (solution, id, dirty) = (_solution, ids[0], _overlays.ContainsKey(path));
        }

        var doc = solution?.GetDocument(id);
        if (doc is null) return null;
        return ((await doc.GetTextAsync()).ToString(), dirty);
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
                            case ReloadWork: await LoadAsync(ct); break;
                            case FilesWork f: await ApplyFilesAsync(f.Paths, ct); break;
                            case OverlayWork o: await ApplyOverlayAsync(o, ct); break;
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"  error   {ex.Message}");
                        if (work is ReloadWork) SetStatus("error", ex.Message);
                    }
                }

                // Idle: refresh files whose meaning may have shifted after a declaration change.
                if (_pendingSync.Count > 0)
                {
                    try { await SyncAsync(ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                    catch (Exception ex) { Console.Error.WriteLine($"  error   {ex.Message}"); _pendingSync.Clear(); }
                }
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
            var skip = (work is ReloadWork && next is ReloadWork)
                       || (work is OverlayWork { Saved: false } a && next is OverlayWork b && SamePath(a.Path, b.Path));
            if (!skip) break;
            _queue.Reader.TryRead(out _);
            work = next;
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

    // ---------------------------------------------------------------- load

    sealed class DirectProgress(Action<ProjectLoadProgress> report) : IProgress<ProjectLoadProgress>
    {
        public void Report(ProjectLoadProgress value) => report(value);
    }

    async Task LoadAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (_options.Restore) await RestoreMissingAsync(ct);
        SetStatus("loading", "Opening " + Name);

        var ws = MSBuildWorkspace.Create();
        ws.SkipUnrecognizedProjects = true;
        var progress = new DirectProgress(p => SetStatus("loading", $"{p.Operation} {Path.GetFileName(p.FilePath)}"));
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Solution solution;
        if (Directory.Exists(_target))
        {
            foreach (var csproj in FindProjects(_target).Order())
            {
                if (_options.Filter.Excludes(csproj)) excluded.Add(csproj);
                else if (!ws.CurrentSolution.Projects.Any(p => SamePath(p.FilePath ?? "", csproj)))
                    await ws.OpenProjectAsync(csproj, progress, ct);
            }
            solution = ws.CurrentSolution;
        }
        else if (_target.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || _target.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            solution = await ws.OpenSolutionAsync(_target, progress, ct);
        }
        else
        {
            solution = (await ws.OpenProjectAsync(_target, progress, ct)).Solution;
        }

        // Projects listed in "exclude" still load when others reference them (their types are needed
        // to understand the code that uses them), but they stay off the map.
        var csharp = solution.Projects.Where(p => p.Language == LanguageNames.CSharp && p.FilePath is not null).ToList();
        foreach (var p in csharp)
            if (_options.Filter.Excludes(p.FilePath!)) excluded.Add(p.FilePath!);

        // One project per csproj: multi-targeted projects load once per framework.
        var chosen = csharp
            .Where(p => !excluded.Contains(p.FilePath!))
            .GroupBy(p => p.FilePath!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(p => p.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);       // csproj path -> display name
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in chosen)
        {
            var name = Path.GetFileNameWithoutExtension(p.FilePath!);
            for (var i = 2; !used.Add(name); i++) name = $"{Path.GetFileNameWithoutExtension(p.FilePath!)} ({i})";
            names[p.FilePath!] = name;
        }
        var nodeIdByPath = names.ToDictionary(kv => kv.Key, kv => "proj:" + kv.Value, StringComparer.OrdinalIgnoreCase);

        var projects = chosen.Select(p => ProjectReader.Read(p, nodeIdByPath[p.FilePath!], names[p.FilePath!], nodeIdByPath)).ToList();
        var projectNodeIds = chosen.ToDictionary(p => p.Id, p => nodeIdByPath[p.FilePath!]);

        var docs = chosen.SelectMany(p => p.Documents.Where(IsUserDocument)).ToList();
        var results = new ConcurrentBag<(Document Doc, DocFacts Facts)>();
        var done = 0;

        await Parallel.ForEachAsync(docs, Parallelism(ct), async (doc, c) =>
        {
            results.Add((doc, await Extractor.ExtractAsync(doc, projectNodeIds[doc.Project.Id], c)));
            var n = Interlocked.Increment(ref done);
            if (n % 20 == 0) SetStatus("loading", $"Analyzing {n}/{docs.Count} files");
        });

        lock (_gate)
        {
            _workspace?.Dispose();
            _workspace = ws;
            _solution = solution;
            _projects = projects;
            _projectNodeIds = projectNodeIds;
            _diagnostics = _restoreProblems.Concat(ws.Diagnostics.Select(d => $"{d.Kind}: {d.Message}")).Distinct().Take(50).ToList();

            _facts.Clear();
            _tracked.Clear();
            foreach (var (doc, facts) in results)
            {
                _facts[doc.Id] = facts;
                if (!_tracked.TryGetValue(doc.FilePath!, out var ids)) _tracked[doc.FilePath!] = ids = [];
                ids.Add(doc.Id);
            }
        }
        _pendingSync.Clear();

        // Unsaved editor buffers survive a reload.
        foreach (var (path, text) in _overlays.ToList())
            if (await SetTextAsync(path, SourceText.From(text, Encoding.UTF8), ct))
                await ReextractAsync(TrackedIds(path), ct);

        var graph = GraphBuilder.Build(_projects, _facts.Values, _options.Checks);
        _snapshot = Snapshot.From(graph, _snapshot.Version + 1, Info(graph));
        _hub.Publish("reload", $"{{\"version\":{_snapshot.Version}}}");

        var count = (string kind) => graph.Nodes.Values.Count(n => n.Kind == kind);
        var left = excluded.Count == 0 ? "" : $", {excluded.Count} left out by {SightlineConfig.FileName}";
        SetStatus("ready", $"{count("project")} projects, {count("type")} types, {count("endpoint")} endpoints in {sw.Elapsed.TotalSeconds:0.0}s{left}");

        if (_options.Watch) _watcher ??= new FileWatcher(WatchRoots(), paths => Enqueue(new FilesWork(paths)));
    }

    /// Loads the solution once, without following changes. For one-shot commands (check, export, diff).
    public async Task<Snapshot> LoadOnceAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        if (_status.State != "ready") throw new InvalidOperationException(_status.Message);
        return _snapshot;
    }

    static ParallelOptions Parallelism(CancellationToken ct) => new()
    {
        MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 1),
        CancellationToken = ct,
    };

    /// Project files under a folder, without walking into build output or tool folders.
    internal static IEnumerable<string> FindProjects(string root)
    {
        var pending = new Stack<string>([root]);
        while (pending.TryPop(out var dir))
        {
            string[] files, dirs;
            try
            {
                files = Directory.GetFiles(dir, "*.csproj");
                dirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }
            foreach (var file in files) yield return file;
            foreach (var sub in dirs)
                if (!SkipDirs.Contains(Path.GetFileName(sub))) pending.Push(sub);
        }
    }

    /// The analysis needs each project's NuGet packages, which exist only after a restore.
    /// Like "dotnet build", restore the projects that never were, so a fresh clone just works.
    async Task RestoreMissingAsync(CancellationToken ct)
    {
        var missing = FindProjects(RootDir)
            .Where(p => !_options.Filter.Excludes(p) && !_restoreTried.Contains(p))
            .Where(p => !File.Exists(Path.Combine(Path.GetDirectoryName(p)!, "obj", "project.assets.json")))
            .ToList();
        if (missing.Count == 0) return;

        // Each project is tried once per run: a feed that needs a login should not slow down every reload.
        foreach (var p in missing) _restoreTried.Add(p);
        SetStatus("loading", $"Restoring NuGet packages for {missing.Count} project{(missing.Count == 1 ? "" : "s")}");

        // A solution or a single project restores in one go; a folder of projects restores one by one.
        IReadOnlyList<string> targets = Directory.Exists(_target) ? missing : [_target];
        foreach (var target in targets)
        {
            var result = await DotnetCli.RunAsync(RootDir, ["restore", target], ct);
            if (result.Code != 0)
                _restoreProblems.Add($"Warning: \"dotnet restore {Path.GetFileName(target)}\" failed, so some references may be missing. {DotnetCli.Tail(result.All, 2)}");
        }
    }

    static bool InBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    static bool IsUserDocument(Document d) =>
        d.FilePath is { } path && d.SourceCodeKind == SourceCodeKind.Regular && !InBuildOutput(path) && !FromPackage(path)
        && !path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase);

    /// Source files that NuGet packages add to a project are not the project's own code.
    /// That includes the starter that Co.Revington.Sightline.Hosting compiles into an app.
    static bool FromPackage(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        return path.Contains($"{sep}.nuget{sep}packages{sep}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{sep}co.revington.sightline.hosting{sep}", StringComparison.OrdinalIgnoreCase);
    }

    List<string> WatchRoots()
    {
        var roots = new List<string> { RootDir };
        foreach (var p in _projects)
            if (!roots.Any(r => p.Dir.StartsWith(r, StringComparison.OrdinalIgnoreCase))) roots.Add(p.Dir);
        return roots;
    }

    object Info(Graph g)
    {
        int Count(string kind) => g.Nodes.Values.Count(n => n.Kind == kind);
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
            diagnostics = _diagnostics,
        };
    }

    // ---------------------------------------------------------------- edits

    async Task ApplyFilesAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        if (_solution is null) return;
        var sw = Stopwatch.StartNew();
        var touched = new List<DocumentId>();
        var files = new List<string>();
        var reload = false;

        foreach (var raw in paths)
        {
            if (raw == FileWatcher.Rescan || !raw.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                reload = true;
                continue;
            }

            var path = Path.GetFullPath(raw);
            if (_overlays.ContainsKey(path)) continue;          // the editor buffer wins until saved or reverted

            var ids = _solution.GetDocumentIdsWithFilePath(path);
            var exists = File.Exists(path);

            if (exists && !ids.IsEmpty)
            {
                var text = await ReadTextAsync(path, ct);
                if (text is null || !await SetTextAsync(path, text, ct)) continue;
                touched.AddRange(TrackedIds(path));
                files.Add(path);
            }
            else if (exists)
            {
                var text = await ReadTextAsync(path, ct);
                if (text is null) continue;
                var added = AddDocument(path, text);
                if (added.Count == 0) continue;
                touched.AddRange(added);
                files.Add(path);
            }
            else if (!ids.IsEmpty)
            {
                RemoveDocument(path, ids);
                files.Add(path);
            }
        }

        if (files.Count > 0)
        {
            await ReextractAsync(touched, ct);
            Publish("disk", files, sw);
        }
        if (reload) _reloadTimer.Change(700, Timeout.Infinite);
    }

    async Task ApplyOverlayAsync(OverlayWork o, CancellationToken ct)
    {
        if (_solution is null || _solution.GetDocumentIdsWithFilePath(o.Path).IsEmpty) return;
        var sw = Stopwatch.StartNew();

        SourceText? text;
        if (o.Text is null)
        {
            lock (_gate) _overlays.Remove(o.Path);
            text = await ReadTextAsync(o.Path, ct);
            if (text is null) return;
        }
        else
        {
            lock (_gate)
            {
                if (o.Saved) _overlays.Remove(o.Path);
                else _overlays[o.Path] = o.Text;
            }
            text = SourceText.From(o.Text, Encoding.UTF8);
        }

        if (!await SetTextAsync(o.Path, text, ct)) return;
        await ReextractAsync(TrackedIds(o.Path), ct);
        Publish(o.Saved ? "save" : "edit", [o.Path], sw);
    }

    List<DocumentId> TrackedIds(string path) => _tracked.TryGetValue(path, out var ids) ? [.. ids] : [];

    /// Replaces a file's text in every project that contains it. False when the text is unchanged.
    async Task<bool> SetTextAsync(string path, SourceText text, CancellationToken ct)
    {
        var solution = _solution!;
        var ids = solution.GetDocumentIdsWithFilePath(path);
        if (ids.IsEmpty) return false;

        var current = await solution.GetDocument(ids[0])!.GetTextAsync(ct);
        if (current.ContentEquals(text)) return false;

        foreach (var id in ids) solution = solution.WithDocumentText(id, text);
        lock (_gate) _solution = solution;
        return true;
    }

    List<DocumentId> AddDocument(string path, SourceText text)
    {
        var solution = _solution!;

        // The deepest project folder that contains the file owns it (SDK-style globbing).
        var owners = solution.Projects
            .Where(p => p.Language == LanguageNames.CSharp && p.FilePath is not null)
            .Select(p => (Project: p, Dir: Path.GetDirectoryName(p.FilePath!)! + Path.DirectorySeparatorChar))
            .Where(x => path.StartsWith(x.Dir, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Dir.Length)
            .ToList();
        if (owners.Count == 0) return [];

        var added = new List<DocumentId>();
        lock (_gate)
        {
            foreach (var (project, _) in owners.Where(x => x.Dir.Length == owners[0].Dir.Length))
            {
                var id = DocumentId.CreateNewId(project.Id);
                solution = solution.AddDocument(id, Path.GetFileName(path), text, filePath: path);
                if (!_projectNodeIds.ContainsKey(project.Id)) continue;

                if (!_tracked.TryGetValue(path, out var ids)) _tracked[path] = ids = [];
                ids.Add(id);
                added.Add(id);
            }
            _solution = solution;
        }
        foreach (var id in added) MarkForSync(id.ProjectId);
        return added;
    }

    void RemoveDocument(string path, ImmutableArray<DocumentId> ids)
    {
        var solution = _solution!;
        foreach (var id in ids) solution = solution.RemoveDocument(id);

        lock (_gate)
        {
            _solution = solution;
            if (_tracked.Remove(path, out var tracked))
                foreach (var id in tracked) _facts.Remove(id);
        }
        foreach (var id in ids) MarkForSync(id.ProjectId);
    }

    async Task ReextractAsync(IEnumerable<DocumentId> ids, CancellationToken ct)
    {
        foreach (var id in ids.Distinct())
        {
            var doc = _solution!.GetDocument(id);
            if (doc is null || !_projectNodeIds.TryGetValue(id.ProjectId, out var nodeId)) continue;

            var facts = await Extractor.ExtractAsync(doc, nodeId, ct);
            if (!_facts.TryGetValue(id, out var old) || old.DeclHash != facts.DeclHash) MarkForSync(id.ProjectId);
            _facts[id] = facts;
        }
    }

    /// A declaration changed: other files in this project and its dependents may now bind differently.
    void MarkForSync(ProjectId project)
    {
        if (!_projectNodeIds.ContainsKey(project)) return;
        _pendingSync.Add(project);
        foreach (var dependent in _solution!.GetProjectDependencyGraph().GetProjectsThatTransitivelyDependOnThisProject(project))
            if (_projectNodeIds.ContainsKey(dependent)) _pendingSync.Add(dependent);
    }

    async Task SyncAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var solution = _solution!;
        var ids = _facts.Keys.Where(id => _pendingSync.Contains(id.ProjectId)).ToList();
        var results = new ConcurrentBag<(DocumentId Id, DocFacts Facts)>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            await Parallel.ForEachAsync(ids, Parallelism(cts.Token), async (id, c) =>
            {
                if (_queue.Reader.Count > 0) cts.Cancel();          // a new edit is waiting: handle it first
                c.ThrowIfCancellationRequested();
                if (solution.GetDocument(id) is { } doc)
                    results.Add((id, await Extractor.ExtractAsync(doc, _projectNodeIds[id.ProjectId], c)));
            });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return;                                                  // keep the sync pending and retry when idle
        }

        _pendingSync.Clear();
        foreach (var (id, facts) in results) _facts[id] = facts;
        Publish("sync", [], sw);
    }

    void Publish(string reason, IReadOnlyList<string> files, Stopwatch sw)
    {
        var graph = GraphBuilder.Build(_projects, _facts.Values, _options.Checks);
        var next = Snapshot.From(graph, _snapshot.Version + 1, Info(graph));
        var relative = files.Select(f => Path.GetRelativePath(RootDir, f).Replace('\\', '/'));
        var patch = Snapshot.Diff(_snapshot, next, reason, relative, sw.ElapsedMilliseconds);
        if (patch is null) return;

        _snapshot = next;
        _hub.Publish("patch", patch);
    }

    static async Task<SourceText?> ReadTextAsync(string path, CancellationToken ct)
    {
        // Editors may hold the file briefly while saving; retry a few times.
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return SourceText.From(stream);
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (IOException) { await Task.Delay(40, ct); }
            catch (UnauthorizedAccessException) { await Task.Delay(40, ct); }
        }
        return null;
    }
}

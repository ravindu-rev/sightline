using System.ComponentModel;
using System.Text.Json;
using Co.Revington.Sightline.Analysis;

namespace Co.Revington.Sightline.Languages.TypeScript;

/// The facts of one file as the analyzer sends them: DocFacts with every list optional.
internal sealed record SidecarDoc(
    string File, string Project, List<TypeFact>? Types, List<MemberFact>? Members, List<RefFact>? Refs, List<ImplFact>? Impls,
    List<HandlerFact>? Handlers, List<DiFact>? Di, List<EndpointFact>? Endpoints, List<GroupPass>? GroupPasses,
    List<DependencyFact>? Dependencies, List<CoverageFact>? Coverage, int Unresolved, string? DeclHash)
{
    public DocFacts ToFacts()
    {
        var f = new DocFacts { File = File, Project = Project, Unresolved = Unresolved, DeclHash = DeclHash ?? "" };
        f.Types.AddRange(Types ?? []);
        f.Members.AddRange(Members ?? []);
        f.Refs.AddRange(Refs ?? []);
        f.Impls.AddRange(Impls ?? []);
        f.Handlers.AddRange(Handlers ?? []);
        f.Di.AddRange(Di ?? []);
        f.Endpoints.AddRange(Endpoints ?? []);
        f.GroupPasses.AddRange(GroupPasses ?? []);
        f.Dependencies.AddRange(Dependencies ?? []);
        f.Coverage.AddRange(Coverage ?? []);
        return f;
    }
}

/// JavaScript and TypeScript, read by the TypeScript compiler in Node.js next to Sightline (analyzers/typescript).
/// This side finds the packages and their files, decides what a changed file means, and keeps the facts;
/// the analyzer reads the code. Node.js starts only when there is a package to read.
internal sealed class TypeScriptWorkspace : ILanguageWorkspace
{
    const int MinimumNode = 18;
    const int SyncChunk = 40;

    static readonly string[] Servers =
    [
        "express", "fastify", "koa", "@koa/router", "koa-router", "hono", "@nestjs/core", "@nestjs/common", "next", "@hapi/hapi",
        "restify", "polka", "@remix-run/node", "nuxt", "@sveltejs/kit", "h3", "elysia",
    ];

    static readonly string[] Interfaces = ["react", "react-dom", "react-native", "vue", "svelte", "@angular/core", "solid-js", "preact", "lit"];

    static readonly HashSet<string> SetupFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "package-lock.json", "yarn.lock", "pnpm-lock.yaml", "pnpm-workspace.yaml", "jsconfig.json", "tsconfig.json",
    };

    sealed record PackageInfo(string Id, string Dir, string? OutDir, bool Dotnet);

    /// Where the packages are, for RoleOf on any thread. Packages: deepest folder first. Waiting: folders of
    /// packages that have no code yet, so their first file loads them.
    sealed record Layout(string Root, IReadOnlyList<PackageInfo> Packages, IReadOnlySet<string> ManifestDirs, IReadOnlySet<string> Waiting)
    {
        public static readonly Layout Empty = new("", [], new HashSet<string>(), new HashSet<string>());
    }

    static readonly TimeSpan OpenTimeout = TimeSpan.FromMinutes(15);
    static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(3);

    readonly object _gate = new();
    readonly Queue<string> _syncQueue = new();
    readonly HashSet<string> _syncQueued = new(StringComparer.OrdinalIgnoreCase);

    Sidecar? _sidecar;
    volatile Layout _layout = Layout.Empty;
    List<ProjectFacts> _projects = [];
    Dictionary<string, DocFacts> _facts = new(StringComparer.OrdinalIgnoreCase);
    HashSet<string> _tracked = new(StringComparer.OrdinalIgnoreCase);                     // guarded by _gate
    Dictionary<string, HashSet<string>> _files = [];                                      // project id -> its files
    Dictionary<string, HashSet<string>> _dependents = [];                                 // project id -> projects that use it
    List<string> _diagnostics = [];
    int _excluded;

    public string Language => "typescript";
    public IReadOnlyList<ProjectFacts> Projects => _projects;
    public IEnumerable<DocFacts> Facts => _facts.Values;
    public IReadOnlyList<string> Diagnostics => _diagnostics;
    public int Excluded => _excluded;
    public IEnumerable<string> WatchRoots => _projects.Select(p => p.Dir);
    public bool SyncPending => _syncQueue.Count > 0;

    public void Dispose()
    {
        _sidecar?.Dispose();
        _sidecar = null;
    }

    // ---------------------------------------------------------------- load

    public async Task LoadAsync(LoadContext context, CancellationToken ct)
    {
        var scan = NpmPackages.Scan(context.RootDir);
        var kept = scan.Packages.Where(p => !context.Filter.Excludes(p.Manifest, p.Name)).ToList();

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var named = new List<(NpmPackage Package, string Name, string Id)>();
        foreach (var p in kept)
        {
            var name = p.Name;
            for (var i = 2; !used.Add(name); i++) name = $"{p.Name} ({i})";
            named.Add((p, name, "proj:npm:" + name));
        }

        // Dependencies name packages by their package.json name; those in the repository are project references.
        var local = named.GroupBy(x => x.Package.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
        var locks = new Dictionary<string, JsonDocument?>(StringComparer.OrdinalIgnoreCase);
        var projects = named.Select(x =>
        {
            var refs = x.Package.Dependencies.Keys.Where(local.ContainsKey).Select(d => local[d]).Where(id => id != x.Id).Distinct().ToList();
            var locked = NpmPackages.LockedVersions(x.Package.Dir, context.RootDir, locks);
            return new ProjectFacts
            {
                Id = x.Id, Name = x.Name, Path = x.Package.Manifest, Dir = x.Package.Dir, Kind = KindOf(x.Package),
                Language = x.Package.IsTypeScript ? "TypeScript" : "JavaScript",
                ProjectRefs = refs, WrittenRefs = refs,
                Packages = x.Package.Dependencies.Where(d => !local.ContainsKey(d.Key))
                    .Select(d => new PackageRef(d.Key, locked.GetValueOrDefault(d.Key) ?? d.Value, true, "npm")).ToList(),
            };
        }).ToList();
        foreach (var doc in locks.Values) doc?.Dispose();

        var layout = new Layout(context.RootDir,
            named.Select(x => new PackageInfo(x.Id, x.Package.Dir, x.Package.OutDir, NpmPackages.IsDotnetFolder(x.Package.Dir)))
                .OrderByDescending(p => p.Dir.Length).ToList(),
            scan.ManifestDirs, scan.Empty);

        var docs = new Dictionary<string, DocFacts>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = scan.Warnings.ToList();
        if (projects.Count > 0)
        {
            context.Progress($"Reading JavaScript and TypeScript in {projects.Count} package{(projects.Count == 1 ? "" : "s")}");
            await StartAsync(context.RootDir, ct);
            var total = named.Sum(x => x.Package.Files.Count);
            try
            {
                var result = await CallAsync("open", new
                {
                    root = context.RootDir,
                    packagesFrom = context.PackagesFrom is { } mirror ? new { from = mirror.From, to = mirror.To } : null,
                    projects = named.Select(x => new
                    {
                        id = x.Id, name = x.Name, packageName = x.Package.Name, dir = x.Package.Dir, manifest = x.Package.Manifest,
                        config = x.Package.Config, files = x.Package.Files, declarations = x.Package.Declarations,
                        dependencies = x.Package.Dependencies.Keys,
                    }),
                }, e => OnOpenEvent(e, docs, total, context), OpenTimeout, ct);
                if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("diagnostics", out var list) && list.ValueKind == JsonValueKind.Array)
                    diagnostics.AddRange(list.EnumerateArray().Select(d => d.GetString()).OfType<string>());
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Whatever the analyzer held is gone with it; the next load starts a new one.
                _sidecar?.Dispose();
                _sidecar = null;
                throw;
            }
        }
        else
        {
            _sidecar?.Dispose();
            _sidecar = null;
        }

        var files = named.ToDictionary(x => x.Id, x => x.Package.Files.ToHashSet(StringComparer.OrdinalIgnoreCase));
        var all = diagnostics.Select(d => "Warning: " + d).Distinct().ToList();
        lock (_gate)
        {
            _layout = layout;
            _projects = projects;
            _facts = docs;
            _files = files;
            _tracked = files.Values.SelectMany(f => f).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _dependents = Dependents(projects);
            _excluded = scan.Packages.Count - kept.Count;
            _diagnostics = all.Count <= 200 ? all : [.. all.Take(200), $"And {all.Count - 200} more."];
            _syncQueue.Clear();
            _syncQueued.Clear();
        }
    }

    static void OnOpenEvent(JsonElement e, Dictionary<string, DocFacts> docs, int total, LoadContext context)
    {
        switch (e.GetProperty("event").GetString())
        {
            case "progress":
                if (e.TryGetProperty("message", out var message) && message.GetString() is { } text) context.Progress(text);
                break;
            case "docs":
                foreach (var doc in Parse(e)) docs[doc.File] = doc;
                context.Progress($"Analyzing {docs.Count}/{total} files");
                break;
        }
    }

    static IEnumerable<DocFacts> Parse(JsonElement holder) =>
        holder.ValueKind == JsonValueKind.Object && holder.TryGetProperty("docs", out var docs) && docs.ValueKind == JsonValueKind.Array
            ? (docs.Deserialize<List<SidecarDoc>>(Sidecar.Json) ?? []).Select(d => d.ToFacts())
            : [];

    static string KindOf(NpmPackage p) =>
        Servers.Any(p.Dependencies.ContainsKey) ? "web"
        : Interfaces.Any(p.Dependencies.ContainsKey) ? "ui"
        : p.HasBin ? "cli"
        : "lib";

    /// Project id -> every project that depends on it, directly or through others.
    static Dictionary<string, HashSet<string>> Dependents(List<ProjectFacts> projects)
    {
        var direct = projects.ToDictionary(p => p.Id, _ => new HashSet<string>(StringComparer.Ordinal));
        foreach (var p in projects)
            foreach (var r in p.ProjectRefs)
                if (direct.TryGetValue(r, out var users)) users.Add(p.Id);

        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var p in projects)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>(direct[p.Id]);
            while (stack.TryPop(out var id))
                if (seen.Add(id)) foreach (var next in direct[id]) stack.Push(next);
            result[p.Id] = seen;
        }
        return result;
    }

    async Task<Sidecar> StartAsync(string root, CancellationToken ct)
    {
        if (_sidecar is { Running: true } running) return running;
        _sidecar?.Dispose();
        _sidecar = null;

        var analyzers = Environment.GetEnvironmentVariable("SIGHTLINE_ANALYZERS") is { Length: > 0 } custom ? custom : Path.Combine(AppContext.BaseDirectory, "analyzers");
        var script = Path.Combine(analyzers, "typescript", "main.mjs");
        if (!File.Exists(script))
            throw new LanguageUnavailableException($"JavaScript and TypeScript are not on the map: the reader for them is missing from this installation ({script}).");

        var node = Environment.GetEnvironmentVariable("SIGHTLINE_NODE") is { Length: > 0 } set ? set : Programs.OnPath("node");
        if (node is null) throw new LanguageUnavailableException(NodeNeeded("Node.js was not found on the PATH"));

        Sidecar sidecar;
        try
        {
            sidecar = Sidecar.Start(node, ["--max-old-space-size=6144", script], root);
        }
        catch (Win32Exception ex)
        {
            throw new LanguageUnavailableException(NodeNeeded($"{node} could not be started ({ex.Message})"));
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var hello = await sidecar.CallAsync("hello", null, null, timeout.Token);
            var version = hello.TryGetProperty("node", out var v) ? v.GetString() ?? "" : "";
            if (Major(version) < MinimumNode) throw new LanguageUnavailableException(NodeNeeded($"this machine has Node.js {version}"));
        }
        catch (Exception ex) when (ex is not LanguageUnavailableException && !ct.IsCancellationRequested)
        {
            sidecar.Dispose();
            throw new LanguageUnavailableException(NodeNeeded($"{Path.GetFileName(node)} could not run the reader: {ex.Message}"));
        }
        catch
        {
            sidecar.Dispose();
            throw;
        }
        _sidecar = sidecar;
        return sidecar;
    }

    static int Major(string version) =>
        int.TryParse(version.TrimStart('v').Split('.')[0], out var major) ? major : 0;

    /// A request to the reader that cannot hang the session: one that takes too long stops the reader.
    async Task<JsonElement> CallAsync(string method, object args, Action<JsonElement>? onEvent, TimeSpan timeout, CancellationToken ct)
    {
        var sidecar = _sidecar is { Running: true } running ? running : throw new SidecarException(Stopped());
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            return await sidecar.CallAsync(method, args, onEvent, limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Stop();
            throw new SidecarException($"The JavaScript and TypeScript reader did not answer within {timeout.TotalMinutes:0} minutes, so it was stopped.");
        }
        catch (SidecarException)
        {
            Stop();
            throw;
        }
    }

    void Stop()
    {
        _sidecar?.Dispose();
        _sidecar = null;
    }

    static string Stopped() => "The JavaScript and TypeScript reader is not running, so edits to that code are not followed until it loads again.";

    static string NodeNeeded(string why) =>
        $"JavaScript and TypeScript are not on the map: reading them needs Node.js {MinimumNode} or newer, and {why}. " +
        "Install Node.js, or set SIGHTLINE_NODE to the node program.";

    // ---------------------------------------------------------------- what a path means

    public bool Tracks(string path)
    {
        lock (_gate) return _tracked.Contains(path);
    }

    public FileRole RoleOf(string path)
    {
        var layout = _layout;
        if (layout.Root.Length == 0) return FileRole.None;
        var dir = Path.GetDirectoryName(path) ?? "";
        if (!SameDir(dir, layout.Root) && !NpmPackages.Inside(dir, layout.Root)) return FileRole.None;

        var file = Path.GetFileName(path);
        var parent = Path.GetFileName(dir);
        if (parent.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
            return file is ".package-lock.json" or ".modules.yaml" or ".yarn-integrity" && IsPackageDir(layout, Path.GetDirectoryName(dir)!) ? FileRole.Project : FileRole.None;
        if (dir.Split(Path.DirectorySeparatorChar).Any(p => p.Equals("node_modules", StringComparison.OrdinalIgnoreCase))) return FileRole.None;

        if (file.Equals("package.json", StringComparison.OrdinalIgnoreCase)) return FileRole.Project;
        if ((SetupFiles.Contains(file) || (file.StartsWith("tsconfig.", StringComparison.OrdinalIgnoreCase) && file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            && IsPackageDir(layout, dir)) return FileRole.Project;
        if (file.Equals("install-state.gz", StringComparison.OrdinalIgnoreCase) && parent.Equals(".yarn", StringComparison.OrdinalIgnoreCase)) return FileRole.Project;

        if (!NpmPackages.IsCodeName(file)) return FileRole.None;
        if (OwnerOf(layout, path) is not null) return FileRole.Source;

        // The first file of a package that had no code yet: the package joins the map.
        for (var up = dir; up.Length > 0 && (SameDir(up, layout.Root) || NpmPackages.Inside(up, layout.Root)); up = Path.GetDirectoryName(up) ?? "")
        {
            if (layout.Waiting.Contains(up)) return FileRole.Project;
            if (layout.ManifestDirs.Contains(up) || NpmPackages.SkipDirs.Contains(Path.GetFileName(up))) break;
        }
        return FileRole.None;
    }

    static bool SameDir(string a, string b) =>
        string.Equals(a.TrimEnd(Path.DirectorySeparatorChar), b.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    static bool IsPackageDir(Layout layout, string dir) => SameDir(dir, layout.Root) || layout.Packages.Any(p => SameDir(p.Dir, dir));

    /// The package whose code a file is: the deepest package folder above it, when no other package.json,
    /// installed packages, build output or tests lie in between.
    static PackageInfo? OwnerOf(Layout layout, string path)
    {
        var owner = layout.Packages.FirstOrDefault(p => NpmPackages.Inside(path, p.Dir));
        if (owner is null) return null;
        for (var dir = Path.GetDirectoryName(path); dir is not null && !SameDir(dir, owner.Dir); dir = Path.GetDirectoryName(dir))
            if (layout.ManifestDirs.Contains(dir) || NpmPackages.Skipped(owner.Dir, dir, owner.OutDir, owner.Dotnet)) return null;
        return owner;
    }

    public async Task<string?> ReadTextAsync(string path, CancellationToken ct) => Tracks(path) ? await ReadDiskAsync(path, ct) : null;

    // ---------------------------------------------------------------- edits

    public async Task<IReadOnlyList<string>> ApplyAsync(IReadOnlyList<FileChange> changes, CancellationToken ct)
    {
        if (_projects.Count == 0) return [];
        // A reader that stopped (out of memory, say) must not make edits vanish without a word: the session
        // reports it and loads the language again.
        if (_sidecar is not { Running: true }) throw new SidecarException(Stopped());
        var items = new List<object>();
        var files = new List<string>();
        var added = new List<(string Path, string Project)>();
        var removed = new List<string>();

        foreach (var change in changes)
        {
            var path = change.Path;
            var tracked = Tracks(path);
            if (change.Text is not null)
            {
                if (!tracked) continue;
                items.Add(new { path, text = change.Text });
                files.Add(path);
                continue;
            }

            if (File.Exists(path) && !NpmPackages.IsLinkFile(path))
            {
                var owner = tracked ? null : OwnerOf(_layout, path);
                if (!tracked && (owner is null || new FileInfo(path).Length > 1024 * 1024 || NpmPackages.ThroughLink(owner.Dir, path))) continue;
                var text = await ReadDiskAsync(path, ct);
                if (text is null) continue;
                items.Add(new { path, text, project = owner?.Id });
                if (owner is not null) added.Add((path, owner.Id));
                files.Add(path);
            }
            else if (tracked)
            {
                items.Add(new { path, remove = true });
                removed.Add(path);
                files.Add(path);
            }
        }
        if (items.Count == 0) return [];

        var result = await CallAsync("apply", new { changes = items }, null, CallTimeout, ct);
        lock (_gate)
        {
            foreach (var (path, _) in added) _tracked.Add(path);
            foreach (var path in removed) _tracked.Remove(path);
        }
        foreach (var (path, project) in added)
        {
            _files[project].Add(path);
            MarkForSync(project, path);
        }
        foreach (var path in removed)
        {
            if (_facts.Remove(path, out var old)) MarkForSync(old.Project, path);
            foreach (var set in _files.Values) set.Remove(path);
        }
        foreach (var doc in Parse(result))
        {
            var declarationsChanged = !_facts.TryGetValue(doc.File, out var old) || old.DeclHash != doc.DeclHash;
            _facts[doc.File] = doc;
            if (declarationsChanged) MarkForSync(doc.Project, doc.File);
        }
        return files;
    }

    /// A declaration changed in a project: its other files, and those of the projects that use it, may now
    /// mean something else.
    void MarkForSync(string project, string changedFile)
    {
        if (!_files.ContainsKey(project)) return;
        foreach (var p in (_dependents.GetValueOrDefault(project) ?? []).Prepend(project))
            foreach (var file in _files.GetValueOrDefault(p) ?? [])
                if (!string.Equals(file, changedFile, StringComparison.OrdinalIgnoreCase) && _syncQueued.Add(file)) _syncQueue.Enqueue(file);
    }

    public void DropSync()
    {
        _syncQueue.Clear();
        _syncQueued.Clear();
    }

    public async Task<bool> SyncAsync(Func<bool> interrupted, CancellationToken ct)
    {
        if (_sidecar is not { Running: true })
        {
            DropSync();
            throw new SidecarException(Stopped());
        }

        while (_syncQueue.Count > 0)
        {
            if (interrupted()) return false;
            var chunk = new List<string>();
            while (chunk.Count < SyncChunk && _syncQueue.TryDequeue(out var file))
            {
                _syncQueued.Remove(file);
                if (Tracks(file)) chunk.Add(file);
            }
            if (chunk.Count == 0) continue;
            var result = await CallAsync("extract", new { paths = chunk }, null, CallTimeout, ct);
            foreach (var doc in Parse(result)) _facts[doc.File] = doc;
        }
        return true;
    }

    /// Editors may hold the file briefly while saving; retry a few times.
    static async Task<string?> ReadDiskAsync(string path, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
                return await reader.ReadToEndAsync(ct);
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (IOException) { await Task.Delay(40, ct); }
            catch (UnauthorizedAccessException) { await Task.Delay(40, ct); }
        }
        return null;
    }
}

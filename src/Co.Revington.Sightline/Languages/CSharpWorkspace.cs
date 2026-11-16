using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Co.Revington.Sightline.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace Co.Revington.Sightline.Languages;

/// C# through Roslyn: the solution as MSBuild loads it, and the facts the Extractor reads from each file.
/// One instance lives as long as the session, so restores that were tried are not tried again on a reload.
internal sealed partial class CSharpWorkspace : ILanguageWorkspace
{
    static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", ".vs", ".idea", "node_modules" };
    static readonly HashSet<string> ProjectExtensions = new(StringComparer.OrdinalIgnoreCase) { ".csproj", ".sln", ".slnx", ".props", ".targets" };

    readonly HashSet<string> _restoreTried = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> _restoreProblems = [];
    readonly object _gate = new();

    MSBuildWorkspace? _workspace;
    Solution? _solution;
    List<ProjectFacts> _projects = [];
    Dictionary<ProjectId, string> _projectNodeIds = [];
    List<string> _diagnostics = [];
    int _excluded;

    readonly Dictionary<DocumentId, DocFacts> _facts = [];
    readonly Dictionary<string, List<DocumentId>> _tracked = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<ProjectId> _pendingSync = [];

    public string Language => "csharp";
    public IReadOnlyList<ProjectFacts> Projects => _projects;
    public IEnumerable<DocFacts> Facts => _facts.Values;
    public IReadOnlyList<string> Diagnostics => _diagnostics;
    public int Excluded => _excluded;
    public IEnumerable<string> WatchRoots => _projects.Select(p => p.Dir);
    public bool SyncPending => _pendingSync.Count > 0;

    /// A target this language opens by itself: a solution, a solution filter or a project file.
    public static bool Opens(string target) => IsSolution(target) || target.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);

    public bool Tracks(string path)
    {
        lock (_gate) return _tracked.ContainsKey(path);
    }

    public FileRole RoleOf(string path)
    {
        var file = Path.GetFileName(path);
        var ext = Path.GetExtension(file);
        if (ext.Equals(".cs", StringComparison.OrdinalIgnoreCase)) return FileRole.Source;
        if (ProjectExtensions.Contains(ext) || file.Equals("global.json", StringComparison.OrdinalIgnoreCase)
            || file.Equals("project.assets.json", StringComparison.OrdinalIgnoreCase)) return FileRole.Project;
        return FileRole.None;
    }

    public async Task<string?> ReadTextAsync(string path, CancellationToken ct)
    {
        Solution? solution;
        DocumentId id;
        lock (_gate)
        {
            if (!_tracked.TryGetValue(path, out var ids)) return null;
            (solution, id) = (_solution, ids[0]);
        }

        var doc = solution?.GetDocument(id);
        if (doc is null) return null;
        return (await doc.GetTextAsync(ct)).ToString();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _workspace?.Dispose();
            _workspace = null;
        }
    }

    // ---------------------------------------------------------------- load

    sealed class DirectProgress(Action<ProjectLoadProgress> report) : IProgress<ProjectLoadProgress>
    {
        public void Report(ProjectLoadProgress value) => report(value);
    }

    public async Task LoadAsync(LoadContext context, CancellationToken ct)
    {
        if (context.Restore) await RestoreMissingAsync(context, ct);
        context.Progress("Opening " + context.Name);

        var ws = MSBuildWorkspace.Create();
        var kept = false;
        try
        {
            kept = await LoadIntoAsync(ws, context, ct);
        }
        finally
        {
            // A load that failed or was cancelled must not leave its MSBuild build host running.
            if (!kept) ws.Dispose();
        }
    }

    /// Opens the solution in the workspace and reads every file. True once the workspace is the live one.
    async Task<bool> LoadIntoAsync(MSBuildWorkspace ws, LoadContext context, CancellationToken ct)
    {
        var target = context.Target;
        var filter = context.Filter;
        ws.SkipUnrecognizedProjects = true;
        var progress = new DirectProgress(p => context.Progress($"{p.Operation} {Path.GetFileName(p.FilePath)}"));
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Solution solution;
        if (Directory.Exists(target))
        {
            foreach (var csproj in FindProjects(target).Order())
            {
                if (filter.Excludes(csproj)) excluded.Add(csproj);
                else if (!ws.CurrentSolution.Projects.Any(p => SamePath(p.FilePath ?? "", csproj)))
                    await ws.OpenProjectAsync(csproj, progress, ct);
            }
            solution = ws.CurrentSolution;
        }
        else if (IsSolution(target))
        {
            solution = await ws.OpenSolutionAsync(target, progress, ct);
        }
        else
        {
            solution = (await ws.OpenProjectAsync(target, progress, ct)).Solution;
        }

        // Projects listed in "exclude" still load when others reference them (their types are needed
        // to understand the code that uses them), but they stay off the map.
        var csharp = solution.Projects.Where(p => p.Language == LanguageNames.CSharp && p.FilePath is not null).ToList();
        foreach (var p in csharp)
            if (filter.Excludes(p.FilePath!)) excluded.Add(p.FilePath!);

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
            if (n % 20 == 0) context.Progress($"Analyzing {n}/{docs.Count} files");
        });

        lock (_gate)
        {
            _workspace?.Dispose();
            _workspace = ws;
            _solution = solution;
            _projects = projects;
            _projectNodeIds = projectNodeIds;
            _excluded = excluded.Count;
            var all = _restoreProblems.Concat(ws.Diagnostics.Select(Describe)).Distinct().ToList();
            _diagnostics = all.Count <= 200 ? all : [.. all.Take(200), $"And {all.Count - 200} more."];

            _facts.Clear();
            _tracked.Clear();
            foreach (var (doc, facts) in results)
            {
                _facts[doc.Id] = facts;
                if (!_tracked.TryGetValue(doc.FilePath!, out var ids)) _tracked[doc.FilePath!] = ids = [];
                ids.Add(doc.Id);
            }
            _pendingSync.Clear();
        }
        return true;
    }

    static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    static bool IsSolution(string path) =>
        path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase);

    /// MSBuild's messages, shorter: which project, and what is wrong. Package advisories are warnings, not failures.
    static string Describe(WorkspaceDiagnostic d)
    {
        var m = MsbuildFailure().Match(d.Message);
        var text = m.Success ? $"{Path.GetFileNameWithoutExtension(m.Groups["file"].Value)}: {m.Groups["message"].Value.Trim()}" : d.Message;
        var advisory = text.Contains("known", StringComparison.OrdinalIgnoreCase) && text.Contains("vulnerability", StringComparison.OrdinalIgnoreCase);
        return (d.Kind == WorkspaceDiagnosticKind.Failure && !advisory ? "Error: " : "Warning: ") + text;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^Msbuild failed when processing the file '(?<file>[^']+)' with message: (?<message>.+)$", System.Text.RegularExpressions.RegexOptions.Singleline)]
    private static partial System.Text.RegularExpressions.Regex MsbuildFailure();

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
    async Task RestoreMissingAsync(LoadContext context, CancellationToken ct)
    {
        var target = context.Target;
        // Only the projects the solution lists: other project files in the folder (samples, fuzzers) are not loaded anyway.
        var candidates = (Directory.Exists(target) ? null : SolutionProjects(target)) ?? [.. FindProjects(context.RootDir)];
        var missing = candidates
            .Where(p => !context.Filter.Excludes(p) && !_restoreTried.Contains(p) && File.Exists(p) && !IsRestored(p))
            .ToList();
        if (missing.Count == 0) return;

        // Each project is tried once per run: a feed that needs a login should not slow down every reload.
        foreach (var p in missing) _restoreTried.Add(p);
        context.Progress($"Restoring NuGet packages for {missing.Count} project{(missing.Count == 1 ? "" : "s")}");

        // A solution or a single project restores in one go. When that fails, the projects still missing
        // are restored one by one, so a single project that cannot be restored (it needs a workload or an
        // SDK that is not installed) does not keep all the others from loading properly.
        var together = !Directory.Exists(target) && await RestoreAsync(context.RootDir, target, ct);
        if (!together)
            foreach (var project in missing.Where(p => !IsRestored(p)))
                await RestoreAsync(context.RootDir, project, ct);
    }

    /// True when restored. A failure is kept as a load warning.
    async Task<bool> RestoreAsync(string rootDir, string target, CancellationToken ct)
    {
        // No lingering MSBuild worker processes after the restore.
        var result = await DotnetCli.RunAsync(rootDir, ["restore", target, "-nodeReuse:false"], ct);
        if (result.Code == 0) return true;
        _restoreProblems.Add($"Warning: \"dotnet restore {Path.GetFileName(target)}\" failed, so some references may be missing. {DotnetCli.Tail(result.All, 2)}");
        return false;
    }

    /// The restore output exists: obj/project.assets.json, or the same under artifacts/obj/<project>
    /// for repositories that use MSBuild's artifacts output layout.
    static bool IsRestored(string project)
    {
        var dir = Path.GetDirectoryName(project)!;
        if (File.Exists(Path.Combine(dir, "obj", "project.assets.json"))) return true;

        var name = Path.GetFileNameWithoutExtension(project);
        for (var up = new DirectoryInfo(dir); up is not null; up = up.Parent)
        {
            if (File.Exists(Path.Combine(up.FullName, "artifacts", "obj", name, "project.assets.json"))) return true;
            if (Directory.Exists(Path.Combine(up.FullName, ".git"))) break;
        }
        return false;
    }

    /// The C# projects a .sln, .slnx or .slnf lists, or null when the file cannot be read.
    internal static List<string>? SolutionProjects(string solution)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(solution))!;
            IEnumerable<string> paths;
            if (solution.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                paths = System.Xml.Linq.XDocument.Load(solution).Descendants()
                    .Where(e => e.Name.LocalName == "Project")
                    .Select(e => e.Attribute("Path")?.Value)
                    .OfType<string>();
            }
            else if (solution.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(solution), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                var inner = doc.RootElement.GetProperty("solution");
                dir = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(dir, inner.GetProperty("path").GetString()!)))!;
                paths = inner.GetProperty("projects").EnumerateArray().Select(p => p.GetString()).OfType<string>().ToList();
            }
            else if (solution.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            {
                paths = File.ReadLines(solution)
                    .Select(line => SlnProject().Match(line))
                    .Where(m => m.Success)
                    .Select(m => m.Groups[1].Value);
            }
            else
            {
                return [Path.GetFullPath(solution)];
            }

            return paths
                .Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                .Select(p => Path.GetFullPath(Path.Combine(dir, p.Replace('\\', Path.DirectorySeparatorChar))))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.Xml.XmlException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// Project("{...}") = "Name", "src\Name\Name.csproj", "{...}"
    [System.Text.RegularExpressions.GeneratedRegex(@"^Project\(""[^""]*""\)\s*=\s*""[^""]*"",\s*""([^""]+)""")]
    private static partial System.Text.RegularExpressions.Regex SlnProject();

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

    // ---------------------------------------------------------------- edits

    public async Task<IReadOnlyList<string>> ApplyAsync(IReadOnlyList<FileChange> changes, CancellationToken ct)
    {
        if (_solution is null) return [];
        var touched = new List<DocumentId>();
        var files = new List<string>();

        foreach (var change in changes)
        {
            var path = change.Path;
            // The solution also holds the files of projects left out by sightline.json: those are edited, not added.
            var ids = _solution.GetDocumentIdsWithFilePath(path);

            if (change.Text is not null)
            {
                if (ids.IsEmpty || !await SetTextAsync(path, SourceText.From(change.Text, Encoding.UTF8), ct)) continue;
                touched.AddRange(TrackedIds(path));
                files.Add(path);
                continue;
            }

            var exists = File.Exists(path);
            if (exists && !ids.IsEmpty)
            {
                var text = await ReadDiskAsync(path, ct);
                if (text is null || !await SetTextAsync(path, text, ct)) continue;
                touched.AddRange(TrackedIds(path));
                files.Add(path);
            }
            else if (exists)
            {
                var text = await ReadDiskAsync(path, ct);
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

        if (files.Count > 0) await ReextractAsync(touched, ct);
        return files;
    }

    List<DocumentId> TrackedIds(string path)
    {
        lock (_gate) return _tracked.TryGetValue(path, out var ids) ? [.. ids] : [];
    }

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

    public void DropSync() => _pendingSync.Clear();

    public async Task<bool> SyncAsync(Func<bool> interrupted, CancellationToken ct)
    {
        var solution = _solution!;
        var ids = _facts.Keys.Where(id => _pendingSync.Contains(id.ProjectId)).ToList();
        var results = new ConcurrentBag<(DocumentId Id, DocFacts Facts)>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            await Parallel.ForEachAsync(ids, Parallelism(cts.Token), async (id, c) =>
            {
                // A new edit is waiting: handle it first.
                if (interrupted()) cts.Cancel();
                c.ThrowIfCancellationRequested();
                if (solution.GetDocument(id) is { } doc)
                    results.Add((id, await Extractor.ExtractAsync(doc, _projectNodeIds[id.ProjectId], c)));
            });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;                                            // keep the sync pending and retry when idle
        }

        _pendingSync.Clear();
        foreach (var (id, facts) in results) _facts[id] = facts;
        return true;
    }

    /// Editors may hold the file briefly while saving; retry a few times.
    static async Task<SourceText?> ReadDiskAsync(string path, CancellationToken ct)
    {
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

namespace Co.Revington.Sightline.Live;

/// Watches source folders and reports changed paths in small batches. Which paths matter is up to the
/// languages that are loaded (accepts); this class only keeps out tool folders and build output.
internal sealed class FileWatcher : IDisposable
{
    public const string Rescan = "<rescan>";

    static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".idea", ".vscode", "node_modules",
    };

    /// Folders that JavaScript builds and dev servers empty and fill all the time. While such code is on the
    /// map, a folder removed in there is not a reason to load everything again.
    static readonly HashSet<string> Churn = new(StringComparer.OrdinalIgnoreCase)
    {
        "dist", "build", "out", ".next", ".nuxt", ".svelte-kit", ".output", ".turbo", ".cache", ".parcel-cache",
        ".vite", ".angular", ".vercel", "coverage", ".nyc_output", "storybook-static", "tmp", "temp",
    };

    readonly List<FileSystemWatcher> _watchers = [];
    readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    readonly Func<string, bool> _accepts;
    readonly Func<bool> _churning;
    readonly Action<IReadOnlyList<string>> _onChanges;
    readonly Timer _timer;
    readonly int _quietMs;

    /// accepts: whether a path matters to a loaded language. churning: whether build output folders churn.
    public FileWatcher(IEnumerable<string> roots, Func<string, bool> accepts, Func<bool> churning, Action<IReadOnlyList<string>> onChanges, int quietMs = 120)
    {
        _accepts = accepts;
        _churning = churning;
        _onChanges = onChanges;
        _quietMs = quietMs;
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);

        foreach (var root in roots)
        {
            var w = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            w.Changed += (_, e) => Touch(e.FullPath, false);
            w.Created += (_, e) => Touch(e.FullPath, false);
            w.Deleted += (_, e) => Touch(e.FullPath, true);
            w.Renamed += (_, e) => { Touch(e.OldFullPath, true); Touch(e.FullPath, false); };
            w.Error += (_, _) => Add(Rescan);      // buffer overflow: events were lost
            w.EnableRaisingEvents = true;
            _watchers.Add(w);
        }
    }

    /// Files that say packages were installed, inside folders that are otherwise left alone.
    static bool Signal(string folder, string file) =>
        (folder.Equals("obj", StringComparison.OrdinalIgnoreCase) && file.Equals("project.assets.json", StringComparison.OrdinalIgnoreCase))
        || (folder.Equals("node_modules", StringComparison.OrdinalIgnoreCase) && file is ".package-lock.json" or ".modules.yaml" or ".yarn-integrity");

    void Touch(string path, bool gone)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var file = parts[^1];

        for (var i = 0; i < parts.Length - 1; i++)
            if (SkipDirs.Contains(parts[i]) && !Signal(parts[i], file)) return;
        if (SkipDirs.Contains(file)) return;

        if (_accepts(path)) Add(path);
        else if (gone && Path.GetExtension(file).Length == 0 && !(_churning() && parts.Any(Churn.Contains))) Add(Rescan);   // a folder was removed or renamed
    }

    void Add(string path)
    {
        lock (_pending)
        {
            _pending.Add(path);
            _timer.Change(_quietMs, Timeout.Infinite);
        }
    }

    void Flush()
    {
        List<string> batch;
        lock (_pending)
        {
            if (_pending.Count == 0) return;
            batch = [.. _pending];
            _pending.Clear();
        }
        _onChanges(batch);
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _timer.Dispose();
    }
}

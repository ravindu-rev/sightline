namespace Sightline.Live;

/// Watches source folders and reports changed paths in small batches.
internal sealed class FileWatcher : IDisposable
{
    public const string Rescan = "<rescan>";

    static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".idea", ".vscode", "node_modules",
    };

    static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".sln", ".slnx", ".props", ".targets",
    };

    readonly List<FileSystemWatcher> _watchers = [];
    readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    readonly Action<IReadOnlyList<string>> _onChanges;
    readonly Timer _timer;
    readonly int _quietMs;

    public FileWatcher(IEnumerable<string> roots, Action<IReadOnlyList<string>> onChanges, int quietMs = 120)
    {
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

    void Touch(string path, bool gone)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var file = parts[^1];
        var isAssets = file.Equals("project.assets.json", StringComparison.OrdinalIgnoreCase);

        for (var i = 0; i < parts.Length - 1; i++)
            if (SkipDirs.Contains(parts[i]) && !(isAssets && parts[i].Equals("obj", StringComparison.OrdinalIgnoreCase))) return;
        if (SkipDirs.Contains(file)) return;

        var ext = Path.GetExtension(file);
        if (isAssets || Extensions.Contains(ext) || file.Equals("global.json", StringComparison.OrdinalIgnoreCase)) Add(path);
        else if (gone && ext.Length == 0) Add(Rescan);      // a folder was removed or renamed
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

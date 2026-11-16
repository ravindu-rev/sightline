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

    /// A new folder with more entries than this loads everything again instead of being walked.
    const int MaxWalk = 20_000;

    readonly List<FileSystemWatcher> _watchers = [];
    readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _newFolders = new(StringComparer.OrdinalIgnoreCase);
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
            w.Changed += (_, e) => Touch(root, e.FullPath, gone: false);
            w.Created += (_, e) => Touch(root, e.FullPath, gone: false, appeared: true);
            w.Deleted += (_, e) => Touch(root, e.FullPath, gone: true);
            w.Renamed += (_, e) => { Touch(root, e.OldFullPath, gone: true); Touch(root, e.FullPath, gone: false, appeared: true); };
            w.Error += (_, _) => Add(Rescan);      // buffer overflow: events were lost
            w.EnableRaisingEvents = true;
            _watchers.Add(w);
        }
    }

    /// Files that say packages were installed, inside folders that are otherwise left alone.
    static bool Signal(string folder, string file) =>
        (folder.Equals("obj", StringComparison.OrdinalIgnoreCase) && file.Equals("project.assets.json", StringComparison.OrdinalIgnoreCase))
        || (folder.Equals("node_modules", StringComparison.OrdinalIgnoreCase) && file is ".package-lock.json" or ".modules.yaml" or ".yarn-integrity");

    /// appeared: the path was created or renamed into place (Changed also fires for folders whose files change).
    /// Folder names count from the watched root down: a repository may itself sit in a folder called bin or temp.
    void Touch(string root, string path, bool gone, bool appeared = false)
    {
        var parts = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var file = parts[^1];

        for (var i = 0; i < parts.Length - 1; i++)
            if (SkipDirs.Contains(parts[i]) && !Signal(parts[i], file)) return;
        if (SkipDirs.Contains(file)) return;

        if (_accepts(path)) Add(path);
        else if (gone && Path.GetExtension(file).Length == 0 && !Churning(parts)) Add(Rescan);   // a folder was removed or renamed
        else if (appeared && !Churning(parts) && IsRealFolder(path)) Add(path, folder: true);   // a folder appeared
    }

    /// A folder that is there and is not a link. Links are not followed: one could lead anywhere on the machine,
    /// and what is behind it would then be read as code of the repository.
    static bool IsRealFolder(string path)
    {
        try
        {
            var dir = new DirectoryInfo(path);
            return dir.Exists && dir.LinkTarget is null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    static bool IsLinkFile(FileSystemInfo file)
    {
        try { return file.Attributes.HasFlag(FileAttributes.ReparsePoint) && file.LinkTarget is not null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }

    bool Churning(string[] parts) => _churning() && parts.Any(Churn.Contains);

    void Add(string path, bool folder = false)
    {
        lock (_pending)
        {
            (folder ? _newFolders : _pending).Add(path);
            _timer.Change(_quietMs, Timeout.Infinite);
        }
    }

    void Flush()
    {
        List<string> batch, folders;
        lock (_pending)
        {
            if (_pending.Count == 0 && _newFolders.Count == 0) return;
            batch = [.. _pending];
            folders = [.. _newFolders];
            _pending.Clear();
            _newFolders.Clear();
        }
        if (folders.Count > 0) batch = [.. batch.Union(FilesIn(folders), StringComparer.OrdinalIgnoreCase)];
        if (batch.Count > 0) _onChanges(batch);
    }

    /// The files that matter in folders that just appeared. A folder moved in raises one event for the folder
    /// and none for its files, and Linux starts watching a new folder only once it hears of it, so files written
    /// into it straight away (a checkout, a new package with its package.json) raise no event of their own.
    /// Links are not followed, to folders or to files. A folder too big to walk loads everything again instead.
    List<string> FilesIn(List<string> folders)
    {
        var found = new List<string>();
        var walk = new Stack<string>(folders.Where(f => !folders.Any(other => IsUnder(f, other))));
        var seen = 0;
        while (walk.Count > 0)
        {
            var folder = walk.Pop();
            if (!IsRealFolder(folder)) continue;     // checked again: it may have been replaced by a link since

            List<FileSystemInfo> entries;
            try { entries = [.. new DirectoryInfo(folder).EnumerateFileSystemInfos()]; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }   // gone again, or not readable

            foreach (var entry in entries)
            {
                if (++seen > MaxWalk) return [Rescan];
                if (entry is DirectoryInfo dir)
                {
                    if (!SkipDirs.Contains(dir.Name) && !(_churning() && Churn.Contains(dir.Name)))
                        walk.Push(dir.FullName);
                }
                else if (!IsLinkFile(entry) && _accepts(entry.FullName))
                {
                    found.Add(entry.FullName);
                }
            }
        }
        return found;
    }

    static bool IsUnder(string path, string folder) =>
        path.Length > folder.Length + 1 && path[folder.Length] is '/' or '\\' && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _timer.Dispose();
    }
}

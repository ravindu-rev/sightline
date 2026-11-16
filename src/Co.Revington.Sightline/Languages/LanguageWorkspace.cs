using Co.Revington.Sightline.Analysis;
using Co.Revington.Sightline.Settings;

namespace Co.Revington.Sightline.Languages;

/// What a changed file means to a language: nothing, code to read again, or part of how its projects are
/// set up, so the language loads again.
internal enum FileRole { None, Source, Project }

/// New text for a file, or Text = null: read it from disk, or it is gone.
internal sealed record FileChange(string Path, string? Text);

/// For "diff": the checkout of another commit (From) has no installed packages, so it uses those of the
/// working tree (To), at the same place relative to each.
internal sealed record PackageMirror(string From, string To);

/// What a workspace needs to load. Name: what the status line calls the code being opened.
internal sealed record LoadContext(
    string Target, string RootDir, string Name, ProjectFilter Filter, bool Restore, Action<string> Progress, PackageMirror? PackagesFrom = null);

/// A language cannot be read on this machine, such as JavaScript without Node.js. The rest of the map still shows.
internal sealed class LanguageUnavailableException(string message) : Exception(message);

/// The code of one language under the target: its projects, and the facts read from each file.
/// The session's loop calls LoadAsync, ApplyAsync and SyncAsync one at a time. The members marked
/// "any thread" are also called by the web server and the file watcher, so they are answered from
/// what the workspace keeps in memory, never by asking another process.
internal interface ILanguageWorkspace : IDisposable
{
    /// "csharp" or "typescript", as "languages" in sightline.json names them.
    string Language { get; }

    /// What the map shows, as of the last successful load and the edits since.
    IReadOnlyList<ProjectFacts> Projects { get; }
    IEnumerable<DocFacts> Facts { get; }
    IReadOnlyList<string> Diagnostics { get; }

    /// Projects that sightline.json leaves out.
    int Excluded { get; }

    /// Folders to watch besides the root.
    IEnumerable<string> WatchRoots { get; }

    /// Loads, or loads again. Nothing visible changes unless it succeeds. Work pending for SyncAsync is dropped.
    Task LoadAsync(LoadContext context, CancellationToken ct);

    /// Any thread. True for files whose code is on the map.
    bool Tracks(string path);

    /// Any thread. What a change to this path means here.
    FileRole RoleOf(string path);

    /// Any thread. The text the map was made from, for a file that Tracks.
    Task<string?> ReadTextAsync(string path, CancellationToken ct);

    /// Applies all changes, then reads again what they touch. Files that are new to the workspace are added
    /// when they belong to one of its projects. Returns the files whose facts may have changed.
    Task<IReadOnlyList<string>> ApplyAsync(IReadOnlyList<FileChange> changes, CancellationToken ct);

    /// A declaration changed, so other files may now mean something else.
    bool SyncPending { get; }

    /// Reads those files again. Returns false only when interrupted() said a new edit is waiting;
    /// the work then stays pending. True once it is done.
    Task<bool> SyncAsync(Func<bool> interrupted, CancellationToken ct);

    /// After a failed sync: give up on the pending work.
    void DropSync();
}

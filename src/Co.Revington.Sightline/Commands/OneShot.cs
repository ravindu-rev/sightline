using System.Text.Json;
using System.Text.Json.Nodes;
using Co.Revington.Sightline.Analysis;
using Co.Revington.Sightline.Live;

namespace Co.Revington.Sightline.Commands;

/// Loading a solution once, for commands that report and exit. Progress goes to stderr, so
/// stdout stays clean for JSON and for pipes.
internal static class OneShot
{
    public static async Task<(LiveSession Session, Snapshot Snapshot)> LoadAsync(SolutionTarget target, CancellationToken ct = default)
    {
        var options = new LoadOptions(target.Filter, target.Restore, target.Checks, Watch: false, Log: Console.Error);
        var session = new LiveSession(target.Path, new EventHub(), options);
        var snapshot = await session.LoadOnceAsync(ct);
        return (session, snapshot);
    }

    /// The top of the git repository that holds a folder, or the folder itself when there is none.
    public static string RepoRoot(string folder)
    {
        for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent)
        {
            var git = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git)) return dir.FullName;
        }
        return folder;
    }
}

/// Rewrites paths from this machine so they can be shared: paths under the repository become
/// relative ("src/App/Program.cs"), and the user's home folder becomes "~".
internal sealed class PathScrubber(string root)
{
    readonly string[] _roots = Variants(Path.GetFullPath(root).TrimEnd('\\', '/'));
    readonly string[] _homes = Variants(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/'));

    static string[] Variants(string path) => path.Length == 0 ? [] : [path, path.Replace('\\', '/'), path.Replace('/', '\\')];

    public string Root => _roots[0];

    /// A file path relative to the repository, with forward slashes.
    public string Relative(string path)
    {
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(Root, full);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? Scrub(full) : relative.Replace('\\', '/');
    }

    public string Scrub(string text)
    {
        foreach (var root in _roots.Distinct())
        {
            for (var at = text.IndexOf(root, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(root, at, StringComparison.OrdinalIgnoreCase))
            {
                var end = at + root.Length;
                if (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] is '.' or '-' or '_'))
                {
                    at = end;                  // "C:\repo" inside "C:\repository": a different folder
                    continue;
                }
                var skip = end < text.Length && text[end] is '\\' or '/' ? 1 : 0;
                // The rest of the path goes to forward slashes too.
                var stop = end + skip;
                while (stop < text.Length && !char.IsWhiteSpace(text[stop]) && text[stop] is not ('"' or '\'' or ')' or ',' or ';')) stop++;
                var rest = text[(end + skip)..stop].Replace('\\', '/');
                var replacement = rest.Length == 0 ? "." : rest;
                text = string.Concat(text.AsSpan(0, at), replacement, text.AsSpan(stop));
                at += replacement.Length;
            }
        }
        foreach (var home in _homes.Distinct())
            text = text.Replace(home, "~", StringComparison.OrdinalIgnoreCase);
        return text;
    }

    /// The same rewrite applied to every string in a JSON document, keys included.
    public JsonNode? Scrub(JsonNode? node) => node switch
    {
        JsonValue v when v.GetValueKind() == JsonValueKind.String => JsonValue.Create(Scrub(v.GetValue<string>())),
        JsonArray a => new JsonArray(a.Select(x => Scrub(x?.DeepClone())).ToArray()),
        JsonObject o => new JsonObject(o.Select(kv => KeyValuePair.Create(Scrub(kv.Key), Scrub(kv.Value?.DeepClone())))),
        _ => node?.DeepClone(),
    };
}

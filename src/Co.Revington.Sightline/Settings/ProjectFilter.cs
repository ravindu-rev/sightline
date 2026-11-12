using System.Text;
using System.Text.RegularExpressions;

namespace Co.Revington.Sightline.Settings;

/// Decides which projects stay off the map, from the "exclude" list in sightline.json.
/// A pattern can name a project ("Shop.Tests"), a folder ("tests") or a path relative to the
/// settings file ("samples/**"), and may use * and ** the way .gitignore does.
internal sealed class ProjectFilter
{
    public static readonly ProjectFilter None = new("", []);

    readonly string _baseDir;
    readonly Regex[] _patterns;

    public ProjectFilter(string baseDir, IEnumerable<string> patterns)
    {
        _baseDir = baseDir;
        _patterns = patterns.Select(ToRegex).ToArray();
    }

    public bool IsEmpty => _patterns.Length == 0;

    public bool Excludes(string projectFile)
    {
        if (_patterns.Length == 0) return false;

        var relative = Path.GetRelativePath(_baseDir, projectFile).Replace('\\', '/');
        var candidates = new List<string> { Path.GetFileNameWithoutExtension(projectFile), relative };

        // Every folder on the way to the project, so "tests" matches "tests/App.Tests/App.Tests.csproj".
        for (var slash = relative.IndexOf('/'); slash > 0; slash = relative.IndexOf('/', slash + 1))
            candidates.Add(relative[..slash]);

        return _patterns.Any(pattern => candidates.Any(pattern.IsMatch));
    }

    /// "*" is anything within one folder name, "**" is anything across folders, "?" is one character.
    static Regex ToRegex(string pattern)
    {
        var glob = pattern.Trim().Replace('\\', '/').TrimEnd('/');
        if (glob.StartsWith("./", StringComparison.Ordinal)) glob = glob[2..];

        var regex = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                var folders = i + 2 < glob.Length && glob[i + 2] == '/';
                regex.Append(folders ? "(?:.*/)?" : ".*");
                i += folders ? 2 : 1;
            }
            else if (c == '*') regex.Append("[^/]*");
            else if (c == '?') regex.Append("[^/]");
            else regex.Append(Regex.Escape(c.ToString()));
        }

        return new Regex(regex.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

using System.Text.Json;
using System.Text.RegularExpressions;

namespace Co.Revington.Sightline.Languages.TypeScript;

/// One package.json with code of its own. Dependencies: name -> version as written. Config: its
/// tsconfig.json or jsconfig.json. Files: the code on the map; Declarations: .d.ts files, read for their
/// types but not shown.
internal sealed record NpmPackage(
    string Name, string Dir, string Manifest, IReadOnlyDictionary<string, string> Dependencies, bool HasBin,
    string? Config, string? OutDir, IReadOnlyList<string> Files, IReadOnlyList<string> Declarations)
{
    public bool IsTypeScript => Files.Any(f => NpmPackages.IsTypeScriptFile(f)) || Path.GetFileName(Config ?? "") == "tsconfig.json";
}

/// What a folder holds: the packages, every folder with a package.json (packages or not), the folders of
/// packages that count but have no code yet, and what could not be read.
internal sealed record NpmScan(IReadOnlyList<NpmPackage> Packages, IReadOnlySet<string> ManifestDirs, IReadOnlySet<string> Empty, IReadOnlyList<string> Warnings);

/// Finds the npm packages under a folder, the way npm, yarn and pnpm workspaces define them, and the
/// files that make up each one. Everything here is plain file reading: nothing is installed or run.
internal static class NpmPackages
{
    public static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".mjs", ".cjs",
    };

    static readonly HashSet<string> TypeScriptExtensions = new(StringComparer.OrdinalIgnoreCase) { ".ts", ".tsx", ".mts", ".cts" };

    /// Folders that hold installed packages, build output, tool state or tests, never the code of the app.
    public static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".hg", ".svn", ".vs", ".idea", ".vscode", ".yarn", ".pnpm-store",
        "dist", "build", "out", ".next", ".nuxt", ".svelte-kit", ".output", ".turbo", ".cache", ".parcel-cache", ".vite",
        ".angular", ".vercel", ".netlify", ".expo", ".docusaurus", "coverage", ".nyc_output", "storybook-static",
        "bower_components", "jspm_packages", "vendor", "obj", "artifacts", "TestResults", "tmp", "temp",
        "test", "tests", "__tests__", "__mocks__", "__snapshots__", "__fixtures__", "fixtures", "e2e", "cypress", "playwright",
    };

    const long MaxFileSize = 1024 * 1024;

    public static bool IsTypeScriptFile(string path) => TypeScriptExtensions.Contains(Path.GetExtension(path)) && !IsDeclaration(path);

    public static bool IsDeclaration(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".d.mts", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".d.cts", StringComparison.OrdinalIgnoreCase);
    }

    /// Test files, stories, configuration, minified bundles and dot files are not the code of the app.
    public static bool IsCodeName(string fileName)
    {
        if (fileName.StartsWith('.') || !SourceExtensions.Contains(Path.GetExtension(fileName)) || IsDeclaration(fileName)) return false;
        var parts = fileName.Split('.');
        if (parts.Length > 2 && parts[1..^1].Any(p => p is "test" or "spec" or "stories" or "story" or "config" or "min" or "bundle")) return false;
        return true;
    }

    /// True when a folder or one below it has a package.json with code.
    public static bool Any(string root) => Scan(root, quick: true).Packages.Count > 0;

    public static NpmScan Scan(string root, bool quick = false)
    {
        var warnings = new List<string>();
        var manifests = new List<(string Dir, string File, JsonElement Json)>();
        foreach (var file in FindManifests(root))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("it does not hold a JSON object");
                manifests.Add((Path.GetDirectoryName(file)!, file, doc.RootElement.Clone()));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{Relative(root, file)} could not be read, so that package is left out: {ex.Message}");
            }
        }

        // Workspace roots decide which packages inside them count: their members. Other package.json files
        // inside a workspace (fixtures, templates, examples) are not packages of the repository.
        var chosen = new List<(string Dir, string File, JsonElement Json)>();
        var inWorkspace = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in manifests)
        {
            var globs = WorkspaceGlobs(m.Dir, m.Json);
            if (globs is null) continue;
            chosen.Add(m);
            inWorkspace.Add(m.Dir);
            var (include, exclude) = (globs.Where(g => !g.StartsWith('!')).Select(Glob).ToList(), globs.Where(g => g.StartsWith('!')).Select(g => Glob(g[1..])).ToList());
            foreach (var other in manifests)
            {
                if (other.Dir == m.Dir || !Inside(other.Dir, m.Dir)) continue;
                inWorkspace.Add(other.Dir);
                var relative = Relative(m.Dir, other.Dir);
                if (include.Any(r => r.IsMatch(relative)) && !exclude.Any(r => r.IsMatch(relative))) chosen.Add(other);
            }
        }
        foreach (var m in manifests)
            if (!inWorkspace.Contains(m.Dir)) chosen.Add(m);

        var manifestDirs = manifests.Select(m => m.Dir).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var packages = new List<NpmPackage>();
        var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (dir, file, json) in chosen.DistinctBy(c => c.Dir, StringComparer.OrdinalIgnoreCase).OrderBy(c => c.Dir, StringComparer.OrdinalIgnoreCase))
        {
            var config = new[] { "tsconfig.json", "jsconfig.json" }.Select(n => Path.Combine(dir, n)).FirstOrDefault(File.Exists);
            var outDir = config is null ? null : OutDir(config);
            var (files, declarations) = ListFiles(dir, manifestDirs, outDir, quick);
            if (files.Count == 0)
            {
                empty.Add(dir);
                continue;
            }
            packages.Add(new NpmPackage(
                json.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString()!.Trim().Length > 0 ? n.GetString()!.Trim() : new DirectoryInfo(dir).Name,
                dir, file, Dependencies(json), json.TryGetProperty("bin", out _), config, outDir, files, declarations));
            if (quick) break;
        }
        return new NpmScan(packages, manifestDirs, empty, warnings);
    }

    /// A file that is a link to another file. Links are left out: one could point at a file anywhere on the
    /// machine, and the map would then show it, and export it, as code of the repository.
    public static bool IsLinkFile(string file)
    {
        try { return new FileInfo(file).LinkTarget is not null; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// package.json files under a folder, without walking into installed packages, build output or tests.
    static IEnumerable<string> FindManifests(string root)
    {
        var pending = new Stack<(string Dir, int Depth)>([(root, 0)]);
        while (pending.TryPop(out var at))
        {
            string[] dirs;
            var manifest = Path.Combine(at.Dir, "package.json");
            if (File.Exists(manifest)) yield return manifest;
            if (at.Depth >= 12) continue;
            try
            {
                dirs = Directory.GetDirectories(at.Dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }
            foreach (var sub in dirs)
            {
                var name = Path.GetFileName(sub);
                if (SkipDirs.Contains(name) || name.Equals("bin", StringComparison.OrdinalIgnoreCase) || IsLink(sub)) continue;
                pending.Push((sub, at.Depth + 1));
            }
        }
    }

    static bool IsLink(string dir)
    {
        try { return new DirectoryInfo(dir).LinkTarget is not null; }
        catch (IOException) { return true; }
    }

    /// The code files of a package: below its folder, but not in folders that hold another package.json,
    /// installed packages, build output (including tsconfig's outDir) or tests. In a folder that is also a
    /// .NET project, bin, obj and wwwroot/lib hold build output and copied libraries.
    static (List<string> Files, List<string> Declarations) ListFiles(string packageDir, IReadOnlySet<string> manifestDirs, string? outDir, bool quick)
    {
        var files = new List<string>();
        var declarations = new List<string>();
        var dotnet = IsDotnetFolder(packageDir);
        var pending = new Stack<(string Dir, int Depth)>([(packageDir, 0)]);
        while (pending.TryPop(out var at))
        {
            string[] here, dirs;
            try
            {
                here = Directory.GetFiles(at.Dir);
                dirs = Directory.GetDirectories(at.Dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var file in here.Order(StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(file);
                if ((IsDeclaration(name) || IsCodeName(name)) && IsLinkFile(file)) continue;
                if (IsDeclaration(name))
                {
                    declarations.Add(file);
                }
                else if (IsCodeName(name) && Small(file))
                {
                    files.Add(file);
                    if (quick) return (files, declarations);
                }
            }
            if (at.Depth >= 20) continue;
            foreach (var sub in dirs)
                if (!Skipped(packageDir, sub, outDir, dotnet) && !manifestDirs.Contains(sub) && !IsLink(sub)) pending.Push((sub, at.Depth + 1));
        }
        files.Sort(StringComparer.OrdinalIgnoreCase);
        return (files, declarations);
    }

    static bool Small(string file)
    {
        try { return new FileInfo(file).Length <= MaxFileSize; }
        catch (IOException) { return false; }
    }

    /// Whether a folder inside a package holds no code of it.
    public static bool Skipped(string packageDir, string dir, string? outDir, bool dotnet)
    {
        var name = Path.GetFileName(dir);
        if (SkipDirs.Contains(name)) return true;
        // bin next to a .NET project file is its build output; elsewhere it holds a package's command-line scripts.
        if (name.Equals("bin", StringComparison.OrdinalIgnoreCase) && (dotnet || IsDotnetFolder(Path.GetDirectoryName(dir)!))) return true;
        if (name.Equals("lib", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(Path.GetDirectoryName(dir)) is { } parent
            && parent.Equals("wwwroot", StringComparison.OrdinalIgnoreCase)) return true;
        return outDir is not null && string.Equals(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar), outDir, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDotnetFolder(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.*proj").Any(f => Path.GetExtension(f) is ".csproj" or ".fsproj" or ".vbproj");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// compilerOptions.outDir of a tsconfig.json, as a full path. Comments and trailing commas are allowed there.
    static string? OutDir(string config)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(config), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.TryGetProperty("compilerOptions", out var options) && options.TryGetProperty("outDir", out var outDir)
                && outDir.ValueKind == JsonValueKind.String && outDir.GetString() is { Length: > 0 } value)
                return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(config)!, value)).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
        return null;
    }

    /// "workspaces" in package.json (a list, or { "packages": [...] }), or a pnpm-workspace.yaml next to it.
    /// Null when the folder is not a workspace root.
    static List<string>? WorkspaceGlobs(string dir, JsonElement json)
    {
        if (json.TryGetProperty("workspaces", out var ws))
        {
            var list = ws.ValueKind == JsonValueKind.Array ? ws : ws.ValueKind == JsonValueKind.Object && ws.TryGetProperty("packages", out var p) ? p : default;
            if (list.ValueKind == JsonValueKind.Array)
                return list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim()).Where(g => g.Length > 0).ToList();
        }

        var pnpm = Path.Combine(dir, "pnpm-workspace.yaml");
        if (!File.Exists(pnpm)) return null;
        var globs = new List<string>();
        var inPackages = false;
        foreach (var raw in File.ReadLines(pnpm))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.TrimStart().StartsWith('#')) continue;
            if (!char.IsWhiteSpace(line[0]) && !line.StartsWith('-'))
            {
                inPackages = line.StartsWith("packages:", StringComparison.Ordinal);
                continue;
            }
            var item = line.Trim();
            if (inPackages && item.StartsWith('-')) globs.Add(item[1..].Trim().Trim('\'', '"'));
        }
        return globs;
    }

    /// A workspace pattern ("packages/*", "apps/**", "./tools/cli") as a pattern for folders relative to the root.
    static Regex Glob(string pattern)
    {
        var glob = pattern.Trim().Replace('\\', '/').TrimEnd('/');
        if (glob.StartsWith("./", StringComparison.Ordinal)) glob = glob[2..];
        var regex = new System.Text.StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                regex.Append(".*");
                i++;
                if (i + 1 < glob.Length && glob[i + 1] == '/') { regex.Append("/?"); i++; }
            }
            else if (c == '*') regex.Append("[^/]*");
            else if (c == '?') regex.Append("[^/]");
            else regex.Append(Regex.Escape(c.ToString()));
        }
        return new Regex(regex.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// Every dependency a package names, with the version as written. Type packages (@types/...) only
    /// describe other packages, so they are left out.
    static Dictionary<string, string> Dependencies(JsonElement json)
    {
        var deps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in new[] { "dependencies", "peerDependencies", "optionalDependencies", "devDependencies" })
        {
            if (!json.TryGetProperty(section, out var list) || list.ValueKind != JsonValueKind.Object) continue;
            foreach (var d in list.EnumerateObject())
            {
                if (d.Name.StartsWith("@types/", StringComparison.Ordinal)) continue;
                deps.TryAdd(d.Name, d.Value.ValueKind == JsonValueKind.String ? d.Value.GetString()! : "");
            }
        }
        return deps;
    }

    public static bool Inside(string path, string dir) =>
        path.StartsWith(dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    static string Relative(string from, string path) => Path.GetRelativePath(from, path).Replace('\\', '/');

    /// Exact versions from package-lock.json, for the packages a package depends on. The lock file sits in
    /// the package's folder or in the folder of the workspace it belongs to.
    public static Dictionary<string, string> LockedVersions(string packageDir, string root, Dictionary<string, JsonDocument?> cache)
    {
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var dir = packageDir; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var file = Path.Combine(dir, "package-lock.json");
            if (!cache.TryGetValue(file, out var doc))
            {
                try
                {
                    doc = File.Exists(file) ? JsonDocument.Parse(File.ReadAllText(file)) : null;
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    doc = null;
                }
                cache[file] = doc;
            }
            if (doc is not null && doc.RootElement.TryGetProperty("packages", out var packages) && packages.ValueKind == JsonValueKind.Object)
            {
                var own = Relative(dir, packageDir);
                foreach (var entry in packages.EnumerateObject())
                {
                    var key = entry.Name;
                    string name;
                    if (own != "." && key.StartsWith(own + "/node_modules/", StringComparison.OrdinalIgnoreCase)) name = key[(own.Length + "/node_modules/".Length)..];
                    else if (key.StartsWith("node_modules/", StringComparison.Ordinal)) name = key["node_modules/".Length..];
                    else continue;
                    if (name.Contains("/node_modules/", StringComparison.Ordinal)) continue;
                    if (entry.Value.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
                    {
                        // Entries for the package's own folder win over the ones at the top of the workspace.
                        if (key.StartsWith("node_modules/", StringComparison.Ordinal)) versions.TryAdd(name, v.GetString()!);
                        else versions[name] = v.GetString()!;
                    }
                }
                return versions;
            }
            if (string.Equals(dir.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) break;
        }
        return versions;
    }
}

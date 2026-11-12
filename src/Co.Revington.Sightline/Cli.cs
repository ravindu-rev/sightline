using Co.Revington.Sightline.Analysis;
using Co.Revington.Sightline.Settings;
using Co.Revington.Sightline.Setup;

namespace Co.Revington.Sightline;

internal abstract record CliCommand;
internal sealed record MapCommand(MapOptions Options) : CliCommand;
internal sealed record InitCommand(InitOptions Options) : CliCommand;
internal sealed record ExitCommand(int Code) : CliCommand;

/// "sightline check": report problems, exit with 1 when there are any at FailOn severity or worse.
internal sealed record CheckCommand(SolutionTarget Target, string Format, string FailOn) : CliCommand;

/// "sightline export": the map as a static website (Out) and/or as JSON (Json).
/// SourceUrl: where the page loads source files from instead of bundling them, with {path} in it.
internal sealed record ExportCommand(SolutionTarget Target, string? Out, string? Json, bool Sources, string? SourceUrl, string? Title) : CliCommand;

/// "sightline diff": what a change does to the map. Either Base (a git commit to compare the
/// working tree with) or two JSON files written by "sightline export --json".
internal sealed record DiffCommand(SolutionTarget? Target, string? Base, string? BaseFile, string? HeadFile, string Format, string? Out, string FailOn) : CliCommand;

/// "sightline mcp": the map for AI assistants, over the Model Context Protocol on stdin and stdout.
internal sealed record McpCommand(SolutionTarget Target) : CliCommand;

/// The solution a command works on, with its settings.
internal sealed record SolutionTarget(string Path, SightlineConfig? Config, ProjectFilter Filter, bool Restore)
{
    public CheckOptions Checks => Config?.Checks ?? CheckOptions.None;
}

/// What "sightline [path]" opens, and how. Command-line options win over sightline.json, which wins over defaults.
/// AttachPid: the app this map runs beside (--attach), or null for a map opened by hand.
internal sealed record MapOptions(
    string Target, int Port, bool Open, bool Restore, string UiDir, string? Token, SightlineConfig? Config, ProjectFilter Filter,
    int? AttachPid = null)
{
    public CheckOptions Checks => Config?.Checks ?? CheckOptions.None;
}

internal static class Cli
{
    const int DefaultPort = 5210;

    static string Help => $$"""
        Sightline {{ToolInfo.Version}}: a live, editable map of a .NET solution

        Usage:
          sightline [path] [options]          Open the map
          sightline init [folder]             Set a solution up for everyone who works on it
          sightline check [path]              List problems; exit code 1 when there are errors
          sightline export [path] --out <dir> Write the map as a static website anyone can open
          sightline diff [path] --base <ref>  What a change does to the map, for pull requests
          sightline mcp [path]                Serve the map to AI assistants (Model Context Protocol)

        The path is a .sln, .slnx or .csproj file, or a folder. Without it, Sightline opens
        the solution named in the nearest sightline.json, or the nearest solution in this
        folder or above it.

        Options:
          --port <n>          Port for the map page (default 5210, or the next free one)
          --no-open           Do not open the browser
          --no-restore        Do not run "dotnet restore" for projects that were never restored
          --version           Show the version
          -h, --help          Show this help

        Options for init:
          --solution <file>   The solution to open, when there are several
          --vscode            Add the VS Code task even if there is no .vscode folder
          --no-vscode         Do not add the VS Code task
          --no-tool           Do not pin Sightline in the tool manifest

        Options for check:
          --format <f>        text (default), json, or github (annotations for GitHub Actions)
          --fail-on <level>   error (default), warning, info, or never

        Options for export:
          --out <folder>      Write a static website: open index.html, or host the folder anywhere
          --json <file>       Write the map as JSON (what "diff" compares)
          --no-source         Leave the source code out of the website
          --source-url <url>  Load source code from this address instead, with {path} for the file,
                              e.g. https://raw.githubusercontent.com/owner/repo/<commit>/{path}
          --title <text>      Title shown on the page

        Options for diff:
          --base <ref>        The git commit, branch or tag to compare with, e.g. origin/main
          <base.json> <head.json>  Or compare two files written by "export --json"
          --format <f>        markdown (default), text, or json
          --out <file>        Write the result to a file instead of the console
          --fail-on <level>   Exit with 1 when the change adds problems: error, warning, or never (default)

        For tools:
          --attach <pid>      Run beside process <pid>: join the map already open for this
                              solution, and close 10 seconds after the last such process ends
                              (used by the Co.Revington.Sightline.Hosting package)
          --ui <folder>       Serve the page from this folder
          --token <value>     Use this API token instead of a random one

        More at https://sightline.revington.co
        """;

    public static CliCommand Parse(string[] args) => args.FirstOrDefault() switch
    {
        "init" => ParseInit(args[1..]),
        "check" => ParseCheck(args[1..]),
        "export" => ParseExport(args[1..]),
        "diff" => ParseDiff(args[1..]),
        "mcp" => ParseMcp(args[1..]),
        _ => ParseMap(args),
    };

    static CliCommand ParseMap(string[] args)
    {
        string? path = null, ui = null, token = null;
        int? port = null, attach = null;
        bool noOpen = false, noRestore = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;

            switch (arg)
            {
                case "-h" or "--help":
                    Console.WriteLine(Help);
                    return new ExitCommand(0);
                case "--version":
                    Console.WriteLine($"Sightline {ToolInfo.Version}");
                    return new ExitCommand(0);
                case "--no-open":
                    noOpen = true;
                    break;
                case "--no-restore":
                    noRestore = true;
                    break;
                case "--port":
                    if (!int.TryParse(Next(), out var p) || p is < 1 or > 65535) return Fail("--port needs a number from 1 to 65535.");
                    port = p;
                    break;
                case "--ui":
                    ui = Next();
                    if (ui is null) return Fail("--ui needs a folder.");
                    break;
                case "--token":
                    token = Next();
                    if (token is null) return Fail("--token needs a value.");
                    break;
                case "--attach":
                    if (!int.TryParse(Next(), out var pid) || pid <= 0) return Fail("--attach needs a process id.");
                    attach = pid;
                    break;
                default:
                    if (arg.StartsWith('-')) return Fail($"Unknown option {arg}.");
                    if (path is not null) return Fail($"Give one path only, not both {path} and {arg}.");
                    path = arg;
                    break;
            }
        }

        if (Resolve(path, noRestore, out var target) is { } failed) return failed;
        var config = target.Config;

        return new MapCommand(new MapOptions(
            Target: target.Path,
            Port: port ?? config?.Port ?? DefaultPort,
            Open: !noOpen && (config?.OpenBrowser ?? true),
            Restore: target.Restore,
            UiDir: ui is null ? Path.Combine(AppContext.BaseDirectory, "wwwroot") : Path.GetFullPath(ui),
            Token: token,
            Config: config,
            Filter: target.Filter,
            AttachPid: attach));
    }

    static CliCommand ParseCheck(string[] args)
    {
        string? path = null;
        var format = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" ? "github" : "text";
        var failOn = "error";
        var noRestore = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (arg)
            {
                case "-h" or "--help":
                    Console.WriteLine(Help);
                    return new ExitCommand(0);
                case "--no-restore": noRestore = true; break;
                case "--format":
                    format = Next() ?? "";
                    if (format is not ("text" or "json" or "github")) return Fail("--format is text, json or github.");
                    break;
                case "--fail-on":
                    failOn = Next() ?? "";
                    if (failOn is not ("error" or "warning" or "info" or "never")) return Fail("--fail-on is error, warning, info or never.");
                    break;
                default:
                    if (arg.StartsWith('-')) return Fail($"Unknown option {arg} for check.");
                    if (path is not null) return Fail($"Give one path only, not both {path} and {arg}.");
                    path = arg;
                    break;
            }
        }

        if (Resolve(path, noRestore, out var target) is { } failed) return failed;
        return new CheckCommand(target, format, failOn);
    }

    static CliCommand ParseExport(string[] args)
    {
        string? path = null, outDir = null, json = null, sourceUrl = null, title = null;
        bool noRestore = false, sources = true;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (arg)
            {
                case "-h" or "--help":
                    Console.WriteLine(Help);
                    return new ExitCommand(0);
                case "--no-restore": noRestore = true; break;
                case "--no-source": sources = false; break;
                case "--out":
                    outDir = Next();
                    if (outDir is null) return Fail("--out needs a folder.");
                    break;
                case "--json":
                    json = Next();
                    if (json is null) return Fail("--json needs a file name.");
                    break;
                case "--source-url":
                    sourceUrl = Next();
                    if (sourceUrl is null || !sourceUrl.Contains("{path}", StringComparison.Ordinal) || !Uri.TryCreate(sourceUrl.Replace("{path}", "x"), UriKind.Absolute, out var u) || u.Scheme != "https")
                        return Fail("--source-url needs an https address with {path} in it.");
                    break;
                case "--title":
                    title = Next();
                    if (string.IsNullOrWhiteSpace(title)) return Fail("--title needs a text.");
                    break;
                default:
                    if (arg.StartsWith('-')) return Fail($"Unknown option {arg} for export.");
                    if (path is not null) return Fail($"Give one path only, not both {path} and {arg}.");
                    path = arg;
                    break;
            }
        }

        if (outDir is null && json is null) return Fail("Say where to write the export: --out <folder> for a website, --json <file> for data.");
        if (Resolve(path, noRestore, out var target) is { } failed) return failed;
        return new ExportCommand(target, outDir is null ? null : Path.GetFullPath(outDir), json is null ? null : Path.GetFullPath(json),
            sources && sourceUrl is null, sourceUrl, title);
    }

    static CliCommand ParseDiff(string[] args)
    {
        string? baseRef = null, outFile = null;
        var format = "markdown";
        var failOn = "never";
        var noRestore = false;
        var positional = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (arg)
            {
                case "-h" or "--help":
                    Console.WriteLine(Help);
                    return new ExitCommand(0);
                case "--no-restore": noRestore = true; break;
                case "--base":
                    baseRef = Next();
                    if (string.IsNullOrWhiteSpace(baseRef)) return Fail("--base needs a git commit, branch or tag.");
                    break;
                case "--format":
                    format = Next() ?? "";
                    if (format is not ("markdown" or "text" or "json")) return Fail("--format is markdown, text or json.");
                    break;
                case "--out":
                    outFile = Next();
                    if (outFile is null) return Fail("--out needs a file name.");
                    break;
                case "--fail-on":
                    failOn = Next() ?? "";
                    if (failOn is not ("error" or "warning" or "never")) return Fail("--fail-on is error, warning or never.");
                    break;
                default:
                    if (arg.StartsWith('-')) return Fail($"Unknown option {arg} for diff.");
                    positional.Add(arg);
                    break;
            }
        }

        var full = outFile is null ? null : Path.GetFullPath(outFile);
        if (positional.Count == 2 && positional.All(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            if (baseRef is not null) return Fail("Give either --base or two JSON files, not both.");
            foreach (var file in positional)
                if (!File.Exists(file)) return Fail($"{file} does not exist.");
            return new DiffCommand(null, null, Path.GetFullPath(positional[0]), Path.GetFullPath(positional[1]), format, full, failOn);
        }

        if (baseRef is null) return Fail("Say what to compare with: --base <git ref>, or two files written by \"sightline export --json\".");
        if (positional.Count > 1) return Fail($"Give one path only, not {string.Join(" and ", positional)}.");
        if (Resolve(positional.FirstOrDefault(), noRestore, out var target) is { } failed) return failed;
        return new DiffCommand(target, baseRef, null, null, format, full, failOn);
    }

    static CliCommand ParseMcp(string[] args)
    {
        string? path = null;
        var noRestore = false;
        foreach (var arg in args)
        {
            switch (arg)
            {
                case "-h" or "--help":
                    Console.Error.WriteLine(Help);
                    return new ExitCommand(0);
                case "--no-restore": noRestore = true; break;
                default:
                    if (arg.StartsWith('-')) return Fail($"Unknown option {arg} for mcp.");
                    if (path is not null) return Fail($"Give one path only, not both {path} and {arg}.");
                    path = arg;
                    break;
            }
        }

        // Clients often start servers from the user's home folder; then the solution has to be named.
        if (Resolve(path, noRestore, out var target) is { } failed) return failed;
        return new McpCommand(target);
    }

    /// Finds the solution and its settings: the path given, the nearest sightline.json, or the nearest solution.
    static ExitCommand? Resolve(string? path, bool noRestore, out SolutionTarget target)
    {
        target = null!;
        var fullPath = path is null ? null : Path.GetFullPath(path);
        var start = fullPath is null ? Directory.GetCurrentDirectory()
            : Directory.Exists(fullPath) ? fullPath
            : Path.GetDirectoryName(fullPath)!;

        SightlineConfig? config = null;
        if (SightlineConfig.Find(start) is { } file)
        {
            try
            {
                config = SightlineConfig.Load(file);
            }
            catch (ConfigException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return new ExitCommand(1);
            }
            foreach (var warning in config.Warnings) Console.Error.WriteLine(warning);
        }

        string? resolved;
        if (fullPath is not null)
        {
            resolved = ResolveTarget(fullPath);
            if (resolved is null) return Fail($"No .sln, .slnx or .csproj file found at {path}.");
        }
        else if (config?.Solution is { } solution)
        {
            if (!File.Exists(solution))
                return Fail($"{config.File}: \"solution\" is {Path.GetRelativePath(config.Dir, solution)}, which does not exist.");
            resolved = solution;
        }
        else
        {
            var here = Directory.GetCurrentDirectory();
            resolved = FindNearestSolution(here) ?? ResolveTarget(here);
            if (resolved is null)
                return Fail("No .sln, .slnx or .csproj file found here. Give the path to a solution, " +
                            "or run \"sightline init\" in the folder that holds it.");
        }

        target = new SolutionTarget(
            resolved,
            config,
            config is null ? ProjectFilter.None : new ProjectFilter(config.Dir, config.Exclude),
            !noRestore && (config?.Restore ?? true));
        return null;
    }

    /// The nearest solution in this folder or above it. The search stops at the top of the
    /// repository, or after a few folders when there is none, so it never wanders into unrelated code.
    internal static string? FindNearestSolution(string start)
    {
        var dir = new DirectoryInfo(start);
        for (var level = 0; dir is not null && level < 6; level++, dir = dir.Parent)
        {
            var solution = Directory.EnumerateFiles(dir.FullName, "*.sln")
                .Concat(Directory.EnumerateFiles(dir.FullName, "*.slnx"))
                .Order(StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (solution is not null) return solution;

            var git = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git)) return null;
        }
        return null;
    }

    static CliCommand ParseInit(string[] args)
    {
        string? folder = null, solution = null;
        bool? vscode = null;
        var pinTool = true;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;

            switch (arg)
            {
                case "-h" or "--help":
                    Console.WriteLine(Help);
                    return new ExitCommand(0);
                case "--solution":
                    solution = Next();
                    if (solution is null) return Fail("--solution needs a .sln, .slnx or .csproj file.");
                    break;
                case "--vscode":
                    vscode = true;
                    break;
                case "--no-vscode":
                    vscode = false;
                    break;
                case "--no-tool":
                    pinTool = false;
                    break;
                default:
                    if (arg.StartsWith('-')) return Fail($"Unknown option {arg} for init.");
                    if (folder is not null) return Fail($"Give one folder only, not both {folder} and {arg}.");
                    folder = arg;
                    break;
            }
        }

        folder = Path.GetFullPath(folder ?? Directory.GetCurrentDirectory());
        if (!Directory.Exists(folder)) return Fail($"The folder {folder} does not exist.");
        return new InitCommand(new InitOptions(folder, solution, vscode, pinTool));
    }

    static ExitCommand Fail(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine("Run \"sightline --help\" for usage.");
        return new ExitCommand(1);
    }

    /// A file is used as given. A folder resolves to its solution file, or stays a folder of projects.
    static string? ResolveTarget(string path)
    {
        if (File.Exists(path)) return path;
        if (!Directory.Exists(path)) return null;

        var solution = Directory.EnumerateFiles(path, "*.sln")
            .Concat(Directory.EnumerateFiles(path, "*.slnx"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (solution is not null) return solution;

        return Live.LiveSession.FindProjects(path).Any() ? path : null;
    }
}

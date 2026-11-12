using Co.Revington.Sightline.Settings;
using Co.Revington.Sightline.Setup;

namespace Co.Revington.Sightline;

internal abstract record CliCommand;
internal sealed record MapCommand(MapOptions Options) : CliCommand;
internal sealed record InitCommand(InitOptions Options) : CliCommand;
internal sealed record ExitCommand(int Code) : CliCommand;

/// What "sightline [path]" opens, and how. Command-line options win over sightline.json, which wins over defaults.
internal sealed record MapOptions(
    string Target, int Port, bool Open, bool Restore, string UiDir, string? Token, SightlineConfig? Config, ProjectFilter Filter);

internal static class Cli
{
    const int DefaultPort = 5210;

    static string Help => $$"""
        Sightline {{ToolInfo.Version}}: a live, editable map of a .NET solution

        Usage:
          sightline [path] [options]   Open the map
          sightline init [folder]      Set a solution up for everyone who works on it

        The path is a .sln, .slnx or .csproj file, or a folder. Without it, Sightline opens
        the solution named in the nearest sightline.json, or the one in the current folder.

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

        For working on Sightline itself:
          --ui <folder>       Serve the page from this folder
          --token <value>     Use this API token instead of a random one

        More at https://sightline.revington.co
        """;

    public static CliCommand Parse(string[] args) =>
        args.Length > 0 && args[0] == "init" ? ParseInit(args[1..]) : ParseMap(args);

    static CliCommand ParseMap(string[] args)
    {
        string? path = null, ui = null, token = null;
        int? port = null;
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
                default:
                    if (arg.StartsWith('-')) return Fail($"Unknown option {arg}.");
                    if (path is not null) return Fail($"Give one path only, not both {path} and {arg}.");
                    path = arg;
                    break;
            }
        }

        // Settings: the nearest sightline.json, looking from the path given or from the current folder.
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

        string? target;
        if (fullPath is not null)
        {
            target = ResolveTarget(fullPath);
            if (target is null) return Fail($"No .sln, .slnx or .csproj file found at {path}.");
        }
        else if (config?.Solution is { } solution)
        {
            if (!File.Exists(solution))
                return Fail($"{config.File}: \"solution\" is {Path.GetRelativePath(config.Dir, solution)}, which does not exist.");
            target = solution;
        }
        else
        {
            target = ResolveTarget(Directory.GetCurrentDirectory());
            if (target is null)
                return Fail("No .sln, .slnx or .csproj file found here. Give the path to a solution, " +
                            "or run \"sightline init\" in the folder that holds it.");
        }

        return new MapCommand(new MapOptions(
            Target: target,
            Port: port ?? config?.Port ?? DefaultPort,
            Open: !noOpen && (config?.OpenBrowser ?? true),
            Restore: !noRestore && (config?.Restore ?? true),
            UiDir: ui is null ? Path.Combine(AppContext.BaseDirectory, "wwwroot") : Path.GetFullPath(ui),
            Token: token,
            Config: config,
            Filter: config is null ? ProjectFilter.None : new ProjectFilter(config.Dir, config.Exclude)));
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

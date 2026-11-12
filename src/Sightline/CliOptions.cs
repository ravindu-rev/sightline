namespace Sightline;

internal sealed record CliOptions(string Target, int Port, bool Open, string UiDir, string? Token)
{
    const string Help = """
        Sightline - live, editable map of a .NET solution

        Usage: sightline [path] [options]

          path           .sln, .slnx or .csproj file, or a folder (default: current folder)

        Options:
          --port <n>     Port to listen on (default 5210; the next free port is used if busy)
          --no-open      Do not open the browser
          --ui <dir>     Serve UI files from this folder (for UI development)
          --token <t>    Fixed API token instead of a random one (for scripts and tests)
          -h, --help     Show this help
        """;

    public static CliOptions? Parse(string[] args)
    {
        string? path = null, ui = null, token = null;
        var port = 5210;
        var open = true;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;

            switch (arg)
            {
                case "-h" or "--help":
                    Console.WriteLine(Help);
                    return null;
                case "--no-open":
                    open = false;
                    break;
                case "--port":
                    if (!int.TryParse(Next(), out port)) return Fail("--port needs a number");
                    break;
                case "--ui":
                    ui = Next();
                    if (ui is null) return Fail("--ui needs a folder");
                    break;
                case "--token":
                    token = Next();
                    if (token is null) return Fail("--token needs a value");
                    break;
                default:
                    if (arg.StartsWith('-')) return Fail($"Unknown option {arg}");
                    path = arg;
                    break;
            }
        }

        var target = ResolveTarget(Path.GetFullPath(path ?? Directory.GetCurrentDirectory()));
        if (target is null) return Fail($"No .sln, .slnx or .csproj found at {path ?? Directory.GetCurrentDirectory()}");

        ui = ui is null ? Path.Combine(AppContext.BaseDirectory, "wwwroot") : Path.GetFullPath(ui);
        return new CliOptions(target, port, open, ui, token);
    }

    static CliOptions? Fail(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine("Run 'sightline --help' for usage.");
        return null;
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

        var hasProjects = Directory.EnumerateFiles(path, "*.csproj", SearchOption.AllDirectories).Any();
        return hasProjects ? path : null;
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using Co.Revington.Sightline.Settings;

namespace Co.Revington.Sightline.Setup;

internal sealed record InitOptions(string Folder, string? Solution, bool? VsCode, bool PinTool);

/// "sightline init" sets Sightline up for one solution, for everyone who works on it:
///   1. pins this version of the tool in the repository's tool manifest (dotnet-tools.json)
///   2. writes sightline.json, which names the solution to open
///   3. adds a VS Code task that opens the map, when the folder uses VS Code
/// Running it again leaves alone whatever is already set up.
internal static class Init
{
    const string TaskLabel = "Sightline: open the map";

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", ".vs", ".idea", "node_modules" };

    public static async Task<int> RunAsync(InitOptions options)
    {
        var folder = Path.GetFullPath(options.Folder);

        string? solution;
        if (options.Solution is not null)
        {
            solution = Path.GetFullPath(options.Solution);
            if (!File.Exists(solution))
            {
                Console.Error.WriteLine($"{options.Solution} does not exist.");
                return 1;
            }
        }
        else
        {
            solution = FindSolution(folder);
            if (solution is null) return 1;
        }

        Console.WriteLine($"Setting up Sightline for {Relative(folder, solution)}");
        var ok = true;
        var commit = new List<string>();

        if (options.PinTool)
        {
            var manifest = await PinToolAsync(folder);
            if (manifest is null) ok = false;
            else commit.Add(Relative(folder, manifest));
        }

        WriteSettings(folder, solution);
        commit.Add(SightlineConfig.FileName);

        if (options.VsCode ?? Directory.Exists(Path.Combine(folder, ".vscode")))
        {
            if (AddVsCodeTask(folder)) commit.Add(".vscode/tasks.json");
        }
        else
        {
            Report("skipped", "VS Code task: there is no .vscode folder here. Add the task anyway with --vscode.");
        }

        NoteIfNotRepositoryRoot(folder);

        Console.WriteLine();
        Console.WriteLine("Commit these files so everyone gets the same setup:");
        foreach (var file in commit) Console.WriteLine($"  {file}");
        Console.WriteLine();
        Console.WriteLine("Then anyone can open the map with:");
        if (options.PinTool)
        {
            Console.WriteLine("  dotnet tool restore");
            Console.WriteLine("  dotnet sightline");
        }
        else
        {
            Console.WriteLine("  sightline");
        }
        return ok ? 0 : 1;
    }

    static void Report(string what, string text) => Console.WriteLine($"  {what,-9} {text}");

    static string Relative(string from, string path) => Path.GetRelativePath(from, path).Replace('\\', '/');

    // ---------------------------------------------------------------- solution

    /// The solution in this folder. Failing that, the only solution in the folders below it,
    /// or the only project in this folder. When there are several, the user picks one.
    static string? FindSolution(string folder)
    {
        string[] solutions = [".sln", ".slnx"];
        var found = Search(folder, solutions, depth: 0);
        if (found.Count == 0) found = Search(folder, solutions, depth: 3);
        if (found.Count == 0) found = Search(folder, [".csproj"], depth: 0);

        if (found.Count == 1) return found[0];
        if (found.Count > 1) return Choose(folder, found);

        Console.Error.WriteLine($"No .sln, .slnx or .csproj file was found in {folder} or the folders below it.");
        Console.Error.WriteLine("Run init in the folder that holds your solution, or name it with --solution <file>.");
        return null;
    }

    static List<string> Search(string root, string[] extensions, int depth)
    {
        var found = new List<string>();
        var level = new List<string> { root };
        for (var d = 0; d <= depth && level.Count > 0; d++)
        {
            var next = new List<string>();
            foreach (var dir in level)
            {
                try
                {
                    found.AddRange(Directory.EnumerateFiles(dir).Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)));
                    next.AddRange(Directory.EnumerateDirectories(dir).Where(sub => !SkipDirs.Contains(Path.GetFileName(sub))));
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    // a folder we may not read: skip it
                }
            }
            level = next;
        }
        return found.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    static string? Choose(string folder, List<string> found)
    {
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("Several solutions were found. Pick one with --solution <file>:");
            foreach (var file in found) Console.Error.WriteLine($"  {Relative(folder, file)}");
            return null;
        }

        Console.WriteLine("Several solutions were found. Which one should Sightline open?");
        for (var i = 0; i < found.Count; i++) Console.WriteLine($"  {i + 1}. {Relative(folder, found[i])}");
        Console.Write("Number: ");
        if (int.TryParse(Console.ReadLine(), out var n) && n >= 1 && n <= found.Count) return found[n - 1];

        Console.Error.WriteLine("No solution was picked. Run init again, or name one with --solution <file>.");
        return null;
    }

    // ---------------------------------------------------------------- tool manifest

    /// Pins this version in the tool manifest that applies to the folder, creating one if needed.
    /// A newer version that is already pinned stays. Returns the manifest's path, or null on failure.
    static async Task<string?> PinToolAsync(string folder)
    {
        var version = ToolInfo.Version;
        var (pinned, manifest) = await LocalToolAsync(folder);

        if (pinned is not null && CompareVersions(pinned, version) >= 0)
        {
            Report("kept", $"{ToolInfo.PackageId} {pinned} in {Relative(folder, manifest!)}");
            return manifest;
        }

        string[] args = pinned is null
            ? ["tool", "install", ToolInfo.PackageId, "--local", "--version", version, "--create-manifest-if-needed"]
            : ["tool", "update", ToolInfo.PackageId, "--local", "--version", version];
        var result = await DotnetCli.RunAsync(folder, args);

        if (result.Code != 0)
        {
            Report("failed", $"Could not pin {ToolInfo.PackageId} {version} in a tool manifest:");
            foreach (var line in DotnetCli.Tail(result.All).Split('\n')) Console.WriteLine($"            {line}");
            Console.WriteLine($"            To do it yourself: dotnet tool install {ToolInfo.PackageId}");
            return null;
        }

        (_, manifest) = await LocalToolAsync(folder);
        manifest ??= Path.Combine(folder, "dotnet-tools.json");
        Report(pinned is null ? "pinned" : "updated",
            $"{ToolInfo.PackageId} {(pinned is null ? "" : pinned + " to ")}{version} in {Relative(folder, manifest)}");
        return manifest;
    }

    /// The version pinned in the manifest that applies to this folder, and that manifest's path.
    static async Task<(string? Version, string? Manifest)> LocalToolAsync(string folder)
    {
        var result = await DotnetCli.RunAsync(folder, ["tool", "list", "--local", "--format", "json"]);
        if (result.Code != 0 || !result.Output.Contains('{')) return (null, null);

        try
        {
            using var doc = JsonDocument.Parse(result.Output[result.Output.IndexOf('{')..]);
            string? anyManifest = null;
            foreach (var tool in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                var manifest = tool.GetProperty("manifest").GetString();
                anyManifest ??= manifest;
                if (string.Equals(tool.GetProperty("packageId").GetString(), ToolInfo.PackageId, StringComparison.OrdinalIgnoreCase))
                    return (tool.GetProperty("version").GetString(), manifest);
            }
            return (null, anyManifest);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return (null, null);
        }
    }

    static int CompareVersions(string a, string b)
    {
        static Version Parse(string v) => Version.TryParse(v.Split('-', '+')[0], out var parsed) ? parsed : new Version(0, 0);
        return Parse(a).CompareTo(Parse(b));
    }

    // ---------------------------------------------------------------- sightline.json

    static void WriteSettings(string folder, string solution)
    {
        var file = Path.Combine(folder, SightlineConfig.FileName);

        if (File.Exists(file))
        {
            // The team's settings stay as they are; only say whether they still work.
            try
            {
                var config = SightlineConfig.Load(file);
                Report("kept", SightlineConfig.FileName);
                foreach (var warning in config.Warnings) Report("", warning);
                if (config.Solution is not null && !File.Exists(config.Solution))
                    Report("", $"It names a solution that does not exist: {Relative(folder, config.Solution)}");
            }
            catch (ConfigException ex)
            {
                Report("kept", $"{SightlineConfig.FileName}, but it has a problem: {ex.Message}");
            }
            return;
        }

        var settings = new JsonObject
        {
            ["$schema"] = SightlineConfig.SchemaUrl,
            ["solution"] = Relative(folder, solution),
            ["exclude"] = new JsonArray(),
        };
        File.WriteAllText(file, settings.ToJsonString(Indented) + Environment.NewLine);
        Report("created", $"{SightlineConfig.FileName} (opens {Relative(folder, solution)})");
    }

    // ---------------------------------------------------------------- VS Code

    /// Adds a task that opens the map. Returns whether .vscode/tasks.json now has one.
    static bool AddVsCodeTask(string folder)
    {
        var dir = Path.Combine(folder, ".vscode");
        var file = Path.Combine(dir, "tasks.json");
        var task = new JsonObject
        {
            ["label"] = TaskLabel,
            ["detail"] = "Open the live map of this solution",
            ["type"] = "process",
            ["command"] = "dotnet",
            ["args"] = new JsonArray("sightline"),
            ["isBackground"] = true,
            ["problemMatcher"] = new JsonArray(),
            ["presentation"] = new JsonObject { ["reveal"] = "always", ["panel"] = "dedicated" },
        };

        if (!File.Exists(file))
        {
            Directory.CreateDirectory(dir);
            var tasks = new JsonObject { ["version"] = "2.0.0", ["tasks"] = new JsonArray(task) };
            File.WriteAllText(file, tasks.ToJsonString(Indented) + Environment.NewLine);
            Report("added", $"the task \"{TaskLabel}\" to .vscode/tasks.json");
            return true;
        }

        var text = File.ReadAllText(file);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);          // strict: no comments, no trailing commas
        }
        catch (JsonException)
        {
            // tasks.json often has comments. Writing it back would drop them, so it stays as it is.
            if (HasSightlineTask(text)) return Kept();
            Report("skipped", ".vscode/tasks.json has comments, so it was left as it is. To add the task, put this in its \"tasks\" list:");
            foreach (var line in task.ToJsonString(Indented).Split('\n')) Console.WriteLine($"            {line.TrimEnd()}");
            return false;
        }

        if (root is not JsonObject obj || obj["tasks"] is not (JsonArray or null))
        {
            Report("skipped", ".vscode/tasks.json does not look like a VS Code tasks file, so it was left as it is.");
            return false;
        }

        var list = obj["tasks"] as JsonArray;
        if (list is not null && list.Any(IsSightlineTask)) return Kept();

        if (list is null) obj["tasks"] = list = new JsonArray();
        obj["version"] ??= "2.0.0";
        list.Add(task);
        File.WriteAllText(file, obj.ToJsonString(Indented) + Environment.NewLine);
        Report("added", $"the task \"{TaskLabel}\" to .vscode/tasks.json");
        return true;

        static bool Kept()
        {
            Report("kept", "the Sightline task in .vscode/tasks.json");
            return true;
        }
    }

    static bool HasSightlineTask(string text)
    {
        try
        {
            return JsonNode.Parse(text, documentOptions: Lenient)?["tasks"] is JsonArray tasks && tasks.Any(IsSightlineTask);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    static bool IsSightlineTask(JsonNode? task) =>
        task?["label"] is JsonValue label && label.TryGetValue<string>(out var text)
        && text.StartsWith("Sightline", StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- notes

    /// sightline.json is found from the folder it is in and the folders inside it,
    /// so it belongs at the root of the repository.
    static void NoteIfNotRepositoryRoot(string folder)
    {
        for (var dir = Directory.GetParent(folder); dir is not null; dir = dir.Parent)
        {
            var git = Path.Combine(dir.FullName, ".git");
            if (!Directory.Exists(git) && !File.Exists(git)) continue;

            Report("note", $"The repository starts at {dir.FullName}. \"dotnet sightline\" finds these settings " +
                           $"from {Relative(dir.FullName, folder)} and the folders inside it, not from the top.");
            return;
        }
    }
}

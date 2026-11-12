using System.Text.Json;

namespace Co.Revington.Sightline.Settings;

/// Settings from sightline.json. The file lives in the repository, so everyone who runs
/// "dotnet sightline" there opens the same solution with the same settings.
internal sealed class SightlineConfig
{
    public const string FileName = "sightline.json";
    public const string SchemaUrl = "https://sightline.revington.co/sightline.schema.json";

    static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    static readonly string[] Known = ["$schema", "solution", "exclude", "port", "openBrowser", "restore"];

    public required string File { get; init; }
    public string Dir => Path.GetDirectoryName(File)!;

    /// Full path of the solution to open, or null when the file does not name one.
    public string? Solution { get; init; }
    public IReadOnlyList<string> Exclude { get; init; } = [];
    public int? Port { get; init; }
    public bool? OpenBrowser { get; init; }
    public bool? Restore { get; init; }

    /// Settings that were ignored, worded for the console.
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// The nearest sightline.json in this folder or a folder above it.
    public static string? Find(string folder)
    {
        for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, FileName);
            if (System.IO.File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// Reads a settings file. A value that would change what gets opened must be right, so a wrong
    /// value is an error. An unknown setting is only a warning.
    public static SightlineConfig Load(string file)
    {
        file = Path.GetFullPath(file);
        var dir = Path.GetDirectoryName(file)!;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(System.IO.File.ReadAllText(file), Lenient);
        }
        catch (JsonException ex)
        {
            throw new ConfigException($"{FileName} is not valid JSON: {ex.Message}");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new ConfigException($"{FileName} must hold a JSON object, such as {{ \"solution\": \"App.sln\" }}.");

            string? solution = null;
            int? port = null;
            bool? open = null, restore = null;
            var exclude = new List<string>();
            var warnings = new List<string>();

            foreach (var setting in doc.RootElement.EnumerateObject())
            {
                var value = setting.Value;
                switch (Known.FirstOrDefault(k => string.Equals(k, setting.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    case "$schema":
                        break;

                    case "solution":
                        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                            throw Wrong("solution", "the path of a .sln, .slnx or .csproj file, relative to this file");
                        solution = Path.GetFullPath(Path.Combine(dir, value.GetString()!));
                        break;

                    case "exclude":
                        if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(e.GetString())))
                            throw Wrong("exclude", "a list of project names or paths, such as [\"*.Tests\", \"samples/**\"]");
                        exclude.AddRange(value.EnumerateArray().Select(e => e.GetString()!.Trim()));
                        break;

                    case "port":
                        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number is < 1 or > 65535)
                            throw Wrong("port", "a whole number from 1 to 65535");
                        port = number;
                        break;

                    case "openBrowser":
                        open = Boolean(value, "openBrowser");
                        break;

                    case "restore":
                        restore = Boolean(value, "restore");
                        break;

                    default:
                        warnings.Add($"{FileName}: \"{setting.Name}\" is not a Sightline setting, so it is ignored. " +
                                     $"Settings are: {string.Join(", ", Known.Skip(1))}.");
                        break;
                }
            }

            return new SightlineConfig
            {
                File = file, Solution = solution, Exclude = exclude, Port = port,
                OpenBrowser = open, Restore = restore, Warnings = warnings,
            };
        }
    }

    static bool Boolean(JsonElement value, string setting) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw Wrong(setting, "true or false"),
    };

    static ConfigException Wrong(string setting, string expected) => new($"{FileName}: \"{setting}\" must be {expected}.");
}

internal sealed class ConfigException(string message) : Exception(message);

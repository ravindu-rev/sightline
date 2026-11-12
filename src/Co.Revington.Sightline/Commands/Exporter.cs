using System.Text.Json;
using System.Text.Json.Nodes;
using Co.Revington.Sightline.Analysis;
using Co.Revington.Sightline.Live;

namespace Co.Revington.Sightline.Commands;

/// "sightline export": the map as a static website, or as JSON for "sightline diff".
/// Nothing from this machine goes into the output: paths become relative to the repository,
/// and load warnings (which name local folders) are left out.
internal static class Exporter
{
    public static async Task<int> RunAsync(ExportCommand command)
    {
        LiveSession session;
        Snapshot snapshot;
        try
        {
            (session, snapshot) = await OneShot.LoadAsync(command.Target);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Sightline could not load the solution: " + ex.Message);
            return 2;
        }

        var scrubber = new PathScrubber(OneShot.RepoRoot(session.RootDir));
        var graph = GraphJson(snapshot, session, scrubber);

        if (command.Json is { } jsonFile)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(jsonFile)!);
            await File.WriteAllTextAsync(jsonFile, graph.ToJsonString());
            Console.Error.WriteLine($"Map data written to {jsonFile}");
        }

        if (command.Out is { } outDir)
        {
            var sources = command.Sources ? await Sources(snapshot, session, scrubber) : null;
            var title = command.Title ?? Path.GetFileNameWithoutExtension(session.Name);
            try
            {
                WriteSite(outDir, graph, sources, command.SourceUrl, title, Tours.Load(command.Target.Config?.Dir ?? session.RootDir, scrubber));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Console.Error.WriteLine("Sightline could not write the website: " + ex.Message);
                return 2;
            }
            Console.Error.WriteLine($"Website written to {outDir}. Host the folder on any static web server, such as GitHub Pages.");
        }
        return 0;
    }

    /// The graph as the page receives it, with paths made relative and machine details removed.
    public static JsonNode GraphJson(Snapshot snapshot, LiveSession session, PathScrubber scrubber)
    {
        var status = JsonSerializer.Serialize(new { state = "ready", message = "Exported map" }, Snapshot.Json);
        var node = JsonNode.Parse(snapshot.ToJson(status))!;
        if (node["info"] is JsonObject info)
        {
            info["diagnostics"] = new JsonArray();         // load warnings name folders on this machine
            info["root"] = ".";
        }
        return scrubber.Scrub(node)!;
    }

    static async Task<Dictionary<string, string>> Sources(Snapshot snapshot, LiveSession session, PathScrubber scrubber)
    {
        var files = snapshot.Graph!.Nodes.Values
            .Where(n => n.File is not null && n.Kind != "project")
            .Select(n => n.File!)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files)
            if (await session.ReadSourceAsync(file) is { } source) sources[scrubber.Relative(file)] = source.Text;
        return sources;
    }

    static void WriteSite(string outDir, JsonNode graph, Dictionary<string, string>? sources, string? sourceUrl, string title, JsonArray tours)
    {
        var ui = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (!File.Exists(Path.Combine(ui, "index.html"))) throw new InvalidOperationException("Sightline's page files are missing: " + ui);

        if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        CopyFolder(ui, outDir);
        File.Copy(Path.Combine(outDir, "js", "static-api.js"), Path.Combine(outDir, "js", "api.js"), overwrite: true);

        var data = new JsonObject
        {
            ["title"] = title,
            ["version"] = ToolInfo.Version,
            ["exported"] = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            ["graph"] = graph,
            ["sources"] = sources is null ? null : new JsonObject(sources.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))),
            ["sourceUrl"] = sourceUrl,
            ["tours"] = tours,
        };
        File.WriteAllText(Path.Combine(outDir, "data.json"), data.ToJsonString());

        // The tool serves the page from "/" with a token; a static copy lives anywhere and has neither.
        var connect = sourceUrl is null ? "'self'" : $"'self' {new Uri(sourceUrl.Replace("{path}", "x")).GetLeftPart(UriPartial.Authority)}";
        var csp = $"default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src {connect}; object-src 'none'; base-uri 'none'";
        var page = Path.Combine(outDir, "index.html");
        var html = File.ReadAllText(page)
            .Replace("href=\"/", "href=\"./")
            .Replace("src=\"/", "src=\"./")
            .Replace("__SIGHTLINE_TOKEN__", "export")
            .Replace("<title>Sightline</title>", $"<title>{System.Net.WebUtility.HtmlEncode(title)}: Sightline map</title>")
            .Replace("<meta charset=\"utf-8\">", $"<meta charset=\"utf-8\">\n  <meta http-equiv=\"Content-Security-Policy\" content=\"{csp}\">");
        File.WriteAllText(page, html);
    }

    static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}

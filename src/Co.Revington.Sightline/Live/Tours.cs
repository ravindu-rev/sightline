using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Co.Revington.Sightline.Commands;

namespace Co.Revington.Sightline.Live;

/// Guided tours through the map, kept in the repository as .sightline/tours/<name>.json so the
/// whole team gets them. A tour is { "title": "...", "steps": [ { "view", "id", "label", "text" } ] }.
internal static class Tours
{
    public const string Folder = ".sightline/tours";

    /// Where tours live: next to sightline.json when there is one, else in the solution folder.
    public static string Dir(string root) => Path.Combine(root, ".sightline", "tours");

    public static JsonArray Load(string root, PathScrubber? scrubber = null)
    {
        var list = new JsonArray();
        var dir = Dir(root);
        if (!Directory.Exists(dir)) return list;

        foreach (var file in Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject tour || tour["steps"] is not JsonArray) continue;
                tour["id"] = Path.GetFileNameWithoutExtension(file);
                list.Add(scrubber is null ? tour : scrubber.Scrub(tour));
            }
            catch (JsonException)
            {
                // a half-written file; it shows up once it is valid again
            }
        }
        return list;
    }

    /// Writes a tour and returns its id (the file name). The name is made from the title, so a
    /// request can never write outside the tours folder.
    public static string Save(string root, JsonObject tour, string? id)
    {
        var title = tour["title"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(title)) throw new ArgumentException("A tour needs a title.");
        if (tour["steps"] is not JsonArray { Count: > 0 } steps) throw new ArgumentException("A tour needs at least one step.");

        var clean = new JsonObject
        {
            ["title"] = title,
            ["steps"] = new JsonArray(steps.OfType<JsonObject>().Select(s => (JsonNode)new JsonObject
            {
                ["view"] = Text(s, "view") is "map" or "flow" or "focus" ? Text(s, "view") : "map",
                ["id"] = Text(s, "id"),
                ["label"] = Text(s, "label"),
                ["dir"] = Text(s, "dir") is "up" ? "up" : null,
                ["text"] = Text(s, "text") ?? "",
            }).ToArray()),
        };

        var name = id is not null && Slug(id) == id ? id : Slug(title);
        var dir = Dir(root);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".json"), clean.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        return name;
    }

    public static bool Delete(string root, string id)
    {
        if (Slug(id) != id) return false;
        var file = Path.Combine(Dir(root), id + ".json");
        if (!File.Exists(file)) return false;
        File.Delete(file);
        return true;
    }

    static string? Text(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length <= 4000 ? s : null;

    /// "How an order is placed" -> "how-an-order-is-placed"
    public static string Slug(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
            if (sb.Length >= 60) break;
        }
        var slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "tour" : slug;
    }
}

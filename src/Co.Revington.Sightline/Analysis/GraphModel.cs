using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Co.Revington.Sightline.Analysis;

internal sealed class Node
{
    public required string Id { get; init; }
    public required string Kind { get; init; }       // project | package | type | member | endpoint | external
    public required string Name { get; init; }
    public string? Parent { get; init; }
    public string? Project { get; init; }
    public string? Ns { get; init; }
    public string? File { get; init; }
    public Dictionary<string, object> Meta { get; init; } = [];
    public int? Line { get; set; }
    public int? EndLine { get; set; }
}

internal sealed class Edge
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Source { get; init; }
    public required string Target { get; init; }
    public int Count { get; set; } = 1;
    public string? Label { get; set; }
    public string? File { get; init; }
    public int? Line { get; set; }

    [JsonIgnore] public SortedSet<string>? Labels { get; set; }
}

internal sealed record ServiceEntry(
    string Id, string? ServiceId, string Service, string? ImplId, string? Impl,
    string Lifetime, string Method, string? RegisteredIn, string File, int Line);

internal sealed class Graph
{
    public Dictionary<string, Node> Nodes { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Edge> Edges { get; } = new(StringComparer.Ordinal);
    public List<ServiceEntry> Services { get; } = [];
    public List<Problem> Problems { get; set; } = [];
    public List<CoverageNote> Coverage { get; set; } = [];

    public bool Has(string id) => Nodes.ContainsKey(id);

    public void Add(Node node) => Nodes.TryAdd(node.Id, node);

    /// Adds an edge, or bumps the count when the same link already exists.
    public void Link(string kind, string source, string target, string? label = null, string? file = null, int? line = null)
    {
        if (source == target) return;
        var id = $"{kind}|{source}|{target}";
        if (!Edges.TryGetValue(id, out var edge))
            Edges[id] = edge = new Edge { Id = id, Kind = kind, Source = source, Target = target, File = file, Line = line, Count = 0 };
        edge.Count++;
        if (label is not null) (edge.Labels ??= []).Add(label);
    }
}

/// Immutable, pre-serialized view of the graph. Cheap to send and to diff.
internal sealed class Snapshot
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// Json = full item. Shape = same item without line numbers, so pure line shifts are not "changes".
    public readonly record struct Item(string Json, string Shape);

    public static readonly Snapshot Empty = new()
    {
        Version = 0, Nodes = [], Edges = [], ServicesJson = "[]", InfoJson = "{}", ProblemsJson = "[]", CoverageJson = "[]",
    };

    public required int Version { get; init; }
    public required Dictionary<string, Item> Nodes { get; init; }
    public required Dictionary<string, Item> Edges { get; init; }
    public required string ServicesJson { get; init; }
    public required string InfoJson { get; init; }
    public required string ProblemsJson { get; init; }
    public required string CoverageJson { get; init; }

    /// The graph this snapshot was made from, for code that needs more than JSON (checks, export, MCP).
    public Graph? Graph { get; init; }

    public static Snapshot From(Graph g, int version, object info)
    {
        var nodes = new Dictionary<string, Item>(g.Nodes.Count, StringComparer.Ordinal);
        foreach (var n in g.Nodes.Values)
        {
            var (line, end) = (n.Line, n.EndLine);
            n.Line = n.EndLine = null;
            var shape = JsonSerializer.Serialize(n, Json);
            (n.Line, n.EndLine) = (line, end);
            nodes[n.Id] = new Item(WithLines(shape, line, end), shape);
        }

        var edges = new Dictionary<string, Item>(g.Edges.Count, StringComparer.Ordinal);
        foreach (var e in g.Edges.Values)
        {
            e.Label = e.Labels is null ? null : string.Join(", ", e.Labels.Take(6));
            var line = e.Line;
            e.Line = null;
            var shape = JsonSerializer.Serialize(e, Json);
            e.Line = line;
            edges[e.Id] = new Item(WithLines(shape, line, null), shape);
        }

        return new Snapshot
        {
            Version = version,
            Nodes = nodes,
            Edges = edges,
            ServicesJson = JsonSerializer.Serialize(g.Services, Json),
            InfoJson = JsonSerializer.Serialize(info, Json),
            ProblemsJson = JsonSerializer.Serialize(g.Problems, Json),
            CoverageJson = JsonSerializer.Serialize(g.Coverage, Json),
            Graph = g,
        };
    }

    static string WithLines(string shape, int? line, int? end)
    {
        if (line is null) return shape;
        var tail = end is null ? $",\"line\":{line}}}" : $",\"line\":{line},\"endLine\":{end}}}";
        return string.Concat(shape.AsSpan(0, shape.Length - 1), tail);
    }

    public string ToJson(string statusJson)
    {
        var sb = new StringBuilder(1 << 16);
        sb.Append("{\"version\":").Append(Version)
          .Append(",\"status\":").Append(statusJson)
          .Append(",\"info\":").Append(InfoJson)
          .Append(",\"nodes\":[").AppendJoin(',', Nodes.Values.Select(i => i.Json))
          .Append("],\"edges\":[").AppendJoin(',', Edges.Values.Select(i => i.Json))
          .Append("],\"services\":").Append(ServicesJson)
          .Append(",\"problems\":").Append(ProblemsJson)
          .Append(",\"coverage\":").Append(CoverageJson).Append('}');
        return sb.ToString();
    }

    /// JSON patch from one snapshot to the next, or null when nothing changed.
    public static string? Diff(Snapshot from, Snapshot to, string reason, IEnumerable<string> files, long ms)
    {
        var sb = new StringBuilder();
        sb.Append("{\"version\":").Append(to.Version)
          .Append(",\"base\":").Append(from.Version)
          .Append(",\"reason\":\"").Append(reason)
          .Append("\",\"ms\":").Append(ms)
          .Append(",\"files\":").Append(JsonSerializer.Serialize(files, Json));

        var changed = Section(sb, "nodes", from.Nodes, to.Nodes);
        changed |= Section(sb, "edges", from.Edges, to.Edges);

        if (from.ServicesJson != to.ServicesJson)
        {
            sb.Append(",\"services\":").Append(to.ServicesJson);
            changed = true;
        }
        if (from.InfoJson != to.InfoJson)
        {
            sb.Append(",\"info\":").Append(to.InfoJson);
            changed = true;
        }
        if (from.ProblemsJson != to.ProblemsJson)
        {
            sb.Append(",\"problems\":").Append(to.ProblemsJson);
            changed = true;
        }
        if (from.CoverageJson != to.CoverageJson)
        {
            sb.Append(",\"coverage\":").Append(to.CoverageJson);
            changed = true;
        }

        sb.Append('}');
        return changed ? sb.ToString() : null;
    }

    static bool Section(StringBuilder sb, string name, Dictionary<string, Item> from, Dictionary<string, Item> to)
    {
        List<string> added = [], changed = [], moved = [], removed = [];

        foreach (var (id, item) in to)
        {
            if (!from.TryGetValue(id, out var old)) added.Add(item.Json);
            else if (old.Shape != item.Shape) changed.Add(item.Json);
            else if (old.Json != item.Json) moved.Add(item.Json);
        }
        foreach (var id in from.Keys)
            if (!to.ContainsKey(id)) removed.Add(id);

        sb.Append(",\"").Append(name).Append("\":{\"added\":[").AppendJoin(',', added)
          .Append("],\"changed\":[").AppendJoin(',', changed)
          .Append("],\"moved\":[").AppendJoin(',', moved)
          .Append("],\"removed\":").Append(JsonSerializer.Serialize(removed, Json)).Append('}');

        return added.Count + changed.Count + moved.Count + removed.Count > 0;
    }
}

using System.Text;
using System.Text.Json.Nodes;
using Co.Revington.Sightline.Analysis;
using Co.Revington.Sightline.Commands;
using Co.Revington.Sightline.Live;

namespace Co.Revington.Sightline.Mcp;

/// The tools an AI assistant can call. Each answers in plain text that reads well to a model:
/// names it can search for, and file:line it can open.
internal sealed class McpTools(LiveSession session, PathScrubber paths)
{
    static readonly HashSet<string> FlowKinds = ["handles", "calls", "dispatches", "db", "http"];
    static readonly HashSet<string> CallerKinds = ["handles", "calls", "dispatches"];
    static readonly HashSet<string> UseKinds = ["calls", "injects", "references", "inherits", "implements", "creates", "dispatches", "handles"];

    static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] properties)
    {
        var props = new JsonObject();
        foreach (var p in properties) props[p.Name] = new JsonObject { ["type"] = p.Type, ["description"] = p.Description };
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = new JsonArray(properties.Where(p => p.Required).Select(p => (JsonNode)p.Name).ToArray()),
        };
    }

    static JsonObject Tool(string name, string title, string description, JsonObject schema) => new()
    {
        ["name"] = name,
        ["title"] = title,
        ["description"] = description,
        ["inputSchema"] = schema,
        ["annotations"] = new JsonObject { ["readOnlyHint"] = true, ["openWorldHint"] = false },
    };

    public static JsonArray Definitions() =>
    [
        Tool("solution_overview", "Solution overview",
            "Projects with their kind, target framework and references; counts of types, endpoints and services; problems found; " +
            "and what the analysis could not follow. Call this first.", Schema()),
        Tool("find_symbol", "Find a class, method or endpoint",
            "Finds types, methods, properties, HTTP endpoints, projects and packages by name. Returns ids and file:line.",
            Schema(("query", "string", "Part of a name, such as OrderService, PlaceOrder or /api/orders.", true),
                ("kind", "string", "Only this kind: type, member, endpoint, project or package.", false))),
        Tool("list_endpoints", "List HTTP endpoints",
            "Every HTTP endpoint (controllers, minimal APIs, SignalR hubs, gRPC) with its route, handler and file:line.",
            Schema(("filter", "string", "Only routes or handlers containing this text.", false))),
        Tool("trace_flow", "Trace a request or method",
            "Steps behind an endpoint or method in source order: handlers, calls, the class that implements each interface " +
            "call (as registered in dependency injection), database access and outbound HTTP. With direction \"up\": who calls it.",
            Schema(("target", "string", "An endpoint (\"POST /api/orders\"), a method (\"OrderService.Place\") or an id from find_symbol.", true),
                ("direction", "string", "\"down\" (what it calls, default) or \"up\" (who calls it).", false),
                ("depth", "integer", "How many steps deep, 1 to 12. Default 6.", false))),
        Tool("describe_type", "Describe a type",
            "A class, interface, record or struct: where it is, its members, how it is registered in dependency injection, " +
            "what it depends on and what uses it.",
            Schema(("name", "string", "The type name, such as OrderRepository, or an id from find_symbol.", true))),
        Tool("list_services", "List dependency injection registrations",
            "Every registration found: service, implementation, lifetime and where it is registered.",
            Schema(("filter", "string", "Only services or implementations containing this text.", false))),
        Tool("list_problems", "List problems",
            "Problems in the dependency injection setup and the layer rules: singletons holding scoped services, services that " +
            "are never registered, circular dependencies, unused registrations and layer violations. Also lists what the analysis " +
            "could not follow, such as assembly scanning.", Schema()),
        Tool("who_depends_on", "Find what depends on something",
            "Impact of a change: the types and endpoints that use a type or method, directly and through others, nearest first.",
            Schema(("target", "string", "A type, method or id from find_symbol.", true),
                ("depth", "integer", "How many steps out, 1 to 4. Default 2.", false))),
    ];

    public string Call(string name, JsonObject args, Graph g)
    {
        var q = new GraphQuery(g);
        return name switch
        {
            "solution_overview" => Overview(q),
            "find_symbol" => Find(q, Arg(args, "query", required: true)!, Arg(args, "kind")),
            "list_endpoints" => Endpoints(q, Arg(args, "filter")),
            "trace_flow" => Flow(q, Resolve(q, Arg(args, "target", required: true)!), Arg(args, "direction") == "up", Number(args, "depth", 6, 1, 12)),
            "describe_type" => Describe(q, Resolve(q, Arg(args, "name", required: true)!)),
            "list_services" => Services(g, Arg(args, "filter")),
            "list_problems" => Problems(g),
            "who_depends_on" => Users(q, Resolve(q, Arg(args, "target", required: true)!), Number(args, "depth", 2, 1, 4)),
            _ => throw new ToolException($"There is no tool named {name}."),
        };
    }

    static string? Arg(JsonObject args, string key, bool required = false)
    {
        var value = args[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0 ? s.Trim() : null;
        if (value is null && required) throw new ToolException($"\"{key}\" is required.");
        return value;
    }

    static int Number(JsonObject args, string key, int fallback, int min, int max) =>
        args[key] is JsonValue v && v.TryGetValue<int>(out var n) ? Math.Clamp(n, min, max) : fallback;

    string Where(Node n) => n.File is null ? "" : $"{paths.Relative(n.File)}{(n.Line is > 0 ? ":" + n.Line : "")}";

    Node Resolve(GraphQuery q, string target)
    {
        var found = q.Resolve(target);
        if (found.Count == 1) return found[0];
        if (found.Count == 0) throw new ToolException($"Nothing called \"{target}\" was found. Try find_symbol with part of the name.");
        throw new ToolException($"\"{target}\" matches several things; name one of these ids:\n" +
            string.Join('\n', found.Take(12).Select(n => $"- {n.Id} ({n.Kind} {q.Label(n.Id)})")));
    }

    // ---------------------------------------------------------------- tools

    string Overview(GraphQuery q)
    {
        var g = q.Graph;
        var sb = new StringBuilder();
        int Count(string kind) => g.Nodes.Values.Count(n => n.Kind == kind);
        sb.AppendLine($"Solution {session.Name} (folder {paths.Root})");
        sb.AppendLine($"{Count("project")} projects, {Count("type")} types, {Count("member")} members, {Count("endpoint")} endpoints, " +
                      $"{g.Services.Count} service registrations, {Count("package")} NuGet packages.");
        sb.AppendLine();
        sb.AppendLine("Projects:");
        foreach (var p in g.Nodes.Values.Where(n => n.Kind == "project").OrderBy(n => n.Name))
        {
            var refs = q.Out(p.Id).Where(e => e.Kind == "projectRef").Select(e => q.Label(e.Target)).Order().ToList();
            var types = g.Nodes.Values.Count(n => n.Kind == "type" && n.Project == p.Id);
            var endpoints = g.Nodes.Values.Count(n => n.Kind == "endpoint" && n.Project == p.Id);
            sb.AppendLine($"- {p.Name} ({p.Meta.GetValueOrDefault("projectKind")}, {p.Meta.GetValueOrDefault("tfm")}): {types} types" +
                          (endpoints > 0 ? $", {endpoints} endpoints" : "") +
                          (refs.Count > 0 ? $"; references {string.Join(", ", refs)}" : ""));
        }
        sb.AppendLine();
        sb.AppendLine(g.Problems.Count == 0 ? "No problems found." : $"{g.Problems.Count} problems; see list_problems.");
        if (g.Coverage.Count > 0)
        {
            sb.AppendLine("Not followed by the analysis:");
            foreach (var c in g.Coverage) sb.AppendLine($"- {c.Count} × {c.Label}");
        }
        return sb.ToString().TrimEnd();
    }

    string Find(GraphQuery q, string query, string? kind)
    {
        var hits = q.Search(query, kind).Take(25).ToList();
        if (hits.Count == 0) return $"Nothing matches \"{query}\".";
        return string.Join('\n', hits.Select(n => $"- {n.Kind} {q.Label(n.Id)}  {Where(n)}  id: {n.Id}"));
    }

    string Endpoints(GraphQuery q, string? filter)
    {
        var list = q.Graph.Nodes.Values.Where(n => n.Kind == "endpoint")
            .Where(n => filter is null || n.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || (n.Meta.GetValueOrDefault("handler") as string is { } h && q.Label(h).Contains(filter, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(n => n.Meta.GetValueOrDefault("route") as string, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (list.Count == 0) return filter is null ? "No HTTP endpoints found." : $"No endpoint matches \"{filter}\".";

        return string.Join('\n', list.Select(n =>
        {
            var handler = n.Meta.GetValueOrDefault("handler") as string ?? n.Meta.GetValueOrDefault("target") as string;
            var auth = n.Meta.ContainsKey("auth") ? " [auth]" : "";
            return $"- {n.Name}{auth} → {(handler is null ? "inline handler" : q.Label(handler))}  {Where(n)}";
        }));
    }

    string Flow(GraphQuery q, Node root, bool up, int depth)
    {
        var sb = new StringBuilder();
        sb.AppendLine(up ? $"Who calls {q.Label(root.Id)} ({Where(root)}):" : $"What {q.Label(root.Id)} does ({Where(root)}), in source order:");
        var seen = new HashSet<string> { root.Id };

        void Down(string id, int level, string indent)
        {
            var steps = q.Out(id).Where(e => FlowKinds.Contains(e.Kind)).OrderBy(e => e.Line ?? 0).ToList();
            if (steps.Count > 0 && level >= depth)
            {
                sb.AppendLine($"{indent}… {steps.Count} more steps (raise depth to see them)");
                return;
            }
            foreach (var e in steps)
            {
                if (!q.Graph.Nodes.TryGetValue(e.Target, out var target)) continue;
                var at = e.File is null ? "" : $"  ({paths.Relative(e.File)}:{e.Line})";
                if (e.Kind is "db" or "http")
                {
                    var what = e.Kind == "db" ? "database " + target.Name : "outbound HTTP call";
                    sb.AppendLine($"{indent}- {what}{(e.Label is { } l ? ": " + l : "")}{at}");
                    continue;
                }

                var impls = target.Kind == "member" && target.Meta.ContainsKey("abstract") ? q.Implementations(target.Id) : [];
                var word = e.Kind switch { "dispatches" => "sends to", "handles" => "handled by", _ => "calls" };
                if (impls.Count > 0)
                {
                    foreach (var impl in impls)
                    {
                        sb.AppendLine($"{indent}- {word} {q.Label(target.Id)} → {q.Label(impl.Id)}  {Where(impl)}{at}");
                        if (seen.Add(impl.Id)) Down(impl.Id, level + 1, indent + "  ");
                    }
                    continue;
                }

                sb.AppendLine($"{indent}- {word} {q.Label(target.Id)}  {Where(target)}{at}");
                if (target.Kind is "member" or "endpoint" && seen.Add(target.Id)) Down(target.Id, level + 1, indent + "  ");
            }
        }

        void Up(string id, int level, string indent)
        {
            var callers = q.In(id).Where(e => CallerKinds.Contains(e.Kind)).Select(e => (e, Via: "")).ToList();
            foreach (var a in q.Abstractions(id))
                callers.AddRange(q.In(a.Id).Where(e => CallerKinds.Contains(e.Kind)).Select(e => (e, Via: q.Label(a.Id))));
            if (callers.Count > 0 && level >= depth)
            {
                sb.AppendLine($"{indent}… {callers.Count} more callers (raise depth to see them)");
                return;
            }
            foreach (var (e, via) in callers)
            {
                if (!q.Graph.Nodes.TryGetValue(e.Source, out var source)) continue;
                sb.AppendLine($"{indent}- {q.Label(source.Id)}{(via.Length > 0 ? " (through " + via + ")" : "")}  {Where(source)}");
                if (seen.Add(source.Id)) Up(source.Id, level + 1, indent + "  ");
            }
        }

        if (up) Up(root.Id, 0, "");
        else Down(root.Id, 0, "");
        if (sb.ToString().Split('\n').Length <= 2) sb.AppendLine(up ? "Nothing in the solution calls it." : "It calls nothing that the analysis can follow.");
        return sb.ToString().TrimEnd();
    }

    string Describe(GraphQuery q, Node node)
    {
        var type = node.Kind == "member" && node.Parent is not null && q.Graph.Nodes.TryGetValue(node.Parent, out var owner) ? owner : node;
        if (type.Kind != "type") throw new ToolException($"{q.Label(node.Id)} is {Article(node.Kind)}, not a type. Use trace_flow for endpoints and methods.");

        var sb = new StringBuilder();
        var m = type.Meta;
        sb.AppendLine($"{m.GetValueOrDefault("typeKind")} {(type.Ns is { Length: > 0 } ns ? ns + "." : "")}{type.Name}  {Where(type)}");
        if (m.GetValueOrDefault("tags") is List<string> tags) sb.AppendLine("Roles: " + string.Join(", ", tags));
        if (m.GetValueOrDefault("base") is string b) sb.AppendLine("Base class: " + b);
        if (m.GetValueOrDefault("lifetime") is string life)
            sb.AppendLine($"Registered as {life}" + (m.GetValueOrDefault("registeredAs") is List<string> asList ? " for " + string.Join(", ", asList) : ""));

        var registrations = q.Graph.Services.Where(s => s.ServiceId == type.Id || s.ImplId == type.Id).ToList();
        foreach (var s in registrations)
            sb.AppendLine($"Registration: {s.Service}{(s.Impl is not null && s.Impl != s.Service ? " → " + s.Impl : "")} ({s.Lifetime}, {s.Method}) at {paths.Relative(s.File)}:{s.Line}");

        var members = q.Graph.Nodes.Values.Where(n => n.Kind == "member" && n.Parent == type.Id).OrderBy(n => n.Line ?? 0).ToList();
        if (members.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Members:");
            foreach (var x in members) sb.AppendLine($"- {x.Meta.GetValueOrDefault("sig")}{(x.Meta.GetValueOrDefault("returns") is string r ? " : " + r : "")}  line {x.Line}");
        }

        var own = members.Select(x => x.Id).Append(type.Id).ToHashSet();
        string TypeOf(string id) => q.Graph.Nodes.TryGetValue(id, out var n) && n.Kind == "member" && n.Parent is not null ? n.Parent : id;
        var uses = own.SelectMany(q.Out).Where(e => UseKinds.Contains(e.Kind)).Select(e => (Id: TypeOf(e.Target), e.Kind))
            .Where(x => x.Id != type.Id).GroupBy(x => x.Id).ToList();
        var usedBy = own.SelectMany(q.In).Where(e => UseKinds.Contains(e.Kind)).Select(e => (Id: TypeOf(e.Source), e.Kind))
            .Where(x => x.Id != type.Id).GroupBy(x => x.Id).ToList();

        if (uses.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Depends on:");
            foreach (var x in uses.OrderBy(x => q.Label(x.Key))) sb.AppendLine($"- {q.Label(x.Key)} ({string.Join(", ", x.Select(y => y.Kind).Distinct())})");
        }
        if (usedBy.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Used by:");
            foreach (var x in usedBy.OrderBy(x => q.Label(x.Key))) sb.AppendLine($"- {q.Label(x.Key)} ({string.Join(", ", x.Select(y => y.Kind).Distinct())})");
        }

        var problems = q.Graph.Problems.Where(p => p.Nodes.Contains(type.Id)).ToList();
        if (problems.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Problems:");
            foreach (var p in problems) sb.AppendLine($"- {p.Severity}: {p.Title}. {paths.Scrub(p.Message)}");
        }
        return sb.ToString().TrimEnd();
    }

    static string Article(string word) => "aeiou".Contains(word[0]) ? "an " + word : "a " + word;

    string Services(Graph g, string? filter)
    {
        var list = g.Services
            .Where(s => filter is null || s.Service.Contains(filter, StringComparison.OrdinalIgnoreCase) || (s.Impl?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderBy(s => s.Service, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (list.Count == 0) return filter is null ? "No dependency injection registrations found." : $"No registration matches \"{filter}\".";
        return string.Join('\n', list.Select(s =>
            $"- {s.Service}{(s.Impl is not null && s.Impl != s.Service ? " → " + s.Impl : "")} ({s.Lifetime}, {s.Method})  {paths.Relative(s.File)}:{s.Line}"));
    }

    string Problems(Graph g)
    {
        var sb = new StringBuilder();
        if (g.Problems.Count == 0) sb.AppendLine("No problems found.");
        foreach (var p in g.Problems)
            sb.AppendLine($"- {p.Severity}: {p.Title} [{p.Rule}]{(p.File is null ? "" : $" at {paths.Relative(p.File)}:{p.Line}")}\n  {paths.Scrub(p.Message)}");
        if (g.Coverage.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Not followed by the analysis (the map may miss things here):");
            foreach (var c in g.Coverage)
                sb.AppendLine($"- {c.Count} × {c.Label}: " + string.Join(", ", c.Sites.Take(4).Select(s => $"{paths.Relative(s.File)}{(s.Line > 0 ? ":" + s.Line : "")}")));
        }
        return sb.ToString().TrimEnd();
    }

    string Users(GraphQuery q, Node root, int depth)
    {
        var start = root.Kind == "member" ? new[] { root.Id } : q.Graph.Nodes.Values.Where(n => n.Parent == root.Id && n.Kind == "member").Select(n => n.Id).Append(root.Id).ToArray();
        string Owner(string id) => q.Graph.Nodes.TryGetValue(id, out var n) && n.Kind == "member" && n.Parent is not null ? n.Parent : id;

        var sb = new StringBuilder();
        sb.AppendLine($"What depends on {q.Label(root.Id)} ({Where(root)}):");
        var seen = new HashSet<string>(start) { Owner(root.Id) };
        var frontier = start.ToList();
        for (var level = 1; level <= depth && frontier.Count > 0; level++)
        {
            var next = new List<string>();
            var found = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            foreach (var id in frontier)
            {
                var incoming = q.In(id).Where(e => UseKinds.Contains(e.Kind)).ToList();
                foreach (var a in q.Abstractions(id)) incoming.AddRange(q.In(a.Id).Where(e => UseKinds.Contains(e.Kind)));
                foreach (var e in incoming)
                {
                    var user = Owner(e.Source);
                    if (found.TryGetValue(user, out var known))
                    {
                        known.Add(e.Kind);
                        continue;
                    }
                    if (!seen.Add(user)) continue;
                    found[user] = [e.Kind];
                    next.Add(user);
                    next.AddRange(q.Graph.Nodes.Values.Where(n => n.Parent == user && n.Kind == "member").Select(n => n.Id));
                }
            }
            if (found.Count == 0) break;
            sb.AppendLine(level == 1 ? "Directly:" : $"{level} steps away:");
            foreach (var (id, kinds) in found)
                if (q.Graph.Nodes.TryGetValue(id, out var n)) sb.AppendLine($"- {q.Label(id)} ({string.Join(", ", kinds)})  {Where(n)}");
            frontier = next;
        }
        if (seen.Count <= start.Length + 1) sb.AppendLine("Nothing in the solution uses it.");
        return sb.ToString().TrimEnd();
    }
}

/// Lookups over a graph: names, edges both ways, and interface calls resolved to implementations.
internal sealed class GraphQuery
{
    readonly Dictionary<string, List<Edge>> _out = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<Edge>> _in = new(StringComparer.Ordinal);

    public GraphQuery(Graph graph)
    {
        Graph = graph;
        foreach (var e in graph.Edges.Values)
        {
            (_out.TryGetValue(e.Source, out var o) ? o : _out[e.Source] = []).Add(e);
            (_in.TryGetValue(e.Target, out var i) ? i : _in[e.Target] = []).Add(e);
        }
    }

    public Graph Graph { get; }

    public IReadOnlyList<Edge> Out(string id) => _out.GetValueOrDefault(id) ?? [];
    public IReadOnlyList<Edge> In(string id) => _in.GetValueOrDefault(id) ?? [];

    public string Label(string id)
    {
        if (!Graph.Nodes.TryGetValue(id, out var n)) return id;
        return n.Kind == "member" && n.Parent is not null && Graph.Nodes.TryGetValue(n.Parent, out var owner) ? $"{owner.Name}.{n.Name}" : n.Name;
    }

    /// Methods that implement an interface or abstract method, preferring the class registered for the interface.
    public List<Node> Implementations(string memberId)
    {
        var impls = Out(memberId).Where(e => e.Kind == "impl").Select(e => Graph.Nodes.GetValueOrDefault(e.Target)).OfType<Node>().ToList();
        if (impls.Count < 2) return impls;
        var iface = Graph.Nodes.GetValueOrDefault(memberId)?.Parent;
        var registered = Graph.Services.Where(s => s.ServiceId == iface && s.ImplId is not null).Select(s => s.ImplId).ToHashSet();
        var preferred = impls.Where(m => registered.Contains(m.Parent)).ToList();
        return preferred.Count > 0 ? preferred : impls;
    }

    public List<Node> Abstractions(string memberId) =>
        In(memberId).Where(e => e.Kind == "impl").Select(e => Graph.Nodes.GetValueOrDefault(e.Source)).OfType<Node>().ToList();

    static readonly Dictionary<string, int> KindOrder = new() { ["endpoint"] = 0, ["type"] = 1, ["member"] = 2, ["project"] = 3, ["package"] = 4 };

    public IEnumerable<Node> Search(string query, string? kind)
    {
        var q = query.Trim();
        return Graph.Nodes.Values
            .Where(n => KindOrder.ContainsKey(n.Kind) && (kind is null || n.Kind == kind))
            .Select(n => (n, Score: Score(n, q)))
            .Where(x => x.Score >= 0)
            .OrderBy(x => x.Score).ThenBy(x => KindOrder[x.n.Kind]).ThenBy(x => x.n.Name.Length)
            .Select(x => x.n);
    }

    int Score(Node n, string q)
    {
        var name = n.Name;
        var label = Label(n.Id);
        if (name.Equals(q, StringComparison.OrdinalIgnoreCase) || label.Equals(q, StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.Contains(q, StringComparison.OrdinalIgnoreCase)) return 2;
        if (label.Contains(q, StringComparison.OrdinalIgnoreCase)) return 3;
        return -1;
    }

    /// An id, an endpoint ("POST /api/orders" or just the route), "Type.Member", or a type name.
    public List<Node> Resolve(string target)
    {
        var t = target.Trim();
        if (Graph.Nodes.TryGetValue(t, out var exact)) return [exact];

        var endpoints = Graph.Nodes.Values.Where(n => n.Kind == "endpoint").ToList();
        var byName = endpoints.Where(n => n.Name.Equals(t, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count > 0) return byName;
        var byRoute = endpoints.Where(n => (n.Meta.GetValueOrDefault("route") as string)?.Equals(t, StringComparison.OrdinalIgnoreCase) == true).ToList();
        if (byRoute.Count > 0) return byRoute;

        var labelled = Graph.Nodes.Values.Where(n => n.Kind is "member" or "type" && Label(n.Id).Equals(t, StringComparison.OrdinalIgnoreCase)).ToList();
        if (labelled.Count > 0)
        {
            // "OrderService.Place" may name several overloads; the methods win over properties.
            var methods = labelled.Where(n => n.Meta.GetValueOrDefault("memberKind") as string == "method").ToList();
            return labelled.Count > 1 && methods.Count == 1 ? methods : labelled;
        }

        var types = Graph.Nodes.Values.Where(n => n.Kind == "type" && n.Name.Equals(t, StringComparison.OrdinalIgnoreCase)).ToList();
        if (types.Count > 0) return types;

        return Search(t, null).Take(8).ToList();
    }
}

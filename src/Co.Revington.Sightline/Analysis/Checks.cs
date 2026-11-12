using System.Text.RegularExpressions;

namespace Co.Revington.Sightline.Analysis;

/// A problem in the code that the map can point at. Severity: "error", "warning" or "info".
/// Nodes: the graph nodes involved, the first one being where to look.
internal sealed record Problem(
    string Id, string Rule, string Severity, string Title, string Message, string? File, int? Line, IReadOnlyList<string> Nodes);

/// Something Sightline saw but could not follow, with a few places where it happens.
internal sealed record CoverageNote(string Kind, string Label, int Count, IReadOnlyList<CoverageSite> Sites);

internal sealed record CoverageSite(string? Node, string File, int Line);

/// "From" projects may not use any project or package that matches "MayNotUse".
internal sealed record UsageRule(IReadOnlyList<string> From, IReadOnlyList<string> MayNotUse, string? Because);

/// Settings for the checks, from sightline.json.
/// Layers: project name patterns from the top layer down; a project may use its own layer and the ones below.
/// Severity: rule id -> "error", "warning", "info" or "off".
internal sealed record CheckOptions(
    IReadOnlyList<IReadOnlyList<string>> Layers,
    IReadOnlyList<UsageRule> Rules,
    IReadOnlyDictionary<string, string> Severity)
{
    public static readonly CheckOptions None = new([], [], new Dictionary<string, string>());
}

/// Problems found by reading the dependency injection setup and the layer rules.
internal static class Checks
{
    public static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>
    {
        ["captive-dependency"] = "error",
        ["missing-registration"] = "warning",
        ["dependency-cycle"] = "error",
        ["layer-violation"] = "error",
        ["unused-registration"] = "info",
    };

    public static readonly string[] Severities = ["error", "warning", "info", "off"];

    sealed record Registration(string ServiceId, string? ImplId, string Lifetime, string Method, string? Owner, string File, int Line);

    sealed record Dependency(DependencyFact Fact, string File);

    sealed class Context
    {
        public required Graph Graph { get; init; }
        public required CheckOptions Options { get; init; }
        public Dictionary<string, List<Registration>> ByService { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, HashSet<string>> ImplLifetimes { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<Dependency>> ByConsumer { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Used { get; } = new(StringComparer.Ordinal);
        public bool Scanning { get; set; }
        public bool Decorating { get; set; }
        public List<Problem> Problems { get; } = [];

        public string Label(string id) => Graph.Nodes.TryGetValue(id, out var n)
            ? n.Kind == "member" && n.Parent is not null && Graph.Nodes.TryGetValue(n.Parent, out var owner) ? $"{owner.Name}.{n.Name}" : n.Name
            : id;

        public string? Severity(string rule)
        {
            var value = Options.Severity.GetValueOrDefault(rule) ?? Defaults[rule];
            return value == "off" ? null : value;
        }

        public void Add(string rule, string severity, string title, string message, string? file, int? line, params string[] nodes)
        {
            var id = rule + ":" + string.Join("|", nodes);
            if (Problems.Any(p => p.Id == id)) return;
            Problems.Add(new Problem(id, rule, severity, title, message, file, line, nodes.Where(Graph.Has).ToList()));
        }
    }

    public static (List<Problem> Problems, List<CoverageNote> Coverage) Run(
        Graph g, IReadOnlyList<DocFacts> docs, IReadOnlyList<ProjectFacts> projects,
        Dictionary<string, List<string>> endpointsByKey, CheckOptions options)
    {
        var c = new Context { Graph = g, Options = options };
        Collect(c, docs, endpointsByKey);

        if (c.Severity("captive-dependency") is { } captive) CaptiveDependencies(c, captive);
        if (c.Severity("missing-registration") is { } missing) MissingRegistrations(c, missing);
        if (c.Severity("dependency-cycle") is { } cycle) Cycles(c, cycle);
        if (c.Severity("unused-registration") is { } unused) UnusedRegistrations(c, unused);
        if (c.Severity("layer-violation") is { } layer) Layers(c, projects, layer);

        var order = new Dictionary<string, int> { ["error"] = 0, ["warning"] = 1, ["info"] = 2 };
        var problems = c.Problems
            .OrderBy(p => order.GetValueOrDefault(p.Severity, 3))
            .ThenBy(p => p.Rule, StringComparer.Ordinal)
            .ThenBy(p => p.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Line)
            .ToList();
        return (problems, Coverage(g, docs));
    }

    static void Collect(Context c, IReadOnlyList<DocFacts> docs, Dictionary<string, List<string>> endpointsByKey)
    {
        foreach (var d in docs)
        {
            foreach (var r in d.Di)
            {
                if (r.ImplId is not null)
                {
                    if (!c.ImplLifetimes.TryGetValue(r.ImplId, out var lifetimes)) c.ImplLifetimes[r.ImplId] = lifetimes = [];
                    lifetimes.Add(Simple(r.Lifetime));
                }
                if (r.ServiceId is null) continue;
                if (!c.ByService.TryGetValue(r.ServiceId, out var list)) c.ByService[r.ServiceId] = list = [];
                list.Add(new Registration(r.ServiceId, r.ImplId, r.Lifetime, r.Method, r.Owner, d.File, r.Line));
            }

            foreach (var dep in d.Dependencies)
            {
                // Minimal API handlers are keyed by the endpoint fact; one fact can be several endpoint nodes.
                var consumers = endpointsByKey.TryGetValue(dep.Consumer, out var ids) ? ids : [dep.Consumer];
                foreach (var consumer in consumers)
                {
                    if (!c.ByConsumer.TryGetValue(consumer, out var list)) c.ByConsumer[consumer] = list = [];
                    if (!list.Any(x => x.Fact.ServiceId == dep.ServiceId && x.Fact.Wrapper == dep.Wrapper))
                        list.Add(new Dependency(dep with { Consumer = consumer }, d.File));
                }
                c.Used.Add(dep.ServiceId);
            }

            foreach (var r in d.Refs)
                if (r.Kind == "lookup") c.Used.Add(r.To);

            foreach (var cov in d.Coverage)
            {
                if (cov.Kind == "scan") c.Scanning = true;
                if (cov.Label.Contains("decorates", StringComparison.Ordinal)) c.Decorating = true;
            }
        }
    }

    static List<string> Tags(Graph g, string id) =>
        g.Nodes.TryGetValue(id, out var n) && n.Meta.GetValueOrDefault("tags") is List<string> tags ? tags : [];

    /// "singleton", "scoped" or "transient": how long an object created by dependency injection lives.
    /// Null when the type is not created by dependency injection.
    static string? LifetimeOf(Context c, string consumer)
    {
        if (c.Graph.Nodes.TryGetValue(consumer, out var node) && node.Kind == "endpoint") return "scoped";

        if (c.ImplLifetimes.TryGetValue(consumer, out var lifetimes))
        {
            if (lifetimes.Contains("singleton")) return "singleton";
            if (lifetimes.Contains("scoped")) return "scoped";
            if (lifetimes.Contains("transient")) return "transient";
        }

        var tags = Tags(c.Graph, consumer);
        if (tags.Contains("controller") || tags.Contains("hub")) return "transient";
        if (tags.Contains("middleware"))
        {
            // Middleware written by convention is created once, for the life of the app. IMiddleware is resolved per request.
            var external = node?.Meta.GetValueOrDefault("externalInterfaces") as List<string> ?? [];
            return external.Contains("IMiddleware") ? null : "singleton";
        }
        return null;
    }

    static string Simple(string lifetime) => lifetime switch
    {
        "hosted" => "singleton",
        "httpclient" => "transient",
        "options" => "options",
        _ => lifetime,
    };

    static string Article(string word) => "aeiouAEIOU".Contains(word[0]) ? "an " + word : "a " + word;

    // ---------------------------------------------------------------- captive dependencies

    /// A singleton that receives a scoped service, directly or through transient services, keeps that
    /// scoped object for the life of the app. ASP.NET Core refuses this in Development.
    static void CaptiveDependencies(Context c, string severity)
    {
        foreach (var consumer in c.ByConsumer.Keys.ToList())
        {
            if (LifetimeOf(c, consumer) != "singleton") continue;
            Walk(consumer, [c.Label(consumer)], 0);

            void Walk(string at, List<string> path, int depth)
            {
                if (depth > 5 || !c.ByConsumer.TryGetValue(at, out var deps)) return;
                foreach (var dep in deps)
                {
                    var service = dep.Fact.ServiceId;
                    var here = path.Append(dep.Fact.ServiceName).ToList();

                    if (dep.Fact.Wrapper == "options-scoped")
                    {
                        Report(dep, "IOptionsSnapshot<" + dep.Fact.ServiceName + ">", here);
                        continue;
                    }
                    if (dep.Fact.Wrapper == "options" || !c.ByService.TryGetValue(service, out var regs)) continue;

                    if (regs.Any(r => Simple(r.Lifetime) == "scoped"))
                    {
                        Report(dep, dep.Fact.ServiceName, here);
                        continue;
                    }
                    foreach (var r in regs.Where(r => Simple(r.Lifetime) == "transient" && r.ImplId is not null && r.ImplId != at))
                        Walk(r.ImplId!, [.. here, c.Label(r.ImplId!)], depth + 1);
                }
            }

            void Report(Dependency dep, string scopedName, List<string> path)
            {
                var name = c.Label(consumer);
                var via = path.Count > 2 ? $" through {string.Join(" → ", path.Skip(1).Take(path.Count - 2))}" : "";
                c.Add("captive-dependency", severity, "A singleton holds on to a scoped service",
                    $"{name} lives as long as the app (singleton), but it receives {scopedName}{via}, which is scoped. " +
                    $"That one {scopedName} is then shared by every request, and in Development ASP.NET Core refuses to start it " +
                    "(\"Cannot consume scoped service from singleton\"). Inject IServiceScopeFactory and create a scope where the " +
                    $"service is needed, or register {name} as scoped.",
                    dep.File, dep.Fact.Line, consumer, dep.Fact.ServiceId);
            }
        }
    }

    // ---------------------------------------------------------------- missing registrations

    static void MissingRegistrations(Context c, string severity)
    {
        if (c.Scanning) severity = "info";

        foreach (var (consumer, deps) in c.ByConsumer)
        {
            if (LifetimeOf(c, consumer) is null) continue;          // not created by dependency injection

            foreach (var dep in deps)
            {
                if (dep.Fact.Optional || dep.Fact.Wrapper is "enumerable" or "options" or "options-scoped") continue;
                if (c.ByService.ContainsKey(dep.Fact.ServiceId) || !c.Graph.Nodes.TryGetValue(dep.Fact.ServiceId, out var service)) continue;

                var impl = c.Graph.Edges.Values
                    .Where(e => e.Kind == "implements" && e.Target == service.Id)
                    .Select(e => c.Graph.Nodes.GetValueOrDefault(e.Source)?.Name)
                    .FirstOrDefault(n => n is not null);
                var example = service.Meta.GetValueOrDefault("typeKind") as string == "interface" && impl is not null
                    ? $"services.AddScoped<{dep.Fact.ServiceName}, {impl}>()"
                    : $"services.AddScoped<{dep.Fact.ServiceName}>()";
                var scanning = c.Scanning
                    ? " The solution also registers services by scanning assemblies, which Sightline cannot follow, so this may be registered there."
                    : "";

                c.Add("missing-registration", severity, "A service that is never registered",
                    $"{c.Label(consumer)} receives {dep.Fact.ServiceName}, but no registration for {dep.Fact.ServiceName} was found, " +
                    $"so creating {c.Label(consumer)} fails at run time (\"Unable to resolve service for type\"). " +
                    $"Register it, for example with {example}.{scanning}",
                    dep.File, dep.Fact.Line, consumer, service.Id);
            }
        }
    }

    // ---------------------------------------------------------------- cycles

    /// Services that need each other to be created. Dependency injection gives up with
    /// "A circular dependency was detected".
    static void Cycles(Context c, string severity)
    {
        if (c.Decorating) severity = "info";       // decorators wrap their own service on purpose

        // service -> services that its implementations need
        var next = new Dictionary<string, List<(string Service, Dependency Dep)>>(StringComparer.Ordinal);
        foreach (var (service, regs) in c.ByService)
        foreach (var r in regs)
        {
            if (r.ImplId is null || !c.ByConsumer.TryGetValue(r.ImplId, out var deps)) continue;
            if (!next.TryGetValue(service, out var list)) next[service] = list = [];
            foreach (var d in deps.Where(d => d.Fact.Wrapper is "" or "enumerable" && c.ByService.ContainsKey(d.Fact.ServiceId)))
                list.Add((d.Fact.ServiceId, d));
        }

        foreach (var cycle in StronglyConnected(next))
        {
            var first = cycle[0];
            var path = new List<string> { first };
            var dep = next[first].First(x => cycle.Contains(x.Service));
            for (var at = dep.Service; at != first && path.Count <= cycle.Count; at = next[at].First(x => cycle.Contains(x.Service)).Service)
                path.Add(at);
            path.Add(first);

            var names = path.Select(c.Label).ToList();
            c.Add("dependency-cycle", severity, "Services that need each other",
                $"Creating {names[0]} needs {string.Join(", which needs ", names.Skip(1))}. Dependency injection cannot create any of them " +
                "(\"A circular dependency was detected\"). Break the loop: move the shared part into its own service, or ask for one of " +
                "them later through IServiceProvider or Lazy<T>.",
                dep.Dep.File, dep.Dep.Fact.Line, [.. cycle.OrderBy(x => x, StringComparer.Ordinal)]);
        }
    }

    /// Groups of nodes that can all reach each other (Tarjan), including a node that needs itself.
    static List<List<string>> StronglyConnected(Dictionary<string, List<(string Service, Dependency Dep)>> next)
    {
        var index = new Dictionary<string, int>();
        var low = new Dictionary<string, int>();
        var stack = new Stack<string>();
        var onStack = new HashSet<string>();
        var result = new List<List<string>>();
        var counter = 0;

        void Visit(string v)
        {
            index[v] = low[v] = counter++;
            stack.Push(v);
            onStack.Add(v);
            foreach (var (w, _) in next.GetValueOrDefault(v) ?? [])
            {
                if (!index.ContainsKey(w))
                {
                    Visit(w);
                    low[v] = Math.Min(low[v], low[w]);
                }
                else if (onStack.Contains(w))
                {
                    low[v] = Math.Min(low[v], index[w]);
                }
            }
            if (low[v] != index[v]) return;

            var group = new List<string>();
            string x;
            do
            {
                x = stack.Pop();
                onStack.Remove(x);
                group.Add(x);
            } while (x != v);

            if (group.Count > 1 || (next.GetValueOrDefault(v) ?? []).Any(e => e.Service == v)) result.Add(group);
        }

        foreach (var v in next.Keys.Order(StringComparer.Ordinal))
            if (!index.ContainsKey(v)) Visit(v);
        return result;
    }

    // ---------------------------------------------------------------- unused registrations

    static void UnusedRegistrations(Context c, string severity)
    {
        foreach (var (service, regs) in c.ByService)
        {
            if (c.Used.Contains(service) || !c.Graph.Nodes.TryGetValue(service, out var node)) continue;
            if (regs.Any(r => Simple(r.Lifetime) == "options" || r.Lifetime == "hosted")) continue;

            // Things the framework or a dispatcher picks up by itself.
            var tags = Tags(c.Graph, service);
            if (tags.Intersect(["controller", "hub", "middleware", "handler", "hosted", "entrypoint"]).Any()) continue;
            if (Regex.IsMatch(node.Name, @"(Handler|Validator|Consumer|Filter|Middleware|Behavior|Behaviour|Profile|HostedService)(<.*>)?$")) continue;

            var r = regs[0];
            c.Add("unused-registration", severity, "A registration nothing asks for",
                $"{node.Name} is registered{(r.Owner is not null ? " in " + c.Label(r.Owner) : "")}, but no class, endpoint or lookup " +
                "in the solution asks for it. A package or reflection may still use it; otherwise the registration can go.",
                r.File, r.Line, service);
        }
    }

    // ---------------------------------------------------------------- layers

    static readonly HashSet<string> UsageKinds = ["calls", "injects", "references", "creates", "inherits", "implements", "dispatches", "db"];

    static void Layers(Context c, IReadOnlyList<ProjectFacts> projects, string severity)
    {
        var options = c.Options;
        if (options.Layers.Count == 0 && options.Rules.Count == 0) return;

        var names = projects.ToDictionary(p => p.Id, p => p.Name, StringComparer.Ordinal);
        var layerOf = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in projects)
            for (var i = 0; i < options.Layers.Count; i++)
                if (options.Layers[i].Any(pattern => Glob.Matches(pattern, p.Name)))
                {
                    layerOf[p.Id] = i;
                    break;
                }

        // project -> project or package it uses, with the first place in the code that shows it
        var uses = new Dictionary<(string From, string To), (string? File, int? Line, string Example)>();
        foreach (var e in c.Graph.Edges.Values)
        {
            string? from, to;
            if (e.Kind is "projectRef" or "packageRef")
            {
                (from, to) = (e.Source, e.Target);
            }
            else if (UsageKinds.Contains(e.Kind))
            {
                from = c.Graph.Nodes.GetValueOrDefault(e.Source)?.Project;
                to = c.Graph.Nodes.GetValueOrDefault(e.Target)?.Project;
            }
            else continue;

            if (from is null || to is null || from == to) continue;
            var example = e.Kind is "projectRef" or "packageRef"
                ? "a reference in the project file"
                : $"{c.Label(e.Source)} {e.Kind} {c.Label(e.Target)}";
            if (!uses.TryGetValue((from, to), out var found) || (found.File is null && e.File is not null))
                uses[(from, to)] = (e.File, e.Line, example);
        }

        foreach (var ((from, to), where) in uses.OrderBy(u => u.Key.From).ThenBy(u => u.Key.To))
        {
            var fromName = names.GetValueOrDefault(from) ?? from;
            var toName = names.GetValueOrDefault(to) ?? (to.StartsWith("pkg:", StringComparison.Ordinal) ? to[4..] : to);

            if (layerOf.TryGetValue(from, out var a) && layerOf.TryGetValue(to, out var b) && a > b)
            {
                var order = string.Join(" > ", options.Layers.Select(l => string.Join(" | ", l)));
                c.Add("layer-violation", severity, "A lower layer uses a higher one",
                    $"{fromName} uses {toName} ({where.Example}), but the layers in sightline.json put {fromName} below {toName}: {order}. " +
                    "A layer may only use itself and the layers below it.",
                    where.File, where.Line, from, to);
            }

            foreach (var rule in options.Rules)
            {
                if (!rule.From.Any(p => Glob.Matches(p, fromName)) || !rule.MayNotUse.Any(p => Glob.Matches(p, toName))) continue;
                c.Add("layer-violation", severity, "A project uses something it may not",
                    $"{fromName} uses {toName} ({where.Example}), but a rule in sightline.json says it may not" +
                    (rule.Because is { Length: > 0 } because ? $": {because}" : "."),
                    where.File, where.Line, from, to);
            }
        }
    }

    // ---------------------------------------------------------------- coverage

    static List<CoverageNote> Coverage(Graph g, IReadOnlyList<DocFacts> docs)
    {
        var notes = docs
            .SelectMany(d => d.Coverage.Select(x => (Doc: d, Fact: x)))
            .GroupBy(x => (x.Fact.Kind, x.Fact.Label))
            .OrderBy(x => x.Key.Kind == "endpoints" ? 0 : x.Key.Kind == "scan" ? 1 : 2)
            .ThenBy(x => x.Key.Label, StringComparer.Ordinal)
            .Select(x => new CoverageNote(x.Key.Kind, x.Key.Label, x.Count(), x
                .Take(8)
                .Select(s => new CoverageSite(g.Has(s.Fact.Owner) ? s.Fact.Owner : null, s.Doc.File, s.Fact.Line))
                .ToList()))
            .ToList();

        var unresolved = docs.Where(d => d.Unresolved > 0).OrderByDescending(d => d.Unresolved).ToList();
        var total = unresolved.Sum(d => d.Unresolved);
        if (total > 0)
            notes.Add(new CoverageNote("unresolved",
                "Calls the compiler could not resolve, usually because a package is missing (try dotnet restore)",
                total, unresolved.Take(8).Select(d => new CoverageSite(null, d.File, 0)).ToList()));
        return notes;
    }
}

/// Matches names against patterns with * (any run of characters) and ? (one character), ignoring case.
internal static class Glob
{
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> Cache = new();

    public static bool Matches(string pattern, string name) =>
        Cache.GetOrAdd(pattern, p => new Regex(
            "^" + Regex.Escape(p.Trim()).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).IsMatch(name);
}

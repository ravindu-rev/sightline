using System.Text.RegularExpressions;

namespace Co.Revington.Sightline.Analysis;

// What a running app reports about itself (through Co.Revington.Sightline.Hosting): the services it
// really registered and the endpoints it really mapped. Reading the code cannot see everything, for
// example services registered by scanning assemblies; the running app can.

/// One registration. Types are full names ("Ns.Outer+Inner", generic definitions as "Ns.Repo`1").
internal sealed record RuntimeService(
    string Service, string? ServiceAssembly, string? ServiceName,
    string? Impl, string? ImplAssembly, string? ImplName,
    string Lifetime, bool Factory, bool Instance);

/// One endpoint: its route pattern as ASP.NET Core sees it, and the method that handles it when known.
internal sealed record RuntimeEndpoint(string Route, string[] Methods, string? Name, string? HandlerType, string? HandlerAssembly, string? HandlerMethod);

internal sealed record RuntimeApp(
    int Pid, string App, string? Environment, string[] Addresses,
    IReadOnlyList<RuntimeService> Services, IReadOnlyList<RuntimeEndpoint> Endpoints, DateTime Seen);

internal static class RuntimeMerge
{
    /// "Shop.Application" + "Shop.Application.IOrderRepository" -> the graph id of that type.
    public static string? TypeId(string? assembly, string? fullName) =>
        assembly is null || string.IsNullOrEmpty(fullName) ? null : $"{assembly}::T:{fullName.Replace('+', '.')}";

    /// Registrations the running apps made that reading the code did not find, as facts the graph
    /// builder treats like any other registration. Only types of the solution count; the hundreds of
    /// framework registrations would bury them.
    public static DocFacts? Registrations(IReadOnlyList<RuntimeApp> apps, IReadOnlyCollection<DocFacts> docs, IReadOnlyList<ProjectFacts> projects)
    {
        if (apps.Count == 0) return null;
        var assemblies = projects.Select(p => p.AssemblyName).ToHashSet(StringComparer.Ordinal);
        var known = docs.SelectMany(d => d.Di)
            .Select(r => (r.ServiceId ?? r.ServiceName, r.ImplId ?? r.ImplName ?? ""))
            .ToHashSet();

        var facts = new DocFacts { File = "", Project = "" };
        var seen = new HashSet<(string, string)>();
        foreach (var app in apps)
        foreach (var s in app.Services)
        {
            var mine = (s.ServiceAssembly is not null && assemblies.Contains(s.ServiceAssembly)) || (s.ImplAssembly is not null && assemblies.Contains(s.ImplAssembly));
            if (!mine) continue;

            var serviceId = s.ServiceAssembly is not null && assemblies.Contains(s.ServiceAssembly) ? TypeId(s.ServiceAssembly, s.Service) : null;
            var implId = s.ImplAssembly is not null && assemblies.Contains(s.ImplAssembly) ? TypeId(s.ImplAssembly, s.Impl) : null;
            var key = (serviceId ?? s.ServiceName ?? s.Service, implId ?? s.ImplName ?? s.Impl ?? "");
            if (known.Contains(key) || !seen.Add(key)) continue;

            facts.Di.Add(new DiFact(serviceId, s.ServiceName ?? Short(s.Service), implId, s.ImplName ?? (s.Impl is null ? null : Short(s.Impl)),
                s.Lifetime.ToLowerInvariant(), "running app", "", 0));
        }
        return facts.Di.Count == 0 ? null : facts;
    }

    static string Short(string fullName)
    {
        var name = fullName[(fullName.LastIndexOfAny(['.', '+']) + 1)..];
        var tick = name.IndexOf('`');
        return tick < 0 ? name : name[..tick];
    }

    /// Marks endpoints the running app confirmed, and adds the ones only the running app knows about
    /// (FastEndpoints, Carter, Razor Pages, routes from configuration, ...).
    public static int Endpoints(Graph g, IReadOnlyList<RuntimeApp> apps, IReadOnlyList<ProjectFacts> projects)
    {
        if (apps.Count == 0) return 0;
        var byAssembly = projects.ToDictionary(p => p.AssemblyName, p => p, StringComparer.Ordinal);
        var existing = g.Nodes.Values.Where(n => n.Kind == "endpoint").ToList();
        var added = 0;

        foreach (var app in apps)
        foreach (var ep in app.Endpoints)
        {
            var route = Normalize(ep.Route);
            var verbs = ep.Methods.Length == 0 ? ["ANY"] : ep.Methods;
            foreach (var verb in verbs)
            {
                var match = existing.FirstOrDefault(n => Same(n, verb, route));
                if (match is not null)
                {
                    match.Meta["runtime"] = "seen";
                    continue;
                }

                var project = byAssembly.GetValueOrDefault(ep.HandlerAssembly ?? "") ?? byAssembly.GetValueOrDefault(app.App);
                var id = $"ep:{project?.Name ?? app.App}:{verb} {route}";
                if (g.Has(id)) continue;

                var meta = new Dictionary<string, object> { ["verb"] = verb, ["route"] = route, ["style"] = "runtime", ["runtime"] = "only" };
                var handler = Handler(g, ep);
                if (handler is not null)
                {
                    meta["handler"] = handler.Id;
                    g.Link("handles", id, handler.Id);
                }
                if (ep.Name is { Length: > 0 } name) meta["displayName"] = name;

                var node = new Node
                {
                    Id = id, Kind = "endpoint", Name = $"{verb} {route}", Parent = project?.Id, Project = project?.Id,
                    File = handler?.File, Line = handler?.Line, EndLine = handler?.EndLine, Meta = meta,
                };
                g.Add(node);
                existing.Add(node);
                added++;
            }
        }
        return added;
    }

    /// The method that handles a runtime endpoint, when its class is in the solution and the name is not overloaded.
    static Node? Handler(Graph g, RuntimeEndpoint ep)
    {
        if (TypeId(ep.HandlerAssembly, ep.HandlerType) is not { } typeId || !g.Has(typeId) || ep.HandlerMethod is null) return null;
        var methods = g.Nodes.Values.Where(n => n.Kind == "member" && n.Parent == typeId && n.Name == ep.HandlerMethod).ToList();
        return methods.Count == 1 ? methods[0] : null;
    }

    static bool Same(Node n, string verb, string route) =>
        (n.Meta.GetValueOrDefault("verb") as string ?? "").Split(',').Any(v => v == verb || v == "ANY" || verb == "ANY")
        && string.Equals(Normalize(n.Meta.GetValueOrDefault("route") as string ?? ""), route, StringComparison.OrdinalIgnoreCase);

    /// "api/orders/" and "/api/orders" are the same route.
    public static string Normalize(string route)
    {
        var r = route.Trim();
        if (!r.StartsWith('/')) r = "/" + r;
        return r.Length > 1 ? r.TrimEnd('/') : r;
    }

    /// The endpoint a request went to: by the route the app matched, or by fitting the path to the routes.
    public static string? EndpointFor(Graph g, string method, string path, string? route)
    {
        var endpoints = g.Nodes.Values.Where(n => n.Kind == "endpoint").ToList();
        if (route is not null)
        {
            var normalized = Normalize(route);
            var exact = endpoints.FirstOrDefault(n => Same(n, method, normalized));
            if (exact is not null) return exact.Id;
        }

        return endpoints
            .Where(n => Same(n, method, Normalize(n.Meta.GetValueOrDefault("route") as string ?? "")) && Template(n.Meta.GetValueOrDefault("route") as string ?? "").IsMatch(path))
            .OrderByDescending(n => (n.Meta.GetValueOrDefault("route") as string ?? "").Count(c => c == '/'))
            .ThenBy(n => (n.Meta.GetValueOrDefault("route") as string ?? "").Count(c => c == '{'))
            .FirstOrDefault()?.Id;
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> Templates = new();

    /// "/orders/{id:int}/{*rest}" as a pattern for request paths: a value stands for one path segment,
    /// an optional value for one or none, and a catch-all for the rest.
    static Regex Template(string route) => Templates.GetOrAdd(route, r =>
    {
        var text = Normalize(r);
        var sb = new System.Text.StringBuilder("^");
        for (var i = 0; i < text.Length;)
        {
            var open = text.IndexOf('{', i);
            var close = open < 0 ? -1 : text.IndexOf('}', open);
            if (open < 0 || close < 0)
            {
                sb.Append(Regex.Escape(text[i..]));
                break;
            }
            sb.Append(Regex.Escape(text[i..open]));
            var inner = text[(open + 1)..close];
            sb.Append(inner.StartsWith('*') ? ".*" : inner.EndsWith('?') ? "[^/]*" : "[^/]+");
            i = close + 1;
        }
        return new Regex(sb.Append("/?$").ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    });
}

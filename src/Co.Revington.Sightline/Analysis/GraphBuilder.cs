namespace Co.Revington.Sightline.Analysis;

/// Turns per-file facts into one graph: nodes, edges and the DI service list.
internal static class GraphBuilder
{
    public static Graph Build(IReadOnlyList<ProjectFacts> projects, IEnumerable<DocFacts> docFacts)
    {
        var g = new Graph();
        var docs = docFacts
            .OrderBy(d => d.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Project, StringComparer.Ordinal)
            .ToList();

        // DI registrations first: they label types and decide what counts as an injection.
        var services = new HashSet<string>(StringComparer.Ordinal);
        var implementations = new Dictionary<string, (string Lifetime, List<string> As)>(StringComparer.Ordinal);
        foreach (var d in docs)
        foreach (var r in d.Di)
        {
            if (r.ServiceId is not null) services.Add(r.ServiceId);
            if (r.ImplId is null) continue;
            if (!implementations.TryGetValue(r.ImplId, out var entry)) implementations[r.ImplId] = entry = (r.Lifetime, []);
            if (r.ServiceId != r.ImplId && !entry.As.Contains(r.ServiceName)) entry.As.Add(r.ServiceName);
        }

        AddProjects(g, projects);
        AddTypes(g, docs, services, implementations);
        AddMembers(g, docs);

        var handlers = docs.SelectMany(d => d.Handlers)
            .GroupBy(h => h.MessageTypeId)
            .ToDictionary(x => x.Key, x => x.Select(h => h.HandlerMemberId).Distinct().ToList());

        var endpoints = AddEndpoints(g, docs);
        AddRefs(g, docs, endpoints, handlers, services, implementations);
        AddTypeLinks(g, docs);
        AddServices(g, docs);
        return g;
    }

    static void AddProjects(Graph g, IReadOnlyList<ProjectFacts> projects)
    {
        var packages = new SortedDictionary<string, (SortedSet<string> Versions, int UsedBy)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in projects)
        foreach (var k in p.Packages.Where(k => k.Direct))
        {
            if (!packages.TryGetValue(k.Name, out var entry)) entry = ([], 0);
            if (k.Version.Length > 0) entry.Versions.Add(k.Version);
            packages[k.Name] = (entry.Versions, entry.UsedBy + 1);
        }

        foreach (var (name, entry) in packages)
            g.Add(new Node
            {
                Id = "pkg:" + name, Kind = "package", Name = name,
                Meta = { ["versions"] = entry.Versions.ToList(), ["usedBy"] = entry.UsedBy },
            });

        foreach (var p in projects)
        {
            var meta = new Dictionary<string, object>
            {
                ["projectKind"] = p.Kind,
                ["direct"] = p.Packages.Count(k => k.Direct),
                ["transitive"] = p.Packages.Count(k => !k.Direct),
            };
            if (p.Tfm is not null) meta["tfm"] = p.Tfm;
            g.Add(new Node { Id = p.Id, Kind = "project", Name = p.Name, File = p.Path, Meta = meta });
        }

        foreach (var p in projects)
        {
            foreach (var r in p.ProjectRefs) g.Link("projectRef", p.Id, r);
            foreach (var k in p.Packages.Where(k => k.Direct)) g.Link("packageRef", p.Id, "pkg:" + k.Name, k.Version);
        }
    }

    static void AddTypes(Graph g, List<DocFacts> docs, HashSet<string> services,
        Dictionary<string, (string Lifetime, List<string> As)> implementations)
    {
        // A partial class has one fact per file; merge them into one node.
        foreach (var group in docs.SelectMany(d => d.Types.Select(t => (Doc: d, Type: t))).GroupBy(x => x.Type.Id))
        {
            var parts = group.ToList();
            var (doc, t) = parts[0];

            var meta = new Dictionary<string, object> { ["typeKind"] = t.Kind, ["access"] = t.Access };
            var tags = parts.SelectMany(p => p.Type.Tags).Distinct().ToList();
            if (tags.Count > 0) meta["tags"] = tags;
            if (parts.Select(p => p.Type.BaseName).FirstOrDefault(b => b is not null) is { } baseName) meta["base"] = baseName;
            var external = parts.SelectMany(p => p.Type.ExternalInterfaces).Distinct().ToList();
            if (external.Count > 0) meta["externalInterfaces"] = external;
            if (t.ContainerId is not null) meta["container"] = t.ContainerId;
            if (implementations.TryGetValue(t.Id, out var reg))
            {
                meta["lifetime"] = reg.Lifetime;
                if (reg.As.Count > 0) meta["registeredAs"] = reg.As;
            }
            else if (services.Contains(t.Id))
            {
                meta["service"] = true;
            }
            if (parts.Count > 1) meta["parts"] = parts.Select(p => p.Doc.File).Distinct().ToList();

            g.Add(new Node
            {
                Id = t.Id, Kind = "type", Name = t.Name, Parent = doc.Project, Project = doc.Project,
                Ns = t.Ns, File = doc.File, Line = t.Line, EndLine = t.EndLine, Meta = meta,
            });
        }
    }

    static void AddMembers(Graph g, List<DocFacts> docs)
    {
        foreach (var d in docs)
        foreach (var m in d.Members)
        {
            if (g.Has(m.Id) || !g.Has(m.TypeId)) continue;

            var meta = new Dictionary<string, object> { ["memberKind"] = m.Kind, ["sig"] = m.Signature, ["access"] = m.Access };
            if (m.Returns is not null) meta["returns"] = m.Returns;
            if (m.IsStatic) meta["static"] = true;
            if (m.IsAbstract) meta["abstract"] = true;
            if (m.IsAsync) meta["async"] = true;

            g.Add(new Node
            {
                Id = m.Id, Kind = "member", Name = m.Name, Parent = m.TypeId, Project = d.Project,
                File = d.File, Line = m.Line, EndLine = m.EndLine, Meta = meta,
            });
        }
    }

    /// Creates endpoint nodes. Returns fact key -> node ids (one endpoint can be mapped under several prefixes).
    static Dictionary<string, List<string>> AddEndpoints(Graph g, List<DocFacts> docs)
    {
        var passes = docs.SelectMany(d => d.GroupPasses)
            .GroupBy(p => (p.MethodId, p.Ordinal))
            .ToDictionary(x => x.Key, x => x.ToList());

        var byKey = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var d in docs)
        foreach (var ep in d.Endpoints)
        {
            var ids = new List<string>();
            var projectName = d.Project["proj:".Length..];

            foreach (var prefix in Prefixes(ep.Param, passes, 0).Distinct())
            {
                var route = ep.Style is "controller" or "grpc" ? ep.Pattern : Routes.Join(prefix, ep.Prefix, ep.Pattern);
                var baseId = $"ep:{projectName}:{ep.Verb} {route}";
                var id = baseId;
                for (var i = 2; g.Has(id); i++) id = $"{baseId} #{i}";

                var meta = new Dictionary<string, object> { ["verb"] = ep.Verb, ["route"] = route, ["style"] = ep.Style };
                if (ep.Auth) meta["auth"] = true;
                if (ep.RouteName is not null) meta["routeName"] = ep.RouteName;
                if (ep.HandlerId is not null && g.Has(ep.HandlerId)) meta["handler"] = ep.HandlerId;
                if (ep.TargetTypeId is not null && g.Has(ep.TargetTypeId)) meta["target"] = ep.TargetTypeId;
                if (g.Has(ep.Owner)) meta["declaredIn"] = ep.Owner;

                g.Add(new Node
                {
                    Id = id, Kind = "endpoint", Name = $"{ep.Verb} {route}", Parent = d.Project, Project = d.Project,
                    File = d.File, Line = ep.Line, EndLine = ep.EndLine, Meta = meta,
                });

                if (ep.HandlerId is not null && g.Has(ep.HandlerId)) g.Link("handles", id, ep.HandlerId, file: d.File, line: ep.Line);
                if (ep.TargetTypeId is not null && g.Has(ep.TargetTypeId)) g.Link("handles", id, ep.TargetTypeId, file: d.File, line: ep.Line);
                ids.Add(id);
            }
            byKey[ep.Key] = ids;
        }
        return byKey;
    }

    /// All route prefixes that reach a group parameter, following callers upward.
    static IEnumerable<string> Prefixes(ParamRef? param, Dictionary<(string, int), List<GroupPass>> passes, int depth)
    {
        if (param is null || depth > 8 || !passes.TryGetValue((param.MethodId, param.Ordinal), out var list))
        {
            yield return "";
            yield break;
        }

        foreach (var pass in list)
        foreach (var outer in Prefixes(pass.Param, passes, depth + 1))
            yield return Routes.Join(outer, pass.Prefix);
    }

    static void AddRefs(Graph g, List<DocFacts> docs, Dictionary<string, List<string>> endpoints,
        Dictionary<string, List<string>> handlers, HashSet<string> services,
        Dictionary<string, (string Lifetime, List<string> As)> implementations)
    {
        bool Injectable(string typeId)
        {
            if (services.Contains(typeId) || implementations.ContainsKey(typeId)) return true;
            if (!g.Nodes.TryGetValue(typeId, out var n)) return false;
            if (n.Meta.GetValueOrDefault("typeKind") as string == "interface") return true;
            return n.Meta.GetValueOrDefault("tags") is List<string> tags && (tags.Contains("abstract") || tags.Contains("dbcontext"));
        }

        foreach (var d in docs)
        foreach (var r in d.Refs)
        {
            // The source is a member, a type, or an endpoint key (lambda handlers).
            var sources = endpoints.TryGetValue(r.From, out var ids) ? ids : g.Has(r.From) ? [r.From] : [];

            foreach (var from in sources)
            {
                switch (r.Kind)
                {
                    case "calls":
                        if (r.Via is not null && handlers.ContainsKey(r.Via)) break;     // shown as a dispatch edge instead
                        if (g.Has(r.To)) g.Link("calls", from, r.To, file: d.File, line: r.Line);
                        break;

                    case "dispatch":
                        if (!handlers.TryGetValue(r.To, out var targets)) break;
                        foreach (var h in targets)
                            if (g.Has(h)) g.Link("dispatches", from, h, r.Label, d.File, r.Line);
                        break;

                    case "creates":
                        if (g.Has(r.To)) g.Link("creates", from, r.To, file: d.File, line: r.Line);
                        break;

                    case "db":
                        var db = g.Has(r.To) ? r.To : External(g, "ext:db", "Database", "db");
                        g.Link("db", from, db, r.Label, d.File, r.Line);
                        break;

                    case "http":
                        g.Link("http", from, External(g, "ext:http", "HTTP (outbound)", "http"), r.Label, d.File, r.Line);
                        break;

                    case "references":
                        if (g.Has(r.To)) g.Link("references", from, r.To, file: d.File, line: r.Line);
                        break;

                    case "ctor-param":
                        if (g.Has(r.To)) g.Link(Injectable(r.To) ? "injects" : "references", from, r.To, file: d.File, line: r.Line);
                        break;
                }
            }
        }

        // Interface method -> implementation, base method -> override.
        foreach (var d in docs)
        foreach (var i in d.Impls)
            if (g.Has(i.AbstractId) && g.Has(i.ImplId)) g.Link("impl", i.AbstractId, i.ImplId, i.Kind);
    }

    static string External(Graph g, string id, string name, string type)
    {
        g.Add(new Node { Id = id, Kind = "external", Name = name, Meta = { ["externalKind"] = type } });
        return id;
    }

    static void AddTypeLinks(Graph g, List<DocFacts> docs)
    {
        foreach (var d in docs)
        foreach (var t in d.Types)
        {
            if (t.BaseId is not null && g.Has(t.BaseId)) g.Link("inherits", t.Id, t.BaseId, file: d.File, line: t.Line);
            foreach (var i in t.InterfaceIds)
                if (g.Has(i)) g.Link("implements", t.Id, i, file: d.File, line: t.Line);
        }
    }

    static void AddServices(Graph g, List<DocFacts> docs)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var d in docs)
        foreach (var r in d.Di)
        {
            var id = $"svc:{r.ServiceId ?? r.ServiceName}|{r.ImplId ?? r.ImplName}|{r.Method}";
            if (!seen.Add(id)) continue;

            g.Services.Add(new ServiceEntry(
                id,
                r.ServiceId is not null && g.Has(r.ServiceId) ? r.ServiceId : null,
                r.ServiceName,
                r.ImplId is not null && g.Has(r.ImplId) ? r.ImplId : null,
                r.ImplName,
                r.Lifetime, r.Method,
                g.Has(r.Owner) ? r.Owner : null,
                d.File, r.Line));

            if (r.ServiceId is not null && r.ImplId is not null && r.ServiceId != r.ImplId && g.Has(r.ServiceId) && g.Has(r.ImplId))
                g.Link("registers", r.ServiceId, r.ImplId, r.Lifetime, d.File, r.Line);
        }
    }
}

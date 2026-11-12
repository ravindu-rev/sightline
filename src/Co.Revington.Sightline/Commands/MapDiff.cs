using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Co.Revington.Sightline.Settings;

namespace Co.Revington.Sightline.Commands;

/// "sightline diff": what a change does to the map. Compares two maps by their stable ids and
/// reports endpoints, request flows, services, project references, packages and problems.
internal static class MapDiff
{
    public const string Marker = "<!-- sightline-map-diff -->";

    static readonly HashSet<string> FlowKinds = ["handles", "calls", "dispatches", "impl", "db", "http"];

    public sealed record Change(string Sign, string What, string? Detail = null);

    public sealed record FlowChange(string Endpoint, List<string> Added, List<string> Removed);

    public sealed record Result(
        List<Change> Endpoints, List<FlowChange> Flows, List<Change> Services, List<Change> Types,
        List<Change> References, List<JsonObject> NewProblems, List<JsonObject> FixedProblems, int BaseNodes, int HeadNodes)
    {
        public bool Empty => Endpoints.Count + Flows.Count + Services.Count + Types.Count + References.Count + NewProblems.Count + FixedProblems.Count == 0;
    }

    public static async Task<int> RunAsync(DiffCommand command)
    {
        JsonNode baseGraph, headGraph;
        try
        {
            if (command.BaseFile is not null && command.HeadFile is not null)
            {
                baseGraph = JsonNode.Parse(await File.ReadAllTextAsync(command.BaseFile))!;
                headGraph = JsonNode.Parse(await File.ReadAllTextAsync(command.HeadFile))!;
            }
            else
            {
                Console.Error.WriteLine("Reading the working tree...");
                headGraph = await GraphAsync(command.Target!);
                Console.Error.WriteLine($"Reading {command.Base}...");
                baseGraph = await AtCommitAsync(command.Target!, command.Base!);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or JsonException)
        {
            Console.Error.WriteLine("Sightline could not compare the maps: " + ex.Message);
            return 2;
        }

        var result = Compare(baseGraph, headGraph);
        var output = command.Format switch
        {
            "json" => JsonSerializer.Serialize(result, new JsonSerializerOptions(Analysis.Snapshot.Json) { WriteIndented = true }),
            "text" => Text(result),
            _ => Markdown(result),
        };

        if (command.Out is { } file)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, output);
        }
        else
        {
            Console.WriteLine(output);
        }

        var limit = command.FailOn switch { "error" => 0, "warning" => 1, _ => -1 };
        var rank = new Dictionary<string, int> { ["error"] = 0, ["warning"] = 1, ["info"] = 2 };
        return result.NewProblems.Any(p => rank.GetValueOrDefault(p["severity"]?.GetValue<string>() ?? "", 9) <= limit) ? 1 : 0;
    }

    static async Task<JsonNode> GraphAsync(SolutionTarget target)
    {
        var (session, snapshot) = await OneShot.LoadAsync(target);
        return Exporter.GraphJson(snapshot, session, new PathScrubber(OneShot.RepoRoot(session.RootDir)));
    }

    /// The map at another commit: checked out next to the repository with "git worktree", read, removed.
    static async Task<JsonNode> AtCommitAsync(SolutionTarget head, string reference)
    {
        var start = Directory.Exists(head.Path) ? head.Path : Path.GetDirectoryName(head.Path)!;
        var repo = OneShot.RepoRoot(start);
        if (!Directory.Exists(Path.Combine(repo, ".git")) && !File.Exists(Path.Combine(repo, ".git")))
            throw new InvalidOperationException("--base needs the solution to be in a git repository.");

        var temp = Path.Combine(Path.GetTempPath(), "sightline-base-" + Guid.NewGuid().ToString("N")[..8]);
        var added = await Git(repo, "worktree", "add", "--detach", temp, reference);
        if (added.Code != 0) throw new InvalidOperationException($"git could not check out {reference}: {DotnetCli.Tail(added.All, 3)}");

        try
        {
            var path = Path.Combine(temp, Path.GetRelativePath(repo, head.Path));
            if (!File.Exists(path) && !Directory.Exists(path))
                throw new InvalidOperationException($"{Path.GetRelativePath(repo, head.Path)} does not exist at {reference}.");

            SightlineConfig? config = null;
            if (SightlineConfig.Find(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!) is { } file && file.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
            {
                try { config = SightlineConfig.Load(file); }
                catch (ConfigException) { /* the old settings were broken; read without them */ }
            }

            var target = new SolutionTarget(path, config,
                config is null ? ProjectFilter.None : new ProjectFilter(config.Dir, config.Exclude), head.Restore);
            var (session, snapshot) = await OneShot.LoadAsync(target);
            return Exporter.GraphJson(snapshot, session, new PathScrubber(temp));
        }
        finally
        {
            await Git(repo, "worktree", "remove", "--force", temp);
        }
    }

    static async Task<DotnetResult> Git(string dir, params string[] args)
    {
        var info = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = System.Diagnostics.Process.Start(info) ?? throw new InvalidOperationException("git is not installed.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new DotnetResult(process.ExitCode, await output, await errors);
    }

    // ---------------------------------------------------------------- comparing

    sealed class Map
    {
        public Dictionary<string, JsonObject> Nodes { get; } = new(StringComparer.Ordinal);
        public List<JsonObject> Edges { get; } = [];
        public Dictionary<string, List<JsonObject>> Out { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, JsonObject> Services { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, JsonObject> Problems { get; } = new(StringComparer.Ordinal);

        public Map(JsonNode graph)
        {
            foreach (var n in graph["nodes"]?.AsArray().OfType<JsonObject>() ?? []) Nodes[S(n, "id")] = n;
            foreach (var e in graph["edges"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                Edges.Add(e);
                if (!Out.TryGetValue(S(e, "source"), out var list)) Out[S(e, "source")] = list = [];
                list.Add(e);
            }
            foreach (var s in graph["services"]?.AsArray().OfType<JsonObject>() ?? []) Services[S(s, "id")] = s;
            foreach (var p in graph["problems"]?.AsArray().OfType<JsonObject>() ?? []) Problems[S(p, "id")] = p;
        }

        public string Label(string id)
        {
            if (!Nodes.TryGetValue(id, out var n)) return id;
            return S(n, "kind") == "member" && n["parent"] is not null && Nodes.TryGetValue(S(n, "parent"), out var owner)
                ? $"{S(owner, "name")}.{S(n, "name")}"
                : S(n, "name");
        }

        /// Everything a request reaches from an endpoint: handlers, calls, implementations, database, outbound HTTP.
        public SortedSet<string> Reach(string start)
        {
            var seen = new HashSet<string> { start };
            var queue = new Queue<string>([start]);
            while (queue.TryDequeue(out var id))
                foreach (var e in Out.GetValueOrDefault(id) ?? [])
                    if (FlowKinds.Contains(S(e, "kind")) && seen.Add(S(e, "target"))) queue.Enqueue(S(e, "target"));
            seen.Remove(start);
            return new SortedSet<string>(seen.Select(Label), StringComparer.Ordinal);
        }
    }

    static string S(JsonNode? node, string key) => node?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    public static Result Compare(JsonNode baseGraph, JsonNode headGraph)
    {
        var a = new Map(baseGraph);
        var b = new Map(headGraph);

        List<string> Ids(Map m, string kind) => m.Nodes.Values.Where(n => S(n, "kind") == kind).Select(n => S(n, "id")).ToList();

        // Endpoints
        var endpoints = new List<Change>();
        foreach (var id in Ids(b, "endpoint").Except(Ids(a, "endpoint")).Order()) endpoints.Add(new Change("+", S(b.Nodes[id], "name"), Where(b.Nodes[id])));
        foreach (var id in Ids(a, "endpoint").Except(Ids(b, "endpoint")).Order()) endpoints.Add(new Change("-", S(a.Nodes[id], "name")));

        // Request flows of endpoints that exist on both sides
        var flows = new List<FlowChange>();
        foreach (var id in Ids(b, "endpoint").Intersect(Ids(a, "endpoint")).Order())
        {
            var before = a.Reach(id);
            var after = b.Reach(id);
            var added = after.Except(before).ToList();
            var removed = before.Except(after).ToList();
            if (added.Count + removed.Count > 0) flows.Add(new FlowChange(S(b.Nodes[id], "name"), added, removed));
        }

        // Dependency injection
        string Service(JsonObject s) => S(s, "impl") is { Length: > 0 } impl && impl != S(s, "service") ? $"{S(s, "service")} → {impl}" : S(s, "service");
        var services = new List<Change>();
        var pairs = (Map m) => m.Services.Values.GroupBy(s => (S(s, "service"), S(s, "impl"))).ToDictionary(g => g.Key, g => g.First());
        var beforeServices = pairs(a);
        var afterServices = pairs(b);
        foreach (var (key, s) in afterServices.OrderBy(x => x.Key.Item1))
        {
            if (!beforeServices.TryGetValue(key, out var old)) services.Add(new Change("+", Service(s), S(s, "lifetime")));
            else if (S(old, "lifetime") != S(s, "lifetime")) services.Add(new Change("~", Service(s), $"{S(old, "lifetime")} → {S(s, "lifetime")}"));
        }
        foreach (var (key, s) in beforeServices.OrderBy(x => x.Key.Item1))
            if (!afterServices.ContainsKey(key)) services.Add(new Change("-", Service(s), S(s, "lifetime")));

        // Types
        var types = new List<Change>();
        foreach (var id in Ids(b, "type").Except(Ids(a, "type")).OrderBy(id => S(b.Nodes[id], "name")))
            types.Add(new Change("+", S(b.Nodes[id], "name"), S(b.Nodes[id], "ns")));
        foreach (var id in Ids(a, "type").Except(Ids(b, "type")).OrderBy(id => S(a.Nodes[id], "name")))
            types.Add(new Change("-", S(a.Nodes[id], "name"), S(a.Nodes[id], "ns")));

        // Project and package references
        var references = new List<Change>();
        Dictionary<string, string> Refs(Map m) => m.Edges.Where(e => S(e, "kind") is "projectRef" or "packageRef")
            .GroupBy(e => $"{m.Label(S(e, "source"))} → {m.Label(S(e, "target"))}")
            .ToDictionary(g => g.Key, g => S(g.First(), "label"));
        var beforeRefs = Refs(a);
        var afterRefs = Refs(b);
        foreach (var (key, version) in afterRefs.OrderBy(x => x.Key))
        {
            if (!beforeRefs.TryGetValue(key, out var old)) references.Add(new Change("+", key, version));
            else if (old != version) references.Add(new Change("~", key, $"{old} → {version}"));
        }
        foreach (var key in beforeRefs.Keys.Except(afterRefs.Keys).Order()) references.Add(new Change("-", key, beforeRefs[key]));

        var newProblems = b.Problems.Where(p => !a.Problems.ContainsKey(p.Key)).Select(p => p.Value).ToList();
        var fixedProblems = a.Problems.Where(p => !b.Problems.ContainsKey(p.Key)).Select(p => p.Value).ToList();

        return new Result(endpoints, flows, services, types, references, newProblems, fixedProblems, a.Nodes.Count, b.Nodes.Count);
    }

    static string? Where(JsonObject n) => S(n, "file") is { Length: > 0 } file ? $"{file}:{n["line"]}" : null;

    // ---------------------------------------------------------------- output

    static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    static string Headline(Result r)
    {
        var parts = new List<string>();
        int Count(List<Change> list, string sign) => list.Count(c => c.Sign == sign);
        if (Count(r.Endpoints, "+") > 0) parts.Add(Plural(Count(r.Endpoints, "+"), "endpoint") + " added");
        if (Count(r.Endpoints, "-") > 0) parts.Add(Plural(Count(r.Endpoints, "-"), "endpoint") + " removed");
        if (r.Flows.Count > 0) parts.Add(Plural(r.Flows.Count, "request flow") + " changed");
        if (r.Services.Count > 0) parts.Add(Plural(r.Services.Count, "service registration") + " changed");
        if (r.References.Count > 0) parts.Add(Plural(r.References.Count, "reference") + " changed");
        if (r.Types.Count > 0) parts.Add($"{Count(r.Types, "+")} types added, {Count(r.Types, "-")} removed");
        if (r.NewProblems.Count > 0) parts.Add(Plural(r.NewProblems.Count, "new problem"));
        if (r.FixedProblems.Count > 0) parts.Add(Plural(r.FixedProblems.Count, "problem") + " fixed");
        return parts.Count == 0 ? "No changes to endpoints, request flows, services or references." : string.Join(" · ", parts);
    }

    public static string Markdown(Result r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Marker);
        sb.AppendLine("### Sightline: what this change does to the map");
        sb.AppendLine();
        sb.AppendLine($"**{Headline(r)}**");

        string Code(string text) => "`" + text.Replace("`", "'") + "`";
        string Sign(string sign) => sign switch { "+" => "➕", "-" => "➖", _ => "✏️" };

        if (r.NewProblems.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("#### New problems");
            foreach (var p in r.NewProblems)
            {
                var at = S(p, "file") is { Length: > 0 } f ? $" ({Code(f + (p["line"] is { } line ? ":" + line : ""))})" : "";
                sb.AppendLine($"- {CheckRunner.Icon(S(p, "severity"))} **{S(p, "title")}**{at}: {S(p, "message")}");
            }
        }

        if (r.Endpoints.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("#### Endpoints");
            foreach (var c in r.Endpoints) sb.AppendLine($"- {Sign(c.Sign)} {Code(c.What)}{(c.Detail is null ? "" : " in " + Code(c.Detail))}");
        }

        if (r.Flows.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("#### Request flows");
            foreach (var f in r.Flows)
            {
                var bits = new List<string>();
                if (f.Added.Count > 0) bits.Add("now also reaches " + string.Join(", ", f.Added.Take(6).Select(Code)) + (f.Added.Count > 6 ? $" and {f.Added.Count - 6} more" : ""));
                if (f.Removed.Count > 0) bits.Add("no longer reaches " + string.Join(", ", f.Removed.Take(6).Select(Code)) + (f.Removed.Count > 6 ? $" and {f.Removed.Count - 6} more" : ""));
                sb.AppendLine($"- {Code(f.Endpoint)} {string.Join("; ", bits)}");
            }
        }

        Section("Service registrations", r.Services);
        Section("Project and package references", r.References);
        if (r.Types.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"<details><summary>Types ({r.Types.Count(t => t.Sign == "+")} added, {r.Types.Count(t => t.Sign == "-")} removed)</summary>");
            sb.AppendLine();
            foreach (var c in r.Types.Take(60)) sb.AppendLine($"- {Sign(c.Sign)} {Code(c.What)}{(string.IsNullOrEmpty(c.Detail) ? "" : " in " + Code(c.Detail))}");
            if (r.Types.Count > 60) sb.AppendLine($"- and {r.Types.Count - 60} more");
            sb.AppendLine();
            sb.AppendLine("</details>");
        }

        if (r.FixedProblems.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"<details><summary>Problems fixed ({r.FixedProblems.Count})</summary>");
            sb.AppendLine();
            foreach (var p in r.FixedProblems) sb.AppendLine($"- ✅ {S(p, "title")}: {S(p, "message")}");
            sb.AppendLine();
            sb.AppendLine("</details>");
        }

        sb.AppendLine();
        sb.AppendLine($"<sub>Made with [Sightline](https://sightline.revington.co) {ToolInfo.Version}, a live map of .NET solutions.</sub>");
        return sb.ToString();

        void Section(string title, List<Change> changes)
        {
            if (changes.Count == 0) return;
            sb.AppendLine();
            sb.AppendLine("#### " + title);
            foreach (var c in changes) sb.AppendLine($"- {Sign(c.Sign)} {Code(c.What)}{(string.IsNullOrEmpty(c.Detail) ? "" : " " + c.Detail)}");
        }
    }

    public static string Text(Result r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Headline(r));
        foreach (var p in r.NewProblems) sb.AppendLine($"  ! {S(p, "severity")}: {S(p, "title")} ({S(p, "file")}:{p["line"]})");
        foreach (var c in r.Endpoints) sb.AppendLine($"  {c.Sign} endpoint {c.What}");
        foreach (var f in r.Flows)
        {
            foreach (var x in f.Added) sb.AppendLine($"  + {f.Endpoint} reaches {x}");
            foreach (var x in f.Removed) sb.AppendLine($"  - {f.Endpoint} reaches {x}");
        }
        foreach (var c in r.Services) sb.AppendLine($"  {c.Sign} service {c.What} {c.Detail}");
        foreach (var c in r.References) sb.AppendLine($"  {c.Sign} reference {c.What} {c.Detail}");
        foreach (var c in r.Types) sb.AppendLine($"  {c.Sign} type {c.What}");
        foreach (var p in r.FixedProblems) sb.AppendLine($"  fixed: {S(p, "title")}");
        return sb.ToString().TrimEnd();
    }
}

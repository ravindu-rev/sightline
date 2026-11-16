namespace Co.Revington.Sightline.Analysis;

/// Route templates of every framework Sightline reads: ASP.NET Core ("/orders/{id:int}", "{*rest}"),
/// Express and its kind ("/orders/:id", "*"), and Next.js folders ("/orders/[id]", "[...slug]").
/// Also the requests code sends, and which endpoint on the map such a request reaches.
internal static class Routes
{
    /// Joins route parts with single slashes and a leading slash.
    public static string Join(params string?[] parts)
    {
        var segments = new List<string>();
        foreach (var p in parts)
        {
            if (string.IsNullOrEmpty(p)) continue;
            var t = p.Trim().Trim('/');
            if (t.Length > 0) segments.Add(t);
        }
        return "/" + string.Join('/', segments);
    }

    /// Names of the values in a route: "/orders/{id:int}/{*rest}", "/orders/:id/*rest" and
    /// "/orders/[id]/[...rest]" all give id and rest.
    public static List<string> Names(string route)
    {
        var names = new List<string>();
        for (var open = route.IndexOf('{'); open >= 0; open = route.IndexOf('{', open + 1))
        {
            var close = route.IndexOf('}', open);
            if (close < 0) break;
            var inner = route[(open + 1)..close].TrimStart('*');
            var end = inner.IndexOfAny([':', '=', '?']);
            var name = end < 0 ? inner : inner[..end];
            if (name.Length > 0) names.Add(name);
        }

        foreach (var segment in route.Split('/'))
        {
            var name = segment.Length > 1 && segment[0] is ':' or '*' ? Identifier(segment[1..])
                : segment.Length > 2 && segment[0] == '[' && segment[^1] == ']' ? Identifier(segment.Trim('[', ']').TrimStart('.'))
                : "";
            if (name.Length > 0 && !names.Contains(name)) names.Add(name);
        }
        return names;
    }

    static string Identifier(string text)
    {
        var end = 0;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] is '_' or '$')) end++;
        return text[..end];
    }

    // ---------------------------------------------------------------- requests sent by code

    static readonly HashSet<string> LocalHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost", "127.0.0.1", "[::1]", "0.0.0.0", "host.docker.internal",
    };

    /// The request to an address as written in the code, with "*" for the parts worked out at run time:
    /// "https://localhost:5001/api/orders?page=2" gives /api/orders, "api/orders" (relative to an address
    /// set elsewhere) gives */api/orders. Null for an address on another host: it never reaches this code.
    public static HttpCall? Request(string verb, string url)
    {
        var u = url.Trim();
        var cut = u.IndexOfAny(['?', '#']);
        if (cut >= 0) u = u[..cut];
        if (u.Length == 0) return null;

        bool unknownStart;
        var scheme = u.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0 && !u[..scheme].Contains('/'))
        {
            var rest = u[(scheme + 3)..];
            var slash = rest.IndexOf('/');
            var host = slash < 0 ? rest : rest[..slash];
            var colon = host.LastIndexOf(':');
            if (colon > 0 && !host.EndsWith(']')) host = host[..colon];
            // A host worked out at run time ("*") may be this code; "*.stripe.com" or any other named host is not.
            // Hosts without a dot are the names services give each other (Docker Compose, Kubernetes).
            var local = host == "*" || LocalHosts.Contains(host) || (!host.Contains('.') && !host.Contains('*'));
            if (!local) return null;
            u = slash < 0 ? "/" : rest[slash..];
            unknownStart = false;
        }
        else if (u.StartsWith('*'))
        {
            u = u.TrimStart('*');
            unknownStart = true;
        }
        else if (u.StartsWith('/'))
        {
            unknownStart = false;
        }
        else
        {
            unknownStart = true;          // relative to an address set somewhere else (BaseAddress, baseURL)
        }

        var segments = u.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Contains('*') ? "*" : s).ToList();
        // Nothing fixed to compare with ("/" aside): every endpoint would fit as well as any other.
        if (!segments.Any(s => s != "*") && (unknownStart || segments.Count > 0)) return null;
        var path = (unknownStart ? "*/" : "/") + string.Join('/', segments);
        return new HttpCall(string.IsNullOrWhiteSpace(verb) ? "*" : verb.Trim().ToUpperInvariant(), path);
    }

    /// How well a request fits an endpoint: 0 not at all; otherwise higher is better. A request whose
    /// path covers the whole route beats one that only fits its end (possible when the start of the
    /// address is not known), and among those, more fixed words in common beat fewer.
    public static int Match(HttpCall call, string verbs, string route)
    {
        if (!VerbFits(call.Verb, verbs)) return 0;
        var routeParts = route.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (routeParts.Length > 0 && CatchAll(routeParts[0], out _)) return 0;   // a fallback takes every request; it says nothing

        var unknownStart = call.Path.StartsWith('*');
        var callParts = (unknownStart ? call.Path.TrimStart('*') : call.Path).Split('/', StringSplitOptions.RemoveEmptyEntries);

        var whole = Fits(callParts, routeParts, 0, 0);
        if (whole > 0) return 200 + whole;
        if (!unknownStart) return 0;
        var best = 0;
        for (var skip = 1; skip < routeParts.Length; skip++) best = Math.Max(best, Fits(callParts, routeParts, 0, skip));
        return best > 0 ? 100 + best : 0;
    }

    static bool VerbFits(string verb, string verbs)
    {
        if (verbs is "HUB" or "GRPC" || verbs.Length == 0) return false;
        if (verb == "*" || verbs == "ANY") return true;
        return verbs.Split(',').Any(v => v.Trim().Equals(verb, StringComparison.OrdinalIgnoreCase));
    }

    /// The fixed words a request has in common with a route when it fits the route from word ri on; 0 when it
    /// does not fit, or fits without a fixed word in common, which says too little to draw a line.
    static int Fits(string[] call, string[] route, int ci, int ri)
    {
        var words = 0;
        while (true)
        {
            if (ri == route.Length) return ci == call.Length ? words : 0;
            var r = route[ri];
            if (CatchAll(r, out var optional)) return optional || ci < call.Length ? words : 0;
            if (ci == call.Length) return ri == route.Length - 1 && Optional(r) ? words : 0;
            var c = call[ci];
            if (IsValue(r)) { }                                         // a value: any word fits
            else if (c == "*") return 0;                                 // a part worked out at run time is no fixed word
            else if (string.Equals(c, r, StringComparison.OrdinalIgnoreCase)) words++;
            else return 0;
            ci++;
            ri++;
        }
    }

    static bool IsValue(string segment) =>
        segment.Contains('{') || segment.StartsWith(':') || (segment.StartsWith('[') && segment.EndsWith(']'));

    static bool Optional(string segment) =>
        segment.EndsWith("?}", StringComparison.Ordinal) || (segment.StartsWith(':') && segment.EndsWith('?'));

    /// "{*rest}", "{**rest}", "*", "*rest", ":rest*" and "[[...rest]]" take any number of words, also none;
    /// "[...rest]" and ":rest+" at least one.
    static bool CatchAll(string segment, out bool optional)
    {
        optional = segment.StartsWith("{*", StringComparison.Ordinal) || segment.StartsWith('*') || segment.StartsWith("[[...", StringComparison.Ordinal)
                   || (segment.StartsWith(':') && segment.EndsWith('*')) || segment == "(.*)";
        return optional || segment.StartsWith("[...", StringComparison.Ordinal) || (segment.StartsWith(':') && segment.EndsWith('+'));
    }
}

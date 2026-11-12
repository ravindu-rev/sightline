using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;

namespace Co.Revington.Sightline.Analysis;

/// Works out what a request to an endpoint carries, from its handler's parameters, and makes up
/// sample values so the request can be tried without typing a body by hand.
/// The rules follow ASP.NET Core's parameter binding, simplified.
internal static class Requests
{
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, NewLine = "\n" };

    // Types the framework hands to a handler itself; they are never part of the request.
    static readonly HashSet<string> Injected =
    [
        "CancellationToken", "HttpContext", "HttpRequest", "HttpResponse", "ClaimsPrincipal", "Stream", "PipeReader",
        "IServiceProvider", "ILogger", "ILoggerFactory", "IConfiguration", "IFormFile", "IFormFileCollection", "IFormCollection",
    ];

    public static RequestShape Shape(IEnumerable<IParameterSymbol> parameters, string route)
    {
        var routeNames = RouteNames(route);
        var list = new List<RequestParam>();
        string? bodyType = null, bodySample = null;

        foreach (var p in parameters)
        {
            if (p.Type is not { } type || Injected.Contains(type.Name)) continue;
            if (Sym.HasAttr(p, "FromServices") || Sym.HasAttr(p, "FromKeyedServices") || Sym.HasAttr(p, "AsParameters")) continue;

            var name = AttributeName(p) ?? p.Name;
            var source =
                Sym.HasAttr(p, "FromRoute") ? "route"
                : Sym.HasAttr(p, "FromQuery") ? "query"
                : Sym.HasAttr(p, "FromHeader") ? "header"
                : Sym.HasAttr(p, "FromBody") || Sym.HasAttr(p, "FromForm") ? "body"
                : routeNames.Contains(p.Name) ? "route"
                : IsSimple(type) || IsSimpleList(type) ? "query"
                : type is INamedTypeSymbol { TypeKind: TypeKind.Interface } || type.IsAbstract ? null      // a service
                : "body";
            if (source is null) continue;

            if (source == "body")
            {
                if (bodyType is not null) continue;              // only one body per request
                bodyType = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                bodySample = Sample(type, p.Name, 0, []).ToJsonString(Pretty);
                continue;
            }

            list.Add(new RequestParam(name, source, type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                Sample(type, p.Name, 0, []).ToJsonString()));
        }

        // Route values that no parameter names still have to be filled in.
        foreach (var rest in routeNames.Where(r => !list.Any(p => p.In == "route" && string.Equals(p.Name, r, StringComparison.OrdinalIgnoreCase))))
            list.Add(new RequestParam(rest, "route", "string", "\"" + rest + "\""));

        return new RequestShape(list, bodyType, bodySample);
    }

    /// The same shape once the endpoint's full route is known: parameters named in a group prefix
    /// (MapGroup("/tenants/{tenant}")) come from the route, not the query string.
    public static RequestShape ForRoute(RequestShape shape, string route)
    {
        var names = RouteNames(route).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = shape.Params.Select(p => p.In == "query" && names.Contains(p.Name) ? p with { In = "route" } : p).ToList();
        foreach (var rest in names.Where(n => !list.Any(p => p.In == "route" && string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))))
            list.Add(new RequestParam(rest, "route", "string", "\"" + rest + "\""));
        return shape with { Params = list };
    }

    /// Names in a route template: "/orders/{id:int}/{*rest}" gives id and rest.
    public static List<string> RouteNames(string route)
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
        return names;
    }

    static string? AttributeName(IParameterSymbol p)
    {
        foreach (var a in p.GetAttributes())
        {
            if (Sym.AttrName(a) is not ("FromQuery" or "FromRoute" or "FromHeader" or "FromForm")) continue;
            foreach (var named in a.NamedArguments)
                if (named.Key == "Name" && named.Value.Value is string s && s.Length > 0) return s;
        }
        return null;
    }

    static ITypeSymbol Unwrap(ITypeSymbol t) =>
        t is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n ? n.TypeArguments[0] : t;

    static bool IsSimple(ITypeSymbol type)
    {
        var t = Unwrap(type);
        if (t.TypeKind == TypeKind.Enum) return true;
        if (t.SpecialType is >= SpecialType.System_Boolean and <= SpecialType.System_String or SpecialType.System_DateTime) return true;
        if (t.Name is "Guid" or "DateTimeOffset" or "DateOnly" or "TimeOnly" or "TimeSpan" or "Uri" or "Half" or "Int128" or "UInt128") return true;
        // Types with a static TryParse bind from the query string too.
        return t.GetMembers("TryParse").OfType<IMethodSymbol>().Any(m => m.IsStatic);
    }

    static bool IsSimpleList(ITypeSymbol t) =>
        t is IArrayTypeSymbol a ? IsSimple(a.ElementType)
        : ElementOf(t) is { } element && IsSimple(element) && t.SpecialType != SpecialType.System_String;

    static ITypeSymbol? ElementOf(ITypeSymbol t)
    {
        if (t is IArrayTypeSymbol a) return a.ElementType;
        if (t.SpecialType == SpecialType.System_String) return null;
        var enumerable = t is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Collections_Generic_IEnumerable_T } self
            ? self
            : t.AllInterfaces.FirstOrDefault(i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T);
        return enumerable?.TypeArguments[0];
    }

    /// The value type of a dictionary, when the type is one.
    static ITypeSymbol? DictionaryValueOf(ITypeSymbol t)
    {
        foreach (var i in t is INamedTypeSymbol n ? n.AllInterfaces.Prepend(n) : t.AllInterfaces)
            if (i.Name is "IDictionary" or "IReadOnlyDictionary" && i.TypeArguments.Length == 2) return i.TypeArguments[1];
        return null;
    }

    // ---------------------------------------------------------------- sample values

    static JsonNode Sample(ITypeSymbol type, string name, int depth, HashSet<ITypeSymbol> seen)
    {
        var t = Unwrap(type);
        var lower = name.ToLowerInvariant();

        switch (t.SpecialType)
        {
            case SpecialType.System_Boolean: return JsonValue.Create(true);
            case SpecialType.System_Char: return JsonValue.Create("a");
            case SpecialType.System_String: return JsonValue.Create(Text(lower));
            case SpecialType.System_Decimal or SpecialType.System_Double or SpecialType.System_Single:
                return JsonValue.Create(lower.Contains("price") || lower.Contains("amount") || lower.Contains("total") ? 19.99m : 1.5m);
            case >= SpecialType.System_SByte and <= SpecialType.System_UInt64:
                return JsonValue.Create(Whole(lower));
            case SpecialType.System_DateTime: return JsonValue.Create("2026-01-01T12:00:00Z");
            case SpecialType.System_Object: return new JsonObject();
        }

        if (t.TypeKind == TypeKind.Enum)
        {
            var first = t.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => f.HasConstantValue);
            return JsonValue.Create(Convert.ToInt64(first?.ConstantValue ?? 0));
        }

        switch (t.Name)
        {
            case "Guid": return JsonValue.Create("3fa85f64-5717-4562-b3fc-2c963f66afa6");
            case "DateTimeOffset": return JsonValue.Create("2026-01-01T12:00:00+00:00");
            case "DateOnly": return JsonValue.Create("2026-01-01");
            case "TimeOnly": return JsonValue.Create("12:00:00");
            case "TimeSpan": return JsonValue.Create("00:30:00");
            case "Uri": return JsonValue.Create("https://example.com");
            case "JsonElement" or "JsonNode" or "JsonObject" or "JsonDocument": return new JsonObject();
        }

        if (depth > 4 || !seen.Add(t.OriginalDefinition)) return new JsonObject();
        try
        {
            if (DictionaryValueOf(t) is { } value)
                return new JsonObject { ["key"] = Sample(value, "value", depth + 1, seen) };
            if (ElementOf(t) is { } element)
                return new JsonArray(Sample(element, Singular(name), depth + 1, seen));
            return Object(t, depth, seen);
        }
        finally
        {
            seen.Remove(t.OriginalDefinition);
        }
    }

    /// Settable properties, and the parameters of a positional record, named the way JSON writes them.
    static JsonObject Object(ITypeSymbol t, int depth, HashSet<ITypeSymbol> seen)
    {
        var result = new JsonObject();
        if (t is not INamedTypeSymbol named || named.TypeKind is TypeKind.Interface) return result;

        var ctorNames = named.InstanceConstructors
            .Where(c => c.DeclaredAccessibility == Accessibility.Public)
            .OrderByDescending(c => c.Parameters.Length)
            .FirstOrDefault()?.Parameters.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        var properties = new List<IPropertySymbol>();
        for (var type = named; type is not null && type.SpecialType != SpecialType.System_Object; type = type.BaseType)
            properties.InsertRange(0, type.GetMembers().OfType<IPropertySymbol>());

        foreach (var p in properties)
        {
            if (p.IsStatic || p.IsIndexer || p.DeclaredAccessibility != Accessibility.Public || p.GetMethod is null) continue;
            if (Sym.HasAttr(p, "JsonIgnore")) continue;
            var settable = p.SetMethod is { DeclaredAccessibility: Accessibility.Public } || ctorNames.Contains(p.Name);
            if (!settable) continue;

            var jsonName = p.GetAttributes().FirstOrDefault(a => Sym.AttrName(a) == "JsonPropertyName")
                ?.ConstructorArguments.FirstOrDefault().Value as string ?? JsonNamingPolicy.CamelCase.ConvertName(p.Name);
            if (result.ContainsKey(jsonName)) continue;
            result[jsonName] = Sample(p.Type, p.Name, depth + 1, seen);
        }
        return result;
    }

    static string Singular(string name) => name.EndsWith('s') && name.Length > 1 ? name[..^1] : name;

    static long Whole(string name) =>
        name.Contains("pagesize") || name.Contains("limit") || name.Contains("take") ? 10
        : name.Contains("year") ? 2026
        : name.Contains("age") ? 30
        : 1;

    static string Text(string name)
    {
        (string Part, string Value)[] samples =
        [
            ("email", "user@example.com"), ("firstname", "Ada"), ("lastname", "Lovelace"), ("username", "ada"),
            ("password", "P@ssw0rd!"), ("phone", "+1 555 0100"), ("url", "https://example.com"), ("uri", "https://example.com"),
            ("city", "Toronto"), ("country", "CA"), ("currency", "USD"), ("slug", "sample-slug"), ("sku", "ABC-123"),
            ("code", "ABC-123"), ("title", "Sample title"), ("description", "Sample text"), ("note", "Sample text"),
            ("comment", "Sample text"), ("name", "Sample name"), ("status", "active"), ("token", "token"), ("id", "abc123"),
        ];
        foreach (var (part, value) in samples)
            if (name.Contains(part, StringComparison.Ordinal)) return value;
        return "string";
    }
}

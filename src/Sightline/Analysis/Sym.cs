using Microsoft.CodeAnalysis;

namespace Sightline.Analysis;

internal static class Sym
{
    /// True when the symbol is declared in the loaded solution (not a NuGet or framework type).
    public static bool IsSource(ISymbol? s) => s is not null && s.Locations.Any(l => l.IsInSource);

    /// Stable id: assembly name + documentation id of the definition.
    public static string? Id(ISymbol? s)
    {
        if (s is null) return null;
        if (s is IMethodSymbol { ReducedFrom: { } reduced }) s = reduced;
        s = s.OriginalDefinition;
        if (s is IErrorTypeSymbol || s.ContainingAssembly is null) return null;
        var id = s.GetDocumentationCommentId();
        return string.IsNullOrEmpty(id) ? null : s.ContainingAssembly.Name + "::" + id;
    }

    public static bool InheritsNamed(INamedTypeSymbol? t, string name)
    {
        for (var b = t?.BaseType; b is not null; b = b.BaseType)
            if (b.Name == name) return true;
        return false;
    }

    public static bool IsOrInheritsNamed(INamedTypeSymbol? t, string name) =>
        t is not null && (t.Name == name || InheritsNamed(t, name));

    /// Solution types mentioned by a type, looking inside generics and arrays.
    public static IEnumerable<INamedTypeSymbol> SourceTypesIn(ITypeSymbol? t)
    {
        switch (t)
        {
            case IArrayTypeSymbol a:
                foreach (var x in SourceTypesIn(a.ElementType)) yield return x;
                break;
            case INamedTypeSymbol n:
                foreach (var arg in n.TypeArguments)
                    foreach (var x in SourceTypesIn(arg)) yield return x;
                if (n.TypeKind != TypeKind.Error && IsSource(n)) yield return n.OriginalDefinition;
                break;
        }
    }

    public static string AttrName(AttributeData a)
    {
        var n = a.AttributeClass?.Name ?? "";
        return n.EndsWith("Attribute", StringComparison.Ordinal) ? n[..^9] : n;
    }

    public static bool HasAttr(ISymbol s, string name) => s.GetAttributes().Any(a => AttrName(a) == name);

    public static bool HasAttrInherited(INamedTypeSymbol t, string name)
    {
        for (INamedTypeSymbol? b = t; b is not null; b = b.BaseType)
            if (HasAttr(b, name)) return true;
        return false;
    }
}

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
}

namespace Co.Revington.Sightline.Analysis;

// Raw findings from one source file. The graph is assembled from these. Every language that Sightline
// reads produces them: C# through Roslyn (Extractor), TypeScript and JavaScript through the TypeScript
// compiler (analyzers/typescript), so everything after this point works the same for all of them.

/// Ecosystem: "nuget" or "npm". Direct: listed in the project file itself rather than pulled in by another package.
internal sealed record PackageRef(string Name, string Version, bool Direct, string Ecosystem = "nuget");

internal sealed class ProjectFacts
{
    public required string Id { get; init; }          // graph node id: "proj:Name" for .NET, "proj:npm:name" for npm packages
    public required string Name { get; init; }
    public string AssemblyName { get; init; } = "";    // .NET only; what running apps report their types under
    public required string Path { get; init; }
    public required string Dir { get; init; }
    public required string Kind { get; init; }        // .NET: web | worker | exe | test | lib. npm: web | ui | cli | lib
    public string? Tfm { get; init; }

    /// "C#", "TypeScript" or "JavaScript".
    public string Language { get; init; } = "C#";

    /// Every project the compiler sees, also those reached through other projects.
    public List<string> ProjectRefs { get; init; } = [];

    /// The references written in the project file itself.
    public List<string> WrittenRefs { get; init; } = [];

    /// Type names that the project's Razor files ask for with "@inject Type name", without namespace or generics.
    public HashSet<string> RazorInjects { get; init; } = [];
    public List<PackageRef> Packages { get; init; } = [];

    public bool IsDotnet => Language == "C#";
}

/// An HTTP request that code sends, as far as reading the code can tell. Verb: "GET", "POST", ... or "*".
/// In a RefFact, Path is the address as written ("https://localhost:5001/api/orders/*", "orders/*"), with "*"
/// for the parts worked out at run time; Routes.Request turns it into the comparable form: "/api/orders/*"
/// for a known path, "*/orders/*" when the start of the address is not known (it comes from settings, say).
internal sealed record HttpCall(string Verb, string Path);

internal sealed record TypeFact(
    string Id, string Name, string Ns, string Kind, string Access, string? ContainerId,
    string? BaseId, string? BaseName, string[] InterfaceIds, string[] ExternalInterfaces,
    string[] Tags, int Line, int EndLine);

internal sealed record MemberFact(
    string Id, string TypeId, string Name, string Kind, string Signature, string? Returns,
    string Access, bool IsStatic, bool IsAbstract, bool IsAsync, int Line, int EndLine);

/// A link from a member (or endpoint key) to another symbol.
/// Kinds: calls, creates, dispatch, db, http, references, ctor-param, lookup, typearg.
/// Request: for "http", the request it sends, when the address can be read from the code.
internal sealed record RefFact(string From, string To, string Kind, int Line, string? Label = null, string? Via = null, HttpCall? Request = null);

/// Interface or base method -> the method that implements or overrides it.
internal sealed record ImplFact(string AbstractId, string ImplId, string Kind);

/// Message type -> handler method (MediatR-style).
internal sealed record HandlerFact(string MessageTypeId, string HandlerMemberId, string Interface);

/// Factory: the instance comes from a delegate (sp => new Foo(...)), so dependency injection does not
/// pick the constructor's parameters itself.
internal sealed record DiFact(
    string? ServiceId, string ServiceName, string? ImplId, string? ImplName,
    string Lifetime, string Method, string Owner, int Line, bool Factory = false);

/// A method parameter that carries a route group, so the prefix comes from the caller.
internal sealed record ParamRef(string MethodId, int Ordinal);

internal sealed record GroupPass(string MethodId, int Ordinal, string Prefix, ParamRef? Param);

internal sealed record EndpointFact(
    string Key, string Verb, string Pattern, string Prefix, ParamRef? Param, string Style,
    string? HandlerId, string? TargetTypeId, string Owner, bool Auth, string? RouteName,
    int Line, int EndLine, RequestShape? Request = null);

/// What a request to an endpoint carries, worked out from the handler's parameters.
/// In: "route", "query", "header" or "body". Sample: a value to start from, as JSON text.
internal sealed record RequestParam(string Name, string In, string Type, string Sample);

internal sealed record RequestShape(IReadOnlyList<RequestParam> Params, string? BodyType, string? BodySample);

/// A service that a class (or an endpoint) receives from dependency injection.
/// Consumer: the type, or the endpoint key for minimal API handlers.
/// Wrapper says how it is asked for: "" directly, or "enumerable", "options", "options-scoped", "lazy", "func".
/// Optional: the parameter has a default value, or the class has several constructors to choose from.
internal sealed record DependencyFact(string Consumer, string ServiceId, string ServiceName, string Wrapper, bool Optional, int Line);

/// Something in the code that Sightline sees but cannot follow, such as services registered by
/// scanning assemblies. Kind: "scan", "reflection", "endpoints". Owner: the member it was found in.
internal sealed record CoverageFact(string Kind, string Label, int Line, string Owner);

internal sealed class DocFacts
{
    public required string File { get; init; }
    public required string Project { get; init; }

    public List<TypeFact> Types { get; } = [];
    public List<MemberFact> Members { get; } = [];
    public List<RefFact> Refs { get; } = [];
    public List<ImplFact> Impls { get; } = [];
    public List<HandlerFact> Handlers { get; } = [];
    public List<DiFact> Di { get; } = [];
    public List<EndpointFact> Endpoints { get; } = [];
    public List<GroupPass> GroupPasses { get; } = [];
    public List<DependencyFact> Dependencies { get; } = [];
    public List<CoverageFact> Coverage { get; } = [];

    /// Calls the compiler could not bind, usually because a package is missing.
    public int Unresolved { get; set; }

    /// Changes only when declarations change, not method bodies.
    public string DeclHash { get; set; } = "";
}

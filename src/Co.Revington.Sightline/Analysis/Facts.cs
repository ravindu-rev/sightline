namespace Co.Revington.Sightline.Analysis;

// Raw findings from one source file. The graph is assembled from these.

internal sealed record TypeFact(
    string Id, string Name, string Ns, string Kind, string Access, string? ContainerId,
    string? BaseId, string? BaseName, string[] InterfaceIds, string[] ExternalInterfaces,
    string[] Tags, int Line, int EndLine);

internal sealed record MemberFact(
    string Id, string TypeId, string Name, string Kind, string Signature, string? Returns,
    string Access, bool IsStatic, bool IsAbstract, bool IsAsync, int Line, int EndLine);

/// A link from a member (or endpoint key) to another symbol.
/// Kinds: calls, creates, dispatch, db, http, references, ctor-param.
internal sealed record RefFact(string From, string To, string Kind, int Line, string? Label = null, string? Via = null);

/// Interface or base method -> the method that implements or overrides it.
internal sealed record ImplFact(string AbstractId, string ImplId, string Kind);

/// Message type -> handler method (MediatR-style).
internal sealed record HandlerFact(string MessageTypeId, string HandlerMemberId, string Interface);

internal sealed record DiFact(
    string? ServiceId, string ServiceName, string? ImplId, string? ImplName,
    string Lifetime, string Method, string Owner, int Line);

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

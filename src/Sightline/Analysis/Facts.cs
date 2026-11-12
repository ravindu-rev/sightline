namespace Sightline.Analysis;

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
    int Line, int EndLine);

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

    /// Changes only when declarations change, not method bodies.
    public string DeclHash { get; set; } = "";
}

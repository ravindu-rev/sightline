using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Co.Revington.Sightline.Analysis;

/// Reads one C# file and lists what it declares, calls, registers and maps.
internal static partial class Extractor
{
    public static async Task<DocFacts> ExtractAsync(Document doc, string projectId, CancellationToken ct)
    {
        var facts = new DocFacts { File = doc.FilePath ?? doc.Name, Project = projectId };
        var root = await doc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var model = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (root is null || model is null) return facts;

        new Pass(model, facts, ct).Run(root);
        facts.DeclHash = HashDeclarations(facts);
        return facts;
    }

    // ---------------------------------------------------------------- tables

    static readonly SymbolDisplayFormat TypeNameFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters);

    static readonly SymbolDisplayFormat SignatureFormat = new(
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    static readonly SymbolDisplayFormat ShortType = SymbolDisplayFormat.MinimallyQualifiedFormat;

    static readonly Dictionary<string, (string Verb, string Style)> MapVerbs = new()
    {
        ["MapGet"] = ("GET", "minimal"), ["MapPost"] = ("POST", "minimal"), ["MapPut"] = ("PUT", "minimal"),
        ["MapDelete"] = ("DELETE", "minimal"), ["MapPatch"] = ("PATCH", "minimal"), ["Map"] = ("ANY", "minimal"),
        ["MapMethods"] = ("ANY", "minimal"), ["MapFallback"] = ("ANY", "fallback"), ["MapHub"] = ("HUB", "hub"),
        ["MapGrpcService"] = ("GRPC", "grpc"), ["MapHealthChecks"] = ("GET", "health"),
    };

    static readonly Dictionary<string, string> HttpAttrs = new()
    {
        ["HttpGet"] = "GET", ["HttpPost"] = "POST", ["HttpPut"] = "PUT", ["HttpDelete"] = "DELETE",
        ["HttpPatch"] = "PATCH", ["HttpHead"] = "HEAD", ["HttpOptions"] = "OPTIONS",
    };

    static readonly HashSet<string> DispatchNames =
    [
        "Send", "SendAsync", "Publish", "PublishAsync", "Dispatch", "DispatchAsync", "InvokeAsync", "Request", "RequestAsync",
    ];

    /// Calls that ask the service provider for a service by type.
    static readonly HashSet<string> LookupNames =
    [
        "GetService", "GetRequiredService", "GetServices", "GetKeyedService", "GetRequiredKeyedService", "GetKeyedServices",
    ];

    /// Calls whose effect Sightline cannot follow by reading the code. Strict: only when the method resolves
    /// to a package (so a solution's own method with the same name is not mistaken for it).
    static readonly Dictionary<string, (string Kind, string Label, bool Strict)> Blind = new()
    {
        ["Scan"] = ("scan", "Scrutor registers services by scanning assemblies", true),
        ["Decorate"] = ("scan", "Scrutor decorates services", true),
        ["RegisterServicesFromAssembly"] = ("scan", "MediatR registers handlers by scanning assemblies", false),
        ["RegisterServicesFromAssemblies"] = ("scan", "MediatR registers handlers by scanning assemblies", false),
        ["RegisterServicesFromAssemblyContaining"] = ("scan", "MediatR registers handlers by scanning assemblies", false),
        ["AddValidatorsFromAssembly"] = ("scan", "FluentValidation registers validators by scanning assemblies", false),
        ["AddValidatorsFromAssemblies"] = ("scan", "FluentValidation registers validators by scanning assemblies", false),
        ["AddValidatorsFromAssemblyContaining"] = ("scan", "FluentValidation registers validators by scanning assemblies", false),
        ["AddAutoMapper"] = ("scan", "AutoMapper finds its profiles by scanning assemblies", true),
        ["RegisterAssemblyTypes"] = ("scan", "Autofac registers types by scanning assemblies", false),
        ["AddConsumers"] = ("scan", "MassTransit finds consumers by scanning", true),
        ["AddConsumersFromNamespaceContaining"] = ("scan", "MassTransit finds consumers by scanning", false),
        ["UseWolverine"] = ("scan", "Wolverine finds message handlers by naming convention", true),
        ["AddWolverine"] = ("scan", "Wolverine finds message handlers by naming convention", true),
        ["AddFastEndpoints"] = ("endpoints", "FastEndpoints endpoints are classes, which Sightline does not list yet", true),
        ["MapCarter"] = ("endpoints", "Carter modules map their routes at run time", true),
        ["MapRazorPages"] = ("endpoints", "Razor Pages are not listed as endpoints", true),
        ["MapRazorComponents"] = ("endpoints", "Blazor pages are not listed as endpoints", true),
        ["MapBlazorHub"] = ("endpoints", "Blazor pages are not listed as endpoints", true),
        ["MapControllerRoute"] = ("endpoints", "Conventional controller routes are listed as controller/action", true),
        ["MapDefaultControllerRoute"] = ("endpoints", "Conventional controller routes are listed as controller/action", true),
        ["MapAreaControllerRoute"] = ("endpoints", "Conventional controller routes are listed as controller/action", true),
        ["MapReverseProxy"] = ("endpoints", "YARP proxies routes that come from configuration", true),
        ["MapIdentityApi"] = ("endpoints", "Identity API endpoints come from a package", true),
    };

    [GeneratedRegex(@"^(TryAdd|Add)(Keyed)?(Scoped|Transient|Singleton)$")]
    private static partial Regex LifetimeMethod();

    // AddDbContext, AddDbContextPool, AddPooledDbContextFactory, and Aspire's AddNpgsqlDbContext, AddSqlServerDbContext, ...
    static bool IsDiMethod(string name) =>
        LifetimeMethod().IsMatch(name) || name is "AddHostedService" or "AddHttpClient" or "Configure" or "AddOptions"
        || name.StartsWith("AddDbContext", StringComparison.Ordinal) || name == "AddPooledDbContextFactory"
        || (name.StartsWith("Add", StringComparison.Ordinal) && name.EndsWith("DbContext", StringComparison.Ordinal));

    // A DbContext added with AddDbContext, AddDbContextPool or AddDbContextFactory is itself a scoped service.
    static string Lifetime(string method) =>
        method.Contains("Scoped", StringComparison.Ordinal) ? "scoped"
        : method.Contains("Transient", StringComparison.Ordinal) ? "transient"
        : method.Contains("Singleton", StringComparison.Ordinal) ? "singleton"
        : method == "AddHostedService" ? "hosted"
        : method == "AddHttpClient" ? "httpclient"
        : method is "Configure" or "AddOptions" ? "options"
        : "scoped";

    /// The service behind a constructor or handler parameter, and how it is asked for.
    /// Null for framework services (ILogger<T>, IConfiguration, ...) and anything not declared in the solution.
    static (INamedTypeSymbol Service, string Wrapper)? DependencyOf(ITypeSymbol t)
    {
        if (t is not INamedTypeSymbol n || n.TypeKind == TypeKind.Error) return null;

        if (n.IsGenericType && !Sym.IsSource(n))
        {
            var ns = n.ContainingNamespace?.ToDisplayString() ?? "";
            var arg = n.TypeArguments.Length == 1 ? n.TypeArguments[0] as INamedTypeSymbol : null;
            var wrapper = (n.Name, ns) switch
            {
                ("IEnumerable", "System.Collections.Generic") => "enumerable",
                ("IOptions" or "IOptionsMonitor", "Microsoft.Extensions.Options") => "options",
                ("IOptionsSnapshot", "Microsoft.Extensions.Options") => "options-scoped",
                ("Lazy", "System") => "lazy",
                ("Func", "System") => "func",
                _ => null,
            };
            if (wrapper is null || arg is null || !Sym.IsSource(arg)) return null;
            return (arg.OriginalDefinition, wrapper);
        }

        return Sym.IsSource(n) ? (n.OriginalDefinition, "") : null;
    }

    static bool IsServiceLike(INamedTypeSymbol t) => t.TypeKind == TypeKind.Interface || (t.TypeKind == TypeKind.Class && t.IsAbstract);

    static bool IsHandlerInterface(string name) =>
        name is "IConsumer" or "IHandleMessages" || (name.Length > 8 && name[0] == 'I' && name.EndsWith("Handler", StringComparison.Ordinal));

    static bool IsRouteBuilder(ITypeSymbol t) => t.Name is "RouteGroupBuilder" or "IEndpointRouteBuilder" or "WebApplication";

    static bool IsController(INamedTypeSymbol t) =>
        t.TypeKind == TypeKind.Class && !t.IsAbstract && !Sym.HasAttr(t, "NonController")
        && (Sym.InheritsNamed(t, "ControllerBase") || Sym.HasAttrInherited(t, "ApiController") || Sym.HasAttr(t, "Controller"));

    static string KindOf(INamedTypeSymbol t) => t.TypeKind switch
    {
        TypeKind.Interface => "interface",
        TypeKind.Enum => "enum",
        TypeKind.Struct => t.IsRecord ? "record struct" : "struct",
        _ => t.IsRecord ? "record" : "class",
    };

    static List<string> Tags(INamedTypeSymbol t)
    {
        var tags = new List<string>();
        if (IsController(t)) tags.Add("controller");
        if (Sym.InheritsNamed(t, "DbContext")) tags.Add("dbcontext");
        if (Sym.InheritsNamed(t, "BackgroundService") || t.AllInterfaces.Any(i => i.Name == "IHostedService")) tags.Add("hosted");
        if (Sym.InheritsNamed(t, "Hub")) tags.Add("hub");
        if (t.AllInterfaces.Any(i => i.Name == "IMiddleware") || IsConventionMiddleware(t)) tags.Add("middleware");
        if (t.AllInterfaces.Any(i => i.IsGenericType && IsHandlerInterface(i.Name))) tags.Add("handler");
        if (Sym.InheritsNamed(t, "Exception")) tags.Add("exception");
        if (t.IsStatic) tags.Add("static");
        else if (t.IsAbstract && t.TypeKind == TypeKind.Class) tags.Add("abstract");
        return tags;
    }

    static bool IsConventionMiddleware(INamedTypeSymbol t) =>
        t.GetMembers().OfType<IMethodSymbol>().Any(m =>
            m.Name is "InvokeAsync" or "Invoke" && m.Parameters.Length > 0 && m.Parameters[0].Type.Name == "HttpContext");

    static IEnumerable<INamedTypeSymbol> SignatureTypes(ISymbol s) => s switch
    {
        IMethodSymbol m => m.Parameters.SelectMany(p => Sym.SourceTypesIn(p.Type))
                            .Concat(m.ReturnsVoid ? [] : Sym.SourceTypesIn(m.ReturnType)),
        IPropertySymbol p => Sym.SourceTypesIn(p.Type),
        IFieldSymbol f => Sym.SourceTypesIn(f.Type),
        IEventSymbol e => Sym.SourceTypesIn(e.Type),
        _ => [],
    };

    static int LineOf(SyntaxNode n) => n.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    static string? ConstString(SemanticModel model, ExpressionSyntax e, CancellationToken ct)
    {
        var v = model.GetConstantValue(e, ct);
        if (v.HasValue && v.Value is string s) return s;
        return e is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression) ? lit.Token.ValueText : null;
    }

    static string HashDeclarations(DocFacts f)
    {
        var sb = new StringBuilder();
        foreach (var t in f.Types)
            sb.Append(t.Id).Append('|').Append(t.BaseId).Append('|').Append(t.BaseName).Append('|')
              .AppendJoin(',', t.InterfaceIds).Append('|').Append(t.Access).Append('\n');
        foreach (var m in f.Members)
            sb.Append(m.Id).Append('|').Append(m.Signature).Append('|').Append(m.Returns).Append('|')
              .Append(m.Access).Append(m.IsStatic).Append(m.IsAbstract).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    // ---------------------------------------------------------------- declarations

    sealed class Pass
    {
        public readonly SemanticModel Model;
        public readonly DocFacts Facts;
        public readonly CancellationToken Ct;
        readonly HashSet<string> _typeRefs = [];
        readonly Dictionary<string, int> _ordinals = [];

        public Pass(SemanticModel model, DocFacts facts, CancellationToken ct)
        {
            Model = model;
            Facts = facts;
            Ct = ct;
        }

        public void Run(SyntaxNode root)
        {
            // Walk type declarations only; method bodies are visited per member.
            var decls = root
                .DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax or TypeDeclarationSyntax)
                .OfType<BaseTypeDeclarationSyntax>();

            foreach (var decl in decls)
            {
                Ct.ThrowIfCancellationRequested();
                if (Model.GetDeclaredSymbol(decl, Ct) is INamedTypeSymbol type) ExtractType(decl, type);
            }
            ExtractTopLevel(root);
        }

        /// Counter that keeps endpoint keys unique when verb and pattern repeat.
        public int NextOrdinal(string key)
        {
            _ordinals.TryGetValue(key, out var n);
            _ordinals[key] = n + 1;
            return n;
        }

        void ExtractType(BaseTypeDeclarationSyntax decl, INamedTypeSymbol type)
        {
            var id = Sym.Id(type);
            if (id is null) return;

            var span = decl.GetLocation().GetLineSpan();
            var baseType = type.TypeKind == TypeKind.Class && type.BaseType is { SpecialType: SpecialType.None } b ? b : null;

            Facts.Types.Add(new TypeFact(
                id,
                type.ToDisplayString(TypeNameFormat),
                type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : "",
                KindOf(type),
                type.DeclaredAccessibility.ToString().ToLowerInvariant(),
                Sym.Id(type.ContainingType),
                baseType is not null && Sym.IsSource(baseType) ? Sym.Id(baseType) : null,
                baseType?.Name,
                type.Interfaces.Where(Sym.IsSource).Select(i => Sym.Id(i)).OfType<string>().Distinct().ToArray(),
                type.Interfaces.Where(i => !Sym.IsSource(i) && i.Name != "IEquatable").Select(i => i.Name).Distinct().ToArray(),
                Tags(type).ToArray(),
                span.StartLinePosition.Line + 1,
                span.EndLinePosition.Line + 1));

            if (decl is not TypeDeclarationSyntax td) return;

            // Primary constructor: class Foo(IBar bar)
            if (td.ParameterList is { Parameters.Count: > 0 } pl
                && Model.GetDeclaredSymbol(pl.Parameters[0], Ct) is { ContainingSymbol: IMethodSymbol primary })
            {
                var ctorId = AddMember(primary, id, pl);
                if (ctorId is not null && td.BaseList is not null) new BodyWalker(this, ctorId).Visit(td.BaseList);
            }

            foreach (var member in td.Members) ExtractMember(member, id);
            ExtractImplementations(type);
            ExtractDependencies(type, id);
            if (IsController(type)) ExtractActions(td, type);
        }

        /// What dependency injection must supply to create this class: the parameters of the constructor
        /// it would use (the longest public one). Recorded in the file that declares that constructor.
        void ExtractDependencies(INamedTypeSymbol type, string typeId)
        {
            if (type.TypeKind != TypeKind.Class || type.IsAbstract || type.IsStatic) return;

            var ctors = type.InstanceConstructors.Where(c => c.DeclaredAccessibility == Accessibility.Public).ToList();
            if (ctors.Count == 0) return;
            var longest = ctors.OrderByDescending(c => c.Parameters.Length).First();
            if (longest.Parameters.Length == 0) return;
            if (longest.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree is { } tree && tree != Model.SyntaxTree) return;

            foreach (var p in longest.Parameters)
            {
                if (DependencyOf(p.Type) is not { } dep || Sym.Id(dep.Service) is not { } serviceId || serviceId == typeId) continue;
                var line = p.Locations.FirstOrDefault()?.GetLineSpan().StartLinePosition.Line + 1 ?? 0;
                Facts.Dependencies.Add(new DependencyFact(
                    typeId, serviceId, dep.Service.ToDisplayString(ShortType), dep.Wrapper,
                    ctors.Count > 1 || p.HasExplicitDefaultValue || p.NullableAnnotation == NullableAnnotation.Annotated, line));
            }
        }

        /// Handler parameters that may be services. Interfaces, abstract classes and [FromServices] parameters
        /// must be registered. A plain class is a service only when it is registered, and is read from the
        /// request body otherwise, so it never counts as missing.
        public void AddEndpointDependencies(string endpointKey, IEnumerable<IParameterSymbol> parameters)
        {
            foreach (var p in parameters)
            {
                var fromServices = Sym.HasAttr(p, "FromServices") || Sym.HasAttr(p, "FromKeyedServices");
                if (DependencyOf(p.Type) is not { } dep || Sym.Id(dep.Service) is not { } serviceId) continue;
                var mustBeRegistered = fromServices || dep.Wrapper != "" || IsServiceLike(dep.Service);
                var line = p.Locations.FirstOrDefault()?.GetLineSpan().StartLinePosition.Line + 1 ?? 0;
                Facts.Dependencies.Add(new DependencyFact(
                    endpointKey, serviceId, dep.Service.ToDisplayString(ShortType), dep.Wrapper,
                    p.HasExplicitDefaultValue || !mustBeRegistered, line));
            }
        }

        void ExtractMember(MemberDeclarationSyntax member, string typeId)
        {
            switch (member)
            {
                case BaseMethodDeclarationSyntax method:
                    if (Model.GetDeclaredSymbol(method, Ct) is { } ms && AddMember(ms, typeId, method) is { } mid)
                    {
                        new BodyWalker(this, mid).Visit(method);
                        if (ms.OverriddenMethod is { } overridden && Sym.IsSource(overridden) && Sym.Id(overridden) is { } baseId)
                            Facts.Impls.Add(new ImplFact(baseId, mid, "overrides"));
                    }
                    break;

                case BasePropertyDeclarationSyntax property:
                    if (Model.GetDeclaredSymbol(property, Ct) is { } ps && AddMember(ps, typeId, property) is { } pid)
                        new BodyWalker(this, pid).Visit(property);
                    break;

                case BaseFieldDeclarationSyntax field:
                    foreach (var v in field.Declaration.Variables)
                        if (Model.GetDeclaredSymbol(v, Ct) is { } fs && AddMember(fs, typeId, field) is { } fid && v.Initializer is not null)
                            new BodyWalker(this, fid).Visit(v.Initializer);
                    break;
            }
        }

        string? AddMember(ISymbol s, string typeId, SyntaxNode node)
        {
            var id = Sym.Id(s);
            if (id is null) return null;

            var span = node.GetLocation().GetLineSpan();
            var line = span.StartLinePosition.Line + 1;

            var (kind, name) = s switch
            {
                IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } => ("ctor", s.ContainingType.Name),
                IMethodSymbol m => ("method", m.Name.Contains('.') ? m.Name[(m.Name.LastIndexOf('.') + 1)..] : m.Name),
                IPropertySymbol p => ("property", p.IsIndexer ? "this[]" : p.Name),
                IFieldSymbol => ("field", s.Name),
                IEventSymbol => ("event", s.Name),
                _ => ("member", s.Name),
            };

            var returns = s switch
            {
                IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor or MethodKind.Destructor } => null,
                IMethodSymbol m => m.ReturnType.ToDisplayString(ShortType),
                IPropertySymbol p => p.Type.ToDisplayString(ShortType),
                IFieldSymbol f => f.Type.ToDisplayString(ShortType),
                IEventSymbol e => e.Type.ToDisplayString(ShortType),
                _ => null,
            };

            Facts.Members.Add(new MemberFact(
                id, typeId, name, kind, s.ToDisplayString(SignatureFormat), returns,
                s.DeclaredAccessibility.ToString().ToLowerInvariant(), s.IsStatic, s.IsAbstract,
                s is IMethodSymbol { IsAsync: true }, line, span.EndLinePosition.Line + 1));

            var refKind = s is IMethodSymbol { MethodKind: MethodKind.Constructor } ? "ctor-param" : "references";
            foreach (var t in SignatureTypes(s)) AddTypeRef(typeId, t, refKind, line);
            return id;
        }

        public void AddTypeRef(string from, INamedTypeSymbol target, string kind, int line)
        {
            var to = Sym.Id(target);
            if (to is null || to == from) return;
            if (_typeRefs.Add($"{from}|{to}|{kind}")) Facts.Refs.Add(new RefFact(from, to, kind, line));
        }

        /// Links interface methods to the methods that implement them, and messages to their handlers.
        void ExtractImplementations(INamedTypeSymbol type)
        {
            if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct)) return;

            foreach (var iface in type.AllInterfaces)
            {
                var ifaceIsSource = Sym.IsSource(iface);
                var message = iface.IsGenericType && IsHandlerInterface(iface.Name)
                              && iface.TypeArguments[0] is INamedTypeSymbol msg && Sym.IsSource(msg) ? msg : null;
                if (!ifaceIsSource && message is null) continue;

                foreach (var member in iface.GetMembers())
                {
                    if (member is not IMethodSymbol { MethodKind: MethodKind.Ordinary }) continue;
                    var impl = type.FindImplementationForInterfaceMember(member);
                    if (impl is null || !SymbolEqualityComparer.Default.Equals(impl.ContainingType, type)) continue;
                    var implId = Sym.Id(impl);
                    if (implId is null) continue;

                    if (ifaceIsSource && Sym.Id(member) is { } abstractId)
                        Facts.Impls.Add(new ImplFact(abstractId, implId, "implements"));
                    if (message is not null && Sym.Id(message) is { } messageId)
                        Facts.Handlers.Add(new HandlerFact(messageId, implId, iface.Name));
                }
            }
        }

        /// MVC controller actions: combines class and method route templates.
        void ExtractActions(TypeDeclarationSyntax td, INamedTypeSymbol type)
        {
            var classRoutes = ClassRoutes(type);
            var controller = type.Name.EndsWith("Controller", StringComparison.Ordinal) ? type.Name[..^10] : type.Name;
            var area = type.GetAttributes().Where(a => Sym.AttrName(a) == "Area").Select(AttrString).FirstOrDefault();
            var classAuth = Sym.HasAttrInherited(type, "Authorize");

            foreach (var md in td.Members.OfType<MethodDeclarationSyntax>())
            {
                if (Model.GetDeclaredSymbol(md, Ct) is not { } ms) continue;
                if (ms.DeclaredAccessibility != Accessibility.Public || ms.IsStatic || ms.MethodKind != MethodKind.Ordinary) continue;
                if (Sym.HasAttr(ms, "NonAction")) continue;
                var memberId = Sym.Id(ms);
                if (memberId is null) continue;

                var attrs = ms.GetAttributes();
                var verbs = new List<(string Verb, string? Template)>();
                foreach (var a in attrs)
                    if (HttpAttrs.TryGetValue(Sym.AttrName(a), out var verb)) verbs.Add((verb, AttrString(a)));

                var methodRoutes = attrs.Where(a => Sym.AttrName(a) == "Route").Select(AttrString).OfType<string>().ToList();
                if (verbs.Count == 0)
                    verbs.AddRange(methodRoutes.Count > 0 ? methodRoutes.Select(r => ("ANY", (string?)r)) : [("ANY", null)]);
                else if (methodRoutes.Count > 0)
                    verbs = verbs.SelectMany(v => v.Template is not null ? [v] : methodRoutes.Select(r => (v.Verb, (string?)r))).ToList();

                var auth = (classAuth || Sym.HasAttr(ms, "Authorize")) && !Sym.HasAttr(ms, "AllowAnonymous");
                var span = md.GetLocation().GetLineSpan();

                foreach (var (verb, template) in verbs)
                foreach (var classRoute in classRoutes)
                {
                    string route;
                    if (template is not null && (template.StartsWith('/') || template.StartsWith("~/", StringComparison.Ordinal)))
                        route = Routes.Join(template.TrimStart('~'));
                    else if (classRoute is null && template is null)
                        route = Routes.Join(area, controller, ms.Name);    // conventional routing: {area}/{controller}/{action}
                    else
                        route = Routes.Join(classRoute, template);

                    route = route.Replace("[controller]", controller, StringComparison.OrdinalIgnoreCase)
                                 .Replace("[action]", ms.Name, StringComparison.OrdinalIgnoreCase)
                                 .Replace("[area]", area ?? "", StringComparison.OrdinalIgnoreCase);

                    var key = $"{memberId}#{verb}#{route}";
                    Facts.Endpoints.Add(new EndpointFact(
                        key, verb, route, "", null, "controller", memberId, null, memberId,
                        auth, null, span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1,
                        Requests.Shape(ms.Parameters, route)));
                    AddEndpointDependencies(key, ms.Parameters);
                }
            }
        }

        List<string?> ClassRoutes(INamedTypeSymbol type)
        {
            for (INamedTypeSymbol? t = type; t is not null; t = t.BaseType)
            {
                var routes = t.GetAttributes().Where(a => Sym.AttrName(a) == "Route").Select(AttrString).ToList();
                if (routes.Count > 0) return routes;
            }
            return [null];
        }

        /// First string argument of an attribute, even when the attribute type did not resolve.
        string? AttrString(AttributeData a)
        {
            if (a.ConstructorArguments.Length > 0 && a.ConstructorArguments[0] is { Kind: TypedConstantKind.Primitive, Value: string s })
                return s;

            if (a.ApplicationSyntaxReference?.GetSyntax(Ct) is AttributeSyntax syntax
                && syntax.ArgumentList?.Arguments.FirstOrDefault(x => x.NameEquals is null && x.NameColon is null) is { } arg)
            {
                var model = arg.SyntaxTree == Model.SyntaxTree ? Model : Model.Compilation.GetSemanticModel(arg.SyntaxTree);
                return ConstString(model, arg.Expression, Ct);
            }
            return null;
        }

        /// Program.cs without a Main method: treat the statements as one entry-point member.
        void ExtractTopLevel(SyntaxNode root)
        {
            if (root is not CompilationUnitSyntax unit) return;
            var statements = unit.Members.OfType<GlobalStatementSyntax>().ToList();
            if (statements.Count == 0) return;
            if (Model.GetDeclaredSymbol(unit, Ct) is not { } main) return;

            var type = main.ContainingType;
            var typeId = Sym.Id(type);
            if (typeId is null) return;
            var mainId = Sym.Id(main) ?? typeId + ".<Main>$";

            var first = statements[0].GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var last = statements[^1].GetLocation().GetLineSpan().EndLinePosition.Line + 1;

            Facts.Types.Add(new TypeFact(typeId, type.Name, "", "class", "internal", null, null, null, [], [], ["entrypoint"], first, last));
            Facts.Members.Add(new MemberFact(mainId, typeId, "Main", "method", "Main (top-level statements)", "void",
                "private", true, false, false, first, last));

            var walker = new BodyWalker(this, mainId);
            foreach (var s in statements) walker.Visit(s);
        }
    }

    // ---------------------------------------------------------------- bodies

    /// Visits the code inside one member and records calls, registrations and endpoints.
    sealed class BodyWalker : CSharpSyntaxWalker
    {
        readonly Pass _p;
        string _owner;

        public BodyWalker(Pass p, string owner)
        {
            _p = p;
            _owner = owner;
        }

        public override void VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var name = InvokedName(node.Expression);
            var method = Invoked(node);

            // Calls the compiler could not bind: a sign of missing packages. Dynamic calls are unbound on purpose.
            if (method is null && name != "nameof" && _p.Model.GetTypeInfo(node, _p.Ct).Type?.TypeKind != TypeKind.Dynamic)
                _p.Facts.Unresolved++;

            if (name is not null)
            {
                if (Blind.TryGetValue(name, out var blind))
                {
                    var def = method is null ? null : (method.ReducedFrom ?? method).OriginalDefinition;
                    if (def is null ? !blind.Strict : !Sym.IsSource(def))
                        _p.Facts.Coverage.Add(new CoverageFact(blind.Kind, blind.Label, LineOf(node), _owner));
                }
                if (MapVerbs.ContainsKey(name) && TryEndpoint(node, name, method)) return;
                if (IsDiMethod(name)) TryRegistration(node, name, method);
            }

            if (method is not null) HandleCall(node, method);
            base.VisitInvocationExpression(node);
        }

        public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            Created(node);
            base.VisitObjectCreationExpression(node);
        }

        public override void VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
        {
            Created(node);
            base.VisitImplicitObjectCreationExpression(node);
        }

        public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            // db.Orders -> the member uses that DbContext
            if (_p.Model.GetSymbolInfo(node, _p.Ct).Symbol is IPropertySymbol { Type.Name: "DbSet" } prop
                && Sym.IsSource(prop) && Sym.Id(prop.ContainingType) is { } contextId)
            {
                _p.Facts.Refs.Add(new RefFact(_owner, contextId, "db", LineOf(node), prop.Name));
            }
            base.VisitMemberAccessExpression(node);
        }

        void Created(ExpressionSyntax node)
        {
            if (_p.Model.GetTypeInfo(node, _p.Ct).Type is INamedTypeSymbol t && Sym.IsSource(t) && Sym.Id(t) is { } id)
                _p.Facts.Refs.Add(new RefFact(_owner, id, "creates", LineOf(node)));
        }

        IMethodSymbol? Invoked(InvocationExpressionSyntax node)
        {
            var info = _p.Model.GetSymbolInfo(node, _p.Ct);
            return (info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) as IMethodSymbol;
        }

        static string? InvokedName(ExpressionSyntax e) => e switch
        {
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
            MemberBindingExpressionSyntax mb => mb.Name.Identifier.ValueText,
            SimpleNameSyntax name => name.Identifier.ValueText,
            _ => null,
        };

        string? Const(ExpressionSyntax e) => ConstString(_p.Model, e, _p.Ct);

        // ------------------------------------------------------------ calls

        void HandleCall(InvocationExpressionSyntax node, IMethodSymbol method)
        {
            var def = (method.ReducedFrom ?? method).OriginalDefinition;
            if (def.MethodKind is MethodKind.LocalFunction or MethodKind.AnonymousFunction or MethodKind.DelegateInvoke) return;

            var line = LineOf(node);
            var args = node.ArgumentList.Arguments;

            // mediator.Send(new PlaceOrder(..)) -> remember the message type; the handler is found later
            string? message = null;
            if (DispatchNames.Contains(def.Name) && args.Count > 0
                && _p.Model.GetTypeInfo(args[0].Expression, _p.Ct).Type is INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Struct } argType
                && Sym.IsSource(argType))
            {
                message = Sym.Id(argType);
                if (message is not null) _p.Facts.Refs.Add(new RefFact(_owner, message, "dispatch", line, argType.Name));
            }

            if (Sym.IsSource(def))
            {
                if (Sym.Id(def) is { } id) _p.Facts.Refs.Add(new RefFact(_owner, id, "calls", line, null, message));
                if (def.Parameters.Any(p => IsRouteBuilder(p.Type))) AddGroupPasses(node, def);
                return;
            }

            // AddEntityFrameworkStores<ApplicationDbContext>(): a package that uses a type of the solution.
            // Counts as a use, so its registration is not reported as unused. (Registrations themselves do not count.)
            if (method.TypeArguments.Length > 0 && !IsDiMethod(def.Name) && !LookupNames.Contains(def.Name))
                foreach (var t in method.TypeArguments.SelectMany(Sym.SourceTypesIn))
                    if (Sym.Id(t) is { } typeArgId) _p.Facts.Refs.Add(new RefFact(_owner, typeArgId, "typearg", line));

            var owner = def.ContainingType;
            if (LookupNames.Contains(def.Name))
            {
                RecordLookup(node, method, line);
            }
            else if (owner?.Name == "Activator" && def.Name == "CreateInstance")
            {
                _p.Facts.Coverage.Add(new CoverageFact("reflection", "Objects created with Activator.CreateInstance", line, _owner));
            }
            else if (owner?.Name is "HttpClient" or "HttpClientJsonExtensions" or "HttpMessageInvoker")
            {
                _p.Facts.Refs.Add(new RefFact(_owner, "ext:http", "http", line, def.Name));
            }
            else if (owner?.Name == "DbContext" && def.Name is "SaveChanges" or "SaveChangesAsync")
            {
                _p.Facts.Refs.Add(new RefFact(_owner, DbContextOf(node), "db", line, "save"));
            }
            else if (owner?.Name == "DbContext" && def.Name == "Set" && method.TypeArguments.Length == 1)
            {
                _p.Facts.Refs.Add(new RefFact(_owner, DbContextOf(node), "db", line, method.TypeArguments[0].Name));
            }
        }

        /// sp.GetRequiredService<IFoo>() and sp.GetService(typeof(IFoo)) ask for IFoo; a Type worked out
        /// at run time cannot be followed.
        void RecordLookup(InvocationExpressionSyntax node, IMethodSymbol method, int line)
        {
            var target = method.TypeArguments.Length == 1 ? method.TypeArguments[0] : null;
            if (target is null && node.ArgumentList.Arguments.FirstOrDefault()?.Expression is TypeOfExpressionSyntax typeOf)
                target = _p.Model.GetTypeInfo(typeOf.Type, _p.Ct).Type;

            if (target is null || target is ITypeParameterSymbol)
            {
                _p.Facts.Coverage.Add(new CoverageFact("reflection", "Services looked up by a type worked out at run time", line, _owner));
                return;
            }
            foreach (var t in Sym.SourceTypesIn(target))
                if (Sym.Id(t) is { } id) _p.Facts.Refs.Add(new RefFact(_owner, id, "lookup", line));
        }

        /// The solution's DbContext class behind a call like db.SaveChanges(), or a generic database node.
        string DbContextOf(InvocationExpressionSyntax node)
        {
            ITypeSymbol? receiver = node.Expression is MemberAccessExpressionSyntax ma
                ? _p.Model.GetTypeInfo(ma.Expression, _p.Ct).Type
                : _p.Model.GetEnclosingSymbol(node.SpanStart, _p.Ct)?.ContainingType;

            return receiver is INamedTypeSymbol t && Sym.IsSource(t) && Sym.InheritsNamed(t, "DbContext") && Sym.Id(t) is { } id
                ? id
                : "ext:db";
        }

        /// app.MapGroup("/api/orders").MapOrders() -> MapOrders receives the "/api/orders" prefix.
        void AddGroupPasses(InvocationExpressionSyntax node, IMethodSymbol def)
        {
            if (_p.Model.GetOperation(node, _p.Ct) is not IInvocationOperation op || Sym.Id(def) is not { } methodId) return;

            foreach (var a in op.Arguments)
            {
                if (a.Parameter is null || !IsRouteBuilder(a.Parameter.Type) || a.Value.Syntax is not ExpressionSyntax value) continue;
                var (prefix, param) = ResolvePrefix(value, 0);
                _p.Facts.GroupPasses.Add(new GroupPass(methodId, a.Parameter.Ordinal, prefix, param));
            }
        }

        /// Route prefix built up by MapGroup calls on the way to this expression.
        (string Prefix, ParamRef? Param) ResolvePrefix(ExpressionSyntax e, int depth)
        {
            if (depth > 12) return ("", null);

            switch (e)
            {
                case ParenthesizedExpressionSyntax p:
                    return ResolvePrefix(p.Expression, depth + 1);

                case CastExpressionSyntax c:
                    return ResolvePrefix(c.Expression, depth + 1);

                case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax ma } inv:
                    var inner = ResolvePrefix(ma.Expression, depth + 1);
                    if (ma.Name.Identifier.ValueText == "MapGroup" && inv.ArgumentList.Arguments.Count > 0)
                        return (Routes.Join(inner.Prefix, Const(inv.ArgumentList.Arguments[0].Expression)), inner.Param);
                    return inner;

                case IdentifierNameSyntax id:
                    switch (_p.Model.GetSymbolInfo(id, _p.Ct).Symbol)
                    {
                        case ILocalSymbol local
                            when local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(_p.Ct) is VariableDeclaratorSyntax { Initializer.Value: { } init }:
                            return ResolvePrefix(init, depth + 1);

                        case IParameterSymbol { ContainingSymbol: IMethodSymbol pm } ps when Sym.Id(pm) is { } methodId:
                            return ("", new ParamRef(methodId, ps.Ordinal));
                    }
                    break;
            }
            return ("", null);
        }

        // ------------------------------------------------------------ endpoints

        bool TryEndpoint(InvocationExpressionSyntax node, string name, IMethodSymbol? method)
        {
            if (method is not null)
            {
                var def = (method.ReducedFrom ?? method).OriginalDefinition;
                if (Sym.IsSource(def)) return false;                                   // the solution's own MapXxx helper
                var ns = def.ContainingNamespace?.ToDisplayString() ?? "";
                if (!ns.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)) return false;
                if (name == "Map" && def.ContainingType?.Name != "EndpointRouteBuilderExtensions") return false;
            }
            else if (name == "Map")
            {
                return false;
            }

            var args = node.ArgumentList.Arguments;
            var (verb, style) = MapVerbs[name];
            var pattern = "";
            ExpressionSyntax? handler = null;
            string? targetType = null;

            switch (name)
            {
                case "MapHub" or "MapGrpcService":
                    var target = TypeArgs(node, method).FirstOrDefault() as INamedTypeSymbol;
                    if (target is null || !Sym.IsSource(target)) return false;
                    targetType = Sym.Id(target);
                    pattern = name == "MapHub" && args.Count > 0 ? Const(args[0].Expression) ?? "" : target.Name;
                    break;

                case "MapHealthChecks":
                    pattern = args.Count > 0 ? Const(args[0].Expression) ?? "" : "";
                    break;

                case "MapFallback":
                    if (args.Count == 0) return false;
                    pattern = args.Count > 1 ? Const(args[0].Expression) ?? "{*path}" : "{*path}";
                    handler = args[^1].Expression;
                    break;

                case "MapMethods":
                    if (args.Count < 3) return false;
                    pattern = Const(args[0].Expression) ?? "?";
                    // ["GET", "HEAD"] or [HttpMethods.Options]: every element with a constant string value.
                    var verbs = args[1].Expression.DescendantNodesAndSelf().OfType<ExpressionSyntax>()
                        .Where(e => e is LiteralExpressionSyntax or MemberAccessExpressionSyntax or IdentifierNameSyntax)
                        .Where(e => e.Parent is not MemberAccessExpressionSyntax { Name: var n } || n != e)
                        .Select(Const).OfType<string>().Select(v => v.ToUpperInvariant()).Distinct().ToList();
                    if (verbs.Count > 0) verb = string.Join(",", verbs);
                    handler = args[2].Expression;
                    break;

                default:
                    if (args.Count < 2) return false;
                    var literal = Const(args[0].Expression);
                    if (literal is null && method is null) return false;
                    pattern = literal ?? "?";
                    handler = args[^1].Expression;
                    break;
            }

            var receiver = (node.Expression as MemberAccessExpressionSyntax)?.Expression;
            var (prefix, param) = receiver is null ? ("", null) : ResolvePrefix(receiver, 0);
            var (auth, routeName) = ChainInfo(node);
            var span = node.GetLocation().GetLineSpan();

            var keyBase = $"{_owner}#{verb}#{pattern}";
            var ordinal = _p.NextOrdinal(keyBase);
            var key = ordinal == 0 ? keyBase : $"{keyBase}#{ordinal}";
            var declaredIn = _owner;
            string? handlerId = null;
            IReadOnlyList<IParameterSymbol>? handlerParams = null;

            if (receiver is not null) Visit(receiver);
            foreach (var a in args)
                if (a.Expression != handler) Visit(a.Expression);

            if (handler is AnonymousFunctionExpressionSyntax lambda)
            {
                // Code inside the lambda belongs to the endpoint, not to the method that maps it.
                AddLambdaParameters(lambda, key);
                handlerParams = (_p.Model.GetSymbolInfo(lambda, _p.Ct).Symbol as IMethodSymbol)?.Parameters;
                _owner = key;
                Visit(lambda);
                _owner = declaredIn;
            }
            else if (handler is not null)
            {
                var info = _p.Model.GetSymbolInfo(handler, _p.Ct);
                if ((info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) is IMethodSymbol target && Sym.IsSource(target))
                {
                    handlerId = Sym.Id(target);
                    handlerParams = target.Parameters;
                }
                else
                {
                    Visit(handler);
                }
            }

            if (handlerParams is not null) _p.AddEndpointDependencies(key, handlerParams);
            var request = handlerParams is null || style is "hub" or "grpc" or "health" ? null : Requests.Shape(handlerParams, pattern);

            _p.Facts.Endpoints.Add(new EndpointFact(
                key, verb, pattern, prefix, param, style, handlerId, targetType, declaredIn, auth, routeName,
                span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1, request));
            return true;
        }

        void AddLambdaParameters(AnonymousFunctionExpressionSyntax lambda, string endpointKey)
        {
            IEnumerable<ParameterSyntax> parameters = lambda switch
            {
                ParenthesizedLambdaExpressionSyntax p => p.ParameterList.Parameters,
                SimpleLambdaExpressionSyntax s => [s.Parameter],
                AnonymousMethodExpressionSyntax { ParameterList: { } list } => list.Parameters,
                _ => [],
            };

            foreach (var parameter in parameters)
                if (_p.Model.GetDeclaredSymbol(parameter, _p.Ct) is { } symbol)
                    foreach (var t in Sym.SourceTypesIn(symbol.Type))
                        _p.AddTypeRef(endpointKey, t, "ctor-param", LineOf(parameter));
        }

        /// Reads fluent calls after MapGet(...): .RequireAuthorization(), .WithName("x").
        (bool Auth, string? Name) ChainInfo(InvocationExpressionSyntax node)
        {
            var auth = false;
            string? name = null;
            SyntaxNode current = node;

            while (current.Parent is MemberAccessExpressionSyntax ma && ma.Expression == current && ma.Parent is InvocationExpressionSyntax outer)
            {
                switch (ma.Name.Identifier.ValueText)
                {
                    case "RequireAuthorization": auth = true; break;
                    case "AllowAnonymous": auth = false; break;
                    case "WithName" when outer.ArgumentList.Arguments.Count > 0: name = Const(outer.ArgumentList.Arguments[0].Expression); break;
                }
                current = outer;
            }
            return (auth, name);
        }

        // ------------------------------------------------------------ dependency injection

        void TryRegistration(InvocationExpressionSyntax node, string name, IMethodSymbol? method)
        {
            if (method is not null)
            {
                var def = (method.ReducedFrom ?? method).OriginalDefinition;
                if (Sym.IsSource(def)) return;
                var ns = def.ContainingNamespace?.ToDisplayString() ?? "";
                if (!ns.StartsWith("Microsoft.Extensions", StringComparison.Ordinal)
                    && !ns.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                    && !ns.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)) return;
            }
            else if (name is "Configure" or "AddOptions")
            {
                return;   // too common a name to guess without a resolved symbol
            }

            var types = TypeArgs(node, method);
            if (types.Count == 0)
                types = node.ArgumentList.Arguments.Select(a => a.Expression).OfType<TypeOfExpressionSyntax>()
                    .Select(t => _p.Model.GetTypeInfo(t.Type, _p.Ct).Type).OfType<ITypeSymbol>().ToList();

            if (types.Count == 0 || types[0] is not INamedTypeSymbol service) return;

            var impl = types.Count > 1 ? types[1] as INamedTypeSymbol : null;
            impl ??= InferImplementation(node, service)
                     ?? (service is { TypeKind: TypeKind.Class, IsAbstract: false } ? service : null);

            // AddSingleton<IFoo>(sp => new Foo(...)): a parameter of type Func<IServiceProvider, ...> got an argument.
            // When the call does not resolve (packages missing), a lambda with one parameter (sp => ...) says the same.
            var factory = node.ArgumentList.Arguments.Count > 0 && (method is not null
                ? method.Parameters.Any(p => p.Type is INamedTypeSymbol { Name: "Func" } f && f.TypeArguments.FirstOrDefault()?.Name == "IServiceProvider")
                : LifetimeMethod().IsMatch(name) && node.ArgumentList.Arguments.Any(a => a.Expression is SimpleLambdaExpressionSyntax
                    or ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters.Count: 1 }));

            _p.Facts.Di.Add(new DiFact(
                Sym.IsSource(service) ? Sym.Id(service) : null,
                service.ToDisplayString(ShortType),
                impl is not null && Sym.IsSource(impl) ? Sym.Id(impl) : null,
                impl?.ToDisplayString(ShortType),
                Lifetime(name), name, _owner, LineOf(node), factory));
        }

        /// AddScoped<IFoo>(sp => new Foo(..)) -> Foo
        INamedTypeSymbol? InferImplementation(InvocationExpressionSyntax node, INamedTypeSymbol service)
        {
            foreach (var creation in node.ArgumentList.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
            {
                if (_p.Model.GetTypeInfo(creation, _p.Ct).Type is not INamedTypeSymbol t || !Sym.IsSource(t)) continue;
                var matches = SymbolEqualityComparer.Default.Equals(t, service)
                              || t.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, service))
                              || Sym.InheritsNamed(t, service.Name);
                if (matches) return t;
            }
            return null;
        }

        List<ITypeSymbol> TypeArgs(InvocationExpressionSyntax node, IMethodSymbol? method)
        {
            var generic = node.Expression switch
            {
                MemberAccessExpressionSyntax { Name: GenericNameSyntax g } => g,
                GenericNameSyntax g => g,
                _ => null,
            };

            if (generic is not null)
                return generic.TypeArgumentList.Arguments
                    .Select(a => _p.Model.GetTypeInfo(a, _p.Ct).Type).OfType<ITypeSymbol>().ToList();

            // Type arguments the compiler inferred.
            if (method is { TypeArguments.Length: > 0 } && method.TypeArguments.All(t => t is not ITypeParameterSymbol))
                return method.TypeArguments.ToList();

            return [];
        }
    }
}

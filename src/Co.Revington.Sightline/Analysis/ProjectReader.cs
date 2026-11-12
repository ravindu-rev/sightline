using System.Text.Json;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace Co.Revington.Sightline.Analysis;

internal sealed record PackageRef(string Name, string Version, bool Direct);

internal sealed class ProjectFacts
{
    public required string Id { get; init; }          // graph node id: "proj:Name"
    public required string Name { get; init; }
    public string AssemblyName { get; init; } = "";
    public required string Path { get; init; }
    public required string Dir { get; init; }
    public required string Kind { get; init; }        // web | worker | exe | test | lib
    public string? Tfm { get; init; }
    public required ProjectId RoslynId { get; init; }

    /// Every project the compiler sees, also those reached through other projects.
    public List<string> ProjectRefs { get; init; } = [];

    /// The ProjectReference items written in the project file itself.
    public List<string> WrittenRefs { get; init; } = [];

    /// Type names that the project's Razor files ask for with "@inject Type name", without namespace or generics.
    public HashSet<string> RazorInjects { get; init; } = [];
    public List<PackageRef> Packages { get; init; } = [];
}

/// Project-level facts: kind, target framework, project and package references.
internal static partial class ProjectReader
{
    static readonly HashSet<string> TestPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.NET.Test.Sdk", "xunit", "xunit.v3", "NUnit", "MSTest", "MSTest.TestFramework", "TUnit",
    };

    public static ProjectFacts Read(Project project, string nodeId, string name, IReadOnlyDictionary<string, string> nodeIdByPath)
    {
        var path = project.FilePath!;
        var dir = System.IO.Path.GetDirectoryName(path)!;
        var xml = LoadXml(path);
        var packages = ReadAssets(dir, out var assetsTfm) ?? ReadPackageReferences(xml);

        var sdk = xml?.Root?.Attribute("Sdk")?.Value ?? "";
        var isExe = project.CompilationOptions?.OutputKind is OutputKind.ConsoleApplication or OutputKind.WindowsApplication;
        var kind = packages.Any(p => p.Direct && TestPackages.Contains(p.Name)) ? "test"
            : sdk.Contains("Web", StringComparison.OrdinalIgnoreCase) && !sdk.Contains("Razor", StringComparison.OrdinalIgnoreCase) ? "web"
            : sdk.Contains("Worker", StringComparison.OrdinalIgnoreCase) ? "worker"
            : isExe ? "exe"
            : "lib";

        var tfm = Element(xml, "TargetFramework")
                  ?? Element(xml, "TargetFrameworks")?.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                  ?? assetsTfm;

        var refs = project.ProjectReferences
            .Select(r => project.Solution.GetProject(r.ProjectId)?.FilePath)
            .OfType<string>()
            .Select(p => nodeIdByPath.GetValueOrDefault(p))
            .OfType<string>()
            .Where(id => id != nodeId)
            .Distinct()
            .ToList();

        // Include="..\Shop.Domain\Shop.Domain.csproj"; paths built from MSBuild properties are left to the compiler's list.
        var written = (xml?.Descendants() ?? [])
            .Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => e.Attribute("Include")?.Value)
            .OfType<string>()
            .Where(include => !include.Contains("$(", StringComparison.Ordinal))
            .Select(include => System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, include.Replace('\\', System.IO.Path.DirectorySeparatorChar))))
            .Select(full => nodeIdByPath.GetValueOrDefault(full))
            .OfType<string>()
            .Where(id => id != nodeId && refs.Contains(id))
            .Distinct()
            .ToList();

        return new ProjectFacts
        {
            Id = nodeId, Name = name, AssemblyName = project.AssemblyName, Path = path, Dir = dir, Kind = kind, Tfm = tfm,
            RoslynId = project.Id, ProjectRefs = refs, WrittenRefs = written, Packages = packages,
            RazorInjects = RazorInjectsIn(dir),
        };
    }

    /// "@inject NavigationManager Nav" in .razor and .cshtml files: the services Razor asks for.
    static HashSet<string> RazorInjectsIn(string dir)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var files = Directory.EnumerateFiles(dir, "*.razor", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(dir, "*.cshtml", SearchOption.AllDirectories))
                .Where(f => !f.Contains($"{System.IO.Path.DirectorySeparatorChar}bin{System.IO.Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                            && !f.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
            foreach (var file in files)
            foreach (var line in File.ReadLines(file))
            {
                var m = Inject().Match(line);
                if (!m.Success) continue;
                var type = m.Groups[1].Value.Split('<')[0];
                names.Add(type[(type.LastIndexOf('.') + 1)..]);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // what could be read is enough
        }
        return names;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*@inject\s+([\w.]+(?:<[^>]*>)?)\s+\w+\s*$")]
    private static partial System.Text.RegularExpressions.Regex Inject();

    static XDocument? LoadXml(string path)
    {
        try { return XDocument.Load(path); }
        catch { return null; }
    }

    static string? Element(XDocument? xml, string name) =>
        xml?.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } v ? v : null;

    /// Resolved packages from the restore output: exact versions, direct and transitive.
    static List<PackageRef>? ReadAssets(string projectDir, out string? tfm)
    {
        tfm = null;
        var file = System.IO.Path.Combine(projectDir, "obj", "project.assets.json");
        if (!File.Exists(file)) return null;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            var root = doc.RootElement;
            var direct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (root.TryGetProperty("project", out var project) && project.TryGetProperty("frameworks", out var frameworks))
            {
                foreach (var fw in frameworks.EnumerateObject())
                {
                    tfm ??= fw.Name;
                    if (!fw.Value.TryGetProperty("dependencies", out var deps)) continue;
                    foreach (var d in deps.EnumerateObject())
                    {
                        var isPackage = !d.Value.TryGetProperty("target", out var target) || target.GetString() == "Package";
                        var isImplicit = d.Value.TryGetProperty("autoReferenced", out var auto) && auto.ValueKind == JsonValueKind.True;
                        if (isPackage && !isImplicit) direct.Add(d.Name);
                    }
                }
            }

            var list = new List<PackageRef>();
            if (root.TryGetProperty("libraries", out var libraries))
            {
                foreach (var lib in libraries.EnumerateObject())
                {
                    if (!lib.Value.TryGetProperty("type", out var type) || type.GetString() != "package") continue;
                    var slash = lib.Name.IndexOf('/');
                    if (slash < 0) continue;
                    var name = lib.Name[..slash];
                    list.Add(new PackageRef(name, lib.Name[(slash + 1)..], direct.Contains(name)));
                }
            }
            return list;
        }
        catch
        {
            return null;
        }
    }

    /// Fallback when the project was never restored: read PackageReference items as written.
    static List<PackageRef> ReadPackageReferences(XDocument? xml)
    {
        if (xml is null) return [];
        return xml.Descendants()
            .Where(e => e.Name.LocalName == "PackageReference")
            .Select(e => new PackageRef(
                e.Attribute("Include")?.Value ?? e.Attribute("Update")?.Value ?? "",
                e.Attribute("Version")?.Value ?? e.Elements().FirstOrDefault(x => x.Name.LocalName == "Version")?.Value ?? "",
                true))
            .Where(p => p.Name.Length > 0)
            .ToList();
    }
}

namespace Co.Revington.Sightline.Languages;

/// The languages Sightline reads, and which of them apply to a target.
internal static class LanguageSet
{
    /// What "languages" in sightline.json accepts, and the reader each one means.
    public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["csharp"] = "csharp",
        ["typescript"] = "typescript",
        ["javascript"] = "typescript",
    };

    public static string Title(string language) => language switch
    {
        "csharp" => "C#",
        "typescript" => "JavaScript and TypeScript",
        _ => language,
    };

    /// One workspace per language that can apply. A package.json is read as JavaScript and TypeScript
    /// only; a solution, a project file or a folder by every language, each finding its own projects.
    public static List<ILanguageWorkspace> For(string target, IReadOnlyList<string>? only)
    {
        bool Wanted(string language) => only is null || only.Contains(language);
        var manifest = Path.GetFileName(target).Equals("package.json", StringComparison.OrdinalIgnoreCase);

        var list = new List<ILanguageWorkspace>();
        if (!manifest && Wanted("csharp")) list.Add(new CSharpWorkspace());
        if (Wanted("typescript")) list.Add(new TypeScript.TypeScriptWorkspace());
        return list;
    }

    /// True when a folder holds code that some language reads.
    public static bool HasCode(string folder) => CSharpWorkspace.FindProjects(folder).Any() || TypeScript.NpmPackages.Any(folder);
}

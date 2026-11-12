using System.Text;
using System.Text.Json;
using Co.Revington.Sightline.Analysis;

namespace Co.Revington.Sightline.Commands;

/// "sightline check": the problems on the map, for the console, for scripts (JSON) or for GitHub Actions.
internal static class CheckRunner
{
    static readonly Dictionary<string, int> Rank = new() { ["error"] = 0, ["warning"] = 1, ["info"] = 2, ["never"] = 99 };

    public static async Task<int> RunAsync(CheckCommand command)
    {
        Snapshot snapshot;
        try
        {
            (_, snapshot) = await OneShot.LoadAsync(command.Target);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Sightline could not load the solution: " + ex.Message);
            return 2;
        }

        var graph = snapshot.Graph!;
        var problems = graph.Problems;
        var scrubber = new PathScrubber(Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") is { Length: > 0 } ws ? ws : Directory.GetCurrentDirectory());
        string Where(Problem p) => p.File is null ? "" : scrubber.Relative(p.File) + (p.Line is > 0 ? ":" + p.Line : "");

        switch (command.Format)
        {
            case "json":
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    version = ToolInfo.Version,
                    solution = scrubber.Relative(command.Target.Path),
                    counts = Counts(problems),
                    problems = problems.Select(p => new
                    {
                        p.Rule, p.Severity, p.Title, message = scrubber.Scrub(p.Message),
                        file = p.File is null ? null : scrubber.Relative(p.File), p.Line, p.Nodes,
                    }),
                    coverage = graph.Coverage.Select(c => new { c.Kind, c.Label, c.Count }),
                }, new JsonSerializerOptions(Snapshot.Json) { WriteIndented = true }));
                break;

            case "github":
                foreach (var p in problems)
                {
                    var level = p.Severity == "info" ? "notice" : p.Severity;
                    var props = new List<string>();
                    if (p.File is not null) props.Add("file=" + Property(scrubber.Relative(p.File)));
                    if (p.Line is > 0) props.Add("line=" + p.Line);
                    props.Add("title=" + Property("Sightline: " + p.Title));
                    Console.WriteLine($"::{level} {string.Join(',', props)}::{Data(scrubber.Scrub(p.Message))}");
                }
                WriteSummary(command, problems, graph.Coverage, Where);
                Console.WriteLine(Text(command, problems, graph.Coverage, Where, scrubber));
                break;

            default:
                Console.WriteLine(Text(command, problems, graph.Coverage, Where, scrubber));
                break;
        }

        var limit = Rank[command.FailOn];
        return problems.Any(p => Rank.GetValueOrDefault(p.Severity, 99) <= limit) ? 1 : 0;
    }

    static object Counts(IReadOnlyList<Problem> problems) => new
    {
        errors = problems.Count(p => p.Severity == "error"),
        warnings = problems.Count(p => p.Severity == "warning"),
        info = problems.Count(p => p.Severity == "info"),
    };

    static string Summary(IReadOnlyList<Problem> problems)
    {
        if (problems.Count == 0) return "no problems";
        var parts = new[] { ("error", "error"), ("warning", "warning"), ("info", "note") }
            .Select(x => (Count: problems.Count(p => p.Severity == x.Item1), Word: x.Item2))
            .Where(x => x.Count > 0)
            .Select(x => $"{x.Count} {x.Word}{(x.Count == 1 ? "" : "s")}");
        return string.Join(", ", parts);
    }

    static string Text(CheckCommand command, IReadOnlyList<Problem> problems, IReadOnlyList<CoverageNote> coverage,
        Func<Problem, string> where, PathScrubber scrubber)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Sightline {ToolInfo.Version}: {Summary(problems)} in {Path.GetFileName(command.Target.Path)}");

        foreach (var p in problems)
        {
            sb.AppendLine();
            sb.AppendLine($"{p.Severity,-8} {p.Title}");
            if (where(p) is { Length: > 0 } at) sb.AppendLine($"         {at}  ({p.Rule})");
            foreach (var line in Wrap(scrubber.Scrub(p.Message), 92)) sb.AppendLine("         " + line);
        }

        if (coverage.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Not followed (the map may miss things here):");
            foreach (var c in coverage) sb.AppendLine($"  {c.Count,4}  {c.Label}");
        }
        return sb.ToString().TrimEnd();
    }

    static IEnumerable<string> Wrap(string text, int width)
    {
        var line = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0) yield return line.ToString();
    }

    /// Markdown for the job summary page of a GitHub Actions run.
    static void WriteSummary(CheckCommand command, IReadOnlyList<Problem> problems, IReadOnlyList<CoverageNote> coverage, Func<Problem, string> where)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is not { Length: > 0 } file) return;

        var sb = new StringBuilder();
        sb.AppendLine($"### Sightline: {Summary(problems)} in {Path.GetFileName(command.Target.Path)}");
        sb.AppendLine();
        if (problems.Count > 0)
        {
            sb.AppendLine("| | Problem | Where |");
            sb.AppendLine("|---|---|---|");
            foreach (var p in problems)
                sb.AppendLine($"| {Icon(p.Severity)} | **{Cell(p.Title)}**<br>{Cell(p.Message)} | `{Cell(where(p))}` |");
            sb.AppendLine();
        }
        if (coverage.Count > 0)
        {
            sb.AppendLine("<details><summary>Not followed by the map</summary>");
            sb.AppendLine();
            foreach (var c in coverage) sb.AppendLine($"- {c.Count} × {c.Label}");
            sb.AppendLine();
            sb.AppendLine("</details>");
        }
        File.AppendAllText(file, sb.ToString());
    }

    public static string Icon(string severity) => severity switch { "error" => "❌", "warning" => "⚠️", _ => "ℹ️" };

    static string Cell(string text) => text.Replace("|", "\\|").Replace("\n", " ");

    // GitHub workflow commands: https://docs.github.com/actions/reference/workflow-commands-for-github-actions
    static string Data(string text) => text.Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A");

    static string Property(string text) => Data(text).Replace(":", "%3A").Replace(",", "%2C");
}

using System.Text.Json.Nodes;
using Co.Revington.Sightline.Settings;
using Co.Revington.Sightline.Setup;

namespace Co.Revington.Sightline.Tests;

/// "sightline init" without the tool manifest step, which needs nuget.org.
public sealed class InitTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("sightline-init-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    string PathOf(params string[] parts) => Path.Combine([_dir, .. parts]);

    Task<int> InitAsync(string? solution = null, bool? vscode = null) =>
        Init.RunAsync(new InitOptions(_dir, solution, vscode, PinTool: false));

    [Fact]
    public async Task Writes_settings_and_a_VS_Code_task_then_leaves_both_alone_on_a_second_run()
    {
        File.WriteAllText(PathOf("Shop.sln"), "");
        Directory.CreateDirectory(PathOf(".vscode"));

        Assert.Equal(0, await InitAsync());

        var config = SightlineConfig.Load(PathOf("sightline.json"));
        Assert.Equal(PathOf("Shop.sln"), config.Solution);
        Assert.Empty(config.Warnings);

        var tasks = JsonNode.Parse(File.ReadAllText(PathOf(".vscode", "tasks.json")))!;
        var task = Assert.Single(tasks["tasks"]!.AsArray())!;
        Assert.Equal("Sightline: open the map", (string?)task["label"]);
        Assert.Equal("dotnet", (string?)task["command"]);
        Assert.Equal("sightline", (string?)task["args"]![0]);

        var before = (File.ReadAllText(PathOf("sightline.json")), File.ReadAllText(PathOf(".vscode", "tasks.json")));
        Assert.Equal(0, await InitAsync());
        Assert.Equal(before, (File.ReadAllText(PathOf("sightline.json")), File.ReadAllText(PathOf(".vscode", "tasks.json"))));
    }

    [Fact]
    public async Task Adds_its_task_next_to_existing_tasks()
    {
        File.WriteAllText(PathOf("Shop.sln"), "");
        Directory.CreateDirectory(PathOf(".vscode"));
        File.WriteAllText(PathOf(".vscode", "tasks.json"), """{ "version": "2.0.0", "tasks": [ { "label": "build", "command": "dotnet", "args": ["build"] } ] }""");

        Assert.Equal(0, await InitAsync());

        var labels = JsonNode.Parse(File.ReadAllText(PathOf(".vscode", "tasks.json")))!["tasks"]!.AsArray().Select(t => (string?)t!["label"]);
        Assert.Equal(["build", "Sightline: open the map"], labels);
    }

    [Fact]
    public async Task Leaves_a_tasks_file_with_comments_exactly_as_it_was()
    {
        File.WriteAllText(PathOf("Shop.sln"), "");
        Directory.CreateDirectory(PathOf(".vscode"));
        const string handWritten = """
            {
              // our build task
              "version": "2.0.0",
              "tasks": [ { "label": "build", "command": "dotnet", "args": ["build"] } ]
            }
            """;
        File.WriteAllText(PathOf(".vscode", "tasks.json"), handWritten);

        Assert.Equal(0, await InitAsync());

        Assert.Equal(handWritten, File.ReadAllText(PathOf(".vscode", "tasks.json")));
        Assert.True(File.Exists(PathOf("sightline.json")));
    }

    [Fact]
    public async Task Adds_no_VS_Code_task_to_a_folder_that_does_not_use_VS_Code()
    {
        File.WriteAllText(PathOf("Shop.sln"), "");

        Assert.Equal(0, await InitAsync());

        Assert.False(Directory.Exists(PathOf(".vscode")));
    }

    [Fact]
    public async Task Finds_a_solution_in_a_folder_below_and_keeps_its_path_relative()
    {
        Directory.CreateDirectory(PathOf("src"));
        File.WriteAllText(PathOf("src", "Shop.sln"), "");

        Assert.Equal(0, await InitAsync());

        Assert.Contains("\"solution\": \"src/Shop.sln\"", File.ReadAllText(PathOf("sightline.json")));
    }

    [Fact]
    public async Task Stops_with_a_clear_error_when_there_is_no_solution()
    {
        Assert.Equal(1, await InitAsync());
        Assert.False(File.Exists(PathOf("sightline.json")));
    }

    [Fact]
    public async Task Keeps_settings_the_team_already_has()
    {
        File.WriteAllText(PathOf("Shop.sln"), "");
        File.WriteAllText(PathOf("Other.sln"), "");
        const string existing = """{ "solution": "Other.sln", "exclude": ["*.Tests"] }""";
        File.WriteAllText(PathOf("sightline.json"), existing);

        Assert.Equal(0, await InitAsync(solution: PathOf("Shop.sln")));

        Assert.Equal(existing, File.ReadAllText(PathOf("sightline.json")));
    }
}

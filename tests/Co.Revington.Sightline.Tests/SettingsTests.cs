using Co.Revington.Sightline.Settings;

namespace Co.Revington.Sightline.Tests;

/// sightline.json: where it is found, how it is read, and what "exclude" matches.
public sealed class SettingsTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("sightline-settings-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    string Write(string relative, string text)
    {
        var path = Path.GetFullPath(Path.Combine(_dir, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void Without_settings_opens_the_nearest_solution_above_but_not_past_the_repository()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "repo", ".git"));
        Write("Outside.sln", "");
        var solution = Write("repo/Shop.sln", "");
        var project = Directory.CreateDirectory(Path.Combine(_dir, "repo", "src", "Shop.Api")).FullName;

        Assert.Equal(solution, Cli.FindNearestSolution(project));

        File.Delete(solution);
        Assert.Null(Cli.FindNearestSolution(project));     // Outside.sln is beyond the top of the repository
    }

    [Fact]
    public void Finds_the_settings_file_from_any_folder_below_it()
    {
        var file = Write("sightline.json", "{}");
        var deep = Directory.CreateDirectory(Path.Combine(_dir, "src", "Shop.Api", "Controllers")).FullName;

        Assert.Equal(file, SightlineConfig.Find(deep));
    }

    [Fact]
    public void Reads_settings_written_by_hand_with_comments_and_trailing_commas()
    {
        var file = Write("sightline.json", """
            {
              "$schema": "https://sightline.revington.co/sightline.schema.json",
              // the solution everyone works on
              "solution": "src/Shop.sln",
              "exclude": ["*.Tests", "samples/**"],
              "port": 5300,
              "openBrowser": false,
              "restore": false,
            }
            """);

        var config = SightlineConfig.Load(file);

        Assert.Equal(Path.Combine(_dir, "src", "Shop.sln"), config.Solution);
        Assert.Equal(["*.Tests", "samples/**"], config.Exclude);
        Assert.Equal(5300, config.Port);
        Assert.False(config.OpenBrowser);
        Assert.False(config.Restore);
        Assert.Empty(config.Warnings);
    }

    [Fact]
    public void Warns_about_a_misspelled_setting_instead_of_failing()
    {
        var config = SightlineConfig.Load(Write("sightline.json", """{ "exlude": ["*.Tests"] }"""));

        Assert.Empty(config.Exclude);
        Assert.Contains("\"exlude\" is not a Sightline setting", Assert.Single(config.Warnings));
    }

    [Theory]
    [InlineData("""{ "port": "fast" }""", "\"port\" must be")]
    [InlineData("""{ "exclude": "*.Tests" }""", "\"exclude\" must be")]
    [InlineData("""{ "solution": 12 }""", "\"solution\" must be")]
    [InlineData("""[ "Shop.sln" ]""", "must hold a JSON object")]
    [InlineData("""{ "solution": """, "is not valid JSON")]
    public void Rejects_values_that_would_open_the_wrong_thing(string json, string message)
    {
        var error = Assert.Throws<ConfigException>(() => SightlineConfig.Load(Write("sightline.json", json)));
        Assert.Contains(message, error.Message);
    }

    [Theory]
    [InlineData("*.Tests", "tests/Shop.Tests/Shop.Tests.csproj", true)]
    [InlineData("tests", "tests/Shop.Tests/Shop.Tests.csproj", true)]
    [InlineData("tests/", "tests/Shop.Tests/Shop.Tests.csproj", true)]
    [InlineData("samples/**", "samples/Shop/Shop.Api/Shop.Api.csproj", true)]
    [InlineData("**/*.Benchmarks.csproj", "perf/App.Benchmarks/App.Benchmarks.csproj", true)]
    [InlineData("Shop.Api", "src/Shop.Api/Shop.Api.csproj", true)]
    [InlineData("shop.api", "src/Shop.Api/Shop.Api.csproj", true)]
    [InlineData("*.Tests", "src/Shop.Api/Shop.Api.csproj", false)]
    [InlineData("tests", "src/tests.Helpers/tests.Helpers.csproj", false)]
    [InlineData("samples/**", "src/samples.Shop/samples.Shop.csproj", false)]
    public void Exclude_patterns_match_project_names_folders_and_paths(string pattern, string project, bool excluded)
    {
        var filter = new ProjectFilter(_dir, [pattern]);

        Assert.Equal(excluded, filter.Excludes(Path.Combine(_dir, project)));
    }
}

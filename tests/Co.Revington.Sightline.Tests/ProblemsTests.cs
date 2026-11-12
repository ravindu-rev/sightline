using Co.Revington.Sightline.Analysis;
using Co.Revington.Sightline.Commands;
using Co.Revington.Sightline.Settings;

namespace Co.Revington.Sightline.Tests;

/// A small web app with one of each dependency injection mistake, and a layer rule it breaks.
/// The web SDK brings every package it needs, so the restore needs no download.
public sealed class ProblemsFixture : IAsyncLifetime
{
    readonly string _dir = Directory.CreateTempSubdirectory("sightline-problems-").FullName;

    internal Analysis.Graph Graph { get; private set; } = null!;
    public string Dir => _dir;

    public async Task InitializeAsync()
    {
        Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup><ProjectReference Include="../Domain/Domain.csproj" /></ItemGroup>
            </Project>
            """);
        Write("Domain/Domain.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        Write("Domain/Thing.cs", "namespace Domain; public class Thing { }");
        Write("App/Program.cs", """
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddSingleton<ICache, Cache>();
            builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
            builder.Services.AddScoped<IA, A>();
            builder.Services.AddScoped<IB, B>();
            builder.Services.AddTransient<Leftover>();
            builder.Services.AddScoped<Reports>();
            builder.Services.AddSingleton<IMadeByHand, MadeByHand>(sp => new MadeByHand(new Part()));
            builder.Services.AddScoped<ShownInRazor>();
            builder.Services.AddScoped<Settings>();
            var settings = System.Text.Json.JsonSerializer.Deserialize<Settings>("{}");
            var app = builder.Build();
            app.MapGet("/report", (Reports reports, IClock clock) => reports.Today(clock));
            app.Run();

            public interface ICache { }
            public sealed class Cache(IUnitOfWork work) : ICache { }
            public interface IUnitOfWork { }
            public sealed class UnitOfWork : IUnitOfWork { }
            public interface IA { }
            public sealed class A(IB b) : IA { }
            public interface IB { }
            public sealed class B(IA a) : IB { }
            public interface IClock { }
            public sealed class Leftover { }
            public sealed class Reports(ICache cache, Domain.Thing thing)
            {
                public string Today(IClock clock) => "ok";
            }
            public interface IMadeByHand { }
            public interface IPart { }
            public sealed class Part : IPart { }
            public sealed class MadeByHand(IPart part) : IMadeByHand { }
            public sealed class ShownInRazor { }
            public sealed class Settings { }
            """);
        Write("App/Pages/Status.razor", "@inject ShownInRazor shown\n<p>@shown</p>\n");
        Write("sightline.json", """
            {
              "layers": ["Domain", "App"],
              "rules": [ { "from": "App", "mayNotUse": ["Microsoft.Extensions.Caching*"], "because": "caching goes through ICache" } ]
            }
            """);

        var config = SightlineConfig.Load(Path.Combine(_dir, "sightline.json"));
        var target = new SolutionTarget(Path.Combine(_dir, "App", "App.csproj"), config, ProjectFilter.None, Restore: true);
        var (_, snapshot) = await OneShot.LoadAsync(target);
        Graph = snapshot.Graph!;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return Task.CompletedTask;
    }

    void Write(string relative, string text)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}

public sealed class ProblemsTests(ProblemsFixture app) : IClassFixture<ProblemsFixture>
{
    Problem One(string rule, string mentions) =>
        Assert.Single(app.Graph.Problems, p => p.Rule == rule && p.Message.Contains(mentions, StringComparison.Ordinal));

    [Fact]
    public void A_singleton_that_receives_a_scoped_service_is_an_error()
    {
        var p = One("captive-dependency", "Cache lives as long as the app");
        Assert.Equal("error", p.Severity);
        Assert.Contains("IUnitOfWork", p.Message);
        Assert.EndsWith("Program.cs", p.File);
    }

    [Fact]
    public void Services_that_need_each_other_are_an_error()
    {
        var p = One("dependency-cycle", "Creating");
        Assert.Equal("error", p.Severity);
        Assert.Contains("IA", p.Message);
        Assert.Contains("IB", p.Message);
    }

    [Fact]
    public void A_service_that_an_endpoint_asks_for_but_nobody_registers_is_reported()
    {
        var p = One("missing-registration", "IClock");
        Assert.Equal("warning", p.Severity);
        Assert.Contains("GET /report", string.Join(' ', p.Nodes));
    }

    [Fact]
    public void A_registration_nothing_asks_for_is_a_note()
    {
        var p = One("unused-registration", "Leftover");
        Assert.Equal("info", p.Severity);
        Assert.DoesNotContain(app.Graph.Problems, x => x.Rule == "unused-registration" && x.Message.StartsWith("Reports", StringComparison.Ordinal));
    }

    [Fact]
    public void Factories_razor_injects_and_type_arguments_are_not_false_alarms()
    {
        // MadeByHand gets its IPart from the factory lambda, not from dependency injection.
        Assert.DoesNotContain(app.Graph.Problems, p => p.Rule == "missing-registration" && p.Message.Contains("IPart"));
        // ShownInRazor is asked for by "@inject" in a .razor file; Settings is used as a type argument.
        Assert.DoesNotContain(app.Graph.Problems, p => p.Rule == "unused-registration" && p.Message.StartsWith("ShownInRazor", StringComparison.Ordinal));
        Assert.DoesNotContain(app.Graph.Problems, p => p.Rule == "unused-registration" && p.Message.StartsWith("Settings", StringComparison.Ordinal));
    }

    [Fact]
    public void A_project_that_uses_a_higher_layer_breaks_the_layer_rule()
    {
        var p = One("layer-violation", "App uses Domain");
        Assert.Equal("error", p.Severity);
        Assert.Contains("Domain > App", p.Message);
    }

    [Fact]
    public void Checks_can_be_switched_off_in_the_settings()
    {
        var options = new CheckOptions([], [], new Dictionary<string, string> { ["unused-registration"] = "off", ["missing-registration"] = "error" });
        var json = $$"""{ "checks": { "unused-registration": "off", "missing-registration": "error" } }""";
        var file = Path.Combine(app.Dir, "other.json");
        File.WriteAllText(file, json);
        var loaded = SightlineConfig.Load(file);

        Assert.Equal("off", loaded.Checks.Severity["unused-registration"]);
        Assert.Equal(options.Severity["missing-registration"], loaded.Checks.Severity["missing-registration"]);
        Assert.Throws<ConfigException>(() =>
        {
            File.WriteAllText(file, """{ "checks": { "no-such-check": "off" } }""");
            SightlineConfig.Load(file);
        });
    }
}

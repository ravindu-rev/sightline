using System.Diagnostics;
using Co.Revington.Sightline.Live;
using Co.Revington.Sightline.Settings;

namespace Co.Revington.Sightline.Tests;

/// "exclude" in sightline.json, on the real sample solution.
public sealed class ExcludeTests
{
    [Fact]
    public async Task Excluded_projects_stay_off_the_map_while_the_rest_is_analyzed_as_usual()
    {
        var sample = Path.Combine(ShopFixture.FindRepoRoot(), "samples", "Shop");
        var options = new LoadOptions(new ProjectFilter(sample, ["Shop.Infrastructure"]), Restore: true);
        var session = new LiveSession(Path.Combine(sample, "Shop.sln"), new EventHub(), options);

        using var stop = new CancellationTokenSource();
        var loop = Task.Run(() => session.RunAsync(stop.Token));
        try
        {
            var waited = Stopwatch.StartNew();
            while (session.Status.State == "loading")
            {
                Assert.True(waited.Elapsed < TimeSpan.FromMinutes(4), "The sample did not finish loading.");
                await Task.Delay(200);
            }
            Assert.Equal("ready", session.Status.State);

            var graph = Graph.Parse(session.Snapshot.ToJson(session.StatusJson));
            Assert.Equal(["Shop.Api", "Shop.Application", "Shop.Domain"], graph.Names("project"));
            Assert.DoesNotContain(graph.Nodes, n => n.Name is "OrderRepository" or "ShopDbContext");

            // The projects that stay are still read in full, including calls into interfaces.
            Assert.Contains("POST /api/orders", graph.Names("endpoint"));
            Assert.True(graph.HasEdge("calls", "PlaceOrderHandler.Handle", "IOrderRepository.SaveAsync"));
            Assert.EndsWith("1 left out by sightline.json", graph.Status.Message);
        }
        finally
        {
            stop.Cancel();
            await loop;
        }
    }
}

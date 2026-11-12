using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Co.Revington.Sightline.Analysis;
using Co.Revington.Sightline.Live;

namespace Co.Revington.Sightline.Server;

/// What an app with Co.Revington.Sightline.Hosting posts to /api/runtime once it has started.
internal sealed record RuntimeReport(
    int Pid, string App, string? Environment, string[]? Addresses,
    List<RuntimeService>? Services, List<RuntimeEndpoint>? Endpoints);

internal sealed record TraceSql(string Text, double Ms, string? Error);

internal sealed record TraceHttp(string Method, string Url, int? Status, double Ms);

/// One request the app handled, as it posts it to /api/trace.
internal sealed record TraceReport(
    string Id, string Method, string Path, string? Query, string? Route, int Status, double Ms, DateTime Start,
    List<TraceSql>? Sql, List<TraceHttp>? Http, string? Error);

internal sealed record TraceBatch(int Pid, string App, List<TraceReport> Traces);

/// A request as the page shows it: the report, which app sent it, and the endpoint it went to.
internal sealed record TraceRecord(
    string Id, int Pid, string App, string Method, string Path, string? Query, string? Route, int Status, double Ms, DateTime Start,
    List<TraceSql> Sql, List<TraceHttp> Http, string? Error, string? Endpoint);

/// The apps running beside the map, what they reported about themselves, and their recent requests.
internal sealed class RuntimeRegistry(LiveSession session, EventHub hub)
{
    const int MaxTraces = 300;

    readonly object _gate = new();
    readonly Dictionary<string, RuntimeApp> _apps = new(StringComparer.Ordinal);     // by app name: a restart replaces it
    readonly LinkedList<TraceRecord> _traces = new();

    public void Report(RuntimeReport report)
    {
        var app = new RuntimeApp(report.Pid, report.App, report.Environment, report.Addresses ?? [],
            report.Services ?? [], report.Endpoints ?? [], DateTime.UtcNow);

        List<RuntimeApp> apps;
        lock (_gate)
        {
            _apps[app.App] = app;
            apps = [.. _apps.Values];
        }
        session.Enqueue(new RuntimeWork(apps));
        hub.Publish("runtime", JsonSerializer.Serialize(Apps(), Snapshot.Json));
    }

    public object Apps()
    {
        lock (_gate)
        {
            return _apps.Values.OrderBy(a => a.App).Select(a => new
            {
                a.Pid, a.App, a.Environment, a.Addresses, running = IsRunning(a.Pid), seen = a.Seen,
                services = a.Services.Count, endpoints = a.Endpoints.Count,
            }).ToList();
        }
    }

    public void AddTraces(TraceBatch batch)
    {
        var graph = session.Snapshot.Graph;
        foreach (var t in batch.Traces.Take(100))
        {
            var endpoint = graph is null ? null : RuntimeMerge.EndpointFor(graph, t.Method, t.Path, t.Route);
            var record = new TraceRecord(
                Clip(t.Id, 64), batch.Pid, Clip(batch.App, 200), Clip(t.Method, 16), Clip(t.Path, 2000), t.Query is null ? null : Clip(t.Query, 2000),
                t.Route is null ? null : Clip(t.Route, 500), t.Status, t.Ms, t.Start,
                (t.Sql ?? []).Take(50).Select(s => s with { Text = Clip(s.Text, 4000) }).ToList(),
                (t.Http ?? []).Take(50).Select(h => h with { Url = Clip(h.Url, 2000) }).ToList(),
                t.Error is null ? null : Clip(t.Error, 2000), endpoint);

            lock (_gate)
            {
                _traces.AddFirst(record);
                while (_traces.Count > MaxTraces) _traces.RemoveLast();
            }
            hub.Publish("trace", JsonSerializer.Serialize(record, Snapshot.Json));
        }
    }

    /// Requests that arrived while the solution was still loading get their endpoint once it is known.
    public List<TraceRecord> Traces()
    {
        var graph = session.Snapshot.Graph;
        lock (_gate)
        {
            if (graph is not null)
            {
                for (var node = _traces.First; node is not null; node = node.Next)
                {
                    var t = node.Value;
                    if (t.Endpoint is null && RuntimeMerge.EndpointFor(graph, t.Method, t.Path, t.Route) is { } endpoint)
                        node.Value = t with { Endpoint = endpoint };
                }
            }
            return [.. _traces];
        }
    }

    /// Requests from the page may only go to apps on this machine.
    public static bool IsLocal(Uri uri) =>
        uri.Scheme is "http" or "https" && (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));

    static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }
}

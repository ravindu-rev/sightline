using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Co.Revington.Sightline.Languages;

internal sealed class SidecarException(string message) : Exception(message);

/// An analyzer that runs as its own process and reads one language with that language's own compiler.
/// They talk in JSON lines: each request { "id", "method", "params" } gets one answer { "id", "result" } or
/// { "id", "error" }, and may get events { "id", "event", ... } before it. Standard output carries nothing
/// else; standard error is kept to say why, if the process stops. All its handles are redirected, so it
/// never writes into the console of "sightline mcp" or of an app that started Sightline.
internal sealed class Sidecar : IDisposable
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    sealed record Call(TaskCompletionSource<JsonElement> Done, Action<JsonElement>? OnEvent);

    readonly Process _process;
    readonly Stream _input;
    readonly SemaphoreSlim _writing = new(1, 1);
    readonly ConcurrentDictionary<long, Call> _calls = new();
    readonly Queue<string> _errors = new();
    long _next;
    volatile bool _stopped;

    Sidecar(Process process)
    {
        _process = process;
        _input = process.StandardInput.BaseStream;
        _ = Task.Run(ReadAsync);
        _ = Task.Run(ReadErrorsAsync);
    }

    public bool Running => !_stopped;

    /// Starts program with the arguments. A program that does not exist gives a Win32Exception.
    public static Sidecar Start(string program, IEnumerable<string> args, string workingDir)
    {
        var info = new ProcessStartInfo(program)
        {
            WorkingDirectory = workingDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        // Settings meant for the user's own Node programs (preloaded modules, debuggers) do not apply here.
        info.Environment.Remove("NODE_OPTIONS");
        var process = Process.Start(info) ?? throw new Win32Exception("The process did not start.");
        return new Sidecar(process);
    }

    /// Sends a request and waits for its answer. Events for it go to onEvent, on the reading thread, in order.
    public async Task<JsonElement> CallAsync(string method, object? args, Action<JsonElement>? onEvent, CancellationToken ct)
    {
        if (_stopped) throw new SidecarException(Stopped());
        var id = Interlocked.Increment(ref _next);
        var call = new Call(new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously), onEvent);
        _calls[id] = call;

        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = args }, Json);
        await _writing.WaitAsync(ct);
        try
        {
            await _input.WriteAsync(bytes, ct);
            await _input.WriteAsync("\n"u8.ToArray(), ct);
            await _input.FlushAsync(ct);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _calls.TryRemove(id, out _);
            throw new SidecarException(Stopped());
        }
        finally
        {
            _writing.Release();
        }

        using (ct.Register(() => call.Done.TrySetCanceled(ct)))
            return await call.Done.Task;
    }

    async Task ReadAsync()
    {
        try
        {
            var output = _process.StandardOutput;
            while (await output.ReadLineAsync() is { } line)
            {
                if (line.Length == 0) continue;
                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    Remember(line);
                    continue;
                }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var idElement)
                        || !idElement.TryGetInt64(out var id) || !_calls.TryGetValue(id, out var call)) continue;

                    if (root.TryGetProperty("event", out _))
                    {
                        try { call.OnEvent?.Invoke(root); }
                        catch (Exception ex) { Remember("Sightline could not take an event: " + ex.Message); }
                        continue;
                    }

                    _calls.TryRemove(id, out _);
                    if (root.TryGetProperty("error", out var error)) call.Done.TrySetException(new SidecarException(error.ToString()));
                    else call.Done.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }

        _stopped = true;
        try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException) { }
        var why = Stopped();
        foreach (var id in _calls.Keys)
            if (_calls.TryRemove(id, out var call)) call.Done.TrySetException(new SidecarException(why));
    }

    async Task ReadErrorsAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync() is { } line) Remember(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    void Remember(string line)
    {
        lock (_errors)
        {
            _errors.Enqueue(line);
            while (_errors.Count > 12) _errors.Dequeue();
        }
    }

    string Stopped()
    {
        string code;
        try { code = _process.HasExited ? $" (exit code {_process.ExitCode})" : ""; }
        catch (InvalidOperationException) { code = ""; }
        string tail;
        lock (_errors) tail = string.Join('\n', _errors.Where(l => l.Trim().Length > 0).TakeLast(6));
        return $"The analyzer stopped{code}." + (tail.Length > 0 ? " " + tail : "");
    }

    public void Dispose()
    {
        _stopped = true;
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
        }
        _process.Dispose();
        _writing.Dispose();
    }
}

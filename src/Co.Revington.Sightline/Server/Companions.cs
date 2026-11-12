using System.ComponentModel;
using System.Diagnostics;

namespace Co.Revington.Sightline.Server;

/// Apps that run with this map beside them (Co.Revington.Sightline.Hosting starts it with --attach).
/// While any of them runs, the map stays open. When the last one stops, the map closes after a short
/// grace period, so an app that restarts (dotnet watch, a new debug session) finds the same map still
/// open, and the browser tab keeps working.
internal sealed class Companions(bool closeWithApps, TimeSpan grace)
{
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(10);

    readonly Dictionary<int, DateTime?> _apps = [];     // process id -> start time, to tell a reused id apart
    readonly object _gate = new();

    public int Count
    {
        get { lock (_gate) return _apps.Count; }
    }

    /// Keeps the map open while this process runs. False when there is no such process.
    public bool Attach(int pid)
    {
        if (!IsRunning(pid)) return false;
        lock (_gate) _apps[pid] = StartTime(pid);
        return true;
    }

    /// Calls stop once no attached app has been running for the grace period.
    /// A map that was opened by hand (not with --attach) never closes this way.
    public async Task WatchAsync(Action stop, CancellationToken ct)
    {
        if (!closeWithApps) return;

        DateTime? emptySince = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            int running;
            lock (_gate)
            {
                foreach (var (pid, started) in _apps.ToList())
                    if (!IsSame(pid, started)) _apps.Remove(pid);
                running = _apps.Count;
            }

            if (running > 0)
            {
                emptySince = null;
                continue;
            }

            emptySince ??= DateTime.UtcNow;
            if (DateTime.UtcNow - emptySince >= grace)
            {
                stop();
                return;
            }
        }
    }

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

    static DateTime? StartTime(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.StartTime;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    static bool IsSame(int pid, DateTime? started) => started is null ? IsRunning(pid) : IsRunning(pid) && StartTime(pid) == started;
}

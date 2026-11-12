using System.Diagnostics;
using Co.Revington.Sightline.Server;

namespace Co.Revington.Sightline.Tests;

/// When a map started beside an app (--attach) closes, and when it stays open.
public sealed class CompanionsTests
{
    [Fact]
    public void Joins_only_processes_that_are_running()
    {
        var companions = new Companions(closeWithApps: true, TimeSpan.Zero);

        Assert.True(companions.Attach(Environment.ProcessId));
        Assert.False(companions.Attach(int.MaxValue - 7));
        Assert.Equal(1, companions.Count);
    }

    [Fact]
    public async Task Closes_after_the_last_app_has_been_gone_for_the_grace_period()
    {
        using var app = Process.Start(new ProcessStartInfo("dotnet", "--version") { RedirectStandardOutput = true })!;
        var companions = new Companions(closeWithApps: true, TimeSpan.FromSeconds(1));
        companions.Attach(app.Id);
        await app.WaitForExitAsync();

        var stopped = new TaskCompletionSource();
        _ = companions.WatchAsync(() => stopped.TrySetResult(), CancellationToken.None);

        Assert.Same(stopped.Task, await Task.WhenAny(stopped.Task, Task.Delay(TimeSpan.FromSeconds(15))));
        Assert.Equal(0, companions.Count);
    }

    [Fact]
    public async Task Stays_open_while_an_app_runs()
    {
        var companions = new Companions(closeWithApps: true, TimeSpan.Zero);
        companions.Attach(Environment.ProcessId);

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var stopped = false;
        await companions.WatchAsync(() => stopped = true, stop.Token);

        Assert.False(stopped);
    }

    [Fact]
    public async Task A_map_opened_by_hand_never_closes_by_itself()
    {
        var companions = new Companions(closeWithApps: false, TimeSpan.Zero);
        var stopped = false;

        await companions.WatchAsync(() => stopped = true, CancellationToken.None);

        Assert.False(stopped);
    }
}

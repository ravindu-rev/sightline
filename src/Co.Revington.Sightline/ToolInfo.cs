using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;

namespace Co.Revington.Sightline;

internal static class ToolInfo
{
    public const string PackageId = "Co.Revington.Sightline";

    /// "0.2.0" for a released package. Local builds report the version in the project file.
    public static string Version { get; } = ReadVersion();

    static string ReadVersion()
    {
        var assembly = typeof(ToolInfo).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString(3)
                      ?? "0.0.0";
        var plus = version.IndexOf('+');          // drop the commit hash the SDK appends
        return plus < 0 ? version : version[..plus];
    }
}

internal sealed record DotnetResult(int Code, string Output, string Errors)
{
    public string All => $"{Output}\n{Errors}".Trim();
}

/// Runs the dotnet command line: restoring projects, and pinning the tool in a tool manifest.
internal static class DotnetCli
{
    public static async Task<DotnetResult> RunAsync(string workingDir, IEnumerable<string> args, CancellationToken ct = default)
    {
        var info = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment["DOTNET_NOLOGO"] = "1";

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new Win32Exception("The process did not start.");
        }
        catch (Win32Exception ex)
        {
            return new DotnetResult(-1, "", $"Could not run dotnet: {ex.Message}");
        }

        using (process)
        using (ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }))
        {
            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
            await process.WaitForExitAsync(CancellationToken.None);
            ct.ThrowIfCancellationRequested();
            return new DotnetResult(process.ExitCode, (await output).Trim(), (await errors).Trim());
        }
    }

    /// The last lines of a command's output: usually enough to see why it failed.
    public static string Tail(string text, int lines = 4) =>
        string.Join('\n', text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(lines));

    /// The dotnet that started this tool when the SDK says so, otherwise the one on the PATH.
    static string Executable =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } path && File.Exists(path) ? path : "dotnet";
}

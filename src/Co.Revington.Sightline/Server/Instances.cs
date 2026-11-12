using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Co.Revington.Sightline.Server;

/// Maps that are open on this machine, one small file per solution. When an app asks for a map of a
/// solution that already has one open, it joins that map instead of opening a second.
/// The files hold the page token, so they live in the user's own app-data folder, readable by them only.
internal static class Instances
{
    internal sealed record Instance(int Pid, int Port, string Token, string Target);

    static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "Sightline", "instances");

    static string FileFor(string target)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(target).ToUpperInvariant()));
        return Path.Combine(Folder, Convert.ToHexString(key)[..20] + ".json");
    }

    public static void Register(string target, int port, string token)
    {
        try
        {
            const UnixFileMode privateFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(Folder);
            else Directory.CreateDirectory(Folder, privateFolder);

            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            using var stream = new FileStream(FileFor(target), options);
            JsonSerializer.Serialize(stream, new Instance(Environment.ProcessId, port, token, Path.GetFullPath(target)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sharing a map is a convenience; this map works without it.
        }
    }

    public static void Unregister(string target)
    {
        var file = FileFor(target);
        if (Read(file)?.Pid == Environment.ProcessId) TryDelete(file);
    }

    /// Asks the map that is open for this solution, if there is one, to stay open while process pid runs.
    /// Returns that map's address, or null when no map for the solution is open.
    public static async Task<string?> JoinAsync(string target, int pid)
    {
        var file = FileFor(target);
        var instance = Read(file);
        if (instance is null || instance.Pid == Environment.ProcessId) return null;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{instance.Port}/api/attach")
            {
                Content = JsonContent.Create(new { pid }),
            };
            request.Headers.Add("X-Sightline-Token", instance.Token);

            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode) return $"http://localhost:{instance.Port}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // not answering: that map has closed
        }

        TryDelete(file);
        return null;
    }

    static Instance? Read(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<Instance>(File.ReadAllText(file)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

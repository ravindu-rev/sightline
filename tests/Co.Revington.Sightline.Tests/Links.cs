using System.Diagnostics;

namespace Co.Revington.Sightline.Tests;

/// Folder links for tests. A symbolic link needs a privilege Windows gives only to administrators or in
/// developer mode; a junction, the other kind of folder link there, needs none.
static class Links
{
    public static bool TryLinkFolder(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!OperatingSystem.IsWindows()) return false;
        }

        var start = new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var mklink = Process.Start(start)!;
        mklink.WaitForExit();
        return mklink.ExitCode == 0 && new DirectoryInfo(link).LinkTarget is not null;
    }
}

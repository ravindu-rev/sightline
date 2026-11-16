using System.Diagnostics;
using Co.Revington.Sightline.Live;

namespace Co.Revington.Sightline.Tests;

/// Which changed paths the watcher reports, on its own.
public sealed class FileWatcherTests : IDisposable
{
    // The repository sits in folders named like ones the watcher leaves alone inside a repository.
    readonly string _root = Directory.CreateDirectory(Path.Combine(Directory.CreateTempSubdirectory("sightline-watch-").FullName, "bin", "temp", "repo")).FullName;
    readonly List<string> _seen = [];

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(_root)!)!)!, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    FileWatcher Watch() =>
        new([_root], path => path.EndsWith(".ts", StringComparison.Ordinal), churning: () => true, paths => { lock (_seen) _seen.AddRange(paths); });

    bool Seen(string path)
    {
        lock (_seen) return _seen.Contains(path);
    }

    async Task Eventually(string path)
    {
        var waited = Stopwatch.StartNew();
        while (!Seen(path))
        {
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(15), "The watcher did not report " + path);
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task A_repository_inside_folders_named_bin_or_temp_is_watched()
    {
        using var watcher = Watch();
        var file = Path.Combine(_root, "a.ts");
        File.WriteAllText(file, "export const a = 1;\n");
        await Eventually(file);
    }

    [Fact]
    public async Task A_linked_folder_that_appears_is_not_walked()
    {
        using var watcher = Watch();
        var outside = Directory.CreateTempSubdirectory("sightline-watch-outside-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(outside, "secret.ts"), "export const s = 1;\n");
            Assert.True(Links.TryLinkFolder(Path.Combine(_root, "linked"), outside), "Could not make a folder link.");

            // A real file after the link: once it is reported, the link had its chance to be walked.
            var real = Path.Combine(_root, "real.ts");
            File.WriteAllText(real, "export const r = 1;\n");
            await Eventually(real);

            Assert.False(Seen(Path.Combine(_root, "linked", "secret.ts")));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task The_files_of_a_folder_moved_in_are_reported_but_build_output_is_not_walked()
    {
        using var watcher = Watch();
        var outside = Directory.CreateTempSubdirectory("sightline-watch-staging-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(outside, "pkg", "src"));
            File.WriteAllText(Path.Combine(outside, "pkg", "src", "index.ts"), "export const b = 1;\n");
            Directory.CreateDirectory(Path.Combine(outside, "dist"));
            File.WriteAllText(Path.Combine(outside, "dist", "index.ts"), "export const c = 1;\n");

            // Moved in after the other, so once the package is reported the build output had its chance too.
            Directory.Move(Path.Combine(outside, "dist"), Path.Combine(_root, "dist"));
            Directory.Move(Path.Combine(outside, "pkg"), Path.Combine(_root, "pkg"));

            await Eventually(Path.Combine(_root, "pkg", "src", "index.ts"));
            Assert.False(Seen(Path.Combine(_root, "dist", "index.ts")));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }
}

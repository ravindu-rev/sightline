using System.ComponentModel;
using System.Diagnostics;

namespace Co.Revington.Sightline.Server;

/// Opens a file at a line in the developer's editor: VS Code, Cursor, Rider, Visual Studio, or a command
/// from sightline.json. Programs are started directly with their arguments, never through a shell, so a
/// file name can never become part of a command.
internal static class EditorLauncher
{
    internal sealed record Result(bool Opened, string Editor, string? Message = null);

    static readonly Dictionary<string, string> Names = new()
    {
        ["vscode"] = "VS Code", ["cursor"] = "Cursor", ["rider"] = "Rider", ["visualstudio"] = "Visual Studio",
    };

    /// requested: what the page asked for ("" for the default). configured: "editor" in sightline.json.
    /// Both may only name a known editor: a repository someone cloned must not be able to start any
    /// program it likes. A command of your own goes in the SIGHTLINE_EDITOR environment variable.
    public static Result Open(string file, int line, string? requested, string? configured)
    {
        static string? Known(string? editor) => Normalize(editor) is { } e && Names.ContainsKey(e) ? e : null;
        var choice = Known(requested) ?? Known(configured) ?? Normalize(Environment.GetEnvironmentVariable("SIGHTLINE_EDITOR")) ?? Detect();
        if (choice is null)
            return new Result(false, "", "No editor was found. Set \"editor\" in sightline.json, for example \"rider\", or a command such as \"subl {file}:{line}\".");

        line = Math.Max(1, line);
        try
        {
            return choice switch
            {
                "vscode" => Url("vscode", $"vscode://file/{UrlPath(file)}:{line}"),
                "cursor" => Url("cursor", $"cursor://file/{UrlPath(file)}:{line}"),
                "rider" => Rider(file, line),
                "visualstudio" => VisualStudio(file, line),
                _ => Command(choice, file, line),
            };
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return new Result(false, Names.GetValueOrDefault(choice, choice), $"Could not start {Names.GetValueOrDefault(choice, choice)}: {ex.Message}");
        }
    }

    static string? Normalize(string? editor) => string.IsNullOrWhiteSpace(editor) ? null : editor.Trim() switch
    {
        var e when e.Equals("code", StringComparison.OrdinalIgnoreCase) || e.Equals("vs code", StringComparison.OrdinalIgnoreCase) => "vscode",
        var e when e.Equals("vs", StringComparison.OrdinalIgnoreCase) || e.Equals("visual studio", StringComparison.OrdinalIgnoreCase) => "visualstudio",
        var e when Names.ContainsKey(e.ToLowerInvariant()) => e.ToLowerInvariant(),
        var e => e,
    };

    /// The editor whose terminal Sightline was started from, or the first one installed.
    static string? Detect()
    {
        if (Environment.GetEnvironmentVariable("TERMINAL_EMULATOR")?.Contains("JetBrains", StringComparison.OrdinalIgnoreCase) == true) return "rider";
        if (Environment.GetEnvironmentVariable("TERM_PROGRAM") == "vscode")
        {
            var askpass = Environment.GetEnvironmentVariable("VSCODE_GIT_ASKPASS_MAIN") ?? "";
            return askpass.Contains("cursor", StringComparison.OrdinalIgnoreCase) ? "cursor" : "vscode";
        }
        if (Environment.GetEnvironmentVariable("VSAPPIDNAME") is { Length: > 0 }) return "visualstudio";

        if (OnPath("code") is not null) return "vscode";
        if (OnPath("cursor") is not null) return "cursor";
        if (RiderPath() is not null) return "rider";
        if (OperatingSystem.IsWindows() && DevenvPath() is not null) return "visualstudio";
        return null;
    }

    static string UrlPath(string file)
    {
        var path = file.Replace('\\', '/');
        if (!path.StartsWith('/')) path = "/" + path;
        return string.Join('/', path.Split('/').Select(Uri.EscapeDataString)).TrimStart('/');
    }

    /// vscode://file/... and cursor://file/... are handled by the editor itself, on every platform.
    static Result Url(string editor, string url)
    {
        using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return new Result(true, Names[editor]);
    }

    static Result Rider(string file, int line)
    {
        var rider = RiderPath() ?? throw new FileNotFoundException("Rider was not found. Set \"editor\" in sightline.json to the command that starts it, for example \"rider --line {line} {file}\".");
        Start(rider, ["--line", line.ToString(System.Globalization.CultureInfo.InvariantCulture), file]);
        return new Result(true, "Rider");
    }

    static Result VisualStudio(string file, int line)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Visual Studio runs on Windows only.");
        var devenv = DevenvPath() ?? throw new FileNotFoundException("Visual Studio was not found.");
        // /edit opens the file in the Visual Studio that is already running.
        Start(devenv, ["/edit", file, "/command", "Edit.GoTo " + line]);
        return new Result(true, "Visual Studio");
    }

    /// A command from sightline.json, such as "subl {file}:{line}". The first word is the program.
    static Result Command(string template, string file, int line)
    {
        var words = Split(template);
        if (words.Count == 0) throw new InvalidOperationException("The editor command is empty.");
        var args = words.Skip(1).Select(w => w.Replace("{file}", file).Replace("{line}", line.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToList();
        if (!words.Any(w => w.Contains("{file}", StringComparison.Ordinal))) args.Add(file);
        var program = OnPath(words[0]) ?? words[0];
        Start(program, args);
        return new Result(true, Path.GetFileNameWithoutExtension(words[0]));
    }

    static void Start(string program, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info);
    }

    /// Splits a command into words, keeping "quoted parts" together.
    static List<string> Split(string command)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in command)
        {
            if (c == '"') quoted = !quoted;
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0) words.Add(current.ToString());
                current.Clear();
            }
            else current.Append(c);
        }
        if (current.Length > 0) words.Add(current.ToString());
        return words;
    }

    /// A program on the PATH, with the extensions Windows would try.
    static string? OnPath(string name)
    {
        if (Path.IsPathRooted(name)) return File.Exists(name) ? name : null;
        var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", "" } : [""];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        foreach (var ext in extensions)
        {
            var candidate = Path.Combine(dir.Trim(), name + ext);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    static string? RiderPath()
    {
        if (OnPath("rider") is { } onPath && !onPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) && !onPath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)) return onPath;
        if (OnPath("rider64") is { } rider64) return rider64;

        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            candidates.Add(Path.Combine(local, "Programs", "Rider", "bin", "rider64.exe"));
            candidates.AddRange(Glob(Path.Combine(local, "JetBrains", "Toolbox", "apps", "Rider"), "rider64.exe"));
            candidates.AddRange(Glob(Path.Combine(programs, "JetBrains"), "rider64.exe"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidates.Add("/Applications/Rider.app/Contents/MacOS/rider");
            candidates.AddRange(Glob(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications"), "rider"));
        }
        else
        {
            candidates.AddRange(Glob(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "JetBrains", "Toolbox", "apps"), "rider.sh"));
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    static IEnumerable<string> Glob(string root, string fileName)
    {
        if (!Directory.Exists(root)) return [];
        try
        {
            return Directory.EnumerateFiles(root, fileName, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 6, IgnoreInaccessible = true })
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (IOException)
        {
            return [];
        }
    }

    /// devenv.exe of the newest Visual Studio, found with vswhere (which every Visual Studio installs).
    static string? DevenvPath()
    {
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere)) return null;
        try
        {
            var info = new ProcessStartInfo(vswhere) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-latest", "-prerelease", "-property", "productPath" }) info.ArgumentList.Add(a);
            using var process = Process.Start(info)!;
            var path = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);
            return File.Exists(path) ? path : null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }
}

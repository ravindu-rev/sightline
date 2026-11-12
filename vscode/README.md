# Sightline for VS Code

A live, editable map of your .NET solution, one click from your code: projects, dependency injection, HTTP endpoints, and the path each request takes down to the database. It updates while you type.

- **Sightline: Open the map** (or the Sightline button in the status bar) opens the map of the solution in your browser, or in a VS Code tab.
- **Show in Sightline** (right-click in a C# file, or `Ctrl+Alt+M` / `Cmd+Alt+M`) shows the method, endpoint or class under the cursor on the map: an endpoint or method as the flow of what it calls, a class with everything around it.
- **Problems panel**: while a map is open, Sightline's findings show there too: a singleton that holds a scoped service, a service that is asked for but never registered, circular dependencies, and the layer rules of your `sightline.json`.

The extension uses a map that is already open for the folder, whether you started it with `dotnet sightline` or it runs beside your app through the `Co.Revington.Sightline.Hosting` package. Otherwise it starts one: the repository's own tool when `sightline init` set it up, or `dnx Co.Revington.Sightline`.

## Needs

The .NET 10 SDK, and a folder you trust: reading a solution runs MSBuild and restores packages, which runs code from the repository, so the extension stays off in Restricted Mode.

## Settings

| Setting | Default | What it does |
| --- | --- | --- |
| `sightline.openIn` | `browser` | Open the map in the browser, or in a VS Code tab (`editor`). |
| `sightline.command` | empty | How to start Sightline, for example `sightline` for a global install. User settings only: a repository's `.vscode/settings.json` cannot set it. |
| `sightline.problems` | `true` | Show Sightline's problems in the Problems panel. |

More about Sightline at [sightline.revington.co](https://sightline.revington.co).

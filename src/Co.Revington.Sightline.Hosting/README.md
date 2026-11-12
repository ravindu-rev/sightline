# Sightline for a project

Add this package to the app you run. Every time you run it from a Debug build, Sightline, a live and editable map of your .NET solution, starts beside it:

```text
Sightline: the live map of this solution is at http://localhost:5210 (or open /sightline on this app)
```

```sh
dotnet add package Co.Revington.Sightline.Hosting
```

The map shows projects, dependency injection, HTTP endpoints and the path each request takes down to the database, and it updates while you edit.

## How it behaves

- Only Debug builds of apps get it. Release builds, test projects, CI runs, and tools that load the app such as `dotnet ef` are left alone. The package adds no assembly to your app.
- Web apps also answer `/sightline` in the Development environment, with a redirect to the map.
- The map stays open across restarts, such as `dotnet watch` or a new debug session, and closes 10 seconds after the app stops. Several apps of one solution share one map.
- It opens the solution named in `sightline.json`, or else the nearest `.sln` in the project's folder or above it.

## Settings

In the project file:

```xml
<PropertyGroup>
  <SightlineAutoStart>false</SightlineAutoStart>     <!-- do not start Sightline -->
  <SightlineOpenBrowser>true</SightlineOpenBrowser>  <!-- also open the map in the browser -->
</PropertyGroup>
```

For a single run, set the environment variable `SIGHTLINE_AUTOSTART=0`.

## Needs

The .NET 10 runtime on the machine. The app itself can target .NET 5 or newer.

The same tool without a package reference: `dnx Co.Revington.Sightline`. More at https://github.com/ravindu-rev/sightline

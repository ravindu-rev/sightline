# Sightline

A live, editable map of a .NET solution. It reads your code with Roslyn and shows, in one page:

- projects, NuGet packages and how projects use each other
- namespaces and types
- dependency injection: which class is registered for which service, and with what lifetime
- HTTP endpoints (controllers, minimal APIs, SignalR hubs, gRPC services)
- the flow behind each endpoint: handler, services, repositories, database and outbound HTTP calls

The map redraws while you edit. Code can be changed in the page itself or in your own editor.

**[Try the live demo at sightline.revington.co](https://sightline.revington.co/)**. It runs in the browser; nothing to install.

## Get started

Needs the .NET 10 SDK or newer. The package is `Co.Revington.Sightline`; the command is `sightline`.

**Try it** without installing anything. In a folder with a `.sln`:

```sh
dnx Co.Revington.Sightline
```

**Set it up for your solution**, so the whole team has it. In the folder with your `.sln`, usually the root of the repository:

```sh
dnx Co.Revington.Sightline init
```

`init` does three things, and running it again changes nothing that is already set up:

1. pins Sightline in the repository's tool manifest, `dotnet-tools.json`, creating it if needed
2. writes [`sightline.json`](#settings), which names the solution to open
3. adds a "Sightline: open the map" task to `.vscode/tasks.json`, when the folder uses VS Code. A `tasks.json` with comments is left as it is, and `init` prints the task to paste in.

Commit the files it lists. From then on, anyone who clones the repository runs:

```sh
dotnet tool restore
dotnet sightline
```

`dotnet sightline` works from any folder in the repository. Projects that were never restored are restored first, as `dotnet build` does.

**Add it to the app you run**, so the map opens beside it every time you run a Debug build:

```sh
dotnet add package Co.Revington.Sightline.Hosting
```

```text
Sightline: the live map of this solution is at http://localhost:5210 (or open /sightline on this app)
```

- Only Debug builds of apps get it. Release builds, test projects, CI runs and tools that load the app, such as `dotnet ef`, are left alone, and the package adds no assembly to the app.
- Web apps also answer `/sightline` in the Development environment, with a redirect to the map.
- The map stays open across restarts (`dotnet watch`, a new debug session) and closes 10 seconds after the app stops. Several apps of one solution share one map.
- Turn it off with `<SightlineAutoStart>false</SightlineAutoStart>` in the project file, or `SIGHTLINE_AUTOSTART=0` for one run. `<SightlineOpenBrowser>true</SightlineOpenBrowser>` also opens the map in the browser.

It is a separate package because NuGet does not let a .NET tool package be added to a project.

**Install it just for yourself**:

```sh
dotnet tool install --global Co.Revington.Sightline
sightline path\to\Your.sln
```

## Use

```sh
sightline [path] [options]
sightline init [folder] [options]
```

The path is a `.sln`, `.slnx` or `.csproj` file, or a folder. Without it, Sightline opens the solution named in the nearest `sightline.json`, or the nearest solution in the current folder or above it, up to the top of the repository. The map opens in the browser at `http://localhost:5210`.

| Option | What it does |
| --- | --- |
| `--port <n>` | Port for the map page. The next free one is used if it is busy. |
| `--no-open` | Do not open the browser. |
| `--no-restore` | Do not run `dotnet restore` for projects that were never restored. |
| `--version` | Show the version. |
| `init --solution <file>` | The solution to open, when the folder has several. Without it, `init` asks. |
| `init --vscode`, `init --no-vscode` | Add the VS Code task even without a `.vscode` folder, or never. |
| `init --no-tool` | Do not pin Sightline in the tool manifest. |

## Settings

`sightline.json` holds the settings for a repository. Sightline uses the nearest one in the current folder or a folder above it, and options on the command line win over it.

```json
{
  "$schema": "https://sightline.revington.co/sightline.schema.json",
  "solution": "Shop.sln",
  "exclude": ["*.Tests", "samples"]
}
```

| Setting | Default | What it does |
| --- | --- | --- |
| `solution` | the solution in the current folder | The `.sln`, `.slnx` or `.csproj` file to open, relative to `sightline.json`. |
| `exclude` | none | Projects to leave off the map. Each entry is a project name, a folder or a path relative to `sightline.json`, and may use `*` within a folder name and `**` across folders: `"*.Tests"`, `"samples"`, `"tools/**"`. Left-out projects still load when others reference them, so the code that uses them is read correctly. |
| `port` | `5210` | Port for the map page. |
| `openBrowser` | `true` | Open the map in the browser at start. |
| `restore` | `true` | Run `dotnet restore` first for projects that were never restored. |

The `$schema` line lets editors suggest these settings as you type. Comments and trailing commas are allowed. A misspelled setting is reported and ignored; a wrong value stops Sightline with a message saying what is expected.

## The three views

| View | Shows | Use it to answer |
| --- | --- | --- |
| Map | Projects as zones, types listed per namespace. Hover or select a type to see its lines. | What is in this solution? Which project uses which? |
| Flow | Every step behind an endpoint or method, numbered in source order. Interface calls continue in the registered implementation. | What happens when this endpoint is hit? Who ends up calling this method? |
| Focus | One type in the middle, between what uses it and what it depends on. | What breaks if I change this class? |

The left panel lists endpoints, services, types and projects. The right panel shows details of the selection and its source code.

Shortcuts: `/` find, `1` `2` `3` switch view, `F` fit to screen, double-click to open a project, focus a type or re-root a flow.

## Editing

- **In the page:** type in the code panel. The map updates as you type, before anything is saved. `Ctrl+S` or **Save file** writes to disk; **Discard changes** goes back to the file on disk.
- **In your editor:** save the file and the map follows. With auto-save on, it follows as you type.

The strip at the bottom logs what each edit changed, for example `GetOrderHandler.Handle calls IOrderRepository.SaveAsync`.

## Limits

- C# only. SDK-style projects.
- The analysis is static. It follows constructor injection, interface implementations, overrides and MediatR-style `Send`/`Publish` to handlers. It does not follow reflection, assembly scanning (for example Scrutor) or generated code.
- Endpoint discovery covers controllers, minimal APIs (including `MapGroup` prefixes passed through extension methods), `MapHub`, `MapGrpcService` and `MapHealthChecks`. FastEndpoints, Carter and Razor Pages are not recognised yet.
- Property reads and writes are not counted as calls.
- Tested on the sample solution and on Sightline itself. Not yet measured on a large solution.

## Safety

The server listens on localhost only. Every API call needs a token that is generated at start and embedded in the page, so other websites cannot read or write your source through the browser. Only files that belong to the loaded solution can be read or saved.

## Working on Sightline

```sh
dotnet run --project src/Co.Revington.Sightline -- samples/Shop/Shop.sln   # run from source on the bundled sample
dotnet test                                                               # end-to-end tests against that sample
```

- `src/Co.Revington.Sightline/Analysis` loads the solution through MSBuild, then extracts facts per file: declarations, calls, DI registrations, endpoints.
- `src/Co.Revington.Sightline/Live` watches the folder. A changed file is re-read alone, the graph is rebuilt, and only the difference is pushed to the browser.
- `src/Co.Revington.Sightline/wwwroot` is the page: plain JavaScript modules, ELK for layout, CodeMirror for the editor. No build step. Add `--ui src/Co.Revington.Sightline/wwwroot` when running to serve these files straight from source.
- `site/` is the website. `node site/generate.mjs --serve` builds it into `_site/` and previews it at `localhost:4173`. The demo on the site is the tool's own page, fed by a recording that the script takes from the real tool running on `samples/Shop`, so it cannot drift from the product. `.github/workflows/website.yml` publishes the site to GitHub Pages after every successful run of the release workflow.

## Releasing

Releases are automatic. Every push to `master` is built and tested by `.github/workflows/release.yml`. If code under `src/` changed, a second job then:

1. works out the next version from the commit messages since the last release
2. publishes the package to nuget.org from the GitHub environment `production`
3. tags the commit `vX.Y.Z` and creates a GitHub release with notes

| Commit message starts with | Version change | Example |
| --- | --- | --- |
| `feat!:` (or the body has a `BREAKING CHANGE:` line) | major, or minor while the version is 0.x | 0.3.1 to 0.4.0 |
| `feat:` | minor | 0.3.1 to 0.4.0 |
| anything else | patch | 0.3.1 to 0.3.2 |

Both packages, `Co.Revington.Sightline` and `Co.Revington.Sightline.Hosting`, are released together with the same version. To jump to a specific version such as 1.0.0, set `<VersionPrefix>` in `src/Directory.Build.props`. To release without a code change, start the workflow by hand from the Actions tab. Release notes leave out `ci`, `test`, `chore`, `docs`, `build` and `style` commits. Pull requests are built and tested by `.github/workflows/ci.yml` and never publish.

### Publishing setup

Publishing uses nuget.org trusted publishing, so no API key is stored anywhere. Three settings have to agree:

| Where | Setting | Value |
| --- | --- | --- |
| nuget.org, Trusted Publishing policy | Repository owner, repository | `ravindu-rev`, `sightline` |
| nuget.org, Trusted Publishing policy | Workflow file, environment | `release.yml`, `production` |
| GitHub, repository variable `NUGET_USER` | nuget.org user name that created the policy | `revington.co` |

The GitHub environment `production` only accepts deployments from `master`. If `NUGET_USER` is empty, the workflow still builds and tests every push but skips the release and says why.

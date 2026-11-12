# Sightline

A live, editable map of a .NET solution. It reads your code with Roslyn and shows, in one page:

- projects, NuGet packages and how projects use each other
- namespaces and types
- dependency injection: which class is registered for which service, and with what lifetime
- HTTP endpoints (controllers, minimal APIs, SignalR hubs, gRPC services)
- the flow behind each endpoint: handler, services, repositories, database and outbound HTTP calls

The map redraws while you edit. Code can be changed in the page itself or in your own editor.

## Install

Requires the .NET 10 SDK or newer.

```sh
dotnet tool install --global Sightline
```

## Use

Run `dotnet restore` on your solution first so references resolve, then:

```sh
sightline path\to\Your.sln
```

The browser opens at `http://localhost:5210`. The path can be a `.sln`, `.slnx` or `.csproj` file, or a folder. Without a path, the current folder is used.

Options: `--port <n>`, `--no-open`, `--help`.

## The three views

| View | Shows | Use it to answer |
| --- | --- | --- |
| Map | Projects as zones, types listed per namespace. Hover or select a type to see its lines. | What is in this solution? Which project uses which? |
| Flow | Every step behind an endpoint or method, numbered in source order. Interface calls continue in the registered implementation. | What happens when this endpoint is hit? Who ends up calling this method? |
| Focus | One type with what it depends on (right) and what uses it (left). | What breaks if I change this class? |

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
dotnet run --project src/Sightline -- samples/Shop/Shop.sln     # run from source on the bundled sample
dotnet test                                                     # end-to-end tests against that sample
```

- `src/Sightline/Analysis` loads the solution through MSBuild, then extracts facts per file: declarations, calls, DI registrations, endpoints.
- `src/Sightline/Live` watches the folder. A changed file is re-read alone, the graph is rebuilt, and only the difference is pushed to the browser.
- `src/Sightline/wwwroot` is the page: plain JavaScript modules, ELK for layout, CodeMirror for the editor. No build step. Add `--ui src/Sightline/wwwroot` when running to serve these files straight from source.

## Releasing

Releases are automatic. Every push to `master` is built and tested by `.github/workflows/release.yml`. If code under `src/` changed, the workflow then:

1. works out the next version from the commit messages since the last release
2. publishes the package to nuget.org
3. tags the commit `vX.Y.Z` and creates a GitHub release with notes

| Commit message starts with | Version change | Example |
| --- | --- | --- |
| `feat!:` (or the body has a `BREAKING CHANGE:` line) | major, or minor while the version is 0.x | 0.3.1 to 0.4.0 |
| `feat:` | minor | 0.3.1 to 0.4.0 |
| anything else | patch | 0.3.1 to 0.3.2 |

To jump to a specific version such as 1.0.0, set `<VersionPrefix>` in `src/Sightline/Sightline.csproj`. To release without a code change, start the workflow by hand from the Actions tab. Pull requests are built and tested by `.github/workflows/ci.yml` and never publish.

### One-time setup on nuget.org

Publishing uses nuget.org trusted publishing, so no API key is stored anywhere.

1. Sign in to nuget.org, open **Trusted Publishing** in the account menu and add a policy with repository owner `ravindu-rev`, repository `sightline`, workflow file `release.yml`, and no environment.
2. In the GitHub repository, under **Settings, Secrets and variables, Actions, Variables**, add `NUGET_USER` with your nuget.org user name (the profile name, not the email address).

Until both are done, the workflow still builds and tests every push but skips the release and says why.

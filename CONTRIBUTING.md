# Contributing to Sightline

Thanks for helping. Bug reports, ideas and pull requests are all welcome.

## Reporting a problem

Open an [issue](https://github.com/ravindu-rev/sightline/issues/new/choose). The most useful reports say what the map showed, what you expected, and the smallest piece of code that shows the difference. The **Feedback** button in the map fills in the version and the size of your solution for you, without names or paths.

Something the map does not see, such as a framework whose endpoints or message handlers it misses, is worth an issue of its own: these are often good first contributions.

Security problems go through a private report instead; see [SECURITY.md](SECURITY.md).

## Building and testing

You need the .NET 10 SDK and Node.js 20 or newer (for the JavaScript and TypeScript reader, its tests, and the website).

```sh
dotnet test                                                               # all tests, end to end
dotnet run --project src/Co.Revington.Sightline -- samples/Shop/Shop.sln   # the tool on the bundled sample
```

Add `--ui src/Co.Revington.Sightline/wwwroot` to serve the page straight from source, so a browser refresh shows your changes to it. The page is plain JavaScript modules with no build step.

The README's "Working on Sightline" section explains where things live.

## Adding a language

Every language reports the same facts about each file: the records in `src/Co.Revington.Sightline/Analysis/Facts.cs` (types, members, calls, registrations, endpoints, constructor dependencies, ...). Everything after that, the graph, the checks, the page, `check`, `export`, `diff` and `mcp`, works the same for all of them. A new language needs two things:

1. **A workspace** in `src/Co.Revington.Sightline/Languages` that implements `ILanguageWorkspace`: it finds the projects under the folder (with the same `exclude` patterns), says what a changed file means (`RoleOf`), keeps the facts, and applies edits in batches. Register it in `LanguageSet`. `TypeScript/TypeScriptWorkspace.cs` is the model to follow for a language whose compiler is not .NET.
2. **A reader** that uses that language's own compiler, so calls are resolved the way the compiler resolves them rather than guessed by name. It runs as a process of its own (`Sidecar`), speaking JSON lines on standard input and output; standard output carries nothing else:
   - `hello` gives the reader's and the runtime's versions.
   - `open { root, projects: [{ id, name, dir, files, ... }] }` reads everything. It may send `{ id, event: "progress", message }` and `{ id, event: "docs", docs: [...] }` before its answer `{ id, result: { diagnostics } }`.
   - `apply { changes: [{ path, text, project? } or { path, remove: true }] }` and `extract { paths }` answer `{ docs }`.
   - A failed request answers `{ id, error }`.

   Each doc is a `DocFacts` in JSON, with camelCase names. Ids must not depend on the machine (use the package name and the path inside it), and `declHash` must change only when declarations change, so an edit inside a function body stays cheap.

`analyzers/typescript` is a complete example in plain JavaScript. Add a sample under `samples/` and end-to-end tests that load it, like `NodeSolutionTests`.

## Pull requests

- Keep each pull request to one change, with tests for what it adds or fixes. Most tests run the real tool on `samples/Shop` or on a small solution written by the test.
- Write commit messages as [Conventional Commits](https://www.conventionalcommits.org): `feat:` for something new, `fix:` for a fix, and `docs:`, `test:`, `ci:`, `chore:` for the rest. Releases are automatic and take their version and their notes from these messages.
- Write for people who read quickly: plain words, short sentences, and comments that say why rather than what.
- Pull requests are built and tested on Linux, Windows and macOS, and get a comment showing what they change on the map of Sightline itself.

## Code of conduct

This project follows the [Contributor Covenant](CODE_OF_CONDUCT.md). Be kind.

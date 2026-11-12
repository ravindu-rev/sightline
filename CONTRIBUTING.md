# Contributing to Sightline

Thanks for helping. Bug reports, ideas and pull requests are all welcome.

## Reporting a problem

Open an [issue](https://github.com/ravindu-rev/sightline/issues/new/choose). The most useful reports say what the map showed, what you expected, and the smallest piece of code that shows the difference. The **Feedback** button in the map fills in the version and the size of your solution for you, without names or paths.

Something the map does not see, such as a framework whose endpoints or message handlers it misses, is worth an issue of its own: these are often good first contributions.

Security problems go through a private report instead; see [SECURITY.md](SECURITY.md).

## Building and testing

You need the .NET 10 SDK and Node 20 or newer (Node only for the website).

```sh
dotnet test                                                               # all tests, end to end
dotnet run --project src/Co.Revington.Sightline -- samples/Shop/Shop.sln   # the tool on the bundled sample
```

Add `--ui src/Co.Revington.Sightline/wwwroot` to serve the page straight from source, so a browser refresh shows your changes to it. The page is plain JavaScript modules with no build step.

The README's "Working on Sightline" section explains where things live.

## Pull requests

- Keep each pull request to one change, with tests for what it adds or fixes. Most tests run the real tool on `samples/Shop` or on a small solution written by the test.
- Write commit messages as [Conventional Commits](https://www.conventionalcommits.org): `feat:` for something new, `fix:` for a fix, and `docs:`, `test:`, `ci:`, `chore:` for the rest. Releases are automatic and take their version and their notes from these messages.
- Write for people who read quickly: plain words, short sentences, and comments that say why rather than what.
- Pull requests are built and tested on Linux, Windows and macOS, and get a comment showing what they change on the map of Sightline itself.

## Code of conduct

This project follows the [Contributor Covenant](CODE_OF_CONDUCT.md). Be kind.

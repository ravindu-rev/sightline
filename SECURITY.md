# Security

## Reporting a vulnerability

Please report security problems privately, through GitHub: open the [Security tab](https://github.com/ravindu-rev/sightline/security) of the repository and choose **Report a vulnerability**. Only the maintainers can see the report. Please do not open a public issue for it.

You can expect an answer within a week. Once a fix is released, the report is published with credit to you, unless you prefer otherwise.

## Supported versions

Fixes go into the latest release. Update with `dotnet tool update Co.Revington.Sightline` (or `dnx Co.Revington.Sightline init` in a repository set up for it).

## What Sightline protects

Sightline runs on your machine and serves its page on localhost only. Things worth reporting include any way for a website or another user to:

- read or change source files through the map's server, which needs a token that only the page and the app it runs beside receive;
- make the map's server send a request anywhere but an app on the same machine ("Try it");
- start a program through "Open in editor", through a repository's `sightline.json`, or through a repository's `.vscode/settings.json` with the VS Code extension;
- learn local paths or source code from an exported map or a `sightline diff` comment when the source was left out.

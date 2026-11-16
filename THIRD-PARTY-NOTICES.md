# Third-party notices

Sightline itself is released under the MIT license (see `LICENSE`).
It ships with, or depends on, the components below. Each keeps its own license.

| Component | Used for | License | Where |
| --- | --- | --- | --- |
| [elkjs](https://github.com/kieler/elkjs) 0.12.1 (Eclipse Layout Kernel) | Arranging nodes and lines in the page | EPL-2.0 | `wwwroot/lib/elk.bundled.js` and `wwwroot/lib/elk-worker.min.js`, license text in `wwwroot/lib/elk.LICENSE.md` |
| [CodeMirror](https://codemirror.net/5/) 5.65.21, with its C-like, JavaScript, XML and JSX modes | Code editor in the page | MIT | `wwwroot/lib/codemirror/`, license text in `wwwroot/lib/codemirror/LICENSE` |
| [Roslyn](https://github.com/dotnet/roslyn) 5.9.0 (`Microsoft.CodeAnalysis.*`) | Reading and analyzing C# code | MIT | NuGet dependency, bundled in the tool package |
| [TypeScript](https://github.com/microsoft/TypeScript) 6.0.3 | Reading and analyzing TypeScript and JavaScript code | Apache-2.0 | `analyzers/typescript/vendor/typescript/` (`lib/typescript.js` and the `lib/lib.*.d.ts` files from the npm package), license text in `LICENSE.txt` and notices in `ThirdPartyNoticeText.txt` there |

The Roslyn packages bring further NuGet dependencies into the tool package.
Those are distributed unmodified under the licenses stated on their nuget.org pages.

elkjs, CodeMirror and TypeScript are included unmodified. Their source code is available at the links above.

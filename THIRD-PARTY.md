# Third-party code and assets

Playfront bundles work that belongs to other people. This file lists all of it, what
licence it carries, and where the original lives. Playfront's own code is covered by
[`LICENSE.md`](LICENSE.md) and nothing here changes that.

If you own something listed below and want it removed, open an issue. Playfront is a
non-commercial project and anything contested gets pulled — the application is built to
keep running with these files absent.

---

## Code

### .NET libraries

Pulled from NuGet at build time, not stored in this repository. The main ones:

| Library | Licence | Used for |
|---|---|---|
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) | MIT | The user interface |
| [Velopack](https://github.com/velopack/velopack) | MIT | Updates |
| [WebView2](https://learn.microsoft.com/microsoft-edge/webview2/) | Microsoft, redistributable | Embedded web content |

The authoritative list is in the `.csproj` files under `src/`.

---

## Assets

Playfront recreates the look of the Xbox console dashboard, so it ships artwork that
belongs to Microsoft and to individual game publishers. **None of it is ours, and none of
it is licensed to us.** It is included because the interface is meaningless without it.

| What | Where | Belongs to |
|---|---|---|
| Navigation and status icons | `src/Playfront.App/Assets/Icons/nav-*`, `status-*` | Microsoft (extracted from the Xbox app on Windows) |
| Battery icons | `src/Playfront.App/Assets/Icons/Battery/` | Microsoft |
| Age-rating seals | `src/Playfront.App/Assets/Icons/Store/` | ESRB |
| Store logos | `src/Playfront.App/Assets/Icons/Store/` | their respective owners |
| Game cover art | `src/Playfront.App/Assets/Library/` | the publisher of each game |
| Game backgrounds and videos | shipped in releases, not in this repository | the publisher of each game |
| `Segoe-Sans-Text.ttf` | `src/Playfront.App/Assets/Fonts/` | Microsoft |

Each icon was taken from its original file rather than cropped from a screenshot, so the
provenance above is exact.

**Playfront runs without any of these.** Missing artwork degrades to empty placeholders;
it does not crash the application. That is deliberate, so that removing a contested file
is always an option.

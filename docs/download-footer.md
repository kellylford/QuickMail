<!--
  Reusable "Download" footer for release notes.

  Copy everything below the divider (from "## Download" to the end) to the END of every
  docs/release-notes-vX.Y.Z.md — AFTER the Reporting Issues footer. What is new in the
  release is what people came to read; the download table is reference material and belongs
  last.

  Replace every X.Y.Z with the version being released. The asset URLs are predictable, so
  they can be written before the release exists: GitHub serves
  https://github.com/kellylford/QuickMail/releases/download/v<TAG>/<ASSET NAME>, and the
  four asset names below are exactly what .github/workflows/quickmail.yml uploads. They go
  live the moment the release is published. Note the tag carries a leading "v" and the MSI
  file names do not.
-->

---

## Download

There are four downloads. Take a regular one unless you know your PC has an ARM processor — to check, open **Settings → System → About** and read **System type**.

| Download | When to use |
|----------|-------------|
| [**QuickMail-X.Y.Z-win.msi**](https://github.com/kellylford/QuickMail/releases/download/vX.Y.Z/QuickMail-X.Y.Z-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-X.Y.Z-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/vX.Y.Z/QuickMail-X.Y.Z-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/vX.Y.Z/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/vX.Y.Z/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 8 runtime — you do not need to install .NET separately.

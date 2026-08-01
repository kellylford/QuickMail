# Windows ARM64 Support — Plan

**Issue:** [#18 — Windows ARM64 Support](https://github.com/kellylford/QuickMail/issues/18)
**Status:** planning. Feasibility proven; not yet implemented.
**Date:** 2026-08-01

## Summary

Ship a native `win-arm64` build of QuickMail alongside the existing `win-x64` build, so
devices with Snapdragon X and similar processors run QuickMail natively instead of under
Prism emulation.

Feasibility is not in question. A full ARM64 build was produced from the current tree with
no source changes:

```
dotnet publish QuickMail/QuickMail.csproj -c Release -r win-arm64 -o out/
```

It cross-compiles on an x64 machine (crossgen2 handles the ReadyToRun step for the ARM64
target), produces a 328 MB self-contained single-file executable, and the resulting
`QuickMail.exe` carries PE machine type `0xAA64` (ARM64).

**All the remaining work is in release packaging and CI, not in application code.**

## Why it is worth doing

- **WebView2 is the largest win.** `WebView2Loader.dll` is architecture-specific and loads
  browser binaries matching the *host process*. An x64 QuickMail on an ARM64 device
  therefore renders every message through an emulated x64 browser stack. The reading pane,
  the simplified-reader path for heavy/table HTML, and message rendering are the app's most
  CPU-intensive work.
- **ReadyToRun is partly wasted under emulation.** `PublishReadyToRun` precompiles IL to
  native x64; Prism then translates that x64 at load. A native ARM64 R2R image restores the
  startup benefit the setting is there to buy.
- **Steady-state CPU and battery.** MIME parsing (MailKit), IMAP sync, SQLite queries, list
  and conversation building, and spell checking are all emulated today. On a fanless ARM64
  laptop that is battery life and thermals, not only milliseconds.
- **Possible screen reader responsiveness benefit.** Screen readers on ARM64 Windows run as
  native ARM64 processes; an emulated x64 application communicates with them across an
  architecture boundary via UIA. Whether this is perceptible is an empirical question to be
  answered during validation, not an assumption to design around.

Realistic framing: Prism emulation is competent. This is a "measurably faster and easier on
the battery" change, not a "broken becomes usable" change.

## Goals

1. A native ARM64 installer (MSI) and portable executable published with every release.
2. ARM64 installs receive automatic updates through Velopack, on their own release channel.
3. Existing x64 installs are completely unaffected — same channel, same update feed, no
   forced migration, no risk of being offered an ARM64 package.
4. The ARM64 artifact is code-signed on the same Azure Trusted Signing path as x64.

## Non-goals

- Automatic migration of an existing x64 install on an ARM64 device to the ARM64 build. This
  is not something Velopack does across channels, and building a bespoke migration is out of
  proportion to the benefit. See *Out of scope*.
- An `arm64ec` or architecture-neutral build.
- A 32-bit ARM build.

## What already works, unchanged

Verified against the current tree:

| Dependency | ARM64 status |
| --- | --- |
| `SQLitePCLRaw.lib.e_sqlite3` | ships `runtimes/win-arm64/` |
| `Microsoft.Web.WebView2` | ships `runtimes/win-arm64/native/WebView2Loader.dll` and `build/native/arm64` |
| MailKit / MimeKit | fully managed |
| `Microsoft.Identity.Client(.Desktop)` | managed; WebView2-backed broker follows the host architecture |
| `CommunityToolkit.Mvvm`, `Markdig`, `Velopack`, `AdysTech.CredentialManager` | managed |
| `Microsoft.Toolkit.Uwp.Notifications` | managed WinRT interop |
| `System.Drawing.Common` | managed wrapper over OS GDI+ |

Application code requiring **no** change:

- `UpdateCheckService` / `VelopackRuntime` — `UpdateManager` is constructed without an
  explicit channel, so Velopack resolves the channel from the installed application's own
  metadata. An ARM64 install packed on the `win-arm64` channel will look for
  `releases.win-arm64.json` on its own. *(To be confirmed during the local `--updateFeed`
  cycle in Phase 3 — this is the single behavioural assumption in the plan.)*
- The portable-exe update path (`CheckViaGitHubApiAsync`) compares release tags and links to
  the release page; it does not select an asset, so it is architecture-agnostic.

## Phase 1 — Build parameterization

`QuickMail/QuickMail.csproj` currently hardcodes `<RuntimeIdentifier>win-x64</RuntimeIdentifier>`.

Keep it as the default (so `build.bat` and every existing local workflow behave exactly as
today) and override with `-r win-arm64` on the ARM64 publish invocation only. This was the
form used for the trial build and it works.

Add an ARM64 target to `build.bat` for local convenience:

```
build.bat publish-arm64
```

`AppendRuntimeIdentifierToOutputPath=false` (kept deliberately, see the csproj comment on
Dependabot's design-time build) means both architectures share `bin/` and `obj/`. Building
one after the other overwrites intermediate output. This is harmless for CI, where each job
starts from a clean checkout, but locally an ARM64 publish leaves ARM64 output in
`bin/Release` until the next normal build. Document it in `build.bat`; do not "fix" it by
re-enabling the RID path suffix.

## Phase 2 — On-demand installer workflow

Extend `.github/workflows/build-installer.yml` with an `architecture` input
(`x64` / `arm64` / `both`) so a signed, testable ARM64 MSI can be produced from any branch
without cutting a release. This workflow already injects the real Google OAuth and
bug-report credentials from repository secrets, which a local build cannot do.

This is the artifact used for validation, and it lands before any change to the release
workflow.

Runner: `windows-latest` (x64) is sufficient — cross-compilation is proven. Running the job
on a `windows-11-arm` runner is unnecessary for building, though it is an option later if
native test execution is wanted.

## Phase 3 — Release workflow and Velopack channels

### Channel strategy

Velopack's default channel is `win`, and two architectures **cannot share a channel** — the
feed would offer each architecture's package to the other. Velopack's own documentation
suggests migrating existing users to a `win-x64` channel and adding `win-arm64`.

**Do not migrate.** Every installed copy of QuickMail already polls `releases.win.json`;
renaming that channel would strand them all on their current version permanently. Instead:

- x64 stays on the existing default `win` channel. No change, no risk.
- ARM64 packs with `--runtime win-arm64 --channel win-arm64`.

The cost is a cosmetic inconsistency (`win` means x64) documented in `docs/INSTALLER.md`. The
benefit is that the update path for every existing user is untouched.

### Workflow changes (`.github/workflows/quickmail.yml`)

1. Second `dotnet publish` with `-r win-arm64` into `publish-arm64/`.
2. Second `vpk pack` with `--runtime win-arm64 --channel win-arm64 --packDir publish-arm64`,
   same signing, shortcuts, and installer wizard arguments as x64.
3. `vpk download github --channel win-arm64` before it, for delta generation. The first ARM64
   release has no predecessor; the existing `continue-on-error: true` already covers that.
4. Rename the ARM64 MSI to `QuickMail-<version>-win-arm64.msi` — `vpk` emits an unversioned
   `QuickMail-win-arm64.msi`, mirroring the existing x64 rename.
5. Sign and upload the ARM64 portable exe with a distinguishing asset name.

**Verification step before the first release:** confirm the ARM64 pack's emitted asset
filenames do not collide with the x64 set in the same GitHub release. Velopack suffixes
package and feed files by channel (`releases.win-arm64.json`, `assets.win-arm64.json`), but
the legacy `RELEASES` file and the `.nupkg` naming must be inspected in a real `vpk pack`
output, not assumed. A collision here would corrupt the x64 feed, which is the highest-risk
item in this plan.

### WebView2 bootstrapper

`--framework webview2` is passed to both packs. The WebView2 Evergreen bootstrapper detects
the device architecture and installs the matching runtime, so the same flag is correct for
ARM64 — confirm the ARM64 Setup actually installs the ARM64 runtime on a machine without it.

## Phase 4 — Documentation

- `docs/INSTALLER.md` — the two-channel layout, the "`win` means x64" note, and the ARM64
  entry in the local `--updateFeed` test procedure.
- `docs/USER-GUIDE.md` — which download to choose, and how to tell which build is running.
- Release notes for the shipping version — including that an existing x64 install on an ARM64
  device will not auto-switch, and how to move over (uninstall, install the ARM64 MSI; the
  profile in `%APPDATA%\QuickMail` and Windows Credential Manager entries are preserved).
- `CLAUDE.md` — the ARM64 build target in the Build & Run section.

## Infrastructure changes

- **CommandRegistry:** none.
- **AutomationProperties.Name values:** none added or changed.
- **AccessibilityHelper.Announce calls:** none added.
- **F6 ring:** unchanged.
- **VM state properties:** none.
- **Files touched:** `QuickMail.csproj` (comment only, RID stays), `build.bat`,
  `.github/workflows/build-installer.yml`, `.github/workflows/quickmail.yml`,
  `docs/INSTALLER.md`, `docs/USER-GUIDE.md`, `CLAUDE.md`.

No keyboard walkthrough section appears in this plan because the change introduces no UI, no
new control, and no new interaction — it produces a second binary of an unchanged
application. The validation checklist below covers behaviour instead.

## Validation

Performed by Kelly on ARM64 hardware. The plan is not considered delivered until each passes.

**Pre-release, from the on-demand installer workflow artifact (Phase 2):**

1. ARM64 MSI installs on ARM64 Windows without the "does not support your CPU architecture"
   error (this was a real Velopack defect, since fixed — confirm the fix holds in 1.2.0).
2. Application launches; Task Manager reports the process as ARM64, not "x64 (ARM64
   compatible)".
3. Reading pane renders; `msedgewebview2.exe` child processes are ARM64.
4. Screen reader behaviour across the message list, folder tree, reading pane, compose
   window, and settings — with attention to responsiveness relative to the x64 build.
5. IMAP sync, send, search, calendar, contacts, notifications and tray behaviour.
6. Google and Microsoft OAuth sign-in (the reason the CI-built artifact is required — a local
   build has placeholder credentials).
7. Subjective comparison against the x64 build on the same machine: startup time, message
   open latency, sync duration, battery drain.

**Pre-release, update feed (Phase 3):**

8. The local `--updateFeed` cycle from `docs/INSTALLER.md`, run against an ARM64 pack, proving
   an ARM64 install finds and applies an ARM64 update.
9. An x64 install is **not** offered the ARM64 package, and an ARM64 install is **not**
   offered the x64 package.

## Risks

| Risk | Mitigation |
| --- | --- |
| ARM64 assets collide with x64 assets in the GitHub release and corrupt the x64 update feed | Inspect real `vpk pack` output before the first release; test both feeds with `--updateFeed` locally |
| Velopack ARM64 installer defects | Validate the MSI from the on-demand workflow before any release change lands |
| Two artifacts to test every release | Accepted. The x64 build remains the reference; ARM64 is validated per release on real hardware |
| Release workflow becomes long and harder to reason about | Keep the ARM64 steps as a parallel matrix leg rather than duplicated inline steps where practical |

## Out of scope

- Automatic architecture migration for existing x64 installs on ARM64 devices. Users switch
  manually; the release notes explain how.
- ARM64 test execution in CI. Tests continue to run on x64 runners. A `windows-11-arm` test
  leg can be added later if an architecture-specific failure is ever observed.
- Performance instrumentation or benchmarking harness. The comparison in validation step 7 is
  subjective and that is sufficient for the decision at hand.
- `arm64ec`, ARM32, and architecture-neutral builds.
- Any change to how the application behaves at runtime. If a behavioural difference between
  the two architectures is found, it is a bug to be filed separately, not part of this work.

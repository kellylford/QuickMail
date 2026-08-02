# .NET 8 End of Support — Plan

**Issue:** [#472 — Migrate to .NET 10 before .NET 8 end of support](https://github.com/kellylford/QuickMail/issues/472)
**Status:** planning. No code changed yet.
**Date:** 2026-08-01
**Deadline:** 2026-11-10 (about 14 weeks from this date)

## The facts

.NET 8 is an LTS release that shipped 2023-11-14 with a 36-month support window. That window
closes on **November 10, 2026**. After that date Microsoft ships no further servicing updates,
security fixes, or technical support for .NET 8.

.NET 9 is **not** an escape hatch — it is an STS release and reaches end of support on the
**same day**, November 10, 2026.

| Version | Released | Track | End of support |
| --- | --- | --- | --- |
| .NET 8 | 2023-11-14 | LTS | **2026-11-10** |
| .NET 9 | 2024-11-12 | STS | **2026-11-10** |
| .NET 10 | 2025-11-11 | LTS | 2028-11-14 |
| .NET 11 | ~2026-11 | STS | ~2028-11 |

**Decision: move QuickMail to .NET 10 (LTS).** It buys two more years, it is already shipped
and mature (out since November 2025, so roughly a year of servicing by the time we migrate),
and it avoids the annual churn of the STS track. .NET 11 lands in the same month as the
deadline and is STS — targeting it would mean doing this again in 2027.

Sources:
- [.NET and .NET Core Support Policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
- [.NET 8 and .NET 9 will reach End of Support on November 10, 2026](https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/)
- [Breaking changes in .NET 10](https://learn.microsoft.com/en-us/dotnet/core/compatibility/10)

## Why this matters even though QuickMail ships self-contained

It is tempting to conclude that end of support is somebody else's problem, because QuickMail
publishes `SelfContained=true` / `PublishSingleFile=true` and bundles the runtime — no user
ever installs .NET, and nothing on a user's machine stops working on November 11.

That reasoning is wrong in a specific way, and the way it is wrong is the reason to act:

- **We are the ones shipping the runtime.** The bundled .NET 8 runtime inside
  `QuickMail.exe` is ours. Every release published after 2026-11-10 would carry a runtime
  that will never receive another security patch, and it would keep carrying it for as long
  as the app stays on .NET 8. A CVE in the BCL, the crypto stack, or the HTTP stack after
  that date has no fix available to us at all.
- **Every installed copy is affected at once.** Because the runtime is bundled, users cannot
  patch it independently the way they could with a framework-dependent app. The only delivery
  mechanism for a runtime fix is a QuickMail release — and there would be no fix to deliver.
- **Automated security tooling will start failing.** The repo runs CodeQL, Dependabot, and
  `NuGetAuditMode=all`. Once .NET 8 is out of support these begin flagging an unsupported
  runtime and unpatched transitive advisories with no upgrade path.
- **The toolchain drifts out from under CI.** GitHub Actions runner images drop preinstalled
  SDKs that have left support, and package authors stop targeting or testing EOL TFMs.
  MailKit, Microsoft.Identity.Client, Microsoft.Data.Sqlite, and WebView2 all ship frequently;
  new versions will eventually raise their floor above `net8.0`, and Dependabot updates will
  start failing to restore.

None of that is an emergency on November 11. All of it is an emergency six months later, at
which point the migration is being done under pressure alongside whatever else is broken.

## Goals

1. All four projects target `net10.0-windows10.0.17763.0`, building clean with the existing
   analyzer settings (`AnalysisMode=Recommended`, `WarningsAsErrors` in the test project).
2. CI, the installer pipeline, and the Velopack update path all work on .NET 10.
3. A release built on .NET 10 ships and updates existing users cleanly through Velopack.
4. **No accessibility regression.** This is the acceptance bar that matters most and it is
   the one that no automated test fully covers — see *Verification* below.
5. Done and released before 2026-11-10.

## Non-goals

- Adopting .NET 10 / C# 14 language or library features. This migration changes the TFM and
  whatever is required to keep the build green. Feature adoption is separate work.
- Adopting the WPF Fluent theme (`ThemeMode`). QuickMail has its own theming system and a
  visual verification harness; switching theme infrastructure is a separate, deliberate project.
- Trimming or NativeAOT. Trimming remains unsafe with WPF/XAML and is off for good reason.
- Replacing `Microsoft.Toolkit.Uwp.Notifications` (see *Known package risk*).

## Breaking changes that actually apply to this codebase

Checked against the tree, not assumed. Each row records what was found.

| Breaking change | Applies here? | Evidence |
| --- | --- | --- |
| **WinForms/WPF `MenuItem`, `ContextMenu`, `Menu`, `ToolBar`, `StatusBar`, `DataGrid` become ambiguous** ([doc](https://learn.microsoft.com/en-us/dotnet/core/compatibility/windows-forms/10.0/menuitem-contextmenu)) | **Probably already mitigated — verify** | QuickMail sets both `UseWPF` and `UseWindowsForms` with `ImplicitUsings=enable`, which is exactly the trigger. But `QuickMail.csproj` already does `<Using Remove="System.Windows.Forms" />` and `<Using Remove="System.Drawing" />`, and no `.cs` file has an explicit `using System.Windows.Forms;`. The ambiguity comes from the implicit global usings, which are already removed. XAML is unaffected (XAML resolves by namespace URI, not C# usings). Expect this to be a non-event; confirm with the first build. |
| **Incorrect `DynamicResource` usage now crashes** ([doc](https://learn.microsoft.com/en-us/dotnet/core/compatibility/wpf/10.0/dynamicresource-crash)) | **Low risk — but 545 usages, so verify by running** | The crash case is a type mismatch, e.g. a `SolidColorBrush` bound to a property expecting a `Color`. There are zero `Color="{DynamicResource …}"` bindings in the tree, and `ThemeService.BuildTokenDictionary` constructs real `SolidColorBrush` objects into a `ResourceDictionary` consumed by brush-typed properties — the correct pattern. Previously this failed silently with an `InvalidOperationException` in the output window; in .NET 10 it is a `XamlParseException` at runtime. Any latent mistake becomes a crash, so every theme must be exercised. |
| **Empty `ColumnDefinitions` / `RowDefinitions` disallowed** ([doc](https://learn.microsoft.com/en-us/dotnet/core/compatibility/wpf/10.0/empty-grid-definitions)) | **No** | No empty definition collections found in any `.xaml`. |
| **Single-file apps no longer probe the executable directory for native libraries** ([doc](https://learn.microsoft.com/en-us/dotnet/core/compatibility/interop/10.0/native-library-search)) | **Needs testing — highest-risk item** | QuickMail is `PublishSingleFile` with `IncludeNativeLibrariesForSelfExtract`, so `WebView2Loader.dll` and `e_sqlite3.dll` extract to a temp directory at first run. All hand-written `DllImport`s target OS DLLs (`user32`, `gdi32`, `dwmapi`) and use the default search path, which still includes the assembly directory — those are fine. The risk is in how the WebView2 and SQLitePCLRaw loaders resolve their own native bits. This is not verifiable by inspection: it must be tested against a **published single-file exe**, not `dotnet run`. |
| **`BinaryFormatter` removed** | **No** | No `BinaryFormatter`, no `[Serializable]`, no `.resx` files in the repo. |
| **`dotnet` CLI logs non-command-relevant data to stderr** ([doc](https://learn.microsoft.com/en-us/dotnet/core/compatibility/sdk/10.0/dotnet-cli-stderr-output)) | **Check `build.bat` and `scripts/`** | `build.bat` already has a known habit of masking compile failures behind exit code 0. Changing what lands on stderr can shift that behaviour in either direction. Re-verify that a deliberate compile error still fails the build and fails CI. |
| **`dotnet` CLI `--interactive` defaults to `true` in user scenarios** ([doc](https://learn.microsoft.com/en-us/dotnet/core/compatibility/sdk/10.0/dotnet-cli-interactive)) | **Watch CI** | The SDK detects CI environments, so Actions should be unaffected. If a restore ever hangs waiting for auth after the bump, this is the cause; pass `--interactive false`. |
| **`dotnet restore` audits transitive packages by default** ([doc](https://learn.microsoft.com/en-us/dotnet/core/compatibility/sdk/10.0/nugetaudit-transitive-packages)) | **Already opted in** | Both projects set `NuGetAuditMode=all`. The property becomes redundant but stays harmless. Keep it — it documents intent. |
| **`PackageReference` without a version is an error** | **No** | Every `PackageReference` in the tree carries an explicit `Version`. |

## Known package risk

`Microsoft.Toolkit.Uwp.Notifications` **7.1.3** is the final release of an archived,
unmaintained package. It is used for new-mail toasts via `ToastNotificationManagerCompat`,
which is why the TFM carries the Windows-10 `17763` platform version at all. Microsoft's
stated successor is the Windows App SDK app-notifications API — a substantially different
model, not a drop-in swap.

It should keep working on .NET 10 (it targets `net5.0-windows10.0.17763.0`, which .NET 10
consumes fine), and its vulnerable `System.Drawing.Common 4.7.0` dependency is already pinned
up to `4.7.2` in `QuickMail.csproj`. **Treat this as a verification item, not a rewrite item:**
confirm toasts still raise and still activate the right message on .NET 10. If it breaks,
that is a separate spec and a separate schedule, and it must be discovered in September, not
in November.

Everything else looks clear: Velopack multi-targets `net8.0`/`net9.0`/`net10.0`, and
`vpk pack` here is invoked with `--framework webview2` — it bootstraps WebView2 only and does
not declare a .NET runtime dependency, because the app is self-contained.

## What changes

23 references to .NET 8 exist outside release notes and planning docs. They fall into four
groups.

**1. Target frameworks (4 files)** — `net8.0-windows10.0.17763.0` → `net10.0-windows10.0.17763.0`:

- `QuickMail/QuickMail.csproj`
- `QuickMail.Tests/QuickMail.Tests.csproj`
- `QuickMail.IntegrationTests/QuickMail.IntegrationTests.csproj`
- `Tools/QuickMail.Fixtures/QuickMail.Fixtures.csproj`

The `17763` platform floor stays. It is required for the WinRT toast API and is unrelated to
the runtime version.

**2. CI SDK pins (5 occurrences in 4 workflows)** — `dotnet-version: '8.0.x'` → `'10.0.x'`:

- `.github/workflows/quickmail.yml` (two jobs: build and the release/installer job)
- `.github/workflows/build-installer.yml`
- `.github/workflows/codeql.yml`
- `.github/workflows/live-smoke.yml`

**3. Documentation and user-facing text:**

- `CLAUDE.md` — the one-line project description
- `README.md` — the SDK prerequisite link
- `USERGUIDE.md` — "Both downloads include the .NET 8 runtime"
- `installer/README.md` (two places), `installer/quickmail.iss` (header comment)

**4. Code comments referencing .NET 8 behaviour** — accurate today, need a second look
rather than a blind find-and-replace, because two of them describe version-specific behaviour
that may have changed:

- `QuickMail/Models/IcsModel.cs:412` — IANA/Windows time zone id resolution
- `QuickMail/Services/OAuthService.cs:148` — embedded WebView2 browser on `net8.0-windows`
- `QuickMail/Services/ThemeService.cs:605` — "**.NET 8 has no managed API**" for the OS
  light/dark app-mode setting. This is the interesting one: `Application.ThemeMode` exists
  from .NET 9. The registry-reading workaround stays correct and does not have to change, but
  the comment is now wrong and the note in
  `docs/planning/theming-visual-design-pm-dev-spec.md` should be reconciled.

**Not a change:** `installer/CodeDependencies.iss` contains `dotnet80` / `dotnet80desktop` /
`dotnet80asp` runtime-installer entries. That file is a vendored Inno Setup helper library and
`installer/quickmail.iss` never calls those functions — QuickMail is self-contained and
installs no runtime. Leave it alone.

Also add a **`global.json`** pinning the SDK feature band with `rollForward: latestFeature`.
There is none today. It costs nothing and it makes the local toolchain and CI agree about
which SDK is in use — worth having at exactly the moment two SDK majors are installed
side by side on the same machine.

## Verification

The `dotnet build` succeeding proves almost nothing here. QuickMail's whole value is in the
accessibility layer, and UIA behaviour is precisely the kind of thing that shifts between WPF
runtime versions without any compile-time signal.

**Automated (necessary, not sufficient):**

1. Full test suite in Release, including the STA/WPF tests:
   `dotnet test QuickMail.Tests/QuickMail.Tests.csproj -c Release`
2. Integration tests (GreenMail / Radicale / TcpRelay / WireMock harness, `QuickMail.IntegrationTests`).
3. `SelectorItemAccessibilityTests`, `TypeAheadWiringTests`, `ThemedControlCoverageTests`,
   `XamlParseTests` — these guard exactly the surfaces most likely to move.
4. `powershell -File scripts\ui-probe.ps1` across every surface × theme × scale entry in
   `scripts/ui-probe-plan.json`, then a diff of the captures against a pre-migration run on
   .NET 8. WPF rendering and default control metrics can shift between runtime majors; this
   harness exists so that claim can be measured instead of guessed. Run the .NET 8 baseline
   **before** starting the migration — after the TFM changes it is too late to capture one.

**Manual, on a published single-file exe (`build.bat publish`), not `dotnet run`:**

5. **Screen reader walkthrough of the primary journeys**, driven by Kelly, not by AI
   inspection of the UIA tree. Message list navigation and field chooser, reading pane and
   WebView2 body, compose in both plain and rich modes, folder picker, settings, calendar
   agenda, F6 ring across every window. Any change in what is announced — including
   announcements appearing that did not before — is a blocking regression.
6. **WebView2 reading pane** — HTML rendering, the simplified-reader path for heavy/table
   messages, plain-text link handling, and the injected F6 relay.
7. **SQLite local store** — first-run creation, round-trip, and `--online` mode.
   Combined with item 6, this is the real test of the single-file native-library change.
8. **Toast notifications** — new-mail toast raises, activating it opens the right message,
   close-to-tray still works.
9. **All built-in themes**, exercising every dialog, to flush out any latent `DynamicResource`
   type mismatch that used to fail silently and now crashes.
10. **Velopack update cycle** — install a .NET 8 build, then update it to a .NET 10 build via
    the local `--updateFeed` procedure in `docs/INSTALLER.md`. The runtime major changing
    inside a self-contained package should be invisible to Velopack, but "should be" is not
    a release criterion.

## Sequencing

**The ARM64 work goes first, and the two do not overlap.** ARM64 support
(`docs/planning/windows-arm64-support-plan.md`, issue #18) is in flight and is entirely a
packaging and CI change; this one is a runtime change touching every project. Interleaving
them means an ARM64 packaging failure and a runtime regression are indistinguishable. Land
and ship ARM64, then do this on a clean main.

A convenient side effect: .NET 10 improves ARM64 code generation, so the ARM64 build gets
faster once both have landed — but sequencing ARM64 first is about diagnosability, not that.

Suggested schedule against the 2026-11-10 deadline:

| Window | Work |
| --- | --- |
| Now | Capture the `ui-probe` baseline on .NET 8 while main is still on .NET 8. Install the .NET 10 SDK alongside .NET 8. |
| After ARM64 ships | Branch, bump the four TFMs and the five CI pins, add `global.json`, build, fix whatever the compiler says. Expect this part to be short. |
| Same branch | Automated verification (items 1–4), then the manual pass (items 5–10). Budget most of the calendar time here — item 5 is Kelly's time and cannot be compressed or delegated. |
| By early October | Merge to main and cut a release. |
| Buffer to 2026-11-10 | Reserved for whatever the manual pass turns up, and for a point release if a regression reaches users. |

Shipping in October rather than November is deliberate. If the .NET 10 release introduces a
regression that only real-world use exposes, there needs to be room for a fix release while
.NET 8 is still receiving security patches and rolling back is still a supported option.

## Out of scope

- Migrating off `Microsoft.Toolkit.Uwp.Notifications` to the Windows App SDK. Verify it works
  on .NET 10; if it does not, that becomes its own spec.
- The WPF Fluent theme / `ThemeMode`, and replacing the `ThemeService` registry workaround
  with `Application.ThemeMode`. Both are real .NET 9+ opportunities. Neither belongs in a
  migration whose success criterion is "nothing changed."
- C# 14 language features, collection expression rewrites, or any opportunistic refactor. A
  migration diff that is only TFM bumps and forced fixes is a migration that can be reviewed
  and reverted.
- Splitting Models and protocol services into a platform-neutral library (raised in
  `docs/planning/live-content-testing-plan.md`). Sensible, unrelated, would triple the diff.
- Changing the `17763` Windows platform floor or the minimum supported Windows version.
- Trimming, NativeAOT, or revisiting `PublishReadyToRun`.

## Open questions

1. **Does anything in the toolchain need to stay on .NET 8?** `vpk` is installed as a global
   tool (`dotnet tool install -g vpk`); confirm the installed version runs on a machine where
   .NET 10 is the newest runtime, and note whether the .NET 8 runtime must remain installed
   locally for it.
2. **Is a .NET 8 support branch worth keeping** for a period after the .NET 10 release, so a
   critical user-facing bug can ship to anyone who hits a .NET 10 regression? Probably not
   worth the maintenance for a single-maintainer project, but it is a decision, not an
   oversight — and it is only available if it is decided before the release, not after.
3. **Do the GitHub Actions `windows-latest` images still preinstall .NET 8** at migration
   time? Not blocking either way, since `actions/setup-dotnet` pins explicitly, but it affects
   how long CI takes and whether a fallback is needed if a job is ever run without setup-dotnet.

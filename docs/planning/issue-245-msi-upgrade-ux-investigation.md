# Issue #245 — MSI major-upgrade UX investigation

Investigation of the two-phase "uninstall the old version, then install the new
version" behavior (plus a data-removal prompt) seen when running a newer QuickMail
MSI over an existing MSI install. Non-blocking; the end state is correct.

## What was verified

The MSI upgrade authoring is **correct**. Across builds the three MSIs share one
stable `UpgradeCode`, each carries a distinct `ProductCode`, `ProductVersion`
increments, and the `Upgrade` table is properly authored (`WIX_UPGRADE_DETECTED` /
`WIX_DOWNGRADE_DETECTED`). A major upgrade therefore succeeds.

| Version | ProductCode | UpgradeCode | ProductVersion |
|---------|-------------|-------------|----------------|
| 0.8.1 | `{5BE56B83…}` | `{4F6E83C5…}` | 0.8.1.0 |
| 0.8.2 | `{DF436B3F…}` | `{4F6E83C5…}` | 0.8.2.0 |
| 0.8.3 (test build) | `{2F0F7B6C…}` | `{4F6E83C5…}` | 0.8.3.0 |

The two-phase experience comes from **`RemoveExistingProducts` scheduling**. In the
MSI's `InstallExecuteSequence`:

| Seq | Action |
|----:|--------|
| 1400 | InstallValidate |
| **1401** | **RemoveExistingProducts** |
| 1500 | InstallInitialize |
| 4000 | InstallFiles |
| 6600 | InstallFinalize |

`RemoveExistingProducts` at 1401 (right after `InstallValidate`, before
`InstallInitialize`) is WiX's `afterInstallValidate` scheduling: Windows Installer
fully uninstalls the old product first — running the old Velopack bootstrap's
uninstall, which is the source of the data-removal prompt — and only then installs
the new one.

## Why this is hard to "just fix"

1. **vpk exposes no control over it.** vpk **1.2.0 is the current release** (no newer
   version exists on NuGet as of this writing). `vpk pack` has no option to change
   `RemoveExistingProducts` scheduling or to suppress the uninstall prompt. The MSI
   knobs are limited to `--msi`, `--msiVersion`, `--instLocation`, banner/logo images,
   and the welcome/license/readme/conclusion text. The MSI is generated from
   Velopack's internal WiX 5 template, which is not overridable from this repo.

2. **The MSI is a one-time bootstrap, not the intended upgrade path.** Per Velopack's
   docs: *"After installation, updates work identically via `Update.exe` regardless of
   whether the app was installed with `Setup.exe` or the `.msi`."* The supported
   version-to-version path is the in-app auto-updater (`Update.exe`), which installs
   in place with no MSI churn. Re-running a newer MSI over an older one is an
   off-the-happy-path flow for a bootstrap installer — hence the rough edges.

3. **Rescheduling `RemoveExistingProducts` later is risky here.** Moving it to
   `afterInstallExecute`/`afterInstallFinalize` (install-new-then-remove-old) is the
   usual way to get a single-phase upgrade, but for a **PerUser** Velopack bootstrap
   both the old and new bootstraps target the same `%LocalAppData%\QuickMail`
   location. Removing the old product *after* laying down the new one risks the old
   uninstall deleting freshly-installed files. The early scheduling is very likely a
   deliberate choice for this architecture. Any post-pack MSI surgery to reschedule
   would require thorough real-machine install/upgrade/rollback testing.

## Options

- **A — Document the MSI as initial-install / enterprise deployment; steer
  version-to-version upgrades to the auto-updater (recommended).** Matches Velopack's
  design. No packaging changes. The two-phase MSI-over-MSI path remains but is not the
  path users are guided toward.
- **B — File an upstream Velopack feature request** for configurable
  `RemoveExistingProducts` scheduling and/or a silent MSI uninstall during major
  upgrade. This is the only route to a clean single-phase MSI upgrade without unsafe
  local MSI edits.
- **C — Post-pack MSI transform to reschedule `RemoveExistingProducts`
  (not recommended).** Technically possible (edit `InstallExecuteSequence` after
  `vpk pack`), but unsupported, fragile against vpk template changes, and risky for the
  PerUser same-location bootstrap. Would need extensive install testing.

## Recommendation

Option **A**, optionally with **B**. The correct, supported upgrade path is the
auto-updater; the MSI's job is first-time (and machine-wide/Group-Policy) deployment.
If the MSI-over-MSI experience must be smoothed, it needs an upstream Velopack change,
not a local reschedule.

Related: #244 (MSI offering repair rather than upgrade) is a *different* symptom —
most likely the auto-updater updating files in place while leaving the registered MSI
`ProductCode`/version stale, so a later MSI run sees inconsistent state. This issue is
specifically the uninstall-then-install UX of a clean MSI-over-MSI major upgrade.

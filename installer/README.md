# QuickMail Installer

An [Inno Setup 6](https://jrsoftware.org/isdl.php) script that packages QuickMail into a
Windows installer (`quickmail-v<version>-setup.exe`).

## Prerequisites

- The .NET 8 SDK (to publish QuickMail).
- [Inno Setup 6](https://jrsoftware.org/isdl.php) — `ISCC.exe` must be installed at the
  default location under `Program Files (x86)\Inno Setup 6` or `Program Files\Inno Setup 6`.

## Building

From the repository root:

```bat
build.bat installer
```

This publishes the self-contained single-file executable to `publish\QuickMail.exe`, then
compiles the installer to `installer\Output\quickmail-v<version>-setup.exe`.

To recompile the installer without re-publishing (when `publish\QuickMail.exe` already
exists), run the compiler directly:

```bat
"%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" installer\quickmail.iss
```

## What gets installed

QuickMail publishes as a **self-contained, single-file** `win-x64` executable: the .NET 8
runtime is bundled inside `QuickMail.exe`, so the installer ships only that one file and
requires **no .NET runtime install**.

The single external prerequisite is the **Microsoft Edge WebView2 Runtime** (used to render
HTML mail). It is preinstalled on Windows 11 and recent Windows 10; when missing, the
installer downloads and installs it on demand.

## Behavior

- **Per-user install by default**, with no elevation required. The user may choose an
  all-users (Program Files) install from the standard privileges dialog, which elevates.
- **Version** is read from `QuickMail.exe`'s file version at compile time, so the installer
  and output filename always match the build.
- On **upgrade**, if QuickMail is running the Restart Manager prompts the user to close it
  before files are replaced.
- On **uninstall**, the user is offered the choice to remove their data under
  `%APPDATA%\QuickMail` (accounts config, local mail cache, contacts, rules, templates and
  saved views). Saved passwords in Windows Credential Manager are never touched.

## Files

| File | Purpose |
|---|---|
| `quickmail.iss` | Main Inno Setup script. |
| `CodeDependencies.iss` | Standard dependency-installer helper (WebView2 detection/install). Vendored verbatim; do not edit. |
| `Languages/Custom.en.isl` | Custom English messages not provided by Inno's `Default.isl`. |
| `Output/` | Compiled installers land here (gitignored). |

# Installer

`installer/quickmail.iss` is an Inno Setup 6 script that packages the **self-contained single-file** `publish/QuickMail.exe` into a Windows installer. `build.bat installer` runs `dotnet publish` then `ISCC.exe`, emitting `installer/Output/quickmail-v<version>-setup.exe` (gitignored).

Key facts:

- **Only `QuickMail.exe` is shipped.** Because the build is self-contained (the .NET 8 runtime is bundled in the exe), there is **no .NET runtime dependency**. The `.pdb` and `Microsoft.Web.WebView2.*.xml` files that also appear in `publish/` are intentionally excluded.
- **The single external prerequisite is the WebView2 Runtime**, installed on demand via `Dependency_AddWebView2` from the bundled `installer/CodeDependencies.iss` (the standard DomGries dependency helper, vendored verbatim — leave it unmodified).
- **Version is read from the exe** at compile time via `GetVersionNumbersString`, so it always matches `FileVersion` in the csproj — never hardcode it in the `.iss`.
- **Per-user install by default** (`PrivilegesRequired=lowest` + `PrivilegesRequiredOverridesAllowed=dialog`); the user may opt into an all-users install, which elevates.
- **No app mutex exists**, so `CloseApplications=yes` uses the Restart Manager to detect a running copy during an upgrade.
- Uninstall offers to delete user data under `%APPDATA%\QuickMail`; Credential Manager entries are left untouched.
- English-only UI strings live in `installer/Languages/Custom.en.isl` (define only messages not already in Inno's `Default.isl`).
- The installer is a downstream packaging artifact — it does **not** alter the GitHub Actions release, which still ships the bare self-contained `QuickMail.exe`.

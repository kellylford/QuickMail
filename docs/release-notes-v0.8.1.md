# QuickMail v0.8.1

## Automatic updates

Starting with this release, QuickMail keeps itself up to date. When a newer version is
available, the installed app downloads it quietly in the background and installs it the next
time you exit and reopen QuickMail — no download page, no installer to run, no security
warnings. The Help menu continues to show whether you are current.

### One-time step for existing installed users

This release uses a new installer, so getting onto the automatic-update track requires one
manual reinstall:

1. Uninstall your current QuickMail from **Settings → Apps**. When the uninstaller offers to
   delete user data, **choose No**.
2. Download and run **QuickMail-win-Setup.exe** from this release page.
3. Start QuickMail. All your accounts, settings, contacts, rules, templates, saved views, and
   cached mail are exactly as you left them.

Your data is safe throughout: settings and mail live in a separate location the installer
never touches, and passwords stay in Windows Credential Manager. After this one reinstall,
all future updates are automatic.

Notes on the new install:

- QuickMail now installs per-user (no administrator prompt). The previous option to install
  for all users is gone.
- A Start Menu entry is created; no desktop shortcut is added.

### Portable exe users

Nothing changes. `QuickMail.exe` remains a single-file download that you update manually;
the Help menu still tells you when a new version is available.

## Other changes

<!-- Fill in remaining changes for this release before tagging. -->

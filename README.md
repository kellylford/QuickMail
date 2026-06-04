# QuickMail

A keyboard-first WPF desktop email client for Windows. Multi-account IMAP/SMTP with a unified inbox, conversation threading, and an HTML reading pane.

## Features

- **Multi-account** — connect any number of IMAP/SMTP accounts simultaneously
- **Unified inbox** — all mail from all accounts in one sorted view
- **Conversation view** — threads grouped by subject with collapsible tree
- **Pooled IMAP connections** — background sync, previews, attachment downloads, and message opening can run without reusing a busy IMAP connection
- **Folder tree shortcut** — Ctrl+2 or Ctrl+Y moves focus to the main folder tree
- **Search folders** — Ctrl+Shift+F opens a virtualized flat folder list; press / in the list to search
- **Responsive reading pane** — complex marketing HTML falls back to a simplified reader mode
- **HTML rendering** — WebView2 with strict CSP (no email-embedded scripts)
- **Keyboard-first** — full navigation, reply, forward, and delete.
- **Secure credentials** — passwords stored in Windows Credential Manager, never in plain text

## Requirements

- Windows 10/11 x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download) (to build from source)
- [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (usually pre-installed on Windows 11)

## Build & Run

```bat
build.bat            # build
build.bat run        # build + launch
build.bat publish    # self-contained win-x64 exe → publish/
build.bat installer  # publish + build the Windows installer → installer/Output/
build.bat clean
```

Building the installer additionally requires [Inno Setup 6](https://jrsoftware.org/isdl.php).
It produces `installer/Output/quickmail-v<version>-setup.exe`. See [`installer/README.md`](installer/README.md)
for details.

Or with the CLI:

```bash
dotnet run --project QuickMail
```

## Keyboard Shortcuts

| Key | Action |
|-----|--------|
| Ctrl+0 | Focus toolbar |
| Ctrl+1 | Focus account list |
| Ctrl+2 / Ctrl+Y | Focus folder tree |
| Ctrl+3 | Focus message list / conversation tree |
| Ctrl+9 | Focus status bar |
| F6 / Shift+F6 | Cycle through panes |
| Ctrl+N | New message |
| Ctrl+R | Reply |
| Ctrl+Shift+R | Reply all |
| Ctrl+F | Forward |
| Ctrl+Shift+F | Search folders |
| Delete | Delete selected message(s) / conversation |
| Escape | Close reading pane |

## Project Layout

```
QuickMail/
├── App.xaml.cs              # DI composition root
├── Views/
│   ├── MainWindow.xaml(.cs) # 3-pane layout; keyboard nav; WebView2
│   ├── ComposeWindow.xaml   # New / Reply / Forward
│   ├── AccountManagerDialog.xaml
│   └── FolderPickerWindow.xaml  # Destination picker for move/copy/go-to-folder commands
├── ViewModels/
│   ├── MainViewModel.cs     # Master state
│   ├── ComposeViewModel.cs
│   └── AccountManagerViewModel.cs
├── Services/
│   ├── ImapService.cs       # IMAP via MailKit; client pool per account
│   ├── SmtpService.cs       # Send via MailKit
│   ├── SyncService.cs       # Background sync
│   ├── ConversationBuilder.cs
│   ├── AccountService.cs    # Persist accounts to %APPDATA%\QuickMail\
│   ├── CredentialService.cs # Windows Credential Manager
│   ├── ConfigService.cs     # config.ini + hotkey settings
│   ├── LocalStoreService.cs # SQLite cache
│   └── LogService.cs
└── Models/
    ├── AccountModel.cs
    ├── MailMessageSummary.cs / MailMessageDetail.cs
    ├── ConversationGroup.cs
    ├── MailFolderModel.cs
    └── ComposeModel.cs
```

## CI / Releases

Every push to `main` and every pull request builds and uploads `QuickMail.exe` as an artifact via GitHub Actions.

To publish a release, push a version tag:

```bash
git tag v1.0.0
git push origin v1.0.0
```

This triggers a GitHub Release with the self-contained exe attached.

## Dependencies

| Package | Purpose |
|---------|---------|
| [MailKit](https://github.com/jstedfast/MailKit) | IMAP + SMTP |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | ObservableProperty, RelayCommand |
| [Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite/) | Local mail cache |
| [Microsoft.Web.WebView2](https://developer.microsoft.com/microsoft-edge/webview2/) | HTML email rendering |
| [AdysTech.CredentialManager](https://github.com/AdysTech/CredentialManager) | Windows Credential Manager |

## IMAP Concurrency

QuickMail opens a small per-account IMAP connection pool. This prevents MailKit's "ImapClient is currently busy" error when background sync is running and you open a message, preview text, download attachments, or move/copy mail. The default is 6 simultaneous IMAP connections per account and can be changed in `%AppData%\QuickMail\config.ini` with `MaxImapConnectionsPerAccount`.

Foreground actions such as opening messages and downloading attachments get reserved connection capacity. Background sync, polling, UID checks, and preview fetching are deliberately capped below the full pool so they do not occupy every available connection.

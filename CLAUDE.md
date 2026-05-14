# QuickMail

WPF desktop email client (.NET 8, C#). Multi-account IMAP/SMTP with unified inbox, keyboard-centric UI.

## Build & Run

```bat
build.bat          # build
build.bat run      # build + launch
build.bat publish  # self-contained win-x64
build.bat clean
```

Or directly: `dotnet run --project QuickMail`

## Project Layout

```
QuickMail/QuickMail/
├── App.xaml.cs              # DI composition root — wires all services/VMs
├── Views/
│   ├── MainWindow.xaml(.cs) # 3-pane layout; keyboard nav; WebView2 HTML rendering
│   ├── ComposeWindow.xaml   # New/Reply/Forward dialog
│   ├── AccountManagerDialog.xaml
│   └── FolderPickerWindow.xaml  # Destination picker for move/copy/go-to-folder commands
├── ViewModels/
│   ├── MainViewModel.cs     # Master state (accounts, folders, messages, selection)
│   ├── ComposeViewModel.cs  # Compose/reply/forward factories
│   └── AccountManagerViewModel.cs
├── Services/
│   ├── ImapService.cs       # IMAP via MailKit; client pool keyed by account Guid
│   ├── SmtpService.cs       # Send via MailKit
│   ├── AccountService.cs    # Persist accounts to %APPDATA%\QuickMail\accounts.json
│   ├── CredentialService.cs # Windows Credential Manager (no plaintext passwords)
│   └── LogService.cs
├── Models/
│   ├── AccountModel.cs
│   ├── MailMessageSummary.cs / MailMessageDetail.cs
│   ├── MailFolderModel.cs
│   └── ComposeModel.cs
└── Styles/AccessibleStyles.xaml
```

## Key Conventions

- **MVVM strictly**: views bind to VM properties/commands; no logic in code-behind except UI-only concerns (keyboard shortcuts, WebView2 navigation)
- **IMAP client pool**: `ImapService` leases one `ImapClient` per active operation from a bounded per-account pool; never run two MailKit commands on the same client concurrently
- **Foreground IMAP priority**: message detail and attachment downloads use foreground leases; background sync, UID checks, polling, and preview fetches use background leases capped below the full pool
- **"All Mail" virtual folder**: identified by `FullName == "\x00AllMail"`; aggregates all accounts sorted newest-first
- **Passwords**: never written to JSON; always round-trip through `CredentialService` (Windows Credential Manager)
- **HTML sandbox**: WebView2 `NavigateToString` with strict CSP — no scripts, no object/embed, no frames
- **Heavy HTML rendering**: build reading-pane HTML off the UI thread; large/table-heavy/data-image messages use simplified reader mode before `NavigateToString`
- **Cancellation**: use separate token sources for connect, folder load, message body load, message mutations, and background sync so unrelated work does not cancel accidentally
- **Pagination**: messages fetched in batches of 100; "Load More" appends next batch
- **IMAP concurrency config**: `MaxImapConnectionsPerAccount` in `%APPDATA%\QuickMail\config.ini` defaults to 6 and is clamped to 1-15
- **Folder shortcuts**: `Ctrl+2` and `Ctrl+Y` focus the main `FolderList` TreeView; do not route those shortcuts to `FolderPickerWindow`
- **Folder picker**: `FolderPickerWindow` is a flat virtualized searchable list, not a `TreeView`; keep construction cheap for large Gmail label sets

## Keyboard Shortcuts (MainWindow)

| Key | Action |
|-----|--------|
| Ctrl+0 | Focus toolbar |
| Ctrl+1 | Focus account list |
| Ctrl+2 / Ctrl+Y | Focus folder tree |
| Ctrl+3 | Focus message list / conversation tree |
| Ctrl+9 | Focus status bar |
| F6 / Shift+F6 | Cycle panes |
| Ctrl+N | New message |
| Delete | Delete selected messages |
| Escape | Close reading pane |

## Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| MailKit | 4.16.0 | IMAP + SMTP protocol |
| CommunityToolkit.Mvvm | 8.4.2 | ObservableProperty, RelayCommand |
| Microsoft.Data.Sqlite | 10.0.7 | Local message cache |
| Microsoft.Web.WebView2 | 1.0.3912.50 | HTML email rendering |
| AdysTech.CredentialManager | 3.1.0 | Windows Credential Manager |

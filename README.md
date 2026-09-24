# QuickMail

A screen reader friendly, keyboard-first email program for Windows. It works with Gmail, Outlook.com, Microsoft 365, iCloud, and any IMAP or POP3 account, and includes a unified inbox, conversation threading, a calendar, and an address book.

QuickMail is built to work correctly out of the box with any screen reader, with no custom scripts required.

## Download

Get the latest version from the [Releases page](https://github.com/kellylford/QuickMail/releases/latest). Most people should take the `.msi` installer; it installs without administrator rights and keeps QuickMail up to date automatically. ARM builds and portable executables are also available.

The [User Guide](https://kellylford.github.io/QuickMail/) covers everything QuickMail does. In the app, press **F1** to open it.

## Features

- **Many account types** — Gmail, Outlook.com, Microsoft 365 (including shared mailboxes), iCloud, and any IMAP or POP3 account, as many as you like at once
- **Unified inbox** — mail from every account in one view
- **Conversations and grouping** — view messages as a flat list, as conversation threads, or grouped by sender or recipient
- **Calendar** — your own appointments plus Microsoft, Google, and iCloud calendars, with reminders and meeting invitation responses
- **Address book** — contacts and groups, with optional sync from your online accounts
- **Mail rules** — sort incoming mail automatically, including server-side rules for Microsoft 365
- **Search** — quick search and advanced search across your mail
- **Offline reading** — a local cache lets you read mail without a connection
- **Compose** — templates, spell checking, and rich text formatting
- **Customizable** — keyboard shortcuts, screen reader announcements, what message rows say, saved views, and themes
- **Safe HTML reading pane** — scripts and remote content in messages are blocked
- **Secure credentials** — passwords are kept in Windows Credential Manager, never in plain text

## Keyboard Shortcuts

A few to get started:

| Key | Action |
|-----|--------|
| F6 / Shift+F6 | Move between panes |
| Ctrl+1 / Ctrl+2 / Ctrl+3 | Account list / folder tree / message list |
| Ctrl+N | New message |
| Ctrl+R / Ctrl+Shift+R | Reply / reply all |
| Ctrl+F | Forward |
| Delete | Delete |
| Ctrl+Shift+S | Search messages |
| Ctrl+Shift+F | Search folders |
| Ctrl+Shift+B | Address book |
| Ctrl+Shift+C | Calendar |
| Ctrl+Shift+P | Command palette — every command, searchable |
| F1 | User Guide |

Every shortcut can be changed in **Settings**. See the User Guide's [Keyboard Shortcuts](https://kellylford.github.io/QuickMail/keyboard-shortcuts.html) section for the complete list, and [`docs/KEYBOARD-SHORTCUTS.md`](docs/KEYBOARD-SHORTCUTS.md) for the developer reference.

## Requirements

- Windows 10 or Windows 11, x64 or ARM64
- [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) — the installer adds it if missing

Releases include the .NET runtime, so there's nothing else to install.

## Building from Source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). `global.json` pins the 10.0 band with `rollForward: latestMajor`, so a newer SDK also works.

```bat
build.bat                # debug build
build.bat run            # build and launch
build.bat release        # release build
build.bat publish        # self-contained x64 exe -> publish\
build.bat publish-arm64  # self-contained ARM64 exe -> publish-arm64\
build.bat installer      # publish and package the installer and update packages
build.bat clean
```

Or run it directly:

```bash
dotnet run --project QuickMail
```

The `installer` target needs the Velopack CLI (`dotnet tool install -g vpk`). See [`docs/INSTALLER.md`](docs/INSTALLER.md) for packaging and automatic updates.

Run the tests with:

```bash
dotnet test QuickMail.Tests/QuickMail.Tests.csproj -c Release
```

## Project Layout

| Folder | Contents |
|--------|----------|
| `QuickMail/` | The WPF app: `Views`, `ViewModels`, `Services`, `Models`, `Controls`, `Themes` |
| `QuickMail.Tests/` | Unit and UI tests (xUnit) |
| `QuickMail.IntegrationTests/` | Tests against real mail servers |
| `Tools/` | Test fixtures and a test-mail generator |
| `relay/` | The service that files in-app bug reports as GitHub issues |
| `scripts/` | Release, documentation, and visual-testing scripts |
| `docs/` | User Guide, release notes, and design documents |

[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) describes how the services fit together.

## Releases

Every push to `main` and every pull request is built by GitHub Actions. Pushing a `v*` tag publishes a GitHub Release with the installers and portable executables attached, and republishes the User Guide.

## Contributing and Support

- Report a problem from inside QuickMail (**Help → Report a Bug**) or on [GitHub Issues](https://github.com/kellylford/QuickMail/issues)
- See [`CONTRIBUTING.md`](CONTRIBUTING.md) and [`SECURITY.md`](SECURITY.md)
- [Privacy policy](https://kellylford.github.io/QuickMail/privacy.html)

## Built With

[MailKit](https://github.com/jstedfast/MailKit) (IMAP, POP3, SMTP), Microsoft Graph and [MSAL](https://github.com/AzureAD/microsoft-authentication-library-for-dotnet) (Microsoft accounts), [Google.Apis.Auth](https://github.com/googleapis/google-api-dotnet-client) (Google accounts), [WebView2](https://developer.microsoft.com/microsoft-edge/webview2/), [Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite/), [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet), [Markdig](https://github.com/xoofx/markdig), and [Velopack](https://velopack.io/).

## License

MIT — see [`LICENSE`](LICENSE).

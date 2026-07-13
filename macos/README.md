# QuickMail for Mac

A native macOS version of QuickMail, written in Swift (SwiftUI + AppKit).
This is an MVP on the `mac/native-swift` branch — a working, keyboard-centric,
screen-reader-first IMAP/SMTP client that mirrors the Windows app's concepts
without sharing its code (yet — see "Strategy" below).

## Why native Swift instead of a cross-platform UI

The cross-platform .NET UI stacks (Avalonia, MAUI/Catalyst, Uno) all treat
macOS accessibility as an adapter layer, and VoiceOver support is the least
mature part of each. For an app whose core promise is a first-class screen
reader experience, only AppKit/SwiftUI gives VoiceOver the same standing that
WPF/UIA gives on Windows.

### Strategy for not maintaining two full apps

The long-term option worth pursuing: split the Windows app into a **headless
cross-platform core** and thin native UIs. Nearly all of QuickMail's business
logic — MailKit IMAP/SMTP, SQLite local store, rules, templates, conversation
building — is portable .NET that runs fine on macOS. Run it as a local
child process speaking JSON-RPC (the language-server pattern), with a thin
SwiftUI shell on Mac and the existing WPF shell on Windows. One protocol and
business-logic codebase, two small UI layers, each with native accessibility.

This MVP is structured to make that migration cheap: everything under
`Sources/QuickMailCore` is UI-free and reachable only through a handful of
async calls from `AppState` (the UI's single state object). Swapping the Swift
engine for a .NET core service later means replacing `AppState`'s internals,
not the views.

## Layout

```
macos/
  QuickMailMac/            Swift package
    Sources/QuickMailCore/ mail engine: IMAP, SMTP, MIME, Keychain, accounts
    Sources/QuickMailMac/  the app: SwiftUI views + AppState
    Sources/qmcli/         headless smoke-test CLI for the engine
    Tests/                 unit tests (IMAP parser, MIME, RFC 2047, renderer)
  Tools/dev-mail-server.py local plaintext IMAP+SMTP server for development
  Scripts/make-app.sh      builds macos/build/QuickMail.app
```

## Build & run

Requires Xcode (Swift 5.10+ toolchain), macOS 14+.

```sh
cd macos/QuickMailMac
swift build                 # compile everything
swift test                  # run unit tests
../Scripts/make-app.sh      # release build -> macos/build/QuickMail.app
../Scripts/make-app.sh run  # build + launch
```

Startup flags (parity with Windows):
- `--debug` — verbose logging to stderr
- `--profileDir <path>` — override the data directory
  (default `~/Library/Application Support/QuickMail`)

### Local end-to-end testing without a real account

```sh
python3 macos/Tools/dev-mail-server.py         # IMAP :1143, SMTP :1025, password "test"
# Headless engine smoke:
.build/debug/qmcli imap 127.0.0.1 1143 dev@example.com test
.build/debug/qmcli smtp 127.0.0.1 1025 dev@example.com jane@example.com
# GUI against the dev server (isolated profile, keychain bypassed):
QM_DEV_PASSWORD=test ./QuickMail --debug --profileDir /tmp/qm-dev-profile
```

`QM_DEV_PASSWORD` is honored **only** when `--profileDir` is also given; real
profiles always use the Keychain. (Note: keychain items created with the
`security` CLI get an `apple-tool:` partition list and hang/prompt when the
app reads them — seed passwords through the app's own account editor, or use
the env override for automated runs.)

## What works (verified against the dev server)

- Multi-account IMAP over TLS or plaintext; passwords in the macOS Keychain
- Folder list with special-use detection (Sent/Drafts/Trash/Junk/Archive)
- Message list: newest 200 envelopes, unread/flagged state, VoiceOver labels
  ("Unread, Jane Doe, subject, date" as one element per row)
- Reading pane: native header block + sandboxed WKWebView
  (JavaScript disabled, strict CSP, all network loads blocked, remote images
  never fetched, links open in the default browser)
- Attachments: listed with size, save via standard save panel
- Compose / Reply / Reply All / Forward with quoting, signatures,
  In-Reply-To/References threading headers; SMTP send + best-effort copy to Sent
- Mark read/unread, flag, archive, delete (moves to Trash; expunges in Trash)
- Mail.app-standard shortcuts: ⌘N new, ⌘R reply, ⇧⌘R reply all, ⇧⌘F forward,
  ⇧⌘U read/unread, ⇧⌘L flag, ⌃⌘A archive, ⌘⌫ delete, ⇧⌘N get new mail,
  ⇧⌘D send
- Result/status announcements through `NSAccessibility` (the analogue of
  `AccessibilityHelper.Announce`), plus a visible status line

## Known limitations (MVP)

- **Auth**: password/app-password only. No OAuth yet — Gmail and Outlook.com
  need an app password or a provider that allows basic IMAP auth.
- **SMTP**: implicit TLS (port 465) or plaintext. STARTTLS-on-587 not yet
  implemented (Network.framework cannot upgrade a connection mid-stream;
  needs a custom framer). Gmail/Fastmail/iCloud/Yahoo all support 465.
- **Online-only**: no local SQLite cache yet; folders fetch on demand.
- Not yet ported: unified inbox, conversations/sender grouping, rules,
  templates, saved views, contacts, calendar/ICS, spell-check announcements,
  command palette, update checks.
- The announcement categories (Hints/Status/Results config gating) are not yet
  user-configurable; results and errors are announced, hints are not baked in.

## Accessibility notes

- Every actionable control has a short `accessibilityLabel` (no role names,
  no instructions), matching the repo's accessibility checklist.
- Message rows collapse to a single accessibility element with unread/flagged
  state first.
- Pane navigation uses the standard macOS mechanisms (Tab / VoiceOver
  VO-arrows / ⌥⌘-arrows in split views) rather than a custom F6 ring; if a
  dedicated pane-cycle key is wanted, that's a small follow-up.
- The reading pane's HTML content is exposed through WebKit's own
  accessibility tree, which VoiceOver handles natively.

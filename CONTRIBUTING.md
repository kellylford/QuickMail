# Contributing to QuickMail

Thank you for your interest in contributing. QuickMail exists to be the email client that gets accessibility right — contributions are held to the same bar as the rest of the codebase.

## Before you start

For anything beyond a small bug fix, **open an issue first** and describe what you want to do. This avoids duplicated effort and lets us align on approach before code is written.

## Getting started

1. Fork the repository and create a branch from `main`.
2. Use a descriptive branch name: `feature/what-it-does`, `fix/what-it-fixes`, `accessibility/what-it-improves`.
3. Read [`CLAUDE.md`](CLAUDE.md) — it is the authoritative reference for architecture, conventions, and enforced rules. The sections on MVVM rules, modal dialog rules, and keyboard shortcuts are especially important.

## Building and testing

```bat
build.bat          # debug build
build.bat run      # build + launch
build.bat publish  # self-contained win-x64 exe
```

Run the full test suite before submitting:

```bat
dotnet test QuickMail.Tests/QuickMail.Tests.csproj -c Release
```

All tests must pass. New features and bug fixes should include tests. See the existing test classes in `QuickMail.Tests/` for patterns — stub implementations are in `StubServices.cs` so tests never make real network or credential calls.

## Accessibility requirements

QuickMail's design bar is not "technically compliant" — it is a keyboard-first, screen-reader-friendly experience that works correctly out of the box with any screen reader, without custom scripting.

Every contribution must meet these expectations:

- **All custom announcements go through `AccessibilityHelper.Announce()`** — never call `RaiseNotificationEvent` directly. Always pass a `category` argument (`Hint`, `Status`, or `Result`).
- **No instructional text in `AutomationProperties.HelpText`** — static `HelpText` bypasses the user's hint-suppression setting. Deliver instructional text as an `AnnouncementCategory.Hint` at the moment the control is focused.
- **No `AutomationProperties.Name` values that contain instructions** — the name should be a short label. Hints belong in programmatic announcements.
- **All keyboard shortcuts registered in `CommandRegistry`** — see the keyboard shortcuts section of `CLAUDE.md`. No raw `if (key == ...)` blocks for new actions.
- **MVVM strictly** — no `MessageBox`, `Window`, or UI types in ViewModels; no `Dispatcher` calls in ViewModels; no business logic in code-behind.

## Code style

- Follow the patterns already in the file you are editing.
- No business logic in code-behind (`.xaml.cs`) — only UI-only concerns: focus, keyboard routing, dialogs requested by the VM, WebView2 behavior.
- Services must have matching `I*.cs` interfaces and stub implementations in `StubServices.cs`.
- File-writing services use atomic temp-then-rename writes.
- Passwords are never written to JSON — always use `CredentialService`.

## Commit messages

- Imperative mood, short first line: `Fix Alt+Enter on folder tree showing stale folder`, not `Fixed` or `Fixes`.
- If the change needs explanation, add a blank line and a body paragraph.
- One logical change per commit.

## Pull requests

- Fill in the pull request template.
- Keep PRs focused — one feature or fix per PR makes review faster.
- Link to the relevant issue if one exists.
- The CI build must be green before a PR can be merged.

## Questions

Open an issue or start a discussion. We are happy to help you get oriented.

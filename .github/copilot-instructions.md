# GitHub Copilot Instructions — QuickMail

See `CLAUDE.md` at the repository root for full project context. Key rules that apply to all AI-assisted work:

## Screen readers

Do not name a specific screen reader product (NVDA, JAWS, VoiceOver, Narrator, etc.) in code comments, documentation, release notes, commit messages, or UI text unless the content is genuinely specific to that product. Use the generic term **screen readers** instead.

## Language and UI text

- Use "activate", "select", "choose", or "press" — not "click".
- Do not use emoji in documentation or UI strings unless explicitly requested.

## Architecture

- **No business logic in code-behind.** ViewModels own state and commands; Views handle focus, keyboard routing, dialogs triggered by VM events, and WebView2 hosting only.
- **No `MessageBox` or `Window` references in ViewModels.** Surface confirmation requests via events or callbacks.
- **No `Dispatcher` calls in ViewModels.**
- Passwords are stored only via `CredentialService` (Windows Credential Manager), never in JSON.

## Keyboard shortcuts

Every new user-facing shortcut must be registered in `CommandRegistry` via `Register(new CommandDefinition(...))`. Registrations are split between two files: `MainWindow.xaml.cs` (the larger share, in `OnLoaded`: commands that need the window's controls or focus) and `MainViewModel.cs` (VM-backed actions such as mail, view, account and help, in `RegisterCommands`; themes, saved views and setting-dependent commands in their own methods and partials). Follow whichever file already registers the related commands, and add the new command's row to `docs/KEYBOARD-SHORTCUTS.md` (`KeyboardShortcutsDocTests` fails without it). Do not add raw key-matching branches in `PreviewKeyDown` for new commands.

## Logging

Do not log credentials, email addresses, message subjects, or other PII at default log levels. PII may appear only under the `/debug` startup flag.

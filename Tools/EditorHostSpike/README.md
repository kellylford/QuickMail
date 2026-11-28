# Editor host spike (#1099, Stage 1)

**State: built, run and checked in the test VM on 2026-11-25. Waiting for Kelly's go or no-go.**
Nothing here is part of `QuickMail.sln`, and nothing on this branch merges to main without Kelly's
decision.

## What it is

A compose-shaped WPF window (menu, To, Subject, body, Send, status line) whose message body is
WinUI 3's `RichEditBox`, hosted in a WinUI XAML island (`DesktopWindowXamlSource`) inside an
`HwndHost`, which is how QuickMail would host it. It follows Microsoft's Islands sample,
WindowsAppSDK-Samples `Samples/Islands/cs-winforms-unpackaged`:

- `Program.cs` runs Velopack first (as QuickMail's `Main` does), creates a dispatcher queue on the
  UI thread and a WinUI `Application` (`XamlApp`, which supplies `XamlControlsXamlMetaDataProvider`
  and `XamlControlsResources`). It passes every message through `ContentPreTranslateMessage` (from
  `Microsoft.UI.Windowing.Core.dll`) via `ComponentDispatcher.ThreadFilterMessage` before WPF sees it.
- `RichEditBoxHost.cs` is the `HwndHost`. A plain "Static" child window is the island's parent.
  All the focus and key handoff between WPF and the island is in this one file.
- `ComposeWindow.cs` adds QuickMail-style keys: Alt+Y to the body, F6/Shift+F6 between To, Subject
  and body, Ctrl+Enter to send, Ctrl+D to open a dialog, Ctrl+Shift+P for a command palette (a
  modeless WPF window), Escape to close, menu access keys, Format > Bold, and View > Light, Dark
  and Parchment themes.

## Result

Every Stage 1 item passes in the test VM (Arm64 Windows 11; the x64 build under emulation).

| Stage 1 item | Result |
|---|---|
| Tab, Shift+Tab and F6 in and out of the body | Pass |
| Alt+key access keys and menus while the body has focus | Pass |
| Ctrl+Enter, Escape, the command palette | Pass |
| Focus back in the body after a dialog, and after the palette | Pass |
| The body announced as part of the window | Pass with NVDA: 48 of 48 (`Tools/EditorPrototypes/nvda-steps/host.steps`). Narrator and JAWS are Kelly's to check. |
| High Contrast and dark themes | Pass, from screenshots: High Contrast Black, WinUI's dark theme, and explicit colors (Parchment). |
| Single-file exe, x64 and Arm64 | Pass, with one condition (below) |
| Velopack installer, x64 and Arm64 | Pass: both pack; the Arm64 MSI installs and the installed copy runs |

### Four things the host has to do (all in `RichEditBoxHost.cs`)

Each was a real failure first.

1. **Typed characters go straight to the editor** (`TranslateCharCore`). WPF offers every `WM_CHAR`
   to its access keys first: typing "t" in the body moved focus to the To field (`_To:`).
   `WM_SYSCHAR` (Alt+letter) is left to WPF, so access keys and menus still work from the body.
2. **Caret keys go straight to the editor** (`OnKeyDown`): arrows, Home, End, Page Up and Page Down,
   with any modifier but Alt. WPF's keyboard navigation takes Ctrl+arrow as "move focus that way":
   Ctrl+Down in the body moved focus to the Send button. The window's `PreviewKeyDown` still sees
   every key first, so QuickMail's command keys keep working.
3. **Focus the editor by name, deferred** (`FocusEditor`). `NavigateFocus(Programmatic)` left focus
   on the island's pane, not the editor. Set Win32 focus on the island's window, then
   `Editor.Focus(Keyboard)`, after WPF has finished its own focus change.
4. **Pass focus on whenever the container window gets it** (`WndProc`, `WM_SETFOCUS`). After a
   dialog closes, WPF puts focus back on the container, where a screen reader finds a nameless
   pane.

### Packaging and size

- **Reference `Microsoft.WindowsAppSDK.WinUI`, not `Microsoft.WindowsAppSDK`.** The whole SDK adds
  about 50 MB of AI and ML libraries, and its single-file exe does not start: the generated
  manifest names `SessionHandleIPCProxyStub.dll` twice, and Windows refuses it ("side-by-side
  configuration is incorrect"). The WinUI component package alone works.
- **Single-file works, but everything is extracted at first start.** Microsoft requires
  `IncludeAllContentForSelfExtract` for a single-file WinUI app (QuickMail uses
  `IncludeNativeLibrariesForSelfExtract` today). The size came out the same with either, so the
  SDK appears to force it. For QuickMail that means `AppContext.BaseDirectory` is the extraction
  folder, not the exe's folder. **Stage 2 has to check every use of it**, and Velopack's.
- `EnableMsixTooling` does put the `.pri` files in the publish output; no copying is needed.

Measured, self-contained, single-file, ReadyToRun (what QuickMail ships):

| | Plain WPF app | This spike | Added by WinUI |
|---|---|---|---|
| x64 exe | 148 MB | 289 MB | 141 MB |
| Arm64 exe | 164 MB | 320 MB | 156 MB |
| x64, compressed (zip) | 61 MB | 104 to 111 MB | about 45 to 50 MB |
| Arm64, compressed (zip) | 56 MB | 103 MB | about 47 MB |

Velopack output for the spike: x64 MSI 92 MB, Setup 119 MB, full package 111 MB; Arm64 MSI 82 MB,
Setup 108 MB, full package 103 MB. For scale, QuickMail 0.8.76 is 291 MB (x64 exe), 325 MB (Arm64
exe), 96 MB and 87 MB (MSIs). So expect QuickMail's exe to grow by roughly 140 to 155 MB and its
installer by roughly 45 to 50 MB. Leaving the Windows projection assemblies out of ReadyToRun
changed nothing; trimming them is untried.

## What Stages 2 and 3 added (in QuickMail itself)

The rules that came out of these are in the repository's `CLAUDE.md`, under "The Message Editor
(hosted WinUI)". The history behind them:

- **The whole Windows App SDK does not start as a single-file exe** ("side-by-side configuration
  is incorrect"). Only the `Microsoft.WindowsAppSDK.WinUI` package is referenced, the SDK's
  automatic start-up is off, and `WinUiRuntime` loads it on first use.
- **WPF loses track of focus in the island.** `Keyboard.FocusedElement` is sometimes null while
  the editor has focus, so focus is asked of Windows (`GetFocus` / `IsChild`), and the window
  hands keys it receives with no focused element to the editor's handlers.
- **`TextChanged` arrives later**, after the call that caused it has returned. Flags held across
  a `SetText` do not work; the code compares against the text it last agreed with the view model.
- **Disposing the island after the window closed is an access violation.** It is released while
  the host's window is being destroyed.
- **The control resets fonts and sizes when it finishes loading**, so a body loaded as the window
  opens is written again on `Loaded`, unless the user has typed since.
- **Hidden text is read by NVDA**, and the caret walks through it. Structure is kept in paragraph
  properties RichEdit stores but does not draw: right indent for quote depth, keep-together for a
  code block, keep-with-next / no-line-number / widow-control for a table's cells, rows and tables.
- **RichEdit's own tables read badly with NVDA** (a row at a time, cells run together), so a
  table is one cell per line and QuickMail says the row and column.
- **RichEdit's paragraph unit stops at a line break inside a paragraph.** A second line in a
  table cell counted as another cell, and ending a code block acted on its last line alone.
  `RichEditDocument.WholeParagraph` widens it.
- **`\uXXXX` escapes for special characters were saved as raw characters** by the tooling more
  than once. The sources use `(char)0xFFFC` and the like.
- **GitHub's runners build with a newer .NET SDK** than this PC, which raised two warnings
  (CsWinRT1032, NETSDK1244) that fail a `-warnaserror` publish.

NVDA comparisons, same keys against both editors: Plain Text and Markdown, new editor 40 of 40
and the WPF editor 39 of 40; HTML mode, new 30 of 30 and WPF 29 of 30 (the WPF editor runs two
words together after Enter). The step files are `compose.steps` and `compose-html.steps`.

## Not covered here

- Narrator and JAWS: Kelly's check, with build 5.
- IME and dead keys (`WM_DEADCHAR` is passed on, untested), touch, drag and drop.
- Window-level `InputBindings`: the caret-key handoff runs before them. QuickMail's compose window
  uses `PreviewKeyDown`, which runs first, so this should not matter, but Stage 2 must check.
- The Windows App SDK 2.0 packages exist; this uses 1.8, as the approved prototype did.

## Building and testing

```
dotnet publish Tools/EditorHostSpike/EditorHostSpike.csproj -c Release -r win-arm64 -p:Platform=arm64 ^
  -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:PublishReadyToRun=true -o <folder>
```

Never run it on Kelly's PC; use the test VM. The NVDA steps are
`Tools/EditorPrototypes/nvda-steps/host.steps` (sr-test skill). See `Tools/EditorPrototypes/` for
the earlier prototypes and #1099 for the history.

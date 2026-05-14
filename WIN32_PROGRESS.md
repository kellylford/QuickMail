# QuickMail — WPF → WinForms Conversion Progress

Branch: `win32`  
Last updated: 2026-05-14

---

## What Was Done

### Full WPF → WinForms Rewrite

All UI was converted from WPF/XAML to native WinForms. The motivation is maximum
screen-reader compatibility: native Win32 controls (ListBox, TreeView, ListView,
SplitContainer) expose standard UIA/MSAA patterns that NVDA, JAWS, and Narrator
handle reliably, whereas WPF's UIAutomation layer is a thinner wrapper with
known gaps.

#### Files replaced

| Old (WPF) | New (WinForms) |
|-----------|----------------|
| `App.xaml` / `App.xaml.cs` | `Program.cs` (DI composition root) |
| `Views/MainWindow.xaml(.cs)` | `Views/MainForm.cs` |
| `Views/ComposeWindow.xaml(.cs)` | `Views/ComposeForm.cs` |
| `Views/AccountManagerDialog.xaml(.cs)` | `Views/AccountManagerForm.cs` |
| `Views/AddAccountDialog.xaml(.cs)` | `Views/AddAccountForm.cs` |
| `Views/FolderPickerWindow.xaml(.cs)` | `Views/FolderPickerForm.cs` |
| `Views/NewFolderDialog.xaml(.cs)` | `Views/NewFolderForm.cs` |
| `Views/CommandPaletteWindow.xaml(.cs)` | `Views/CommandPaletteForm.cs` |
| `Styles/AccessibleStyles.xaml` | (removed; styles are inline) |

#### Layout — MainForm

Three-pane layout using nested `SplitContainer` controls:

```
┌─────────────────────────────────────────────────────────┐
│ ToolStrip (New / Reply / Reply All / Forward / Delete…) │
├──────────┬──────────────┬────────────────────────────────┤
│ Accounts │ Folder tree  │ Message list (ListView virtual) │
│ (ListBox)│ (TreeView)   ├────────────────────────────────┤
│          │              │ Reading pane (WebView2)         │
│          │              │   From / To / Cc / Subject /   │
│          │              │   Date header + attachments     │
├──────────┴──────────────┴────────────────────────────────┤
│ Status bar (read-only TextBox + ProgressBar)             │
└─────────────────────────────────────────────────────────┘
```

#### Build / publish

`build.bat` with no arguments now runs `dotnet publish` (self-contained,
single-file, win-x64, ~200 MB). Named targets: `build`, `run`, `publish`,
`clean`, `smoke`.

`QuickMail.csproj` already had:
```xml
<PublishSingleFile>true</PublishSingleFile>
<SelfContained>true</SelfContained>
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
<PublishReadyToRun>true</PublishReadyToRun>
```

---

## Bugs Fixed This Session

### 1. Screen reader reading column header words ("From:", "Subject:", …)

**Root cause:** `ListView` in Details view exposes `ColumnHeader.Text` to UIA via
`TableItem.GetColumnHeaderItems()` regardless of `HeaderStyle`. Screen readers
prepend the header text before each cell value even when the header bar is
hidden.

**Fix:** Set `Text = ""` on all five `ColumnHeader` objects AND set
`HeaderStyle = ColumnHeaderStyle.None`. The `None` style destroys the Win32
header control entirely; blank `Text` ensures UIA has nothing to report even if
it queries headers.

---

### 2. Cross-thread crash when switching to conversation view

**Root cause:** `MainViewModel` is constructed before `Application.Run()`, so
`SynchronizationContext.Current` is `null` at construction time. The fallback
`new SynchronizationContext()` dispatches callbacks on the thread pool, not the
UI thread. Collection-changed events fired from those callbacks touched WinForms
controls cross-thread.

**Fix:** Added `InvokeRequired` / `BeginInvoke` guards at the top of every
method that touches a WinForms control and can be called from a non-UI thread:
`OnVmPropertyChanged`, `OnMessagesCollectionChanged`,
`OnConversationsCollectionChanged`, `RebuildFolderTree`, `RebuildAccountList`.

---

### 3. Screen reader duplicating message announcements

**Root cause:** `SelectedIndexChanged` on the `ListView` called
`AccessibilityHelper.Announce` in addition to the ListView's own native UIA
selection event. Screen readers heard the message twice.

**Fix:** Removed the explicit `Announce` call from `SelectedIndexChanged`. The
ListView's native UIA `SelectionItem.Select` event is sufficient.

---

### 4. Delete not advancing cursor to the next message

**Root cause:** `OnMessagesCollectionChanged` always called
`SelectMessageListItem(0)` when the selection was cleared after a deletion,
jumping to the top of the list.

**Fix:** Record the selected index *before* resizing the virtual list, then use
`Math.Min(prevIndex, newCount - 1)`. The cursor now lands on the message
immediately after the deleted one (or the new last message if deleting at the
end).

---

### 5. Self-contained standalone publish

**Status:** Done. `publish\QuickMail.exe` is a ~200 MB single-file self-contained
executable (no .NET runtime required on the target machine).

---

## Outstanding Issue — Escape / Reading Pane Focus

**Status: NOT FIXED.** This is the most complex remaining problem.

### What the user wants

1. Open a message → reading pane shows email → screen reader enters browse mode
   → arrow keys navigate the email text.
2. Press Escape → reading pane closes → screen reader focus returns to the
   message list → arrow keys navigate between messages.

### What actually happens

The reading pane *does* close (confirmed via log: `Panel2Collapsed` goes
`False → True` on the first Escape press). But the screen reader's virtual
cursor stays anchored to the WebView2 HTML content even after the panel is
hidden. The user cannot tell the pane closed, presses Escape repeatedly, and
gets no useful feedback.

### Attempted fixes (all unsuccessful)

| Approach | Why it failed |
|----------|---------------|
| `ProcessCmdKey` override | Fires for WinForms controls only; Chromium (separate process HWND) absorbs the keypress before WinForms sees it |
| `_webView.KeyDown` / `PreviewKeyDown` | These WinForms events do not fire for keys typed inside the Chromium renderer |
| Reflection → `CoreWebView2Controller.AcceleratorKeyPressed` | Escape is not always classified as an "accelerator key"; fires after the fact for the first press, too late |
| JS `window.addEventListener('keydown', …, true)` via `AddScriptToExecuteOnDocumentCreatedAsync` | Blocked by `script-src 'none'` CSP injected into HTML email bodies |
| Same JS relay via `ExecuteScriptAsync` (post-navigation) | Script installs correctly and `WebMessageReceived` fires; `CloseReadingPane` is called; panel collapses; but screen reader virtual cursor remains in WebView2 |
| Navigate WebView2 to blank page on close | Screen reader announced "about:blank" window — wrong behaviour |
| `role="application"` on email body | Prevents browse mode entirely: arrow keys no longer navigate email text, and only focusable elements are readable — completely wrong for an email reading pane |
| `CoreWebView2Controller.IsVisible = false` | Panel collapses; pane is gone visually; screen reader still traverses the invisible WebView2 DOM via its cached virtual buffer |
| Pre-collapse `FocusActiveMessagePanel()` + post-collapse `BeginInvoke(FocusActiveMessagePanel)` + explicit `Announce` | Announce fires and is heard; but screen reader virtual cursor is still in WebView2 and arrow keys still navigate the email |

### Root cause (current understanding)

WebView2 embeds Chromium as a **separate Win32 process** with its own HWNDs.
When NVDA/JAWS enters **browse mode** on the email HTML, it builds a virtual
buffer and tracks it independently of Win32 keyboard focus. Collapsing the
`SplitContainer` panel, calling `_messageList.Focus()`, setting
`CoreWebView2Controller.IsVisible = false`, and raising UIA notifications all
affect different layers. None of them convinces the screen reader to tear down
its virtual buffer and follow Win32 focus to the native message list.

### Recommended next approach

Replace WebView2 for the reading pane with a **native `RichTextBox`**:

- Plain-text emails: display verbatim in `RichTextBox` (read-only).
- HTML emails: strip tags (already have `StripHtml` in `MainViewModel`) and
  display the plain-text extraction, or convert basic HTML to RTF.

A `RichTextBox` is a native Win32 control. Screen readers handle it in
application mode with no virtual buffer, no browse mode, no separate process.
Arrow keys navigate text character/word/line exactly as the user expects.
Pressing Escape while focus is in the `RichTextBox` is a straightforward
WinForms `ProcessCmdKey` intercept — no WebView2 complexity at all.

The trade-off is that rich HTML rendering (images, CSS layout) is lost. For
most email clients this is acceptable; the alternative of running a full browser
engine inside the app creates the accessibility problems documented above.

---

## Key Technical Notes for Future Work

- **WebView2 keyboard interception:** `AcceleratorKeyPressed` on
  `CoreWebView2Controller` (accessed via reflection on `_coreWebView2Controller`
  private field of the WinForms `WebView2` control) is the only host-side hook
  for non-accelerator keys. `ExecuteScriptAsync` bypasses CSP; use it for
  per-navigation JS injection instead of `AddScriptToExecuteOnDocumentCreatedAsync`.
- **UIA announcements:** `AccessibilityHelper.Announce` calls
  `AccessibleObject.RaiseAutomationNotification` via reflection (works on
  .NET 8 WinForms without referencing UIAutomation assemblies directly).
- **Virtual-mode ListView:** `FocusedItem` must be set explicitly (not just
  `SelectedIndices`) for screen readers to announce the focused row reliably.
- **VM construction timing:** `MainViewModel` is built before `Application.Run()`;
  always guard UI-touching callbacks with `InvokeRequired`.
- **Log file:** `%AppData%\QuickMail\quickmail.log` — all `LogService.Log()`
  calls append here.

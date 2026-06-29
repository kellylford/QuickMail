# QuickMail v0.7.8.1 Release Notes

## Download

Two options are available for v0.7.8.1:

| Download | When to use |
|----------|-------------|
| **`quickmail-v0.7.8.1-setup.exe`** — Windows installer | Recommended for most users. Installs per-user with no elevation required, checks for the WebView2 Runtime, and registers an uninstaller. |
| **`QuickMail.exe`** — standalone portable executable | No installation required. Copy it anywhere and run. |

Both downloads include the .NET 8 runtime — you do not need to install .NET separately.

---

## Bug Fixes

- **Attachment keyboard access was completely broken when reading messages.** Pressing Tab to move to the attachment list did not set focus on any attachment; pressing Enter did nothing; pressing Shift+F10 opened the message context menu (Reply, Forward, Delete) instead of the attachment menu (Open, Save, Save All). This affected all three message viewing modes — reading pane, tab, and standalone window. All three interactions now work correctly in all modes.

- **First Shift+F10 in a session showed the Windows system menu.** The very first time Shift+F10 was pressed after starting QuickMail, the Windows system menu (Restore, Maximize, Minimize, Close) appeared instead of QuickMail's context menu. All subsequent presses in the same session worked correctly. This has been fixed; QuickMail's context menu now opens consistently from the first keypress.

---

## Thank You to Contributors

Thank you to everyone who has contributed to QuickMail through code, bug reports, feature suggestions, and other feedback. Your contributions make the project better for everyone.

---

## Internal

### Attachment list keyboard fixes (issues #162 and #148)

**Issue #162 — three root causes fixed:**

- `GotKeyboardFocus` on each attachment `ListBox` now calls `.Focus()` on the `ListBoxItem` container (not just `SelectedIndex = 0`). Setting `SelectedIndex` alone selects an item visually but leaves WPF keyboard focus on the `ListBox` shell. The handler also checks `e.OriginalSource` to distinguish focus landing on the shell from a child item already having focus, preventing unwanted resets on subsequent navigation.
- `PreviewKeyDown` on each attachment `ListBox` now handles Shift+F10 and the Apps key explicitly, opening `ContextMenu.IsOpen = true` directly and marking `e.Handled = true`. This bypasses WPF's ContextMenu routing mechanism, which was allowing `ContextMenuOpening` to bubble to the window-level fallback handler and open the message context menu instead.
- `OnWindowContextMenuOpening` in `MainWindow` gained an exclusion check for the attachment list (`IsDescendantOf(ReadingPaneAttachmentList, e.OriginalSource)`) as belt-and-suspenders against the same fallback scenario from right-click.

Fixes applied to both `MainWindow.xaml.cs` (reading pane and tab modes) and `MessageWindow.xaml.cs` (window mode).

**Issue #148 — Win32 hook approach:**

A `WM_CONTEXTMENU` Win32 hook (`HwndSource.AddHook`) detects when `Keyboard.FocusedElement` is null at the moment WM_CONTEXTMENU arrives — the state that occurs on the very first Shift+F10 after startup, when WebView2 initialisation has taken Win32 focus without WPF tracking it. The hook calls `FocusPanelSynchronously()` before returning, giving WPF a non-null focused element to route `ContextMenuOpening` from. This is a synchronous update; an earlier attempt using `Dispatcher.InvokeAsync` lost the timing race and did not fix the issue.

### Dependency updates

- `Microsoft.Identity.Client` (MSAL) 4.85.0 → 4.85.2
- `Microsoft.NET.Test.Sdk` 18.6.0 → 18.7.0
- `actions/checkout` v4 → v7
- `peaceiris/actions-gh-pages` v3 → v4

### Debug input trace

`AccessibilityHelper.RegisterDebugInputTrace(Window)` wires window-level `PreviewKeyDown` and `GotKeyboardFocus` handlers (both with `handledEventsToo: true`) when `/debug` is active. Each key press logs the key, modifiers, handled state, and focused element; each focus change logs the old and new elements labelled by `AutomationProperties.Name`, XAML `x:Name`, DataContext type, or element type in that priority order — matching the event-stream style of AccEvent for MSAA. Called from both `MainWindow` and `MessageWindow` on load. No-op in normal runs.

# StatusBar Branch Review

**Branch:** `StatusBar`  
**Date:** 2026-05-24  
**Reviewer:** Claude Code  
**Commit reviewed:** `8563a13` — "Status bar accessibility: keyboard navigation, screen reader support, connection status"  
**Files changed:** 6 (AccessibleStyles.xaml, MainViewModel.cs, MainWindow.xaml, MainWindow.xaml.cs, USERGUIDE.md, docs/planning/status-bar-accessibility-plan.md)

---

## Summary

The branch makes meaningful accessibility improvements to the status bar: it adds left/right arrow key navigation between regions, converts the clickable Rules item from a TextBox-with-cursor to a proper `Button`, adds a new `ConnectionStatusText` region, and ensures each region has a well-formed `AutomationProperties.Name`. The architectural approach is sound and the code is generally clean.

However, there are several issues — one of them a likely silent focus bug — that should be resolved before merging.

---

## Findings

### 1. Tooltip references an unregistered shortcut — `Ctrl+Shift+L`

**Severity: Blocking**

`MainWindow.xaml` (Region 3, Rules button):
```xml
<Button x:Name="RulesStatusButton"
        ...
        ToolTip="Open Rules Manager (Ctrl+Shift+L)"/>
```

There is no `Ctrl+Shift+L` registered in `CommandRegistry` anywhere in the branch. No such shortcut appears in the keyboard shortcut table in `CLAUDE.md` or in `USERGUIDE.md`. The tooltip is false advertising — a sighted user who reads it and presses `Ctrl+Shift+L` gets nothing.

**Fix:** Either register a `mail.openRulesManager` command with `Ctrl+Shift+L` as its `defaultKey` (and add it to the shortcut table in `CLAUDE.md` and `USERGUIDE.md`), or remove the shortcut hint from the tooltip entirely until it is implemented.

---

### 2. `StatusProgressBar` is probably not focusable — region 4 navigation silently fails

**Severity: Blocking**

`MainWindow.xaml.cs`, `FocusStatusBarRegion`:
```csharp
case 4:
    if (StatusProgressItem.Visibility == Visibility.Visible)
        StatusProgressBar.Focus();
    else
        FocusStatusBarRegion(1); // fallback: wrap to first
    break;
```

WPF's `ProgressBar` inherits from `RangeBase`, which sets `Focusable = false` in its default style. No `Focusable="True"` is set on `StatusProgressBar` in the XAML. The call to `StatusProgressBar.Focus()` will return `false` silently — focus will not move, and `GetFocusedStatusBarRegion()` will continue to return 3. If the user presses `Right Arrow` from region 3 while sync is running, focus appears to freeze.

There is also a secondary symptom: `GetFocusedStatusBarRegion()` checks `StatusProgressBar.IsKeyboardFocused`, which can never be `true` if the element is not focusable.

**Fix:** Add `Focusable="True"` to the `StatusProgressBar` element, or add it to the `StatusProgressItem` `StatusBarItem` and redirect focus there. If you want screen readers to read the progress bar's `AutomationProperties.Name` when navigated to, `Focusable="True"` on the element itself is the right call.

---

### 3. `TabNavigation="Once"` contradicts the stated keyboard contract

**Severity: Significant**

The XAML sets `KeyboardNavigation.TabNavigation="Once"` on `MainStatusBar`:
```xml
<StatusBar x:Name="MainStatusBar"
           KeyboardNavigation.TabNavigation="Once"
           KeyboardNavigation.DirectionalNavigation="Contained"
           ...>
```

All four `StatusBarItem`s have `KeyboardNavigation.IsTabStop="True"`.

With `TabNavigation="Once"`, pressing **Tab** from region 1 moves focus to region 2, then 3, then 4 (if visible), then finally exits the status bar. This is the same behaviour as the Left/Right arrows — it cycles through every region before exiting.

The keyboard contract documented in the planning spec and in `USERGUIDE.md` says:
> **Tab** → move to Toolbar (next F6 pane)

These are incompatible. Users (and screen reader users in particular) will press Tab expecting to leave the status bar immediately and instead get cycled through every region.

**Fix:** Change to `KeyboardNavigation.TabNavigation="None"` on the `StatusBar`, which prevents Tab from entering or cycling through child elements. The custom Left/Right handler already owns internal navigation. Exiting via Tab will then fall through to the window's default focus behaviour, which (given `TabIndex` ordering) should route to the toolbar. Verify Tab exits to the correct next pane after making the change.

---

### 4. `ConnectionStatusText` is not updated when the user goes offline

**Severity: Significant**

`MainViewModel.cs` updates `ConnectionStatusText` in:
- `InitialLoadAsync` → `"Connecting…"`
- `BackgroundSyncAsync` (start) → `"Syncing…"`
- `BackgroundSyncAsync` (success) → `"N accounts connected"`
- `BackgroundSyncAsync` (error) → `"Connection error"`
- `ConnectAllAccountsAsync` (start) → `"Connecting…"`
- `ConnectAllAccountsAsync` (finish) → `"N account(s) connected"` or `"Offline"`

There is no code path that resets `ConnectionStatusText` to `"Offline"` when the user manually triggers an offline-mode toggle. After a successful sync session the text says `"2 accounts connected"`, and it stays that way even after the user deliberately goes offline. The field initialiser starts at `"Offline"` but it will never return to that state through normal usage after the first successful sync.

**Fix:** Set `ConnectionStatusText = "Offline"` wherever the app transitions to offline mode (the existing toggle path in `MainViewModel`). Check that `GoOfflineModeAsync` / the equivalent command handler also sets it.

---

### 5. Screen reader product names appear in XAML comments and USERGUIDE.md

**Severity: Significant**

`MainWindow.xaml`, the status bar XAML comment reads:
```xml
<!-- ... screen readers'
     dedicated status-bar commands (JAWS Insert+PageDown, NVDA Insert+End) work. -->
```

`USERGUIDE.md` adds:
> Screen readers can read the entire status bar at once with their dedicated status-bar command: **Insert+PageDown** (JAWS), **Insert+End** (NVDA), or **CapsLock+PageDown** (Narrator).

`CLAUDE.md` is explicit:
> Do not name a specific screen reader product (NVDA, JAWS, VoiceOver, Narrator, etc.) in documentation, release notes, commit messages, or UI text unless the content is specific to that product.

The USERGUIDE entry is a genuine edge case — the content *is* product-specific (each product has a different key combination). However the XAML comment is implementation notes and does not need product names. Both violate the letter of the rule; the USERGUIDE entry is arguably justified on its merits, but should be discussed before merging. The XAML comment should simply be reworded.

**Fix (XAML comment):** Replace with something like:
```xml
<!-- The StatusBar UIA control pattern enables screen readers' built-in
     status-bar read commands. -->
```

**Fix (USERGUIDE):** Either follow the rule strictly and replace with:
> Screen readers that support reading a status bar directly can usually do so with a dedicated keyboard command — consult your screen reader's documentation for details.

…or obtain a deliberate exception to the no-product-names rule given that the key commands are product-specific. That decision should be made consciously.

---

### 6. Both `StatusBarItem` and its child control carry `AutomationProperties.Name`

**Severity: Moderate**

Each region pairs a name on the `StatusBarItem` wrapper *and* on its child:

```xml
<StatusBarItem x:Name="StatusTextItem"
               AutomationProperties.Name="Status">
    <TextBox AutomationProperties.Name="{Binding StatusText, StringFormat='Status — {0}'}"/>
</StatusBarItem>
```

In UIA, when the TextBox receives focus, the screen reader may announce both the containing `StatusBarItem`'s name ("Status") and the child's name ("Status — Ready"), producing a stuttered read: *"Status  Status — Ready"*. This depends on how each screen reader navigates the UIA tree, but it is a known source of double announcement.

**Fix:** Remove `AutomationProperties.Name` from the `StatusBarItem` wrappers and rely entirely on the child element's name. The child's name already contains the full context (e.g., `"Status — Ready"`). Alternatively, keep the name on the child and make the `StatusBarItem`'s name an empty string via `AutomationProperties.Name=""` to suppress it from the UIA tree.

---

### 7. `StatusBarAccessibleName` computed in the ViewModel is a mild MVVM stretch

**Severity: Minor**

`MainViewModel.cs`:
```csharp
partial void OnStatusTextChanged(string value)
{
    StatusBarAccessibleName = string.IsNullOrEmpty(value)
        ? "Status bar"
        : $"Status bar — {value}";
}
```

This property exists solely to set `AutomationProperties.Name` on the `StatusBar` element — a View-layer concern. The ViewModel is aware of how the View will label itself. For the other regions the same information is expressed with a `StringFormat` directly in XAML:
```xml
AutomationProperties.Name="{Binding StatusText, StringFormat='Status — {0}'}"
```

The same approach would work for the status bar itself:
```xml
AutomationProperties.Name="{Binding StatusText, StringFormat='Status bar — {0}',
                             FallbackValue='Status bar'}"
```

Removing `StatusBarAccessibleName` and `OnStatusTextChanged` from the ViewModel would clean up two properties from a class that already has many.

**Note:** This is not a blocking concern — the existing pattern does not break MVVM rules — but it's worth tidying if the other issues are being fixed in the same pass.

---

### 8. `FocusStatusBar()` always lands on region 1 regardless of F6 direction

**Severity: Minor / Informational**

Whether the user presses **F6** (forward) or **Shift+F6** (backward), `FocusPaneAtAsync(5)` always calls `FocusStatusBar()` which always focuses `StatusTextBox` (region 1). In practice this is consistent with how the other panes work (account list, folder list, and message list all focus their first/default item regardless of F6 direction), so it is not a regression or a rule violation. However it is worth noting because some apps (VS Code is the reference in the planning spec) do reverse the entry point on backward navigation. If that behaviour is desired in a future pass, `CycleFocusAsync` would need to pass the direction into `FocusStatusBar`.

No fix required; document as a known limitation if relevant.

---

## Items Confirmed Fine

- **Removal of the old `RulesStatusItem.KeyDown` handler** and replacement with `RulesStatusButton_Click` is correct. The old handler was attached to a `Focusable="False"` element and could never fire via keyboard; the new Button approach is the right fix.
- **`NavigateStatusBar` correctly excludes region 4 when not visible.** The visibility guard is in place and the fallback in `FocusStatusBarRegion(4)` is a safe no-op.
- **The `StatusBarSpacerStyle` approach** (Separator with a zero-height Rectangle template) is the standard WPF idiom for a status-bar spacer and should push the right-side items correctly given the DockPanel layout inside StatusBar.
- **The planning document** (`docs/planning/status-bar-accessibility-plan.md`) is appropriately filed in `docs/planning/` alongside the other planning documents already checked into `main`. No concern there.
- **The `StatusBarButtonStyle`** is well-constructed: it has a `FocusVisualStyle`, keyboard-focus border, mouse-over highlight, pressed state, and transparent baseline — all the expected states for an accessible button.
- **`ConnectionStatusText` field initialiser** (`"Offline"`) is correct for app startup before any accounts connect.

---

## Merge Readiness

| # | Issue | Severity | Must-fix before merge? |
|---|-------|----------|------------------------|
| 1 | Tooltip references unregistered `Ctrl+Shift+L` | Blocking | Yes |
| 2 | `StatusProgressBar` likely not focusable — region 4 navigation silently fails | Blocking | Yes |
| 3 | `TabNavigation="Once"` contradicts keyboard contract | Significant | Yes |
| 4 | `ConnectionStatusText` not updated on offline toggle | Significant | Yes |
| 5 | Screen reader product names in XAML comment (and USERGUIDE.md) | Significant | Yes (XAML) / Discuss (USERGUIDE) |
| 6 | Double `AutomationProperties.Name` on container + child | Moderate | Recommended |
| 7 | `StatusBarAccessibleName` in ViewModel | Minor | No |
| 8 | F6 backward always enters region 1 | Minor / Info | No |

**Recommended path:** Address items 1–5 (and ideally 6) before merging. Items 7–8 can be deferred to a follow-up.

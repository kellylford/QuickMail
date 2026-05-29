# Selection Keyboard Shortcuts — Combined PM & Dev Specification

**Status:** Implemented  
**Version:** 1.2  
**Date:** 2026-05-29  
**Author:** AI coding agent (based on comprehensive app audit)  
**Target release:** v0.6.8

### Implementation notes (v1.1)

- **5.4 (Home/End multi-select preservation) cut** per request — not implemented.
- **`IsDescendantOf` helper not added** — an identical static helper already existed in `MainWindow` with signature `(DependencyObject ancestor, DependencyObject descendant)`. `IsMessageListFocused` reuses it with corrected argument order (`IsDescendantOf(MessageList, dep)`).
- **`SelectAllMessages` uses no batch scope** — `BatchObservableCollection.BeginBatchScope()` suppresses `CollectionChanged` events on the underlying data collection, not `SelectionChanged` on `ListView`. The batch scope would be a no-op around `MessageList.SelectAll()`, so it was omitted for clarity.
- **`ExtendSelectionToBottom` loop starts at `anchorIndex`** (spec says inclusive) — the `Contains` guard prevents double-adding already-selected items, so this is correct and identical to the spec.
- **5.6 (TokenizedAddressBox Ctrl+A) revised** — spec recommended deleting all chips immediately on Ctrl+A (Appendix A.3). This was wrong: Ctrl+A is a selection gesture, not a destructive one. Final behavior: Ctrl+A selects all chips (highlighted using system highlight color), then Delete/Backspace removes all selected, Ctrl+C copies all selected. Arrow-key navigation or click clears the selection. This matches standard Windows multi-token field behavior.

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Audit Findings](#2-audit-findings)
3. [Design Principles](#3-design-principles)
4. [Feature Scope](#4-feature-scope)
5. [Detailed Specifications](#5-detailed-specifications)
   - [5.1 Message List — Ctrl+A (Select All)](#51-message-list--ctrla-select-all)
   - [5.2 Message List — Ctrl+Shift+Home (Extend to Top)](#52-message-list--ctrlshifthome-extend-to-top)
   - [5.3 Message List — Ctrl+Shift+End (Extend to Bottom)](#53-message-list--ctrlshiftend-extend-to-bottom)
   - [5.4 Message List — Home/End (Move Focus Without Losing Selection)](#54-message-list--homeend-move-focus-without-losing-selection)
   - [5.5 Dialog ListBoxes/ListViews — Ctrl+A](#55-dialog-listboxeslistviews--ctrla)
   - [5.6 TokenizedAddressBox — Ctrl+A (Select All Chips)](#56-tokenizedaddressbox--ctrla-select-all-chips)
   - [5.7 Reading Pane Header Fields — Ctrl+A](#57-reading-pane-header-fields--ctrla)
   - [5.8 Command Registration](#58-command-registration)
6. [Implementation Order](#6-implementation-order)
7. [Files to Modify](#7-files-to-modify)
8. [Edge Cases & Error Handling](#8-edge-cases--error-handling)
9. [Accessibility Checklist](#9-accessibility-checklist)
10. [Test Plan](#10-test-plan)
11. [Build Verification](#11-build-verification)

---

## 1. Executive Summary

QuickMail has significant gaps in standard Windows selection keyboard shortcuts. The three most critical missing shortcuts are:

| Shortcut | Expected Behavior | Current State |
|---|---|---|
| **Ctrl+A** | Select all items in the focused list | ❌ Not implemented in message list or any dialog list |
| **Ctrl+Shift+Home** | Extend selection from current item to first item | ❌ Not implemented anywhere |
| **Ctrl+Shift+End** | Extend selection from current item to last item | ❌ Not implemented anywhere |

These are table-stakes keyboard conventions that every Windows user expects. Their absence makes QuickMail feel broken to keyboard-centric users — which is QuickMail's core audience.

Additionally, several secondary gaps were found:
- **Home/End** in the message list move focus but collapse the selection to a single item (WPF default behavior), which is destructive when the user has carefully built a multi-select range.
- **Ctrl+A in dialog lists** (Address Book, Folder Picker, Template Picker, Rules Manager, etc.) is not explicitly handled, though WPF may provide partial defaults depending on `SelectionMode`.
- **Ctrl+A in the TokenizedAddressBox** selects only the text in the current input box, not all address chips.

This spec covers all identified gaps with precise implementation instructions.

---

## 2. Audit Findings

A comprehensive audit was performed on 2026-05-28 covering every list, tree, and text input control in the application.

### 2.1 Message List

**Control:** `ListView` with `SelectionMode="Extended"`  
**Location:** `MainWindow.xaml` line ~940, handler at `MainWindow.xaml.cs` line ~1227

| Shortcut | Status | Notes |
|---|---|---|
| Ctrl+A | ❌ Missing | WPF ListView does not auto-respond to Ctrl+A even in Extended mode |
| Ctrl+Shift+Home | ❌ Missing | No handler exists |
| Ctrl+Shift+End | ❌ Missing | No handler exists |
| Shift+Up/Down | ✅ Works | Custom `ExtendMessageSelection()` at line ~1280 |
| Shift+Click | ✅ Works | WPF ListView default |
| Home/End | ⚠️ Partial | WPF default moves focus but collapses multi-select to single item |
| Delete | ✅ Works | Custom handler deletes all selected messages |
| Enter | ✅ Works | Opens selected message |

### 2.2 Conversation / Sender Group / To Group Trees

**Control:** `TreeView` (inherently single-select)  
**Location:** `MainWindow.xaml` lines ~1063, ~1167; handlers at `MainWindow.xaml.cs` lines ~2142, ~2656, ~2819

| Shortcut | Status | Notes |
|---|---|---|
| Ctrl+A | N/A | TreeView is single-select; selecting "all" items is not meaningful |
| Ctrl+Shift+Home/End | N/A | Single-select control |
| Type-ahead | ✅ Works | Via `GroupedMessageTreeController` |

**Decision:** TreeViews are single-select by design. No changes needed for grouped views.

### 2.3 Folder Tree

**Control:** `TreeView` (single-select)  
**Location:** `MainWindow.xaml` line ~710, handler at `MainWindow.xaml.cs` line ~853

| Shortcut | Status | Notes |
|---|---|---|
| Ctrl+A | N/A | Single-select TreeView |
| Type-ahead | ✅ Works | Custom handler |

**Decision:** No changes needed.

### 2.4 Compose Window — TokenizedAddressBox

**Control:** Custom `TokenizedAddressBox` with embedded `TextBox` + chip `Button` list  
**Location:** `Controls/TokenizedAddressBox.xaml.cs`

| Shortcut | Status | Notes |
|---|---|---|
| Ctrl+A (in InputBox) | ✅ Works | WPF TextBox default — selects text being typed |
| Ctrl+A (all chips) | ❌ Missing | No way to select all address chips at once |
| Ctrl+Shift+Home/End (in InputBox) | ✅ Works | WPF TextBox default |
| Ctrl+C on chip | ✅ Works | Copies single chip's address |
| Backspace on empty input | ✅ Works | Removes last chip |

### 2.5 Compose Window — Subject & Body

**Control:** Standard WPF `TextBox`  
**Location:** `ComposeWindow.xaml` lines ~136, ~215

| Shortcut | Status | Notes |
|---|---|---|
| Ctrl+A | ✅ Works | WPF TextBox default |
| Ctrl+Shift+Home/End | ✅ Works | WPF TextBox default |
| Alt+U (focus Subject) | ✅ Works | Calls `SelectAll()` |

**Decision:** No changes needed for Subject/Body fields.

### 2.6 Dialog ListBoxes & ListViews

| Dialog | Control | SelectionMode | Ctrl+A |
|---|---|---|---|
| Address Book | `ListView` (`ContactList`) | `Extended` | ❌ Not explicitly handled |
| Folder Picker | `ListBox` (`FolderListBox`) | `Single` (default) | N/A (single-select) |
| Template Picker | `ListBox` (`TemplateListBox`) | `Single` (default) | N/A (single-select) |
| Rules Manager | `ListBox` (`RuleListBox`) | `Single` (default) | N/A (single-select) |
| Account Manager | `ListBox` | `Single` (default) | N/A (single-select) |
| Command Palette | `ListBox` (`CommandList`) | `Single` (default) | N/A (single-select) |
| View Manager | `ListBox` (`ViewsList`) | `Single` (default) | N/A (single-select) |

**Decision:** Only the Address Book contact list uses multi-select and needs Ctrl+A. All other dialog lists are single-select.

### 2.7 Reading Pane Header Fields

**Control:** Read-only `TextBox` controls (Subject, From, To, Cc, Date)  
**Location:** `MainWindow.xaml` lines ~788–830

| Shortcut | Status | Notes |
|---|---|---|
| Ctrl+A | ✅ Works | WPF TextBox default (read-only with `IsReadOnlyCaretVisible="True"`) |
| Ctrl+Shift+Home/End | ✅ Works | WPF TextBox default |

**Decision:** No changes needed.

### 2.8 Global Key Handlers

**MainWindow `OnWindowKeyDown`** (`MainWindow.xaml.cs` line ~621):
- Does NOT intercept Ctrl+A, Ctrl+Shift+Home, or Ctrl+Shift+End
- These keys bubble to the focused child control — safe to add handlers there

**ComposeWindow `Window_PreviewKeyDown`** (`ComposeWindow.xaml.cs` line ~430):
- Does NOT intercept Ctrl+A, Ctrl+Shift+Home, or Ctrl+Shift+End
- Safe to add handlers in child controls

---

## 3. Design Principles

1. **Follow Windows conventions exactly.** Ctrl+A means "select all." Ctrl+Shift+Home means "extend selection to first item." No surprises.

2. **Preserve existing selection behavior.** The custom `ExtendMessageSelection` logic for Shift+Up/Down works well. New shortcuts must compose with it — e.g., after Ctrl+Shift+End, pressing Shift+Up should shrink the selection from the bottom.

3. **Announce to screen readers.** Every selection change that results from a keyboard shortcut must be announced via `AccessibilityHelper.Announce()` with the appropriate `AnnouncementCategory`.

4. **Register commands where appropriate.** Ctrl+A in the message list should be a registered `CommandDefinition` so it appears in the Command Palette and is user-customizable. Dialog-local shortcuts (Address Book) do not need registration since those windows have their own command registries.

5. **No TreeView changes.** TreeViews are single-select by design. Do not attempt to add multi-select to folder trees or grouped message trees.

6. **Minimal scope.** Fix what's broken. Don't redesign selection. Don't add features like Ctrl+Space toggle or rectangular selection.

---

## 4. Feature Scope

### In scope (this spec)

| # | Feature | Priority |
|---|---|---|
| 1 | Ctrl+A in message list — select all messages | P0 |
| 2 | Ctrl+Shift+Home in message list — extend selection to first message | P0 |
| 3 | Ctrl+Shift+End in message list — extend selection to last message | P0 |
| 4 | Home/End in message list — preserve multi-select (move focus only) | P1 |
| 5 | Ctrl+A in Address Book contact list | P1 |
| 6 | Ctrl+A in TokenizedAddressBox — select all chips | P2 |
| 7 | Register `mail.selectAll` command in CommandRegistry | P0 |
| 8 | Screen-reader announcements for selection changes | P0 |

### Out of scope (future)

- Multi-select in TreeView controls (folder tree, conversation tree, sender/to group trees)
- Ctrl+Space to toggle individual item selection
- Shift+PageUp/PageDown for page-based selection extension
- Rectangular / block selection
- Select-all in single-select dialog lists (Folder Picker, Template Picker, etc.) — these are single-select by design

---

## 5. Detailed Specifications

### 5.1 Message List — Ctrl+A (Select All)

**What:** Pressing Ctrl+A when the message list has focus selects every message in the list.

**Implementation:**

In `MainWindow.xaml.cs`, add a case to `MessageList_PreviewKeyDown`:

```csharp
// Ctrl+A: select all messages
if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
{
    e.Handled = true;
    SelectAllMessages();
    return;
}
```

Add a helper method:

```csharp
private void SelectAllMessages()
{
    if (MessageList.Items.Count == 0) return;

    // Suppress per-item notifications during bulk selection
    using (var batch = _vm.Messages is BatchObservableCollection<MailMessageSummary> boc
        ? boc.BeginBatchScope()
        : null)
    {
        MessageList.SelectAll();
    }

    var count = MessageList.SelectedItems.Count;
    AccessibilityHelper.Announce(this,
        $"{count} message{(count == 1 ? "" : "s")} selected.",
        category: AnnouncementCategory.Result);
}
```

**Why `BatchObservableCollection`:** `MessageList.SelectAll()` fires `SelectionChanged` for every item. If the VM's `Messages` collection is a `BatchObservableCollection`, wrapping in a batch scope prevents screen readers from re-announcing the focused item after each insert-like notification. If it's not a `BatchObservableCollection`, the batch scope is a no-op — safe either way.

**Command Registration:** Register in `MainWindow.xaml.cs` constructor:

```csharp
_registry.Register(new CommandDefinition(
    id: "mail.selectAll",
    category: "Mail",
    title: "Select All",
    execute: SelectAllMessages,
    defaultKey: Key.A,
    defaultModifiers: ModifierKeys.Control));
```

Then in `MessageList_PreviewKeyDown`, instead of handling Ctrl+A directly, let the global `OnWindowKeyDown` dispatch it through the registry. However, `OnWindowKeyDown` runs at the Window level (PreviewKeyDown), which fires *before* `MessageList_PreviewKeyDown`. So the registry lookup in `OnWindowKeyDown` will catch Ctrl+A first.

**Revised approach:** Add the Ctrl+A case to `OnWindowKeyDown`'s registry dispatch path. Since `OnWindowKeyDown` already calls `_registry.FindByGesture(key, modifiers)` at line ~706, registering the command is sufficient — no changes needed in `MessageList_PreviewKeyDown`.

**But wait:** `OnWindowKeyDown` fires for *all* keys, even when a TextBox has focus. If the user presses Ctrl+A in the search box, we want the TextBox default (select all text), not the message list select-all. The registry command must check availability:

```csharp
_registry.Register(new CommandDefinition(
    id: "mail.selectAll",
    category: "Mail",
    title: "Select All",
    execute: SelectAllMessages,
    defaultKey: Key.A,
    defaultModifiers: ModifierKeys.Control,
    isAvailable: () => IsMessageListFocused()));
```

Where `IsMessageListFocused()` returns true only when the message list (or conversation/sender/to tree) has keyboard focus. This prevents the command from firing when a TextBox or other control is focused.

**Implementation detail for `IsMessageListFocused()`:**

```csharp
private bool IsMessageListFocused()
{
    var focused = Keyboard.FocusedElement;
    return focused == MessageList
        || focused == ConversationTree
        || focused == SenderGroupTree
        || focused == ToGroupTree
        || (focused is DependencyObject dep
            && (IsDescendantOf(dep, MessageList)
                || IsDescendantOf(dep, ConversationTree)
                || IsDescendantOf(dep, SenderGroupTree)
                || IsDescendantOf(dep, ToGroupTree)));
}

private static bool IsDescendantOf(DependencyObject element, DependencyObject parent)
{
    var current = element;
    while (current != null)
    {
        if (current == parent) return true;
        current = VisualTreeHelper.GetParent(current);
    }
    return false;
}
```

**Screen reader announcement:** After selection, announce the count. Use `AnnouncementCategory.Result` since this is a direct outcome of a user action.

---

### 5.2 Message List — Ctrl+Shift+Home (Extend to Top)

**What:** Pressing Ctrl+Shift+Home extends the current selection from the anchor item to the first item in the list.

**Behavior specification:**
- If no item is selected, select the first item (same as just Home).
- If items are selected, extend the selection to include all items from the current anchor (the first item in the selection range) up to the first item in the list.
- The focused item should become the first item in the list.
- The selection anchor should remain at its original position (so Shift+Down afterward shrinks from the top).

**Implementation:**

In `MessageList_PreviewKeyDown`, add:

```csharp
// Ctrl+Shift+Home: extend selection to first message
if (e.Key == Key.Home && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
{
    e.Handled = true;
    ExtendSelectionToTop();
    return;
}
```

Helper method:

```csharp
private void ExtendSelectionToTop()
{
    if (MessageList.Items.Count == 0) return;

    // Determine the anchor: the first selected item (lowest index)
    int anchorIndex = -1;
    if (MessageList.SelectedItems.Count > 0)
    {
        anchorIndex = MessageList.Items.Count;
        foreach (var item in MessageList.SelectedItems)
        {
            var idx = MessageList.Items.IndexOf(item);
            if (idx >= 0 && idx < anchorIndex)
                anchorIndex = idx;
        }
    }

    if (anchorIndex < 0)
    {
        // Nothing selected — just select the first item
        MessageList.SelectedIndex = 0;
        var firstItem = MessageList.Items[0] as MailMessageSummary;
        if (firstItem != null)
            MessageList.ScrollIntoView(firstItem);
        Dispatcher.InvokeAsync(() =>
        {
            if (MessageList.ItemContainerGenerator.ContainerFromIndex(0) is ListViewItem container)
                container.Focus();
        }, DispatcherPriority.Input);
        AccessibilityHelper.Announce(this, "1 message selected.",
            category: AnnouncementCategory.Result);
        return;
    }

    // Select all items from index 0 to anchorIndex
    using (var batch = _vm.Messages is BatchObservableCollection<MailMessageSummary> boc
        ? boc.BeginBatchScope()
        : null)
    {
        for (int i = 0; i <= anchorIndex; i++)
        {
            var item = MessageList.Items[i];
            if (!MessageList.SelectedItems.Contains(item))
                MessageList.SelectedItems.Add(item);
        }
    }

    var count = MessageList.SelectedItems.Count;
    MessageList.ScrollIntoView(MessageList.Items[0]);
    Dispatcher.InvokeAsync(() =>
    {
        if (MessageList.ItemContainerGenerator.ContainerFromIndex(0) is ListViewItem container)
            container.Focus();
    }, DispatcherPriority.Input);

    AccessibilityHelper.Announce(this,
        $"{count} message{(count == 1 ? "" : "s")} selected.",
        category: AnnouncementCategory.Result);
}
```

**Note on WPF ListView selection behavior:** WPF's `ListView` with `SelectionMode="Extended"` uses an internal anchor that tracks where Shift-selection started. Our custom `ExtendMessageSelection` for Shift+Up/Down manages its own anchor via the focused container index. The Ctrl+Shift+Home/End handlers must be compatible: after Ctrl+Shift+Home, the user should be able to press Shift+Down to shrink the selection from the top. This works naturally because `ExtendMessageSelection` uses the focused container's index as its reference point.

---

### 5.3 Message List — Ctrl+Shift+End (Extend to Bottom)

**What:** Pressing Ctrl+Shift+End extends the current selection from the anchor item to the last item in the list.

**Behavior specification:**
- If no item is selected, select the last item.
- If items are selected, extend the selection to include all items from the current anchor (the last item in the selection range) down to the last item in the list.
- The focused item should become the last item in the list.

**Implementation:**

In `MessageList_PreviewKeyDown`, add:

```csharp
// Ctrl+Shift+End: extend selection to last message
if (e.Key == Key.End && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
{
    e.Handled = true;
    ExtendSelectionToBottom();
    return;
}
```

Helper method:

```csharp
private void ExtendSelectionToBottom()
{
    if (MessageList.Items.Count == 0) return;

    int lastIndex = MessageList.Items.Count - 1;

    // Determine the anchor: the last selected item (highest index)
    int anchorIndex = -1;
    if (MessageList.SelectedItems.Count > 0)
    {
        foreach (var item in MessageList.SelectedItems)
        {
            var idx = MessageList.Items.IndexOf(item);
            if (idx > anchorIndex)
                anchorIndex = idx;
        }
    }

    if (anchorIndex < 0)
    {
        // Nothing selected — just select the last item
        MessageList.SelectedIndex = lastIndex;
        var lastItem = MessageList.Items[lastIndex] as MailMessageSummary;
        if (lastItem != null)
            MessageList.ScrollIntoView(lastItem);
        Dispatcher.InvokeAsync(() =>
        {
            if (MessageList.ItemContainerGenerator.ContainerFromIndex(lastIndex) is ListViewItem container)
                container.Focus();
        }, DispatcherPriority.Input);
        AccessibilityHelper.Announce(this, "1 message selected.",
            category: AnnouncementCategory.Result);
        return;
    }

    // Select all items from anchorIndex to lastIndex
    using (var batch = _vm.Messages is BatchObservableCollection<MailMessageSummary> boc
        ? boc.BeginBatchScope()
        : null)
    {
        for (int i = anchorIndex; i <= lastIndex; i++)
        {
            var item = MessageList.Items[i];
            if (!MessageList.SelectedItems.Contains(item))
                MessageList.SelectedItems.Add(item);
        }
    }

    var count = MessageList.SelectedItems.Count;
    MessageList.ScrollIntoView(MessageList.Items[lastIndex]);
    Dispatcher.InvokeAsync(() =>
    {
        if (MessageList.ItemContainerGenerator.ContainerFromIndex(lastIndex) is ListViewItem container)
            container.Focus();
    }, DispatcherPriority.Input);

    AccessibilityHelper.Announce(this,
        $"{count} message{(count == 1 ? "" : "s")} selected.",
        category: AnnouncementCategory.Result);
}
```

---

### (CUT, do not do) 5.4 Message List — Home/End (Move Focus Without Losing Selection)

**What:** Pressing Home or End (without modifiers) in the message list should move focus to the first or last item without collapsing a multi-select to a single item.

**Current WPF behavior:** In a `ListView` with `SelectionMode="Extended"`, pressing Home/End moves focus to the first/last item AND sets `SelectedIndex`, which clears any existing multi-select. This is destructive.

**Desired behavior:**
- If multiple items are selected, Home/End moves focus only — the selection is preserved.
- If exactly one item is selected (or none), Home/End behaves as WPF default (selects first/last item).

**Implementation:**

In `MessageList_PreviewKeyDown`, add:

```csharp
// Home/End: move focus to first/last item without destroying multi-select
if ((e.Key == Key.Home || e.Key == Key.End)
    && Keyboard.Modifiers == ModifierKeys.None
    && MessageList.SelectedItems.Count > 1)
{
    e.Handled = true;
    int targetIndex = e.Key == Key.Home ? 0 : MessageList.Items.Count - 1;
    var targetItem = MessageList.Items[targetIndex] as MailMessageSummary;
    if (targetItem != null)
    {
        MessageList.ScrollIntoView(targetItem);
        Dispatcher.InvokeAsync(() =>
        {
            if (MessageList.ItemContainerGenerator.ContainerFromIndex(targetIndex) is ListViewItem container)
                container.Focus();
        }, DispatcherPriority.Input);
    }
    return;
}
```

**Note:** This must be placed *before* the existing Shift+Up/Down handler in the if/else chain, since Home/End with no modifiers would otherwise fall through.

---

### 5.5 Dialog ListBoxes/ListViews — Ctrl+A

#### 5.5.1 Address Book Contact List

**Control:** `ListView` with `SelectionMode="Extended"`  
**File:** `AddressBookWindow.xaml.cs`

Add to `ContactList_PreviewKeyDown`:

```csharp
if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
{
    e.Handled = true;
    ContactList.SelectAll();
    AccessibilityHelper.Announce(this,
        $"{ContactList.SelectedItems.Count} contact(s) selected.",
        category: AnnouncementCategory.Result);
    return;
}
```

#### 5.5.2 Other Dialog Lists

All other dialog lists (Folder Picker, Template Picker, Rules Manager, Account Manager, Command Palette, View Manager) use `SelectionMode="Single"`. Ctrl+A is not applicable to single-select lists. No changes needed.

---

### 5.6 TokenizedAddressBox — Ctrl+A (Select All Chips)

**What:** When the TokenizedAddressBox has focus and the user presses Ctrl+A, select all address chips. A second Delete or Backspace press then removes all selected chips.

**Design decision:** WPF Button controls don't support multi-select natively. Rather than building a complex multi-select chip system, we use a simpler approach:

- **Ctrl+A when InputBox is empty:** Select all chips visually (apply a "selected" style) and move focus to the last chip. The user can then press Delete to remove all chips one by one, or press Ctrl+A again to select the InputBox text.
- **Ctrl+A when InputBox has text:** WPF TextBox default — select all text in the input. (This already works.)
- **Ctrl+A when a chip button has focus:** Select all chips visually.

**Implementation approach — simplified:**

Rather than building full chip multi-select (which is complex and has low ROI), implement a pragmatic shortcut: **Ctrl+A when the InputBox is empty and there are chips → delete all chips.** This matches the mental model of "select all and delete" in one step.

In `TokenizedAddressBox.xaml.cs`, modify `InputBox_PreviewKeyDown`:

```csharp
// Ctrl+A: if input is empty and there are chips, select (focus) all chips
// so the user can delete them. If input has text, let WPF TextBox handle it.
if (e.Key == Key.A && mods == ModifierKeys.Control)
{
    if (string.IsNullOrEmpty(InputBox.Text) && _chips.Count > 0)
    {
        // Focus the last chip — user can then press Delete repeatedly
        // or we could remove all chips at once. Let's go with "select all"
        // by focusing the first chip and announcing the count.
        ((Button)ChipPanel.Children[0]).Focus();
        AccessibilityHelper.Announce(this,
            $"{_chips.Count} address(es). Press Delete to remove all.",
            category: AnnouncementCategory.Hint);
        e.Handled = true;
        return;
    }
    // Otherwise let the TextBox handle Ctrl+A natively (select all text)
    return;
}
```

**Alternative (more aggressive):** Ctrl+A when InputBox is empty removes all chips immediately:

```csharp
if (e.Key == Key.A && mods == ModifierKeys.Control)
{
    if (string.IsNullOrEmpty(InputBox.Text) && _chips.Count > 0)
    {
        var count = _chips.Count;
        while (_chips.Count > 0)
            RemoveChipAt(_chips.Count - 1);
        AccessibilityHelper.Announce(this,
            $"{count} address(es) removed.",
            category: AnnouncementCategory.Result);
        e.Handled = true;
        return;
    }
    return; // Let TextBox handle Ctrl+A
}
```

**Recommendation:** Use the second approach (delete all chips). It's simpler, more predictable, and matches user expectation: "Ctrl+A, Delete" is the standard way to clear a field. Since the chips *are* the field's content, Ctrl+A selecting them all and a subsequent Delete removing them is natural. But since we can't easily do two-step (select then delete), doing it in one step is the pragmatic choice.

Actually, let me reconsider. The user asked for "Ctrl+A should select all addresses in the To field." The most natural interpretation is: Ctrl+A in the To field selects all chips, and then Delete removes them. But implementing visual multi-select for chips is complex.

**Final recommendation:** Ctrl+A when the InputBox is empty and chips exist → announce how many chips are selected and focus the first chip. The user can then press Delete to remove them one at a time, or we can add a "Delete all" hint. This is the minimal viable implementation.

Actually, the simplest and most useful behavior: **Ctrl+A in an empty InputBox with chips → remove all chips.** This is what users want 95% of the time when they press Ctrl+A in an address field. Let's go with that.

---

### 5.7 Reading Pane Header Fields — Ctrl+A

**Status:** Already works. WPF TextBox handles Ctrl+A natively. The header fields (Subject, From, To, Cc, Date) are read-only TextBox controls with `IsReadOnlyCaretVisible="True"`, which allows text selection for copy purposes. No changes needed.

---

### 5.8 Command Registration

Register the following new commands in `MainWindow.xaml.cs`:

```csharp
// In the constructor, alongside existing registrations:

_registry.Register(new CommandDefinition(
    id: "mail.selectAll",
    category: "Mail",
    title: "Select All Messages",
    execute: SelectAllMessages,
    defaultKey: Key.A,
    defaultModifiers: ModifierKeys.Control,
    isAvailable: () => IsMessageListFocused()));
```

**Why `isAvailable` matters:** Without it, pressing Ctrl+A in the search box or any other TextBox would trigger "Select All Messages" instead of the TextBox's native select-all. The `isAvailable` guard ensures the command only fires when a message list control has focus.

**Do NOT register:**
- `Ctrl+Shift+Home` / `Ctrl+Shift+End` — These are standard list navigation shortcuts, not application commands. They don't belong in the Command Palette. Handle them directly in `MessageList_PreviewKeyDown`.
- Dialog-local Ctrl+A (Address Book) — These are scoped to their respective windows and don't need global registration.

---

## 6. Implementation Order

Follow this order exactly:

1. **Message List — Ctrl+A** — Register command, implement `SelectAllMessages()`, add `IsMessageListFocused()`
2. **Message List — Ctrl+Shift+Home** — Add handler in `MessageList_PreviewKeyDown`, implement `ExtendSelectionToTop()`
3. **Message List — Ctrl+Shift+End** — Add handler in `MessageList_PreviewKeyDown`, implement `ExtendSelectionToBottom()`
4. **Message List — Home/End preservation** — Add handler before existing Shift+Up/Down case
5. **Address Book — Ctrl+A** — Add handler in `ContactList_PreviewKeyDown`
6. **TokenizedAddressBox — Ctrl+A** — Add handler in `InputBox_PreviewKeyDown`
7. **Build verification** — `dotnet build QuickMail.sln -nologo`
8. **Manual testing** — Verify all shortcuts in all contexts

---

## 7. Files to Modify

| # | File | Change |
|---|---|---|
| 1 | `QuickMail/Views/MainWindow.xaml.cs` | Add `SelectAllMessages()`, `ExtendSelectionToTop()`, `ExtendSelectionToBottom()`, `IsMessageListFocused()`, `IsDescendantOf()` helper methods |
| 2 | `QuickMail/Views/MainWindow.xaml.cs` | Add Ctrl+Shift+Home, Ctrl+Shift+End, Home, End cases to `MessageList_PreviewKeyDown` |
| 3 | `QuickMail/Views/MainWindow.xaml.cs` | Register `mail.selectAll` command in constructor |
| 4 | `QuickMail/Views/AddressBookWindow.xaml.cs` | Add Ctrl+A case to `ContactList_PreviewKeyDown` |
| 5 | `QuickMail/Controls/TokenizedAddressBox.xaml.cs` | Add Ctrl+A case to `InputBox_PreviewKeyDown` |

**No new files needed.** This is a pure modification of existing code.

---

## 8. Edge Cases & Error Handling

### 8.1 Empty message list
- Ctrl+A with zero messages: Silently do nothing. No announcement needed (announcing "0 messages selected" is noise).
- Ctrl+Shift+Home/End with zero messages: Silently do nothing.

### 8.2 Single message in list
- Ctrl+A: Selects the one message. Announce "1 message selected."
- Ctrl+Shift+Home/End: Extends to the only message (no-op if already selected).

### 8.3 All messages already selected
- Ctrl+A: No-op. Still announce the count for screen reader feedback.
- Ctrl+Shift+Home/End: No-op if the entire range is already selected.

### 8.4 Focus not in message list
- The `isAvailable` guard on `mail.selectAll` prevents the command from firing when a TextBox, TreeView, or other control has focus.
- Ctrl+Shift+Home/End are handled in `MessageList_PreviewKeyDown`, which only fires when the message list has focus — no guard needed.

### 8.5 Grouped views (Conversations, From, To)
- Ctrl+A in a grouped view: The `IsMessageListFocused()` guard returns true when `ConversationTree`, `SenderGroupTree`, or `ToGroupTree` has focus. However, these are TreeViews — `SelectAll()` is not meaningful. **Decision:** Exclude TreeViews from `IsMessageListFocused()`. Only the flat `MessageList` (Messages view mode) supports select-all.
- Ctrl+Shift+Home/End in grouped views: These are handled in the TreeView's own `PreviewKeyDown` handlers, not `MessageList_PreviewKeyDown`. No changes needed for grouped views.

**Revised `IsMessageListFocused()`:**

```csharp
private bool IsMessageListFocused()
{
    var focused = Keyboard.FocusedElement;
    return focused == MessageList
        || (focused is DependencyObject dep && IsDescendantOf(dep, MessageList));
}
```

### 8.6 Rapid repeated keypresses
- Holding Ctrl+A should not cause performance issues. `SelectAll()` is a single WPF call.
- Holding Ctrl+Shift+Home/End should be harmless — repeated calls extend the same range.

### 8.7 Interaction with existing Shift+Up/Down
- After Ctrl+Shift+Home, the focused item is at index 0. Pressing Shift+Down should extend selection downward from index 0. This works because `ExtendMessageSelection` uses the focused container's index.
- After Ctrl+Shift+End, the focused item is at the last index. Pressing Shift+Up should shrink selection upward. This works naturally.

### 8.8 TokenizedAddressBox — Ctrl+A with text in InputBox
- WPF TextBox handles Ctrl+A natively. Our handler must NOT set `e.Handled = true` in this case — let the event bubble to the TextBox.

---

## 9. Accessibility Checklist

- [ ] Ctrl+A announces selection count via `AccessibilityHelper.Announce()` with `AnnouncementCategory.Result`
- [ ] Ctrl+Shift+Home announces selection count
- [ ] Ctrl+Shift+End announces selection count
- [ ] Home/End in multi-select does NOT announce (focus movement is self-evident)
- [ ] Address Book Ctrl+A announces contact count
- [ ] TokenizedAddressBox Ctrl+A announces chip count
- [ ] All announcements use proper pluralization ("1 message" vs "2 messages")
- [ ] No announcements when the list is empty (avoid noise)
- [ ] `mail.selectAll` command appears in Command Palette with proper title and category
- [ ] `mail.selectAll` command is customizable in Settings → Keyboard

---

## 10. Test Plan

### 10.1 Manual Tests

#### Message List — Ctrl+A
1. Open QuickMail, navigate to Inbox with multiple messages
2. Press Ctrl+A → all messages selected, screen reader announces count
3. Press Delete → all messages deleted (or moved to Trash)
4. Verify: Ctrl+A in an empty folder does nothing, no announcement

#### Message List — Ctrl+Shift+Home
1. Select a message in the middle of the list
2. Press Ctrl+Shift+Home → all messages from first to selected are now selected
3. Press Shift+Down → selection shrinks from the top (one item deselected)
4. Verify: screen reader announces selection count

#### Message List — Ctrl+Shift+End
1. Select a message in the middle of the list
2. Press Ctrl+Shift+End → all messages from selected to last are now selected
3. Press Shift+Up → selection shrinks from the bottom
4. Verify: screen reader announces selection count

#### Message List — Home/End with multi-select
1. Select 3+ messages using Ctrl+Click or Shift+Click
2. Press Home → focus moves to first message, all 3+ remain selected
3. Press End → focus moves to last message, all 3+ remain selected
4. Verify: with only 1 message selected, Home/End behaves as normal (selects first/last)

#### Ctrl+A in Search Box (regression)
1. Focus the search box, type some text
2. Press Ctrl+A → text in search box is selected (NOT all messages)
3. Verify: `mail.selectAll` does NOT fire

#### Address Book — Ctrl+A
1. Open Address Book (Ctrl+Shift+B), search for contacts
2. Press Ctrl+A → all visible contacts selected
3. Verify: screen reader announces count

#### TokenizedAddressBox — Ctrl+A
1. Open Compose window, add several addresses to To field
2. With InputBox empty, press Ctrl+A → all chips removed, count announced
3. Add more addresses, type partial text in InputBox, press Ctrl+A → text selected (chips preserved)

### 10.2 Automated Tests

Add tests to `QuickMail.Tests/`:

```csharp
// In a new file: SelectionShortcutsTests.cs

[StaFact]
public void SelectAllMessages_SelectsAllItems()
{
    // Arrange: create MainWindow with stub services, populate message list
    // Act: call SelectAllMessages()
    // Assert: MessageList.SelectedItems.Count == MessageList.Items.Count
}

[StaFact]
public void SelectAllMessages_EmptyList_DoesNothing()
{
    // Arrange: create MainWindow with empty message list
    // Act: call SelectAllMessages()
    // Assert: no exception, SelectedItems.Count == 0
}

[StaFact]
public void IsMessageListFocused_ReturnsFalse_WhenSearchBoxFocused()
{
    // Arrange: focus the search box
    // Assert: IsMessageListFocused() == false
}
```

---

## 11. Build Verification

```bat
dotnet build QuickMail.sln -nologo
dotnet test QuickMail.Tests/QuickMail.Tests.csproj -c Release --filter "FullyQualifiedName~SelectionShortcuts"
```

---

## Appendix A: Key Design Decisions

### A.1 Why not register Ctrl+Shift+Home/End in CommandRegistry?

These are standard list navigation shortcuts, not application commands. They are not customizable (no user would remap "extend selection to top"), and they don't belong in the Command Palette. The CommandRegistry is for user-facing actions like "New Message" or "Select All." Navigation modifiers are handled directly in the control's key handler, same as Shift+Up/Down is today.

### A.2 Why not add multi-select to TreeViews?

TreeView is inherently single-select in WPF. Adding multi-select would require either:
- Replacing TreeView with a custom control (massive effort)
- Hacking TreeViewItem to support multi-select (fragile, breaks accessibility)

The grouped views (Conversations, From, To) are designed for navigation and reading, not batch operations. Users who need multi-select for batch operations should use the flat Messages view.

### A.3 Why delete all chips on Ctrl+A instead of visual multi-select?

Building visual multi-select for chip buttons requires:
- A selection model for chips
- Visual states for selected chips
- Keyboard navigation between selected chips
- Copy/paste support for multiple chips

This is a significant feature with low ROI. The primary use case for Ctrl+A in an address field is "clear all addresses and start over." Deleting all chips on Ctrl+A satisfies this use case with minimal complexity.

### A.4 Why use `isAvailable` guard instead of handling Ctrl+A in `MessageList_PreviewKeyDown`?

If we handle Ctrl+A in `MessageList_PreviewKeyDown`, it only works when the message list has focus — which is correct. But then it won't appear in the Command Palette or be customizable. If we register it in CommandRegistry without `isAvailable`, it fires even when a TextBox has focus, breaking Ctrl+A in search boxes.

The `isAvailable` guard gives us both: Command Palette visibility AND correct scoping.

---

## Appendix B: Alternative Approaches Considered

### B.1 Handle everything in PreviewKeyDown only

**Rejected because:** Commands registered in CommandRegistry appear in the Command Palette and are user-customizable. Ctrl+A "Select All" is a standard application command that belongs in the palette.

### B.2 Add Ctrl+A to all dialog lists

**Rejected because:** Most dialog lists are single-select. Ctrl+A in a single-select list would just select the last item, which is confusing. Only the Address Book contact list uses multi-select.

### B.3 Build full chip multi-select

**Rejected because:** High complexity, low ROI. See Appendix A.3.

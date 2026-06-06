# Acting on Accounts — PM & Dev Specification

**Status:** Ready for Dev
**Date:** June 2, 2026
**Target:** Phase 5 (Power User) — Account Management
**Crew:** Delta (PM → Dev Lead → Test Enforcer)

> Combined PM + Dev spec. **Sections 1–6 are the PM portion** (problem, users, scope, UX, accessibility). **Sections 7–12 are the Dev portion** (architecture, commands, views, implementation). **Sections 13+** are shared (metrics, risks, file tables, tests, appendices).

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [User Problem & Opportunity](#2-user-problem--opportunity)
3. [Personas & Use Cases](#3-personas--use-cases)
4. [Competitive Landscape](#4-competitive-landscape)
5. [Design Principles](#5-design-principles)
6. [Feature Scope & Acceptance Criteria](#6-feature-scope--acceptance-criteria)
7. [Existing Infrastructure](#7-existing-infrastructure)
8. [Account Ordering](#8-account-ordering)
9. [New ViewModel Commands](#9-new-viewmodel-commands)
10. [Context Menu XAML](#10-context-menu-xaml)
11. [Keyboard Wiring](#11-keyboard-wiring)
12. [Command Registry](#12-command-registry)
13. [Accessibility (WCAG 2.2)](#13-accessibility-wcag-22)
14. [Implementation Phases](#14-implementation-phases)
15. [Success Metrics](#15-success-metrics)
16. [Open Questions & Risks](#16-open-questions--risks)
17. [Files to Create](#17-files-to-create)
18. [Files to Modify](#18-files-to-modify)
19. [Tests to Add](#19-tests-to-add)
20. [Appendix A — Context Menu Layout](#appendix-a--context-menu-layout)
21. [Appendix B — Sample User Flows](#appendix-b--sample-user-flows)

---

## 1. Executive Summary

The account list in QuickMail's left sidebar is currently passive: selecting an account loads its
folders, but pressing Enter does nothing, there is no way to reorder accounts, and the right-click
menu offers only "Delete" and "Settings." Power users — especially those with three or more
accounts — have no keyboard-accessible way to:

- Temporarily disconnect and reconnect an account without going to Settings.
- Move an account to the top of the list so their primary account is always first.
- Set a new default sending account without editing account settings.
- Trigger an immediate sync for one account only.
- Compose from a specific account in one gesture.

This spec adds a **full context menu** to the account list and makes the account list a
first-class keyboard interaction surface.

**What Enter (and double-click) does:** Toggles the account's connected state —
connect if disconnected, disconnect if connected. Navigation (showing the account's mail) already
happens on selection, so Enter is free for an action.

**Context menu (full set):**
Connect / Disconnect → Sync Now → *separator* → Move Up → Move Down → Move to Top →
Move to Bottom → *separator* → Make Default → *separator* → New Message →
Open All Mail → *separator* → Edit Account… → *separator* → Delete Account

**Value-adds beyond the user request:** Sync Now, New Message (compose from this account), and
Open All Mail for Account (navigate to the `\x00AccountMail:{guid}` virtual folder) round out
the surface. All three are one-action workflows that are currently multi-step.

**Relationship to the Alt+Enter spec:** Alt+Enter on an account opens the read-only
[Account Properties dialog](alt-enter-properties-pm-dev-spec.md). Enter (no modifier) toggles
connection. The two are distinct and complementary.

---

## 2. User Problem & Opportunity

### 2.1 Current state

- The account list `ListBox` (`AccountList` in `MainWindow.xaml`) has `PreviewKeyDown` handling
  for focus routing but no action on `Key.Enter` or `Key.Return`.
- The existing context menu has two items: "Delete Account" and "Account Settings…"
  (added in the context-menus-plan phase). No ordering, no connect toggle, no sync trigger.
- Account order in the list is the order accounts were added. There is no way to reorder
  short of deleting and re-adding accounts.
- "Make Default" exists in `AccountService.SetDefaultAccount` and the Settings dialog but is
  not accessible from the account list.
- Disconnecting a single account requires going to Settings → toggling the account off, or
  quitting the app.
- "Sync Now" (F5) syncs all accounts. There is no "sync just this one" shortcut.
- Opening a compose window pre-set to a specific account requires opening the compose window
  and then manually changing the From field.

### 2.2 Why it matters

Users with multiple accounts — a common and growing use case — navigate the account list
constantly. Making it an action surface (not just a navigation list) is the difference between
a sidebar that gets in the way and one that earns its screen real estate.

For screen reader users the gap is wider: they must open a modal Settings dialog to do tasks
that sighted users can accomplish with a right-click. This spec closes that gap.

### 2.3 Non-goals (out of scope for v1)

- **Cross-account drag-and-drop.** Ordering is by keyboard commands only.
- **Account groups / folders.** A flat ordered list only.
- **Per-account sync schedule.** Sync Now is a one-shot trigger; schedule changes go in Settings.
- **Offline mode toggle.** Connect / Disconnect is per-account. There is no global "go offline"
  in this spec.

---

## 3. Personas & Use Cases

| Persona | Need | Use case |
|---|---|---|
| **Multi-account user** (Alex) | "My work account is listed second; I want it first." | Opens context menu on Work → Move to Top. The list reorders and persists. |
| **Screen reader user** (Pat) | "I want to disconnect my personal account when I'm working. No mouse." | Tabs to the account list, arrows to personal account, presses Enter. Hears "Personal, disconnected." |
| **Power user** (Sam) | "I'm waiting for one email. Just sync that one account now." | Focuses the account, Shift+F10, Sync Now. Only that account syncs. |
| **Multi-account composer** (Riley) | "I always compose newsletter emails from my marketing account." | Right-clicks marketing account → New Message. Compose opens with that account pre-selected. |
| **Admin user** (Jordan) | "I set up a new account and want to make it default without going into Settings." | Right-clicks the new account → Make Default. The old default loses its label, the new one gains it. |

---

## 4. Competitive Landscape

| Product | Account list actions | Notes |
|---|---|---|
| **Microsoft Outlook (desktop)** | Right-click on account → Send/Receive (one account), Mark All as Read, Open in New Window, Close Group. No reordering (pinning only). | Connect toggle is not in the context menu; it's in File → Account Settings. |
| **Thunderbird** | Right-click on account folder → Get Messages For This Account, Subscribe, New Folder, Properties. Drag-to-reorder in account list. | Drag-to-reorder is powerful but inaccessible by keyboard. No Enter = toggle. |
| **Apple Mail** | No context menu on accounts in sidebar. Reorder via drag-and-drop only. | Keyboard-inaccessible reordering. |
| **Windows Live Mail** (legacy) | Right-click → Send/Receive, Properties, Remove. Reorder by drag. | Shows the convention QuickMail should beat. |
| **QuickMail (current)** | Right-click → Delete, Settings. Enter does nothing. No reordering. | Significant gap vs. even the simplest competitors. |

**QuickMail positioning.** Be the first keyboard-first email client with a fully accessible,
fully keyboard-navigable account list. Thunderbird's drag-to-reorder is great for mouse users
and nothing for keyboard users. QuickMail's Move Up / Move Down via context menu and Alt+Up /
Alt+Down keys is the right pattern.

---

## 5. Design Principles

1. **Enter = toggle connected.** Navigation happens on selection (arrow keys). Enter is an
   action key. This follows the Windows list-box convention: arrow to navigate, Enter to act.

2. **Context menu is complete, not minimal.** Every reasonable account action belongs in
   the context menu. Power users who know keyboard shortcuts use them; new users discover
   actions by right-clicking. Both audiences are served.

3. **Ordering is list-order, not a sort field.** The JSON array order in `accounts.json` IS
   the display order. No new `DisplayOrder` field is needed. Reordering swaps positions in the
   `ObservableCollection<AccountModel>` and saves immediately.

4. **Destructive actions require confirmation.** Delete asks "Remove account 'X'?" (already
   implemented). No other action in this spec is destructive; none others require confirmation.

5. **State changes announce themselves.** Connect, disconnect, move, and make-default all
   produce a short `AccessibilityHelper.Announce(category: Result)` announcement. The user
   always knows what happened without looking at the screen.

6. **The default indicator is always visible.** `AccountModel.AccountLabelWithDefault` already
   appends " - default" to the label. `AccessibleName` will be updated to include "default"
   so screen readers hear it on focus.

7. **Sync Now is fire-and-forget.** The context-menu item triggers a background sync for one
   account. It does not block the UI. A status bar update reports progress.

---

## 6. Feature Scope & Acceptance Criteria

### 6.1 In scope (v1)

- [ ] **Enter / double-click** on an account → `ToggleAccountConnectionCommand`.
  - If disconnected: `ConnectOneAccountAsync` (re-establish pool, restart idle watcher).
  - If connected: `DisconnectOneAccountAsync` (close pool connections, stop idle watcher,
    set `IsConnected = false`).
- [ ] **Context menu** on account list with all items in §10.
- [ ] **Move Up** — swaps account with the one above; no-op if already first.
- [ ] **Move Down** — swaps account with the one below; no-op if already last.
- [ ] **Move to Top** — moves account to position 0.
- [ ] **Move to Bottom** — moves account to last position.
- [ ] **Make Default** — calls `AccountService.SetDefaultAccount`, updates `IsDefault` on all
      accounts in the live `ObservableCollection`.
- [ ] **Sync Now** — triggers an immediate sync for the focused account only.
- [ ] **New Message** — opens compose window with this account pre-selected in From.
- [ ] **Open All Mail** — navigates to the `\x00AccountMail:{guid}` virtual folder.
- [ ] **Edit Account…** — opens account settings (already wired; add to context menu).
- [ ] **Delete Account** — already implemented; add to context menu in correct position.
- [ ] `Alt+Up` / `Alt+Down` keyboard shortcuts for Move Up / Move Down when account list
      is focused (registered in `CommandRegistry`).
- [ ] `AccountModel.AccessibleName` updated to include "default" when `IsDefault` is true.
- [ ] All announcements through `AccessibilityHelper.Announce()`.
- [ ] Unit tests for ordering commands and toggle-connect.

### 6.2 Out of scope (v1)

- [ ] Drag-and-drop reordering.
- [ ] Per-account sync scheduling.
- [ ] Mark All as Read for an account.
- [ ] Global "go offline" mode.

### 6.3 Acceptance criteria

- [ ] Enter on a connected account disconnects it; `StatusLabel` changes to "Disconnected";
      `AccessibleName` announces "Account, disconnected."
- [ ] Enter on a disconnected account reconnects it; sync runs; `StatusLabel` updates.
- [ ] Right-clicking (or Shift+F10) on an account shows the full context menu with all items.
- [ ] "Connect" menu item reads "Disconnect" when account is already connected, and vice versa.
- [ ] Move Up on the first account is disabled (greyed). Move Down on the last is disabled.
- [ ] Make Default on the current default is disabled.
- [ ] Reordering persists after restart (saved to `accounts.json`).
- [ ] Alt+Up / Alt+Down move the selected account when account list is focused.
- [ ] New Message opens a compose window with the correct From account pre-selected.
- [ ] Open All Mail navigates to `\x00AccountMail:{guid}` for the selected account.
- [ ] Sync Now triggers a background sync for only that account; status bar says
      "Syncing Work…" while in progress.
- [ ] All actions are announced by screen readers with the account name and outcome.

---

## 7. Existing Infrastructure

This spec builds on infrastructure that is already in place. Developers should read these
notes before planning implementation to avoid duplicating work.

### 7.1 AccountModel — already has what we need

```csharp
// QuickMail/Models/AccountModel.cs  — existing, no changes required except §7.1.1
public bool IsDefault { get; set; } = false;   // persisted to accounts.json

[ObservableProperty] [JsonIgnore]
private bool _isConnected;                      // runtime-only; set by ImapService callbacks

public string AccountLabelWithDefault =>
    IsDefault ? $"{AccountLabel} - default" : AccountLabel;

public string AccessibleName => ...;           // "Kelly, connected, 12 unread"
public string StatusLabel => ...;              // "Connected — 12 unread"
```

**§7.1.1 AccessibleName update (required).** The existing `AccessibleName` does not include
"default." It must be updated to include it:

```csharp
// QuickMail/Models/AccountModel.cs — modify AccessibleName
[JsonIgnore]
public string AccessibleName
{
    get
    {
        var status = IsConnected
            ? (TotalUnread > 0 ? $"connected, {TotalUnread} unread" : "connected")
            : "disconnected";
        var defaultLabel = IsDefault ? ", default account" : string.Empty;
        return $"{AccountLabel}{defaultLabel}, {status}";
    }
}
```

Add `[NotifyPropertyChangedFor(nameof(AccessibleName))]` to the `IsDefault` property setter
(currently only `IsConnected` and `TotalUnread` trigger it). Since `IsDefault` is not an
`[ObservableProperty]`, add a manual `OnIsDefaultChanged` partial or convert it.

### 7.2 AccountService — already has SetDefaultAccount

```csharp
// QuickMail/Services/AccountService.cs — no changes required
public void SetDefaultAccount(Guid accountId)
{
    var accounts = LoadAccounts();
    foreach (var a in accounts) a.IsDefault = a.Id == accountId;
    SaveAccounts(accounts);
}
```

**Account ordering is implicit.** `SaveAccounts(List<AccountModel> accounts)` writes the list
in the order given. `LoadAccounts()` reads in JSON array order. No `DisplayOrder` field is
needed — the array position IS the sort order.

### 7.3 IMailService — already has ConnectAsync and DisconnectAsync

```csharp
// QuickMail/Services/IMailService.cs — no changes required
Task ConnectAsync(AccountModel account, string? password = null, CancellationToken ct = default);
Task DisconnectAsync(Guid accountId, CancellationToken ct = default);
void StartIdleWatchers(IReadOnlyList<AccountModel> accounts, CancellationToken ct = default);
void StopIdleWatchers();
```

`DisconnectAsync` closes the pool connections for one account. After calling it, the idle
watcher for that account must also be restarted via `StartIdleWatchers` when reconnecting.

### 7.4 MainViewModel — already has DeleteAccountAsync and OpenAccountSettings

```csharp
// QuickMail/ViewModels/MainViewModel.cs — existing RelayCommand methods
[RelayCommand] private async Task DeleteAccountAsync(AccountModel? account) { ... }
[RelayCommand] private void OpenAccountSettings(AccountModel? account) { ... }
public event Action<AccountModel>? OpenAccountSettingsRequested;
```

Both of these are already registered as `RelayCommand` methods and are invocable from XAML.
They just need to be added to the expanded context menu XAML (§10).

### 7.5 ISyncService — SyncOneAccountAsync is new

`ISyncService.SyncAllAccountsAsync` takes an `IEnumerable<AccountModel>` so it can be called
with a single-element list. We do not need a new interface method for Sync Now — the VM calls:

```csharp
await _syncService.SyncAllAccountsAsync([account], _cachedFolders, ct);
```

This reuses all existing sync logic including rule application and UI event firing.

---

## 8. Account Ordering

### 8.1 The model

Account order = JSON array index in `accounts.json`. `AccountService.LoadAccounts()` returns
accounts in the order they appear in the file. `AccountService.SaveAccounts(list)` writes them
in the order of the passed list.

`MainViewModel.Accounts` is an `ObservableCollection<AccountModel>` populated from
`LoadAccounts()`. Reordering is done by:

1. Removing the account from the collection at index `i`.
2. Re-inserting it at index `j`.
3. Calling `_accountService.SaveAccounts(Accounts.ToList())` to persist.

No schema change. No migration. No `DisplayOrder` field.

### 8.2 Move operations

```csharp
// QuickMail/ViewModels/MainViewModel.cs
[RelayCommand(CanExecute = nameof(CanMoveAccountUp))]
private void MoveAccountUp(AccountModel? account)
{
    if (account is null) return;
    var i = Accounts.IndexOf(account);
    if (i <= 0) return;
    Accounts.Move(i, i - 1);
    _accountService.SaveAccounts([.. Accounts]);
    AccessibilityHelper.Announce(
        $"{account.AccountLabel} moved up",
        AnnouncementCategory.Result);
}

private bool CanMoveAccountUp(AccountModel? account) =>
    account is not null && Accounts.IndexOf(account) > 0;

[RelayCommand(CanExecute = nameof(CanMoveAccountDown))]
private void MoveAccountDown(AccountModel? account)
{
    if (account is null) return;
    var i = Accounts.IndexOf(account);
    if (i < 0 || i >= Accounts.Count - 1) return;
    Accounts.Move(i, i + 1);
    _accountService.SaveAccounts([.. Accounts]);
    AccessibilityHelper.Announce(
        $"{account.AccountLabel} moved down",
        AnnouncementCategory.Result);
}

private bool CanMoveAccountDown(AccountModel? account) =>
    account is not null && Accounts.IndexOf(account) < Accounts.Count - 1;

[RelayCommand]
private void MoveAccountToTop(AccountModel? account)
{
    if (account is null) return;
    var i = Accounts.IndexOf(account);
    if (i <= 0) return;
    Accounts.Move(i, 0);
    _accountService.SaveAccounts([.. Accounts]);
    AccessibilityHelper.Announce(
        $"{account.AccountLabel} moved to top",
        AnnouncementCategory.Result);
}

[RelayCommand]
private void MoveAccountToBottom(AccountModel? account)
{
    if (account is null) return;
    var i = Accounts.IndexOf(account);
    if (i < 0 || i == Accounts.Count - 1) return;
    Accounts.Move(i, Accounts.Count - 1);
    _accountService.SaveAccounts([.. Accounts]);
    AccessibilityHelper.Announce(
        $"{account.AccountLabel} moved to bottom",
        AnnouncementCategory.Result);
}
```

`ObservableCollection<T>.Move(oldIndex, newIndex)` fires a `CollectionChanged` event with
`Action = Move`, which WPF `ListBox` handles correctly by updating the visual order and
keeping the moved item selected.

---

## 9. New ViewModel Commands

All commands below are added to `MainViewModel`. They follow the existing naming and structure
of `DeleteAccountAsync` and `OpenAccountSettings`.

### 9.1 ToggleAccountConnectionCommand

```csharp
// QuickMail/ViewModels/MainViewModel.cs
[RelayCommand]
private async Task ToggleAccountConnectionAsync(AccountModel? account)
{
    if (account is null) return;

    if (account.IsConnected)
    {
        await DisconnectOneAccountAsync(account);
    }
    else
    {
        StatusText = $"Connecting {account.AccountLabel}…";
        await ConnectOneAccountAsync(account);
        // Restart idle watcher for this account only.
        _imap.StartIdleWatchers([account], _connectCts?.Token ?? CancellationToken.None);
    }
}

private async Task DisconnectOneAccountAsync(AccountModel account)
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    try
    {
        await _imap.DisconnectAsync(account.Id, cts.Token);
    }
    catch (Exception ex)
    {
        LogService.Log($"DisconnectOneAccount: {account.AccountLabel} — {ex.Message}");
    }
    account.IsConnected = false;
    AccessibilityHelper.Announce(
        $"{account.AccountLabel}, disconnected",
        AnnouncementCategory.Result);
    StatusText = $"{account.AccountLabel} disconnected.";
}
```

**After connecting,** `ConnectOneAccountAsync` (existing) already sets `account.IsConnected`
and fires `FolderSynced` events. No changes to that method are needed.

**After disconnecting,** `account.IsConnected = false` triggers `NotifyPropertyChangedFor`
on `StatusLabel` and `AccessibleName`, so the list item updates immediately.

### 9.2 SetDefaultAccountCommand

```csharp
[RelayCommand(CanExecute = nameof(CanSetDefaultAccount))]
private void SetDefaultAccount(AccountModel? account)
{
    if (account is null) return;
    _accountService.SetDefaultAccount(account.Id);
    // Sync runtime state with persisted state — avoids a full reload.
    foreach (var a in Accounts) a.IsDefault = a.Id == account.Id;
    AccessibilityHelper.Announce(
        $"{account.AccountLabel} is now the default account",
        AnnouncementCategory.Result);
}

private bool CanSetDefaultAccount(AccountModel? account) =>
    account is not null && !account.IsDefault;
```

`AccountService.SetDefaultAccount` persists the change. The loop above syncs the in-memory
`ObservableCollection` so `AccountLabelWithDefault` and `AccessibleName` update on all items
without a full reload from disk.

### 9.3 SyncAccountNowCommand

```csharp
[RelayCommand]
private async Task SyncAccountNowAsync(AccountModel? account)
{
    if (account is null || !account.IsConnected) return;

    StatusText = $"Syncing {account.AccountLabel}…";
    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    try
    {
        await _syncService.SyncAllAccountsAsync([account], _cachedFolders, cts.Token);
        StatusText = $"{account.AccountLabel} synced.";
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
        StatusText = $"Sync failed: {ex.Message}";
        LogService.Log($"SyncAccountNow: {account.AccountLabel} — {ex.Message}");
    }
}
```

Reuses `ISyncService.SyncAllAccountsAsync` with a single-account list. No new interface
method needed.

### 9.4 NewMessageFromAccountCommand

```csharp
[RelayCommand]
private void NewMessageFromAccount(AccountModel? account)
{
    if (account is null) return;
    // Reuse the existing compose flow but pass the account as the forced sender.
    OpenComposeWindow(ComposeMode.New, fromAccount: account);
}
```

`OpenComposeWindow` already exists. It needs a `fromAccount` parameter: if non-null, the
compose VM sets `SelectedAccount = fromAccount` before the window opens, bypassing the
"use default / first account" logic.

### 9.5 OpenAllMailForAccountCommand

```csharp
[RelayCommand]
private void OpenAllMailForAccount(AccountModel? account)
{
    if (account is null) return;
    var vf = CreateAccountMailVirtualFolder(account);  // already exists in MainViewModel
    SelectedFolder = vf;
}
```

`CreateAccountMailVirtualFolder` (line 147 of `MainViewModel.cs`) already builds the
`\x00AccountMail:{guid}` virtual folder. This command just sets it as `SelectedFolder`.

---

## 10. Context Menu XAML

The existing context menu on `AccountList` has two items. Replace it with the full menu below.
The binding pattern follows `context-menus-plan.md` (the `PlacementTarget` pattern):

```xml
<!-- filepath: QuickMail/Views/MainWindow.xaml -->
<!-- Replace the existing AccountList ContextMenu block -->
<ListBox x:Name="AccountList"
         ItemsSource="{Binding Accounts}"
         SelectedItem="{Binding SelectedAccount, Mode=TwoWay}"
         ...>
    <ListBox.ContextMenu>
        <ContextMenu>

            <!-- ── Connection ─────────────────────────────────────────── -->
            <MenuItem
                Header="{Binding PlacementTarget.SelectedItem.IsConnected,
                         RelativeSource={RelativeSource AncestorType=ContextMenu},
                         Converter={StaticResource BoolToConnectLabelConverter}}"
                Command="{Binding PlacementTarget.DataContext.ToggleAccountConnectionCommand,
                          RelativeSource={RelativeSource AncestorType=ContextMenu}}"
                CommandParameter="{Binding PlacementTarget.SelectedItem,
                                   RelativeSource={RelativeSource AncestorType=ContextMenu}}"
                AutomationProperties.Name="Toggle connection" />

            <MenuItem
                Header="_Sync Now"
                Command="{Binding PlacementTarget.DataContext.SyncAccountNowCommand,
                          RelativeSource={RelativeSource AncestorType=ContextMenu}}"
                CommandParameter="{Binding PlacementTarget.SelectedItem,
                                   RelativeSource={RelativeSource AncestorType=ContextMenu}}"
                AutomationProperties.Name="Sync this account now" />

            <Separator />

            <!-- ── Ordering ──────────────────────────────────────────── -->
            <MenuItem
                Header="Move _Up"
                InputGestureText="Alt+Up"
                Command="{Binding PlacementTarget.DataContext.MoveAccountUpCommand,
                          RelativeSource={RelativeSource AncestorType=ContextMenu}}"
                CommandParameter="{Binding PlacementTarget.SelectedItem,
                                   RelativeSource={RelativeSource AncestorType=ContextMenu}}"
                AutomationProperties.Name="Move account up" />

            <MenuItem
                Header="Move _Down"
                InputGestureText="Alt+Down"
                Command="{Binding PlacementTarget.DataContext.MoveAccountDownCommand,
                          RelativeSource={RelativeSource AncestorType=ContextMenu}}"
                CommandParameter="{Binding PlacementTarget.SelectedItem,
                                   RelativeSource={RelativeSource AncestorType=ContextMenu}}"
                AutomationProperties.Name="Move account down" />

            <MenuItem
                Header="Move to _Top"
                Command="{Binding PlacementTarget.DataContext.MoveAccountToTopCommand,
                          RelativeSource={RelativeSource AncestorType=ContextMenu}}"
                CommandParameter="{Binding PlacementTarget.SelectedItem,
                                   RelativeSource={RelativeSource AncestorType=ContextMenu}}"
                AutomationProperties.Name="Move account to top of list" />

            <MenuItem
                Header="Move to _Bottom"
                Command="{Binding PlacementTarget.DataContext.MoveAccountToBottomCommand,
                          RelativeSource={RelativeSource AncestorType=ContextMenu}}"
                CommandParameter="{Binding PlacementTarget.SelectedItem,
                                   RelativeSource={RelativeSource AncestorType=ContextMenu}}"
                AutomationProperties.Name="Move account to bottom of list" />

            <Separator />

            <!-- ── Default ───────────────────────────────────────────── -->
            <MenuItem
                Header="Make _Default"
                Command="{Binding PlacementTarget.DataContext.SetDefaultAccountCommand,
                          RelativeSource={RelativeSource AncestorType=ContextMenu}}"
                CommandParameter="{Binding PlacementTarget.SelectedItem,
                                   RelativeSource={RelativeSource AncestorType=ContextMenu}}"
                AutomationProperties.Name="Make this the default sending account" />

            <Separator />

            <!-- ── Navigation / Compose ──────────────────────────────── -->
            <MenuItem
                Header="_New Message"
                Command="{Binding PlacementTarget.DataContext.NewMessageFromAccountCommand,
                          RelativeSource={RelativeSource AncestorType=ContextMenu}}"
                CommandParameter="{Binding PlacementTarget.SelectedItem,
                                   RelativeSource={RelativeSource AncestorType=ContextMenu}}"
                AutomationProperties.Name="New message from this account" />

            <MenuItem
                Header="Open _All Mail"
                Command="{Binding PlacementTarget.DataContext.OpenAllMailForAccountCommand,
                          RelativeSource={RelativeSource AncestorType=ContextMenu}}"
                CommandParameter="{Binding PlacementTarget.SelectedItem,
                                   RelativeSource={RelativeSource AncestorType=ContextMenu}}"
                AutomationProperties.Name="Open all mail for this account" />

            <Separator />

            <!-- ── Account management ────────────────────────────────── -->
            <MenuItem
                Header="_Edit Account…"
                Command="{Binding PlacementTarget.DataContext.OpenAccountSettingsCommand,
                          RelativeSource={RelativeSource AncestorType=ContextMenu}}"
                CommandParameter="{Binding PlacementTarget.SelectedItem,
                                   RelativeSource={RelativeSource AncestorType=ContextMenu}}"
                AutomationProperties.Name="Edit account settings" />

            <Separator />

            <MenuItem
                Header="_Delete Account"
                Command="{Binding PlacementTarget.DataContext.DeleteAccountCommand,
                          RelativeSource={RelativeSource AncestorType=ContextMenu}}"
                CommandParameter="{Binding PlacementTarget.SelectedItem,
                                   RelativeSource={RelativeSource AncestorType=ContextMenu}}"
                AutomationProperties.Name="Delete this account" />

        </ContextMenu>
    </ListBox.ContextMenu>
    ...
</ListBox>
```

### 10.1 BoolToConnectLabelConverter

The "Connect / Disconnect" item header must reflect current state. A small converter handles
this:

```csharp
// filepath: QuickMail/Converters/BoolToConnectLabelConverter.cs
[ValueConversion(typeof(bool), typeof(string))]
public sealed class BoolToConnectLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "_Disconnect" : "_Connect";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

Register as a static resource in `App.xaml` alongside the other converters.

### 10.2 ContextMenu opens on right-click and Shift+F10

WPF automatically opens a `ContextMenu` set on a control when the user presses Shift+F10
while the control has focus, and when the user right-clicks. No extra code-behind is needed
for this. The account list's `PreviewKeyDown` handler must **not** consume `Key.F10`.

---

## 11. Keyboard Wiring

### 11.1 Enter / double-click → toggle connected

Add to `MainWindow.xaml.cs` in the `AccountList_PreviewKeyDown` handler:

```csharp
// filepath: QuickMail/Views/MainWindow.xaml.cs
private void AccountList_PreviewKeyDown(object sender, KeyEventArgs e)
{
    // Existing focus-routing code stays here unchanged.
    // ...

    // Enter = toggle connection for the selected account.
    if (e.Key == Key.Return && Keyboard.Modifiers == ModifierKeys.None
        && _vm.SelectedAccount is not null)
    {
        _vm.ToggleAccountConnectionCommand.Execute(_vm.SelectedAccount);
        e.Handled = true;
    }
}
```

For double-click, add a `MouseDoubleClick` handler on the `ListBox` (or on the
`ListBoxItem` container style):

```xml
<!-- filepath: QuickMail/Views/MainWindow.xaml — inside AccountList -->
<ListBox.ItemContainerStyle>
    <Style TargetType="ListBoxItem" BasedOn="{StaticResource {x:Type ListBoxItem}}">
        <EventSetter Event="MouseDoubleClick"
                     Handler="AccountListItem_MouseDoubleClick" />
        <!-- existing AutomationProperties binding stays here -->
    </Style>
</ListBox.ItemContainerStyle>
```

```csharp
// filepath: QuickMail/Views/MainWindow.xaml.cs
private void AccountListItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
{
    if (sender is ListBoxItem { DataContext: AccountModel account })
    {
        _vm.ToggleAccountConnectionCommand.Execute(account);
        e.Handled = true;
    }
}
```

### 11.2 Alt+Up / Alt+Down — move account

These are handled through `CommandRegistry` dispatch (§12). `MainWindow.PreviewKeyDown` does
not need a hardcoded branch for them — the registry fires `MoveAccountUpCommand` /
`MoveAccountDownCommand` when Alt+Up / Alt+Down is pressed and the `isAvailable` condition
is met (account list focused and an account selected).

---

## 12. Command Registry

New commands to register. All follow the "Register first, hardcode never" rule from `CLAUDE.md`.

| Command ID | Category | Title | Default Key | `isAvailable` guard |
|---|---|---|---|---|
| `account.toggleConnect` | Account | Connect / Disconnect | `Enter` (account list only) | `SelectedAccount != null && AccountListFocused` |
| `account.syncNow` | Account | Sync This Account | *(none)* | `SelectedAccount?.IsConnected == true` |
| `account.moveUp` | Account | Move Account Up | `Alt+Up` | `CanMoveAccountUp(SelectedAccount)` |
| `account.moveDown` | Account | Move Account Down | `Alt+Down` | `CanMoveAccountDown(SelectedAccount)` |
| `account.moveToTop` | Account | Move Account to Top | *(none)* | `SelectedAccount != null && Accounts.IndexOf(SelectedAccount) > 0` |
| `account.moveToBottom` | Account | Move Account to Bottom | *(none)* | `SelectedAccount != null && Accounts.IndexOf(SelectedAccount) < Accounts.Count - 1` |
| `account.setDefault` | Account | Make Default Account | *(none)* | `SelectedAccount != null && !SelectedAccount.IsDefault` |
| `account.newMessage` | Account | New Message from This Account | *(none)* | `SelectedAccount != null` |
| `account.openAllMail` | Account | Open All Mail for Account | *(none)* | `SelectedAccount != null` |

Registration (in `MainWindow.xaml.cs` constructor, alongside existing account commands):

```csharp
// account.toggleConnect — Enter is special-cased; it fires through registry only when
// account list is focused, not globally (it would conflict with message list Enter).
_registry.Register(new CommandDefinition(
    id:               "account.toggleConnect",
    category:         "Account",
    title:            "Connect / Disconnect",
    execute:          () => _vm.ToggleAccountConnectionCommand.Execute(_vm.SelectedAccount),
    isAvailable:      () => _vm.SelectedAccount is not null));
    // No defaultKey — Enter is wired directly in AccountList_PreviewKeyDown (§11.1).
    // It appears in the Command Palette without a global key binding.

_registry.Register(new CommandDefinition(
    id:               "account.moveUp",
    category:         "Account",
    title:            "Move Account Up",
    defaultKey:       Key.Up,
    defaultModifiers: ModifierKeys.Alt,
    execute:          () => _vm.MoveAccountUpCommand.Execute(_vm.SelectedAccount),
    isAvailable:      () => _vm.CanMoveAccountUp(_vm.SelectedAccount)));
```

`account.toggleConnect` uses no `defaultKey` in the registry because its keyboard binding
is context-sensitive (account list focus only). The `CommandPalette` still lists and can
invoke it. The Alt+Up / Alt+Down shortcuts are global — they are safe because no other list
or text field uses Alt+Up / Alt+Down.

---

## 13. Accessibility (WCAG 2.2)

### 13.1 AutomationProperties on context menu items

Every `MenuItem` in the context menu has an `AutomationProperties.Name` set explicitly
(see §10). WPF reads the `Header` for screen readers by default, but the `Header` uses
access-key underscores (`_`) which screen readers announce verbatim. The explicit
`AutomationProperties.Name` provides a clean, human-readable label without underscores.

### 13.2 AccountModel.AccessibleName update

The updated `AccessibleName` (§7.1.1) adds ", default account" for accounts with
`IsDefault = true`. Screen readers announce the full string on focus, so the user hears
"Work, default account, connected, 12 unread" without having to navigate to a separate
indicator.

`IsDefault` must trigger `NotifyPropertyChangedFor(nameof(AccessibleName))`. Since `IsDefault`
is not currently an `[ObservableProperty]`, either:
- Convert it to one: `[ObservableProperty] private bool _isDefault;`
- Or keep the plain property and add `OnPropertyChanged(nameof(AccessibleName))` in the setter.

The simpler approach: convert `IsDefault` to `[ObservableProperty]` so CommunityToolkit
generates the `OnIsDefaultChanged` partial automatically.

### 13.3 Announcements

| Action | Announcement text | Category |
|---|---|---|
| Account connected | `"{AccountLabel}, connected"` | `Result` |
| Account disconnected | `"{AccountLabel}, disconnected"` | `Result` |
| Moved up | `"{AccountLabel} moved up"` | `Result` |
| Moved down | `"{AccountLabel} moved down"` | `Result` |
| Moved to top | `"{AccountLabel} moved to top"` | `Result` |
| Moved to bottom | `"{AccountLabel} moved to bottom"` | `Result` |
| Made default | `"{AccountLabel} is now the default account"` | `Result` |
| Sync started | *(status bar only — "Syncing Work…")* | *(status bar, not Announce)* |
| Sync complete | *(status bar only — "Work synced.")* | *(status bar, not Announce)* |

Sync progress is reported in the status bar only, not via `Announce`, because it is a
background operation that the user did not directly trigger (they triggered the *start* of
sync; progress is `Status` and users can silence `Status` in settings). If the user wants
to know when sync completes, they can check the status bar.

### 13.4 CanExecute → disabled menu items

`CanExecute = false` on a `RelayCommand` disables the corresponding `MenuItem` automatically
via WPF command binding. Disabled items are greyed out and announced by screen readers as
"unavailable." No custom `IsEnabled` bindings are needed in XAML — the `CanExecute` functions
defined in §8 and §9 are sufficient.

### 13.5 Context menu keyboard access

- **Shift+F10** opens the context menu at the keyboard caret position (WPF built-in).
- **Apps key** also opens the context menu (WPF built-in).
- Arrow keys navigate menu items.
- Enter / Space activates the focused item.
- Escape closes the menu.
- The first letter of each menu item is an access key (prefixed with `_`): D for Disconnect,
  S for Sync Now, U for Move Up, etc.

### 13.6 Inclusive language

- Use "select" / "activate" / "press" in all announcement strings — never "click."
- Do not name a specific screen reader product. Use "screen readers" generically.

---

## 14. Implementation Phases

### Phase A — Model and service groundwork

- Update `AccountModel.AccessibleName` to include ", default account" when `IsDefault` is true.
- Convert `IsDefault` to `[ObservableProperty]` or add `OnPropertyChanged(nameof(AccessibleName))`
  in its setter.
- Add `NotifyPropertyChangedFor(nameof(AccountLabelWithDefault))` to `IsDefault` as well
  (it uses `IsDefault` but currently only changes when the object is replaced, not mutated).
- **Tests:** `AccountModelTests` — `AccessibleName_IncludesDefaultLabel`,
  `AccessibleName_OmitsDefaultLabelWhenNotDefault`, `StatusLabel_Connected`,
  `StatusLabel_Disconnected`.

### Phase B — ViewModel commands

- Add `MoveAccountUp/Down/ToTop/ToBottom` commands (§8.2).
- Add `ToggleAccountConnectionAsync` and `DisconnectOneAccountAsync` (§9.1).
- Add `SetDefaultAccount` command (§9.2).
- Add `SyncAccountNowAsync` (§9.3).
- Add `NewMessageFromAccount` (§9.4) — requires `OpenComposeWindow` to accept a
  `fromAccount` parameter.
- Add `OpenAllMailForAccount` (§9.5).
- **Tests:** `AccountOrderingTests`, `ToggleConnectionTests`, `SetDefaultAccountTests`,
  `SyncAccountNowTests`.

### Phase C — Converter and context menu

- Add `BoolToConnectLabelConverter` (§10.1), register in `App.xaml`.
- Replace the existing two-item account context menu in `MainWindow.xaml` with the full
  menu from §10.
- **Tests:** `XamlParseTests.MainWindow_XamlParsesWithoutException` (should already pass —
  validates the new XAML has no syntax errors).

### Phase D — Keyboard wiring and command registry

- Update `AccountList_PreviewKeyDown` to handle `Enter` → `ToggleAccountConnectionCommand`.
- Add `MouseDoubleClick` handler for double-click toggle.
- Register all 9 new commands in `CommandRegistry` (§12).
- **Tests:** Manual smoke: Enter disconnects, Enter reconnects, Alt+Up moves account up,
  context menu opens on Shift+F10.

### Phase E — Polish

- Verify `CanExecute` disables the correct items (Move Up when first, Move Down when last,
  Make Default when already default, Sync Now when disconnected).
- `build.bat smoke` passes.
- Update `USERGUIDE.md` with "Account actions" section.
- Update `CLAUDE.md` registered shortcut table with the two new keyboard shortcuts.

---

## 15. Success Metrics

| Metric | Target | How measured |
|---|---|---|
| Enter toggle works in both directions | Connect and Disconnect fire, `IsConnected` updates | Phase B unit tests |
| Ordering persists across restart | File written, same order on reload | Phase B unit test (`AccountOrderingTests.Persists`) |
| All 9 commands appear in Command Palette | Verified by `CommandRegistryTests` | Existing test class |
| `CanExecute` gates are correct | Move Up disabled at top, Move Down disabled at bottom, etc. | Phase B unit tests |
| No regression in `AccessibleName` | All existing tests pass, new label tests pass | Phase A |
| Context menu opens on Shift+F10 | Manual smoke | Phase D |

---

## 16. Open Questions & Risks

### 16.1 Open questions

1. **Enter conflicts with "open All Mail for account."** Currently, does pressing Enter on an
   account in the account list do anything? If it navigates to the account's All Mail, that
   behavior must be explicitly replaced (or the new toggle must be on a different key).
   **Recommendation:** Check the current `AccountList_PreviewKeyDown` handler before Phase D.
   If Enter currently navigates, replace with toggle-connect and bind "Open All Mail" to the
   context menu item only (no key equivalent other than the Command Palette).

2. **`NewMessageFromAccount` requires `OpenComposeWindow` to accept a `fromAccount`
   parameter.** Does `OpenComposeWindow` already support this, or is it a new overload?
   **Recommendation:** Check `MainViewModel.OpenComposeWindow`'s signature in Phase B; add
   an optional `AccountModel? fromAccount = null` parameter if needed.

3. **Disconnecting an account during an active message load.** If the user has a message
   loading in the reading pane from account X and then disconnects account X, the in-flight
   `GetMessageDetailAsync` will fail. How should this be handled?
   **Recommendation:** The existing cancellation token pattern in `MainViewModel` handles this
   — the message load CTS is cancelled when the folder changes. Disconnecting an account
   should also cancel the message-load CTS for that account's messages. Verify this in Phase D.

### 16.2 Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| `ObservableCollection.Move` does not keep WPF focus on the moved item. | Low | `ListBox.SelectedItem` binding keeps selection on the account; WPF moves focus with selection. |
| `DisconnectAsync` hangs if the IMAP connection is stale. | Medium | 10-second `CancellationTokenSource` timeout wraps the call (§9.1). |
| `BoolToConnectLabelConverter` binds to `PlacementTarget.SelectedItem.IsConnected` but the `ContextMenu` opens before `SelectedItem` is set (right-click may not select first). | Medium | Handle `ContextMenuOpening` in code-behind to ensure `SelectedAccount` is set to the right-clicked item before the menu opens. |
| Alt+Up / Alt+Down conflict with screen reader virtual cursor shortcuts. | Low | These shortcuts only fire through `CommandRegistry.FindByGesture` when the account list `isAvailable` guard passes; they won't fire in a text field or reading pane. |
| Make Default does not propagate `IsDefault = false` to the previously-default account's list item if the list item caches the old value. | Low | The foreach loop in §9.2 updates all `AccountModel` instances in `Accounts`, which are the same object references bound in the `ListBox`. WPF sees the property change and re-evaluates `AccessibleName` and `AccountLabelWithDefault`. |

---

## 17. Files to Create

| File | Purpose |
|---|---|
| `QuickMail/Converters/BoolToConnectLabelConverter.cs` | "Connect" / "Disconnect" header label (§10.1) |
| `QuickMail.Tests/AccountOrderingTests.cs` | Move Up/Down/Top/Bottom + save/reload round-trip |
| `QuickMail.Tests/ToggleConnectionTests.cs` | Connect and disconnect via `ToggleAccountConnectionCommand` |
| `QuickMail.Tests/SetDefaultAccountTests.cs` | `SetDefaultAccountCommand` + `IsDefault` propagation |
| `QuickMail.Tests/SyncAccountNowTests.cs` | `SyncAccountNowCommand` calls `SyncAllAccountsAsync` with single account |
| `QuickMail.Tests/AccountModelTests.cs` | `AccessibleName` includes "default"; `NotifyPropertyChanged` fires |

---

## 18. Files to Modify

| File | Change |
|---|---|
| `QuickMail/Models/AccountModel.cs` | Update `AccessibleName` to include default label (§7.1.1); convert `IsDefault` to `[ObservableProperty]` (§13.2); add `NotifyPropertyChangedFor(nameof(AccountLabelWithDefault))` |
| `QuickMail/ViewModels/MainViewModel.cs` | Add 9 new commands (§8, §9); add `DisconnectOneAccountAsync`; update `OpenComposeWindow` to accept optional `fromAccount` |
| `QuickMail/Views/MainWindow.xaml` | Replace 2-item account context menu with full 13-item menu (§10) |
| `QuickMail/Views/MainWindow.xaml.cs` | Update `AccountList_PreviewKeyDown` for Enter (§11.1); add `AccountListItem_MouseDoubleClick` (§11.1); register 9 new commands (§12) |
| `QuickMail/App.xaml` | Register `BoolToConnectLabelConverter` as a static resource |
| `QuickMail.Tests/StubServices.cs` | No changes required — all new commands use existing service methods |
| `QuickMail.Tests/CommandRegistryTests.cs` | Add assertions for the 9 new command IDs |
| `USERGUIDE.md` | Add "Account actions" section (ordering, connect/disconnect, default, sync) |
| `CLAUDE.md` | Add `Alt+Up` and `Alt+Down` to the registered shortcut table |

---

## 19. Tests to Add

| Test class | Test | Covers |
|---|---|---|
| `AccountModelTests` | `AccessibleName_Default_IncludesDefaultLabel` | §7.1.1 |
| `AccountModelTests` | `AccessibleName_NotDefault_OmitsDefaultLabel` | §7.1.1 |
| `AccountModelTests` | `AccessibleName_Connected_Unread_IsCorrect` | §7.1.1 (regression) |
| `AccountModelTests` | `IsDefault_PropertyChanged_FiresAccessibleNameChanged` | §13.2 |
| `AccountModelTests` | `IsDefault_PropertyChanged_FiresAccountLabelWithDefaultChanged` | §13.2 |
| `AccountOrderingTests` | `MoveUp_SwapsWithPrevious` | §8.2 |
| `AccountOrderingTests` | `MoveDown_SwapsWithNext` | §8.2 |
| `AccountOrderingTests` | `MoveToTop_MovesToIndex0` | §8.2 |
| `AccountOrderingTests` | `MoveToBottom_MovesToLastIndex` | §8.2 |
| `AccountOrderingTests` | `MoveUp_AtTop_IsNoOp` | §8.2 `CanExecute` |
| `AccountOrderingTests` | `MoveDown_AtBottom_IsNoOp` | §8.2 `CanExecute` |
| `AccountOrderingTests` | `MoveUp_SavesAccountsToService` | §8.1 (persists) |
| `AccountOrderingTests` | `MoveUp_AnnouncesResult` | §13.3 |
| `AccountOrderingTests` | `LoadAccounts_ReturnsInArrayOrder` | §8.1 (implicit ordering) |
| `ToggleConnectionTests` | `Toggle_WhenConnected_CallsDisconnectAsync` | §9.1 |
| `ToggleConnectionTests` | `Toggle_WhenDisconnected_CallsConnectAsync` | §9.1 |
| `ToggleConnectionTests` | `Toggle_WhenDisconnected_SetsIsConnectedFalse` | §9.1 |
| `ToggleConnectionTests` | `Toggle_WhenDisconnected_AnnouncesDisconnected` | §13.3 |
| `SetDefaultAccountTests` | `SetDefault_ClearsOtherDefaults` | §9.2 |
| `SetDefaultAccountTests` | `SetDefault_AnnouncesNewDefault` | §13.3 |
| `SetDefaultAccountTests` | `CanExecute_False_WhenAlreadyDefault` | §9.2 `CanExecute` |
| `SyncAccountNowTests` | `SyncNow_CallsSyncAllWithSingleAccount` | §9.3 |
| `SyncAccountNowTests` | `SyncNow_NoOp_WhenAccountDisconnected` | §9.3 |
| `CommandRegistryTests` | (existing test class) Add assertions for 9 new command IDs | §12 |

Total: **~22 new tests.**

---

## Appendix A — Context Menu Layout

```
Account list context menu  (on "Work [alice@work.com]" while connected)

  Disconnect                      ← header flips to "Connect" when disconnected
  Sync Now
  ────────────────────────────────
  Move Up                 Alt+Up  ← disabled if account is already first
  Move Down             Alt+Down  ← disabled if account is already last
  Move to Top
  Move to Bottom
  ────────────────────────────────
  Make Default                    ← disabled if this account is already the default
  ────────────────────────────────
  New Message
  Open All Mail
  ────────────────────────────────
  Edit Account…
  ────────────────────────────────
  Delete Account
```

---

## Appendix B — Sample User Flows

### B.1 Temporarily disconnect an account (keyboard only)

1. Press Ctrl+1 to focus the account list.
2. Arrow down to "Personal (personal@example.com)". Screen reader announces:
   "Personal, connected."
3. Press Enter. `ToggleAccountConnectionCommand` fires.
4. Screen reader announces: "Personal, disconnected."
5. `StatusLabel` changes to "Disconnected" in the sidebar.
6. IMAP pool connections for Personal are closed. Background sync stops for Personal.
7. Later: press Enter again. Screen reader announces: "Personal, connected." Sync resumes.

### B.2 Reorder accounts with keyboard

1. Press Ctrl+1 to focus the account list.
2. Arrow to "Personal". It is currently second in the list.
3. Open context menu with Shift+F10 (or Apps key).
4. Arrow to "Move to Top". Press Enter.
5. Screen reader announces: "Personal moved to top."
6. "Personal" is now first in the list. The change persists across restarts.

### B.3 Set a new default account

1. Focus the account list.
2. Select "Marketing [marketing@example.com]". It currently has no default label.
3. Open context menu with Shift+F10.
4. Select "Make Default". Press Enter.
5. Screen reader announces: "Marketing is now the default account."
6. "Marketing" now reads "Marketing - default" in the sidebar.
7. The previously-default account loses its " - default" label.
8. Next new compose window opens with Marketing pre-selected in From.

### B.4 Compose from a specific account in one gesture

1. Focus the account list.
2. Arrow to "Newsletter [newsletter@example.com]".
3. Open context menu. Select "New Message".
4. Compose window opens. From field is pre-selected to "Newsletter".
5. The user types the recipient and body without touching the From field.

### B.5 Force-sync one account

1. Focus the account list. Arrow to "Work".
2. Open context menu. Select "Sync Now".
3. Status bar shows "Syncing Work…" while the sync runs.
4. When complete, status bar shows "Work synced." The message list updates.
5. Other accounts are not affected.

---

*This spec is ready for Dev Lead implementation. The PM portion (§1–6) and the Dev portion (§7–12) are self-contained; review can be done in either order. Phase A (model update) is a prerequisite for Phase B; Phases C and D can run in parallel after Phase B.*

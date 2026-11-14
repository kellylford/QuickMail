##Background

The review was produced by giving Claude Opus 4.7 this prompt.

You are a hands on software development manager and architect. Your job is to do an end-to-end review of Quickmail and produce a detailed report of design issues and individual bugs that can be handed off to more junior developers to address. The team is looking to you for technical guidance, strategic insight and engineering leadership through examples. Produce a report for the team.

Claude's Response:

I'll explore the codebase systematically to produce a fresh, actionable end-to-end review. There's a prior review from May 16 — I'll verify what's still relevant and find new issues.

MainViewModel has grown to 3355 lines and MainWindow.xaml.cs to 3010 — both nearly tripled since the prior review. Let me dig into the actual code to find concrete issues.

I have enough material. Writing the report now.

Report saved to [QuickMail-Review-2026-05-21.md](QuickMail-Review-2026-05-21.md).

## Headline findings

**One confirmed correctness bug worth interrupting work for:**
- **§1.1 — User-customised hotkeys are silently inert.** `CommandRegistry.FindByGesture` ([CommandRegistry.cs:33-37](C:/Users/kelly/GitHub/QuickMail/QuickMail/Services/CommandRegistry.cs)) reads `binding.Key`/`binding.Modifiers` (the legacy integer fields, always `0` for new bindings), while `SettingsViewModel.ToBinding()` and the saved-views path only populate `Gesture`. The keyboard-customisations dialog appears to work, the file saves correctly, but on next launch the user's chosen keys do nothing. ~30 lines to fix.

**Structural concern that's getting worse, not better:**
- `MainViewModel`: 1,200 → 3,355 lines since the May 16 review.
- `MainWindow.xaml.cs`: ~800 → 3,010 lines.
- Both have at least three repeated patterns (5–12 occurrences each) that mechanically extract into one method without behaviour change. Detailed in §2.1 and §2.2 — these are excellent first PRs for junior devs.

**Other P0 items (correctness):** no global unhandled-exception handler (§1.2); `LogService.AppendAllText` is not thread-safe and silently loses log lines (§1.3); `BatchObservableCollection` can deadlock its internal state on exception or re-entry (§1.5); `ContactService` mutates the cache without a lock (§1.6); `LoadAllSummariesAsync` doesn't populate `HasAttachments` (§1.10).

**Recommended PR ordering for the team** at the end of the report — ten discrete tickets sized for a junior dev, plus three strategic decompositions for the next milestone.
# QuickMail — Engineering Review

**Date:** 2026-05-21
**Branch / version:** main, ~v0.5.9
**Prior review:** `CODE_REVIEW.md` (2026-05-16). This document supersedes it; items that were closed (e.g. PR #10 extractions, `MailMessageDetail.To` shadowing fix) are not repeated. Where a prior finding is still open, it is restated here with current line references.

---

## Summary for the team

The codebase is in a strong place: MVVM discipline is mostly clean, accessibility is genuinely excellent for v0.5, the IMAP pool design is mature, and the SQLite cache + background-sync pattern works well. The headline concerns are no longer "missing features" — they are **growth pains**:

- `MainViewModel` is now **3,355 lines** (up from ~1,200 in May 16). `MainWindow.xaml.cs` is **3,010 lines** (up from ~800). Both have become catch-alls and need to be broken apart before anything else in those files.
- There is **one confirmed correctness bug that breaks user-customised keyboard shortcuts** (see #1 below). Customisations save fine, but never take effect on key press.
- **No global unhandled-exception handler.** Any unhandled async exception after `OnStartup` crashes the app silently from the user's perspective.
- **Test coverage is still thin** on the things that matter most: `ImapService`, `SyncService`, `CommandRegistry`, pure builders (`ConversationBuilder`, `SenderGroupBuilder`), and `ConfigService` parsing.

The findings below are sized so that one developer can pick up one item and ship a focused PR. Section §6 lists strategic refactors that are bigger but unblock further work.

I recommend the team work through §1 (P0 correctness) first, in order, then §2 (P1 quality) in any order, then plan §6 (strategic) for the next milestone.

---

## §1. Correctness bugs (P0 — fix first)

### 1.1 User-customised hotkeys are silently inert

**Severity:** High. Confirmed bug — the feature documented in `CLAUDE.md` lines 197–214 does not work for any user override saved through the Settings dialog.

**Where:** [QuickMail/Services/CommandRegistry.cs:33-37](QuickMail/Services/CommandRegistry.cs)

```csharp
foreach (var binding in _userOverrides)
{
    if ((Key)binding.Key == key && (ModifierKeys)binding.Modifiers == modifiers)
        return FindById(binding.CommandId);
}
```

`binding.Key` and `binding.Modifiers` are the **legacy integer fields**. Per [QuickMail/Models/HotkeyBinding.cs:18-24](QuickMail/Models/HotkeyBinding.cs), those are kept "read-only for migration" and `[JsonIgnore(WhenWritingDefault)]` — so any newly-written binding has them at `0`. The authoritative field is `Gesture` (string, e.g. `"Ctrl+Shift+K"`).

The settings UI writes only `Gesture` ([SettingsViewModel.cs:157-161](QuickMail/ViewModels/SettingsViewModel.cs)) and so does saved-view hotkey saving ([ViewManagerViewModel.cs:522-526](QuickMail/ViewModels/ViewManagerViewModel.cs)). Result: `FindByGesture` compares `0 == Key.K` and falls through.

**Fix (one line):** parse `binding.Gesture` once in `ApplyUserOverrides` and store the parsed `Key`/`ModifierKeys` in a small private DTO that the lookup loop consults:

```csharp
private readonly List<(string CommandId, Key Key, ModifierKeys Mods)> _userOverrides = new();

public void ApplyUserOverrides(IEnumerable<HotkeyBinding> overrides)
{
    _userOverrides.Clear();
    foreach (var b in overrides)
    {
        if (string.IsNullOrEmpty(b.Gesture)) continue;
        if (!GestureHelper.TryParse(b.Gesture, out var key, out var mods)) continue;
        _userOverrides.Add((b.CommandId, key, mods));
    }
}
```

**Tests to add:** `CommandRegistryTests.UserOverride_FromGestureString_TakesPrecedence()` and a regression test using the same JSON shape `SettingsViewModel.ToBinding()` produces. Both belong in a new `CommandRegistryTests.cs` file — there are currently **zero** tests for `CommandRegistry`.

### 1.2 No global unhandled-exception handler

**Where:** [QuickMail/App.xaml.cs:10-58](QuickMail/App.xaml.cs)

The `try/catch` in `OnStartup` only covers initial wiring. Once `mainWindow.Show()` returns, any `DispatcherUnhandledException`, `TaskScheduler.UnobservedTaskException`, or `AppDomain.UnhandledException` will crash the process. None of the many `_ = Task.Run(...)` and fire-and-forget patterns in `MainViewModel` and `SyncService` are guaranteed to log on failure.

**Fix:** add three handlers at the top of `OnStartup`, each one writing to `LogService.Log` before optionally setting `Handled = true` for `DispatcherUnhandledException` so the user gets a graceful message box instead of a crash.

### 1.3 `LogService.AppendAllText` is not thread-safe

**Where:** [QuickMail/Services/LogService.cs:19-26](QuickMail/Services/LogService.cs)

`File.AppendAllText` opens, writes, closes. Concurrent calls from background sync, prefetch, and the UI thread will collide. The outer `try/catch` swallows the resulting `IOException` and **silently loses log lines** — exactly the lines you want most during a sync crash.

**Fix:** a single `static readonly object _gate = new();` guarding the write block, or a `Channel<string>` consumed by one background writer task. Either is a 5-line change.

### 1.4 `GetMessagesSinceAsync` ignores the date window on backfill of an old folder

**Where:** [QuickMail/Services/ImapService.cs:252-258](QuickMail/Services/ImapService.cs)

When `sinceUid == 0` and `initialCount > 0`, it fetches the most recent `initialCount` messages from the folder regardless of how far back they go. `SyncService.SyncFolderAsync` ([SyncService.cs:95-104](QuickMail/Services/SyncService.cs)) handles the `_syncDays > 0` branch correctly by calling `GetMessagesSinceDateAsync` instead — but [MainViewModel.FetchAccountNewMessagesAsync](QuickMail/ViewModels/MainViewModel.cs:2060-2094) duplicates this same conditional, and [MainViewModel.FetchViewFoldersAsync](QuickMail/ViewModels/MainViewModel.cs:651-661) too. The logic is correct in three places and easy to get wrong in the fourth. Extract it into one method on the service (`ImapService.GetFolderSyncAsync(accountId, folder, lastUid, sinceDate?, initialCount, ct)`) and delete the three call sites' branching.

### 1.5 `BatchObservableCollection` is fragile under exceptions and re-entry

**Where:** [QuickMail/Helpers/BatchObservableCollection.cs:29-47](QuickMail/Helpers/BatchObservableCollection.cs)

Two issues:

1. If a mutation inside the batch throws, `_batchActive` stays `true` forever. Every subsequent mutation is swallowed and the UI silently desyncs.
2. Nested `BeginBatch`/`EndBatch` calls clobber `_pendingReset`.

**Fix:** add `Reset()` invariant in `EndBatch`'s `finally`, support a depth counter for re-entry, and document that callers should use `try/finally`. Provide a `using var batch = collection.BeginBatchScope();` helper that disposes safely.

```csharp
public IDisposable BeginBatchScope() { BeginBatch(); return new Releaser(this); }
private sealed class Releaser(BatchObservableCollection<T> c) : IDisposable { public void Dispose() => c.EndBatch(); }
```

Then change [MainViewModel.cs:1023-1026](QuickMail/ViewModels/MainViewModel.cs) (`Messages.BeginBatch(); … Messages.EndBatch();`) to `using (Messages.BeginBatchScope()) { … }`.

### 1.6 `ContactService` mutates the cache without a lock

**Where:** [QuickMail/Services/ContactService.cs:27-73](QuickMail/Services/ContactService.cs)

`_loadLock` only protects `EnsureLoadedAsync`. `UpsertContactAsync` and `DeleteContactAsync` then read and write `_cache` without any synchronisation. Two concurrent upserts (e.g. user sends two messages in quick succession; `MainViewModel.SendAsync` and contact-autocomplete writes overlap) can produce duplicate IDs from `_cache.Max(c => c.Id) + 1` and race the JSON write.

**Fix:** hold `_loadLock` for the entire body of `UpsertContactAsync`, `DeleteContactAsync`, and the read parts of `SearchContactsAsync`/`LoadAllContactsAsync` (or switch to a single `AsyncLock` that gates both load and writes).

### 1.7 `IsSelectedFolderDrafts` uses a substring heuristic

**Where:** [QuickMail/ViewModels/MainViewModel.cs:2531-2534](QuickMail/ViewModels/MainViewModel.cs)

```csharp
public bool IsSelectedFolderDrafts =>
    SelectedFolder != null &&
    (SelectedFolder.DisplayName.Contains("draft", StringComparison.OrdinalIgnoreCase) ||
     SelectedFolder.FullName.Contains("draft", StringComparison.OrdinalIgnoreCase));
```

A user folder called "Draft ideas" or "old drafts" matches; a localised Drafts folder ("Brouillons", "Borradores") does not. `MailFolderModel.Kind` is already set to `SpecialFolderKind.Drafts` by `ImapService` and is the source of truth. Replace with `SelectedFolder?.Kind == SpecialFolderKind.Drafts || SelectedFolder?.FullName == AllDraftsFolder.FullName`.

### 1.8 `ConversationBuilder` / `SenderGroupBuilder` reads are O(n) under sync churn

**Where:** [MainViewModel.cs:1444-1542](QuickMail/ViewModels/MainViewModel.cs) — every group rebuild snapshots `Messages.ToList()`, runs LINQ on a background task, then replaces the entire `ObservableCollection`. During an active sync this can fire many times per second across All Mail with tens of thousands of messages, each rebuild copying the full list.

The version-stamp guard prevents stale results from winning, but does not throttle. Add a debounce (250 ms) so a burst of `FolderSynced` events triggers exactly one rebuild after the burst settles. The plumbing already exists — see `QueueStatusAnnounce` in `MainWindow.xaml.cs:296-316` for the pattern.

### 1.9 Sequential preview persistence in `SyncService`

**Where:** [SyncService.cs:165-171](QuickMail/Services/SyncService.cs)

```csharp
foreach (var s in incoming)
{
    if (!previews.TryGetValue(s.UniqueId, out var p)) continue;
    await Application.Current.Dispatcher.InvokeAsync(() => s.Preview = p);
    await _store.UpdatePreviewAsync(s.AccountId, s.FolderName, s.UniqueId, p);
}
```

For a folder of 100 messages this is 100 dispatcher hops and 100 SQLite round-trips. Batch:

1. Apply the `s.Preview` assignments inside a single `Dispatcher.InvokeAsync` block.
2. Add `ILocalStoreService.UpdatePreviewsBatchAsync(IEnumerable<(Guid, string, uint, string)>)` and run as one transaction.

### 1.10 `LocalStoreService.LoadAllSummariesAsync` does not populate `HasAttachments`

**Where:** [LocalStoreService.cs:148-150 / 169-171 / ReadSummariesAsync:379-401](QuickMail/Services/LocalStoreService.cs)

The `has_attachments` column exists (added by migration line 67) but `ReadSummariesAsync` does not select it. On a cold start, the attachment-indicator column in the message list is blank until the user opens each message individually. Add the column to all three `SELECT` statements and one line in `ReadSummariesAsync`.

### 1.11 `GetFoldersAsync` does N OPEN/CLOSE round-trips just to read counts

**Where:** [ImapService.cs:128-165](QuickMail/Services/ImapService.cs)

For a Gmail account with 100+ labels this is 100+ IMAP `SELECT`/`CLOSE` pairs per cold connect. Use `folder.StatusAsync(StatusItems.Unread | StatusItems.Count, ct)` which does not require opening the folder. Bonus: it works on folders the user can't `SELECT`.

---

## §2. Quality issues (P1)

### 2.1 `MainViewModel`: extract repeated patterns

The class has at least **three** patterns that appear five or more times each. These are mechanical extractions and a good first PR for a junior dev who wants to learn the codebase.

**Pattern A — "rebuild current view mode" (12 occurrences).** Every action that mutates `Messages` does:

```csharp
if (ViewMode == ViewMode.Conversations) ScheduleConversationRebuild();
else if (ViewMode == ViewMode.From)     ScheduleSenderGroupRebuild();
else if (ViewMode == ViewMode.To)       ScheduleToGroupRebuild();
```

Replace with one method:

```csharp
private void RebuildActiveGroupView()
{
    switch (ViewMode)
    {
        case ViewMode.Conversations: ScheduleConversationRebuild(); break;
        case ViewMode.From:          ScheduleSenderGroupRebuild();  break;
        case ViewMode.To:            ScheduleToGroupRebuild();      break;
    }
}
```

Then grep for the pattern and replace all callers. ~30 lines deleted.

**Pattern B — "phase 1 cache, phase 2 IMAP" (4 near-identical methods).** [FetchAllMailAsync](QuickMail/ViewModels/MainViewModel.cs:1901-2027), [FetchAccountAllMailAsync](QuickMail/ViewModels/MainViewModel.cs:2121-2205), [FetchViewFoldersAsync](QuickMail/ViewModels/MainViewModel.cs:610-719), and (to a lesser extent) [FetchVirtualFolderAsync](QuickMail/ViewModels/MainViewModel.cs:2207-2257) all do: increment version stamp; `Messages.Clear()`; cancel previous CTS; phase 1 cache load; phase 2 IMAP per-folder loop; merge by key set; rebuild groups. Extract into one method with a delegate for "where the messages come from":

```csharp
private async Task FetchAndMergeAsync(
    MailFolderModel sentinel,
    string statusLabel,
    Func<CancellationToken, Task<IReadOnlyList<MailMessageSummary>>> loadCache,
    Func<CancellationToken, Task<IReadOnlyList<MailMessageSummary>>> loadFromImap)
{
    // ... shared version-stamp, busy, try/finally, status text, group rebuild
}
```

**Pattern C — context menu commands (12 near-identical group-action methods).** [Reply/ReplyAll/Forward/Delete × Conversation/SenderGroup/ToGroup](QuickMail/ViewModels/MainViewModel.cs:2962-3120) are the same four-line body 12 times. Either make the commands accept a `IReadOnlyList<MailMessageSummary>` directly (no group-specific overload), or generate them by `[RelayCommand]` on a base type and reuse.

### 2.2 `MainWindow.xaml.cs`: extract the three tree-view triplets

There are three tree views (`ConversationTree`, `SenderGroupTree`, `ToGroupTree`) and each has its own copy of:

`*_GotKeyboardFocus`, `*_SelectedItemChanged`, `*_PreviewKeyDown`, `*_PreviewTextInput`, `*_PreviewMouseRightButtonDown`, `*_ContextMenuOpening`, `*_MoveToFolder_Click`, `*_CopyToFolder_Click`, plus `LandOn{X}AfterRebuild` and `Focus{X}Message`.

That's ~30 methods that differ only in which collection they index into. Extract a `GroupedMessageTreeController<TGroup>` class in `Views/` that takes the tree, the source collection accessor, and the move/copy actions. The current focus-management logic is genuinely complex — wrapping it once is worth more than three copies.

Pair this with [TreeViewFocusHelper.cs](QuickMail/Views/TreeViewFocusHelper.cs) (already merged in PR #10) so they live next to each other.

### 2.3 Missing CTS disposal

Throughout [MainViewModel](QuickMail/ViewModels/MainViewModel.cs) — search for `new CancellationTokenSource()` and you'll find ~15 cases like:

```csharp
_folderCts?.Cancel();
_folderCts = new CancellationTokenSource();
```

The previous CTS is never disposed. `CancellationTokenSource` holds a waitable handle (`CancellationTokenSource._kernelEvent`); leaking it isn't catastrophic but is a true resource leak. The fix is a small helper:

```csharp
private static void ReplaceCts(ref CancellationTokenSource? slot, out CancellationToken token)
{
    var fresh = new CancellationTokenSource();
    var old = Interlocked.Exchange(ref slot, fresh);
    old?.Cancel();
    old?.Dispose();
    token = fresh.Token;
}
```

Some sites (`SelectFolderAsync`, `SelectMessageAsync`) already use `Interlocked.Exchange` and `await previousCts?.CancelAsync()`, but still don't `Dispose`. Add the dispose.

### 2.4 `ConfigService` stores `ViewMode` / `Sort` as magic strings

Both [ConfigModel.ViewMode](QuickMail/Models/ConfigModel.cs) and `ConfigModel.Sort` are `string` and are converted by `switch` in at least six places (`MainViewModel.ApplyViewAsync`, `OnViewModeChanged`, `OnActiveSortChanged`, `SetViewMode`, `SetSort`, constructor). Either make them `ViewMode` and `MessageSort` enums (serialised by JsonStringEnumConverter — but config is INI, not JSON, so write a small helper) **or** centralise the string ↔ enum mapping in one static helper. Six switch statements that must stay in sync is a guaranteed bug-source.

### 2.5 `LocalStoreService.Initialize` runs the backfill on every startup

**Where:** [LocalStoreService.cs:70-82](QuickMail/Services/LocalStoreService.cs)

The `UPDATE … WHERE to_addr = ''` runs unconditionally. On a 100k-message DB it scans the whole table to find rows that no longer exist. Gate behind `PRAGMA user_version`: bump to `1` after the first successful backfill, skip if already at `≥ 1`.

This is the right time to introduce the `user_version` migration system the May 16 review recommended — the try/catch ALTER TABLE pattern is already at six migrations and growing.

### 2.6 `MatchesSearch` ignores the To field in To-mode

**Where:** [MainViewModel.cs:1115-1121](QuickMail/ViewModels/MainViewModel.cs)

Search is documented as "filter messages by from/subject/preview" but the app has a To view mode. Add `|| msg.To.Contains(q, StringComparison.OrdinalIgnoreCase)` so that searching in To view filters by recipient.

### 2.7 `ComposeViewModel` violates the MVVM rule

**Where:** [ComposeViewModel.cs:271-291](QuickMail/ViewModels/ComposeViewModel.cs)

```csharp
var result = System.Windows.MessageBox.Show(
    $"'{attachment.FileName}' is an executable file type. Opening it could be dangerous. Continue?",
    "Security Warning",
    System.Windows.MessageBoxButton.YesNo,
    System.Windows.MessageBoxImage.Warning);
```

This is the exact pattern that `CLAUDE.md:161-167` forbids. The codebase already has the right pattern: `MainViewModel.ConfirmationRequested` ([MainViewModel.cs:2678](QuickMail/ViewModels/MainViewModel.cs)). Add a `Func<string, string, bool>? ConfirmationRequested` property to `ComposeViewModel`, have `ComposeWindow.xaml.cs` wire it to `MessageBox.Show`, and use it here.

### 2.8 `ComposeViewModel.AddAttachmentFromPath` blocks the UI thread

**Where:** [ComposeViewModel.cs:235](QuickMail/ViewModels/ComposeViewModel.cs)

`File.ReadAllBytes(path)` for a 24 MB attachment will freeze the window for noticeable time on slow disks. Make the method `async Task` and use `File.ReadAllBytesAsync`. The callers (`AddAttachments`, clipboard paste in ComposeWindow code-behind) already run from async contexts.

### 2.9 `CreateReplyAll` can put the sender in both `To` and `Cc`

**Where:** [ComposeViewModel.cs:331-349](QuickMail/ViewModels/ComposeViewModel.cs)

`CreateReply` sets `To = detail.ReplyTo ?? detail.From`. `CreateReplyAll` then merges `detail.To + detail.Cc` (minus `ownAddress`) into `Cc`, but does **not** exclude the original `From` (or `ReplyTo`) from `Cc`. If the sender was also Cc'd on their own message — common with mailing lists — they end up on both lines of the reply. Exclude the address that landed in `To` from the new `Cc`.

### 2.10 Filename collision in `OpenComposeAttachment`

**Where:** [ComposeViewModel.cs:286-290](QuickMail/ViewModels/ComposeViewModel.cs)

Two attachments with the same name (`invoice.pdf`) overwrite each other in `%TEMP%\QuickMail`. The reading-pane version in [MainViewModel.cs:3308-3312](QuickMail/ViewModels/MainViewModel.cs) has the same issue. Use a per-attachment temp subfolder (`%TEMP%\QuickMail\{Guid}\{safeFileName}`) or include a hash in the temp filename.

### 2.11 `LocalStoreService.DeleteSummariesAsync` is N round-trips in a loop

**Where:** [LocalStoreService.cs:189-206](QuickMail/Services/LocalStoreService.cs)

```csharp
foreach (var uid in uniqueIds)
{
    pUid.Value = (long)uid;
    await cmd.ExecuteNonQueryAsync();
}
```

Two `DELETE` statements per UID. For a 1000-message "Empty Trash" this is 2000 round-trips. Use `DELETE … WHERE unique_id IN (…)` — SQLite parameterised IN list is fine up to several hundred, and you can chunk above that.

---

## §3. Security & robustness

### 3.1 No certificate pinning, but `ImapAcceptInvalidCert` is an account setting

Acceptable trade-off for a desktop client, but the setting should never be on by default and the UI should display a persistent banner when it's enabled. Today the user can flip it and forget.

### 3.2 Temp attachments persist forever

`%TEMP%\QuickMail` accumulates every attachment the user opens. The May 16 review flagged this and it has not changed. Either clean on app startup (delete files > 24h old) or delete each file when the spawned `Process` exits (subscribe to `Exited` event with `EnableRaisingEvents = true`).

### 3.3 OAuth client ID is hardcoded

Still embedded at [OAuthService.cs:14](QuickMail/Services/OAuthService.cs). Standard for public native apps but means enterprise users cannot point QuickMail at their own Azure AD registration. Make it configurable via `config.ini` with the current value as the default.

### 3.4 `RegisterTokenCache` does `.GetAwaiter().GetResult()` in the constructor

**Where:** [OAuthService.cs:52](QuickMail/Services/OAuthService.cs)

Still present from the May 16 review. Convert to an async factory:

```csharp
public static async Task<OAuthService> CreateAsync() { ... }
```

Update `App.xaml.cs` to await it.

---

## §4. Test coverage gaps

Tests that should exist and don't:

| Area | Why |
|---|---|
| `CommandRegistryTests` | Would have caught §1.1. Pure logic, trivial to test. |
| `ConversationBuilderTests` | Pure function, no dependencies. The subject-normalisation logic (strips all leading `Re:`/`Fwd:` chains) is non-obvious. |
| `SenderGroupBuilderTests` | Same; including `BuildByTo`. |
| `ConfigServiceTests` | INI parser is hand-rolled; per-account override merging is non-trivial. |
| `SyncServiceTests` | UID-deletion-detection logic (compare local UID set to server UID set) is exactly the kind of code that breaks on edge cases (empty folder, empty local cache, server returning UIDs out of order). |
| `LocalStoreServiceTests` | Only one test exists. Add: upsert preview preservation (the `CASE WHEN excluded.preview_text = ''` branch), `DeleteSummariesAsync`, `LoadDetailAsync` with and without attachment JSON, migration idempotency. |
| `BatchObservableCollectionTests` | Reset semantics, exception during batch, nested batch. |

The existing tests are good — `MessageFilterTests` is a model for how to test VM logic against a stub store. Imitate that pattern.

### Stub fidelity gap

`StubCommandRegistry.FindByGesture` ([StubServices.cs:127-128](QuickMail.Tests/StubServices.cs)) scans by `DefaultKey/DefaultModifiers` only — it doesn't even attempt to honour overrides. That is why a real-registry test wouldn't have caught §1.1. When you add `CommandRegistryTests`, test against the **real** `CommandRegistry`, not the stub.

---

## §5. Build & deploy

No CI/CD pipeline exists. `build.bat` includes a smoke test but it has to be run manually. A GitHub Actions workflow that runs `dotnet test` on every PR is half a day of work and would have caught every test regression added since v0.5.1.

No code signing. SmartScreen will flag every release. Out of scope for engineering but worth raising to the team owner.

---

## §6. Strategic refactors (next milestone)

These are too big for a single PR but should be on the roadmap before the next major feature lands. Each one unblocks several P1 items above.

### 6.1 Decompose `MainViewModel`

Target: bring it under 1,000 lines. Split into:

- **`FolderNavigationViewModel`** — `Folders`, `FolderTree`, `SelectedFolder`, `BuildFolderTree`, virtual-folder sentinel handling, `RebuildFolderListFromCache`, `RefreshFolderListAsync`.
- **`MessageListViewModel`** — `Messages`, `_rawMessages`, `ActiveFilter`, `ActiveSort`, `IsSearchActive`, `SearchText`, `SetMessages`, `ApplyFiltersAndSearch`, `InsertMessageSorted`, `OnFolderSynced`, `OnMessagesRemoved`.
- **`ReadingPaneViewModel`** — `SelectedMessage`, `MessageDetail`, `IsMessageOpen`, `SelectMessageAsync`, prefetch logic, attachment commands.
- **`GroupedViewsViewModel`** — `Conversations`, `SenderGroups`, `ToGroups`, the three `Schedule*Rebuild` methods, group context-menu commands.
- **`SavedViewsViewModel`** — `SavedViews`, `ActiveView`, `ApplyViewAsync`, `RegisterViewCommands`.

`MainViewModel` becomes the orchestrator that wires these together and exposes them to the View via composition. Each child is independently testable.

### 6.2 Decompose `MainWindow.xaml.cs`

After §2.2 (`GroupedMessageTreeController`), the remaining ~2,000 lines fall into three groups:

- WebView2 reading-pane host (HTML sanitisation, navigation, focus). Extract to a `MessageBodyController` class that takes the `WebView2` and exposes `ShowAsync(detail)` and `Close()`.
- Window-level keyboard routing (`OnWindowKeyDown`, `CycleFocusAsync`, pane focus helpers). Stays in the window.
- Menu/toolbar click handlers and dialog opens. Stays.

### 6.3 Replace virtual-folder sentinel strings with a type

The ` AllMail` / ` View:{guid}` sentinel scheme works but is implicit. Already noted in the May 16 review. After §6.1, `FolderNavigationViewModel` is the right home for a `VirtualFolderKind` enum (`AllMail`, `AllInboxes`, `AllDrafts`, `AllSent`, `AllTrash`, `PerAccountAll`, `SavedView`, `SavedViewAll`) with the GUID where applicable. Replace every `TryGet*FromSentinel` and `IsVirtualFolder` with type-safe pattern matching. Backwards-compatible because the sentinel format only escapes into `SavedView.VirtualFolderKey` — translate at the boundary.

### 6.4 Database migration framework

Introduce a real migration runner using `PRAGMA user_version`. Each migration is a numbered class with `Apply(SqliteConnection)`. The current try/catch ALTER TABLE pattern works for column adds but cannot handle data migrations, dropped columns, or anything that requires a transaction.

---

## §7. Things that are good — keep doing them

Worth calling out so the team doesn't accidentally regress these:

- `ImapClientLease` / `AccountConnectionPool` design ([ImapService.cs:822-1023](QuickMail/Services/ImapService.cs)). Foreground/background slot separation is exactly right.
- Version-stamp + cancellation pattern for stale results (`_folderLoadVersion`, `_messageLoadVersion`). Prevents almost an entire class of bugs.
- `AccessibilityHelper.Announce` discipline with categories (Hint/Status/Result). Few teams get this right.
- `MimeMessageBuilder` and `AddressParser` extractions from PR #10. Both used in two places now; that's the right call.
- The modal-dialog rules in `CLAUDE.md` (Never fire `ViewsChanged` mid-`ShowDialog`). The fact that these are written down at all is unusual and valuable.
- INI config with explanatory comments — much friendlier for end-users than JSON.

---

## Suggested PR ordering for junior devs

If you're handing this out as discrete tickets, this is the order I'd recommend:

1. **§1.1** — fix hotkey override bug. ~30 lines plus tests. Highest-impact, lowest-risk.
2. **§1.2** — add unhandled exception handlers. Mechanical.
3. **§1.3** — make `LogService` thread-safe. Mechanical.
4. **§1.10** — `HasAttachments` column read. One-line bug fix plus regression test.
5. **§1.7** — `IsSelectedFolderDrafts` use Kind. One-line fix.
6. **§2.7** — `ComposeViewModel` MVVM violation. Good intro to the codebase's event-callback pattern.
7. **§4** — write `CommandRegistryTests`, `ConversationBuilderTests`, `SenderGroupBuilderTests`. Parallelisable across multiple devs.
8. **§2.1 Pattern A** — extract `RebuildActiveGroupView()`. Touches many files but each change is identical.
9. **§1.5** — `BatchObservableCollection` safety. Small but requires understanding `IDisposable` patterns.
10. **§1.8 / §1.9 / §2.11** — performance work on hot paths. Best given to a developer who has shipped at least one of items 1–9 above.

Strategic items §6 should be planned as deliberate work for one senior dev, not handed out piecemeal.

---

## §8. Resolution log (2026-05-21)

Sixteen commits landed against this review on the same day it was written, walking the suggested PR ordering and then continuing into perf, quality, and security/robustness items. Tests went from 106 passing at baseline to 150 passing (44 new tests).

The intent of this section is to make this review document the durable home for the work — not just a list of commits, but the *commentary* on why each fix was made the way it was, what was deliberately deferred, and how to use this review process on future iterations.

### Commits, in order

| Review item | Commit | Description |
|---|---|---|
| §1.1 — Hotkey overrides silently inert | [3f5db0d](https://github.com/kellylford/QuickMail/commit/3f5db0d) | Parse `Gesture` string in `ApplyUserOverrides`; suppress default gesture when remapped; migrate legacy ints. Added `CommandRegistryTests.cs` (8 cases). |
| §1.2 — No unhandled-exception handler | [99263b2](https://github.com/kellylford/QuickMail/commit/99263b2) | Added `DispatcherUnhandledException` + `UnobservedTaskException` + `AppDomain.UnhandledException` handlers in `App.xaml.cs` — installed *before* startup wiring. |
| §1.3 — `LogService` not thread-safe | [b87c07c](https://github.com/kellylford/QuickMail/commit/b87c07c) | Static lock around `File.AppendAllText`. |
| §1.10 — `HasAttachments` not read | [02e8e70](https://github.com/kellylford/QuickMail/commit/02e8e70) | Added `has_attachments` to all three SELECTs in `LocalStoreService.cs`; regression tests. |
| §1.7 — `IsSelectedFolderDrafts` substring | [4594223](https://github.com/kellylford/QuickMail/commit/4594223) | Now uses `SpecialFolderKind.Drafts`. |
| §1.6 — `ContactService` cache race | [13873c5](https://github.com/kellylford/QuickMail/commit/13873c5) | `_loadLock` held for all mutations and reads; `SaveAsync` renamed to `SaveAsyncLocked` with precondition documented. |
| §1.5 — `BatchObservableCollection` fragile | [ee2d9d3](https://github.com/kellylford/QuickMail/commit/ee2d9d3) | Depth counter, `BeginBatchScope()` `IDisposable`, MainViewModel switched to `using`. 5 new tests. |
| §2.7 + §2.8 + §2.10 — Compose hardening | [0eaf9a6](https://github.com/kellylford/QuickMail/commit/0eaf9a6) | Removed `MessageBox` from VM (added `ConfirmationRequested` event); `AddAttachmentFromPathAsync`; per-attachment Guid temp subfolder. |
| §2.9 — Reply-all duplicates sender | [733e51e](https://github.com/kellylford/QuickMail/commit/733e51e) | Exclude To-addresses (incl. ReplyTo) from new Cc; fix ineffective `Distinct()` call (MailboxAddress uses reference equality). 5 new tests. |
| §2.6 — To-field search | [52a2178](https://github.com/kellylford/QuickMail/commit/52a2178) | One-line addition to `MatchesSearch`. |
| §2.1 — `RebuildActiveGroupView` helper | [c0eeccf](https://github.com/kellylford/QuickMail/commit/c0eeccf) | Replaced 10 open-coded 3-branch switches; fixed latent bug in move-to-folder where To branch was missing entirely. -29 net lines. |
| §4 — Builder tests | [4d06ca4](https://github.com/kellylford/QuickMail/commit/4d06ca4) | `ConversationBuilderTests.cs` + `SenderGroupBuilderTests.cs`, 21 new tests. |
| §2.11 — Per-UID Delete loop | [cafb7c1](https://github.com/kellylford/QuickMail/commit/cafb7c1) | Chunked `WHERE unique_id IN (…)`; 1100-UID regression test crosses chunk boundary. |
| §1.9 — Per-message preview persistence | [e5cc35c](https://github.com/kellylford/QuickMail/commit/e5cc35c) | Added `UpdatePreviewsBatchAsync` (single transaction); single `Dispatcher.InvokeAsync` for the whole batch. |
| §2.5 — Backfill on every startup | [3858fdc](https://github.com/kellylford/QuickMail/commit/3858fdc) | `PRAGMA user_version` migration framework gates data migrations. |
| §3.2 — Temp attachments leak forever | [c1d8b81](https://github.com/kellylford/QuickMail/commit/c1d8b81) | Background sweep on startup deletes `%TEMP%\QuickMail` entries older than 24h. Pairs with §2.10 (per-attachment subfolder) to give bounded per-attachment scope. |

### Commentary on individual fixes

A few of the fixes deserve more explanation than fits in the commit message — either because the scope drifted slightly from what the review suggested, or because there was a judgement call worth recording.

**§1.1 — Hotkey override default-suppression.** The review prescribed parsing the `Gesture` string in `ApplyUserOverrides`. I went one step further: when a user remaps a command (e.g. Reply from `Ctrl+R` to `Alt+R`), the *old* default gesture is now suppressed in the lookup so `Ctrl+R` does nothing. Without this, both old and new gestures would fire Reply, which contradicts the Settings UI calling these "customisations" rather than "additions". This was a behaviour change beyond the minimum bug fix, and worth flagging if a user reports that an old hotkey "stopped working" after this commit.

**§1.2 — DispatcherUnhandledException + MessageBox.** The review suggested optionally calling `MessageBox.Show` in the handler. I implemented it that way, with a defensive try/catch around the `MessageBox` call itself in case the dispatcher is in such a bad state that even showing a dialog faults. `Handled = true` keeps the window alive — the alternative is letting the process die after every transient bug, which is worse for users than a logged error and a "try again."

**§1.5 — BatchObservableCollection depth counter + scope.** The review proposed a `BeginBatchScope` returning `IDisposable`. I extended it to a depth counter (rather than a bool) so nested batches are supported as a side-effect — the inner scope no longer clobbers the outer's `_pendingReset`. Cost is zero (one integer instead of one bool), benefit is that callers don't have to worry about whether they might be nested.

**§2.1 — RebuildActiveGroupView fixed a latent bug.** While replacing the ten 3-branch switches, I found one site (the move-to-folder path) that had only Conversations and From — the To branch was missing entirely. Moving messages did not refresh the To view. The helper now covers all three views and the inconsistency is gone. Worth knowing if anyone reports "To view feels more responsive after moves" — it's a real behavioural change, not a regression.

Two single-branch sites (after cache load, after server refresh) only rebuild Conversations and I deliberately left them alone. They might be intentional (waiting for full sync before rebuilding the heavier From/To groupings) and I didn't want to fold a behavioural change into a refactor PR. If From/To users notice stale groupings after refresh, those two sites are the place to look.

**§2.9 — `.Distinct()` was a no-op.** While fixing reply-all, I noticed the existing `.Distinct()` call on `IEnumerable<MailboxAddress>` was effectively a no-op: `MailboxAddress` doesn't override `Equals`/`GetHashCode`, so reference-equality Distinct never deduplicated different parses of the same address. Replaced with `GroupBy(a => a.Address, OrdinalIgnoreCase).First()`. Picked up by the new "different display names" test.

**§2.10 + §3.2 are paired.** §2.10 puts each opened attachment in its own Guid subfolder under `%TEMP%\QuickMail`. That alone gives correctness (no overwrites) but worsens disk leakage (more dirs to clean up). §3.2 adds the 24h sweep on startup, so the two together give bounded per-attachment scope. They were committed separately for review clarity but should be thought of as one fix.

**§2.5 — Migration framework, lightweight version.** The review suggested introducing "the `user_version` migration system the May 16 review recommended." I added the minimum viable framework: a single `RunDataMigrations` method with a `CurrentSchemaVersion` constant and per-version `if (version < N)` blocks. The schema (`ALTER TABLE`) migrations still use the existing try/catch pattern because column-adds are idempotent on their own; only the *data* migration (the backfill) needs version-gating. When the next data migration arrives, the pattern is in place — but I didn't force-fit existing schema migrations through it because the change-set should be minimal.

### Tests added (44 total)

- `CommandRegistryTests.cs` — 8 cases (default match, no match, gesture-string override, default suppression, legacy integer migration, Key.None, malformed gesture, idempotent re-apply)
- `BatchObservableCollectionTests.cs` — 5 cases (single Reset, exception cleanup, nesting, empty batch, EndBatch without Begin)
- `ComposeViewModelReplyTests.cs` — 5 cases (mailing-list duplicate, ReplyTo exclusion, own-address exclusion, empty fields, dedup-by-email)
- `ConversationBuilderTests.cs` — 11 cases (prefix-chain stripping × 10 inputs, embedded-Re false positive, grouping case-insensitive, newest-first order, empty input, all-blank subjects)
- `SenderGroupBuilderTests.cs` — 6 cases (sender grouping, whitespace trim, within-group order, empty input, BuildByTo grouping, BuildByTo placeholder)
- `LocalStoreServiceTests` additions — 4 cases (HasAttachments persist+load, HasAttachments default false, DeleteSummariesAsync 1100-UID across chunks, DeleteSummariesAsync empty input, Initialize idempotency)

### What was deliberately left

The review listed several items I did not address in this pass. Each was a judgement call:

- **§1.4 — Extract `GetFolderSyncAsync` from three call sites.** The logic exists correctly in three places. Extracting it requires careful cross-checking of date-window vs count-based semantics between `MainViewModel.FetchAccountNewMessagesAsync`, `MainViewModel.FetchViewFoldersAsync`, and `SyncService.SyncFolderAsync`. Worth a focused PR by someone who has time to verify each call site does what the unified method should do.
- **§1.8 — Debounce group rebuilds.** Suggested a 250ms debounce on `FolderSynced` bursts. The plumbing pattern exists (`QueueStatusAnnounce`) but the right debounce value depends on real-world sync cadence — needs UX testing under live sync before bundling.
- **§1.11 — `GetFoldersAsync` uses `StatusAsync`.** Replacing `SELECT/CLOSE` round-trips with `STATUS` requires verification against real IMAP servers (especially Gmail's label semantics). Without that confirmation I'd rather not change the cold-connect path.
- **§2.2 — `MainWindow.xaml.cs` tree-view triplet extraction.** Labelled "good first PR" in the review but is in practice a 30-method refactor with subtle focus-management interactions. Should be its own session with manual focus testing in the running app.
- **§2.3 — CTS disposal helper.** Mechanical but ~15 sites scattered through `MainViewModel`. Better as a focused PR than mixed in here.
- **§2.4 — ViewMode/Sort magic strings.** Pure cleanup, low priority, easy to do later.
- **§3.1, §3.3, §3.4** — These are policy questions (default of `ImapAcceptInvalidCert`, configurability of OAuth client ID, OAuth `RegisterTokenCache` constructor pattern). Should be discussed before code change, not unilaterally decided.
- **§5 — CI workflow.** Infrastructure decision for the project owner; not engineering work.
- **§6 — Strategic decomposition.** Explicitly flagged in the review as senior-dev milestone work, not for piecemeal handout.

### Notes on the review process itself

The review's structure (Headline → P0 → P1 → security → tests → strategy → "things that are good") worked well as an actionable punch list. A few things that made it work:

- **Every finding had file + line references.** Made fixes verifiable rather than vibes-based.
- **Each fix included a "fix in N lines" sketch.** Useful for sanity-checking that the proposed change isn't bigger than billed — twice during this pass I ended up implementing close to the sketched form, which is a healthy sign the review's sizing was honest.
- **The "PR ordering" section gave a clear path.** I followed it for items 1–9 then continued into the perf items in the order they were listed.

For weekly cadence going forward, two recommendations:

1. **Carry forward an "open from prior review" section.** Items like §1.4, §2.3, and the strategic decompositions need a durable home; otherwise they will be re-discovered every week. This document's §8 (resolution log) is one such home — future reviews can reference "still open from 2026-05-21: §1.4, §2.3, §2.4, …" rather than restating the same finding.
2. **The reviewer's line-count claims are verifiable; the *interpretation* is the human-judgement part.** "MainViewModel grew from 1,200 to 3,355 lines" is a fact. "This is drift" is an opinion. Worth keeping a senior eye on the strategic findings rather than auto-trusting them. (In this case the drift call seems right — but the discipline of separating measurement from judgement matters.)

A third observation worth recording: the review caught a confirmed correctness bug (§1.1) that had shipped to users. That alone justifies the cost of the review pass. The other P0 items (§1.2 through §1.10) were largely latent — bugs waiting to happen rather than bugs currently happening — and the value of fixing them is proportional to how unhappy you'd be debugging them in production. The pattern of one confirmed-bug + several latent-bugs is a healthy signal: it means the review process is finding both kinds and not just churning on hypotheticals.

---

## §9. Follow-up bug investigation: saved-view system (2026-05-21)

After the §1–§4 commits landed, the user reported two suspected bugs in the saved-view system that the review hadn't surfaced:

1. **Hotkey assignment via the View Manager dialog doesn't work, even though hotkey assignment via Settings → Keyboard does.**
2. **Applying a saved view changes the title bar but doesn't refresh the message list.**

The investigation surfaced one real bug (with a non-obvious root cause), and one that turned out to be a false alarm. The reasoning behind both is worth recording — the false-alarm path was longer than the fix and is a useful template for future investigations.

### Bug #2.1 — View Manager hotkeys silently swallowed by orphan bindings

**Resolution:** Fixed in [293e280](https://github.com/kellylford/QuickMail/commit/293e280).

**Root cause.** The user's real `hotkeys.json` (read directly from `%APPDATA%\QuickMail`) contained four `view.saved.{guid}` bindings, but `views.json` did not exist — the saved views had been lost at some point while their hotkey entries survived in the config. Code-reading alone could not have found this; it required looking at the live data.

Walking the flow with that data in hand:

1. `CommandRegistry.ApplyUserOverrides` parses all four orphan bindings into `_userOverrides`.
2. None of the corresponding `view.saved.{guid}` *commands* are registered (no views in `views.json` to register them from).
3. The user creates a new view in the View Manager and assigns, say, Ctrl+Shift+8 — which happens to be one of the orphan gestures.
4. `PersistHotkey` adds a new binding for the new view's commandId, with `Gesture = "Ctrl+Shift+8"`. The override list now has **two** bindings claiming Ctrl+Shift+8: one orphan, one live.
5. The user presses Ctrl+Shift+8. `FindByGesture` iterates `_userOverrides` in insertion order, hits the orphan first, calls `FindById(orphanCommandId)`, gets back `null`, and **returns null without trying the next override or falling back to defaults**.
6. The keypress is silently swallowed.

The reason Settings → Keyboard worked but View Manager didn't: the Settings UI iterates `registry.GetAll()` which only contains *registered* commands. Orphan view bindings have no corresponding row, so a Settings save effectively prunes them (the `cfg.CustomHotkeys = HotkeyRows.Where(HasCustomBinding).Select(ToBinding)` replacement throws orphans away). View Manager preserves them.

**Three coordinated fixes:**

1. `CommandRegistry.FindByGesture` now skips overrides whose `CommandId` isn't a registered command, instead of returning `null` from the override-match branch. The first *live* override that matches wins; otherwise we fall through to the default-gesture loop. The default-suppression set is built only from live overrides, so an orphan no longer suppresses unrelated defaults either.
2. New `ICommandRegistry.GetOrphanOverrideCommandIds()` lets the app detect orphan `view.saved.*` entries after registration. `MainViewModel.RegisterViewCommands` calls a new `PruneOrphanHotkeys()` after registering all live view commands — this sweeps orphans from `hotkeys.json` so the config stays clean and the next `FindByGesture` pass doesn't have to skip them. Scoped to `view.saved.*` only so the prune never touches user overrides for built-in commands (which might be unregistered transiently mid-startup).
3. `ViewManagerViewModel.PersistHotkey` now removes any *other* binding that claims the same gesture (not just the same CommandId) before adding the new one. The conflict check in the dialog code-behind can't see orphans because it only walks `SavedViews`; this is the belt-and-suspenders guard.

**Tests added** to `CommandRegistryTests.cs`:
- `FindByGesture_IgnoresOrphanOverride_FallsThroughToDefault`
- `FindByGesture_OrphanOverride_DoesNotBlockNewerLiveOverrideOnSameGesture` — the user's exact scenario
- `GetOrphanOverrideCommandIds_ReturnsBindingsWithoutCommands`
- `FindByGesture_OrphanOverride_DoesNotSuppressUnrelatedDefault`

Plus a new `ViewManagerHotkeyIntegrationTests.cs` with two end-to-end-ish tests that exercise the real `CommandRegistry` through the dialog VM flow.

### Bug #2.2 — Apply View not refreshing messages: not reproduced

**Resolution:** No code change. Investigation suggested the original report may have been a misreading of behaviour rather than a real bug.

The user reported that applying a saved view updated the title bar but not the message list. Read of `ApplyViewAsync` and its callees (`FetchVirtualAsync`, `FetchFolderAsync`, `FetchViewFoldersAsync`) couldn't find a path where `Messages` wouldn't be repopulated after the view applied. The setter ordering (`ViewMode` → `ActiveFilter` → `ActiveSort` → `ActiveDayLimit`, then `Fetch*`) does have a quirk where `OnActiveSortChanged` calls `ApplyFiltersAndSearch` with the new filter against stale `_rawMessages`, but the subsequent `Fetch*` reload should overwrite that with fresh, properly-filtered data.

Before opening the question-asker, the user concluded the report had been wrong and the apply-view path was actually working. Stopped investigation.

**What I would have asked, if it does come back:** which kind of view (single real folder, virtual, multi-folder), what trigger (View Manager Apply button, Views menu, hotkey, folder tree), and what specifically stays on screen (previous flat list, previous grouped tree, blank pane, or mixed). Those three axes narrow it to one of the four `Fetch*` paths in `ApplyViewAsync` and the investigation can be focused.

### Notes for the weekly review process

Two methodology points worth keeping in mind:

1. **Read the live data, not just the code.** The hotkey bug was invisible from code-reading alone because the code is correct given a consistent on-disk state. The bug was in the *interaction* between the code and an inconsistent state (orphan bindings) that the code didn't anticipate. Spending a minute to look at `%APPDATA%\QuickMail\hotkeys.json` and `views.json` was the entire turning point of the investigation. Future weekly reviews should include "look at a real user's data directory" as a standard step, not just "read the source."

2. **A retracted bug report is still useful.** Bug #2.2 turned out to be wrong, but the investigation around it produced (a) confirmation that the `Fetch*` paths handle each view shape correctly, (b) a documented question-set for next time the symptom recurs, and (c) a noted quirk in setter ordering (`OnActiveSortChanged` running on stale data) that isn't a bug today but could become one if the subsequent `Fetch*` ever short-circuits. Worth recording even when no code changes.

### Carried-forward open items

For convenience when the next review runs, these were deliberately left open from the original §1–§6 review and are still open after this follow-up:

- §1.4 (extract folder-sync method across three call sites)
- §1.8 (debounce group rebuilds)
- §1.11 (use `StatusAsync` in `GetFoldersAsync`)
- §2.2 (extract tree-view controller in `MainWindow.xaml.cs`)
- §2.3 (CTS disposal helper)
- §2.4 (ViewMode/Sort magic-string cleanup)
- §3.1, §3.3, §3.4 (security/policy decisions)
- §5 (CI workflow)
- §6 (strategic decomposition: MainViewModel, MainWindow, virtual-folder sentinels, migration framework)


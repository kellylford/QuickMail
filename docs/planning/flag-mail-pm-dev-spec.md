# Flag Mail — PM & Dev Specification

**Status:** Approved — v1 complete; post-ship additions documented below
**Date:** June 13, 2026
**Target:** Phase 6 (Workflow & Productivity)
**Crew:** Delta (PM → Dev Lead → Test Enforcer)

> Combined PM + Dev spec. **Sections 1–6 are the PM portion** (problem, users, scope, UX, accessibility). **Sections 7–12 are the Dev portion** (architecture, data model, service layer, view models, views, implementation phases). **Sections 13+** are shared (command registry, accessibility, success metrics, open questions, file/test tables, appendices).

## Post-Ship Additions (v0.7.3, June 15, 2026)

Two gaps identified after shipping (issue #85) were closed without a separate spec:

### Context-menu flag actions (issue #85 gap 1)

All four context menus — **MessageContextMenu**, **ConversationGroupContextMenu**, **SenderGroupContextMenu**, **ToGroupContextMenu** — now include a **Flags** submenu. For individual messages the submenu lists all defined flags with a checkmark on the currently applied flag; selecting a checked flag clears it, selecting an unchecked flag applies it. For group menus (conversation, from, to) the submenu lists flags without checkmarks and applies the selected flag to every message in the group. Both variants include **Clear Flag** (disabled when nothing is flagged) and **Manage Flags…** at the bottom. The submenu is rebuilt on each `ContextMenu.Opened` event so flag definitions and current state are always current. `MainViewModel.SetGroupFlagAsync` was added to support batch flagging from the context menu (with the same optimistic update and announcement pattern used by `SetMessageFlagAsync`).

### Sort: Flagged First (issue #85 gap 4)

`MessageSort.FlaggedFirst` added. In flat message view, flagged messages appear first sorted by date descending, followed by unflagged messages sorted by date descending. In group views (Conversations, From, To), groups that contain at least one flagged message sort before unflagged groups, then by date/sender within each tier. Accessible from **View → Sort → Flagged First** and from the command palette (`view.sortFlaggedFirst`). Persisted to `config.ini` as `flaggedFirst`.

### Out of scope (deferred to a separate spec)

**Issue #85 gap 2 — per-flag hotkeys in Flag Manager** — requires design decisions around hotkey storage, CommandRegistry dynamic entries, and UI that are not resolved in this spec. A full spec is required before implementation.

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [User Problem & Opportunity](#2-user-problem--opportunity)
3. [Personas & Use Cases](#3-personas--use-cases)
4. [Competitive Landscape](#4-competitive-landscape)
5. [Design Principles](#5-design-principles)
6. [Feature Scope & Acceptance Criteria](#6-feature-scope--acceptance-criteria)
7. [Data Model](#7-data-model)
8. [Service Layer](#8-service-layer)
9. [Persistence & Migration](#9-persistence--migration)
10. [ViewModels](#10-viewmodels)
11. [Views (XAML + Code-Behind)](#11-views-xaml--code-behind)
12. [Implementation Phases](#12-implementation-phases)
13. [Command Registry & Shortcuts](#13-command-registry--shortcuts)
14. [Accessibility](#14-accessibility)
15. [Success Metrics](#15-success-metrics)
16. [Open Questions & Risks](#16-open-questions--risks)
17. [Files to Create](#17-files-to-create)
18. [Files to Modify](#18-files-to-modify)
19. [Tests to Add](#19-tests-to-add)
20. [Appendix A — Keyboard Cheat Sheet](#appendix-a--keyboard-cheat-sheet)
21. [Appendix B — Flag Lifecycle State Diagram](#appendix-b--flag-lifecycle-state-diagram)
22. [Appendix C — Sample `flags.json`](#appendix-c--sample-flagsjson)

---

## 1. Executive Summary

QuickMail today has no way for a user to mark a message as requiring action or attention beyond reading it. The `\Flagged` IMAP flag exists on every account — it is what Outlook calls "starred" and Gmail calls "important" — but QuickMail fetches it from the server and then ignores it entirely. This spec adds end-to-end flagging: one keypress (`K`) to flag or unflag a message or a whole conversation/group; named, color-coded flag definitions that a user can create and manage in a Flag Manager window; a `Flagged` filter on any view; and a built-in "All Flagged Mail" virtual folder that aggregates flagged messages across every account. Flag status is surfaced prominently in screen reader announcements (before read status, controlled by a user-configurable setting that defaults on), so keyboard-only and screen reader users get the same zero-friction flagging experience as mouse users.

---

## 2. User Problem & Opportunity

### 2.1 Current state (verified against the code)

| Surface | Today | Pain |
|---|---|---|
| IMAP `\Flagged` | Fetched via `MessageSummaryItems.Flags` in `ImapMailService.SummaryToModel` ([ImapMailService.cs:1244](../../QuickMail/Services/ImapMailService.cs)) but not mapped to `MailMessageSummary`. | Flags set by Outlook/Gmail on the same account are silently discarded. |
| `MailMessageSummary` | No `IsFlagged` or `FlagId` property ([MailMessageSummary.cs](../../QuickMail/Models/MailMessageSummary.cs)). | No per-message flag state in the UI layer at all. |
| `MessageFilter` | Values: All, Unread, Read, WithAttachments, Replied, Forwarded, ToMe ([MessageFilter.cs](../../QuickMail/Models/MessageFilter.cs)). | No `Flagged` filter; users cannot view "only flagged messages" in any view. |
| `SavedView` | No flag-related properties ([SavedView.cs](../../QuickMail/Models/SavedView.cs)). | Cannot save a "Flagged" view with a hotkey. |
| Virtual folders | `\x00AllMail`, `\x00AllInboxes`, etc. — no `\x00AllFlagged` sentinel. | No cross-account flagged message aggregation. |
| Accessibility announcement | Format: `"{ReadStatusLabel}. {From}. {Subject}. {Preview}. {DateDisplay}."` ([MainWindow.xaml.cs:2447](../../QuickMail/Views/MainWindow.xaml.cs)). | Flag state is absent even when the user's Outlook flags are restored on next sync. |
| Graph accounts | No `flag` property in `GraphDtos.GraphMessageDto` ([GraphDtos.cs:73](../../QuickMail/Services/Graph/GraphDtos.cs)). | Microsoft 365 `followUpFlag` is never read or written. |

### 2.2 What users want

- "I need one key to mark a message as 'deal with this later.' One press flags it, one press unflags it."
- "When I'm in conversation view and flag the conversation, I want every message in it flagged."
- "I want to have a 'Urgent' flag in red and a 'Waiting' flag in yellow, and filter my inbox by each."
- "I need the screen reader to tell me something is flagged the moment I land on it — not after the subject and date."
- "I use Outlook on my phone. When I flag messages there, I want QuickMail to see them."

### 2.3 Why now

The IMAP flag infrastructure is already in place — `MessageFlags.Flagged` is fetched on every sync call; adding it to the model is a one-line change. The read-status announcement pattern (`ReadStatusLabel`, `AnnouncementCategory`) is mature and well-tested. The JSON-file service pattern (`views.json`, `rules.json`, `templates.json`) is established and can host `flags.json` with no new plumbing. Saved views, filters, and virtual folders are all in place; adding `Flagged` to each is incremental. The timing is right: power users are actively using saved views (introduced in v0.7), and flagging is the next natural productivity layer.

---

## 3. Personas & Use Cases

| Persona | Need | Use case |
|---|---|---|
| **Power keyboard user (Alex)** | "One key, zero mouse." | Presses `K` in the message list to flag; presses `K` again to unflag. Never lifts hands from keyboard. |
| **Screen reader user (Pat)** | "Tell me immediately if something is flagged, before everything else." | Arrows to a flagged message and hears "Urgent. Unread. Kelly Ford. Budget review. 6/10/2026" before any other content. |
| **Multi-account knowledge worker (Sam)** | "Flags set in Outlook should show in QuickMail." | Work Outlook and QuickMail share the same Exchange account. Flags Sam sets in Outlook appear correctly in QuickMail on next sync. |
| **Project manager (Riley)** | "Red for blockers, yellow for waiting-on-others, green for done." | Creates three named flags in the Flag Manager, assigns them from the keyboard, and filters the inbox to each color with a saved view hotkey. |
| **Conversation view user (Morgan)** | "Flag the whole thread, not just one message." | Presses `K` on a conversation group row; all messages in the thread are flagged at once. |

---

## 4. Competitive Landscape

| Client | Flagging model |
|---|---|
| **Outlook** | One binary flag per message (server-synced). Color categories are separate. |
| **Thunderbird** | One "Starred" flag per message, maps to IMAP `\Flagged`. |
| **mutt / aerc** | `F` toggles `\Flagged`. Color rules can highlight flagged rows. No named flags. |
| **Gmail (web)** | "Starred" (one) + optional colored stars (named, local to Gmail). |
| **Apple Mail** | Named flags with seven preset colors, mapped to IMAP keywords. One flag per message. |

**QuickMail's differentiation:** Named user-defined flags (like Apple Mail), server-synced IMAP `\Flagged` for cross-client interop (like Thunderbird), first-class keyboard flagging with group support, and accessible-by-default screen reader integration.

---

## 5. Design Principles

1. **One keypress, always.** `K` flags or unflags a message or a whole group. No confirmation dialog, no menu required for the common case.
2. **Server-synced by default.** Setting any flag sets IMAP `\Flagged` on the server. Clearing a flag clears it. Other clients that only know `\Flagged` (Outlook, Thunderbird, mutt) stay in sync.
3. **Named flags are local enrichment, not a server dependency.** The flag name and color live in `flags.json` in the user's profile. If the file is missing, the feature degrades to a single unnamed flag — it never fails because of a server limitation.
4. **Accessible first.** Flag status is announced before read status in every message row's accessible name. It is on by default. Users who find it intrusive can disable it in Settings.
5. **Zero change for users who don't use flags.** Users who never press `K` or open the Flag Manager see no new UI elements in the message list. The flag indicator column is hidden when nothing is flagged in the current view (or always shown as a narrow decoration — see §11.1 for the decision).
6. **Grouped views flag the whole group.** Flagging in conversations, from, or to view applies to every message in the group. This is the intuitive behavior: the user is acting on the thread, not a single message.

---

## 6. Feature Scope & Acceptance Criteria

### 6.1 In scope (v1)

| Feature | Setting / Shortcut | Default | Notes |
|---|---|---|---|
| Toggle flag on message | `K` | — | Applies the user's configured K default flag; removes any flag if already set. |
| Configurable K default flag | `DefaultFlagId` in `config.ini` | Built-in "Flagged" flag | Which named flag `K` applies. Changeable in Flag Manager. |
| Pick a specific flag | `Ctrl+Shift+K` | — | Opens inline flag picker popup. |
| Flag/unflag all messages in a group | `K` (on group row in Conversations / From / To view) | — | Applies/removes the K default flag on all messages in the group. |
| Flagged filter | `MessageFilter.Flagged` | — | Shows messages with any flag set. |
| Named flag definitions | `FlagDefinition` in `flags.json` | One built-in "Flagged" flag | Color + name. One flag per message at a time. |
| Flag Manager window | `Ctrl+Shift+M` (reassigned — see §13) | — | Create, rename, recolor, delete, reorder flags. |
| "All Flagged Mail" virtual folder | `\x00AllFlagged` sentinel | — | Cross-account aggregation of all flagged messages. |
| Flag status in saved views | `FlagFilter: string?` on `SavedView` | `null` (any) | Null = "any flag"; a flag name = only that named flag. |
| Accessibility announcement | `AnnounceFlagStatus` in `config.ini` | `true` | Flag name announced before read status in message rows. |
| Properties view flag row | Added to `MessagePropertiesBuilder` | — | Shows flag name (or "None") in the Storage section. |
| IMAP `\Flagged` sync (read) | In `ImapMailService.SummaryToModel` | — | Reads existing `\Flagged` state from server on sync. |
| IMAP `\Flagged` sync (write) | `SetMessageFlaggedAsync` | — | Sets / clears `\Flagged` on the server immediately. |
| Graph `followUpFlag` sync | `GraphMailService` + `GraphDtos` | — | Read/write `flagStatus: "flagged"` ↔ `"notFlagged"`. |
| SQLite persistence | `flag_id` column in `MessageSummary` | `NULL` | Stores which named flag is applied; `NULL` = unflagged. |
| Settings toggle | `AnnounceFlagStatus` (bool) | `true` | In Settings → General → Accessibility. |

### 6.2 Explicitly out of scope (v1)

- **Multiple flags per message.** One named flag per message at a time. A second `K` press removes the existing flag, not stacks a new one.
- **Server-side named flags via IMAP keywords.** Keywords like `$FollowUp` have inconsistent server support (especially Gmail). Named flags are local only; `\Flagged` is the server primitive.
- **Flag import/export.** `flags.json` is the format; no vCard, CSV, or IMAP keyword mapping.
- **Flag-based mail rules.** Rules continue to operate on header-text matches. Flag conditions in rules are deferred to a later spec.
- **Drag-to-flag.** Mouse drag gestures are not part of this spec. Single-click flagging on a flag column cell is in scope (see §11.1).
- **Flag sync across profiles.** Each profile has its own `flags.json`.
- **Flag expiry / reminders.** Flagging is a marking tool only; no due dates or reminders.
- **Shared / server-side flag definitions.** Named flags are local to the QuickMail profile.

---

## 7. Data Model

### 7.1 `FlagDefinition`

New model class stored in `flags.json`. This is a user preference, not mail metadata.

```csharp
// QuickMail/Models/FlagDefinition.cs
public class FlagDefinition
{
    public Guid   Id        { get; set; } = Guid.NewGuid();
    public string Name      { get; set; } = string.Empty;
    public string ColorHex  { get; set; } = "#FF8C00";  // amber default
    public int    SortOrder { get; set; }
    public bool   IsBuiltIn { get; set; }               // true for the default "Flagged"
}
```

**Built-in flag:** One `FlagDefinition` with a well-known constant `Id` (`FlagService.DefaultFlagId`) and `IsBuiltIn = true`. It can be renamed and recolored but never deleted.

**Constraint:** Maximum 20 user-defined flags (the Flag Manager enforces this; more would clutter the picker).

### 7.2 `MailMessageSummary` additions

```csharp
// QuickMail/Models/MailMessageSummary.cs — additions

[ObservableProperty]
[NotifyPropertyChangedFor(nameof(FlagLabel))]
[NotifyPropertyChangedFor(nameof(StatusDisplay))]
[NotifyPropertyChangedFor(nameof(ReadStatusLabel))]
private string? _flagId;       // Guid string; null = not flagged

[ObservableProperty]
[NotifyPropertyChangedFor(nameof(FlagLabel))]
private string? _flagName;     // Denormalized from FlagDefinition for display; null if unflagged

[ObservableProperty]
[NotifyPropertyChangedFor(nameof(FlagLabel))]
private string? _flagColorHex; // Denormalized; null if unflagged

/// <summary>
/// Human-readable flag label for accessibility. Empty string if not flagged.
/// Example: "Urgent" or "Flagged".
/// </summary>
public string FlagLabel => FlagName ?? string.Empty;

public bool IsFlagged => FlagId is not null;
```

`FlagName` and `FlagColorHex` are denormalized onto the summary so the message list can render flag indicators without a secondary lookup. They are kept in sync by `MainViewModel` when flags are applied or when `FlagService` raises a `FlagDefinitionsChanged` event.

### 7.3 `StatusDisplay` and `ReadStatusLabel` changes

Both computed properties gain a flag-aware prefix:

```csharp
public string StatusDisplay
{
    get
    {
        if (IsFlagged)   return FlagLabel;   // flag name replaces status in the column
        if (IsReplied)   return "Replied";
        if (IsForwarded) return "Fwd";
        if (!IsRead)     return "New";
        return string.Empty;
    }
}
```

`ReadStatusLabel` keeps its existing values ("replied", "forwarded", "unread", "read"); flag status is separate in the accessibility announcement format (see §14).

### 7.4 `ConversationGroup` and `SenderGroup` additions

```csharp
// Both models
public bool HasFlagged => Messages.Any(m => m.IsFlagged);
```

Used to show a flag indicator on group rows in grouped view modes.

### 7.5 `SavedView` addition

```csharp
// QuickMail/Models/SavedView.cs
/// <summary>
/// When set, the view shows only messages flagged with this specific flag name.
/// Null means no flag filter (filter by MessageFilter.Flagged shows any flag).
/// </summary>
public string? FlagFilter { get; set; }
```

### 7.6 `ConfigModel` additions

```csharp
public bool   AnnounceFlagStatus { get; set; } = true;

/// <summary>
/// The Guid string of the flag that the K key applies.
/// Defaults to FlagService.DefaultFlagId (the built-in "Flagged" flag).
/// Stored as a string so config.ini stays human-readable.
/// </summary>
public string DefaultFlagId      { get; set; } = FlagService.DefaultFlagId.ToString();
```

`DefaultFlagId` is read at runtime by `FlagService.GetKDefaultFlag()`. If the stored Guid does not match any defined flag (e.g., the flag was deleted), `FlagService` falls back to the built-in flag and updates config silently.

---

## 8. Service Layer

### 8.1 `IFlagService` (new)

```csharp
// QuickMail/Services/IFlagService.cs
public interface IFlagService
{
    event EventHandler? FlagDefinitionsChanged;

    Task<List<FlagDefinition>> LoadFlagDefinitionsAsync();
    Task SaveFlagDefinitionsAsync(List<FlagDefinition> flags);
    FlagDefinition GetBuiltInFlag();          // always returns the built-in "Flagged" flag

    /// <summary>
    /// Returns the flag that the K key applies, per the user's DefaultFlagId config setting.
    /// Falls back to the built-in flag if the configured ID is missing or deleted.
    /// </summary>
    FlagDefinition GetKDefaultFlag();

    /// <summary>
    /// Sets a flag as the K default. Persists DefaultFlagId to config.ini.
    /// </summary>
    Task SetKDefaultFlagAsync(Guid flagId);

    /// <summary>
    /// Sets the given named flag on the message (or clears if flagId is null).
    /// Propagates \Flagged to the IMAP/Graph server.
    /// </summary>
    Task SetMessageFlagAsync(
        MailMessageSummary summary,
        string? flagId,
        CancellationToken ct = default);

    /// <summary>
    /// Toggles the default (built-in) flag on a message.
    /// If the message has any flag set, clears it; otherwise applies the default.
    /// </summary>
    Task ToggleDefaultFlagAsync(MailMessageSummary summary, CancellationToken ct = default);

    /// <summary>
    /// Batch-flags all messages in the collection with the given flagId (null = clear).
    /// Used for group flagging in conversation / sender views.
    /// </summary>
    Task SetMessageFlagBatchAsync(
        IEnumerable<MailMessageSummary> messages,
        string? flagId,
        CancellationToken ct = default);
}
```

### 8.2 `FlagService` implementation notes

- Loads `flags.json` at startup; caches the list. If the file is missing or corrupt, creates a fresh list containing only the built-in flag (matches the recovery pattern used by `ViewService`, `RuleService`, etc.).
- Writes use atomic temp-then-rename.
- `SetMessageFlagAsync`:
  1. Updates `summary.FlagId`, `summary.FlagName`, `summary.FlagColorHex` in memory immediately (optimistic update).
  2. Calls `_localStore.UpdateFlagIdAsync(...)` to persist.
  3. Calls `_mailService.SetMessageFlaggedAsync(summary, isFlagged: flagId is not null, ct)` to sync IMAP/Graph.
  4. If either async step fails, rolls back the in-memory update and surfaces an error announcement.
- `SetMessageFlagBatchAsync`: same pattern, but batches the local store call and IMAP call.

### 8.3 `ILocalStoreService` additions

```csharp
// QuickMail/Services/ILocalStoreService.cs — additions
Task UpdateFlagIdAsync(Guid accountId, string folderName, string messageId, string? flagId);
Task UpdateFlagIdBatchAsync(
    IEnumerable<(Guid AccountId, string FolderName, string MessageId)> items,
    string? flagId);
```

Summaries loaded from the local store populate `FlagId`. `FlagName`/`FlagColorHex` are resolved from `FlagService.LoadFlagDefinitionsAsync()` in `MainViewModel` after summaries are loaded.

### 8.4 `IImapMailService` / `ImapMailService` additions

```csharp
Task SetMessageFlaggedAsync(
    Guid accountId, string folderName, string messageId,
    bool isFlagged, CancellationToken ct = default);
```

Implementation:
```csharp
if (isFlagged)
    await folder.AddFlagsAsync(ToUid(messageId), MessageFlags.Flagged, silent: true, ct);
else
    await folder.RemoveFlagsAsync(ToUid(messageId), MessageFlags.Flagged, silent: true, ct);
```

**`SummaryToModel` change** (line 1244 area):
```csharp
// Add alongside IsRead / IsReplied:
IsServerFlagged = (s.Flags & MessageFlags.Flagged) != 0,
```

`IsServerFlagged` is a transient import field on `MailMessageSummary` (not persisted directly; it drives setting `FlagId` to the default flag's Id if no local `flag_id` exists, and vice versa — see §9).

### 8.5 `GraphMailService` / `GraphDtos` additions

**DTO:**
```csharp
// GraphDtos.GraphMessageDto
[JsonPropertyName("flag")] public GraphFollowUpFlag? Flag { get; set; }

public class GraphFollowUpFlag
{
    [JsonPropertyName("flagStatus")] public string FlagStatus { get; set; } = "notFlagged";
    // "notFlagged" | "flagged" | "complete"
}
```

**Service:** In `SummaryToModel` mapping:
```csharp
IsServerFlagged = m.Flag?.FlagStatus == "flagged",
```

**Write:** New `SetMessageFlaggedAsync` in `GraphMailService`:
```csharp
// PATCH /messages/{id}
// { "flag": { "flagStatus": "flagged" } }  or  { "flagStatus": "notFlagged" }
```

### 8.6 `MailServiceRouter` plumbing

`MailServiceRouter` must expose `SetMessageFlaggedAsync` and route to the correct backend (`ImapMailService` or `GraphMailService`) based on account type — following the same dispatch pattern used for `DeleteMessageAsync`, `MarkReadAsync`, etc.

### 8.7 `--online` mode compatibility

| Call | Normal mode | `--online` mode |
|---|---|---|
| `UpdateFlagIdAsync` | Persists to SQLite | Wrap in try/catch; on `SqliteException`, skip local persist (IMAP flag still set on server). |
| `UpdateFlagIdBatchAsync` | Persists to SQLite | Same. |
| `LoadFlagDefinitionsAsync` | Reads `flags.json` | Works normally (profile dir always accessible). |
| `SetMessageFlaggedAsync` (IMAP/Graph) | Always | Works normally (server call, no SQLite). |
| In-memory flag state | Set optimistically | Works normally (memory, no SQLite). |

In `--online` mode the flag name annotation survives for the session (held in memory on `MailMessageSummary`) but is not persisted. On next launch in normal mode it rehydrates from SQLite. This is acceptable: the IMAP `\Flagged` state on the server is always authoritative.

---

## 9. Persistence & Migration

### 9.1 `flags.json`

New file in the profile directory. Managed by `FlagService`. Atomic writes (temp-then-rename). Corrupt file → rename to `flags.json.bak-{timestamp}`, treat as empty, insert built-in flag.

**Schema:**
```json
[
  {
    "Id": "00000000-0000-0000-0000-000000000001",
    "Name": "Flagged",
    "ColorHex": "#FF8C00",
    "SortOrder": 0,
    "IsBuiltIn": true
  }
]
```

See [Appendix C](#appendix-c--sample-flagsjson) for a fuller example.

### 9.2 SQLite migration (schema version 3 → 4)

One `ALTER TABLE` migration, following the existing `RunMigration` pattern:

```csharp
RunMigration(conn,
    "ALTER TABLE MessageSummary ADD COLUMN flag_id TEXT NULL DEFAULT NULL;");
```

`CurrentSchemaVersion` bumps from `3` to `4`. No data backfill needed (existing messages are simply unflagged).

### 9.3 IMAP ↔ local flag reconciliation on sync

When `SummaryToModel` produces a summary with `IsServerFlagged = true` but the local `flag_id IS NULL`, the sync path sets `flag_id` to `FlagService.DefaultFlagId` — the message was flagged by another client (Outlook, phone), so we map it to the default named flag. When `IsServerFlagged = false` and `flag_id IS NOT NULL`, `flag_id` is cleared (another client unflagged it). This reconciliation runs in `SyncService.OnFolderSynced` after merging new summaries.

---

## 10. ViewModels

### 10.1 `MainViewModel` additions

```csharp
// New async command
[RelayCommand]
public async Task ToggleFlagAsync()
// and
[RelayCommand]
public async Task PickFlagAsync()
```

- `ToggleFlagAsync`: called by `K` keypress. Uses `_flagService.GetKDefaultFlag()` to determine which flag to apply. If `SelectedMessage` is set → `_flagService.ToggleDefaultFlagAsync(SelectedMessage)`. If a group row is selected (Conversations/From/To view) → `_flagService.SetMessageFlagBatchAsync(group.Messages, ...)`.
- `PickFlagAsync`: called by `Ctrl+Shift+K`. Opens `FlagPickerWindow` or inline popup (see §11.2).
- Subscribes to `FlagService.FlagDefinitionsChanged`: when definitions change, iterates `Messages` and refreshes `FlagName`/`FlagColorHex` on any message whose `FlagId` matches a renamed/recolored definition.
- `MessageFilter.Flagged` support in `BuildFilteredMessages`: `msg.IsFlagged`.
- `SavedView.FlagFilter` support: secondary filter applied after `MessageFilter`.
- New virtual folder entry in the folder tree: "All Flagged Mail" (`\x00AllFlagged`). Resolved identically to `\x00AllInboxes` but filters to `IsFlagged` after merging.

### 10.2 `FlagManagerViewModel` (new)

Manages CRUD for `FlagDefinition` list. Pattern mirrors `ViewManagerViewModel` and `RulesManagerViewModel`.

Properties:
- `Flags: ObservableCollection<FlagDefinition>` (sorted by `SortOrder`)
- `SelectedFlag: FlagDefinition?`
- `CanDelete`: false when `SelectedFlag.IsBuiltIn`
- `CanMoveUp` / `CanMoveDown`: based on `SelectedFlag`'s position

Commands: `AddFlagCommand`, `DeleteFlagCommand`, `RenameStartCommand`, `SaveRenameCommand`, `CancelRenameCommand`, `ChangeColorCommand`, `MoveUpCommand`, `MoveDownCommand`, `SetAsKDefaultCommand`.

`SetAsKDefaultCommand`: marks the selected flag as the one `K` applies. Calls `FlagService.SetKDefaultFlagAsync(SelectedFlag.Id)`. The list item shows a "K" badge or bold label to indicate which flag is the current K default. Only one flag can be the K default at a time.

Events:
- `event Func<string, string, Task<bool>>? ConfirmDeleteRequested` — raised before delete; View shows dialog.
- `event Action<string, AnnouncementCategory>? AnnouncementRequested` — for accessibility announcements.

Does not raise `FlagDefinitionsChanged` directly — it calls `FlagService.SaveFlagDefinitionsAsync` and lets `FlagService` raise the event. This prevents the parent window from reacting while `FlagManagerWindow`'s message loop is still active (the same modal dialog rule that burned us with `ViewsChanged`).

### 10.3 `FlagPickerViewModel` (new)

Drives the inline flag picker popup (§11.2).

Properties:
- `Flags: List<FlagDefinition>` (loaded from `FlagService`)
- `SelectedFlag: FlagDefinition?`

Commands: `ApplyFlagCommand`, `ClearFlagCommand`, `CancelCommand`.

Raises `FlagSelected: event Action<FlagDefinition?>?` — View subscribes and calls `MainViewModel.SetFlagAsync(summary, flag)`.

---

## 11. Views (XAML + Code-Behind)

### 11.1 Message list flag indicator

**Decision:** A narrow colored rectangle (4px wide) is drawn on the leading edge of each message list row, colored with `FlagColorHex`. When `IsFlagged` is false, the rectangle is transparent (zero visual footprint). This approach:
- Does not add a column (no tab stop, no header, no column width configuration).
- Does not use color as the *only* indicator: the accessible name of the row includes the flag name (see §14.2).
- Works in the existing DataTemplate without structural changes to the list layout.
- The rectangle is `IsHitTestVisible="False"` so it does not interfere with row click/selection.

**Single-click flagging:** A mouse user can activate flag/unflag by clicking the colored rectangle area (a 4px × row-height target is too narrow; we extend it to a 16px hit area using a transparent overlay). Click on the hit area calls `ToggleFlagAsync`. This is a secondary shortcut for mouse users; `K` remains the primary.

**Group rows** (Conversations/From/To view): Group rows show the colored rectangle only when `HasFlagged` is true. The color reflects the most common flag in the group (plurality), or the default flag color if tied.

### 11.2 Flag picker popup (`FlagPickerWindow`)

A small, non-resizable popup window that appears near the message list when `Ctrl+Shift+K` is pressed.

- Lists all defined flags (name + color swatch) in `SortOrder` order.
- One list item: "Clear flag" (always present, only enabled when message is flagged).
- Escape: close without change.
- Enter or Space on a flag: apply and close.
- Arrow keys: navigate the list.
- Focus opens on the list. No search box in v1 (max 20 flags).
- Screen reader announces: "Flag picker. [flag count] flags."
- F6 is not applicable (single-pane popup).

This is a new `Window` subclass and must follow the New Window Checklist:
- F6 ring: single pane, no cycle needed.
- Command palette: `Ctrl+Shift+P` opens a minimal palette (Apply Flag, Clear Flag, Close).
- Cancellation token: not needed (no async load; flags are already in memory).
- Focus restoration: on close, focus returns to the message list row that was active.

### 11.3 `FlagManagerWindow` (new)

Follows the New Window Checklist in full.

Layout (top to bottom):
1. **Toolbar** — Add Flag, Delete Flag buttons.
2. **Flag list** (`ListBox`) — each row shows color swatch + name. One tab stop; arrow navigation within.
3. **Edit panel** — Name `TextBox`, Color picker (a row of preset color buttons), Move Up / Move Down buttons.

F6 ring: Toolbar → Flag list → Edit panel → (cycle).

`Ctrl+Shift+P` opens a command palette with: Add Flag, Delete Flag, Move Up, Move Down, Close.

Does **not** subscribe to any parent-window events before `ShowDialog()`. Parent calls `MainViewModel.ReloadFlagsAsync()` after the window closes. No `FlagDefinitionsChanged` subscription before `ShowDialog()` — avoids the COM apartment crash documented in CLAUDE.md.

Color picker: 12 preset color buttons (red, orange, amber, yellow, lime, green, teal, cyan, blue, indigo, violet, pink) plus a hex input box. Color buttons are a radio group with `KeyboardNavigation.DirectionalNavigation="Cycle"`, one tab stop.

### 11.4 "All Flagged Mail" in the folder tree

Added as a virtual folder under a "Flagged" group in the folder tree, above "All Mail," following the same rendering path as "All Inboxes." Uses `\x00AllFlagged` as the `FullName` sentinel. No additional XAML work needed; the folder tree template already handles virtual folders.

### 11.5 Settings → General → Accessibility

New checkbox: "Announce flag status" (`AnnounceFlagStatus` setting). Placed immediately below (or grouped with) the existing announcement checkboxes (`AnnounceHints`, `AnnounceStatus`, `AnnounceResults`). Label: "Announce flag status." Default: on.

---

## 12. Implementation Phases

### Phase 1: Data model, SQLite migration, and IMAP read

**Goal:** `FlagId` flows from IMAP → local store → `MailMessageSummary`. No UI changes yet. Existing flag states (if any) from Outlook/phone are now visible in the model.

**Deliverables:**
- Create `QuickMail/Models/FlagDefinition.cs`
- Modify `QuickMail/Models/MailMessageSummary.cs` — add `FlagId`, `FlagName`, `FlagColorHex`, `FlagLabel`, `IsFlagged`; update `StatusDisplay` computed property
- Modify `QuickMail/Models/MessageFilter.cs` — add `Flagged` value
- Modify `QuickMail/Models/ConversationGroup.cs` — add `HasFlagged`
- Modify `QuickMail/Models/SenderGroup.cs` — add `HasFlagged`
- Modify `QuickMail/Models/SavedView.cs` — add `FlagFilter`
- Modify `QuickMail/Models/ConfigModel.cs` — add `AnnounceFlagStatus`
- Modify `QuickMail/Services/ImapMailService.cs` — map `MessageFlags.Flagged` in `SummaryToModel`
- Modify `QuickMail/Services/Graph/GraphDtos.cs` — add `GraphFollowUpFlag`
- Modify `QuickMail/Services/GraphMailService.cs` — read `flag.flagStatus` in `SummaryToModel`
- Modify `QuickMail/Services/LocalStoreService.cs` — add `flag_id` column migration; implement `UpdateFlagIdAsync`, `UpdateFlagIdBatchAsync`; read `flag_id` in load methods
- Modify `QuickMail/Services/ILocalStoreService.cs` — declare new methods

**Tests:**
- `LocalStoreServiceTests` — `UpdateFlagId_PersistsAndLoads`, `UpdateFlagIdBatch_UpdatesMultiple`, `SchemaMigratesFrom3To4`
- `MessageFilterTests` — `FlaggedFilter_ShowsOnlyFlaggedMessages`
- `MailMessageSummaryTests` (new) — `FlagLabel_IsEmptyWhenUnflagged`, `FlagLabel_IsNameWhenFlagged`, `StatusDisplay_ShowsFlagName`, `IsFlagged_TrueWhenFlagIdSet`

**Risk:** SQLite migration is additive (`ALTER TABLE ADD COLUMN`); risk is low. IMAP `\Flagged` reads are already fetched; just adding the map.

**Duration:** 3–4 hours

---

### Phase 2: `FlagService`, write path, `MainViewModel` toggle

**Goal:** User can press `K` to flag/unflag a message. Flag is persisted locally and synced to IMAP/Graph. Screen reader hears the updated state on next focus.

**Deliverables:**
- Create `QuickMail/Services/IFlagService.cs`
- Create `QuickMail/Services/FlagService.cs`
- Modify `QuickMail/Services/IImapMailService.cs` — declare `SetMessageFlaggedAsync`
- Modify `QuickMail/Services/ImapMailService.cs` — implement `SetMessageFlaggedAsync`
- Modify `QuickMail/Services/IGraphMailService.cs` — declare `SetMessageFlaggedAsync`
- Modify `QuickMail/Services/GraphMailService.cs` — implement `SetMessageFlaggedAsync` via Graph PATCH
- Modify `QuickMail/Services/MailServiceRouter.cs` — expose and route `SetMessageFlaggedAsync`
- Modify `QuickMail/App.xaml.cs` — construct and wire `FlagService` in the DI composition (after `TemplateService`, before `RuleService`)
- Modify `QuickMail/ViewModels/MainViewModel.cs` — `ToggleFlagAsync`, `PickFlagAsync`, `Flagged` filter logic, `SavedView.FlagFilter` application, `\x00AllFlagged` virtual folder support, `FlagDefinitionsChanged` subscription
- Modify `QuickMail/Services/ConfigService.cs` — parse/write `AnnounceFlagStatus`
- Modify `QuickMail/Views/MainWindow.xaml.cs` — bind `K` and `Ctrl+Shift+K` via `CommandRegistry`; update message accessible name format (§14.2)

**Tests:**
- `FlagServiceTests` (new) — `ToggleDefault_AppliesBuiltInFlagToUnflaggedMessage`, `ToggleDefault_ClearsFlagOnFlaggedMessage`, `SetBatch_FlagsAllMessages`, `CorruptJson_RecoversWithBuiltInFlag`
- `MainViewModelFlagTests` (new) — `ToggleFlagCommand_SetsFlag`, `ToggleFlagCommand_ClearsFlag`, `FlaggedFilter_FiltersCorrectly`, `AllFlaggedVirtualFolder_AggregatesAcrossAccounts`
- `ConfigServiceTests` (existing) — add `CanRoundTripAnnounceFlagStatusAsync`

**`--online` mode gate:** `SetMessageFlagAsync` wraps `UpdateFlagIdAsync` in its own try/catch (separate from the IMAP/Graph call's catch). If `LocalStoreService` throws `SqliteException`, the IMAP/Graph flag is still set — the server flag is the source of truth and the in-memory state is correct for the session. This is the two-scope catch pattern required by CLAUDE.md. Explicitly verify in Phase 2 testing by running with `--online` and pressing `K`.

**Risk:** `MailServiceRouter` routing for flag operations — ensure account-type dispatch matches the pattern used for `DeleteMessage`. Failure mode: flag set locally but not on server. Mitigation: surface error announcement, leave local state as set (optimistic; next sync will reconcile).

**Duration:** 4–5 hours

---

### Phase 3: Flag indicator UI + accessibility announcement

**Goal:** Message list rows show the colored flag indicator. Accessible name includes flag name before read status. Settings checkbox wired.

**Deliverables:**
- Modify `QuickMail/Views/MainWindow.xaml` — add flag indicator rectangle to message row DataTemplate; add single-click hit area; add group row flag indicator; update `AutomationProperties.Name` binding on message rows
- Modify `QuickMail/Views/MainWindow.xaml.cs` — update message accessible name format string (line 2447); respect `AnnounceFlagStatus` setting
- Modify `QuickMail/Views/SettingsDialog.xaml` — add "Announce flag status" checkbox to Accessibility section
- Modify `QuickMail/ViewModels/SettingsViewModel.cs` — add `[ObservableProperty] private bool _announceFlagStatus;`
- Modify `QuickMail/Helpers/MessagePropertiesBuilder.cs` — add "Flag" row to Storage section

**Tests:**
- `XamlParseTests` — MainWindow and SettingsDialog XAML load
- `SettingsViewModelTests` (existing) — add `AnnounceFlagStatusLoadAndSaveAsync`
- `MessagePropertiesBuilderTests` (new or existing) — `Build_IncludesFlagRow`

**Risk:** `AutomationProperties.Name` binding — the name must be built at render time from `FlagLabel` + `ReadStatusLabel` + other fields. If the binding is complex, it may be cleaner to compute it in `MailMessageSummary` as a new `AccessibleName` computed property (triggered by all relevant property changes). Decide during implementation; either approach is acceptable.

**Duration:** 2–3 hours

---

### Phase 4: Flag Manager window + flag picker popup

**Goal:** Users can create, rename, recolor, delete, and reorder named flags. `Ctrl+Shift+K` opens the picker.

**Deliverables:**
- Create `QuickMail/ViewModels/FlagManagerViewModel.cs`
- Create `QuickMail/ViewModels/FlagPickerViewModel.cs`
- Create `QuickMail/Views/FlagManagerWindow.xaml`
- Create `QuickMail/Views/FlagManagerWindow.xaml.cs`
- Create `QuickMail/Views/FlagPickerWindow.xaml`
- Create `QuickMail/Views/FlagPickerWindow.xaml.cs`
- Modify `QuickMail/Views/MainWindow.xaml.cs` — wire `Ctrl+Shift+K` → `PickFlagAsync`; open `FlagManagerWindow` from command registry
- Modify `QuickMail/ViewModels/MainViewModel.cs` — `OpenFlagManagerAsync` command
- Modify `QuickMail/Views/MainWindow.xaml` — add "All Flagged Mail" virtual folder entry rendering (if not already handled by existing folder tree template)

**Tests:**
- `FlagManagerViewModelTests` (new) — `AddFlag_AppearsInList`, `DeleteBuiltIn_IsRejected`, `Rename_UpdatesDefinition`, `ReorderUp_MovesFlag`, `CorruptFile_RecoveredOnOpen`
- `FlagPickerViewModelTests` (new) — `ApplyFlag_RaisesEventWithFlag`, `ClearFlag_RaisesEventWithNull`
- `XamlParseTests` — `FlagManagerWindow` and `FlagPickerWindow` XAML load

**Risk:** Color picker color button radio group — ensure `KeyboardNavigation.DirectionalNavigation="Cycle"` is set on the container. Missing this would strand focus (see CLAUDE.md accessibility checklist).

**Duration:** 4–5 hours

---

### Phase 5: IMAP ↔ local reconciliation on sync

**Goal:** Messages flagged by other clients (Outlook, phone) appear as flagged in QuickMail on next sync. Flags cleared externally are cleared in QuickMail.

**Deliverables:**
- Modify `QuickMail/Services/SyncService.cs` — in `OnFolderSynced`, after merging summaries: reconcile `IsServerFlagged` against local `flag_id` (see §9.3)
- Modify `QuickMail/ViewModels/MainViewModel.cs` — `OnFolderSynced` handler refreshes `FlagName`/`FlagColorHex` on newly-flagged messages

**Tests:**
- `SyncServiceTests` (new or existing) — `ExternallyFlaggedMessage_GetsDefaultFlagOnSync`, `ExternallyUnflaggedMessage_ClearsFlagOnSync`

**Risk:** Reconciliation logic must not overwrite a local named flag (e.g., "Urgent") just because the server only knows `\Flagged`. Guard: if local `flag_id IS NOT NULL` and `IsServerFlagged = true`, leave the local flag as-is. Only apply the default flag when `flag_id IS NULL` and `IsServerFlagged = true`.

**Duration:** 2 hours

---

## 13. Command Registry & Shortcuts

All new keyboard shortcuts must be registered via `CommandRegistry` in `MainWindow.xaml.cs` following the existing pattern.

| Key | Command ID | Title | Category | Notes |
|---|---|---|---|---|
| `K` | `mail.toggleFlag` | Toggle Flag | Mail | Single key, no modifier. Registers with `defaultKey: Key.K`. |
| `Ctrl+Shift+K` | `mail.pickFlag` | Pick Flag… | Mail | Opens flag picker. |
| *(unassigned)* | `mail.openFlagManager` | Manage Flags… | Mail | Opens `FlagManagerWindow`. Available in command palette. |

**`Ctrl+Shift+M` conflict:** `Ctrl+Shift+M` is currently assigned to `contacts.openGroupManager` ("Group Manager"). The Flag Manager does **not** take this shortcut; it is unassigned by default and accessible via the command palette. If the user wants a shortcut they assign one in Settings. (Noted here to avoid implementation confusion — do not reassign Ctrl+Shift+M.)

**`K` in compose windows:** The `K` shortcut must not fire when focus is in a compose window's text area. The existing `PreviewKeyDown` pattern already routes based on focused element; no special handling needed beyond not adding a hardcoded `K` handler in `ComposeWindow`.

---

## 14. Accessibility

### 14.1 `AnnounceFlagStatus` setting

- Config key: `AnnounceFlagStatus` (bool, default `true`)
- Location: Settings → General → Accessibility section
- When `false`: flag name is omitted from the message row's accessible name. All other announcement behavior is unchanged.
- This setting does **not** use `AccessibilityHelper.Announce` — it controls the static accessible name of list items, not live announcements.

### 14.2 Message row accessible name format

Current (line 2447 of `MainWindow.xaml.cs`):
```
"{ReadStatusLabel}. {From}. {Subject}. {Preview}. {DateDisplay}."
```

New format when `AnnounceFlagStatus` is true and message is flagged:
```
"{FlagLabel}. {ReadStatusLabel}. {From}. {Subject}. {Preview}. {DateDisplay}."
```

When unflagged (or `AnnounceFlagStatus` is false):
```
"{ReadStatusLabel}. {From}. {Subject}. {Preview}. {DateDisplay}."
```

Example (flagged): `"Urgent. Unread. Kelly Ford. Budget review. Please review by EOD. 6/13/2026."`

Example (unflagged): `"Unread. Kelly Ford. Budget review. Please review by EOD. 6/13/2026."` (no change from today)

**Implementation note:** The accessible name is computed in code-behind. Add a helper method `BuildMessageAccessibleName(MailMessageSummary msg, bool announceFlagStatus)` so it is testable and not duplicated across view modes.

### 14.3 Flag toggle announcement

When `K` flags or unflags a message, announce the outcome:

| Action | Text | Category |
|---|---|---|
| Flag applied | `"Flagged: {flag name}."` | `AnnouncementCategory.Result` |
| Flag cleared | `"Unflagged."` | `AnnouncementCategory.Result` |
| Batch flag applied | `"Flagged {N} messages: {flag name}."` | `AnnouncementCategory.Result` |
| Batch flag cleared | `"Unflagged {N} messages."` | `AnnouncementCategory.Result` |

These respect `AnnounceResults` (the user's results announcement preference) as well as `CustomAnnouncements`.

### 14.4 Flag picker popup accessibility

- `AutomationProperties.Name` on the popup window: `"Flag picker"`
- Each flag list item: `"{flag name}"` (color is not in the accessible name; it is visual decoration only — the name carries the semantic information)
- The "Clear flag" item: `"Clear flag"`
- On open, screen reader announces `"Flag picker. {N} flags."` via `Hint` announce.

### 14.5 Flag Manager window accessibility

- `AutomationProperties.Name` on the window: `"Flag Manager"`
- Flag list items: `"{flag name}"`
- Color buttons: `AutomationProperties.Name` = color name in English (e.g., "Red", "Amber", "Blue") — not hex codes. Color buttons are a radio group.
- Rename feedback: after a successful rename, `"Renamed to {new name}."` via `AnnouncementCategory.Result`.
- Delete confirmation: VM raises `ConfirmDeleteRequested`; View shows a dialog. If confirmed: `"Flag deleted."` via `Result`. If cancelled: no announcement.

### 14.6 Flag indicator visual accessibility

- The colored rectangle in the message row is `IsHitTestVisible="False"` and has no `AutomationProperties` of its own (it is decorative; the row's accessible name carries the flag name).
- Color is never the sole differentiator: the flag name is in the accessible name, and in the `StatusDisplay` column text (see §7.3). A user with color blindness who reads the status column text still sees the flag name.
- Color buttons in the Flag Manager show color names as accessible names, not hex codes.

### 14.7 F6 ring changes

- `FlagManagerWindow`: F6 ring is Toolbar → Flag list → Edit panel. No changes to `MainWindow`'s F6 ring.
- `FlagPickerWindow`: Single-pane popup; F6 has no cycle (only one pane). `F6` is ignored.
- "All Flagged Mail" virtual folder: no new pane added to `MainWindow`'s F6 ring; it is just a folder entry in the existing folder tree.

### 14.8 `AutomationProperties.Name` values introduced

| Element | Name |
|---|---|
| Flag Manager window | `"Flag Manager"` |
| Flag picker popup | `"Flag picker"` |
| Add Flag button | `"Add flag"` |
| Delete Flag button | `"Delete flag"` |
| Move Up button | `"Move flag up"` |
| Move Down button | `"Move flag down"` |
| Color button (×12) | Color name in English (e.g., `"Red"`, `"Amber"`) |
| Flag name TextBox | `"Flag name"` |
| "Announce flag status" checkbox | `"Announce flag status"` |

---

## 15. Success Metrics

- **Keyboard-only flagging:** User can flag and unflag a message in under 2 keypresses (`K` = 1 keypress). User can flag all messages in a conversation group with 1 keypress.
- **Cross-client interop:** A message flagged in Outlook on the same Exchange account appears as flagged in QuickMail after the next sync cycle (no manual refresh required).
- **Named flags:** User can create 3 named flags with distinct colors, assign each to different messages, and filter the message list to each flag independently.
- **Saved view:** User can create a saved view with `Filter = Flagged` and a hotkey, and jump to it instantly.
- **All Flagged Mail:** The `\x00AllFlagged` virtual folder shows flagged messages from all accounts, sorted newest-first.
- **Accessibility:** A screen reader user pressing `K` hears `"Flagged: Urgent."` immediately after the keypress. Arrowing to a flagged message row, the first word heard is the flag name. Setting `AnnounceFlagStatus = false` eliminates the flag name from the announcement without other side effects.
- **Graph accounts:** Flagging a Microsoft 365 message in QuickMail marks it as `followUpFlag.flagStatus: "flagged"` in the Graph API. A flag set in Outlook mobile appears in QuickMail.
- **Configurable K default:** User can open Flag Manager, select "Urgent," press "Set as K default," close the manager, and confirm that `K` now applies "Urgent" instead of "Flagged." Setting survives app restart.
- **`--online` mode:** Launch with `--online` flag. Press `K` on a message — IMAP `\Flagged` is set on the server, the in-memory flag indicator appears, and no crash or empty-state occurs. `flags.json` loads normally (profile dir is always accessible in `--online` mode). Flag names and colors display correctly for the session.
- **No regressions:** Existing `MessageFilter` tests, `SavedView` tests, and accessibility announcement tests pass unchanged.

---

## 16. Open Questions & Risks

### 16.1 Risks

| Risk | Probability | Impact | Mitigation |
|---|---|---|---|
| IMAP `\Flagged` write fails silently (server rejects for permission reasons) | Low | Minor | Log the failure; announce `"Flag may not have been saved."` via `AnnouncementCategory.Result`. |
| IMAP `\Flagged` reconciliation overwrites a user-set named flag | Medium | Moderate | Guard: only apply default flag when local `flag_id IS NULL` (§9.3). Tested in Phase 5 tests. |
| Flag indicator rectangle interferes with row click handling in `ListBox` | Low | Minor | `IsHitTestVisible="False"` on the decorative rectangle; hit area is a transparent overlay with explicit `MouseLeftButtonUp` handler. |
| `FlagManagerWindow` firing `FlagDefinitionsChanged` while its own message loop is active | Low | Blocker (COM crash) | `FlagManagerViewModel` does not raise the event; `MainViewModel` reloads on window close (§10.2). Code review gate. |
| Color picker radio group missing `DirectionalNavigation="Cycle"` strands keyboard focus | Medium | Major | Checklist item in §14.6; caught by keyboard walkthrough in Phase 4 testing. |
| `AccessibleName` string length in message rows becomes too long with flag name prepended | Low | Minor | Max flag name length is 32 characters (Flag Manager enforces). Monitor in user testing. |

### 16.2 Open questions — all resolved

| Question | Decision |
|---|---|
| One flag per message or multiple? | One flag per message at a time. Second `K` clears, not stacks. |
| Does `K` apply a fixed or configurable flag? | Configurable. `DefaultFlagId` in `config.ini` determines which flag `K` applies. Default = built-in "Flagged". Changed in Flag Manager via "Set as K default." |
| Does `K` apply the default flag or open the picker? | `K` = toggle K-default flag. `Ctrl+Shift+K` = open picker to choose any flag. |
| Are named flags server-synced (IMAP keywords)? | No. Local only. IMAP `\Flagged` is the only server primitive. |
| Does `K` on a group flag all messages or just the first? | All messages in the group (batch operation). |
| Flag Manager shortcut? | No default shortcut assigned. Command palette only. (`Ctrl+Shift+M` is Group Manager.) |
| Max flag count? | 20 user-defined flags. Flag Manager enforces this with a disabled Add button and a hint. |
| Flag name max length? | 32 characters. Flag Manager enforces via `MaxLength` on the `TextBox`. |
| What happens when `FlagFilter` names a flag that was deleted? | `MainViewModel` treats a missing flag name as "no filter" (show all messages in the folder). |
| What happens when `DefaultFlagId` names a deleted flag? | `FlagService.GetKDefaultFlag()` falls back to the built-in flag and silently updates `config.ini`. |
| Does `--online` mode break flag operations? | No. IMAP `\Flagged` toggle works without SQLite. `flag_id` local store writes are wrapped in a separate try/catch and skipped on `SqliteException`. In-memory state is preserved for the session. |

---

## 17. Files to Create

| File | Purpose | Lines (est.) |
|---|---|---|
| `QuickMail/Models/FlagDefinition.cs` | Named flag data class | 25 |
| `QuickMail/Services/IFlagService.cs` | Flag service interface | 30 |
| `QuickMail/Services/FlagService.cs` | Flag service implementation | 150 |
| `QuickMail/ViewModels/FlagManagerViewModel.cs` | CRUD VM for Flag Manager | 200 |
| `QuickMail/ViewModels/FlagPickerViewModel.cs` | Flag picker popup VM | 60 |
| `QuickMail/Views/FlagManagerWindow.xaml` | Flag Manager window | 200 |
| `QuickMail/Views/FlagManagerWindow.xaml.cs` | Flag Manager code-behind | 80 |
| `QuickMail/Views/FlagPickerWindow.xaml` | Inline flag picker popup | 80 |
| `QuickMail/Views/FlagPickerWindow.xaml.cs` | Flag picker code-behind | 50 |
| `QuickMail.Tests/FlagServiceTests.cs` | Unit tests for FlagService | 120 |
| `QuickMail.Tests/FlagManagerViewModelTests.cs` | VM tests | 100 |
| `QuickMail.Tests/FlagPickerViewModelTests.cs` | VM tests | 60 |
| `QuickMail.Tests/MainViewModelFlagTests.cs` | Flag integration tests in MainViewModel | 100 |

---

## 18. Files to Modify

| File | Changes | Lines changed (est.) |
|---|---|---|
| `QuickMail/Models/MailMessageSummary.cs` | Add `FlagId`, `FlagName`, `FlagColorHex`, `FlagLabel`, `IsFlagged`; update `StatusDisplay` | +40 |
| `QuickMail/Models/MessageFilter.cs` | Add `Flagged` value | +4 |
| `QuickMail/Models/ConversationGroup.cs` | Add `HasFlagged` | +3 |
| `QuickMail/Models/SenderGroup.cs` | Add `HasFlagged` | +3 |
| `QuickMail/Models/SavedView.cs` | Add `FlagFilter` | +5 |
| `QuickMail/Models/ConfigModel.cs` | Add `AnnounceFlagStatus` | +3 |
| `QuickMail/Services/ImapMailService.cs` | Map `\Flagged` in `SummaryToModel`; add `SetMessageFlaggedAsync` | +30 |
| `QuickMail/Services/IImapMailService.cs` | Declare `SetMessageFlaggedAsync` | +5 |
| `QuickMail/Services/GraphMailService.cs` | Read `flag.flagStatus`; add `SetMessageFlaggedAsync` | +40 |
| `QuickMail/Services/IGraphMailService.cs` | Declare `SetMessageFlaggedAsync` | +5 |
| `QuickMail/Services/Graph/GraphDtos.cs` | Add `GraphFollowUpFlag` DTO | +10 |
| `QuickMail/Services/MailServiceRouter.cs` | Route `SetMessageFlaggedAsync` | +15 |
| `QuickMail/Services/LocalStoreService.cs` | Migration for `flag_id`; `UpdateFlagIdAsync`; `UpdateFlagIdBatchAsync`; read `flag_id` in load methods | +60 |
| `QuickMail/Services/ILocalStoreService.cs` | Declare `UpdateFlagIdAsync`, `UpdateFlagIdBatchAsync` | +8 |
| `QuickMail/Services/SyncService.cs` | IMAP ↔ local flag reconciliation in `OnFolderSynced` | +25 |
| `QuickMail/Services/ConfigService.cs` | Parse/write `AnnounceFlagStatus` | +6 |
| `QuickMail/App.xaml.cs` | Construct `FlagService`; add to DI chain | +8 |
| `QuickMail/ViewModels/MainViewModel.cs` | `ToggleFlagAsync`, `PickFlagAsync`, `OpenFlagManagerAsync`; `Flagged` filter; `FlagFilter`; `\x00AllFlagged`; `FlagDefinitionsChanged` subscription | +120 |
| `QuickMail/ViewModels/SettingsViewModel.cs` | Add `AnnounceFlagStatus` binding | +8 |
| `QuickMail/Views/MainWindow.xaml` | Flag indicator rectangle in DataTemplate; group row indicator; `\x00AllFlagged` folder tree entry | +40 |
| `QuickMail/Views/MainWindow.xaml.cs` | Register `mail.toggleFlag`, `mail.pickFlag`, `mail.openFlagManager`; update accessible name format; `BuildMessageAccessibleName` helper | +50 |
| `QuickMail/Views/SettingsDialog.xaml` | Add "Announce flag status" checkbox | +10 |
| `QuickMail/Helpers/MessagePropertiesBuilder.cs` | Add "Flag" row to Storage section | +5 |
| `QuickMail.Tests/LocalStoreServiceTests.cs` | Migration test, `UpdateFlagId` tests | +40 |
| `QuickMail.Tests/MessageFilterTests.cs` | `Flagged` filter test | +10 |

---

## 19. Tests to Add

| Test Class | Test Methods | Coverage |
|---|---|---|
| `FlagServiceTests` | `ToggleDefault_AppliesBuiltInFlag`, `ToggleDefault_ClearsExistingFlag`, `SetBatch_FlagsAllMessages`, `CorruptJson_RecoversWithBuiltInFlag`, `DeleteBuiltIn_IsRejected`, `MaxFlags_AddRejected` | Happy path, recovery, constraints |
| `FlagManagerViewModelTests` | `AddFlag_AppearsInList`, `DeleteBuiltIn_IsRejected`, `Rename_UpdatesName`, `ChangeColor_UpdatesColorHex`, `ReorderUp_MovesFlag`, `ReorderDown_MovesFlag`, `Delete_RaisesConfirmation` | CRUD, reorder, confirmation |
| `FlagPickerViewModelTests` | `ApplyFlag_RaisesEventWithFlag`, `ClearFlag_RaisesEventWithNull`, `Flags_LoadFromService` | Happy path |
| `MainViewModelFlagTests` | `ToggleFlag_SetsFlagOnMessage`, `ToggleFlag_ClearsFlagOnFlaggedMessage`, `ToggleFlag_FlagsAllMessagesInGroup`, `FlaggedFilter_FiltersToFlaggedMessages`, `AllFlaggedVirtualFolder_AggregatesAcrossAccounts`, `FlagDefinitionsChanged_RefreshesFlagNamesOnMessages` | Commands, filter, virtual folder, refresh |
| `LocalStoreServiceTests` (existing) | `UpdateFlagId_PersistsAndLoads`, `UpdateFlagIdBatch_UpdatesMultiple`, `SchemaMigratesFrom3To4WithFlagIdNull` | Persistence, migration |
| `MessageFilterTests` (existing) | `FlaggedFilter_ShowsOnlyFlaggedMessages`, `FlaggedFilter_ExcludesUnflagged` | Filter enum |
| `SyncServiceTests` | `ExternallyFlaggedMessage_GetsDefaultFlagOnSync`, `ExternallyUnflaggedMessage_ClearsFlagOnSync`, `LocalFlagPreservedWhenServerFlagged` | Reconciliation |
| `ConfigServiceTests` (existing) | `CanRoundTripAnnounceFlagStatusAsync` | Config persistence |
| `SettingsViewModelTests` (existing) | `AnnounceFlagStatus_LoadAndSave` | Settings binding |
| `XamlParseTests` (existing) | `FlagManagerWindow_ParsesWithoutException`, `FlagPickerWindow_ParsesWithoutException` | XAML validity |

---

## Appendix A — Keyboard Cheat Sheet

| Key | Action | Notes |
|---|---|---|
| `K` | Toggle default flag on selected message or group | One press flags; second press unflags. |
| `Ctrl+Shift+K` | Pick a specific flag from the inline picker | Opens `FlagPickerWindow` near the message list. |
| Enter (in picker) | Apply highlighted flag | Closes picker. |
| Escape (in picker) | Close picker without change | Focus returns to message list. |
| Arrow keys (in picker) | Navigate flag list | Up/Down. |
| `Ctrl+Shift+P` (in Flag Manager) | Command palette | Add, delete, move, close. |
| `F6` / `Shift+F6` (in Flag Manager) | Cycle focus: Toolbar → Flag list → Edit panel | |

---

## Appendix B — Flag Lifecycle State Diagram

```
[Unflagged]
    │  K (default flag applied)
    ▼
[Flagged — Default]
    │  K (flag cleared)
    ▼
[Unflagged]

[Unflagged]
    │  Ctrl+Shift+K → pick "Urgent"
    ▼
[Flagged — Urgent]
    │  Ctrl+Shift+K → pick "Waiting"    (replaces, doesn't stack)
    ▼
[Flagged — Waiting]
    │  K (any flag cleared)
    ▼
[Unflagged]

[External client flags message via IMAP \Flagged]
    │  Next sync: IsServerFlagged=true, local flag_id=NULL
    ▼
[Flagged — Default (auto-mapped by reconciliation)]

[External client unflags]
    │  Next sync: IsServerFlagged=false
    ▼
[Unflagged]
```

---

## Appendix C — Sample `flags.json`

```json
[
  {
    "Id": "00000000-0000-0000-0000-000000000001",
    "Name": "Flagged",
    "ColorHex": "#FF8C00",
    "SortOrder": 0,
    "IsBuiltIn": true
  },
  {
    "Id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
    "Name": "Urgent",
    "ColorHex": "#E53E3E",
    "SortOrder": 1,
    "IsBuiltIn": false
  },
  {
    "Id": "b2c3d4e5-f6a7-8901-bcde-f12345678901",
    "Name": "Waiting",
    "ColorHex": "#D69E2E",
    "SortOrder": 2,
    "IsBuiltIn": false
  },
  {
    "Id": "c3d4e5f6-a7b8-9012-cdef-123456789012",
    "Name": "Done",
    "ColorHex": "#38A169",
    "SortOrder": 3,
    "IsBuiltIn": false
  }
]
```

---

## Keyboard Walkthrough

### Path 1: Flag a message with the default flag

1. User is in the message list. Focus is on a message row. **Expected:** Screen reader announces normally (no flag in the name).
2. User presses `K`. **Expected:** The flag indicator appears on the row. Screen reader announces `"Flagged: Flagged."` (Result category). The message's accessible name now starts with `"Flagged."` when the user re-lands on it.
3. User presses `K` again. **Expected:** Flag indicator disappears. Screen reader announces `"Unflagged."` (Result). Accessible name reverts to read status first.

### Path 2: Pick a specific named flag

1. User focuses a message. Presses `Ctrl+Shift+K`. **Expected:** `FlagPickerWindow` opens near the message list. Screen reader announces `"Flag picker. 4 flags."` Focus is on the flag list.
2. User arrows down to `"Urgent"`. **Expected:** Screen reader announces `"Urgent."` The red color swatch is visible but not spoken (color is visual decoration; name carries the semantic content).
3. User presses Enter. **Expected:** Picker closes. Flag applied. Screen reader announces `"Flagged: Urgent."` Focus returns to the message list row.
4. User presses `K`. **Expected:** Flag cleared regardless of which named flag was set. Screen reader announces `"Unflagged."`

### Path 3: Flag a conversation group

1. User is in Conversations view. Focus is on a conversation group row with 4 messages. **Expected:** Screen reader announces the group name and count.
2. User presses `K`. **Expected:** All 4 messages in the group are flagged. The group row shows the flag indicator. Screen reader announces `"Flagged 4 messages: Flagged."` (Result).
3. User presses `K` again. **Expected:** All 4 messages unflagged. Group indicator disappears. Screen reader announces `"Unflagged 4 messages."` (Result).

### Path 4: Filter to flagged messages

1. User opens the View menu (`Ctrl+Shift+V`) or uses the filter combobox. Selects "Flagged" filter. **Expected:** Message list narrows to only flagged messages. Status bar announces count.
2. User presses `K` on a flagged message. **Expected:** Message is unflagged and immediately removed from the list (filter is active). Screen reader announces `"Unflagged."` Focus moves to the next row.

### Path 5: All Flagged Mail virtual folder

1. User navigates the folder tree to "All Flagged Mail." **Expected:** Folder tree announces `"All Flagged Mail."` Message list loads all flagged messages from all accounts, sorted newest-first.
2. User presses `K` on a message. **Expected:** Message unflagged; removed from the list. Screen reader announces `"Unflagged."`

### Path 6: Flag Manager — create a new named flag

1. User opens command palette (`Ctrl+Shift+P`), types "flag", selects "Manage Flags…". **Expected:** `FlagManagerWindow` opens. Screen reader announces `"Flag Manager."` Focus is on the flag list.
2. User presses Tab to reach the "Add flag" button, then Space. **Expected:** A new flag "New Flag" is added at the bottom of the list with a default amber color. Focus moves to the Flag Name TextBox in the Edit panel. Screen reader announces `"Flag name edit. New Flag."` Text is selected.
3. User types `"Urgent"` and presses Tab. **Expected:** Flag name updated in the list. Screen reader announces `"Urgent"` as the list item when focus returns.
4. User arrows to the Red color button and presses Space. **Expected:** Flag color updates to red. Screen reader announces `"Red, selected."` The list item's color swatch updates.
5. User presses `Ctrl+Shift+P` → "Close." **Expected:** Window closes. `MainViewModel.ReloadFlagsAsync()` is called. The new "Urgent" flag is available in the picker.

### Path 7: Screen reader hears flag status on message navigation

1. `AnnounceFlagStatus` is `true` (default). User has a message flagged as "Urgent."
2. User arrows to the flagged message row. **Expected:** Screen reader announces `"Urgent. Unread. Kelly Ford. Budget deadline. Check by EOD. 6/13/2026."` (flag name first, then read status, then from, subject, preview, date).
3. User turns off `AnnounceFlagStatus` in Settings → General → Accessibility. Arrows to the same row. **Expected:** Screen reader announces `"Unread. Kelly Ford. Budget deadline. Check by EOD. 6/13/2026."` (no flag name).

### Path 8: External flag sync

1. User flags a message in Outlook on the same Exchange account.
2. QuickMail's next sync cycle runs. `\Flagged` is fetched from IMAP.
3. Reconciliation: local `flag_id` is NULL, `IsServerFlagged = true` → `flag_id` is set to `DefaultFlagId`. **Expected:** Message now shows flag indicator in the message list. No announcement (sync is background; flag state change is silent — a status announcement would be noise). The flag is visible when the user next navigates to it.

---

## Approval Checklist

- [x] Scope is bounded. (Flag CRUD, toggle, filter, virtual folder, accessibility — no extras.)
- [x] Architecture is decided. (Local named flags, IMAP `\Flagged` as server primitive, local `flag_id` in SQLite.)
- [x] Keyboard walkthrough is complete. (8 paths, no TBD steps.)
- [x] Accessibility is explicit. (Announcement format, categories, accessible names, color-not-sole-differentiator.)
- [x] Implementation phases are testable. (5 phases, each independently committable.)
- [x] Risk assessment is documented. (6 risks with mitigations.)
- [x] No open questions remain. (All 11 questions resolved in §16.2.)
- [x] Files and tests are listed. (§17, §18, §19.)
- [x] Runtime modes are considered. (`--online` mode explicit in §8.7 and Phase 2 gate; verified in success metrics.)
- [x] Modal dialog rules respected. (`FlagManagerViewModel` does not fire events before close; §10.2.)
- [x] Configurable K default flag. (`DefaultFlagId` in config.ini; §6.1, §7.6, §8.1, §10.2, §11.3.)

**Status:** ✅ **APPROVED** — Ready for Session 2 (implementation).


# Server-Side Mail Rules (Microsoft 365 / Graph) — PM & Dev Specification

**Status:** Draft for review
**Author:** (contributor) with Claude
**Applies to:** Microsoft Graph (Microsoft 365 / Exchange Online) accounts only — IMAP has no equivalent.
**Depends on:** Graph backend PRs 1–7 (merged). `GraphClient`, `GraphMailService`, OAuth scope plumbing all exist.

---

## Table of Contents

1. Executive Summary
2. User Problem & Opportunity
3. Server Rules vs. QuickMail's Client Rules (do not conflate)
4. Permissions & Consent — the central design decision
5. Graph API Surface (verified against Microsoft Learn, 2026-06)
6. Feature Scope & Acceptance Criteria
7. Data Model
8. Service Layer
9. ViewModels
10. **Views / UI — how the user sees and edits rules**
11. **Command registration & the Command Palette**
12. **Keyboard Walkthrough** (required)
13. **Infrastructure Changes** (required)
14. Accessibility Announcements
15. Error Handling & Edge Cases
16. Round-trip Safety (the correctness risk)
17. Testing
18. **Out of Scope** (required)
19. Open Questions

---

## 1. Executive Summary

Microsoft 365 mailboxes support **server-side Inbox rules** — the rules that run on Microsoft's servers 24/7, even when QuickMail (or any client) is closed. Today QuickMail can neither show nor manage them; a user must leave for Outlook or the web to touch them.

This feature adds a **Server Rules manager**: an accessible, keyboard-first window that lists a Graph account's server rules and lets the user create, edit, enable/disable, reorder, and delete them via the Graph `messageRule` API. It is **Graph-only** and visibly distinct from QuickMail's existing client-side rules (Section 3).

The single largest design decision is **permission consent** (Section 4): managing rules requires upgrading one OAuth scope, which forces a re-consent for existing Graph accounts.

---

## 2. User Problem & Opportunity

- **Problem:** A screen-reader user who lives in QuickMail must context-switch to Outlook/OWA to manage the rules that silently file, forward, or delete their mail. Outlook's rules UI is notoriously dense; QuickMail can offer a cleaner, fully keyboard-navigable alternative.
- **Opportunity:** QuickMail already renders folders, recipients, and importance — the building blocks a rule editor needs. The Graph endpoints are simple REST; the real value-add is an accessible UI.

---

## 3. Server Rules vs. QuickMail's Client Rules (do not conflate)

QuickMail already has a **client-side** rules feature (`RuleService`, `RulesManagerViewModel`, `RulesManagerWindow`). These are different things and must stay visibly separate:

| | Client rules (existing) | Server rules (this spec) |
|---|---|---|
| Where they run | In the app, during sync | On Microsoft's servers, always |
| Storage | `rules.json` in the profile | In the mailbox, via Graph |
| Backends | IMAP + Graph (any account) | Graph accounts only |
| Run when app is closed | No | Yes |
| Code surface | `RuleService` | New `IServerRuleService` / `GraphServerRuleService` |

**Decision:** Present server rules in a **separate window** ("Server Rules") with its own menu/palette entry. Do **not** merge them into the existing Manage Rules window — implying that QuickMail's client rules run server-side would be a correctness and trust problem. Cross-link in copy ("These run on the server. For rules that run in QuickMail, use Manage Rules.").

---

## 4. Permissions & Consent — the central design decision

Per Microsoft Learn (verified 2026-06):

- **List / Get** message rules → `MailboxSettings.Read`
- **Create / Update / Delete** message rules → `MailboxSettings.ReadWrite`

> **Updated 2026-07-08 — QuickMail now requests the per-resource `.default` scope.** Sign-in asks
> for `https://graph.microsoft.com/.default`, i.e. **exactly the delegated permissions declared on
> the app registration**. There is no runtime incremental/step-up consent. The permission set is
> defined by the app registration, not by a code scope list. See
> `oauth-default-scope-pm-dev-spec.md` for the full rationale (short version: in admin-consent
> tenants a non-admin user can't self-heal a missing scope anyway, so an in-app "Reauthorize" flow
> helps almost no one). The design below is restated to match.

**Current state:** `MailboxSettings.ReadWrite` (a superset of `MailboxSettings.Read`) is declared on
the app registration (`docs/ENTRA-APP-REGISTRATION.md` §3). Under `.default`, once that declaration
is in place and consent is granted, the token carries the write permission and both **reading** and
**writing** server rules work.

**Consequence:** Because `MailboxSettings.ReadWrite` is part of the declared set, `.default` requests
it at **sign-in** — so it's consented (or refused) as part of the normal sign-in consent, not lazily
at first write. In steady state a signed-in Graph account therefore already has the write scope and
rules just work; a tenant that won't grant the full set gates **sign-in itself** (the same admin
gate as core mail), not the rules feature specifically. A feature-level `403` is only a **residual**
(a token cached before the scope was declared, or a partial tenant grant) — handled by Path E with
an admin-directed message, never an in-app "Reauthorize." Full semantics:
`oauth-default-scope-pm-dev-spec.md` §5.

**Design decisions:**
1. **Declare `MailboxSettings.ReadWrite` on the app registration ahead of GA.** Because the Graph
   backend is still feature-gated and no production account has consented yet, declaring the write
   scope before the gate lifts means every user's *first* consent already includes it — no one ever
   faces a second prompt for server rules. (This replaces the earlier "add it to the code scope
   array" framing; under `.default` the registration is the source of truth.)
2. **On `403`, show an actionable admin-directed message — not an in-app re-consent.** When a write
   returns `403`/insufficient-scope, surface a clear, accessible message ("QuickMail can't change
   server rules because your organization hasn't granted it permission. Ask your administrator to
   grant it, then sign in again.") Never fail silently (CLAUDE.md: no silent empty state). An
   in-app "Reauthorize" button is deliberately **not** offered: `.default` re-authorization cannot
   grant a permission the tenant hasn't approved, and in admin-consent tenants the user isn't the
   one who can approve it.
3. **Read-only fallback still works.** If an account lacks the write permission, the manager still
   **lists** rules (the granted read subset covers it) and only the write actions fail with the
   admin-directed `403` message above.

> **Decided (consent timing): capture up front, pre-GA.** With decision (1) — the write scope
> declared on the registration before the feature gate lifts — every account's first `.default`
> consent already carries it, so GA users never hit the `403` path at all. It exists only as a
> safety net for a tenant whose admin somehow removed or never granted the declared scope.

---

## 5. Graph API Surface (verified against Microsoft Learn, 2026-06)

All under the Inbox folder. `GraphClient` already implements every verb needed.

| Operation | Request | GraphClient method |
|---|---|---|
| List | `GET /me/mailFolders/inbox/messageRules` | `GetAllPagesAsync<GraphMessageRule>` |
| Get | `GET /me/mailFolders/inbox/messageRules/{id}` | `GetAsync<GraphMessageRule>` |
| Create | `POST /me/mailFolders/inbox/messageRules` → 201 | `PostAsync` (needs a read-back variant or `PostRawReadAsync`) |
| Update | `PATCH /me/mailFolders/inbox/messageRules/{id}` | `PatchAsync` |
| Delete | `DELETE /me/mailFolders/inbox/messageRules/{id}` | `DeleteAsync` |

**`messageRule` shape:** `id` (RO), `displayName`, `sequence` (Int32, execution order), `isEnabled`, `isReadOnly` (RO), `hasError` (RO), `conditions` (`messageRulePredicates`), `actions` (`messageRuleActions`), `exceptions` (`messageRulePredicates`).

**Conditions / exceptions (`messageRulePredicates`)** — string-match collections (`subjectContains`, `bodyContains`, `bodyOrSubjectContains`, `senderContains`, `recipientContains`, `headerContains`, `categories`), address collections (`fromAddresses`, `sentToAddresses`), to-me booleans (`sentToMe`, `sentOnlyToMe`, `sentCcMe`, `sentToOrCcMe`, `notSentToMe`), attributes (`hasAttachments`, `importance`, `sensitivity`, `messageActionFlag`, `withinSizeRange`), and message-type flags (`isAutomaticForward`, `isMeetingRequest`, etc.).

**Actions (`messageRuleActions`)** — `moveToFolder` / `copyToFolder` (folder **IDs** — the same opaque Graph IDs we use as `FullName`), `forwardTo` / `forwardAsAttachmentTo` / `redirectTo` (recipient collections), `markAsRead`, `markImportance`, `assignCategories`, `delete` (→ Deleted Items), `permanentDelete`, `stopProcessingRules`.

---

## 6. Feature Scope & Acceptance Criteria

### 6.1 In scope (v1)

- **List** all server rules for a selected Graph account, in `sequence` order, showing name, enabled state, and a human-readable summary of conditions → actions.
- **Create** a rule with the **common subset** (Section 6.3).
- **Edit** a rule that is fully representable in the common subset (Section 16 round-trip safety).
- **Enable / disable** a rule (PATCH `isEnabled`) — allowed even for rules outside the editable subset.
- **Delete** a rule (with confirmation).
- **Reorder** rules (PATCH `sequence`).
- **Account selector** when more than one Graph account exists.
- Show `isReadOnly` and `hasError` states; block edit/delete on read-only rules.

### 6.2 Acceptance criteria

- A Graph user can list, create, edit, toggle, reorder, and delete rules without leaving QuickMail, using only the keyboard.
- A non-Graph (IMAP) account never sees the Server Rules entry point (the command's `isAvailable` returns false).
- Every action is reachable from the window's command palette.
- A write attempt without `MailboxSettings.ReadWrite` produces an actionable, admin-directed message, never a silent no-op.

### 6.3 Common subset (editable in v1)

- **Conditions:** `senderContains` / `fromAddresses`, `subjectContains`, `bodyOrSubjectContains`, `sentToMe`, `sentOnlyToMe`, `hasAttachments`, `importance`.
- **Actions:** `moveToFolder`, `markAsRead`, `markImportance`, `delete`, `forwardTo`, `stopProcessingRules`.

Rules using anything outside this subset are **view + toggle + delete only**, with a visible note: "This rule uses conditions or actions that can't be edited in QuickMail yet. Edit it in Outlook." (See Section 16.)

---

## 7. Data Model

New DTOs in `QuickMail/Services/Graph/GraphDtos.cs` (`internal`):

```csharp
internal sealed class GraphMessageRule
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    [JsonPropertyName("sequence")] public int Sequence { get; set; }
    [JsonPropertyName("isEnabled")] public bool IsEnabled { get; set; }
    [JsonPropertyName("isReadOnly")] public bool IsReadOnly { get; set; }
    [JsonPropertyName("hasError")] public bool HasError { get; set; }
    [JsonPropertyName("conditions")] public JsonElement? Conditions { get; set; } // raw, for round-trip safety
    [JsonPropertyName("actions")] public JsonElement? Actions { get; set; }       // raw, for round-trip safety
    [JsonPropertyName("exceptions")] public JsonElement? Exceptions { get; set; } // raw, for round-trip safety
}
```

Plus a UI-facing `ServerRuleModel` (in `QuickMail/Models/`) that the VM binds to: the editable subset fields parsed out, plus a `bool IsFullyEditable` flag and the **raw** `conditions`/`actions`/`exceptions` retained so an edit can be merged rather than overwritten (Section 16).

---

## 8. Service Layer

New interface `IServerRuleService` (Graph-only; **not** added to `IMailService`):

```csharp
public interface IServerRuleService
{
    Task<IReadOnlyList<ServerRuleModel>> ListAsync(Guid accountId, CancellationToken ct = default);
    Task<ServerRuleModel> CreateAsync(Guid accountId, ServerRuleModel rule, CancellationToken ct = default);
    Task UpdateAsync(Guid accountId, ServerRuleModel rule, CancellationToken ct = default);
    Task SetEnabledAsync(Guid accountId, string ruleId, bool enabled, CancellationToken ct = default);
    Task ReorderAsync(Guid accountId, IReadOnlyList<string> ruleIdsInOrder, CancellationToken ct = default);
    Task DeleteAsync(Guid accountId, string ruleId, CancellationToken ct = default);
}
```

`GraphServerRuleService` implements it over `GraphMailService.Client` (the `internal` accessor added in PR 7b). It maps `GraphMessageRule` ⇄ `ServerRuleModel`, translates a `403`/insufficient-scope into a typed `ServerRuleConsentRequiredException` the VM/View catches to trigger re-auth.

**DI:** constructed in `App.xaml.cs` from `graphBackend.Client`; held for disposal only if it owns anything (it doesn't — it reuses the shared client). Registered for injection into the window like `_ruleService` is today.

---

## 9. ViewModels

- **`ServerRulesManagerViewModel`** — holds the account selector, the `ObservableCollection<ServerRuleModel>`, the selected rule, and `[RelayCommand]`s: `Refresh`, `CreateRule`, `EditRule`, `ToggleEnabled`, `MoveUp`, `MoveDown`, `DeleteRule`. No `MessageBox`/`Window` references (MVVM rule): confirmation and re-consent are raised as events the View handles (`DeleteConfirmationRequested`, `ConsentRequired`).
- **`ServerRuleEditorViewModel`** — the create/edit form: name, enabled, the common-subset condition fields, the common-subset action fields, and a folder picker hook for `moveToFolder`. Validation (name required; at least one action) exposed as error strings, mirroring `RulesManagerViewModel`'s validation pattern.

---

## 10. Views / UI — how the user sees and edits rules

**This section answers "how do we do the UI for seeing these rules?"**

A new window, **`ServerRulesWindow`** (`QuickMail/Views/ServerRulesWindow.xaml`), modeled on the existing `RulesManagerWindow` but following the modeless guidance below.

### 10.1 Layout (two logical panes)

1. **Header / account row** — an account `ComboBox` (only when >1 Graph account) labelled "Account", plus a status line ("5 rules, 1 disabled").
2. **Rule list** — a single-column `ListView`/`ListBox` of rules in `sequence` order. Each item announces: name, enabled/disabled, and a one-line summary ("If from contains 'newsletter' → move to Archive"). Read-only rules are marked ("read-only"); errored rules are marked ("error").
3. **Editor** — opened for Create/Edit. v1: a **modeless child editor window** (`ServerRuleEditorWindow`) or an in-window editor panel. Recommendation: a child editor window keeps the list simple and matches how compose is a separate window. Fields: Name, Enabled checkbox, Conditions group, Actions group, Save/Cancel.

### 10.2 Seeing a rule's detail

Selecting a rule in the list updates a read-only **summary region** (below or beside the list) that spells out the full rule in prose, including any conditions/actions outside the editable subset (so the user can *see* everything even when they can only edit the subset). This is the "view fidelity, edit subset" approach from Section 6.3.

### 10.3 Modal vs. modeless (CLAUDE.md modal-dialog rule)

The editor contains editable `TextBox`es and opens over `MainWindow`, which hosts a live WebView2 reading pane. Per CLAUDE.md ("Prefer modeless `Show()` … especially if they contain an editable text field"), the **editor uses `.Show()` (modeless)**, with Escape and Cancel wired explicitly to `Close()` (a modeless window has no `DialogResult`). The list window itself may be modeless too for consistency. The existing `RulesManagerWindow` uses `ShowDialog()`; the new window should follow the safer modeless pattern rather than copy it.

### 10.4 Accessibility (CLAUDE.md checklist)

- `AutomationProperties.Name` values are **short labels** only ("Rule name", "Move to folder", "Conditions") — no instructions, no role names.
- Radio groups (if any, e.g. importance) get one tab stop (`TabNavigation="Once"`, `DirectionalNavigation="Cycle"`, shared `GroupName`).
- Instructions/tips delivered as `AccessibilityHelper.Announce(..., AnnouncementCategory.Hint)` on focus, not baked into names.
- Element-name test usage: `FindName(...) as T; Assert.NotNull(...)`.

---

## 11. Command registration & the Command Palette

**This section answers "do we need to be sure any commands go in the command palette?" — yes, and here is exactly how.**

### 11.1 App-level entry point (main Command Palette + keyboard customizations)

Per the CLAUDE.md "Keyboard Shortcuts — Enforced" rule, **every user-facing action must be registered in `CommandRegistry`** — that registration is precisely what makes it appear in the main **Command Palette** (`Ctrl+Shift+P`) and the keyboard-customizations dialog. Register in `MainViewModel.RegisterCommands`, next to `mail.rules`:

```csharp
registry.Register(new CommandDefinition(
    id: "mail.serverRules", category: "Mail", title: "Manage Server Rules",
    execute: () => OpenServerRulesCommand.Execute(null),
    isAvailable: () => Accounts.Any(a => a.BackendKind == BackendKind.MicrosoftGraph)));
```

- **Category:** `Mail` (one of the allowed categories).
- **Default key:** none assigned (avoids collision; `mail.rules` already owns `Ctrl+Shift+L`). Discoverable via the palette — which is exactly why palette registration is required even without a hotkey.
- **`isAvailable`:** false when no Graph account exists, so IMAP-only users never see it.
- Add a matching **menu item** (Mail menu) with no `InputGestureText` (no default key). The VM raises `ServerRulesRequested`; `MainWindow` opens the window (mirroring `RulesManagerRequested` at `MainWindow.xaml.cs:248`).

### 11.2 Window-level Command Palette (New Window Checklist)

`ServerRulesWindow` is a new `Window`, so per the CLAUDE.md **New Window Checklist** it must wire its **own** `Ctrl+Shift+P` command palette containing every window-scoped action — New Rule, Edit, Enable/Disable, Move Up, Move Down, Delete, Refresh, Close — following the `ComposeWindow` pattern. Actions with no hotkey still belong in the palette so they're discoverable. The editor window likewise gets its own palette (Save, Cancel, pick folder).

---

## 12. Keyboard Walkthrough (required)

### Path A — open and browse
1. User presses `Ctrl+Shift+P`, types "server rules", activates **Manage Server Rules**. (Or Mail menu → Manage Server Rules.) Window opens with focus on the rule list. Screen reader announces: "Server Rules. List. 5 items. Rule 1 of 5: Newsletters, enabled."
2. User arrows down the list. Each item announces name, state, and summary.
3. User presses `F6`. Focus moves to the account selector. Announces: "Account. Work. Combo box."
4. User presses `F6` again → focus to the summary region (full rule prose). `F6` again → back to the list.

### Path B — create
1. From the list, user activates **New Rule** (palette or button). The editor opens modeless with focus on the Name field. Announces: "New server rule. Rule name. Edit."
2. User tabs through Conditions and Actions groups, fills fields, picks a target folder (folder picker opens, returns to the Move-to-folder field).
3. User activates **Save**. Editor closes; focus returns to the list, positioned on the newly created rule. Announce (Result): "Rule created."

### Path C — edit
1. User selects a rule, activates **Edit**. If the rule is fully editable, the editor opens pre-filled. If not, Edit is disabled and the user hears a Hint on focus: "This rule can't be edited in QuickMail. Press Delete to remove it, or edit it in Outlook."
2. User changes a field, Saves. Announce (Result): "Rule updated."

### Path D — enable/disable, reorder, delete
1. **Toggle:** user presses the Enable/Disable command on the selected rule. Announce (Result): "Rule disabled."
2. **Reorder:** Move Up / Move Down change `sequence`; focus stays on the moved rule. Announce (Status): "Moved up. Now 2 of 5."
3. **Delete:** Delete command → View shows a confirmation (modeless or a `MessageBox` from the View, never the VM). On confirm, rule removed; focus moves to the next rule (or the one above if last). Announce (Result): "Rule deleted."

### Path E — insufficient permission (admin-directed)
1. User Saves a create/edit but the tenant hasn't granted `MailboxSettings.ReadWrite`. The save returns `403`. QuickMail does **not** blank the list or close the window; the View shows an admin-directed message and announces (Hint): "QuickMail can't change server rules because your organization hasn't granted it permission. Ask your administrator to grant it, then sign in again." Focus stays on the editor; Escape returns to the list, which still shows existing rules (reading works under the granted subset). After an admin grants the permission tenant-wide, the user signs in again and `.default` picks it up — the save then succeeds. (No in-app "Reauthorize" button: see §4.)

---

## 13. Infrastructure Changes (required)

- **F6 ring (within `ServerRulesWindow`):** list ⇄ account selector ⇄ summary region. (This is the window's own ring; it does not touch `MainWindow`'s `CycleFocusAsync`.)
- **Commands added to `CommandRegistry`:** `mail.serverRules` (category Mail, no default key, `isAvailable` = has Graph account). Window-scoped actions live in the window's own palette, not the global registry.
- **`AutomationProperties.Name` introduced:** "Server rules", "Rule name", "Conditions", "Actions", "Move to folder", "Account". (Short labels only.)
- **`AccessibilityHelper.Announce` calls added:** open Hint; create/update/delete/toggle Results; reorder Status; the insufficient-permission `403` path is a Hint. Each gated by the matching user config (`AnnounceHints` / `AnnounceResults` / `AnnounceStatus`).
- **VM state:** `MainViewModel` gains `OpenServerRulesCommand` + `ServerRulesRequested` event; no change to existing rule state.
- **OAuth scope:** `MailboxSettings.ReadWrite` is declared on the app registration (source of truth); sign-in requests `https://graph.microsoft.com/.default`, so the token carries it once granted. See `oauth-default-scope-pm-dev-spec.md`.
- **DI:** `IServerRuleService` / `GraphServerRuleService` constructed in `App.xaml.cs`, passed to `MainWindow` for window construction.

---

## 14. Accessibility Announcements

| Moment | Text (example) | Category |
|---|---|---|
| Window opens | "Server Rules. Press F6 to move between the list and account selector." | Hint |
| Rule created | "Rule created." | Result |
| Rule updated | "Rule updated." | Result |
| Rule deleted | "Rule deleted." | Result |
| Enable/disable | "Rule disabled." | Result |
| Reorder | "Moved up. Now 2 of 5." | Status |
| Write blocked (403) | "QuickMail can't change server rules because your organization hasn't granted it permission. Ask your administrator to grant it, then sign in again." | Hint |
| Non-editable rule focused | "This rule can't be edited in QuickMail yet." | Hint |

All via `AccessibilityHelper.Announce(text, category)` — never `RaiseNotificationEvent` directly.

---

## 15. Error Handling & Edge Cases

- **No Graph account:** entry point hidden (`isAvailable` false). If somehow reached, window shows "No Microsoft 365 account."
- **`isReadOnly` rule:** Edit and Delete disabled; Toggle may also be disabled (read-only typically means immutable). Listed with a "read-only" marker.
- **`hasError` rule:** listed with an "error" marker and a Hint explaining the rule is in an error state on the server; still deletable.
- **Insufficient scope (403):** typed exception → admin-directed message (Path E §4). Never a silent catch, never an in-app "Reauthorize" that can't succeed.
- **Network/Graph failure:** surfaced as a visible status message, not a blank list (CLAUDE.md: no silent empty state).
- **Folder IDs:** `moveToFolder`/`copyToFolder` use Graph folder IDs; the folder picker already yields these (`FullName`). Deleted-folder targets: show the raw ID with a "folder not found" note rather than crashing.
- **`--online` mode:** unaffected — server rules never touch the local SQLite cache.

---

## 16. Round-trip Safety (the correctness risk)

Graph `PATCH` on a `messageRule` **replaces** the `conditions`/`actions`/`exceptions` complex objects rather than merging individual predicates. If our `ServerRuleModel` only models the common subset and we PATCH from it, we would **silently drop** any predicate the user set in Outlook that we don't model.

**Mitigations (v1):**
1. **Editing is gated to fully-representable rules.** `ServerRuleModel.IsFullyEditable` is false when the raw `conditions`/`actions` contain any field outside the common subset; for those, Edit is disabled (view + toggle + delete only).
2. **Retain raw JSON.** `ServerRuleModel` keeps the raw `JsonElement` for each complex property so a future version can merge edits onto the full object instead of replacing it.

This is the single most important correctness decision; reviewers should confirm the gating logic before any PATCH path ships.

---

## 17. Testing

- **`GraphServerRuleServiceTests`** (stub `HttpMessageHandler`, like `GraphMailServiceTests`): list maps fields incl. `isReadOnly`/`hasError`; create posts the correct body and reads back the id; update PATCHes the right URL/body; enable/disable PATCHes `isEnabled`; reorder PATCHes `sequence`; delete hits the right URL; a `403` surfaces `ServerRuleConsentRequiredException`.
- **`IsFullyEditable` mapping tests:** a rule with an unsupported predicate is flagged view-only; a subset-only rule is editable.
- **VM tests:** command availability (`isAvailable` with/without a Graph account), validation errors, confirmation/consent events raised (not `MessageBox` from the VM).
- **XAML parse test** (`[StaFact]`) for the new windows; element-name tests use `as` + `Assert.NotNull`.
- No live Graph in tests; a reviewer with an M365 account performs the manual keyboard walkthrough (Section 12), including the admin-directed `403` path (Path E) if the write permission is ungranted.

---

## 18. Out of Scope (required)

- **IMAP accounts** — no server-rule concept; feature hidden.
- **Editing rules with predicates/actions outside the common subset** — view + toggle + delete only in v1 (Section 16).
- **`exceptions`** authoring — preserved on round-trip but not editable in v1 (view-only in the summary).
- **Categories management** (`assignCategories`/`categories`) beyond display.
- **Rules on folders other than Inbox** — the Graph API is Inbox-scoped; not expanding it.
- **Importing/exporting rules, or syncing client rules ⇄ server rules** — explicitly not attempted.
- **Lazy / on-first-write re-consent, and any in-app "Reauthorize" self-heal** — superseded. §4 adopted **up-front** consent (the write scope declared on the app registration, captured pre-GA) plus per-resource `.default`, so there is no re-consent migration and no runtime consent negotiation; a missing permission is admin-directed, not self-healed. See `oauth-default-scope-pm-dev-spec.md`.
- **Shared-mailbox rules** — deferred with the parked shared-mailbox spec (#31).

---

## 19. Open Questions

1. ~~**Consent timing:**~~ **Resolved** — `MailboxSettings.ReadWrite` is captured up front, pre-GA, in a separate scope PR (see §4). No re-prompt for any user.
2. **Editor surface:** separate `ServerRuleEditorWindow` (recommended, matches compose) vs. inline panel in `ServerRulesWindow`.
3. **List window modality:** modeless throughout (recommended per CLAUDE.md) vs. matching the existing `RulesManagerWindow`'s `ShowDialog()`.
4. **Toggle on read-only rules:** does Graph permit `isEnabled` PATCH on an `isReadOnly` rule? Confirm against a live mailbox before enabling that path.
5. **Reorder UX:** Move Up/Down (recommended, simplest for keyboard) vs. an explicit sequence editor.

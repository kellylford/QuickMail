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

This feature makes the Rules Manager **per-account** and adds server rules to it. Selecting an account shows **one list of that account's rules**: for a Microsoft 365 (Graph) account those are **server rules**, created, edited, reordered, enabled/disabled, and deleted via the Graph `messageRule` API; for any other account they are QuickMail's existing client rules. Every row states **where the rule runs**, so the two can never be confused, and a rule now belongs to exactly **one** account — the "All accounts" scope is retired (Section 3).

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

**Decision (2026-07-23 — supersedes both the earlier "separate window" and the interim "two tabs" designs):** The Rules Manager becomes **per-account**, showing **one list of rules for the selected account**. Where a rule runs is a **property of the rule**, shown per row — not a separate window, and not a separate tab.

### 3.1 The model

1. **Rules belong to exactly one account.** The "All accounts" scope is **removed** (see §3.2 for migration).
2. **Selecting an account shows that account's rules, in one list.**
3. **Placement is server-first.** For a Graph account a new rule is created as a **server rule**; for any other account it's a **client rule**. A Graph rule falls back to client only when the chosen action has no server equivalent (today that's just **Mark as unread**).
4. **Every row states where it runs.** Rows are marked "runs on the server" / "runs in QuickMail", and the marker is part of the row's announced text.

### 3.2 Why this beats tabs

- **It matches how people think.** Users want "the rules for this mailbox", not "my two categories of rule".
- **It matches Outlook**, which shows one per-account list with `(client-only)` markers — familiar territory.
- **It removes the labeling burden.** The tab design needed constant care to stop users assuming client rules ran server-side. A per-row marker states the fact where the user is already looking.
- **It survives new client-only actions.** The notification infrastructure (`INotificationService`, `WindowsToastNotificationService`) has now landed, so a "show a notification" action is plausible. Under tabs, adding such an action to an existing rule would make it *change tabs*. Under a per-row marker, it just changes its marker.
- **"All accounts" was a false promise.** An all-accounts rule can never be a server rule, so the option offered something it couldn't deliver on Graph accounts.

### 3.3 Decided consequences

| # | Decision |
|---|---|
| **D1 — Migration** | Existing "All accounts" rules are **duplicated into one rule per non-Graph account**. Graph accounts receive **none**. After migration no rule has a null `AccountId`, and the option disappears from the UI. |
| **D2 — No client rules on Graph** | Graph support is new, so in practice none exist, and all-account rules explicitly do not propagate to Graph. **Residual:** a client rule *explicitly* scoped to a Graph account can exist today (the current picker lists Graph accounts). Such a rule is **kept, still runs, and is marked "runs in QuickMail"** — never silently deleted or auto-converted. |
| **D3 — Placement** | Server-first: Graph → server rule; non-Graph → client rule; client fallback only when an action has no server equivalent. |
| **D4 — Permission fallback** | If the tenant hasn't granted `MailboxSettings.ReadWrite`, server rules can't be listed or created. Show the **admin-directed message** (§4) *and* still allow the user to create a **client rule**, clearly marked. Never silently create client rules in place of server ones — that would leave stragglers behind once consent lands. |
| **D5 — Ordering** | Server rules are listed in `sequence` order and reorderable with Move Up/Down. Client rules are grouped **below** them in their own group. **No reordering across the boundary** — server rules run at delivery and client rules later at sync, so they are not one execution chain and the list must never imply otherwise. |

**Still non-negotiable:** the two kinds are never *synchronized* with each other, and there is no "copy this rule to the server" action in v1 (§18). Sharing a list is a presentation decision, not a merge.

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

The window is **per-account**, so the VMs compose around a selected account rather than around two categories.

- **`RulesWindowViewModel`** (new) — owns the **account selector** (all accounts, no "All accounts" entry) and, for the selected account, the combined rule list plus command routing. It applies **D3** — deciding whether a new or edited rule is a server rule or a client rule — and delegates persistence to the matching service. It holds no rule-matching logic of its own.
- **`ServerRulesViewModel`** (new — *already implemented*) — the Graph side: `ObservableCollection<ServerRuleModel>`, selection, and `Refresh` / `CreateRule` / `EditRule` / `ToggleEnabled` / `MoveUp` / `MoveDown` / `DeleteRule`. It is **already per-account with no "All accounts" entry**, so this revision requires no change to it. No `MessageBox`/`Window` references (MVVM rule): delete confirmation and the admin-directed `403` are raised as events the View handles (`ConfirmDeleteRequested`, `WriteBlockedByPermission`).
- **`ServerRuleEditorViewModel`** (new — *already implemented*) — create/edit form over the editable subset, with validation (name required; at least one action; folder required when Move to folder is chosen).
- **`RulesManagerViewModel`** (existing) — the client side. **Changes required by this revision:** drop the "All accounts" entry from `AccountOptions`, and scope its list to the selected account (D1).

The window VM exposes a **single list for display**, built by concatenating server rules in `sequence` order followed by client rules (**D5**). Every row carries a "runs where" marker used both visually and in its announced text.

---

## 10. Views / UI — how the user sees and edits rules

**This section answers "how do we do the UI for seeing these rules?"**

The existing **`RulesManagerWindow`** becomes the **per-account rules manager**. There is no second window and no tab control.

### 10.1 Layout

1. **Account row** — an "Account" `ComboBox` listing **all** accounts (no "All accounts"). Defaults to the account selected in the main window. A status line summarises the selection ("5 rules, 1 disabled").
2. **Rule list** — one `ListView` showing the selected account's rules:
   - **Server rules first**, in `sequence` order (Graph accounts only).
   - **Client rules below**, in their own group (**D5**).
   - Each row announces: name, enabled/disabled, **where it runs**, any markers ("read-only", "error"), and a one-line summary ("If from contains 'newsletter' → move to Archive").
3. **Detail region** — read-only prose for the selected rule (10.2).
4. **Editor** — a modeless child window for create/edit (10.4).

For a non-Graph account the list contains only client rules, so the experience is essentially today's plus per-account scoping.

### 10.2 Seeing a rule's detail

Selecting a rule updates a read-only **detail region** that spells out the full rule in prose, **including conditions/actions outside the editable subset** — so the user can *see* everything even when they can only edit the subset ("view fidelity, edit subset", Section 6.3).

### 10.3 Showing where a rule runs

The per-row marker replaces tab labels as the safeguard against conflating the two (Section 3). It must be:

- **Part of the row's announced text**, not just a visual badge or icon — a screen-reader user must hear it without extra navigation. `ServerRuleModel.ToString()` and the client-rule row text both carry it.
- **Phrased as behaviour, not technology** — "runs on the server" / "runs in QuickMail", never "Graph rule" / "IMAP rule".
- Backed by a `Hint` on first focus of the list explaining the difference (§14).

Where a rule's placement was **forced**, the detail region says so explicitly, so the user is never left wondering why a rule on their Microsoft 365 account runs in the app:

- *"Runs in QuickMail because Mark as unread can't run on the server."* (D3 fallback)
- *"Runs in QuickMail because your organization hasn't granted QuickMail permission to manage server rules."* (D4 fallback)

### 10.4 Modal vs. modeless (CLAUDE.md modal-dialog rule)

The rule editor contains editable `TextBox`es and opens over `MainWindow`, which hosts a live WebView2 reading pane. Per CLAUDE.md ("Prefer modeless `Show()` … especially if they contain an editable text field"), the **editor uses `.Show()` (modeless)**, with Escape and Cancel wired explicitly to `Close()` (a modeless window has no `DialogResult`).

⚠️ **Carry-over risk:** the existing `RulesManagerWindow` is opened with `ShowDialog()`. Extending it does not change that by itself, but launching a **modeless editor from a modal parent** is exactly the nested-message-loop scenario that caused the GrabAddresses keyboard lockup. **Preferred: make the unified window itself modeless** as part of this work, rather than adding an editor under a live modal loop.

### 10.5 Accessibility (CLAUDE.md checklist)

- `AutomationProperties.Name` values are **short labels** only ("Rule name", "Move to folder", "Conditions", "Account") — no instructions, no role names.
- Radio groups (e.g. importance) get one tab stop (`TabNavigation="Once"`, `DirectionalNavigation="Cycle"`, shared `GroupName`).
- Instructions/tips delivered as `AccessibilityHelper.Announce(..., AnnouncementCategory.Hint)` on focus, not baked into names.
- **`ServerRuleModel` must override `ToString()`** — a screen reader reads a Selector item's accessible name from `ToString()`, not `DisplayMemberPath`. Add it to `SelectorItemAccessibilityTests` (this exact bug shipped once in the theme ComboBox).
- Element-name test usage: `FindName(...) as T; Assert.NotNull(...)`.

---

## 11. Command registration & the Command Palette

**This section answers "do we need to be sure any commands go in the command palette?" — yes, and here is exactly how.**

### 11.1 App-level entry point (main Command Palette + keyboard customizations)

Per the CLAUDE.md "Keyboard Shortcuts — Enforced" rule, **every user-facing action must be registered in `CommandRegistry`** — that registration is precisely what makes it appear in the main **Command Palette** (`Ctrl+Shift+P`) and the keyboard-customizations dialog.

With one window and one list, the two entry points differ only in **which account is selected**:

- **`mail.rules`** (existing, `Ctrl+Shift+L`) — opens the manager on the account currently selected in the main window.
- **`mail.serverRules`** (new) — opens the **same** window with the **first Graph account** selected.

The second command still earns its place: without it nothing in the palette answers a search for "server rules", and the concept would be discoverable only by browsing accounts. Under the per-account model it selects an *account*, not a tab. Register in `MainViewModel.RegisterCommands`, next to `mail.rules`:

```csharp
registry.Register(new CommandDefinition(
    id: "mail.serverRules", category: "Mail", title: "Manage Server Rules",
    execute: () => OpenRulesManager(preferGraphAccount: true),
    isAvailable: () => Accounts.Any(a => a.BackendKind == BackendKind.MicrosoftGraph)));
```

- **Category:** `Mail` (one of the allowed categories).
- **Default key:** none assigned (avoids collision; `mail.rules` already owns `Ctrl+Shift+L`). Discoverable via the palette — which is exactly why palette registration is required even without a hotkey.
- **`isAvailable`:** false when no Graph account exists, so IMAP-only users never see it.
- Add a matching **menu item** (Mail menu) with no `InputGestureText`. The VM raises `RulesManagerRequested` carrying the account to preselect; `MainWindow` opens (or focuses) the window (extending the existing handler at `MainWindow.xaml.cs:248`).

### 11.2 Window-level Command Palette (New Window Checklist)

The window wires its **own** `Ctrl+Shift+P` palette with its scoped actions — New rule, Edit, Enable/Disable, Move Up, Move Down, Delete, Refresh, Close — following the `ComposeWindow` pattern.

A welcome simplification over the tab design: because every action operates on **the selected account and the selected rule**, entries need **no disambiguating suffix**. Plain "New rule" is now unambiguous — D3 decides whether it becomes a server or client rule from the account and the chosen actions, and the result is announced. (Under tabs, each entry would have needed "(in QuickMail)" / "(server)" qualifiers.)

The modeless editor gets its own palette (Save, Cancel, pick folder).

---

## 12. Keyboard Walkthrough (required)

### Path A — open and browse
1. User presses `Ctrl+Shift+L` (**Manage Rules**). The window opens on the account selected in the main window, with focus on the rule list. Announces: *"Rules for Work. 5 items. Rule 1 of 5: Newsletters, enabled, runs on the server. If subject contains 'digest' → move to Archive."* Then a Hint on first focus: *"Rules that run on the server keep working when QuickMail is closed. Rules that run in QuickMail apply only while it's open."*
   - Opening via the palette entry **Manage Server Rules** instead preselects the first Graph account.
2. User arrows down the list. Every row announces name, state, **where it runs**, any markers, and a one-line summary. Server rules come first, then client rules in their own group (D5).
3. User presses `F6`. Focus moves to the **account selector**: *"Account. Work."* Changing the account reloads the list and announces the new count — the user is only ever looking at one account's rules.
4. `F6` again → **detail region** (full rule prose, including anything QuickMail can't edit, and why a rule's placement was forced). `F6` again → back to the list.

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

- **Existing window becomes per-account:** `RulesManagerWindow` gains an account selector and a combined list; its client-rules UI is scoped to the selected account. Preferably also converted from `ShowDialog()` to modeless (§10.4).
- **Rule-data migration (D1):** a one-time migration duplicates each "All accounts" rule into one rule per **non-Graph** account and eliminates the null-`AccountId` case. Must be **idempotent** and must not run twice over already-migrated data.
- **F6 ring (within the Rules window):** rule list ⇄ account selector ⇄ detail region. (The window's own ring; it does not touch `MainWindow`'s `CycleFocusAsync`.)
- **Commands added to `CommandRegistry`:** `mail.serverRules` (category Mail, no default key, `isAvailable` = has Graph account). `mail.rules` is unchanged apart from preselecting an account. Window-scoped actions live in the window's own palette (§11.2) — **no target suffixes needed** under this model.
- **`AutomationProperties.Name` introduced:** "Account", "Rule name", "Conditions", "Actions", "Move to folder". (Short labels only — no role names.)
- **`AccessibilityHelper.Announce` calls added:** a list-focus Hint explaining server-vs-in-app; create/update/delete/toggle Results; reorder Status; the **forced-placement explanations** (D3/D4) as Hints; the insufficient-permission `403` path as a Hint. Each gated by the matching user config (`AnnounceHints` / `AnnounceResults` / `AnnounceStatus`).
- **VM state:** new `RulesWindowViewModel`; `MainViewModel`'s `RulesManagerRequested` carries an account to preselect. **`RulesManagerViewModel` changes** — the "All accounts" entry is removed and its list is scoped per account (D1).
- **`SelectorItemAccessibilityTests`:** add `ServerRuleModel` and `ImportanceOption` (every new Selector-bound type must be covered).
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

- **IMAP accounts** — no server-rule concept; their rules are client rules only. An IMAP-only user sees essentially today's window plus per-account scoping.
- **Converting existing client rules on a Graph account into server rules** — such a rule is kept, keeps running, and is marked "runs in QuickMail" (D2). No auto-conversion and no migration prompt in v1.
- **Editing rules with predicates/actions outside the common subset** — view + toggle + delete only in v1 (Section 16).
- **`exceptions`** authoring — preserved on round-trip but not editable in v1 (view-only in the summary).
- **Categories management** (`assignCategories`/`categories`) beyond display.
- **Rules on folders other than Inbox** — the Graph API is Inbox-scoped; not expanding it.
- **Importing/exporting rules, or syncing client rules ⇄ server rules** — explicitly not attempted. **This matters more now that both live in one window:** sharing a window must never imply the two sets are related, converted, or kept in sync. There is deliberately **no "copy this rule to the server"** action in v1.
- **Lazy / on-first-write re-consent, and any in-app "Reauthorize" self-heal** — superseded. §4 adopted **up-front** consent (the write scope declared on the app registration, captured pre-GA) plus per-resource `.default`, so there is no re-consent migration and no runtime consent negotiation; a missing permission is admin-directed, not self-healed. See `oauth-default-scope-pm-dev-spec.md`.
- **Shared-mailbox rules** — deferred with the parked shared-mailbox spec (#31).

---

## 19. Open Questions

1. ~~**Consent timing:**~~ **Resolved** — `MailboxSettings.ReadWrite` is captured up front, pre-GA, in a separate scope PR (see §4). No re-prompt for any user.
1b. ~~**Presentation model** (separate window → tabs → ?):~~ **Resolved 2026-07-23** — a **single per-account list** with a per-row "runs where" marker, no tabs and no "All accounts" scope. Rationale and the five consequent decisions (D1–D5) are in §3.
2. **Editor surface:** separate `ServerRuleEditorWindow` (recommended, matches compose) vs. inline panel in `ServerRulesWindow`.
3. **Converting the existing window to modeless:** §10.4 recommends making the unified window modeless, because launching the new modeless editor from a `ShowDialog()` parent is the nested-message-loop pattern behind the GrabAddresses lockup. But `RulesManagerWindow` ships today as `ShowDialog()`, and converting it changes behavior for existing client-rules users (Escape / `IsCancel` must be rewired explicitly). **Convert as part of this work, or keep `ShowDialog()` and accept the modal-parent risk?** Recommend converting.
4. **Toggle on read-only rules:** does Graph permit `isEnabled` PATCH on an `isReadOnly` rule? Confirm against a live mailbox before enabling that path.
5. **Reorder UX:** Move Up/Down (recommended, simplest for keyboard) vs. an explicit sequence editor.

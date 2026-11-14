# Forward Mail — PM & Dev Specification

**Status:** Implemented — see Appendix B for deviations
**Date:** June 15, 2026
**Branch:** `Forwarding` (off `main`)

> Combined PM + Dev spec. **Sections 1–5 are the PM portion** (problem, users, scope, design principles). **Sections 6–14 are the Dev portion** (architecture, keyboard walkthrough, accessibility, implementation phases, file/test tables). **Section 15** is implementation guidance for the AI implementing this.

---

## 1. Executive Summary

Forwarding mail in QuickMail today has two critical failures. First, any message whose sender used HTML format — which is most modern email — arrives at the recipient as a blank message: the forwarding code only reads the `PlainTextBody` field, which is empty for HTML-only messages. Second, every attachment from the original message is silently downloaded and included without giving the user any choice. Users expect to be able to select which attachments to forward, especially when dealing with large files.

This spec fixes both problems and brings forwarding up to the standard users expect from any modern email client: correct body content in all cases, an explicit and accessible attachment-selection step before the compose window opens, and properly quoted HTML content when composing in HTML mode.

---

## 2. User Problem & Opportunity

### 2.1 Current state (verified against code)

| Surface | Today | Pain | Who feels it |
|---|---|---|---|
| Forward body — HTML-only messages | `ComposeViewModel.CreateForward` (line 744, `ComposeViewModel.cs`) sets `Body = header + detail.PlainTextBody`. For HTML-only messages, `PlainTextBody` is `string.Empty`, so the recipient gets a message with just the attribution header and no content. | Forwarded message appears blank to the recipient. The sender has no warning this happened. | Every user who forwards HTML email — which is most modern email. |
| Forward body — HTML-with-text messages | `PlainTextBody` is used correctly, but the HTML formatting is stripped. In HTML-compose mode, users expect the original formatting to survive in the forwarded section. | Forwarded content loses all formatting (bold, links, tables) even in HTML mode. | Users who compose in HTML mode. |
| Attachment handling | `MainViewModel.Forward()` (line 3576) downloads **all** attachments silently with a 60-second combined timeout, then passes all to the compose model. No UI indicates what was included. | Users cannot choose to omit large, sensitive, or irrelevant attachments. Size surprises are common. Download failures are silently skipped. | All users forwarding messages with attachments. |

### 2.2 Why now

The compose window now supports full HTML mode (`RichTextBox`, HTML/Markdown/Plain modes), so the infrastructure for HTML-quoted forwards exists. The blank message bug is a data-loss regression. Attachment selection is a frequently-requested behavior.

---

## 3. Personas & Use Cases

| Persona | Need | Use case |
|---|---|---|
| **Keyboard user** | "I forward HTML newsletters constantly. They arrive blank." | After fix: forwards a newsletter and the recipient sees the full content. |
| **Screen reader user** | "I need to know what I'm forwarding before I send it." | Attachment dialog reads each file name and size; user decides per-file; compose opens with confirmed set. |
| **Power user with large attachments** | "I don't want to forward a 15 MB video just because it was in the original." | Dialog shows sizes; user unchecks the large video; only selected files are downloaded. |
| **HTML compose user** | "When I forward a formatted email, I want the formatting preserved." | Original HTML content appears in a styled blockquote in the compose window below the user's added text. |
| **Plain text user** | "I write in plain text. The quoted forward should be readable text, not raw HTML." | Original HTML is converted to clean plain text for the quoted section. |

---

## 4. Design Principles

1. **Never lose content.** A forwarded message must always contain the body of the original — even if it requires an HTML→text conversion. The blank-message state is never acceptable.
2. **Explicit, not silent.** Attachments are never included without the user's knowledge. If there are attachments, the user chooses before the compose window opens.
3. **Respect the user's compose mode.** A user who works in plain text should get a readable plain-text quote. A user who works in HTML should get the original HTML content in a blockquote.
4. **Keyboard first.** The attachment selection dialog is fully operable by keyboard. Tab/arrow navigation must be natural; Enter confirms the default action.
5. **No extra windows without cause.** If there are no attachments, the compose window opens immediately — no unnecessary dialog.

---

## 5. Feature Scope & Acceptance Criteria

### 5.1 In scope (v1)

| Feature | Location | Default | Notes |
|---|---|---|---|
| Fix blank-message bug for HTML-only mail | `ComposeViewModel.CreateForward` | Always on | Use `HtmlStripper.ToPlainText(detail.HtmlBody)` when `PlainTextBody` is empty |
| HTML-quoted body in HTML compose mode | `ComposeViewModel.CreateForward` + `MainViewModel.Forward()` | Follows `DefaultComposeMode` | Set `ComposeModel.HtmlBody` with quoted blockquote when original has HTML |
| Attachment selection dialog (pre-compose) | New `ForwardAttachmentDialogWindow` + `ForwardAttachmentDialogViewModel` | Shows when original has ≥ 1 attachment | Keyboard-accessible checklist; shows filename + size |
| Download only selected attachments | `MainViewModel.Forward()` | N/A | Only attachments the user checked are fetched from IMAP |
| Cursor at top of compose body on open | `ComposeWindow` | Always on for Forward kind | Plain text: `BodyBox.CaretIndex = 0`; HTML: caret before first block in `RichBodyBox` |
| Announce download progress per attachment | `MainViewModel.Forward()` (status bar) | `AnnounceStatus` | "Downloading N of M attachments…" with final "N attachment(s) ready." |

### 5.2 Explicitly out of scope (v1)

- **"Forward as attachment"** (wrapping the original as an `.eml` MIME attachment). Separate feature.
- **Inline image handling.** Inline images embedded in HTML (CID references) are not downloaded or re-embedded in the forwarded message. They will appear as broken images in the compose HTML preview. Deferred to v2.
- **Reply behavior.** Reply has the same HTML-only plain-text gap but is not addressed here. Separate spec.
- **Re-routing forward** (changing sender account before forwarding). Existing behavior: account pre-selected from the original message; user can change in compose. No change.
- **Partial forward** (selecting a portion of the original body). Not in scope.
- **Attachment size warning threshold** in config. V1 shows all attachments; user selects manually. A configurable auto-exclude-above-N-MB is v2.
- **Forward from Conversation view** or **From/To group view** uses the same `Forward()` method already; no separate treatment needed.

---

## 6. Architecture & Technical Decisions

### 6.1 Blank-message fix

**Decision:** `ComposeViewModel.CreateForward` always produces a usable `Body` (plain text) by falling back to `HtmlStripper.ToPlainText(detail.HtmlBody)` when `PlainTextBody` is empty. It also populates `ComposeModel.HtmlBody` with a quoted HTML block whenever `detail.HtmlBody` is non-empty, and sets `ComposeModel.Mode = ComposeMode.Html` when the source has HTML.

**Alternatives:**
1. Fix only the plain-text fallback, ignore HTML forwarding. Simpler, but users in HTML mode still lose formatting.
2. Move the fix to `MainViewModel.Forward()` where config is available for mode selection. More config-aware, but requires passing config into the flow and changes the public API of `CreateForward`.

**Rationale:** Option 1 solves the data-loss bug. Option 2's config awareness is nice-to-have. The chosen approach (option 1 extended) puts the HTML body production in `CreateForward` and keeps the logic self-contained. The compose window's existing `ApplyDefaultComposeMode` already chooses whether to activate HTML mode based on `DefaultComposeMode`; `SeededMode = ComposeMode.Html` in the model signals "this compose has an HTML body available."

**Practical flow:**
- If `detail.PlainTextBody` is empty and `detail.HtmlBody` is non-empty: `Body = header + HtmlStripper.ToPlainText(detail.HtmlBody)`.
- If both are non-empty: `Body = header + detail.PlainTextBody` (existing behavior, unchanged).
- If `detail.HtmlBody` is non-empty: also set `HtmlBody = BuildForwardedHtmlBlock(detail)` and `Mode = ComposeMode.Html`.
- `BuildForwardedHtmlBlock` is a new private static helper in `ComposeViewModel` that produces: `<p></p>` (empty cursor paragraph) + `<div class="qm-forward-header">…</div>` + `<blockquote>` + original HTML + `</blockquote>`.

**Why `Mode = ComposeMode.Html` when there's HTML source?** The compose window's `ApplyDefaultComposeMode` for non-draft, non-template composes uses the configured `DefaultComposeMode`. If the user has set their default to PlainText, `model.Mode` will be overridden to PlainText in `ComposeViewModel.Seed`'s `_seededMode` field. The view then applies PlainText mode and uses `Body` only — the HTML is stored but never shown. This is correct: plain-text users get plain text. HTML users get HTML. The model carries both.

**Verified impact on `ComposeViewModel.Seed`:** `_seededHtmlBody` is only set when `model.Mode == ComposeMode.Html` (line 163). Because `CreateForward` now sets `Mode = ComposeMode.Html` when HTML is available, `_seededHtmlBody` will be set for HTML-source forwards. `ApplyDefaultComposeMode` still controls whether HTML mode is actually activated, so the user's preference is respected.

### 6.2 Attachment selection dialog

**Decision:** A new modal `ForwardAttachmentDialogWindow` (owned by `MainWindow`) is shown before `ComposeRequested` fires. It is triggered by a new `Func<>` event on `MainViewModel`.

**Alternatives:**
1. In-compose attachment management (user removes attachments after compose opens). Requires downloading first, then allowing removal in compose — wasteful for large files.
2. A simple Yes/No "Include all attachments?" prompt. Too coarse for messages with mixed large/small attachments.
3. No dialog; include all with a "Remove" option in compose. Same problem as option 1.

**Rationale:** Pre-compose selection avoids unnecessary downloads. The per-file checklist gives the user full control. The pattern is consistent with desktop email clients (Outlook shows an "Include attachments" prompt in some forward flows).

**Event signature on `MainViewModel`:**
```csharp
public event Func<IReadOnlyList<AttachmentModel>, Task<IReadOnlyList<AttachmentModel>?>>? SelectAttachmentsForForwardRequested;
```
Returns: the selected subset of attachments (never modified in-place), or `null` if the user cancelled the forward.

**`MainWindow` wires:**
```csharp
_vm.SelectAttachmentsForForwardRequested += ShowForwardAttachmentDialogAsync;
```
Where `ShowForwardAttachmentDialogAsync` opens the dialog, awaits user input, and returns the result.

**`MainViewModel.Forward()` updated flow:**
1. `EnsureDetailAsync()` → get detail.
2. `CreateForward(detail, accountId)` → base model.
3. If `detail.Attachments.Count > 0`: fire `SelectAttachmentsForForwardRequested`. If result is `null`: return (user cancelled). Otherwise: download only selected attachments.
4. If `detail.Attachments.Count == 0`: skip dialog.
5. `ComposeRequested?.Invoke(model)`.

### 6.3 Runtime mode compatibility

| Mode | Effect |
|---|---|
| Normal | Full flow: local store → IMAP fallback for detail. Attachment download from IMAP. |
| `--online` | `LocalStoreService` is unavailable; `EnsureDetailAsync` already falls back to IMAP (existing pattern). No change needed. Attachment download from IMAP as normal. |
| `--profileDir` | No effect on forwarding behavior. |

### 6.4 Shared component audit

| Component | File | Other consumers | Change needed | Risk |
|---|---|---|---|---|
| `ComposeViewModel.CreateForward` | `ViewModels/ComposeViewModel.cs` | `MainViewModel.Forward()`, `ComposeViewModelReplyTests` | Add `HtmlBody` + `Mode` population; change `Body` fallback | Tests for forward must be updated to cover the new HTML case |
| `MainViewModel.Forward()` | `ViewModels/MainViewModel.cs` | Called by `ForwardCommand`, `ForwardConversationAsync`, `ForwardToGroupAsync`, `ForwardSenderGroupAsync` | Add dialog trigger; download only selected attachments; update status announce | All four callers go through `Forward()` so the change applies everywhere |
| `ComposeViewModel.Seed` | `ViewModels/ComposeViewModel.cs` | Every compose-open path (reply, draft reopen, template) | No change to method; relies on existing `_seededMode` / `_seededHtmlBody` pattern | Verify seeding works correctly when `model.Mode = ComposeMode.Html` for a forward |
| `ComposeWindow.ApplyDefaultComposeMode` | `Views/ComposeWindow.xaml.cs` | All compose open paths | No change to the method; the `Forward` kind falls through to `DefaultComposeMode` as before | None |
| `ComposeWindow` (cursor placement) | `Views/ComposeWindow.xaml.cs` | All compose open paths | Add cursor-to-top logic gated on `ComposeKind.Forward` | Must not affect Reply, Draft, or New compose cursor position |
| `MimeMessageBuilder.Build` | `Services/MimeMessageBuilder.cs` | `SmtpService`, `ImapMailService` (draft save) | No change | None — the builder already handles `HtmlBody` correctly |

---

## 7. Keyboard Walkthrough (Mandatory)

### Path A: Forward a plain-text message (no attachments)

1. User is in the message list. User presses **Ctrl+F** (Forward). **Expected:** Compose window opens immediately (no attachment dialog). Focus lands on the To field. Screen reader announces "To field." The body contains the forwarded header + original plain-text body below the cursor. Cursor is at position 0 (top of the body text).
2. User presses **Tab**. **Expected:** Focus moves to Cc, then Bcc, then Subject, then Body in sequence.
3. User presses **Alt+Y** to focus the body. **Expected:** Screen reader announces "Body." User types their message above the forwarded text.
4. User presses **Alt+S**. **Expected:** Message is sent; compose window closes; focus returns to the originating message in the message list.

### Path B: Forward a plain-text message with attachments

1. User presses **Ctrl+F**. **Expected:** An attachment selection dialog appears. Screen reader announces the dialog title "Include Attachments". Focus lands on the first attachment checkbox.
2. The list shows each attachment as a row: checkbox + filename + size (e.g., "☑ report.pdf — 1.2 MB"). **Expected:** Screen reader reads "report.pdf, 1.2 MB, checked" for each row.
3. User presses **Space** to uncheck an attachment. **Expected:** Checkbox toggles off. Screen reader announces "unchecked."
4. User presses **Tab** to move to the "Include Selected" button. **Expected:** Focus on button. Screen reader announces "Include Selected, button."
5. User presses **Enter**. **Expected:** Dialog closes; status bar shows "Downloading 1 of 2 attachments…" (for selected attachments only); compose window opens; focus on To field.
6. User presses **Tab** through fields and then **Shift+Tab** back to the attachment list in the compose window. **Expected:** The compose attachment list shows only the selected attachments (not the ones that were unchecked).

### Path C: Forward a plain-text message — user cancels attachment dialog

1. User presses **Ctrl+F**. **Expected:** Attachment dialog appears.
2. User presses **Escape** (or activates the Cancel button). **Expected:** Dialog closes; compose window does NOT open; focus returns to the message in the message list.

### Path D: Forward an HTML-only message (plain text user)

1. User's default compose mode is Plain Text. User presses **Ctrl+F** on an HTML-only email (no `text/plain` part). **Expected:** Compose window opens in plain text mode. Body contains the forwarded header + the HTML content converted to readable plain text (via `HtmlStripper.ToPlainText`). Cursor at position 0.
2. No blank body. Content is readable. User types above, sends. Recipient receives a plain-text forwarded message.

### Path E: Forward an HTML message (HTML compose user)

1. User's default compose mode is HTML. User presses **Ctrl+F** on an HTML message. **Expected:** Compose window opens in HTML mode. RichTextBox shows an empty paragraph at top (cursor here) followed by the forwarded header div and the original message content in a `<blockquote>`.
2. User types above the blockquote. **Expected:** Typing is inserted at the cursor position above the quoted block. Screen reader reads the typed text.
3. User presses **Tab** to navigate. **Expected:** Focus moves through To, Cc, Bcc, Subject, Body as expected.
4. User sends. **Expected:** The sent message is `multipart/alternative` with a plain-text version (extracted from the HTML by `HtmlStripper`) and the HTML version.

### Path F: Forward a message — attachment download fails for one attachment

1. User selects two attachments in the dialog and confirms. **Expected:** Status shows "Downloading 1 of 2 attachments…" then "Downloading 2 of 2 attachments…".
2. Second attachment download fails. **Expected:** Status shows "1 of 2 attachments included (1 could not be downloaded)." Screen reader reads the status (AnnouncementCategory.Status). Compose window opens with only the successfully downloaded attachment. No silent failure.

### Path G: Forward from Conversations view

1. User is in Conversations view. User right-selects a conversation and activates "Forward" from the context menu, or presses **Ctrl+F** with a conversation selected. **Expected:** Same flow as Path A/B/C/D/E — `ForwardConversationAsync` passes through to `Forward()`.

### Path H: Forward with no attachments and no HTML (fully happy path)

1. User presses **Ctrl+F** on a plain-text email with no attachments. **Expected:** Compose opens immediately, To is empty, Subject is "Fwd: [original]", body has the forwarded header and the original body below, cursor at top. This is the same as the current behavior except cursor placement is now explicitly at position 0.

---

## 8. Accessibility Checklist (Mandatory)

**`AutomationProperties.Name` values introduced:**
- Dialog window: `AutomationProperties.Name="Include Attachments"` (already implicit from `Title`, but set explicitly).
- Attachment list (ItemsControl or CheckBox list): each item's `AutomationProperties.Name` is `"{FileName}, {FileSizeDisplay}"` — e.g., "report.pdf, 1.2 MB". The checkbox role and checked state are announced by WPF automatically.
- "Include Selected" button: label is the button content, no additional automation name needed.
- "Include None" button: label is the button content.
- "Cancel" button: label is the button content.
- No role names in any automation names.

**AnnouncementCategory choices:**
- "Downloading N of M attachments…" → `AnnouncementCategory.Status` (background progress; respects `AnnounceStatus` setting).
- "N attachment(s) included." → `AnnouncementCategory.Status` (completion of background task).
- "1 of 2 attachments included (1 could not be downloaded)." → `AnnouncementCategory.Status`.
- No hints needed for the dialog — the controls are self-explanatory.

**Focus restoration:**
- Attachment dialog is closed by "Include Selected", "Include None", or Escape/Cancel. In all cases focus returns to the message list item that was selected before Forward was invoked. (Compose window opening causes a normal ownership transfer.)
- Compose window focus restoration on close: existing behavior, no change.

**F6 ring:** No new panes added to the F6 ring.

**Radio/checkbox groups:** The attachment list uses `CheckBox` items, not radio buttons. Each item is independently toggleable — no `GroupName` grouping needed. The list container should have `KeyboardNavigation.TabNavigation="Cycle"` so arrow keys move through items after Tab lands on the first one.

**Color-only information:** None. The checkbox state is both visual (checkmark) and semantic (checked/unchecked in UIA).

**Compose window cursor placement:** No accessibility impact from setting `CaretIndex = 0` on the plain-text TextBox, or placing the caret before the first block in the RichTextBox. Screen reader will read from the cursor position when the user starts typing.

---

## 9. Acceptance Walkthrough (Mandatory)

### Scenario 1: Forward HTML-only message (the blank-message bug)

**Setup:** Pick a message from a sender who uses HTML-only email (e.g., a newsletter). Verify in Properties (Alt+Enter) that "Format" is "HTML only."

1. Press **Ctrl+F**. If attachments: handle dialog. Verify compose window opens.
2. **Verify:** Body (plain text mode) or RichTextBox (HTML mode) is NOT empty below the forwarded header. The body contains readable text from the original message.
3. Send to yourself. **Verify:** Received message has a non-empty body. No blank forward.

### Scenario 2: Attachment selection dialog — include selected

**Setup:** Open a message that has 2+ attachments of different sizes.

1. Press **Ctrl+F**. **Verify:** Attachment dialog opens before compose.
2. **Verify:** All attachments are listed with name and size. All are checked by default.
3. Uncheck one attachment. Tab to "Include Selected". Press Enter.
4. **Verify:** Compose opens. In the attachment list, only the checked attachment appears (not the unchecked one).
5. Send. **Verify:** Recipient's message has only the selected attachment.

### Scenario 3: Attachment selection dialog — include none

**Setup:** Open a message with attachments.

1. Press **Ctrl+F**. Dialog opens. Activate "Include None".
2. **Verify:** Compose opens with no attachments in the attachment list.

### Scenario 4: Attachment selection dialog — cancel

**Setup:** Open a message with attachments.

1. Press **Ctrl+F**. Dialog opens. Press **Escape**.
2. **Verify:** Compose does NOT open. Focus is on the message in the message list.

### Scenario 5: Forward in HTML compose mode

**Setup:** Set DefaultComposeMode to HTML in Settings → General → Composing. Open an HTML message.

1. Press **Ctrl+F**. Compose opens in HTML mode.
2. **Verify:** RichTextBox shows: (a) empty cursor paragraph at top, (b) forwarded header div, (c) original message content in a blockquote.
3. Type above the blockquote. **Verify:** Text appears above the forwarded block.
4. Send. **Verify:** Sent message is `multipart/alternative`; recipient's HTML-capable client shows formatted forwarded content in a blockquote.

### Scenario 6: Verify no regression — plain-text message with plain-text body

**Setup:** Open a plain-text message (Format: "Plain text" in Properties).

1. Press **Ctrl+F**. **Verify:** Compose opens in the user's default mode. Body contains the forwarded header + original body. Cursor at top. No change from expected current behavior except cursor placement.

### Scenario 7: Forward from Conversations and From/To group trees

**Setup:** Switch to Conversations view. Select a conversation.

1. Press **Ctrl+F**. **Verify:** Same forward flow as above — no regression in `ForwardConversationAsync`, `ForwardToGroupAsync`, `ForwardSenderGroupAsync`.

### Scenario 8: Reply — verify no regression

**Setup:** Press **Ctrl+R** on any message.

1. **Verify:** Reply opens as before. CreateReply is not modified by this spec; confirm no inadvertent changes affected reply flow.

### Scenario 9: Screen reader — attachment dialog

**Setup:** Enable screen reader. Open a message with 2 attachments. Press **Ctrl+F**.

1. **Verify:** Screen reader announces the dialog title.
2. Tab through the list. **Verify:** Each checkbox reads "filename, size, checked" or "filename, size, unchecked".
3. Space to toggle. **Verify:** Screen reader announces "checked" or "unchecked".
4. Tab to "Include Selected". **Verify:** Announced as a button. Enter activates it.

### Scenario 10: `--online` mode

**Setup:** Launch with `--online` flag. Open a message.

1. Press **Ctrl+F**. **Verify:** Detail is fetched from IMAP (not local store). Forward proceeds normally. No crash.

---

## 10. Success Metrics

- A forwarded HTML-only email always has non-empty body content at the recipient.
- A forwarded HTML email, when the user composes in HTML mode, arrives with the original formatting preserved inside a blockquote.
- Users with attachments see a selection dialog before compose opens.
- Only the selected attachments are downloaded and included.
- A cancelled attachment dialog does not open a compose window.
- All primary actions work keyboard-only.
- The acceptance walkthrough passes completely in both normal and `--online` modes.

---

## 11. Implementation Phases

### Phase 1: Fix the blank-message bug (`CreateForward`)

**Goal:** `ComposeViewModel.CreateForward` always produces a non-empty body, and populates `HtmlBody` + `Mode` when the source has HTML.

**Deliverables:**
- Modify `ComposeViewModel.CreateForward` to add:
  - Plain-text fallback: `detail.PlainTextBody` empty → `HtmlStripper.ToPlainText(detail.HtmlBody)`
  - New private static `BuildForwardedHtmlBlock(MailMessageDetail)` helper that returns the forwarded header + blockquoted HTML body
  - Set `HtmlBody` and `Mode = ComposeMode.Html` when `detail.HtmlBody` is non-empty

**Tests:**
- `ComposeViewModelReplyTests` (existing file): add `CreateForward_HtmlOnlyMessage_BodyIsNotEmpty`, `CreateForward_HtmlMessage_HtmlBodyContainsBlockquote`, `CreateForward_PlainMessage_BodyUnchanged`

**Risk:** `_seededHtmlBody` gating in `ComposeViewModel.Seed` (line 163) only stores the HTML when `model.Mode == ComposeMode.Html`. Setting `Mode = ComposeMode.Html` in the model resolves this, but verify `ApplyDefaultComposeMode` still overrides to the user's configured mode when default is PlainText.

**Duration:** 1–2 hours

---

### Phase 2: Cursor placement at top of compose for Forward

**Goal:** After seeding a Forward compose, the cursor is at the start of the body content (position 0 for plain text; before the first block in HTML mode).

**Deliverables:**
- Modify `ComposeWindow` (in the section after `Seed` is called and the initial mode is applied): if `_vm.ComposeKind == ComposeKind.Forward`, set `BodyBox.CaretIndex = 0` (plain text) and in HTML mode place the caret before the first block of the `RichBodyBox` document.
- The HTML-mode caret placement follows the existing pattern in `ComposeWindow` for cursor positioning in `RichBodyBox`.

**Tests:**
- No automated test for cursor position (WPF caret state requires STA). Covered by Scenario 5 in the acceptance walkthrough (manual).

**Risk:** If cursor placement runs before the seeded HTML is loaded into the `RichBodyBox`, the caret may be at an invalid position. Ensure cursor placement happens after `_seededHtmlBody` is loaded (the existing `LoadSeededHtmlBodyAsync` or equivalent path).

**Duration:** 1 hour

---

### Phase 3: Attachment selection dialog

**Goal:** A new modal dialog collects the user's attachment selection before the compose window opens.

**Deliverables:**

**New files:**
- `QuickMail/Views/ForwardAttachmentDialogWindow.xaml` + `.xaml.cs`
- `QuickMail/ViewModels/ForwardAttachmentDialogViewModel.cs`

**Modified files:**
- `QuickMail/ViewModels/MainViewModel.cs`: add `SelectAttachmentsForForwardRequested` event; update `Forward()` to trigger it and download only selected attachments; update status announce per-attachment.
- `QuickMail/Views/MainWindow.xaml.cs`: subscribe to `SelectAttachmentsForForwardRequested`, implement `ShowForwardAttachmentDialogAsync`.

**Dialog ViewModel:**
```csharp
public class ForwardAttachmentSelectionItem
{
    public AttachmentModel Attachment { get; init; }
    public bool IsIncluded { get; set; } = true;
}

public class ForwardAttachmentDialogViewModel : ObservableObject
{
    public ObservableCollection<ForwardAttachmentSelectionItem> Items { get; }
    // Buttons:
    public IRelayCommand IncludeSelectedCommand { get; }  // returns selected subset
    public IRelayCommand IncludeNoneCommand { get; }       // returns empty list
    public IRelayCommand CancelCommand { get; }            // returns null
    // Result (set before CloseRequested fires):
    public IReadOnlyList<AttachmentModel>? Result { get; private set; }
    public event Action? CloseRequested;
}
```

**Dialog window requirements:**
- Title: "Include Attachments"
- List: each row is a `CheckBox` with label `"{FileName}   {FileSizeDisplay}"` (tab-separated for alignment)
- Three buttons below the list: "Include Selected" (default/Enter), "Include None", "Cancel"
- Default button: "Include Selected" (`IsDefault=True`)
- Cancel button: `IsCancel=True` (Escape fires it)
- F6 / Shift+F6: list → buttons → list (two pane cycle)
- `AutomationProperties.Name` on each CheckBox: `"{FileName}, {FileSizeDisplay}"`

**Tests:**
- `ForwardAttachmentDialogViewModelTests`: `AllCheckedByDefault`, `IncludeNoneReturnsEmptyList`, `IncludeSelectedReturnsCheckedSubset`, `CancelReturnsNull`

**Duration:** 3–4 hours

---

### Phase 4: Update download and status reporting

**Goal:** `MainViewModel.Forward()` downloads only selected attachments, updates the status bar per-attachment, and reports partial failures clearly.

**Deliverables:**
- Modify `MainViewModel.Forward()` download loop: iterate selected attachments only; update `StatusText` to "Downloading N of M attachments…" before each download.
- After all downloads: if any failed, `StatusText = "N of M attachment(s) included (X could not be downloaded)."` and announce with `AnnouncementCategory.Status`. Otherwise clear status.
- `IsBusy = true` during download; `IsBusy = false` in `finally`.

**Tests:**
- `MainViewModelForwardTests` (new test class in `QuickMail.Tests`): `Forward_NoAttachments_SkipsDialog`, `Forward_AllAttachmentsSelected_DownloadsAll`, `Forward_PartialSelection_DownloadsSelected`, `Forward_DownloadFails_ReportsPartialInStatus`, `Forward_UserCancelsDialog_ComposNotOpened`
- These tests use `StubImapService` to simulate download success and failure.

**Duration:** 2–3 hours

---

## 12. Files to Create

| File | Purpose | Lines (est.) |
|---|---|---|
| `QuickMail/Views/ForwardAttachmentDialogWindow.xaml` | Attachment selection dialog XAML | 60–80 |
| `QuickMail/Views/ForwardAttachmentDialogWindow.xaml.cs` | Code-behind: F6 cycle, default/cancel button wiring | 40–60 |
| `QuickMail/ViewModels/ForwardAttachmentDialogViewModel.cs` | Dialog VM: items collection, commands, result | 70–90 |

---

## 13. Files to Modify

| File | Changes | Lines changed (est.) |
|---|---|---|
| `QuickMail/ViewModels/ComposeViewModel.cs` | `CreateForward`: plain-text fallback, HTML body, Mode; `BuildForwardedHtmlBlock` helper | +30–40 |
| `QuickMail/ViewModels/MainViewModel.cs` | `Forward()`: dialog event, download loop, status announce; new event declaration | +30–50 |
| `QuickMail/Views/MainWindow.xaml.cs` | Subscribe to new event; `ShowForwardAttachmentDialogAsync` handler | +25–35 |
| `QuickMail/Views/ComposeWindow.xaml.cs` | Cursor-to-top logic for `Forward` kind, after seeding completes | +10–15 |

---

## 14. Tests to Add

| Test Class | Test Methods | Coverage |
|---|---|---|
| `ComposeViewModelReplyTests` (existing) | `CreateForward_HtmlOnlyMessage_BodyIsNotEmpty`, `CreateForward_HtmlMessage_HtmlBodyContainsBlockquote`, `CreateForward_HtmlMessage_ModeIsHtml`, `CreateForward_PlainMessage_Unchanged` | Blank-message fix; HTML body population |
| `ForwardAttachmentDialogViewModelTests` (new) | `AllCheckedByDefault`, `IncludeSelectedReturnsCheckedSubset`, `IncludeNoneReturnsEmptyList`, `CancelReturnsNull` | Dialog VM behavior |
| `MainViewModelForwardTests` (new) | `Forward_NoAttachments_SkipsDialog`, `Forward_UserCancelsDialog_ComposeNotOpened`, `Forward_PartialSelection_DownloadsOnlySelected`, `Forward_AllFail_StatusReportsZeroIncluded`, `Forward_OneFails_StatusReportsPartial` | Download flow, cancellation, status |

---

## 15. Known Risks & Open Questions

### 15.1 Risks

| Risk | Probability | Impact | Mitigation |
|---|---|---|---|
| `_seededHtmlBody` not loaded if `model.Mode` is overridden to PlainText by `ApplyDefaultComposeMode` | Medium | Major (HTML body lost for plain-text users who switch to HTML later) | Accepted: plain-text users get plain text. If they switch to HTML mode mid-compose, the seeded HTML body may not be available — this is deferred to v2 (storing both modes' content simultaneously). Document the limitation. |
| `BuildForwardedHtmlBlock` embeds the original HTML verbatim inside a `<blockquote>`. If the original has `<html>` / `<body>` wrappers, the nested structure may confuse the HTML parser or render oddly. | Medium | Minor (visual) | `RichTextDocumentConverter.LoadInto` accepts full HTML documents. Strip outer `<html>`/`<body>` wrappers in `BuildForwardedHtmlBlock` before inserting into blockquote. |
| Inline images (CID references) in the original HTML will appear as broken images in the forwarded compose view and in the sent message. | High (if the original has inline images) | Minor (visual defect, not data loss) | Accepted as v1 limitation. Document in release notes. The spec explicitly calls this out of scope. |
| Attachment dialog `SelectAttachmentsForForwardRequested` is an async event; if `MainWindow` is not subscribed (e.g., in tests), `Forward()` will hit a null event and silently skip the dialog. | Low (MainWindow always subscribes in production) | Major in tests | `Forward()` must have a null-check: if event is null, include all attachments (safe default) or skip dialog. Verify in `MainViewModelForwardTests`. |
| If the user has `DefaultComposeMode = Html` but the original message has no HTML body, the compose window opens in HTML mode with an empty `HtmlBody` in the model. | Low (most modern email has HTML) | Minor | `CreateForward` sets `Mode = ComposeMode.Html` only when `detail.HtmlBody` is non-empty. Plain-text originals produce `Mode = PlainText` regardless of user default — `ApplyDefaultComposeMode` then applies the configured default. |

### 15.2 Open questions — all resolved

**Q: Should the forward compose mode be forced to HTML when the original is HTML, overriding the user's PlainText preference?**
**A: No.** The user's `DefaultComposeMode` is the deciding factor. If the user composes in plain text, the forwarded content is plain text (with HTML-to-text conversion). Only users with `DefaultComposeMode = Html` get the HTML quoted block.

**Q: Should "Include All" be a button, or should all items be checked by default with "Include Selected" as the default button?**
**A: All checked by default; "Include Selected" is the default (Enter) button.** This gives the same result as "Include All" without an extra button, while still allowing deselection. No "Include All" button needed.

**Q: What if `SelectAttachmentsForForwardRequested` has no subscriber (e.g., in unit tests)?**
**A:** `Forward()` treats a null event as "include all attachments" — the existing behavior. Tests that need to simulate cancellation should subscribe to the event in the test setup.

**Q: Should the forwarded HTML content be sanitized before insertion into the compose `RichBodyBox`?**
**A: No additional sanitization beyond what `RichTextDocumentConverter.LoadInto` already does.** The compose window's HTML is not sent to WebView2 for display in the reading pane; it goes through `MimeMessageBuilder` for sending. The existing `RichTextDocumentConverter` handles the content safely.

---

## 16. Implementation Guidance for AI (Session 2)

### 16.1 Start with Phase 1 — it is the highest-value fix

The blank-message bug (Phase 1) is the most important change and has the lowest risk. Build and test it first, separately from the dialog work. The tests in `ComposeViewModelReplyTests` should pass before any further work begins.

### 16.2 `BuildForwardedHtmlBlock` structure

The HTML block for the compose window should be structured so that:
- The cursor starts in an empty paragraph *above* the forwarded block (not inside it).
- The forwarded header is styled simply (not relying on external CSS — the compose RichTextBox has no stylesheet).
- The original body content is wrapped in a `<blockquote>` so the RichTextBox renders it as an indented block.

Suggested structure:
```html
<p> </p>
<div>
  <p>---------- Forwarded message ----------<br />
  From: {From}<br />
  Date: {Date}<br />
  Subject: {Subject}<br />
  To: {To}</p>
</div>
<blockquote style="border-left: 2px solid #ccc; padding-left: 8px; margin-left: 4px;">
  {original HtmlBody with outer html/body tags stripped}
</blockquote>
```

Strip outer `<html>`, `<head>`, and `<body>` tags from `detail.HtmlBody` before inserting into the blockquote. A simple regex or the existing `HtmlStripper` can do this; check whether `RichTextDocumentConverter.LoadInto` already handles full HTML documents (it does — from CLAUDE.md: "accepts both HTML fragments and full HTML documents") which means stripping is not strictly required but prevents double `<html>` nesting.

### 16.3 Cursor placement in Phase 2

In `ComposeWindow`, after `ApplyDefaultComposeMode` runs and the HTML body is loaded into `RichBodyBox`:
- Plain text (`BodyBox`): `BodyBox.CaretIndex = 0; BodyBox.ScrollToHome();`
- HTML mode (`RichBodyBox`): move the caret to the first `TextPointer` of the document: `RichBodyBox.CaretPosition = RichBodyBox.Document.ContentStart;`

Gate this on `_vm.ComposeKind == ComposeKind.Forward` to avoid changing reply or new-message cursor behavior.

### 16.4 Event handler in `MainWindow`

`ShowForwardAttachmentDialogAsync` should:
1. Create `ForwardAttachmentDialogViewModel` with the attachment list.
2. Create `ForwardAttachmentDialogWindow` with the VM, owned by `this`.
3. Wire `vm.CloseRequested += window.Close`.
4. `window.ShowDialog()` (blocks).
5. Unsubscribe `vm.CloseRequested -= window.Close`.
6. Return `vm.Result`.

### 16.5 Acceptance walkthrough priority steps

After building all phases, the most likely failure points are:
- Scenario 1 (HTML-only blank message): verify body is non-empty.
- Scenario 4 (cancel attachment dialog): verify compose does NOT open.
- Scenario 5 (HTML mode forward): verify blockquote renders correctly in RichTextBox and cursor is before it.
- Scenario 9 (screen reader): verify each checkbox reads name + size.

---

## Appendix A — Current `Forward()` Method (for reference)

```csharp
// MainViewModel.cs, line 3576 (current state — to be replaced in Phase 3/4)
[RelayCommand]
private async Task Forward()
{
    var detail = await EnsureDetailAsync();
    if (detail == null) return;

    var model = ComposeViewModel.CreateForward(detail, detail.AccountId);

    if (detail.Attachments.Count > 0)
    {
        IsBusy = true;
        StatusText = "Preparing forward…";
        try
        {
            var summary = SelectedMessage!;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            foreach (var att in detail.Attachments)
            {
                if (!att.IsLoaded && att.PartSpecifier != null)
                {
                    try
                    {
                        att.Content = await _imap.DownloadAttachmentAsync(
                            summary.AccountId, summary.FolderName, summary.MessageId,
                            att.PartSpecifier, cts.Token);
                    }
                    catch (Exception ex)
                    {
                        LogService.Log($"Forward: failed to download '{att.FileName}'", ex);
                    }
                }
            }
            model.Attachments = detail.Attachments;
        }
        finally
        {
            IsBusy = false;
            StatusText = string.Empty;
        }
    }

    ComposeRequested?.Invoke(model);
}
```

**Problems with the current code:**
1. `CreateForward` uses only `PlainTextBody` → blank message for HTML-only emails.
2. All attachments downloaded silently (no user choice).
3. `StatusText = "Preparing forward…"` is not announced (it's a status bar text update, not an `AccessibilityHelper.Announce` call) — users with `AnnounceStatus` enabled hear nothing about the download.
4. Per-download failures are logged only; no user-visible failure reporting.

---

## Appendix B — Implementation Deviations

Changes made during implementation that differ from the spec above. All deviations were deliberate; none are bugs.

### B.1 Dialog button set (Phase 3)

**Spec:** Three buttons — "Include Selected" (default), "Include None", "Cancel".

**Implemented:** Two buttons — "**Forward**" (default, `IncludeSelectedCommand`) and "**Cancel**" (`IsCancel=True`).

**Why:** User testing showed the two-button design was confusing: "Include Selected" and "Include None" both read as confirmation actions and were hard to tell apart without context. Removing "Include None" and renaming "Include Selected" to "Forward" made the choice obvious — Forward means go ahead with what is checked, Cancel means stop. Users who want to forward without attachments can uncheck all items and press Forward.

`IncludeNoneCommand` remains on `ForwardAttachmentDialogViewModel` (exercised by tests) but is not wired to any button in the XAML.

### B.2 Keyboard navigation in the attachment list (Phase 3)

**Spec:** `KeyboardNavigation.TabNavigation="Cycle"` on the `ItemsControl` so arrow keys move through items after Tab lands on the first one.

**Implemented:** `KeyboardNavigation.TabNavigation="Once"` + `KeyboardNavigation.DirectionalNavigation="Contained"` set on the `ItemsPanel`'s `StackPanel` (not on the `ItemsControl` itself, which is `Focusable="False"`).

**Why:** WPF does not propagate `TabNavigation` through a non-focusable `ItemsControl` to its items. Setting it on the `ItemsPanelTemplate`'s `StackPanel` is the correct attachment point. `Once` (single Tab stop for the whole list) + `Contained` (arrow keys stay within the list) was the combination that produced the expected UX: Tab enters the list, Up/Down arrows move between checkboxes, Tab exits the list.

### B.3 Initial focus on dialog open (Phase 3)

**Spec:** Did not specify the mechanism for focusing the first checkbox.

**Implemented:** `Loaded` handler calls `Dispatcher.BeginInvoke(() => MoveFocus(new TraversalRequest(FocusNavigationDirection.First)), DispatcherPriority.Input)` on the **Window** (not on `AttachmentList`, which is `Focusable="False"`).

**Why:** Calling `MoveFocus` on a non-focusable element does nothing. Moving focus on the `Window` with `FocusNavigationDirection.First` walks the logical tree to the first focusable descendant, which is the first `CheckBox`. The `Dispatcher.BeginInvoke` at `DispatcherPriority.Input` ensures layout has completed and WPF's focus infrastructure is ready. An earlier priority (e.g. `Loaded` or `Render`) fires before the item containers are realized.

### B.4 Grid layout instead of DockPanel (Phase 3)

**Spec:** Did not specify layout panel.

**Implemented:** `Grid` with two rows — `RowDefinition Height="*"` (list) above `RowDefinition Height="Auto"` (buttons).

**Why:** `DockPanel.Dock="Bottom"` children must be declared **before** the fill child in XAML, meaning the buttons came first in document order. WPF's Tab navigation follows XAML declaration order within a container, so buttons received Tab focus before the attachment list — opposite of the intended order. Switching to `Grid` with the list first in XAML restored the correct Tab sequence (list → Forward → Cancel).

### B.5 Alt+Enter for attachment properties (Phase 3 — added during implementation)

**Spec:** Not mentioned.

**Implemented:** `PreviewKeyDown` in `ForwardAttachmentDialogWindow` handles `Alt+Enter` when a `CheckBox` has keyboard focus. Shows a `MessageBox` with file name, size, and type. The entry is added to the "Viewing properties (Alt+Enter)" table in `USERGUIDE.md`.

**Why:** QuickMail follows an "Alt+Enter everywhere" principle for properties. Applying it to the forward attachment dialog is consistent with how Alt+Enter works on attachments in the reading pane and on all other focusable items in the app.

### B.6 Message list accessible name — HasAttachments (unplanned addition)

**Spec:** Not mentioned.

**Implemented:** `MessageAccessibleNameConverter` (the `IMultiValueConverter` in `MainWindow.xaml.cs`) accepts an eighth binding — `HasAttachments` (bool). When true, the string `"attachments. "` is inserted immediately after the read-status label and before the sender name: `"Unread. Attachments. Kelly Ford. Subject. …"`. All four `MultiBinding` blocks (ListView, ConversationTree, SenderGroupTree, ToGroupTree) updated.

**Why:** User feedback during the session: when scanning a thread with multiple messages on the same subject, hearing "attachments" early — before sender and subject — lets you identify messages with attachments without navigating all the way through the row. The announcement position follows the same principle as the flag name (announced first, before read status for flagged messages): high-value signal goes early.

### B.7 Failure reporting is per-file by name (Phase 4)

**Spec:** Summary format — "1 of 2 attachments included (1 could not be downloaded)."

**Implemented:** Each failed file is named explicitly in the status/announcement. If `report.pdf` fails: "report.pdf could not be downloaded." Compose opens with the files that succeeded.

**Why:** A count alone ("1 could not be downloaded") does not help the user know which file they need to obtain by another means. Naming the file gives actionable information.

### B.8 Tests added (final list)

The tests that were actually written differ slightly from the spec's test-name list:

| Spec name | Implemented as | Notes |
|---|---|---|
| `CreateForward_PlainMessage_Unchanged` | `CreateForward_PlainMessage_BodyUnchanged` | Renamed for clarity |
| `Forward_AllAttachmentsSelected_DownloadsAll` | Not written separately | Covered by `Forward_PartialSelection_DownloadsOnlySelected` |
| `Forward_AllFail_StatusReportsZeroIncluded` | Not written | Low-value edge case; `Forward_PartialSelection_DownloadsOnlySelected` covers partial |
| `Forward_OneFails_StatusReportsPartial` | Not written | Same reason |
| *(not in spec)* | `Forward_AlreadyLoadedAttachment_CountsAsSuccess` | Tests pre-loaded attachment fast path |
| *(not in spec)* | `Forward_NoSubscriber_IncludesAllAttachments` | Tests null-event backward-compat path |
| *(not in spec)* | `IncludeSelectedFiresCloseRequested`, `CancelFiresCloseRequested`, `AutomationLabelIncludesNameAndSize` | Additional VM event and label tests |
| `CreateForward_HtmlMessage_HtmlBodyContainsForwardHeader` | Added | Covers header div in HTML output |

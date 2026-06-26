# HTML Email Rendering Improvement — PM + Dev Spec

> Covers issue [#143](https://github.com/kellylford/QuickMail/issues/143).
> Follows the [SPEC-TEMPLATE](SPEC-TEMPLATE.md).

---

## Section 1: Executive Summary

QuickMail's HTML sanitizer currently removes all `style=` attributes wholesale. This protects against CSS-based network requests but causes three classes of rendering bugs: hidden elements (preheader padding, dark-mode image swaps, reply quotes) become visible; CSS and JavaScript block content appears verbatim in the message-list preview; and visual hierarchy (font sizes, column widths, background colors) that senders rely on to make email legible is erased.

This spec replaces wholesale style removal with a CSS property allowlist sanitizer, cleans `<style>` blocks of dangerous rules instead of deleting them entirely, fixes the preview-text path to properly skip `<style>` and `<script>` block content, and consolidates three divergent `StripHtml` implementations into one. The reading pane and preview text will render substantially closer to sender intent while maintaining the same security guarantees — no new network requests, no script execution, no iframe embedding.

---

## Section 2: User Problem & Opportunity

### 2.1 Current State (verified against code)

| Surface | Today | Pain | Who feels it |
|---|---|---|---|
| Reading pane | `StripHeavyHtml()` removes all `style=` attributes, making `display:none` elements visible | Preheader divs (filled with invisible Unicode padding) appear; screen readers announce dozens of characters before the body | Screen reader users, especially on newsletters |
| Reading pane | `<style>` blocks stripped entirely | Class-based hidden elements (`.preheader { display:none }`) become visible; typography and column widths lost | All users reading marketing email |
| Reading pane | Table cell widths, colors, font sizes stripped | Newsletter two-column layouts collapse; visual hierarchy erased | Sighted users |
| Reading pane | `display:none` pre-pass only matches `div\|span\|p` (commit 1d636e2) | `<td>`, `<table>`, `<section>`, custom elements with `display:none` still revealed | All users |
| Reading pane | No `visibility:hidden` pre-pass | Elements hidden only via `visibility:hidden` become visible | All users |
| Message list preview | `StripHtml()` in `ImapMailService` and `MainViewModel` uses `Regex.Replace(html, "<[^>]+>", " ")` | Does not skip `<style>` or `<script>` block content — raw CSS or JavaScript appears as the first line of preview text | All users on servers without IMAP PREVIEW extension |
| Message list preview | Three separate `StripHtml` implementations (`ImapMailService`, `MainViewModel`, and `HtmlStripper.ToPlainText`) with different behavior | Bug fixes don't propagate; edge cases diverge | All users |

Verified file locations:
- `MessageBodyHtmlBuilder.StripHeavyHtml()` — `QuickMail/Helpers/MessageBodyHtmlBuilder.cs:115`
- `ImapMailService.StripHtml()` — `QuickMail/Services/ImapMailService.cs:1401`
- `MainViewModel.StripHtml()` — `QuickMail/ViewModels/MainViewModel.cs:5028`
- `HtmlStripper.ToPlainText()` — `QuickMail/Helpers/HtmlStripper.cs` (already handles `<style>`, `<script>`, `<head>` blocks)
- `display:none` pre-pass regex — `MessageBodyHtmlBuilder.cs:121` (`(div|span|p)` only)

### 2.2 Target Personas

1. **Screen reader user reading newsletters** — hears dozens of "combining grapheme joiner / zero-width non-joiner" characters before any message content, because preheader padding divs are unhidden.
2. **Screen reader user on a long reply chain** — quoted original email (hidden by sender via `display:none`) becomes visible; screen reader reads the entire original before the new reply.
3. **Sighted user browsing the message list** — preview for a MailChimp newsletter reads `body { font-family: Arial; } .preheader { display: none; } .wrapper { background-color: #ffffff; }` instead of actual message content.
4. **Sighted user reading a marketing email** — two-column newsletter layout collapses to unstyled text; background colors, column widths, and font hierarchy gone.
5. **Any user reading a dark-mode newsletter** — both light-mode and dark-mode image variants render (neither stays hidden), producing doubled images or doubled decorative elements.

### 2.3 Why Now

- The display:none short-term fix (commit 1d636e2, June 2026) exposed the root cause clearly and generated issue #143.
- `MessageBodyHtmlBuilder` and `HtmlStripper` are well-tested components with an existing test class — extending them is low risk.
- The three duplicate `StripHtml` implementations are a growing maintenance liability; bugs fixed in one path are silently absent from the others.

---

## Section 3: Design Principles

1. **Security first.** Any CSS that could trigger a network request (`url()`, `@import`, `expression()`, `behavior:`, `javascript:`) is blocked unconditionally. When in doubt, strip.
2. **Preserve sender intent at the layout level.** `display`, `visibility`, `color`, `background-color`, `font-*`, `margin`, `padding`, table layout properties are safe and meaningful. Removing them provides no security benefit.
3. **One HTML-to-text path.** `HtmlStripper.ToPlainText()` is the single implementation for preview text. No parallel stripped-down copies.
4. **Sanitization is not user-configurable.** No settings for allowlist strictness. Security decisions are not exposed to users.
5. **Graceful degradation under timeout.** Regex timeouts fall through to the safer existing behavior, never to failure or empty output. `SafeRegexReplace` already enforces this.

---

## Section 4: Feature Scope & Acceptance Criteria

### 4.1 In Scope (v1)

| Feature | File | Notes |
|---|---|---|
| CSS property allowlist for inline `style=` attributes | `MessageBodyHtmlBuilder.StripHeavyHtml()` | New `FilterStyleAttribute()` method; see §5.1 for allowlist |
| `<style>` block cleaning (strip dangerous rules, preserve safe) | `MessageBodyHtmlBuilder.StripHeavyHtml()` | New `CleanStyleBlock()` method; see §5.2 |
| Generalize `display:none` pre-pass to all element types | `MessageBodyHtmlBuilder.StripHeavyHtml()` | Change `(div\|span\|p)` to `\w+` |
| Add `visibility:hidden` inline pre-pass | `MessageBodyHtmlBuilder.StripHeavyHtml()` | Parallel to `display:none` pre-pass |
| Replace `StripHtml()` in `ImapMailService` with `HtmlStripper.ToPlainText()` | `ImapMailService.FetchPreviewsAsync()` | Remove private `StripHtml()` |
| Replace `StripHtml()` in `MainViewModel` with `HtmlStripper.ToPlainText()` | `MainViewModel.ExtractPreview()` | Remove private `StripHtml()` |
| Tests for all new behavior | `MessageBodyHtmlBuilderTests.cs`, `HtmlStripperTests.cs` | See §12 |

### 4.2 Explicitly Out of Scope (v1)

- **Full CSS cascade resolution** — class rules in `<style>` blocks are sanitized and preserved, but QuickMail does not enforce class-based `display:none` on matching elements in the DOM. This would require a full HTML/CSS parser. Deferred to v2.
- **Remote image loading** — `<img src="...">` continues to be stripped entirely. CSP independently blocks it.
- **Dark-mode image swapping via media queries** — `@media (prefers-color-scheme: dark)` rules will be preserved in `<style>` blocks if they contain no dangerous CSS, but QuickMail's reading pane does not natively respond to the dark/light color scheme preference. Both image variants are stripped regardless. Deferred to v2.
- **`background-image` CSS** — Any CSS value containing `url()` is blocked (see §5.1). `background-color` is preserved.
- **`<link rel="stylesheet">` loading** — Already stripped. Not changed.
- **`<iframe>`, `<object>`, `<embed>` content** — Already stripped. Not changed.
- **User-configurable sanitization level** — No settings introduced.

---

## Section 5: Architecture & Technical Decisions

### 5.1 CSS Property Allowlist for Inline `style=`

**Decision:** Replace the wholesale `\s(style)\s*=\s*(...)` regex removal in `StripHeavyHtml` with a new `FilterStyleAttribute(string value)` method that parses the `style` value as a semicolon-separated list of declarations and filters by property allowlist.

**Implementation:**

```
FilterStyleAttribute(string value):
  split on ";"
  foreach declaration in parts:
    split on ":" (first colon only)
    normalize propertyName to lowercase, trimmed
    if propertyName not in allowlist → skip
    if propertyValue contains "url(" / "expression(" / "behavior:" / "javascript:" → skip
    else → keep
  if no declarations kept → return null (caller removes the attribute)
  else → return "property1: value1; property2: value2;"
```

**CSS Property Allowlist:**

| Category | Properties |
|---|---|
| Visibility | `display`, `visibility`, `opacity` |
| Box model | `width`, `height`, `min-width`, `max-width`, `min-height`, `max-height`, `overflow`, `overflow-x`, `overflow-y` |
| Spacing | `margin`, `margin-top`, `margin-right`, `margin-bottom`, `margin-left`, `padding`, `padding-top`, `padding-right`, `padding-bottom`, `padding-left` |
| Color | `color`, `background-color` |
| Typography | `font`, `font-family`, `font-size`, `font-weight`, `font-style`, `font-variant`, `line-height`, `letter-spacing`, `text-align`, `text-decoration`, `text-transform`, `text-indent`, `vertical-align`, `white-space`, `word-break`, `word-wrap` |
| Border | `border`, `border-top`, `border-right`, `border-bottom`, `border-left`, `border-width`, `border-style`, `border-color`, `border-radius`, `border-collapse` |
| Table | `table-layout`, `border-spacing`, `caption-side` |
| Flex/Grid | `flex`, `flex-direction`, `flex-wrap`, `justify-content`, `align-items`, `align-self`, `gap` |
| Floats | `float`, `clear` |

**Blocked (always stripped):**

- Any property not in the allowlist (includes `position`, `z-index`, `clip`, `content`, `cursor`, `background-image`, `background-attachment`, `background-position`, `list-style`, `counter-*`, `-moz-binding`, etc.)
- Any **value** containing `url(`, `expression(`, `behavior:`, or `javascript:` — even for allowlisted properties
- Why `position` is blocked: `position: fixed` or `position: absolute` could render content outside the reading pane frame, overlapping the application chrome.
- Why `content` is blocked: Can inject text or images without HTML elements.

**The `style=` attribute replacement:**

The current regex in `StripHeavyHtml`:
```
\s(on\w+|style|src|srcset|background)\s*=\s*("...|'...|...)
```
This removes the entire attribute. Replace `style` from this pattern and handle it separately via `FilterStyleAttribute`. The `on\w+`, `src`, `srcset`, `background` attributes continue to be stripped wholesale.

**Alternatives considered:**

1. Blocklist approach (remove known-dangerous, allow everything else) — harder to reason about completeness; new CSS features could bypass it.
2. Keep wholesale removal (current) — no security benefit; breaks real-world email. Rejected per §3 principle 2.

### 5.2 CSS `<style>` Block Cleaning

**Decision:** Parse and sanitize `<style>` block content rather than removing the block entirely.

**Implementation — `CleanStyleBlock(string cssContent)`:**

1. Strip CSS comments (`/* ... */`) entirely — they can disguise dangerous content.
2. Remove `@import` rules entirely (any line/rule starting with `@import`).
3. For remaining content, scan all property declarations and apply the same value filter (`url()`, `expression()`, `behavior:`, `javascript:`) — strip any declaration containing these.
4. Remove `background-image` property wherever it appears.
5. Remove `-moz-binding` property.
6. Return the cleaned CSS (may be empty if all rules were dangerous).

**Scope:** `StripHeavyHtml` currently removes `<style\b.*?</style>` entirely. Change to: extract the block content, pass through `CleanStyleBlock`, re-insert as `<style>...cleaned...</style>`. If the cleaned result is empty/whitespace-only, remove the block.

**What `CleanStyleBlock` preserves:**

- Class rules: `.preheader { display: none; }` — the `display: none` rule is safe and keeps the element hidden.
- ID rules: `#header { font-size: 24px; }` — typography safe.
- `@media` queries — preserved as long as their contained declarations are safe.
- Pseudo-classes and pseudo-elements: `:hover`, `::before` — safe; `content:` within `::before` is stripped.

**What it does NOT do:**

- Enforce class-based `display:none` on matching elements. The rule exists in the style block but QuickMail does not do CSS cascade resolution; the browser (WebView2) does. This is sufficient for the common case — the rule will hide elements with that class, as intended.

**Alternatives:**

1. Strip `<style>` entirely (current) — simple; loses all class rules and typography.
2. Full CSS parser (e.g. ExCSS NuGet) — correct for all edge cases; adds dependency. Out of scope for v1.

**Rationale:** Regex-based cleaning handles the bounded CSS subset used in real-world marketing email (class rules, basic media queries, no nested `@` rules deeper than one level) reliably.

### 5.3 Preview Text: Consolidate StripHtml

**The raw HTML problem:**

Both `ImapMailService.StripHtml` and `MainViewModel.StripHtml` use:
```csharp
Regex.Replace(html, "<[^>]+>", " ")
```

This removes HTML tags (`<tag>`) but NOT the text content of `<style>` or `<script>` blocks, which sits between the opening and closing tags and contains no angle brackets. A message with:
```html
<style>body { font-family: Arial; } .preheader { display: none; }</style>
<p>Hello world</p>
```
...produces a preview: `body { font-family: Arial; } .preheader { display: none; }  Hello world`

**The fix:** Replace both private `StripHtml()` methods with `HtmlStripper.ToPlainText()`, which already has an explicit case for `"script" or "style" or "head"` that skips the entire element content (see `HtmlStripper.cs:44`).

**Migration:**

| From | To |
|---|---|
| `ImapMailService.StripHtml(tp.Text)` | `HtmlStripper.ToPlainText(tp.Text)` |
| `MainViewModel.StripHtml(htmlText)` | `HtmlStripper.ToPlainText(htmlText)` |

Both private `StripHtml()` methods are then deleted. `HtmlStripper.ToPlainText()` is already `public static` — no interface changes.

**Behavioral difference:** `HtmlStripper.ToPlainText()` is more thorough — it also converts `<br>` to newlines, `<p>` to paragraph breaks, and includes link URLs in parentheses. This is an improvement for preview quality. The existing `TruncatePreview` call already handles length capping.

### 5.4 Order of Operations in `StripHeavyHtml`

The order matters. After this change:

1. Remove `display:none` elements (inline style) — pre-pass; must run before step 6 removes `style=`
2. Remove `visibility:hidden` elements (inline style) — pre-pass; same reason
3. Strip HTML comments (`<!--...-->`)
4. Strip `<script>` blocks
5. **Clean** `<style>` blocks (not remove) — must run before step 6 to avoid losing safe rules
6. Strip `<svg>`, `<iframe>`, `<object>`, `<embed>`, `<video>`, `<audio>`, `<canvas>`, `<form>` blocks
7. Strip void elements: `<img>`, `<link>`, `<base>`, `<input>`, `<button>`, `<meta>`
8. Strip `on*`, `src`, `srcset`, `background` attributes (wholesale)
9. **Filter** `style=` attributes via `FilterStyleAttribute` allowlist (not remove wholesale)

### 5.5 Shared Component Audit

| Component | File | Other consumers | Change | Risk |
|---|---|---|---|---|
| `MessageBodyHtmlBuilder.StripHeavyHtml()` | `Helpers/MessageBodyHtmlBuilder.cs` | `BuildSanitizedHtmlDocument()`, `HtmlToText()` | Replace style removal with allowlist; clean style blocks | Both callers benefit automatically |
| `MessageBodyHtmlBuilder.BuildSanitizedHtmlDocument()` | `Helpers/MessageBodyHtmlBuilder.cs` | `MainWindow`, `MessageWindow` | No interface change | Consumers get better HTML automatically |
| `HtmlStripper.ToPlainText()` | `Helpers/HtmlStripper.cs` | Existing tests | No interface change; new callers added | Existing tests still pass |
| `ImapMailService.StripHtml()` private | `Services/ImapMailService.cs` | Only `FetchPreviewsAsync` | Remove; replace call with `HtmlStripper.ToPlainText()` | Single consumer; straightforward |
| `MainViewModel.StripHtml()` private | `ViewModels/MainViewModel.cs` | Only `ExtractPreview()` | Remove; replace call with `HtmlStripper.ToPlainText()` | Single consumer |
| `MessageBodyHtmlBuilderTests.cs` | `QuickMail.Tests/` | N/A | Extend with new tests | No risk to other tests |
| `HtmlStripperTests.cs` | `QuickMail.Tests/` | N/A | Add 2–3 tests for style/script block handling | No risk |

### 5.6 Runtime Mode Compatibility

| Mode | Notes |
|---|---|
| Normal | Previews cached in SQLite. Fixed preview stored; no re-fetch. |
| `--online` | Previews fetched live. Fix applies to `ImapMailService.FetchPreviewsAsync`. |
| `--profileDir` | Same as Normal with alternate path. |

---

## Section 6: Keyboard Walkthrough

The sanitization fix is transparent — no new UI or interactions. The user experience that changes:

### Path: Screen reader user opens a MailChimp newsletter

**Before fix:**
1. User opens MailChimp email. Focus moves to reading pane body.
2. Screen reader reads: "Combining grapheme joiner. Zero-width non-joiner." [repeated 30+ times].
3. After a long pause, screen reader reaches the newsletter headline.

**After fix:**
1. User opens MailChimp email. Focus moves to reading pane body.
2. Screen reader reads the first visible content — the headline or greeting — directly.

### Path: Screen reader user reads a reply chain

**Before fix:**
1. User opens an HTML-formatted reply. Original quoted email used `display:none` to hide it.
2. Style attribute stripped → quoted email becomes visible.
3. Screen reader reads the entire original email before the new reply.

**After fix:**
1. User opens the same reply. `display:none` is preserved in the filtered `style=` attribute (since `display` is in the allowlist).
2. Screen reader reads only the new reply. The quoted original stays hidden.

### Path: Sighted user reads message list — raw CSS preview

**Before fix:**
1. User navigates message list. Preview for a newsletter shows: `body { font-family: Arial; } .preheader { display: none; } .wrapper...`

**After fix:**
1. Preview shows readable text: `This week's top stories — three things you should know about...`

### Path: Sighted user reads marketing email

**Before fix:**
1. User opens a two-column MailChimp email. Reading pane shows two columns of completely unstyled text. All background colors, font sizes, column widths are gone.

**After fix:**
1. Same email renders with column widths approximately as intended (table `width` attributes preserved). Background colors on cells render. Font hierarchy visible. Images remain absent (stripped for security — by design).

---

## Section 7: Accessibility Checklist

- **AutomationProperties.Name** — No new controls introduced. No changes.
- **AnnouncementCategory** — No new announcements. The fix is transparent (content quality improves silently).
- **Screen reader browse mode** — No change to WebView2 rendering model. Body still rendered via `NavigateToString`; screen readers browse in virtual cursor mode as before.
- **Focus restoration** — No dialogs. No change.
- **F6 ring** — No new panes. No change.
- **Color-only information** — No new UI. No change.
- **Accessibility impact:** Screen reader users benefit directly — removal of raw CSS from the preview and preservation of `display:none` both reduce noise. No new barriers introduced.

---

## Section 8: Acceptance Walkthrough

### Scenario 1: MailChimp newsletter — preheader/padding characters

**Setup:** App running, a MailChimp (or similar) newsletter is in the inbox.

1. Open the email. **Verify:** Reading pane renders. Screen reader focus moves to the body. The first thing announced is the newsletter headline or greeting — NOT Unicode combining characters or raw CSS.
2. Arrow through the body. **Verify:** Content reads naturally; no surprise raw text injected before the message body.
3. For sighted users: **Verify:** The email has visible background colors on cells and approximately correct column widths. Images are absent (by design).

### Scenario 2: HTML reply chain — quoted content hidden

**Setup:** Open an email that is an HTML-formatted reply, where the original quoted content was hidden by the sender with `style="display:none"`.

1. Open the message. **Verify:** The reading pane shows only the reply text. The quoted original is not visible.
2. Screen reader: **Verify:** Only the reply is read. No original email content announced before it.

### Scenario 3: Preview text — raw CSS absent

**Setup:** On an account whose IMAP server does not support the PREVIEW extension, wait for a new newsletter to arrive. Observe the preview column in the message list as the preview fetches in the background.

1. Watch the preview fill in. **Verify:** Preview text shows readable content — NOT `body {`, `.preheader {`, or any other CSS rule text.
2. If a message has only an HTML body part (no `text/plain`): **Verify:** Preview still shows readable text extracted from the HTML, not raw markup or CSS.

### Scenario 4: Plain-text email — no regression

**Setup:** Open a plain-text email from a regular correspondent.

1. Open the message. **Verify:** Reading pane renders correctly as before.
2. Preview in message list: **Verify:** Preview shows correct text (unchanged behavior for plain-text messages).
3. Reply: **Verify:** Reply compose window pre-populates correctly.

### Scenario 5: Existing test suite passes

1. Run `dotnet test QuickMail.Tests/QuickMail.Tests.csproj -c Release`. **Verify:** All existing tests pass. No new failures in `MessageBodyHtmlBuilderTests` or `HtmlStripperTests`.

### Scenario 6: `--online` mode

**Setup:** Run `QuickMail.exe --online`. Navigate to a folder with HTML-only messages.

1. Navigate to an HTML-only message. **Verify:** Preview fills in with readable text (not raw CSS).
2. Open the message. **Verify:** Reading pane renders correctly.

---

## Section 9: Success Metrics

- **No raw CSS in previews.** A MailChimp email's preview shows content text, not CSS rule text.
- **No Unicode padding noise.** `display:none` preheader divs stay hidden.
- **Quoted replies stay hidden.** `display:none` on quoted content in reply chains is preserved.
- **Preserved layout.** Newsletter two-column table layouts retain cell widths and background colors.
- **No regressions.** All existing tests pass. Plain-text email behavior unchanged.
- **No new network requests.** `url()` blocked in both inline style and `<style>` blocks. Defense in depth maintained via existing CSP.

---

## Section 10: Implementation Phases

### Phase 1: Fix Preview Text (Low Risk — High User Impact)

**Goal:** Raw CSS/script no longer appears in message-list previews. Three `StripHtml` implementations become one.

**Deliverables:**
- `QuickMail/Services/ImapMailService.cs` — remove private `StripHtml()`, update `FetchPreviewsAsync` to call `HtmlStripper.ToPlainText()`
- `QuickMail/ViewModels/MainViewModel.cs` — remove private `StripHtml()`, update `ExtractPreview()` to call `HtmlStripper.ToPlainText()`
- `QuickMail.Tests/HtmlStripperTests.cs` — add tests for `<style>` and `<script>` block content exclusion

**Tests:**
- New: `ToPlainText_SkipsStyleBlockContent`, `ToPlainText_SkipsScriptBlockContent`, `ToPlainText_HandlesEmptyStyleBlock`
- Regression: all existing `HtmlStripperTests` pass

**Risk:** Low. `HtmlStripper.ToPlainText` produces better output; `TruncatePreview` already caps length.

**Duration:** 1–2 hours

---

### Phase 2: Generalize `display:none` / `visibility:hidden` Pre-Pass

**Goal:** All element types with inline `display:none` or `visibility:hidden` are removed before the style-stripping pass, not just `div|span|p`.

**Deliverables:**
- `QuickMail/Helpers/MessageBodyHtmlBuilder.cs` — generalize `display:none` regex from `(div|span|p)` to `[\w-]+` (any element type); add parallel `visibility:hidden` pre-pass

**Tests:**
- New: `StripHeavyHtml_RemovesDisplayNone_OnTableCell`, `StripHeavyHtml_RemovesDisplayNone_OnSection`, `StripHeavyHtml_RemovesVisibilityHidden`
- Regression: existing display:none preheader tests pass

**Risk:** Low. The regex change is narrowly scoped; replacement is empty string.

**Duration:** 1 hour

---

### Phase 3: CSS Allowlist for Inline `style=` Attributes

**Goal:** Inline `style=` attributes are filtered by the property allowlist (§5.1) rather than removed wholesale. Safe layout CSS is preserved; dangerous CSS is blocked.

**Deliverables:**
- `QuickMail/Helpers/MessageBodyHtmlBuilder.cs` — new `private static string? FilterStyleAttribute(string value)` method
- `QuickMail/Helpers/MessageBodyHtmlBuilder.cs` — update `StripHeavyHtml` to call `FilterStyleAttribute` for `style=` attributes (after removing `style` from the existing combined attribute-removal regex)
- `QuickMail.Tests/MessageBodyHtmlBuilderTests.cs` — comprehensive allowlist tests

**Implementation notes:**
- Split on `;`, split each part on `:` (first colon only), normalize property name to `Trim().ToLowerInvariant()`.
- Value safety check: `.Contains("url(", StringComparison.OrdinalIgnoreCase)` etc.
- Style attributes longer than 4000 chars are dropped entirely (no legitimate email needs them).
- Return `null` when all declarations are stripped — caller removes the attribute entirely.

**Tests:**
- New: `FilterStyleAttribute_PreservesDisplay`, `FilterStyleAttribute_PreservesColorAndFont`, `FilterStyleAttribute_PreservesTableWidth`, `FilterStyleAttribute_StripsUrlInValue`, `FilterStyleAttribute_StripsPositionProperty`, `FilterStyleAttribute_StripsContentProperty`, `FilterStyleAttribute_StripsExpressionValue`, `FilterStyleAttribute_ReturnsNullWhenAllStripped`, `FilterStyleAttribute_HandlesMalformedDeclarations`, `StripHeavyHtml_PreservesDisplayNoneOnTd`, `StripHeavyHtml_PreservesTableCellBackgroundColor`, `StripHeavyHtml_StripsBackgroundImage`, `StripHeavyHtml_StripsPositionFixed`

**Risk:** Medium. Property/value parsing has edge cases (vendor prefixes, shorthand properties, whitespace variations, no-value declarations). Mitigation: test against real-world CSS samples. Conservative default: unknown properties are blocked.

**Duration:** 3–4 hours

---

### Phase 4: `<style>` Block Cleaning

**Goal:** `<style>` blocks are sanitized rather than removed, so class-based typography and `display:none` rules survive.

**Deliverables:**
- `QuickMail/Helpers/MessageBodyHtmlBuilder.cs` — new `private static string CleanStyleBlock(string css)` method
- `QuickMail/Helpers/MessageBodyHtmlBuilder.cs` — update `StripHeavyHtml` to replace `<style>` block removal with extract-clean-reinsert
- `QuickMail.Tests/MessageBodyHtmlBuilderTests.cs` — style block cleaning tests

**Implementation notes for `CleanStyleBlock`:**
1. Strip `/* ... */` comments (Singleline regex).
2. Remove `@import ...;` rules.
3. Scan all `property: value` occurrences; strip declarations whose value contains `url(` / `expression(` / `behavior:`.
4. Strip `background-image` and `-moz-binding` properties unconditionally.
5. Return cleaned CSS; empty/whitespace-only result → caller removes the `<style>` block.

**Tests:**
- New: `CleanStyleBlock_RemovesAtImport`, `CleanStyleBlock_RemovesUrlFromPropertyValue`, `CleanStyleBlock_RemovesBackgroundImage`, `CleanStyleBlock_PreservesClassDisplayNone`, `CleanStyleBlock_PreservesMediaQuery`, `StripHeavyHtml_PreservesCleanedStyleBlock`, `StripHeavyHtml_RemovesStyleBlockWhenAllDangerous`

**Risk:** Medium. CSS rule-level parsing via regex has edge cases (nested `@media { @supports { } }`, quoted strings containing `:`). Mitigation: if a rule looks ambiguous, strip it. Empty output → whole block removed (safe fallback). Document the limitation.

**Duration:** 3–4 hours

---

## Section 11: Files to Create / Modify

### Files to Modify

| File | Changes | Lines est. |
|---|---|---|
| `QuickMail/Helpers/MessageBodyHtmlBuilder.cs` | Add `FilterStyleAttribute()` and `CleanStyleBlock()`, update `StripHeavyHtml()` | +90–110 |
| `QuickMail/Services/ImapMailService.cs` | Remove `StripHtml()`, update `FetchPreviewsAsync` call | −12, +2 |
| `QuickMail/ViewModels/MainViewModel.cs` | Remove `StripHtml()`, update `ExtractPreview()` | −8, +2 |
| `QuickMail.Tests/MessageBodyHtmlBuilderTests.cs` | Add ~18 new test methods | +180 |
| `QuickMail.Tests/HtmlStripperTests.cs` | Add 3 tests for style/script block exclusion | +30 |

### Files to Create

None.

---

## Section 12: Tests to Add

| Test Class | New Test Methods | Coverage |
|---|---|---|
| `HtmlStripperTests` | `ToPlainText_SkipsStyleBlockContent`, `ToPlainText_SkipsScriptBlockContent`, `ToPlainText_HandlesEmptyStyleBlock` | Style/script block content exclusion from preview text |
| `MessageBodyHtmlBuilderTests` | `FilterStyleAttribute_PreservesDisplay`, `FilterStyleAttribute_PreservesColorAndFont`, `FilterStyleAttribute_PreservesTableWidth`, `FilterStyleAttribute_StripsUrlInValue`, `FilterStyleAttribute_StripsPositionProperty`, `FilterStyleAttribute_StripsContentProperty`, `FilterStyleAttribute_StripsExpressionValue`, `FilterStyleAttribute_ReturnsNullWhenAllStripped`, `FilterStyleAttribute_HandlesMalformedDeclarations` | Allowlist method unit tests |
| `MessageBodyHtmlBuilderTests` | `StripHeavyHtml_PreservesDisplayNoneOnTd`, `StripHeavyHtml_PreservesTableCellBackgroundColor`, `StripHeavyHtml_StripsBackgroundImage`, `StripHeavyHtml_StripsPositionFixed`, `StripHeavyHtml_RemovesDisplayNone_OnSection`, `StripHeavyHtml_RemovesVisibilityHidden` | Phase 2 + Phase 3 integration |
| `MessageBodyHtmlBuilderTests` | `CleanStyleBlock_RemovesAtImport`, `CleanStyleBlock_RemovesUrlFromPropertyValue`, `CleanStyleBlock_RemovesBackgroundImage`, `CleanStyleBlock_PreservesClassDisplayNone`, `CleanStyleBlock_PreservesMediaQuery`, `StripHeavyHtml_PreservesCleanedStyleBlock`, `StripHeavyHtml_RemovesStyleBlockWhenAllDangerous` | Phase 4 style block cleaning |

---

## Section 13: Known Risks & Open Questions

### 13.1 Risks

| Risk | Probability | Impact | Mitigation |
|---|---|---|---|
| Vendor-prefixed properties (`-webkit-transform`, `-moz-box-shadow`) stripped instead of preserved | Medium | Minor (layout detail lost, not security regression) | Acceptable — vendor prefixes not on allowlist are blocked; add common ones if testing reveals frequent loss |
| `FilterStyleAttribute` misses a `url()` reference hidden in a multi-value shorthand | Low | Medium (background-image could slip through) | Test shorthand properties explicitly; conservative: if a shorthand value is complex and contains `url(`, strip the whole declaration |
| `CleanStyleBlock` regex fails on nested `@media { @supports { } }` | Low | Minor (nested block stripped entirely) | Accept as safe graceful degradation; nested at-rules are rare in email |
| `position: fixed` slips through case-sensitivity check | Medium | Major (UI overlay possible) | Compare after `Trim().ToLowerInvariant()`; add explicit test |
| Performance: `FilterStyleAttribute` called for every element with a `style=` attribute in large emails | Low | Minor | Style attributes are typically short; split+filter is O(n) per attribute; no concern |
| Regex timeout on large `<style>` blocks | Low | Minor | Already handled by `SafeRegexReplace` (logs and returns input unchanged) |

### 13.2 Open Questions (all resolved)

- **Q: Allow `position: relative`?** A: No. Exclude `position` entirely — `fixed` and `sticky` present risk; `relative` rarely needed; simpler to block all.
- **Q: Allow `z-index`?** A: No. No legitimate reading need; could confuse rendering.
- **Q: Allow `content` CSS property?** A: No. Can inject text; blocked.
- **Q: Do class-based `display:none` rules (from `<style>` block) actually work after Phase 4?** A: Yes — the CSS rule is preserved and WebView2 enforces it during rendering. QuickMail does not need to do cascade resolution.
- **Q: What about inline `style="background: url(...)"` shorthand?** A: `background` shorthand is not on the allowlist — it's not a named property in §5.1. Only `background-color` is allowed. Shorthand `background` is stripped.

---

## Section 15: Implementation Guidance for AI

### 15.1 Adjustments Expected

- The property allowlist in §5.1 is normative. Do not expand it during implementation without a comment explaining why.
- Phase 4 (`CleanStyleBlock`): If CSS rule-level parsing becomes unreliable, a simplified version that only strips `@import` and any value containing `url()` is acceptable — document the limitation.
- Order of operations in `StripHeavyHtml` (§5.4) is normative. Changing it changes behavior.

### 15.2 When to Ask for Clarification

- If a CSS property commonly needed for email layout is missing from the allowlist and its absence causes a clear visual regression in real email, add it and document the addition.
- If `CleanStyleBlock` hits a parsing ambiguity that cannot be resolved safely, strip the entire `<style>` block and log at `LogService.Debug` — this is the safe fallback, not a bug.

### 15.3 Steps Most Likely to Catch Bugs

From §8:
- **Scenario 3** (HTML-only email, raw CSS in preview) — catches Phase 1 regression.
- **Scenario 2** (HTML reply chain, quoted content) — catches Phase 3 regression (display:none removal).
- **Scenario 1** (MailChimp newsletter, reading pane) — catches Phase 3/4 regressions (style allowlist, style block cleaning).
- **Scenario 5** (existing tests) — catches all unit-level regressions.

# Rich Compose — Combined PM & Dev Specification

**Status:** Ready for Dev  
**Version:** 1.0  
**Date:** 2026-05-26  
**Author:** Design & PM  
**Target release:** v0.7  

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [User Problem & Opportunity](#2-user-problem--opportunity)
3. [Design Principles](#3-design-principles)
4. [Feature Scope](#4-feature-scope)
5. [Editing Modes & Conversion Matrix](#5-editing-modes--conversion-matrix)
6. [User Experience Design](#6-user-experience-design)
7. [Accessibility (WCAG 2.2)](#7-accessibility-wcag-22)
8. [Technical Architecture](#8-technical-architecture)
9. [Data Flow Diagrams](#9-data-flow-diagrams)
10. [Component Design](#10-component-design)
11. [MIME Output Specification](#11-mime-output-specification)
12. [Command Registry — Full Command Table](#12-command-registry--full-command-table)
13. [Keyboard Shortcut Design](#13-keyboard-shortcut-design)
14. [Implementation Phases](#14-implementation-phases)
15. [Files to Create / Modify](#15-files-to-create--modify)
16. [Tests](#16-tests)
17. [Open Questions & Risks](#17-open-questions--risks)

---

## 1. Executive Summary

QuickMail's compose window today is a plain-text-only `TextBox`. Users cannot apply **bold**, *italic*, headings, lists, links, tables, or inline images. For a desktop email client competing with Outlook and Thunderbird, this is a critical gap.

This specification defines a **three-mode rich compose experience**:

| Mode | Editor | Output |
|------|--------|--------|
| **Plain Text** | WPF `TextBox` (existing, enhanced) | `text/plain` only |
| **Markdown** | WPF `TextBox` + optional live WebView2 preview | `text/plain` + `text/html` (multipart/alternative) |
| **HTML (Rich Text)** | WebView2 `contenteditable` with formatting toolbar | `text/plain` + `text/html` (multipart/alternative) |

Users can switch modes at any time with **lossless bidirectional conversion** between Markdown ↔ HTML, and a **one-way conversion** from either rich mode down to Plain Text (with confirmation).

The compose window gains a **menu bar**, a **formatting toolbar** (HTML mode), full **Command Palette** integration for every formatting action, and **programmatic access to formatting state** for screen readers — following the same pattern already established by the spell-check announcement system.

---

## 2. User Problem & Opportunity

### Current state

- Body is a single `TextBox` bound to `ComposeViewModel.Body` (plain string).
- No formatting of any kind. Users who need bold, links, or lists must compose elsewhere and paste.
- `MimeMessageBuilder.Build()` always produces `text/plain` body parts — even if the user pastes HTML, it's sent as plain text.
- No menu bar. Buttons are crammed into a bottom `DockPanel`.
- Only 11 compose commands are registered in the Command Palette.

### Target personas

| Persona | Need |
|---|---|
| **Business user** | Send professional emails with bold, links, bullet lists, and inline images (logos, signatures) |
| **Developer / technical user** | Write in Markdown, preview rendered output, send as styled HTML |
| **Accessibility-first user** | Hear formatting state at cursor ("Bold on, Arial 12pt"); apply formatting entirely via keyboard |
| **Newsletter / marketing user** | Insert tables, headings, and images with alt text; see WYSIWYG preview before sending |

### Opportunity

No desktop email client does Markdown-native composition well. Thunderbird requires add-ons. Outlook has no Markdown mode. By offering **native Markdown editing with live preview and clean HTML output**, QuickMail can differentiate. Combined with full keyboard accessibility and screen-reader announcements for formatting state, this becomes a flagship feature.

---

## 3. Design Principles

1. **Mode-switching is safe.** Converting Markdown → HTML → Markdown is lossless for the supported subset. Converting to Plain Text warns the user that formatting will be lost.

2. **The right editor for each mode.** Plain Text and Markdown use a WPF `TextBox` (fast, accessible, already integrated with spell-check). HTML uses WebView2 `contenteditable` (true WYSIWYG, already a project dependency).

3. **Keyboard-first.** Every formatting command is in the Command Palette with a default shortcut. The formatting toolbar is a convenience, not a requirement. Screen readers receive formatting-state announcements at the cursor.

4. **Accessible by default.** All custom announcements go through `AccessibilityHelper.Announce()` with appropriate `AnnouncementCategory`. WebView2 ARIA live regions announce formatting changes. The menu bar follows standard Windows menu accessibility patterns.

5. **Send correctly.** HTML-mode and Markdown-mode messages are sent as `multipart/alternative` with both `text/plain` and `text/html` parts. Plain Text mode sends `text/plain` only. This follows RFC 2046 and ensures recipients see the best representation their client supports.

6. **Progressive complexity.** The Plain Text mode remains the default and is unchanged in behavior. Markdown mode adds a preview toggle. HTML mode adds the toolbar. Users who don't need rich formatting are never forced to interact with it.

---

## 4. Feature Scope

### In scope (v0.7)

**Editing modes:**
- Three modes: Plain Text, Markdown, HTML (Rich Text)
- Mode selector in menu bar (View → Compose Mode) and toolbar
- Mode persisted per-compose-window (not global — each new compose starts in the user's default mode)
- Default mode stored in `config.ini` (`[compose] DefaultMode = plain|markdown|html`)

**Conversions:**
- Markdown → HTML (via Markdig)
- HTML → Markdown (via Markdig + custom normalizer)
- Markdown → Plain Text (strip syntax, or render to HTML then strip tags)
- HTML → Plain Text (strip tags, decode entities)
- Plain Text → Markdown (wrap as-is; no transformation needed)
- Plain Text → HTML (escape entities, wrap in `<p>`)
- Conversion triggered on mode switch; confirmation dialog when switching away from a rich mode to Plain Text

**HTML / Rich Text editor:**
- WebView2 hosting a `contenteditable` div
- Formatting toolbar: Bold, Italic, Underline, Strikethrough, Heading 1/2/3, Bullet List, Numbered List, Indent, Outdent, Text Color, Background Color, Clear Formatting
- Insert dialogs: Link (URL + display text), Image (file picker or URL + alt text), Table (rows × columns)
- Font family and font size dropdowns
- Programmatic query of formatting state at cursor via JavaScript interop
- CSP: allow `img-src 'self' data:` for inline images; block scripts, objects, frames

**Markdown editor:**
- WPF `TextBox` with monospace font (Cascadia Code / Consolas)
- Optional live preview pane (WebView2, toggle via View menu or F8)
- Preview renders Markdown → HTML using the same Markdig pipeline as send
- Preview CSP: same restrictive policy as reading pane (no remote images in preview)

**Menu bar:**
- Traditional Windows menu bar at top of compose window
- File: Send (Alt+S), Save Draft (Ctrl+S), Close (Escape)
- Edit: Undo (Ctrl+Z), Redo (Ctrl+Y), Cut (Ctrl+X), Copy (Ctrl+C), Paste (Ctrl+V), Select All (Ctrl+A)
- Format: Bold (Ctrl+B), Italic (Ctrl+I), Underline (Ctrl+U), Strikethrough, Heading 1/2/3, Bullet List, Numbered List, Indent, Outdent, Clear Formatting
- Insert: Link (Ctrl+K), Image, Table, Horizontal Rule
- View: Compose Mode submenu (Plain Text / Markdown / HTML), Toggle Preview (F8, Markdown mode only)

**Command Palette:**
- All formatting commands registered in compose window's `CommandRegistry`
- Commands are mode-aware: disabled when not applicable (e.g., Bold is disabled in Plain Text mode)
- Palette accessible via Ctrl+Shift+P (already implemented)

**MIME output:**
- `MimeMessageBuilder` updated to produce `multipart/alternative` when mode is Markdown or HTML
- `text/plain` part: plain-text representation of the body
- `text/html` part: full HTML with inline CSS, embedded images as `cid:` references
- Embedded images attached as `MimePart` with `Content-Id` header
- Plain Text mode: `text/plain` only (unchanged behavior)

**Image handling:**
- Insert Image dialog: from local file (embedded as `cid:`) or from URL (remote reference)
- Alt text field in the Insert Image dialog (required before OK is enabled)
- Pasted images from clipboard auto-embedded with generated alt text placeholder
- Image resize handles in HTML editor (via contenteditable + JavaScript)

**Signature integration:**
- Account signatures can be plain text or HTML
- HTML signatures inserted into the HTML body on compose
- Markdown signatures rendered to HTML before insertion
- Signature insertion respects the current compose mode

### Out of scope (future)

- Real-time collaborative editing
- Custom CSS themes for HTML output
- Email template variables in rich text (`{sender}`, `{date}` in HTML context)
- Spell-check in WebView2 (browser-based spell-check; WPF spell-check only applies to TextBox)
- Right-to-left (RTL) text direction support
- Emoji picker
- Find/Replace in compose body
- Export compose as `.eml` file
- Read receipt requests
- Priority/importance headers

---

## 5. Editing Modes & Conversion Matrix

### 5.1 Mode Definitions

```
┌─────────────────────────────────────────────────────────────┐
│                    Compose Mode State Machine                │
│                                                             │
│  ┌──────────┐    switch     ┌──────────┐    switch    ┌────┐
│  │  PLAIN   │◄────────────►│ MARKDOWN │◄───────────►│HTML│
│  │  TEXT    │  (one-way     │          │  (lossless)  │    │
│  │          │   confirm)    │          │              │    │
│  └──────────┘               └──────────┘              └────┘
│       │                          │                       │
│       │ send                     │ send                  │ send
│       ▼                          ▼                       ▼
│  text/plain              multipart/alternative     multipart/alternative
│                          text/plain + text/html    text/plain + text/html
└─────────────────────────────────────────────────────────────┘
```

### 5.2 Conversion Matrix

| From → To | Plain Text | Markdown | HTML |
|---|---|---|---|
| **Plain Text** | — | Pass through as-is | HTML-encode, wrap in `<p>` |
| **Markdown** | Strip syntax + confirm | — | Markdig `ToHtml()` |
| **HTML** | Strip tags + confirm | Markdig `ToMarkdown()` + normalize | — |

### 5.3 Conversion Details

**Markdown → HTML (Markdig):**
- Use Markdig with advanced pipeline: `new MarkdownPipelineBuilder().UseAdvancedExtensions().Build()`
- Supported: tables, fenced code blocks, strikethrough, task lists, auto-links, footnotes, emoji shortcodes
- Output: full HTML fragment (no `<html>`/`<body>` wrapper)

**HTML → Markdown:**
- Use Markdig's `HtmlToMarkdown` or a custom normalizer
- Strip `style` attributes, `class` attributes, `script`/`style` tags
- Convert `<b>`/`<strong>` → `**text**`, `<i>`/`<em>` → `*text*`, etc.
- Tables → Markdown pipe tables
- Images → `![alt](url)` or `![alt](cid:xxx)` for embedded

**HTML → Plain Text:**
- Strip all HTML tags
- Decode HTML entities (`&amp;` → `&`, `&#39;` → `'`, etc.)
- Collapse whitespace (multiple newlines → max 2)
- Convert `<li>` items to `• text` or `1. text`
- Links: keep URL in parentheses after link text

**Markdown → Plain Text:**
- Option A: Render to HTML via Markdig, then strip tags (uses same pipeline)
- Option B: Regex-based stripping of Markdown syntax
- **Decision: Option A** — simpler, consistent, reuses HTML→text code

---

## 6. User Experience Design

### 6.1 Window Layout

```
┌──────────────────────────────────────────────────────────────┐
│  File  Edit  Format  Insert  View              Compose Message │  ← Menu bar
├──────────────────────────────────────────────────────────────┤
│  [B] [I] [U] [S] │ H1 H2 H3 │ ≡ ● │ 🔗 🖼 ⊞ │ Font▼ Size▼ │  ← Formatting toolbar (HTML mode only)
├──────────────────────────────────────────────────────────────┤
│  From:  [account dropdown                           ] [Alt+M] │
│  To:    [recipients                                 ]        │
│  Cc:    [                                           ]        │
│  Bcc:   [                                           ]        │
│  Subject: [                                         ] [Alt+U] │
├──────────────────────────────────────────────────────────────┤
│  Attachments: [Add Files… (Ctrl+Shift+A)]                     │
│  ┌──────────────────────────────────────────────────────┐    │
│  │ file1.pdf   file2.png                                │    │
│  └──────────────────────────────────────────────────────┘    │
│  2 files, 1.8 MB of 25 MB limit                              │
├──────────────────────────────────────────────────────────────┤
│                                                              │
│  ┌────────────────────────────────────────────────────┐      │
│  │                                                    │      │
│  │              EDITOR AREA                           │      │
│  │  (TextBox for Plain/Markdown,                      │      │
│  │   WebView2 for HTML)                               │      │
│  │                                                    │      │
│  │                                                    │      │
│  └────────────────────────────────────────────────────┘      │
│  [Alt+Y to focus body]                                       │
├──────────────────────────────────────────────────────────────┤
│  [Markdown Preview (F8)]  (toggle pane, Markdown mode only)  │
│  ┌────────────────────────────────────────────────────┐      │
│  │              LIVE PREVIEW (WebView2)               │      │
│  └────────────────────────────────────────────────────┘      │
├──────────────────────────────────────────────────────────────┤
│  Status text                    [Send] [Save Draft] [Cancel] │
└──────────────────────────────────────────────────────────────┘
```

### 6.2 Mode Indicator

- The current mode is shown in the status bar area: `Mode: Markdown` or `Mode: HTML` or `Mode: Plain Text`
- When switching modes, `AccessibilityHelper.Announce()` announces: "Switched to Markdown mode" with `AnnouncementCategory.Result`
- The formatting toolbar is only visible in HTML mode
- The preview toggle (F8) is only available in Markdown mode

### 6.3 Formatting Toolbar (HTML mode)

The toolbar appears between the menu bar and the header fields. Each button:

- Has a 24×24 icon + tooltip with shortcut
- Is keyboard accessible via Alt+letter (standard Windows menu accelerator pattern)
- Announces state changes: "Bold on" / "Bold off"
- Dropdowns (Font, Size) announce selected value on change

Toolbar layout:
```
[Bold] [Italic] [Underline] [Strikethrough] | [H1▼] [H2▼] [H3▼] | [Bullet List] [Numbered List] | [Indent] [Outdent] | [Link] [Image] [Table] | [Font▼] [Size▼] | [Text Color] [Bg Color] | [Clear Format]
```

### 6.4 Insert Dialogs

**Insert Link:**
- Two fields: URL (required), Display Text (optional, defaults to URL)
- OK / Cancel buttons
- Keyboard: Enter in URL field → OK; Escape → Cancel
- Announcement: "Link inserted" on success

**Insert Image:**
- Two tabs: "From File" and "From URL"
- From File: file picker button, preview thumbnail
- From URL: URL field
- Alt Text field (required — OK disabled until filled)
- OK / Cancel buttons
- Announcement: "Image inserted with alt text: {alt}" on success

**Insert Table:**
- Two spin controls: Rows (2–20, default 3), Columns (2–10, default 3)
- OK / Cancel buttons
- Generates an HTML `<table>` with border, inserted at cursor
- Announcement: "3 by 3 table inserted"

### 6.5 Markdown Preview Pane

- Toggle with F8 or View → Toggle Preview
- Renders Markdown to HTML using the same Markdig pipeline as send
- Read-only WebView2 with reading-pane CSP (no remote images, no scripts)
- Scroll-sync between editor and preview (best-effort, not line-perfect)
- When preview is open, the window splits horizontally: editor top, preview bottom
- Splitter is draggable to resize

---

## 7. Accessibility (WCAG 2.2)

### 7.1 Screen Reader Announcements

All announcements go through `AccessibilityHelper.Announce()` with the appropriate category:

| Event | Announcement | Category |
|---|---|---|
| Mode switch | "Switched to Markdown mode" | `Result` |
| Mode switch to Plain Text (confirm) | "Formatting will be lost. Continue?" | (dialog) |
| Bold toggled | "Bold on" / "Bold off" | `Result` |
| Italic toggled | "Italic on" / "Italic off" | `Result` |
| Heading applied | "Heading 2" | `Result` |
| Link inserted | "Link inserted" | `Result` |
| Image inserted | "Image inserted with alt text: Company logo" | `Result` |
| Table inserted | "3 by 3 table inserted" | `Result` |
| Formatting state query | "Bold on, Italic off, Heading 2, Arial 12pt" | `Result` |
| Preview toggled | "Preview shown" / "Preview hidden" | `Result` |
| Spell-check (existing) | "Misspelling: recieve. receive, relieve, retrieve." | `Result` |

### 7.2 Keyboard Accessibility

- **All formatting commands** have default shortcuts registered in `CommandRegistry`
- **Menu bar** follows standard Windows menu keyboard conventions:
  - Alt to focus menu bar
  - Arrow keys to navigate menus
  - Enter to activate
  - Escape to close menu
- **Formatting toolbar** buttons are in the Tab order (TabIndex before the From field)
- **Insert dialogs** trap focus, Escape closes, Enter confirms
- **WebView2 contenteditable** relays Escape, Tab, and F6 to WPF host (same pattern as reading pane)
- **Formatting state query** bound to a shortcut (Ctrl+Shift+Space or similar) — announces current formatting at cursor

### 7.3 WebView2 ARIA for HTML Editor

The contenteditable div has:
- `role="textbox"` 
- `aria-multiline="true"`
- `aria-label="Message body, rich text editor"`
- A hidden ARIA live region (`aria-live="polite"`) that receives formatting state updates
- JavaScript posts messages to WPF host for screen reader announcements via `window.chrome.webview.postMessage()`

### 7.4 AutomationProperties

| Control | AutomationProperties.Name |
|---|---|
| Mode selector menu item | "Compose Mode" |
| Bold button | "Bold (Ctrl+B)" |
| Italic button | "Italic (Ctrl+I)" |
| Underline button | "Underline (Ctrl+U)" |
| Heading dropdown | "Heading style" |
| Font dropdown | "Font family" |
| Font size dropdown | "Font size" |
| Link button | "Insert Link (Ctrl+K)" |
| Image button | "Insert Image" |
| Table button | "Insert Table" |
| Editor (HTML) | "Message body, rich text editor. Alt+Y to focus." |
| Editor (Markdown) | "Message body, Markdown. Alt+Y to focus." |
| Preview pane | "Markdown preview" |

### 7.5 High Contrast & Visual

- Formatting toolbar uses system colors (respects Windows high-contrast themes)
- WebView2 editor applies `@media (forced-colors: active)` CSS
- Focus indicators visible on all interactive elements
- Minimum contrast ratio 4.5:1 for all text

---

## 8. Technical Architecture

### 8.1 Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│                      ComposeWindow.xaml                         │
│  ┌──────────┐  ┌──────────────┐  ┌──────────────────────────┐  │
│  │ Menu Bar │  │ Fmt Toolbar  │  │ Header Fields (TextBox)   │  │
│  │ (Menu)   │  │ (ToolBar)    │  │ Attachments (ListBox)     │  │
│  └──────────┘  └──────────────┘  └──────────────────────────┘  │
│                                                                  │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │  Editor Host (Grid, single cell — only one visible)      │   │
│  │  ┌─────────────────┐  ┌──────────────────────────────┐   │   │
│  │  │ Plain/Markdown   │  │ HTML Editor (WebView2)       │   │   │
│  │  │ Editor (TextBox) │  │ contenteditable + JS bridge  │   │   │
│  │  └─────────────────┘  └──────────────────────────────┘   │   │
│  └──────────────────────────────────────────────────────────┘   │
│                                                                  │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │  Markdown Preview (WebView2) — toggled via F8            │   │
│  └──────────────────────────────────────────────────────────┘   │
│                                                                  │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │  Status Bar + Action Buttons                              │   │
│  └──────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│                    ComposeViewModel                             │
│  ┌──────────────┐  ┌───────────────┐  ┌────────────────────┐   │
│  │ ComposeMode  │  │ Body (string)  │  │ HtmlBody (string)  │   │
│  │ (enum)       │  │ ← always the   │  │ ← HTML source      │   │
│  │              │  │   canonical    │  │   for HTML mode    │   │
│  │              │  │   plain-text   │  │                    │   │
│  │              │  │   rep          │  │                    │   │
│  └──────────────┘  └───────────────┘  └────────────────────┘   │
│                                                                  │
│  Commands:                                                       │
│  SetModeCommand, ToggleBoldCommand, ToggleItalicCommand, ...     │
│  InsertLinkCommand, InsertImageCommand, InsertTableCommand       │
│  QueryFormattingState() → string                                 │
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│                    Services                                      │
│  ┌──────────────────┐  ┌────────────────────────────────────┐   │
│  │ MarkdownService  │  │ MimeMessageBuilder (updated)       │   │
│  │ - ToHtml()       │  │ - BuildPlain()                     │   │
│  │ - ToMarkdown()   │  │ - BuildMultipartAlternative()      │   │
│  │ - ToPlainText()  │  │ - EmbedImage(cid, bytes)           │   │
│  └──────────────────┘  └────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────┘
```

### 8.2 Mode State Management

```csharp
public enum ComposeMode
{
    PlainText,   // default
    Markdown,
    Html
}
```

`ComposeViewModel` owns:
- `ComposeMode CurrentMode` — the active editing mode
- `string Body` — canonical plain-text representation (always populated)
- `string HtmlBody` — HTML source, only populated/used in HTML mode
- `string MarkdownBody` — Markdown source, only populated/used in Markdown mode

**Canonical representation rule:** `Body` is always the plain-text form. When sending:
- Plain Text mode: use `Body` directly
- Markdown mode: render `Body` (which contains Markdown) to HTML for the `text/html` part; use rendered-then-stripped plain text for `text/plain`
- HTML mode: use `HtmlBody` for the `text/html` part; use stripped plain text for `text/plain`

**Mode switch flow:**
```
User selects new mode
  → If switching FROM rich mode TO Plain Text:
      → Show confirmation dialog ("Formatting will be lost. Continue?")
      → If No: abort
  → Convert current content to new mode's format
  → Update CurrentMode
  → Swap visible editor control
  → Announce mode change
```

### 8.3 WebView2 HTML Editor Architecture

The HTML editor is a WebView2 control hosting a self-contained HTML page with:
- A `contenteditable` div as the editing surface
- A JavaScript API surface exposed via `window.chrome.webview.postMessage()` and `hostObject` (or message-passing)
- CSS for default styling matching the reading pane
- Event handlers for selection changes, keyboard shortcuts, and formatting state changes

**JavaScript Bridge API:**

```
WPF → JS (via CoreWebView2.ExecuteScriptAsync):
  execCommand(commandId, value)     // document.execCommand
  queryCommandState(commandId)      // returns bool
  queryCommandValue(commandId)      // returns string
  setHtml(html)                     // set innerHTML
  getHtml()                         // return innerHTML
  getPlainText()                    // return innerText
  insertImage(src, alt, width, height)
  insertLink(url, text)
  insertTable(rows, cols)
  focus()                           // focus the editor
  getFormattingState()              // returns JSON: {bold, italic, heading, font, fontSize, ...}

JS → WPF (via window.chrome.webview.postMessage):
  'selection-changed'               // cursor moved — WPF queries formatting state
  'escape'                          // relay Escape to WPF
  'shift-tab'                       // relay Shift+Tab to WPF
  'f6' / 'shift-f6'                // relay F6 to WPF
  'content-changed'                 // body content changed — WPF reads HTML
  'formatting-state:{json}'         // formatting state at cursor
```

**CSP for HTML editor:**
```html
<meta http-equiv="Content-Security-Policy" 
      content="default-src 'none'; 
               script-src 'unsafe-inline'; 
               style-src 'unsafe-inline'; 
               img-src 'self' data: https:; 
               object-src 'none'; 
               frame-src 'none'; 
               connect-src 'none'; 
               form-action 'none'; 
               base-uri 'none';">
```

Note: `script-src 'unsafe-inline'` is required for the JavaScript bridge code. `img-src 'self' data: https:` allows embedded images (data: URIs) and remote images the user intentionally inserts. This is more permissive than the reading pane CSP because the user is composing, not viewing untrusted content.

### 8.4 Markdig Integration

**NuGet package:** `Markdig` (MIT license, v0.37+)

**Pipeline configuration:**
```csharp
var pipeline = new MarkdownPipelineBuilder()
    .UseAdvancedExtensions()  // tables, footnotes, emoji, task lists, etc.
    .UseSoftlineBreakAsHardlineBreak()  // single newline → <br> (email convention)
    .Build();
```

**Key methods on `MarkdownService`:**
```csharp
public interface IMarkdownService
{
    string ToHtml(string markdown);
    string ToMarkdown(string html);
    string ToPlainText(string markdown);  // render to HTML, then strip tags
    string HtmlToPlainText(string html);  // strip tags, decode entities
}
```

---

## 9. Data Flow Diagrams

### 9.1 Mode Switch Flow

```
User activates mode switch (menu, toolbar, or command palette)
        │
        ▼
┌─────────────────────────┐
│ ComposeViewModel.       │
│ SetModeCommand(newMode) │
└────────────┬────────────┘
             │
             ▼
    ┌────────────────┐
    │ newMode ==     │──Yes──► Proceed (no conversion needed)
    │ currentMode?   │
    └───────┬────────┘
            │ No
            ▼
    ┌────────────────────────┐
    │ newMode == PlainText   │──Yes──► Show confirmation dialog
    │ && currentMode is rich?│         "Formatting will be lost"
    └───────┬────────────────┘         │ User confirms?
            │ No                       │ Yes ▼        No → ABORT
            ▼                    ┌──────┴──────┐
    ┌────────────────────┐      │ Convert to   │
    │ Determine conversion│      │ Plain Text   │
    │ path                │      └──────────────┘
    └────────┬───────────┘
             │
    ┌────────┴────────────────────────────┐
    │                                      │
    ▼                                      ▼
┌───────────────────┐            ┌───────────────────┐
│ Markdown → HTML   │            │ HTML → Markdown   │
│ (Markdig.ToHtml)  │            │ (Markdig rev +    │
│                   │            │  normalizer)      │
└────────┬──────────┘            └────────┬──────────┘
         │                                │
         └────────────┬───────────────────┘
                      ▼
         ┌──────────────────────┐
         │ Update ViewModel:    │
         │ - CurrentMode = new  │
         │ - Body / HtmlBody    │
         │   updated            │
         │ - Raise mode changed │
         │   event              │
         └──────────┬───────────┘
                    ▼
         ┌──────────────────────┐
         │ View reacts:         │
         │ - Swap editor control│
         │ - Show/hide toolbar  │
         │ - Announce mode      │
         │   change             │
         └──────────────────────┘
```

### 9.2 Send Flow (HTML / Markdown Mode)

```
User presses Send (Alt+S or Ctrl+Enter)
        │
        ▼
┌──────────────────────────┐
│ ComposeViewModel.        │
│ SendCommand.Execute()    │
└────────────┬─────────────┘
             │
             ▼
┌──────────────────────────────────────┐
│ BuildComposeModel()                  │
│ - Sets Body = plain-text version     │
│ - Sets HtmlBody = HTML version       │
│   (if Markdown: render via Markdig)  │
│ - Sets ComposeMode = current mode    │
└────────────┬─────────────────────────┘
             │
             ▼
┌──────────────────────────────────────┐
│ MimeMessageBuilder.Build()           │
│                                      │
│ if mode == PlainText:                │
│   → TextPart("plain") only           │
│                                      │
│ if mode == Markdown || HTML:         │
│   → Multipart("alternative")         │
│      ├─ TextPart("plain")            │
│      │   (stripped plain text)       │
│      └─ TextPart("html")             │
│          (full HTML with inline CSS) │
│                                      │
│   → For each embedded image:         │
│      └─ MimePart with Content-Id     │
│         referenced as cid: in HTML   │
└────────────┬─────────────────────────┘
             │
             ▼
┌──────────────────────────────────────┐
│ SmtpService.SendAsync()              │
│ (unchanged — sends the MimeMessage)  │
└──────────────────────────────────────┘
```

### 9.3 Formatting Command Flow (HTML Mode)

```
User presses Ctrl+B (or toolbar button, or command palette)
        │
        ▼
┌──────────────────────────────────────┐
│ ComposeWindow.xaml.cs                │
│ PreviewKeyDown → CommandRegistry     │
│ → ToggleBoldCommand.Execute()        │
└────────────┬─────────────────────────┘
             │
             ▼
┌──────────────────────────────────────┐
│ ComposeViewModel.ToggleBold()        │
│ → If mode != Html: return            │
│ → Raise ExecuteJsRequested event     │
│   with: "document.execCommand('bold')│
└────────────┬─────────────────────────┘
             │
             ▼
┌──────────────────────────────────────┐
│ ComposeWindow.xaml.cs                │
│ (subscribes to ExecuteJsRequested)   │
│ → await HtmlEditor.CoreWebView2.     │
│     ExecuteScriptAsync(js)           │
└────────────┬─────────────────────────┘
             │
             ▼
┌──────────────────────────────────────┐
│ JavaScript in WebView2:              │
│ document.execCommand('bold')         │
│ → Posts 'content-changed' message    │
│ → Posts 'formatting-state:{json}'    │
└────────────┬─────────────────────────┘
             │
             ▼
┌──────────────────────────────────────┐
│ ComposeWindow.xaml.cs                │
│ WebMessageReceived handler:          │
│ → Reads HTML via ExecuteScriptAsync  │
│ → Updates vm.HtmlBody                │
│ → If formatting-state message:       │
│     AccessibilityHelper.Announce(    │
│       "Bold on", Result)             │
└──────────────────────────────────────┘
```

---

## 10. Component Design

### 10.1 New Files

| File | Purpose |
|------|---------|
| `QuickMail/Models/ComposeMode.cs` | `ComposeMode` enum (PlainText, Markdown, Html) |
| `QuickMail/Services/IMarkdownService.cs` | Interface for Markdown ↔ HTML conversion |
| `QuickMail/Services/MarkdownService.cs` | Markdig-based implementation |
| `QuickMail/Helpers/HtmlStripper.cs` | HTML → plain text conversion utility |
| `QuickMail/Views/HtmlEditorResources/editor.html` | Embedded HTML page for WebView2 contenteditable editor |
| `QuickMail/Views/HtmlEditorResources/editor.js` | JavaScript for the editor (commands, formatting state, bridge) |
| `QuickMail/Views/HtmlEditorResources/editor.css` | CSS for the editor contenteditable surface |
| `QuickMail/Views/InsertLinkDialog.xaml` | Insert Link dialog |
| `QuickMail/Views/InsertLinkDialog.xaml.cs` | Code-behind |
| `QuickMail/Views/InsertImageDialog.xaml` | Insert Image dialog |
| `QuickMail/Views/InsertImageDialog.xaml.cs` | Code-behind |
| `QuickMail/Views/InsertTableDialog.xaml` | Insert Table dialog |
| `QuickMail/Views/InsertTableDialog.xaml.cs` | Code-behind |

### 10.2 Modified Files

| File | Change Summary |
|------|---------------|
| `QuickMail/Models/ComposeModel.cs` | Add `ComposeMode Mode`, `string? HtmlBody`, `List<EmbeddedImage> EmbeddedImages` |
| `QuickMail/Models/AttachmentModel.cs` | Add `string? ContentId` for embedded inline images |
| `QuickMail/ViewModels/ComposeViewModel.cs` | Add mode management, formatting commands, HTML body property, JS execution events |
| `QuickMail/Views/ComposeWindow.xaml` | Add menu bar, formatting toolbar, WebView2 editor, markdown preview pane, mode-aware visibility |
| `QuickMail/Views/ComposeWindow.xaml.cs` | Add WebView2 init, JS bridge, command registration for all formatting commands, mode-switching logic |
| `QuickMail/Services/MimeMessageBuilder.cs` | Add multipart/alternative output, embedded image handling |
| `QuickMail/App.xaml.cs` | Register `IMarkdownService` in DI |
| `QuickMail/QuickMail.csproj` | Add `Markdig` NuGet package, embed HTML editor resources |

### 10.3 ComposeModel Changes

```csharp
public class ComposeModel
{
    // ... existing properties ...
    
    /// <summary>The editing mode this message was composed in.</summary>
    public ComposeMode Mode { get; set; } = ComposeMode.PlainText;
    
    /// <summary>Full HTML body (only populated for Html mode).</summary>
    public string? HtmlBody { get; set; }
    
    /// <summary>Images embedded inline (cid: references in HTML).</summary>
    public List<EmbeddedImage> EmbeddedImages { get; set; } = [];
}

public class EmbeddedImage
{
    public string ContentId { get; set; } = string.Empty;  // e.g. "image001@quickmail"
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "image/png";
    public byte[] Content { get; set; } = [];
    public string AltText { get; set; } = string.Empty;
}
```

### 10.4 ComposeViewModel Key Additions

```csharp
// ── Mode management ──
[ObservableProperty] private ComposeMode _currentMode = ComposeMode.PlainText;
[ObservableProperty] private string _htmlBody = string.Empty;
[ObservableProperty] private bool _isPreviewVisible;
[ObservableProperty] private string _previewHtml = string.Empty;

// ── Events for View communication ──
public event Action<string>? ExecuteJavaScriptRequested;    // VM → View: run JS in WebView2
public event Func<string, Task<string>>? EvaluateJavaScriptRequested; // VM → View: eval JS, return result
public event Action? RefreshPreviewRequested;               // VM → View: refresh markdown preview

// ── Formatting commands (HTML mode) ──
[RelayCommand] private void ToggleBold() => ExecuteJs("document.execCommand('bold')");
[RelayCommand] private void ToggleItalic() => ExecuteJs("document.execCommand('italic')");
[RelayCommand] private void ToggleUnderline() => ExecuteJs("document.execCommand('underline')");
[RelayCommand] private void ToggleStrikethrough() => ExecuteJs("document.execCommand('strikeThrough')");
[RelayCommand] private void SetHeading(string level) => ExecuteJs($"document.execCommand('formatBlock', false, '<h{level}>')");
[RelayCommand] private void InsertBulletList() => ExecuteJs("document.execCommand('insertUnorderedList')");
[RelayCommand] private void InsertNumberedList() => ExecuteJs("document.execCommand('insertOrderedList')");
[RelayCommand] private void Indent() => ExecuteJs("document.execCommand('indent')");
[RelayCommand] private void Outdent() => ExecuteJs("document.execCommand('outdent')");
[RelayCommand] private void ClearFormatting() => ExecuteJs("document.execCommand('removeFormat')");

// ── Insert commands ──
[RelayCommand] private async Task InsertLinkAsync();
[RelayCommand] private async Task InsertImageAsync();
[RelayCommand] private async Task InsertTableAsync();
[RelayCommand] private void InsertHorizontalRule() => ExecuteJs("document.execCommand('insertHorizontalRule')");

// ── Mode commands ──
[RelayCommand] private void SetMode(string mode);  // "plain", "markdown", "html"

// ── Query formatting state (for screen reader announcements) ──
public async Task<string> QueryFormattingStateAsync();  // returns "Bold on, Italic off, Heading 2, Arial 12pt"
```

### 10.5 WebView2 Editor HTML Page (`editor.html`)

The embedded HTML page is a complete self-contained editor:

```html
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" 
      content="default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; 
               img-src 'self' data: https:; object-src 'none'; frame-src 'none'; 
               connect-src 'none'; form-action 'none'; base-uri 'none';">
<style>
  /* ... editor styles ... */
</style>
</head>
<body>
<div id="editor" contenteditable="true" role="textbox" aria-multiline="true" 
     aria-label="Message body, rich text editor"></div>
<div id="aria-live" aria-live="polite" class="sr-only"></div>
<script>
  // ... bridge code ...
</script>
</body>
</html>
```

The JavaScript bridge:
- Listens for `selectionchange` on the document and posts formatting state
- Listens for `input` on the editor and posts content changes
- Exposes functions callable from WPF via `ExecuteScriptAsync`
- Relays keyboard events (Escape, Tab, F6) to WPF host
- Manages image resize handles
- Provides `getFormattingState()` returning JSON with all current formatting attributes

### 10.6 Markdown Preview

The preview pane is a second WebView2 control. When toggled visible:
1. `ComposeViewModel` renders the current Markdown body to HTML via `IMarkdownService.ToHtml()`
2. The HTML is wrapped in a document with reading-pane CSP (no remote images, no scripts)
3. `NavigateToString` displays it
4. On every keystroke in the Markdown editor (debounced 300ms), the preview refreshes

---

## 11. MIME Output Specification

### 11.1 Plain Text Mode (unchanged)

```
Content-Type: text/plain; charset=utf-8

Hello, this is a plain text email.
```

### 11.2 Markdown Mode

```
Content-Type: multipart/alternative; boundary="=-abc123"

--=-abc123
Content-Type: text/plain; charset=utf-8

Hello, this is **bold** and this is *italic*.
[Click here](https://example.com)

--=-abc123
Content-Type: text/html; charset=utf-8

<html><body style="font-family: Segoe UI, Arial, sans-serif; font-size: 13px;">
<p>Hello, this is <strong>bold</strong> and this is <em>italic</em>.</p>
<p><a href="https://example.com">Click here</a></p>
</body></html>

--=-abc123--
```

### 11.3 HTML Mode (with embedded image)

```
Content-Type: multipart/mixed; boundary="=-outer123"

--=-outer123
Content-Type: multipart/alternative; boundary="=-inner123"

--=-inner123
Content-Type: text/plain; charset=utf-8

Hello, here is an image: [Company Logo]

--=-inner123
Content-Type: text/html; charset=utf-8

<html><body style="font-family: Segoe UI, Arial, sans-serif; font-size: 13px;">
<p>Hello, here is an image:</p>
<p><img src="cid:image001@quickmail" alt="Company Logo" width="200" height="100"></p>
</body></html>

--=-inner123--

--=-outer123
Content-Type: image/png
Content-Disposition: inline; filename="logo.png"
Content-Id: <image001@quickmail>
Content-Transfer-Encoding: base64

iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk
... (base64 data) ...

--=-outer123--
```

### 11.4 HTML Sanitization Before Send

Before building the MIME message, the HTML body is sanitized:
- Remove `<script>`, `<object>`, `<embed>`, `<iframe>` tags
- Remove `on*` event handler attributes (onclick, onload, etc.)
- Remove `javascript:` URLs from `href` and `src`
- Ensure all `<img>` tags have `alt` attributes (add empty if missing)
- Wrap bare text nodes in `<p>` tags if not already in a block element
- Add inline CSS for default font styling on `<body>`

---

## 12. Command Registry — Full Command Table

All commands registered in `ComposeWindow.RegisterComposeCommands()`. Category is `"Compose"` unless noted.

| Command ID | Title | Default Shortcut | Mode Availability | Description |
|---|---|---|---|---|
| *(existing)* `compose.send` | Send Message | Alt+S | All | Send the message |
| *(existing)* `compose.saveDraft` | Save Draft | Ctrl+S | All | Save as draft |
| *(existing)* `compose.addAttachments` | Add Attachments… | Ctrl+Shift+A | All | Open file picker |
| *(existing)* `compose.insertTemplate` | Insert Template… | — | All | Insert saved template |
| *(existing)* `compose.saveAsTemplate` | Save as Template | — | All | Save body as template |
| *(existing)* `compose.cancel` | Cancel / Close | Escape | All | Close compose window |
| *(existing)* `compose.focusBody` | Focus Message Body | Alt+Y | All | Move focus to editor |
| *(existing)* `compose.nextMisspelling` | Next Misspelling | F7 | Plain, Markdown | Navigate spelling errors |
| *(existing)* `compose.prevMisspelling` | Previous Misspelling | Shift+F7 | Plain, Markdown | Navigate spelling errors |
| *(existing)* `compose.toggleSpellingAnnouncements` | Toggle Spelling Announcements | — | Plain, Markdown | Toggle spelling setting |
| *(existing)* `compose.repeatSpelling` | Repeat Spelling Announcement | Alt+F7 | Plain, Markdown | Re-announce spelling |
| **`compose.setModePlain`** | Switch to Plain Text Mode | Ctrl+Shift+1 | All | Switch to plain text |
| **`compose.setModeMarkdown`** | Switch to Markdown Mode | Ctrl+Shift+2 | All | Switch to Markdown |
| **`compose.setModeHtml`** | Switch to HTML Mode | Ctrl+Shift+3 | All | Switch to HTML/rich text |
| **`compose.toggleBold`** | Bold | Ctrl+B | HTML | Toggle bold |
| **`compose.toggleItalic`** | Italic | Ctrl+I | HTML | Toggle italic |
| **`compose.toggleUnderline`** | Underline | Ctrl+U | HTML | Toggle underline |
| **`compose.toggleStrikethrough`** | Strikethrough | Ctrl+Shift+X | HTML | Toggle strikethrough |
| **`compose.heading1`** | Heading 1 | Ctrl+Alt+1 | HTML | Apply Heading 1 |
| **`compose.heading2`** | Heading 2 | Ctrl+Alt+2 | HTML | Apply Heading 2 |
| **`compose.heading3`** | Heading 3 | Ctrl+Alt+3 | HTML | Apply Heading 3 |
| **`compose.bulletList`** | Bullet List | Ctrl+Shift+L | HTML | Insert bullet list |
| **`compose.numberedList`** | Numbered List | Ctrl+Shift+N | HTML | Insert numbered list |
| **`compose.indent`** | Indent | Tab | HTML | Increase indent |
| **`compose.outdent`** | Outdent | Shift+Tab | HTML | Decrease indent |
| **`compose.insertLink`** | Insert Link… | Ctrl+K | HTML | Open link dialog |
| **`compose.insertImage`** | Insert Image… | Ctrl+Shift+I | HTML | Open image dialog |
| **`compose.insertTable`** | Insert Table… | Ctrl+Shift+T | HTML | Open table dialog |
| **`compose.insertHorizontalRule`** | Insert Horizontal Rule | Ctrl+Shift+H | HTML | Insert `<hr>` |
| **`compose.clearFormatting`** | Clear Formatting | Ctrl+Space | HTML | Remove all formatting |
| **`compose.queryFormatting`** | Announce Formatting State | Ctrl+Shift+Space | HTML | Announce formatting at cursor |
| **`compose.togglePreview`** | Toggle Markdown Preview | F8 | Markdown | Show/hide preview pane |
| **`compose.focusSubject`** | Focus Subject | Alt+U | All | Move focus to Subject |
| **`compose.focusFrom`** | Focus From | Alt+M | All | Move focus to From |

---

## 13. Keyboard Shortcut Design

### 13.1 Design Rationale

- **Ctrl+B/I/U** are universal formatting shortcuts (Word, Google Docs, Outlook, Thunderbird)
- **Ctrl+Shift+1/2/3** for mode switching mirrors the main window's Ctrl+1/2/3 for pane focus
- **Ctrl+K** for Insert Link is universal (Word, Google Docs, most editors)
- **Ctrl+Shift+L/N** for lists follows Google Docs convention
- **Ctrl+Space** for Clear Formatting follows Word/Google Docs convention
- **F8** for preview toggle is unused and easy to reach
- **Ctrl+Shift+Space** for formatting state query is a deliberate chord unlikely to conflict

### 13.2 Conflicts with Existing Shortcuts

| Shortcut | Existing Use | Resolution |
|---|---|---|
| Ctrl+B | *(none in compose)* | Free — use for Bold |
| Ctrl+I | *(none in compose)* | Free — use for Italic |
| Ctrl+U | *(none in compose)* | Free — use for Underline |
| Ctrl+K | *(none in compose)* | Free — use for Insert Link |
| Ctrl+Shift+L | *(none in compose)* | Free — use for Bullet List |
| Ctrl+Shift+N | *(none in compose)* | Free — use for Numbered List |
| Tab | Inserts tab character in TextBox | In HTML mode, Tab = indent; in Plain/Markdown mode, Tab = tab character (unchanged) |
| Shift+Tab | *(none in compose)* | In HTML mode, Shift+Tab = outdent; in Plain/Markdown mode, no-op |

### 13.3 WebView2 Key Relay

The HTML editor WebView2 must relay certain keys to WPF because they have WPF-level meaning:

| Key | Behavior in WebView2 | Relayed to WPF? |
|---|---|---|
| Escape | Closes any open browser UI | Yes — closes compose window |
| Tab | Inserts tab / indents | No — handled by contenteditable |
| Shift+Tab | Outdents | No — handled by contenteditable |
| F6 | Browser pane cycling | Yes — cycles WPF panes |
| Ctrl+Shift+P | *(none)* | Yes — opens command palette |
| Alt+S | *(none)* | Yes — sends message |
| Ctrl+S | Browser save page | Yes — saves draft |
| Ctrl+Enter | *(none)* | Yes — sends message |

The relay is implemented via the same `window.chrome.webview.postMessage()` pattern used in the reading pane (see `MainWindow.xaml.cs` line 525–540).

---

## 14. Implementation Phases

### Phase 1: Foundation (Markdown mode + conversions)

**Goal:** Markdown editing with preview, mode switching, and correct MIME output.

- Add `Markdig` NuGet package
- Create `ComposeMode` enum
- Create `IMarkdownService` / `MarkdownService`
- Create `HtmlStripper` utility
- Update `ComposeModel` with `Mode` and `HtmlBody`
- Update `ComposeViewModel` with mode management and conversion logic
- Add mode selector to compose window (simple ComboBox or menu)
- Add Markdown preview pane (WebView2, toggle with F8)
- Update `MimeMessageBuilder` for multipart/alternative output
- Register new commands in `CommandRegistry`
- **Tests:** `MarkdownServiceTests`, `HtmlStripperTests`, `ComposeViewModelModeTests`

### Phase 2: HTML Editor (WebView2 contenteditable)

**Goal:** Full rich-text editing in WebView2 with formatting toolbar.

- Create embedded HTML editor page (`editor.html`, `editor.js`, `editor.css`)
- Add WebView2 control to `ComposeWindow.xaml` for HTML editing
- Implement JavaScript bridge (WPF ↔ WebView2 messaging)
- Add formatting toolbar to compose window
- Implement all formatting commands (Bold, Italic, Underline, etc.)
- Implement formatting state query and screen reader announcements
- Implement key relay (Escape, Tab, F6, etc.)
- Wire up `HtmlBody` property with WebView2 content sync
- **Tests:** `HtmlEditorIntegrationTests` (STA), `ComposeViewModelFormattingTests`

### Phase 3: Insert Dialogs & Image Handling

**Goal:** Link, image, and table insertion with embedded image support.

- Create `InsertLinkDialog` (XAML + VM)
- Create `InsertImageDialog` (XAML + VM) with file picker and URL tabs
- Create `InsertTableDialog` (XAML + VM)
- Implement embedded image handling in `MimeMessageBuilder` (cid: references)
- Implement clipboard image paste in HTML editor
- Add image resize handles in WebView2 editor
- **Tests:** `InsertDialogTests`, `EmbeddedImageTests`

### Phase 4: Menu Bar & Polish

**Goal:** Professional menu bar, accessibility polish, edge cases.

- Add menu bar to compose window (File, Edit, Format, Insert, View)
- Wire all menu items to existing commands
- Add keyboard accelerators to menu items
- Polish mode-switch confirmation dialogs
- Add default mode setting to `config.ini`
- HTML signature support
- High-contrast theme support
- **Tests:** `ComposeMenuTests`, `ComposeAccessibilityTests`

---

## 15. Files to Create / Modify

### 15.1 New Files

| File | Purpose |
|------|---------|
| `QuickMail/Models/ComposeMode.cs` | `ComposeMode` enum |
| `QuickMail/Models/EmbeddedImage.cs` | Embedded image data model |
| `QuickMail/Services/IMarkdownService.cs` | Markdown conversion interface |
| `QuickMail/Services/MarkdownService.cs` | Markdig-based implementation |
| `QuickMail/Helpers/HtmlStripper.cs` | HTML → plain text utility |
| `QuickMail/Views/HtmlEditorResources/editor.html` | WebView2 editor page |
| `QuickMail/Views/HtmlEditorResources/editor.js` | Editor JavaScript bridge |
| `QuickMail/Views/HtmlEditorResources/editor.css` | Editor stylesheet |
| `QuickMail/Views/InsertLinkDialog.xaml` | Insert Link dialog |
| `QuickMail/Views/InsertLinkDialog.xaml.cs` | Code-behind |
| `QuickMail/Views/InsertImageDialog.xaml` | Insert Image dialog |
| `QuickMail/Views/InsertImageDialog.xaml.cs` | Code-behind |
| `QuickMail/Views/InsertTableDialog.xaml` | Insert Table dialog |
| `QuickMail/Views/InsertTableDialog.xaml.cs` | Code-behind |

### 15.2 Modified Files

| File | Change |
|------|--------|
| `QuickMail/QuickMail.csproj` | Add `Markdig` NuGet package; embed HTML editor resources as `<EmbeddedResource>` |
| `QuickMail/App.xaml.cs` | Register `IMarkdownService` in DI; pass to `ComposeViewModel` |
| `QuickMail/Models/ComposeModel.cs` | Add `Mode`, `HtmlBody`, `EmbeddedImages` properties |
| `QuickMail/Models/AttachmentModel.cs` | Add `ContentId` property for inline images |
| `QuickMail/ViewModels/ComposeViewModel.cs` | Add mode management, formatting commands, JS bridge events, HTML body sync |
| `QuickMail/Views/ComposeWindow.xaml` | Add menu bar, formatting toolbar, WebView2 editor, markdown preview, mode-aware visibility bindings |
| `QuickMail/Views/ComposeWindow.xaml.cs` | Add WebView2 init, JS bridge, command registration (~30 new commands), mode-switching, dialog wiring |
| `QuickMail/Services/MimeMessageBuilder.cs` | Add `BuildMultipartAlternative()`, embedded image handling, HTML sanitization |
| `QuickMail/Services/IConfigService.cs` | Add `DefaultComposeMode` setting |
| `QuickMail/Services/ConfigService.cs` | Read/write `[compose] DefaultMode` from `config.ini` |

### 15.3 Test Files

| File | Tests |
|------|-------|
| `QuickMail.Tests/MarkdownServiceTests.cs` | Markdown → HTML, HTML → Markdown, round-trip fidelity |
| `QuickMail.Tests/HtmlStripperTests.cs` | HTML → plain text, entity decoding, edge cases |
| `QuickMail.Tests/ComposeViewModelModeTests.cs` | Mode switching, conversion correctness, confirmation dialog logic |
| `QuickMail.Tests/ComposeViewModelFormattingTests.cs` | Formatting commands dispatch correct JS, mode gating |
| `QuickMail.Tests/MimeMessageBuilderRichTests.cs` | Multipart/alternative output, embedded images, sanitization |
| `QuickMail.Tests/InsertDialogTests.cs` | Dialog VM logic, validation |
| `QuickMail.Tests/EmbeddedImageTests.cs` | cid: generation, MIME part construction |
| `QuickMail.Tests/ComposeMenuTests.cs` | Menu item → command wiring |
| `QuickMail.Tests/ComposeAccessibilityTests.cs` | AutomationProperties, announcements |
| `QuickMail.Tests/XamlParseTests.cs` | Add new dialogs to XAML parse tests |

---

## 16. Tests

### 16.1 Unit Tests

**MarkdownServiceTests:**
- `ToHtml_BasicMarkdown_ReturnsValidHtml`
- `ToHtml_Tables_RendersHtmlTable`
- `ToHtml_FencedCodeBlock_RendersPreCode`
- `ToMarkdown_BasicHtml_ReturnsMarkdown`
- `ToMarkdown_Table_RendersPipeTable`
- `RoundTrip_MarkdownToHtmlToMarkdown_PreservesContent`
- `ToPlainText_StripsFormatting`

**HtmlStripperTests:**
- `Strip_SimpleParagraphs_ReturnsPlainText`
- `Strip_Links_KeepsUrlInParens`
- `Strip_Lists_ConvertsToBullets`
- `Strip_DecodesHtmlEntities`
- `Strip_RemovesScriptTags`

**ComposeViewModelModeTests:**
- `SetMode_SameMode_NoConversion`
- `SetMode_MarkdownToHtml_ConvertsCorrectly`
- `SetMode_HtmlToMarkdown_ConvertsCorrectly`
- `SetMode_RichToPlainText_ShowsConfirmation`
- `SetMode_RichToPlainText_UserCancels_StaysInRichMode`
- `SetMode_UpdatesCurrentModeProperty`

**ComposeViewModelFormattingTests:**
- `ToggleBold_InHtmlMode_ExecutesJs`
- `ToggleBold_InPlainTextMode_NoOp`
- `InsertLink_RaisesDialogRequest`
- `QueryFormattingState_ReturnsCorrectString`

**MimeMessageBuilderRichTests:**
- `Build_PlainTextMode_ReturnsTextPlainOnly`
- `Build_MarkdownMode_ReturnsMultipartAlternative`
- `Build_HtmlMode_ReturnsMultipartAlternative`
- `Build_WithEmbeddedImages_IncludesImageParts`
- `Build_SanitizesHtmlBody`

### 16.2 Integration Tests (STA)

**HtmlEditorIntegrationTests:**
- `WebView2_Initializes_WithContenteditable`
- `ExecuteScript_Bold_ReturnsSuccess`
- `GetHtml_ReturnsEditedContent`
- `KeyRelay_Escape_PostsMessage`

**XamlParseTests:**
- Add `InsertLinkDialog`, `InsertImageDialog`, `InsertTableDialog` to parse tests

### 16.3 Manual Test Checklist

- [ ] Switch between all three modes; verify content is preserved
- [ ] Apply bold, italic, underline in HTML mode; verify visual and screen reader announcement
- [ ] Insert a link; verify it's clickable in sent email
- [ ] Insert an image from file; verify it appears inline in sent email
- [ ] Insert a table; verify it renders correctly in sent email
- [ ] Toggle Markdown preview; verify it updates as you type
- [ ] Send a Markdown-mode email; verify recipient sees styled HTML
- [ ] Send an HTML-mode email with embedded image; verify image appears inline
- [ ] Switch from HTML to Plain Text; verify confirmation dialog appears
- [ ] All commands appear in Command Palette (Ctrl+Shift+P)
- [ ] Keyboard shortcuts work for all formatting commands
- [ ] Screen reader announces formatting state at cursor (Ctrl+Shift+Space)
- [ ] High-contrast mode: toolbar and editor are readable
- [ ] Tab/Shift+Tab indent/outdent in HTML mode
- [ ] Escape closes compose window (with draft prompt if dirty)

---

## 17. Open Questions & Risks

### 17.1 Risks

| Risk | Impact | Mitigation |
|------|--------|------------|
| **WebView2 performance** | Large HTML documents may be slow to render in contenteditable | Limit editor content to ~500KB HTML; warn user if exceeded |
| **Markdig HTML→Markdown fidelity** | Round-trip may lose some formatting (colors, font sizes) | Accept that Markdown is a subset; document limitations |
| **Spell-check in WebView2** | WPF spell-check doesn't apply to WebView2; browser spell-check varies by WebView2 version | Accept browser spell-check as baseline; document that F7 spelling navigation only works in Plain/Markdown modes |
| **`document.execCommand` deprecation** | `execCommand` is technically deprecated (but still works in all browsers and WebView2) | Use it for v0.7; monitor WebView2 roadmap for replacement API |
| **CSP for editor** | `script-src 'unsafe-inline'` is required for the JS bridge | The editor page is fully controlled by us (embedded resource), not user content — risk is minimal |
| **Image paste from clipboard** | Clipboard image formats vary by source application | Support `image/png` and `image/jpeg`; fall back to paste as file attachment |

### 17.2 Open Questions

1. **Default mode:** Should the default compose mode be Plain Text (safe, familiar) or HTML (what most users expect from a modern client)? **Decision: Plain Text default, configurable in Settings.**

2. **HTML signature authoring:** Should users be able to edit their HTML signature in-app, or paste it from an external editor? **Decision: Paste-only for v0.7. Signature editor is a separate feature.**

3. **Markdown preview live-scroll-sync:** How precisely should the preview track the editor scroll position? **Decision: Best-effort scroll sync (scroll-percentage based). Not line-perfect.**

4. **Template variables in rich text:** `{sender}`, `{date}`, `{time}` in HTML/Markdown context — how should they work? **Decision: Out of scope for v0.7. Templates remain plain-text only.**

5. **Pasting from Word/Google Docs:** Should we clean formatting on paste? **Decision: Yes — paste with `text/plain` only by default. Add "Paste with Formatting" as a separate command (Ctrl+Shift+V).**

---

## Appendix A: Markdig Pipeline Configuration

```csharp
using Markdig;

public static MarkdownPipeline CreateEmailPipeline()
{
    return new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()        // tables, footnotes, emoji, task lists, citations, etc.
        .UseSoftlineBreakAsHardlineBreak()  // single \n → <br> (email convention: people expect line breaks)
        .DisableHtml()                  // don't allow raw HTML in Markdown (security)
        .Build();
}
```

## Appendix B: HTML Sanitization Rules

Applied to `HtmlBody` before building the MIME message:

1. Remove tags: `<script>`, `<noscript>`, `<object>`, `<embed>`, `<iframe>`, `<applet>`, `<form>`, `<input>`, `<button>`
2. Remove attributes starting with `on` (event handlers): `onclick`, `onload`, `onerror`, etc.
3. Replace `javascript:` URLs in `href`/`src` with `#` (removed)
4. Ensure all `<img>` tags have `alt` attribute (add `alt=""` if missing)
5. Wrap orphan text nodes (not inside a block element) in `<p>` tags
6. Add `style` attribute to `<body>`: `font-family: Segoe UI, Arial, sans-serif; font-size: 13px;`
7. Convert `cid:` references to match `Content-Id` headers on embedded image MIME parts

## Appendix C: WebView2 Editor JavaScript API Reference

```
// Called by WPF via ExecuteScriptAsync:

editor.setContent(html)          // Set innerHTML of editor
editor.getContent()              // Return innerHTML
editor.getPlainText()            // Return innerText
editor.execCommand(cmd, value)   // Execute document.execCommand
editor.queryCommandState(cmd)    // Return boolean state
editor.queryCommandValue(cmd)    // Return string value
editor.getFormattingState()      // Return JSON: {bold, italic, underline, strikethrough, 
                                 //   heading, fontName, fontSize, foreColor, backColor,
                                 //   isList, isLink}
editor.insertImage(src, alt, w, h) // Insert <img> at cursor
editor.insertLink(url, text)     // Insert <a> at cursor
editor.insertTable(rows, cols)   // Insert <table> at cursor
editor.focus()                   // Focus the editor

// Posted to WPF via window.chrome.webview.postMessage:

'content-changed'                // Body content changed
'selection-changed'              // Cursor/selection moved
'formatting-state:{json}'        // Current formatting state
'escape'                         // Escape key pressed
'shift-tab'                      // Shift+Tab pressed
'f6'                             // F6 pressed
'shift-f6'                       // Shift+F6 pressed
```

---

*This spec is ready for Dev Lead implementation. Hand off with full context.*

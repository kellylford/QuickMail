# Compose Editor — Review and Prioritized Plan

Status: draft for Kelly's review, 2026-09-17. Nothing here is implemented yet.
Tracking issue: [#729](https://github.com/kellylford/QuickMail/issues/729).

Goal: support the things people commonly put in email today, and make each of them
produce accessible content. This is not a word processor. Every item below either
adds structure a recipient's assistive technology can use (headings, quotes, images
with alt text, table headers) or protects that structure (paste, checking before send).

Each phase below is a plan, not a spec. Before any phase is built it needs a full spec
per CLAUDE.md: a keyboard walkthrough for every path, the infrastructure-change list,
and an out-of-scope section. The walkthroughs here are sketches. Where they mention
speech, they list only the announcements QuickMail itself makes. What a screen reader
says on its own is for Kelly to confirm by listening.

---

## Part 1 — What the editor offers today

### Modes

- Three modes: Plain Text, Markdown, HTML. Switch with Ctrl+Shift+1/2/3, the View menu,
  or the mode combo box in the status row.
- Plain Text and Markdown edit in a `TextBox` (`BodyBox`). HTML edits in a native WPF
  `RichTextBox` (`RichBodyBox`), which was chosen over WebView2 `contenteditable` on purpose
  so screen readers stay in their normal editing mode.
- The default mode for new messages is a setting. Drafts reopen in the mode they were
  saved in. Templates are always plain text.
- Switching from a rich mode to Plain Text asks for confirmation.

### Formatting commands (HTML and Markdown)

| Command | Key | Markdown mode |
|---|---|---|
| Bold | Ctrl+B | inserts `**` |
| Italic | Ctrl+I | inserts `*` |
| Underline | Ctrl+U | HTML only; Markdown announces it is unavailable |
| Strikethrough | Ctrl+Shift+X | inserts `~~` |
| Heading 1, 2, 3 | Ctrl+Alt+1, 2, 3 | inserts `#`, `##`, `###` |
| Bullet list | Ctrl+Shift+L | inserts `- ` |
| Numbered list | Ctrl+Shift+N | inserts `1. ` |
| Indent or outdent a list item | Tab, Shift+Tab | yes |
| Insert link | Ctrl+L | inserts `[text](url)` |
| Clear formatting | Ctrl+Space | yes |
| Announce formatting at cursor | Ctrl+T | yes |
| Show formatting as a list | Ctrl+Shift+T | yes |
| Preview | F8 | opens a separate, focusable WebView2 window |

- Every command is in the Format menu, the command palette, and (for HTML) a toolbar.
- Every command announces its result ("Bold on", "Heading 2") as a Result announcement.
- In HTML mode, moving into a different kind of block announces it ("Heading 2",
  "Bullet list item, level 2", "Quote") when the "announce formatting while navigating"
  setting is on.
- Spelling works in both editors: F7 dialog, Ctrl+F7 and Ctrl+Shift+F7 inline, Alt+1/2/3 to
  accept a suggestion.

### What the converter already handles, but the user cannot create

`Helpers/RichTextDocumentConverter.cs` moves content between HTML, the rich editor, and
Markdown. It already supports more than the commands above can create:

- Headings 4, 5 and 6 (tags `H4` to `H6`, sizes, HTML and Markdown output).
- Block quotes (tag `BLOCKQUOTE`; consecutive quote paragraphs become one `<blockquote>`).
- Tables with header cells (`<th scope="col">`) and column alignment.
- Code blocks with a language, inline code, and horizontal rules.
- Images, but only as text: the alt text is shown as ordinary run text and the `src` is
  hidden in `Run.Tag`. An image only gets into the editor through Markdown `![alt](url)` or
  forwarded HTML.

These arrive through Markdown mode or a forwarded message and survive the round trip. In
HTML mode the user can read them but cannot create them.

### Sending

- `Services/MimeMessageBuilder.cs` is the single place a message is built. SMTP, Graph,
  IMAP drafts, POP drafts and sent copies all go through it. That keeps the image work
  below to one place.
- A rich-mode message is sent as `multipart/alternative` (text/plain + text/html). The HTML
  is wrapped in a full document with `lang`, a charset, and the subject as the title.
- There is no `multipart/related`, so a message cannot carry an embedded image.
- The Markdown pipeline is deliberately narrow: pipe tables, strikethrough, autolinks.
  Raw HTML is disabled. Task lists are excluded because they render as unlabeled
  checkboxes.

### Signatures, replies, forwards

- Signatures are plain text only (`AccountModel.Signature`), added with a `-- ` separator.
- Replies quote the original as plain text with `> ` in front of each line
  (`ComposeViewModel.CreateReply`). Issue #262 already tracks improving this.
- Forwards of HTML mail put the original inside an HTML `<blockquote>` and open in HTML mode.

---

## Part 2 — Gaps found in the review

Every item comes from reading the code. The ones marked **verify** have not been
confirmed in the running app yet.

1. **No headings below level 3.** No command, menu item, toolbar button or key for 4 to 6,
   although the converter supports them.
2. **No way to mark text as a quote.** The block type exists (it is even announced as
   "Quote" while navigating) but nothing creates or removes it.
3. **In HTML mode, replies send literal `>` characters.** A reply starts as plain text with
   `> ` on each quoted line. When the default mode is HTML, `SetMode` turns that into
   paragraphs with `PlainTextToHtml`, so the recipient gets lines that begin with a `>`
   character instead of a quote. Markdown mode does not have this problem, because
   `> ` is already Markdown quote syntax. (Related: #262.)
4. **No image insertion of any kind.** There is no Insert Image command, no pasting an
   image, and no embedding.
5. **Existing images do not look like images.** An image is a run of its alt text with
   nothing to set it apart, so it cannot be told apart from the words around it. Ctrl+T
   does not report it, and an empty alt text shows as "(image)". There is no way to see or
   change the alt text as alt text.
6. **Images in forwarded messages break.** A forwarded HTML message keeps
   `<img src="cid:…">`, but the original's image parts are not attached, so the recipient
   gets a broken image.
7. **Paste may silently lose content (verify).** `RichBodyBox` uses WPF's default paste,
   which accepts rich text. The converter has no case for `InlineUIContainer` or
   `BlockUIContainer`, so a pasted picture would be dropped when the message is sent. A
   heading pasted from Word comes in as large bold text without our heading tag, so it
   would go out as bold text, not a heading. Fonts and colors are dropped with no
   warning. None of this has been tried in the running app.
8. **Clear Formatting only clears the first and last paragraph** of a selection
   (`ComposeWindow.xaml.cs` `ClearFormatting`), unlike headings, which use
   `GetSelectedParagraphs`. It also does not reset a quote's indent and color.
9. **A list inside a quote loses the quote in Markdown.** `EmitBlocksMarkdown` only treats
   quoted paragraphs as quotes. The HTML output handles it correctly.
10. **Nested quotes flatten** to one level when loaded (the tag is a single value).
    Deep reply chains therefore lose their nesting when forwarded.
11. **Links can be inserted but not edited or removed.** Ctrl+T does not say the caret
    is in a link.
12. **Signatures cannot contain a link or any formatting.**
13. **Tables can be received but not created.** No insert, and no adding a row or column.
14. **Enter at the end of a heading (verify).** WPF may copy the heading's size and
    weight to the new paragraph but not our `Tag`. The next line could then look like a
    heading but go out as normal text.
15. Small: the comment on `GetFormattingParts` still names Ctrl+Shift+Space and
    Ctrl+Alt+Space. The real keys are Ctrl+T and Ctrl+Shift+T.

---

## Part 3 — The plan, in priority order

Priority rule: first finish what the editor half-supports already (cheap, and the gaps
are visible). Then the largest missing capability, images. Then the things that protect
accessible structure. Then everything else.

### Phase 1 — Headings 4 to 6, quotes, and real quotes in replies

Small and low-risk. The converter already round-trips all of it, so this phase is mostly
commands, menu items and announcements. Ship it as one release.

**1.1 Headings 4, 5, 6 and Normal text**

- New commands `compose.heading4/5/6`, keys Ctrl+Alt+4/5/6.
- New command `compose.normalText`, key Ctrl+Alt+0. It turns any heading, quote or code
  block back into a normal paragraph. Today the only way out of a heading is to apply the
  same level again.
- Markdown: `MarkdownEditing.ToggleHeading` already takes any level.
- Announcements (Result): "Heading 4", "Normal text". These are the same wording as today.
- Fix gap 14 at the same time: Enter at the end of a heading gives a normal paragraph,
  which is what Word and Outlook do.
- Keyboard layouts: where AltGr is Ctrl+Alt, Ctrl+Alt+digit can type a character. This
  risk already exists for 1 to 3. The palette and menu are the fallback, and the keys can
  be reassigned.

**1.2 Quote**

- New command `compose.quote`, recommended key **Ctrl+Shift+9** (Gmail's composer uses the
  same key). It is a toggle on the selected paragraphs, in both HTML and Markdown (`> `).
- Enter inside a quote continues the quote. Enter on an empty quoted line ends the quote.
  This is the usual convention; please confirm it is what you want.
- Announcements (Result): "Quote on", "Quote off".
- Fix gaps 8, 9 and 15 in the same change.

**1.3 Real quotes in replies (closes #262)**

- When a reply opens in HTML mode, build the quoted original as a `<blockquote>` with the
  attribution line ("On … wrote:") above it. When the original has HTML, reuse
  `BuildForwardedHtmlBlock` and `StripHtmlWrappers`, as the issue suggests.
- Markdown mode keeps `> ` lines, which are already correct.
- Plain Text mode is unchanged.
- Preserve nested quote levels (gap 10). The block tag needs a depth (for example
  `BLOCKQUOTE:2`) and the navigation announcement becomes "Quote, level 2", the same
  pattern lists use today.

**1.4 Formatting toolbar and menu**

- With six heading levels plus Normal text and Quote, three H buttons no longer fit.
  **Decision for you:** either replace H1, H2, H3 with one "Paragraph style" combo box
  (Normal, Heading 1 to 6, Quote, Code block), which also shows sighted users the style
  at the caret, or keep buttons and add H4 to H6.
- Menu: a Format → Heading submenu (Normal text, Heading 1 to 6), with Quote as its own
  item in the Format menu. `InputGestureText` must match the registered keys.

**1.5 Better "what is here" reporting**

- Ctrl+T and Ctrl+Shift+T add the facts that are missing: in a link (and its address),
  inline code, quote level, and, after Phase 2, image.

### Phase 2 — Images with alt text

The largest phase and the most important new capability. Ship it only when the image
also survives drafts, the Outbox, and forwarding. An image that disappears from a saved
draft is worse than having no image support.

**2.0 Listening spike first (a decision only you can make)**

How an image should appear inside the rich editor is a question about what you hear. Per
the "control listening spike" practice, build a small harness with several candidates and
listen before choosing:

- A. Today's model made distinct: a run such as "Image: *alt text*", read-only as a unit and
  styled so it stands out, with the picture not drawn at all.
- B. An `InlineUIContainer` holding a real `Image` with `AutomationProperties.Name` set to the
  alt text.
- C. A hybrid: a small thumbnail drawn next to an "Image: alt text" run.

Things to judge: how the image is read when arrowing by character, word and line; whether
Backspace and Delete remove it as one unit; whether the spelling check skips it; and
whether it shows up in say-all.

**2.1 Insert Image**

- Command `compose.insertImage`, recommended key **Ctrl+Shift+I**, in a new **Insert**
  menu with Link, Image, and later Table and Horizontal line. Insert Link moves there and
  keeps Ctrl+L.
- A file picker first, then an **Image description** dialog:
  - "Alternative text" box, focused when the dialog opens.
  - A "Decorative image (no description)" check box.
  - OK is disabled until there is alt text or Decorative is checked. Leaving it empty is
    never allowed without saying so.
  - The file name is shown as context. It is never used as the alt text.
- The dialog has an editable text box and opens over the compose window. Use modeless
  `Show()` following the GrabAddresses rule, even though compose has no live WebView2.
  The preview window can be open at the same time.
- Announcement (Result): "Image inserted." When the image is decorative: "Decorative image
  inserted."

**2.2 Image properties**

- Command `compose.imageProperties`, recommended key **Alt+Enter** with the caret on an
  image. This matches Alt+Enter for Properties elsewhere in QuickMail. It opens the same
  dialog, pre-filled, plus a Remove image button.
- With the caret anywhere else, Alt+Enter announces "No image at the cursor." (Result)

**2.3 Paste and drag-drop**

- Pasting a picture from the clipboard, or dropping an image file on the body, opens the
  Image description dialog. Today, dropping a file anywhere on the window attaches it.
  Dropping on the body in HTML or Markdown mode would embed an image, while other files,
  or any drop in Plain Text mode, still attach. **Decision for you.**

**2.4 Sending: `multipart/related`**

- A new store on the compose side holds the inline images (bytes, content type, content ID,
  alt text, decorative). Both editors refer to an image as `cid:…`.
- `MimeMessageBuilder` produces:
  `mixed[ alternative[ text/plain, related[ text/html, image parts ] ], attachments ]`,
  with the image parts marked `Content-Disposition: inline`.
- The HTML gets `alt` (empty when decorative), `width` and `height`, and
  `style="max-width:100%"`.
- The text/plain part gets "[Image: alt text]". Decorative images are left out.
- `docs/privacy.html` does not change: no new outbound host is contacted.

**2.5 Round trips that must not lose the image**

- Drafts: when a draft is reopened, its related image parts go back into the inline store,
  not into the attachment list. This needs checking against IMAP, Graph, and POP drafts.
- The Outbox: queued messages persist the inline store.
- Forwarding: carry the original's `cid:` image parts into the new message (fixes gap 6).
- Replies: the same, once 1.3 has quoted HTML in replies.
- Mode switches: HTML and Markdown share the store (`![alt](cid:…)` in Markdown). Switching
  to Plain Text states how many images will be removed in the existing confirmation.

**2.6 Markdown and Plain Text modes**

- Markdown: Insert Image inserts `![alt text](cid:image1)` and keeps the bytes in the
  store. An image typed with a web address keeps working as it does today.
- Plain Text: Insert Image offers to add the file as an attachment instead, and says why.

**2.7 Preview and size**

- F8 preview maps `cid:` to `data:` so the image appears. The preview's security policy
  already allows `data:` images.
- Large photos: offer to resize images wider than a set width, and warn once when the
  message passes a size limit. The limits are yours to set.

**Out of scope for Phase 2:** images by web address (many recipients block remote images;
Markdown `![](https://…)` keeps working), cropping or rotating, wrapping text around
images, and animated GIF handling beyond passing the file through. AI-written
alt-text suggestions are deferred: they would send the image to an outside service, would
need a privacy.html change, and could connect to Image Description Toolkit. That is a
separate decision.

### Phase 3 — Paste that keeps structure

First confirm gap 7 in the running app. Then:

- Plain paste (Ctrl+V) in HTML mode keeps meaning and drops look. Headings stay headings
  (mapped from Word's heading styles), and lists, links, bold, italic, and tables with
  their header rows are kept. Fonts, sizes and colors are dropped. A pasted picture goes
  through the Phase 2 description dialog. It is never dropped silently.
- **Paste as plain text**: command `compose.pastePlain`, recommended key Ctrl+Shift+V
  (Ctrl+Shift+V is the main window's View menu, but compose has its own registry).
- Markdown mode: paste stays as text, as it is today.
- If anything cannot be kept, say so once (Result), for example "Pasted. 2 images need
  descriptions."

### Phase 4 — Check Accessibility

An on-demand check, with an optional check before sending. This is where QuickMail can
go beyond other mail clients.

- Command `compose.checkAccessibility` in the Tools menu and the palette. No default key is
  proposed (F7 and its variants are taken); you choose.
- The results are a list, like Show Formatting. Enter on an item closes the list and puts
  the caret on the problem.
- Candidate checks (please choose which ones to keep):
  - An image with no alt text and not marked decorative. This can only happen with
    content that came from outside the editor, such as a paste, a forward or an old draft.
  - Link text that says nothing on its own ("click here", "here", "link", "read more").
  - A skipped heading level (Heading 2 followed by Heading 4), or an empty heading.
  - A table with no header row.
  - Alt text that looks like a file name ("IMG_2041.jpg").
- Setting: "Check accessibility before sending" (off or on by default is your call). When a
  problem is found, Send asks "Send anyway / Review" instead of blocking.

### Phase 5 — Links

- Ctrl+L with the caret in a link opens the dialog pre-filled to edit it, with a Remove
  link button.
- The link part of 1.5 (Ctrl+T reports the link) ships in Phase 1.

### Phase 6 — Formatted signatures

- Store an optional HTML signature for each account next to the plain one. Edit it in
  Manage Accounts with the same rich editor (headings off; bold, italic, links and one
  small image allowed).
- New messages in HTML or Markdown mode get the formatted signature. Plain Text mode gets
  the plain one.
- An image in a signature must have alt text, which the Phase 2 dialog enforces.

### Phase 7 — Tables

Tables are less common in personal mail but common in work mail. Do a listening spike
first: how usable is moving between cells in a WPF `RichTextBox` table?

- Insert Table: rows and columns, with "First row is a header" checked by default, so
  the table goes out with `<th scope="col">`.
- In a table: Tab and Shift+Tab move between cells, and Tab in the last cell adds a row.
  Commands to insert or delete a row or column.
- Ctrl+T reports "Row 2 of 4, column 3 of 3, header: Price" (exact wording to your ear).
- Markdown already supports pipe tables. Insert Table in Markdown writes one.

### Phase 8 — Smaller inserts

- Horizontal line (Insert menu).
- Inline code and Code block (Format menu). The converter already supports them. They
  matter to technical correspondents and cost little.

---

## Part 4 — Considered and not recommended now

- **Fonts, font sizes, text color, highlight, alignment.** These are the step toward a
  word processor that this plan avoids. Color also tends to carry meaning that a screen
  reader never hears. If highlight is ever added, it should be semantic (`<mark>`) and
  reported by Ctrl+T.
- **Emoji picker.** The Windows emoji panel (Win+period) already covers it. Verify that it
  inserts into both editors.
- **Right-to-left text, marking the language of a passage, find and replace, rich
  templates.** These are real features, but not editor formatting. Track them separately.
- **Moving to WebView2 `contenteditable`.** The original rich-compose spec (`rich-compose-pm-dev-spec.md`) planned this, and
  the decision to use `RichTextBox` was made for good reason. Nothing in this plan needs to
  reopen it.

---

## Part 5 — Infrastructure summary

New commands for the compose `CommandRegistry` (all Category Compose; the keys proposed
are free in the compose registry today):

| Id | Key | Phase |
|---|---|---|
| `compose.heading4` / `5` / `6` | Ctrl+Alt+4 / 5 / 6 | 1 |
| `compose.normalText` | Ctrl+Alt+0 | 1 |
| `compose.quote` | Ctrl+Shift+9 | 1 |
| `compose.insertImage` | Ctrl+Shift+I | 2 |
| `compose.imageProperties` | Alt+Enter (on an image) | 2 |
| `compose.pastePlain` | Ctrl+Shift+V | 3 |
| `compose.checkAccessibility` | none proposed | 4 |
| `compose.insertTable` + row/column commands | none proposed | 7 |
| `compose.insertHorizontalLine`, `compose.inlineCode`, `compose.codeBlock` | none proposed | 8 |

- Menus: a new Insert menu (Link moves there), and a Format → Heading submenu.
- F6 ring: no change.
- New windows: the Image description dialog (modeless), the Check Accessibility results
  list, and Insert Table. Each needs the New Window Checklist.
- Announcements: every formatting result stays in the Result category. The only Hint
  proposed is the existing "requires Markdown or HTML mode" pattern, when image insertion
  is refused in Plain Text mode.
- Tests: extend `RichTextDocumentConverterTests` and `MarkdownRoundTripTests` for nested
  quotes, list-in-quote, `cid:` images and decorative images; new
  `MimeMessageBuilder` tests for `multipart/related`; draft round-trip tests that
  keep inline images out of the attachment list; `SelectorItemAccessibilityTests` for any
  new combo box (for example the Paragraph style combo).
- Docs: USER-GUIDE Formatting section and Compose Window key table, KEYBOARD-SHORTCUTS.md,
  and release notes for each phase.

## Part 6 — Decisions needed from you

1. Toolbar: one "Paragraph style" combo box, or more H buttons? (1.4)
2. The quote key: Ctrl+Shift+9 or something else? And the Enter behavior inside a quote. (1.2)
3. How an image appears in the editor: chosen after the listening spike. (2.0)
4. Insert Image key (Ctrl+Shift+I?) and Alt+Enter for image properties. (2.1, 2.2)
5. Whether dropping an image on the body embeds it instead of attaching it. (2.3)
6. Image size limits and whether resizing is offered. (2.7)
7. Which accessibility checks to keep, and whether the check before sending is on by
   default. (Phase 4)
8. Whether Phase 3 (paste) should move ahead of Phase 2 once gap 7 is confirmed. If
   paste is losing headings and pictures today, it is a data-loss bug, not an enhancement.

# Saving and Exporting Messages — Plan for Review

> Tracking issue: #728

> Status: **Implemented for 0.8.47** (phases 1–4; phase 5, opening .eml files, is not built).
> Kelly answered the questions in §8 on 2026-09-18; §9 records what was built from those answers.

---

## 1. The problem

QuickMail has no way to save a message. The only thing you can save today is an attachment
(**Save…** / **Save All…** on the attachment list). A message you want to keep outside your mail
account — a receipt, a confirmation, evidence for a dispute, something to send a colleague who
is not on email, something to archive before you leave a job — has nowhere to go.

Verified in the code:

| What | Where | Today |
|---|---|---|
| File menu, main window | `Views/MainWindow.xaml:361` | New Message, Manage Accounts, Settings, Exit. No Save. |
| File menu, message window | `Views/MessageWindow.xaml:29` | Close Window only. |
| Mail backend interface | `Services/IMailService.cs` | Fetches parsed parts (plain, HTML, attachments). **No call returns the original message.** |
| IMAP | `Services/ImapMailService.cs:431` | Fetches the body structure, then individual parts. Never downloads the whole message. |
| Microsoft Graph | `Services/GraphMailService.cs:314` | Already downloads the original (`/me/messages/{id}/$value`), but only for meeting invites. |
| POP3 | `Services/LocalStoreService.cs:242` | Keeps the original bytes **only when the message has attachments** (#128). |
| Local cache | `MessageDetail` table | Plain body, HTML body, addresses, attachment list. Not the original. |
| Print | — | None anywhere. |
| Ctrl+S | `CommandRegistry` | Free in the main window and the message window. (Ctrl+Shift+S is search.) |

So the one piece of real work underneath every option below is: **get the original message
back from the server.** Everything else is formatting and file dialogs.

## 2. What people mean by "save a message"

Four different needs, and they want different files:

1. **Keep the message itself** — complete and faithful, attachments included, openable later in
   any mail program. That is an `.eml` file: the message exactly as the server holds it.
2. **Read it outside a mail program** — a plain text file: headers at the top, the body below.
   Opens in Notepad, any editor, any device. For a screen-reader user this is often the most
   useful format of all. Or a web page, when the message's formatting — tables, headings, links —
   matters and should be kept.
3. **Hand it to someone or file it as a document** — PDF, or print to paper.
4. **Take a whole folder with me** — bulk export or backup. A different feature (see §7).

## 3. Recommendation, in phases

### Phase 1 — Save as `.eml` (the foundation)

- **Message → Save As…** (Ctrl+S) in the main window and the message window; also in the message
  list's context menu and the command palette.
- Saves the original message, byte for byte, as the server holds it. Attachments are inside it.
- **Works on a multi-selection:** with several messages selected, it asks for a folder instead of
  a file name and writes one `.eml` per message.
- Default file name: `2026-09-17 1432 - Sender Name - Subject.eml`, run through the existing
  `AttachmentSafety.SanitizeFileName`, with ` (2)` added rather than overwriting.
- Remembers the last folder used.

Why first: it is the only format that loses nothing, every mail program and Windows itself can
open it, and it builds the "get the original message" plumbing every later phase reuses — including
**Forward as Attachment**, which the forward spec already lists as a separate future feature.

### Phase 2 — Save as text

- A **Save as type** choice in the same dialog: *Email message (.eml)* or *Text file (.txt)*.
- The text file is a short header block — From, To, Cc, Date, Subject, and the attachment names —
  then the body. The body is the sender's own plain-text part when there is one, otherwise text
  extracted from the HTML. Both paths already exist in `MessageBodyHtmlBuilder` (the plain-text
  view uses them).
- Needs no server round-trip, so it also works offline for any message whose body is cached.

### Phase 3 — Save as HTML

- A third *Save as type* choice: *Web page (.html)*.
- One self-contained file: the same header block as the text file, then the body as the reading
  pane renders it — formatting, tables, headings, lists, and working links kept. Opens in any
  browser, on any device, with no mail program.
- Ranked above PDF because it keeps the message's structure in a form screen readers and
  browsers already handle well, it needs nothing we have to verify first, and it is the page PDF
  and printing are made from anyway — so this phase builds most of phase 4.
- **Safety:** the saved page keeps the reading pane's sanitizing — no scripts, no forms, no active
  content — and its Content-Security-Policy, so opening it in a browser is no riskier than reading
  it in QuickMail. Remote images are never fetched.
- Built from `MessageBodyHtmlBuilder`, which already produces this page; it needs a variant that
  leaves out the reading pane's own chrome (theme CSS, the WebView2 keyboard relay script) and
  uses a neutral, printable style instead.
- Needs no server round-trip, so it works offline wherever the body is cached.
- Open question: pictures embedded in the message itself (inline `cid:` images, such as a logo or
  a screenshot the sender pasted). The reading pane shows no images at all today (`img-src 'none'`).
  The saved file could match that, or carry the embedded pictures inside the file. (§8, question 7.)

### Phase 4 — Print and Save as PDF

- **Message → Print…** (Ctrl+P) and a *PDF (.pdf)* choice in Save As.
- WebView2 can print and write PDFs itself (`ShowPrintUI`, `PrintToPdfAsync`), from the phase 3
  page. No new library.
- **Needs a listen before we commit to it:** whether the PDF WebView2 writes is tagged, and so
  readable with a screen reader, is something to test and hear, not assume. If it is not, PDF is
  a print-for-others format only and HTML or text are the accessible ones.

### Phase 5 — Open a saved `.eml` (optional, later)

Saving is only half of it if QuickMail cannot open what it saved. Opening a `.eml` in a read-only
message window, and registering the file type, completes the round trip. The mail-client
registration spec (#241) already deferred the file association; this would pick it up.

## 4. The hard part: getting the original message

A new method on `IMailService`, something like `GetOriginalMessageAsync(account, folder, id)`
returning the bytes. Per backend:

| Backend | How | Cost |
|---|---|---|
| IMAP | MailKit `folder.GetStreamAsync(uid)` — downloads the whole message. Must use `BODY.PEEK` so saving does not mark the message read. | One download, attachments included. |
| Graph | `/me/messages/{id}/$value` — already written and working for invites. | Same. |
| POP3, message with attachments | The stored bytes. | Free, works offline. |
| POP3, no attachments, still on server | Download it again. | One download. |
| POP3, no attachments, no longer on server | **The original no longer exists anywhere.** Only a rebuilt copy is possible (see §8, question 3). | — |

Offline, IMAP and Graph cannot produce an `.eml` at all — the cache holds the readable parts, not
the original. The honest behavior is to say so and offer the text file, rather than quietly
writing something that is not the original. (§8, question 2.)

Side benefit: POP3 could start keeping the original for **every** message, not just those with
attachments. That closes the gap in the table above going forward, at some disk cost.

## 5. Keyboard walkthrough (sketch — to be finished in the spec)

Single message:

1. Focus is on a message in the list. User presses Ctrl+S.
2. The Windows Save dialog opens, focus in the file name box, which reads
   `2026-09-17 1432 - Jane Smith - Your order has shipped`. Save as type is *Email message (.eml)*.
3. User presses Enter. The dialog closes; focus returns to the same message in the list.
4. Status bar: "Saved Your order has shipped." Announced as a Result.

Several messages:

1. User selects five messages and presses Ctrl+S.
2. A folder picker opens. User chooses a folder and presses Enter.
3. Focus returns to the list with the selection unchanged. Status: "Saved 5 messages." (Result.)
   If some failed: "Saved 3 of 5 messages. 2 could not be downloaded." (Result.)

Offline:

1. User presses Ctrl+S while the account is offline.
2. The Save dialog opens with *Text file (.txt)* already selected, because that is the only format
   that can be produced — or, if we prefer, a message explains why `.eml` is not available.
   (§8, question 2.)

## 6. Infrastructure (preliminary)

- **Commands** (`CommandRegistry`, category `Mail`): `mail.saveAs` (Ctrl+S) and, in phase 4,
  `mail.print` (Ctrl+P). The message window gets matching local commands and palette entries.
- **Menus:** *Save As…* and *Print…* on the Message menu of both windows, and on the message list
  context menu. `InputGestureText` matches the registered keys.
- **F6 ring:** unchanged. Only system dialogs are added.
- **Announcements:** one Result announcement per save (success, partial, failure). Progress for a
  large multi-message save as Status.
- **Service:** `IMailService.GetOriginalMessageAsync` on all three backends and the router; a stub
  in `StubServices.cs`.
- **VM → View:** the existing `SaveFilePathRequested` / `SaveFolderPathRequested` callbacks already
  used by attachment saving. No dialogs in the VM.
- **Privacy page:** no change — no new host, no new scope.

## 7. Out of scope for this plan

- **Exporting a whole folder or account** (mbox, a folder of `.eml` files) and backup. That belongs
  with #507 (move settings to another computer) and #129 (backing up), and should be designed with
  them.
- **Importing** `.eml` or mbox into an account.
- **Outlook `.msg` files.** A proprietary format; `.eml` covers the need.
- **Drag a message out to Explorer or the desktop.** Nice, but a mouse gesture with real WPF
  plumbing behind it; Save As covers the keyboard path.
- **Forward as Attachment.** Not in this plan, but phase 1 builds what it needs.

## 8. Questions for Kelly

1. **Phases.** Priority order is `.eml`, text, HTML, then print/PDF. Should the first release be
   `.eml` + text, or `.eml` + text + HTML together (they share the dialog and the header block)?
Kelly: If it makes sense to group morework, then do so.


2. **Offline.** When the original cannot be downloaded, should Save As fall back to the text file
   automatically, or refuse `.eml` with an explanation and let you choose?
Kelly: Refuse with an explanation and let the user choose.

3. **POP3 messages whose original is gone.** Offer a *rebuilt* `.eml` from the cached parts
   (clearly named as rebuilt), offer text only, or refuse? And should POP3 keep the original of
   every message from now on?
Kelly: Pop should keep the  full message. No rebuilding. Either way have the message or we don't.

4. **Ctrl+S.** Fine as the default key in both the main window and the message window?
Yes. We should likely also have a save as option.

5. **Text file contents.** Is From / To / Cc / Date / Subject / attachment names the right header
   block, in that order? Anything to add (Reply-To, account, folder)?
Kelly: Add as much as you can beyond internet headers like message path. Human understandable text.

6. **File name.** Is `date time - sender - subject` the right default, or would you rather it lead
   with the subject?
Kelly: Start with subjecgt


7. **Pictures in saved HTML.** Leave them out, as the reading pane does today, or carry the
   message's own embedded pictures inside the saved file? (Remote images stay blocked either way.)

Kelly: leave them out but find or file an issue that weneed to improve image handling. Longer term, if the images are there, we should save them but I think we have a different work item to give the user the option to download images so that should understand this work also.



## 9. As built (2026-09-18)

- **Save (Ctrl+S) and Save As (F12) are separate commands** (answer 4). Save writes the default format
  into the save folder with no dialog; Save As is the Save dialog with a type list. Both act on the
  whole selection, or a group header's messages. Print (Ctrl+P) acts on one message. All three are on
  the File menu of both windows, in the palette, and relayed out of both message bodies.
- **Every format shipped together** (answer 1): .eml, text, web page, and PDF, plus Print.
- **Offline or gone: refuse, explain, let the user choose** (answer 2). Save As reopens on Text.
- **POP3 keeps every original; nothing is rebuilt** (answer 3). Older POP3 messages with no kept
  original are downloaded again by UIDL if the server still has them, and kept from then on.
- **Details block** (answer 5): subject, from, reply-to (when different), to, cc, date, invitation
  (summary, when, where, organizer), account, folder path, status (read, flag name, replied,
  forwarded), attachments with sizes, and when it was saved.
- **File name leads with the subject** (answer 6): `Subject - Sender - yyyy-MM-dd HHmm.ext`.
- **Pictures are left out of the web page and PDF** (answer 7). Tracked with remote-image loading in #508.
- **PDFs are tagged.** WebView2's `PrintToPdfAsync` writes an untagged PDF (measured). The DevTools
  protocol's `Page.printToPDF` with `generateTaggedPDF` writes a structure tree with H1, Table, TH and P
  roles. `MessageSavePdfTests` asserts this against a real WebView2.
- **Several messages in Save As** use the standard Save dialog: its title says how many, and the file-name
  box reads "Each message is saved under its own name". The folder and type chosen apply to all of them.
- **Settings → General → Saving Messages**: the format combo, and the save folder with Choose Folder… and
  Use Documents.

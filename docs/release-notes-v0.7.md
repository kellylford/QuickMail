# QuickMail v0.7 Release Notes

## New Features

### Spell check in compose window

The message body now has spell check enabled. Misspelled words are underlined with the standard red squiggle. Press **F7** to jump to the next misspelling; press **Shift+F7** to go back to the previous one.

Spell check is built on the Windows spell-checking engine, the same one used by WordPad and other Windows applications. No configuration needed.

**Screen reader announcements:** As you navigate through the message body with arrow keys, QuickMail automatically detects when the caret enters a misspelled word and announces it through your screen reader. When the "Announce spelling suggestions" setting is on (the default), you hear the word and up to three suggestions — for example, "Misspelling: recieve. receive, relieve, retrieve." When the setting is off, only the misspelled word is announced.

**Quick replacement:** When a misspelling is announced, press **Alt+1**, **Alt+2**, or **Alt+3** to replace the word with the first, second, or third suggestion. QuickMail announces "Replaced with receive." and moves on. This lets experienced users correct errors without opening a context menu.

**Settings:** A new "Announce spelling suggestions" checkbox in **Tools → Settings → Screen Reader Announcements** controls whether suggestions are spoken. Turn it off for faster, quieter spell checking once you know the Alt+number workflow.

### Email signatures

Each account now has a **Signature** field in Account Settings. Enter any plain-text signature — your name, title, phone number, or a full closing block — and QuickMail automatically appends it to the end of every new message, reply, and forward from that account.

Signatures follow the standard `-- \n` convention, so email clients that recognize signature separators will display them correctly. Drafts that are re-opened for editing do not get a duplicate signature — the signature is only added when the compose window first opens.

To set a signature, open **File → Manage Accounts**, select an account, and fill in the Signature field at the bottom of the settings form.

### Calendar invite handling

When someone sends you a meeting invitation (an ICS file attachment), QuickMail now displays an **Event Invitation** card directly in the reading pane — above the message body. The card shows:

- Event title
- Organizer name
- Start and end time (converted to your local time zone)
- Location (when included)
- Full event description

Three buttons let you respond: **Accept**, **Tentative**, or **Decline**. Each generates a proper ICS reply and sends it back to the organizer via SMTP. The entire flow works from the keyboard — Tab to the card, Tab between buttons, Enter to respond.

Screen readers announce the full event summary when the card receives focus, and a confirmation is announced after each response.

### Keyboard Tutorial

A new keyboard tutorial is available off the Help Menu. It allows you to try some of the important keyboard commands for QuickMail and get feedback that you pressed the correct keys.

| Step | Shortcut | What it does |
|------|----------|--------------|
| 1 | **F6** | Cycle focus through all panes |
| 2 | **Ctrl+1** | Focus the account list |
| 3 | **Ctrl+2** | Focus the folder tree |
| 4 | **Ctrl+3** | Focus the message list |
| 5 | **Ctrl+Shift+P** | Open the Command Palette |
| 6 | **Escape** | Close the reading pane or dismiss dialogs |

Each step announces the instruction through your screen reader, waits for you to press the correct key, then confirms success and moves to the next step. Press **Escape** at any time to exit the tutorial.

Once completed, the tutorial does not appear again. You can replay it at any time from **Help → Keyboard Tutorial**.

### Message templates

Save common responses as templates and insert them with a few keystrokes. Templates are stored in `%APPDATA%\QuickMail\templates.json`.

**Inserting a template:** In the compose window, activate the **Insert Template…** button. A searchable list appears — type to filter by title, press **Down** to move into the list, and press **Enter** to insert. The template body is appended to your message.

**Saving a template:** Compose a message you want to reuse, then activate **Save as Template**. The subject line becomes the template title (or "Untitled" if the subject is empty).

**Placeholders:** Templates support three placeholders that are filled in automatically when inserted:

| Placeholder | Replaced with |
|-------------|---------------|
| `{sender}` | Your display name (or email address if no display name is set) |
| `{date}` | Today's date |
| `{time}` | The current time |

Placeholders are case-insensitive — `{Sender}`, `{SENDER}`, and `{sender}` all work.

---

## Improvements

- **Inline spell check announcements** — Screen readers now announce misspelled words automatically as you navigate through the message body with arrow keys, not just when pressing F7.
- **Alt+1/2/3 quick replacement** — Replace a misspelled word with a suggestion in one keystroke. No context menu needed.
- **Spelling suggestions setting** — New "Announce spelling suggestions" option in Settings lets experienced users silence suggestion speech while keeping the Alt+number workflow.
- **Compose window command palette** — Press **Ctrl+Shift+P** in the compose window to open the Command Palette with 11 compose-specific commands (Send, Save Draft, Add Attachments, Insert Template, Save as Template, Cancel, Focus Subject, Focus From, Focus Body, Next/Previous Misspelling).
- **Alt+Y in compose** — Press **Alt+Y** to jump directly to the message body from any field.
- **Tutorial wrong-key feedback** — The keyboard tutorial now announces what key you pressed when it's incorrect (e.g. "You pressed Ctrl+N. Try again."), so you get immediate feedback instead of silence.
- **Tutorial manual-only launch** — The tutorial is now available only from **Help → Keyboard Tutorial**; it no longer auto-launches on first run.
- **Signature auto-insertion** — Signatures are appended to new messages, replies, and forwards automatically. Drafts re-opened for editing do not receive a duplicate signature.
- **ICS parsing** — Calendar invites are detected in `text/calendar` MIME parts and displayed as an inline event card. ICS replies are generated and sent via SMTP.
- **Template picker** — Searchable list dialog for inserting saved templates. Filter-as-you-type with screen reader match count announcements.
- **Template placeholders** — `{sender}`, `{date}`, and `{time}` are replaced automatically when a template is inserted.

---

## Internal

- **Refactored MainViewModel** — Extracted `RebuildActiveGroupView()` to replace 12 repeated if-else chains. Extracted `ReplaceCts()` helper for proper `CancellationTokenSource` disposal.
- **Refactored MainWindow.xaml.cs** — Extracted `GroupedMessageTreeController` to eliminate ~30 near-duplicate event handlers across the three tree views (ConversationTree, SenderGroupTree, ToGroupTree).
- **Centralized ViewMode and Sort serialization** — `ConfigModel.ParseViewMode()`, `ConfigModel.ParseSort()`, and their `ToConfigString()` counterparts now handle all string↔enum conversion in one place, replacing six duplicate switch statements.
- **New service: TemplateService** — JSON file storage for message templates, following the same pattern as `ContactService` and `ViewService`.
- **New model: IcsModel** — Parses ICS calendar files and generates reply messages with proper `PARTSTAT` values.
- **New model: MessageTemplate** — Template data model with title, subject, and body.
- **DI wiring** — `SmtpService` and `TemplateService` are now injected into `MainViewModel` and `ComposeViewModel` respectively.
- Total test count: 235 (v0.6.3) → 341 (v0.7). New test files: `IcsModelTests.cs` (30 tests), `TutorialViewModelTests.cs` (14 tests), `TemplateServiceTests.cs` (15 tests), `TemplatePickerViewModelTests.cs` (15 tests), `ComposeViewModelTemplateTests.cs` (16 tests).

---

## Bug Fixes

- **Tutorial overlay crash** — The `Steps.Count` binding in `TutorialOverlay.xaml` was missing `Mode=OneWay`, causing an `InvalidOperationException` when WPF tried to write to the read-only `ObservableCollection<T>.Count` property. Fixed by adding explicit one-way binding mode.
- **Alt+1/2/3 focus jump** — Replacing a misspelled word with Alt+1/2/3 would move focus to the first error on the page instead of staying on the replaced word. Fixed by suppressing spelling announcements during the programmatic text change so the `SelectionChanged` handler doesn't fire with a stale caret position.

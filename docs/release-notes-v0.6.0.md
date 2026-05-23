# QuickMail v0.6 Release Notes

## New Features

### Mail rules

Mail rules let you define automatic actions that run on incoming messages during background sync. A rule tests each arriving message against a set of conditions and, if everything matches, performs an action — move it to a folder, mark it read or unread, or send it to Trash.

Rules run **locally on your machine** within seconds of messages arriving. No data is sent to any server for processing.

**Opening the Rules Manager**

- **Message → Rules…** from the menu bar
- **Ctrl+Shift+L**
- Command palette (`Ctrl+Shift+P`) → "Manage Rules"

**Conditions** (all checked conditions must match):

| Condition | What it matches |
|-----------|----------------|
| From | Sender address or name contains the text you enter |
| To | Recipients field contains the text you enter |
| Subject | Subject line contains the text you enter |
| Body | Message body preview contains the text you enter |
| Has attachments | Message has at least one attachment |
| Account | Scopes the rule to one account, or leave as "All accounts" |

**Actions:**

| Action | What it does |
|--------|-------------|
| Mark as read | Marks matching messages as read |
| Mark as unread | Marks matching messages as unread |
| Move to folder | Moves matching messages to a folder you choose |
| Delete | Moves matching messages to Trash |

Each condition can be enabled or disabled independently with its checkbox, so you can keep a condition's text without having it contribute to the match.

**Creating a rule from a message**

Select any message, then right-click → **Create Rule from Message…** (or press `Ctrl+Shift+T`). The Rules Manager opens with the sender address and subject already filled in — choose an action and save.

**Testing a rule**

Before saving, press **Test Rule** to see how many of the messages currently shown in your message list would match. The result appears in the status bar at the bottom of the dialog.

**Applying rules to existing mail**

When you save a new or edited rule, QuickMail immediately applies it to your existing cached messages — you don't have to wait for the next sync cycle to see the effect.

**Rules status bar**

The status bar shows how many rules are active, how many are disabled, and when rules last ran. Selecting the rules area in the status bar opens the Rules Manager.

**Keyboard shortcuts**

| Shortcut | Action |
|----------|--------|
| `Ctrl+Shift+L` | Open Rules Manager |
| `Ctrl+Shift+T` | Create rule from selected message |
| `Ctrl+N` | New rule (when Rules Manager is focused) |
| `Escape` | Close Rules Manager |

Rules are stored in `rules.json` in your profile directory and can be inspected or backed up in any text editor.

---

### Profile support

A new `--profileDir <path>` command-line option lets you point QuickMail at a custom data directory instead of the default `%AppData%\QuickMail`. Every data file — accounts, mail cache, configuration, contacts, saved views, rules, and the log — is read from and written to the specified path. The directory is created automatically if it does not already exist.

**Common uses:**

- **Separate work and personal mail** — run two shortcuts, each with a different `--profileDir`, and switch between them without any accounts bleeding across.
- **Store data on a synced drive** — point `--profileDir` at a OneDrive or Dropbox folder to keep your account configuration and settings in sync across machines. (The mail cache itself is large and regenerable, so syncing it is optional.)

```
QuickMail.exe --profileDir "C:\Users\YourName\OneDrive\QuickMail"
QuickMail.exe --profileDir "C:\Users\YourName\AppData\Roaming\QuickMail-Work"
```

Passwords are stored in Windows Credential Manager (system-wide) and are shared across profiles — you do not need to re-enter them when switching.

A `--help` option (also `-h`, `-help`, `/?`) shows a summary of available command-line options and exits without starting the app.

---

## Bug Fixes

- **Messages in regular folders not updating live during sync** — When a regular IMAP folder (such as INBOX) was open and background sync completed, new messages were not added to the message list until you pressed Refresh. Only virtual folders (All Mail, All Inboxes, and so on) were receiving live sync updates. Regular folders now update in real-time the same way virtual folders do.

---

## Internal

- Added `RulesManagerViewModelTests.cs` with 35 tests covering rule creation, deletion, saving, validation (name required, target folder required for Move, at least one condition required for Move and Delete), test-rule dry run, the `UseXCondition` per-condition enable/disable flags, accessibility announcements, and account option population.
- Added `RulesManagerWindow` XAML parse test and `RulesManagerViewModel` construction test.
- `RuleServiceTests.cs` covers load/save round-trip, corrupted file recovery, caching, all five condition types, AND semantics, case-insensitive matching, empty-condition pass-through, account scoping, disabled rule skipping, cancellation propagation, and match counting. 452 lines, 16 tests.
- Total test count: 196 (v0.5.9) → 223 (v0.6).
- `rules.json` is written atomically (write to temp file, rename) so a crash during save cannot corrupt the rule list.
- `RuleService` passes `profile.ProfileDir` to keep rules in the active profile directory when `--profileDir` is used.

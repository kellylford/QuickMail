# QuickMail v0.8.38 Release Notes

## Download

There are four downloads. Take a regular one unless you know your PC has an ARM processor — to check, open **Settings → System → About** and read **System type**.

| Download | When to use |
|----------|-------------|
| **`QuickMail-0.8.38-win.msi`** — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| **`QuickMail-0.8.38-win-arm64.msi`** — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| **`QuickMail.exe`** — standalone portable executable | No installation required. Copy it anywhere and run. |
| **`QuickMail-arm64.exe`** — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 8 runtime — you do not need to install .NET separately.

---

## New: choose which calendar new appointments start on

If most of your appointments belong on one calendar, you no longer have to steer the **Calendar** picker away from **Local Calendar** every time you create one.

In the folder tree, move to the calendar you want — **Local Calendar**, an account, or one of the calendars beneath an account — open its context menu with **Shift+F10** or the Applications key, and choose **Use as Default Calendar for New Appointments**. From then on the appointment editor opens on that calendar. **Clear Default Calendar** on the same menu goes back to the local calendar. Both are also in the Command Palette (**Ctrl+Shift+P**) and can be given keys in **Settings → Keyboard**.

The calendar you picked is marked **(default)** in the folder tree and its name is announced as "…, default calendar", so which one is set is something you can check rather than remember.

Two things it deliberately does not do. It does not change which events you are *looking at* — that is still whatever you have selected in the tree. And it is a starting point, not a rule: the **Calendar** picker in the editor still sends any individual appointment wherever you want. ([#497](https://github.com/kellylford/QuickMail/issues/497))

## Fixed: the calendar's context menu offered mail folder actions

The context menu on the **Calendar** node and everything under it was the mail folder menu — **New Folder**, **Move Folder**, **Set as Archive Folder**, **Delete Folder**. None of them mean anything on a calendar, and every one of them did nothing at all when activated. That menu is now the calendar's own. ([#497](https://github.com/kellylford/QuickMail/issues/497))

## Fixed: Run on Existing Mail has a button again with a Microsoft 365 account

With a Microsoft 365 account, **Tools → Rules** opens the Rules Manager that works one account at a time, and in v0.8.37 that window had no **Run on Existing Mail** control — the only way to reach it was **Ctrl+Shift+P**. It is now a button beside the others, and is also on the rule list's context menu, alongside the Command Palette entry that was already there.

It runs **the account shown in the picker**, not every account. That is the difference you would expect from a window that shows you one account at a time: it never acts on rules for a mailbox you are not looking at. The Rules Manager you get without a Microsoft 365 account lists every account together and still covers them all, unchanged.

The button is turned off when the account in the picker has no enabled QuickMail rules to run — a mailbox whose rules are all server-side has nothing for it to do, because server rules run in Exchange and cannot be applied to existing mail from here. The outcome is written to the window's status line as well as announced, so it is there to read whether or not you have announcements turned on. ([#493](https://github.com/kellylford/QuickMail/issues/493))

## Fixed: "Show field labels in the rules list" now works with a Microsoft 365 account

The checkbox is in **Settings → General** for everyone, but only the Rules Manager you get *without* a Microsoft 365 account was reading it. With one, turning it on changed nothing.

Both windows honor it now. With it on, a rule in the account-at-a-time window reads as "Rule Newsletters, runs on server, status enabled" rather than running the pieces together. The setting is read when the Rules Manager opens, so change it in Settings and then open Rules to hear the difference. ([#493](https://github.com/kellylford/QuickMail/issues/493))

## Fixed: three Settings checkboxes were read out with words that were not their labels

The three **Show field labels in the … list** checkboxes — contact list, calendar event list, rules list — were each read out with "accessible names" tacked on the end, and the contact one called the list "the address book contact list" rather than what the label says. Each is now read out as its own label. ([#493](https://github.com/kellylford/QuickMail/issues/493))

## Changed: Report a Bug files through a QuickMail service

**Help → Report a Bug → Send** works exactly as it did — fill in the report, choose **Send**, and the issue is filed for you. What changed is what happens behind that button.

Until now QuickMail talked to GitHub directly, using a GitHub access token built into the program. A token inside a program you can download is a token anyone who downloads it can pull back out, and every report filed this way arrived on GitHub under the QuickMail author's own account rather than as a report from a user.

Reports now go to a QuickMail service that files them, and that service is the only thing holding any GitHub credential. Issues you send arrive under a QuickMail bot identity. The report itself is unchanged, and still contains no email address or anything else identifying you.

If you are upgrading, QuickMail removes the old credential from Windows Credential Manager the first time it starts. Nothing you need to do. ([#501](https://github.com/kellylford/QuickMail/issues/501))

## Fixed: a meeting invitation opened in its own window had no date or time

With **Settings → Windowing** set to open messages in their own window, an invitation was missing the card that states when the meeting is and offers **Accept**, **Tentative**, and **Decline**. The card only ever appeared in the reading pane.

This is worse than it sounds, because for many invitations the card is the *only* place the date and time appear. An invitation sent through Outlook for a Zoom meeting is a body full of join links and dial-in numbers; the actual when lives in the attached calendar part and nowhere else. In window mode there was no date, no time, and no way to answer.

The card now appears wherever a message is opened, and the message window answers invitations too — the three responses are on its command palette (**Ctrl+Shift+P**) as well as in the card. ([#513](https://github.com/kellylford/QuickMail/issues/513))

## Fixed: pictures in a message lost their descriptions

QuickMail does not load images from the internet, and that has not changed. But it was removing each image so completely that the description the sender wrote for it — the text meant to be read when the picture cannot be seen — went with it. Where a sender had described a picture properly, you got nothing at all.

It was worst where a picture *is* a link, which is how nearly every newsletter builds the row of social media links at the bottom. Removing the picture left a link with no text in it, so it was announced by its web address instead — and those addresses are tracking links. A row that should read "Facebook link, Twitter link, Instagram link" read as "redirect" three times over.

Descriptions now stay when the picture goes. A picture the sender marked as decorative still contributes nothing, which is what marking it that way asks for. ([#163](https://github.com/kellylford/QuickMail/issues/163))

---

## Internal

Everything below is developer detail — implementation notes, test coverage, and build changes. Nothing here is needed to use QuickMail.

### Calendar

- **The default is stored as the tree node's own tail encoding, so there is one parser rather than two.** `ConfigModel.DefaultCalendarSource` holds exactly what follows `CalendarSourcePrefix` on the node the user chose — `local`, `{guid}`, or `{guid}|{escapedCalId}` — and `MainViewModel.DefaultCalendarFilter` reads it back through the existing `CalendarFilterFor`. Storing an account/calendar pair instead would have meant a second encoding to keep in step with the tree's, for no gain: the setting is only ever produced by, and only ever refers to, a node in that tree. Empty means no preference, which is distinct from an explicit `local` in the config file but identical in behavior.
- **Honoring the default is a preselection in `NewEvent`, not a change to `BuildSaveTargets`.** `EventEditorViewModel.SelectTarget(accountId, calendarId)` moves `SelectedTargetIndex`; the target list itself still puts Local at index 0 unconditionally. That keeps the no-default path byte-for-byte what it was, and keeps the fallbacks in one testable place. The fallback ladder matters because the tree and the picker do not offer the same set: the tree shows a node per *discovered* calendar for any account with more than one, while only iCloud contributes a target per calendar (Microsoft and Google contribute one, their default calendar). So an exact `(account, calendar)` match is tried first, then any target on the same account — landing on "that account" rather than on Local, which is a different mailbox entirely — and only a default whose account is gone at all leaves the editor on Local. `SelectTarget` returns whether it matched so the fallback is observable in tests rather than inferred.
- **Calendar nodes now carry `IsCalendarNode` and get their own context menu.** The `ItemContainerStyle` set `FolderContextMenu` on every tree item including the Calendar subtree, where all five entries fell through `IsMovableFolder`'s `\0` guard and silently did nothing. A `DataTrigger` on the new flag swaps in `CalendarContextMenu`. The menu is declared in `Window.Resources` and referenced from the trigger's setter — the XAML-compiler crash the neighbouring comment documents is about *declaring* a Click-handler menu inside a `Style.Setter.Value`, which this does not do.
- **The marker lives in `AutomationName`, not only in `ItemStatus`.** `FolderTreeNode.IsDefaultCalendar` appends ", default calendar" to the accessible name and drives a `(default)` badge modelled on the unread badge. This is the #227 finding applied to a second piece of state: a marker carried only in `ItemStatus` is not reliably spoken, and a default the user cannot hear is a default they cannot check. `PersistDefaultCalendar` moves the marker between existing node objects via `MarkDefaultCalendarNodes` rather than rebuilding the tree, which would replace every node and throw keyboard focus out of the item the user just acted on; `BuildFolderTree` re-applies it after a genuine rebuild.
- **Both commands are registered, so the context menu is not the only way in.** `calendar.setDefaultCalendar` and `calendar.clearDefaultCalendar` are Calendar-category `CommandDefinition`s with no default key — palette-reachable and rebindable. That is the #250 lesson (folder creation was context-menu-only and therefore undiscoverable without a mouse) applied up front. Refusals are sentences, not silence: the bare Calendar node and All Calendars are not one calendar, and `SetDefaultCalendar` returns why, which the View sends to both `StatusText` and a `Result` announcement.

### Rules

- **Run on Existing Mail was the same parity gap as Test Rule, one sweep later, and it carried a scope decision with it.** The unified window had the command wired and registered but no button and no context-menu item, so it was palette-only for every Microsoft 365 user — the one the v0.8.37 Test-Rule sweep missed (#493). The mechanical half was the easy half. `RunClientRulesOnExisting` had always built its Inbox map over **every** account in `CachedFolders`, which was unambiguous in the client-only window because that window lists every account together; under an account picker, a button directly below **Account: Work** would have read as "run this for Work" while processing every mailbox. `RunOnExistingRequested` now carries a `Guid?`: the unified window passes `SelectedAccount.Id` and the shared handler filters the map to it; the client-only window passes `null` and keeps its all-accounts run, byte-for-byte unchanged. The scope is enforced where it is load-bearing rather than cosmetically — `ApplyRulesToExistingAsync` only ever touches messages whose account is a key in that map, so an *unscoped* legacy rule reaching an out-of-scope mailbox is not possible. The scope decision then settled the `CanExecute` question filed alongside it: because the run is now exactly the account on screen, "this account has no enabled QuickMail rules" is an unambiguous statement, so `CanRunOnExisting` gates on it. A Graph mailbox whose rules are all server-side therefore gets a disabled control rather than one that reports "0 moved". The outcome also sets `StatusText`, not just `Announce`: the same `Result`-category reasoning as Test Rule, since a newly surfaced button whose only feedback is an announcement is imperceptible to a user running with announcements off, error included.
- **`RuleListShowFieldLabels` was read by one window and ignored by the other.** The **Show field labels in the rules list** checkbox is in Settings for every user regardless of account type, but only `RulesManagerViewModel` read it — so for a Microsoft 365 user the checkbox silently did nothing. `UnifiedRuleRow` now takes the flag through its `ForServer`/`ForClient` factories and prefixes each field in `RowText`: `Rule Newsletters, runs on server, status enabled` rather than `Newsletters, on server, enabled`. Read once when the manager opens, matching the client window, so a Settings change applies the next time it is opened. Note the one asymmetry between the two windows: in the client window the setting shapes `AccessibleName` only, while `UnifiedRuleRow.ToString()` returns `RowText`, so here it changes the visible row text too. The detail pane is untouched either way — it always carries the full definition.

### Message rendering

- **The event card moved from a call site to the builder, because the obligation was the bug.** `MainViewModel` built the invite card and `MainWindow` injected it, so every other render surface silently shipped without one — `MessageWindow` had done so since it existed. Splitting it into `Helpers/EventCardHtmlBuilder` and injecting it inside `MessageBodyHtmlBuilder.BuildMessageHtml`, from the detail's own `CalendarInvite`, means no surface can omit it by forgetting. The failure mode is what makes this worth the move rather than a second call: the card is the only rendering of the ICS part, so a surface that drops it shows an invitation with no date and no time at all, and a body that is all join links reads as though the meeting has no when. `MessageWindow` also intercepts the card's `quickmail:` links, guards its `aria-live` confirmation against having navigated away mid-send, and carries the three RSVP actions in its palette. ([#513](https://github.com/kellylford/QuickMail/issues/513))
- **Image alt text is substituted before the removal pass, not preserved by weakening it.** `TryStripHeavyHtml` deleted `<img>` outright; the CSP's `img-src 'none'` is what blocks the pixels, so the deletion was discarding the name and buying nothing. The new pass replaces each image with its alt text and leaves the removal pass to take the tag. `alt=""` and whitespace-only alt are deliberately not matched — that is the author declaring the image decorative, and plain removal is the correct handling — and a missing `alt` keeps today's href fallback because there is no name to recover. The value is HTML-decoded and re-encoded rather than spliced: an attribute may hold entities that are already correct as text, but may equally hold a bare `<`, which is legal inside a quoted attribute and would open a tag once moved into content. The pass fails closed with the rest of the chain, since a partially substituted document is not a sanitized one. The link case is what makes this an accessibility defect rather than a cosmetic one: an anchor whose only content was the image is left empty, an empty link has no accessible name, and WebView2 names it from the `href` — which for a newsletter footer is a tracking URL. `MessageBodyHtmlBuilderTests` pins the icon-only-link shape against a real captured example, plus the three no-useful-alt shapes, entity round-tripping, markup in an alt, and the timeout. Message-list preview text is unaffected: it comes from the server (IMAP `PREVIEW` / Graph `BodyPreview`), not from this path. ([#163](https://github.com/kellylford/QuickMail/issues/163))
- **Loading remote images at all is deliberately still not offered.** ([#508](https://github.com/kellylford/QuickMail/issues/508)) tracks the opt-in — per message, per sender, and the `img-src` relaxation confined to that path. Alt text is the right fix independent of it: it is what names an image whether or not the pixels ever load.

### Bug reporting

- **The capability moved out of the binary; the key that stayed in it was reduced to something worth extracting nothing for.** Release builds compiled a fine-grained GitHub PAT into the single-file executable and posted to `api.github.com` with it, so anyone who downloaded QuickMail could pull the token out, and GitHub attributed every user-filed report to the token owner (#222). A GitHub App now owns the write capability with its private key held by a Cloudflare Worker (`relay/`). The client posts to the Worker with a relay key that still ships in the binary and is still extractable — the difference is that its whole authority is "file an issue on kellylford/QuickMail", it is rate-limited at the relay, and it rotates without touching a GitHub account. The client also stopped sending its own labels: a client that names them lets anyone holding the extracted key apply arbitrary ones, so the relay decides labels. Upgrading installs delete the pre-relay PAT from Credential Manager on first run.
- **The PKCS#1 → PKCS#8 conversion is tested against a real Node export, byte for byte.** GitHub hands out App keys as PKCS#1 and WebCrypto imports only PKCS#8, so a wrong conversion fails at runtime with an opaque error and nothing to bisect. `relay/test/jwt.test.js` asserts identical DER rather than "it parsed".
- **`docs/BUG-REPORTING.md` is the runbook** — rotating the relay key, reading Worker logs, and what to check when reports stop arriving. `relay/README.md` records the workers.dev subdomain step that broke the first deploy.

---

## Reporting Issues

Found a problem or have a suggestion? There are three ways to reach us — pick the one that fits:

1. **Report a Bug → Send** (Help menu, inside QuickMail). Files the report for you anonymously — it includes no email address or other identifying information, so there is no way to follow up with you. **Best when you don't want any follow-up.**
2. **Report a Bug → Copy report and open GitHub** (Help menu). Opens a pre-filled issue that you submit under your own GitHub account, so your GitHub contact information is attached. **Best when you have a GitHub account and want automatic filing plus direct contact.**
3. **Email** [quickmailissues@theideaplace.net](mailto:quickmailissues@theideaplace.net). **Best when you don't mind sending email and want a personal follow-up.**

Full details, including exactly what a report contains (and what it never contains), are in the [Reporting Issues section of the User Guide](https://kellylford.github.io/QuickMail/reporting-issues.html).

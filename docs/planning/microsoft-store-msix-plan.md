# Microsoft Store (MSIX) Distribution — Plan

**Issue:** [#746 — Publish QuickMail to the Microsoft Store as an MSIX package](https://github.com/kellylford/QuickMail/issues/746)
**Status: PLAN ONLY. Nothing is built, nothing is submitted.** Phase 0 is a measurement
spike whose results decide whether the rest of this plan is worth doing in its current shape.

**Date:** 2026-09-21

## Summary

Publish QuickMail to the Microsoft Store as an MSIX package, in parallel with the MSI and
portable downloads that exist today. Microsoft re-signs a Store package with its own
certificate, so a Store install never meets Microsoft Defender SmartScreen's download
warning, and Windows keeps the app updated on its own.

The packaging itself is the small part. The work is in what changes around it: a packaged
app runs as a different principal, so its profile directory and possibly its saved passwords
are not the ones the installed copy uses today, and the Velopack updater has to be switched
off inside that flavor because a Store package cannot replace its own files.

## Why it is worth doing

Signing is already done and demonstrably correct. On 0.8.47 the published MSI verifies:

```
status:    Valid ("Signature verified")
signer:    CN=kelly ford, O=kelly ford, L=Madison, S=wi, C=US
issuer:    CN=Microsoft ID Verified CS AOC CA 03
timestamp: Microsoft Public RSA Time Stamping Authority
```

Edge still warns on the download, and the wording is **"isn't commonly downloaded"**. That
is SmartScreen's *prevalence* prompt, not a malware verdict and not a transport block.
Signing is what keeps it at that mild tier rather than "Windows protected your PC", and
signing cannot take it further, because:

- **Prevalence is keyed to the file hash.** Every release publishes four brand-new hashes,
  each starting at zero. Microsoft's guidance puts the threshold at "several weeks and
  hundreds of clean installs from a wide audience".
- **QuickMail's volume will not reach that**, on any single asset, at any point. Waiting is
  not a strategy; it is the current state, indefinitely.
- **There is no appeal.** Microsoft states plainly that there is no need or mechanism to
  submit a file for consumer SmartScreen reputation review. The Security Intelligence portal
  is for enterprise administrators and malware false positives.
- EV certificates no longer bypass SmartScreen either, so there is no certificate to buy
  that changes this.

One caveat that may be depressing the result further: QuickMail's certificate chains to
`Microsoft ID Verified CS AOC CA 03`, one of the 2026 intermediates that Trusted Signing
customers report reputation did not carry across
([Q&A thread](https://learn.microsoft.com/en-us/answers/questions/5855708/trusted-signing-regression-in-smartscreen-reputati),
[artifact-signing-action#128](https://github.com/Azure/artifact-signing-action/issues/128)).
Opening a Trusted Signing support case is cheap and independent of this plan; it does not
replace it, because even unaffected publisher reputation does not clear the prevalence prompt.

What the Store changes: Microsoft re-signs the package, so it carries Microsoft's reputation
rather than accumulating its own. Registration is free for individuals (since late 2025) and
for companies (since May 2026); hosting and signing are free; updates are delivered by Windows.

## Goals

1. A Store listing that installs QuickMail with no SmartScreen prompt at any point.
2. Updates delivered automatically by Windows, with no in-app updater in that flavor.
3. The existing MSI + portable + Velopack track untouched and still primary.
4. An honest, hearable first-launch path for anyone who moves from an MSI install to the
   Store build — including the parts that cannot be migrated.

## Non-goals

- Replacing the MSI. Every current user installed that way and keeps self-updating.
- A UWP or Windows App SDK rewrite. This packages the existing WPF app as full-trust MSIX.
- Winget ([#536](https://github.com/kellylford/QuickMail/issues/536)). Separate track,
  separate blocker, unaffected either way.
- Store-hosted *unpackaged* Win32 listings (pointing the Store at the MSI). Microsoft does
  not re-sign those, so they do not remove the warning and are not worth the submission.

## Phase 0 — Measure, before building anything

Three questions decide the shape of everything after this. Each is a measurement, on a
throwaway package, not a judgement call.

**0a. Does a packaged build see the existing profile?**
MSIX redirects writes aimed at `%APPDATA%` into
`%LOCALAPPDATA%\Packages\<PackageFamilyName>\LocalCache\Roaming`, while reads can fall
through to the real path when the file is not in the container yet. The failure mode that
matters is the split brain: QuickMail reads the real `accounts.json`, writes the modified
copy into the container, and from then on two files disagree with no sign of it in the UI.

Test: package a build, launch it on a machine with a populated `%APPDATA%\QuickMail`, and
record — for `accounts.json`, `config.ini`, `mail.db` — which path is read and which is
written, on first launch and after a settings change.

**0b. Can a packaged build read credentials the MSI build saved?**
`CredentialService` stores each account's password under `QuickMail:<accountId>`. Credential
Manager appears to isolate the vault by package identity, which would mean the Store build
starts with no passwords at all and every account needs re-authentication. This is the single
finding most likely to change the migration design, and it is currently inferred, not measured.

Test: write a credential from the unpackaged build, then `CredRead` the same target name from
the packaged build. Then the reverse. Record both.

**0c. WebView2 without the bootstrapper.**
The MSI installs the Evergreen runtime when it is missing; an MSIX cannot. Test on a machine
with no WebView2 runtime: what does the packaged app do, and is the failure legible? Decide
between declaring the runtime a documented requirement and bundling the fixed-version runtime
(which is very large and would have to be updated by us for every WebView2 security fix).

**Also worth measuring while a package exists:** download size and whether Store differential
updates do anything useful against a single-file self-contained exe. If they do not, the Store
flavor should publish non-single-file so the block map can diff — a 300 MB download per update
is a bad trade against Velopack's delta packages.

## Phase 1 — Package

- Package identity, publisher display name and Store reservation in Partner Center.
- MSIX manifest declaring `runFullTrust`; the app code is unchanged.
- Version scheme: MSIX requires four parts with the revision at 0 — `0.8.48.0`. The build
  already carries a three-part version in `QuickMail.csproj`; the packaging step appends `.0`.
- Both architectures, from the existing `publish` and `publish-arm64` outputs.
- Age rating, privacy policy URL (the existing `privacy.html` — see Phase 5), and the
  Store listing text.

## Phase 2 — What the Store flavor turns off

A Store package cannot replace its own files, so the in-app updater must be inert there, and
must be inert *visibly* — an update item that does nothing is worse than no item.

- Detect package identity at runtime with `GetCurrentPackageFullName`
  (`APPMODEL_ERROR_NO_PACKAGE` means unpackaged). One helper, one boolean, used everywhere below.
- `UpdateCheckService` is not constructed in `App.xaml.cs` when packaged.
- The Help menu's update entry and `UpdateDialog` / `UpdateInstalledDialog` are not reachable;
  the command is not registered, so it does not appear in the Command Palette or the keyboard
  customizations dialog either. (Registering a command and then having it do nothing would
  violate the keyboard-shortcut rules in `CLAUDE.md` in spirit — the palette is a discovery
  surface and must not list dead actions.)
- `VelopackApp.Build().Run()` stays in `Main` — `vpk pack` verifies it by IL inspection for
  the MSI track, and it is a no-op when the app was not installed by Velopack.
- About should say where updates come from, so the answer to "why is there no update check"
  is in the app rather than only in the guide.

## Phase 3 — Migration, and the part that cannot be migrated

Shape depends on Phase 0, but the design question is the same either way: someone who has
been running QuickMail for months installs the Store build and must not be left staring at
an app with no accounts and no explanation.

Proposed, assuming 0a shows redirection and 0b shows credential isolation:

- On first launch, the packaged build looks for a legacy profile at `%APPDATA%\QuickMail`
  and, if its own profile is empty, offers to bring it across.
- Importing copies the profile wholesale — accounts, settings, rules, templates, saved views,
  contacts, and the mail cache. The legacy profile is left untouched, so the MSI copy still
  works and the move is reversible.
- Passwords and OAuth tokens **do not come across** if 0b confirms vault isolation. Every
  account then needs signing in again: a browser round trip for OAuth accounts, a typed
  password for the rest. This must be stated before the import, not discovered after it.
- QuickMail already has `--profileDir`, so the packaged flavor should resolve its profile
  deliberately rather than relying on redirection to do something sensible.

### Keyboard walkthrough — first launch of the Store build, legacy profile present

1. QuickMail starts. Before the main window appears, a dialog opens with focus on its first
   control. Screen reader announces: "Bring your QuickMail data across? dialog. QuickMail
   found settings from a copy installed earlier: accounts, rules, templates, saved views and
   cached mail. This copy can bring them across. Your saved passwords cannot come across, so
   each account will need signing in again."
2. User presses Tab. Focus moves through **Bring my data across** (default), **Start fresh**,
   and **Decide later**. Each is announced by its label alone.
3. User presses Enter on **Bring my data across**. The dialog closes. Announced as
   `AnnouncementCategory.Status`: "Copying your QuickMail data." On a large mail cache this
   is the only place a wait is visible, so it reports progress and then completion.
4. The main window opens with focus in the message list, as it does on any other launch.
   Announced as `AnnouncementCategory.Status`: "Data copied. Five accounts need signing in."
5. User presses F6 to the account list. Each account that needs credentials is announced with
   its name and that state — carried by the item's own accessible name, not by an
   announcement on top of it.
6. User presses Enter on an account. The existing sign-in path runs, unchanged.
7. **Start fresh** skips the copy and opens the normal first-run experience.
   **Decide later** opens QuickMail empty and leaves the offer in place for the next launch,
   reachable from the File menu so it is not lost.

Open design point flagged for Kelly: whether step 1 should be a dialog at all, or the
first-run screen with the offer on it. A dialog before the main window avoids the modal +
WebView2 hazard in `CLAUDE.md` entirely, which argues for the dialog.

### Keyboard walkthrough — Help menu in the Store flavor

1. User opens the Help menu. There is no **Check for Updates** item; the menu is one item
   shorter and nothing announces as unavailable.
2. User opens **About QuickMail**. Among the fields is one reading: "Installed from the
   Microsoft Store. Windows keeps this copy up to date."

## Phase 4 — CI and submission

- A packaging step in `quickmail.yml` producing both architectures' MSIX on a `v*` tag.
- First submission by hand, to see the certification report.
- After that, the Store submission API from the release job, gated the same way the signing
  steps are.
- Certification adds a delay between tag and availability that the MSI track does not have.
  The Store listing will trail the GitHub release; the release notes should not claim
  otherwise.

## Phase 5 — Documentation

- **User Guide**: the Store as an installation option, what it changes (no update prompts,
  updates arrive from Windows), and the migration caveat for anyone switching.
- **`docs/privacy.html`**: the profile-directory table says "normally `%APPDATA%\QuickMail`".
  A Store install keeps the same files somewhere else, and the page enumerates them by name,
  so it needs the packaged path and a new effective date in the same change.
- **`docs/download-footer.md`**: a Store link once the listing is live. Note this footer is
  copied verbatim into every release notes file, so the change lands in the next release's
  notes too.
- **`docs/INSTALLER.md`**: the Store flavor's build, and the fact that it has no Velopack feed.

## Infrastructure changes

- **F6 ring**: unchanged. The migration dialog is its own window and is not a pane.
- **CommandRegistry**: no commands added. One command — the update check — is **not
  registered** when packaged, which is the only registry-visible difference.
- **`AutomationProperties.Name`**: new names for the migration dialog's three buttons and its
  window. Short labels only: "Bring my data across", "Start fresh", "Decide later".
- **Announcements**: two new calls, both `AnnouncementCategory.Status` — the copy starting,
  and the result naming how many accounts need signing in. No `Hint` calls: the dialog's own
  text carries the explanation, and it is read as the dialog opens.
- **VM state**: a `IsPackaged` (or similarly named) flag read from package identity, surfaced
  where the Help menu and About are built.
- **Selector-bound types**: none added.

## Out of scope

- Any change to how the MSI track updates. Velopack stays exactly as it is.
- Moving existing users to the Store. This gives them a path if they want it; it does not
  push anyone, and there will be no in-app prompt to switch.
- Migrating saved passwords or OAuth tokens, if Phase 0b confirms the vault is isolated.
  Re-authentication is the cost of switching, stated up front.
- Store-only features (ratings prompts, in-app purchase, Store-managed licensing).
- Winget, and the Trusted Signing support case — both tracked separately.
- Retiring the portable exes. Worth discussing for the SmartScreen prevalence math, but it is
  a separate decision from this one.

## The name (2026-09-22)

**"QuickMail" cannot be reserved in the Store.** Kelly checked; Partner Center will not take
it. No app by that name appears in a Store search, which per Microsoft's documentation usually
means another developer reserved the name and never submitted anything.

Nothing technical breaks. Package identity is separate from display name, so Phase 0 and the
whole migration question are untouched. What the name decides is what appears on the Store
listing and in the Start menu of a Store install — the app's own window titles and in-app text
are ours regardless.

Three facts that bound the choices:

- **Reservations expire after three months without a submission**, and the holder is free to
  renew. So the name may free up, on nobody's schedule but theirs.
- **The only dispute path is a trademark or other legal right**, raised with Microsoft. That
  is not a route here, and there is a commercial QuickMail in the email business, so the mark
  is unlikely to be available either.
- **A display name is not a one-way door.** Shipping as one name and later reserving the plain
  one is a new submission with a changed display name, not a re-listing.

Options, for Kelly to settle:

1. **Ship under a variant** — "QuickMail for Windows" or similar. Reads naturally aloud and in
   a list, keeps the app's own name intact, and does not block Phase 0.
2. **Wait for the reservation to lapse.** Costs nothing but blocks the Store route for an
   unknown time, possibly forever if the holder renews.
3. **Rename the product.** Touches the UI, docs, site, profile directory, credential entries,
   update feed and repository — by far the largest option, and it does not escape the
   trademark question.
4. **Drop the Store** and put the effort into winget, accepting that the SmartScreen prompt
   stays for anyone downloading from the site.

**This does not gate Phase 0.** The measurements — profile data, credential vault, WebView2 —
are the same whatever the listing ends up being called, and their results are what decide
whether the Store route is worth the migration work at all. Settle the name before submission,
not before measurement.

## Open questions

1. **Q1.** Does the Store build get its own profile, or should it deliberately use the same
   `%APPDATA%\QuickMail` path as the MSI build so a person can run either against one set of
   data? Sharing sounds friendlier and is probably a trap — two installs with one SQLite
   cache, and no way to stop both running at once.
2. **Q2.** If credentials cannot migrate, is the switch worth offering to existing users at
   all, or should the Store listing be aimed purely at new ones?
3. **Q3.** Single-file or loose files for the packaged flavor, decided on what Phase 0 shows
   about differential updates and download size.
4. **Q4.** WebView2: documented requirement, or bundle the fixed-version runtime?
5. **Q5 — answered, 2026-09-22: "QuickMail" is NOT available in the Store.** See *The name*
   below. What remains open is which way round it.
6. **Q6.** Certification review: does a mail client attract manual review, and does anything
   in the listing need to say what the app does with mail data beyond the privacy policy?

## Sequencing and effort

Phase 0 is a day's work and answers whether the rest is a fortnight or a month. Nothing after
it should start until 0a and 0b are measured — the migration design, which is the bulk of the
remaining work, is a different design depending on what they return.

The packaging, the CI step and the Store flavor's switches are all small and well understood.
The first-launch migration is the part that deserves the care, because it is the only part a
person experiences, and they experience it exactly once, with all their mail at stake.

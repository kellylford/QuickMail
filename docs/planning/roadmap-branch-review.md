# Code Review: roadmap branch
**Date:** 2026-05-25  
**Branch:** `roadmap` vs `main`  
**Reviewer:** Claude Code (claude-sonnet-4-6)  
**Scope:** All 54 changed files — 5,750 insertions, 291 deletions

## Post-Review Fixes Applied (2026-05-25)

The following issues from this review have been addressed:

| # | Status | Title |
|---|---|---|
| BUG-1 | ✅ Fixed | `Cancel()` now fires `TutorialCancelled` (new event) instead of `TutorialCompleted`. `MainWindow` handles them separately: completion saves `TutorialCompleted = true` and announces "Tutorial complete"; cancellation announces "Tutorial cancelled" without marking the tutorial done. |
| BUG-2 | ✅ Fixed | `SendIcsReplyAsync` now takes an explicit `organizerEmail` parameter. `MainViewModel.SendIcsReply` passes `invite.Organizer ?? ""`. The regex extraction from ICS content is removed. |
| QUALITY-1 | ✅ Fixed | F7 handler in `BodyBox_PreviewKeyDown` now delegates to `NavigateSpellingError(forward)`, eliminating ~60 lines of duplicate code. |
| QUALITY-2 | ✅ Fixed | Removed unused `using System.ComponentModel`, `using System.Windows`, and `using CommunityToolkit.Mvvm.Input` from `TemplatePickerViewModel.cs`. |
| QUALITY-3 | ✅ Fixed | Replaced `global::QuickMail.Models.ViewMode` with `Models.ViewMode` in `ConfigModel.cs` (the `ViewMode` name conflicts with the string property, so namespace qualification is still needed but is now minimal). |
| QUALITY-5 | ✅ Fixed | `TutorialOverlay.SetViewModel` now stores the handler in a field and unsubscribes before re-subscribing, preventing event subscription leaks on restart. |
| QUALITY-7 | ✅ Fixed | Added `LIMITATION:` comment to `IcsModel.ParseIcsDateTime` documenting that TZID parameters are ignored. |
| QUALITY-8 | ✅ Fixed | `TutorialViewModel.Start()` now notifies `CurrentStepNumber` in addition to `CurrentStep`. |
| DOC-1 | ✅ Fixed | Added missing entries to CLAUDE.md shortcut table: `mail.acceptInvite`, `mail.declineInvite`, `mail.tentativeInvite`, `help.keyboardTutorial`. Added note about compose window's private `CommandRegistry`. |

**Not addressed (deferred):**
- BUG-3 (false positive test) — low priority, no production impact
- DESIGN-1 through DESIGN-4 — design observations, not bugs
- QUALITY-4, QUALITY-6 — very low priority
- TEST-1 through TEST-5 — test coverage gaps
- DOC-2 — addressed via QUALITY-7 comment

## Summary

**Build:** ✅ Clean (0 warnings, 0 errors)  
**Tests:** ✅ 341 / 341 passing

Five major feature areas landed on this branch:

| Feature | Files |
|---|---|
| Email signatures | `AccountModel`, `AccountEditorViewModel`, `AccountManagerViewModel`, `AddAccountViewModel`, `ComposeViewModel`, both account dialogs |
| ICS calendar invite handling | `IcsModel`, `MailMessageDetail`, `ImapService`, `SmtpService`, `MainViewModel`, `MainWindow` |
| Message templates | `MessageTemplate`, `ITemplateService`, `TemplateService`, `ComposeViewModel`, `TemplatePickerViewModel`, `TemplatePickerWindow` |
| First-run keyboard tutorial | `ConfigModel`, `TutorialViewModel`, `TutorialOverlay`, `MainViewModel`, `MainWindow` |
| Phase 5 refactoring | `GroupedMessageTreeController`, `ConfigModel` (ViewMode/Sort helpers), `MainViewModel` (`ReplaceCts`) |
| Spell check (compose) | `ComposeWindow.xaml.cs`, `ConfigModel`, `SettingsViewModel`, `SettingsDialog` |

Overall quality is high. Architecture follows existing conventions. The most important findings are two bugs that alter runtime behaviour: the tutorial cancellation path incorrectly marks the tutorial as "completed", and `SendIcsReplyAsync` can throw an unhandled exception when no organiser is extracted from the ICS payload.

---

## BUGS

### BUG-1 — `TutorialViewModel.Cancel()` fires `TutorialCompleted`, marking the tutorial done on early Escape  
**Severity:** Medium  
**File:** `QuickMail/ViewModels/TutorialViewModel.cs:126-129`  
**Also:** `QuickMail/Views/MainWindow.xaml.cs:1095-1107`

`Cancel()` and the last-step `Advance()` both call `TutorialCompleted?.Invoke()`, and `MainWindow.OnTutorialCompleted` responds identically to both: it sets `cfg.TutorialCompleted = true` and announces **"Tutorial complete. You can replay it anytime from the Help menu."** regardless of whether the user pressed Escape at step 1 or finished all six steps.

Consequence: a first-time user who accidentally opens the tutorial and immediately presses Escape will never see it again automatically (if first-run logic is ever added), and will hear a misleading announcement.

```csharp
// CURRENT — both paths look the same to OnTutorialCompleted
public void Cancel()
{
    IsActive = false;
    TutorialCompleted?.Invoke();  // ← fires the exact same event as completion
}
```

**Fix options:**
- Add a second event `TutorialCancelled`, or
- Pass a `bool completed` argument to the event, or
- Have `Cancel()` set `IsActive = false` and let the view react to the `IsActive` change directly without calling into `OnTutorialCompleted`.

The test comment in `TutorialViewModelTests.AllSixSteps_CompleteAndFireTutorialCompleted` also describes the wrong behaviour ("Escape at step 6 cancels the tutorial") — at step 6, the cancel-guard is skipped because `CurrentStep.ExpectedKey == Key.Escape`, so `Advance()` is called, not `Cancel()`. The test passes because both paths fire the same event. The comment needs correcting regardless.

---

### BUG-2 — `SendIcsReplyAsync` silently sends a no-recipient message when ORGANIZER is absent  
**Severity:** Medium  
**File:** `QuickMail/Services/SmtpService.cs:82-95`

```csharp
var organizerMatch = Regex.Match(
    icsReplyContent, @"ORGANIZER[^:]*:mailto:([^\r\n]+)", ...);
if (organizerMatch.Success)
{
    var orgEmail = organizerMatch.Groups[1].Value.Trim();
    message.To.Add(MailboxAddress.Parse(orgEmail));
}
// ← no else clause; if match fails, message.To is empty
await client.SendAsync(message, ct);  // throws: "No recipients specified"
```

The exception propagates up and is caught by `MainViewModel.SendIcsReply`'s catch block, so it won't crash the app. But the error will always surface as a confusing "Failed to send calendar response: No recipients specified" announcement rather than the real problem (the ICS content didn't include an ORGANIZER line with a mailto: URI).

Additionally, `SendIcsReplyAsync` re-parses the ICS string it just generated (via `IcsModel.GenerateReply`) to extract the organiser email. The correct approach is to pass the email directly from `MainViewModel`, which already has it in `invite.Organizer`.

**Fix:**
```csharp
// In MainViewModel.SendIcsReply — pass the organiser email explicitly
await _smtp.SendIcsReplyAsync(icsContent, account, password, invite.Organizer);

// In ISmtpService / SmtpService — add organiser parameter and guard
Task SendIcsReplyAsync(string icsContent, AccountModel account, string? password,
    string organizerEmail, CancellationToken ct = default);
```

---

### BUG-3 — `TemplateService.UpdateAsync_PersistsAcrossInstances` test is a false positive  
**Severity:** Low (test quality, no production impact)  
**File:** `QuickMail.Tests/TemplateServiceTests.cs:1582-1599`

The test verifies that an update through `service2` is visible when loaded through `service`. It passes — but for the wrong reason:

```csharp
var t = await service.AddAsync(new MessageTemplate { Title = "T1", Body = "B1" });
// t is the SAME object reference stored in service._cache

t.Title = "T1 Modified";           // mutates service._cache[0].Title in-place
await service2.UpdateAsync(t);     // writes to disk (correct), but cache mutation already happened

var all = await service.LoadAllAsync();  // returns from service._cache (_loaded = true)
Assert.Equal("T1 Modified", all[0].Title);  // passes via reference, not persistence
```

Because `service._cache.Add(template)` adds the same object reference, mutating `t.Title` directly changes `service._cache[0]`. The test never actually exercises cross-instance persistence.

**Fix:** Clone the template before mutating it and compare through a fresh third instance:
```csharp
var t = await service.AddAsync(new MessageTemplate { Title = "T1", Body = "B1" });

// Use a COPY so service._cache is untouched
var tCopy = new MessageTemplate { Id = t.Id, Title = "T1 Modified", Body = t.Body };
var service2 = new TemplateService(new ProfileContext(dir));
await service2.UpdateAsync(tCopy);

// Verify through a THIRD instance (no cached state)
var service3 = new TemplateService(new ProfileContext(dir));
var all = await service3.LoadAllAsync();
Assert.Equal("T1 Modified", all[0].Title);
```

---

## DESIGN & ARCHITECTURE ISSUES

### DESIGN-1 — `GroupedMessageTreeController.OnContextMenuOpening` exists but is never called  
**Severity:** Low (dead code)  
**File:** `QuickMail/Views/GroupedMessageTreeController.cs:666-682`

The refactoring to `GroupedMessageTreeController` extracted `GotKeyboardFocus`, `SelectedItemChanged`, `PreviewTextInput`, `PreviewMouseRightButtonDown`, and `FocusFirstItem` handlers. The controller also defines `OnContextMenuOpening`, but the three `*Tree_ContextMenuOpening` handlers in `MainWindow.xaml.cs` still have their own inline implementations and do not delegate to the controller. The `OnContextMenuOpening` method is dead code.

Either remove `OnContextMenuOpening` from the controller, or complete the extraction and replace the inline handlers with controller delegation.

---

### DESIGN-2 — `BuildEventCardHtml` generates CSS-embedded HTML inside `MainViewModel`  
**Severity:** Low (MVVM smell)  
**File:** `QuickMail/ViewModels/MainViewModel.cs:3700-3655`

Generating presentation HTML (with inline CSS colours, border radii, etc.) inside a ViewModel blurs the MVVM boundary. The method works, but it means visual decisions are embedded in a component that should be ignorant of how data is displayed.

**Options:**
- Move to a static `EventCardBuilder` utility class in the Views or a `Rendering` namespace.
- Return only the structured data and let `MainWindow.xaml.cs` build the HTML.

This is low priority since there are no tests coupling the HTML output to ViewModel tests, but it's worth noting for the next refactor cycle.

---

### DESIGN-3 — Compose `CommandRegistry` is private and non-customisable  
**Severity:** Low (design limitation)  
**File:** `QuickMail/Views/ComposeWindow.xaml.cs:132`

```csharp
private readonly CommandRegistry _registry = new();
```

A fresh `CommandRegistry` is created per compose window. Commands registered in `RegisterComposeCommands()` do not flow through `hotkeys.json`, so users cannot rebind compose shortcuts (Alt+S, Ctrl+S, F7, etc.) via the Settings dialog.

If keyboard customisation of compose shortcuts is a future goal, this needs to be wired into the same `IConfigService`-backed hotkey system used by the main window. For now it is a documented limitation, but the CLAUDE.md shortcut table does not mention compose shortcuts at all.

---

### DESIGN-4 — `InsertTemplateRequested` uses `Func<Task<T>>` instead of a typed event  
**Severity:** Very Low  
**File:** `QuickMail/ViewModels/ComposeViewModel.cs:99`

```csharp
public event Func<Task<MessageTemplate?>>? InsertTemplateRequested;
```

Using `event Func<Task<T?>>?` is an unusual pattern. While it works with a single subscriber, multi-subscriber scenarios (e.g., unit tests wiring multiple handlers) will only receive the return value of the last subscriber; all others' return values are silently discarded. The existing `ConfirmationRequested` property uses `Func<string, string, bool>?` (not an event), which avoids this. Consider either:

- Making this a plain property (`public Func<Task<MessageTemplate?>>? InsertTemplateRequested { get; set; }`)  
- Using a typed `EventArgs` subclass if multiple subscribers should ever be supported

---

## CODE QUALITY

### QUALITY-1 — `NavigateSpellingError` duplicates the F7/Shift-F7 block (~60 lines)  
**Severity:** Medium  
**File:** `QuickMail/Views/ComposeWindow.xaml.cs`

`BodyBox_PreviewKeyDown` contains a full F7 navigation block (lines ~354–427). `NavigateSpellingError(bool forward)` (lines ~489–542) contains almost identical code — same loop, same word-boundary walk, same tracking-state update, same announcement. The duplication is acknowledged in the comment but not resolved.

**Fix:** Extract to a single `NavigateToSpellingError(bool forward)` method and call it from both places. The F7 handler can simply call `NavigateToSpellingError(forward: true/false)` and set `e.Handled = true`.

---

### QUALITY-2 — Unused `using` directives in `TemplatePickerViewModel`  
**Severity:** Low  
**File:** `QuickMail/ViewModels/TemplatePickerViewModel.cs:3,6,8`

Three imports have no references in the file:
- `using System.ComponentModel;`
- `using System.Windows;` — **this is an MVVM-layer violation flag** (even though nothing from `System.Windows` is actually used). The presence of the import suggests it was copy-pasted from a View file without cleanup.
- `using CommunityToolkit.Mvvm.Input;` — no `[RelayCommand]` or `IRelayCommand` in the VM

Remove all three.

---

### QUALITY-3 — `ConfigModel` uses `global::QuickMail.Models.ViewMode` inside its own namespace  
**Severity:** Low  
**File:** `QuickMail/Models/ConfigModel.cs:91-107`

```csharp
public static global::QuickMail.Models.ViewMode ParseViewMode(string? s) => ...
    "conversations" => global::QuickMail.Models.ViewMode.Conversations,
```

`ConfigModel` is already in the `QuickMail.Models` namespace. The `global::` qualifier and full namespace path are unnecessary. Should be just `ViewMode` and `MessageSort`.

---

### QUALITY-4 — `_loaded` is not `volatile` in `TemplateService`  
**Severity:** Very Low (theoretical on x86)  
**File:** `QuickMail/Services/TemplateService.cs:17`

`EnsureLoadedAsync` uses a double-checked locking pattern with a plain `bool _loaded`. Without `volatile`, a processor could theoretically observe a stale `false` value even after the flag was set, causing an unnecessary second attempt. On .NET on x86/x64 this works due to the strong memory model, but it is not formally correct per the CLI spec.

```csharp
private volatile bool _loaded;  // add volatile
```

---

### QUALITY-5 — `TutorialOverlay.SetViewModel` leaks event subscriptions  
**Severity:** Low  
**File:** `QuickMail/Views/TutorialOverlay.xaml.cs:23-35`

```csharp
public void SetViewModel(TutorialViewModel vm)
{
    _viewModel = vm;
    DataContext = vm;
    vm.PropertyChanged += (_, e) => { ... };  // ← never unsubscribed
}
```

Each call to `SetViewModel` adds a new subscriber. If the user starts, cancels, and restarts the tutorial (which calls `ShowTutorial()` → `new TutorialViewModel()` → `SetViewModel()`), each call adds another listener. With the current UI flow this only fires once per session, but it is a correctness issue.

**Fix:**
```csharp
private PropertyChangedEventHandler? _propertyChangedHandler;

public void SetViewModel(TutorialViewModel vm)
{
    if (_viewModel != null && _propertyChangedHandler != null)
        _viewModel.PropertyChanged -= _propertyChangedHandler;

    _viewModel = vm;
    DataContext = vm;

    _propertyChangedHandler = (_, e) => { ... };
    vm.PropertyChanged += _propertyChangedHandler;
}
```

---

### QUALITY-6 — Redundant `Cancel()` before `ReplaceCts` in several methods  
**Severity:** Very Low (harmless)  
**File:** `QuickMail/ViewModels/MainViewModel.cs` (several locations)

Several methods call `_folderCts?.Cancel()` immediately before `ReplaceCts(ref _folderCts, out var ct)`. `ReplaceCts` already cancels the previous CTS via `Interlocked.Exchange` + `Cancel()`. Calling `Cancel()` twice is idempotent but redundant.

```csharp
// BEFORE (redundant):
_folderCts?.Cancel();
ReplaceCts(ref _folderCts, out var ct);

// AFTER (clean):
ReplaceCts(ref _folderCts, out var ct);
```

Affected locations: folder load, RefreshAsync, SearchAsync, virtual-folder load, and background sync start.

---

### QUALITY-7 — ICS timezone identifiers are silently ignored  
**Severity:** Low (documented limitation)  
**File:** `QuickMail/Models/IcsModel.cs:152-164`

`DTSTART;TZID=America/Chicago:20260115T140000` correctly splits on the first colon, giving `value = "20260115T140000"`. The `TZID` parameter is available in `prop` but `ParseIcsDateTime` discards it. The result is treated as local machine time, which could be wrong for attendees in different time zones.

This is acceptable as an MVP limitation, but it should be documented with a `// LIMITATION: TZID parameters are ignored; times are treated as local` comment, and there is no test that verifies the UTC vs. local-time choice for ambiguous inputs.

---

### QUALITY-8 — `TutorialViewModel.Start()` doesn't notify `CurrentStepNumber`  
**Severity:** Very Low  
**File:** `QuickMail/ViewModels/TutorialViewModel.cs:141-147`

```csharp
public void Start()
{
    CurrentStepIndex = 0;
    IsActive = true;
    OnPropertyChanged(nameof(CurrentStep));
    // ← missing: OnPropertyChanged(nameof(CurrentStepNumber));
}
```

If the tutorial is restarted while at step > 1, the `CurrentStepNumber` binding (used for the "Step N of 6" label) won't update until `Advance()` is called. `Advance()` notifies both properties. Add the missing notification to `Start()`.

---

## MISSING TESTS

### TEST-1 — No `XamlParseTests` for `TutorialOverlay` or `TemplatePickerWindow`  
New XAML files are not covered by the existing XAML parse test suite. A bad binding or resource key would only be caught at runtime.

**Add to `XamlParseTests`:**
```csharp
[StaFact]
public void TutorialOverlay_LoadsWithoutException()
{
    var overlay = new TutorialOverlay();
    Assert.NotNull(overlay);
}

[StaFact]
public void TemplatePickerWindow_LoadsWithoutException()
{
    var vm = new TemplatePickerViewModel(new StubTemplateService());
    var window = new TemplatePickerWindow(vm);
    Assert.NotNull(window);
}
```

---

### TEST-2 — No test for `BuildEventCardHtml`  
`MainViewModel.BuildEventCardHtml()` builds non-trivial HTML with aria attributes and inline CSS. There are no tests for its output structure. At minimum: null invite returns empty string, populated invite includes the event summary and aria label, and all user-visible strings are HTML-encoded.

---

### TEST-3 — No test for signature insertion on reply/forward  
`ComposeViewModel.Seed()` appends a signature when `DraftUid == null` and the sender account has a signature. There are no tests for:
- New message with signature → body ends with `-- \n{sig}`
- Reply with existing body → separator `\n-- \n` is inserted
- Draft re-open → signature is NOT duplicated
- `_isDirty` is `false` after `Seed` (with and without signature)

---

### TEST-4 — No test for `SendIcsReplyAsync` recipient guard  
The bug in BUG-2 would be caught by a test that calls `SendIcsReplyAsync` with ICS content that has no `ORGANIZER:mailto:` line and verifies that either no exception is thrown or a meaningful exception type is raised.

---

### TEST-5 — F7 forward search from inside a misspelled word  
The `BodyBox_PreviewKeyDown` F7 handler starts the search from `BodyBox.CaretIndex`. If the caret is already inside a misspelled word, the scan will find and re-announce the same word rather than moving to the *next* error. This should be a deliberate design decision (with a test that documents the behaviour), or the start index should advance past the current word boundary first.

---

## DOCUMENTATION GAPS

### DOC-1 — CLAUDE.md shortcut table is incomplete  
**File:** `CLAUDE.md`

The registered shortcut table in CLAUDE.md does not include:

| Key | Command ID | Title |
|---|---|---|
| *(unassigned)* | `mail.acceptInvite` | Accept Invitation |
| *(unassigned)* | `mail.declineInvite` | Decline Invitation |
| *(unassigned)* | `mail.tentativeInvite` | Tentatively Accept Invitation |
| *(unassigned)* | `help.keyboardTutorial` | Keyboard Tutorial |

Add these to the table. Also add a note that compose-window commands (Alt+S, Ctrl+S, Ctrl+Shift+A, F7, Alt+Y) are registered in a private `ComposeWindow` registry and are not user-customisable via Settings.

---

### DOC-2 — No documentation on ICS timezone handling limitation  
Add a `// LIMITATION:` comment to `IcsModel.ParseIcsDateTime` noting that `TZID` parameters are ignored and times are treated as local.

---

## WHAT WORKED WELL

The following patterns are clean and well-executed:

- **`ReplaceCts` helper** — Centralises the "cancel old CTS, create new one" pattern that was repeated ~10 times. The `Interlocked.Exchange` approach is thread-safe. The extraction is complete and consistent.

- **`GroupedMessageTreeController`** — The delegation pattern reduces ~200 lines of duplicated handler code across three trees. All delegated handlers work correctly. The constructor parameter set is verbose but explicit.

- **`ConfigModel` ViewMode/Sort helpers** — The switch expressions were duplicated in at least 6 places in `MainViewModel` and are now consolidated in two `ParseX`/`ToConfigString` methods. Every call site was updated.

- **`IcsModel`** — The ICS parser handles line folding, property parameters, escaped text, UTC vs. local time, and date-only events. The `GenerateReply` method produces a valid RFC 5545 REPLY payload. Test coverage is thorough (25 tests covering parse, GenerateReply, DisplaySummary, BriefSummary, and edge cases).

- **`TemplateService`** — Atomic write via temp file + `File.Move(overwrite: true)`. Thread-safe with `SemaphoreSlim`. Defensive copy on load. Proper double-checked load guard.

- **Spell check accessibility** — Announcing misspelling on cursor entry (not just F7), suppressing re-announcement of the same word, Alt+1/2/3 suggestion replacement, and the `_suppressSpellingAnnouncement` guard during programmatic text changes are all thoughtfully implemented.

- **Tutorial overlay** — `interrupt: true` on accessibility announcements means screen reader users hear each step. The Escape cancellation guard that skips bare modifier keys is a nice detail.

- **Modal dialog rule compliance** — `TemplatePickerWindow` and the compose window's `InsertTemplateRequested` pattern both follow the documented rule (fire events after `ShowDialog()` returns, not during).

---

## SUMMARY TABLE

| # | Severity | Category | Title |
|---|---|---|---|
| BUG-1 | Medium | Bug | Cancel fires TutorialCompleted, marking tutorial done |
| BUG-2 | Medium | Bug | SendIcsReplyAsync throws when ORGANIZER missing |
| BUG-3 | Low | Bug | Cross-instance persistence test is a false positive |
| DESIGN-1 | Low | Design | OnContextMenuOpening in controller is dead code |
| DESIGN-2 | Low | Design | BuildEventCardHtml embeds CSS in ViewModel |
| DESIGN-3 | Low | Design | Compose shortcuts are not user-customisable |
| DESIGN-4 | Very Low | Design | InsertTemplateRequested event discards multi-subscriber returns |
| QUALITY-1 | Medium | Code Quality | NavigateSpellingError duplicates 60 lines |
| QUALITY-2 | Low | Code Quality | Unused `using System.Windows` in TemplatePickerViewModel |
| QUALITY-3 | Low | Code Quality | `global::` qualifiers redundant in ConfigModel |
| QUALITY-4 | Very Low | Code Quality | `_loaded` not volatile in TemplateService |
| QUALITY-5 | Low | Code Quality | SetViewModel leaks PropertyChanged subscriptions |
| QUALITY-6 | Very Low | Code Quality | Redundant Cancel() before ReplaceCts |
| QUALITY-7 | Low | Code Quality | ICS TZID silently ignored without comment |
| QUALITY-8 | Very Low | Code Quality | Start() missing CurrentStepNumber notification |
| TEST-1 | Low | Testing | No XamlParseTests for TutorialOverlay/TemplatePickerWindow |
| TEST-2 | Low | Testing | No tests for BuildEventCardHtml |
| TEST-3 | Low | Testing | No tests for signature insertion scenarios |
| TEST-4 | Low | Testing | No test for missing-recipient guard in SendIcsReplyAsync |
| TEST-5 | Very Low | Testing | F7 same-word re-announce behaviour undocumented |
| DOC-1 | Low | Docs | CLAUDE.md shortcut table incomplete |
| DOC-2 | Very Low | Docs | ICS TZID limitation not commented |

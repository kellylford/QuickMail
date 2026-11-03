# Plan: sound support for QuickMail

## Context

QuickMail plays no sound of its own today: there is no audio code or sound asset in the repo. The roadmap lists audio feedback (F14 in `docs/planning/competitive-analysis-roadmap.md`), a code comment already anticipates a "play sound" rule action (`ServerRuleEditorViewModel.cs:928`), and open issue #129 asks for a custom sound file, chosen per address, kept in settings backups.

The result: a playback service, sounds for mail events, a "Play a sound" rule action, one generated sound pack, and a person's own WAV or MP3 for any event or rule. #129 is met by a rule with a From condition and a custom sound.

**Decisions made with Kelly:** we generate our own sounds, event sounds are off until turned on, and the first release has one built-in pack.

**Choices I made, open to correction:**
- Playback uses NAudio.
- Sounds play one at a time from a short queue.
- Background sounds stay quiet during presentations and full-screen use.
- Delete, archive and move sounds stay off even after event sounds are turned on, because those actions repeat quickly.
- Adding a sound to a rule is enough for it to play, unless all sounds are muted. Kelly's default decision covered event sounds, and it would be confusing to add a rule sound and hear nothing.

## Design

### Playback

New `Services/ISoundService.cs` and `SoundService.cs`, wired in `App.xaml.cs` beside the toast service (field, construct, pass as an optional parameter, `_sounds?.Dispose()` in `OnExit`). Passed `null` under `--ui-probe`, like the other services at `App.xaml.cs:646-652`.

```csharp
void Play(SoundEvent e);            // gated by settings; returns at once; callable from any thread
void PlayRule(string? soundRef);    // gated by mute and quiet state only
void Preview(string soundRef);      // explicit, so it plays even when muted
bool IsAudible(SoundEvent e);       // whether the event's toast should be silent
```

- **Engine:** NAudio (MIT), behind a small internal `ISoundOutput` so tests use a fake and the engine can be swapped. `SoundPlayer` has no volume control, and `MediaPlayer` can't read embedded resources without writing them to disk first (the raw `QuickMail.exe` ships as a single file, so embedded it is). Step 1 proves NAudio on x64 and Arm64 before anything else depends on it.
- **Threading:** one background worker reads a bounded queue. Rules run on background threads, and a slow or missing audio device must never delay mail.
- **Device:** the Windows default output, opened per sound rather than held open.
- **Failures never reach the caller.** No device or a bad file gets logged once per sound. A missing custom file falls back to the pack's sound.
- **Flood control** lives in a pure `SoundPolicy` class with an injected clock, so it can be tested:
  - Sounds play one at a time, with at most 3 waiting; further ones are dropped.
  - The same sound within 1 second is dropped.
  - A new-mail sound within 3 seconds of a rule sound is dropped, so a filed message doesn't sound twice.
- **Quiet state:** `SHQueryUserNotificationState` silences background sounds (new mail, watched, rule, connection, reminder) during presentations and full-screen use. Sounds that answer something the person just did (sent, failed, delete) still play. It does not report Do Not Disturb; see below.

### What the spike measured (2026-11-03)

Run in a test VM on x64 and Arm64 self-contained single-file builds, published with warnings as errors, against a virtual sound card, with the output measured by loopback capture.

- **NAudio 3.3.0 works, but not as first planned.** `WasapiOut` is obsolete; use `WasapiPlayerBuilder`. Its default-device stream routing can't be used with `Build()`, and isn't needed because a player is opened per sound.
- **Every sound needs 200 ms of trailing silence.** Playback reports it has stopped while audio is still buffered, so disposing at that point lost about 50 ms of a 600 ms tone. With the lead-out, all of it plays.
- **No audio device** gives one `CoreAudioException` (0x80070490) when the player is built, and the service logs it once.
- **Any sample rate plays correctly:** 8, 22.05, 44.1, 48 and 96 kHz, mono and stereo, at the right length and level. Volume is accurate.
- **MP3 decoding and clear refusals:** MP3 decodes, and junk files throw `FormatException` (WAV) or `MediaFoundationException` (MP3).
- **Toasts do play a sound:** level 0.32 with a normal toast, against 0 for a toast marked silent and 0 with no toast. So the `silent` flag is needed and works.
- **`SHQueryUserNotificationState` reports presentation mode, but not Do Not Disturb.** During Do Not Disturb it still says it accepts notifications, while Windows silences the toast sound itself. Microsoft documents no API for that state; `FocusSessionManager` covers focus sessions, which it says are not the same thing.

**So background sounds play during Do Not Disturb.** The user guide says so, and Mute All Sounds is the way to silence them. This needs Kelly's review at handoff.

Not measured: Media Foundation being absent (Windows N editions), and real hardware such as Bluetooth headsets.

### Events

Each hooks where the outcome is already known, so existing de-duplication carries over. Sounds never replace an announcement or status text.

| Event | On when event sounds are on? | Hook |
|---|---|---|
| Message sent | Yes | `ComposeViewModel.SendAsync:885`; once per drain from `OnOutboxFlushCompleted` if `Sent > 0` |
| Message queued | Yes | `ComposeViewModel.QueueSendAsync:427` |
| Send failed | Yes | `ComposeViewModel.SendAsync:924`; flush handler if `Failed > 0` |
| New mail | Yes | New `MaybePlayMailSounds` (below) |
| Watched conversation mail | Yes | Same |
| Action failed | Yes | Failure branches at `MainViewModel.cs:7989`, `8216`, `9739` |
| Connection lost / restored | Yes | `Connectivity.cs` `AnnounceOfflineOnce:138` and `OnOnlineChanged:323` |
| Calendar reminder | Yes | `CheckReminders:10913` |
| Message deleted / archived / moved | No | Success branches of `DeleteMessagesAsync`, `ArchiveMessagesAsync`, `MoveSelectedMessagesToFolderAsync`; not when cancelled |

`ComposeViewModel` gains an optional `ISoundService?`, passed at its two construction sites (`MainWindow.xaml.cs:6185`, `7464`).

**New mail must not disturb toasts.** `NewMailFilter.SelectNew` consumes what it sees, and the watched-before-inbox order is a tested contract, while `MaybeNotifyNewMail:4302` returns early when toasts are off. Sharing that path would lose toasts in some settings combinations. So add a separate `MaybePlayMailSounds` with **its own key set**, called next to the toast calls at `MainViewModel.cs:4256` and `3894`. It uses the same launch threshold, shared-mailbox opt-in and backlog limit (`MaxNotifyBatchSize`). Toast logic stays as it is.

**No double sound.** Toasts set no audio today, so I expect Windows plays its default sound with them; step 1 confirms this. `ShowNewMail`, `ShowWatchedMail` and `ShowInfo` gain a `silent` flag, set to `IsAudible(event)`. With sounds off, toasts behave exactly as they do now.

### Rule action

Append `RuleAction.PlaySound` to the enum (stored by number) and add `MailRule.Sound` (string, `WhenWritingNull`). The action is client-only, and the existing classifier forces that once it's listed.

- **Run order:** gets its own last bucket in `RunOrder`, so the switch can see what happened. A rule that moves or deletes sounds only if `taken.Count > 0`; a failed move stays quiet.
- **Once per rule pass:** `ExecuteActionsAsync` already runs once per rule per batch, so 50 matches give one sound.
- **Run on Existing Mail stays silent:** `ApplyRulesToExistingAsync:599` shares this method, so add a `playSounds: false` parameter. Otherwise it would sound for every rule across the old mail.
- **Older builds:** `MainAction` must exclude `PlaySound`, or a pre-#682 build would run the wrong thing. A sound-only rule writes `PlaySound`, which those builds ignore. Newer older builds ignore the unknown number but show it raw, and re-saving there drops the action. That's acceptable, and a test pins it.
- **No condition needed:** a sound-only rule can match all mail, since it can't lose anything. `NoConditionError` stays as it is.

All the places a new action must change (from the code survey):
- `Models/MailRule.cs`: enum, `Sound`, `RunOrder`
- `RuleService`: the switch, plus `CloneForAccount:165`, which copies by hand and would silently lose the sound
- `ServerRuleEditorViewModel`: `ForEditClient`, form properties, `ToClientRule`, `ClientActions`, `MainAction`, `ClientOnlyFeaturesUsed`, `HasAnyAction`, `MarksReadAndNothingElse` and its change list, `HasAdvancedContent` if placed there, a `CanPlaySound` disabled for server rules, `Validate`
- `Views/ServerRuleEditorWindow.xaml`
- `Models/UnifiedRuleRow.cs:152`
- `USERGUIDE.md:604`

### Settings and storage

`ConfigModel` / `ConfigService` / `SettingsConfigTransfer` (Portable):
- `PlayEventSounds` (default off)
- `MuteSounds` (default off), which silences everything
- `SoundVolume` (default 100)
- `SoundPack`
- `SoundEvents`, a new `[sounds]` dictionary following `Features`; written before `[windowing]` as the existing comment requires

A sound reference is a string: `builtin:<name>`, `custom:<file>` or `none`. Only a filename is stored, never a full path, and resolving it keeps the path inside the folder (as `ThemeStore.PathForId` does).

**Custom files** are copied into `{profile}\sounds\`, which gives #129 its backup copy and survives the original being moved.
- They are validated on import by actually decoding them: WAV or MP3, at most 5 MB and 30 seconds, with a plain-language error otherwise.
- `SettingsBundle` currently accepts only `.json` under `themes/`, so extend it to carry `sounds/`. Its 64 MB total cap needs a defined behavior for overflow.
- The `EveryProfileFileAServiceWritesIsClassified` test will require the classification.

**Settings → new Sounds tab:**
- Checkboxes "Play sounds for mail events" and "Mute all sounds"
- Volume
- Pack combo (one entry now)
- An event list, with rows like "New mail: Chime"
- For the selected event: a Sound combo, Preview, and Choose File

The VM raises a request and code-behind shows the file dialog (MVVM rule). One list with a single detail area, rather than a combo per event, avoids about 13 extra Tab stops.

**Mute command:** `settings.toggleMuteSounds` (category Settings, no default key), modeled on `settings.toggleCustomAnnouncements` at `MainWindow.xaml.cs:4886`, with a checkable menu item using `sc:CommandShortcut.Id`.

### The sounds

A small generator in `app-kit/sounds/` writes deterministic 44.1 kHz mono WAVs under about 1 second. QuickMail's copies are committed to `QuickMail/Sounds/BuiltIn/<pack>/` as `EmbeddedResource`, about 1 MB in all. Reading the embedded stream works in single-file publish and needs no temporary files.

Sounds should be told apart by pitch and shape, not only by being pleasant: rising for sent, falling for failed, and so on. **Kelly chooses by ear:** candidates go to `OneDrive\testing\quickmail\sounds`, and only his picks ship.

### What goes in app-kit

Added at Kelly's request; The Chat Place and GHManage are candidates for sounds too. Those two are Python/wx and QuickMail is C#, so no playback code can be shared between them. app-kit has no .NET package yet, QuickMail doesn't consume app-kit, and `baseline/BASELINE.md:36` says to share code only where three or more apps use the same stack.

| Piece | Where | When |
|---|---|---|
| Sound files and their generator | `app-kit/sounds/`, not `QuickMail/Tools` | Now. WAV files work in any stack |
| Sound names | Named for meaning (`sent`, `failed`, `arrived`, `attention`), with each app mapping its own events to them | Now |
| Shared behavior spec: off by default, mute, flood limits, quiet state, sounds never replace speech | `app-kit/sounds/README.md` | Now |
| C# playback service and policy | Stay in QuickMail, with no mail types in them, so they can move intact | Moves to `TheIdeaPlace.AppKit` when a second .NET app wants sounds |
| Python player | `theideaplace-appkit` | Follow-up issue, built with the first Python app that adopts sounds |

QuickMail copies the WAVs it uses into `QuickMail/Sounds/BuiltIn/` and records the app-kit commit they came from. Mail-specific parts stay in QuickMail: event hooks, the rule action, Settings UI and config.

## Spec sections (required by CLAUDE.md)

### Keyboard walkthrough

What a screen reader says for standard controls is for Kelly to confirm, not assumed here (step-4 list below).

**Turn on sounds and change one**
1. Open Settings and move to the Sounds tab.
2. Tab to "Play sounds for mail events" and press Space. It reports as checked.
3. Tab to the event list and arrow to "New mail: Chime".
4. Tab to Preview and press Enter. The sound plays; there is no announcement.
5. Tab to the Sound combo and arrow to another sound. The list row changes to match.
6. Tab to Choose File and press Enter. In the file dialog, pick a WAV. The combo now shows that file. A bad file shows and announces (Result) why it was refused, and focus returns to Choose File.
7. Save. Sounds apply at once.

**Sound for one sender (#129)**
1. Open a new rule and enter a From condition.
2. Tab to "Play a sound" and press Space. The Sound combo, Preview and Choose File become available, with the first built-in sound selected.
3. Pick a sound, optionally Preview it, and save.
4. The rule list row says "Play sound: \<name\>". A matching message plays it once per rule pass.

**Mute:** Ctrl+Shift+P, "Mute All Sounds", Enter. Announces "Sounds muted." (Result, `force: true`, like the announcements toggle). Running it again announces "Sounds on."

**Send:** Ctrl+Enter, then the sent sound plays as the existing "Message sent." status is reported and the window closes. Focus behaves as it does today.

### Infrastructure changes

- **F6 ring:** no change; no new primary panes or windows.
- **CommandRegistry:** `settings.toggleMuteSounds`, category Settings, no default key.
- **AutomationProperties.Name:** "Sounds" tab; "Volume"; "Sound events" list; row names from the row's `AccessibleName` on the container, via `ItemContainerStyle`. The rule's "Play a sound" checkbox has no explicit Name, so its client-only qualifier is spoken, as with the other client-only checkboxes.
- **Selector items:** `SoundChoice` and `SoundEventRow` override `ToString()`, are added to `SelectorItemAccessibilityTests`, and are registered in `TypeAheadWiringTests` `Sites`.
- **Announcements:** mute toggle (Result, forced); refused sound file (Result). No announcement accompanies a sound.
- **VM state:** `MainViewModel` gets the sound service and a mute menu check state, resynced in `ApplySettings:2769`.
- **Themes:** if `Slider` isn't already styled, `ThemedControlCoverageTests` requires an implicit style for Volume.
- **Dependencies:** NAudio packages; a new profile folder `sounds\`.
- **Privacy:** no change to `docs/privacy.html`, since there is no new host, scope or bug-report field.

### Out of scope

- Importing or downloading sound packs made by others, and a pack manager window.
- Choosing an output device.
- Sounds from Microsoft server rules, which run on Microsoft's side with no hook in QuickMail.
- Rule sounds outside the Inbox, or while QuickMail is closed (client rules don't run then).
- Sync-error sounds: folder and targeted sync failures are only logged today, so there's nothing reliable to attach to, and #312 covers that area.
- Per-account connection sounds.
- Sounds for refused sends (no recipient, too large), which are immediate validation messages.
- Interface sounds such as keystrokes or navigation.

## Build order

One tracking issue linking #129, with the spec committed as `docs/planning/sounds-pm-dev-spec.md`. Each PR gets tests, independent `code-reviewer` review, its own user-guide changes, and merges when CI is green.

1. **Spike, in the VM (nothing merged).** Prove on x64 and Arm64 single-file publish, including `-warnaserror`:
   - NAudio plays an embedded WAV
   - MP3 decoding, and behavior when Media Foundation is absent
   - behavior with no audio device
   - whether toasts really play default audio, and what `SHQueryUserNotificationState` reports under Do Not Disturb
2. **PR 1, infrastructure.** Service, policy, output seam, config, profile folder and bundle, mute command, stubs, a test WAV; no events yet.
3. **Sounds and audition.** SoundGen, candidates to OneDrive. **Stop for Kelly's picks** (push notification). Everything else continues with placeholders.
4. **PR 2, events and Settings tab.** Hooks, `MaybePlayMailSounds`, silent toasts, the Sounds tab, custom file import.
5. **PR 3, rule action.** Every location listed above.
6. **PR 4, final sounds and docs.** Chosen sounds; `USER-GUIDE.md` (Sounds section and Contents, Rules, Notifications, Settings, moving settings), `ARCHITECTURE.md` service paragraph, `KEYBOARD-SHORTCUTS.md` row, release notes.

## Verification

All tests and app runs happen in the VM (`vmtest` skill), never on Kelly's PC. Build `QuickMail.IntegrationTests` as well as the app, and run the full suite.

**Unit tests:**
- `SoundPolicy`: queue cap, same-sound drop, rule-then-new-mail drop, quiet state, mute
- `SoundRef` parsing and path escape attempts
- Corrupt, oversized and misnamed custom files
- Failing output doesn't throw
- Config round-trip, `[sounds]` placement, and classification
- Every event has an embedded sound; length and peak limits

**Rule tests** (extending `ClientRuleActionsTests`, `RuleEditorValidationTests`, `RuleServiceTests`, `RuleMigrationTests`, `UnifiedRuleRowTests`, `ServerOnlyOptionsDisabledTests`):
- Failed move stays silent
- 50 matches give one sound
- Run on Existing Mail stays silent
- `CloneForAccount` keeps `Sound`
- `MainAction` never returns `PlaySound` beside another action
- A `rules.json` with an unknown action number loads

**Regression:** existing toast and watched-order tests pass unchanged; with sounds off, toasts have no `silent` change.

**Integration:** GreenMail run where a message matches a move-and-sound rule, using a recording output, and asserting a single sound with no new-mail sound after it.

**Real app in the VM:**
- Every entry point, in normal and `--online` mode
- A send with the network down (queued, then sent on drain)
- `scripts/ui-probe.ps1` for the Sounds tab and rule editor in parchment and dark (add the surface to `ui-probe-plan.json`)
- `sr-test` speech check for the Sounds tab and rule editor

**Kelly to check** (only a person can judge):
- Choose among the candidate sounds.
- With a screen reader running, do sounds play well over speech, or cut it off or get lost? Is the volume right next to speech?
- Tab through the Sounds tab and the rule editor's sound controls: do you hear what you expect?
- New mail with a toast: one sound, not two?
- A rule for your own address on real mail, and Mute All Sounds.
- Bluetooth or USB headset: is the start of a sound clipped?

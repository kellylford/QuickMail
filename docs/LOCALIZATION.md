# Localization

QuickMail's UI ships in English, Spanish, German, and French. Every user-visible string —
XAML labels, screen reader announcements, dialog text, command palette titles — lives in
`.resx` resource files instead of hardcoded literals. This doc covers what you need to know
to develop, test, and ship with localization in place. For the original design rationale
(why `.resx`, why restart-to-apply, why the spoken UI counts as a localization surface),
see `docs/planning/localization-pm-dev-spec.md`.

## The files

| File | Purpose |
|---|---|
| `QuickMail/Resources/Strings.resx` | The neutral catalog — English source of truth. Every key lives here. |
| `QuickMail/Resources/Strings.es.resx` | Spanish translation. Same keys as neutral, translated values. |
| `QuickMail/Resources/Strings.de.resx` | German translation. |
| `QuickMail/Resources/Strings.fr.resx` | French translation. |
| `QuickMail/Resources/Strings.Designer.cs` | **Generated. Never hand-edit.** Strongly-typed accessor (`Strings.SomeKey`) regenerated from `Strings.resx` on every build. |
| `QuickMail/Resources/StringsHelper.cs` | `StringsHelper.Count(baseKey, n)` — pluralization (resolves `baseKey_One` / `baseKey_Other`). |
| `scripts/Generate-ResxDesigner.ps1` | Runs automatically as an MSBuild step before every compile; regenerates `Strings.Designer.cs` from `Strings.resx`. |
| `scripts/check-resx-keys.ps1` | Run manually. Diffs each language file's key set against neutral and reports missing/extra keys. |

All four `.resx` files currently carry the same 1263 real keys (`check-resx-keys.ps1` reports
"full parity" for all three languages as of this writing).

## Day-to-day tasks

### Change existing translated text
Open the language file, find the key, edit the `<value>`, save, rebuild. No code involved.

```
QuickMail/Resources/Strings.es.resx
  <data name="Compose_SendButton" xml:space="preserve">
    <value>Enviar</value>          <-- edit this
  </data>
```

### Add a brand-new string (as a developer)
1. Add a `<data name="YourNewKey" xml:space="preserve"><value>English text</value></data>` block
   to `QuickMail/Resources/Strings.resx`. Key names must be valid C# identifiers
   (letters/digits/underscore, can't start with a digit).
2. Reference it:
   - XAML: `{x:Static strings:Strings.YourNewKey}` (root element needs
     `xmlns:strings="clr-namespace:QuickMail.Resources"` — every existing view already has this).
   - C#: `Strings.YourNewKey`, or `StringsHelper.Count("YourNewKey", n)` for a count that needs
     singular/plural forms (`YourNewKey_One`, `YourNewKey_Other`).
   - A composed sentence with more than one dynamic value should use indexed placeholders —
     `string.Format(Strings.YourNewKey, arg0, arg1)` with `{0}`/`{1}` in the resource value —
     so a translator can reorder them for their language's grammar.
3. Build. `Strings.Designer.cs` regenerates automatically (see "How the build works" below).
   If you reference a key that doesn't exist, you get a build error
   (`MC3011` in XAML, `CS0117` in C#) — not a silent blank at runtime. That's deliberate.
4. Translate the new key into `Strings.es.resx` / `.de.resx` / `.fr.resx` when you can. Until
   then, .NET's resource fallback silently uses the English value in that language — the app
   still runs correctly, that language is just not fully translated yet.

### Add a new language
1. Copy `Strings.resx` to `Strings.<culture>.resx` (e.g. `Strings.it.resx` for Italian —
   use the two-letter culture code) and translate every value.
2. Add the culture tag to `SupportedUiLanguages` in `App.xaml.cs` (`ApplyUiCulture`'s
   allow-list — both the "follow Windows language" auto-detect and the config value are
   checked against it).
3. Add a `ComboBoxItem` to the language group in `SettingsDialog.xaml`, plus a
   `Settings_Language_<Name>` display-string key (see the existing `es`/`de`/`fr` entries
   for the pattern — native name, with an English gloss in parentheses for the *other*
   languages' entries, e.g. Spanish's own picker shows "Deutsch (German)").
4. Build. The .NET SDK auto-detects `Strings.it.resx` by its filename and produces the
   satellite assembly with zero other project configuration.
5. Run `.\scripts\check-resx-keys.ps1` to confirm full key parity before enabling it.

### Switch language while testing
- In the running app: Settings → General → "Application language" → pick one → it announces
  a restart is required → relaunch.
- Directly: edit `config.ini` in the profile directory and set `UILanguage = es` (or `de`,
  `fr`; empty = follow Windows display language, falling back to English if that's not one
  of the supported languages).
- `--profileDir <path>` (see main `CLAUDE.md`) lets you keep a separate profile per test
  language side by side, so you don't have to keep flipping one profile's setting back and
  forth.

### Check translation completeness
```
.\scripts\check-resx-keys.ps1
```
Reports, per language file: keys **missing** (present in neutral, absent here — falls back
to English at runtime, not a crash, but a signal that language isn't finished) and keys
**extra** (present here but not in neutral — a stale/orphaned key, worth deleting). Run this
before enabling a language in the picker, and again before any release where translations
changed.

### Enforcement: catching a new hardcoded string before it merges

`QuickMail.Tests/LocalizationRegressionGuardTests.cs` runs as part of the normal
`dotnet test` suite — the same command `build.bat` and CI already run on every PR — so a new
hardcoded, untranslated string fails the build instead of quietly shipping English-only text.
It checks two things:

1. **View XAML** (`Views/`, `Controls/`, `Styles/`, `App.xaml`) — flags any `Content`, `Text`,
   `Header`, `ToolTip`, `Title`, `AutomationProperties.Name`, or `AutomationProperties.HelpText`
   set to a literal string instead of `{x:Static strings:Strings.*}`.
2. **Code-behind** — flags any literal string (not `Strings.*`/`StringsHelper.Count(...)`)
   passed to `MessageBox.Show(...)` or `AccessibilityHelper.Announce(...)`.

If the test fails, the fix is almost always: add the key to `Strings.resx` (+ es/de/fr, or at
least leave es/de/fr for later — `check-resx-keys.ps1` will report it as missing until you do)
and reference it instead of the literal, per "Add a brand-new string" above.

**Known limitation**: the code-behind check matches the literal call text `MessageBox.Show(`/
`AccessibilityHelper.Announce(`, not the resolved method — a local wrapper method (e.g. a
private `Announce(...)` helper that calls `AccessibilityHelper.Announce` internally) evades
detection at its own call sites. Don't rely on this guard alone when adding a wrapper like that;
route the text through `Strings.*`/`StringsHelper.Count` by inspection, the same as everywhere
else.

A small, explicitly reviewed allowlist exists in that test file for the rare deliberate
exception — e.g. rich-text toolbar glyphs like "B"/"I"/"U" that are conventionally shown
identically in every locale, or `App.xaml.cs` startup/crash paths that run before the user's
language preference is even loaded from config. Extending the allowlist should be rare and
each entry should say *why* right next to it, the same way the existing entries do.

## How the build works

Nothing about `build.bat` changes. Every target already produces all four languages with no
extra flags or steps:

- **`build.bat`** / **`build.bat release`** (`dotnet build`) — compiles the main assembly and,
  because .NET's resource-generation step auto-detects any `Strings.<culture>.resx` sitting
  next to the neutral file, also produces satellite resource assemblies at
  `bin\<Debug|Release>\net8.0-windows\win-x64\<culture>\QuickMail.resources.dll`
  (`de\`, `es\`, `fr\`). Confirmed present after a clean rebuild.
- **`build.bat clean`** — removes `bin/`, `obj/`, and `publish/` as before; nothing localization-specific to clean up separately.
- **`build.bat publish`** (`dotnet publish -c Release`, single-file self-contained) — the
  satellite assemblies get **bundled inside the single `QuickMail.exe`**, not shipped as
  separate `de\`/`es\`/`fr\` folders next to it. Verified directly: after a publish, the
  `publish\` folder contains only `QuickMail.exe`, `QuickMail.pdb`, and the WebView2 XML docs
  — no per-culture subfolders — and grepping the compiled `QuickMail.exe` binary for
  Spanish/German/French strings (e.g. `Idioma de la aplicación`, `Anwendungssprache`,
  `Langue de l'application`) finds them embedded in the exe itself. This is standard .NET
  single-file bundling behavior, not something this feature added, but it was worth verifying
  rather than assuming — it's exactly the risk the original design spec flagged as needing a
  check before shipping.
- **`build.bat installer`** — packages `publish\QuickMail.exe` via Inno Setup
  (`installer/quickmail.iss`). **No changes were needed here.** The `[Files]` section only
  ever shipped the one exe (see `docs/INSTALLER.md`), and since all four languages are
  already bundled inside that exe by the publish step above, the installer carries them
  automatically. There is nothing to add to `quickmail.iss`.
- **GitHub Actions release workflow** — same reasoning applies; it runs the same
  `dotnet publish` and ships the same self-contained `QuickMail.exe`. No workflow changes
  needed for localization specifically.

## What's *not* done — read before shipping a language to users

These are machine-generated drafts, exactly as intended at this stage (see spec Decision E
and Section 9): AI produces the first pass, a human reviews before it's trusted in front of
users. Specifically not yet done:

- **No native-speaker review.** Tone, natural phrasing, and regional register (the translations
  used es-ES/es-419 neutral Spanish, formal "Sie" German, formal "vous" French — all
  reasonable defaults, not confirmed choices) haven't been checked by a fluent speaker.
- **No cross-file mnemonic-collision check.** Each string's access-key underscore (`_Guardar`)
  was checked for uniqueness within the file/window it came from, not exhaustively across
  every sibling menu in the app. A German or French build could have two commands in the same
  menu accidentally sharing an accelerator key — worth a manual keyboard pass per language
  before shipping it as a first-class option.
- **No layout/truncation pass.** German text in particular runs ~30% longer than English;
  nothing has been checked for clipped labels or wrapped buttons in a translated build.
- **No RTL support** — out of scope for this phase entirely (see spec Decision F).
- **Dynamic language switching is intentionally not supported** — changing the language always
  requires a restart (see spec Decision B). Don't try to make `{x:Static}` bindings update live;
  that's a deliberate scope boundary, not a bug.

Practically: es/de/fr are safe to ship functionally (full build + full test suite green,
satellite bundling verified), but treat them as beta-quality translations until someone fluent
in each language does a keyboard-and-speech walkthrough of the core mail flows, per the
Acceptance Walkthrough scenarios in the design spec (Section 8).

# EditBoxTest — NVDA typed-word echo repro (issue #300)

A minimal WPF app that reproduces [#300](https://github.com/kellylford/QuickMail/issues/300):
NVDA with keyboard echo set to **Words** does not echo per-word while typing in
QuickMail's message body — the whole run of letters is spoken at once when a
period/punctuation is typed.

This app is built to mirror how `QuickMail/Views/ComposeWindow.xaml` sets up its
editors, so the behavior can be studied without the rest of QuickMail:

- a multiline `TextBox` (like `BodyBox`)
- a `RichTextBox` (like `RichBodyBox`)
- WPF spell-check enabled **in code**, deferred to Background dispatcher priority
  (matching `ComposeWindow.EnableSpellCheckDeferred`)

## Run

```
dotnet build -c Release
bin/Release/net8.0-windows/EditBoxTest.exe
```

## Controls

- **Alt+1** — focus plain body (TextBox)
- **Alt+2** — focus rich body (RichTextBox)
- **Alt+3** — focus the "Disable input method (TSF)" checkbox
- **Alt+4** — focus the "Enable spell-check" checkbox

(The editors have `AcceptsTab="True"`, so Tab stays inside them — use the Alt keys
to move between fields.)

## Diagnostic toggles

- **Disable input method (TSF)** — flips `InputMethod.IsInputMethodEnabled` on both
  editors. This was candidate fix #1 (documented workaround for related WPF↔NVDA
  text bugs). **Observed to make no difference** to NVDA word-echo.
- **Enable spell-check** — WPF spell-check runs the NL speller over TSF and is a
  suspect for forcing the TSF composition path. Uncheck it to test whether NVDA
  per-word echo returns.

## Where the investigation stands

See the investigation notes in issue #300. Short version: NVDA reads typed
characters from raw `WM_CHAR` messages (`nvdaHelper/remote/typedCharacter.cpp`)
and flushes a spoken word on the first non-letter character
(`source/speech/speech.py`). The leading hypothesis is that WPF commits the space
through a TSF edit session, and NVDA's own TSF handler
(`nvdaHelper/remote/tsf.cpp` → `OnEndEdit`) nulls its tracking window and drops
the space's `WM_CHAR`, so no word boundary is seen until a character slips through
on the plain path (the period). Not yet confirmed at the message level.

**Best next step:** set NVDA's logging level to Input/output and read the log
while typing — it records each typed character and typed word, which will show
definitively whether NVDA receives the space.

# Message editor keyboard tests

Drives a **published QuickMail** with **real keystrokes** and checks the message editor's actual
text and caret after each. It is the test of record for the hosted editor (#1099): the editor's
unit tests cannot see a key go to the wrong place, and the hosted editor's worst faults were
exactly that.

Three rules it is built on, each learned from a fault that got through:

1. **Keys are typed without bringing the window to the front between them.** The window is
   brought forward once, when QuickMail starts. A harness that re-activates the window before
   every key repairs focus that a person's next key would find broken, and so passes builds that
   are broken. (That is how build 4's focus faults were missed.)
2. **Focus is proven by typing.** `typing-works` types a letter and requires it to arrive in the
   editor. UI Automation reported "Message body, focused" while typed text went nowhere.
3. **The editor is read, not copied.** Text and selection come from UI Automation's text
   pattern, the way a screen reader reads them, not from Ctrl+A, Ctrl+C, which are themselves
   keys under test.

## Running it

Never on Kelly's PC: it types into whatever has focus. In the test VM, or in CI.

```
dotnet publish Tools/EditorKeyTests -c Release -r win-arm64 --self-contained true -o <folder>
<folder>\EditorKeyTests.exe --exe <QuickMail.exe> --profile <fixture profile folder>
    [--cases <dir or file>] [--report <file.md>] [--label "NVDA"]
    [--filter <text in a case name or file name>] [--modes plain,markdown,html] [--delay 60]
```

- `--profile` is a fixture profile (`Tools/QuickMail.Fixtures`) with **Use the new message
  editor** on. It is copied to a temporary folder for every start, so nothing carries over.
- Exit code 0 when every case passed, 1 when any failed, 2 when the run could not start.
- After a failed case QuickMail is restarted, so one fault cannot pass for many or hide them.
- Run it with no screen reader, and again with each screen reader running (`--label` names it in
  the report). A screen reader can change which keys reach QuickMail, so the same cases are
  worth running under each. What the screen reader *says* is tested separately, by the speech
  steps in `Tools/EditorPrototypes/nvda-steps/`.

## Case files (`cases/*.keys`)

One step per line, under a `== Name` line. A case runs once in each format unless `modes:` says
otherwise. `#` starts a comment.

| Step | Meaning |
|---|---|
| `modes: plain markdown html` | The formats to run the case in. Default: all three. |
| `judgment: ...` | The expected result is a judgment call; the question is listed in the report. |
| `text: one two\nthree` | Sets the body. `\n` is a line end, `\t` a tab. Quote it to keep spaces at the ends. |
| `markdown: # Title` | HTML only: sets a formatted body by writing Markdown and switching to HTML. |
| `caret: before "two"` | Puts the caret: `start`, `end`, `at 5`, `before "x"`, `after "x"`, or `select "x"`. |
| `keys: ^{RIGHT}` | Presses keys. `^` Ctrl, `+` Shift, `%` Alt; `{RIGHT}`, `{F7}`, `{DOWN 3}`. |
| `type: hello` | Types text, a key at a time. |
| `wait: 1.5` | Waits, in seconds. |
| `window: main` | Switches to QuickMail's main window (`main`) or back to the message (`message`), as Alt+Tab does. |
| `caret-is: before "three"` | The caret must be there, with nothing selected. |
| `selection-is: "two "` | The selected text must be exactly this. |
| `text-is: one three` | The body's text must be exactly this. |
| `mode-is: html` | The window's title must show this format. |
| `front-is: Insert Link` | The window in front must have this in its title. |
| `focus-is: Subject` | UI Automation's focused element must have this in its name. |
| `typing-works` | Types a letter; it must arrive at the caret. Then removes it. |
| `markdown-is: one **two**` | HTML only: switches to Markdown, compares the body, and switches back. |

Steps run in order, so a case can press keys, check, press more and check again.

## Adding a key

Every key the editor is meant to handle belongs in a case. A key that is not in a case file is
untested, and the report makes that visible: it lists every case, per format.

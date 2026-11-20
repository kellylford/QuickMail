# Built-in sounds

These WAV files are generated, not recorded. The script that makes them, what each sound means, and the rules for how an app should use sounds are in the kit other apps share: [`sounds/` in TheIdeaPlace/app-kit](https://github.com/TheIdeaPlace/app-kit/tree/main/sounds) ([PR 26](https://github.com/TheIdeaPlace/app-kit/pull/26)).

The copies here are byte-identical to what that script produces. To change a sound, change it there and copy the result here, so the apps stay the same.

`edge.wav` (issue #923, "Reached the end of a list") is ahead of app-kit: it was made by that script with one entry added, which app-kit should take so the two match again. In `SOUNDS`: `"edge": [(A4, 0.00, 1.0)]`, one soft, short middle note; in `SOFT`: `"edge": {"peak": 0.30, "tail": 0.15}`, since it plays often and sits under speech. Every other file the script wrote with that change was unchanged.

# Built-in sounds

These WAV files are generated, not recorded. The script that makes them, what each sound means, and the rules for how an app should use sounds are in the kit other apps share: [`sounds/` in TheIdeaPlace/app-kit](https://github.com/TheIdeaPlace/app-kit/tree/main/sounds) ([PR 26](https://github.com/TheIdeaPlace/app-kit/pull/26)).

The copies here are byte-identical to what that script produces. To change a sound, change it there and copy the result here, so the apps stay the same.

`suggest.wav` ("Address suggestions appeared") and `inserted.wav` ("Address inserted from suggestions"), issue #1048, come from that script as of [app-kit PR 48](https://github.com/TheIdeaPlace/app-kit/pull/48) (commit 4475250), byte-identical.

`edge.wav` (issue #923, "Reached the end of a list") was made here first and then taken into app-kit in [PR 43](https://github.com/TheIdeaPlace/app-kit/pull/43), so it too matches what the script produces.

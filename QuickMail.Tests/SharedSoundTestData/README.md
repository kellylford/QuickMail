# Shared sound scheme test files

Copied unchanged from TheIdeaPlace/app-kit, `sounds/testdata/`, at commit
1e9268855c6c906a179425df486885389a5bbaaa ("Sound schemes and the gallery: one spec, shared test
files and the Python implementation"). The spec they test is app-kit's `sounds/SCHEMES.md`.

Every app that reads sound schemes reads these same files and checks it gets what `expected.json`
says, so QuickMail reads them the way The Chat Place and app-kit's Python do.
`SharedSoundTestDataTests` does that for QuickMail.

- `index-*.signed`: gallery lists, signed with app-kit's test key. `test-key.pub.pem` is the key to
  read them with. It is for tests only and signs nothing real. (app-kit's private test keys and the
  script that made these files are not copied.)
- `*.qmsounds`, `*.tcpsounds`: scheme files, some deliberately hostile.
- `expected.json`: what each file must give, for each app that reads it.
- `apps.json`: copied from app-kit's `sounds/apps.json` at the same commit. It lists each app's
  events in order; the tests check QuickMail's own list matches it. **One local change:** its
  QuickMail entry also lists `ListEnd` ("Reached the end of a list", sound `edge`, issue #923),
  which app-kit's copy doesn't have yet. When app-kit's `apps.json` and `generate.py` gain it,
  copy the folder again as usual and drop this note.

Don't edit these files here. To pick up a change, copy the folder from app-kit again and update
the commit above. `.gitattributes` marks the folder binary, so a signature keeps its exact bytes.

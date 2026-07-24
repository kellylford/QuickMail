# Integration Testing (live protocol servers)

`QuickMail.IntegrationTests` exercises real protocol code — `ImapMailService`, `SmtpService`,
and (in later phases) the CalDAV/CardDAV clients — against local servers, per the
[live-content testing plan](planning/live-content-testing-plan.md) (issue #304).

These tests are **separate from `QuickMail.Tests`** (the stub-based unit suite) and only run
meaningfully when the local servers are up. Without them, the tests skip with an explanatory
message, so running `dotnet test` on the whole solution stays green on any machine.

## Prerequisites

- **Java 11+** (any JRE — Temurin recommended). The server script finds it via `JAVA_HOME`,
  `PATH`, or an explicit `-JavaExe` argument.
- **Python 3 with pip** — for Radicale (CalDAV/CardDAV). The script pip-installs the pinned
  Radicale version on first run.

## Running locally

```powershell
# one-time per session: start GreenMail (IMAP 127.0.0.1:3143, SMTP 127.0.0.1:3025)
# and Radicale (CalDAV/CardDAV 127.0.0.1:5232)
.\scripts\start-test-servers.ps1

# run the integration suite
dotnet test QuickMail.IntegrationTests/QuickMail.IntegrationTests.csproj -c Release

# stop the servers
.\scripts\start-test-servers.ps1 -Stop
```

The script downloads the pinned GreenMail standalone jar from Maven Central once, into
`.testservers/` (gitignored), and writes server logs there (`greenmail.log`).

GreenMail runs with **auth disabled**: any username/password authenticates and mailboxes are
created on first use. Tests create unique per-run users (`<prefix>-<guid>@greenmail.test`), so
no server-side user configuration exists and tests never collide.

Radicale runs with **auth-type `none`** (same idea: any credentials work; each user owns the
`/<user>/` collection hierarchy) and is launched through `scripts/radicale-launcher.py`, which
works around a Radicale-on-Windows startup crash: its symlink-support probe catches only
`PermissionError`, but an unprivileged Windows `os.symlink` raises `OSError` (WinError 1314).
The shim makes the probe fail soft (symlink support is an optional storage optimization).
Tests create collections explicitly via `RadicaleFixture.CreateCalendarAsync` /
`CreateAddressbookAsync` and seed events/contacts with raw `PUT`s.

## CI

The `integration` job in `.github/workflows/quickmail.yml` starts the same servers on the
`windows-latest` runner (Java is preinstalled) and runs the suite with
`QUICKMAIL_IT_SERVERS=1`. That variable flips server-absence from *skip* to *fail*: in CI a
missing server is an infrastructure error and must never look like a passing run. Server logs
are uploaded as artifacts on every run.

## Known GreenMail limitations

**Multipart section fetch is broken.** GreenMail's `BODY[n.MIME]` FETCH response omits the
blank line that must terminate the MIME headers (RFC 3501 / RFC 822), so when MailKit
reassembles a nested body part it parses every body line as a header and yields an **empty
entity**. Verified against GreenMail 1.6.15 and 2.1.11 with the raw protocol log; single-part
messages are unaffected (MailKit fetches `BODY[]` for a top-level part, which GreenMail serves
correctly). Related upstream reports: [greenmail#172](https://github.com/greenmail-mail-test/greenmail/issues/172),
[MailKit#723](https://github.com/jstedfast/MailKit/issues/723).

Consequences for this suite:

- Seed **single-part** messages when the test must read the body back through
  `ImapMailService` (plain text, or a bare `text/calendar` invite).
- Tests that genuinely require multipart body decoding are checked in but `Skip`-annotated
  with a pointer here (see `InvitePersistenceTests.MultipartInvite_...`), so the gap stays
  visible in test output instead of silently narrowing coverage.
- Multipart-heavy coverage belongs to a future tier (recorded IMAP fixtures or a
  spec-compliant server), tracked in the live-content testing plan.

## Writing integration tests

- Join the collection: `[Collection(GreenMailCollection.Name)]`, take `GreenMailFixture` in the
  constructor, and call `fixture.RequireServers()` first in every test.
- Get accounts from `fixture.CreateAccount("prefix")` — unique user per call, password auth,
  pointed at GreenMail. Pass any password to `ConnectAsync`.
- Seed mail by SMTP delivery (see `InvitePersistenceTests.DeliverAsync`) or IMAP `APPEND`.
- No WPF: this project has no STA facts. Test services directly; VM/UI behavior belongs in
  `QuickMail.Tests`.
- Poll with a deadline (see `WaitForInboxMessageAsync`) rather than fixed sleeps.

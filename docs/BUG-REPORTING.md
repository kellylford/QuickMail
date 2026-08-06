# Bug reporting — how it works and how to fix it

What happens when a user chooses **Help → Report a Bug**, where every moving part lives, and
what to check when it stops working.

Setup instructions for building the relay from scratch are in
[`relay/README.md`](../relay/README.md). This document is the runbook for when it already
exists and something is wrong.

---

## The path a report takes

1. The user fills in **Report a Bug** — [`Views/ReportBugWindow.xaml`](../QuickMail/Views/ReportBugWindow.xaml),
   driven by [`ViewModels/ReportBugViewModel.cs`](../QuickMail/ViewModels/ReportBugViewModel.cs).
   The window shows a read-only preview (`PreviewText`) of exactly what will be sent.
2. **Send** calls `BugReportService.SubmitAsync`
   ([`Services/BugReportService.cs`](../QuickMail/Services/BugReportService.cs)), which POSTs
   `{title, body}` as JSON to the relay with an `X-QuickMail-Key` header.
3. The relay — a Cloudflare Worker, [`relay/src/index.js`](../relay/src/index.js) — checks the
   key, rate-limits by IP, signs a JWT with the GitHub App's private key, exchanges it for a
   one-hour installation token, and creates the issue.
4. The relay returns `{issueUrl, number}`. The window shows the link.

If any of that fails, the VM falls back: **Copy and Open** puts the full report on the
clipboard and opens a pre-filled `issues/new` URL in the browser. That path needs no relay and
no credentials — but it does need the user to have a GitHub account, which is exactly what the
relay exists to avoid.

### What the app does *not* do

- It never holds a GitHub credential. The relay key it ships authorises one thing: "file an
  issue on kellylford/QuickMail".
- It never sends labels. The relay applies `bug` and `user-reported` itself, so an extracted
  relay key cannot apply arbitrary ones.
- It never reads `quickmail.log` or any message content. Product decision, see
  `docs/planning/bug-reporting-pm-dev-spec.md` §4.2.

---

## Where each piece is configured

| Piece | Value | Where |
|---|---|---|
| Relay endpoint | `https://quickmail-bug-relay.quickmail.workers.dev/report` | Cloudflare Workers |
| GitHub App | `quickmail-bug-reporter`, App ID `4506736` | <https://github.com/settings/apps/quickmail-bug-reporter> |
| App installation | `151714686`, scoped to this repo, Issues read/write only | <https://github.com/settings/installations> |
| Cloudflare account | `7c86d5a72a98b9de96e24f55aaecf9af`, under kelly@kellford.com | <https://dash.cloudflare.com> |

**Repository secrets** (<https://github.com/kellylford/QuickMail/settings/secrets/actions>):

| Secret | Used by | For |
|---|---|---|
| `BUG_REPORT_RELAY_KEY` | build workflows + deploy workflow | compiled into the app; synced to the Worker |
| `RELAY_GITHUB_APP_ID` | deploy workflow | synced to the Worker |
| `RELAY_GITHUB_INSTALLATION_ID` | deploy workflow | synced to the Worker |
| `RELAY_GITHUB_PRIVATE_KEY` | deploy workflow | synced to the Worker |
| `CLOUDFLARE_API_TOKEN` | deploy workflow | authenticates the deploy |
| `BUG_REPORT_TOKEN` | *legacy* | the pre-relay PAT — see "Retiring the old token" |

**Repository variables** (same page, Variables tab): `BUG_REPORT_RELAY_URL`,
`CLOUDFLARE_ACCOUNT_ID`.

**Worker secrets** at Cloudflare — `GITHUB_APP_ID`, `GITHUB_INSTALLATION_ID`,
`GITHUB_PRIVATE_KEY`, `RELAY_KEY`. These are *copies*, pushed by the deploy workflow. Changing
a repository secret does nothing until the workflow re-runs **with the secrets box ticked**.

**Compiled into the app**: the four build workflows (`quickmail.yml`, `codeql.yml`,
`build-installer.yml`, `live-smoke.yml`) each write
`QuickMail/Services/BugReportService.Credentials.cs` at build time from
`BUG_REPORT_RELAY_URL` and `BUG_REPORT_RELAY_KEY`. That file is gitignored; locally it holds
empty placeholders, which is why **local debug builds always take the clipboard fallback.**
That is expected, not a bug.

---

## Start here when something is wrong

| What you see | Most likely cause | Go to |
|---|---|---|
| App says "This build has no relay configured" | local build, or CI variable missing | [A](#a-build-has-no-relay-configured) |
| App says "Could not reach the bug-report relay" | network, or the Worker is not deployed | [B](#b-cannot-reach-the-relay) |
| App reports status 401 | relay key mismatch between app and Worker | [C](#c-401-key-mismatch) |
| App reports status 502 | Worker cannot reach GitHub — App credentials or installation | [D](#d-502-worker-cannot-create-the-issue) |
| App says "Too many reports… try again in a minute" | rate limit, working as designed | [E](#e-429-rate-limited) |
| Issues appear authored by **kellylford** | an old build still using the PAT | [F](#f-issues-attributed-to-the-maintainer) |
| Issues appear with no labels | labels are applied by the relay; check its deploy | [D](#d-502-worker-cannot-create-the-issue) |
| Everything works but you changed a secret | secrets are not live until re-synced | [G](#g-changing-a-secret) |

### Quick end-to-end check

Confirms the relay, the App, and the token exchange all still work. Needs the relay key:

```bash
curl -X POST https://quickmail-bug-relay.quickmail.workers.dev/report -H "X-QuickMail-Key: PASTE_RELAY_KEY" -H "Content-Type: application/json" -d "{\"title\":\"Relay check - ignore\",\"body\":\"Close me.\"}"
```

Success is `{"issueUrl":"...","number":N}`. Close the issue afterwards. If `curl` fails with
a `schannel` TLS error on Windows, that is local curl, not the relay — use PowerShell:

```powershell
Invoke-RestMethod -Method Post -Uri "https://quickmail-bug-relay.quickmail.workers.dev/report" -Headers @{ "X-QuickMail-Key" = "PASTE_RELAY_KEY" } -ContentType "application/json" -Body '{"title":"Relay check - ignore","body":"Close me."}'
```

---

## A. Build has no relay configured

The app checks `RelayUrl` and `RelayKey` before doing anything; if either is blank it fails
straight to the clipboard path.

- **Local builds**: expected. `BugReportService.Credentials.cs` holds empty placeholders.
  To test the real path locally, copy `docs/BugReportService.Credentials.example` over it and
  fill in the two values.
- **Released builds**: the CI variable or secret is missing. Check that
  `BUG_REPORT_RELAY_URL` exists as a repository **variable** (not a secret) and
  `BUG_REPORT_RELAY_KEY` as a **secret**:

```bash
gh variable list && gh secret list
```

The URL must end in `/report`. Without it the Worker returns 404 and the app reports a
status-404 failure rather than this message.

## B. Cannot reach the relay

`SubmitAsync` catches `HttpRequestException`/timeouts and reports this. Distinguish "user is
offline" from "Worker is gone":

```bash
curl -s -o /dev/null -w "%{http_code}\n" -X POST https://quickmail-bug-relay.quickmail.workers.dev/report
```

`401` means the Worker is alive and rejecting an unauthenticated request — good. Anything
else, or no response, means the deploy is missing. Check it exists:

**Cloudflare dashboard** → **Compute** → **Workers & Pages** → `quickmail-bug-relay`.

If it is not there, re-run the deploy (see [G](#g-changing-a-secret)).

## C. 401 key mismatch

The `X-QuickMail-Key` the app sends does not equal `RELAY_KEY` at Cloudflare. Neither value is
readable after being set, so you cannot compare them — you can only re-set both from one known
value.

1. Generate a new key: `node -e "console.log(require('crypto').randomBytes(32).toString('base64url'))"`
2. Set it as the `BUG_REPORT_RELAY_KEY` repository secret.
3. Re-run the deploy workflow **with the secrets box ticked** — this pushes it to Cloudflare.
4. Ship a build so the app carries the matching key.

Between steps 3 and 4, already-released apps will get 401 and fall back to the clipboard path.
That is the unavoidable cost of rotating; it is why the key is only rotated when it has to be.

## D. 502 — Worker cannot create the issue

The relay reached GitHub and GitHub refused. The relay deliberately returns a generic message
so a caller cannot probe the App's permissions, so the real error is only in the Worker log:

**Cloudflare dashboard** → **Workers & Pages** → `quickmail-bug-relay` → **Logs** → **Begin
log stream**, then reproduce.

What the underlying GitHub status usually means:

| GitHub status | Cause |
|---|---|
| 401 on `/app/installations/.../access_tokens` | wrong private key, or wrong App ID |
| 404 on `/app/installations/.../access_tokens` | wrong installation ID |
| 404 on `/repos/.../issues` | the App is no longer installed on this repo |
| 403 | the App's Issues permission was reduced below Read and write |

Verify the App is still installed with Issues read/write:
<https://github.com/settings/installations> → **Configure** beside QuickMail Bug Reporter.

**If the private key is the problem, do not regenerate it casually** — generating a new key at
GitHub does not revoke the old one, but you must then push the new one through
`RELAY_GITHUB_PRIVATE_KEY` and re-run the deploy with secrets, or nothing changes.

### The PKCS#1 trap

GitHub issues App private keys as **PKCS#1** (`-----BEGIN RSA PRIVATE KEY-----`). WebCrypto,
which the Worker uses, can only import **PKCS#8**. The Worker converts between them itself
(`wrapPkcs1AsPkcs8` in `relay/src/index.js`), and
[`relay/test/jwt.test.js`](../relay/test/jwt.test.js) asserts the conversion is byte-identical
to a real PKCS#8 export.

This is the live path, not a fallback. If you ever touch that code, run:

```bash
node relay/test/jwt.test.js
```

A broken conversion fails at runtime as an opaque 502 with no indication of where it went
wrong, which is why the test exists.

## E. 429 — rate limited

5 reports per minute per IP, configured in `relay/wrangler.toml`. Working as designed.

The limiter **fails open**: if the binding is unavailable the report still goes through. A
junk issue is deletable; a report a user could not file is gone. If you see junk issues appear
in bursts, check whether the limiter is erroring — `console.error('rate limiter unavailable…')`
shows in the Worker log.

## F. Issues attributed to the maintainer

The relay always authors as `app/quickmail-bug-reporter`. An issue authored by **kellylford**
came from a build that predates the relay and is still using the embedded PAT.

That is expected until users update, and it is why `BUG_REPORT_TOKEN` must stay valid for now.
Nothing to fix — but it does tell you which build the reporter is running.

### Retiring the old token

Once a release carrying the relay has been out long enough that un-updated installs no longer
matter:

1. Revoke the PAT at <https://github.com/settings/tokens>.
2. Delete the `BUG_REPORT_TOKEN` repository secret.

After that, old builds fall back to the clipboard path rather than filing silently as you.

## G. Changing a secret

Repository secrets are not live at Cloudflare until they are pushed there.

**Actions** → **Deploy bug-report relay** → **Run workflow** → tick **Also push the Worker's
secrets from repository secrets**.

The box is off by default on purpose: a routine code redeploy whose repository secrets were
missing would otherwise overwrite working Worker secrets with empty strings and break bug
reporting silently. The sync step fails loudly on an empty secret instead of writing it.

Code-only changes under `relay/` redeploy automatically on merge to `main`, without touching
secrets.

```bash
gh workflow run deploy-relay.yml --ref main -f sync_secrets=true
gh run list --workflow=deploy-relay.yml --limit 3
```

---

## Things that look broken but are not

- **Local builds never reach the relay.** No credentials are compiled in. Expected.
- **`npm install` in `relay/` fails on this machine.** Windows ARM64; `workerd` has no arm64
  build. It is not needed — `relay/package.json` deliberately declares no dependencies, and
  the tests use only the Node standard library. Deploy runs in CI. See `relay/README.md`.
- **`gh workflow run deploy-relay.yml` returns 404 from a feature branch.** GitHub only
  registers `workflow_dispatch` from the default branch. Merge first.
- **The relay key is extractable from the shipped binary.** By design. It can only file an
  issue on one repo, is rate-limited, and rotates without touching any GitHub account. This is
  the whole point of the relay — see #222 and #501.

---

## History

- **#222** — reports were filed under the maintainer's personal account.
- **#501 / PR #502** — replaced the embedded PAT with the GitHub App + relay. Live and verified
  2026-08-06; test issue #503 was authored by `app/quickmail-bug-reporter`.
- `docs/BUG-REPORT-BOT-ACCOUNT.md` describes a machine-account approach that was **not**
  adopted: it fixes attribution but leaves the credential in the binary.

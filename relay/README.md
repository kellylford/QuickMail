# QuickMail bug-report relay

A single Cloudflare Worker that turns a bug report from the app into a GitHub issue.

QuickMail used to POST straight to `api.github.com` with a personal access token compiled
into the shipped executable. That token was extractable from any downloaded copy, and it
made every user-filed issue look like the maintainer had filed it. This relay holds a GitHub
App private key instead, so the only secret still shipping in the binary is a relay key that
can do nothing but file an issue on one repo. See issue #501.

Nothing runs until a report arrives. There is no server to patch, monitor, or restart.

## Deployment runs in CI, not from a maintainer's machine

**Do not try to install wrangler locally on Windows ARM64.** It will not work — wrangler
imports `workerd`, which publishes no `win32-arm64` build and throws `Unsupported platform:
win32 arm64 LE` on startup. That applies to `wrangler deploy` as much as to `wrangler dev`,
and `npm install --ignore-scripts` does not get around it, because the failure is at import
time rather than install time.

The **Deploy bug-report relay** workflow
(`.github/workflows/deploy-relay.yml`) runs on x64 Linux instead. Every value the Worker
needs is a repository secret; the workflow pushes both the code and the secrets. You never
run wrangler.

The tests in `test/` deliberately use only the Node standard library, so they stay runnable
here:

```bash
node relay/test/jwt.test.js
```

## One-time setup

### 1. Create the GitHub App

At <https://github.com/settings/apps/new>:

- **Name**: `QuickMail Bug Reporter` (this becomes the issue author, shown as
  `quickmail-bug-reporter[bot]`)
- **Homepage URL**: `https://github.com/kellylford/QuickMail`
- **Webhook**: uncheck **Active**. The relay does not receive webhooks.
- **Repository permissions** → **Issues**: **Read and write**. Leave every other permission
  at *No access*.
- **Where can this App be installed?**: *Only on this account*

Create it, then from the App's settings page:

- Note the **App ID** near the top.
- **Generate a private key** at the bottom. A `.pem` file downloads — this is the only copy,
  GitHub does not show it again.
- **Install App** in the left sidebar → install on your account → **Only select
  repositories** → `QuickMail`.
- After installing, the browser lands on a URL ending in a number, e.g.
  `.../installations/12345678`. That number is the **installation ID**. To find it later:
  <https://github.com/settings/installations> → **Configure** beside the App → read it off
  the URL.

### 2. Create a Cloudflare API token

A Cloudflare account is needed, but nothing has to be configured inside it — no domain, no
nameservers, no subdomain chosen up front. Signing in with GitHub is fine.

At <https://dash.cloudflare.com/profile/api-tokens> → **Create Token**. Use **Create Custom
Token** → **Get started**, which is at the *top* of that page, above the templates. Do not
use the **Edit Cloudflare Workers** template: it grants a spread of Workers-adjacent
permissions and a zone-scoped one, and there is no zone here to scope it to.

- **Token name**: `QuickMail relay deploy`
- **Permissions**, two rows:
  - `Account` / `Workers Scripts` / **Edit** — uploads the Worker and sets its secrets
  - `Account` / `Account Settings` / **Read** — lets wrangler resolve which account to deploy to
- **Account Resources**: `Include` / your account
- **Zone Resources**: leave empty. Nothing here touches DNS or zones.
- **Client IP Address Filtering**: leave empty — GitHub runner IPs change constantly.
- **TTL**: leave empty. An expiring token here fails silently months later, in a workflow
  nobody is watching.

**Continue to summary** → confirm it reads `Workers Scripts:Edit, Account Settings:Read` →
**Create Token**.

Copy the token. Like the private key, it is shown once.

### 3. Set the repository secrets

At <https://github.com/kellylford/QuickMail/settings/secrets/actions>:

| Secret | Value |
|---|---|
| `CLOUDFLARE_API_TOKEN` | the token from step 2 |
| `RELAY_GITHUB_APP_ID` | the App ID from step 1 |
| `RELAY_GITHUB_INSTALLATION_ID` | the installation ID from step 1 |
| `RELAY_GITHUB_PRIVATE_KEY` | the entire `.pem` file, `-----BEGIN`/`-----END` lines included |
| `BUG_REPORT_RELAY_KEY` | a long random string you generate (below) |

The `RELAY_` prefixes are not decoration: GitHub rejects repository secrets whose names begin
with `GITHUB_`.

Either PEM format works — the Worker converts PKCS#1 to PKCS#8 itself, so paste whichever
GitHub gave you.

To generate the relay key:

```bash
node -e "console.log(require('crypto').randomBytes(32).toString('base64url'))"
```

Keep that value: step 5 needs it again, and it is not readable back out of GitHub.

### 4. Deploy

The workflow must be on `main` before it can be run — GitHub only registers
`workflow_dispatch` from the default branch, and dispatching it from a feature branch fails
with a 404 that reads as if the file does not exist.

**Actions** → **Deploy bug-report relay** → **Run workflow**, and tick **Also push the
Worker's secrets from repository secrets**. That box is off by default so a routine code
redeploy cannot blank the Worker's secrets; it needs to be on for this first run and any time
a secret changes.

The **Deploy Worker** step's log ends with the Worker URL, of the form
`https://quickmail-bug-relay.<your-subdomain>.workers.dev`.

If that step fails saying more than one account is available, add a repository **variable**
`CLOUDFLARE_ACCOUNT_ID` (Variables tab, same settings page) with your account ID — it is in
the dashboard URL after `dash.cloudflare.com/`. Otherwise it is not needed.

### 5. Point the app at the relay

Still at <https://github.com/kellylford/QuickMail/settings/variables/actions>, add a
repository **variable** (not a secret):

| Variable | Value |
|---|---|
| `BUG_REPORT_RELAY_URL` | the Worker URL from step 4, plus `/report` |

CI compiles that URL and `BUG_REPORT_RELAY_KEY` into release builds. The next release picks
them up; nothing further is needed per build.

### 6. Check it works

```bash
curl -X POST https://quickmail-bug-relay.<your-subdomain>.workers.dev/report -H "X-QuickMail-Key: <relay key>" -H "Content-Type: application/json" -d "{\"title\":\"Relay smoke test\",\"body\":\"Ignore and close.\"}"
```

Success returns `{"issueUrl":"...","number":N}`, and the issue is authored by
`quickmail-bug-reporter[bot]` rather than by you — that authorship is the whole point, so it
is worth confirming. Close the issue afterwards.

Failures worth recognising:

| Response | Cause |
|---|---|
| `401 Unauthorized` | the `X-QuickMail-Key` header does not match `RELAY_KEY` at Cloudflare |
| `502 Could not create the issue` | App credentials wrong, or the App is not installed on the repo |
| `429` | the per-IP rate limit; wait a minute |

For the underlying GitHub error behind a 502, check the Worker's logs: **Cloudflare
dashboard** → **Workers & Pages** → `quickmail-bug-relay` → **Logs**.

## Retiring the old token

Installs that have not updated yet still post to `api.github.com` with the old PAT, so
revoking it early breaks bug reporting for exactly the users least likely to notice why.
Ship the release first, give it a few weeks, then revoke the PAT at
<https://github.com/settings/tokens> and delete the `BUG_REPORT_TOKEN` repository secret.

## If the relay key leaks

It will eventually — it ships in a public binary, and that is the design assumption, not a
failure. The blast radius is junk issues on one repo. To cut it off: update the
`BUG_REPORT_RELAY_KEY` repository secret, re-run the deploy workflow **with the secrets box
ticked**, and ship a build. Older installs fall back to the pre-filled issue URL and
clipboard path, which needs no relay at all.

Nothing about a leaked relay key touches your GitHub account, the App's private key, or any
other repository.

## Operating notes

- **Cost.** The free tier is 100,000 requests/day. Expected volume is a handful per week.
- **Rate limiting** is 5 reports per minute per IP, and fails *open* — if the limiter binding
  is ever unavailable the report still goes through. A junk issue is deletable; a report a
  user could not file is gone.
- **The relay never sees mail content.** It forwards the same text the report window already
  shows the user before sending — no log file, no message bodies, no addresses beyond an
  optional contact line the user types.
- **Changing the Worker's code** needs no ceremony: merge to `main` with changes under
  `relay/`, and the workflow redeploys automatically without touching secrets.

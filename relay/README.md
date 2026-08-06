# QuickMail bug-report relay

A single Cloudflare Worker that turns a bug report from the app into a GitHub issue.

QuickMail used to POST straight to `api.github.com` with a personal access token compiled
into the shipped executable. That token was extractable from any downloaded copy, and it
made every user-filed issue look like the maintainer had filed it. This relay holds a GitHub
App private key instead, so the only secret still shipping in the binary is a relay key that
can do nothing but file an issue on one repo. See issue #501.

Nothing runs until a report arrives. There is no server to patch, monitor, or restart.

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
  App settings → **Install App** → the gear icon beside your account.

### 2. Deploy the Worker

From this folder:

```bash
npm install
```

```bash
npx wrangler login
```

```bash
npx wrangler deploy
```

The deploy prints the Worker URL, something like
`https://quickmail-bug-relay.<your-subdomain>.workers.dev`. The app posts to that URL plus
`/report`.

### 3. Set the four secrets

Each command prompts for the value and stores it encrypted at Cloudflare. Values are never
echoed back and never appear in the repo.

```bash
npx wrangler secret put GITHUB_APP_ID
```

```bash
npx wrangler secret put GITHUB_INSTALLATION_ID
```

```bash
npx wrangler secret put GITHUB_PRIVATE_KEY
```

For the private key, paste the entire `.pem` file including the `-----BEGIN`/`-----END`
lines. Either format GitHub gives you works — the Worker converts PKCS#1 to PKCS#8 itself.

```bash
npx wrangler secret put RELAY_KEY
```

Generate that one first — any long random string. For example:

```bash
node -e "console.log(require('crypto').randomBytes(32).toString('base64url'))"
```

### 4. Give the same relay key to the build

Add the value as a repository secret named `BUG_REPORT_RELAY_KEY` at
<https://github.com/kellylford/QuickMail/settings/secrets/actions>, and set the Worker URL
as a repository **variable** named `BUG_REPORT_RELAY_URL` (Variables tab, same page). CI
compiles both into the release build.

The old `BUG_REPORT_TOKEN` secret can be deleted once a release carrying the new build has
shipped — see *Retiring the old token* below.

## Checking it works

```bash
curl -X POST https://quickmail-bug-relay.<your-subdomain>.workers.dev/report -H "X-QuickMail-Key: <relay key>" -H "Content-Type: application/json" -d "{\"title\":\"Relay smoke test\",\"body\":\"Ignore and close.\"}"
```

A success returns `{"issueUrl":"...","number":N}`. Close the issue afterwards.

Live logs, while a report is being filed:

```bash
npx wrangler tail
```

## Retiring the old token

Installs that have not updated yet still post to `api.github.com` with the old PAT, so
revoking it early breaks bug reporting for exactly the users least likely to notice why.
Ship the release first, give it a few weeks, then revoke the PAT at
<https://github.com/settings/tokens> and delete the `BUG_REPORT_TOKEN` repository secret.

## If the relay key leaks

It will eventually — it ships in a public binary, and that is the design assumption, not a
failure. The blast radius is junk issues on one repo. To cut it off: set a new `RELAY_KEY`
secret, update `BUG_REPORT_RELAY_KEY` in the repo, and ship a build. Older installs fall
back to the pre-filled issue URL and clipboard path, which needs no relay at all.

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

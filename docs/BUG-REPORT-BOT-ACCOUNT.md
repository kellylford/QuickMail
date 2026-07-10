# Bug-Report Bot Account Setup

**Purpose:** QuickMail's in-app "Report a Bug" feature submits directly to the GitHub Issues
API using a token baked into the release build, so users never have to sign into GitHub or
reveal an email address. GitHub attributes each created issue to **whoever owns that token.**

This document explains how to provision a **dedicated bot account** to own that token, so that
in-app reports are authored by an anonymous machine account (e.g. `quickmail-bot`) instead of a
real person's account. It fixes [#222](https://github.com/kellylford/QuickMail/issues/222).

> **Do not use a personal account's token for this.** That is the exact defect #222 describes:
> every user report ends up looking like the maintainer authored it, and an extracted token acts
> as a real person's identity instead of an isolated throwaway.

This doc is written to be followed either by the maintainer by hand, or by an AI agent that has
been asked to do the setup. Steps that **must** be done by a human in a browser (GitHub does not
allow them via API/CLI) are marked **[HUMAN-ONLY]**.

---

## What you are creating, and why each piece matters

| Piece | What it is | Why it must be this way |
|---|---|---|
| A second GitHub account | A "machine account" whose only job is filing bug issues | GitHub attributes issues to the token owner. A dedicated account means reports are authored by `quickmail-bot`, not by you. |
| Triage collaborator role on this repo | The bot is added to `kellylford/QuickMail` as a collaborator with **Triage** | On a public repo *anyone* can open an issue, but **applying labels requires Triage or Write.** Without it, the `bug` / `user-reported` labels the app sends are silently dropped. Triage is the least privilege that keeps labeling working — it grants **no code write access.** |
| Fine-grained PAT from the bot | A token scoped to this repo only, `Issues: Read and write` only | The token ships inside the distributed `.exe` and is extractable. Minimal scope on a throwaway account means the worst case of extraction is spam issues on this one repo — never a compromise of a real account or of code. |

**What this deliberately does *not* do:** it does not collect the reporter's name or email, and it
does not require reporters to have a GitHub account. Those properties are intentional and must be
preserved. This change only alters *which account owns the submitting token.*

---

## Prerequisites

- Admin access to `kellylford/QuickMail` (you have this).
- A separate email address for the bot account. A `+` alias on your existing email works
  (e.g. `you+quickmailbot@example.com`) — GitHub treats it as a distinct address. Some password
  managers also let you generate a unique alias.
- An authenticator app for the bot account's 2FA (GitHub now requires 2FA on accounts that
  contribute; set it up at creation time rather than being locked out later).

---

## Step 1 — Create the bot account **[HUMAN-ONLY]**

1. Sign **out** of your personal GitHub account (or use a separate browser profile / incognito
   window — GitHub does not let you create a second account while signed in, and you want to avoid
   accidentally acting as your personal account during setup).
2. Go to <https://github.com/signup> and create the account:
   - **Username:** `quickmail-bot` (or `quickmail-reports` if taken). Pick something that reads as
     obviously-a-bot so issue authorship is self-explanatory.
   - **Email:** the dedicated/alias address above.
3. Verify the email.
4. **Turn on two-factor authentication immediately:** Settings → Password and authentication →
   Two-factor authentication → set up with an authenticator app. **Save the recovery codes**
   somewhere durable (your password manager). If you lose access to this account, in-app bug
   reporting breaks until you re-provision.
5. **Set the profile so its purpose is unmistakable.** In Settings → Public profile:
   - Name: `QuickMail Bug Bot`
   - Bio: `Automated account. Files bug reports submitted through the QuickMail app. Not a person.`
   - This matters because these issues are public; anyone reading them should immediately understand
     the account is automated.

> **Policy note:** GitHub's Terms of Service explicitly permit "machine accounts" — one per human is
> fine. A bot account tied to and controlled by you (the maintainer) for this purpose is within the
> rules. Do not create *multiple* accounts to evade anything; that is what the ToS prohibits.

---

## Step 2 — Add the bot as a Triage collaborator **[HUMAN-ONLY]**

Do this from your **personal** account (the repo admin), not the bot.

1. Go to <https://github.com/kellylford/QuickMail/settings/access>.
2. **Add people** → enter `quickmail-bot` → select it.
3. Choose the **Triage** role (not Write, not Admin). Send the invite.
4. Sign in as the bot account (separate browser profile) and **accept the invitation**
   (check the bot's email, or <https://github.com/kellylford/QuickMail/invitations>).

**Why Triage specifically:** the app sends `labels: ["bug", "user-reported"]` on each issue
(`QuickMail/Services/BugReportService.cs`). GitHub only honors those labels if the submitting
account can triage the repo. A plain non-collaborator could still *create* the issue on this public
repo, but the labels would be silently ignored — reports would land unlabeled. Triage fixes that
without granting any ability to push code.

---

## Step 3 — Generate the fine-grained PAT from the bot **[HUMAN-ONLY]**

Signed in **as the bot account**:

1. Go to Settings → Developer settings → **Fine-grained tokens** → **Generate new token**
   (<https://github.com/settings/personal-access-tokens/new>).
2. Configure it exactly:
   - **Token name:** `quickmail-inapp-bugreports`
   - **Expiration:** 1 year (the maximum). Note the date — see Step 6 on rotation. (Avoid
     "No expiration": a non-expiring secret shipped in a binary is worse if leaked.)
   - **Resource owner:** `kellylford`. If `kellylford` does not appear as a selectable owner, the
     collaborator invitation from Step 2 has not been accepted yet — finish that first.
   - **Repository access:** **Only select repositories** → `kellylford/QuickMail`. Nothing else.
   - **Permissions → Repository permissions → Issues:** **Read and write.**
   - Leave **every other permission at "No access."** Do not grant Contents, Administration,
     Pull requests, Metadata-beyond-default, or anything else.
3. Generate, then **copy the token now** (`github_pat_...`) — GitHub shows it only once. Store it in
   your password manager immediately.

> **Minimal scope is a named security principle for this feature** (see the spec, §3.5). If GitHub
> ever indicates `Issues: Read and write` on this one repo is insufficient to create a labeled
> issue, **stop and confirm before widening scope** — do not reflexively add permissions.

---

## Step 4 — Put the token into the build

The build reads the token from a **gitignored** partial-class file, per
`docs/BugReportService.Credentials.example`:

1. Copy the example to the real (gitignored) file if it does not already exist:

   ```
   cp docs/BugReportService.Credentials.example QuickMail/Services/BugReportService.Credentials.cs
   ```

2. Edit `QuickMail/Services/BugReportService.Credentials.cs` and set the bot's token:

   ```csharp
   namespace QuickMail.Services;

   public partial class BugReportService
   {
       private const string AppOwnedToken = "github_pat_...";   // bot account token from Step 3
   }
   ```

3. Confirm it is **not** tracked by git (it must never be committed):

   ```
   git check-ignore QuickMail/Services/BugReportService.Credentials.cs
   ```

   This should print the path (meaning it is ignored). If it prints nothing, **stop** — the
   `.gitignore` entry is missing and the token would be committed. Fix `.gitignore` before going on.

> On release CI the token is injected the same way (into `AppOwnedToken`) rather than being checked
> in. Wherever you store CI secrets, replace the old personal-account token value with this bot
> token. **Then revoke the old personal-account PAT** (Step 5).

---

## Step 5 — Revoke the old personal-account token **[HUMAN-ONLY]**

Once a build with the bot token is verified working (Step 7), retire the old one so nothing keeps
authoring issues as you:

1. Sign in as your **personal** account.
2. Settings → Developer settings → Fine-grained tokens → find the old QuickMail bug-report token →
   **Revoke.**

Any binary still carrying the old token will simply fall back to the browser/clipboard path once
it's revoked — it fails safe, it does not crash.

---

## Step 6 — Record the rotation reminder

Fine-grained PATs expire. When this one lapses, in-app submission starts failing and silently falls
back to the browser path until a new build ships. To avoid a surprise:

- Add a calendar reminder ~11 months out (a few weeks before the Step 3 expiration date) to
  generate a fresh token from the bot account and ship a build with it.
- Rotation is just Step 3 → Step 4 again (generate new, swap into the build). No account or
  collaborator changes needed.

---

## Step 7 — Verify

1. Build QuickMail with the bot token embedded (`build.bat` / your normal release build).
2. Run it, open **Report a Bug**, and submit a throwaway test report.
3. Confirm on GitHub that the new issue:
   - is **authored by `quickmail-bot`**, not by your personal account, **and**
   - carries the **`bug`** and **`user-reported`** labels (this proves the Triage role is working).
4. Close the test issue.

If the author is correct but the labels are missing, the Triage collaborator step (Step 2) was not
completed or accepted — revisit it. If submission fails entirely and falls back to the browser, the
token is missing, mis-scoped, or the resource owner wasn't `kellylford` — revisit Step 3.

---

## Quick reference for an AI agent doing this

Most steps here are **[HUMAN-ONLY]** (account creation, 2FA, PAT generation, collaborator invite/
accept, token revocation) because GitHub gates them behind an interactive, signed-in browser
session and does not expose them to API/CLI. An agent's role is limited to:

- **Step 4:** creating/editing the gitignored `BugReportService.Credentials.cs` with a token the
  human supplies, and verifying `git check-ignore` reports it as ignored. **Never commit this file
  or echo the token into any tracked file, log, or commit message.**
- Prompting the human, in order, through the HUMAN-ONLY steps and confirming each is done before
  moving on (especially: collaborator invite **accepted** before PAT generation, since the
  `kellylford` resource owner won't appear otherwise).
- Running the Step 7 verification checks against the GitHub API (issue author == `quickmail-bot`,
  labels present) once the human reports a build exists.

Do not attempt to script account creation or PAT generation; there is no supported non-interactive
path, and trying to automate around GitHub's auth is out of scope and against the spirit of the ToS.

---

## Related

- [#222](https://github.com/kellylford/QuickMail/issues/222) — the issue this resolves
- `docs/planning/bug-reporting-pm-dev-spec.md` — original feature spec (Decision A, §3.5 minimal scope)
- `docs/BugReportService.Credentials.example` — the token file template
- `QuickMail/Services/BugReportService.cs` — the submission code (unchanged by this setup)

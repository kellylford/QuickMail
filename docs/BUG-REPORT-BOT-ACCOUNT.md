# Bug-Report Bot Account Setup — superseded

**This approach was never adopted. Do not follow it.** Setup for the mechanism actually in
use is in [`relay/README.md`](../relay/README.md).

This document described provisioning a dedicated GitHub machine account (`quickmail-bot`) to
own the fine-grained token that QuickMail's "Report a Bug" feature used to submit issues
with. It was written to fix
[#222](https://github.com/kellylford/QuickMail/issues/222) — user-filed reports appearing
under the maintainer's personal account.

## Why it was replaced

A machine account fixes attribution and nothing else. The token still had to be compiled
into the shipped single-file executable, so it was still extractable by anyone who
downloaded QuickMail; all that changed was which account the extracted credential belonged
to. The recurring cost — a second GitHub account with its own 2FA, recovery path, and
collaborator grant on this repository — bought only the rename.

[#501](https://github.com/kellylford/QuickMail/issues/501) removed the credential from the
binary instead. A GitHub App's private key lives in a small Cloudflare Worker; the app posts
reports there with a relay key whose entire authority is "file an issue on
kellylford/QuickMail", rate-limited at the relay and rotatable without touching any GitHub
account. Issues are authored by the App's bot identity, so the attribution problem #222
raised is fixed as a side effect rather than as the goal.

## What carried over

Two properties of the original design are load-bearing and were deliberately preserved:

- Reporters do **not** need a GitHub account. This is why the pre-filled-issue-URL path
  could not simply become the only path.
- The report collects no name or email. The relay accepts an optional contact field, but the
  app does not currently offer one — that remains an open product decision on #501, because
  anything sent this way lands in a public issue.

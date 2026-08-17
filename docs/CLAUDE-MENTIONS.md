# Mentioning @claude in QuickMail Issues and Pull Requests

This guide covers the `@claude` mention integration: what has to be in place before it
works, who is allowed to trigger it, where a mention is recognized, and what Claude can
and cannot do once mentioned.

> **Status: not yet set up.** As of this writing the repository has no Claude GitHub App
> installation, no authentication secret, and no mention workflow. The
> [Setup](#setup) section is the work required to turn it on.

## What this is

`@claude` mentions are powered by
[`anthropics/claude-code-action`](https://github.com/anthropics/claude-code-action), a
GitHub Action that runs Claude Code inside a workflow on GitHub's runners. Write a comment
containing `@claude` on an issue or pull request, and Claude reads the repository, works on
the request, and replies in a comment.

Nothing runs on a maintainer's machine, and no session has to be open. The mention itself is
the trigger.

This is distinct from two other things that share the name:

- **`/code-review` in a local Claude Code session** — what maintainers run by hand today.
- **Code Review** (the hosted product) — automatic review on every pull request with no
  workflow file to maintain. Complementary; it reviews, it does not converse.

## Setup

Three pieces are required. All three are missing today.

### 1. The Claude GitHub App

Install [the Claude GitHub App](https://github.com/apps/claude) on the repository. The
mention integration uses three of its permissions:

| Permission    | Access         | Why |
| ------------- | -------------- | --- |
| Contents      | Read and write | Read the repository, push commits |
| Issues        | Read and write | Reply on issues |
| Pull requests | Read and write | Reply on pull requests, push to PR branches |

Installing grants the app's full permission set, not just these three — GitHub does not
offer partial acceptance. The full set is listed in the
[GitHub Actions documentation](https://code.claude.com/docs/en/github-actions#github-app-permissions).

Notably **absent in practice**: the app has no workflow write access, by design. Claude
cannot edit anything under `.github/workflows/` when mentioned.

### 2. An authentication secret

One of these, as a repository secret:

- **`ANTHROPIC_API_KEY`** — an API key from the Claude Console. Usage bills to the API
  account.
- **`CLAUDE_CODE_OAUTH_TOKEN`** — a long-lived token tied to a Claude subscription,
  generated with `claude setup-token`. Usage draws on the subscription instead of API
  billing.

Pick one. The workflow references whichever you chose by its matching input name
(`anthropic_api_key` or `claude_code_oauth_token`).

### 3. A workflow file

The mention integration runs in **interactive mode**, which the action selects by noticing
that the workflow supplies **no `prompt` input**. Supply a `prompt` and it switches to
automation mode and stops waiting for mentions.

```yaml
name: Claude Mention
on:
  issue_comment:
    types: [created]
  pull_request_review_comment:
    types: [created]
jobs:
  claude:
    if: contains(github.event.comment.body, '@claude')
    runs-on: windows-latest
    permissions:
      contents: write
      pull-requests: write
      issues: write
      id-token: write
      actions: read
    steps:
      - uses: actions/checkout@v6
        with:
          fetch-depth: 1
      - uses: anthropics/claude-code-action@v1
        with:
          anthropic_api_key: ${{ secrets.ANTHROPIC_API_KEY }}
```

Why each non-boilerplate line is there:

- `id-token: write` — required for the action's default GitHub App authentication.
- `actions: read` — lets Claude read CI results and job logs on the pull request.
- `actions/checkout` — gives Claude a working copy to read and build.
- `if: contains(...)` — stops a runner from spinning up on every unrelated comment. The
  action re-checks the trigger phrase itself.
- **`runs-on: windows-latest`** — QuickMail-specific and important. Both CI jobs (`build`,
  `integration`) run on Windows because this is a WPF/.NET 8 desktop app. On
  `ubuntu-latest` Claude could read the code but could not build it or run the test suite,
  so half of what you would want from a mention would not work.

### Fastest path

From an interactive `claude` terminal in the repository, with `gh auth login` already done
and admin access on the repository:

```bash
claude
```

Then run `/install-github-app`. It installs the app, stores the secret, pushes a branch with
the workflow files, and opens a pull request ready to create. Merge that pull request and
mentions are live.

Adjust `runs-on` to `windows-latest` in the generated workflow before merging — the
generated default is Ubuntu.

## Who is allowed to mention Claude

The action runs two checks on whoever triggered it, and **fails the run** if either
rejects:

1. **Write access.** The triggering user must have write access to the repository.

   | Person | Access | Can mention? |
   | ------ | ------ | ------------ |
   | `kellylford` | admin | Yes |
   | `CityDweller` | push | Yes |
   | Anyone else (issue reporters, drive-by commenters) | none | No |

   To allow specific users without write access, set `allowed_non_write_users` and pass your
   own `github_token` input.

2. **Human actor.** Bot accounts are rejected unless listed in `allowed_bots`. This exists to
   keep bots from triggering Claude in a loop.

This matters for bug reports: a user who files an issue through the in-app bug reporter
cannot summon Claude on their own issue. A maintainer has to do it.

## Where a mention is recognized

- The **body or title of a newly opened issue**
- An **issue comment**
- A **pull request comment**
- A **pull request review comment** (inline, attached to a specific line of code)
- A **pull request review** body

Not recognized: commit comments, repository discussions, and edits to an existing issue or
pull request body. Only a *newly opened* issue's body counts — editing one in later to add
`@claude` does nothing.

The mention must be `@claude` as a complete word. `/claude` and `@claude-bot` do not
trigger.

## What Claude can do when mentioned

**Read and reason**

- Read the entire repository at the triggering ref, including `CLAUDE.md` — so every
  house rule in it (MVVM boundaries, the accessibility checklist, announcement categories,
  modal dialog rules, keyboard registration requirements) is already in context on every
  run, with no prompting.
- Read the pull request diff.
- Read CI results: workflow runs, job logs, and test output on the pull request. It can tell
  you why `integration` went red.
- Build and run tests on the runner, given a Windows runner.

**Write**

- Reply in a comment on the triggering issue or pull request, updating that comment in place
  as it works so you can watch progress.
- Make code changes and **push commits to the branch it was invoked on**.

## What Claude cannot do when mentioned

These are enforced, not conventions:

| Cannot | Detail |
| ------ | ------ |
| Merge | "It does not merge branches, rebase, force push, or perform other destructive git operations." |
| Rebase or force push | Blocked in the system prompt even where tooling would permit it. |
| Push to any other branch | Only the branch it was invoked on. |
| Create a pull request | Not by default. It pushes commits and hands back a link to a pre-filled PR submission page. |
| Edit `.github/workflows/**` | The GitHub App has no workflow write access, for security. |
| Post many inline comments | It maintains a **single** updating comment rather than scattering comments through the thread. |
| Approve or request changes | Submitting a formal review is not a documented capability of the mention path. |

That last two rows are why a *review* of a pull request is a separate job from a
*conversation* about one. Inline per-finding comments come from the review path
(the `code-review` skill invoked with `--comment`), not from a mention.

## Things worth knowing about Claude's pushes

- **CI does run on them.** GitHub suppresses workflow triggers for commits made with the
  default `GITHUB_TOKEN`. Because the action authenticates as the Claude GitHub App instead,
  its pushes trigger `build` and `integration` normally. Do not pass
  `github_token: ${{ secrets.GITHUB_TOKEN }}` to the action — that is what breaks it.
- **A push dismisses approvals.** `main` protection sets `dismiss_stale_reviews: true`, so
  any commit Claude pushes to a pull request invalidates existing approving reviews and the
  pull request needs re-approval.
- **Conversations must be resolved.** `required_conversation_resolution: true` on `main`
  means an unresolved thread — including one Claude started — blocks merge.

## Example mentions

Questions:

```text
@claude why would this change break the F6 focus ring?
@claude this PR adds a type bound to a ComboBox — is it registered in SelectorItemAccessibilityTests?
@claude the integration job is red. What broke, and is it this PR's fault?
@claude does this new dialog follow the modeless rule for windows hosting a WebView2?
```

Changes:

```text
@claude the InputGestureText on the new menu item does not match the registered default key. Fix it.
@claude add the new Selector-bound record to SelectorItemAccessibilityTests and override ToString().
@claude this command is not registered in CommandRegistry. Register it under the Mail category.
```

Claude replies in one comment, edits that comment as it works, and — for the change
requests — pushes commits to the pull request branch.

## Controlling cost

Each mention consumes GitHub Actions minutes and model tokens. Levers:

- `--max-turns N` in `claude_args` caps iterations per run.
- A job-level `timeout-minutes` prevents a runaway run.
- GitHub `concurrency` controls limit parallel runs.
- Specific requests finish in fewer turns than vague ones.

Windows runners consume Actions minutes at a higher multiplier than Linux on private
repositories. On a public repository, standard runners are free.

## Turning it off

Delete the mention workflow from `.github/workflows/`. Mentions stop immediately; the app
and secret can stay for other Claude features.

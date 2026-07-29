# How to Get GitHub Release Download Numbers — and What They Actually Mean

GitHub counts every download of every file you attach to a release. It shows you
none of it in the web interface, and it barely documents that the data exists.

This is a practical guide to getting those numbers out, and — the part almost
nobody writes about — how to read them without fooling yourself.

---

## First: what GitHub officially publishes

Almost nothing.

There used to be a dedicated help article, "Getting the download count for your
releases." It now exists only in the archived Enterprise Server 2.16 docs, a
version [discontinued on 2020-01-22](https://docs.github.com/en/enterprise/2.16/user/github/administering-a-repository/getting-the-download-count-for-your-releases).
It has not been carried forward into the current documentation.

What remains today is:

- One sentence in [About releases](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases):
  "You can also use the Releases API to gather information, such as the number of
  times people download a release asset." No instructions follow.
- In the [REST API reference for release assets](https://docs.github.com/en/rest/releases/assets),
  the field is listed as `download_count`: required, integer. That is the entire
  description. Nothing about what it counts, how it is calculated, or whether
  history is available.

So the data is real and supported, but you are on your own for both the how and
the interpretation.

---

## Getting the numbers

### The zero-effort way: a browser

Public repositories need no authentication. Open this and read the JSON:

```
https://api.github.com/repos/OWNER/REPO/releases
```

Each release has an `assets` array, and each asset has `name` and
`download_count`. Fine for a quick look, unwieldy past one release.

### The practical way: the `gh` CLI

If you have the [GitHub CLI](https://cli.github.com/) installed and
authenticated, this prints every release with every asset and its count:

```bash
gh api repos/OWNER/REPO/releases --paginate -q '.[] | "\(.tag_name)\t" + ([.assets[] | "\(.name)=\(.download_count)"] | join("  "))'
```

Just the latest release:

```bash
gh api repos/OWNER/REPO/releases/latest -q '.assets[] | "\(.download_count)\t\(.name)"'
```

A lifetime total across every release:

```bash
gh api repos/OWNER/REPO/releases --paginate -q '[.[].assets[].download_count] | add'
```

`--paginate` matters. Without it you get the first 30 releases and a quietly
incomplete total.

### With `curl` instead

If you do not have `gh`, `curl` fetches exactly the same JSON:

```bash
curl -s https://api.github.com/repos/OWNER/REPO/releases
```

Add `-H "Authorization: Bearer $TOKEN"` for private repositories, or to lift the
unauthenticated rate limit (60 requests per hour, versus 5,000 authenticated).

That hands you the raw JSON to read or feed into whatever you already use. The
`gh` examples above are easier where you have it, because `-q` does the
filtering for you and returns finished lines rather than a wall of JSON.

### With a GUI

If you would rather read this in a window than a terminal,
[GHManage](https://github.com/kellylford/GHManage) is a keyboard-driven,
screen-reader-friendly desktop app for browsing GitHub repositories. Its
Releases view shows a downloads column per release and a repo-wide total in the
status bar; pressing Enter on a release drills into its individual files, ordered
most-downloaded first, and the details panel breaks a release down file by file
without leaving the list.

Download it from the [v0.2.1 release](https://github.com/kellylford/GHManage/releases/tag/v0.2.1)
(a standalone `.exe`, no install required — it needs the `gh` CLI authenticated),
or read the source at [github.com/kellylford/GHManage](https://github.com/kellylford/GHManage).
Every number in the case study below was read out of it.

---

## Now the part that matters: reading them honestly

Getting the number is five minutes. Not misreading it is the whole job.

### A download is an HTTP request, not a person

`download_count` increments when someone fetches the file. It does not
deduplicate by user, by IP, or by anything else. One person downloading twice
counts twice. A CI job pulling your installer on every build counts every time.
Nothing in the number is a human being.

### It is a lifetime running total, and there is no history

The count is cumulative from the moment the asset was uploaded. There is no
date breakdown, no "last 30 days," and **no API that offers one**. If you want
downloads over time, you have to record the numbers yourself on a schedule and
diff them. Nobody can retroactively give you last month.

A minimal snapshot, run from a scheduled task or cron:

```bash
gh api repos/OWNER/REPO/releases --paginate \
  -q '.[] | .tag_name as $t | .assets[] | [$t, .name, .download_count] | @tsv' \
  | sed "s/^/$(date -u +%Y-%m-%d)\t/" >> downloads-history.tsv
```

PowerShell equivalent:

```powershell
$stamp = (Get-Date -Format 'yyyy-MM-dd')
gh api repos/OWNER/REPO/releases --paginate -q '.[] | .tag_name as $t | .assets[] | [$t, .name, .download_count] | @tsv' |
  ForEach-Object { "$stamp`t$_" } | Add-Content downloads-history.tsv
```

Both end their filter with `@tsv`, which emits the fields tab-separated, rather
than assembling the line by hand. That is deliberate. On Windows, PowerShell
strips the double quotes out of an argument on its way to a native executable,
so a filter written the natural way — `"\($t)\t\(.name)"` — reaches `gh` with
its quotes gone and fails with `unexpected token "\"`. `@tsv` has no quotes to
lose, so the identical command works in both shells.

Start this today even if you have no use for it yet. The data does not exist
until you begin collecting it, and the day you want a trend is always after the
day you should have started.

### Different files are different populations — do not average them

This is the mistake that produces wrong conclusions, and it is easy to make.

If your release contains an installer, an auto-updater package, and a portable
build, those three numbers describe three different groups of people:

- **Installer** — someone acquiring the software. New users, reinstalls, and
  people who update by reinstalling.
- **Updater packages** (`.nupkg`, delta packages, patch bundles) — an **already
  installed copy updating itself**. Only a running install ever fetches these.
- **Portable build** — someone who wants no installer, and who will never
  auto-update.

They add up. They do not overlap, and no single one of them is "the" number.

### Your best retention signal is the updater package

If you ship an auto-updater, its package download count is the most honest
number you have. An installer download tells you someone took a copy. An
**updater** download tells you a copy was still installed and still running when
you shipped the next version. That is retention, measured directly.

Watch it across consecutive releases. The full worked example is below.

Treat the updater figure as a **floor**, not a total. Portable users and people
who reinstall manually never appear in it.

### Uneven counts mean real clients; uniform counts mean scraping

A useful heuristic. If a bot is crawling your release page and grabbing
everything, all your assets show roughly the same count. Real users produce a
jagged profile, because each one fetches exactly the file they need.

Uniformity across every asset is the warning sign. Apply the same suspicion to
spikes: a release that scores five or ten times its neighbours, with nothing in
its content to explain it, is far more likely to be automated traffic than a
sudden burst of humans.

### Look for a number that counts people, not requests

Before you build any conclusion on download counts, check whether your project
already produces a number **tied to an identity** — an OAuth grant, a licence
activation, an account registration, a sync service seeing a login. Anything a
person has to complete a real action to trigger.

Such a number is worth more than your entire release history, because it counts
people instead of HTTP requests. It deduplicates. Bots do not appear in it. If
you have one and are still reasoning from downloads, you are working from your
weakest data. The case study below turns on exactly this — the most informative
figure in it is not a download count at all.

### Things that quietly are not counted

- **Source code archives are invisible.** The auto-generated "Source code (zip)"
  and "(tar.gz)" links on every release are **not** assets, do not appear in the
  `assets` array, and have no `download_count`. Only files you explicitly
  attached are counted.
- **Clones and `git` traffic are not downloads.** Those live in a completely
  separate place — repository Insights → Traffic — which only retains 14 days
  and requires push access.
- **A re-uploaded file starts over.** The count belongs to the asset, not the
  filename. Delete an asset and upload a replacement and you have a new asset
  with a fresh count of zero. The old number is gone.

### Two things that surprise people

- **Draft releases accumulate downloads.** A draft is not public, but its assets
  still record fetches by anyone with repository access. If you see counts on an
  unpublished release, that is your own team and your testers.
- **Forks start at zero.** Download counts are per repository and are not
  inherited. A fork of a popular project shows nothing until it publishes its
  own releases with its own assets.

---

## A worked example, with real numbers

Abstract advice is easy to nod along to and hard to apply, so here is an entire
repository's history with nothing withheld.

[QuickMail](https://github.com/kellylford/QuickMail) is a keyboard and screen
reader friendly email client for Windows. It is a personal project still under
active development, and promotion so far has been light — some Mastodon and
mailing list posts, and a mention in a few newsletters. There has been no
concerted promotional effort, deliberately, while the software is still being
built. These are all of its numbers as of 29 July 2026.

### What its releases contain

Understanding the files is a prerequisite for reading the counts, and this is
true of any project — you cannot interpret a number until you know which
population produces it.

| File | Who fetches it |
|------|----------------|
| `QuickMail-win.msi` / `-setup.exe` | A person installing it. New users, reinstalls, manual updaters. |
| `QuickMail.exe` | A person who wants the portable build and no installer. Never auto-updates. |
| `QuickMail-<v>-delta.nupkg` | An **installed, running** copy patching itself from the previous version. |
| `QuickMail-<v>-full.nupkg` | The same updater, taking the whole package because a delta would not apply. |
| `releases.win.json` | The updater checking whether a new version exists. |
| `RELEASES`, `assets.win.json` | Legacy or unused feed metadata. |

The last three are machine traffic by definition. The updater arrived in v0.8.0
on 8 July 2026; before that there was no auto-update at all, which is why the
nupkg columns are empty earlier in the history.

### The whole history

| Release | Date | Installer | Portable | Delta | Full | Feed |
|---------|------|----------:|---------:|------:|-----:|-----:|
| v0.8.36 | 07-24 | 27 | 11 | **18** | 12 | 110 |
| v0.8.35 | 07-23 | 9 | 9 | **14** | 12 | 172 |
| v0.8.34 | 07-22 | 37 | 15 | **13** | 4 | 256 |
| v0.8.33 | 07-20 | 15 | 8 | **13** | 5 | 331 |
| v0.8.32 | 07-15 | 16 | 13 | **11** | 8 | 476 |
| v0.8.31 | 07-14 | 5 | 2 | **11** | 8 | 516 |
| v0.8.3 | 07-13 | 8 | 5 | **9** | 11 | 565 |
| v0.8.2 | 07-12 | 10 | 8 | **9** | 11 | 640 |
| v0.8.1 | 07-09 | 41 | 29 | 0 | 9 | 712 |
| v0.8.0 | 07-08 | 14 | 12 | 0 | 3 | 744 |
| v0.7.9.1 | 07-03 | 57 | 35 | — | — | — |
| v0.7.4 | 06-17 | **234** | 9 | — | — | — |
| v0.7.2 | 06-12 | **258** | 5 | — | — | — |
| v0.6.6 | 05-28 | 0 | **164** | — | — | — |
| v0.5.1 | 05-12 | 0 | **149** | — | — | — |
| *(29 more)* | | | | | | |
| **Lifetime** | | **1,091** | **771** | **98** | **83** | **4,522** |

Lifetime downloads across every asset of every release: **6,571**. Lifetime
downloads of things a human would deliberately choose — installers and the
portable build — **1,862**.

### Reading it

**1,862 is not a user count, and neither is 6,571.** The larger figure is mostly
the updater talking to itself. The smaller one counts acquisitions over three
months, including bots, repeat downloads, and everyone who tried it once and
deleted it.

**The delta column is the honest one.** Look at it in isolation: 9, 9, 11, 11,
13, 13, 14, 18. Eight consecutive releases, each number earned only by a copy of
QuickMail that was still installed and still running when the next version
shipped. It is not noisy, it does not spike, and it grows slowly. That is the
same group of people showing up again and again.

Compare the installer column over exactly the same releases: 10, 8, 5, 16, 15,
37, 9, 27. Same period, triple the volatility, and measuring something else
entirely — arrival, not retention.

**So how many people actively update QuickMail?** The defensible answer is
**roughly 15 to 20**, with a plausible ceiling around 30 to 35. Hold that number
lightly — a second, better data point further down revises the picture
considerably.

The reasoning, in full:

- **18** installs auto-updated into the most recent release. That is a hard
  floor — each one is a running installation.
- Add the **12** that took the full package instead. Some of those are the same
  kind of user on a machine where the delta failed; some skipped a version.
  Call the updating population **20 to 30**.
- Portable users are **invisible** to this. They never auto-update, so they never
  appear in any nupkg count. Roughly 11 people took the portable build of the
  latest release, but there is no way to know how many still run it.
- Anyone who updates by re-running the installer is invisible too, mixed
  indistinguishably into the installer column with genuinely new users.
- The feed count of 110 is **not** 110 people. It is cumulative update checks.
  QuickMail checks at startup, so one install contributes seven by being opened
  once a day for a week — and contributes exactly the same seven if one person
  quits and relaunches it seven times in a single afternoon. The number rises
  with **how often the program is started**, which is a measure of habit, not of
  headcount.

**Two spikes do not belong.** v0.7.2 at 258 and v0.7.4 at 234 sit among
neighbours in the 30 to 40 range, with nothing in either release to explain a
sevenfold jump. The same shape appears in the portable column at v0.6.6 (164)
and v0.5.1 (149). These are almost certainly automated. Excluding them removes
roughly 800 from the lifetime total — which is to say, **nearly half of that
1,862 is probably not human at all.**

**One thing I genuinely cannot explain.** The feed column declines steadily as
releases get newer — 744 at v0.8.0 down to 110 at v0.8.36. Older releases have
had longer to accumulate checks, which accounts for some of it, but not cleanly.
I do not know exactly how the updater distributes those requests across
releases, and rather than invent a mechanism I will say so. It is a reminder
that not every number in this data has a confident interpretation, and the
temptation to supply one is exactly the failure mode this article is about.

### The best number came from outside the download data entirely

Everything above is derived from download counts, and download counts can never
identify a person. But QuickMail has a second signal that can, and it is worth
more than the whole table.

Google caps an **unverified** application at **100 users**. QuickMail requests
Gmail scopes, which Google classes as restricted, so until the app passes
verification — a process that runs weeks and can require a paid third-party
security assessment — only 100 accounts may ever authorise it. QuickMail **hit
that cap.** New users now get "This app has been blocked," which is why Gmail
sign-in became opt-in and app passwords became the default route.

That is a fundamentally better measurement than anything downloads produce,
because it is **bound to identity**. Each of those authorisations is a distinct
Google account belonging to a distinct person who installed the software, opened
it, reached the account setup, and completed a sign-in. No bot does that. No
duplicate counts twice.

So: **at least 100 different people got QuickMail working with Gmail** — against
the 18 the delta count could see.

**How can both be true?** Because they answer different questions. And the gap
has a specific, checkable explanation: **auto-update did not exist until v0.8.0
on 8 July 2026.** Roughly thirty releases shipped before it, from v0.5.0 in May
onwards, with no updater at all. Every one of those users had to notice a new
version and manually download it.

Anyone who installed during that period and stopped manually updating is **still
running that old build today, and is invisible to every signal in the table
above** — permanently. They will never fetch a delta package, because their copy
has no idea deltas exist. The pre-updater installer numbers were not small
either: 57, 43, 40, 34, 32 across those releases.

That reframes the whole reading. The honest summary is now three numbers, not
one:

- **~18** installs are actively auto-updating and current.
- **≥100** distinct people authorised Gmail at some point.
- **Unknown, possibly large** — people stranded on a pre-auto-update build who
  are still using it and cannot be counted by any means available.

**The general lesson, which is the reason this section exists:** before drawing
conclusions from download counts, look for a number somewhere else that is
**tied to an identity**. An OAuth grant, a licence activation, an account
registration, a sync endpoint. One of those is worth more than the entire
release history, because it counts people rather than HTTP requests. If you have
one and are still reasoning from downloads, you are using your weakest data.

The caveats stay honest, though. That 100 is **cumulative, not current** — it
includes the developer's own test accounts, and everyone who authorised once and
abandoned it. It counts only Gmail users, so anyone on Outlook, iCloud, or a
plain IMAP server is outside it entirely. And a cap tells you the number was
*reached*, not by how much it was exceeded — demand above 100 is simply
invisible, since Google turns it away at the door.

### What the numbers do and do not say

They say roughly 15 to 20 people keep an up-to-date copy running, at least 100
have used it, and an uncounted group sits on old builds in between.

They do **not** say the software is good or bad. QuickMail has had little
promotion — a few Mastodon and mailing list posts and some newsletter mentions,
with no concerted effort while it is still under development — and a project
promoted that lightly gets a number like this regardless of quality. For
contrast, a comparable Windows accessibility project in the same niche recorded
roughly 3,000 downloads in two months, with an organisation behind it and a
36-episode onboarding podcast, which is a marketing budget wearing a disguise.
Nothing in either dataset speaks to which program is better. They measure reach,
and reach is mostly a function of effort spent on reach.

And they never, under any circumstance, distinguish a person who downloaded and
still uses it from a person who downloaded and deleted it ten minutes later.
Downloads measure acquisition. Only an updater measures retention, and only
approximately.

That is the whole point of publishing these figures rather than a tidy
hypothetical. Small numbers are the normal case for independent software, they
are worth reading accurately, and reading them accurately is more useful than
either inflating them or being embarrassed by them.

---

## The one-line summary

The numbers are easy to get and easy to misread. Pull them per asset rather than
per release, remember that each file describes a different group of people, treat
your updater's package count as your only real retention signal, and start
snapshotting today — because GitHub keeps no history, and the trend you will
eventually want can only be built forward from now.

And before you conclude anything from them, look for a number elsewhere that is
tied to an identity. If you have one, it outranks everything here.

---

*Sources: [About releases](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases) ·
[REST API endpoints for release assets](https://docs.github.com/en/rest/releases/assets) ·
[REST API endpoints for releases](https://docs.github.com/en/rest/releases/releases) ·
[the retired Enterprise 2.16 article](https://docs.github.com/en/enterprise/2.16/user/github/administering-a-repository/getting-the-download-count-for-your-releases)*

---

## Reporting Issues

Have a question about this article, a correction to it, or a problem with QuickMail itself? There are three ways to reach us — pick the one that fits:

1. **Report a Bug → Send** (Help menu, inside QuickMail). Files the report for you anonymously — it includes no email address or other identifying information, so there is no way to follow up with you. **Best when you don't want any follow-up.**
2. **Report a Bug → Copy report and open GitHub** (Help menu). Opens a pre-filled issue that you submit under your own GitHub account, so your GitHub contact information is attached. **Best when you have a GitHub account and want automatic filing plus direct contact.**
3. **Email** [quickmailissues@theideaplace.net](mailto:quickmailissues@theideaplace.net). **Best when you don't mind sending email and want a personal follow-up.**

Full details, including exactly what a report contains (and what it never contains), are in the [Reporting Issues section of the User Guide](https://kellylford.github.io/QuickMail/reporting-issues.html).

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

Same data, no CLI, works against public repos unauthenticated:

```bash
curl -s https://api.github.com/repos/OWNER/REPO/releases | jq -r '.[] | "\(.tag_name): \([.assets[].download_count] | add // 0)"'
```

Add `-H "Authorization: Bearer $TOKEN"` for private repositories or to lift the
unauthenticated rate limit (60 requests per hour, versus 5,000 authenticated).

Note that `jq` is a separate program you have to install. The `gh` examples
above do not need it — `gh api -q` has a jq-compatible filter built in, which is
why it is the easier route on a machine you do not control.

### With a GUI

If you would rather read this in a window than a terminal,
[GHManage](https://github.com/kellylford/GHManage) is a keyboard-driven,
screen-reader-friendly desktop app whose Releases view shows a downloads column
per release, a repo-wide total, and a per-file breakdown when you press Enter on
a release.

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

Both use jq's `@tsv` rather than building the string by hand, and that is
deliberate. On Windows, PowerShell strips the double quotes out of an argument
on its way to a native executable, so a filter written as
`"\($t)\t\(.name)"` reaches `gh` with its quotes removed and fails with
`unexpected token "\"`. `@tsv` contains no quotes to lose, produces properly
tab-separated output, and behaves identically in both shells.

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

Watch it across consecutive releases. A real example from a small Windows app
using an auto-updater:

| Release | Delta package | Full package |
|---------|--------------:|-------------:|
| 0.8.31  | 11 | 8 |
| 0.8.32  | 11 | 8 |
| 0.8.33  | 13 | 5 |
| 0.8.34  | 13 | 4 |
| 0.8.35  | 14 | 12 |
| 0.8.36  | 18 | 12 |

That steadiness is the finding. It is the same population showing up six
releases running, growing slowly. Meanwhile the installer count for those same
releases bounced between 5 and 41 — noisier, and measuring something else
entirely.

Treat the updater figure as a **floor**, not a total. Portable users and people
who reinstall manually never appear in it.

### Uneven counts mean real clients; uniform counts mean scraping

A useful heuristic. If a bot is crawling your release page and grabbing
everything, all your assets show roughly the same count. Real users produce a
jagged profile, because each one fetches exactly the file they need.

From the same release: the update-feed metadata file had 110 downloads, the
installer 27, the delta package 18, the portable build 11 — while a stale
package from the previous version had 1 and one metadata file had 0. That spread
is the signature of software behaving like software. Uniformity across every
asset would be the warning sign.

Apply the same suspicion to spikes. In that project's history, two releases
scored 258 and 234 when their neighbours sat between 30 and 40. A jump of that
shape, out of pattern with everything around it, is much more likely to be
automated traffic than a sudden burst of humans.

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

## A worked reading

Suppose one release shows:

```
installer.msi          27
app-delta.nupkg        18
app-full.nupkg         12
portable.exe           11
update-feed.json      110
```

The wrong read: "somewhere between 12 and 27 users."

The right read:

- 18 + 12 = **30 existing installs updated themselves.** Both nupkg files are
  fetched only by the updater; the full package is what it falls back to when a
  delta will not apply. Different machines, so they add.
- 27 installer + 11 portable = **38 fresh acquisitions**, of which an unknown
  share are bots, repeat downloads, or people who tried it once.
- 110 feed checks is **not** 110 users — it is cumulative polls, and one install
  polling daily for a week is seven of them.
- Ceiling on machines that obtained this version: about 68. Floor on machines
  demonstrably running it: about 30. The truth is in between and closer to the
  floor.

And the honest limit: none of this distinguishes someone who downloaded and kept
using it from someone who downloaded and deleted it ten minutes later. Downloads
measure acquisition. They can never measure whether anyone stayed.

---

## The one-line summary

The numbers are easy to get and easy to misread. Pull them per asset rather than
per release, remember that each file describes a different group of people, treat
your updater's package count as your only real retention signal, and start
snapshotting today — because GitHub keeps no history, and the trend you will
eventually want can only be built forward from now.

---

*Sources: [About releases](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases) ·
[REST API endpoints for release assets](https://docs.github.com/en/rest/releases/assets) ·
[REST API endpoints for releases](https://docs.github.com/en/rest/releases/releases) ·
[the retired Enterprise 2.16 article](https://docs.github.com/en/enterprise/2.16/user/github/administering-a-repository/getting-the-download-count-for-your-releases)*

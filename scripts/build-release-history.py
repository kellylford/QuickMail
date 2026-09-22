#!/usr/bin/env python3
"""Build the release history pages for the GitHub Pages site.

One HTML page per released version, generated from docs/release-notes-vX.Y.Z.md,
plus releases.html listing every version newest first.

The history is driven by the repository's v* tags, not by the files on disk: a
notes file with no tag is a release that has not happened yet (the open cycle's
skeleton, or a version that was prepared and never shipped), and it must not
appear in the history. A tag with no notes file is the opposite problem and
fails the run -- a released version silently missing from the history is the
kind of gap that goes unnoticed for months (see issue #556 for the same shape
of failure with privacy.html).

Run it from the repository root. Requires pandoc on PATH unless --dry-run.
"""

import argparse
import html
import os
import re
import subprocess
import sys
from datetime import date

# Sections copied from docs/download-footer.md and docs/reporting-issues-footer.md
# into every release-notes file. They are reference material for one release at
# one moment -- asset links, where to report a bug -- and repeating them on fifty
# pages would bury what each release actually changed. Stripped only where they
# are a trailing block, so an older file that opens with a download table (the
# shape used up to 0.8.34) keeps it rather than losing the prose sitting inside it.
FOOTER_TITLES = {"download", "reporting issues"}

MONTHS = ["January", "February", "March", "April", "May", "June",
          "July", "August", "September", "October", "November", "December"]

RELEASES_URL = "https://github.com/kellylford/QuickMail/releases/tag/"


def run(cmd):
    return subprocess.run(cmd, capture_output=True, text=True, check=True).stdout


def version_key(version):
    """0.8.9 sorts before 0.8.31, and 0.7.9 before 0.7.9.1."""
    return tuple(int(p) for p in version.split("."))


def tagged_releases():
    """Every v* tag, as (version, date), newest first."""
    # The tag's own recorded date -- the day the release was made, in the timezone it
    # was made in, so the answer does not depend on the machine running this. GitHub
    # shows each viewer that moment in their own timezone, so a date here can differ
    # by a day from the one on the release page.
    out = run(["git", "for-each-ref", "--format=%(refname:short)\t%(creatordate:short)",
               "refs/tags"])
    found = []
    for line in out.splitlines():
        if not line.strip():
            continue
        tag, tagged = line.split("\t")
        if not re.fullmatch(r"v\d+(\.\d+)*", tag):
            continue
        y, m, d = (int(p) for p in tagged.split("-"))
        found.append((tag[1:], date(y, m, d)))
    if not found:
        raise SystemExit("No v* tags found. The checkout needs fetch-depth: 0 for tags.")
    found.sort(key=lambda r: version_key(r[0]), reverse=True)
    return found


def split_sections(markdown):
    """(text before the first '## ', [(title, whole section text), ...])."""
    parts = re.split(r"^(## .*)$", markdown, flags=re.MULTILINE)
    preamble = parts[0]
    sections = []
    for i in range(1, len(parts), 2):
        heading = parts[i]
        body = parts[i + 1] if i + 1 < len(parts) else ""
        sections.append((heading[3:].strip(), heading + body))
    return preamble, sections


def strip_trailing_footers(markdown):
    preamble, sections = split_sections(markdown)
    while sections and sections[-1][0].lower() in FOOTER_TITLES:
        sections.pop()
    text = preamble + "".join(s for _, s in sections)
    # A dropped footer leaves the '---' that separated it from what came before.
    text = re.sub(r"(?:\r?\n)+(?:---(?:\r?\n)+)+\Z", "\n", text)
    return text.rstrip() + "\n"


def nav(links):
    return ('<div class="nav">'
            + " | ".join(f'<a href="{href}">{html.escape(label)}</a>' for label, href in links)
            + "</div>")


def page(title, css, nav_html, body):
    return f"""<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>{html.escape(title)}</title>
  {css}
</head>
<body>
  {nav_html}
  {body}
  {nav_html}
</body>
</html>
"""


def render(markdown):
    result = subprocess.run(["pandoc", "--from", "markdown", "--to", "html5"],
                            input=markdown, capture_output=True, text=True)
    if result.returncode != 0:
        raise SystemExit(f"pandoc failed: {result.stderr}")
    return result.stdout


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", default="build/docs", help="directory to write pages into")
    parser.add_argument("--css", default="", help="file holding the shared <style> block")
    parser.add_argument("--dry-run", action="store_true",
                        help="report what would be generated; do not run pandoc or write files")
    args = parser.parse_args()

    css = ""
    if args.css:
        with open(args.css, encoding="utf-8") as f:
            css = f.read()

    releases = tagged_releases()

    missing = [v for v, _ in releases if not os.path.exists(f"docs/release-notes-v{v}.md")]
    if missing:
        raise SystemExit(
            "Released versions with no docs/release-notes-vX.Y.Z.md file: "
            + ", ".join(missing)
            + "\nEvery released version needs one, or it drops out of the history.")

    untagged = sorted(
        (m.group(1) for m in (re.fullmatch(r"release-notes-v(.+)\.md", f)
                              for f in os.listdir("docs")) if m),
        key=version_key)
    tagged = {v for v, _ in releases}
    untagged = [v for v in untagged if v not in tagged]

    if not args.dry_run:
        os.makedirs(args.out, exist_ok=True)

    for i, (version, released) in enumerate(releases):
        with open(f"docs/release-notes-v{version}.md", encoding="utf-8") as f:
            body_md = strip_trailing_footers(f.read())

        when = f"{MONTHS[released.month - 1]} {released.day}, {released.year}"
        links = [("User Guide", "index.html"), ("Release History", "releases.html")]
        if i > 0:
            links.append((f"Newer: {releases[i - 1][0]}",
                          f"release-notes-v{releases[i - 1][0]}.html"))
        if i < len(releases) - 1:
            links.append((f"Older: {releases[i + 1][0]}",
                          f"release-notes-v{releases[i + 1][0]}.html"))

        if args.dry_run:
            print(f"  {version:<10} {when:<20} {len(body_md):>6} characters")
            continue

        body = render(body_md)
        downloads = (f'<p class="version">Released {html.escape(when)} — '
                     f'<a href="{RELEASES_URL}v{version}">downloads for this release</a></p>')
        # The version line belongs under the title pandoc just made from the "# ..." line.
        body = re.sub(r"(</h1>)", r"\1\n" + downloads.replace("\\", "\\\\"), body, count=1)
        with open(os.path.join(args.out, f"release-notes-v{version}.html"),
                  "w", encoding="utf-8") as f:
            f.write(page(f"QuickMail {version} Release Notes", css,
                         nav(links), body))

    items = "\n".join(
        f'    <li><a href="release-notes-v{v}.html">Version {v}</a> — '
        f'{MONTHS[d.month - 1]} {d.day}, {d.year}'
        + (" (current release)" if i == 0 else "")
        + "</li>"
        for i, (v, d) in enumerate(releases))

    index_body = f"""<h1>QuickMail Release History</h1>
  <p>Every QuickMail release, newest first, with the notes published for it.
  There are {len(releases)} of them, starting in May 2026.</p>
  <p>To find out which version you are running, open <strong>Help → About
  QuickMail</strong>. QuickMail installed from the installer updates itself, so
  you are normally on the newest version already.</p>
  <ul>
{items}
  </ul>
  <hr>
  <p>Each release's downloads are on
  <a href="https://github.com/kellylford/QuickMail/releases">its page on GitHub</a>.</p>"""

    if args.dry_run:
        print(f"\nWould write {len(releases)} release pages and releases.html to {args.out}")
        if untagged:
            print("Notes files for versions that have not been released (left out of the "
                  "history): " + ", ".join(untagged))
        return

    with open(os.path.join(args.out, "releases.html"), "w", encoding="utf-8") as f:
        f.write(page("QuickMail Release History", css,
                     nav([("User Guide", "index.html"), ("Single Page", "full.html")]),
                     index_body))

    print(f"Generated {len(releases)} release pages and releases.html")
    if untagged:
        print("Not released, so left out of the history: " + ", ".join(untagged))


if __name__ == "__main__":
    sys.exit(main())

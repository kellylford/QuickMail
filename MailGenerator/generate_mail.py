#!/usr/bin/env python3
"""
MailGenerator — Populate an IMAP folder with realistic test messages.

Uses IMAP APPEND to inject messages directly into a folder without
actually sending them through SMTP. This is fast and doesn't clutter
your Sent folder.

Usage:
    python generate_mail.py              # 50 messages (default)
    python generate_mail.py -n 200       # 200 messages
    python generate_mail.py -n 100 -f "Archive"  # into Archive folder
    python generate_mail.py --dry-run    # preview without injecting

Credentials are read from credentials.ini (gitignored).
Copy credentials.example.ini to credentials.ini and fill in your password.
"""

import argparse
import configparser
import imaplib
import os
import random
import sys
import time
from datetime import datetime, timedelta, timezone
from email.encoders import encode_base64
from email.mime.base import MIMEBase
from email.mime.multipart import MIMEMultipart
from email.mime.text import MIMEText
from email.utils import format_datetime
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent
CREDENTIALS_FILE = SCRIPT_DIR / "credentials.ini"


# ── Realistic message templates ────────────────────────────────────────────

SENDERS = [
    ("Alice Johnson", "alice.johnson@example.com"),
    ("Bob Martinez", "bob.martinez@example.com"),
    ("Carol Chen", "carol.chen@example.com"),
    ("Dave Williams", "dave.williams@example.com"),
    ("Elena Rossi", "elena.rossi@example.com"),
    ("Frank Okafor", "frank.okafor@example.com"),
    ("Grace Nilsson", "grace.nilsson@example.com"),
    ("Henry Park", "henry.park@example.com"),
    ("Iris Kowalski", "iris.kowalski@example.com"),
    ("James Murphy", "james.murphy@example.com"),
    ("Joseph Brown", "joe.brown@example.com"),
    ("Karen Nakamura", "karen.nakamura@example.com"),
    ("Leo Andersson", "leo.andersson@example.com"),
    ("Maria Santos", "maria.santos@example.com"),
    ("Nathan Price", "nathan.price@example.com"),
    ("Olivia Berg", "olivia.berg@example.com"),
]

SUBJECTS = [
    # Plain subjects
    "Q3 budget review — draft attached",
    "Meeting notes from Thursday's standup",
    "Server migration scheduled for Saturday",
    "New hire onboarding document",
    "Invoice #INV-2026-0412",
    "Updated privacy policy — please review",
    "Your order has shipped",
    "Newsletter: May 2026 edition",
    "Reminder: performance reviews due Friday",
    "Office closed Monday for holiday",
    "API deprecation notice — v1 endpoints",
    "Welcome to the team!",
    "Password reset request",
    "System maintenance window: Sunday 2–4 AM",
    "Lunch next week?",
    "Contract signed — next steps",
    "Build v2.4.1 deployed to staging",
    "Customer feedback summary — April",
    "Travel reimbursement form",
    "Security alert: unusual login detected",
    "Podcast recommendation",
    "Conference registration open",
    "Your subscription renewal",
    "Bug report: search not returning drafts",
    "Design review at 3 PM",
    "Please update your emergency contact info",
    "New coffee machine in break room",
    "401(k) enrollment period starts Monday",
    "Parking garage closed for repairs",
    "Team photo this Thursday at noon",
    "Draft of the quarterly report for review",
    "VPN certificate expiring — action required",
    "Feedback requested on new landing page",
    "Your training module is overdue",
    "Happy hour this Friday at 5 PM",
]

# Some subjects get Re:/Fwd: prefixes to create conversation threads
THREAD_PREFIXES = [
    "",  # no prefix (most common)
    "",
    "",
    "",
    "Re: ",
    "Re: ",
    "Re: ",
    "Fwd: ",
    "Fwd: ",
    "Re: Re: ",
]

PLAIN_BODIES = [
    """Hi,

Please find the attached document for your review. Let me know if you have any questions.

Thanks,
{sender_name}""",

    """Hey,

Just checking in on the status of the API work. The client is asking for an ETA — can you give me a rough estimate by end of day?

Cheers,
{sender_name}""",

    """All,

The deployment went smoothly. All tests are passing in staging. We're on track for the Monday production release.

{sender_name}""",

    """Hi there,

Can we reschedule tomorrow's standup to 10:30 instead of 9? I have a conflict with the architecture review.

Thanks,
{sender_name}""",

    """FYI — the new documentation site is live at docs.internal.example.com. Please update your bookmarks.

{sender_name}""",

    """Quick question: does anyone have the slide deck from last month's all-hands? I need to pull a couple of numbers from it.

Thanks!
{sender_name}""",

    """The numbers for Q2 are in. Revenue is up 12% quarter-over-quarter. Full report attached — let's discuss at the Friday meeting.

{sender_name}""",

    """Reminder: please submit your expense reports by end of day Friday. Finance won't process anything submitted after the deadline.

{sender_name}""",

    """I'm out of the office this Thursday and Friday. {sender_name_short} will be the point of contact for anything urgent.

{sender_name}""",

    """Just wanted to say great job on the presentation yesterday. The client was really impressed with the demo.

{sender_name}""",

    """Can someone point me to the latest Figma mockups? I think I'm looking at an outdated version.

{sender_name}""",

    """The build is broken on main — looks like a missing migration script. I'm looking into it now, will update the channel when it's fixed.

{sender_name}""",

    """Happy to help with the onboarding! I've added a few notes to the welcome doc. Let me know when the new hire starts.

{sender_name}""",

    """Don't forget: the team offsite is next month. If you haven't filled out the dietary restrictions form yet, please do it today.

{sender_name}""",

    """I reviewed the PR — left a few comments but overall looks solid. Nice work on the error handling refactor.

{sender_name}""",
]

HTML_BODIES = [
    """<html><body>
<p>Hi,</p>
<p>Please review the <strong>attached proposal</strong> and let me know your thoughts by Friday.</p>
<p>Key points:</p>
<ul>
<li>Budget: $45,000</li>
<li>Timeline: 8 weeks</li>
<li>Team: 3 engineers</li>
</ul>
<p>Thanks,<br>{sender_name}</p>
</body></html>""",

    """<html><body>
<p>Hey team,</p>
<p>The <a href="https://example.com/dashboard">new dashboard</a> is live. Here's what changed:</p>
<ol>
<li>Faster load times (2x improvement)</li>
<li>New export-to-CSV feature</li>
<li>Dark mode support</li>
</ol>
<p>Please kick the tires and report any issues in <strong>#bugs</strong>.</p>
<p>{sender_name}</p>
</body></html>""",

    """<html><body>
<p>Hi all,</p>
<p>Quick reminder about the <strong>code freeze</strong> starting Friday at 5 PM. After that:</p>
<ul>
<li>No merges to main without director approval</li>
<li>Hotfixes go through the release branch</li>
<li>Staging will be locked for QA</li>
</ul>
<p>Release is scheduled for Tuesday at 10 AM.</p>
<p>{sender_name}</p>
</body></html>""",
]

# ── Attachment templates ───────────────────────────────────────────────────

# Small text-based attachments generated on the fly.
# Each entry: (filename, content_generator_function)
# The function receives no args and returns bytes.

def _make_csv() -> bytes:
    rows = [
        "Date,Description,Amount,Category",
        "2026-01-15,Office supplies,142.50,Expenses",
        "2026-02-03,Software license,1200.00,IT",
        "2026-02-20,Team lunch,89.75,Meals",
        "2026-03-10,Conference registration,450.00,Travel",
        "2026-04-01,Cloud hosting,3200.00,Infrastructure",
    ]
    return "\n".join(rows).encode("utf-8")


def _make_json() -> bytes:
    import json
    data = {
        "project": "QuickMail",
        "version": "0.6.0",
        "metrics": {
            "users": 1240,
            "messages_processed": 89500,
            "uptime_pct": 99.97,
        },
        "dependencies": ["MailKit", "SQLite", "WebView2"],
    }
    return json.dumps(data, indent=2).encode("utf-8")


def _make_log() -> bytes:
    lines = [
        "[2026-05-20 08:00:01] INFO  Server started on port 443",
        "[2026-05-20 08:00:05] INFO  Database connection pool initialized (size=10)",
        "[2026-05-20 08:15:22] WARN  High memory usage detected: 87%",
        "[2026-05-20 09:30:00] INFO  Scheduled backup completed successfully",
        "[2026-05-20 10:45:12] ERROR Connection timeout to upstream service (retry 1/3)",
        "[2026-05-20 10:45:15] INFO  Connection restored after retry",
        "[2026-05-20 12:00:00] INFO  Health check: OK",
    ]
    return "\n".join(lines).encode("utf-8")


def _make_xml() -> bytes:
    return b"""<?xml version="1.0" encoding="UTF-8"?>
<config>
    <database>
        <host>localhost</host>
        <port>5432</port>
        <name>quickmail_prod</name>
    </database>
    <cache>
        <ttl>3600</ttl>
        <maxSize>512MB</maxSize>
    </cache>
</config>"""


def _make_markdown() -> bytes:
    return b"""# Meeting Notes -- May 20, 2026

## Attendees
- Alice
- Bob
- Carol

## Agenda
1. Sprint review
2. Bug triage
3. Planning for next sprint

## Decisions
- **API v2** will launch June 1
- **Bug #452** is deferred to next sprint
- **Hiring** approved for one additional backend engineer

## Action Items
- [ ] Alice: draft API v2 migration guide
- [ ] Bob: investigate memory leak in worker pool
- [ ] Carol: update onboarding docs
"""


ATTACHMENTS = [
    ("report.csv", _make_csv),
    ("metrics.json", _make_json),
    ("server.log", _make_log),
    ("config.xml", _make_xml),
    ("meeting-notes.md", _make_markdown),
]


def add_random_attachments(msg: MIMEMultipart, max_count: int = 2) -> int:
    """Add 1–max_count random attachments to the message. Returns count added."""
    count = random.randint(1, max_count)
    chosen = random.sample(ATTACHMENTS, min(count, len(ATTACHMENTS)))
    for filename, generator in chosen:
        part = MIMEBase("application", "octet-stream")
        part.set_payload(generator())
        encode_base64(part)
        part.add_header(
            "Content-Disposition",
            f'attachment; filename="{filename}"',
        )
        msg.attach(part)
    return len(chosen)


def load_config() -> configparser.SectionProxy:
    """Load credentials from credentials.ini."""
    if not CREDENTIALS_FILE.exists():
        print(f"ERROR: {CREDENTIALS_FILE} not found.")
        print("Copy credentials.example.ini to credentials.ini and fill in your password.")
        sys.exit(1)

    config = configparser.ConfigParser()
    config.read(CREDENTIALS_FILE)

    if "imap" not in config:
        print("ERROR: [imap] section missing from credentials.ini")
        sys.exit(1)

    imap = config["imap"]
    required = ["host", "port", "username", "password"]
    missing = [k for k in required if k not in imap or not imap[k].strip()]
    if missing:
        print(f"ERROR: Missing required [imap] fields: {', '.join(missing)}")
        sys.exit(1)

    return imap


def build_message(
    sender_name: str,
    sender_addr: str,
    recipient: str,
    subject: str,
    date: datetime,
    use_html: bool = False,
    attachments: int = 0,
) -> bytes:
    """Build a MIME message and return it as bytes.

    Args:
        attachments: Number of attachments to add (0 = none, -1 = random 1-2).
    """
    # Determine if we need multipart/mixed (for attachments)
    has_attachments = False
    if attachments == -1:
        has_attachments = random.random() < 0.3  # 30% chance of attachments
    elif attachments > 0:
        has_attachments = True

    if has_attachments:
        msg = MIMEMultipart("mixed")
    elif use_html:
        msg = MIMEMultipart("alternative")
    else:
        msg = MIMEText("", "plain")  # placeholder, replaced below

    # Build body parts
    if use_html:
        body_template = random.choice(HTML_BODIES)
        body = body_template.format(sender_name=sender_name)
        plain = "This message contains HTML content. Please view in an HTML-capable client."
        msg.attach(MIMEText(plain, "plain"))
        msg.attach(MIMEText(body, "html"))
    else:
        body_template = random.choice(PLAIN_BODIES)
        name_parts = sender_name.split()
        short = name_parts[0] if name_parts else sender_name
        body = body_template.format(sender_name=sender_name, sender_name_short=short)
        if has_attachments:
            msg.attach(MIMEText(body, "plain"))
        else:
            msg = MIMEText(body, "plain")

    # Add attachments
    if has_attachments:
        add_random_attachments(msg)

    msg["From"] = f"{sender_name} <{sender_addr}>"
    msg["To"] = recipient
    msg["Subject"] = subject
    msg["Date"] = format_datetime(date)
    msg["Message-ID"] = f"<{random.getrandbits(64):016x}@mailgenerator.local>"

    return msg.as_bytes()


def connect_imap(config: configparser.SectionProxy) -> imaplib.IMAP4_SSL:
    """Connect and log in to the IMAP server."""
    host = config["host"]
    port = int(config["port"])
    username = config["username"]
    password = config["password"]

    print(f"Connecting to {host}:{port} as {username} ...")
    mail = imaplib.IMAP4_SSL(host, port)
    mail.login(username, password)
    print("Connected.")
    return mail


def main():
    parser = argparse.ArgumentParser(
        description="Generate test emails via IMAP APPEND",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Examples:
  python generate_mail.py                     # 50 messages into INBOX
  python generate_mail.py -n 200              # 200 messages
  python generate_mail.py -n 100 -f Archive   # 100 messages into Archive
  python generate_mail.py --dry-run           # preview only
  python generate_mail.py -n 20 --html-only   # all HTML-formatted
  python generate_mail.py -n 30 --unread-only # all marked unread
        """,
    )
    parser.add_argument(
        "-n", "--count", type=int, default=50,
        help="Number of messages to generate (default: 50)",
    )
    parser.add_argument(
        "-f", "--folder", type=str, default=None,
        help="IMAP folder to append to (default: from credentials.ini, or INBOX)",
    )
    parser.add_argument(
        "--date-spread", type=int, default=None,
        help="Spread messages over this many days (default: from credentials.ini, or 60)",
    )
    parser.add_argument(
        "--html-only", action="store_true",
        help="Generate only HTML-formatted messages",
    )
    parser.add_argument(
        "--plain-only", action="store_true",
        help="Generate only plain-text messages",
    )
    parser.add_argument(
        "--unread-only", action="store_true",
        help="Mark all generated messages as unread (\\Seen flag omitted)",
    )
    parser.add_argument(
        "--dry-run", action="store_true",
        help="Preview messages without actually appending them",
    )
    parser.add_argument(
        "--delay", type=float, default=0.1,
        help="Delay in seconds between APPEND commands (default: 0.1)",
    )
    parser.add_argument(
        "--attachments", type=int, default=-1,
        help="Attachment mode: -1 = random 30%% of messages (default), "
             "0 = none, 1 = every message gets 1-2 attachments",
    )
    args = parser.parse_args()

    # Load config
    imap_config = load_config()

    # Read generator settings from config, CLI args override
    config = configparser.ConfigParser()
    config.read(CREDENTIALS_FILE)

    folder = args.folder or (
        config.get("generator", "folder", fallback="INBOX")
    )
    date_spread = args.date_spread or (
        config.getint("generator", "date_spread_days", fallback=60)
    )
    recipient = imap_config["username"]

    # Connect
    if not args.dry_run:
        mail = connect_imap(imap_config)

    # Generate
    now = datetime.now(timezone.utc)
    print(f"\nGenerating {args.count} messages into '{folder}' "
          f"spread over {date_spread} days...\n")

    for i in range(args.count):
        sender_name, sender_addr = random.choice(SENDERS)
        base_subject = random.choice(SUBJECTS)
        prefix = random.choice(THREAD_PREFIXES)
        subject = prefix + base_subject

        # Spread dates, with a bias toward recent
        days_ago = random.randint(0, date_spread)
        # Square the fraction to bias toward recent
        days_ago = int(date_spread * (random.random() ** 1.5))
        date = now - timedelta(days=days_ago, hours=random.randint(0, 23),
                               minutes=random.randint(0, 59))

        # HTML vs plain
        if args.html_only:
            use_html = True
        elif args.plain_only:
            use_html = False
        else:
            use_html = random.random() < 0.25  # 25% HTML

        msg_bytes = build_message(sender_name, sender_addr, recipient,
                                  subject, date, use_html,
                                  attachments=args.attachments)

        # Flags
        flags = []
        if not args.unread_only:
            flags.append("\\Seen")
        if random.random() < 0.1:  # 10% flagged
            flags.append("\\Flagged")
        if random.random() < 0.05:  # 5% answered
            flags.append("\\Answered")
        flag_str = " ".join(flags) if flags else ""

        if args.dry_run:
            flag_label = flag_str if flag_str else "(no flags)"
            kind = "HTML" if use_html else "plain"
            att_label = ""
            if args.attachments == 0:
                att_label = ""
            elif args.attachments > 0:
                att_label = " +att"
            else:
                att_label = " +att" if random.random() < 0.3 else ""
            print(f"  [{i+1:4d}] {date.strftime('%Y-%m-%d')} | "
                  f"{sender_name:<20s} | {flag_label:<20s} | "
                  f"{kind:<5s}{att_label:<5s} | {subject[:50]}")
        else:
            result = mail.append(
                f'"{folder}"',
                flag_str,
                imaplib.Time2Internaldate(date),
                msg_bytes,
            )
            if result[0] != "OK":
                print(f"  [{i+1:4d}] FAILED: {result}")
            elif (i + 1) % 10 == 0 or i == args.count - 1:
                print(f"  [{i+1:4d}] appended successfully")

            time.sleep(args.delay)

    if not args.dry_run:
        mail.logout()
        print(f"\nDone. {args.count} messages appended to '{folder}'.")
    else:
        print(f"\nDry run complete. {args.count} messages would be appended.")


if __name__ == "__main__":
    main()

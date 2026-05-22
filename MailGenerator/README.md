# MailGenerator

Populates an IMAP folder with realistic test messages using IMAP `APPEND` — no SMTP sending required.

## Setup

1. Copy the credentials template and fill in your password:

   ```bash
   cp credentials.example.ini credentials.ini
   ```

2. Edit `credentials.ini` — replace `your-password` with your actual password.

3. `credentials.ini` is gitignored and will never be committed.

## Usage

```bash
# 50 messages into INBOX (default)
python generate_mail.py

# 200 messages
python generate_mail.py -n 200

# Into a specific folder
python generate_mail.py -n 100 -f "Archive"

# Preview without injecting
python generate_mail.py --dry-run

# All HTML-formatted messages
python generate_mail.py -n 20 --html-only

# All plain-text messages
python generate_mail.py -n 30 --plain-only

# All marked unread
python generate_mail.py -n 30 --unread-only

# With attachments (default: random 30% of messages)
python generate_mail.py -n 50 --attachments -1

# Every message gets 1-2 attachments
python generate_mail.py -n 20 --attachments 1

# No attachments at all
python generate_mail.py -n 50 --attachments 0
```

## What it generates

- **10 realistic sender names/addresses** (Alice Johnson, Bob Martinez, etc.)
- **30 varied subjects** — some with `Re:` / `Fwd:` prefixes to create conversation threads
- **15 plain-text body templates** — meeting notes, status updates, reminders, etc.
- **3 HTML body templates** — with lists, links, and formatting
- **Dates spread over 60 days** (configurable), biased toward recent
- **Random flags** — 10% flagged, 5% answered, rest seen (configurable)
- **Random attachments** — 30% of messages get 1–2 attachments (CSV, JSON, log, XML, Markdown); configurable via `--attachments`

## Requirements

- Python 3.7+
- No external packages — uses only the standard library (`imaplib`, `email`, `argparse`, `configparser`)

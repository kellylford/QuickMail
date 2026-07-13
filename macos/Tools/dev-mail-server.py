#!/usr/bin/env python3
"""Tiny plaintext IMAP + SMTP server for local QuickMail Mac development.

Not a real mail server: just enough protocol for the app and qmcli to
exercise login, folder list, fetch, flags, move, append, and send.

Usage:  python3 dev-mail-server.py [--imap-port 1143] [--smtp-port 1025]
Login:  any username with password "test" (e.g. dev@example.com / test)
"""
import argparse
import email
import email.utils
import socket
import socketserver
import threading
from email.message import EmailMessage

LOCK = threading.Lock()
NEXT_UID = [100]


def make_message(from_name, from_addr, to_addr, subject, text, html=None, date_offset=0):
    msg = EmailMessage()
    msg["From"] = email.utils.formataddr((from_name, from_addr))
    msg["To"] = to_addr
    msg["Subject"] = subject
    msg["Date"] = email.utils.formatdate(1783300000 + date_offset * 3600)
    msg["Message-ID"] = email.utils.make_msgid(domain="dev.example.com")
    msg.set_content(text)
    if html:
        msg.add_alternative(html, subtype="html")
    return msg.as_bytes().replace(b"\n", b"\r\n")


def seed():
    inbox = [
        {
            "raw": make_message("Jane Doe", "jane@example.com", "dev@example.com",
                                "Welcome to QuickMail for Mac",
                                "Hi Kelly,\n\nThis is a plain text message with a link: https://example.com/guide\n\n— Jane",
                                date_offset=0),
            "flags": set(),
        },
        {
            "raw": make_message("Büro Café", "buro@example.com", "dev@example.com",
                                "Unicode tëst — häder ✓",
                                "Plain fallback body.",
                                html="<p>This is <b>HTML</b> with an <a href='https://example.com'>anchor</a>.</p>"
                                     "<script>alert('should never run')</script>",
                                date_offset=1),
            "flags": set(),
        },
        {
            "raw": make_message("Status Bot", "bot@example.com", "dev@example.com",
                                "Read message example",
                                "You already read this one.",
                                date_offset=2),
            "flags": {"\\Seen"},
        },
    ]
    folders = {
        "INBOX": [],
        "Sent": [],
        "Drafts": [],
        "Trash": [],
        "Archive": [],
    }
    for m in inbox:
        m["uid"] = NEXT_UID[0]
        NEXT_UID[0] += 1
        folders["INBOX"].append(m)
    return folders


FOLDERS = seed()
FOLDER_ATTRS = {"Sent": "\\Sent", "Drafts": "\\Drafts", "Trash": "\\Trash", "Archive": "\\Archive"}


def envelope(raw):
    """Build an IMAP ENVELOPE string from raw message bytes."""
    m = email.message_from_bytes(raw)

    def q(s):
        if s is None:
            return "NIL"
        return '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'

    def addrs(header):
        vals = m.get_all(header)
        if not vals:
            return "NIL"
        parsed = email.utils.getaddresses(vals)
        if not parsed:
            return "NIL"
        out = []
        for name, addr in parsed:
            mailbox, _, host = addr.partition("@")
            # Header values are already RFC2047-encoded in the stored bytes;
            # the envelope carries them verbatim (no re-encoding).
            out.append(f"({q(name or None)} NIL {q(mailbox)} {q(host)})")
        return "(" + "".join(out) + ")"

    subject = m.get("Subject") or None
    return (f'({q(m.get("Date"))} {q(subject)} {addrs("From")} {addrs("From")} '
            f'{addrs("Reply-To") if m.get("Reply-To") else addrs("From")} {addrs("To")} '
            f'{addrs("Cc")} {addrs("Bcc")} {q(m.get("In-Reply-To"))} {q(m.get("Message-ID"))})')


def internal_date(raw):
    m = email.message_from_bytes(raw)
    dt = email.utils.parsedate_to_datetime(m.get("Date"))
    return dt.strftime("%d-%b-%Y %H:%M:%S %z")


class IMAPHandler(socketserver.StreamRequestHandler):
    def send_line(self, text):
        self.wfile.write(text.encode() + b"\r\n")

    def handle(self):
        self.selected = None
        self.send_line("* OK dev-mail-server ready")
        while True:
            try:
                line = self.rfile.readline()
            except ConnectionError:
                return
            if not line:
                return
            line = line.rstrip(b"\r\n").decode("utf-8", "replace")
            parts = line.split(" ", 2)
            if len(parts) < 2:
                continue
            tag, cmd = parts[0], parts[1].upper()
            rest = parts[2] if len(parts) > 2 else ""
            try:
                if not self.dispatch(tag, cmd, rest):
                    return
            except Exception as e:  # noqa: BLE001
                self.send_line(f"{tag} BAD internal error: {e}")

    def dispatch(self, tag, cmd, rest):
        if cmd == "CAPABILITY":
            self.send_line("* CAPABILITY IMAP4rev1 MOVE")
            self.send_line(f"{tag} OK done")
        elif cmd == "LOGIN":
            if rest.rstrip().endswith('"test"') or rest.rstrip().endswith(" test"):
                self.send_line(f"{tag} OK logged in")
            else:
                self.send_line(f"{tag} NO invalid credentials")
        elif cmd == "LIST":
            with LOCK:
                for name in FOLDERS:
                    attr = FOLDER_ATTRS.get(name, "")
                    attrs = f"(\\HasNoChildren {attr})".replace(" )", ")")
                    self.send_line(f'* LIST {attrs} "/" "{name}"')
            self.send_line(f"{tag} OK done")
        elif cmd == "SELECT":
            name = rest.strip().strip('"')
            with LOCK:
                if name not in FOLDERS:
                    self.send_line(f"{tag} NO no such folder")
                    return True
                self.selected = name
                self.send_line(f"* {len(FOLDERS[name])} EXISTS")
            self.send_line("* FLAGS (\\Seen \\Flagged \\Answered \\Deleted)")
            self.send_line(f"{tag} OK [READ-WRITE] selected")
        elif cmd == "FETCH":
            self.do_fetch(tag, rest, by_uid=False)
        elif cmd == "UID":
            sub, _, subrest = rest.partition(" ")
            sub = sub.upper()
            if sub == "FETCH":
                self.do_fetch(tag, subrest, by_uid=True)
            elif sub == "STORE":
                self.do_store(tag, subrest)
            elif sub == "MOVE":
                self.do_move(tag, subrest)
            elif sub == "COPY":
                self.do_copy(tag, subrest)
            else:
                self.send_line(f"{tag} BAD unknown uid subcommand")
        elif cmd == "APPEND":
            self.do_append(tag, rest)
        elif cmd == "EXPUNGE":
            with LOCK:
                msgs = FOLDERS.get(self.selected, [])
                FOLDERS[self.selected] = [m for m in msgs if "\\Deleted" not in m["flags"]]
            self.send_line(f"{tag} OK expunged")
        elif cmd == "NOOP":
            self.send_line(f"{tag} OK noop")
        elif cmd == "LOGOUT":
            self.send_line("* BYE bye")
            self.send_line(f"{tag} OK logged out")
            return False
        else:
            self.send_line(f"{tag} BAD unknown command {cmd}")
        return True

    def msgs(self):
        return FOLDERS.get(self.selected, [])

    def parse_range(self, spec, by_uid):
        lo, _, hi = spec.partition(":")
        with LOCK:
            msgs = list(self.msgs())
        if by_uid:
            lo_n = int(lo)
            hi_n = int(hi) if hi and hi != "*" else (lo_n if not hi else 2**32)
            return [(i + 1, m) for i, m in enumerate(msgs) if lo_n <= m["uid"] <= hi_n]
        lo_n = int(lo)
        hi_n = int(hi) if hi else lo_n
        return [(i + 1, m) for i, m in enumerate(msgs) if lo_n <= i + 1 <= hi_n]

    def do_fetch(self, tag, rest, by_uid):
        spec, _, items = rest.partition(" ")
        items = items.upper()
        for seq, m in self.parse_range(spec, by_uid):
            fields = [f"UID {m['uid']}"]
            if "FLAGS" in items:
                fields.append("FLAGS (" + " ".join(sorted(m["flags"])) + ")")
            if "INTERNALDATE" in items:
                fields.append(f'INTERNALDATE "{internal_date(m["raw"])}"')
            if "ENVELOPE" in items:
                fields.append(f"ENVELOPE {envelope(m['raw'])}")
            if "BODY.PEEK[]" in items or "BODY[]" in items:
                raw = m["raw"]
                self.wfile.write(
                    f"* {seq} FETCH ({' '.join(fields)} BODY[] {{{len(raw)}}}\r\n".encode())
                self.wfile.write(raw)
                self.wfile.write(b")\r\n")
                continue
            self.send_line(f"* {seq} FETCH ({' '.join(fields)})")
        self.send_line(f"{tag} OK fetch done")

    def find_by_uid(self, uid):
        for m in self.msgs():
            if m["uid"] == uid:
                return m
        return None

    def do_store(self, tag, rest):
        parts = rest.split(" ", 2)
        uid = int(parts[0])
        op = parts[1].upper()
        flags = set(parts[2].strip("()").split()) if len(parts) > 2 else set()
        with LOCK:
            m = self.find_by_uid(uid)
            if m:
                if op.startswith("+"):
                    m["flags"] |= flags
                else:
                    m["flags"] -= flags
        self.send_line(f"{tag} OK store done")

    def do_move(self, tag, rest):
        uid_s, _, dest = rest.partition(" ")
        dest = dest.strip().strip('"')
        with LOCK:
            m = self.find_by_uid(int(uid_s))
            if not m or dest not in FOLDERS:
                self.send_line(f"{tag} NO move failed")
                return
            FOLDERS[self.selected].remove(m)
            FOLDERS[dest].append(m)
        self.send_line(f"{tag} OK moved")

    def do_copy(self, tag, rest):
        uid_s, _, dest = rest.partition(" ")
        dest = dest.strip().strip('"')
        with LOCK:
            m = self.find_by_uid(int(uid_s))
            if not m or dest not in FOLDERS:
                self.send_line(f"{tag} NO copy failed")
                return
            copy = dict(m)
            copy["uid"] = NEXT_UID[0]
            NEXT_UID[0] += 1
            FOLDERS[dest].append(copy)
        self.send_line(f"{tag} OK copied")

    def do_append(self, tag, rest):
        # APPEND "Sent" (\Seen) {123}
        folder = rest.split('"')[1] if '"' in rest else rest.split()[0]
        size = int(rest.rsplit("{", 1)[1].rstrip("}+"))
        self.send_line("+ go ahead")
        data = self.rfile.read(size)
        self.rfile.readline()  # trailing CRLF
        with LOCK:
            if folder not in FOLDERS:
                self.send_line(f"{tag} NO no such folder")
                return
            FOLDERS[folder].append({"raw": data, "flags": {"\\Seen"}, "uid": NEXT_UID[0]})
            NEXT_UID[0] += 1
        self.send_line(f"{tag} OK appended")


class SMTPHandler(socketserver.StreamRequestHandler):
    def send_line(self, text):
        self.wfile.write(text.encode() + b"\r\n")

    def handle(self):
        self.send_line("220 dev-mail-server SMTP ready")
        sender, rcpts = None, []
        while True:
            line = self.rfile.readline()
            if not line:
                return
            text = line.rstrip(b"\r\n").decode("utf-8", "replace")
            upper = text.upper()
            if upper.startswith("EHLO") or upper.startswith("HELO"):
                self.send_line("250-dev-mail-server")
                self.send_line("250 AUTH PLAIN LOGIN")
            elif upper.startswith("AUTH"):
                self.send_line("235 ok")
            elif upper.startswith("MAIL FROM"):
                sender = text.split(":", 1)[1].strip()
                self.send_line("250 ok")
            elif upper.startswith("RCPT TO"):
                rcpts.append(text.split(":", 1)[1].strip())
                self.send_line("250 ok")
            elif upper == "DATA":
                self.send_line("354 go ahead")
                body = bytearray()
                while True:
                    dline = self.rfile.readline()
                    if dline in (b".\r\n", b".\n"):
                        break
                    if dline.startswith(b".."):
                        dline = dline[1:]
                    body.extend(dline)
                with LOCK:
                    FOLDERS["INBOX"].append(
                        {"raw": bytes(body), "flags": set(), "uid": NEXT_UID[0]})
                    NEXT_UID[0] += 1
                print(f"SMTP: accepted message from {sender} to {rcpts} "
                      f"({len(body)} bytes) -> INBOX")
                self.send_line("250 ok queued")
            elif upper == "QUIT":
                self.send_line("221 bye")
                return
            else:
                self.send_line("250 ok")


class Server(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--imap-port", type=int, default=1143)
    ap.add_argument("--smtp-port", type=int, default=1025)
    args = ap.parse_args()
    imap = Server(("127.0.0.1", args.imap_port), IMAPHandler)
    smtp = Server(("127.0.0.1", args.smtp_port), SMTPHandler)
    threading.Thread(target=imap.serve_forever, daemon=True).start()
    threading.Thread(target=smtp.serve_forever, daemon=True).start()
    print(f"dev-mail-server: IMAP on 127.0.0.1:{args.imap_port}, "
          f"SMTP on 127.0.0.1:{args.smtp_port}; password 'test'")
    threading.Event().wait()


if __name__ == "__main__":
    main()

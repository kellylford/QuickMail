# Security Policy

## Reporting a vulnerability

**Please do not open a public GitHub issue for security vulnerabilities.**

Use GitHub's private vulnerability reporting instead: on the [Security tab](https://github.com/kellylford/QuickMail/security) of this repository, choose **Report a vulnerability**. If you are unable to use that, send a private message to [@kellylford](https://github.com/kellylford) on GitHub.

Include as much detail as you can:

- A description of the vulnerability and what an attacker could do with it
- Steps to reproduce, or a minimal proof of concept
- The version of QuickMail you tested against
- Your operating system and Windows version

You will receive an acknowledgement within a few days. If the report is confirmed, a fix will be prepared and released as quickly as possible, and you will be credited in the release notes unless you prefer otherwise.

## Scope

QuickMail is a local desktop application. Areas most likely to be relevant:

- **HTML rendering** — email bodies are rendered in a sandboxed WebView2 component with a strict Content Security Policy. Bypasses that allow script execution or data exfiltration from a crafted email are in scope.
- **Credential handling** — passwords are stored in Windows Credential Manager and OAuth2 tokens are encrypted with Windows DPAPI. Any path that exposes credentials in plaintext is in scope.
- **IMAP/SMTP communication** — the app connects to mail servers over TLS. Anything that downgrades or bypasses transport security is in scope.

Reports about email servers, third-party dependencies, or Windows itself are generally out of scope unless QuickMail's code makes the exposure worse.

# QuickMail v0.8.41 Release Notes

## Download

There are four downloads. Take a regular one unless you know your PC has an ARM processor — to check, open **Settings → System → About** and read **System type**.

| Download | When to use |
|----------|-------------|
| **`QuickMail-0.8.41-win.msi`** — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| **`QuickMail-0.8.41-win-arm64.msi`** — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| **`QuickMail.exe`** — standalone portable executable | No installation required. Copy it anywhere and run. |
| **`QuickMail-arm64.exe`** — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 8 runtime — you do not need to install .NET separately.

---

## Changed: hearing "unread" without also hearing "read"

Arrowing through the message list used to say **read** on every message you had already read. Plenty
of people want to know which messages are unread and have no use at all for being told, once per row,
about the ones they have finished with — and there was no way to switch the one off and keep the
other.

The reason was buried in **View → Message List Fields**. The field doing the talking was called
**Status (combined)**, and it says exactly one word per message — *replied*, *forwarded*, *unread* or
*read*, whichever fits first. Being a single word, it has no "only when true" choice, so *read* came
with *unread* whether you wanted it or not. There was a separate **Unread** field that does have that
choice, but it arrived switched off — so anyone hunting for the unread setting found a switched-off
box named Unread while the list was plainly saying "unread", switched it off and on again, and heard
nothing change.

QuickMail now uses **Unread**, **Replied** and **Forwarded** as three separate fields, each set to
speak only when it applies. In practice:

- An unread message says **unread**, in the same place in the row as before.
- A message you have read says **nothing at all** about read state.
- A message that is unread *and* replied to now says **both**. The old single word could only ever
  say *replied*, so the fact that it was still unread went unsaid.

The combined field is still there, renamed **Read status (combined)** so its name mentions the thing
it speaks. If you preferred the one-word version, switch it back on and switch the three separate
fields off.

### If you have opened Message List Fields before, please read this

**Your existing setup is kept exactly as it is**, which means this change does not reach you
automatically — QuickMail will not overwrite choices you have made. If you have ever opened
**View → Message List Fields**, even just to look, your arrangement was saved at that moment.

To pick up the new behaviour: open **View → Message List Fields**, leave **Row type** on **Messages**,
and press **Reset to Defaults**. That is the whole job. If you had arranged other fields the way you
like them, note that Reset restores everything on that row type, so you may want to set those back
afterwards — or simply switch **Read status (combined)** off and **Unread** on by hand, which gets you
the same result without disturbing anything else.

([#558](https://github.com/kellylford/QuickMail/issues/558))

## Fixed: "speak only when true" did nothing on a field that was switched off

In **View → Message List Fields**, the **Speak only when true** and **Always speak** choices stayed
available for a field you had just switched off — and setting them did nothing whatever, because a
field that is off is not spoken at any time. Following the obvious path of switching a field off and
then choosing when it should speak left you with a setting that looked applied and had no effect.

Those two choices are now unavailable while the field is switched off, and the field's description
says why: *Turn this field on to choose when it is spoken.* ([#558](https://github.com/kellylford/QuickMail/issues/558))

## Fixed: the explanations in Message List Fields were announced first line only

The **Spoken preview** and the description of the selected field are boxes you can move through and
copy from, but they had no cursor to move — so screen readers reached the first line and no further.
Both now behave like any other read-only box in QuickMail: arrow through them a line at a time, select
and copy as usual.

The same gap was fixed in four other places with the same kind of box: the raw message headers under
**Message → Properties**, the context line in the spell check window, the shared-mailbox note in
Manage Accounts, and the error line in the appointment editor.
([#558](https://github.com/kellylford/QuickMail/issues/558))

## Fixed: a field description was sometimes skipped

Still in **View → Message List Fields**: when two fields in a row carried the same description —
"Turn this field on to choose when it is spoken" applies to every switched-off field of that kind —
the description was announced for the first and silently skipped for the second. Arrowing from
**Mailing list** to **Watched** explained one and said nothing about the other. Every field now
announces its own description.

---

## Reporting Issues

Found a problem or have a suggestion? There are three ways to reach us — pick the one that fits:

1. **Report a Bug → Send** (Help menu, inside QuickMail). Files the report for you anonymously — it includes no email address or other identifying information, so there is no way to follow up with you. **Best when you don't want any follow-up.**
2. **Report a Bug → Copy report and open GitHub** (Help menu). Opens a pre-filled issue that you submit under your own GitHub account, so your GitHub contact information is attached. **Best when you have a GitHub account and want automatic filing plus direct contact.**
3. **Email** [quickmailissues@theideaplace.net](mailto:quickmailissues@theideaplace.net). **Best when you don't mind sending email and want a personal follow-up.**

Full details, including exactly what a report contains (and what it never contains), are in the [Reporting Issues section of the User Guide](https://kellylford.github.io/QuickMail/reporting-issues.html).

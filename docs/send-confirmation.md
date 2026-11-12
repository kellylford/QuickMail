# Send confirmation

The optional **Confirm before sending a message** preference lives under **Settings → General → Composing**. It defaults off, applies to compose windows opened afterwards, persists in `config.ini`, and travels with exported preferences.

When enabled, every compose Send action asks before sending or queuing the message. The native Yes/No/Cancel dialog defaults to No. No, Cancel, and Escape leave the compose open and its contents intact. An accepted offline message is confirmed when queued, with no second prompt when the Outbox sends it.

Send ignores a repeated invocation while a confirmation or send is running, while another save is busy, or after the compose has already been sent or queued. A rejected send can be retried. These protections also apply when confirmation is disabled.

## Verification

Automated regression tests: `ComposeSendConfirmationTests` covers plain text, Markdown, HTML, cancellation, missing confirmation handler, nested send invocation during confirmation, repeated invocation during the network wait and after completion, retry after rejection, busy draft saving, offline queuing, and settings save/reload.

With JAWS or NVDA, check the native dialog and focus behavior on a test account:

1. Open Settings, General. Tab to **Confirm before sending a message**. Expect its checkbox name and checked state. Press Space to enable it, then save settings.
2. Open a new compose and enter a test recipient, subject, and body. Press Ctrl+Enter. Expect **Confirm Send**, its question, and the **No** button announced. Press Enter. Expect the compose to remain open with the draft intact and keyboard focus restored to the compose.
3. Invoke Send with Alt+S, the Send button, and the command palette. Each must ask once. Escape must cancel the dialog and leave the draft intact.
4. Repeat in Plain Text, Markdown, and HTML. Choose Yes for one test message; verify one received message after pressing Ctrl+Enter rapidly twice.
5. Disable the preference and open a fresh compose. Send must proceed without confirmation, and rapid repeated Ctrl+Enter must still produce one message.
6. Disconnect the test account and enable the preference again. A fresh compose must ask before queuing. No must leave the Outbox unchanged; Yes must queue one message. Reconnect and expect one delivery with no second confirmation.

Actual screen reader speech and receipt through a customer's Titan account require a person and an available mailbox; automated checks do not establish those results.

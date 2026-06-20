# QuickMail v0.7.5 Release Notes

## Download

Two options are available for v0.7.5:

| Download | When to use |
|----------|-------------|
| **`quickmail-v0.7.5-setup.exe`** — Windows installer | Recommended for most users. Installs per-user with no elevation required, checks for the WebView2 Runtime, and registers an uninstaller. |
| **`QuickMail.exe`** — standalone portable executable | No installation required. Copy it anywhere and run. |

Both downloads include the .NET 8 runtime — you do not need to install .NET separately.

---

## New Features

### iCloud Email Support

You can now add an iCloud email account using your iCloud address and an Apple app-specific password.

**Adding an iCloud account:** Open the account manager (**Settings → Accounts**) and choose **Add Account**. Enter an account name, a sender display name, and your iCloud address in the **Email / Username** field. When QuickMail sees an `@icloud.com`, `@me.com`, or `@mac.com` address, it automatically fills in Apple's IMAP and SMTP server settings — you do not need to enter them yourself.

**App-specific password required:** Apple does not allow third-party apps to sign in with your Apple ID password. You must generate an app-specific password at **appleid.apple.com** under Sign-In & Security → App-Specific Passwords. QuickMail shows a reminder about this in the password field when it detects an iCloud address. Enter the app-specific password in the Password field; it is stored securely in Windows Credential Manager.

Once added, iCloud accounts sync and send mail the same way as any other IMAP/SMTP account.

---

## Bug Fixes

- **Shift+Tab navigation now wraps from the account list back to the message panel.** Pressing Shift+Tab moved focus backward through the message panel, folder tree, and account list, but pressing Shift+Tab again from the account list produced no response — focus was stuck. The backward cycle now completes correctly: Shift+Tab from the account list moves focus to the active message panel, consistent with how the forward Tab cycle works. (#106)

- **Keyboard shortcuts list now announces correctly with screen readers.** When arrowing through the keyboard shortcuts list in Settings, NVDA and Narrator were reading the internal class name (`SettingsViewModel+HotkeyRowViewModel`) instead of the command name. Each row now has a proper accessible label — command name, category, and current shortcut — that screen readers announce when focus moves to a row. This bug did not affect JAWS, which has a long-standing workaround for this specific WPF pattern. (#107)

- **Deleted iCloud messages going to Junk instead of Trash.** When deleting a message from an iCloud account, QuickMail was moving it to the Junk folder rather than Trash. The delete logic was falling back to Junk when it could not identify the Trash folder by name — iCloud names its trash folder "Deleted Messages" rather than "Trash". The name-based fallback now recognises "Deleted Messages", and the fallback to Junk has been removed from the delete path entirely (Junk is never an acceptable destination for deleted messages).

---

## Thank You to Contributors

Thank you to everyone who has contributed to QuickMail through code, bug reports, feature suggestions, and other feedback. Your contributions make the project better for everyone.

---

## Internal

### iCloud Support

- `AddAccountViewModel`: detects `@icloud.com`, `@me.com`, and `@mac.com` addresses in the `Username` `PropertyChanged` handler and auto-fills `imap.mail.me.com:993 (SSL)` and `smtp.mail.me.com:587 (STARTTLS)`. Auth type stays `Password`; no feature gate required. Restructured the Gmail auto-fill guard so the two domain-detection paths are independent.
- `AccountEditorViewModel`: `IsICloudAccount` computed property derived from `ImapHost == "imap.mail.me.com"`, with `[NotifyPropertyChangedFor]` on `_imapHost` so it updates when the host changes. Available in both Add and Edit dialogs.
- `AddAccountDialog` / `AccountManagerDialog`: orange hint panel inside the password `StackPanel`, bound to `IsICloudAccount`, directing users to generate an app-specific password at `appleid.apple.com`.
- `AccountPropertiesBuilder`: iCloud accounts display "App-Specific Password (iCloud)" in the Authentication row instead of the generic "Password (Windows Credential Manager)".
- `PropertiesBuilderTests`: new `Build_ICloudAccount_ShowsAppSpecificPassword` test.

### Delete to Trash

- `ImapMailService.MoveToTrashAsync` / `MoveToTrashBatchAsync`: removed `SpecialFolder.Junk` from the `FindSpecialFolderAsync` candidate list. The existing `else` branch (set `\Deleted` flag) is the correct fallback when no Trash folder can be found; routing deleted mail to Junk was wrong.
- `FindSpecialFolderAsync` name-based fallback: added `"Deleted Messages"` as an alias for `SpecialFolder.Trash` to cover servers (including iCloud) that use that name rather than `"Trash"`. The attribute-based lookup (`client.GetFolder(SpecialFolder.Trash)`) remains the primary path and handles RFC 6154-compliant servers without the name fallback.
- `EmptyTrashAsync` and `CountTrashMessagesAsync` retain `SpecialFolder.Junk` as a secondary candidate — those operations intentionally treat Junk as part of the trash concept.

---

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

- **Screen readers now announce the correct title for compose and message windows.** When a compose or message window was open and you used a screen reader's "report window title" command, it announced the main application title ("QuickMail — All Mail") instead of the title of the open window ("Reply to Kelly — HTML — QuickMail", for example). All screen readers now correctly identify compose and message windows by their own titles.

- **Alt shortcuts in the compose window no longer also activate the menu bar.** Pressing Alt+U (Subject field), Alt+M (From account), or Alt+Y (message body) moved focus to the right place but also sometimes activated the menu bar, pulling focus away from where you had just navigated. This no longer happens.

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

### Screen Reader / Compose Window Fixes

- `ComposeWindow.xaml` / `MessageWindow.xaml`: added `AutomationProperties.Name="{Binding WindowTitle}"` so the UIA `Name` property on the window element tracks the window-specific title. WPF's `WindowAutomationPeer.GetNameCore()` does not reliably propagate `Window.Title` to UIA in .NET 8, so screen readers that read the UIA tree (rather than the HWND title) were falling back to the application name.
- `MainWindow.xaml.cs`: removed `Owner = this` from both `ComposeWindow` creation sites. WPF places owned windows as descendants of the owner in the UIA element tree; screen readers that walk the tree to the topmost `Window` ancestor reported the main window title rather than the compose window title. Without an owner, the compose window is a peer window in the UIA tree and its own title is reported correctly. `ComposeWindow.xaml` startup location changed from `CenterOwner` to `CenterScreen`. A `_openComposeWindows` tracking list and `OnClosed` override were added to explicitly close any open compose windows on main window exit — WPF auto-closes owned windows but not independent ones.
- `ComposeWindow.xaml.cs`: added a Win32 `WM_SYSCOMMAND SC_KEYMENU` hook (registered in `OnSourceInitialized` via `HwndSource.AddHook`). WPF's `AccessKeyManager` posts `SC_KEYMENU` after every Alt key press regardless of `e.Handled = true` in `PreviewKeyDown`. The hook intercepts the message and discards it when `_suppressNextMenuActivation` is set; that flag is set by the Alt+U, Alt+M, and Alt+Y handlers and by the registry command dispatcher when an Alt-modified command fires.

---

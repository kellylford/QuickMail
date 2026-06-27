# QuickMail v0.7.8 Release Notes

## Download

Two options are available for v0.7.8:

| Download | When to use |
|----------|-------------|
| **`quickmail-v0.7.8-setup.exe`** — Windows installer | Recommended for most users. Installs per-user with no elevation required, checks for the WebView2 Runtime, and registers an uninstaller. |
| **`QuickMail.exe`** — standalone portable executable | No installation required. Copy it anywhere and run. |

Both downloads include the .NET 8 runtime — you do not need to install .NET separately.

---

## Bug Fixes

- **Attachment list now announces correctly with screen readers.** When arrowing through the attachment list in a compose window or reading pane, NVDA and Narrator were reading the internal class name (`QuickMail.Models.AttachmentModel`) instead of the file name. Each attachment now has a proper accessible label — file name and size — that screen readers announce when focus moves to it. This bug did not affect JAWS, which has a long-standing workaround for this specific WPF pattern.

---

## Thank You to Contributors

Thank you to everyone who has contributed to QuickMail through code, bug reports, feature suggestions, and other feedback. Your contributions make the project better for everyone.

---

## Internal

### Attachment list accessible names

- `AttachmentModel`: added `AccessibleName` property returning `"{FileName}, {FileSizeDisplay}"` (e.g. "report.pdf, 152 KB").
- `MainWindow.xaml`, `ComposeWindow.xaml`, `MessageWindow.xaml`: added `ListBox.ItemContainerStyle` to each attachment list binding `AutomationProperties.Name` to `{Binding AccessibleName}`. Without this, WPF's automation peer fell back to `AttachmentModel.ToString()` — the full type name — as the accessible name of each list item. Same root cause and fix as the keyboard shortcuts list in v0.7.5 (#107).

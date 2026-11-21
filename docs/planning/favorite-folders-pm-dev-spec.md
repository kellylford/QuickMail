# Favorite folders: a Favorites section in the folder tree (#1056)

Status: approved design, not yet implemented. Citations are to `main` at 0930684 (0.8.75).

## Summary

The folder tree gets a **Favorites** section: a header node like **Views**, holding folders the user picks from any account, in the order they were added. A favorite node holds the *same* `MailFolderModel` object as the folder's node under its account, so Enter on it runs the same `SelectFolderCommand`, its unread count is in its accessible name, and the existing count refresh keeps it current.

One command, `folder.toggleFavorite`, default **Ctrl+D**, adds the folder if it is not a favorite and removes it if it is. It is on the folder context menu, the Folder menu and in the Command Palette.

Favorites are stored on each account, the way Hide Folder stores hidden folders, with the time each was added. The section lists them across accounts by that time. They therefore go with the account through settings export/import and disappear when the account is removed.

## Decisions made by Kelly

- **Key:** Ctrl+D. Nothing in QuickMail binds it, and it is the browsers' "add favorite/bookmark" key. It is Delete in Outlook. An Outlook habit would favorite the folder, but QuickMail says so, and a second press undoes it.
- **Order:** strictly the order added, across all accounts.
- **Account name:** every favorite shows its account name whenever more than one account exists, in the form `Folder - Account` (for example, "GitHub - Kelly").

## Decisions

**D1. Follows the Views-section pattern; hidden when empty.** Views is a header node `{ IsHeader = true, Label = "Views", IsExpanded = true }` added in `BuildFolderTree` only when `SavedViews.Count > 0` (MainViewModel.cs:5516-5560). Favorites is built the same way, and is added only when at least one favorite resolves to a loaded folder. An empty heading would be a dead end in the tree.

**D2. Position: after Calendar (when Calendar is at the top), before Views.** Roots today: Calendar-at-top (5513), Views (5559), All Mail (5579), accounts (5581-5615), Calendar-at-end (5619). "Calendar at Top of Folder List" is the user's explicit choice, so Favorites does not go above it.

**D3. A favorite node is a leaf sharing the real folder object.** It is `new FolderTreeNode { Folder = <live MailFolderModel from _cachedFolders>, Label = <label> }`, the shape Views uses for multi-folder views (5549-5554).
- Enter in the tree runs `_vm.SelectFolderCommand.ExecuteAsync(node.Folder)` (MainWindow.xaml.cs:2783-2789), so opening a favorite is identical to opening the folder in its account. Per-folder view state is keyed by account and FullName (IFolderViewStateService.cs:16-22), so the remembered sort and filter come back too.
- `ApplyFolderCounts` changes cached folders in place, then calls `NotifyUnreadChanged()` on every tree node of that account (9708-9728). The favorite's `AutomationName` (`"{Label}, {n} unread"`, FolderTreeNode.cs:52-56) therefore refreshes with no new code, and the count stays in the accessible Name.
- Subfolders stay under the account; the favorite has no children.

**D4. Label and accessible name.**
- With one account, the label is `folder.DisplayName`, as in the account branch (FolderTreeBuilder.cs:205).
- With two or more accounts, it is `"{DisplayName} - {AccountLabel}"`, for example "GitHub - Kelly".
- AutomationName reads "GitHub - Kelly, 3 unread", with no change to FolderTreeNode. The header's name is "Favorites".
- The name sits on the container through the existing setter (MainWindow.xaml:1255).

**D5. Only real mail folders of a known account can be favorites.** The refusals are `HideFolderRefusal`'s (MainViewModel.HiddenFolders.cs:45-64) minus its Inbox rule; the Inbox *can* be a favorite. Refused, with the reason said:
- null
- calendar nodes
- account headers
- other headers and path segments (`Folder == null`), including the Favorites, Views and All Mail headers
- virtual folders (FullName starting `\0`: All Inboxes, All Mail, Outbox, saved views, each account's All Mail)
- folders of an unknown account

**D6. Persistence: `List<FavoriteFolder> FavoriteFolders` on `AccountModel`, next to `HiddenFolders` (AccountModel.cs:218-226).**
- `FavoriteFolder` is `{ string FullName; DateTime AddedUtc }`. The FullName is compared ordinally, as for HiddenFolders, because Graph ids are case-sensitive. `AddedUtc` is what gives the cross-account order (D7).
- Each toggle saves with `_accountService.SaveAccounts([.. Accounts])` then `BuildFolderTree()`, as `HideFolder` does (HiddenFolders.cs:86-87).
- Account removal drops the account's favorites with it.
- Export/import carries them in accounts.json (the Accounts category, SettingsBundle.cs:48). The importer replaces a matched account's whole model (SettingsImporter.cs:444-470).
- A missing property loads as an empty list.
- `SettingsConfigTransfer` needs no change, because it only classifies config.ini keys.

**D7. Order: strictly the order added, across accounts.**
- `BuildFavoritesGroup` collects every account's favorites that resolve to a loaded folder and sorts them by `AddedUtc`, using account-list order and then list position to break ties.
- Re-adding a removed folder puts it at the end.
- Moving favorites up and down is out of scope.

**D8. Stale entries.**
- An entry with no matching folder in `_cachedFolders` is skipped and kept, because a folder can be missing for a while (an account connecting, Graph being eventually consistent).
- `ForgetFavoriteFolders(accountId, fullName)` drops the folder and everything under it on in-app Move Folder and Delete Folder. It mirrors `ForgetHiddenFolders` (HiddenFolders.cs:131-139; called at MainViewModel.cs:9990 and 10075).
- Delete splices only one node out (10073-10074), so when anything was forgotten the delete path also calls `BuildFolderTree()`.
- A folder renamed on the server leaves an ignored entry.
- IMAP-to-Graph conversion and import healing rewrite FullNames to Graph ids. Favorites go through `FolderReferenceRemapper` beside `hiddenFolders` (FolderReferenceRemapper.cs:60-66, 171-187; ImportFolderHeal.cs:109-121). Each entry keeps its `AddedUtc`, and unmatched entries are dropped.

**D9. Hide Folder.**
- A hidden folder stays in Favorites, the way a saved view keeps it (HiddenFolders.cs:95-96).
- `HideFolder` appends " It still shows in Favorites." when the folder is a favorite.

**D10. Other folder actions on a favorite act on the real folder.** That covers Move, Copy, Paste Messages, Delete Folder (with its confirmation) and Set as Startup Folder. **Delete deletes the folder; it does not remove the favorite.**

**D11. The command.** It is registered in `MainWindow.xaml.cs` `OnLoaded` beside `folder.hide` (1434-1447).
- id `folder.toggleFavorite`; category `Mail`; title "Toggle Favorite Folder"; description "Add the folder to Favorites, or remove it from Favorites".
- Always available; it says why when it cannot act.
- Target in the folder tree (the keyboard-or-logical-focus test from `HideSelectedFolder`, 7373-7375): `FolderList.SelectedItem`.
- Target elsewhere: the tree node for `_vm.SelectedFolder`, the folder whose messages are on screen.

**D12. Menu items are checkable**, like Watch Conversation (MainWindow.xaml:550-554) and Plain Text View (673-677).
- Header `Fa_vorite Folder`, `IsCheckable="True"`, `sc:CommandShortcut.Id="folder.toggleFavorite"`, `AutomationProperties.Name="Favorite Folder"`.
- One goes in `FolderContextMenu` (before the Hide group, 347) and one in the Folder menu (before 657). V is free in both.
- `IsChecked` is set in the item's own `Loaded` handler, which runs each time its menu opens. The platform then reports whether the folder is a favorite, with no custom announcement.
- The context menu resolves its node through `GetContextMenuFolderNode(sender)` (7157-7162).

**D13. Announcements.**
- Outcome and refusals go through the existing `Report(message)`: status bar plus `AnnouncementCategory.Result` (7250-7256), as for Hide Folder.
- Messages: `"{label} added to Favorites."` / `"{label} removed from Favorites."`.
- The change happens elsewhere in the tree or after a menu closes, so the platform does not report it.
- No Hint and no Status announcements.

**D14. Key: Ctrl+D.**
- No Ctrl+D default or override exists in the app; the only `Key.D` default is unmodified D in the calendar (MainWindow.xaml.cs:1845).
- Ctrl+D is not a WPF text-editing gesture.
- Folder-tree type-ahead runs only with no modifiers.
- A user's own binding on Ctrl+D wins, since overrides beat defaults (CommandRegistry.cs:59-75).

**D15. Focus when removing a favorite while focused in Favorites.** This is the Hide Folder rule (`HideFolderWithFocus`, 7381-7410).
- Focus lands on the item above: the previous favorite, or the heading if it was the first.
- If it was the only favorite, the heading goes too, and focus lands on the nearest surviving item above, or else the first item in the tree.
- Adding, or removing from the account copy, leaves focus where it is (the merge keeps the focused container, 5627-5638).

**D16. Returning to the tree lands on the copy you left.**
- Today `SyncFolderTreeSelection` (2849-2874) picks the *first* node matching the open folder (TreeViewFocusHelper.cs:125-160). With Favorites above the accounts, F6 back from a folder opened in its account branch would land on the Favorites copy.
- Fix: if `FolderList.SelectedItem` already matches `_vm.SelectedFolder` and is still in the tree, keep it. Put the check in a static, testable `TreeViewFocusHelper` method.
- This also fixes the same latent issue for Views' copies.

**D17. No change to the F6 ring, type-ahead or Selector tests.** Favorites lives inside `FolderList`, which is already in the ring. Folder-tree type-ahead matches visible nodes by `Label` (2829-2837). There is no new Selector item type.

**D18. Startup.** `InitialLoadAsync` builds the tree from the local store's folder cache (3388-3404), so favorites show before the network comes up. In `--online` mode they appear as each account's folders arrive.

## Keyboard walkthrough

The app controls only each item's Name and the quoted announcements. What the screen reader adds is its own.

### A. Add with the key, from the account branch
1. Press Ctrl+2. Focus is in the folder tree. Arrow to Lists under Work. The screen reader reads the item name, "Lists, 4 unread".
2. Press Ctrl+D. The status bar shows, and the app announces (Result), "Lists - Work added to Favorites." Focus stays on Lists under Work. A Favorites heading appears at the top of the tree (after Calendar, if Calendar is at the top), holding "Lists - Work".

### B. Add from the message list
1. With Notes in Home open and focus in the message list, press Ctrl+D. "Notes - Home added to Favorites." is announced. Focus stays in the message list.

### C. Add from the context menu, the Folder menu or the palette
1. On Inbox under Home, press Shift+F10 or the Applications key. The folder menu opens.
2. Arrow to "Favorite Folder", or press V. The screen reader reads the item, its not-checked state and Ctrl+D.
3. Press Enter. The menu closes and focus returns to Inbox under Home. "Inbox - Home added to Favorites." is announced.
4. Folder menu: Alt, D, V. It acts on the tree selection when you were in the tree, otherwise on the open folder, and is checked when that folder is a favorite.
5. Palette: Ctrl+Shift+P, type "favorite", press Enter on "Toggle Favorite Folder". The target rule and announcement are the same, and focus returns to where the palette was opened.

### D. Open a favorite
1. Press Ctrl+2 and arrow to "Lists - Work, 4 unread" under Favorites. Press Enter.
2. The folder opens exactly as it does from Work, with its remembered sort and filter, and focus moves to the message list.
3. After reading a message, the count refreshes on both copies: "Lists - Work, 3 unread".
4. F6 back to the tree lands on Lists under Favorites, where you left from.

### E. Remove
1. Inside Favorites, on the second favorite, press Ctrl+D. "Inbox - Work removed from Favorites." is announced, and focus moves to the favorite above.
2. On the first favorite: focus moves to the Favorites heading.
3. On the only favorite: the heading disappears. Focus lands on Calendar if Calendar is at the top; otherwise on the first item in the tree (Views if there are saved views, else All Mail).
4. From the account copy: focus stays put, and the Favorites copy disappears.
5. In the context menu on a favorite, "Favorite Folder" is reported checked. Enter removes it, with the same focus rules.

### F. Empty state and refusals
1. With no favorites there is no Favorites heading; the tree starts as it does today.
2. Ctrl+D on All Inboxes: "'All Inboxes' is a view, not a folder, so it cannot be a favorite." Nothing changes.
3. On an account node: "'Work' is an account, not a folder, so it cannot be a favorite."
4. On the Favorites heading or a path segment: "'Favorites' only groups the folders inside it, so it cannot be a favorite."
5. A favorite deleted on the server stops showing. If it was the only one, the heading goes too.

## Infrastructure changes

- **F6 ring:** no change.
- **CommandRegistry:** `folder.toggleFavorite`, category Mail, title "Toggle Favorite Folder", default **Ctrl+D**, always available, registered in `MainWindow.OnLoaded`.
- **AutomationProperties.Name:** the header is "Favorites"; each favorite is `"{DisplayName}[ - {AccountLabel}][, N unread]"` through the existing `AutomationName`; both menu items are "Favorite Folder".
- **Announce:** toggle outcomes and refusals, as `Result` through `Report()`. No Hint or Status.
- **VM:** no new bindable property. New members: `IsFavoriteFolder`, `FavoriteFolderRefusal`, `ToggleFavoriteFolder`, `ForgetFavoriteFolders`, `IsInFavoritesGroup`, `BuildFavoritesGroup`. New model: `FavoriteFolder` and `AccountModel.FavoriteFolders`.
- **Shared tree code:** `SyncFolderTreeSelection` prefers the current selection when it already shows the open folder (D16).
- **`FolderReferenceRemapper.Remap`:** an optional `favoriteFolders` parameter plus `Report.FavoriteFoldersChanged`, left out of `AnythingChanged` and `Summary()`, as for hidden folders.

## Data

`accounts.json`, per account:
```json
"HiddenFolders": ["RSS Feeds"],
"FavoriteFolders": [
  { "FullName": "INBOX", "AddedUtc": "2026-11-21T15:02:11Z" },
  { "FullName": "Lists/Dev", "AddedUtc": "2026-11-21T15:04:40Z" }
]
```

## Implementation steps

1. `Models/AccountModel.cs`: add the `FavoriteFolder` record/class and `public List<FavoriteFolder> FavoriteFolders { get; set; } = [];` after `HiddenFolders`.
2. New `ViewModels/MainViewModel.Favorites.cs`, modelled on `MainViewModel.HiddenFolders.cs`:
   - `FavoriteFolderRefusal`: optionally share the "not a folder" checks with `HideFolderRefusal` through a helper that takes the verb phrase.
   - `IsFavoriteFolder`.
   - `ToggleFavoriteFolder`: returns the sentence, saves, rebuilds.
   - `ForgetFavoriteFolders`: drops the path and anything continuing with `/` or `.`.
   - `IsInFavoritesGroup`: generalize `IsInViewsGroup`, HiddenFolders.cs:142-152.
   - `BuildFavoritesGroup`: sort by `AddedUtc`; labels per D4.
3. `MainViewModel.BuildFolderTree`: add `if (BuildFavoritesGroup() is { } fav) roots.Add(fav);` just before the Views block (5516).
4. `MoveFolderToAsync` (9990) and `DeleteFolderAsync` (10075): call `ForgetFavoriteFolders` beside `ForgetHiddenFolders`. In delete, call `BuildFolderTree()` when it returns true.
5. `HideFolder`: add the "still shows in Favorites" clause.
6. `FolderReferenceRemapper.cs`, `ImportFolderHeal.cs`, `MainViewModel.cs:2142-2165`, `MainViewModel.SettingsImport.cs:100-111`: carry favorites through as hidden folders are carried, keeping `AddedUtc`.
7. `Views/TreeViewFocusHelper.cs` plus `SyncFolderTreeSelection`: the D16 preference.
8. `MainWindow.xaml.cs`: register the command; add `ToggleFavoriteFromSelection()` (target rule D11), `ToggleFavoriteWithFocus` (D15), and the click handlers plus `FavoriteMenuItem_Loaded`.
9. `MainWindow.xaml`: two menu items (D12); update the access-key comments.
10. Tests, then docs.

## Tests

New `QuickMail.Tests/FavoriteFoldersTests.cs`, using the `HiddenFoldersTests` fixture (`MakeVmWithAsync`, `FolderedMailService`, `RecordingAccountService`, `NodeFor`):

- **Adding and removing:**
  - toggle adds: the entry is saved on the account, the header exists, and the favorite's `Folder` is `ReferenceEquals` the account node's folder
  - a second toggle removes it, and the header goes when empty
  - no favorites, no header
  - toggling on the favorite node removes it
  - toggling on a Views copy acts on the folder
- **Order:**
  - Favorites sits before Views and All Mail
  - favorites are sorted by `AddedUtc` across accounts, not grouped by account
  - re-adding puts the folder last
- **Labels:**
  - one account: "Inbox"
  - two accounts: "Inbox - Work" on every favorite
  - `AutomationName` carries "N unread"
- **What can be a favorite:**
  - refusal texts for: null, account header, Favorites header, All Mail header, All Inboxes, an account's All Mail, Outbox, a calendar node, a path segment
  - the Inbox is accepted
- **Stale entries:**
  - an entry with no cached folder is hidden and kept
  - `ForgetFavoriteFolders` drops the path and its children, but not siblings with a shared prefix ("Archives", "Archive2")
- **Persistence:** round trip through accounts.json, and a missing property loads as empty.
- **Hide:** hiding a favorite keeps it in Favorites, and the sentence says so.
- **Remapper and import heal:** favorites are remapped or dropped, `AddedUtc` is kept, and `FavoriteFoldersChanged` is set.
- **Tree stability:** the toggle keeps other node objects, so focus survives.
- **Focus helper:** the D16 method prefers the current selection and falls back to the first match.
- **Source tests:**
  - registered as `folder.toggleFavorite` with `Key.D` and Control
  - present on both menus with the right handlers
  - the palette ranks it first for "favorite", "add to favorites" and "remove favorite"

**Covered automatically:** `KeyboardShortcutsDocTests` (needs the doc row), `ShortcutExposureTests`, `XamlParseTests`.

## Docs

- `docs/KEYBOARD-SHORTCUTS.md`: a row after `folder.showHidden`: `| Ctrl+D | folder.toggleFavorite | Toggle Favorite Folder … (#1056) |`.
- `docs/USER-GUIDE.md`:
  - a "Favorite Folders" section before "Hiding Folders", covering: the three ways in plus Ctrl+D; where Favorites sits; that it opens the real folder with its count; the account name; the order; what cannot be a favorite; that Delete deletes the real folder; how it combines with Hide; that favorites go with the account on export
  - Ctrl+D in the main window shortcut table
- The next version's release notes, under Changed.

## Out of scope

- Reordering favorites, or renaming a favorite.
- Virtual folders, saved views, accounts or calendars as favorites.
- Showing a favorite's subfolders under it.
- A favorite following its folder through a move or a server-side rename.
- Favorites in Go to Folder or in destination pickers.
- Remembering whether the Favorites heading is collapsed across restarts (session-only, like Views).
- Ctrl+D from inside the reading-pane body (the WebView2 relays only a fixed set of keys, MainWindow.xaml.cs:4166-4240), or in a separate message window.
- "Remove all favorites", drag and drop, and syncing with server-side or Outlook favorites.
- Delete or Backspace removing a favorite. Delete deletes the real folder, after confirmation.

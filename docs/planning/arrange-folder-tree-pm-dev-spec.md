# Arranging the folder tree: accounts, sections and favorites (#1058)

Status: approved design, not yet implemented. Builds on #1056 (favorite folders, `docs/planning/favorite-folders-pm-dev-spec.md`), which is also not implemented yet. Citations are to `main` at 0930684 (0.8.75).

## Summary

You can move three kinds of thing in the folder tree:
- **Each account**, for example kelly@kellford.com.
- **Each section:** Calendar, Favorites, Views and All Mail.
- **Each favorite**, within Favorites.

**One rule for the top level.** Accounts and sections are all items in one list at the top of the tree. Moving one up puts it above whatever is directly above it, account or section. An account can sit between Calendar and Favorites, or below All Mail, wherever you put it.

**The keys and commands.** The keys are the ones the Accounts list already uses: **Alt+Up**, **Alt+Down**, **Alt+Home** and **Alt+End**, through its four existing commands. These now work in the folder tree too, on whatever is focused. The same moves are on a new **Reorder** submenu of the folder tree's context menu and in the Command Palette. Each move:
- says where the item landed;
- keeps focus on what moved;
- is saved straight away.

**Calendar** becomes just another section. The two "Calendar at Top / Bottom of Folder List" items are retired.

**Order elsewhere.** The order of the accounts among themselves is still the order used everywhere else: the Accounts list, the compose From list, and the calendar's account sources.

## Decisions made by Kelly

- **What moves:** each account, and each section (Calendar, Favorites, Views, All Mail). Accounts are not tied together in a block.
- **Context menu:** one **Reorder** submenu holding the four moves.
- **Calendar at Top / Bottom of Folder List:** retired. Calendar is just another section.

## Decisions

**D1. Keep the four command ids; widen where they work.** `account.moveUp`, `account.moveDown`, `account.moveToStart` and `account.moveToEnd` are registered today with `isAvailable: () => AccountList.IsKeyboardFocusWithin` (MainWindow.xaml.cs:1769-1791). All four call `MoveSelectedAccount(_vm.MoveAccountX)` (7515-7523).
- **Availability** becomes `AccountList.IsKeyboardFocusWithin || FolderList.IsKeyboardFocusWithin`.
- **Why keep the ids:** keys people have rebound in hotkeys.json keep working, the palette keeps one entry per move, and the existing tests stay valid.
- **Titles** become "Move Up", "Move Down", "Move to Start" and "Move to End", the names the account menu items already use (MainWindow.xaml:1155-1170).
- **Descriptions:** "Move the selected account, section or favorite …". The palette matches descriptions (CommandMatcher.cs:89-92), so "move account" still finds them.
- **Category** changes from `Account` to `View`.

**D2. Alt+Up, Alt+Down, Alt+Home and Alt+End are free in the folder tree.**
- The registry runs from the window's preview handler and reads `e.SystemKey` for Alt combinations (MainWindow.xaml.cs:704, 2328-2335, 2530-2541).
- The only other Alt+Up/Down defaults are `mail.previousUnread` and `mail.nextUnread` (1294-1306). They are scoped to the message area (3811), and `FindByGesture` picks the command available for the current focus (CommandRegistry.cs:90-104).
- No other Alt+Home or Alt+End default exists.
- Folder-tree type-ahead needs no modifiers (2931).
- `TabStripNavigation` ignores modified keys (TabStripNavigation.cs:82-89).
- The reading-pane body relays only Alt+letter (2124-2133).

**D3. What a move acts on.**
1. With the Accounts list focused, today's path runs unchanged.
2. With the folder tree focused, or logically focused (the `HideSelectedFolder` test, 7369-7377, which also covers a palette opened from the tree), the move acts on `FolderList.SelectedItem`.
3. Otherwise it acts on the account selected in the Accounts list, as today.

The context menu acts on the node it opened on. `GetContextMenuFolderNode` (7157-7162) must walk up through the submenu's parent `MenuItem`s.

**D4. What can move.** A pure VM function classifies the node:
- **Top-level item:** an account root (`AccountId` set, including unloaded-account placeholders, MainViewModel.cs:5602-5614), or a section root carrying a new init-only `FolderTreeNode.SectionKey` (`calendar`, `favorites`, `views`, `allmail`).
- **Favorite:** a child of the Favorites header.
- **Anything else is refused:** "'{label}' cannot be moved. Only accounts, sections and favorites can be moved."

**D5. One list at the top level.**
- **Today:** roots are built as Calendar-at-top (5513), Views (5516-5560), All Mail (5562-5579), the accounts (5581-5615) and Calendar-at-end (5619).
- **New:** `BuildFolderTree` builds each section root and each account root, then emits them in the stored order (D7).
- **Up / Down** swap the item with its visible neighbour above or below, whatever that is.
- **Move to Start / Move to End** go to the very top or bottom of the tree.
- **Moving an account past another account** also reorders the accounts in accounts.json, through the existing `MoveAccountUp` / `MoveAccountDown` logic (2312-2364), so the Accounts list and From list follow. Moving an account past a section changes only where it sits in the tree; its order among accounts is unchanged.

**D6. Hidden sections are skipped.** Favorites is hidden when empty (#1056 D1). Views is hidden with no saved views (5516). Calendar is absent without a calendar service (5459).
- Moves work on what you can see: up places the item just before its visible neighbour above, down just after its visible neighbour below.
- Hidden entries keep their places.
- One pure helper, `ListMove.Apply<T>(all, visible, item, move)`, serves the top level and Favorites. It returns: moved above or below a named neighbour, already at the start, already at the end, or nothing to reorder.

**D7. Stored order: `FolderTreeOrder` in config.ini.**
- The value lists section keys and one `account` placeholder per account: `FolderTreeOrder = calendar, favorites, views, allmail, account, account`.
- The placeholders are filled with the accounts in accounts.json order. config.ini holds no account ids, accounts.json stays the one record of account order, and settings import needs no id rewriting for it.
- **Moves.** An account moving past a section swaps that `account` token with the section's key. Accounts moving past each other reorder accounts.json, and the tokens stay put.
- **Normalizing on load**, in one static function `FolderTreeOrder.Normalize(tokens, accountCount)` in `Models/`:
  - Tokens are matched ignoring case.
  - Unknown tokens and duplicate section keys are dropped.
  - A missing section is inserted at its default neighbour.
  - Too few `account` tokens: the missing ones are added after the last existing one, or at the end if there are none. So a new account appears after the last account.
  - Too many: they are removed from the end.
- **Removing an account** removes the token at that account's position, in the account-removal path. Normalizing is the fallback.
- **Default:** `calendar, favorites, views, allmail` followed by one `account` per account, which is today's layout plus Favorites.

**D8. Migration from `CalendarAtEndOfFolderList`.**
- The old key is read at ConfigService.cs:421, stored at ConfigModel.cs:578-583 and loaded at MainViewModel.cs:1977.
- If only the old key is present, `calendar` goes at the start (off) or the end (on) of the default order.
- If both are present, `FolderTreeOrder` wins.
- The old key is no longer written, and `ConfigModel.CalendarAtEndOfFolderList` is removed.
- `SettingsConfigTransfer.Portable` (128) swaps to `FolderTreeOrder`, which `ConfigKeyClassificationTests` enforces.
- **Downgrade:** an older QuickMail run afterwards puts Calendar at the top.

**D9. Retire the two Calendar position items.**
- Remove `calendar.calendarAtTopOfFolderList` and `calendar.calendarAtBottomOfFolderList` (MainWindow.xaml.cs:1740-1754) and their menu items (MainWindow.xaml:418-425).
- Remove `SetCalendarPosition`, `ApplyCalendarPosition` and `SyncCalendarPositionChecks` (7446-7494), the VM's `CalendarAtEndOfFolderList` / `SetCalendarAtEndOfFolderList` (MainViewModel.cs:440-470), and their KEYBOARD-SHORTCUTS rows.
- Calendar moves with the same keys and Reorder submenu as every section. Its context menu gets the Reorder submenu.
- `CalendarFolderPositionTests` is replaced by the new tests.

**D10. Favorites get a user-arranged order** (changes #1056 D6/D7).
- `FavoriteFolder` gains `int? Order`.
- **Sort:** `Order` (missing sorts last), then `AddedUtc`, then account index, then list position.
- **Every add or move renumbers** all favorites 0..n-1 across accounts, stale entries included, then applies the change. A new favorite gets n, so it goes last. Then `SaveAccounts` and `BuildFolderTree()`.
- Favorites stay stored on their account, so they still leave with it and travel in accounts.json.
- A favorite moves within Favorites only.

**D11. What does not move**, refused with the D4 sentence:
- Folders inside an account. These are sorted by `FolderTreeBuilder`: Inbox, Drafts, Sent, Deleted and Junk first (FolderTreeBuilder.cs:210-224), then alphabetically, with each account's own All Mail first (MainViewModel.cs:5588-5596).
- Saved views inside Views, which keep views.json order. The View Manager has no reorder.
- All Mail's children.
- Calendar's children.
- Subfolders and path segments.

**D12. Focus stays on the moved item.**
- `BuildFolderTree` merges into the live tree and keeps nodes whose `MergeKey` matches (5627-5708), so the moved node is the same object afterwards.
- `live.Move` regenerates its container, so the View re-focuses the node with `FocusTreeItem(FolderList, node)` (2967-2978), as `ApplyCalendarPosition` does today (7478-7485).
- A gated real-keystroke test proves focus lands on the moved item.

**D13. Announcements.** All are Result, through `Report()` (status bar plus Announce, 7252-7256). There are no Hint or Status announcements. The platform does not report a reorder, because focus stays on the same item.
- **Top level:**
  - "Moved above Views." / "Moved below kelly@theideaplace.net." The neighbour is named by its tree label: a section name or an account name.
  - "Already at the top of the folder list." / "Already at the bottom of the folder list."
- **Favorites:**
  - "Moved above Inbox - Home."
  - "Already at the start of Favorites." / "Already at the end of Favorites."
  - "There is only one favorite, so there is nothing to reorder."
- **Accounts list:** keeps today's sentences ("Moved above Work.", "Already at the start of the account list.").
- **Refusal:** the D4 sentence.

**D14. Context menu: a Reorder submenu.**
- `Header="Re_order"`, `AutomationProperties.Name="Reorder"`. It goes in `FolderContextMenu` (MainWindow.xaml:315-377), which every non-calendar tree node uses, and in `CalendarContextMenu` (383-426).
- Its four items match the account menu: Move _Up, Move Do_wn, Move to Sta_rt, Move to E_nd, each with `sc:CommandShortcut.Id="account.move…"`.
- O is free in both menus, alongside #1056's V.
- The items are always enabled and say why when they cannot act.

**D15. Settings export and import.**
- `FolderTreeOrder` is a portable Preferences setting.
- An older exported file with `CalendarAtEndOfFolderList` and no `FolderTreeOrder` migrates as D8.
- A file mentioning neither keeps the local order (SettingsTransferTests.cs:876-893).
- Account order and favorites' `Order` travel in accounts.json.
- If the imported account count differs from the token count, normalizing reconciles it.

**D16. Startup.** The order is loaded in the constructor where `_calendarAtEndOfFolderList` is now (MainViewModel.cs:1977), before the first tree is built, so the arranged tree shows from the first paint.

## Keyboard walkthrough

Example tree: Calendar; Favorites (Inbox - Home, Lists - Work); Views; All Mail; kelly@kellford.com; kelly@theideaplace.net. The app controls only names and the quoted announcements.

### A. Move an account
1. Press Ctrl+2, then arrow to "kelly@theideaplace.net".
2. Press Alt+Up. "Moved above kelly@kellford.com." Focus stays on it. The Accounts list and the compose From list now list it first too.
3. Press Alt+Up. "Moved above All Mail." It now sits between Views and All Mail. Its place among the accounts is unchanged.
4. Press Alt+Home. "Moved above Calendar." It is now the first item in the tree.
5. Press Alt+Up. "Already at the top of the folder list."

### B. Move a section
1. Arrow to the All Mail heading and press Alt+Up. "Moved above Views."
2. Press Alt+End. "Moved below kelly@kellford.com." All Mail is now last.
3. Arrow to Calendar and press Alt+End. "Moved below All Mail." Calendar is now last.
4. Restart. The tree opens in the same order.

### C. Move a favorite
1. Arrow to "Lists - Work" under Favorites and press Alt+Up. "Moved above Inbox - Home."
2. Press Alt+Up. "Already at the start of Favorites."
3. With one favorite, Alt+Down: "There is only one favorite, so there is nothing to reorder."

### D. Context menu and palette
1. On Views, press Shift+F10, then O (Reorder). The submenu lists Move Up, Move Down, Move to Start and Move to End, each with its key.
2. Press W (Move Down). The menu closes, focus returns to Views, and "Moved below All Mail." is announced.
3. On Calendar, the same submenu is in its menu.
4. In the tree, Ctrl+Shift+P, type "move down", press Enter. The same thing happens, and focus returns to the moved item.

### E. Refusals and other places
1. On "Lists", a folder inside an account, press Alt+Up. "'Lists' cannot be moved. Only accounts, sections and favorites can be moved." Focus stays.
2. With no saved views, Views is not in the tree and moves pass over it.
3. In the message list, Alt+Up is still Previous Unread Message. In a ComboBox, Alt+Down still opens it.
4. In the Accounts list (Ctrl+1), the keys behave exactly as today.

## Infrastructure changes

- **F6 ring:** no change.
- **CommandRegistry:**
  - The four `account.move*` commands get new titles, category `View`, descriptions, and the wider availability. Their default keys are unchanged.
  - `calendar.calendarAtTopOfFolderList` and `calendar.calendarAtBottomOfFolderList` are removed.
- **AutomationProperties.Name:** a "Reorder" submenu in two menus. Its items reuse the existing names.
- **Announce:** the D13 sentences, all Result.
- **VM:**
  - new `MoveFolderTreeNode(node, TreeMove)` and `FolderTreeOrder`;
  - `MoveFavorite` and `RenumberFavorites` in the #1056 partial;
  - the calendar-position members are removed.
- **Model:**
  - `FolderTreeNode.SectionKey`;
  - `ConfigModel.FolderTreeOrder` replaces `CalendarAtEndOfFolderList`;
  - `FavoriteFolder.Order`;
  - new `TreeMove` enum, `FolderTreeOrder` helper and `ListMove` helper.

## Changes to the #1056 spec

- **D2 (position):** Favorites' default place in `FolderTreeOrder` is after Calendar, before Views. Its root carries `SectionKey = "favorites"`.
- **D6/D7 (order):** add `Order`. The order is user-arranged, and a new favorite goes last. "Reordering favorites" leaves Out of scope.
- **D8:** the remapper and import heal keep `Order`.
- **If #1056 ships first,** entries without `Order` sort by `AddedUtc`, so nothing breaks.

## Implementation steps

1. `Models/FolderTreeOrder.cs` (new): keys, default, `Normalize`, `TreeMove`.
2. `Helpers/ListMove.cs` (new): the pure generic move.
3. `ConfigModel`, `ConfigService` (parse plus migration around 421, write in place of 1027-1029) and `SettingsConfigTransfer`.
4. `FolderTreeNode.SectionKey`.
5. New partial `MainViewModel.FolderTreeOrder.cs`: load at 1977, `MoveFolderTreeNode`, token swaps, and delegation to the account-move logic for account-past-account moves. Remove the calendar-position members.
6. `BuildFolderTree`: build the section and account roots into a map, then emit them in `FolderTreeOrder`, replacing the two `_calendarAtEndOfFolderList` branches.
7. Account removal: drop that account's `account` token.
8. #1056 partial: `Order`, the sort, `RenumberFavorites` and `MoveFavorite`.
9. `MainWindow.xaml.cs`: re-register the four commands, add `MoveSelected` and `MoveTreeNode`, add the Reorder click handlers, make `GetContextMenuFolderNode` walk up through submenus, and remove the calendar-position code.
10. `MainWindow.xaml`: add the Reorder submenus, remove the two Calendar items, and update the access-key comments.
11. Tests, then docs.

## Tests

- **`FolderTreeOrderTests` (pure):**
  - `Normalize`: empty gives the default; unknown and duplicate keys are dropped; a missing section is inserted; too few or too many `account` tokens are reconciled; case is ignored.
  - `ListMove`: all four moves, hidden entries keep their place, and the end and single-item results.
- **Config:**
  - `FolderTreeOrder` round-trips.
  - `CalendarAtEndOfFolderList = on` alone puts Calendar last; `off` gives the default.
  - With both keys, the new one wins.
  - The old key is not written.
- **`FolderTreeArrangeTests` (VM, `HiddenFoldersTests` fixture with a calendar service):**
  - Account past account: accounts.json is reordered, and the From list order follows.
  - Account past section: tokens swap and accounts.json is unchanged.
  - Section past an account, past a hidden section, and to the start and end.
  - The moved node is `ReferenceEquals` before and after.
  - The order is persisted.
  - Refusal sentences for the D11 cases.
  - Removing an account drops its token, and the others keep their places.
  - A new account goes after the last account.
- **Favorites (`FavoriteFoldersTests`):**
  - a move across accounts;
  - renumbering is saved;
  - stale entries keep their place;
  - a missing `Order` sorts by `AddedUtc`;
  - a new favorite goes last;
  - the end and only-one sentences.
- **`AccountOrderMenuTests` / `AccountOrderCommandRegistrationTests`:**
  - the new category, titles and availability;
  - both tree menus have a Reorder submenu carrying the four ids with unique access keys;
  - the Calendar items are gone.
- **`AccountOrderTests`:** unchanged, and must still pass.
- **`SettingsTransferTests`:** an old-format file migrates; favorites' `Order` survives export and import.
- **Context menu (STA, `[Collection("WpfTests")]`):** `GetContextMenuFolderNode` works from a submenu item.
- **Gated input test:** Alt+Up on a section leaves keyboard focus on it.
- **Covered automatically:** `KeyboardShortcutsDocTests`, `ShortcutExposureTests`, `ConfigKeyClassificationTests`, `XamlParseTests`, `FolderTreeMergeTests`.

## Docs

- **`docs/KEYBOARD-SHORTCUTS.md`:**
  - the four move rows get their new titles and "(Accounts list or folder tree)";
  - the two Calendar rows are removed;
  - the scope paragraph is updated.
- **`docs/USER-GUIDE.md`:**
  - "Changing the order of your accounts" becomes "Arranging the folder tree": what moves, the keys, Reorder, what stays fixed, that the order is remembered and travels with exported settings.
  - "Where Calendar sits in the folder list" points to it.
  - Add the keys to the main-window table.
  - Update the Favorites section on order.
- **Release notes:** the next version, under Changed. Say that the Calendar position items were replaced by moving Calendar like any section.

## Out of scope

- Moving folders inside an account, saved views inside Views, or the children of All Mail or Calendar.
- A Reorder item on the main Folder menu (the palette and context menu cover it).
- Drag and drop.
- "Reset to default order".
- Syncing order with a server or Outlook.
- Keeping an old QuickMail's Calendar position after a downgrade.

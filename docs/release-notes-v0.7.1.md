# QuickMail v0.7.1 Release Notes

## Download

Two options are available for v0.7.1:

| Download | When to use |
|----------|-------------|
| **`quickmail-v0.7.1-setup.exe`** — Windows installer | Recommended for most users. Installs per-user with no elevation required, checks for the WebView2 Runtime, and registers an uninstaller. |
| **`QuickMail.exe`** — standalone portable executable | No installation required. Copy it anywhere and run. |

Both downloads include the .NET 8 runtime — you do not need to install .NET separately.

---

## New Features

### Address Book Contacts management

The Address Book Contacts tab has been completely redesigned with explicit Add, Edit, and Delete workflows:

**Adding a contact:**
- Press **Add** or activate the button from the action buttons
- Screen reader announces: "Add contact"
- Name and Email fields become editable
- Type the contact's name and email address
- Press Enter or activate **Save** to add the contact
- The contact is added to the list and fields return to readonly

**Editing a contact:**
- Select a contact from the list
- Press **F2**, click **Edit**, or activate the Edit command (`contacts.edit`)
- Screen reader announces: "Edit {contact name}"
- Name and Email fields become editable
- Modify the contact's name or email address
- Press Enter or activate **Save** to save changes
- If the email address is already used by another contact, an error message appears and you can correct it
- On successful save, the contact list updates and fields return to readonly

**Deleting a contact:**
- Select a contact and press Delete or activate the Delete button
- A confirmation dialog asks you to confirm
- If confirmed, the contact is deleted from the address book

**Reading contact details (keyboard-only access):**
- Select a contact to populate the Name and Email display fields
- Press Tab to focus the Name field (readonly, but fully navigable)
- Use arrow keys to move through the text character-by-character (useful for email addresses)
- Your screen reader will read the text as you navigate
- Press Ctrl+A to select all text; Ctrl+C to copy
- Press Tab to move to the Email field or other controls

### Readonly field navigation

Name and Email display fields are fully keyboard-navigable even when readonly:
- Arrow keys move the cursor character-by-character
- Home / End jump to the start or end of the field
- Tab / Shift+Tab navigate to the next/previous control
- Ctrl+A selects all text
- Screen readers can read the content using their standard text access commands

---

## Accessibility

- **Address Book contact editing is now fully discoverable.** The previous implicit "edit-on-populate" behavior has been replaced with explicit **Add**, **Edit**, and **Save** buttons visible in the command palette and as keyboard commands (`contacts.add`, `contacts.edit`, `contacts.save`, `contacts.cancelEdit`).
- **Readonly fields support full cursor navigation.** The Name and Email fields in display mode allow arrow-key navigation, Home/End, selection, and copying without allowing text modification. Screen reader users can read the content using their reader's standard text access commands.
- **Error feedback is visible and announced.** When an email conflict is detected during edit, an error message appears inline and is announced to screen readers.
- **Field labels are properly associated.** The Name and Email fields use `AutomationProperties.LabeledBy` so screen readers announce the label when focusing each field.
- **Edit-mode button set is distinct from display-mode buttons.** When editing, the **Add** / **Edit** / **Delete** buttons are replaced with **Save** and **Cancel** buttons, making the current mode clear to all users.

---

## Bug Fixes

- **Contact editing was impossible.** The previous design auto-populated name/email fields when a contact was selected, but immediately cleared the selection when the user started typing, losing the email address association. Changing an email address created a new contact instead of updating the existing one. This is now fixed with explicit edit modes.
- **No way to discover how to edit a contact.** There were no visible affordances (buttons, menu options, keyboard shortcuts) for editing. Now **Edit** is a prominent button and a registered keyboard command.
- **Readonly TextBox behavior was unintuitive for screen reader users.** The previous `IsReadOnly="True"` binding prevented cursor navigation entirely. Fields now allow full keyboard navigation while preventing text modification.
- **Contact.Display property was serialized to JSON redundantly.** The computed `Display` property (formatted as "Name <email>") was being written to `contacts.json` alongside the authoritative `DisplayName` and `EmailAddress` fields, creating noise in the file. The property is now marked `[JsonIgnore]`.

---

## Internal

- `IContactService.UpdateContactAsync(id, displayName, emailAddress)` — new method for updating a contact's name and email with email-conflict detection. Returns false if another contact owns the target email address.
- `AddressBookViewModel` — refactored with explicit `BeginAddContactCommand`, `BeginEditContactCommand`, `SaveContactCommand`, and `CancelEditCommand`. Removed the implicit "populate-on-select" behavior and replaced it with explicit mode state (`IsEditingContact`, `EditName`, `EditEmail`, `ContactError`).
- `ContactFieldBox_PreviewKeyDown` — new keyboard handler that allows navigation keys (arrows, Tab, Home, End, Ctrl+A) in readonly mode while blocking all text-modification operations.
- `ContactModel.Display` — now marked `[JsonIgnore]` to prevent redundant JSON serialization.
- 465 tests, all green.

---

## Known Limitations

- **Email address changes are not merged with existing contacts.** If you change a contact's email from `old@example.com` to `new@example.com`, the old email entry remains in the address book if it was separate. A future merge/deduplicate feature will address this.
- **Contacts are not automatically harvested from incoming mail.** Contacts must be added manually via the **Grab Addresses** feature (Ctrl+Shift+B on a message) or the Compose window's right-click "Add to Address Book" option.

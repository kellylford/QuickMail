using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Views;

/// <summary>
/// What a window that opens a destination folder picker needs in order to offer "New Folder…"
/// inside it (issue #645), and Rename Folder (#1083). Handed down from <c>MainWindow</c>, which owns
/// the <see cref="ViewModels.MainViewModel"/> the rules windows deliberately do not see; null where
/// the caller has no way to create a folder, which hides the button.
///
/// <para>Two members rather than one because creating a folder from inside a picker is split in
/// half. <see cref="CreateAsync"/> runs while the picker's modal message loop is still on the stack,
/// so it may only refresh the folder cache — rebuilding the main window's folder tree from there is
/// the documented re-entrancy crash (CLAUDE.md, "Modal Dialog Rules"). <see cref="PickerClosed"/> is
/// the deferred half, and the owner must run it once the picker has closed, on Cancel as well as on
/// Open: the folder was created either way. A rename is split the same way, and the same
/// <see cref="PickerClosed"/> completes it.</para>
/// </summary>
/// <param name="CreateAsync">Creates the folder and returns the owning account's refreshed folder
/// list, so the picker can rebuild its tree in place and select what was just made. Throws when the
/// folder could not be created, with a message fit to read to the user ("There is already a folder
/// named 'Lists' there"); the picker announces it, since it is modal over the window whose status
/// line would otherwise be the only place it appeared (#794). Null is also taken as a failure, with
/// no reason to give.</param>
/// <param name="PickerClosed">Applies whatever was deferred past the picker's modal loop. A no-op
/// when no folder was created or renamed, so it is always safe to call.</param>
/// <param name="Rename">Lets the picker rename a folder in place (#1083); null leaves Rename out.</param>
public sealed record FolderCreationSupport(
    Func<Guid, string?, string, Task<IReadOnlyList<MailFolderModel>?>> CreateAsync,
    Action PickerClosed,
    FolderRenameSupport? Rename = null);

/// <summary>
/// What a folder picker needs to rename a folder in place (#1083).
/// </summary>
/// <param name="RefusalFor">Why a folder cannot be renamed — a standard folder, a view — or null when
/// it can. Asked before the name box opens, so a refusal never costs the user any typing.</param>
/// <param name="RenameAsync">Renames the folder, carrying every reference to it across, and never
/// throws: a refusal or failure comes back with the sentence to say. The picker is modal, so whoever
/// supplies this defers the main window's own catching up to the owner's
/// <see cref="FolderCreationSupport.PickerClosed"/> (or <c>MainViewModel.CommitPendingFolderTreeRebuild</c>).</param>
public sealed record FolderRenameSupport(
    Func<MailFolderModel, string?> RefusalFor,
    Func<MailFolderModel, string, Task<FolderRenameResult>> RenameAsync);

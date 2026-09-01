using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Views;

/// <summary>
/// What a window that opens a destination folder picker needs in order to offer "New Folder…"
/// inside it (issue #645). Handed down from <c>MainWindow</c>, which owns the
/// <see cref="ViewModels.MainViewModel"/> the rules windows deliberately do not see; null where the
/// caller has no way to create a folder, which hides the button.
///
/// <para>Two members rather than one because creating a folder from inside a picker is split in
/// half. <see cref="CreateAsync"/> runs while the picker's modal message loop is still on the stack,
/// so it may only refresh the folder cache — rebuilding the main window's folder tree from there is
/// the documented re-entrancy crash (CLAUDE.md, "Modal Dialog Rules"). <see cref="PickerClosed"/> is
/// the deferred half, and the owner must run it once the picker has closed, on Cancel as well as on
/// Open: the folder was created either way.</para>
/// </summary>
/// <param name="CreateAsync">Creates the folder and returns the owning account's refreshed folder
/// list, so the picker can rebuild its tree in place and select what was just made. Null means the
/// creation failed and the reason has already been surfaced to the user.</param>
/// <param name="PickerClosed">Applies whatever was deferred past the picker's modal loop. A no-op
/// when no folder was created, so it is always safe to call.</param>
public sealed record FolderCreationSupport(
    Func<Guid, string?, string, Task<IReadOnlyList<MailFolderModel>?>> CreateAsync,
    Action PickerClosed);

using System;
using System.IO;
using System.Linq;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Settings → Keyboard Shortcuts checks a new key against every command's binding, defaults included,
/// and taking a default key from a command leaves it with an explicit "no key" override that survives
/// a restart (#1044). Commands that share a default key on purpose, told apart by focus, are not
/// reported as conflicting with each other.
/// </summary>
public sealed class ShortcutConflictTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qm-shortcuts-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>The real registry, with the commands the issue names, under their real ids and default keys.</summary>
    private static CommandRegistry Registry()
    {
        var registry = new CommandRegistry();
        void Add(string id, string category, string title, Key key = Key.None, ModifierKeys mods = ModifierKeys.None) =>
            registry.Register(new CommandDefinition(id, category, title, () => { }, key, mods));

        Add("contacts.openAddressBook", "Contacts", "Address Book", Key.B, ModifierKeys.Control | ModifierKeys.Shift);
        Add("mail.sendOutboxNow", "Mail", "Send Outbox Now");   // no default key
        Add("mail.delete", "Mail", "Delete", Key.Delete);
        Add("folder.delete", "Mail", "Delete Folder", Key.Delete);
        Add("mail.previousUnread", "Mail", "Previous Unread", Key.Up, ModifierKeys.Alt);
        Add("account.moveUp", "Account", "Move Up", Key.Up, ModifierKeys.Alt);
        Add("mail.archive", "Mail", "Move to Archive", Key.M, ModifierKeys.Control | ModifierKeys.Shift);
        Add("mail.reply", "Mail", "Reply", Key.R, ModifierKeys.Control);
        return registry;
    }

    private static SettingsViewModel.HotkeyRowViewModel Row(SettingsViewModel vm, string id) =>
        vm.HotkeyRows.Single(r => r.CommandId == id);

    private const ModifierKeys CtrlShift = ModifierKeys.Control | ModifierKeys.Shift;

    [Fact]
    public void ADefaultKey_IsAConflict()
    {
        var vm = new SettingsViewModel(new StubConfigService(), Registry());
        var outboxNow = Row(vm, "mail.sendOutboxNow");

        var conflict = vm.FindConflict(Key.B, CtrlShift, outboxNow);

        Assert.NotNull(conflict);
        Assert.Equal("contacts.openAddressBook", conflict!.CommandId);
    }

    [Fact]
    public void AKeyTheUserAssigned_IsStillAConflict()
    {
        var vm = new SettingsViewModel(new StubConfigService(), Registry());
        Row(vm, "mail.reply").SetCustomBinding(Key.K, ModifierKeys.Control);

        Assert.Equal("mail.reply", vm.FindConflict(Key.K, ModifierKeys.Control, Row(vm, "mail.archive"))?.CommandId);
        // Its default no longer runs it, so it no longer conflicts.
        Assert.Null(vm.FindConflict(Key.R, ModifierKeys.Control, Row(vm, "mail.archive")));
    }

    [Fact]
    public void ACommandsOwnKey_IsNotAConflictWithItself()
    {
        var vm = new SettingsViewModel(new StubConfigService(), Registry());
        var book = Row(vm, "contacts.openAddressBook");
        Assert.Empty(vm.FindConflicts(Key.B, CtrlShift, book));
    }

    [Fact]
    public void AFreeKey_IsNotAConflict()
    {
        var vm = new SettingsViewModel(new StubConfigService(), Registry());
        Assert.Empty(vm.FindConflicts(Key.F11, ModifierKeys.Control, Row(vm, "mail.reply")));
    }

    [Theory]
    [InlineData("mail.delete", Key.Delete, ModifierKeys.None)]
    [InlineData("folder.delete", Key.Delete, ModifierKeys.None)]
    [InlineData("mail.previousUnread", Key.Up, ModifierKeys.Alt)]
    [InlineData("account.moveUp", Key.Up, ModifierKeys.Alt)]
    public void CommandsSharingADefaultByFocus_DoNotConflictWithEachOther(string id, Key key, ModifierKeys mods)
    {
        var vm = new SettingsViewModel(new StubConfigService(), Registry());
        var row = Row(vm, id);

        // Recording its own default as a custom key, as a user naturally might.
        Assert.Empty(vm.FindConflicts(key, mods, row));

        // And having done so, the partner still keeps its default.
        vm.AssignShortcut(row, key, mods);
        Assert.All(vm.HotkeyRows.Where(r => r != row && r.HasDefault(key, mods)), partner =>
        {
            Assert.False(partner.HasCustomBinding);
            Assert.True(partner.MatchesBinding(key, mods));
        });
    }

    [Fact]
    public void ASharedDefault_GivenToAThirdCommand_ConflictsWithBoth_AndBothLoseIt()
    {
        var vm = new SettingsViewModel(new StubConfigService(), Registry());
        var archive = Row(vm, "mail.archive");

        var conflicts = vm.FindConflicts(Key.Delete, ModifierKeys.None, archive);
        Assert.Equal(["folder.delete", "mail.delete"], conflicts.Select(c => c.CommandId).OrderBy(id => id));

        vm.AssignShortcut(archive, Key.Delete, ModifierKeys.None);

        Assert.True(Row(vm, "mail.delete").IsNoKey);
        Assert.True(Row(vm, "folder.delete").IsNoKey);
        Assert.Equal("Delete", archive.ActiveGesture);
    }

    [Fact]
    public void Reassigning_ADefault_LeavesTheOtherCommandWithNoKey()
    {
        var vm = new SettingsViewModel(new StubConfigService(), Registry());
        var outboxNow = Row(vm, "mail.sendOutboxNow");
        var book = Row(vm, "contacts.openAddressBook");

        vm.AssignShortcut(outboxNow, Key.B, CtrlShift);

        Assert.True(book.IsNoKey);
        Assert.True(book.HasCustomBinding);           // saved, and Restore Default undoes it
        Assert.Equal(string.Empty, book.ActiveGesture);
        Assert.Equal("Address Book, Contacts, no shortcut", book.AccessibleName);
        Assert.Equal(HotkeyBinding.NoKeyGesture, book.ToBinding().Gesture);
        Assert.Equal("Ctrl+Shift+B", outboxNow.ActiveGesture);

        // Restore Default gives it back.
        book.ClearCustomBinding();
        Assert.False(book.IsNoKey);
        Assert.Equal("Ctrl+Shift+B", book.ActiveGesture);
    }

    [Fact]
    public void Reassigning_AKeyTheUserGave_PutsBackTheDefaultWhenItIsFree()
    {
        var vm = new SettingsViewModel(new StubConfigService(), Registry());
        var reply = Row(vm, "mail.reply");
        reply.SetCustomBinding(Key.K, ModifierKeys.Control);

        vm.AssignShortcut(Row(vm, "mail.archive"), Key.K, ModifierKeys.Control);

        Assert.False(reply.HasCustomBinding);
        Assert.Equal("Ctrl+R", reply.ActiveGesture);
    }

    [Fact]
    public void Reassigning_AKeyTheUserGave_LeavesNoKeyWhenTheDefaultIsInUse()
    {
        var vm = new SettingsViewModel(new StubConfigService(), Registry());
        var reply = Row(vm, "mail.reply");
        reply.SetCustomBinding(Key.K, ModifierKeys.Control);
        Row(vm, "mail.sendOutboxNow").SetCustomBinding(Key.R, ModifierKeys.Control);   // reply's default

        vm.AssignShortcut(Row(vm, "mail.archive"), Key.K, ModifierKeys.Control);

        Assert.True(reply.IsNoKey);
    }

    [Fact]
    public void NoKey_SurvivesARestart_AndTheRegistryHonorsIt()
    {
        var profile = new ProfileContext(_dir);
        var vm = new SettingsViewModel(new ConfigService(profile), Registry());
        vm.AssignShortcut(Row(vm, "mail.sendOutboxNow"), Key.B, CtrlShift);
        vm.SaveCommand.Execute(null);

        // A fresh start: the file read again, by a new service, into a new registry and dialog.
        var cfg = new ConfigService(profile).Load();
        var saved = cfg.CustomHotkeys.Single(h => h.CommandId == "contacts.openAddressBook");
        Assert.True(saved.IsNoKey);

        var registry = Registry();
        registry.ApplyUserOverrides(cfg.CustomHotkeys);
        Assert.Equal("mail.sendOutboxNow", registry.FindByGesture(Key.B, CtrlShift)?.Id);
        Assert.Equal(string.Empty, registry.GetGestureText("contacts.openAddressBook"));

        var reopened = new SettingsViewModel(new ConfigService(profile), registry);
        Assert.True(Row(reopened, "contacts.openAddressBook").IsNoKey);
        Assert.Equal("Ctrl+Shift+B", Row(reopened, "mail.sendOutboxNow").ActiveGesture);
    }

    [Fact]
    public void NoKey_OnASharedDefault_LeavesThePartnerWorking()
    {
        // mail.delete given no key must not stop Delete reaching folder.delete.
        var registry = Registry();
        registry.ApplyUserOverrides([new HotkeyBinding { CommandId = "mail.delete", Gesture = HotkeyBinding.NoKeyGesture }]);

        Assert.Equal("folder.delete", registry.FindByGesture(Key.Delete, ModifierKeys.None)?.Id);
        Assert.Equal(string.Empty, registry.GetGestureText("mail.delete"));
        Assert.Equal("Delete", registry.GetGestureText("folder.delete"));
    }

    [Fact]
    public void NoKey_ReachesTheMessageWindowsOwnCommands()
    {
        var local = MessageWindowKeys(new HotkeyBinding { CommandId = "mail.reply", Gesture = HotkeyBinding.NoKeyGesture });
        Assert.Null(local.FindByGesture(Key.R, ModifierKeys.Control));
        Assert.Equal(string.Empty, local.GetGestureText("message.reply"));
    }

    [Fact]
    public void ASavedViewWhoseDefaultCameFromItsBinding_StillConflictsWithABuiltInKey()
    {
        // Saved views register with the key from hotkeys.json as their "default", so after a restart a
        // view given Ctrl+Shift+B looks like it shares Address Book's default by design. It doesn't.
        var registry = Registry();
        registry.Register(new CommandDefinition("view.saved.11111111-1111-1111-1111-111111111111", "Views", "Unread work",
            () => { }, Key.B, CtrlShift));
        var vm = new SettingsViewModel(new StubConfigService(), registry);
        var book = Row(vm, "contacts.openAddressBook");

        var conflict = vm.FindConflict(Key.B, CtrlShift, book);

        Assert.Equal("view.saved.11111111-1111-1111-1111-111111111111", conflict?.CommandId);
    }

    [Fact]
    public void AThemeWhoseDefaultCameFromItsBinding_StillConflictsWithABuiltInKey()
    {
        var registry = Registry();
        registry.Register(new CommandDefinition("theme.apply.dark", "Settings", "Theme: Dark", () => { }, Key.R, ModifierKeys.Control));
        var vm = new SettingsViewModel(new StubConfigService(), registry);

        Assert.Equal("theme.apply.dark", vm.FindConflict(Key.R, ModifierKeys.Control, Row(vm, "mail.reply"))?.CommandId);
    }

    [Fact]
    public void RestoreDefault_WhenAnotherCommandHoldsTheKey_IsAConflict_AndTakesItBack()
    {
        var vm = new SettingsViewModel(new StubConfigService(), Registry());
        var book = Row(vm, "contacts.openAddressBook");
        var outboxNow = Row(vm, "mail.sendOutboxNow");
        vm.AssignShortcut(outboxNow, Key.B, CtrlShift);

        Assert.Equal(["mail.sendOutboxNow"], vm.FindRestoreConflicts(book).Select(r => r.CommandId));

        var changes = vm.RestoreDefault(book);

        Assert.Equal("Ctrl+Shift+B", book.ActiveGesture);
        Assert.False(book.HasCustomBinding);
        Assert.Equal(string.Empty, outboxNow.ActiveGesture);
        Assert.Equal(["Send Outbox Now now has no shortcut"], changes);
    }

    [Fact]
    public void RestoreDefault_WhenTheKeyIsFree_ChangesNothingElse()
    {
        var vm = new SettingsViewModel(new StubConfigService(), Registry());
        var reply = Row(vm, "mail.reply");
        reply.SetCustomBinding(Key.K, ModifierKeys.Control);

        Assert.Empty(vm.FindRestoreConflicts(reply));
        Assert.Empty(vm.RestoreDefault(reply));
        Assert.Equal("Ctrl+R", reply.ActiveGesture);
    }

    [Fact]
    public void RestoreDefault_OnAFocusSharedDefault_IsNotAConflict()
    {
        var vm = new SettingsViewModel(new StubConfigService(), Registry());
        var mailDelete = Row(vm, "mail.delete");
        mailDelete.SetNoKey();

        Assert.Empty(vm.FindRestoreConflicts(mailDelete));   // folder.delete still has Delete, by design
    }

    [Fact]
    public void Reassigning_SaysWhatHappenedToTheOtherCommand()
    {
        var vm = new SettingsViewModel(new StubConfigService(), Registry());
        Row(vm, "mail.reply").SetCustomBinding(Key.K, ModifierKeys.Control);

        Assert.Equal(["Address Book now has no shortcut"], vm.AssignShortcut(Row(vm, "mail.archive"), Key.B, CtrlShift));
        Assert.Equal(["Reply is back on Ctrl+R"], vm.AssignShortcut(Row(vm, "mail.archive"), Key.K, ModifierKeys.Control));
        Assert.Empty(vm.AssignShortcut(Row(vm, "mail.archive"), Key.F11, ModifierKeys.Control));
    }

    [Fact]
    public void ACommandWithNoDefault_ThatLosesItsKey_IsSimplyCleared()
    {
        var vm = new SettingsViewModel(new StubConfigService(), Registry());
        var outboxNow = Row(vm, "mail.sendOutboxNow");
        outboxNow.SetCustomBinding(Key.F11, ModifierKeys.Control);

        vm.AssignShortcut(Row(vm, "mail.reply"), Key.F11, ModifierKeys.Control);

        Assert.False(outboxNow.HasCustomBinding);   // no redundant "None" entry in hotkeys.json
        Assert.False(outboxNow.IsNoKey);
    }

    [Theory]
    [InlineData(new string[0], "")]
    [InlineData(new[] { "Delete" }, "Delete")]
    [InlineData(new[] { "Delete", "Delete Folder" }, "Delete and Delete Folder")]
    [InlineData(new[] { "Delete", "Delete Folder", "Delete Appointment" }, "Delete, Delete Folder and Delete Appointment")]
    public void ConflictingTitles_ReadAsAList(string[] titles, string expected) =>
        Assert.Equal(expected, SettingsViewModel.JoinTitles(titles));

    private static CommandRegistry MessageWindowKeys(params HotkeyBinding[] mainBindings)
    {
        var local = new CommandRegistry();
        local.Register(new CommandDefinition("message.reply", "Mail", "Reply", () => { }, Key.R, ModifierKeys.Control));
        local.ApplyUserOverrides(QuickMail.Views.MessageWindow.TranslateMainOverrides(mainBindings));
        return local;
    }
}

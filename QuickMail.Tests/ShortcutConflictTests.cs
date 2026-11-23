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
        Add("view.toggleFavoriteFolder", "View", "Toggle Favorite Folder");   // no default key
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
        var favorite = Row(vm, "view.toggleFavoriteFolder");

        var conflict = vm.FindConflict(Key.B, CtrlShift, favorite);

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
        var favorite = Row(vm, "view.toggleFavoriteFolder");
        var book = Row(vm, "contacts.openAddressBook");

        vm.AssignShortcut(favorite, Key.B, CtrlShift);

        Assert.True(book.IsNoKey);
        Assert.True(book.HasCustomBinding);           // saved, and Restore Default undoes it
        Assert.Equal(string.Empty, book.ActiveGesture);
        Assert.Equal("Address Book, Contacts, no shortcut", book.AccessibleName);
        Assert.Equal(HotkeyBinding.NoKeyGesture, book.ToBinding().Gesture);
        Assert.Equal("Ctrl+Shift+B", favorite.ActiveGesture);

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
        Row(vm, "view.toggleFavoriteFolder").SetCustomBinding(Key.R, ModifierKeys.Control);   // reply's default

        vm.AssignShortcut(Row(vm, "mail.archive"), Key.K, ModifierKeys.Control);

        Assert.True(reply.IsNoKey);
    }

    [Fact]
    public void NoKey_SurvivesARestart_AndTheRegistryHonorsIt()
    {
        var profile = new ProfileContext(_dir);
        var vm = new SettingsViewModel(new ConfigService(profile), Registry());
        vm.AssignShortcut(Row(vm, "view.toggleFavoriteFolder"), Key.B, CtrlShift);
        vm.SaveCommand.Execute(null);

        // A fresh start: the file read again, by a new service, into a new registry and dialog.
        var cfg = new ConfigService(profile).Load();
        var saved = cfg.CustomHotkeys.Single(h => h.CommandId == "contacts.openAddressBook");
        Assert.True(saved.IsNoKey);

        var registry = Registry();
        registry.ApplyUserOverrides(cfg.CustomHotkeys);
        Assert.Equal("view.toggleFavoriteFolder", registry.FindByGesture(Key.B, CtrlShift)?.Id);
        Assert.Equal(string.Empty, registry.GetGestureText("contacts.openAddressBook"));

        var reopened = new SettingsViewModel(new ConfigService(profile), registry);
        Assert.True(Row(reopened, "contacts.openAddressBook").IsNoKey);
        Assert.Equal("Ctrl+Shift+B", Row(reopened, "view.toggleFavoriteFolder").ActiveGesture);
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

    private static CommandRegistry MessageWindowKeys(params HotkeyBinding[] mainBindings)
    {
        var local = new CommandRegistry();
        local.Register(new CommandDefinition("message.reply", "Mail", "Reply", () => { }, Key.R, ModifierKeys.Control));
        local.ApplyUserOverrides(QuickMail.Views.MessageWindow.TranslateMainOverrides(mainBindings));
        return local;
    }
}

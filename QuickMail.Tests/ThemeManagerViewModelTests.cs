using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class ThemeManagerViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"QM-ThemeMgrTests-{Guid.NewGuid():N}");
    private readonly StubThemeService _themes = new();
    private readonly StubConfigService _config = new();

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private ThemeManagerViewModel NewVm() => new(_themes, _config);

    private ThemeDefinition AddUserTheme(string id = "custom", string name = "Custom")
    {
        var theme = new ThemeDefinition { Id = id, Name = name, Base = "light" };
        _themes.SaveUserTheme(theme);
        return theme;
    }

    [Fact]
    public void Construction_ListsThemes_AndSelectsTheConfiguredOne()
    {
        _themes.ApplyTheme("dark");
        var vm = NewVm();

        Assert.Equal(new[] { "system", "parchment", "dark" }, vm.Themes.Select(t => t.Id));
        Assert.Equal("dark", vm.SelectedTheme?.Id);
        Assert.True(vm.Themes.First(t => t.Id == "dark").IsCurrent);
    }

    [Fact]
    public void AccessibleName_CarriesKindAndCurrentMarker()
    {
        _themes.ApplyTheme("parchment");
        var vm = NewVm();

        Assert.Equal("Parchment, built-in, current theme",
            vm.Themes.First(t => t.Id == "parchment").AccessibleName);
        Assert.Equal("Parchment Dark, built-in",
            vm.Themes.First(t => t.Id == "dark").AccessibleName);
    }

    [Fact]
    public void Apply_AppliesAndPersistsTheConfiguredTheme()
    {
        var vm = NewVm();
        vm.SelectedTheme = vm.Themes.First(t => t.Id == "dark");

        vm.ApplyCommand.Execute(null);

        Assert.Contains("dark", _themes.AppliedThemeIds);
        Assert.Equal("dark", _config.Load().AppearanceThemeId);
        Assert.True(vm.Themes.First(t => t.Id == "dark").IsCurrent);
    }

    [Fact]
    public void Apply_WhenEffectivePaletteUnchanged_AnnouncesSoItIsNotSilent()
    {
        // When applying a theme that resolves to the same palette (e.g. System
        // already shows the built-in you pick), the service raises no ThemeChanged
        // event, so the main window stays silent — the VM must announce instead.
        var vm = NewVm();
        var announced = new List<(string Text, AnnouncementCategory Category)>();
        vm.AnnouncementRequested += (text, cat) => announced.Add((text, cat));

        vm.SelectedTheme = vm.Themes.First(t => t.Id == "parchment");
        vm.ApplyCommand.Execute(null);

        Assert.Contains(announced, a => a.Text.StartsWith("Theme changed to", StringComparison.Ordinal)
                                        && a.Category == AnnouncementCategory.Status);
    }

    [Fact]
    public void Duplicate_OpensNamePanelPrefilled_ThenCreatesUserTheme()
    {
        var vm = NewVm();
        vm.SelectedTheme = vm.Themes.First(t => t.Id == "parchment");
        var announced = new List<string>();
        vm.AnnouncementRequested += (text, _) => announced.Add(text);

        vm.DuplicateCommand.Execute(null);
        Assert.True(vm.IsNamePanelOpen);
        Assert.Equal("Parchment copy", vm.EditName);

        vm.ConfirmNameCommand.Execute(null);

        Assert.False(vm.IsNamePanelOpen);
        var copy = vm.Themes.FirstOrDefault(t => t.Name == "Parchment copy");
        Assert.NotNull(copy);
        Assert.False(copy!.IsBuiltIn);
        Assert.Equal(copy.Id, vm.SelectedTheme?.Id);
        Assert.Contains("Theme Parchment copy created.", announced);
    }

    [Fact]
    public void Duplicate_IsUnavailableForSystem()
    {
        var vm = NewVm();
        vm.SelectedTheme = vm.Themes.First(t => t.Id == "system");
        Assert.False(vm.CanDuplicate);
    }

    [Fact]
    public void Rename_ChangesUserThemeName_AndIsDisabledForBuiltIns()
    {
        AddUserTheme();
        var vm = NewVm();

        vm.SelectedTheme = vm.Themes.First(t => t.Id == "parchment");
        Assert.False(vm.CanRename);

        vm.SelectedTheme = vm.Themes.First(t => t.Id == "custom");
        Assert.True(vm.CanRename);

        vm.RenameCommand.Execute(null);
        Assert.Equal("Custom", vm.EditName);
        vm.EditName = "My Colors";
        vm.ConfirmNameCommand.Execute(null);

        Assert.Equal("My Colors", vm.Themes.First(t => t.Id == "custom").Name);
    }

    [Fact]
    public void CancelName_ClosesThePanelWithoutChanges()
    {
        AddUserTheme();
        var vm = NewVm();
        vm.SelectedTheme = vm.Themes.First(t => t.Id == "custom");

        vm.RenameCommand.Execute(null);
        vm.EditName = "Ignored";
        vm.CancelNameCommand.Execute(null);

        Assert.False(vm.IsNamePanelOpen);
        Assert.Equal("Custom", vm.Themes.First(t => t.Id == "custom").Name);
    }

    [Fact]
    public void Delete_Confirmed_RemovesTheme_AndLandsOnANeighbor()
    {
        AddUserTheme();
        var vm = NewVm();
        vm.SelectedTheme = vm.Themes.First(t => t.Id == "custom");
        vm.ConfirmDeleteRequested = (_, _) => true;

        vm.DeleteCommand.Execute(null);

        Assert.DoesNotContain(vm.Themes, t => t.Id == "custom");
        Assert.NotNull(vm.SelectedTheme);
    }

    [Fact]
    public void Delete_Declined_KeepsTheTheme()
    {
        AddUserTheme();
        var vm = NewVm();
        vm.SelectedTheme = vm.Themes.First(t => t.Id == "custom");
        vm.ConfirmDeleteRequested = (_, _) => false;

        vm.DeleteCommand.Execute(null);

        Assert.Contains(vm.Themes, t => t.Id == "custom");
    }

    [Fact]
    public void Delete_IsDisabledForBuiltIns()
    {
        var vm = NewVm();
        vm.SelectedTheme = vm.Themes.First(t => t.Id == "parchment");
        Assert.False(vm.CanDelete);
    }

    [Fact]
    public void ExportThenImport_RoundTripsThroughTheFilePickers()
    {
        Directory.CreateDirectory(_dir);
        var vm = NewVm();
        var path = Path.Combine(_dir, "parchment.quickmailtheme");

        vm.SelectedTheme = vm.Themes.First(t => t.Id == "parchment");
        vm.ExportPathRequested = _ => path;
        vm.ExportCommand.Execute(null);
        Assert.True(File.Exists(path));

        // Give the exported copy a fresh id so the stub lists it as a new theme.
        var json = File.ReadAllText(path).Replace("\"parchment\"", "\"parchment-2\"");
        File.WriteAllText(path, json);

        vm.ImportPathRequested = () => path;
        vm.ImportCommand.Execute(null);

        Assert.Contains(vm.Themes, t => t.Id == "parchment-2");
        Assert.Equal("parchment-2", vm.SelectedTheme?.Id);
    }

    [Fact]
    public void Import_MalformedFile_ReportsTheError_AndLeavesTheListUnchanged()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "broken.quickmailtheme");
        File.WriteAllText(path, "{ not a theme");

        var vm = NewVm();
        var countBefore = vm.Themes.Count;
        string? error = null;
        vm.ErrorRequested += message => error = message;
        vm.ImportPathRequested = () => path;

        vm.ImportCommand.Execute(null);

        Assert.NotNull(error);
        Assert.Equal(countBefore, vm.Themes.Count);
    }

    [Fact]
    public void SelectedThemeDescription_DescribesTheSelection_AndUpdatesOnChange()
    {
        var vm = NewVm();

        vm.SelectedTheme = vm.Themes.First(t => t.Id == "parchment");
        var parchmentText = vm.SelectedThemeDescription;
        Assert.False(string.IsNullOrWhiteSpace(parchmentText));
        Assert.Contains("Window background", parchmentText);   // a "where it is used" line
        Assert.Contains("Fonts", parchmentText);

        var changed = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.SelectedThemeDescription)) changed = true;
        };

        vm.SelectedTheme = vm.Themes.First(t => t.Id == "system");
        Assert.True(changed, "changing the selection should raise SelectedThemeDescription");
        Assert.Contains("follows the Windows light and dark setting", vm.SelectedThemeDescription);
    }

    [Fact]
    public void OpenThemesFolder_RaisesTheFolderRequest()
    {
        _themes.UserThemesFolder = @"C:\somewhere\themes";
        var vm = NewVm();
        string? opened = null;
        vm.OpenFolderRequested += folder => opened = folder;

        vm.OpenThemesFolderCommand.Execute(null);

        Assert.Equal(@"C:\somewhere\themes", opened);
    }
}

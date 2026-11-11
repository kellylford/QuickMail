using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

public class SoundTutorialTests
{
    private readonly StubConfigService _config = new();
    private readonly RecordingSoundService _sounds = new();

    private SoundTutorialViewModel MakeVm() => new(_config, _sounds);

    private void UseCalmScheme()
    {
        var cfg = _config.Load();
        cfg.SoundSchemes["user-1"] = new SoundScheme("user-1", "Calm scheme", isBuiltIn: false);
        cfg.SoundSchemeId = "user-1";
    }

    // ── The view model ───────────────────────────────────────────────────────────

    [Fact]
    public void ListsEveryEventAndStartsOnTheFirst()
    {
        var vm = MakeVm();
        Assert.Equal(SoundCatalog.Events.Select(e => e.Label), vm.Rows.Select(r => r.Label));
        Assert.Same(vm.Rows[0], vm.SelectedRow);
    }

    [Fact]
    public void PlayingPreviewsTheSelectedEvent()
    {
        var vm = MakeVm();
        vm.SelectedRow = vm.Rows.Single(r => r.Info.Event == SoundEvent.NewMail);
        vm.PlaySelectedCommand.Execute(null);
        Assert.Equal([SoundEvent.NewMail], _sounds.PreviewedEvents);
        Assert.False(vm.HasNote);
    }

    [Fact]
    public void AnEventWithNoSoundSaysSo()
    {
        _config.Load().ActiveSoundEvents()["MessageSent"] = "none";
        var vm = MakeVm();
        var said = new System.Collections.Generic.List<string>();
        vm.NoteAnnounced += said.Add;

        vm.PlaySelectedCommand.Execute(null);

        Assert.Equal("No sound.", vm.Note);
        Assert.True(vm.HasNote);
        Assert.Equal(["No sound."], said);
        Assert.Equal([SoundEvent.MessageSent], _sounds.PreviewedEvents);   // which stops the last sound
    }

    [Fact]
    public void AFileThatHasGoneSaysSoAndAFileThatIsThereDoesNot()
    {
        _config.Load().ActiveSoundEvents()["MessageSent"] = SoundRef.Custom("mine.wav").ToStored();
        var vm = MakeVm();

        vm.PlaySelectedCommand.Execute(null);
        Assert.Equal("Its sound file is missing, so the built-in sound plays.", vm.Note);

        _sounds.CustomFiles.Add("mine.wav");
        vm.PlaySelectedCommand.Execute(null);
        Assert.Equal(string.Empty, vm.Note);
    }

    [Fact]
    public void MovingToAnotherEventClearsTheNote()
    {
        _config.Load().ActiveSoundEvents()["MessageSent"] = "none";
        var vm = MakeVm();
        vm.PlaySelectedCommand.Execute(null);
        vm.SelectedRow = vm.Rows[1];
        Assert.False(vm.HasNote);
    }

    [Theory]
    [InlineData("Glass", "the Glass scheme.")]
    [InlineData("Calm scheme", "the Calm scheme.")]
    [InlineData("Calm SCHEME", "the Calm scheme.")]
    [InlineData("Scheme", "the Scheme scheme.")]
    public void TheIntroNamesTheSchemeOnce(string name, string expected)
    {
        var intro = SoundTutorialViewModel.IntroFor(new SoundScheme("user-1", name, isBuiltIn: false));
        Assert.Contains($"to hear it in {expected}", intro);
    }

    [Fact]
    public void TheIntroCatchesUpWithTheSchemeInUse()
    {
        var vm = MakeVm();
        Assert.Contains("the Glass scheme", vm.Intro);
        UseCalmScheme();
        vm.Refresh();
        Assert.Contains("the Calm scheme", vm.Intro);
    }

    [Fact]
    public void ClosingStopsOnlyASoundPlayedHere()
    {
        var vm = MakeVm();
        vm.StopSounds();
        Assert.Equal(0, _sounds.Stops);   // a new-mail sound just starting isn't this window's to stop

        vm.PlaySelectedCommand.Execute(null);
        vm.StopSounds();
        Assert.Equal(1, _sounds.Stops);
    }

    [Fact]
    public void TheHelpCommandIsRegistered()
    {
        var source = System.IO.File.ReadAllText(RepoFile("QuickMail/ViewModels/MainViewModel.cs"));
        Assert.Contains("id: \"help.soundTutorial\"", source);
        Assert.Contains("sc:CommandShortcut.Id=\"help.soundTutorial\"",
            System.IO.File.ReadAllText(RepoFile("QuickMail/Views/MainWindow.xaml")));
    }

    private static string RepoFile(string relative)
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, relative))) dir = dir.Parent;
        Assert.NotNull(dir);
        return System.IO.Path.Combine(dir.FullName, relative);
    }

    // ── The window ───────────────────────────────────────────────────────────────

    private SoundTutorialWindow MakeWindow()
    {
        WpfTestHost.EnsureStyles("AccessibleStyles", "ThemedControls");
        return new SoundTutorialWindow(MakeVm());
    }

    private static ListBoxItem RowContainer(ListBox list, int index) =>
        Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(index));

    private static KeyEventArgs Press(Visual source, Key key) =>
        new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(source)!, 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };

    [StaFact]
    public void EachRowsNameIsOnItsContainer()
    {
        var window = MakeWindow();
        try
        {
            window.Show();
            window.UpdateLayout();
            var list = window.FindName("SoundEventList") as ListBox;
            Assert.NotNull(list);
            Assert.Equal(SoundCatalog.Events[0].Label, AutomationProperties.GetName(RowContainer(list, 0)));
            Assert.Equal(SoundCatalog.Events[1].Label, AutomationProperties.GetName(RowContainer(list, 1)));
        }
        finally { window.Close(); }
    }

    [StaTheory]
    [InlineData(Key.Space)]
    [InlineData(Key.Enter)]
    public void SpaceAndEnterPlayTheRowAndLeaveTheWindowOpen(Key key)
    {
        var window = MakeWindow();
        try
        {
            window.Show();
            window.UpdateLayout();
            var list = window.FindName("SoundEventList") as ListBox;
            Assert.NotNull(list);
            var row = RowContainer(list, 3);

            var args = Press(row, key);
            row.RaiseEvent(args);

            Assert.True(args.Handled);
            Assert.Equal([SoundCatalog.Events[3].Event], _sounds.PreviewedEvents);
            Assert.True(window.IsVisible);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void ClosingStopsTheSound()
    {
        var window = MakeWindow();
        window.Show();
        window.UpdateLayout();
        ((SoundTutorialViewModel)window.DataContext).PlaySelectedCommand.Execute(null);
        window.Close();
        Assert.Equal(1, _sounds.Stops);
    }

    [StaFact]
    public void SpaceInSettingsSoundEventsPreviewsThatEvent()
    {
        WpfTestHost.EnsureStyles("AccessibleStyles", "ThemedControls");
        var vm = new SettingsViewModel(_config, new StubCommandRegistry(), sounds: _sounds);
        var dialog = new SettingsDialog(vm);
        try
        {
            dialog.Show();
            var list = dialog.FindName("SoundEventList") as ListBox;
            Assert.NotNull(list);

            var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog)!, 0, Key.Space)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };
            list.RaiseEvent(args);

            Assert.True(args.Handled);
            Assert.Equal([vm.SelectedSoundEvent!.Choice.Stored], _sounds.Previewed);
        }
        finally { dialog.Close(); }
    }
}

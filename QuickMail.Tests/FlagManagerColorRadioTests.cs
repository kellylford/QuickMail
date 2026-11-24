// Regression tests for issue #1080: the Flag Manager's twelve preset color radio buttons.
// Up/Down only reached the button in the other row, arrows moved focus without choosing a color
// (each button applied its color through a Command, which only Space, Enter and the mouse run),
// and nothing bound IsChecked to the selected flag's color.
//
// The window is constructed but never shown, and keys are fed through the real PreviewKeyDown
// route as in RadioGroupNavigationTests, so nothing here depends on focus or timing.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

[Collection("WpfTests")]
public class FlagManagerColorRadioTests
{
    private const string Blue = "#3182CE";
    private const string Red  = "#E53E3E";
    private const string Lime = "#276749";   // last button of the first row of six
    private const string Teal = "#319795";   // first button of the second row

    [StaFact]
    public void ColorPanel_SelectsOnArrow_AndCheckedButtonFollowsTheFlag()
    {
        WpfTestHost.EnsureStyles("AccessibleStyles", "ThemedControls");
        var previousModifiers = RadioGroupNavigation.ModifiersOf;
        // Pin the modifier read: the shipped one asks the physical keyboard (see ModifiersOf).
        RadioGroupNavigation.ModifiersOf = _ => ModifierKeys.None;

        var svc = new RecordingFlagService(
        [
            FlagDefinition.CreateBuiltIn(),
            new() { Name = "Urgent",  SortOrder = 1, ColorHex = Blue },
            new() { Name = "Later",   SortOrder = 2, ColorHex = Red.ToLowerInvariant() },
        ]);
        var vm = new FlagManagerViewModel(svc);
        var window = new FlagManagerWindow(vm);
        try
        {
            vm.LoadAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            vm.SelectedFlag = vm.Flags[1];
            DoEvents();

            var panel = window.FindName("ColorPanel") as WrapPanel;
            Assert.NotNull(panel);
            Assert.True(RadioGroupNavigation.GetSelectionFollowsFocus(panel));
            Assert.Equal(KeyboardNavigationMode.Once, KeyboardNavigation.GetTabNavigation(panel));
            Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetDirectionalNavigation(panel));
            Assert.True(panel.IsEnabled);

            var buttons = panel.Children.OfType<RadioButton>().ToArray();
            Assert.Equal(FlagManagerViewModel.PresetColors.Length, buttons.Length);
            Assert.All(buttons, b => Assert.Null(b.Command));

            // The flag's own color is the checked button, and only that one.
            Assert.Equal([Blue], CheckedTags(buttons));

            // Down from the last button of row one goes to the next button in order (row two),
            // checks it and applies its color — not to the button visually below.
            vm.SelectedColorHex = Lime;
            DoEvents();
            var lime = buttons.Single(b => (string)b.Tag == Lime);
            Assert.True(lime.IsChecked);

            PressKey(panel, lime, Key.Down);

            var teal = buttons.Single(b => (string)b.Tag == Teal);
            Assert.Equal(Array.IndexOf(buttons, lime) + 1, Array.IndexOf(buttons, teal));
            Assert.True(teal.IsChecked);
            Assert.Equal([Teal], CheckedTags(buttons));
            Assert.Equal(Teal, vm.Flags[1].ColorHex);
            Assert.Equal(Teal, svc.Saved.Single(f => f.Name == "Urgent").ColorHex);

            // Tab into the group arrives at the checked button.
            Assert.Same(teal, TabEntry(panel));

            // Selecting another flag checks its color (stored in a different case) and moves
            // where Tab enters, without saving anything.
            var savesBefore = svc.SaveCount;
            vm.SelectedFlag = vm.Flags[2];
            DoEvents();
            var red = buttons.Single(b => (string)b.Tag == Red);
            Assert.Equal([Red], CheckedTags(buttons));
            Assert.Same(red, TabEntry(panel));
            Assert.Equal(savesBefore, svc.SaveCount);
            Assert.Equal(Teal, vm.Flags[1].ColorHex);   // the first flag kept its color

            // The built-in flag's color is fixed: its color shows as checked, and the group is
            // disabled so arrowing cannot show another color as chosen.
            vm.SelectedFlag = vm.Flags[0];
            DoEvents();
            Assert.False(panel.IsEnabled);
            Assert.Equal(["#C05621"], CheckedTags(buttons));
            Assert.Equal(savesBefore, svc.SaveCount);
        }
        finally
        {
            RadioGroupNavigation.ModifiersOf = previousModifiers;
            window.Close();
        }
    }

    [StaFact]
    public void TabEntryAccessors_StillExistInWpf()
    {
        // RadioGroupNavigation keeps Tab arriving at the checked button through WPF's internal
        // KeyboardNavigation.SetTabOnceActiveElement. If a WPF update removes it, Tab would
        // silently go back to the last-focused button; fail here instead.
        Assert.NotNull(RadioGroupNavigation.SetTabOnceActiveElementMethod);
        Assert.NotNull(RadioGroupNavigation.GetTabOnceActiveElementMethod);
    }

    private static string[] CheckedTags(RadioButton[] buttons) =>
        [.. buttons.Where(b => b.IsChecked == true).Select(b => ((string)b.Tag).ToUpperInvariant())];

    private static object? TabEntry(DependencyObject container) =>
        RadioGroupNavigation.GetTabOnceActiveElementMethod!.Invoke(null, [container]);

    private static void PressKey(UIElement panel, RadioButton source, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, new PresentationSourceStub(), timestamp: 0, key)
        {
            RoutedEvent = UIElement.PreviewKeyDownEvent,
            Source      = source,
        };
        panel.RaiseEvent(args);
    }

    // KeyEventArgs needs a PresentationSource; the window is never shown, so a stub suffices.
    private sealed class PresentationSourceStub : PresentationSource
    {
        public override Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }

    private static void DoEvents()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private sealed class RecordingFlagService(List<FlagDefinition> flags) : IFlagService
    {
        public List<FlagDefinition> Saved { get; private set; } = [];
        public int SaveCount { get; private set; }

#pragma warning disable CS0067
        public event EventHandler? FlagDefinitionsChanged;
#pragma warning restore CS0067
        public FlagDefinition GetBuiltInFlag() => FlagDefinition.CreateBuiltIn();
        public Task<List<FlagDefinition>> LoadFlagDefinitionsAsync() => Task.FromResult(new List<FlagDefinition>(flags));
        public Task SaveFlagDefinitionsAsync(List<FlagDefinition> list) { Saved = [.. list]; SaveCount++; return Task.CompletedTask; }
        public Task<FlagDefinition> GetKDefaultFlagAsync() => Task.FromResult(flags[0]);
        public Task SetKDefaultFlagAsync(Guid flagId) => Task.CompletedTask;
        public Task<FlagDefinition?> SetMessageFlagAsync(MailMessageSummary message, string? flagId, FlagDefinition? resolvedDef = null, CancellationToken ct = default)
            => Task.FromResult<FlagDefinition?>(null);
        public Task<FlagDefinition?> ToggleDefaultFlagAsync(MailMessageSummary message, CancellationToken ct = default)
            => Task.FromResult<FlagDefinition?>(null);
    }
}

public class StringEqualsParameterConverterTests
{
    private static readonly StringEqualsParameterConverter Converter = new();

    [Theory]
    [InlineData("#E53E3E", "#E53E3E", true)]
    [InlineData("#e53e3e", "#E53E3E", true)]    // hex colors compare ignoring case
    [InlineData("#3182CE", "#E53E3E", false)]
    [InlineData(null,      "#E53E3E", false)]
    public void Convert_IsTrueOnlyForTheParameter(string? value, string parameter, bool expected) =>
        Assert.Equal(expected, Converter.Convert(value, typeof(bool?), parameter, CultureInfo.InvariantCulture));

    [Fact]
    public void ConvertBack_CheckedWritesTheParameter() =>
        Assert.Equal("#E53E3E", Converter.ConvertBack(true, typeof(string), "#E53E3E", CultureInfo.InvariantCulture));

    [Fact]
    public void ConvertBack_UncheckedLeavesTheSourceAlone() =>
        Assert.Same(Binding.DoNothing, Converter.ConvertBack(false, typeof(string), "#E53E3E", CultureInfo.InvariantCulture));
}

using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using QuickMail.Models;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

[Collection("WpfTests")]
public class ComposeSendConfirmationUiTests
{
    [StaFact]
    public void SettingsCheckbox_ExposesNameStateAndTwoWayBinding()
    {
        var vm = new SettingsViewModel(new StubConfigService(), new StubCommandRegistry());
        var window = new SettingsDialog(vm) { ShowInTaskbar = false, ShowActivated = false };
        try
        {
            window.Show();
            var checkbox = window.FindName("ConfirmBeforeSendCheckBox") as CheckBox;
            Assert.NotNull(checkbox);
            checkbox.BringIntoView();
            window.UpdateLayout();
            var peer = new CheckBoxAutomationPeer(checkbox);
            Assert.Equal("Confirm before sending a message", peer.GetName());
            Assert.Equal(AutomationControlType.CheckBox, peer.GetAutomationControlType());
            Assert.False(checkbox.IsChecked);
            checkbox.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
            Assert.True(vm.ConfirmBeforeSend);
            vm.ConfirmBeforeSend = false;
            Assert.False(checkbox.IsChecked);
        }
        finally { window.Close(); }
    }

    [StaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComposeWindow_ReadsTheSavedPreference(bool enabled)
    {
        var config = new StubConfigService();
        config.Save(new ConfigModel { ConfirmBeforeSend = enabled });
        var vm = new ComposeViewModel(new StubSmtpService(), new StubAccountService(), new StubCredentialService(),
            new StubImapMailService(), new StubTemplateService());
        var window = new ComposeWindow(vm, new StubContactService(), new StubTemplateService(), config)
        {
            ShowInTaskbar = false, ShowActivated = false, ConfirmSaveOnClose = null,
        };
        try
        {
            window.Show();
            Assert.Equal(enabled, vm.ConfirmBeforeSend);
        }
        finally { window.Close(); }
    }
}

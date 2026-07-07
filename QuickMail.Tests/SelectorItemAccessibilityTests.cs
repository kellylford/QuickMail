using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Regression guard for a whole class of accessibility bug: WPF reads a data-bound
/// ComboBox/ListBox/ListView item's UIA Name from <c>item.ToString()</c> — NOT from
/// <c>DisplayMemberPath</c>, which only drives the visual. So an item type bound into
/// a Selector must override <c>ToString()</c> to its display text, or a screen reader
/// reads the fully-qualified type name (plain class) or record punctuation
/// (<c>"ThemeOption { Id = ..., Name = ... }"</c>). The theme ComboBox shipped with
/// exactly that record bug; these tests lock the fix and cover every item type the app
/// binds into a Selector so a new one can't silently reintroduce it.
/// </summary>
public class SelectorItemAccessibilityTests
{
    // End-to-end: the actual reported control. Reads the real UIA item peer names a
    // screen reader would speak — this is what the reviews never checked.
    [StaFact]
    public void ThemeComboBox_ItemPeerNames_AreCleanDisplayNames()
    {
        var combo = new ComboBox
        {
            DisplayMemberPath = "Name",
            ItemsSource = new List<SettingsViewModel.ThemeOption>
            {
                new("system", "System"),
                new("parchment", "Parchment"),
                new("dark", "Parchment Dark"),
            },
        };
        var window = new Window
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            Width = 200, Height = 100, Content = combo,
        };
        window.Show();
        try
        {
            combo.IsDropDownOpen = true;
            combo.UpdateLayout();
            var names = (UIElementAutomationPeer.CreatePeerForElement(combo).GetChildren() ?? new List<AutomationPeer>())
                .Select(p => p.GetName())
                .ToList();
            Assert.Equal(new[] { "System", "Parchment", "Parchment Dark" }, names);
        }
        finally { window.Close(); }
    }

    // Cheap, fast guard on the item types themselves: their ToString() must be display
    // text — never the default type name or record "{ ... }" punctuation. If a new
    // Selector-bound type is added without a ToString override, add it here.
    [Fact]
    public void BoundItemTypes_ToString_IsDisplayText()
    {
        Assert.Equal("Quill", new SettingsViewModel.ThemeOption("quill", "Quill").ToString());
        Assert.Equal("My View", new SavedView { Name = "My View" }.ToString());
        Assert.Equal("Greeting", new MessageTemplate { Title = "Greeting" }.ToString());
        Assert.Equal("Spam rule", new MailRule { Name = "Spam rule" }.ToString());
        Assert.Equal("Work", new AccountModel { AccountName = "Work" }.ToString());

        var group = new GroupModel { Name = "Team" };
        Assert.Equal(group.Display, group.ToString());
    }

    [Fact]
    public void BoundItemTypes_ToString_NeverLooksLikeTypeNameOrRecordDump()
    {
        var strings = new[]
        {
            new SettingsViewModel.ThemeOption("a", "Alpha").ToString(),
            new SavedView { Name = "Alpha" }.ToString(),
            new MessageTemplate { Title = "Alpha" }.ToString(),
            new MailRule { Name = "Alpha" }.ToString(),
            new GroupModel { Name = "Alpha" }.ToString(),
        };
        foreach (var s in strings)
        {
            Assert.DoesNotContain("{", s);                 // record dump
            Assert.DoesNotContain("QuickMail.", s);        // fully-qualified type name
        }
    }
}

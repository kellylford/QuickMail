using System;
using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// A control inside a ToolBar takes its style from a ToolBar key, not from the
/// implicit style for its type, so ThemedControls maps each key onto the themed
/// implicit style with <c>BasedOn</c>. That <c>BasedOn</c> is a StaticResource,
/// which only sees what is defined above it: a key declared before its implicit
/// style silently bases itself on an earlier, unthemed style instead. The
/// compose toolbar's Paragraph style box shipped light in the dark theme that way
/// (#729), and the CheckBox, RadioButton and TextBox keys had the same latent fault.
/// </summary>
[Collection("WpfTests")]
public class ToolBarStyleKeyTests
{
    [StaTheory]
    [InlineData("Button", typeof(Button))]
    [InlineData("ToggleButton", typeof(System.Windows.Controls.Primitives.ToggleButton))]
    [InlineData("CheckBox", typeof(CheckBox))]
    [InlineData("RadioButton", typeof(RadioButton))]
    [InlineData("TextBox", typeof(TextBox))]
    [InlineData("ComboBox", typeof(ComboBox))]
    public void ToolBarKey_IsBasedOnTheThemedImplicitStyle(string name, Type type)
    {
        WpfTestHost.EnsureStyles("AccessibleStyles"); // registers the pack:// scheme first
        var themed = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/QuickMail;component/Styles/ThemedControls.xaml", UriKind.Absolute),
        };

        object key = name switch
        {
            "Button" => ToolBar.ButtonStyleKey,
            "ToggleButton" => ToolBar.ToggleButtonStyleKey,
            "CheckBox" => ToolBar.CheckBoxStyleKey,
            "RadioButton" => ToolBar.RadioButtonStyleKey,
            "TextBox" => ToolBar.TextBoxStyleKey,
            _ => ToolBar.ComboBoxStyleKey,
        };

        var toolbarStyle = themed[key] as Style;
        var implicitStyle = themed[type] as Style;
        Assert.NotNull(toolbarStyle);
        Assert.NotNull(implicitStyle);
        Assert.Same(implicitStyle, toolbarStyle!.BasedOn);
    }
}

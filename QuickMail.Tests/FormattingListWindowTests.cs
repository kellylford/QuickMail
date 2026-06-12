using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

[Collection("WpfTests")]
public class FormattingListWindowTests
{
    [StaFact]
    public void ListShowsOneRowPerFact_AndFirstItemIsSelected()
    {
        var window = new FormattingListWindow(
            ["Heading 2", "Bold on", "Italic off", "Strikethrough off"])
        {
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
        };
        window.Show();
        try
        {
            var list = window.FindName("FormattingList") as ListBox;
            Assert.NotNull(list);
            Assert.Equal(4, list!.Items.Count);
            Assert.Equal("Heading 2", list.Items[0]);
            // First entry is selected so arrowing starts from the block type.
            Assert.Equal(0, list.SelectedIndex);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void CloseButton_IsCancelAndDefault_SoEscapeAndEnterClose()
    {
        var window = new FormattingListWindow(new List<string> { "Normal text" })
        {
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
        };
        window.Show();
        try
        {
            Button? closeButton = null;
            foreach (var child in LogicalTreeWalker(window))
            {
                if (child is Button b) { closeButton = b; break; }
            }
            Assert.NotNull(closeButton);
            Assert.True(closeButton!.IsCancel, "Escape must close the formatting list");
            Assert.True(closeButton.IsDefault, "Enter must close the formatting list");
        }
        finally
        {
            window.Close();
        }
    }

    private static IEnumerable<object> LogicalTreeWalker(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            yield return child;
            if (child is DependencyObject d)
                foreach (var inner in LogicalTreeWalker(d))
                    yield return inner;
        }
    }
}

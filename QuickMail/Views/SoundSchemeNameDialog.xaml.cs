using System;
using System.Windows;
using System.Windows.Input;
using QuickMail.Models;

namespace QuickMail.Views;

/// <summary>
/// Asks for a sound scheme's name, for New, Copy and Rename on the Sounds tab (#932). Modeless, so it
/// reports back through <paramref name="accept"/> rather than a DialogResult: that returns why a name
/// can't be used, which stays in the dialog, or null once the name has been used, which closes it.
/// </summary>
public partial class SoundSchemeNameDialog : Window
{
    private readonly Func<string, string?> _accept;

    /// <param name="title">The window title, such as "New Sound Scheme".</param>
    /// <param name="initialName">The name to start with, selected, for Rename.</param>
    /// <param name="accept">Tries the name typed; returns why it was refused, or null if it was used.</param>
    public SoundSchemeNameDialog(string title, string initialName, Func<string, string?> accept)
    {
        _accept = accept;
        InitializeComponent();
        Title = title;
        NameBox.Text = initialName;
        Loaded += (_, _) =>
        {
            NameBox.SelectAll();
            NameBox.Focus();
        };
    }

    /// <summary>Whether a name was used. Read once the window has closed.</summary>
    public bool Accepted { get; private set; }

    private void OkButton_Click(object sender, RoutedEventArgs e) => Commit();

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            Close();
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && e.OriginalSource is not System.Windows.Controls.Button)
        {
            // Enter on a focused button presses that button, Cancel included.
            e.Handled = true;
            Commit();
        }
    }

    private void Commit()
    {
        var problem = _accept(NameBox.Text);
        if (problem is null)
        {
            Accepted = true;
            Close();
            return;
        }

        // Shown in the dialog, and spoken, since the text box keeps focus and reports nothing new.
        // Focus first, so moving it back from OK doesn't talk over the reason.
        ProblemText.Text = problem;
        ProblemText.Visibility = Visibility.Visible;
        System.Windows.Automation.AutomationProperties.SetHelpText(NameBox, problem);
        NameBox.SelectAll();
        NameBox.Focus();
        AccessibilityHelper.Announce(this, problem, interrupt: true, AnnouncementCategory.Result);
    }
}

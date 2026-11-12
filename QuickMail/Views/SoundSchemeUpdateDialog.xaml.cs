using System.Windows;
using System.Windows.Automation;
using QuickMail.Services;

namespace QuickMail.Views;

/// <summary>
/// Asks how to update a gallery scheme added before QuickMail kept track of the changes made to it
/// (#971): add only what's missing (the default), replace everything, or cancel. Its buttons say what
/// they do, where a message box could only say Yes and No. Escape, Cancel and closing the window all
/// cancel. Focus goes back where it was by the caller, which knows what the answer changed.
/// </summary>
public partial class SoundSchemeUpdateDialog : Window
{
    public SoundSchemeUpdateDialog(SoundSchemeUpdateQuestion question)
    {
        InitializeComponent();
        Title = question.Title;
        ExplanationText.Text = question.Text;
        // Read with the window, and with the button that has focus as it opens.
        AutomationProperties.SetHelpText(this, question.Text);
        AutomationProperties.SetHelpText(AddMissingButton, question.Text);
        Loaded += (_, _) => AddMissingButton.Focus();
    }

    /// <summary>What was chosen; Cancel until a choice is made. Read once the dialog has closed.</summary>
    public SoundSchemeUpdateChoice Choice { get; private set; } = SoundSchemeUpdateChoice.Cancel;

    private void AddMissingButton_Click(object sender, RoutedEventArgs e) => Choose(SoundSchemeUpdateChoice.Apply);

    private void ReplaceButton_Click(object sender, RoutedEventArgs e) => Choose(SoundSchemeUpdateChoice.ReplaceEverything);

    /// <summary>Records a choice and closes, as the buttons do; also what the tests press.</summary>
    internal void Choose(SoundSchemeUpdateChoice choice)
    {
        Choice = choice;
        // DialogResult closes a dialog shown with ShowDialog; one never shown (a test) is just closed.
        if (IsVisible) DialogResult = choice != SoundSchemeUpdateChoice.Cancel;
        else Close();
    }
}

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuickMail.Helpers;

namespace QuickMail.Views;

/// <summary>What the Image Description dialog was closed with.</summary>
/// <param name="Alt">The alternative text; empty when <paramref name="Decorative"/>.</param>
/// <param name="Decorative">The picture is decorative and is sent with <c>alt=""</c>.</param>
/// <param name="Shrink">The user kept "Shrink to 1600 pixels wide" checked (insert only).</param>
/// <param name="Remove">Image Properties only: remove the picture from the message.</param>
public sealed record ImageDescriptionResult(string Alt, bool Decorative, bool Shrink, bool Remove);

/// <summary>
/// Describes a picture being put into a message, or changes one already there (#729). A picture
/// cannot go in without a decision: OK stays unavailable until there is alternative text or
/// Decorative is checked. The file name is shown for context and is never used as alt text.
///
/// Modeless, by the rule for dialogs with an editable text box (CLAUDE.md, Modal Dialog Rules):
/// the caller gets the answer through <see cref="Completed"/>, raised exactly once when the
/// window closes — with null for Cancel, Escape, or closing it any other way.
/// </summary>
public partial class ImageDescriptionDialog : Window
{
    private ImageDescriptionResult? _result;

    /// <summary>Raised once, after the dialog has closed: the answer, or null when cancelled.</summary>
    public event Action<ImageDescriptionResult?>? Completed;

    /// <param name="context">What the picture is, e.g. "photo.jpg, 3024 by 4032 pixels".</param>
    /// <param name="existing">For Image Properties: the picture's current description. Null when inserting.</param>
    /// <param name="offerShrinkFromWidth">When inserting a picture wider than the shrink limit: its width.</param>
    public ImageDescriptionDialog(string context, ComposeImage? existing = null, int? offerShrinkFromWidth = null)
    {
        InitializeComponent();
        FileText.Text = context;

        if (existing is not null)
        {
            Title = "Image Properties";
            RemoveButton.Visibility = Visibility.Visible;
            AltBox.Text = existing.Alt ?? string.Empty;
            DecorativeBox.IsChecked = existing.IsDecorative;
        }

        if (offerShrinkFromWidth is { } width && width > ImageProcessing.ShrinkThreshold)
        {
            ShrinkBox.Content = $"_Shrink to {ImageProcessing.ShrinkThreshold} pixels wide (it is {width})";
            ShrinkBox.Visibility = Visibility.Visible;
        }

        UpdateState();
        Loaded += (_, _) =>
        {
            AltBox.Focus();
            AltBox.SelectAll();
        };
        Closed += (_, _) => Completed?.Invoke(_result);
    }

    private void UpdateState()
    {
        bool decorative = DecorativeBox.IsChecked == true;
        AltBox.IsEnabled = !decorative;
        OkButton.IsEnabled = decorative || !string.IsNullOrWhiteSpace(AltBox.Text);
    }

    private void AltBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateState();

    private void DecorativeBox_Changed(object sender, RoutedEventArgs e) => UpdateState();

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!OkButton.IsEnabled) return;
        bool decorative = DecorativeBox.IsChecked == true;
        _result = new ImageDescriptionResult(
            decorative ? string.Empty : AltBox.Text.Trim(), decorative,
            Shrink: ShrinkBox.Visibility == Visibility.Visible && ShrinkBox.IsChecked == true,
            Remove: false);
        Close();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        _result = new ImageDescriptionResult(string.Empty, false, false, Remove: true);
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Modeless windows get no IsCancel handling, so Escape closes the dialog here. Enter reaches
    /// OK through IsDefault.
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            Close();
        }
    }
}

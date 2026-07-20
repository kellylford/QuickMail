using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace EditBoxTest;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Enable spell-check the same way QuickMail does: in code, deferred to
        // Background dispatcher priority (see ComposeWindow.EnableSpellCheckDeferred).
        // The SpellCheckCheck box is checked by default, so this matches QuickMail.
        EnableSpellCheckDeferred();

        Loaded += (_, _) => BodyBox.Focus();
    }

    // Both editors have AcceptsTab="True", which traps Tab inside them, so give
    // each field a direct jump key. Alt+digit arrives as e.SystemKey while
    // e.Key is Key.System.
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0)
            return;

        UIElement? target = e.SystemKey switch
        {
            Key.D1 => BodyBox,
            Key.D2 => RichBodyBox,
            Key.D3 => DisableImeCheck,
            Key.D4 => SpellCheckCheck,
            _ => null
        };

        if (target != null)
        {
            target.Focus();
            e.Handled = true;
        }
    }

    private void EnableSpellCheckDeferred()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!IsLoaded) return;
            SetSpellCheck(true);
        }));
    }

    private void SetSpellCheck(bool enabled)
    {
        foreach (var editor in new TextBoxBase[] { BodyBox, RichBodyBox })
            SpellCheck.SetIsEnabled(editor, enabled);
    }

    // Candidate fix from issue #300: taking the editors off WPF's TSF path so
    // typed characters flow as plain WM_CHAR, which is what NVDA's typed-word
    // echo watches. Trade-off: this also disables IME composition on the field.
    // NOTE: observed to make no difference to NVDA word-echo in testing.
    private void DisableImeCheck_Changed(object sender, RoutedEventArgs e)
    {
        bool enableInputMethod = DisableImeCheck.IsChecked != true;
        InputMethod.SetIsInputMethodEnabled(BodyBox, enableInputMethod);
        InputMethod.SetIsInputMethodEnabled(RichBodyBox, enableInputMethod);
    }

    // WPF spell-check runs the NL speller over TSF; toggle it to test whether it
    // is what forces the TSF composition path that swallows the space's WM_CHAR.
    private void SpellCheckCheck_Changed(object sender, RoutedEventArgs e)
    {
        // Guard: the Checked event can fire from XAML init before the boxes exist.
        if (BodyBox == null || RichBodyBox == null) return;
        SetSpellCheck(SpellCheckCheck.IsChecked == true);
    }
}

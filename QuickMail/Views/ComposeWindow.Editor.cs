using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using QuickMail.Controls;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Views;

// The Plain Text and Markdown body, behind IPlainTextEditor (#1099). With "Use the new message
// editor" off it is the WPF EditorTextBox the window has always had (BodyBox). With it on, BodyBox
// stays collapsed and unbound and a hosted WinUI editor takes its place in the same grid cell.
// HTML mode is RichBodyBox either way.
public partial class ComposeWindow
{
    private IPlainTextEditor _plainBody = null!; // set by InitializePlainBody, in the constructor
    private WinUiEditorHost? _newEditor;

    // One spell checker for the process: creating Windows' is a COM activation.
    private static WindowsSpellChecker? s_windowsSpeller;

    /// <summary>The Plain Text and Markdown editor in use.</summary>
    private IPlainTextEditor PlainBody => _plainBody;

    /// <summary>The plain editor as an element, for focus moves.</summary>
    private UIElement PlainBodyElement => (UIElement?)_newEditor ?? BodyBox;

    /// <summary>True when this window's plain body is the hosted WinUI editor.</summary>
    internal bool UsesNewEditor => _newEditor != null;

    private void InitializePlainBody(ConfigModel config)
    {
        if (config.UseNewEditor && TryCreateNewEditor())
            return;

        _plainBody = new WpfTextEditor(BodyBox);
        BodyBox.SelectionChanged += BodyBox_SelectionChanged;
    }

    /// <summary>
    /// Puts the hosted WinUI editor in BodyBox's place. If WinUI cannot start (a damaged install,
    /// a missing runtime file), the window falls back to the WPF editor and says so in the log:
    /// a compose window with no body is never acceptable.
    /// </summary>
    private bool TryCreateNewEditor()
    {
        WinUiEditorHost host;
        try
        {
            s_windowsSpeller ??= new WindowsSpellChecker();
            var spelling = new PlainTextSpelling(s_windowsSpeller, word => _customDictionary?.Contains(word) == true);
            host = new WinUiEditorHost("Message body", spelling)
            {
                Margin = BodyBox.Margin,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            System.Windows.Input.KeyboardNavigation.SetTabIndex(host, BodyBox.TabIndex);
            host.SetText(_vm.Body);
        }
#pragma warning disable CA1031 // any failure to start WinUI must fall back to the WPF editor, whatever it throws
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogService.Log("The new message editor could not start; using the standard editor", ex);
            return false;
        }

        // BodyBox stays in the tree, collapsed and unbound, so nothing that names it breaks.
        BindingOperations.ClearBinding(BodyBox, TextBox.TextProperty);
        BodyBox.Visibility = Visibility.Collapsed;
        Grid.SetRow(host, Grid.GetRow(BodyBox));
        BodyEditorGrid.Children.Add(host);

        _newEditor = host;
        _plainBody = host;

        // The two-way binding BodyBox had, by hand. WinUI raises TextChanged after the fact, so
        // both directions compare texts and no flag is held across a call.
        host.TextChanged += (_, _) =>
        {
            var text = host.Text;
            if (!string.Equals(text, NormalizeLineBreaks(_vm.Body), StringComparison.Ordinal))
                _vm.Body = text;
        };
        _vm.PropertyChanged += OnViewModelBodyChanged;
        Closed += (_, _) => _vm.PropertyChanged -= OnViewModelBodyChanged;

        host.SelectionChanged += (_, _) => BodyBox_SelectionChanged(host, new RoutedEventArgs());
        host.PreviewKeyDown += BodyBox_PreviewKeyDown;
        host.TabPressed = HandleBodyTab;
        host.FilesDropped += async files => await TakeFilesIntoBodyAsync(files.ToList());
        // Windows' wavy underlines stay off for now. In this control they come with Windows'
        // autocorrect ("recieve" became "receive" as it was typed, with nothing said), and the two
        // cannot be had apart. QuickMail's own spelling announcements, Next Misspelling and Check
        // Spelling ask Windows' spell checker directly and do not need the underlines. Whether to
        // turn them on is an open question for the experiment (#1099).
        host.IsSpellCheckEnabled = false;

        ApplyNewEditorTheme();
        if (_themeService != null)
        {
            _themeService.ThemeChanged += OnThemeChangedRefreshNewEditor;
            Closed += (_, _) => _themeService.ThemeChanged -= OnThemeChangedRefreshNewEditor;
        }
        return true;
    }

    private void OnViewModelBodyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(_vm.Body) || _newEditor == null) return;
        if (!string.Equals(_newEditor.Text, NormalizeLineBreaks(_vm.Body), StringComparison.Ordinal))
            _newEditor.SetText(_vm.Body);
    }

    /// <summary>Every line break as "\r\n", which is how the hosted editor reports its text.</summary>
    internal static string NormalizeLineBreaks(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (text.IndexOfAny(LineBreakChars) < 0) return text;
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
                   .Replace("\n", "\r\n", StringComparison.Ordinal);
    }

    private static readonly char[] LineBreakChars = { '\r', '\n' };

    private void OnThemeChangedRefreshNewEditor(object? sender, EventArgs e) => ApplyNewEditorTheme();

    /// <summary>Gives the hosted editor the colors and font BodyBox's style would have had.</summary>
    private void ApplyNewEditorTheme()
    {
        if (_newEditor == null) return;
        Color Token(string key, Color fallback) =>
            TryFindResource(key) is SolidColorBrush brush ? brush.Color : fallback;

        _newEditor.ApplyColors(
            Token("Theme.InputBackground", SystemColors.WindowColor),
            Token("Theme.TextPrimary", SystemColors.WindowTextColor),
            Token("Theme.InputBorder", SystemColors.ActiveBorderColor),
            Token("Theme.Accent", SystemColors.HighlightColor));
        _newEditor.SetAcceleratorKey(_registry.GetGestureText("compose.focusBody"));
    }

    /// <summary>Shows the plain editor in Plain Text and Markdown, and hides it in HTML mode.</summary>
    private void SetPlainBodyVisibility(bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (_newEditor != null) _newEditor.Visibility = visibility;
        else BodyBox.Visibility = visibility;
    }

    private void SetPlainBodyFont(FontFamily family)
    {
        if (_newEditor != null) _newEditor.SetFont(family.Source, BodyBox.FontSize);
        else BodyBox.FontFamily = family;
    }
}

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

    // The hosted editor's text as it was when it and the view model last agreed. The editor
    // reports its text in its own form (line breaks as "\r\n", whatever else the control changes
    // on the way in), so "has the user edited it" is asked against this, never against Body.
    private string _syncedBody = string.Empty;

    // The font the current mode asked for, kept so a theme or text-size change can reapply it.
    private FontFamily? _plainBodyFont;

    // One spell checker for the process: creating Windows' is a COM activation.
    private static WindowsSpellChecker? s_windowsSpeller;

    /// <summary>
    /// The editor the text commands work on: the Plain Text and Markdown editor, or, in HTML mode
    /// with the hosted rich editor in use, that one (for spelling and Alt+1/2/3, which work on
    /// text and positions and so are the same code in every mode).
    /// </summary>
    private IPlainTextEditor PlainBody =>
        _vm.CurrentMode == ComposeMode.Html && _newRich != null ? _newRich : _plainBody;

    /// <summary>The plain editor as an element, for focus moves.</summary>
    private UIElement PlainBodyElement => (UIElement?)_newEditor ?? BodyBox;

    /// <summary>True when this window's plain body is the hosted WinUI editor.</summary>
    internal bool UsesNewEditor => _newEditor != null;

    /// <summary>
    /// False keeps every compose window on the WPF editors whatever the setting says. The test
    /// assembly turns it off for the whole run: its compose tests drive BodyBox and RichBodyBox
    /// by name, which the setting-off path still is, and a test host is no place to start WinUI.
    /// Tests of the new editor turn it on for themselves.
    /// </summary>
    internal static bool NewEditorAllowed { get; set; } = true;

    private void InitializePlainBody(ConfigModel config)
    {
        if (NewEditorAllowed && config.UseNewEditor && TryCreateNewEditor())
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
            var spelling = new PlainTextSpelling(s_windowsSpeller, IsKnownWord);
            host = new WinUiEditorHost("Message body", spelling)
            {
                Margin = BodyBox.Margin,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            System.Windows.Input.KeyboardNavigation.SetTabIndex(host, BodyBox.TabIndex);
            host.SetText(_vm.Body);
            _syncedBody = host.Text;
            LogService.Debug($"New editor: body of {_vm.Body.Length} characters loaded as {_syncedBody.Length}.");
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

        // The two-way binding BodyBox had, by hand.
        host.TextChanged += OnNewEditorTextChanged;
        _vm.PropertyChanged += OnViewModelBodyChanged;
        host.SelectionChanged += OnNewEditorSelectionChanged;
        host.PreviewKeyDown += BodyBox_PreviewKeyDown;
        host.TabPressed = HandleBodyTab;
        host.FilesDropped += OnNewEditorFilesDropped;
        host.HostingFailed += OnNewEditorHostingFailed;
        host.ContextMenuItemsRequested = NewEditorSpellingMenuItems;
        // Windows' wavy underlines stay off for now. In this control they come with Windows'
        // autocorrect ("recieve" became "receive" as it was typed, with nothing said), and the two
        // cannot be had apart. QuickMail's own spelling announcements, Next Misspelling and Check
        // Spelling ask Windows' spell checker directly and do not need the underlines. Whether to
        // turn them on is an open question for the experiment (#1099).
        host.IsSpellCheckEnabled = false;

        ApplyNewEditorTheme();
        // After the window's own key handlers (wired in XAML), so those still go first.
        PreviewKeyDown += HostedFocus_PreviewKeyDown;
        KeyDown += HostedFocus_KeyDown;
        _registry.BindingsChanged += OnBindingsChangedRefreshNewEditor;
        if (_themeService != null)
            _themeService.ThemeChanged += OnThemeChangedRefreshNewEditor;
        Closed += (_, _) => ReleaseNewEditor();
        return true;
    }

    /// <summary>
    /// The window has closed: drop every subscription that points at the hosted editor and release
    /// its island. WPF would not dispose it until QuickMail exits.
    /// </summary>
    private void ReleaseNewEditor()
    {
        _vm.PropertyChanged -= OnViewModelBodyChanged;
        PreviewKeyDown -= HostedFocus_PreviewKeyDown;
        KeyDown -= HostedFocus_KeyDown;
        _registry.BindingsChanged -= OnBindingsChangedRefreshNewEditor;
        if (_themeService != null)
            _themeService.ThemeChanged -= OnThemeChangedRefreshNewEditor;
        if (_newEditor is { } host)
        {
            host.PreviewKeyDown -= BodyBox_PreviewKeyDown;
            host.Close();   // clears the editor's own events and disposes the island
        }
        if (_newRich is { } rich)
        {
            rich.PreviewKeyDown -= NewRich_PreviewKeyDown;
            rich.Close();
        }
    }

    // Words the user chose Ignore All for in this message.
    private readonly System.Collections.Generic.HashSet<string> _ignoredWords = new(StringComparer.Ordinal);

    /// <summary>A word the hosted editors do not count as misspelled: in the custom dictionary, or ignored in this message.</summary>
    private bool IsKnownWord(string word) =>
        _ignoredWords.Contains(word) || _customDictionary?.Contains(word) == true;

    /// <summary>
    /// What the hosted editor's context menu offers above Cut, Copy and Paste when the caret is
    /// on a misspelled word: the suggestions, Ignore All, and Add to Dictionary. The WPF editors
    /// get the first two from WPF's own menu.
    /// </summary>
    private System.Collections.Generic.List<(string Text, Action Run)> NewEditorSpellingMenuItems()
    {
        var items = new System.Collections.Generic.List<(string Text, Action Run)>();
        // As the announcements: nothing while "Check spelling as I type" is off and no check has been asked for.
        if (!_spellCheckEnabled && !_checkSpellingAsYouType) return items;
        var editor = PlainBody;
        var text = editor.Text;
        if (text.Length == 0) return items;

        // The caret may sit just past the word's last letter.
        int caret = Math.Min(editor.CaretIndex, text.Length - 1);
        int start = editor.GetSpellingErrorStart(caret);
        int probe = caret;
        if (start < 0 && caret > 0)
        {
            probe = caret - 1;
            start = editor.GetSpellingErrorStart(probe);
        }
        if (start < 0 || SpellScan.IsInAddress(text, start)) return items;
        int length = editor.GetSpellingErrorLength(probe);
        if (length <= 0 || start + length > text.Length) return items;
        var word = text.Substring(start, length);

        foreach (var suggestion in (editor.GetSpellingSuggestions(probe) ?? Array.Empty<string>()).Take(5))
            items.Add((suggestion, () => ReplaceMisspellingFromMenu(start, word, suggestion)));
        items.Add(("Ignore All", () => _ignoredWords.Add(word)));
        if (_customDictionary != null)
            items.Add(("Add to Dictionary", () => _customDictionary.AddWord(word)));
        return items;
    }

    private void ReplaceMisspellingFromMenu(int start, string word, string replacement)
    {
        var editor = PlainBody;
        var text = editor.Text;
        // The menu was open a while; only replace what is still there.
        if (start + word.Length > text.Length || !text.AsSpan(start, word.Length).SequenceEqual(word)) return;
        var wasSuppressed = _suppressSpellingAnnouncement;
        _suppressSpellingAnnouncement = true;
        try
        {
            editor.Select(start, word.Length);
            editor.SelectedText = replacement;
            editor.Select(start + replacement.Length, 0);
        }
        finally { _suppressSpellingAnnouncement = wasSuppressed; }
        ClearSpellingContext();
        if (NewRich != null) _vm.MarkBodyDirty();
        AccessibilityHelper.Announce(this, $"Replaced with {replacement}.", category: AnnouncementCategory.Result);
    }

    private void OnNewEditorSelectionChanged(object? sender, EventArgs e) =>
        BodyBox_SelectionChanged(sender ?? this, new RoutedEventArgs());

    private async void OnNewEditorFilesDropped(System.Collections.Generic.IReadOnlyList<string> files) =>
        await TakeFilesIntoBodyAsync(files.ToList());

    /// <summary>
    /// The editor's text changed. WinUI raises this after the fact, from its own queue, so no flag
    /// can be held across the call that caused it: the text is compared with what was last agreed.
    /// </summary>
    private void OnNewEditorTextChanged(object? sender, EventArgs e) => FlushNewEditorBody();

    /// <summary>
    /// Writes the hosted editor's text to the view model now, if the user has changed it. Called
    /// when the editor reports a change, and before anything that reads the body (a key that may
    /// be a command, closing, an auto-save), because the editor's own report can still be queued.
    /// </summary>
    private void FlushNewEditorBody()
    {
        if (_newEditor == null) return;
        var text = _newEditor.Text;
        if (string.Equals(text, _syncedBody, StringComparison.Ordinal)) return;
        _syncedBody = text;
        _vm.Body = text;
    }

    private void OnViewModelBodyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(_vm.Body) || _newEditor == null) return;
        // Our own write coming back, or the same text the editor already shows.
        if (string.Equals(_vm.Body, _syncedBody, StringComparison.Ordinal)) return;
        if (string.Equals(NormalizeLineBreaks(_vm.Body), _newEditor.Text, StringComparison.Ordinal))
        {
            _syncedBody = _newEditor.Text;
            return;
        }
        _newEditor.SetText(_vm.Body);
        _syncedBody = _newEditor.Text;
    }

    /// <summary>
    /// The island could not be created at the window's first layout. Put the WPF editor back with
    /// the text the view model holds, so the window still has a body.
    /// </summary>
    private void OnNewEditorHostingFailed(Exception ex)
    {
        LogService.Log("The new message editor could not be hosted; using the standard editor", ex);
        if (_newEditor is not { } host) return;
        bool hadFocus = host.IsKeyboardFocusWithin;
        ReleaseNewEditor();
        BodyEditorGrid.Children.Remove(host);
        _newEditor = null;

        _plainBody = new WpfTextEditor(BodyBox);
        BodyBox.SetBinding(TextBox.TextProperty,
            new Binding(nameof(_vm.Body)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        BodyBox.SelectionChanged += BodyBox_SelectionChanged;
        BodyBox.Visibility = _vm.CurrentMode == ComposeMode.Html ? Visibility.Collapsed : Visibility.Visible;
        if (_plainBodyFont != null) BodyBox.FontFamily = _plainBodyFont;
        if (hadFocus) BodyBox.Focus();
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

    private void OnBindingsChangedRefreshNewEditor(object? sender, EventArgs e)
    {
        var key = _registry.GetGestureText("compose.focusBody");
        _newEditor?.SetAcceleratorKey(key);
        _newRich?.SetAcceleratorKey(key);
    }

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
        // BodyBox is collapsed but still in the tree, so its inherited font size is the theme's.
        _newEditor.SetFont((_plainBodyFont ?? BodyBox.FontFamily).Source, BodyBox.FontSize);

        if (_newRich == null) return;
        _newRich.ApplyColors(
            Token("Theme.InputBackground", SystemColors.WindowColor),
            Token("Theme.TextPrimary", SystemColors.WindowTextColor),
            Token("Theme.InputBorder", SystemColors.ActiveBorderColor),
            Token("Theme.Accent", SystemColors.HighlightColor));
        _newRich.SetAcceleratorKey(_registry.GetGestureText("compose.focusBody"));
        // As the WPF rich editor's document: Segoe UI at the body size.
        _newRich.SetFont("Segoe UI", BodyBox.FontSize);
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
        _plainBodyFont = family;
        if (_newEditor != null) _newEditor.SetFont(family.Source, BodyBox.FontSize);
        else BodyBox.FontFamily = family;
    }
}

using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

public partial class ComposeWindow : Window
{
    private readonly ComposeViewModel   _vm;
    private readonly IContactService    _contactService;
    private readonly ITemplateService   _templateService;
    private readonly IConfigService     _configService;
    private readonly CommandRegistry    _registry = new();
    private TextBox? _activeAddressBox;
    private CancellationTokenSource? _autocompleteCts;
    private int _lastAnnouncedSpellingIndex = -1;

    // Track the current spelling error so Alt+1/2/3 can replace it with a suggestion.
    private int _currentSpellingWordStart = -1;
    private int _currentSpellingWordEnd = -1;
    private System.Collections.Generic.List<string>? _currentSpellingSuggestions;

    // Suppress spelling announcements during programmatic text changes (e.g. Alt+1 replacement).
    private bool _suppressSpellingAnnouncement;

    // Set to true in PreviewKeyDown when a character-generating key is pressed (typing).
    // BodyBox_SelectionChanged skips spelling announcements while this is true; if the
    // AnnounceSpellingWhileTyping setting is on, a debounce timer fires instead after
    // the user pauses so the full word (not a partial) is what gets announced.
    private bool _caretMovedByTyping;

    // Debounce timer for spelling announcements while typing. Resets on every character
    // keystroke; fires ~500 ms after the user pauses, by which point the word is complete.
    private DispatcherTimer? _spellingTypingTimer;
    private static readonly TimeSpan SpellingTypingDelay = TimeSpan.FromMilliseconds(500);

    public ComposeWindow(ComposeViewModel vm, IContactService contactService, ITemplateService templateService, IConfigService configService)
    {
        _vm = vm;
        _contactService = contactService;
        _templateService = templateService;
        _configService = configService;
        InitializeComponent();
        DataContext = vm;

        // ── Compose command palette ──────────────────────────────────────────────
        RegisterComposeCommands();

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.StatusText) && !string.IsNullOrEmpty(vm.StatusText))
                AccessibilityHelper.Announce(this, vm.StatusText, category: AnnouncementCategory.Status);
        };

        // Wire the View confirmation callback so the VM stays out of System.Windows.
        // Mirrors the pattern used by MainViewModel.ConfirmationRequested.
        vm.ConfirmationRequested = (message, title) =>
            MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning)
            == MessageBoxResult.Yes;

        // Wire the template picker so the VM stays out of System.Windows.
        vm.InsertTemplateRequested += () =>
        {
            var pickerVm = new TemplatePickerViewModel(_templateService);
            var dialog = new TemplatePickerWindow(pickerVm) { Owner = this };
            if (dialog.ShowDialog() == true)
                return Task.FromResult(dialog.SelectedTemplate);
            return Task.FromResult<MessageTemplate?>(null);
        };

        foreach (var box in new[] { ToBox, CcBox, BccBox })
        {
            box.TextChanged       += AddressBox_TextChanged;
            box.PreviewKeyDown    += AddressBox_PreviewKeyDown;
            box.LostKeyboardFocus += AddressBox_LostKeyboardFocus;
        }

        // Reply / Reply-All: To is already filled in, so land in the body at the top.
        // New compose / Forward: To is empty, so land in the To field.
        Loaded += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_vm.To))
            {
                ToBox.Focus();
            }
            else
            {
                BodyBox.Focus();
                BodyBox.CaretIndex = 0;
            }
        };
        BodyBox.SelectionChanged += BodyBox_SelectionChanged;
        Closing += OnWindowClosing;
    }

    // ── Autocomplete ─────────────────────────────────────────────────────────

    private async void AddressBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _activeAddressBox = (TextBox)sender;
        var searchToken = GetCurrentToken(_activeAddressBox.Text, _activeAddressBox.CaretIndex);
        if (searchToken.Length < 1) { AutoCompletePopup.IsOpen = false; return; }

        // Cancel any previous search and create a new cancellation token
        _autocompleteCts?.Cancel();
        _autocompleteCts = new CancellationTokenSource();

        try
        {
            var results = await _contactService.SearchContactsAsync(searchToken, _autocompleteCts.Token);
            if (results.Count == 0) { AutoCompletePopup.IsOpen = false; return; }

            SuggestionList.ItemsSource = results;
            AutoCompletePopup.PlacementTarget = _activeAddressBox;
            AutoCompletePopup.Placement       = PlacementMode.Bottom;
            AutoCompletePopup.IsOpen          = true;

            AccessibilityHelper.Announce(this,
                results.Count == 1 ? "1 suggestion" : $"{results.Count} suggestions",
                category: AnnouncementCategory.Result);
        }
        catch (OperationCanceledException)
        {
            // Search was cancelled by a more recent keystroke, ignore
        }
    }

    private void AddressBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!AutoCompletePopup.IsOpen) return;
        if (e.Key == Key.Down)
        {
            SuggestionList.Focus();
            SuggestionList.SelectedIndex = 0;
            (SuggestionList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            AutoCompletePopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private void AddressBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (!SuggestionList.IsKeyboardFocusWithin)
                AutoCompletePopup.IsOpen = false;
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    internal void SuggestionList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Tab)
        {
            if (SuggestionList.SelectedItem is ContactModel c) AcceptSuggestion(c);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            AutoCompletePopup.IsOpen = false;
            _activeAddressBox?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Up && SuggestionList.SelectedIndex <= 0)
        {
            AutoCompletePopup.IsOpen = false;
            _activeAddressBox?.Focus();
            e.Handled = true;
        }
    }

    internal void SuggestionList_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (SuggestionList.SelectedItem is ContactModel c) AcceptSuggestion(c);
    }

    private void AcceptSuggestion(ContactModel contact)
    {
        if (_activeAddressBox == null) return;
        var text  = _activeAddressBox.Text;
        var caret = _activeAddressBox.CaretIndex;
        var sub   = text[..caret];
        var lastComma = sub.LastIndexOf(',');
        var lastSemi = sub.LastIndexOf(';');
        var last = Math.Max(lastComma, lastSemi);
        // Determine which separator to use: whatever was last used, or default to semicolon
        var separator = lastSemi > lastComma ? ";" : ",";
        var prefix = last < 0 ? string.Empty : text[..(last + 1)] + " ";
        var suffix = text[caret..].TrimStart();
        _activeAddressBox.Text       = prefix + contact.Display + separator + " " + suffix;
        _activeAddressBox.CaretIndex = (prefix + contact.Display + separator + " ").Length;
        AutoCompletePopup.IsOpen     = false;
        _activeAddressBox.Focus();
    }

    private static string GetCurrentToken(string text, int caretIndex)
    {
        var sub  = text[..caretIndex];
        var last = Math.Max(sub.LastIndexOf(','), sub.LastIndexOf(';'));
        return last < 0 ? sub.Trim() : sub[(last + 1)..].Trim();
    }

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        // If the message was sent, or nothing was edited, let the window close freely.
        if (_vm.IsSent || !_vm.IsDirty)
            return;

        // Prevent the window from closing until the user decides.
        e.Cancel = true;

        var result = MessageBox.Show(
            this,
            "Do you want to save this message as a draft before closing?",
            "Save Draft?",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Cancel)
            return; // stay open

        if (result == MessageBoxResult.No)
        {
            // Synchronous path — still inside the original Close() call stack.
            // Setting e.Cancel = false lets that Close() proceed normally
            // without a nested Close() call, which would crash WPF.
            Closing -= OnWindowClosing;
            e.Cancel = false;
            return;
        }

        // result == Yes: save the draft first
        await _vm.SaveDraftCommand.ExecuteAsync(null);
        // Only close if the save succeeded (status won't say "failed")
        if (_vm.StatusText.Contains("failed", System.StringComparison.OrdinalIgnoreCase))
            return;

        // After an await the original Close() has already returned (e.Cancel was true),
        // so we need a fresh Close() here to actually close the window.
        Closing -= OnWindowClosing;
        Close();
    }

    // Delete key removes selected attachment from the compose list.
    private void AttachmentList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && AttachmentList.SelectedItem is AttachmentModel a)
        {
            _vm.RemoveAttachmentCommand.Execute(a);
            e.Handled = true;
        }
    }

    // Drag-and-drop: accept file drops anywhere on the compose window.
    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            foreach (var f in files)
                await _vm.AddAttachmentFromPathAsync(f);
    }

    // Alt+U → Subject field; Alt+M → From combo; Alt+Y → Body; Ctrl+V with files → add attachments; Escape → cancel.
    // Ctrl+Shift+P → Command Palette; F7 → next misspelling; Shift+F7 → previous misspelling.
    // Ctrl+Enter → Send message (secondary shortcut alongside Alt+S).
    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+Shift+P: open the command palette
        if (e.Key == Key.P && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            OpenCommandPalette();
            e.Handled = true;
            return;
        }

        // Ctrl+Enter: send the message (secondary shortcut alongside Alt+S)
        if (e.Key == Key.Return && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _vm.SendCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Ctrl+V: if the clipboard contains files, paste them as attachments
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && Clipboard.ContainsFileDropList())
        {
            foreach (string? f in Clipboard.GetFileDropList())
            {
                if (f != null) await _vm.AddAttachmentFromPathAsync(f);
            }
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            _vm.CancelCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.SystemKey == Key.U && (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            SubjectBox.Focus();
            SubjectBox.SelectAll();
            e.Handled = true;
        }
        else if (e.SystemKey == Key.M && (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            FromCombo.Focus();
            FromCombo.IsDropDownOpen = true;
            e.Handled = true;
        }
        else if (e.SystemKey == Key.Y && (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            BodyBox.Focus();
            e.Handled = true;
        }

        // ── Registry-based compose commands ──────────────────────────────────
        var key = e.Key == Key.System ? e.SystemKey
            : e.Key == Key.ImeProcessed ? e.ImeProcessedKey
            : e.Key;
        var modifiers = Keyboard.Modifiers;
        var cmd = _registry.FindByGesture(key, modifiers);
        if (cmd != null && (cmd.IsAvailable?.Invoke() ?? true))
        {
            cmd.Execute();
            e.Handled = true;
        }
    }

    /// <summary>
    /// When the caret moves into a misspelled word during normal cursor navigation,
    /// announce it to screen readers so users hear about spelling errors without
    /// needing to press F7. WPF's SpellCheck shows red squiggly underlines visually
    /// but does not expose them through UIA, so we detect them manually.
    /// </summary>
    private void BodyBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressSpellingAnnouncement) return;
        // Typing: always skip the immediate check here. If AnnounceSpellingWhileTyping is
        // on, the debounce timer fires AnnounceSpellingAtCurrentPosition after the user pauses.
        if (_caretMovedByTyping) return;
        // Navigation: skip if setting is off.
        if (!_configService.Load().AnnounceSpellingWhileNavigating) return;
        AnnounceSpellingAtCurrentPosition();
    }

    private void ClearSpellingContext()
    {
        _lastAnnouncedSpellingIndex = -1;
        _currentSpellingWordStart = -1;
        _currentSpellingWordEnd = -1;
        _currentSpellingSuggestions = null;
    }

    // Core spelling check: finds the misspelled word at the current caret position and
    // announces it. Called by BodyBox_SelectionChanged (navigation) and by the typing
    // debounce timer (after a pause while typing with AnnounceSpellingWhileTyping on).
    private void AnnounceSpellingAtCurrentPosition()
    {
        var text = BodyBox.Text;
        if (string.IsNullOrEmpty(text)) return;

        int caret = BodyBox.CaretIndex;
        int checkIndex = (caret > 0 && caret == text.Length) ? caret - 1 : caret;
        if (checkIndex < 0 || checkIndex >= text.Length)
        {
            ClearSpellingContext();
            return;
        }

        var error = BodyBox.GetSpellingError(checkIndex);
        if (error == null)
        {
            ClearSpellingContext();
            return;
        }

        int wordStart = checkIndex;
        while (wordStart > 0 && !char.IsWhiteSpace(text[wordStart - 1]))
            wordStart--;
        int wordEnd = checkIndex;
        while (wordEnd < text.Length && !char.IsWhiteSpace(text[wordEnd]))
            wordEnd++;

        if (wordStart == _lastAnnouncedSpellingIndex) return;
        _lastAnnouncedSpellingIndex = wordStart;

        var word = text.Substring(wordStart, wordEnd - wordStart);
        var suggestions = error.Suggestions.Take(3).ToList();

        _currentSpellingWordStart = wordStart;
        _currentSpellingWordEnd = wordEnd;
        _currentSpellingSuggestions = suggestions;

        AccessibilityHelper.Announce(this, BuildSpellingAnnouncement(word, suggestions),
            category: AnnouncementCategory.Result);
    }

    // Starts or resets the debounce timer used when AnnounceSpellingWhileTyping is on.
    // Each character keystroke resets the timer; it fires once the user pauses typing.
    private void ResetSpellingTypingTimer()
    {
        if (_spellingTypingTimer == null)
        {
            _spellingTypingTimer = new DispatcherTimer { Interval = SpellingTypingDelay };
            _spellingTypingTimer.Tick += (_, _) =>
            {
                _spellingTypingTimer!.Stop();
                AnnounceSpellingAtCurrentPosition();
            };
        }
        _spellingTypingTimer.Stop();
        _spellingTypingTimer.Start();
    }

    /// <summary>
    /// Builds the spelling announcement string. When AnnounceSpellingSuggestions
    /// is on, includes up to 3 suggestions. When off, only announces the
    /// misspelled word. Experienced users can press Alt+1/2/3 to replace.
    /// </summary>
    private string BuildSpellingAnnouncement(string word, System.Collections.Generic.List<string> suggestions)
    {
        var announceSuggestions = _configService.Load().AnnounceSpellingSuggestions;

        if (!announceSuggestions || suggestions.Count == 0)
            return $"Misspelling: {word}.";

        return $"Misspelling: {word}. {string.Join(", ", suggestions)}.";
    }

    // Returns true when the key generates a character in the TextBox (i.e. the user is
    // mid-word typing), so BodyBox_SelectionChanged can suppress spelling announcements.
    // Navigation, control, modifier, and function keys return false.
    private static bool IsTypingKey(Key key, ModifierKeys modifiers)
    {
        // Ctrl/Alt combos are shortcuts, never character input
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != ModifierKeys.None)
            return false;

        return key switch
        {
            Key.Back or Key.Delete or Key.Return or Key.Tab or Key.Escape or Key.Space or
            Key.Left or Key.Right or Key.Up or Key.Down or
            Key.Home or Key.End or Key.PageUp or Key.PageDown or
            Key.F1  or Key.F2  or Key.F3  or Key.F4  or Key.F5  or Key.F6  or
            Key.F7  or Key.F8  or Key.F9  or Key.F10 or Key.F11 or Key.F12 or
            Key.Insert or Key.PrintScreen or Key.Pause or Key.Scroll or
            Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or
            Key.LeftAlt   or Key.RightAlt   or Key.LWin or Key.RWin or
            Key.CapsLock  or Key.NumLock    or Key.Apps or Key.Sleep => false,
            _ => true   // all other keys produce a character
        };
    }

    /// <summary>
    /// F7 moves to the next misspelled word; Shift+F7 moves to the previous one.
    /// Announces the misspelling to screen readers so users know what needs correction.
    /// </summary>
    private void BodyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Track whether this keystroke is character-generating (typing) or navigation so
        // BodyBox_SelectionChanged can suppress mid-word spelling announcements.
        _caretMovedByTyping = IsTypingKey(e.Key, e.KeyboardDevice.Modifiers);
        if (_caretMovedByTyping && _configService.Load().AnnounceSpellingWhileTyping)
            ResetSpellingTypingTimer();     // fires after user pauses — announces complete word
        else if (!_caretMovedByTyping)
            _spellingTypingTimer?.Stop();   // navigation takes over; cancel any pending timer

        // Alt+1/2/3: replace the current misspelled word with the corresponding suggestion.
        // Only works when the caret is on a spelling error that was just announced.
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0
            && _currentSpellingSuggestions is { Count: > 0 }
            && _currentSpellingWordStart >= 0
            && _currentSpellingWordEnd > _currentSpellingWordStart)
        {
            int suggestionIndex = e.SystemKey switch
            {
                Key.D1 => 0,
                Key.D2 => 1,
                Key.D3 => 2,
                _ => -1
            };
            if (suggestionIndex >= 0 && suggestionIndex < _currentSpellingSuggestions.Count)
            {
                var replacement = _currentSpellingSuggestions[suggestionIndex];
                var text = BodyBox.Text;
                var before = text[.._currentSpellingWordStart];
                var after = text[_currentSpellingWordEnd..];

                // Suppress spelling announcements while we programmatically change
                // the text and caret position. Setting BodyBox.Text fires
                // SelectionChanged before CaretIndex is applied, which would
                // otherwise announce the first error on the page.
                _suppressSpellingAnnouncement = true;
                BodyBox.Text = before + replacement + after;
                BodyBox.CaretIndex = _currentSpellingWordStart + replacement.Length;
                _suppressSpellingAnnouncement = false;

                BodyBox.Focus();
                AccessibilityHelper.Announce(this,
                    $"Replaced with {replacement}.",
                    category: AnnouncementCategory.Result);
                ClearSpellingContext();
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.F7)
        {
            var forward = (Keyboard.Modifiers & ModifierKeys.Shift) == 0;
            NavigateSpellingError(forward);
            e.Handled = true;
        }
    }

    // ── Command Palette ──────────────────────────────────────────────────────

    private void RegisterComposeCommands()
    {
        _registry.Register(new CommandDefinition(
            id: "compose.send", category: "Compose", title: "Send Message",
            execute: () => _vm.SendCommand.Execute(null),
            defaultKey: Key.S, defaultModifiers: ModifierKeys.Alt));

        _registry.Register(new CommandDefinition(
            id: "compose.saveDraft", category: "Compose", title: "Save Draft",
            execute: () => _vm.SaveDraftCommand.Execute(null),
            defaultKey: Key.S, defaultModifiers: ModifierKeys.Control));

        _registry.Register(new CommandDefinition(
            id: "compose.addAttachments", category: "Compose", title: "Add Attachments…",
            execute: () => _vm.AddAttachmentsCommand.Execute(null),
            defaultKey: Key.A, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift));

        _registry.Register(new CommandDefinition(
            id: "compose.insertTemplate", category: "Compose", title: "Insert Template…",
            execute: () => _vm.InsertTemplateCommand.Execute(null)));

        _registry.Register(new CommandDefinition(
            id: "compose.saveAsTemplate", category: "Compose", title: "Save as Template",
            execute: () => _vm.SaveAsTemplateCommand.Execute(null)));

        _registry.Register(new CommandDefinition(
            id: "compose.cancel", category: "Compose", title: "Cancel / Close",
            execute: () => _vm.CancelCommand.Execute(null),
            defaultKey: Key.Escape, defaultModifiers: ModifierKeys.None));

        _registry.Register(new CommandDefinition(
            id: "compose.focusBody", category: "Compose", title: "Focus Message Body",
            execute: () => BodyBox.Focus(),
            defaultKey: Key.Y, defaultModifiers: ModifierKeys.Alt));

        _registry.Register(new CommandDefinition(
            id: "compose.nextMisspelling", category: "Compose", title: "Next Misspelling",
            execute: () => NavigateSpellingError(forward: true),
            defaultKey: Key.F7, defaultModifiers: ModifierKeys.None));

        _registry.Register(new CommandDefinition(
            id: "compose.prevMisspelling", category: "Compose", title: "Previous Misspelling",
            execute: () => NavigateSpellingError(forward: false),
            defaultKey: Key.F7, defaultModifiers: ModifierKeys.Shift));

        _registry.Register(new CommandDefinition(
            id: "compose.toggleSpellingAnnouncements", category: "Compose",
            title: "Toggle Spelling Announcements",
            execute: ToggleSpellingAnnouncements));

        _registry.Register(new CommandDefinition(
            id: "compose.repeatSpelling", category: "Compose",
            title: "Repeat Spelling Announcement",
            execute: RepeatSpellingAnnouncement,
            defaultKey: Key.F7, defaultModifiers: ModifierKeys.Alt));
    }

    // Re-announces the spelling error at the current caret position.
    // Clears the dedup guard first so the same word is spoken again even if it was
    // the last word announced (e.g. the user wants to hear the suggestions again).
    private void RepeatSpellingAnnouncement()
    {
        _lastAnnouncedSpellingIndex = -1;
        AnnounceSpellingAtCurrentPosition();
    }

    private void ToggleSpellingAnnouncements()
    {
        var cfg = _configService.Load();
        cfg.AnnounceSpellingWhileNavigating = !cfg.AnnounceSpellingWhileNavigating;
        _configService.Save(cfg);
        var state = cfg.AnnounceSpellingWhileNavigating ? "on" : "off";
        AccessibilityHelper.Announce(this, $"Spelling announcements while navigating {state}.",
            interrupt: true, category: AnnouncementCategory.Result, force: true);
    }

    private void OpenCommandPalette()
    {
        var previousFocus = Keyboard.FocusedElement as IInputElement;
        var palette = new CommandPaletteWindow(_registry) { Owner = this };
        palette.ShowDialog();
        (previousFocus ?? BodyBox).Focus();
    }

    /// <summary>
    /// Programmatically triggers F7-style spelling navigation so the command
    /// palette entry can invoke it without duplicating the search logic.
    /// </summary>
    private void NavigateSpellingError(bool forward)
    {
        var text = BodyBox.Text;
        if (string.IsNullOrEmpty(text)) return;

        int start = BodyBox.CaretIndex;
        int foundIndex = -1;

        if (forward)
        {
            for (int i = start; i < text.Length; i++)
            {
                if (BodyBox.GetSpellingError(i) != null) { foundIndex = i; break; }
            }
        }
        else
        {
            for (int i = Math.Min(start - 1, text.Length - 1); i >= 0; i--)
            {
                if (BodyBox.GetSpellingError(i) != null) { foundIndex = i; break; }
            }
        }

        if (foundIndex >= 0)
        {
            int wordStart = foundIndex;
            while (wordStart > 0 && !char.IsWhiteSpace(text[wordStart - 1])) wordStart--;
            int wordEnd = foundIndex;
            while (wordEnd < text.Length && !char.IsWhiteSpace(text[wordEnd])) wordEnd++;

            BodyBox.SelectionStart = wordStart;
            BodyBox.SelectionLength = wordEnd - wordStart;
            BodyBox.Focus();

            _lastAnnouncedSpellingIndex = wordStart;

            var word = text.Substring(wordStart, wordEnd - wordStart);
            var suggestions = BodyBox.GetSpellingError(foundIndex)
                ?.Suggestions.Take(3).ToList() ?? new System.Collections.Generic.List<string>();

            _currentSpellingWordStart = wordStart;
            _currentSpellingWordEnd = wordEnd;
            _currentSpellingSuggestions = suggestions;

            var announce = BuildSpellingAnnouncement(word, suggestions);
            AccessibilityHelper.Announce(this, announce, category: AnnouncementCategory.Result);
        }
        else
        {
            ClearSpellingContext();
            AccessibilityHelper.Announce(this, "No more misspellings found.",
                category: AnnouncementCategory.Result);
        }
    }
}

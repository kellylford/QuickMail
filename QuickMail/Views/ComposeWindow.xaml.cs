using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using QuickMail.Controls;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

/// <summary>
/// A single item in the autocomplete suggestion list. Wraps either a
/// <see cref="ContactModel"/> (one address) or a <see cref="GroupModel"/>
/// (expands to all member addresses on accept).
/// </summary>
internal sealed class AddressSuggestion
{
    public ContactModel? Contact { get; }
    public GroupModel?   Group   { get; }
    public bool IsGroup => Group is not null;

    /// <summary>Bold first line shown in the popup row.</summary>
    public string PrimaryText => IsGroup
        ? Group!.Name
        : string.IsNullOrWhiteSpace(Contact!.DisplayName) ? Contact.EmailAddress : Contact.DisplayName;

    /// <summary>Dimmed second line shown below the primary text.</summary>
    public string SecondaryText => IsGroup
        ? $"{Group!.ResolvedMemberCount} member{(Group!.ResolvedMemberCount == 1 ? "" : "s")} — group"
        : Contact!.EmailAddress;

    /// <summary>Full accessible name read by the screen reader for the list item.</summary>
    public string Display => IsGroup
        ? $"{Group!.Name}, group, {Group!.ResolvedMemberCount} member{(Group!.ResolvedMemberCount == 1 ? "" : "s")}"
        : string.IsNullOrWhiteSpace(Contact!.DisplayName)
            ? Contact.EmailAddress
            : $"{Contact.DisplayName} {Contact.EmailAddress}";

    public AddressSuggestion(ContactModel c) { Contact = c; }
    public AddressSuggestion(GroupModel g)   { Group   = g; }
}

public partial class ComposeWindow : Window
{
    private readonly ComposeViewModel   _vm;
    private readonly IContactService    _contactService;
    private readonly ITemplateService   _templateService;
    private readonly IConfigService     _configService;
    private readonly CommandRegistry    _registry = new();
    private TokenizedAddressBox? _activeAddressControl;
    private CancellationTokenSource? _autocompleteCts;
    // Suppresses the chip-list announcement on the next focus-arrived event.
    // Set before moving focus into the autocomplete popup so the return trip
    // doesn't re-announce addresses the user is actively editing.
    private bool _suppressNextFocusAnnouncement;
    private int _lastAnnouncedSpellingIndex = -1;

    // Track the current spelling error so Alt+1/2/3 can replace it with a suggestion.
    private int _currentSpellingWordStart = -1;
    private int _currentSpellingWordEnd = -1;
    private List<string>? _currentSpellingSuggestions;

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
            box.InputTextChanged             += AddressBox_InputTextChanged;
            box.PreviewKeyDown               += AddressBox_PreviewKeyDown;
            box.IsKeyboardFocusWithinChanged += AddressBox_IsKeyboardFocusWithinChanged;
            box.AddToContactsRequested       =  AddChipToContacts;
        }

        // Commit any typing left in address boxes before the VM's Send check runs.
        SendButton.Click += (_, _) => CommitAllAddressInputs();

        // When the autocomplete popup closes for any reason, restore the suppress flag
        // so the address box will commit normally on the next LostKeyboardFocus.
        AutoCompletePopup.Closed += (_, _) =>
        {
            if (_activeAddressControl != null)
                _activeAddressControl.SuppressLostFocusCommit = false;
        };

        // Reply / Reply-All: To is already filled in, so land in the body at the top.
        // New compose / Forward: To is empty, so land in the To field.
        Loaded += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_vm.To))
                ToBox.FocusInput();
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

    private async void AddressBox_InputTextChanged(object? sender, TextChangedEventArgs e)
    {
        _activeAddressControl = (TokenizedAddressBox)sender!;
        var searchToken = _activeAddressControl.CurrentInputText.Trim();
        if (searchToken.Length < 1) { AutoCompletePopup.IsOpen = false; return; }

        _autocompleteCts?.Cancel();
        _autocompleteCts = new CancellationTokenSource();

        try
        {
            var ct = _autocompleteCts.Token;
            // Run both searches concurrently (both are cache-backed so fast).
            var contactTask = _contactService.SearchContactsAsync(searchToken, ct);
            var groupTask   = _contactService.SearchGroupsAsync(searchToken, ct);
            await Task.WhenAll(contactTask, groupTask);

            // Groups appear first: picking a group is more efficient than picking
            // many individuals. Empty groups are already excluded by SearchGroupsAsync.
            var combined = groupTask.Result.Select(g => new AddressSuggestion(g))
                .Concat(contactTask.Result.Select(c => new AddressSuggestion(c)))
                .ToList();

            if (combined.Count == 0) { AutoCompletePopup.IsOpen = false; return; }

            SuggestionList.ItemsSource        = combined;
            AutoCompletePopup.PlacementTarget = _activeAddressControl;
            AutoCompletePopup.Placement       = PlacementMode.Bottom;
            AutoCompletePopup.IsOpen          = true;

            AccessibilityHelper.Announce(this,
                combined.Count == 1 ? "1 suggestion" : $"{combined.Count} suggestions",
                category: AnnouncementCategory.Result);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer keystroke
        }
    }

    private void AddressBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!AutoCompletePopup.IsOpen) return;
        if (e.Key == Key.Down)
        {
            // Suppress the LostKeyboardFocus commit before moving focus into the popup.
            // The popup lives in a separate HwndSource so IsKeyboardFocusWithin on the
            // address box becomes false the moment SuggestionList gets focus, which
            // would otherwise cause CommitPendingInput to run on the partial input.
            ((TokenizedAddressBox)sender).SuppressLostFocusCommit = true;
            // Also suppress the chip-list announcement for the return trip — the user
            // is actively editing this field and doesn't need it read back to them.
            _suppressNextFocusAnnouncement = true;
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

    private void AddressBox_IsKeyboardFocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue)
        {
            // Focus arrived in this address box.  If there are already chips, announce
            // them so the user knows the field isn't empty — without this, a screen
            // reader lands on the edit cursor after all the chips and just says "Edit".
            if (!_suppressNextFocusAnnouncement)
            {
                var box = (TokenizedAddressBox)sender;
                var chips = box.GetChips();
                if (chips.Count > 0)
                {
                    var text = chips.Count <= 5
                        ? string.Join(", ", chips.Select(c => c.Label))
                        : $"{chips.Count} addresses";
                    AccessibilityHelper.Announce(this, text, category: AnnouncementCategory.Result);
                }
            }
            _suppressNextFocusAnnouncement = false;
            return;
        }

        // Focus left — close the autocomplete popup unless it's the popup that took focus.
        Dispatcher.InvokeAsync(() =>
        {
            if (!SuggestionList.IsKeyboardFocusWithin)
                AutoCompletePopup.IsOpen = false;
        }, DispatcherPriority.Background);
    }

    internal void SuggestionList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Tab)
        {
            // Mark handled immediately so the Enter key does not reach the IsDefault
            // Send button.  AcceptSuggestion is deferred so focus does not transition
            // from the popup's HwndSource to the main window while the key event is
            // still routing — that mid-event transition is what triggers the default
            // button.
            e.Handled = true;
            if (SuggestionList.SelectedItem is AddressSuggestion s)
                Dispatcher.InvokeAsync(() => AcceptSuggestion(s), DispatcherPriority.Input);
        }
        else if (e.Key == Key.Escape)
        {
            AutoCompletePopup.IsOpen = false;
            _activeAddressControl?.FocusInput();
            e.Handled = true;
        }
        else if (e.Key == Key.Up && SuggestionList.SelectedIndex <= 0)
        {
            AutoCompletePopup.IsOpen = false;
            _activeAddressControl?.FocusInput();
            e.Handled = true;
        }
    }

    internal void SuggestionList_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (SuggestionList.SelectedItem is AddressSuggestion s) AcceptSuggestion(s);
    }

    private void AcceptSuggestion(AddressSuggestion suggestion)
    {
        if (_activeAddressControl == null) return;
        if (suggestion.IsGroup)
            AcceptGroupSuggestion(suggestion.Group!);
        else
            AcceptContactSuggestion(suggestion.Contact!);
        AutoCompletePopup.IsOpen = false;
    }

    private void AcceptContactSuggestion(ContactModel contact)
    {
        _activeAddressControl!.AcceptSuggestion(contact.DisplayName ?? string.Empty, contact.EmailAddress);
    }

    /// <summary>
    /// Expands a group suggestion into individual address chips — one per resolved
    /// member. The input text is cleared on the first chip (via AcceptSuggestion on
    /// TokenizedAddressBox) and subsequent members are added via AddAddress. Async
    /// because loading the contact list is cache-backed but needs to be awaited.
    /// </summary>
    private async void AcceptGroupSuggestion(GroupModel group)
    {
        // Capture the target box before any await so it cannot change out from under us.
        var targetBox = _activeAddressControl;
        if (targetBox == null) return;

        var allContacts = await _contactService.LoadAllContactsAsync();
        var byId = allContacts.ToDictionary(c => c.Id);
        var members = group.MemberContactIds
            .Where(id => byId.ContainsKey(id))
            .Select(id => byId[id])
            .OrderByDescending(c => c.LastUsedTicks)
            .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (members.Count == 0) return;

        // First member clears the partial input text; remaining just add chips.
        targetBox.AcceptSuggestion(members[0].DisplayName ?? string.Empty, members[0].EmailAddress);
        for (int i = 1; i < members.Count; i++)
            targetBox.AddAddress(members[i].DisplayName ?? string.Empty, members[i].EmailAddress);

        _ = _contactService.TouchGroupAsync(group.Id);

        AccessibilityHelper.Announce(this,
            members.Count == 1
                ? $"Inserted 1 address from group '{group.Name}'"
                : $"Inserted {members.Count} addresses from group '{group.Name}'",
            category: AnnouncementCategory.Result);
    }

    // ── Address helpers ───────────────────────────────────────────────────────

    private void CommitAllAddressInputs()
    {
        foreach (var box in new[] { ToBox, CcBox, BccBox })
            box.CommitPendingInput();
    }

    private async Task AddChipToContacts(string displayName, string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var existing = await _contactService.SearchContactsAsync(email, cts.Token);
        var dup = existing.FirstOrDefault(c =>
            c.EmailAddress.Equals(email, StringComparison.OrdinalIgnoreCase));
        if (dup != null)
        {
            var msg = $"{email} is already in your address book.";
            _vm.StatusText = msg;
            AccessibilityHelper.Announce(this, msg, category: AnnouncementCategory.Result);
        }
        else
        {
            await _contactService.UpsertContactAsync(new Models.ContactModel
            {
                DisplayName = displayName,
                EmailAddress = email
            });
            var label = string.IsNullOrWhiteSpace(displayName) ? email : $"{displayName} ({email})";
            var msg = $"Added {label} to address book.";
            _vm.StatusText = msg;
            AccessibilityHelper.Announce(this, msg, category: AnnouncementCategory.Result);
        }
    }

    // ── Ctrl+K: Check Addresses ───────────────────────────────────────────────

    private async void CheckAddresses()
    {
        int total = ToBox.GetChips().Count + CcBox.GetChips().Count + BccBox.GetChips().Count;
        if (total == 0)
        {
            AccessibilityHelper.Announce(this, "No addresses to check.", category: AnnouncementCategory.Result);
            return;
        }

        int resolved = 0, invalid = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        foreach (var box in new[] { ToBox, CcBox, BccBox })
        {
            var chips = box.GetChips().ToList();
            for (int i = 0; i < chips.Count; i++)
            {
                if (cts.IsCancellationRequested) break;
                var chip = chips[i];

                if (!IsValidEmailAddress(chip.EmailAddress))
                {
                    if (!chip.EmailAddress.Contains('@'))
                    {
                        // Might be a bare name — try contact lookup
                        var results = await _contactService.SearchContactsAsync(chip.EmailAddress, cts.Token);
                        if (results.Count == 1)
                        {
                            box.UpdateChip(i, results[0].DisplayName ?? string.Empty, results[0].EmailAddress, isInvalid: false);
                            resolved++;
                        }
                        else
                        {
                            box.UpdateChip(i, chip.DisplayName, chip.EmailAddress, isInvalid: true);
                            invalid++;
                        }
                    }
                    else
                    {
                        box.UpdateChip(i, chip.DisplayName, chip.EmailAddress, isInvalid: true);
                        invalid++;
                    }
                }
                else if (string.IsNullOrWhiteSpace(chip.DisplayName))
                {
                    // Valid email — try to enrich with a contact display name
                    var results = await _contactService.SearchContactsAsync(chip.EmailAddress, cts.Token);
                    var match = results.FirstOrDefault(r =>
                        r.EmailAddress.Equals(chip.EmailAddress, StringComparison.OrdinalIgnoreCase));
                    if (match != null && !string.IsNullOrEmpty(match.DisplayName))
                    {
                        box.UpdateChip(i, match.DisplayName, chip.EmailAddress, isInvalid: false);
                        resolved++;
                    }
                }
            }
        }

        string summary;
        if (invalid > 0)
            summary = $"{total} address{(total == 1 ? "" : "es")} checked. {invalid} unrecognized.";
        else if (resolved > 0)
            summary = $"{total} address{(total == 1 ? "" : "es")} checked. {resolved} resolved from contacts.";
        else
            summary = $"{total} address{(total == 1 ? "" : "es")} checked. All valid.";

        _vm.StatusText = summary;
        AccessibilityHelper.Announce(this, summary, category: AnnouncementCategory.Result);
    }

    private static bool IsValidEmailAddress(string email)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@')) return false;
        try { return new MailAddress(email).Host.Contains('.'); }
        catch { return false; }
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
            Closing -= OnWindowClosing;
            e.Cancel = false;
            return;
        }

        // result == Yes: save the draft first
        await _vm.SaveDraftCommand.ExecuteAsync(null);
        if (_vm.StatusText.Contains("failed", StringComparison.OrdinalIgnoreCase))
            return;

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
            CommitAllAddressInputs();
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
    /// needing to press F7.
    /// </summary>
    private void BodyBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressSpellingAnnouncement) return;
        if (_caretMovedByTyping) return;
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

    private string BuildSpellingAnnouncement(string word, List<string> suggestions)
    {
        var announceSuggestions = _configService.Load().AnnounceSpellingSuggestions;
        if (!announceSuggestions || suggestions.Count == 0)
            return $"Misspelling: {word}.";
        return $"Misspelling: {word}. {string.Join(", ", suggestions)}.";
    }

    private static bool IsTypingKey(Key key, ModifierKeys modifiers)
    {
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
            _ => true
        };
    }

    /// <summary>
    /// F7 moves to the next misspelled word; Shift+F7 moves to the previous one.
    /// </summary>
    private void BodyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        _caretMovedByTyping = IsTypingKey(e.Key, e.KeyboardDevice.Modifiers);
        if (_caretMovedByTyping && _configService.Load().AnnounceSpellingWhileTyping)
            ResetSpellingTypingTimer();
        else if (!_caretMovedByTyping)
            _spellingTypingTimer?.Stop();

        // Alt+1/2/3: replace the current misspelled word with the corresponding suggestion.
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

                _suppressSpellingAnnouncement = true;
                BodyBox.Text = before + replacement + after;
                BodyBox.CaretIndex = _currentSpellingWordStart + replacement.Length;
                _suppressSpellingAnnouncement = false;

                BodyBox.Focus();
                AccessibilityHelper.Announce(this, $"Replaced with {replacement}.",
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
            execute: () => { CommitAllAddressInputs(); _vm.SendCommand.Execute(null); },
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
            id: "compose.checkAddresses", category: "Compose", title: "Check Addresses",
            execute: CheckAddresses,
            defaultKey: Key.K, defaultModifiers: ModifierKeys.Control));

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

        _registry.Register(new CommandDefinition(
            id: "compose.openAddressBook", category: "Compose", title: "Address Book",
            execute: OpenAddressBook,
            defaultKey: Key.B, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift));
    }

    private void OpenAddressBook()
    {
        var vm = new AddressBookViewModel(_contactService);
        vm.SetInsertActions(
            toAction:  c => ToBox.AddAddress(c.DisplayName ?? string.Empty, c.EmailAddress),
            ccAction:  c => CcBox.AddAddress(c.DisplayName ?? string.Empty, c.EmailAddress),
            bccAction: c => BccBox.AddAddress(c.DisplayName ?? string.Empty, c.EmailAddress));
        var win = new AddressBookWindow(vm) { Owner = this };
        win.ShowDialog();
    }

    /// <summary>
    /// Adds an address token to the To field. Called by <c>MainWindow</c> when
    /// the address book is opened standalone and the user picks a contact or group.
    /// </summary>
    public void AddToAddress(string name, string email)  => ToBox.AddAddress(name, email);
    public void AddCcAddress(string name, string email)  => CcBox.AddAddress(name, email);
    public void AddBccAddress(string name, string email) => BccBox.AddAddress(name, email);

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
                ?.Suggestions.Take(3).ToList() ?? new List<string>();

            _currentSpellingWordStart = wordStart;
            _currentSpellingWordEnd = wordEnd;
            _currentSpellingSuggestions = suggestions;

            AccessibilityHelper.Announce(this, BuildSpellingAnnouncement(word, suggestions),
                category: AnnouncementCategory.Result);
        }
        else
        {
            ClearSpellingContext();
            AccessibilityHelper.Announce(this, "No more misspellings found.",
                category: AnnouncementCategory.Result);
        }
    }
}

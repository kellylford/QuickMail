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
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QuickMail.Controls;
using QuickMail.Helpers;
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
    private TextPointer? _richSpellingWordStart;
    private TextPointer? _richSpellingWordEnd;

    // Track the current spelling error so Alt+1/2/3 can replace it with a suggestion.
    private int _currentSpellingWordStart = -1;
    private int _currentSpellingWordEnd = -1;
    private List<string>? _currentSpellingSuggestions;

    // Suppress spelling announcements during programmatic text changes (e.g. Alt+1 replacement).
    private bool _suppressSpellingAnnouncement;

    // Last block type announced while navigating in HTML mode (e.g. "Heading 2", "Normal text").
    // Used to suppress repeat announcements when the caret stays on the same paragraph type.
    private string? _lastAnnouncedBlockType;

    // Suppress formatting-while-navigating announcements during mode switches and programmatic loads.
    private bool _suppressFormattingAnnouncement;

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
            else if (e.PropertyName == nameof(vm.CurrentMode))
                ApplyComposeMode();
        };

        WireRichCompose();

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
            ApplyDefaultComposeMode();
            if (string.IsNullOrWhiteSpace(_vm.To))
                ToBox.FocusInput();
            else
            {
                FocusActiveEditor();
                if (_vm.CurrentMode != ComposeMode.Html)
                    BodyBox.CaretIndex = 0;
            }
        };
        BodyBox.SelectionChanged += BodyBox_SelectionChanged;
        RichBodyBox.SelectionChanged += RichBodyBox_SelectionChanged;
        RichBodyBox.PreviewKeyDown += RichBodyBox_PreviewKeyDown;
        Closing += OnWindowClosing;
        ConfirmSaveOnClose = () =>
        {
            var r = MessageBox.Show(this,
                "Do you want to save this message as a draft before closing?",
                "Save Draft?",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);
            return Task.FromResult<bool?>(r switch
            {
                MessageBoxResult.Yes => true,
                MessageBoxResult.No  => false,
                _                    => null,
            });
        };

        // Auto-save: quiet periodic draft saves. Success shows in the status row
        // only; a failure is announced once (Status category) by the VM event.
        vm.AutoSaveFailed += message =>
            AccessibilityHelper.Announce(this, message, category: AnnouncementCategory.Status);
        var cfg = _configService.Load();
        if (cfg.AutoSaveDrafts)
        {
            _autoSaveTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(Math.Clamp(cfg.AutoSaveIntervalSeconds, 30, 600)),
            };
            _autoSaveTimer.Tick += (_, _) => _ = _vm.AutoSaveAsync();
            _autoSaveTimer.Start();
            Closed += (_, _) => _autoSaveTimer.Stop();
        }
    }

    private DispatcherTimer? _autoSaveTimer;
    private MarkdownPreviewWindow? _previewWindow;

    /// <summary>
    /// Invoked when a dirty window is closing to ask whether to save the draft.
    /// Returns true = save, false = discard, null = cancel (keep open).
    /// Set to null in headless/test contexts to skip the dialog and discard.
    /// </summary>
    internal Func<Task<bool?>>? ConfirmSaveOnClose { get; set; }

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
        _vm.CancelAutoSave();
        _previewWindow?.Close();

        // If the message was sent, or nothing was edited, let the window close freely.
        if (_vm.IsSent || !_vm.IsDirty)
            return;

        // Prevent the window from closing until the user decides.
        e.Cancel = true;

        // ConfirmSaveOnClose is null in headless/test contexts — discard without asking.
        var confirm = ConfirmSaveOnClose;
        var decision = confirm != null ? await confirm() : false;

        if (decision == null)
            return; // cancelled — keep window open

        if (decision == false)
        {
            Closing -= OnWindowClosing;
            e.Cancel = false;
            return;
        }

        // decision == true: save the draft first
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
            // Transient UI consumes Escape itself: an open menu (focus sits on a
            // MenuItem) or an open combo dropdown must close without closing the
            // whole compose window. This preview handler tunnels ahead of them,
            // so step aside instead of cancelling.
            if (Keyboard.FocusedElement is MenuItem
                || FromCombo.IsDropDownOpen
                || ModeSelector.IsDropDownOpen
                || AutoCompletePopup.IsOpen
                || _previewWindow != null)
            {
                _previewWindow?.Close();
                return;
            }
            _vm.CancelCommand.Execute(null);
            e.Handled = true;
            return;
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
            FocusActiveEditor();
            e.Handled = true;
        }

        // ── Registry-based compose commands ──────────────────────────────────
        if (e.Handled) return;  // already dispatched above — don't double-execute
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

    private void RichBodyBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressFormattingAnnouncement) return;
        if (_vm.CurrentMode != ComposeMode.Html) return;

        var config = _configService.Load();

        if (config.AnnounceFormattingWhileNavigating)
        {
            var paragraph = RichBodyBox.Selection.Start.Paragraph;
            var blockType = BlockTypeLabel(paragraph);
            if (blockType != _lastAnnouncedBlockType)
            {
                _lastAnnouncedBlockType = blockType;
                AccessibilityHelper.Announce(this, blockType, category: AnnouncementCategory.Result);
            }
        }

        if (!_caretMovedByTyping && config.AnnounceSpellingWhileNavigating)
            AnnounceSpellingAtCurrentPosition();
    }

    private void ClearSpellingContext()
    {
        _lastAnnouncedSpellingIndex = -1;
        _richSpellingWordStart = null;
        _richSpellingWordEnd = null;
        _currentSpellingWordStart = -1;
        _currentSpellingWordEnd = -1;
        _currentSpellingSuggestions = null;
    }

    private void AnnounceSpellingAtCurrentPosition()
    {
        if (_vm.CurrentMode == ComposeMode.Html)
        {
            AnnounceSpellingAtRichPosition();
            return;
        }

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

    private void AnnounceSpellingAtRichPosition()
    {
        var pos = RichBodyBox.CaretPosition;
        var error = RichBodyBox.GetSpellingError(pos);
        if (error == null)
        {
            // CaretPosition may be at the end of a selected word (one position past the
            // error boundary), so check one insertion position back before giving up.
            var prev = pos.GetNextInsertionPosition(LogicalDirection.Backward);
            if (prev != null) error = RichBodyBox.GetSpellingError(prev);
            if (error == null) { ClearSpellingContext(); return; }
            pos = prev!;
        }

        var wordRange = RichBodyBox.GetSpellingErrorRange(pos);
        if (wordRange == null) { ClearSpellingContext(); return; }

        if (_richSpellingWordStart != null &&
            _richSpellingWordStart.CompareTo(wordRange.Start) == 0)
            return;

        _richSpellingWordStart = wordRange.Start;
        _richSpellingWordEnd   = wordRange.End;

        var suggestions = error.Suggestions.Take(3).ToList();
        _currentSpellingSuggestions = suggestions;

        AccessibilityHelper.Announce(this, BuildSpellingAnnouncement(wordRange.Text, suggestions),
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

        // Tab/Shift+Tab on a list line in Markdown mode: indent or dedent.
        if (e.Key == Key.Tab && _vm.CurrentMode == ComposeMode.Markdown
            && (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Shift))
        {
            var isShift = Keyboard.Modifiers == ModifierKeys.Shift;
            var edit = isShift
                ? MarkdownEditing.DedentListItem(BodyBox.Text, BodyBox.CaretIndex)
                : MarkdownEditing.IndentListItem(BodyBox.Text, BodyBox.CaretIndex);
            if (edit.HasValue)
            {
                ApplyMarkdownEdit(edit.Value);
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

    private void RichBodyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        _caretMovedByTyping = IsTypingKey(e.Key, e.KeyboardDevice.Modifiers);
        if (_caretMovedByTyping && _configService.Load().AnnounceSpellingWhileTyping)
            ResetSpellingTypingTimer();
        else if (!_caretMovedByTyping)
            _spellingTypingTimer?.Stop();

        // Alt+1/2/3: replace the current misspelled word with the corresponding suggestion.
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0
            && _currentSpellingSuggestions is { Count: > 0 }
            && _richSpellingWordStart != null && _richSpellingWordEnd != null)
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
                // Use the stored TextPointers directly — re-fetching via GetSpellingErrorRange
                // can return null when the word is selected or the caret is past its boundary.
                var wordRange = new TextRange(_richSpellingWordStart, _richSpellingWordEnd);

                _suppressFormattingAnnouncement = true;
                wordRange.Text = replacement;
                RichBodyBox.Selection.Select(wordRange.End, wordRange.End);
                _suppressFormattingAnnouncement = false;

                AccessibilityHelper.Announce(this, $"Replaced with {replacement}.",
                    category: AnnouncementCategory.Result);
                ClearSpellingContext();
                e.Handled = true;
                // Defer focus restoration so WPF's Alt-key menu activation logic,
                // which runs after our handler returns, doesn't steal focus.
                Dispatcher.BeginInvoke(() => { RichBodyBox.Focus(); Keyboard.Focus(RichBodyBox); },
                    DispatcherPriority.Input);
                return;
            }
        }

        if (e.Key != Key.Tab) return;
        if (Keyboard.Modifiers != ModifierKeys.None && Keyboard.Modifiers != ModifierKeys.Shift) return;

        var para = RichBodyBox.CaretPosition.Paragraph;
        if (para?.Parent is not ListItem) return;

        _suppressFormattingAnnouncement = true;
        if (Keyboard.Modifiers == ModifierKeys.Shift)
            EditingCommands.DecreaseIndentation.Execute(null, RichBodyBox);
        else
            EditingCommands.IncreaseIndentation.Execute(null, RichBodyBox);
        _suppressFormattingAnnouncement = false;

        e.Handled = true;
        _vm.MarkBodyDirty();

        var newPara = RichBodyBox.CaretPosition.Paragraph;
        var label = BlockTypeLabel(newPara);
        _lastAnnouncedBlockType = label;
        AccessibilityHelper.Announce(this, label, category: AnnouncementCategory.Result);
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
            execute: FocusActiveEditor,
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

        RegisterRichComposeCommands();

        _registry.Register(new CommandDefinition(
            id: "compose.openAddressBook", category: "Compose", title: "Address Book",
            execute: OpenAddressBook,
            defaultKey: Key.B, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift));

        _registry.Register(new CommandDefinition(
            id: "compose.announceAutoSave", category: "Compose", title: "Announce Last Auto-Save",
            execute: () => AccessibilityHelper.Announce(this,
                string.IsNullOrEmpty(_vm.AutoSaveText) ? "Not auto-saved yet." : _vm.AutoSaveText + ".",
                category: AnnouncementCategory.Result)));
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

    private void OpenPreview()
    {
        if (_previewWindow != null)
        {
            _previewWindow.Close();
            return;
        }
        var fragment = _vm.GetBodyHtml();
        _previewWindow = new MarkdownPreviewWindow(_vm.Subject, fragment) { Owner = this };
        _previewWindow.Closed += (_, _) => { _previewWindow = null; FocusActiveEditor(); };
        _previewWindow.Show();
    }

    private void NavigateSpellingError(bool forward)
    {
        if (_vm.CurrentMode == ComposeMode.Html)
        {
            NavigateSpellingErrorInRichBox(forward);
            return;
        }

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
            if (foundIndex < 0 && start > 0)
            {
                for (int i = 0; i < start; i++)
                {
                    if (BodyBox.GetSpellingError(i) != null) { foundIndex = i; break; }
                }
            }
        }
        else
        {
            for (int i = Math.Min(start - 1, text.Length - 1); i >= 0; i--)
            {
                if (BodyBox.GetSpellingError(i) != null) { foundIndex = i; break; }
            }
            if (foundIndex < 0)
            {
                for (int i = text.Length - 1; i >= start; i--)
                {
                    if (BodyBox.GetSpellingError(i) != null) { foundIndex = i; break; }
                }
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
            AccessibilityHelper.Announce(this, "No misspellings found.",
                category: AnnouncementCategory.Result);
        }
    }

    private void NavigateSpellingErrorInRichBox(bool forward)
    {
        var doc = RichBodyBox.Document;
        var direction = forward ? LogicalDirection.Forward : LogicalDirection.Backward;
        var errorPos = RichBodyBox.GetNextSpellingErrorPosition(RichBodyBox.CaretPosition, direction);

        if (errorPos == null)
        {
            var wrapFrom = forward ? doc.ContentStart : doc.ContentEnd;
            errorPos = RichBodyBox.GetNextSpellingErrorPosition(wrapFrom, direction);
        }

        if (errorPos != null)
        {
            var wordRange = RichBodyBox.GetSpellingErrorRange(errorPos);
            if (wordRange == null)
            {
                ClearSpellingContext();
                AccessibilityHelper.Announce(this, "No misspellings found.", category: AnnouncementCategory.Result);
                return;
            }

            RichBodyBox.Selection.Select(wordRange.Start, wordRange.End);
            RichBodyBox.Focus();

            var error = RichBodyBox.GetSpellingError(errorPos);
            var suggestions = error?.Suggestions.Take(3).ToList() ?? new List<string>();
            _currentSpellingSuggestions = suggestions;
            _richSpellingWordStart = wordRange.Start;
            _richSpellingWordEnd   = wordRange.End;

            AccessibilityHelper.Announce(this, BuildSpellingAnnouncement(wordRange.Text, suggestions),
                category: AnnouncementCategory.Result);
        }
        else
        {
            ClearSpellingContext();
            AccessibilityHelper.Announce(this, "No misspellings found.", category: AnnouncementCategory.Result);
        }
    }

    // ── Rich compose: modes, formatting, preview ─────────────────────────────
    //
    // Three editing modes share the body area. Plain Text and Markdown use
    // BodyBox (TextBox); HTML mode uses RichBodyBox — a native RichTextBox, so
    // screen readers stay in their normal edit cursor (no embedded browser, no
    // virtual cursor). Conversions run through the ViewModel; this code-behind
    // only swaps visibility, serializes the FlowDocument, and announces state.

    private bool _suppressRichTextChanged;
    private bool _syncingModeSelector;

    private void WireRichCompose()
    {
        _vm.RichBodyProvider = () => RichTextDocumentConverter.Snapshot(RichBodyBox.Document);

        // The editor keeps its original FlowDocument for the window's lifetime.
        // Replacing RichTextBox.Document breaks UIA: the automation peer stays
        // bound to the first document's text container, so screen readers would
        // permanently read the stale (empty) document. All loads mutate Blocks
        // in place via LoadInto instead.
        RichBodyBox.Document.FontFamily = new FontFamily("Segoe UI");
        RichBodyBox.Document.FontSize = 13;
        RichBodyBox.Document.PagePadding = new Thickness(4);

        _vm.LoadHtmlIntoEditorRequested += html =>
        {
            // Programmatic load is not a user edit — don't mark the draft dirty.
            _suppressRichTextChanged = true;
            _suppressFormattingAnnouncement = true;
            _lastAnnouncedBlockType = null;
            RichTextDocumentConverter.LoadInto(RichBodyBox.Document, html);
            RichBodyBox.CaretPosition = RichBodyBox.Document.ContentStart;
            _suppressRichTextChanged = false;
            _suppressFormattingAnnouncement = false;
        };

        _vm.InsertTextIntoEditorRequested += text =>
        {
            RichBodyBox.Selection.Select(RichBodyBox.CaretPosition, RichBodyBox.CaretPosition);
            RichBodyBox.Selection.Text = text;
            RichBodyBox.Selection.Select(RichBodyBox.Selection.End, RichBodyBox.Selection.End);
        };

        SyncModeSelector();
    }

    /// <summary>
    /// Applies the initial editing mode after the View's event handlers are wired:
    /// drafts restore their saved mode; templates stay plain text; new composes use the configured default.
    /// </summary>
    private void ApplyDefaultComposeMode()
    {
        ComposeMode targetMode;
        if (_vm.ComposeKind is ComposeKind.EditDraft or ComposeKind.NewDraft)
            targetMode = _vm.SeededMode;       // restore whatever mode the draft was saved in
        else if (_vm.ComposeKind is ComposeKind.EditTemplate)
            targetMode = ComposeMode.PlainText; // templates are plain-text only
        else
            targetMode = _configService.Load().DefaultComposeMode;

        if (targetMode == ComposeMode.PlainText) return;
        _vm.SetMode(targetMode);
    }

    private void RichBodyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_suppressRichTextChanged)
            _vm.MarkBodyDirty();
    }

    private void FocusActiveEditor()
    {
        if (_vm.CurrentMode == ComposeMode.Html)
            RichBodyBox.Focus();
        else
            BodyBox.Focus();
    }

    /// <summary>Reacts to a mode change: swaps editors, toolbar, fonts, and announces.</summary>
    private void ApplyComposeMode()
    {
        var mode = _vm.CurrentMode;
        var bodyHadFocus = BodyBox.IsKeyboardFocusWithin || RichBodyBox.IsKeyboardFocusWithin;
        _suppressFormattingAnnouncement = true;
        _lastAnnouncedBlockType = null;

        FormattingToolbarTray.Visibility = mode == ComposeMode.Html ? Visibility.Visible : Visibility.Collapsed;
        RichBodyBox.Visibility           = mode == ComposeMode.Html ? Visibility.Visible : Visibility.Collapsed;
        BodyBox.Visibility               = mode == ComposeMode.Html ? Visibility.Collapsed : Visibility.Visible;
        BodyBox.FontFamily = mode == ComposeMode.Markdown ? new FontFamily("Consolas") : new FontFamily("Segoe UI");

        SyncModeSelector();
        if (bodyHadFocus)
            FocusActiveEditor();

        var name = mode switch
        {
            ComposeMode.Markdown => "Markdown",
            ComposeMode.Html     => "HTML",
            _                    => "Plain Text",
        };
        AccessibilityHelper.Announce(this, $"Switched to {name} mode.", category: AnnouncementCategory.Result);
        _suppressFormattingAnnouncement = false;
    }

    private void SyncModeSelector()
    {
        _syncingModeSelector = true;
        ModeSelector.SelectedIndex = _vm.CurrentMode switch
        {
            ComposeMode.Markdown => 1,
            ComposeMode.Html     => 2,
            _                    => 0,
        };
        _syncingModeSelector = false;

        // Radio-style check marks on the View menu's mode items.
        MenuModePlain.IsChecked    = _vm.CurrentMode == ComposeMode.PlainText;
        MenuModeMarkdown.IsChecked = _vm.CurrentMode == ComposeMode.Markdown;
        MenuModeHtml.IsChecked     = _vm.CurrentMode == ComposeMode.Html;
    }

    // ── Menu bar handlers (same dispatch as the equivalent shortcuts) ─────────

    private void MenuSend_Click(object sender, RoutedEventArgs e)
    {
        CommitAllAddressInputs();
        _vm.SendCommand.Execute(null);
    }

    private void MenuModePlain_Click(object sender, RoutedEventArgs e)    => _vm.SetMode(ComposeMode.PlainText);
    private void MenuModeMarkdown_Click(object sender, RoutedEventArgs e) => _vm.SetMode(ComposeMode.Markdown);
    private void MenuModeHtml_Click(object sender, RoutedEventArgs e)     => _vm.SetMode(ComposeMode.Html);

    /// <summary>
    /// WPF skips disabled menu items during arrow navigation, so when every
    /// Format item is grayed (Plain Text mode) an opened Format menu would be
    /// silent dead air. Announce why the items are unavailable instead.
    /// </summary>
    private void FormatMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (_vm.CurrentMode == ComposeMode.PlainText)
            AccessibilityHelper.Announce(this,
                "Formatting commands require Markdown or HTML mode.",
                category: AnnouncementCategory.Hint);
    }

    private void MenuNextMisspelling_Click(object sender, RoutedEventArgs e) => NavigateSpellingError(forward: true);
    private void MenuPrevMisspelling_Click(object sender, RoutedEventArgs e) => NavigateSpellingError(forward: false);
    private void MenuAnnounceFormatting_Click(object sender, RoutedEventArgs e) => AnnounceFormattingState();
    private void MenuShowFormatting_Click(object sender, RoutedEventArgs e)     => ShowFormatting();
    private void MenuAddressBook_Click(object sender, RoutedEventArgs e)     => OpenAddressBook();
    private void MenuCheckAddresses_Click(object sender, RoutedEventArgs e)  => CheckAddresses();
    private void MenuToggleSpelling_Click(object sender, RoutedEventArgs e)  => ToggleSpellingAnnouncements();
    private void MenuCommandPalette_Click(object sender, RoutedEventArgs e)  => OpenCommandPalette();
    private void MenuOpenPreview_Click(object sender, RoutedEventArgs e) => OpenPreview();

    private void ModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingModeSelector) return;
        var requested = ModeSelector.SelectedIndex switch
        {
            1 => ComposeMode.Markdown,
            2 => ComposeMode.Html,
            _ => ComposeMode.PlainText,
        };
        if (requested == _vm.CurrentMode) return;
        if (!_vm.SetMode(requested))
            SyncModeSelector();   // user declined the confirmation — revert the selector
    }

    private void RegisterRichComposeCommands()
    {
        // Formatting works in both rich modes: HTML applies real formatting,
        // Markdown inserts the equivalent syntax.
        bool InRichMode() => _vm.CurrentMode != ComposeMode.PlainText;

        _registry.Register(new CommandDefinition(
            id: "compose.setModePlain", category: "Compose", title: "Switch to Plain Text Mode",
            execute: () => _vm.SetMode(ComposeMode.PlainText),
            defaultKey: Key.D1, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift));

        _registry.Register(new CommandDefinition(
            id: "compose.setModeMarkdown", category: "Compose", title: "Switch to Markdown Mode",
            execute: () => _vm.SetMode(ComposeMode.Markdown),
            defaultKey: Key.D2, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift));

        _registry.Register(new CommandDefinition(
            id: "compose.setModeHtml", category: "Compose", title: "Switch to HTML Mode",
            execute: () => _vm.SetMode(ComposeMode.Html),
            defaultKey: Key.D3, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift));

        _registry.Register(new CommandDefinition(
            id: "compose.toggleBold", category: "Compose", title: "Bold",
            execute: ToggleBold,
            defaultKey: Key.B, defaultModifiers: ModifierKeys.Control,
            isAvailable: InRichMode));

        _registry.Register(new CommandDefinition(
            id: "compose.toggleItalic", category: "Compose", title: "Italic",
            execute: ToggleItalic,
            defaultKey: Key.I, defaultModifiers: ModifierKeys.Control,
            isAvailable: InRichMode));

        _registry.Register(new CommandDefinition(
            id: "compose.toggleUnderline", category: "Compose", title: "Underline",
            execute: ToggleUnderline,
            defaultKey: Key.U, defaultModifiers: ModifierKeys.Control,
            isAvailable: InRichMode));

        _registry.Register(new CommandDefinition(
            id: "compose.toggleStrikethrough", category: "Compose", title: "Strikethrough",
            execute: ToggleStrikethrough,
            defaultKey: Key.X, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift,
            isAvailable: InRichMode));

        _registry.Register(new CommandDefinition(
            id: "compose.heading1", category: "Compose", title: "Heading 1",
            execute: () => ApplyHeading(1),
            defaultKey: Key.D1, defaultModifiers: ModifierKeys.Control | ModifierKeys.Alt,
            isAvailable: InRichMode));

        _registry.Register(new CommandDefinition(
            id: "compose.heading2", category: "Compose", title: "Heading 2",
            execute: () => ApplyHeading(2),
            defaultKey: Key.D2, defaultModifiers: ModifierKeys.Control | ModifierKeys.Alt,
            isAvailable: InRichMode));

        _registry.Register(new CommandDefinition(
            id: "compose.heading3", category: "Compose", title: "Heading 3",
            execute: () => ApplyHeading(3),
            defaultKey: Key.D3, defaultModifiers: ModifierKeys.Control | ModifierKeys.Alt,
            isAvailable: InRichMode));

        _registry.Register(new CommandDefinition(
            id: "compose.bulletList", category: "Compose", title: "Bullet List",
            execute: () => ToggleList(ordered: false),
            defaultKey: Key.L, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift,
            isAvailable: InRichMode));

        _registry.Register(new CommandDefinition(
            id: "compose.numberedList", category: "Compose", title: "Numbered List",
            execute: () => ToggleList(ordered: true),
            defaultKey: Key.N, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift,
            isAvailable: InRichMode));

        _registry.Register(new CommandDefinition(
            id: "compose.insertLink", category: "Compose", title: "Insert Link…",
            execute: InsertLink,
            defaultKey: Key.L, defaultModifiers: ModifierKeys.Control,
            isAvailable: InRichMode));

        _registry.Register(new CommandDefinition(
            id: "compose.clearFormatting", category: "Compose", title: "Clear Formatting",
            execute: ClearFormatting,
            defaultKey: Key.Space, defaultModifiers: ModifierKeys.Control,
            isAvailable: InRichMode));

        _registry.Register(new CommandDefinition(
            id: "compose.queryFormatting", category: "Compose", title: "Announce Formatting State",
            execute: AnnounceFormattingState,
            defaultKey: Key.T, defaultModifiers: ModifierKeys.Control,
            isAvailable: InRichMode));

        _registry.Register(new CommandDefinition(
            id: "compose.showFormatting", category: "Compose", title: "Show Formatting…",
            execute: ShowFormatting,
            defaultKey: Key.T, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift,
            isAvailable: InRichMode));

        _registry.Register(new CommandDefinition(
            id: "compose.openPreview", category: "Compose", title: "Open Preview",
            execute: OpenPreview,
            defaultKey: Key.F8, defaultModifiers: ModifierKeys.None,
            isAvailable: () => _vm.IsPreviewAvailable));
    }

    // ── Formatting commands (HTML and Markdown modes) ─────────────────────────
    //
    // Every command dispatches by mode: HTML applies real formatting to the
    // RichTextBox; Markdown inserts the equivalent syntax into BodyBox through
    // MarkdownEditing (pure text operations, applied via SelectedText so each
    // toggle is one undo unit).

    /// <summary>
    /// Formatting feedback is announced deferred (ApplicationIdle) with
    /// interrupt. Menu invocations close the menu and restore focus to the
    /// editor, and the screen reader's focus speech ("Message body, edit")
    /// otherwise lands after — and silences — an immediate announcement.
    /// </summary>
    private void AnnounceFormatting(string text) =>
        Dispatcher.InvokeAsync(
            () => AccessibilityHelper.Announce(this, text, interrupt: true, category: AnnouncementCategory.Result),
            DispatcherPriority.ApplicationIdle);

    /// <summary>
    /// EditingCommands and selection formatting need the editor focused — palette
    /// and toolbar invocations arrive with focus elsewhere.
    /// </summary>
    private void EnsureRichEditorFocused()
    {
        if (!RichBodyBox.IsKeyboardFocusWithin)
            RichBodyBox.Focus();
    }

    private void ApplyMarkdownEdit(MarkdownEdit edit)
    {
        _suppressSpellingAnnouncement = true;
        BodyBox.Select(edit.ReplaceStart, edit.ReplaceLength);
        BodyBox.SelectedText = edit.Replacement;
        BodyBox.Select(edit.SelectionStart, edit.SelectionLength);
        _suppressSpellingAnnouncement = false;
        if (!BodyBox.IsKeyboardFocusWithin)
            BodyBox.Focus();
    }

    private void ToggleMarkdownInline(string marker, string name)
    {
        var edit = MarkdownEditing.ToggleInline(
            BodyBox.Text, BodyBox.SelectionStart, BodyBox.SelectionLength, marker);
        ApplyMarkdownEdit(edit);
        AnnounceFormatting($"{name} {(edit.TurnedOn ? "on" : "off")}");
    }

    private void ToggleBold()
    {
        if (_vm.CurrentMode == ComposeMode.Markdown) { ToggleMarkdownInline("**", "Bold"); return; }
        EnsureRichEditorFocused();
        EditingCommands.ToggleBold.Execute(null, RichBodyBox);
        var on = Equals(RichBodyBox.Selection.GetPropertyValue(TextElement.FontWeightProperty), FontWeights.Bold);
        AnnounceFormatting(on ? "Bold on" : "Bold off");
    }

    private void ToggleItalic()
    {
        if (_vm.CurrentMode == ComposeMode.Markdown) { ToggleMarkdownInline("*", "Italic"); return; }
        EnsureRichEditorFocused();
        EditingCommands.ToggleItalic.Execute(null, RichBodyBox);
        var on = Equals(RichBodyBox.Selection.GetPropertyValue(TextElement.FontStyleProperty), FontStyles.Italic);
        AnnounceFormatting(on ? "Italic on" : "Italic off");
    }

    private void ToggleUnderline()
    {
        if (_vm.CurrentMode == ComposeMode.Markdown)
        {
            // Markdown has no underline and the pipeline blocks raw HTML.
            AnnounceFormatting("Underline is not available in Markdown. Use HTML mode for underline.");
            return;
        }
        EnsureRichEditorFocused();
        EditingCommands.ToggleUnderline.Execute(null, RichBodyBox);
        var on = SelectionHasDecoration(TextDecorationLocation.Underline);
        AnnounceFormatting(on ? "Underline on" : "Underline off");
    }

    private void ToggleStrikethrough()
    {
        if (_vm.CurrentMode == ComposeMode.Markdown) { ToggleMarkdownInline("~~", "Strikethrough"); return; }
        EnsureRichEditorFocused();
        var selection = RichBodyBox.Selection;
        var current = selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
        var has = current?.Any(d => d.Location == TextDecorationLocation.Strikethrough) ?? false;

        TextDecorationCollection updated;
        if (has)
            updated = new TextDecorationCollection(current!.Where(d => d.Location != TextDecorationLocation.Strikethrough));
        else
        {
            updated = current == null ? new TextDecorationCollection() : new TextDecorationCollection(current);
            updated.Add(TextDecorations.Strikethrough[0]);
        }
        selection.ApplyPropertyValue(Inline.TextDecorationsProperty, updated);
        AnnounceFormatting(has ? "Strikethrough off" : "Strikethrough on");
    }

    private bool SelectionHasDecoration(TextDecorationLocation location)
    {
        var value = RichBodyBox.Selection.GetPropertyValue(Inline.TextDecorationsProperty);
        return value is TextDecorationCollection c && c.Any(d => d.Location == location);
    }

    /// <summary>Toggles a heading on all paragraphs touched by the current selection. Applying the same level again returns to normal text.</summary>
    private void ApplyHeading(int level)
    {
        if (_vm.CurrentMode == ComposeMode.Markdown)
        {
            var edit = MarkdownEditing.ToggleHeading(BodyBox.Text, BodyBox.CaretIndex, level);
            ApplyMarkdownEdit(edit);
            AnnounceFormatting(edit.TurnedOn ? $"Heading {level}" : "Normal text");
            return;
        }

        EnsureRichEditorFocused();
        var paragraphs = GetSelectedParagraphs();
        if (paragraphs.Count == 0) return;

        var tag = "H" + level;
        bool turnOff = paragraphs.All(p => p.Tag as string == tag);

        foreach (var paragraph in paragraphs)
        {
            if (turnOff)
            {
                paragraph.Tag = null;
                paragraph.ClearValue(TextElement.FontSizeProperty);
                paragraph.ClearValue(TextElement.FontWeightProperty);
            }
            else
            {
                paragraph.Tag = tag;
                paragraph.FontSize = RichTextDocumentConverter.HeadingFontSize(level);
                paragraph.FontWeight = FontWeights.Bold;
            }
        }

        AnnounceFormatting(turnOff ? "Normal text" : $"Heading {level}");
        _vm.MarkBodyDirty();
    }

    /// <summary>
    /// Returns all Paragraphs touched by the current RichBodyBox selection.
    /// When the caret has no selection this is the single caret paragraph.
    /// When the selection ends exactly at a paragraph boundary (e.g. Shift+End
    /// selected to the paragraph break but not into the next paragraph) that
    /// trailing paragraph is excluded.
    /// </summary>
    private List<Paragraph> GetSelectedParagraphs()
    {
        var sel = RichBodyBox.Selection;
        var startPara = sel.Start.Paragraph;
        if (startPara == null) return [];

        var endPara = sel.End.Paragraph;

        // Selection entirely within one paragraph, or no selection.
        if (endPara == null || startPara == endPara)
            return [startPara];

        // If the selection ends at or before the content start of the end paragraph,
        // the user selected up to the paragraph break but didn't intend to include
        // the next line. WPF places element-boundary positions between paragraphs,
        // so sel.End can land in that structural gap (< 0) rather than exactly at
        // ContentStart (== 0); both cases mean the end paragraph was not selected.
        if (sel.End.CompareTo(endPara.ContentStart) <= 0)
            return [startPara];

        // Collect all paragraphs from startPara through endPara in document order.
        var result = new List<Paragraph>();
        bool inRange = false;
        foreach (var block in EnumerateDocumentBlocks(RichBodyBox.Document))
        {
            if (block == startPara) inRange = true;
            if (inRange && block is Paragraph p) result.Add(p);
            if (block == endPara) break;
        }
        return result.Count > 0 ? result : [startPara];
    }

    /// <summary>Flattens top-level blocks and one level of List → ListItem → Block nesting.</summary>
    private static IEnumerable<Block> EnumerateDocumentBlocks(FlowDocument doc)
    {
        foreach (var block in doc.Blocks)
        {
            yield return block;
            if (block is System.Windows.Documents.List list)
                foreach (var item in list.ListItems)
                    foreach (var inner in item.Blocks)
                        yield return inner;
        }
    }

    private static int ListDepthOf(Paragraph? para)
    {
        int depth = 0;
        DependencyObject? el = para?.Parent;
        while (el != null)
        {
            if (el is System.Windows.Documents.List) depth++;
            el = (el as FrameworkContentElement)?.Parent;
        }
        return depth;
    }

    private static string BlockTypeLabel(Paragraph? para)
    {
        if (para?.Tag is string tag)
        {
            return tag switch
            {
                "H1" => "Heading 1",
                "H2" => "Heading 2",
                "H3" => "Heading 3",
                "H4" => "Heading 4",
                "H5" => "Heading 5",
                "H6" => "Heading 6",
                "BLOCKQUOTE" => "Quote",
                _ when RichTextDocumentConverter.IsPreTag(tag) => "Code block",
                _ => "Normal text",
            };
        }
        if (para?.Parent is ListItem item)
        {
            var kind = item.Parent is System.Windows.Documents.List { MarkerStyle: System.Windows.TextMarkerStyle.Decimal }
                ? "Numbered list item" : "Bullet list item";
            var depth = ListDepthOf(para);
            return depth > 1 ? $"{kind}, level {depth}" : kind;
        }
        return "Normal text";
    }

    private void ToggleList(bool ordered)
    {
        var name = ordered ? "Numbered list" : "Bullet list";
        if (_vm.CurrentMode == ComposeMode.Markdown)
        {
            var edit = MarkdownEditing.ToggleListPrefix(
                BodyBox.Text, BodyBox.SelectionStart, BodyBox.SelectionLength, ordered);
            ApplyMarkdownEdit(edit);
            AnnounceFormatting($"{name} {(edit.TurnedOn ? "on" : "off")}");
            return;
        }

        EnsureRichEditorFocused();
        var command = ordered ? EditingCommands.ToggleNumbering : EditingCommands.ToggleBullets;
        command.Execute(null, RichBodyBox);
        var inList = RichBodyBox.CaretPosition.Paragraph?.Parent is ListItem;
        AnnounceFormatting($"{name} {(inList ? "on" : "off")}");
    }

    private void InsertLink()
    {
        var inMarkdown = _vm.CurrentMode == ComposeMode.Markdown;
        var selectionText = inMarkdown
            ? BodyBox.SelectedText.Trim()
            : RichBodyBox.Selection.Text.Trim();
        var dialog = new InsertLinkDialog(selectionText) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            FocusActiveEditor();
            return;
        }

        if (inMarkdown)
        {
            var edit = MarkdownEditing.InsertLink(
                BodyBox.Text, BodyBox.SelectionStart, BodyBox.SelectionLength,
                dialog.DisplayText, dialog.Url);
            ApplyMarkdownEdit(edit);
            AnnounceFormatting("Link inserted");
            return;
        }

        var selection = RichBodyBox.Selection;
        if (!selection.IsEmpty)
            selection.Text = string.Empty;

        var link = new Hyperlink(new Run(dialog.DisplayText), RichBodyBox.CaretPosition);
        if (Uri.TryCreate(dialog.Url, UriKind.Absolute, out var uri))
            link.NavigateUri = uri;
        RichBodyBox.CaretPosition = link.ElementEnd;

        FocusActiveEditor();
        _vm.MarkBodyDirty();
        AnnounceFormatting("Link inserted");
    }

    private void ClearFormatting()
    {
        if (_vm.CurrentMode == ComposeMode.Markdown)
        {
            var edit = MarkdownEditing.ClearFormatting(
                BodyBox.Text, BodyBox.SelectionStart, BodyBox.SelectionLength);
            ApplyMarkdownEdit(edit);
            AnnounceFormatting("Formatting cleared");
            return;
        }

        EnsureRichEditorFocused();
        RichBodyBox.Selection.ClearAllProperties();
        // ClearAllProperties resets character/paragraph formatting but not our
        // heading tags — clear those on the paragraphs the selection touches.
        foreach (var paragraph in new[] { RichBodyBox.Selection.Start.Paragraph, RichBodyBox.Selection.End.Paragraph })
        {
            if (paragraph?.Tag is string)
            {
                paragraph.Tag = null;
                paragraph.ClearValue(TextElement.FontSizeProperty);
                paragraph.ClearValue(TextElement.FontWeightProperty);
            }
        }
        _vm.MarkBodyDirty();
        AnnounceFormatting("Formatting cleared");
    }

    /// <summary>
    /// The formatting at the cursor as one fact per entry — block type first,
    /// then each inline attribute. Shared by the spoken announcement
    /// (Ctrl+Shift+Space) and the Show Formatting list (Ctrl+Alt+Space).
    /// </summary>
    private System.Collections.Generic.List<string> GetFormattingParts()
    {
        if (_vm.CurrentMode == ComposeMode.Markdown)
            return MarkdownEditing.DescribeFormattingParts(BodyBox.Text, BodyBox.CaretIndex);

        var selection = RichBodyBox.Selection;

        string StateOf(object value, object onValue) =>
            value == DependencyProperty.UnsetValue ? "mixed" : Equals(value, onValue) ? "on" : "off";

        var bold   = StateOf(selection.GetPropertyValue(TextElement.FontWeightProperty), FontWeights.Bold);
        var italic = StateOf(selection.GetPropertyValue(TextElement.FontStyleProperty), FontStyles.Italic);
        var underline = SelectionHasDecoration(TextDecorationLocation.Underline) ? "on" : "off";
        var strike    = SelectionHasDecoration(TextDecorationLocation.Strikethrough) ? "on" : "off";

        var paragraph = selection.Start.Paragraph;
        var block = BlockTypeLabel(paragraph);

        return
        [
            block,
            $"Bold {bold}",
            $"Italic {italic}",
            $"Underline {underline}",
            $"Strikethrough {strike}",
        ];
    }

    /// <summary>Announces the formatting at the cursor, e.g. "Heading 2. Bold on, Italic off, …".</summary>
    private void AnnounceFormattingState()
    {
        var parts = GetFormattingParts();
        AnnounceFormatting($"{parts[0]}. {string.Join(", ", parts.Skip(1))}.");
    }

    /// <summary>
    /// Shows the same formatting facts in a modal list the user can arrow
    /// through at their own pace. Escape or Enter closes; focus returns to
    /// the editor.
    /// </summary>
    private void ShowFormatting()
    {
        var window = new FormattingListWindow(GetFormattingParts()) { Owner = this };
        window.ShowDialog();
        FocusActiveEditor();
    }

    // ── Toolbar click handlers (mouse path for the same commands) ────────────

    private void ToolbarBold_Click(object sender, RoutedEventArgs e)          { ToggleBold(); FocusActiveEditor(); }
    private void ToolbarItalic_Click(object sender, RoutedEventArgs e)        { ToggleItalic(); FocusActiveEditor(); }
    private void ToolbarUnderline_Click(object sender, RoutedEventArgs e)     { ToggleUnderline(); FocusActiveEditor(); }
    private void ToolbarStrikethrough_Click(object sender, RoutedEventArgs e) { ToggleStrikethrough(); FocusActiveEditor(); }
    private void ToolbarHeading1_Click(object sender, RoutedEventArgs e)      { ApplyHeading(1); FocusActiveEditor(); }
    private void ToolbarHeading2_Click(object sender, RoutedEventArgs e)      { ApplyHeading(2); FocusActiveEditor(); }
    private void ToolbarHeading3_Click(object sender, RoutedEventArgs e)      { ApplyHeading(3); FocusActiveEditor(); }
    private void ToolbarBulletList_Click(object sender, RoutedEventArgs e)    { ToggleList(ordered: false); FocusActiveEditor(); }
    private void ToolbarNumberedList_Click(object sender, RoutedEventArgs e)  { ToggleList(ordered: true); FocusActiveEditor(); }
    private void ToolbarInsertLink_Click(object sender, RoutedEventArgs e)    => InsertLink();
    private void ToolbarClearFormatting_Click(object sender, RoutedEventArgs e) { ClearFormatting(); FocusActiveEditor(); }

}

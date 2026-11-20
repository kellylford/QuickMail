using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Views;

namespace QuickMail.Controls;

public partial class TokenizedAddressBox : UserControl
{
    private readonly List<AddressChipModel> _chips = new();
    private readonly HashSet<int> _selectedChipIndices = new();

    // Tracks the last string we serialized from chips so we can ignore WPF's
    // deferred source→target binding feedback that would otherwise re-parse our
    // own value and destroy the chips.
    private string _lastSerializedText = string.Empty;

    // Guards CommitCurrentInput against re-entrancy. Assignments to InputBox.Text
    // inside the commit raise TextChanged synchronously, and that handler auto-commits
    // whenever the text still contains a comma or semicolon. Without this guard, text
    // that parses to no routable address but keeps a delimiter (e.g. a pasted bare
    // "Last, First" name) gets written back to the box, re-triggers the commit, and
    // recurses without bound — overflowing the stack and crashing the process. The
    // crash surfaced in the screen reader's in-process event-cache module because its
    // hook rides every UIA event on the stack, but the runaway recursion is ours.
    private bool _isCommitting;

    /// <summary>
    /// When true the LostKeyboardFocus handler will not auto-commit the input.
    /// Set by ComposeWindow while focus is in the autocomplete popup (which lives
    /// outside this control's visual tree) so navigating to a suggestion does not
    /// prematurely commit a partial address as a chip.
    /// </summary>
    internal bool SuppressLostFocusCommit { get; set; }

    public static readonly DependencyProperty AddressTextProperty =
        DependencyProperty.Register(nameof(AddressText), typeof(string), typeof(TokenizedAddressBox),
            new FrameworkPropertyMetadata(string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnAddressTextChanged));

    public string AddressText
    {
        get => (string)GetValue(AddressTextProperty);
        set => SetValue(AddressTextProperty, value);
    }

    private static void OnAddressTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (TokenizedAddressBox)d;
        var newValue = (string)(e.NewValue ?? string.Empty);

        // Skip if this is feedback from our own UpdateAddressText call (deferred TwoWay
        // binding can fire after our flag has already cleared, so we compare by value).
        if (newValue == ctrl._lastSerializedText) return;

        // Record the incoming value before loading so the inevitable deferred feedback
        // from vm.PropertyChanged → binding → this DP is recognised and skipped.
        ctrl._lastSerializedText = newValue;
        ctrl.LoadChipsFromText(newValue);
    }

    /// <summary>Fires when the inner TextBox text changes. Used by ComposeWindow for autocomplete.</summary>
    public event TextChangedEventHandler? InputTextChanged;

    /// <summary>
    /// Called by ComposeWindow when the user chooses "Add to Address Book" from a chip's context menu.
    /// Parameters are (displayName, emailAddress). The delegate may be async.
    /// </summary>
    public Func<string, string, Task>? AddToContactsRequested { get; set; }

    public TokenizedAddressBox()
    {
        InitializeComponent();

        Loaded += OnLoaded;

        InputBox.TextChanged += (s, e) =>
        {
            InputTextChanged?.Invoke(this, e);

            // Auto-commit pasted content that contains delimiters.  Don't call
            // UpdateAddressText here — doing so causes a WPF binding feedback loop
            // that destroys chips whenever the user types (see _lastSerializedText).
            if (InputBox.Text.Contains(',') || InputBox.Text.Contains(';'))
                CommitPendingInput();
        };

        InputBox.PreviewKeyDown += InputBox_PreviewKeyDown;

        // A single-line TextBox keeps only the first line of a multi-line paste, so a list with
        // one address per line lost every address after the first (#980). Every paste gesture
        // (Ctrl+V, Shift+Insert, the context menu) passes through here.
        DataObject.AddPastingHandler(InputBox, InputBox_Pasting);

        InputBox.GotKeyboardFocus += (_, _) => ClearChipSelection();

        InputBox.LostKeyboardFocus += (_, _) =>
            Dispatcher.InvokeAsync(() =>
            {
                if (!IsKeyboardFocusWithin && !SuppressLostFocusCommit)
                    CommitPendingInput();
            }, System.Windows.Threading.DispatcherPriority.Background);

        IsKeyboardFocusWithinChanged += (_, _) =>
            OuterBorder.SetResourceReference(Border.BorderBrushProperty,
                IsKeyboardFocusWithin ? Theming.ThemeKeys.Accent : Theming.ThemeKeys.InputBorder);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // AutomationProperties set on the outer UserControl don't reach the inner InputBox
        // because focus lands on InputBox (the UserControl is Focusable=False). Transfer
        // LabeledBy to InputBox and clear it from the outer control so only one element
        // in the UIA tree carries the label — otherwise screen readers announce the name
        // twice when tabbing in (once for the container, once for the focused input).
        // HelpText is intentionally not propagated: screen readers announce it on every
        // focus, which is too verbose for a field visited repeatedly.
        var labeledBy = AutomationProperties.GetLabeledBy(this);
        if (labeledBy != null)
        {
            AutomationProperties.SetLabeledBy(InputBox, labeledBy);
            AutomationProperties.SetLabeledBy(this, null);
        }
    }

    /// <summary>Text currently being typed — the search token for autocomplete.</summary>
    public string CurrentInputText => InputBox.Text;

    /// <summary>Moves keyboard focus to the inner text input.</summary>
    public void FocusInput() => InputBox.Focus();

    /// <summary>Commits any text in the inner input box as chips.
    /// Called by ComposeWindow before Send so no typed address is silently dropped.</summary>
    public void CommitPendingInput() => CommitCurrentInput();

    /// <summary>Adds a contact as a chip without changing keyboard focus. Use when inserting from the address book.</summary>
    public void AddAddress(string displayName, string emailAddress)
        => AddChip(new AddressChipModel { DisplayName = displayName, EmailAddress = emailAddress });

    /// <summary>Commits a contact suggestion as a chip and refocuses the input.</summary>
    public void AcceptSuggestion(string displayName, string emailAddress)
    {
        InputBox.Text = string.Empty;
        AddChip(new AddressChipModel { DisplayName = displayName, EmailAddress = emailAddress });
        // Defer focus return so that the key event that triggered acceptance (Enter)
        // finishes routing in the popup's HwndSource before focus transitions to the
        // main window — otherwise the IsDefault Send button activates.
        Dispatcher.InvokeAsync(FocusInput, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Returns a snapshot of current chips for Ctrl+K validation.</summary>
    public IReadOnlyList<AddressChipModel> GetChips() => _chips.AsReadOnly();

    /// <summary>Updates a chip's data and visual state after validation.</summary>
    public void UpdateChip(int index, string displayName, string emailAddress, bool isInvalid)
    {
        if (index < 0 || index >= _chips.Count) return;
        _chips[index].DisplayName = displayName;
        _chips[index].EmailAddress = emailAddress;
        _chips[index].IsInvalid = isInvalid;
        RefreshChipButton(index);
        UpdateAddressText();
    }

    // ── Parsing ───────────────────────────────────────────────────────────────

    private void LoadChipsFromText(string text)
    {
        while (ChipPanel.Children.Count > 1)
            ChipPanel.Children.RemoveAt(0);
        _chips.Clear();
        _selectedChipIndices.Clear();

        if (string.IsNullOrWhiteSpace(text)) return;

        // The bound text is our own "; "-joined serialization, so it must be read with the
        // same splitting a paste gets (#979): MimeKit alone cannot read a semicolon list.
        var parsed = AddressInput.Parse(text);
        foreach (var mb in parsed.Mailboxes)
        {
            var chip = new AddressChipModel { DisplayName = mb.Name, EmailAddress = mb.Address };
            _chips.Add(chip);
            ChipPanel.Children.Insert(ChipPanel.Children.Count - 1, CreateChipButton(chip, _chips.Count - 1));
        }

        // Text with no address in it goes back to the input box for the user to correct.
        if (parsed.Leftover.Length > 0)
            SetLeftover(parsed.Leftover);
    }

    private void CommitCurrentInput()
    {
        // Re-entrancy guard: the InputBox.Text assignments below fire TextChanged
        // synchronously, and that handler calls back here on comma/semicolon. If a
        // re-entrant call slips through it would recurse without bound (see _isCommitting).
        if (_isCommitting) return;
        _isCommitting = true;
        try
        {
            var raw = InputBox.Text.Trim().TrimEnd(',', ';').Trim();
            if (string.IsNullOrEmpty(raw)) { InputBox.Text = string.Empty; return; }

            // Clear first so UpdateAddressText called from AddChip excludes this raw text
            InputBox.Text = string.Empty;

            // One chip per address, whether the list was separated by commas, semicolons
            // (Outlook's separator and our own copy format, #979) or line breaks (#980).
            var parsed = AddressInput.Parse(raw);
            foreach (var mb in parsed.Mailboxes)
                AddChip(new AddressChipModel { DisplayName = mb.Name, EmailAddress = mb.Address });

            // Not recognisable — return to the input box so the user can fix it
            if (parsed.Leftover.Length > 0)
                SetLeftover(parsed.Leftover);
        }
        finally
        {
            _isCommitting = false;
        }
    }

    /// <summary>
    /// Replaces pasted text holding line breaks or tabs with the same addresses separated by "; ",
    /// which the input then commits as chips like any other separated list (#980).
    /// </summary>
    internal static void InputBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.SourceDataObject.GetData(DataFormats.UnicodeText, true) is not string text) return;
        if (AddressInput.FlattenForSingleLine(text) is not { } flat) return;

        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, flat);
        data.SetData(DataFormats.Text, flat);
        e.DataObject = data;
        e.FormatToApply = DataFormats.UnicodeText;
    }

    /// <summary>Puts text that held no address back in the input, with the caret after it so typing continues it.</summary>
    private void SetLeftover(string text)
    {
        InputBox.Text = text;
        InputBox.CaretIndex = text.Length;
    }

    // ── Chip management ───────────────────────────────────────────────────────

    private void AddChip(AddressChipModel chip)
    {
        _chips.Add(chip);
        ChipPanel.Children.Insert(ChipPanel.Children.Count - 1, CreateChipButton(chip, _chips.Count - 1));
        UpdateAddressText();
    }

    private void RemoveChipAt(int index)
    {
        if (index < 0 || index >= _chips.Count) return;
        _chips.RemoveAt(index);
        ChipPanel.Children.RemoveAt(index);
        UpdateAddressText();
        for (int i = index; i < _chips.Count; i++)
            ((Button)ChipPanel.Children[i]).Tag = i;
        if (_chips.Count == 0)
            FocusInput();
        else
            ((Button)ChipPanel.Children[Math.Min(index, _chips.Count - 1)]).Focus();
    }

    private static string ChipAccessibleName(AddressChipModel chip) =>
        chip.IsInvalid ? $"Unrecognized: {chip.FullAddress}" : chip.FullAddress;

    private Button CreateChipButton(AddressChipModel chip, int index)
    {
        var fullAddress = chip.FullAddress;
        var btn = new Button
        {
            Content = chip.Label,
            Tag = index,
            ToolTip = fullAddress,
            Style = (Style)FindResource("ChipStyle")
        };
        ApplyChipValidationStyle(btn, chip.IsInvalid);
        // Accessible name is the full address; prefix "Unrecognized:" when validation fails
        // so screen readers convey the error state without relying on colour alone.
        AutomationProperties.SetName(btn, ChipAccessibleName(chip));
        btn.Click += (_, _) => { ClearChipSelection(); btn.Focus(); };
        btn.PreviewKeyDown += ChipButton_PreviewKeyDown;
        btn.ContextMenu = CreateChipContextMenu(chip, btn);
        return btn;
    }

    private void RefreshChipButton(int index)
    {
        if (index < 0 || index >= _chips.Count) return;
        var chip = _chips[index];
        var btn = (Button)ChipPanel.Children[index];
        btn.Content = chip.Label;
        var fullAddress = chip.FullAddress;
        btn.ToolTip = fullAddress;
        ApplyChipValidationStyle(btn, chip.IsInvalid);
        AutomationProperties.SetName(btn, ChipAccessibleName(chip));
        btn.ContextMenu = CreateChipContextMenu(chip, btn);
    }

    private static void ApplyChipValidationStyle(Button btn, bool isInvalid)
    {
        if (isInvalid)
        {
            // Resource references (not brush copies) so the error state restyles
            // live on theme change and stays visually distinct in every theme.
            btn.SetResourceReference(BackgroundProperty, Theming.ThemeKeys.ErrorBackground);
            btn.SetResourceReference(BorderBrushProperty, Theming.ThemeKeys.Error);
        }
        else
        {
            btn.ClearValue(BackgroundProperty);
            btn.ClearValue(BorderBrushProperty);
        }
    }

    // ── Chip multi-select ─────────────────────────────────────────────────────

    private void SetChipSelected(int index, bool selected)
    {
        if (index < 0 || index >= _chips.Count) return;
        var btn = (Button)ChipPanel.Children[index];
        if (selected)
        {
            btn.SetResourceReference(BackgroundProperty, Theming.ThemeKeys.SelectionBackground);
            btn.SetResourceReference(ForegroundProperty, Theming.ThemeKeys.SelectionText);
        }
        else
        {
            btn.ClearValue(ForegroundProperty);
            ApplyChipValidationStyle(btn, _chips[index].IsInvalid);
        }
    }

    private void ClearChipSelection()
    {
        foreach (var idx in _selectedChipIndices)
            SetChipSelected(idx, false);
        _selectedChipIndices.Clear();
    }

    private void SelectAllChips()
    {
        if (_chips.Count == 0) return;
        ClearChipSelection();
        for (int i = 0; i < _chips.Count; i++)
        {
            _selectedChipIndices.Add(i);
            SetChipSelected(i, true);
        }
        AccessibilityHelper.Announce(this,
            $"{_chips.Count} address{(_chips.Count == 1 ? "" : "es")} selected.",
            category: AnnouncementCategory.Result);
    }

    private void RemoveSelectedChips()
    {
        var indices = _selectedChipIndices.OrderByDescending(i => i).ToList();
        _selectedChipIndices.Clear();
        foreach (var idx in indices)
        {
            if (idx < 0 || idx >= _chips.Count) continue;
            _chips.RemoveAt(idx);
            ChipPanel.Children.RemoveAt(idx);
        }
        UpdateAddressText();
        for (int i = 0; i < _chips.Count; i++)
            ((Button)ChipPanel.Children[i]).Tag = i;
        FocusInput();
    }

    private ContextMenu CreateChipContextMenu(AddressChipModel chip, Button btn)
    {
        var menu = new ContextMenu();

        var copy = new MenuItem { Header = "Copy _Address" };
        copy.Click += (_, _) => Clipboard.SetText(chip.Serialize());

        var addToBook = new MenuItem { Header = "Add to Address _Book" };
        addToBook.Click += async (_, _) =>
        {
            if (AddToContactsRequested != null)
                await AddToContactsRequested(chip.DisplayName, chip.EmailAddress);
        };

        var remove = new MenuItem { Header = "_Remove" };
        remove.Click += (_, _) => RemoveChipAt((int)btn.Tag);

        menu.Items.Add(copy);
        menu.Items.Add(addToBook);
        menu.Items.Add(new Separator());
        menu.Items.Add(remove);
        return menu;
    }

    private void UpdateAddressText()
    {
        var serialized = string.Join("; ", _chips.Select(c => c.Serialize()));
        _lastSerializedText = serialized;
        if (AddressText != serialized)
            AddressText = serialized;
    }

    // ── Keyboard handlers ─────────────────────────────────────────────────────

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;

        if ((e.Key == Key.OemComma || e.Key == Key.OemSemicolon) && mods == ModifierKeys.None)
        {
            CommitCurrentInput();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Return && mods == ModifierKeys.None)
        {
            CommitCurrentInput();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Tab)
        {
            CommitCurrentInput();
            return; // don't mark Handled — let Tab move focus to the next field
        }

        if (e.Key == Key.A && mods == ModifierKeys.Control
            && string.IsNullOrEmpty(InputBox.Text) && _chips.Count > 0)
        {
            SelectAllChips();
            e.Handled = true;
            return;
        }

        if ((e.Key == Key.Back || e.Key == Key.Delete)
            && string.IsNullOrEmpty(InputBox.Text) && _selectedChipIndices.Count > 0)
        {
            RemoveSelectedChips();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Back && string.IsNullOrEmpty(InputBox.Text) && _chips.Count > 0)
        {
            RemoveChipAt(_chips.Count - 1);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Left && InputBox.CaretIndex == 0 && _chips.Count > 0)
        {
            ClearChipSelection();
            ((Button)ChipPanel.Children[_chips.Count - 1]).Focus();
            e.Handled = true;
        }
    }

    private void ChipButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var btn = (Button)sender;
        var index = (int)btn.Tag;

        switch (e.Key)
        {
            case Key.Delete:
            case Key.Back:
                if (_selectedChipIndices.Count > 1 && _selectedChipIndices.Contains(index))
                    RemoveSelectedChips();
                else
                    RemoveChipAt(index);
                e.Handled = true;
                break;

            case Key.Left:
                ClearChipSelection();
                if (index > 0) ((Button)ChipPanel.Children[index - 1]).Focus();
                e.Handled = true;
                break;

            case Key.Right:
                ClearChipSelection();
                if (index < _chips.Count - 1) ((Button)ChipPanel.Children[index + 1]).Focus();
                else FocusInput();
                e.Handled = true;
                break;

            case Key.A when Keyboard.Modifiers == ModifierKeys.Control:
                SelectAllChips();
                e.Handled = true;
                break;

            case Key.C when Keyboard.Modifiers == ModifierKeys.Control:
                var addresses = _selectedChipIndices.Count > 1
                    ? string.Join("; ", _selectedChipIndices.OrderBy(i => i).Select(i => _chips[i].Serialize()))
                    : _chips[index].Serialize();
                Clipboard.SetText(addresses);
                e.Handled = true;
                break;
        }
    }

}

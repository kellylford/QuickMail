using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MimeKit;

namespace QuickMail.Controls;

public partial class TokenizedAddressBox : UserControl
{
    private readonly List<AddressChipModel> _chips = new();

    // Tracks the last string we serialized from chips so we can ignore WPF's
    // deferred source→target binding feedback that would otherwise re-parse our
    // own value and destroy the chips.
    private string _lastSerializedText = string.Empty;

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

        InputBox.LostKeyboardFocus += (_, _) =>
            Dispatcher.InvokeAsync(() =>
            {
                if (!IsKeyboardFocusWithin && !SuppressLostFocusCommit)
                    CommitPendingInput();
            }, System.Windows.Threading.DispatcherPriority.Background);

        IsKeyboardFocusWithinChanged += (_, _) =>
            OuterBorder.BorderBrush = IsKeyboardFocusWithin
                ? SystemColors.HotTrackBrush
                : SystemColors.ControlDarkBrush;
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

        if (string.IsNullOrWhiteSpace(text)) return;

        if (InternetAddressList.TryParse(text, out var list))
        {
            foreach (var addr in list.OfType<MailboxAddress>())
            {
                // Skip bare words / local-only strings MimeKit accepted but that aren't
                // routable email addresses (e.g. "k", "hello" with no domain).
                if (string.IsNullOrWhiteSpace(addr.Address)) continue;
                if (!addr.Address.Contains('@')) continue;
                var chip = new AddressChipModel
                {
                    DisplayName = addr.Name ?? string.Empty,
                    EmailAddress = addr.Address
                };
                _chips.Add(chip);
                ChipPanel.Children.Insert(ChipPanel.Children.Count - 1, CreateChipButton(chip, _chips.Count - 1));
            }
        }
        else
        {
            // Unparseable — put into InputBox for the user to correct
            InputBox.Text = text;
        }
    }

    private void CommitCurrentInput()
    {
        var raw = InputBox.Text.Trim().TrimEnd(',', ';').Trim();
        if (string.IsNullOrEmpty(raw)) { InputBox.Text = string.Empty; return; }

        // Clear first so UpdateAddressText called from AddChip excludes this raw text
        InputBox.Text = string.Empty;

        if (InternetAddressList.TryParse(raw, out var list))
        {
            bool added = false;
            foreach (var addr in list.OfType<MailboxAddress>())
            {
                if (string.IsNullOrWhiteSpace(addr.Address)) continue;
                if (!addr.Address.Contains('@')) continue;
                AddChip(new AddressChipModel { DisplayName = addr.Name ?? string.Empty, EmailAddress = addr.Address });
                added = true;
            }
            if (!added)
            {
                // MimeKit parsed it but produced no usable address — treat as bare email
                if (raw.Contains('@'))
                    AddChip(new AddressChipModel { EmailAddress = raw });
                else
                    InputBox.Text = raw; // return unrecognised text to the input box
            }
        }
        else if (raw.Contains('@'))
        {
            AddChip(new AddressChipModel { EmailAddress = raw });
        }
        else
        {
            // Not recognisable — return to the input box so the user can fix it
            InputBox.Text = raw;
        }
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
        btn.Click += (_, _) => btn.Focus();
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
            btn.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xEE, 0xEE));
            btn.BorderBrush = Brushes.Red;
        }
        else
        {
            btn.ClearValue(BackgroundProperty);
            btn.ClearValue(BorderBrushProperty);
        }
    }

    private ContextMenu CreateChipContextMenu(AddressChipModel chip, Button btn)
    {
        var menu = new ContextMenu();

        var copy = new MenuItem { Header = "Copy _Address" };
        copy.Click += (_, _) => Clipboard.SetText(chip.FullAddress);

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

        if (e.Key == Key.Back && string.IsNullOrEmpty(InputBox.Text) && _chips.Count > 0)
        {
            RemoveChipAt(_chips.Count - 1);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Left && InputBox.CaretIndex == 0 && _chips.Count > 0)
        {
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
                RemoveChipAt(index);
                e.Handled = true;
                break;

            case Key.Left:
                if (index > 0) ((Button)ChipPanel.Children[index - 1]).Focus();
                e.Handled = true;
                break;

            case Key.Right:
                if (index < _chips.Count - 1) ((Button)ChipPanel.Children[index + 1]).Focus();
                else FocusInput();
                e.Handled = true;
                break;

            case Key.C when Keyboard.Modifiers == ModifierKeys.Control:
                Clipboard.SetText(_chips[index].FullAddress);
                e.Handled = true;
                break;
        }
    }

}

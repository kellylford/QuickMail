using System;
using System.Collections.Generic;
using System.Linq;
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
    private bool _updatingFromProperty;

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
        if (ctrl._updatingFromProperty) return;
        ctrl._updatingFromProperty = true;
        try { ctrl.LoadChipsFromText((string)(e.NewValue ?? string.Empty)); }
        finally { ctrl._updatingFromProperty = false; }
    }

    /// <summary>Raised when the inner input TextBox text changes. Used by ComposeWindow for autocomplete.</summary>
    public event TextChangedEventHandler? InputTextChanged;

    public TokenizedAddressBox()
    {
        InitializeComponent();

        InputBox.TextChanged += (s, e) =>
        {
            InputTextChanged?.Invoke(this, e);
            UpdateAddressText();
            // Auto-commit if pasted content includes delimiters
            if (InputBox.Text.Contains(',') || InputBox.Text.Contains(';'))
                CommitCurrentInput();
        };

        InputBox.PreviewKeyDown += InputBox_PreviewKeyDown;

        InputBox.LostKeyboardFocus += (_, _) =>
            Dispatcher.InvokeAsync(() =>
            {
                // If focus stayed within the control (e.g. moved to a chip), don't commit
                if (!IsKeyboardFocusWithin)
                    CommitCurrentInput();
            }, System.Windows.Threading.DispatcherPriority.Background);

        IsKeyboardFocusWithinChanged += (_, _) =>
            OuterBorder.BorderBrush = IsKeyboardFocusWithin
                ? SystemColors.HotTrackBrush
                : SystemColors.ControlDarkBrush;
    }

    /// <summary>The text currently being typed in the input box. Used as the autocomplete search token.</summary>
    public string CurrentInputText => InputBox.Text;

    /// <summary>Moves keyboard focus to the inner text input.</summary>
    public void FocusInput() => InputBox.Focus();

    /// <summary>Commits a contact suggestion as a chip and refocuses the input.</summary>
    public void AcceptSuggestion(string displayName, string emailAddress)
    {
        InputBox.Text = string.Empty;
        AddChip(new AddressChipModel { DisplayName = displayName, EmailAddress = emailAddress });
        FocusInput();
    }

    /// <summary>Returns a snapshot of the current chips for Ctrl+K validation.</summary>
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
            // Unparseable text — put into InputBox for the user to correct
            InputBox.Text = text;
        }
    }

    private void CommitCurrentInput()
    {
        var raw = InputBox.Text.Trim().TrimEnd(',', ';').Trim();
        if (string.IsNullOrEmpty(raw)) { InputBox.Text = string.Empty; return; }

        // Clear first so UpdateAddressText called from AddChip doesn't include raw text
        InputBox.Text = string.Empty;

        if (InternetAddressList.TryParse(raw, out var list))
        {
            foreach (var addr in list.OfType<MailboxAddress>())
                AddChip(new AddressChipModel { DisplayName = addr.Name ?? string.Empty, EmailAddress = addr.Address });
        }
        else if (raw.Contains('@'))
        {
            // Bare email without display name
            AddChip(new AddressChipModel { EmailAddress = raw });
        }
        else
        {
            // Not recognizable as an address — return to input box so user can fix it
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
        // Re-index remaining chip button Tags
        for (int i = index; i < _chips.Count; i++)
            ((Button)ChipPanel.Children[i]).Tag = i;
        // Move focus to the adjacent chip or the input box
        if (_chips.Count == 0)
            FocusInput();
        else
            ((Button)ChipPanel.Children[Math.Min(index, _chips.Count - 1)]).Focus();
    }

    private Button CreateChipButton(AddressChipModel chip, int index)
    {
        var fullAddress = chip.FullAddress;
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = chip.Label,
            VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = " ×",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = SystemColors.GrayTextBrush,
            FontSize = 10
        });

        var btn = new Button
        {
            Content = panel,
            Tag = index,
            ToolTip = fullAddress,
            Style = (Style)FindResource("ChipStyle")
        };
        ApplyChipValidationStyle(btn, chip.IsInvalid);
        AutomationProperties.SetName(btn, fullAddress);
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
        var panel = (StackPanel)btn.Content;
        ((System.Windows.Controls.TextBlock)panel.Children[0]).Text = chip.Label;
        btn.ToolTip = chip.FullAddress;
        ApplyChipValidationStyle(btn, chip.IsInvalid);
        AutomationProperties.SetName(btn, chip.FullAddress);
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
        var remove = new MenuItem { Header = "_Remove" };
        remove.Click += (_, _) => RemoveChipAt((int)btn.Tag);
        menu.Items.Add(copy);
        menu.Items.Add(remove);
        return menu;
    }

    private void UpdateAddressText()
    {
        if (_updatingFromProperty) return;
        _updatingFromProperty = true;
        try
        {
            var parts = _chips.Select(c => c.Serialize()).ToList();
            // Include any uncommitted text so SendAsync doesn't see an empty To field
            var inputRaw = InputBox.Text.Trim().TrimEnd(',', ';').Trim();
            if (!string.IsNullOrEmpty(inputRaw))
                parts.Add(inputRaw);
            AddressText = string.Join("; ", parts);
        }
        finally
        {
            _updatingFromProperty = false;
        }
    }

    // ── Keyboard handlers ─────────────────────────────────────────────────────

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;

        // Comma or semicolon: commit current input and stay in the field
        if ((e.Key == Key.OemComma || e.Key == Key.OemSemicolon) && mods == ModifierKeys.None)
        {
            CommitCurrentInput();
            e.Handled = true;
            return;
        }

        // Enter: commit and stay
        if (e.Key == Key.Return && mods == ModifierKeys.None)
        {
            CommitCurrentInput();
            e.Handled = true;
            return;
        }

        // Tab: commit pending text, then let Tab move focus to next field
        if (e.Key == Key.Tab)
        {
            CommitCurrentInput();
            return; // don't mark Handled — Tab propagates normally
        }

        // Backspace on empty input: remove last chip
        if (e.Key == Key.Back && string.IsNullOrEmpty(InputBox.Text) && _chips.Count > 0)
        {
            RemoveChipAt(_chips.Count - 1);
            e.Handled = true;
            return;
        }

        // Left arrow at position 0: move focus to last chip
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

    private void UserControl_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Redirect focus arriving directly on the UserControl to the input box
        if (e.NewFocus == this)
            FocusInput();
    }
}

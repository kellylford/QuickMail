using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

public partial class FlagPickerWindow : Window
{
    private readonly FlagPickerViewModel _vm;
    private readonly CommandRegistry _localRegistry = new();
    private CancellationTokenSource? _loadCts;

    public string? ResultFlagId { get; private set; }

    public FlagPickerWindow(IFlagService flagService, bool currentlyFlagged)
    {
        _vm = new FlagPickerViewModel(flagService, currentlyFlagged);
        InitializeComponent();
        DataContext = _vm;

        RegisterLocalCommands();

        _vm.FlagSelected += OnFlagSelected;

        Loaded += async (_, _) =>
        {
            _loadCts = new CancellationTokenSource();
            try
            {
                await _vm.LoadAsync();
                if (!_loadCts.IsCancellationRequested)
                    FlagList.Focus();
            }
            catch (OperationCanceledException) { }
        };
    }

    private void RegisterLocalCommands()
    {
        _localRegistry.Register(new CommandDefinition(
            "flagpicker.apply", "Flag", "Apply Selected Flag",
            () => _vm.ApplyFlagCommand.Execute(null),
            isAvailable: () => _vm.SelectedFlag != null));
        _localRegistry.Register(new CommandDefinition(
            "flagpicker.clear", "Flag", "Clear Flag",
            () => _vm.ClearFlagCommand.Execute(null)));
        _localRegistry.Register(new CommandDefinition(
            "flagpicker.cancel", "Flag", "Cancel",
            () => { DialogResult = false; }));
    }

    private void OnFlagSelected(FlagDefinition? flag)
    {
        ResultFlagId = flag?.Id.ToString();
        DialogResult = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (e.Key == Key.F6)
        {
            CycleFocus(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            e.Handled = true;
            return;
        }

        if (e.Key == Key.P &&
            Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            OpenCommandPalette();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && FlagList.IsKeyboardFocusWithin)
        {
            _vm.ApplyFlagCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void CycleFocus(bool reverse)
    {
        UIElement[] panes = [FlagList, ClearButton, CancelButton];
        int current = -1;
        for (int i = 0; i < panes.Length; i++)
        {
            if (panes[i].IsKeyboardFocusWithin) { current = i; break; }
        }
        int next = reverse
            ? (current <= 0 ? panes.Length - 1 : current - 1)
            : (current >= panes.Length - 1 ? 0 : current + 1);
        panes[next].Focus();
    }

    private void OpenCommandPalette()
    {
        var prev = Keyboard.FocusedElement as IInputElement;
        var palette = new CommandPaletteWindow(_localRegistry) { Owner = this };
        palette.ShowDialog();
        (prev ?? FlagList).Focus();
    }

    protected override void OnClosed(EventArgs e)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _vm.FlagSelected -= OnFlagSelected;
        base.OnClosed(e);
    }
}

using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace QuickMail.ViewModels;

public class TutorialStep
{
    public string InstructionText { get; init; } = string.Empty;
    public Key ExpectedKey { get; init; }
    public ModifierKeys ExpectedModifiers { get; init; }
    public string SuccessMessage { get; init; } = string.Empty;
}

public partial class TutorialViewModel : ObservableObject
{
    [ObservableProperty]
    private int _currentStepIndex;

    [ObservableProperty]
    private bool _isActive;

    public ObservableCollection<TutorialStep> Steps { get; } = new();

    public TutorialStep? CurrentStep =>
        CurrentStepIndex >= 0 && CurrentStepIndex < Steps.Count
            ? Steps[CurrentStepIndex]
            : null;

    /// <summary>1-based step number for display.</summary>
    public int CurrentStepNumber => CurrentStepIndex + 1;

    /// <summary>Raised when the user completes all six steps.</summary>
    public event Action? TutorialCompleted;

    /// <summary>Raised when the user cancels the tutorial early (Escape).</summary>
    public event Action? TutorialCancelled;

    public TutorialViewModel()
    {
        Steps.Add(new TutorialStep
        {
            InstructionText = "Press F6 to cycle focus through all panes.",
            ExpectedKey = Key.F6,
            ExpectedModifiers = ModifierKeys.None,
            SuccessMessage = "Correct! F6 cycles focus through all panes: toolbar, account list, folder tree, message list, reading pane, and status bar."
        });
        Steps.Add(new TutorialStep
        {
            InstructionText = "Press Ctrl+1 to focus the account list.",
            ExpectedKey = Key.D1,
            ExpectedModifiers = ModifierKeys.Control,
            SuccessMessage = "Correct! Ctrl+1 moves focus to the account list."
        });
        Steps.Add(new TutorialStep
        {
            InstructionText = "Press Ctrl+2 to focus the folder tree.",
            ExpectedKey = Key.D2,
            ExpectedModifiers = ModifierKeys.Control,
            SuccessMessage = "Correct! Ctrl+2 moves focus to the folder tree."
        });
        Steps.Add(new TutorialStep
        {
            InstructionText = "Press Ctrl+3 to focus the message list.",
            ExpectedKey = Key.D3,
            ExpectedModifiers = ModifierKeys.Control,
            SuccessMessage = "Correct! Ctrl+3 moves focus to the message list."
        });
        Steps.Add(new TutorialStep
        {
            InstructionText = "Press Ctrl+Shift+P to open the Command Palette.",
            ExpectedKey = Key.P,
            ExpectedModifiers = ModifierKeys.Control | ModifierKeys.Shift,
            SuccessMessage = "Correct! Ctrl+Shift+P opens the Command Palette where you can search for any command."
        });
        Steps.Add(new TutorialStep
        {
            InstructionText = "Press Escape to close the reading pane or dismiss dialogs.",
            ExpectedKey = Key.Escape,
            ExpectedModifiers = ModifierKeys.None,
            SuccessMessage = "Correct! Escape returns focus to the message list from the reading pane."
        });
    }

    /// <summary>
    /// Checks whether the pressed key matches the current step's expected key.
    /// Returns true if the key was correct and the step advanced.
    /// </summary>
    public bool CheckKeyPress(Key key, ModifierKeys modifiers)
    {
        if (!IsActive || CurrentStep == null)
            return false;

        // Escape cancels the tutorial UNLESS the current step expects Escape
        if (key == Key.Escape && modifiers == ModifierKeys.None
            && CurrentStep.ExpectedKey != Key.Escape)
        {
            Cancel();
            return true;
        }

        var step = CurrentStep;
        if (key == step.ExpectedKey && modifiers == step.ExpectedModifiers)
        {
            Advance();
            return true;
        }

        return false;
    }

    private void Advance()
    {
        if (CurrentStepIndex < Steps.Count - 1)
        {
            CurrentStepIndex++;
            OnPropertyChanged(nameof(CurrentStepNumber));
            // Notify the view to announce the new step
            OnPropertyChanged(nameof(CurrentStep));
        }
        else
        {
            IsActive = false;
            TutorialCompleted?.Invoke();
        }
    }

    public void Cancel()
    {
        IsActive = false;
        TutorialCancelled?.Invoke();
    }

    /// <summary>Starts the tutorial from step 0.</summary>
    public void Start()
    {
        CurrentStepIndex = 0;
        IsActive = true;
        OnPropertyChanged(nameof(CurrentStep));
        OnPropertyChanged(nameof(CurrentStepNumber));
    }
}

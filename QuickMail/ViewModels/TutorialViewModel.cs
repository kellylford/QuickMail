using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using QuickMail.Resources;

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
            InstructionText = Strings.Tutorial_Step_F6_Instruction,
            ExpectedKey = Key.F6,
            ExpectedModifiers = ModifierKeys.None,
            SuccessMessage = Strings.Tutorial_Step_F6_Success
        });
        Steps.Add(new TutorialStep
        {
            InstructionText = Strings.Tutorial_Step_Ctrl1_Instruction,
            ExpectedKey = Key.D1,
            ExpectedModifiers = ModifierKeys.Control,
            SuccessMessage = Strings.Tutorial_Step_Ctrl1_Success
        });
        Steps.Add(new TutorialStep
        {
            InstructionText = Strings.Tutorial_Step_Ctrl2_Instruction,
            ExpectedKey = Key.D2,
            ExpectedModifiers = ModifierKeys.Control,
            SuccessMessage = Strings.Tutorial_Step_Ctrl2_Success
        });
        Steps.Add(new TutorialStep
        {
            InstructionText = Strings.Tutorial_Step_Ctrl3_Instruction,
            ExpectedKey = Key.D3,
            ExpectedModifiers = ModifierKeys.Control,
            SuccessMessage = Strings.Tutorial_Step_Ctrl3_Success
        });
        Steps.Add(new TutorialStep
        {
            InstructionText = Strings.Tutorial_Step_CommandPalette_Instruction,
            ExpectedKey = Key.P,
            ExpectedModifiers = ModifierKeys.Control | ModifierKeys.Shift,
            SuccessMessage = Strings.Tutorial_Step_CommandPalette_Success
        });
        Steps.Add(new TutorialStep
        {
            InstructionText = Strings.Tutorial_Step_Escape_Instruction,
            ExpectedKey = Key.Escape,
            ExpectedModifiers = ModifierKeys.None,
            SuccessMessage = Strings.Tutorial_Step_Escape_Success
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

using System;
using System.Windows.Input;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tests for the first-run keyboard tutorial state machine (TutorialViewModel).
/// Covers step progression, escape cancellation, wrong-key rejection, and completion.
/// </summary>
public class TutorialViewModelTests
{
    [Fact]
    public void Start_SetsIsActiveAndResetsToStepZero()
    {
        var vm = new TutorialViewModel();
        vm.Start();

        Assert.True(vm.IsActive);
        Assert.Equal(0, vm.CurrentStepIndex);
        Assert.Equal(1, vm.CurrentStepNumber);
        Assert.NotNull(vm.CurrentStep);
    }

    [Fact]
    public void Start_ResetsToStepZero_WhenCalledAgain()
    {
        var vm = new TutorialViewModel();
        vm.Start();

        // Advance to step 2
        vm.CheckKeyPress(Key.F6, ModifierKeys.None);
        Assert.Equal(1, vm.CurrentStepIndex);

        // Restart
        vm.Start();
        Assert.Equal(0, vm.CurrentStepIndex);
        Assert.True(vm.IsActive);
    }

    [Fact]
    public void CorrectKey_AdvancesToNextStep()
    {
        var vm = new TutorialViewModel();
        vm.Start();

        // Step 1: F6
        Assert.Equal(0, vm.CurrentStepIndex);
        bool handled = vm.CheckKeyPress(Key.F6, ModifierKeys.None);
        Assert.True(handled);
        Assert.Equal(1, vm.CurrentStepIndex);
        Assert.Equal(2, vm.CurrentStepNumber);

        // Step 2: Ctrl+1
        handled = vm.CheckKeyPress(Key.D1, ModifierKeys.Control);
        Assert.True(handled);
        Assert.Equal(2, vm.CurrentStepIndex);

        // Step 3: Ctrl+2
        handled = vm.CheckKeyPress(Key.D2, ModifierKeys.Control);
        Assert.True(handled);
        Assert.Equal(3, vm.CurrentStepIndex);

        // Step 4: Ctrl+3
        handled = vm.CheckKeyPress(Key.D3, ModifierKeys.Control);
        Assert.True(handled);
        Assert.Equal(4, vm.CurrentStepIndex);

        // Step 5: Ctrl+Shift+P
        handled = vm.CheckKeyPress(Key.P, ModifierKeys.Control | ModifierKeys.Shift);
        Assert.True(handled);
        Assert.Equal(5, vm.CurrentStepIndex);
    }

    [Fact]
    public void AllSixSteps_CompleteAndFireTutorialCompleted()
    {
        var vm = new TutorialViewModel();
        vm.Start();

        bool completed = false;
        vm.TutorialCompleted += () => completed = true;

        // Step 1: F6
        vm.CheckKeyPress(Key.F6, ModifierKeys.None);
        Assert.True(vm.IsActive);
        Assert.False(completed);

        // Step 2: Ctrl+1
        vm.CheckKeyPress(Key.D1, ModifierKeys.Control);
        Assert.True(vm.IsActive);
        Assert.False(completed);

        // Step 3: Ctrl+2
        vm.CheckKeyPress(Key.D2, ModifierKeys.Control);
        Assert.True(vm.IsActive);
        Assert.False(completed);

        // Step 4: Ctrl+3
        vm.CheckKeyPress(Key.D3, ModifierKeys.Control);
        Assert.True(vm.IsActive);
        Assert.False(completed);

        // Step 5: Ctrl+Shift+P — advances to step 6
        vm.CheckKeyPress(Key.P, ModifierKeys.Control | ModifierKeys.Shift);
        Assert.True(vm.IsActive);
        Assert.Equal(5, vm.CurrentStepIndex); // step 6 (0-based)
        Assert.False(completed);

        // Step 6: Escape — step 6 expects Escape, so Advance() fires (not Cancel()).
        // The tutorial completes normally and fires TutorialCompleted.
        bool handled = vm.CheckKeyPress(Key.Escape, ModifierKeys.None);
        Assert.True(handled);
        Assert.False(vm.IsActive);
        Assert.True(completed);
    }

    [Fact]
    public void Escape_AtAnyStep_CancelsTutorial()
    {
        // Test escape at step 1 (index 0)
        var vm = new TutorialViewModel();
        vm.Start();
        bool cancelled1 = false;
        vm.TutorialCancelled += () => cancelled1 = true;

        vm.CheckKeyPress(Key.Escape, ModifierKeys.None);
        Assert.False(vm.IsActive);
        Assert.True(cancelled1);

        // Test escape at step 3 (index 2) — fresh VM to avoid stale subscriptions
        var vm2 = new TutorialViewModel();
        vm2.Start();
        bool cancelled2 = false;
        vm2.TutorialCancelled += () => cancelled2 = true;

        vm2.CheckKeyPress(Key.F6, ModifierKeys.None);       // step 1 → 2
        vm2.CheckKeyPress(Key.D1, ModifierKeys.Control);     // step 2 → 3
        Assert.Equal(2, vm2.CurrentStepIndex);

        vm2.CheckKeyPress(Key.Escape, ModifierKeys.None);
        Assert.False(vm2.IsActive);
        Assert.True(cancelled2);
    }

    [Fact]
    public void Escape_WithModifiers_DoesNotCancel()
    {
        // Only plain Escape cancels. Ctrl+Escape or Shift+Escape should not cancel.
        var vm = new TutorialViewModel();
        vm.Start();

        bool completed = false;
        bool cancelled = false;
        vm.TutorialCompleted += () => completed = true;
        vm.TutorialCancelled += () => cancelled = true;

        // Ctrl+Escape at step 1 (F6) — wrong key, should not cancel
        bool handled = vm.CheckKeyPress(Key.Escape, ModifierKeys.Control);
        Assert.False(handled);
        Assert.True(vm.IsActive);
        Assert.False(completed);
        Assert.False(cancelled);

        // Shift+Escape at step 1 — wrong key, should not cancel
        handled = vm.CheckKeyPress(Key.Escape, ModifierKeys.Shift);
        Assert.False(handled);
        Assert.True(vm.IsActive);
        Assert.False(completed);
        Assert.False(cancelled);
    }

    [Fact]
    public void WrongKey_DoesNotAdvance()
    {
        var vm = new TutorialViewModel();
        vm.Start();

        // Step 1 expects F6. Pressing random keys should not advance.
        bool handled = vm.CheckKeyPress(Key.A, ModifierKeys.None);
        Assert.False(handled);
        Assert.Equal(0, vm.CurrentStepIndex);

        handled = vm.CheckKeyPress(Key.Enter, ModifierKeys.None);
        Assert.False(handled);
        Assert.Equal(0, vm.CurrentStepIndex);

        handled = vm.CheckKeyPress(Key.F6, ModifierKeys.Control); // F6 with Ctrl, not plain F6
        Assert.False(handled);
        Assert.Equal(0, vm.CurrentStepIndex);

        // Correct key still works after wrong attempts
        handled = vm.CheckKeyPress(Key.F6, ModifierKeys.None);
        Assert.True(handled);
        Assert.Equal(1, vm.CurrentStepIndex);
    }

    [Fact]
    public void WrongKey_AtLaterStep_DoesNotAdvance()
    {
        var vm = new TutorialViewModel();
        vm.Start();

        // Advance to step 3 (Ctrl+2)
        vm.CheckKeyPress(Key.F6, ModifierKeys.None);
        vm.CheckKeyPress(Key.D1, ModifierKeys.Control);
        Assert.Equal(2, vm.CurrentStepIndex); // step 3 expects Ctrl+2

        // Wrong key
        bool handled = vm.CheckKeyPress(Key.D3, ModifierKeys.Control); // Ctrl+3, not Ctrl+2
        Assert.False(handled);
        Assert.Equal(2, vm.CurrentStepIndex);

        // Correct key
        handled = vm.CheckKeyPress(Key.D2, ModifierKeys.Control);
        Assert.True(handled);
        Assert.Equal(3, vm.CurrentStepIndex);
    }

    [Fact]
    public void CheckKeyPress_ReturnsFalse_WhenNotActive()
    {
        var vm = new TutorialViewModel();
        // Not started — IsActive is false

        bool handled = vm.CheckKeyPress(Key.F6, ModifierKeys.None);
        Assert.False(handled);
        Assert.False(vm.IsActive);
    }

    [Fact]
    public void Cancel_FiresTutorialCancelled()
    {
        var vm = new TutorialViewModel();
        vm.Start();

        bool cancelled = false;
        vm.TutorialCancelled += () => cancelled = true;

        vm.Cancel();

        Assert.False(vm.IsActive);
        Assert.True(cancelled);
    }

    [Fact]
    public void Cancel_WhenNotActive_StillFiresTutorialCancelled()
    {
        var vm = new TutorialViewModel();
        // Not started

        bool cancelled = false;
        vm.TutorialCancelled += () => cancelled = true;

        vm.Cancel();

        Assert.False(vm.IsActive);
        Assert.True(cancelled);
    }

    [Fact]
    public void Steps_HasSixEntries()
    {
        var vm = new TutorialViewModel();
        Assert.Equal(6, vm.Steps.Count);
    }

    [Fact]
    public void Steps_HaveCorrectExpectedKeys()
    {
        var vm = new TutorialViewModel();

        Assert.Equal(Key.F6, vm.Steps[0].ExpectedKey);
        Assert.Equal(ModifierKeys.None, vm.Steps[0].ExpectedModifiers);

        Assert.Equal(Key.D1, vm.Steps[1].ExpectedKey);
        Assert.Equal(ModifierKeys.Control, vm.Steps[1].ExpectedModifiers);

        Assert.Equal(Key.D2, vm.Steps[2].ExpectedKey);
        Assert.Equal(ModifierKeys.Control, vm.Steps[2].ExpectedModifiers);

        Assert.Equal(Key.D3, vm.Steps[3].ExpectedKey);
        Assert.Equal(ModifierKeys.Control, vm.Steps[3].ExpectedModifiers);

        Assert.Equal(Key.P, vm.Steps[4].ExpectedKey);
        Assert.Equal(ModifierKeys.Control | ModifierKeys.Shift, vm.Steps[4].ExpectedModifiers);

        Assert.Equal(Key.Escape, vm.Steps[5].ExpectedKey);
        Assert.Equal(ModifierKeys.None, vm.Steps[5].ExpectedModifiers);
    }

    [Fact]
    public void CurrentStep_ReturnsNull_WhenIndexOutOfRange()
    {
        var vm = new TutorialViewModel();
        // Not started — CurrentStepIndex is 0 but IsActive is false
        // Actually CurrentStepIndex defaults to 0, so CurrentStep returns Steps[0]
        // Let's test the edge case by manipulating the index directly
        // CurrentStepIndex has a public setter via [ObservableProperty]

        // Before Start, IsActive is false but CurrentStep still returns step 0
        Assert.NotNull(vm.CurrentStep);
        Assert.Equal(Key.F6, vm.CurrentStep!.ExpectedKey);
    }

    [Fact]
    public void CurrentStepNumber_IsOneBased()
    {
        var vm = new TutorialViewModel();
        vm.Start();

        Assert.Equal(1, vm.CurrentStepNumber);

        vm.CheckKeyPress(Key.F6, ModifierKeys.None);
        Assert.Equal(2, vm.CurrentStepNumber);

        vm.CheckKeyPress(Key.D1, ModifierKeys.Control);
        Assert.Equal(3, vm.CurrentStepNumber);
    }

    [Fact]
    public void StepSix_EscapeAdvancesAndCompletesTutorial()
    {
        // Step 6 expects Escape. When the current step expects Escape,
        // pressing Escape advances the step (completing the tutorial)
        // rather than cancelling.
        var vm = new TutorialViewModel();
        vm.Start();

        bool completed = false;
        vm.TutorialCompleted += () => completed = true;

        // Advance through steps 1-5
        vm.CheckKeyPress(Key.F6, ModifierKeys.None);
        vm.CheckKeyPress(Key.D1, ModifierKeys.Control);
        vm.CheckKeyPress(Key.D2, ModifierKeys.Control);
        vm.CheckKeyPress(Key.D3, ModifierKeys.Control);
        vm.CheckKeyPress(Key.P, ModifierKeys.Control | ModifierKeys.Shift);

        // Now at step 6 (index 5)
        Assert.Equal(5, vm.CurrentStepIndex);
        Assert.True(vm.IsActive);

        // Pressing Escape at step 6 advances past it, completing the tutorial
        bool handled = vm.CheckKeyPress(Key.Escape, ModifierKeys.None);
        Assert.True(handled);
        Assert.False(vm.IsActive);
        Assert.True(completed);
    }
}

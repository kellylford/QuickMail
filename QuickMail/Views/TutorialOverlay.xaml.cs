using System.Windows;
using System.Windows.Controls;
using QuickMail.Models;
using QuickMail.Resources;
using QuickMail.ViewModels;

namespace QuickMail.Views;

public partial class TutorialOverlay : UserControl
{
    private TutorialViewModel? _viewModel;
    private System.ComponentModel.PropertyChangedEventHandler? _propertyChangedHandler;

    public TutorialOverlay()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public void SetViewModel(TutorialViewModel vm)
    {
        if (_viewModel != null && _propertyChangedHandler != null)
            _viewModel.PropertyChanged -= _propertyChangedHandler;

        _viewModel = vm;
        DataContext = vm;

        _propertyChangedHandler = (_, e) =>
        {
            if (e.PropertyName == nameof(TutorialViewModel.CurrentStep) && vm.IsActive)
            {
                var step = vm.CurrentStep;
                if (step != null)
                {
                    var text = step.InstructionText + Strings.Tutorial_Announce_PressEscapeToSkipSuffix;
                    AccessibilityHelper.Announce(this, text, interrupt: true, category: AnnouncementCategory.Hint);
                }
            }
        };
        vm.PropertyChanged += _propertyChangedHandler;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.IsActive == true && _viewModel.CurrentStep != null)
        {
            var text = _viewModel.CurrentStep.InstructionText + Strings.Tutorial_Announce_PressEscapeToSkipSuffix;
            AccessibilityHelper.Announce(this, text, interrupt: true, category: AnnouncementCategory.Hint);
        }
    }
}

using System.Collections.Generic;
using System.ComponentModel;
using QuickMail.Models;

namespace QuickMail.Tests;

/// <summary>
/// Records the (text, category) pairs a ViewModel's status announcements would be spoken as.
///
/// Asserting on the category AFTER a command has finished proves nothing, because the category is
/// one-shot: it returns to Status the instant the announcement has been raised, exactly so a later
/// message assigned outside the helpers cannot inherit it. The View reads it synchronously inside
/// the PropertyChanged handler, so a test has to listen the same way the View does — which also
/// means these tests fail if the ViewModel ever sets the category AFTER the text.
/// </summary>
sealed class StatusAnnouncementRecorder
{
    private readonly List<(string Text, AnnouncementCategory Category)> _announced = new();

    /// <summary>Everything the View would have announced, in order. Empty text is skipped, as the View skips it.</summary>
    public IReadOnlyList<(string Text, AnnouncementCategory Category)> Announced => _announced;

    /// <summary>The last announcement, or a sentinel that fails any assertion when nothing was announced.</summary>
    public (string Text, AnnouncementCategory Category) Last =>
        _announced.Count > 0 ? _announced[^1] : ("<nothing was announced>", AnnouncementCategory.Status);

    private StatusAnnouncementRecorder() { }

    /// <summary>Attaches to a ComposeViewModel, mirroring ComposeWindow's PropertyChanged handler.</summary>
    public static StatusAnnouncementRecorder Watch(ViewModels.ComposeViewModel vm)
    {
        var recorder = new StatusAnnouncementRecorder();
        vm.PropertyChanged += (_, e) => recorder.Capture(e, vm.StatusText, vm.StatusCategory);
        return recorder;
    }

    /// <summary>Attaches to an account editor VM, mirroring both account dialogs' handlers.</summary>
    public static StatusAnnouncementRecorder Watch(ViewModels.AccountEditorViewModel vm)
    {
        var recorder = new StatusAnnouncementRecorder();
        vm.PropertyChanged += (_, e) => recorder.Capture(e, vm.StatusText, vm.StatusCategory);
        return recorder;
    }

    private void Capture(PropertyChangedEventArgs e, string text, AnnouncementCategory category)
    {
        if (e.PropertyName != "StatusText" || string.IsNullOrEmpty(text)) return;
        _announced.Add((text, category));
    }
}

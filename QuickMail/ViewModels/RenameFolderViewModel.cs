using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;

namespace QuickMail.ViewModels;

/// <summary>
/// The Rename Folder window's ViewModel (#1083). Holds the name being typed and hands it to the
/// rename the owner supplied; a refusal keeps the window open with the reason said, so the user can
/// correct the name rather than start again. Pure VM — no View types. The View subscribes to
/// <see cref="Renamed"/> and <see cref="Cancelled"/> (close), <see cref="AnnouncementRequested"/>
/// (say it) and <see cref="SelectNameRequested"/> (put focus back in the box with the name selected).
/// </summary>
public partial class RenameFolderViewModel : ObservableObject
{
    private readonly Func<string, Task<FolderRenameResult>> _rename;

    public RenameFolderViewModel(string currentName, Func<string, Task<FolderRenameResult>> rename)
    {
        CurrentName = currentName;
        _newName = currentName;
        _rename = rename;
    }

    /// <summary>The folder's name now.</summary>
    public string CurrentName { get; }

    /// <summary>
    /// The text of the label on the name box: "New name for Projects:". Underscores are doubled
    /// because a Label reads a single one as an access key, which would swallow it from a folder
    /// called "to_do".
    /// </summary>
    public string Prompt => $"New name for {CurrentName.Replace("_", "__", StringComparison.Ordinal)}:";

    /// <summary>The name in the box. Bound two-way.</summary>
    [ObservableProperty]
    private string _newName;

    /// <summary>True while the server is renaming. The box and both buttons are unavailable then.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(RenameCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isWorking;

    public bool IsIdle => !IsWorking;

    /// <summary>Raised when the folder has been renamed.</summary>
    public event Action<FolderRenameResult>? Renamed;

    /// <summary>Raised when the user cancels.</summary>
    public event Action? Cancelled;

    /// <summary>Raised to have something said: a refusal, as a Result.</summary>
    public event Action<string, AnnouncementCategory>? AnnouncementRequested;

    /// <summary>Raised after a refusal, so the View can select the name for retyping.</summary>
    public event Action? SelectNameRequested;

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task Rename()
    {
        IsWorking = true;
        FolderRenameResult result;
        try
        {
            result = await _rename(NewName);
        }
        catch (Exception ex)
        {
            // The rename reports its own failures; this is for one it did not foresee, which must
            // still reach the user rather than end in silence with the window stuck.
            Services.LogService.Log("RenameFolderViewModel", ex);
            var reason = $"Could not rename {CurrentName}. {ex.Message}";
            result = new FolderRenameResult(false, reason, reason);
        }
        finally
        {
            IsWorking = false;
        }

        if (result.Succeeded)
        {
            Renamed?.Invoke(result);
            return;
        }

        AnnouncementRequested?.Invoke(result.Message, AnnouncementCategory.Result);
        SelectNameRequested?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void Cancel() => Cancelled?.Invoke();
}

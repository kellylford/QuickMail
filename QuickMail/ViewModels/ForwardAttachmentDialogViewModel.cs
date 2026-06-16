using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;

namespace QuickMail.ViewModels;

public class ForwardAttachmentSelectionItem : ObservableObject
{
    public AttachmentModel Attachment { get; init; } = null!;

    private bool _isIncluded = true;
    public bool IsIncluded
    {
        get => _isIncluded;
        set => SetProperty(ref _isIncluded, value);
    }

    public string DisplayLabel => $"{Attachment.FileName}   {Attachment.FileSizeDisplay}";
    public string AutomationLabel => $"{Attachment.FileName}, {Attachment.FileSizeDisplay}";
}

public class ForwardAttachmentDialogViewModel : ObservableObject
{
    public ObservableCollection<ForwardAttachmentSelectionItem> Items { get; }

    public IRelayCommand IncludeSelectedCommand { get; }
    public IRelayCommand IncludeNoneCommand { get; }
    public IRelayCommand CancelCommand { get; }

    /// <summary>
    /// The selected attachment list after the dialog closes.
    /// Null means the user cancelled; an empty list means "include none".
    /// </summary>
    public IReadOnlyList<AttachmentModel>? Result { get; private set; }

    public event Action? CloseRequested;

    public ForwardAttachmentDialogViewModel(IReadOnlyList<AttachmentModel> attachments)
    {
        Items = new ObservableCollection<ForwardAttachmentSelectionItem>(
            attachments.Select(a => new ForwardAttachmentSelectionItem { Attachment = a }));

        IncludeSelectedCommand = new RelayCommand(IncludeSelected);
        IncludeNoneCommand     = new RelayCommand(IncludeNone);
        CancelCommand          = new RelayCommand(Cancel);
    }

    private void IncludeSelected()
    {
        Result = Items.Where(i => i.IsIncluded).Select(i => i.Attachment).ToList();
        CloseRequested?.Invoke();
    }

    private void IncludeNone()
    {
        Result = Array.Empty<AttachmentModel>();
        CloseRequested?.Invoke();
    }

    private void Cancel()
    {
        Result = null;
        CloseRequested?.Invoke();
    }
}

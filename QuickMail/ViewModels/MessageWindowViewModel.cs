using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;

namespace QuickMail.ViewModels;

/// <summary>
/// View model for a standalone MessageWindow.
/// Owns the message being displayed and a mini message list for prev/next navigation.
/// </summary>
public sealed partial class MessageWindowViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private MailMessageDetail? _messageDetail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private MailMessageSummary? _selectedMessage;

    [ObservableProperty]
    private bool _isLoading;

    public ObservableCollection<MailMessageSummary> MessageList { get; } = [];
    public MailMessageSummary? OriginalSummary { get; init; }

    public string WindowTitle
    {
        get
        {
            var subject = MessageDetail?.Subject ?? SelectedMessage?.Subject;
            if (string.IsNullOrWhiteSpace(subject))
                return "QuickMail";
            var trimmed = subject.Trim();
            return trimmed.Length > 80 ? trimmed[..80] + "…" : trimmed;
        }
    }

    public event Action<MessageWindowViewModel>? RequestClose;
    public event Action<MessageWindowViewModel>? RequestMoveToMainWindow;

    [RelayCommand]
    private void Close() => RequestClose?.Invoke(this);

    [RelayCommand]
    private void MoveToMainWindow() => RequestMoveToMainWindow?.Invoke(this);

    public bool CanNavigatePrevious =>
        SelectedMessage != null && MessageList.Count > 0
        && MessageList.IndexOf(SelectedMessage) > 0;

    public bool CanNavigateNext =>
        SelectedMessage != null && MessageList.Count > 0
        && MessageList.IndexOf(SelectedMessage) < MessageList.Count - 1;

    [RelayCommand(CanExecute = nameof(CanNavigatePrevious))]
    private void PreviousMessage()
    {
        if (SelectedMessage == null) return;
        var idx = MessageList.IndexOf(SelectedMessage);
        if (idx > 0)
            SelectedMessage = MessageList[idx - 1];
    }

    [RelayCommand(CanExecute = nameof(CanNavigateNext))]
    private void NextMessage()
    {
        if (SelectedMessage == null) return;
        var idx = MessageList.IndexOf(SelectedMessage);
        if (idx < MessageList.Count - 1)
            SelectedMessage = MessageList[idx + 1];
    }

    partial void OnSelectedMessageChanged(MailMessageSummary? value)
    {
        PreviousMessageCommand.NotifyCanExecuteChanged();
        NextMessageCommand.NotifyCanExecuteChanged();
    }
}

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
    [NotifyPropertyChangedFor(nameof(PositionText))]
    private MailMessageSummary? _selectedMessage;

    [ObservableProperty]
    private bool _isLoading;

    public ObservableCollection<MailMessageSummary> MessageList { get; } = [];
    public MailMessageSummary? OriginalSummary { get; init; }

    // Action callbacks wired by the window opener (MainWindow) so mail operations
    // execute against the correct MainViewModel state.
    public Action? ReplyAction       { get; set; }
    public Action? ReplyAllAction    { get; set; }
    public Action? ForwardAction     { get; set; }
    public Action? DeleteAction      { get; set; }
    public Action? MarkReadAction    { get; set; }
    public Action? GrabAddressesAction { get; set; }

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

    public string PositionText
    {
        get
        {
            if (SelectedMessage == null || MessageList.Count == 0) return string.Empty;
            var idx = MessageList.IndexOf(SelectedMessage);
            if (idx < 0) return string.Empty;
            return $"Message {idx + 1} of {MessageList.Count}";
        }
    }

    public event Action<MessageWindowViewModel>? RequestClose;
    public event Action<MessageWindowViewModel>? RequestMoveToMainWindow;

    [RelayCommand]
    private void Close() => RequestClose?.Invoke(this);

    [RelayCommand]
    private void MoveToMainWindow() => RequestMoveToMainWindow?.Invoke(this);

    [RelayCommand]
    private void Reply() => ReplyAction?.Invoke();

    [RelayCommand]
    private void ReplyAll() => ReplyAllAction?.Invoke();

    [RelayCommand]
    private void Forward() => ForwardAction?.Invoke();

    [RelayCommand]
    private void DeleteMessage() => DeleteAction?.Invoke();

    [RelayCommand]
    private void MarkRead() => MarkReadAction?.Invoke();

    [RelayCommand]
    private void GrabAddresses() => GrabAddressesAction?.Invoke();

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

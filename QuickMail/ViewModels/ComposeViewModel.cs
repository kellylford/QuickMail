using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

public partial class ComposeViewModel : ObservableObject
{
    private readonly ISmtpService _smtp;
    private readonly IAccountService _accountService;
    private readonly ICredentialService _credentials;

    [ObservableProperty] private string _to = string.Empty;
    [ObservableProperty] private string _cc = string.Empty;
    [ObservableProperty] private string _bcc = string.Empty;
    [ObservableProperty] private string _subject = string.Empty;
    [ObservableProperty] private string _body = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isBusy = false;
    [ObservableProperty] private ObservableCollection<AccountModel> _senderAccounts = [];
    [ObservableProperty] private AccountModel? _senderAccount;

    private string? _inReplyToMessageId;

    public event Action? CloseRequested;

    public ComposeViewModel(ISmtpService smtp, IAccountService accountService, ICredentialService credentials)
    {
        _smtp = smtp;
        _accountService = accountService;
        _credentials = credentials;
    }

    public void Seed(ComposeModel model)
    {
        _inReplyToMessageId = model.InReplyToMessageId;
        To = model.To;
        Cc = model.Cc;
        Bcc = model.Bcc;
        Subject = model.Subject;
        Body = model.Body;

        var accounts = _accountService.LoadAccounts();
        SenderAccounts = new ObservableCollection<AccountModel>(accounts);
        SenderAccount = SenderAccounts.FirstOrDefault(a => a.Id == model.AccountId)
                        ?? SenderAccounts.FirstOrDefault();
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(To))
        {
            StatusText = "Please enter at least one recipient.";
            return;
        }

        var account = SenderAccount;
        if (account == null)
        {
            StatusText = "Please select a sender account.";
            return;
        }

        var password = _credentials.GetPassword(account.Id);
        if (string.IsNullOrEmpty(password) && account.AuthType == Models.AuthType.Password)
        {
            StatusText = "No password stored for this account.";
            return;
        }

        IsBusy = true;
        StatusText = "Sending…";
        try
        {
            var compose = new ComposeModel
            {
                AccountId = account.Id,
                To = To,
                Cc = Cc,
                Bcc = Bcc,
                Subject = Subject,
                Body = Body,
                InReplyToMessageId = _inReplyToMessageId
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _smtp.SendAsync(compose, account, password, cts.Token);
            StatusText = "Message sent.";
            CloseRequested?.Invoke();
        }
        catch (Exception ex)
        {
            StatusText = $"Send failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();

    // ── Factory helpers ────────────────────────────────────────────────────────

    public static ComposeModel CreateReply(MailMessageDetail detail, Guid accountId)
    {
        var subject = detail.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)
            ? detail.Subject
            : $"Re: {detail.Subject}";

        var attribution = $"\n\nOn {detail.Date.ToLocalTime():f}, {detail.From} wrote:\n";
        var quoted = string.Join("\n", System.Array.ConvertAll(
            detail.PlainTextBody.Split('\n'),
            line => "> " + line));

        return new ComposeModel
        {
            AccountId = accountId,
            To = string.IsNullOrEmpty(detail.ReplyTo) ? detail.From : detail.ReplyTo,
            Subject = subject,
            Body = attribution + quoted,
            InReplyToMessageId = detail.MessageId
        };
    }

    public static ComposeModel CreateReplyAll(MailMessageDetail detail, Guid accountId)
    {
        var model = CreateReply(detail, accountId);
        model.Cc = detail.Cc;
        return model;
    }

    public static ComposeModel CreateForward(MailMessageDetail detail, Guid accountId)
    {
        var subject = detail.Subject.StartsWith("Fwd:", StringComparison.OrdinalIgnoreCase)
            ? detail.Subject
            : $"Fwd: {detail.Subject}";

        var header = $"\n\n---------- Forwarded message ----------\n"
                   + $"From: {detail.From}\n"
                   + $"Date: {detail.Date.ToLocalTime():f}\n"
                   + $"Subject: {detail.Subject}\n"
                   + $"To: {detail.To}\n\n";

        return new ComposeModel
        {
            AccountId = accountId,
            Subject = subject,
            Body = header + detail.PlainTextBody
        };
    }
}

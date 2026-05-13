using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using MimeKit;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

public partial class ComposeViewModel : ObservableObject
{
    private readonly ISmtpService _smtp;
    private readonly IAccountService _accountService;
    private readonly ICredentialService _credentials;
    private readonly IImapService _imap;

    [ObservableProperty] private string _to = string.Empty;
    [ObservableProperty] private string _cc = string.Empty;
    [ObservableProperty] private string _bcc = string.Empty;
    [ObservableProperty] private string _subject = string.Empty;
    [ObservableProperty] private string _body = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isBusy = false;
    [ObservableProperty] private ObservableCollection<AccountModel> _senderAccounts = [];
    [ObservableProperty] private AccountModel? _senderAccount;
    [ObservableProperty] private ObservableCollection<AttachmentModel> _attachments = [];

    private string? _inReplyToMessageId;
    private uint? _draftUid;
    private string? _draftFolderName;
    private bool _isDirty;
    private bool _isSent;

    public bool IsDirty => _isDirty;
    public bool IsSent  => _isSent;

    public event Action? CloseRequested;

    public ComposeViewModel(ISmtpService smtp, IAccountService accountService, ICredentialService credentials, IImapService imap)
    {
        _smtp = smtp;
        _accountService = accountService;
        _credentials = credentials;
        _imap = imap;
        _attachments.CollectionChanged += (_, _) =>
        {
            _isDirty = true;
            OnPropertyChanged(nameof(AttachmentSummaryText));
        };
    }

    // Dirty-marking partial methods — fired by the [ObservableProperty] source generator
    partial void OnToChanged(string value)      => _isDirty = true;
    partial void OnCcChanged(string value)      => _isDirty = true;
    partial void OnBccChanged(string value)     => _isDirty = true;
    partial void OnSubjectChanged(string value) => _isDirty = true;
    partial void OnBodyChanged(string value)    => _isDirty = true;

    public void Seed(ComposeModel model)
    {
        _inReplyToMessageId = model.InReplyToMessageId;
        _draftUid           = model.DraftUid;
        _draftFolderName    = model.DraftFolderName;

        To      = model.To;
        Cc      = model.Cc;
        Bcc     = model.Bcc;
        Subject = model.Subject;
        Body    = model.Body;

        Attachments.Clear();
        foreach (var att in model.Attachments)
            Attachments.Add(att);

        // Loading existing data (reply, forward, or re-opened draft) is not itself a dirty edit
        _isDirty = false;

        var accounts = _accountService.LoadAccounts();
        SenderAccounts = new ObservableCollection<AccountModel>(accounts);
        SenderAccount = SenderAccounts.FirstOrDefault(a => a.Id == model.AccountId)
                        ?? SenderAccounts.FirstOrDefault(a => a.IsDefault)
                        ?? SenderAccounts.FirstOrDefault();
    }

    [RelayCommand]
    private async Task SaveDraftAsync()
    {
        var account = SenderAccount;
        if (account == null)
        {
            StatusText = "Please select a sender account.";
            return;
        }

        if (Attachments.Sum(a => a.FileSize) > 25_000_000)
        {
            StatusText = "Total attachment size exceeds 25 MB. Please remove some attachments.";
            return;
        }

        IsBusy = true;
        StatusText = "Saving draft…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            _draftFolderName ??= await _imap.FindDraftsFolderNameAsync(account.Id, cts.Token);
            if (_draftFolderName == null)
            {
                StatusText = "No Drafts folder found on this account.";
                return;
            }

            var compose = BuildComposeModel(account.Id);
            _draftUid = await _imap.AppendDraftAsync(account.Id, compose, _draftUid, cts.Token);
            _isDirty = false;
            StatusText = "Draft saved.";
        }
        catch (Exception ex)
        {
            StatusText = $"Save draft failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
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

        if (Attachments.Sum(a => a.FileSize) > 25_000_000)
        {
            StatusText = "Total attachment size exceeds 25 MB. Please remove some attachments.";
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
            var compose = BuildComposeModel(account.Id);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _smtp.SendAsync(compose, account, password, cts.Token);
            StatusText = "Message sent.";
            _isSent = true;

            // Delete the draft from the server (if one was saved)
            if (_draftUid.HasValue && _draftFolderName != null)
            {
                try
                {
                    using var delCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await _imap.MoveToTrashAsync(account.Id, _draftFolderName, _draftUid.Value, delCts.Token);
                }
                catch (Exception ex)
                {
                    LogService.Log("SendAsync: failed to delete draft after send", ex);
                }
            }

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

    [RelayCommand]
    private void AddAttachments()
    {
        var dlg = new OpenFileDialog { Multiselect = true, Title = "Add Attachments" };
        if (dlg.ShowDialog() != true) return;
        foreach (var path in dlg.FileNames)
            AddAttachmentFromPath(path);
    }

    /// <summary>Adds a file as an attachment (shared by AddAttachmentsCommand and clipboard paste).</summary>
    public void AddAttachmentFromPath(string path)
    {
        if (!File.Exists(path)) return;
        var info  = new FileInfo(path);
        var bytes = File.ReadAllBytes(path);
        Attachments.Add(new AttachmentModel
        {
            FileName    = info.Name,
            ContentType = AttachmentModel.ContentTypeFromFileName(info.Name),
            FileSize    = info.Length,
            Content     = bytes,
        });
    }

    [RelayCommand]
    private void RemoveAttachment(AttachmentModel? attachment)
    {
        if (attachment != null)
            Attachments.Remove(attachment);
    }

    /// <summary>e.g. "3 files, 1.8 MB of 25 MB limit"</summary>
    public string AttachmentSummaryText
    {
        get
        {
            var count = Attachments.Count;
            if (count == 0) return string.Empty;
            var totalBytes = Attachments.Sum(a => a.FileSize);
            var totalDisplay = totalBytes >= 1_048_576
                ? $"{totalBytes / 1_048_576.0:F1} MB"
                : $"{totalBytes / 1_024.0:F0} KB";
            return $"{count} file{(count == 1 ? "" : "s")}, {totalDisplay} of 25 MB limit";
        }
    }

    private static readonly string[] DangerousExtensions =
        [".exe", ".bat", ".cmd", ".ps1", ".msi", ".scr", ".vbs", ".js", ".jar"];

    [RelayCommand]
    private void OpenComposeAttachment(AttachmentModel? attachment)
    {
        if (attachment?.Content == null) return;

        var ext = Path.GetExtension(attachment.FileName).ToLowerInvariant();
        if (DangerousExtensions.Contains(ext))
        {
            var result = System.Windows.MessageBox.Show(
                $"'{attachment.FileName}' is an executable file type. Opening it could be dangerous. Continue?",
                "Security Warning",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (result != System.Windows.MessageBoxResult.Yes) return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "QuickMail");
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, attachment.FileName);
        File.WriteAllBytes(tempPath, attachment.Content);
        Process.Start(new ProcessStartInfo(tempPath) { UseShellExecute = true });
    }

    private ComposeModel BuildComposeModel(Guid accountId) => new()
    {
        AccountId           = accountId,
        To                  = To,
        Cc                  = Cc,
        Bcc                 = Bcc,
        Subject             = Subject,
        Body                = Body,
        InReplyToMessageId  = _inReplyToMessageId,
        DraftUid            = _draftUid,
        DraftFolderName     = _draftFolderName,
        Attachments         = Attachments.ToList(),
    };

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

    /// <param name="ownAddress">The sender's own email address; excluded from the Cc list to avoid self-addressing.</param>
    public static ComposeModel CreateReplyAll(MailMessageDetail detail, Guid accountId, string ownAddress = "")
    {
        var model = CreateReply(detail, accountId);

        // Merge original To + Cc, excluding the sender's own address, into the new Cc.
        var recipients = InternetAddressList.Parse(detail.To ?? string.Empty)
            .Concat(InternetAddressList.Parse(detail.Cc ?? string.Empty))
            .OfType<MailboxAddress>()
            .Where(a => !string.Equals(a.Address, ownAddress, StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();

        model.Cc = string.Join(", ", recipients.Select(a => a.ToString()));
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

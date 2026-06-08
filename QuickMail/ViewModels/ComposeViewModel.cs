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
    private readonly IMailService _imap;
    private readonly ITemplateService _templateService;

    [ObservableProperty] private string _to = string.Empty;
    [ObservableProperty] private string _cc = string.Empty;
    [ObservableProperty] private string _bcc = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private string _subject = string.Empty;

    /// <summary>What kind of composition this is; drives the window title prefix.</summary>
    public ComposeKind ComposeKind { get; private set; } = ComposeKind.NewMessage;

    /// <summary>Dynamic window title: "{prefix} — {subject or Untitled}".</summary>
    public string WindowTitle => ComposeKind switch
    {
        ComposeKind.Reply        => "Reply",
        ComposeKind.ReplyAll     => "Reply All",
        ComposeKind.Forward      => "Forward",
        ComposeKind.EditDraft    => "Draft",
        ComposeKind.NewDraft     => "Draft",
        ComposeKind.EditTemplate => "Edit Template",
        _                        => "Compose",
    } + " — " + (string.IsNullOrWhiteSpace(Subject) ? "Untitled" : Subject.Trim());
    [ObservableProperty] private string _body = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isBusy = false;
    [ObservableProperty] private ObservableCollection<AccountModel> _senderAccounts = [];
    [ObservableProperty] private AccountModel? _senderAccount;
    [ObservableProperty] private ObservableCollection<AttachmentModel> _attachments = [];

    private string? _inReplyToMessageId;
    private string? _draftMessageId;
    private string? _draftFolderName;
    private bool _isDirty;
    private bool _isSent;

    public bool IsDirty => _isDirty;
    public bool IsSent  => _isSent;

    public event Action? CloseRequested;

    /// <summary>
    /// Set by the View to show a Yes/No confirmation dialog.
    /// Parameters: message, title. Returns true when the user confirms.
    /// Mirrors the pattern on MainViewModel — see CLAUDE.md MVVM Rules.
    /// </summary>
    public Func<string, string, bool>? ConfirmationRequested { get; set; }

    public ComposeViewModel(ISmtpService smtp, IAccountService accountService, ICredentialService credentials, IMailService imap, ITemplateService templateService)
    {
        _smtp = smtp;
        _accountService = accountService;
        _credentials = credentials;
        _imap = imap;
        _templateService = templateService;
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
        _draftMessageId     = model.DraftMessageId;
        _draftFolderName    = model.DraftFolderName;
        ComposeKind         = model.Kind;
        OnPropertyChanged(nameof(WindowTitle));

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

        // Auto-append signature if this is a new compose (not a draft re-open) and the
        // account has a signature configured. Drafts already have the signature in the body.
        if (model.DraftMessageId == null && SenderAccount != null && !string.IsNullOrWhiteSpace(SenderAccount.Signature))
        {
            var sig = SenderAccount.Signature;
            // Add separator if body already has content (reply/forward)
            if (!string.IsNullOrWhiteSpace(Body) && !Body.EndsWith("\n"))
                Body += "\n";
            if (!string.IsNullOrWhiteSpace(Body))
                Body += "\n-- \n";
            Body += sig;
            _isDirty = false; // signature insertion is not a user edit
        }
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
            _draftMessageId = await _imap.AppendDraftAsync(account.Id, compose, _draftMessageId, cts.Token);
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

            // Append to Sent folder (best-effort — fire and forget so it doesn't block the UI).
            // Providers that auto-save sent mail (e.g. Gmail) may produce a duplicate; that is
            // harmless and the background sync will eventually deduplicate via UID tracking.
            _ = Task.Run(async () =>
            {
                try
                {
                    using var sentCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await _imap.AppendToSentAsync(account.Id, compose, sentCts.Token);
                }
                catch (Exception ex)
                {
                    LogService.Log("SendAsync: failed to append to Sent folder", ex);
                }
            });

            // Delete the draft from the server (if one was saved)
            if (!string.IsNullOrEmpty(_draftMessageId) && _draftFolderName != null)
            {
                try
                {
                    using var delCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await _imap.MoveToTrashAsync(account.Id, _draftFolderName, _draftMessageId, delCts.Token);
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

    /// <summary>
    /// Opens the template picker. The View subscribes to this event to show the dialog.
    /// </summary>
    public event Func<Task<MessageTemplate?>>? InsertTemplateRequested;

    [RelayCommand]
    private async Task InsertTemplateAsync()
    {
        if (InsertTemplateRequested == null) return;
        var template = await InsertTemplateRequested();
        if (template == null) return;

        var displayName = !string.IsNullOrWhiteSpace(SenderAccount?.DisplayName)
            ? SenderAccount!.DisplayName
            : !string.IsNullOrWhiteSpace(SenderAccount?.Username)
                ? SenderAccount!.Username
                : string.Empty;
        var now = DateTime.Now;

        var body = template.Body
            .Replace("{sender}", displayName, StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", now.ToString("d"), StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", now.ToString("t"), StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(template.Subject) && string.IsNullOrWhiteSpace(Subject))
            Subject = template.Subject
                .Replace("{sender}", displayName, StringComparison.OrdinalIgnoreCase)
                .Replace("{date}", now.ToString("d"), StringComparison.OrdinalIgnoreCase)
                .Replace("{time}", now.ToString("t"), StringComparison.OrdinalIgnoreCase);

        Body += body;
        StatusText = $"Template '{template.Title}' inserted.";
    }

    [RelayCommand]
    private async Task SaveAsTemplateAsync()
    {
        if (string.IsNullOrWhiteSpace(Body))
        {
            StatusText = "Nothing to save — body is empty.";
            return;
        }

        var template = new MessageTemplate
        {
            Title = Subject.Trim().Length > 0 ? Subject.Trim() : "Untitled",
            Subject = Subject,
            Body = Body
        };

        await _templateService.AddAsync(template);
        StatusText = $"Template saved as '{template.Title}'.";
    }

    [RelayCommand]
    private async Task AddAttachmentsAsync()
    {
        var dlg = new OpenFileDialog { Multiselect = true, Title = "Add Attachments" };
        if (dlg.ShowDialog() != true) return;
        foreach (var path in dlg.FileNames)
            await AddAttachmentFromPathAsync(path);
    }

    /// <summary>
    /// Adds a file as an attachment (shared by AddAttachmentsCommand and clipboard paste).
    /// Reads asynchronously so large attachments don't freeze the window on slow disks.
    /// </summary>
    public async Task AddAttachmentFromPathAsync(string path)
    {
        if (!File.Exists(path)) return;
        var info  = new FileInfo(path);
        var bytes = await File.ReadAllBytesAsync(path);
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
            // CLAUDE.md MVVM Rules: ViewModels must not call MessageBox directly.
            // If the View hasn't wired a confirmation handler, treat that as deny so
            // we never silently open something potentially dangerous.
            var confirmed = ConfirmationRequested?.Invoke(
                $"'{attachment.FileName}' is an executable file type. Opening it could be dangerous. Continue?",
                "Security Warning") ?? false;
            if (!confirmed) return;
        }

        // Per-attachment subfolder so two files with the same name (invoice.pdf, invoice.pdf
        // from different messages or sessions) don't overwrite each other in %TEMP%\QuickMail.
        var tempDir = Path.Combine(Path.GetTempPath(), "QuickMail", Guid.NewGuid().ToString("N"));
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
        DraftMessageId      = _draftMessageId,
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
            Kind      = ComposeKind.Reply,
            AccountId = accountId,
            To = string.IsNullOrEmpty(detail.ReplyTo) ? detail.From : detail.ReplyTo,
            Subject = subject,
            Body = attribution + quoted,
            InReplyToMessageId = detail.InternetMessageId
        };
    }

    /// <param name="ownAddress">The sender's own email address; excluded from the Cc list to avoid self-addressing.</param>
    public static ComposeModel CreateReplyAll(MailMessageDetail detail, Guid accountId, string ownAddress = "")
    {
        var model = CreateReply(detail, accountId);

        // Also exclude whichever address landed in model.To (the original From or ReplyTo).
        // Otherwise mailing-list senders who were Cc'd on their own message appear on both
        // the To and Cc lines of the reply-all.
        var toAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (InternetAddressList.TryParse(model.To ?? string.Empty, out var modelToList))
        {
            foreach (var a in modelToList.OfType<MailboxAddress>())
                toAddresses.Add(a.Address);
        }
        if (!string.IsNullOrEmpty(ownAddress))
            toAddresses.Add(ownAddress);

        // Merge original To + Cc, excluding the sender's own address and the To recipient,
        // into the new Cc. Use TryParse so empty/malformed address strings return an empty
        // list rather than throwing MimeKit.ParseException.
        InternetAddressList.TryParse(detail.To ?? string.Empty, out var toList);
        InternetAddressList.TryParse(detail.Cc ?? string.Empty, out var ccList);
        var recipients = (toList ?? [])
            .Concat(ccList ?? [])
            .OfType<MailboxAddress>()
            .Where(a => !toAddresses.Contains(a.Address))
            .GroupBy(a => a.Address, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        model.Cc   = string.Join(", ", recipients.Select(a => a.ToString()));
        model.Kind = ComposeKind.ReplyAll;
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
            Kind      = ComposeKind.Forward,
            AccountId = accountId,
            Subject   = subject,
            Body      = header + detail.PlainTextBody
        };
    }
}

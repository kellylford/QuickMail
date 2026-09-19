using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

/// <summary>Where the user chose to save: a file for one message, a folder for several.</summary>
public sealed record MessageSaveTarget(string Path, MessageSaveFormat Format);

/// <summary>
/// The window-side half of saving and printing (#728): the dialogs, and the two things that need a
/// browser engine. Implemented by each window that offers Save, so every dialog is owned by the
/// window the user is in. No WPF type crosses this interface.
/// </summary>
public interface IMessageSaveUi
{
    /// <summary>The Save As dialog for one message. Null when cancelled.</summary>
    MessageSaveTarget? ChooseFile(string suggestedFileName, MessageSaveFormat format, string initialFolder);

    /// <summary>
    /// The Save As dialog for several messages: the folder and format apply to all of them, and each
    /// is named from its own subject. Returns the chosen folder as <see cref="MessageSaveTarget.Path"/>.
    /// </summary>
    MessageSaveTarget? ChooseFolder(int count, MessageSaveFormat format, string initialFolder);

    /// <summary>Explains why the original could not be saved and asks whether to pick another format.</summary>
    bool ConfirmTryAnotherFormat(string explanation);

    /// <summary>Writes <paramref name="html"/> to <paramref name="path"/> as a PDF.</summary>
    Task WritePdfAsync(string html, string path, CancellationToken ct);

    /// <summary>Shows the Print dialog for <paramref name="html"/> and prints it. False when cancelled.</summary>
    Task<bool> PrintAsync(string html, string documentTitle, CancellationToken ct);
}

/// <summary>What happened, for the status bar. <see cref="Text"/> is null when nothing needs saying (cancelled).</summary>
public sealed record MessageSaveOutcome(string? Text);

/// <summary>
/// Saves and prints messages (#728). Kept out of MainViewModel so the whole flow — format choice, the
/// refusal when the original is out of reach, naming, never overwriting — is testable with a fake
/// <see cref="IMessageSaveUi"/> and stub services.
/// </summary>
public sealed class MessageSaver
{
    private readonly IMailService _mail;
    private readonly ILocalStoreService? _store;
    private readonly IConfigService _config;
    private readonly Func<MailMessageSummary, MessageSaveContext> _context;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string> _defaultFolder;

    public MessageSaver(
        IMailService mail,
        ILocalStoreService? store,
        IConfigService config,
        Func<MailMessageSummary, MessageSaveContext> context,
        Func<string, bool>? fileExists = null,
        Func<string>? defaultFolder = null)
    {
        _mail          = mail;
        _store         = store;
        _config        = config;
        _context       = context;
        _fileExists    = fileExists ?? File.Exists;
        _defaultFolder = defaultFolder ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
    }

    /// <summary>The folder Save writes into: the one set in Settings, else Documents.</summary>
    public string SaveFolder()
    {
        var configured = _config.Load().SaveMessageFolder;
        return string.IsNullOrWhiteSpace(configured) ? _defaultFolder() : configured.Trim();
    }

    /// <summary>
    /// Save (<paramref name="chooseLocation"/> false: the default format into the save folder, no
    /// dialog) or Save As (true: the dialog). <paramref name="formatOverride"/> preselects a type in
    /// the dialog — used when retrying in another format after the original was out of reach.
    /// </summary>
    public async Task<MessageSaveOutcome> SaveAsync(
        IReadOnlyList<MailMessageSummary> messages, bool chooseLocation, IMessageSaveUi ui,
        MessageSaveFormat? formatOverride = null, CancellationToken ct = default)
    {
        if (messages.Count == 0) return new MessageSaveOutcome(null);

        var config = _config.Load();
        var format = formatOverride ?? MessageSaveFormats.FromConfigValue(config.SaveMessageFormat);

        string? singlePath = null;   // Save As on one message: exactly the path the dialog returned
        string folder;
        if (chooseLocation)
        {
            var start = !string.IsNullOrWhiteSpace(config.LastSaveAsFolder) && Directory.Exists(config.LastSaveAsFolder)
                ? config.LastSaveAsFolder
                : SaveFolder();

            var target = messages.Count == 1
                ? ui.ChooseFile(MessageExport.BuildFileName(messages[0], format), format, start)
                : ui.ChooseFolder(messages.Count, format, start);
            if (target is null) return new MessageSaveOutcome(null);

            format = target.Format;
            if (messages.Count == 1)
            {
                singlePath = target.Path;
                folder     = Path.GetDirectoryName(target.Path) ?? start;
            }
            else
            {
                folder = target.Path;
            }

            RememberSaveAsFolder(folder);
        }
        else
        {
            folder = SaveFolder();
            try { Directory.CreateDirectory(folder); }
            catch (Exception ex)
            {
                LogService.Log("MessageSaver: save folder unavailable", ex);
                return new MessageSaveOutcome(
                    $"The save folder {folder} is not available. Choose another in Settings, or use Save As.");
            }
        }

        var saved          = new List<string>();
        var originalFailed = new List<(MailMessageSummary Message, string Reason)>();
        var otherFailed    = new List<string>();

        foreach (var message in messages)
        {
            ct.ThrowIfCancellationRequested();
            var path = singlePath ?? MessageExport.UniquePath(folder, MessageExport.BuildFileName(message, format), _fileExists);
            try
            {
                await WriteOneAsync(message, format, path, ui, ct);
                saved.Add(path);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (format == MessageSaveFormat.Eml && OriginalOutOfReach(ex, ct) is { } reason)
            {
                originalFailed.Add((message, reason));
            }
            catch (Exception ex)
            {
                LogService.Log($"MessageSaver: could not save message {message.MessageId} as {format}", ex);
                otherFailed.Add(ConnectionFailure.IsConnectionFailure(ex, ct)
                    ? "the message is not available offline"
                    : ex.Message);
            }
        }

        var outcome = Describe(messages.Count, saved, folder, originalFailed.Count + otherFailed.Count,
                               otherFailed.FirstOrDefault() ?? originalFailed.FirstOrDefault().Reason);

        // The original could not be had. Say why, and let the user choose another format — never
        // substitute one silently (#728 decision): a file that looks like the original must be it.
        if (originalFailed.Count > 0)
        {
            var retry = originalFailed.Select(f => f.Message).ToList();
            if (ui.ConfirmTryAnotherFormat(ExplainOriginalFailure(messages.Count, retry.Count, originalFailed[0].Reason, saved.Count)))
            {
                var second = await SaveAsync(retry, chooseLocation: true, ui, MessageSaveFormat.Text, ct);
                // Whatever the retry reports supersedes the first pass's failure line for those messages.
                return saved.Count == 0 ? second : new MessageSaveOutcome(Join(outcome.Text, second.Text));
            }
        }
        return outcome;
    }

    /// <summary>Prints one message: the same page Save As Web Page writes.</summary>
    public async Task<MessageSaveOutcome> PrintAsync(MailMessageSummary message, IMessageSaveUi ui, CancellationToken ct = default)
    {
        MailMessageDetail detail;
        try { detail = await LoadDetailAsync(message, ct); }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            LogService.Log("MessageSaver: could not load message to print", ex);
            return new MessageSaveOutcome(ConnectionFailure.IsConnectionFailure(ex, ct)
                ? "This message is not available offline, so it cannot be printed."
                : $"Could not print: {ex.Message}");
        }

        var html = await Task.Run(() => MessageExport.BuildHtmlDocument(detail, _context(detail)), ct);
        var title = string.IsNullOrWhiteSpace(detail.Subject) ? "Message" : detail.Subject.Trim();
        try
        {
            return await ui.PrintAsync(html, title, ct)
                ? new MessageSaveOutcome($"Sent {title} to the printer.")
                : new MessageSaveOutcome(null);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            LogService.Log("MessageSaver: print failed", ex);
            return new MessageSaveOutcome($"Could not print: {ex.Message}");
        }
    }

    private async Task WriteOneAsync(MailMessageSummary message, MessageSaveFormat format, string path, IMessageSaveUi ui, CancellationToken ct)
    {
        if (format == MessageSaveFormat.Eml)
        {
            var bytes = await _mail.GetOriginalMessageAsync(message.AccountId, message.FolderName, message.MessageId, ct);
            if (bytes.Length == 0)
                throw new MessageOriginalUnavailableException("The server returned an empty message.");
            await File.WriteAllBytesAsync(path, bytes, ct);
            return;
        }

        var detail  = await LoadDetailAsync(message, ct);
        var context = _context(detail);
        switch (format)
        {
            case MessageSaveFormat.Text:
                var text = await Task.Run(() => MessageExport.BuildTextDocument(detail, context), ct);
                // UTF-8 with a byte-order mark, so Notepad and every other Windows editor reads a
                // non-English message correctly instead of guessing a code page.
                await File.WriteAllTextAsync(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), ct);
                break;
            case MessageSaveFormat.Html:
                var html = await Task.Run(() => MessageExport.BuildHtmlDocument(detail, context), ct);
                await File.WriteAllTextAsync(path, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
                break;
            case MessageSaveFormat.Pdf:
                var page = await Task.Run(() => MessageExport.BuildHtmlDocument(detail, context), ct);
                await ui.WritePdfAsync(page, path, ct);
                break;
        }
    }

    /// <summary>
    /// The readable parts: the cached copy when there is one, else the server — without marking the
    /// message read (the prefetch path), since saving a message is not reading it.
    /// </summary>
    private async Task<MailMessageDetail> LoadDetailAsync(MailMessageSummary message, CancellationToken ct)
    {
        MailMessageDetail? detail = message as MailMessageDetail;
        if (detail is null && _store is not null)
        {
            try { detail = await _store.LoadDetailAsync(message.AccountId, message.FolderName, message.MessageId); }
            catch (Exception ex) { LogService.Log("MessageSaver: local store unavailable — asking the server", ex); }
        }
        detail ??= await _mail.PrefetchMessageDetailAsync(message.AccountId, message.FolderName, message.MessageId, ct);

        // The cached detail row does not carry list state (read, flags, folder label); the summary does.
        detail.IsRead      = message.IsRead;
        detail.IsReplied   = message.IsReplied;
        detail.IsForwarded = message.IsForwarded;
        detail.FlagId      = message.FlagId;
        detail.FlagName    = message.FlagName;
        if (string.IsNullOrWhiteSpace(detail.Subject)) detail.Subject = message.Subject;
        if (string.IsNullOrWhiteSpace(detail.From))    detail.From    = message.From;
        if (detail.Date == default)                    detail.Date    = message.Date;
        return detail;
    }

    /// <summary>
    /// The user-facing reason the original is out of reach, or null when the failure is something
    /// else (a full disk, say) that another format would not get around.
    /// </summary>
    private static string? OriginalOutOfReach(Exception ex, CancellationToken ct) => ex switch
    {
        MessageOriginalUnavailableException gone => gone.Message,
        _ when ConnectionFailure.IsConnectionFailure(ex, ct) || ex is AccountNotConnectedException
            => "QuickMail cannot reach the server right now, and the original message is only on the server.",
        _ => null,
    };

    internal static string ExplainOriginalFailure(int requested, int failed, string reason, int savedCount)
    {
        var what = requested == 1
            ? "The original of this message could not be saved."
            : $"The originals of {failed} of {requested} messages could not be saved{(savedCount > 0 ? $"; the other {savedCount} were" : string.Empty)}.";
        var instead = failed == 1 ? "it" : "them";
        return $"{what} {reason}\n\nYou can save {instead} as a text file, web page, or PDF instead. Choose another format?";
    }

    private static MessageSaveOutcome Describe(int requested, List<string> saved, string folder, int failedCount, string? firstReason)
    {
        var where = FolderLabel(folder);
        if (failedCount == 0)
        {
            return new MessageSaveOutcome(requested == 1
                ? $"Saved {Path.GetFileName(saved[0])} in {where}."
                : $"Saved {saved.Count} messages in {where}.");
        }
        if (saved.Count == 0)
        {
            return new MessageSaveOutcome(requested == 1
                ? $"Could not save the message: {Sentence(firstReason)}"
                : $"Could not save the {requested} messages: {Sentence(firstReason)}");
        }
        return new MessageSaveOutcome(
            $"Saved {saved.Count} of {requested} messages in {where}. {failedCount} could not be saved: {Sentence(firstReason)}");
    }

    private static string Sentence(string? reason)
    {
        var r = string.IsNullOrWhiteSpace(reason) ? "an unknown error" : reason.Trim();
        return r.EndsWith('.') ? r : r + ".";
    }

    private static string? Join(string? a, string? b) =>
        string.IsNullOrWhiteSpace(a) ? b : string.IsNullOrWhiteSpace(b) ? a : a + " " + b;

    /// <summary>The folder's own name ("Documents"), which is what a person calls it; the full path is noise.</summary>
    internal static string FolderLabel(string folder)
    {
        var trimmed = folder.TrimEnd('\\', '/');
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? folder : name;
    }

    private void RememberSaveAsFolder(string folder)
    {
        try
        {
            var cfg = _config.Load();
            if (string.Equals(cfg.LastSaveAsFolder, folder, StringComparison.OrdinalIgnoreCase)) return;
            cfg.LastSaveAsFolder = folder;
            _config.Save(cfg);
        }
        catch (Exception ex) { LogService.Log("MessageSaver: could not remember the Save As folder", ex); }
    }
}

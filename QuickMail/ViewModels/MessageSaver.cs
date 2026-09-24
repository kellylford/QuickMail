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
/// <param name="OverwriteConfirmed">
/// True only when the Save dialog itself asked about replacing exactly <paramref name="Path"/> — that
/// is, the path came back as the dialog returned it. Anything else is never overwritten.
/// </param>
public sealed record MessageSaveTarget(string Path, MessageSaveFormat Format, bool OverwriteConfirmed = false);

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

    /// <summary>
    /// Several messages, some already saved in the folder: replace those copies, keep both, or stop.
    /// (One message goes to the Save As dialog instead, which asks in Windows' own way.)
    /// </summary>
    ExistingSaveChoice AskAboutExisting(int existing, int total, string folderName);

    /// <summary>Lays <paramref name="html"/> out and returns it as a PDF.</summary>
    Task<byte[]> RenderPdfAsync(string html, CancellationToken ct);

    /// <summary>Shows the Print dialog for <paramref name="html"/> and prints it. False when cancelled.</summary>
    Task<bool> PrintAsync(string html, string documentTitle, CancellationToken ct);
}

/// <summary>What to do about messages that were saved in the folder before.</summary>
public enum ExistingSaveChoice { Replace, KeepBoth, Cancel }

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

    /// <summary>
    /// The folder Save writes into: the one set in Settings, else Documents. A relative path (only
    /// possible by editing config.ini) is ignored: it would resolve against whatever the working
    /// directory is, which for an installed copy is the folder the next update replaces.
    /// </summary>
    public string SaveFolder()
    {
        var configured = _config.Load().SaveMessageFolder?.Trim();
        return string.IsNullOrEmpty(configured) || !Path.IsPathFullyQualified(configured)
            ? _defaultFolder()
            : configured;
    }

    /// <summary>
    /// Save (<paramref name="chooseLocation"/> false: the default format into the save folder, no
    /// dialog) or Save As (true: the dialog). <paramref name="formatOverride"/> preselects a type in
    /// the dialog — used when retrying in another format after the original was out of reach.
    /// </summary>
    public Task<MessageSaveOutcome> SaveAsync(
        IReadOnlyList<MailMessageSummary> messages, bool chooseLocation, IMessageSaveUi ui,
        MessageSaveFormat? formatOverride = null, CancellationToken ct = default)
        => SaveCoreAsync(messages, chooseLocation, ui, formatOverride, startFolder: null, ct);

    private async Task<MessageSaveOutcome> SaveCoreAsync(
        IReadOnlyList<MailMessageSummary> messages, bool chooseLocation, IMessageSaveUi ui,
        MessageSaveFormat? formatOverride, string? startFolder, CancellationToken ct)
    {
        if (messages.Count == 0) return new MessageSaveOutcome(null);

        var config = _config.Load();
        var format = formatOverride ?? MessageSaveFormats.FromConfigValue(config.SaveMessageFormat);

        string? singleName = null;   // Save As on one message: the name the dialog returned
        var overwriteSingle = false; // ...and whether the dialog asked about replacing it
        string folder;
        if (chooseLocation)
        {
            var start = startFolder
                ?? (!string.IsNullOrWhiteSpace(config.LastSaveAsFolder) && Directory.Exists(config.LastSaveAsFolder)
                    ? config.LastSaveAsFolder
                    : SaveFolder());

            var target = messages.Count == 1
                ? ui.ChooseFile(MessageExport.BuildFileName(messages[0], format), format, start)
                : ui.ChooseFolder(messages.Count, format, start);
            if (target is null) return new MessageSaveOutcome(null);

            format = target.Format;
            if (messages.Count == 1)
            {
                singleName      = Path.GetFileName(target.Path);
                overwriteSingle = target.OverwriteConfirmed;
                folder          = Path.GetDirectoryName(target.Path) ?? start;
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
                LogService.Log($"MessageSaver: save folder unavailable: {ex.GetType().Name} (0x{ex.HResult:X8})");
                return new MessageSaveOutcome(
                    $"The save folder {folder} is not available. Choose another in Settings, or use Save As.");
            }
        }

        // Saved here before? Never replace or duplicate it without asking (Windows' own rule for Save).
        // One message: the Save As dialog, on this folder and name — it asks before replacing, and lets
        // the user choose another name. Several: ask once for all of them.
        var replaceExisting = overwriteSingle;
        if (singleName is null)
        {
            var existing = messages.Count(m => Exists(Path.Combine(folder, MessageExport.BuildFileName(m, format))));
            if (existing > 0)
            {
                if (messages.Count == 1 && !chooseLocation)
                    return await SaveCoreAsync(messages, chooseLocation: true, ui, format, startFolder: folder, ct);

                switch (ui.AskAboutExisting(existing, messages.Count, FolderLabel(folder)))
                {
                    case ExistingSaveChoice.Cancel:  return new MessageSaveOutcome(null);
                    case ExistingSaveChoice.Replace: replaceExisting = true; break;
                }
            }
        }

        var saved          = new List<string>();
        var originalFailed = new List<(MailMessageSummary Message, string Reason)>();
        var otherFailed    = new List<string>();

        foreach (var message in messages)
        {
            ct.ThrowIfCancellationRequested();
            var name = singleName ?? MessageExport.BuildFileName(message, format);
            try
            {
                var replace = singleName is not null ? overwriteSingle
                            : replaceExisting && Exists(Path.Combine(folder, name));
                saved.Add(await WriteOneAsync(message, format, folder, name, replace, ui, ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (format == MessageSaveFormat.Eml && OriginalOutOfReach(ex, ct) is { } reason)
            {
                originalFailed.Add((message, reason));
            }
            catch (Exception ex)
            {
                // The type and code only: a file-system exception's message carries the path, and the
                // path is built from the subject and sender, which the log must not record.
                LogService.Log($"MessageSaver: could not save a message as {format}: {ex.GetType().Name} (0x{ex.HResult:X8})");
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
                var second = await SaveCoreAsync(retry, chooseLocation: true, ui, MessageSaveFormat.Text, startFolder: null, ct);
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
            LogService.Log($"MessageSaver: could not load a message to print: {ex.GetType().Name} (0x{ex.HResult:X8})");
            return new MessageSaveOutcome(ConnectionFailure.IsConnectionFailure(ex, ct)
                ? "This message is not available offline, so it cannot be printed."
                : $"Could not print: {ex.Message}");
        }

        var title = string.IsNullOrWhiteSpace(detail.Subject) ? "Message" : detail.Subject.Trim();
        try
        {
            var html = await HtmlDocumentAsync(detail, _context(detail), ct);
            return await ui.PrintAsync(html, title, ct)
                ? new MessageSaveOutcome($"Sent {title} to the printer.")
                : new MessageSaveOutcome(null);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            LogService.Log($"MessageSaver: print failed: {ex.GetType().Name} (0x{ex.HResult:X8})");
            return new MessageSaveOutcome($"Could not print: {ex.Message}");
        }
    }

    /// <summary>Saves one message, and returns the path it was written to.</summary>
    private async Task<string> WriteOneAsync(
        MailMessageSummary message, MessageSaveFormat format, string folder, string fileName, bool overwrite,
        IMessageSaveUi ui, CancellationToken ct)
    {
        if (format == MessageSaveFormat.Eml)
        {
            // Streamed from the server straight into the file; never held in memory whole.
            var (stream, writing, final) = CreateFile(folder, fileName, overwrite);
            var keep = false;
            try
            {
                await using (stream)
                {
                    await _mail.CopyOriginalMessageToAsync(message.AccountId, message.FolderName, message.MessageId, stream, ct);
                    if (stream.Length == 0)
                        throw new MessageOriginalUnavailableException("The server returned an empty message.");
                }
                Finish(writing, final);
                keep = true;
                return final;
            }
            finally
            {
                // A failed or cancelled download leaves a partial file that is not the original.
                if (!keep) TryDelete(writing);
            }
        }

        var detail  = await LoadDetailAsync(message, ct);
        var context = _context(detail);
        byte[] bytes = format switch
        {
            // UTF-8 with a byte-order mark, so Notepad and every other Windows editor reads a
            // non-English message correctly instead of guessing a code page.
            MessageSaveFormat.Text => WithBom(await Task.Run(() => MessageExport.BuildTextDocument(detail, context), ct)),
            MessageSaveFormat.Html => Encoding.UTF8.GetBytes(await HtmlDocumentAsync(detail, context, ct)),
            _                      => await ui.RenderPdfAsync(await HtmlDocumentAsync(detail, context, ct), ct),
        };

        var (file, writingTo, finalPath) = CreateFile(folder, fileName, overwrite);
        var done = false;
        try
        {
            await using (file) await file.WriteAsync(bytes, ct);
            Finish(writingTo, finalPath);
            done = true;
            return finalPath;
        }
        finally
        {
            if (!done) TryDelete(writingTo);
        }
    }

    private static byte[] WithBom(string text)
    {
        var body = Encoding.UTF8.GetBytes(text);
        var bytes = new byte[body.Length + 3];
        bytes[0] = 0xEF; bytes[1] = 0xBB; bytes[2] = 0xBF;
        body.CopyTo(bytes, 3);
        return bytes;
    }

    /// <summary>
    /// Creates the file to write. Never replaces an existing file unless <paramref name="overwrite"/>
    /// says the Save dialog asked about exactly this one: otherwise the file is created with
    /// <see cref="FileMode.CreateNew"/>, which fails rather than overwrites, and a name that turns out
    /// to be taken — by another save finishing between the check and the create — moves on to the next
    /// "(n)". Checking and then writing with an overwriting mode let two saves of look-alike messages
    /// land on one name and silently keep only the second.
    /// </summary>
    /// <para>A confirmed replacement is written beside the old file first and moved over it only once
    /// complete, so a failed download does not destroy the file the user chose to replace.</para>
    private (FileStream Stream, string Writing, string Final) CreateFile(string folder, string fileName, bool overwrite)
    {
        if (overwrite)
        {
            var exact = Path.Combine(folder, fileName);
            var temp  = Path.Combine(folder, $".{Guid.NewGuid():N}.quickmail-saving");
            return (new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None), temp, exact);
        }

        for (var attempt = 0; attempt < 100; attempt++)
        {
            var path = MessageExport.UniquePath(folder, fileName, p => _fileExists(p) || File.Exists(p));
            try
            {
                return (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None), path, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Taken since UniquePath looked; try the next number.
            }
        }
        throw new IOException("No free file name could be found in the folder.");
    }

    private bool Exists(string path) => _fileExists(path) || File.Exists(path);

    private static void Finish(string writing, string final)
    {
        if (!string.Equals(writing, final, StringComparison.OrdinalIgnoreCase))
            File.Move(writing, final, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) { LogService.Log($"MessageSaver: could not remove a partial file: {ex.GetType().Name}"); }
    }

    /// <summary>The web-page form of a message, with the pictures it shows written in.</summary>
    private async Task<string> HtmlDocumentAsync(MailMessageDetail detail, MessageSaveContext context, CancellationToken ct)
    {
        var pictures = await PicturesForAsync(detail, ct);
        return await Task.Run(() => MessageExport.BuildHtmlDocument(detail, context, pictures), ct);
    }

    private static readonly IReadOnlyDictionary<string, SavedPicture> NoPictures =
        new Dictionary<string, SavedPicture>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The pictures a saved web page, PDF or printout shows, as the reading pane would (#728, #729):
    /// the message's own pictures unless Show pictures included in messages is off, and pictures
    /// from the web only if QuickMail already loaded them. Nothing new is fetched from the web.
    /// A picture that cannot be had is simply left out; its description stands in.
    /// </summary>
    private async Task<SavedPictures> PicturesForAsync(MailMessageDetail detail, CancellationToken ct)
    {
        var embedded = NoPictures;
        if (_config.Load().ShowEmbeddedPictures && InlineImages.HasReferences(detail.HtmlBody))
        {
            try
            {
                // The load is shared with the reading pane and is not cancelled; Cancel stops waiting.
                var found = await EmbeddedPictureLoader.LoadAsync(_mail, detail).WaitAsync(ct);
                embedded = found
                    .Where(p => p.Value.Content is { Length: > 0 })
                    .ToDictionary(p => p.Key, p => new SavedPicture(p.Value.Content!, p.Value.ContentType),
                        StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                LogService.Log($"MessageSaver: the message's pictures could not be read: {ex.GetType().Name}");
            }
        }
        return new SavedPictures(embedded, url =>
            WebPictureFetcher.TryGetCached(url) is { } picture ? new SavedPicture(picture.Bytes, picture.ContentType) : null);
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
        catch (Exception ex) { LogService.Log($"MessageSaver: could not remember the Save As folder: {ex.GetType().Name}"); }
    }
}

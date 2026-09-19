using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

// Saving and printing messages (#728). The flow itself lives in MessageSaver; this is the part that
// knows the app's state — accounts, cached folders, the Outbox — and reports to the status bar.
public partial class MainViewModel
{
    private MessageSaver? _messageSaver;

    /// <summary>Built on first use, so the hundreds of test constructions of this VM never pay for it.</summary>
    internal MessageSaver MessageSaver =>
        _messageSaver ??= new MessageSaver(_imap, _localStore, _configService, BuildSaveContext);

    /// <summary>
    /// Save (<paramref name="chooseLocation"/> false) or Save As (true) the given messages. The window
    /// resolves what is selected — a multi-selection, a group header's messages — and passes itself
    /// as <paramref name="ui"/> so its dialogs are owned by the window the user is in.
    /// </summary>
    public async Task SaveMessagesAsync(IReadOnlyList<MailMessageSummary> messages, bool chooseLocation, IMessageSaveUi ui,
        Action<string>? report = null)
    {
        // An Outbox row is a queued compose, not a message: there is no original and no server copy.
        var toSave = messages.Where(m => !IsOutboxRow(m)).ToList();
        if (toSave.Count == 0)
        {
            if (messages.Count > 0)
                Report(report, "Messages waiting in the Outbox cannot be saved until they are sent.", AnnouncementCategory.Result);
            return;
        }

        if (toSave.Count > 1 && !chooseLocation)
            Report(report, $"Saving {toSave.Count} messages…", AnnouncementCategory.Status);

        try
        {
            var outcome = await MessageSaver.SaveAsync(toSave, chooseLocation, ui, ct: _messageActionShutdownCts.Token);
            if (outcome.Text is not null)
                Report(report, outcome.Text, AnnouncementCategory.Result);
        }
        catch (OperationCanceledException) { /* app is shutting down */ }
        catch (Exception ex)
        {
            LogService.Log("SaveMessages", ex);
            Report(report, $"Could not save: {ex.Message}", AnnouncementCategory.Result);
        }
    }

    /// <summary>Prints one message.</summary>
    public async Task PrintMessageAsync(MailMessageSummary? message, IMessageSaveUi ui, Action<string>? report = null)
    {
        if (message is null) return;
        if (IsOutboxRow(message))
        {
            Report(report, "Messages waiting in the Outbox cannot be printed until they are sent.", AnnouncementCategory.Result);
            return;
        }
        try
        {
            var outcome = await MessageSaver.PrintAsync(message, ui, _messageActionShutdownCts.Token);
            if (outcome.Text is not null)
                Report(report, outcome.Text, AnnouncementCategory.Result);
        }
        catch (OperationCanceledException) { /* app is shutting down */ }
        catch (Exception ex)
        {
            LogService.Log("PrintMessage", ex);
            Report(report, $"Could not print: {ex.Message}", AnnouncementCategory.Result);
        }
    }

    /// <summary>
    /// An outcome in the status bar and spoken - by this window as a Result, or through
    /// <paramref name="report"/> when the user is in another window (a message window), where a
    /// notification raised on this one would not be heard.
    /// </summary>
    private void Report(Action<string>? report, string text, AnnouncementCategory category)
    {
        if (report is null) { SetStatus(text, category); return; }
        SetStatusSilently(text);
        if (category == AnnouncementCategory.Result) report(text);
    }

    /// <summary>The account, folder and flag a saved copy names, as a person would say them.</summary>
    private MessageSaveContext BuildSaveContext(MailMessageSummary message)
    {
        var account = Accounts.FirstOrDefault(a => a.Id == message.AccountId);
        string accountLabel;
        if (account is null)
            accountLabel = string.Empty;
        else if (string.IsNullOrWhiteSpace(account.Username)
                 || string.Equals(account.AccountLabel, account.Username, StringComparison.OrdinalIgnoreCase))
            accountLabel = account.AccountLabel;
        else
            accountLabel = $"{account.AccountLabel} ({account.Username})";

        CachedFolders.TryGetValue(message.AccountId, out var folders);
        var folderPath = FolderPaths.Describe(message.FolderName, folders, message.FolderDisplayName);

        return new MessageSaveContext(accountLabel, folderPath,
            message.IsFlagged ? message.FlagName : null, DateTimeOffset.Now);
    }
}

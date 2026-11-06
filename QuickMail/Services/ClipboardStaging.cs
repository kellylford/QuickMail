using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Helpers;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// The temporary folder that holds the files QuickMail puts on the Windows clipboard: copied messages
/// as .eml files (#929), and copied attachments. A file on the clipboard is only a path, so the file
/// has to exist until it is pasted; it lives here until the next copy replaces it or QuickMail closes.
///
/// <para>Each running QuickMail has its own folder, named for its process id, under
/// <c>%TEMP%\QuickMail\Clipboard</c>. Two profiles can run at once, and one closing must not delete
/// files the other still has on the clipboard. A folder whose process is no longer running was left by
/// a QuickMail that did not close cleanly, and is removed at the next start.</para>
/// </summary>
public sealed class ClipboardStaging
{
    private readonly string _root;
    private readonly string _mine;
    private readonly object _gate = new();

    public ClipboardStaging(string? root = null, int? processId = null)
    {
        _root = root ?? Path.Combine(Path.GetTempPath(), "QuickMail", "Clipboard");
        _mine = Path.Combine(_root, (processId ?? Environment.ProcessId).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>This process's folder. Exposed for tests.</summary>
    public string Folder => _mine;

    /// <summary>
    /// A new, empty folder for one copy's files, after deleting the files of every earlier copy:
    /// once a copy has replaced it on the clipboard, nothing can paste them any more.
    /// </summary>
    public string NewSet()
    {
        lock (_gate)
        {
            DeleteQuietly(_mine);
            var set = Path.Combine(_mine, Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(set);
            return set;
        }
    }

    /// <summary>Deletes this process's files. Called when QuickMail closes.</summary>
    public void DeleteMine()
    {
        lock (_gate) DeleteQuietly(_mine);
    }

    /// <summary>
    /// Deletes the folders of QuickMail processes that are no longer running. Called at startup.
    /// <paramref name="isRunning"/> is replaceable for tests.
    /// </summary>
    public void DeleteAbandoned(Func<int, bool>? isRunning = null)
    {
        isRunning ??= IsQuickMailRunning;
        if (!Directory.Exists(_root)) return;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_root))
            {
                if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
                if (string.Equals(dir, _mine, StringComparison.OrdinalIgnoreCase)) continue;
                if (!isRunning(pid)) DeleteQuietly(dir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Log("ClipboardStaging: could not tidy abandoned clipboard files", ex);
        }
    }

    /// <summary>
    /// Writes each message's original, as the server sent it, to a .eml file in a new set, named as
    /// Save names it ("Subject - Sender - date.eml"), and returns the paths in the order given.
    /// All or nothing: if any message cannot be fetched, the set is deleted and the exception passes
    /// up, because a clipboard holding some of the copied messages and silently not others would
    /// paste something other than what the user copied.
    /// </summary>
    public async Task<IReadOnlyList<string>> StageMessagesAsync(
        IMailService mail, IReadOnlyList<MailMessageSummary> messages, CancellationToken ct)
    {
        var set   = NewSet();
        var paths = new List<string>(messages.Count);
        try
        {
            foreach (var message in messages)
            {
                ct.ThrowIfCancellationRequested();
                var path = MessageExport.UniquePath(set, MessageExport.BuildFileName(message, MessageSaveFormat.Eml), File.Exists);
                await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await mail.CopyOriginalMessageToAsync(message.AccountId, message.FolderName, message.MessageId, file, ct);
                    if (file.Length == 0)
                        throw new MessageOriginalUnavailableException("The server returned an empty message.");
                }
                paths.Add(path);
            }
            return paths;
        }
        catch
        {
            DeleteQuietly(set);
            throw;
        }
    }

    private static bool IsQuickMailRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }          // no such process
        catch (InvalidOperationException) { return false; }  // exited while we looked
    }

    private static void DeleteQuietly(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still open in another program (Explorer mid-paste, a viewer). It goes at the next
            // start's tidy instead; nothing depends on it being gone now.
            LogService.Debug($"ClipboardStaging: could not delete {dir}: {ex.Message}");
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QuickMail.Services;

/// <summary>
/// What the one-time sender-address backfill (#803) has already done on this computer, so a launch that cannot finish
/// it repeats only what is left. <c>.sender-address-backfilled</c> says the whole thing is done. Until then
/// <c>.sender-address-backfill-progress</c> lists, one per line, the parts that are: <c>local</c> once the startup
/// pass over the cache has run, and the id of each IMAP account the server pass has worked through.
/// <para>
/// Without it, one IMAP account that never connects (an expired sign-in nobody renews) kept the whole pass pending
/// for good: every launch re-ran the local scan before the first window, re-asked every finished account's server
/// for rows that can never be filled, and showed the progress region again (review of #806).
/// </para>
/// Both files are about this computer's mail.db, so a settings export leaves them behind with it.
/// </summary>
public sealed class SenderAddressBackfillProgress(string profileDir)
{
    private const string LocalLine = "local";
    private readonly object _gate = new();

    private string DonePath => Path.Combine(profileDir, ".sender-address-backfilled");
    private string ProgressPath => Path.Combine(profileDir, ".sender-address-backfill-progress");

    /// <summary>Whether the whole backfill is done, local and server halves alike.</summary>
    public bool IsDone => File.Exists(DonePath);

    /// <summary>Whether the startup pass over the cache has already run.</summary>
    public bool IsLocalDone => ReadLines().Contains(LocalLine);

    /// <summary>The accounts of <paramref name="imapAccountIds"/> the server pass has not yet worked through.</summary>
    public IReadOnlyList<Guid> Owed(IEnumerable<Guid> imapAccountIds)
    {
        var finished = ReadLines();
        return [.. imapAccountIds.Where(id => !finished.Contains(id.ToString("D")))];
    }

    public void RecordLocalDone() => Append(LocalLine);

    public void RecordAccountDone(Guid accountId) => Append(accountId.ToString("D"));

    /// <summary>Records the whole backfill done; the per-part record is no longer needed.</summary>
    public void RecordDone()
    {
        lock (_gate)
        {
            try
            {
                File.WriteAllText(DonePath, DateTime.UtcNow.ToString("o"));
                File.Delete(ProgressPath);
            }
            catch (Exception ex) { LogService.Log("Sender-address backfill: could not write its marker.", ex); }
        }
    }

    // A record that can't be written only costs repeating that part next launch, so a failure is logged, not thrown.
    private void Append(string line)
    {
        lock (_gate)
        {
            try { File.AppendAllLines(ProgressPath, [line]); }
            catch (Exception ex) { LogService.Log($"Sender-address backfill: could not record \"{line}\" as done.", ex); }
        }
    }

    private HashSet<string> ReadLines()
    {
        lock (_gate)
        {
            try
            {
                return File.Exists(ProgressPath)
                    ? File.ReadAllLines(ProgressPath).Select(l => l.Trim()).Where(l => l.Length > 0)
                          .ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : [];
            }
            catch (Exception ex)
            {
                LogService.Log("Sender-address backfill: could not read its progress; starting over.", ex);
                return [];
            }
        }
    }
}

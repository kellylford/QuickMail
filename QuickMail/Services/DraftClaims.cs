using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace QuickMail.Services;

/// <summary>
/// The locally-stored drafts a compose window currently has open, which the background upload pass
/// skips (#637).
///
/// <para>Uploading a draft deletes the local row and its stored bytes. Do that while a window is
/// still editing it and the window's next auto-save re-creates the row having lost the header that
/// says which server draft it replaces — so the upload files a SECOND copy instead of replacing the
/// first, and the user is left with two or three drafts where they wrote one, plus an orphan in
/// Drafts after they send.</para>
///
/// <para>Held in memory only, deliberately. A claim exists to protect a window that is open right
/// now; if the app dies the window is gone too, and the draft simply becomes uploadable again —
/// which is the correct state, and needs no recovery step.</para>
/// </summary>
public static class DraftClaims
{
    /// <summary>
    /// Static because the holder and the checker are different objects: every compose window takes
    /// its own claim, and the sweep that must honour it runs inside SyncService. Making this an
    /// instance field would silently disable the protection with every test still passing, which
    /// is why <c>DraftClaimTests</c> asserts it across two separate callers.
    /// </summary>
    private static readonly ConcurrentDictionary<(Guid Account, string Folder, string Id), int> Claims = new();

    /// <summary>
    /// Claims a draft while a compose window has it open. Dispose the handle to release it.
    /// </summary>
    public static IDisposable Claim(Guid accountId, string folderName, string messageId)
    {
        // Counted, not set: the same draft can legitimately be open in two windows, and the first
        // one to close must not unclaim it out from under the second — which would let the sweep
        // upload the pre-edit copy while the other window is still being typed in.
        var key = (accountId, folderName, messageId);
        Claims.AddOrUpdate(key, 1, (_, n) => n + 1);
        LogService.Debug($"Drafts: {messageId} claimed by a compose window");
        return new Handle(key);
    }

    /// <summary>True while a compose window holds this draft open.</summary>
    public static bool IsClaimed(Guid accountId, string folderName, string messageId)
        => Claims.ContainsKey((accountId, folderName, messageId));

    /// <summary>Releases a claim exactly once, however the window went away.</summary>
    private sealed class Handle((Guid Account, string Folder, string Id) key) : IDisposable
    {
        private readonly (Guid Account, string Folder, string Id) _key = key;
        private int _released;

        public void Dispose()
        {
            // Interlocked rather than a plain bool: the rest of this state is explicitly
            // thread-safe, which invites the assumption that this is too, and two concurrent
            // Dispose calls on one handle could both pass a bool check and both decrement —
            // dropping another window's count.
            if (Interlocked.Exchange(ref _released, 1) != 0) return;

            // Only the last holder removes it.
            while (Claims.TryGetValue(_key, out var n))
            {
                if (n <= 1)
                {
                    // By key AND value: a fresh claim taken between the read and the remove would
                    // otherwise be thrown away, leaving a window's draft unprotected.
                    if (Claims.TryRemove(new KeyValuePair<(Guid, string, string), int>(_key, n))) break;
                }
                else if (Claims.TryUpdate(_key, n - 1, n)) break;
            }
            LogService.Debug($"Drafts: {_key.Id} released");
        }
    }
}

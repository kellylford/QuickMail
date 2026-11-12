using System;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

/// <summary>
/// Saying when a sound scheme someone added from the gallery has an update waiting (#971). At most
/// once a day, well after startup, and never in its way: a failure is only logged, and an update found
/// goes in the status bar, announced as a Result, once per newer version.
/// </summary>
public partial class MainViewModel
{
    private readonly ISoundGalleryService? _soundGallery;
    private CancellationTokenSource? _soundUpdateCheckCts;

    /// <summary>
    /// Started by the View once the main window has loaded, and runs until QuickMail closes: waits for
    /// startup to settle, then looks every hour whether a check is due. The check itself decides whether
    /// it is (once a day, setting on, online, a gallery scheme here). Does nothing without a gallery.
    /// </summary>
    public async Task WatchForSoundSchemeUpdatesAsync()
    {
        if (_soundGallery is null) return;
        ReplaceCts(ref _soundUpdateCheckCts, out var ct);
        var checker = new SoundSchemeUpdateChecker(_configService, _soundGallery,
            () => _connectivity is { IsNetworkAvailable: false });
        try
        {
            await Task.Delay(SoundSchemeUpdateCheck.SettleDelay, ct);
            while (!ct.IsCancellationRequested)
            {
                await CheckForSoundSchemeUpdatesAsync(checker, ct);
                await Task.Delay(SoundSchemeUpdateCheck.LookAgainEvery, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // QuickMail is closing.
        }
    }

    /// <summary>One look: checks if due, and says what it found. Never throws for a gallery problem.</summary>
    internal async Task CheckForSoundSchemeUpdatesAsync(SoundSchemeUpdateChecker checker, CancellationToken ct)
    {
        string? notice;
        try
        {
            notice = await checker.CheckIfDueAsync(DateTime.UtcNow, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Saving config.ini, say; nothing the person needs to hear about.
            LogService.Log("Sound scheme update check failed", ex);
            return;
        }
        // Result, not Status, as for QuickMail's own update notice: a one-time discovery that someone who
        // silences background chatter should still hear. Said once per newer version (the checker's job).
        if (notice is not null) SetStatus(notice, AnnouncementCategory.Result);
    }
}

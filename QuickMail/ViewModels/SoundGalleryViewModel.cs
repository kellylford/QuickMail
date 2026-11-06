using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

/// <summary>The Sound Scheme Gallery window (#937): the gallery's schemes, and fetching one to add.</summary>
public sealed partial class SoundGalleryViewModel(ISoundGalleryService gallery) : ObservableObject
{
    public ObservableCollection<SoundGalleryEntry> Entries { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd), nameof(Details))]
    private SoundGalleryEntry? _selectedEntry;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    private bool _isBusy;

    /// <summary>What the gallery is doing, or why it couldn't.</summary>
    [ObservableProperty] private string _status = string.Empty;

    public bool CanAdd => SelectedEntry is not null && !IsBusy;

    /// <summary>The selected scheme's description and licence.</summary>
    public string Details => SelectedEntry is { } e
        ? (e.Description.Length > 0 ? e.Description + " " : string.Empty) + $"Licence: {e.License}."
        : string.Empty;

    /// <summary>Something no control reports; the View announces it.</summary>
    public event Action<string, AnnouncementCategory>? AnnouncementRequested;

    /// <summary>Lists the gallery's schemes. A failure leaves the list empty and says why.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        IsBusy = true;
        Entries.Clear();
        SetStatus("Loading the sound scheme gallery…", AnnouncementCategory.Status);
        try
        {
            var entries = await gallery.LoadAsync(ct);
            foreach (var entry in entries) Entries.Add(entry);
            SelectedEntry = Entries.Count > 0 ? Entries[0] : null;
            // The list reports its own rows; only an empty gallery needs saying.
            Status = Entries.Count == 0 ? "The gallery has no schemes yet." : string.Empty;
            if (Entries.Count == 0) AnnouncementRequested?.Invoke(Status, AnnouncementCategory.Result);
        }
        catch (SoundGalleryException ex)
        {
            SetStatus(ex.Message, AnnouncementCategory.Result);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Downloads the selected scheme and checks it.</summary>
    /// <returns>The checked file, for the caller to import then delete; null if it couldn't be fetched.</returns>
    public async Task<string?> DownloadSelectedAsync(CancellationToken ct)
    {
        if (SelectedEntry is not { } entry || IsBusy) return null;
        IsBusy = true;
        SetStatus($"Downloading {entry.Name}…", AnnouncementCategory.Status);
        try
        {
            var path = await gallery.DownloadAsync(entry, ct);
            Status = string.Empty;
            return path;
        }
        catch (SoundGalleryException ex)
        {
            SetStatus(ex.Message, AnnouncementCategory.Result);
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SetStatus(string text, AnnouncementCategory category)
    {
        Status = text;
        AnnouncementRequested?.Invoke(text, category);
    }
}

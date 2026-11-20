using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

/// <summary>Whether a gallery scheme is already here (app-kit sounds/SCHEMES.md, "Where an added scheme came from").</summary>
public enum SoundGalleryState
{
    /// <summary>Not added yet.</summary>
    New,

    /// <summary>Added, and the gallery still lists the same file.</summary>
    Added,

    /// <summary>Added, and the gallery now lists a different file for it.</summary>
    UpdateAvailable,
}

/// <summary>A row of the gallery's list: a scheme and whether it's here. A screen reader reads the row from <see cref="ToString"/>.</summary>
/// <param name="Entry">The gallery's listing.</param>
/// <param name="State">Whether it's added, or has an update.</param>
public sealed record SoundGalleryRow(SoundGalleryEntry Entry, SoundGalleryState State)
{
    public string Id => Entry.Id;

    /// <summary>What type-ahead matches.</summary>
    public string Name => Entry.Name;

    /// <summary>The listing, then "Added." or "Update available.", as The Chat Place says it.</summary>
    public string AccessibleName => Entry.AccessibleName + State switch
    {
        SoundGalleryState.Added => " Added.",
        SoundGalleryState.UpdateAvailable => " Update available.",
        _ => string.Empty,
    };

    public string HelpText => Entry.HelpText;

    public override string ToString() => AccessibleName;
}

/// <summary>The Sound Scheme Gallery window (#937): the gallery's schemes, and fetching one to add or update.</summary>
/// <param name="gallery">The gallery.</param>
/// <param name="origins">Where each scheme already here came from: gallery id to the SHA-256 of its file.</param>
public sealed partial class SoundGalleryViewModel(ISoundGalleryService gallery, IReadOnlyDictionary<string, string>? origins = null) : ObservableObject
{
    private readonly IReadOnlyDictionary<string, string> _origins = origins ?? new Dictionary<string, string>();

    public ObservableCollection<SoundGalleryRow> Entries { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd), nameof(Details), nameof(HasDetails), nameof(AddLabel), nameof(AddName))]
    private SoundGalleryRow? _selectedEntry;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    private bool _isBusy;

    /// <summary>What the gallery is doing, or why it couldn't.</summary>
    [ObservableProperty] private string _status = string.Empty;

    /// <summary>Add, or Update, applies to a scheme that isn't here, or has a newer version; not to one already added.</summary>
    public bool CanAdd => SelectedEntry is { State: not SoundGalleryState.Added } && !IsBusy;

    /// <summary>The button's text: Update for a scheme with a newer version, Add otherwise.</summary>
    public string AddLabel => SelectedEntry?.State == SoundGalleryState.UpdateAvailable ? "_Update" : "_Add";

    /// <summary>The button's name for a screen reader: its text without the access key.</summary>
    public string AddName => AddLabel.Replace("_", string.Empty, StringComparison.Ordinal);

    public bool HasDetails => SelectedEntry is not null;

    /// <summary>The selected scheme's description and licence.</summary>
    public string Details => SelectedEntry?.Entry is { } e
        ? (e.Description.Length > 0 ? e.Description + " " : string.Empty) + $"By {e.Author}. Licence: {e.License}."
        : string.Empty;

    /// <summary>Something no control reports; the View announces it.</summary>
    public event Action<string, AnnouncementCategory>? AnnouncementRequested;

    // Which load is current: Reload during a load supersedes it, and only the latest may fill the
    // list or say the window is no longer busy.
    private int _loadGeneration;

    /// <summary>Whether a listed scheme is here already, and if so whether the gallery's file has changed.</summary>
    public SoundGalleryState StateOf(SoundGalleryEntry entry) =>
        !_origins.TryGetValue(entry.Id, out var sha) ? SoundGalleryState.New
        : string.Equals(sha, entry.Sha256, StringComparison.Ordinal) ? SoundGalleryState.Added
        : SoundGalleryState.UpdateAvailable;

    /// <summary>Lists the gallery's schemes. A failure leaves the list empty and says why.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        var generation = ++_loadGeneration;
        IsBusy = true;
        Entries.Clear();
        SelectedEntry = null;
        SetStatus("Loading the sound scheme gallery…", AnnouncementCategory.Status);
        try
        {
            var entries = await gallery.LoadAsync(ct);
            if (generation != _loadGeneration) return;
            ct.ThrowIfCancellationRequested();
            foreach (var entry in entries) Entries.Add(new SoundGalleryRow(entry, StateOf(entry)));
            SelectedEntry = Entries.Count > 0 ? Entries[0] : null;
            // The list reports its own rows; only an empty gallery needs saying.
            Status = Entries.Count == 0 ? "The gallery has no schemes yet." : string.Empty;
            if (Entries.Count == 0) AnnouncementRequested?.Invoke(Status, AnnouncementCategory.Result);
        }
        catch (SoundGalleryException ex)
        {
            if (generation == _loadGeneration) SetStatus(ex.Message, AnnouncementCategory.Result);
        }
        finally
        {
            if (generation == _loadGeneration) IsBusy = false;
        }
    }

    /// <summary>
    /// Says that the selected scheme is already added, for Enter on its row, which would otherwise do
    /// nothing at all. Returns false if it isn't one already added.
    /// </summary>
    public bool SayAlreadyAdded()
    {
        if (SelectedEntry is not { State: SoundGalleryState.Added } row) return false;
        AnnouncementRequested?.Invoke($"You've already added {row.Name}.", AnnouncementCategory.Result);
        return true;
    }

    /// <summary>Downloads the selected scheme and checks it.</summary>
    /// <returns>The checked file, for the caller to import then delete; null if it couldn't be fetched.</returns>
    public async Task<string?> DownloadSelectedAsync(CancellationToken ct)
    {
        if (SelectedEntry is not { } row || !CanAdd) return null;
        IsBusy = true;
        SetStatus($"Downloading {row.Name}…", AnnouncementCategory.Status);
        try
        {
            var path = await gallery.DownloadAsync(row.Entry, ct);
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

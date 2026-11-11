using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

/// <summary>One event in the Sound Tutorial's list. A screen reader reads the item from <see cref="ToString"/>.</summary>
public sealed record SoundTutorialRow(SoundEventInfo Info)
{
    public string Label => Info.Label;

    public override string ToString() => Label;
}

/// <summary>
/// Help, Sound Tutorial: every event by name, and playing one sounds it as it would really sound, in
/// the scheme in use and at the saved volume, even with sounds off or muted, since the person asked to
/// hear it. Nothing here changes a setting; that's Settings, Sounds.
/// </summary>
public sealed partial class SoundTutorialViewModel : ObservableObject
{
    private const string SchemeSuffix = " scheme";

    private readonly IConfigService _config;
    private readonly ISoundService _sounds;
    private bool _played;

    [ObservableProperty] private string _intro = string.Empty;
    [ObservableProperty] private SoundTutorialRow? _selectedRow;

    /// <summary>What the last play had to say: that there's no sound, or that its file has gone. Otherwise empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    private string _note = string.Empty;

    public SoundTutorialViewModel(IConfigService config, ISoundService sounds)
    {
        _config = config;
        _sounds = sounds;
        Rows = [.. SoundCatalog.Events.Select(info => new SoundTutorialRow(info))];
        SelectedRow = Rows[0];
        Refresh();
    }

    public IReadOnlyList<SoundTutorialRow> Rows { get; }

    public bool HasNote => Note.Length > 0;

    /// <summary>A play had something to say; the View announces it.</summary>
    public event Action<string>? NoteAnnounced;

    /// <summary>Catches up with the scheme in use: the window is modeless, so Settings can change it while this is open.</summary>
    public void Refresh() => Intro = IntroFor(SoundCatalog.ActiveScheme(_config.Load()));

    /// <summary>The window's one line. A scheme called "Calm scheme" reads "the Calm scheme", not "the Calm scheme scheme".</summary>
    internal static string IntroFor(SoundScheme scheme)
    {
        var name = scheme.Name.Trim();
        if (name.EndsWith(SchemeSuffix, StringComparison.OrdinalIgnoreCase))
            name = name[..^SchemeSuffix.Length].TrimEnd();
        return $"Arrow to an event and press Space to hear it in the {name} scheme. Change sounds in Settings, Sounds.";
    }

    [RelayCommand]
    private void PlaySelected()
    {
        if (SelectedRow is not { } row) return;

        _played = true;
        _sounds.PreviewEvent(row.Info.Event);   // an event with no sound just stops the last one

        var note = NoteFor(row.Info.Event, SoundCatalog.ActiveScheme(_config.Load()));
        Note = note;
        if (note.Length > 0) NoteAnnounced?.Invoke(note);
    }

    private string NoteFor(SoundEvent soundEvent, SoundScheme scheme)
    {
        var sound = SoundCatalog.Resolve(soundEvent, scheme);
        return sound.Kind switch
        {
            SoundRefKind.None => "No sound.",
            SoundRefKind.Custom when _sounds.CustomSoundPath(scheme.Id, sound.Name) is null =>
                "Its sound file is missing, so the built-in sound plays.",
            _ => string.Empty,
        };
    }

    partial void OnSelectedRowChanged(SoundTutorialRow? value) => Note = string.Empty;

    /// <summary>Ends a sound played here when the window closes, so it doesn't play on over speech.</summary>
    public void StopSounds()
    {
        // Without a play, a sound for new mail just starting isn't this window's to stop.
        if (_played) _sounds.StopAll();
    }
}

using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

/// <summary>A sound someone can choose. A screen reader reads the item from <see cref="ToString"/>.</summary>
public sealed record SoundChoice(string Stored, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Builds the lists of sounds to choose from, for Settings and the rule editor alike.</summary>
public static class SoundChoiceList
{
    public static SoundChoice[] BuiltIn() =>
    [
        .. SoundCatalog.BuiltInSounds.Distinct().Order(StringComparer.Ordinal)
            .Select(name => new SoundChoice(SoundRef.BuiltIn(name).ToStored(), SoundCatalog.BuiltInLabel(name))),
    ];

    /// <summary>The entry for a sound not in the list because its file has gone. Stays listed, so it isn't silently changed.</summary>
    public static SoundChoice Missing(SoundRef sound) => new(sound.ToStored(), sound.Name + " (missing)");
}

/// <summary>A volume setting in the list. A screen reader reads the item from <see cref="ToString"/>.</summary>
public sealed record SoundVolumeOption(int Percent)
{
    public string Label => $"{Percent}%";

    public override string ToString() => Label;
}

/// <summary>One event in the Sounds tab's list, with the sound chosen for it.</summary>
public sealed partial class SoundEventRow(SoundEventInfo info, SoundChoice choice) : ObservableObject
{
    public SoundEventInfo Info { get; } = info;

    public string Label => Info.Label;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    private SoundChoice _choice = choice;

    /// <summary>What the row says: the event and its sound, so arrowing the list reads what's set.</summary>
    public string AccessibleName => $"{Label}: {Choice.Label}";

    public override string ToString() => AccessibleName;
}

public partial class SettingsViewModel
{
    private ISoundService? _sounds;

    [ObservableProperty] private bool _playEventSounds;
    [ObservableProperty] private bool _muteSounds;
    [ObservableProperty] private SoundVolumeOption? _selectedSoundVolume;
    [ObservableProperty] private SoundEventRow? _selectedSoundEvent;
    [ObservableProperty] private SoundChoice? _selectedSoundChoice;

    /// <summary>Why the last chosen file was refused, or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSoundFileMessage))]
    private string _soundFileMessage = string.Empty;

    public bool HasSoundFileMessage => SoundFileMessage.Length > 0;

    public ObservableCollection<SoundVolumeOption> SoundVolumeOptions { get; } = [];
    public ObservableCollection<SoundEventRow> SoundEventRows { get; } = [];
    public ObservableCollection<SoundChoice> SoundChoices { get; } = [];

    /// <summary>Set by the View: asks for a sound file and returns its path, or null if cancelled.</summary>
    public Func<string?>? PickSoundFileRequested { get; set; }

    /// <summary>A chosen file was refused; the View announces the reason.</summary>
    public event Action<string>? SoundFileRefused;

    private void LoadSoundSettings(ConfigModel cfg, ISoundService? sounds)
    {
        _sounds = sounds;
        PlayEventSounds = cfg.PlayEventSounds;
        MuteSounds = cfg.MuteSounds;

        for (var percent = 10; percent <= 100; percent += 10)
            SoundVolumeOptions.Add(new SoundVolumeOption(percent));
        if (SoundVolumeOptions.All(o => o.Percent != cfg.SoundVolume))
            InsertVolumeInOrder(cfg.SoundVolume);   // A value set by hand in config.ini, so saving doesn't change it.
        SelectedSoundVolume = SoundVolumeOptions.First(o => o.Percent == cfg.SoundVolume);

        SoundChoices.Add(new SoundChoice(SoundRef.None.ToStored(), "None"));
        foreach (var choice in SoundChoiceList.BuiltIn())
            SoundChoices.Add(choice);
        foreach (var file in sounds?.CustomSoundFiles() ?? [])
            SoundChoices.Add(new SoundChoice(SoundRef.Custom(file).ToStored(), file));

        foreach (var info in SoundCatalog.Events)
            SoundEventRows.Add(new SoundEventRow(info, ChoiceFor(SoundCatalog.Resolve(info.Event, cfg.SoundEvents))));
        SelectedSoundEvent = SoundEventRows[0];
    }

    private void InsertVolumeInOrder(int percent)
    {
        var index = SoundVolumeOptions.TakeWhile(o => o.Percent < percent).Count();
        SoundVolumeOptions.Insert(index, new SoundVolumeOption(percent));
    }

    /// <summary>The list's entry for a sound. A custom one whose file has gone stays listed, so it isn't silently changed.</summary>
    private SoundChoice ChoiceFor(SoundRef sound)
    {
        var stored = sound.ToStored();
        var found = SoundChoices.FirstOrDefault(c => c.Stored == stored);
        if (found is not null) return found;
        var missing = SoundChoiceList.Missing(sound);
        SoundChoices.Add(missing);
        return missing;
    }

    private void SaveSoundSettings(ConfigModel cfg)
    {
        cfg.PlayEventSounds = PlayEventSounds;
        cfg.MuteSounds = MuteSounds;
        if (SelectedSoundVolume is not null) cfg.SoundVolume = SelectedSoundVolume.Percent;

        // A new dictionary rather than changing the one in use: the sound worker reads it from another
        // thread. Only what differs from the default is kept, as [sounds] records only what was changed.
        var none = new System.Collections.Generic.Dictionary<string, string>();
        var changed = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // An entry no row owns came from a newer QuickMail; saving on any tab shouldn't erase it.
        foreach (var (key, value) in cfg.SoundEvents)
        {
            if (SoundEventRows.All(r => !r.Info.ConfigKey.Equals(key, StringComparison.OrdinalIgnoreCase)))
                changed[key] = value;
        }

        foreach (var row in SoundEventRows)
        {
            if (row.Choice.Stored != SoundCatalog.Resolve(row.Info.Event, none).ToStored())
                changed[row.Info.ConfigKey] = row.Choice.Stored;
        }
        cfg.SoundEvents = changed;
    }

    partial void OnSelectedSoundEventChanged(SoundEventRow? value)
    {
        if (value is not null) SelectedSoundChoice = value.Choice;
        SoundFileMessage = string.Empty;
    }

    partial void OnSelectedSoundChoiceChanged(SoundChoice? value)
    {
        // Null while the combo box is rebuilt; that isn't anyone choosing no sound, which is the "None" entry.
        if (value is not null && SelectedSoundEvent is not null) SelectedSoundEvent.Choice = value;
    }

    [RelayCommand]
    private void PreviewSound()
    {
        // At the volume showing, which may not be saved yet: that's how someone tries a volume out.
        if (SelectedSoundChoice is not null) _sounds?.Preview(SelectedSoundChoice.Stored, SelectedSoundVolume?.Percent);
    }

    /// <summary>Ends a preview when Settings closes, so it doesn't play on over speech afterward.</summary>
    public void StopSounds() => _sounds?.StopAll();

    [RelayCommand]
    private void ChooseSoundFile()
    {
        if (_sounds is null || PickSoundFileRequested?.Invoke() is not { } path) return;

        if (!_sounds.TryAddCustomSound(path, out var fileName, out var error))
        {
            SoundFileMessage = error;
            SoundFileRefused?.Invoke(error);
            return;
        }

        SoundFileMessage = string.Empty;
        var sound = SoundRef.Custom(fileName);
        var stored = sound.ToStored();
        var choice = new SoundChoice(stored, fileName);
        var existing = SoundChoices.FirstOrDefault(c => c.Stored == stored);
        if (existing is null)
        {
            SoundChoices.Add(choice);
        }
        else if (existing == choice)
        {
            choice = existing;
        }
        else
        {
            // It was listed as missing, and choosing the file again has just repaired it.
            SoundChoices[SoundChoices.IndexOf(existing)] = choice;
            foreach (var row in SoundEventRows.Where(r => r.Choice == existing))
                row.Choice = choice;
        }
        SelectedSoundChoice = choice;
    }
}

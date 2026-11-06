using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
    /// <summary>What the rule editor calls a rule's default sound, which is the scheme's "A rule matched".</summary>
    public const string DefaultRuleSoundLabel = "Default sound";

    /// <summary>Every built-in sound, playing in whatever pack the scheme in use has.</summary>
    public static SoundChoice[] BuiltIn() =>
    [
        .. SoundCatalog.BuiltInSounds.Order(StringComparer.Ordinal)
            .Select(name => new SoundChoice(SoundRef.BuiltIn(name).ToStored(), SoundCatalog.BuiltInLabel(name))),
    ];

    /// <summary>Every built-in sound in every pack, grouped by pack: "Arrived (Glass)", for a scheme of the person's own (#932).</summary>
    public static SoundChoice[] BuiltInEveryPack() =>
    [
        .. SoundCatalog.Packs.SelectMany(pack => SoundCatalog.BuiltInSounds.Order(StringComparer.Ordinal)
            .Select(name => SoundRef.BuiltIn(name, pack.Id))
            .Select(sound => new SoundChoice(sound.ToStored(), sound.DisplayName))),
    ];

    /// <summary>
    /// The rule editor's built-in sounds. The rule sound comes first as the default, since a rule set to
    /// it plays whatever the scheme in use has for "A rule matched" (#932).
    /// </summary>
    public static SoundChoice[] ForRules() =>
    [
        new(SoundRef.BuiltIn(SoundCatalog.DefaultRuleSound).ToStored(), DefaultRuleSoundLabel),
        .. BuiltIn().Where(c => c.Stored != SoundRef.BuiltIn(SoundCatalog.DefaultRuleSound).ToStored()),
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
    private bool _previewedSound;

    // Sound schemes (#932). The schemes as config.ini had them; the scheme shown when Settings opened,
    // which differs from config's id when that id is one this build doesn't have; and, for every
    // scheme shown since, each event's stored sound as edited. Edits to a scheme someone switches away
    // from stay here until Save or Cancel, so switching back and forth loses nothing.
    private Dictionary<string, SoundScheme> _storedSchemes = [];
    private string _schemeShownAtLoad = SoundCatalog.DefaultPack;
    private string? _shownSchemeId;
    private readonly Dictionary<string, Dictionary<string, string>> _schemeValues = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty] private bool _playEventSounds;
    [ObservableProperty] private bool _muteSounds;
    [ObservableProperty] private SoundVolumeOption? _selectedSoundVolume;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUserSchemeSelected))]
    private SoundScheme? _selectedSoundScheme;

    [ObservableProperty] private SoundEventRow? _selectedSoundEvent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemoveSoundFile))]
    private SoundChoice? _selectedSoundChoice;

    /// <summary>Why the last chosen file was refused, or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSoundFileMessage))]
    private string _soundFileMessage = string.Empty;

    /// <summary>What the last import or export of a scheme did, or why it couldn't.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSoundSchemeMessage))]
    private string _soundSchemeMessage = string.Empty;

    public bool HasSoundFileMessage => SoundFileMessage.Length > 0;

    public bool HasSoundSchemeMessage => SoundSchemeMessage.Length > 0;

    public ObservableCollection<SoundVolumeOption> SoundVolumeOptions { get; } = [];

    /// <summary>The built-in schemes, then the person's own by name.</summary>
    public ObservableCollection<SoundScheme> SoundSchemes { get; } = [];

    public ObservableCollection<SoundEventRow> SoundEventRows { get; } = [];
    public ObservableCollection<SoundChoice> SoundChoices { get; } = [];

    /// <summary>Rename and Delete apply only to a scheme of the person's own.</summary>
    public bool IsUserSchemeSelected => SelectedSoundScheme is { IsBuiltIn: false };

    /// <summary>Reset applies to a built-in scheme that differs from how it ships.</summary>
    public bool CanResetScheme =>
        SelectedSoundScheme is { IsBuiltIn: true }
        && SoundEventRows.Any(r => r.Choice.Stored != SoundRef.BuiltIn(r.Info.BuiltInSound).ToStored());

    /// <summary>How many events in the scheme shown make a sound, such as "4 of 14 events have a sound".</summary>
    public string SchemeSoundCount
    {
        get
        {
            var withSound = SoundEventRows.Count(r => r.Choice.Stored != SoundRef.None.ToStored());
            return $"{withSound} of {SoundEventRows.Count} events have a sound";
        }
    }

    /// <summary>Remove file applies to a custom sound whose file is in the folder.</summary>
    public bool CanRemoveSoundFile =>
        _sounds is not null && SelectedSoundChoice is { } choice
        && SoundRef.TryParse(choice.Stored, out var sound) && sound.Kind == SoundRefKind.Custom
        && _sounds.CustomSoundPath(sound.Name) is not null;

    /// <summary>Set by the View: asks for a sound file and returns its path, or null if cancelled.</summary>
    public Func<string?>? PickSoundFileRequested { get; set; }

    /// <summary>Set by the View: asks where to save a scheme, given a suggested file name. Null if cancelled.</summary>
    public Func<string, string?>? PickSchemeExportPathRequested { get; set; }

    /// <summary>Set by the View: asks for a scheme file to import. Null if cancelled.</summary>
    public Func<string?>? PickSchemeImportPathRequested { get; set; }

    /// <summary>Set by the View: asks a yes-or-no question and returns true for Yes.</summary>
    public Func<string, string, bool>? ConfirmRequested { get; set; }

    /// <summary>Set by the owner: the stored sound of every rule that plays one, for Remove file's warning.</summary>
    public Func<IEnumerable<string?>>? RuleSoundsProvider { get; set; }

    /// <summary>The outcome of a sound action that no control reports; the View announces it as a Result.</summary>
    public event Action<string>? SoundResultAnnounced;

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

        _storedSchemes = cfg.SoundSchemes;
        foreach (var pack in SoundCatalog.Packs)
            SoundSchemes.Add(cfg.FindSoundScheme(pack.Id)!);
        foreach (var own in cfg.SoundSchemes.Values.Where(s => SoundCatalog.IsUserSchemeId(s.Id)))
            InsertUserScheme(own);

        // A scheme this build doesn't have, from a newer one or a hand edit, plays and shows as Glass.
        var shown = SoundSchemes.FirstOrDefault(s => s.Id.Equals(cfg.SoundSchemeId, StringComparison.OrdinalIgnoreCase))
            ?? SoundSchemes.First(s => s.Id == SoundCatalog.DefaultPack);
        _schemeShownAtLoad = shown.Id;
        SelectedSoundScheme = shown;
    }

    private void InsertVolumeInOrder(int percent)
    {
        var index = SoundVolumeOptions.TakeWhile(o => o.Percent < percent).Count();
        SoundVolumeOptions.Insert(index, new SoundVolumeOption(percent));
    }

    /// <summary>Adds a scheme of the person's own after the built-in ones, in order of name.</summary>
    private void InsertUserScheme(SoundScheme scheme)
    {
        var index = SoundSchemes.TakeWhile(s => s.IsBuiltIn
            || string.Compare(s.Name, scheme.Name, StringComparison.OrdinalIgnoreCase) <= 0).Count();
        SoundSchemes.Insert(index, scheme);
    }

    partial void OnSelectedSoundSchemeChanged(SoundScheme? value)
    {
        // Null while the combo box is rebuilt, or an item renamed; that isn't a choice of scheme.
        if (value is null || value.Id == _shownSchemeId) return;
        CaptureSchemeRows();
        ShowScheme(value);
    }

    /// <summary>Keeps the edits to the scheme showing, so they survive switching to another.</summary>
    private void CaptureSchemeRows()
    {
        if (_shownSchemeId is null || SoundEventRows.Count == 0) return;
        _schemeValues[_shownSchemeId] = SoundEventRows.ToDictionary(r => r.Info.ConfigKey, r => r.Choice.Stored, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The stored sound of every event in a scheme, as edited if it has been shown.</summary>
    private Dictionary<string, string> SchemeValues(SoundScheme scheme)
    {
        if (scheme.Id == _shownSchemeId) CaptureSchemeRows();
        if (_schemeValues.TryGetValue(scheme.Id, out var edited)) return edited;
        return SoundCatalog.Events.ToDictionary(i => i.ConfigKey, i => SoundCatalog.Resolve(i.Event, scheme).ToStored(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Fills the event list, and the sounds to choose from, for a scheme.</summary>
    private void ShowScheme(SoundScheme scheme)
    {
        var values = SchemeValues(scheme);
        var eventIndex = SelectedSoundEvent is { } current ? SoundEventRows.IndexOf(current) : 0;
        _shownSchemeId = scheme.Id;

        // A scheme of the person's own has no pack, so it offers every built-in sound in every pack.
        SoundChoices.Clear();
        SoundChoices.Add(new SoundChoice(SoundRef.None.ToStored(), "None"));
        foreach (var choice in scheme.IsBuiltIn ? SoundChoiceList.BuiltIn() : SoundChoiceList.BuiltInEveryPack())
            SoundChoices.Add(choice);
        foreach (var file in _sounds?.CustomSoundFiles() ?? [])
            SoundChoices.Add(new SoundChoice(SoundRef.Custom(file).ToStored(), file));

        foreach (var row in SoundEventRows) row.PropertyChanged -= OnSoundEventRowChanged;
        SoundEventRows.Clear();
        foreach (var info in SoundCatalog.Events)
        {
            var stored = values.TryGetValue(info.ConfigKey, out var v) && SoundRef.TryParse(v, out var sound) ? sound : SoundRef.None;
            var row = new SoundEventRow(info, ChoiceFor(stored));
            row.PropertyChanged += OnSoundEventRowChanged;
            SoundEventRows.Add(row);
        }
        SelectedSoundEvent = SoundEventRows[Math.Clamp(eventIndex, 0, SoundEventRows.Count - 1)];
        SelectedSoundChoice = SelectedSoundEvent.Choice;   // the same row index may already have been selected
        OnSchemeRowsChanged();
    }

    private void OnSoundEventRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SoundEventRow.Choice)) OnSchemeRowsChanged();
    }

    private void OnSchemeRowsChanged()
    {
        OnPropertyChanged(nameof(CanResetScheme));
        OnPropertyChanged(nameof(SchemeSoundCount));
        OnPropertyChanged(nameof(CanRemoveSoundFile));
    }

    /// <summary>The list's entry for a sound. One not listed, such as a custom file that has gone, is added, so it isn't silently changed.</summary>
    private SoundChoice ChoiceFor(SoundRef sound)
    {
        var stored = sound.ToStored();
        var found = SoundChoices.FirstOrDefault(c => c.Stored == stored);
        if (found is not null) return found;
        var added = sound.Kind == SoundRefKind.Custom ? SoundChoiceList.Missing(sound) : new SoundChoice(stored, sound.DisplayName);
        SoundChoices.Add(added);
        return added;
    }

    private void SaveSoundSettings(ConfigModel cfg)
    {
        cfg.PlayEventSounds = PlayEventSounds;
        cfg.MuteSounds = MuteSounds;
        if (SelectedSoundVolume is not null) cfg.SoundVolume = SelectedSoundVolume.Percent;
        // Only if changed: a scheme this build doesn't have shows as Glass, and saving on another tab
        // shouldn't erase it, any more than an unknown section below.
        if (SelectedSoundScheme is not null && SelectedSoundScheme.Id != _schemeShownAtLoad) cfg.SoundSchemeId = SelectedSoundScheme.Id;

        // A new dictionary rather than changing the one in use: the sound worker reads it from another thread.
        var schemes = new Dictionary<string, SoundScheme>(StringComparer.OrdinalIgnoreCase);

        // A section no scheme here owns came from a newer QuickMail; saving on any tab shouldn't erase it.
        foreach (var (id, stored) in _storedSchemes)
        {
            if (!SoundCatalog.IsBuiltInSchemeId(id) && !SoundCatalog.IsUserSchemeId(id)) schemes[id] = stored;
        }

        foreach (var scheme in SoundSchemes)
        {
            _storedSchemes.TryGetValue(scheme.Id, out var original);
            if (scheme.Id != _shownSchemeId && !_schemeValues.ContainsKey(scheme.Id))
            {
                // Never shown, so unchanged, apart from perhaps its name.
                if (original is not null) schemes[scheme.Id] = original.With(name: scheme.Name);
                continue;
            }

            var values = SchemeValues(scheme);
            var events = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // An entry no event owns came from a newer QuickMail, and stays.
            foreach (var (key, value) in original?.Events ?? [])
            {
                if (SoundCatalog.Events.All(i => !i.ConfigKey.Equals(key, StringComparison.OrdinalIgnoreCase))) events[key] = value;
            }
            foreach (var info in SoundCatalog.Events)
            {
                var value = values.TryGetValue(info.ConfigKey, out var v) ? v : SoundRef.None.ToStored();
                // A built-in scheme records only what differs from how it ships; one of the person's own, everything.
                if (!scheme.IsBuiltIn || value != SoundRef.BuiltIn(info.BuiltInSound).ToStored()) events[info.ConfigKey] = value;
            }
            if (scheme.IsBuiltIn && events.Count == 0) continue;
            schemes[scheme.Id] = new SoundScheme(scheme.Id, scheme.Name, scheme.IsBuiltIn, scheme.Pack, events);
        }
        cfg.SoundSchemes = schemes;
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

    // ── Making, renaming and deleting schemes (#932) ─────────────────────────────

    /// <summary>Why a name can't be used for a scheme, or null if it can.</summary>
    /// <param name="name">The name typed.</param>
    /// <param name="renaming">The scheme being renamed, which may keep its own name; null for a new one.</param>
    public string? SchemeNameProblem(string? name, SoundScheme? renaming = null)
    {
        var text = (name ?? string.Empty).Trim();
        if (text.Length == 0) return "Type a name for the sound scheme.";
        if (text.Any(char.IsControl)) return "A scheme's name must be on one line.";
        if (text.Length > SoundCatalog.MaxSchemeNameLength)
            return $"That name is too long. Use {SoundCatalog.MaxSchemeNameLength} characters or fewer.";
        if (SoundSchemes.Any(s => s != renaming && s.Name.Equals(text, StringComparison.OrdinalIgnoreCase)))
            return $"There is already a sound scheme called {text}. Choose another name.";
        return null;
    }

    /// <summary>
    /// Makes a scheme of the person's own and shows it. A new one starts with every event at None; a
    /// copy starts from the sounds of the scheme showing, each built-in sound pinned to the pack it
    /// played in there, so the copy sounds the same.
    /// </summary>
    /// <returns>False if the name can't be used; <see cref="SchemeNameProblem"/> says why.</returns>
    public bool CreateScheme(string name, bool copyShown)
    {
        if (SchemeNameProblem(name) is not null) return false;
        var scheme = new SoundScheme(SoundCatalog.NewUserSchemeId(), name.Trim(), isBuiltIn: false);
        _schemeValues[scheme.Id] = copyShown && SelectedSoundScheme is { } source
            ? SoundSchemeFile.Portable(source.With(events: SchemeValues(source)))
            : SoundCatalog.Events.ToDictionary(i => i.ConfigKey, _ => SoundRef.None.ToStored(), StringComparer.OrdinalIgnoreCase);
        InsertUserScheme(scheme);
        SelectedSoundScheme = scheme;
        return true;
    }

    /// <summary>Renames the scheme showing, which must be one of the person's own.</summary>
    /// <returns>False if the name can't be used, or the scheme isn't one that can be renamed.</returns>
    public bool RenameSelectedScheme(string name)
    {
        if (SelectedSoundScheme is not { IsBuiltIn: false } scheme || SchemeNameProblem(name, scheme) is not null) return false;
        CaptureSchemeRows();
        var renamed = scheme.With(name: name.Trim());
        // Same id, so OnSelectedSoundSchemeChanged leaves the rows alone; re-placed so the list stays in order.
        SoundSchemes.Remove(scheme);
        InsertUserScheme(renamed);
        SelectedSoundScheme = renamed;
        return true;
    }

    /// <summary>Deletes the scheme showing, after asking. Its sound files stay: other schemes or rules may use them.</summary>
    /// <returns>True if it was deleted, so the View can put focus on Sound scheme.</returns>
    public bool DeleteSelectedScheme()
    {
        if (SelectedSoundScheme is not { IsBuiltIn: false } scheme) return false;
        if (ConfirmRequested?.Invoke($"Delete the sound scheme {scheme.Name}?", "Delete Sound Scheme") != true) return false;
        _schemeValues.Remove(scheme.Id);
        _shownSchemeId = null;   // nothing to keep from the rows of a scheme that's gone
        SoundSchemes.Remove(scheme);
        SelectedSoundScheme = SoundSchemes.First(s => s.Id == SoundCatalog.DefaultPack);
        return true;
    }

    /// <summary>Puts the built-in scheme showing back to how it ships, after asking.</summary>
    [RelayCommand]
    private void ResetScheme()
    {
        if (!CanResetScheme || SelectedSoundScheme is not { } scheme) return;
        if (ConfirmRequested?.Invoke($"Reset {scheme.Name} to how it ships? Every event goes back to its built-in sound.", "Reset Sound Scheme") != true) return;
        foreach (var row in SoundEventRows)
            row.Choice = ChoiceFor(SoundRef.BuiltIn(row.Info.BuiltInSound));
        if (SelectedSoundEvent is not null) SelectedSoundChoice = SelectedSoundEvent.Choice;
    }

    // ── Sharing a scheme (#932) ─────────────────────────────────────────────────

    [RelayCommand]
    private void ExportScheme()
    {
        if (_sounds is null || SelectedSoundScheme is not { } scheme) return;
        var safeName = string.Concat(scheme.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (PickSchemeExportPathRequested?.Invoke(safeName + SoundSchemeFile.FileExtension) is not { } path) return;
        try
        {
            SoundSchemeFile.Export(path, scheme.Name, SoundSchemeFile.Portable(scheme.With(events: SchemeValues(scheme))), _sounds);
            ReportScheme($"Saved the sound scheme {scheme.Name} to {Path.GetFileName(path)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Log("Exporting a sound scheme failed", ex);
            ReportScheme($"The sound scheme could not be saved. {ex.Message}");
        }
    }

    [RelayCommand]
    private void ImportScheme()
    {
        if (_sounds is null || PickSchemeImportPathRequested?.Invoke() is not { } path) return;
        SoundSchemeImport imported;
        try
        {
            imported = SoundSchemeFile.Import(path, _sounds);
        }
        catch (SoundSchemeFileException ex)
        {
            LogService.Log("Importing a sound scheme failed", ex);
            ReportScheme(ex.Message);
            return;
        }

        // Numbered rather than refused: the name is the file's, not one the person typed.
        var name = imported.Name;
        for (var n = 2; SchemeNameProblem(name) is not null; n++)
        {
            var suffix = $" {n}";
            var stem = imported.Name.Length + suffix.Length > SoundCatalog.MaxSchemeNameLength
                ? imported.Name[..(SoundCatalog.MaxSchemeNameLength - suffix.Length)].TrimEnd()
                : imported.Name;
            name = stem + suffix;
        }

        var scheme = new SoundScheme(SoundCatalog.NewUserSchemeId(), name, isBuiltIn: false);
        _schemeValues[scheme.Id] = SoundCatalog.Events.ToDictionary(
            i => i.ConfigKey,
            i => imported.Events.TryGetValue(i.ConfigKey, out var v) ? v : SoundRef.None.ToStored(),
            StringComparer.OrdinalIgnoreCase);
        InsertUserScheme(scheme);
        SelectedSoundScheme = scheme;

        var message = $"Added the sound scheme {name}";
        if (imported.SoundsAdded > 0) message += $", with {Plural(imported.SoundsAdded, "sound file")}";
        message += ".";
        if (imported.SoundsDropped > 0)
            message += $" {Plural(imported.SoundsDropped, "event")} had a sound file that was missing or could not be played, and now {(imported.SoundsDropped == 1 ? "has" : "have")} no sound.";
        ReportScheme(message + " Save to keep it.");
    }

    private void ReportScheme(string message)
    {
        SoundSchemeMessage = message;
        SoundResultAnnounced?.Invoke(message);
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    // ── Sounds for one event ──────────────────────────────────────────────────────

    [RelayCommand]
    private void PreviewSound()
    {
        // At the volume and in the scheme showing, which may not be saved yet: that's how someone tries them out.
        if (SelectedSoundChoice is null) return;
        _previewedSound = true;
        _sounds?.Preview(SelectedSoundChoice.Stored, SelectedSoundVolume?.Percent, SelectedSoundScheme?.Pack ?? SoundCatalog.DefaultPack);
    }

    /// <summary>Ends a preview when Settings closes, so it doesn't play on over speech afterward.</summary>
    public void StopSounds()
    {
        // Without a preview, a sound for new mail just starting isn't this dialog's to stop.
        if (_previewedSound) _sounds?.StopAll();
    }

    [RelayCommand]
    private void ChooseSoundFile() => TryChooseSoundFile();

    /// <summary>Asks for a sound file and gives it to the event showing.</summary>
    /// <returns>True if one was chosen and accepted, so the View can put focus where its name is read.</returns>
    public bool TryChooseSoundFile()
    {
        if (_sounds is null || PickSoundFileRequested?.Invoke() is not { } path) return false;

        if (!_sounds.TryAddCustomSound(path, out var fileName, out var error))
        {
            SoundFileMessage = error;
            SoundResultAnnounced?.Invoke(error);
            return false;
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
        return true;
    }

    /// <summary>What still names a custom sound file: the schemes, by name, and how many rules.</summary>
    internal (IReadOnlyList<string> Schemes, int Rules) SoundFileUses(string fileName)
    {
        var stored = SoundRef.Custom(fileName).ToStored();
        bool Names(string? value) => SoundRef.TryParse(value, out var s) && s.ToStored().Equals(stored, StringComparison.OrdinalIgnoreCase);

        var schemes = SoundSchemes.Where(s => SchemeValues(s).Values.Any(Names)).Select(s => s.Name).ToList();
        var rules = RuleSoundsProvider?.Invoke().Count(Names) ?? 0;
        return (schemes, rules);
    }

    /// <summary>
    /// Deletes the custom sound file showing, after asking, and says what still uses it. Those uses
    /// stay, listed as missing, and play their fallback, as for any file that has gone.
    /// </summary>
    [RelayCommand]
    private void RemoveSoundFile()
    {
        if (!CanRemoveSoundFile || _sounds is null || SelectedSoundChoice is not { } choice
            || !SoundRef.TryParse(choice.Stored, out var sound)) return;

        var (schemes, rules) = SoundFileUses(sound.Name);
        var uses = new List<string>();
        if (schemes.Count > 0)
            uses.Add((schemes.Count == 1 ? "the sound scheme " : "the sound schemes ") + JoinNames(schemes));
        if (rules > 0) uses.Add(Plural(rules, "rule"));
        var question = $"Remove the sound file {sound.Name} from QuickMail?"
            + (uses.Count == 0 ? " Nothing uses it."
                : $" It is still used by {string.Join(", and by ", uses)}, which will play a built-in sound instead.");
        if (ConfirmRequested?.Invoke(question, "Remove Sound File") != true) return;

        if (!_sounds.TryRemoveCustomSound(sound.Name, out var refusal))
        {
            SoundFileMessage = refusal;
            SoundResultAnnounced?.Invoke(refusal);
            return;
        }

        SoundFileMessage = string.Empty;
        var missing = SoundChoiceList.Missing(sound);
        SoundChoices[SoundChoices.IndexOf(choice)] = missing;
        foreach (var row in SoundEventRows.Where(r => r.Choice == choice))
            row.Choice = missing;
        if (SelectedSoundEvent is not null) SelectedSoundChoice = SelectedSoundEvent.Choice;
        OnPropertyChanged(nameof(CanRemoveSoundFile));
    }

    private static string JoinNames(IReadOnlyList<string> names) => names.Count switch
    {
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        _ => string.Join(", ", names.Take(names.Count - 1)) + ", and " + names[^1],
    };
}

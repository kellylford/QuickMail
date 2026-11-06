using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

/// <summary>The "Play a sound" action (#912). A client-only action, so it's off while editing a server rule.</summary>
public partial class ServerRuleEditorViewModel
{
    private ISoundService? _sounds;
    private bool _previewedSound;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlaySoundSelected))]
    private bool _playSound;

    [ObservableProperty] private SoundChoice? _selectedRuleSound = DefaultRuleChoice();

    /// <summary>Why the last chosen file was refused, or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRuleSoundMessage))]
    private string _ruleSoundMessage = string.Empty;

    public bool HasRuleSoundMessage => RuleSoundMessage.Length > 0;

    /// <summary>A server rule runs on the server, where there is nothing to play a sound. As with Mark as unread, a new rule keeps it.</summary>
    public bool CanPlaySound => !IsEditingServerRule;

    /// <summary>Whether the sound's own controls are available. A disabled control drops out of the Tab order.</summary>
    public bool IsPlaySoundSelected => PlaySound && CanPlaySound;

    public ObservableCollection<SoundChoice> RuleSoundChoices { get; } = [.. SoundChoiceList.ForRules()];

    /// <summary>Set by the View: asks for a sound file and returns its path, or null if cancelled.</summary>
    public Func<string?>? PickSoundFileRequested { get; set; }

    private static SoundChoice DefaultRuleChoice() => SoundChoiceList.ForRules()
        .First(c => c.Stored == SoundRef.BuiltIn(SoundCatalog.DefaultRuleSound).ToStored());

    /// <summary>Gives the editor what it needs to list custom sounds, preview them and add new ones. Set by the owner.</summary>
    internal void AttachSounds(ISoundService? sounds)
    {
        _sounds = sounds;
        foreach (var file in sounds?.CustomSoundFiles() ?? [])
        {
            var stored = SoundRef.Custom(file).ToStored();
            var existing = RuleSoundChoices.FirstOrDefault(c => c.Stored == stored);
            var choice = new SoundChoice(stored, file);
            if (existing is null)
            {
                RuleSoundChoices.Add(choice);
            }
            else if (existing != choice)
            {
                // ShowRuleSound listed it as missing before this could look in the folder.
                RuleSoundChoices[RuleSoundChoices.IndexOf(existing)] = choice;
                if (SelectedRuleSound == existing) SelectedRuleSound = choice;
            }
        }
    }

    partial void OnPlaySoundChanged(bool value) => RuleSoundMessage = string.Empty;

    partial void OnSelectedRuleSoundChanged(SoundChoice? value) => RuleSoundMessage = string.Empty;

    /// <summary>Selects a rule's stored sound. One that can't be read shows as the default, which is what it would play.</summary>
    private void ShowRuleSound(string? stored)
    {
        if (!SoundRef.TryParse(stored, out var sound) || sound.Kind == SoundRefKind.None) return;
        var value = sound.ToStored();
        var choice = RuleSoundChoices.FirstOrDefault(c => c.Stored == value);
        if (choice is null)
        {
            choice = SoundChoiceList.Missing(sound);
            RuleSoundChoices.Add(choice);
        }
        SelectedRuleSound = choice;
    }

    /// <summary>Ends this editor's preview when it closes. Without one, a sound for new mail just starting isn't its to stop.</summary>
    public void StopSounds()
    {
        if (_previewedSound) _sounds?.StopAll();
    }

    [RelayCommand]
    private void PreviewRuleSound()
    {
        if (SelectedRuleSound is null) return;
        _previewedSound = true;
        _sounds?.Preview(SelectedRuleSound.Stored);
    }

    [RelayCommand]
    private void ChooseRuleSoundFile() => TryChooseRuleSoundFile();

    /// <summary>Asks for a sound file and makes it the rule's sound.</summary>
    /// <returns>True if one was chosen and accepted, so the View can put focus where its name is read.</returns>
    public bool TryChooseRuleSoundFile()
    {
        if (_sounds is null || PickSoundFileRequested?.Invoke() is not { } path) return false;

        if (!_sounds.TryAddCustomSound(path, out var fileName, out var refusal))
        {
            RuleSoundMessage = refusal;
            AnnouncementRequested?.Invoke(refusal, AnnouncementCategory.Result);
            return false;
        }

        var stored = SoundRef.Custom(fileName).ToStored();
        var choice = new SoundChoice(stored, fileName);
        var existing = RuleSoundChoices.FirstOrDefault(c => c.Stored == stored);
        if (existing is null) RuleSoundChoices.Add(choice);
        else if (existing != choice) RuleSoundChoices[RuleSoundChoices.IndexOf(existing)] = choice;
        else choice = existing;
        SelectedRuleSound = choice;
        return true;
    }
}

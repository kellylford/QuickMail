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

    public ObservableCollection<SoundChoice> RuleSoundChoices { get; } = [.. SoundChoiceList.BuiltIn()];

    /// <summary>Set by the View: asks for a sound file and returns its path, or null if cancelled.</summary>
    public Func<string?>? PickSoundFileRequested { get; set; }

    private static SoundChoice DefaultRuleChoice() => SoundChoiceList.BuiltIn()
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

    /// <summary>Ends a preview when the editor closes.</summary>
    public void StopSounds() => _sounds?.StopAll();

    [RelayCommand]
    private void PreviewRuleSound()
    {
        if (SelectedRuleSound is not null) _sounds?.Preview(SelectedRuleSound.Stored);
    }

    [RelayCommand]
    private void ChooseRuleSoundFile()
    {
        if (_sounds is null || PickSoundFileRequested?.Invoke() is not { } path) return;

        if (!_sounds.TryAddCustomSound(path, out var fileName, out var refusal))
        {
            RuleSoundMessage = refusal;
            AnnouncementRequested?.Invoke(refusal, AnnouncementCategory.Result);
            return;
        }

        RuleSoundMessage = string.Empty;
        var stored = SoundRef.Custom(fileName).ToStored();
        var choice = new SoundChoice(stored, fileName);
        var existing = RuleSoundChoices.FirstOrDefault(c => c.Stored == stored);
        if (existing is null) RuleSoundChoices.Add(choice);
        else if (existing != choice) RuleSoundChoices[RuleSoundChoices.IndexOf(existing)] = choice;
        else choice = existing;
        SelectedRuleSound = choice;
    }
}

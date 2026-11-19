using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Services;

namespace QuickMail.ViewModels;

// Settings → Composing → "Remember people I send to" (#1037).
public partial class SettingsViewModel
{
    /// <summary>
    /// Remember the people messages are sent to and suggest them while typing an address. Off by
    /// default. Saved with the rest of the settings; the store itself is <see cref="RecipientHistory"/>.
    /// </summary>
    [ObservableProperty]
    private bool _rememberRecipients;

    /// <summary>The master switch for address suggestions; the two sources below have no effect while it is off.</summary>
    [ObservableProperty]
    private bool _suggestAddresses;

    [ObservableProperty]
    private bool _suggestFromAddressBook;

    [ObservableProperty]
    private bool _suggestPeopleEmailed;

    /// <summary>Where remembered people are kept. Null where there is none (tests), which hides Forget.</summary>
    public IRecipientHistory? RecipientHistory
    {
        get => _recipientHistory;
        set
        {
            if (SetProperty(ref _recipientHistory, value))
                OnPropertyChanged(nameof(HasRecipientHistory));
        }
    }
    private IRecipientHistory? _recipientHistory;

    public bool HasRecipientHistory => _recipientHistory is not null;

    /// <summary>The outcome of Forget, which no control reports; the View announces it as a Result.</summary>
    public event Action<string>? RecipientResultAnnounced;

    /// <summary>
    /// Forgets everyone remembered, after asking. Takes effect at once rather than on Save: it is an
    /// action, like deleting a sound scheme, not a setting. The address book is not touched.
    /// </summary>
    [RelayCommand]
    private async Task ForgetRecipientsAsync()
    {
        if (_recipientHistory is null) return;
        var count = await _recipientHistory.CountAsync();
        if (count == 0)
        {
            RecipientResultAnnounced?.Invoke("No one is remembered.");
            return;
        }
        var people = count == 1 ? "the 1 person" : $"all {count} people";
        if (ConfirmRequested?.Invoke(
                $"Forget {people} QuickMail remembered sending to? Your address book is not changed.",
                "Forget People I've Sent To") != true)
            return;
        try
        {
            await _recipientHistory.ClearAsync();
            RecipientResultAnnounced?.Invoke(count == 1 ? "Forgot 1 person." : $"Forgot {count} people.");
        }
        catch (Exception ex)
        {
            LogService.Log("Settings: could not forget remembered recipients", ex);
            RecipientResultAnnounced?.Invoke("Couldn't forget the people you've sent to. " + ex.Message);
        }
    }
}

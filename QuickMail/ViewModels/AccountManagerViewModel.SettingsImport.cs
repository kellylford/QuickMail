using System.Collections.ObjectModel;
using System.Collections.Specialized;
using QuickMail.Models;

namespace QuickMail.ViewModels;

/// <summary>
/// Import Settings offered beside New on a new install (issue #507 follow-up): someone setting up
/// QuickMail on a second computer can bring everything across instead of adding accounts by hand.
/// Offered only while the new profile has no accounts, and without turning on
/// AllowSettingsTransfer: once the first account exists, or the import has restarted QuickMail,
/// the offer is gone and the File menu items stay behind that setting as before.
/// </summary>
public partial class AccountManagerViewModel
{
    /// <summary>Set by the main window when this profile is a new install.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    [CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(ShowImportSettings))]
    private bool _offerImportSettings;

    /// <summary>Drives the Import Settings button: a new install that has no account yet.</summary>
    public bool ShowImportSettings => OfferImportSettings && Accounts.Count == 0;

    /// <summary>True once the user chose Import Settings; the main window starts the import after the dialog closes.</summary>
    public bool ImportSettingsChosen { get; private set; }

    /// <summary>Records the choice. The View closes the dialog: the import's own windows must not open inside this one's modal loop.</summary>
    public void ChooseImportSettings()
    {
        if (ShowImportSettings) ImportSettingsChosen = true;
    }

    partial void OnAccountsChanged(ObservableCollection<AccountModel>? oldValue, ObservableCollection<AccountModel> newValue)
    {
        if (oldValue is not null) oldValue.CollectionChanged -= OnAccountsCollectionChanged;
        newValue.CollectionChanged += OnAccountsCollectionChanged;
        OnPropertyChanged(nameof(ShowImportSettings));
    }

    private void OnAccountsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        OnPropertyChanged(nameof(ShowImportSettings));
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

/// <summary>
/// The Settings Imported window, shown by the start that applied an import (issue #507, spec
/// Revision 2 R6): what came in, and the accounts that still need signing in. Passwords and
/// sign-ins never travel (Principle 1), so an account new to this computer cannot connect until
/// the user signs in; this is where that moment is handled, instead of as failed connections.
/// </summary>
public partial class SettingsImportedViewModel : ObservableObject, IDisposable
{
    private readonly ICredentialService _credentials;
    private readonly Func<AccountModel, Task<bool>> _reconnect;

    /// <summary>One account to sign in. Its accessible name says whether it still needs to.</summary>
    public sealed partial class SignInRow : ObservableObject, IDisposable
    {
        public SignInRow(AccountModel account)
        {
            Account = account;
            Account.PropertyChanged += OnAccountChanged;
        }

        public AccountModel Account { get; }

        public bool IsPasswordAccount => Account.AuthType == AuthType.Password;

        /// <summary>Set when a sign-in from this window worked. The account object can be replaced by a
        /// reload of the account list, so its own IsConnected is not the only word on it.</summary>
        public bool SignedIn { get => _signedIn; set { _signedIn = value; OnPropertyChanged(nameof(AccessibleName)); } }
        private bool _signedIn;

        public string AccessibleName => $"{Account.AccountLabel}, {(SignedIn || Account.IsConnected ? "signed in" : "needs sign-in")}";

        public override string ToString() => AccessibleName;

        private void OnAccountChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AccountModel.IsConnected))
                OnPropertyChanged(nameof(AccessibleName));
        }

        public void Dispose() => Account.PropertyChanged -= OnAccountChanged;
    }

    public SettingsImportedViewModel(
        SettingsImportResult result,
        IEnumerable<AccountModel> accounts,
        ICredentialService credentials,
        Func<AccountModel, Task<bool>> reconnect)
    {
        _credentials = credentials;
        _reconnect = reconnect;
        Report = result.Report;
        Succeeded = result.Succeeded;
        Title = result.Title;
        var byId = accounts.ToDictionary(a => a.Id);
        Rows = new ObservableCollection<SignInRow>(
            result.AccountsNeedingSignIn.Where(byId.ContainsKey).Select(id => new SignInRow(byId[id])));
        SelectedRow = Rows.FirstOrDefault();
    }

    public bool Succeeded { get; }

    /// <summary>The window title, which says how it went before the report is read.</summary>
    public string Title { get; }

    /// <summary>The import report, copyable.</summary>
    public string Report { get; }

    public ObservableCollection<SignInRow> Rows { get; }

    public bool HasRows => Rows.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordNeeded))]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    private SignInRow? _selectedRow;

    /// <summary>True when the selected account signs in with a password, so the password box applies.</summary>
    public bool PasswordNeeded => SelectedRow?.IsPasswordAccount == true;

    /// <summary>Set by the View from the PasswordBox, which cannot be bound.</summary>
    public string Password { get; set; } = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>Raised with the outcome of a sign-in, for the View to announce.</summary>
    public event Action<string, AnnouncementCategory>? AnnouncementRequested;

    /// <summary>Raised after a password has been used, so the View can clear the box.</summary>
    public event Action? PasswordConsumed;

    private bool CanSignIn() => SelectedRow is not null;

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        var row = SelectedRow;
        if (row is null) return;
        if (row.IsPasswordAccount)
        {
            if (string.IsNullOrEmpty(Password))
            {
                Say($"Enter the password for {row.Account.AccountLabel}.");
                return;
            }
            _credentials.SavePassword(row.Account.Id, Password);
            Password = string.Empty;
            PasswordConsumed?.Invoke();
        }

        StatusText = $"Signing in to {row.Account.AccountLabel}…";
        var connected = await _reconnect(row.Account);
        row.SignedIn = connected;
        // Shown, not spoken: Reconnect Account announces its own outcome, and saying it twice
        // would talk over it.
        StatusText = connected
            ? $"{row.Account.AccountLabel} signed in."
            : $"{row.Account.AccountLabel} did not sign in. Check the password, or try again.";
    }

    // The password box belongs to the selected account: moving to another clears it, so a password typed
    // for one account can never be saved under another.
    partial void OnSelectedRowChanged(SignInRow? value)
    {
        Password = string.Empty;
        PasswordConsumed?.Invoke();
    }

    private void Say(string text)
    {
        StatusText = text;
        AnnouncementRequested?.Invoke(text, AnnouncementCategory.Result);
    }

    public void Dispose()
    {
        foreach (var row in Rows) row.Dispose();
        GC.SuppressFinalize(this);
    }
}

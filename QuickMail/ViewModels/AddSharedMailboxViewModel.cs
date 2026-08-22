using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

/// <summary>
/// The add-shared-mailbox dialog (#31, spec §6 Path A). Collects an address and a parent account, and
/// on Add builds a credential-less shared <see cref="AccountModel"/> (Approach B) linked to that parent
/// — the owner persists it, registers its backend, and adds it to the account list. No backend access
/// happens here; validation is synchronous, so the dialog is a leaf single-form window (no F6/palette).
/// </summary>
public partial class AddSharedMailboxViewModel : ObservableObject
{
    private readonly List<AccountModel> _allAccounts;

    public AddSharedMailboxViewModel(IEnumerable<AccountModel> accounts, Guid? preferredParentId = null)
    {
        _allAccounts = accounts.ToList();

        // Only shared-capable accounts can host a shared mailbox: a work/school Microsoft 365 (Graph)
        // Only a WORK/SCHOOL Microsoft account can be a shared-mailbox parent — that is the sole place a
        // shared mailbox exists. It applies whether the account is on Graph (reads via /users/{shared})
        // or on IMAP-OAuth (Exchange IMAP, XOAUTH2 user={shared}), so the test is the Microsoft OAuth
        // auth type, not the backend. Everything else is excluded because it genuinely has no shared
        // mailbox to offer:
        //   - Personal Microsoft (Outlook.com/Hotmail/Live), on Graph OR IMAP: consumer accounts are not
        //     in an Exchange org and cannot be granted access to another mailbox. Resolved via
        //     ResolveIsPersonalMicrosoftAccount (flag, else domain guess), the same as scope selection.
        //   - Non-Microsoft IMAP (Gmail app-password, Yahoo, iCloud, Fastmail, "Other"): RFC 2342
        //     "shared folders" are namespace folders inside the user's own connection, not a delegated
        //     mailbox added by address — a different concept, deliberately out of scope (spec §4.2).
        //   - POP3, and a shared account itself, have no second mailbox at all.
        ParentOptions = _allAccounts
            .Where(a => !a.IsShared
                        && a.AuthType == AuthType.OAuth2Microsoft
                        && !OAuthService.ResolveIsPersonalMicrosoftAccount(a))
            .ToList();

        _selectedParent = ParentOptions.FirstOrDefault(a => a.Id == preferredParentId)
                          ?? ParentOptions.FirstOrDefault();

        // A shared mailbox reads through a parent account's token, so it needs one. Say so rather than
        // present an inert form with no parent and a disabled Add. The Account Manager also reads this
        // to decline opening the dialog at all and report it in its status line (the primary path).
        if (ParentOptions.Count == 0)
            _errorText = NoEligibleParentMessage;
    }

    /// <summary>Shown when no account can host a shared mailbox. The Account Manager reports this in its
    /// status line instead of opening a dead-end dialog; the dialog also carries it as a fallback.</summary>
    public const string NoEligibleParentMessage =
        "Add a Microsoft 365 work or school account first — a shared mailbox reads through one, and only work or school accounts have them.";

    public List<AccountModel> ParentOptions { get; }

    /// <summary>True when there is more than one parent to choose between (one needs no picker).</summary>
    public bool ShowParentSelector => ParentOptions.Count > 1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string _address = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGraphPollNote))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private AccountModel? _selectedParent;

    /// <summary>Graph shared mailboxes have no live watcher — only the #456 sweep. True for a Graph
    /// parent (shows the poll caption and drives its Hint announce); false for an IMAP parent (IDLE).</summary>
    public bool ShowGraphPollNote => SelectedParent?.BackendKind == BackendKind.MicrosoftGraph;

    /// <summary>The poll caption for a Graph parent. Shown visibly AND spoken once as a Hint when the
    /// dialog opens on a Graph parent — a screen-reader user would not otherwise discover static text.</summary>
#pragma warning disable CA1822 // instance property: bound via {Binding GraphPollNote} in the window XAML, which resolves against the DataContext instance
    public string GraphPollNote => "Shared mailboxes update every few minutes, not instantly.";
#pragma warning restore CA1822

    [ObservableProperty] private string _errorText = string.Empty;

    /// <summary>Raised when a valid shared mailbox is created. The owner persists it, registers its
    /// backend, adds it to the account list, and closes the dialog.</summary>
    public event Action<AccountModel>? SharedMailboxAdded;
    public event Action? CancelRequested;
    public event Action<string, AnnouncementCategory>? AnnouncementRequested;

    private bool CanAdd => !string.IsNullOrWhiteSpace(Address) && SelectedParent != null;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        var address = Address.Trim();
        if (SelectedParent is not { } parent) return;

        if (!LooksLikeEmail(address))
        {
            Fail("Enter a valid email address for the shared mailbox.");
            return;
        }
        if (_allAccounts.Any(a => AddressMatches(a, address)))
        {
            Fail("An account with that address already exists.");
            return;
        }

        SharedMailboxAdded?.Invoke(new AccountModel
        {
            AccountName     = address,
            Username        = address,
            SharedAddress   = address,
            IsShared        = true,
            ParentAccountId = parent.Id,
            BackendKind     = parent.BackendKind,   // access follows the parent's backend (§5.1)
            AuthType        = parent.AuthType,
            // Copied so SyncService can group by host in PR 1 (no access yet). This is a second copy of
            // the parent's state that would drift if the parent's host later changed — PR 2 (when access
            // is actually wired) should resolve the host from the parent at access time to match the
            // "reads through its parent" architecture, rather than persist a snapshot here. (review)
            ImapHost        = parent.ImapHost,
            ProviderId      = parent.ProviderId,
        });
    }

    [RelayCommand]
    private void Cancel() => CancelRequested?.Invoke();

    private void Fail(string message)
    {
        ErrorText = message;
        AnnouncementRequested?.Invoke(message, AnnouncementCategory.Result);
    }

    private static bool AddressMatches(AccountModel a, string address) =>
        string.Equals(a.Username, address, StringComparison.OrdinalIgnoreCase)
        || string.Equals(a.SharedAddress, address, StringComparison.OrdinalIgnoreCase);

    // Deliberately minimal: a non-empty local part, an "@", and a dotted domain. The real gate is the
    // backend rejecting a bad address on first access (PR 2); this only stops obvious typos here.
    private static bool LooksLikeEmail(string s)
    {
        var at = s.IndexOf('@');
        return at > 0 && at < s.Length - 1 && s.IndexOf('.', at) > at + 1 && !s.Contains(' ');
    }
}

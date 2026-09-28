using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

/// <summary>
/// Reconnect Account (#615): reconnect one disconnected account on request, without restarting the
/// app. Startup and the automatic reconnects never show a sign-in window (#206), so an account whose
/// sign-in has lapsed stays disconnected until the user asks; this is where they ask. A shared
/// mailbox (#31) reads through its parent's sign-in, so reconnecting one signs the parent in and
/// connects it first.
///
/// Everything here runs on the UI thread: the command is invoked there, and no await in this file
/// uses ConfigureAwait(false), so each continuation — ApplyAccountStatus, SetCachedFolders,
/// WireUpWatchers, all UI-thread-owned — resumes on the dispatcher.
/// </summary>
public partial class MainViewModel
{
    // Accounts with a reconnect in progress. UI-thread-owned. Also read by ReconnectOfflineAccountsAsync
    // so the automatic reconnect leaves these accounts alone.
    private readonly HashSet<Guid> _reconnecting = [];

    // Concurrent: reconnecting one account must not grey out the menu item for every other account
    // while a sign-in window is open. _reconnecting keeps a second request for the SAME account out.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ReconnectAccountAsync(AccountModel? requested)
    {
        requested ??= SelectedAccount;
        if (requested == null)
        {
            SetStatusEvenIfUnchanged("Select an account to reconnect.", AnnouncementCategory.Result);
            return;
        }

        var id = requested.Id;
        var label = requested.AccountLabel;
        if (requested.IsConnected)
        {
            SetStatusEvenIfUnchanged($"{label} is already connected.", AnnouncementCategory.Result);
            return;
        }
        if (!_reconnecting.Add(id))
        {
            SetStatusEvenIfUnchanged($"Already reconnecting {label}…", AnnouncementCategory.Status);
            return;
        }
        // A shared mailbox's parent is connected here too, so keep the automatic reconnect off it as
        // well. Only if it is not already being reconnected on its own — that request owns the id.
        var heldParentId = requested.IsShared && requested.ParentAccountId is { } pid && _reconnecting.Add(pid)
            ? pid : (Guid?)null;

        try
        {
            if (requested.IsShared && requested.BackendKind != BackendKind.MicrosoftGraph)
            {
                // Nothing else connects one of these (AccountsNeedingConnect skips them): a shared
                // mailbox is opened only through a Microsoft 365 account's Graph connection.
                SetStatusEvenIfUnchanged(
                    $"Could not reconnect {label}: a shared mailbox can only be opened through a Microsoft 365 account.",
                    AnnouncementCategory.Result);
                return;
            }

            AccountModel? parent = null;
            if (requested.IsShared)
            {
                parent = FindAccount(requested.ParentAccountId);
                if (parent == null)
                {
                    SetStatusEvenIfUnchanged(
                        $"Could not reconnect {label}: the account it belongs to is no longer in QuickMail.",
                        AnnouncementCategory.Result);
                    return;
                }
            }

            SetStatus($"Reconnecting {label}…", AnnouncementCategory.Status);

            // The sign-in that backs the connection: the shared mailbox's parent, or the account itself.
            var identity = parent ?? requested;
            if (identity.AuthType is AuthType.OAuth2Microsoft or AuthType.OAuth2Google
                && !await EnsureSignedInForReconnectAsync(identity))
                return;

            // The sign-in window may have been open a long time. Closing Manage Accounts meanwhile
            // rebuilds the account list with new objects, and the account may have been removed:
            // work on what is in the list now, never on an object the list no longer holds.
            var account = FindAccount(id);
            if (account == null)
            {
                SetStatusEvenIfUnchanged($"{label} was removed from QuickMail.", AnnouncementCategory.Result);
                return;
            }
            if (parent != null)
            {
                parent = FindAccount(parent.Id);
                if (parent == null)
                {
                    SetStatusEvenIfUnchanged(
                        $"Could not reconnect {label}: the account it belongs to is no longer in QuickMail.",
                        AnnouncementCategory.Result);
                    return;
                }
            }

            if (parent is { IsConnected: false })
            {
                var (parentOk, parentFailure) = await ConnectAndApplyAsync(parent, "reconnect-command");
                if (!parentOk)
                {
                    SetStatusEvenIfUnchanged(
                        $"Could not reconnect {label}: {parent.AccountLabel}, the account it belongs to, did not connect. {ReconnectFailureReason(parent, parentFailure)}".TrimEnd(),
                        AnnouncementCategory.Result);
                    return;
                }
            }

            var (ok, failure) = await ConnectAndApplyAsync(account, "reconnect-command");
            SetStatusEvenIfUnchanged(
                ok ? $"{label} reconnected." : $"Could not reconnect {label}. {ReconnectFailureReason(account, failure)}".TrimEnd(),
                AnnouncementCategory.Result);
        }
        finally
        {
            _reconnecting.Remove(id);
            if (heldParentId is { } held) _reconnecting.Remove(held);
        }
    }

    private AccountModel? FindAccount(Guid? id)
        => id is { } value ? Accounts.FirstOrDefault(a => a.Id == value) : null;

    /// <summary>
    /// Makes sure the account has a usable sign-in, showing the sign-in window if the saved one has
    /// lapsed. That is allowed here, and nowhere automatic, because the user asked. Returns false,
    /// having said why, when the account is still not signed in.
    /// </summary>
    private async Task<bool> EnsureSignedInForReconnectAsync(AccountModel identity)
    {
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            try
            {
                if (identity.AuthType == AuthType.OAuth2Google)
                {
                    // The router's EnsureSilentTokenAsync does nothing for Google, so ask for a token
                    // outright: Google's token call is silent-only — it refreshes the stored token or
                    // throws, and never opens a browser.
                    await _oauthService.GetAccessTokenSilentAsync(identity, [], cts.Token);
                }
                else
                {
                    await _oauthService.EnsureSilentTokenAsync(identity, cts.Token);
                }
                return true;
            }
            catch (InteractiveSignInRequiredException)
            {
                // The saved sign-in has lapsed: fall through to the sign-in window.
            }
            catch (Exception ex) when (ConnectionFailure.IsConnectionFailure(ex, CancellationToken.None))
            {
                // The network or a slow token service, not the sign-in. Let the connect try; it
                // reports its own failure.
                LogService.Log($"Reconnect/{identity.AccountLabel}: silent sign-in check could not reach the server", ex);
                return true;
            }
            catch (Exception ex) when (identity.AuthType == AuthType.OAuth2Google)
            {
                // Google reports a lapsed or revoked sign-in as an ordinary exception (no stored
                // token, invalid_grant): fall through to the sign-in window.
                LogService.Log($"Reconnect/{identity.AccountLabel}: Google silent sign-in failed", ex);
            }
            catch (Exception ex)
            {
                LogService.Log($"Reconnect/{identity.AccountLabel}: silent sign-in check failed", ex);
                return true;
            }
        }

        // Microsoft's sign-in window ends the wait when it is closed, so it needs no limit. Google's
        // sign-in is a page in the system browser, and closing that tab ends nothing: without a
        // limit the wait would never finish, and this account could not be reconnected again until
        // a restart.
        using var signInCts = identity.AuthType == AuthType.OAuth2Google
            ? new CancellationTokenSource(GoogleSignInTimeout)
            : new CancellationTokenSource();
        try
        {
            var result = await _oauthService.SignInInteractiveAsync(identity, signInCts.Token);
            if (string.IsNullOrEmpty(result.Username))
            {
                SetStatusEvenIfUnchanged($"Sign-in for {identity.AccountLabel} did not complete.", AnnouncementCategory.Result);
                return false;
            }
            if (!string.Equals(result.Username, identity.Username, StringComparison.OrdinalIgnoreCase))
            {
                // The same guard the account editor applies (#202): another identity's sign-in must
                // not stand in for this account's.
                SetStatusEvenIfUnchanged(
                    $"Signed in as {result.Username}, not {identity.Username}. Reconnect again and sign in as {identity.Username}.",
                    AnnouncementCategory.Result);
                return false;
            }
            return true;
        }
        catch (OperationCanceledException) when (signInCts.IsCancellationRequested)
        {
            SetStatusEvenIfUnchanged(
                $"Sign-in for {identity.AccountLabel} was not finished within {GoogleSignInTimeout.TotalMinutes:0} minutes. Reconnect again to retry.",
                AnnouncementCategory.Result);
            return false;
        }
        catch (Exception ex)
        {
            LogService.Log($"Reconnect/{identity.AccountLabel}: interactive sign-in failed", ex);
            SetStatusEvenIfUnchanged($"Sign-in for {identity.AccountLabel} did not complete: {ex.Message}", AnnouncementCategory.Result);
            return false;
        }
    }

    private static readonly TimeSpan GoogleSignInTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Connects one account, once, and brings the window up to date: its status, its folders in the
    /// tree, and its new-mail watcher — the same steps the automatic reconnect takes. On failure,
    /// also returns what kind of failure it was, read before ApplyAccountStatus consumes it.
    /// </summary>
    private async Task<(bool Connected, ConnectFailureKind? Failure)> ConnectAndApplyAsync(AccountModel account, string source)
    {
        var result = await Task.Run(() => ConnectOneAccountAsync(account, maxAttempts: 1));
        ConnectFailureKind? failure = _lastConnectFailure.TryGetValue(account.Id, out var kind) ? kind : null;
        ApplyAccountStatus(account, result.Folders, source);
        if (result.Folders == null) return (false, failure);

        SetCachedFolders(result.Id, result.Folders);
        RebuildFolderListFromCache();
        WireUpWatchers();
        return (true, null);
    }

    /// <summary>The sentence that follows "Could not reconnect X." — what the connect ran into.</summary>
    internal string ReconnectFailureReason(AccountModel account, ConnectFailureKind? failure) => failure switch
    {
        ConnectFailureKind.NotAttempted when account.AuthType == AuthType.Password
            => "It has no saved password. Enter it in Manage Accounts.",
        ConnectFailureKind.NotAttempted
            => "It needs you to sign in again.",
        ConnectFailureKind.Transport when _connectivity is { IsNetworkAvailable: false }
            => "There is no network connection.",
        ConnectFailureKind.Transport
            => "The server could not be reached.",
        ConnectFailureKind.ServerRefused
            => "The server refused the connection. Check the account in Manage Accounts.",
        _ => "",
    };
}

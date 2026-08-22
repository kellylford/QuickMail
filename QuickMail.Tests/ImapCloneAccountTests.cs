using System;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// #31 PR 3 regression: <see cref="ImapMailService.CloneAccount"/> stores the copy that the IDLE
/// watcher and per-folder calls later hand to CreateAuthenticatedClientAsync → GetAccessTokenAsync.
/// If the shared-mailbox identity (IsShared / ParentAccountId / SharedAddress) is dropped from that
/// clone, an IMAP-parent shared mailbox authenticates as ITSELF — no MSAL entry exists for the shared
/// address, the parent-token resolver never fires, and the silent-only guard is bypassed — so the app
/// launches an interactive sign-in for the shared address (the "password prompt" bug). Lock the fields.
/// </summary>
public class ImapCloneAccountTests
{
    [Fact]
    public void CloneAccount_PreservesSharedMailboxIdentity()
    {
        var parentId = Guid.NewGuid();
        var shared = new AccountModel
        {
            Id              = Guid.NewGuid(),
            Username        = "support@contoso.com",
            AuthType        = AuthType.OAuth2Microsoft,
            BackendKind     = BackendKind.ImapSmtp,
            ImapHost        = "outlook.office365.com",
            IsShared        = true,
            ParentAccountId = parentId,
            SharedAddress   = "support@contoso.com",
        };

        var clone = ImapMailService.CloneAccount(shared);

        Assert.True(clone.IsShared);
        Assert.Equal(parentId, clone.ParentAccountId);
        Assert.Equal("support@contoso.com", clone.SharedAddress);
        Assert.Equal(BackendKind.ImapSmtp, clone.BackendKind);
        // Sanity: the ordinary identity fields still round-trip too.
        Assert.Equal(shared.Id, clone.Id);
        Assert.Equal("support@contoso.com", clone.Username);
        Assert.Equal(AuthType.OAuth2Microsoft, clone.AuthType);
    }

    [Fact]
    public void CloneAccount_NormalAccount_IsNotMarkedShared()
    {
        var normal = new AccountModel
        {
            Id          = Guid.NewGuid(),
            Username    = "me@contoso.com",
            AuthType    = AuthType.OAuth2Microsoft,
            BackendKind = BackendKind.ImapSmtp,
        };

        var clone = ImapMailService.CloneAccount(normal);

        Assert.False(clone.IsShared);
        Assert.Null(clone.ParentAccountId);
        Assert.Null(clone.SharedAddress);
    }
}

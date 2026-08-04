using System;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The folder picker opens pre-selected on the folder the user came from (issue #431), which puts
/// "move/copy this where it already is" one Enter away. Neither backend refuses that, and it is not
/// harmless: a same-folder copy duplicates, a same-folder IMAP <c>UID MOVE</c> re-creates the
/// messages under new UIDs while QuickMail deletes the old ids locally, and Graph's folder
/// <c>/copy</c> into the current parent leaves a second copy of the folder and all its mail.
///
/// <para>These cover <see cref="MainViewModel.IsAlreadyUnder"/>, the parent test behind the folder
/// guard, across both hierarchy models — IMAP encodes the parent in the separator-delimited
/// FullName, Graph references it by id.</para>
/// </summary>
public class FolderDestinationGuardTests
{
    private static readonly Guid Account = Guid.NewGuid();

    private static MailFolderModel F(string fullName, string? parentId = null, Guid? account = null) =>
        new()
        {
            FullName = fullName,
            DisplayName = fullName,
            ParentId = parentId,
            AccountId = account ?? Account,
        };

    [Theory]
    // The destination IS the folder's immediate parent.
    [InlineData("INBOX/Projects", "INBOX", true)]
    [InlineData("Clients.Acme", "Clients", true)]      // '.' separator servers
    // A grandparent is not the parent: moving Projects/2026 to INBOX is a real move.
    [InlineData("INBOX/Projects/2026", "INBOX", false)]
    // Unrelated folders.
    [InlineData("Archive", "INBOX", false)]
    [InlineData("INBOX/Projects", "Archive", false)]
    // A prefix that is not a path boundary — "INBOXED" must not read as a child of "INBOX".
    [InlineData("INBOXED", "INBOX", false)]
    // The folder itself is not "already under" itself; the picker excludes it anyway.
    [InlineData("INBOX", "INBOX", false)]
    public void Imap_ParentIsReadFromTheSeparatorPath(string folder, string destination, bool expected)
        => Assert.Equal(expected, MainViewModel.IsAlreadyUnder(F(folder), F(destination)));

    [Fact]
    public void Graph_ParentIsReadFromParentId()
    {
        var inbox    = F("AAA");
        var projects = F("BBB", parentId: "AAA");
        var year     = F("CCC", parentId: "BBB");

        Assert.True(MainViewModel.IsAlreadyUnder(projects, inbox));
        Assert.True(MainViewModel.IsAlreadyUnder(year, projects));
        Assert.False(MainViewModel.IsAlreadyUnder(year, inbox));   // grandparent
    }

    /// <summary>Graph folder ids are case-sensitive, so the parent match must be ordinal.</summary>
    [Fact]
    public void Graph_ParentMatchIsCaseSensitive()
        => Assert.False(MainViewModel.IsAlreadyUnder(F("BBB", parentId: "AAA"), F("aaa")));

    /// <summary>
    /// A destination in another account is never "already under" — and the picker no longer offers
    /// one, but the guard must not accidentally report a same-name folder elsewhere as a no-op and
    /// swallow a move the user did ask for.
    /// </summary>
    [Fact]
    public void ADestinationInAnotherAccountIsNeverTheParent()
        => Assert.False(MainViewModel.IsAlreadyUnder(
            F("INBOX/Projects"), F("INBOX", account: Guid.NewGuid())));

    [Fact]
    public void AnEmptyDestinationPathIsNotAParent()
        => Assert.False(MainViewModel.IsAlreadyUnder(F("INBOX/Projects"), F(string.Empty)));
}

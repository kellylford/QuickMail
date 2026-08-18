using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// #491: a Microsoft 365 (Graph) folder's unread badge froze at the last full fetch because
/// ScheduleFolderCountRefresh excluded every non-IMAP backend. The badge is part of the folder's
/// accessible name, so a screen reader spoke the stale number on every pass. Graph exposes an
/// authoritative per-folder unread count (unreadItemCount) through the same router GetFoldersAsync the
/// IMAP path already uses, so it belongs in the live refresh; POP3 has no per-folder counts and stays out.
/// </summary>
public class FolderCountRefreshTests
{
    [Theory]
    [InlineData(BackendKind.ImapSmtp, true)]
    [InlineData(BackendKind.MicrosoftGraph, true)]
    [InlineData(BackendKind.Pop3Smtp, false)]
    public void BackendGetsLiveFolderCounts_ImapAndGraphYes_Pop3No(BackendKind kind, bool expected)
        => Assert.Equal(expected, MainViewModel.BackendGetsLiveFolderCounts(kind));
}

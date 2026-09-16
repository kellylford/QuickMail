using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

/// <summary>
/// Full-message search in the search box (#717). The box stays scoped to the folder on screen — a
/// real folder or a virtual one — and now reads the whole cached message: as the user types, the
/// query is parsed (<see cref="MessageSearchQuery"/>), the rows are matched as they always were, and
/// after a short pause the full-text index is asked which messages in this folder hold the words in
/// their body, Cc or attachment names. The list is rebuilt once, when that answer arrives.
/// </summary>
public partial class MainViewModel
{
    private MessageSearchMatcher? _searchMatcher;
    private string? _searchMatcherText;
    private CancellationTokenSource? _searchIndexCts;

    /// <summary>Pause after a keystroke before the index is asked, so a word typed quickly is one query,
    /// not one per letter. Settable so tests do not wait.</summary>
    internal TimeSpan SearchIndexDelay { get; set; } = TimeSpan.FromMilliseconds(150);

    /// <summary>The in-flight index query, for tests to await.</summary>
    internal Task? PendingSearchIndexQuery { get; private set; }

    private MessageSearchMatcher CurrentSearchMatcher()
    {
        if (_searchMatcher == null || !string.Equals(_searchMatcherText, SearchText, StringComparison.Ordinal))
        {
            _searchMatcher = new MessageSearchMatcher(
                MessageSearchQuery.Parse(SearchText), SearchFolderNameFor, SearchAccountNameFor);
            _searchMatcherText = SearchText;
        }
        return _searchMatcher;
    }

    private bool MatchesSearch(MailMessageSummary msg) => CurrentSearchMatcher().Matches(msg);

    private string SearchFolderNameFor(MailMessageSummary msg)
    {
        if (!string.IsNullOrEmpty(msg.FolderDisplayName)) return msg.FolderDisplayName;
        if (_cachedFolders.TryGetValue(msg.AccountId, out var folders))
        {
            var folder = folders.FirstOrDefault(f => string.Equals(f.FullName, msg.FolderName, StringComparison.Ordinal));
            if (folder != null && !string.IsNullOrEmpty(folder.DisplayName)) return folder.DisplayName;
        }
        return msg.FolderName;
    }

    private string SearchAccountNameFor(MailMessageSummary msg)
    {
        var account = Accounts.FirstOrDefault(a => a.Id == msg.AccountId);
        return account == null ? string.Empty : account.AccountLabel + " " + account.Username;
    }

    /// <summary>
    /// Called when the search text changes. Returns true when an index query was started, in which case
    /// that query rebuilds the list when it answers; false when the caller should rebuild now.
    /// </summary>
    private bool StartSearchIndexQuery()
    {
        DrainCts(ref _searchIndexCts);
        PendingSearchIndexQuery = null;

        if (OnlineMode || !_localStore.IsSearchIndexAvailable) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return false;
        var matcher = CurrentSearchMatcher();
        if (!matcher.Query.HasText) return false;

        var scope = SearchIndexScope();
        if (scope.Count == 0) return false;

        var cts = new CancellationTokenSource();
        _searchIndexCts = cts;
        PendingSearchIndexQuery = RunSearchIndexQueryAsync(matcher, scope, cts.Token);
        return true;
    }

    /// <summary>
    /// The folders the index is asked about: this folder when it is a real one; every folder of the
    /// accounts on screen when it is an aggregate, because the copy of a message shown there (the Inbox
    /// copy, say) need not be the copy whose body was cached (a Gmail label's).
    /// </summary>
    private List<(Guid AccountId, string FolderName)> SearchIndexScope()
    {
        if (!IsVirtualFolder(SelectedFolder))
            return [.. _rawMessages.Select(m => (m.AccountId, m.FolderName)).Distinct()];

        var accounts = _rawMessages.Select(m => m.AccountId).ToHashSet();
        var scope = new List<(Guid, string)>();
        foreach (var accountId in accounts)
        {
            if (_cachedFolders.TryGetValue(accountId, out var folders) && folders.Count > 0)
                scope.AddRange(folders.Where(f => !string.IsNullOrEmpty(f.FullName)).Select(f => (accountId, f.FullName)));
            else
                scope.AddRange(_rawMessages.Where(m => m.AccountId == accountId).Select(m => (m.AccountId, m.FolderName)).Distinct());
        }
        return scope;
    }

    private async Task RunSearchIndexQueryAsync(
        MessageSearchMatcher matcher, List<(Guid AccountId, string FolderName)> scope, CancellationToken ct)
    {
        try
        {
            if (SearchIndexDelay > TimeSpan.Zero)
                await Task.Delay(SearchIndexDelay, ct);

            var wanted = SearchMatchExpression.AllOf(matcher.Query.Terms.Where(t => !t.Negated));
            var unwanted = SearchMatchExpression.AnyOf(matcher.Query.Terms.Where(t => t.Negated));
            // Microsoft.Data.Sqlite's async calls run synchronously, and the query first indexes anything
            // cached since the last search — off the UI thread, or typing would stall on it.
            var (positive, negative) = await Task.Run(async () =>
            {
                var p = wanted == null ? null : await _localStore.FindMessagesAsync(wanted, scope, ct);
                var n = unwanted == null ? null : await _localStore.FindMessagesAsync(unwanted, scope, ct);
                return (p, n);
            }, ct);

            if (ct.IsCancellationRequested) return;
            matcher.SetIndexHits(positive, negative);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            // The rows still answer: fall through and rebuild from them rather than leaving the list
            // showing the previous search's results.
            LogService.Log("Search index query failed; matching loaded messages only", ex);
        }

        if (!ReferenceEquals(matcher, _searchMatcher) || ct.IsCancellationRequested) return;
        ApplyFiltersAndSearch();
    }
}

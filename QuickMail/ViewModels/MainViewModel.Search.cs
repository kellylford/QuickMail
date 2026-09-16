using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
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
    private Dictionary<(Guid, string), string>? _searchFolderNames;

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
            _searchFolderNames = null;
        }
        return _searchMatcher;
    }

    private bool MatchesSearch(MailMessageSummary msg) => CurrentSearchMatcher().Matches(msg);

    /// <summary>
    /// Called when the folder's messages are replaced — a load finishing, a refresh — while a search is in the
    /// box. The index's answer was for the messages that were there before (and if the search was typed before
    /// the folder had loaded, there was nothing to ask about), so it is asked again for these.
    /// </summary>
    private void RefreshSearchIndexForNewMessages()
    {
        if (_suppressFilterRebuild || string.IsNullOrWhiteSpace(SearchText)) return;
        // A new matcher for the same text, so the old answer is not applied to the new messages.
        _searchMatcher = null;
        StartSearchIndexQuery();
    }

    private string SearchFolderNameFor(MailMessageSummary msg)
    {
        if (!string.IsNullOrEmpty(msg.FolderDisplayName)) return msg.FolderDisplayName;
        // Built once per query, on first use: a folder: condition asks for every message on screen.
        if (_searchFolderNames == null)
        {
            _searchFolderNames = [];
            foreach (var (accountId, folders) in _cachedFolders)
                foreach (var f in folders)
                    if (!string.IsNullOrEmpty(f.DisplayName))
                        _searchFolderNames[(accountId, f.FullName)] = f.DisplayName;
        }
        return _searchFolderNames.TryGetValue((msg.AccountId, msg.FolderName), out var name) ? name : msg.FolderName;
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
                // The first call already indexed what was waiting; the second need not wait again.
                var n = unwanted == null ? null : await _localStore.FindMessagesAsync(unwanted, scope, ct,
                    indexPendingFirst: wanted == null);
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

    // ── Search Results folder (#717, phase 2) ────────────────────────────────────

    // Sentinel for Advanced Search's results across accounts: SearchResultsPrefix + escaped account ids
    // (comma-separated) + "|" + escaped query. The request lives in the folder name, like the contact-mail
    // sentinel, so a refresh or a sync arrival can rebuild the results from the folder alone.
    internal const string SearchResultsPrefix = "\u0000Search:";

    public static MailFolderModel CreateSearchResultsFolder(string query, IEnumerable<Guid> accountIds) => new()
    {
        FullName = SearchResultsPrefix
            + Uri.EscapeDataString(string.Join(",", accountIds))
            + "|" + Uri.EscapeDataString(query),
        DisplayName = $"Search results: {query}",
    };

    internal static bool TryGetSearchResultsFromSentinel(string? fullName, out string query, out List<Guid> accountIds)
    {
        query = string.Empty;
        accountIds = [];
        if (fullName == null || !fullName.StartsWith(SearchResultsPrefix, StringComparison.Ordinal)) return false;
        var tail = fullName[SearchResultsPrefix.Length..];
        var sep = tail.IndexOf('|', StringComparison.Ordinal);
        if (sep < 0) return false;
        foreach (var part in Uri.UnescapeDataString(tail[..sep]).Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (Guid.TryParse(part, out var id)) accountIds.Add(id);
        query = Uri.UnescapeDataString(tail[(sep + 1)..]);
        return true;
    }

    /// <summary>True while the message list is showing Advanced Search's results.</summary>
    public bool IsSearchResultsView =>
        SelectedFolder != null && TryGetSearchResultsFromSentinel(SelectedFolder.FullName, out _, out _);

    /// <summary>The query the Search Results folder on screen was built from; empty elsewhere.</summary>
    public string SearchResultsQuery =>
        SelectedFolder != null && TryGetSearchResultsFromSentinel(SelectedFolder.FullName, out var q, out _) ? q : string.Empty;

    /// <summary>The search on screen, as a request Advanced Search can reopen with; null outside Search Results.</summary>
    public AdvancedSearchRequest? CurrentSearchResultsRequest =>
        SelectedFolder != null && TryGetSearchResultsFromSentinel(SelectedFolder.FullName, out var q, out var ids)
            ? new AdvancedSearchRequest(q, InCurrentFolder: false, ids)
            : null;

    // Where closing the results returns to; null when the search began before any folder was open.
    private MailFolderModel? _searchResultsReturnFolder;

    /// <summary>
    /// Runs an Advanced Search request and returns how many messages it found. In the current folder it is
    /// the search box's query; across accounts it opens a Search Results folder.
    /// </summary>
    public async Task<int> RunAdvancedSearchAsync(AdvancedSearchRequest request)
    {
        if (request.InCurrentFolder)
            return await ApplySearchTextAsync(request.Query);

        if (!IsSearchResultsView && !IsContactMailView) _searchResultsReturnFolder = SelectedFolder;
        await SelectFolderAsync(CreateSearchResultsFolder(request.Query, request.AccountIds));
        return Messages.Count;
    }

    /// <summary>Puts <paramref name="query"/> in the search box and waits for the list to reflect it.</summary>
    public async Task<int> ApplySearchTextAsync(string query)
    {
        IsSearchActive = true;
        SearchText = query;
        if (PendingSearchIndexQuery != null) await PendingSearchIndexQuery;
        return Messages.Count;
    }

    /// <summary>Closes Search Results and goes back to the folder the search started from (All Mail if none).</summary>
    [RelayCommand]
    public async Task CloseSearchResultsAsync()
    {
        if (!IsSearchResultsView) return;
        var back = _searchResultsReturnFolder ?? AllMailFolder;
        _searchResultsReturnFolder = null;
        await SelectFolderAsync(back);
    }

    /// <summary>
    /// The query with <c>account:</c> turned into the account list it narrows, which is how the store takes
    /// it: the chosen accounts whose name or address contains any <c>account:</c> value. Shared mailboxes are
    /// left out, as every aggregate view leaves them out (#31).
    /// </summary>
    private (MessageSearchQuery Query, List<Guid> Accounts) ResolveSearchAccounts(string text, IReadOnlyCollection<Guid> chosen)
    {
        var query = MessageSearchQuery.Parse(text);
        var accounts = Accounts
            .Where(a => chosen.Contains(a.Id) && !a.IsShared)
            .Where(a => query.Accounts.Count == 0 || query.Accounts.Any(v =>
                (a.AccountLabel + " " + a.Username).Contains(v, StringComparison.OrdinalIgnoreCase)))
            .Select(a => a.Id)
            .ToList();
        query.Accounts.Clear();
        return (query, accounts);
    }

    private async Task FetchSearchResultsAsync(string text, IReadOnlyCollection<Guid> chosen)
    {
        var loadVersion = Interlocked.Increment(ref _folderLoadVersion);
        var expectedFolder = SelectedFolder;
        Messages.Clear();
        StatusText = "Searching…";
        IsBusy = true;

        _folderCts?.Cancel();
        ReplaceCts(ref _folderCts, out var ct);

        try
        {
            var (query, accounts) = ResolveSearchAccounts(text, chosen);
            List<MailMessageSummary> found;
            if (OnlineMode)
            {
                // No cache: read every folder of the chosen accounts and match the rows, as contact mail does.
                var matcher = new MessageSearchMatcher(query, SearchFolderNameFor, _ => string.Empty);
                found = [];
                foreach (var account in Accounts.Where(a => accounts.Contains(a.Id)))
                {
                    if (!_cachedFolders.TryGetValue(account.Id, out var folders)) continue;
                    foreach (var folder in folders)
                    {
                        if (folder.ExcludeFromAllMail || string.IsNullOrEmpty(folder.FullName)) continue;
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            var msgs = _syncDays > 0
                                ? await _imap.GetMessagesSinceDateAsync(account.Id, folder.FullName, DateTime.UtcNow.AddDays(-_syncDays), ct)
                                : await _imap.GetMessageSummariesAsync(account.Id, folder.FullName, 50000, ct);
                            found.AddRange(msgs.Where(matcher.Matches));
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            LogService.Log($"Search results online {account.AccountLabel}/{folder.DisplayName}", ex);
                        }
                    }
                }
            }
            else
            {
                // Off the UI thread: the store indexes pending work first, and its async calls run synchronously.
                found = await Task.Run(() => _localStore.SearchSummariesAsync(query, accounts, ct), ct);
            }
            if (!IsCurrentFolderLoad(loadVersion, expectedFolder)) return;

            await ResolveFlagNamesAsync(found);
            SetMessages(found);
            var n = Messages.Count;
            StatusText = n == 0 ? "No messages found." : $"{n} {(n == 1 ? "message" : "messages")} found.";
        }
        catch (OperationCanceledException)
        {
            if (loadVersion == _folderLoadVersion)
                StatusText = "Search cancelled.";
        }
        catch (Exception ex)
        {
            LogService.Log("Search results failed", ex);
            StatusText = "Could not search.";
        }
        finally
        {
            if (loadVersion == _folderLoadVersion)
                IsBusy = false;
        }
    }

    /// <summary>Whether a message arriving while Search Results is open belongs in it, judged from its row.</summary>
    private bool BelongsInSearchResults(MailMessageSummary msg, string text, IReadOnlyCollection<Guid> chosen)
    {
        var (query, accounts) = ResolveSearchAccounts(text, chosen);
        if (!accounts.Contains(msg.AccountId)) return false;
        return new MessageSearchMatcher(query, SearchFolderNameFor, _ => string.Empty).Matches(msg);
    }
}

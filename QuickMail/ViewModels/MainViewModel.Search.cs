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
                var p = wanted == null ? null : await _localStore.FindMessagesAsync(wanted, scope, ct: ct);
                // The first call already indexed what was waiting; the second need not wait again.
                var n = unwanted == null ? null : await _localStore.FindMessagesAsync(unwanted, scope,
                    indexPendingFirst: wanted == null, ct: ct);
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

    /// <summary>What running an Advanced Search request came to.</summary>
    /// <param name="Found">How many messages the list now shows.</param>
    /// <param name="Failed">The search could not run (the store failed); the list shows nothing new.</param>
    /// <param name="Cancelled">The user moved to another folder before it finished; it was abandoned.</param>
    public sealed record AdvancedSearchOutcome(int Found, bool Failed, bool Cancelled = false);

    // Set by FetchSearchResultsAsync when it could not search, for RunAdvancedSearchAsync to report.
    private bool _searchResultsFailed;

    /// <summary>
    /// Runs an Advanced Search request. In the current folder it is the search box's query; across accounts it
    /// opens a Search Results folder — and when that finds nothing or fails, goes straight back to the folder
    /// the user was in, so the form's "this folder" still means that folder and closing it lands somewhere
    /// real rather than in an empty results list.
    /// </summary>
    public async Task<AdvancedSearchOutcome> RunAdvancedSearchAsync(AdvancedSearchRequest request)
    {
        if (request.InCurrentFolder)
            return new AdvancedSearchOutcome(await ApplySearchTextAsync(request.Query), Failed: false);

        var previous = SelectedFolder;
        var previousReturn = _searchResultsReturnFolder;
        if (IsContactMailView) _searchResultsReturnFolder = _contactMailReturnFolder;
        else if (!IsSearchResultsView) _searchResultsReturnFolder = SelectedFolder;

        _searchResultsFailed = false;
        var results = CreateSearchResultsFolder(request.Query, request.AccountIds);
        await SelectFolderAsync(results);
        // The form is modeless and an online search can take a while: if the user has gone to another folder
        // since, that folder's count is not this search's, and pulling them back would be worse.
        if (!string.Equals(SelectedFolder?.FullName, results.FullName, StringComparison.Ordinal))
            return new AdvancedSearchOutcome(0, Failed: false, Cancelled: true);

        var failed = _searchResultsFailed;
        if (!failed && request.SearchServer)
        {
            var server = await SearchServerTooAsync();
            if (server.Cancelled && !string.Equals(SelectedFolder?.FullName, results.FullName, StringComparison.Ordinal))
                return new AdvancedSearchOutcome(0, Failed: false, Cancelled: true);
        }

        var found = Messages.Count;
        if (found == 0 || failed)
        {
            _searchResultsReturnFolder = previousReturn;
            await SelectFolderAsync(previous ?? AllMailFolder);
        }
        return new AdvancedSearchOutcome(found, failed);
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
                        // Every folder, Sent and Trash included, as the cached search covers them.
                        if (folder.IsHeader || string.IsNullOrEmpty(folder.FullName)) continue;
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
            {
                _searchResultsFailed = true;
                StatusText = "Search cancelled.";
            }
        }
        catch (Exception ex)
        {
            LogService.Log("Search results failed", ex);
            _searchResultsFailed = true;
            StatusText = "Could not search.";
        }
        finally
        {
            if (loadVersion == _folderLoadVersion)
                IsBusy = false;
        }
    }

    // ── Search the server too (#717, phase 3) ────────────────────────────────────

    /// <summary>At most this many server results per account: enough to find the message, few enough to list.</summary>
    internal const int ServerSearchMaxResults = 200;

    /// <summary>What asking the servers added to a Search Results folder.</summary>
    /// <param name="Added">Messages that were not already in the results.</param>
    /// <param name="FailedAccounts">Accounts whose server could not be asked.</param>
    /// <param name="Asked">Accounts whose server was asked (connected, with a server that searches).</param>
    /// <param name="Cancelled">The search was abandoned — the folder changed, or another search started.</param>
    public sealed record ServerSearchOutcome(int Added, IReadOnlyList<string> FailedAccounts, int Asked, bool Cancelled = false);

    /// <summary>True while Search Results is on screen, where asking the servers adds to it.</summary>
    public bool CanSearchServer => IsSearchResultsView;

    /// <summary>
    /// Asks each chosen account's server for the search on screen and adds what it finds that the results do
    /// not already have — mail older than the sync range, or whose text was never downloaded. Server results are
    /// shown, not cached: caching old mail would make it wait for client rules as though it had just arrived
    /// (#712), and the sync would drop it again. So they last until the results are refreshed or closed.
    /// </summary>
    public async Task<ServerSearchOutcome> SearchServerTooAsync()
    {
        // One at a time, held until the results are in the list: a second run that started before the first
        // had inserted its rows would not know about them and add the same messages again.
        if (Interlocked.CompareExchange(ref _serverSearchRunning, 1, 0) != 0)
            return new ServerSearchOutcome(0, [], 0, Cancelled: true);
        try { return await SearchServerTooCoreAsync(); }
        finally { Volatile.Write(ref _serverSearchRunning, 0); }
    }

    private async Task<ServerSearchOutcome> SearchServerTooCoreAsync()
    {
        var failed = new List<string>();
        if (SelectedFolder == null || !TryGetSearchResultsFromSentinel(SelectedFolder.FullName, out var text, out var chosen))
            return new ServerSearchOutcome(0, failed, 0);

        var expectedFolder = SelectedFolder;
        var loadVersion = _folderLoadVersion;
        var ct = _folderCts?.Token ?? CancellationToken.None;
        var (query, accountIds) = ResolveSearchAccounts(text, chosen);

        var targets = Accounts
            .Where(a => accountIds.Contains(a.Id) && a.BackendKind != BackendKind.Pop3Smtp)
            // An account known to be unreachable is not asked; any other is, and says so if it fails.
            .Where(a => _connectivity?.IsAccountOnline(a.Id) ?? true)
            .ToList();
        if (targets.Count == 0) return new ServerSearchOutcome(0, failed, 0);

        IsBusy = true;
        // No status text of its own: the View says "Searching the server…" once, and a status change would
        // be announced again behind it.
        var found = new List<MailMessageSummary>();
        try
        {
            foreach (var account in targets)
            {
                ct.ThrowIfCancellationRequested();
                var folders = _cachedFolders.TryGetValue(account.Id, out var cached)
                    ? cached.Where(f => !f.IsHeader && !string.IsNullOrEmpty(f.FullName))
                            .Where(f => query.Folders.Count == 0
                                || query.Folders.Any(n => f.DisplayName.Contains(n, StringComparison.OrdinalIgnoreCase)))
                            .Select(f => f.FullName)
                            .ToList()
                    : [];
                try
                {
                    var hits = await _imap.SearchServerAsync(account.Id, query, folders, ServerSearchMaxResults, ct);
                    // IMAP summaries never carry attachments, so has:attachment can only be judged where the
                    // server reports it (Microsoft 365); elsewhere the server's own answer stands.
                    var check = account.BackendKind == BackendKind.MicrosoftGraph
                        ? query
                        : WithoutAttachmentCondition(query);
                    var matcher = new MessageSearchMatcher(check, SearchFolderNameFor, _ => string.Empty);
                    foreach (var m in hits)
                    {
                        if (m.IsServerFlagged && m.FlagId == null)
                            m.FlagId = FlagDefinition.BuiltInFlagId.ToString();
                        if (matcher.MatchesConditions(m)) found.Add(m);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    LogService.Log($"Search the server: {account.AccountLabel}", ex);
                    failed.Add(account.AccountLabel);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return new ServerSearchOutcome(0, failed, targets.Count, Cancelled: true);
        }
        finally
        {
            if (loadVersion == _folderLoadVersion) IsBusy = false;
        }

        if (!IsCurrentFolderLoad(loadVersion, expectedFolder))
            return new ServerSearchOutcome(0, failed, targets.Count, Cancelled: true);

        var have = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in _rawMessages)
        {
            have.Add(MessageDeduplicator.PerFolderKeyFor(m));
            if (!string.IsNullOrWhiteSpace(m.InternetMessageId)) have.Add(MessageDeduplicator.CollapseKeyFor(m));
        }
        var added = found
            .Where(m => !have.Contains(MessageDeduplicator.PerFolderKeyFor(m))
                     && (string.IsNullOrWhiteSpace(m.InternetMessageId) || !have.Contains(MessageDeduplicator.CollapseKeyFor(m))))
            .ToList();
        // One copy of each message the servers returned in several folders.
        added = MessageDeduplicator.CollapseForAggregate(added, ResolveFolderKind);

        if (added.Count > 0)
        {
            await ResolveFlagNamesAsync(added);
            // Added to the list in place, the way live arrivals are, rather than through SetMessages: replacing
            // the collection moves focus into the list, which would take the user away from wherever they were.
            ApplyFolderDisplayNames(added);
            StampWatchedFlags(added);
            foreach (var m in added)
                m.Preview = _showPreview ? TruncatePreview(m.Preview, _previewLines) : string.Empty;
            _rawMessages.AddRange(added);

            var previouslySelected = SelectedMessage;
            using (Messages.BeginBatchScope())
            {
                foreach (var m in added)
                {
                    if (!MatchesFilter(m) || !MatchesDayLimit(m)) continue;
                    if (!string.IsNullOrWhiteSpace(SearchText) && !MatchesSearch(m)) continue;
                    InsertMessageSorted(m);
                }
            }
            if (previouslySelected != null && SelectedMessage == null && Messages.Contains(previouslySelected))
                SelectedMessage = previouslySelected;
            RebuildActiveGroupView();
        }
        var n = Messages.Count;
        StatusText = $"{n} {(n == 1 ? "message" : "messages")} found.";
        return new ServerSearchOutcome(added.Count, failed, targets.Count);
    }

    // One server search at a time: a second one would ask every server again and add the same messages.
    private int _serverSearchRunning;

    private static MessageSearchQuery WithoutAttachmentCondition(MessageSearchQuery query)
    {
        var copy = MessageSearchQuery.Parse(query.ToQueryString());
        copy.HasAttachment = null;
        return copy;
    }

    private (string Text, List<Guid> Accounts, MessageSearchMatcher Matcher)? _arrivalMatcher;

    /// <summary>
    /// Whether a message arriving while Search Results is open belongs in it, judged from its row. The parsed
    /// query is kept between arrivals of the same search rather than rebuilt for every message.
    /// </summary>
    private bool BelongsInSearchResults(MailMessageSummary msg, string text, IReadOnlyCollection<Guid> chosen)
    {
        if (_arrivalMatcher is not { } cached || cached.Text != text || !cached.Accounts.SequenceEqual(chosen))
        {
            var (query, accounts) = ResolveSearchAccounts(text, chosen);
            cached = (text, [.. chosen], new MessageSearchMatcher(query, SearchFolderNameFor, _ => string.Empty));
            _arrivalMatcher = cached;
            _arrivalAccounts = accounts;
        }
        return _arrivalAccounts.Contains(msg.AccountId) && cached.Matcher.Matches(msg);
    }

    private List<Guid> _arrivalAccounts = [];
}

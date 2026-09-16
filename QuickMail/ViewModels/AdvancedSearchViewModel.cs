using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;

namespace QuickMail.ViewModels;

/// <summary>What Advanced Search asks for (#717): a query in the search box's own syntax, and where to look.</summary>
/// <param name="Query">The query text, as <see cref="MessageSearchQuery.ToQueryString"/> writes it.</param>
/// <param name="InCurrentFolder">True: search the folder on screen, through the search box. False: search
/// every folder of <paramref name="AccountIds"/> and show a Search Results folder.</param>
/// <param name="AccountIds">The accounts to search when not searching the current folder.</param>
public sealed record AdvancedSearchRequest(string Query, bool InCurrentFolder, IReadOnlyList<Guid> AccountIds);

/// <summary>An account Advanced Search can look in; the check box's name is the account's.</summary>
public sealed partial class AdvancedSearchAccount : ObservableObject
{
    public AdvancedSearchAccount(Guid id, string name, bool isChosen)
    {
        Id = id;
        Name = name;
        _isChosen = isChosen;
    }

    public Guid Id { get; }
    public string Name { get; }

    [ObservableProperty]
    private bool _isChosen;

    public override string ToString() => Name;
}

/// <summary>A choice in one of Advanced Search's drop-downs. <see cref="ToString"/> is what is read out.</summary>
public sealed record AdvancedSearchChoice(string Label, bool? Value)
{
    public override string ToString() => Label;
}

/// <summary>
/// The Advanced Search window (#717): one field per part of a message and per condition, building the
/// query the search box understands. Nothing here searches — <see cref="SearchRequested"/> hands the
/// request to the main window, which runs it and reports back whether anything was found.
/// </summary>
public sealed partial class AdvancedSearchViewModel : ObservableObject
{
    public static readonly IReadOnlyList<AdvancedSearchChoice> ReadChoices =
    [
        new("Read or unread", null),
        new("Unread", false),
        new("Read", true),
    ];

    public static readonly IReadOnlyList<AdvancedSearchChoice> FlagChoices =
    [
        new("Flagged or not", null),
        new("Flagged", true),
        new("Not flagged", false),
    ];

    public AdvancedSearchViewModel(
        IEnumerable<(Guid Id, string Name)> accounts,
        string? currentFolderName,
        AdvancedSearchRequest? previous = null)
    {
        CurrentFolderName = currentFolderName ?? string.Empty;
        CanSearchCurrentFolder = !string.IsNullOrEmpty(currentFolderName);

        var chosen = previous is { InCurrentFolder: false } ? previous.AccountIds.ToHashSet() : null;
        foreach (var (id, name) in accounts)
            Accounts.Add(new AdvancedSearchAccount(id, name, chosen == null || chosen.Contains(id)));

        _readState = ReadChoices[0];
        _flagState = FlagChoices[0];
        // Opened from the message list, the question is usually about this folder; opened to refine a
        // Search Results folder, it is about the accounts that search covered.
        _searchInCurrentFolder = CanSearchCurrentFolder && previous == null;

        if (previous != null)
            Load(MessageSearchQuery.Parse(previous.Query), previous.InCurrentFolder && CanSearchCurrentFolder);
    }

    public ObservableCollection<AdvancedSearchAccount> Accounts { get; } = [];

    public string CurrentFolderName { get; }
    public bool CanSearchCurrentFolder { get; }

    /// <summary>
    /// The "this folder" choice's text, naming the folder, with its access key (I). Underscores in the folder
    /// name are doubled so they show as themselves rather than as a second access key.
    /// </summary>
    public string CurrentFolderChoiceLabel => CanSearchCurrentFolder
        ? $"Th_is folder ({CurrentFolderName.Replace("_", "__", StringComparison.Ordinal)})"
        : "Th_is folder";

    [ObservableProperty] private string _words = string.Empty;
    [ObservableProperty] private string _from = string.Empty;
    [ObservableProperty] private string _to = string.Empty;
    [ObservableProperty] private string _cc = string.Empty;
    [ObservableProperty] private string _subject = string.Empty;
    [ObservableProperty] private string _body = string.Empty;
    [ObservableProperty] private string _attachment = string.Empty;
    [ObservableProperty] private bool _hasAttachments;
    [ObservableProperty] private AdvancedSearchChoice _readState;
    [ObservableProperty] private AdvancedSearchChoice _flagState;

    /// <summary>Received on or after this day.</summary>
    [ObservableProperty] private DateTime? _receivedFrom;

    /// <summary>Received on or before this day — the day itself included.</summary>
    [ObservableProperty] private DateTime? _receivedTo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchInAccounts))]
    private bool _searchInCurrentFolder;

    public bool SearchInAccounts
    {
        get => !SearchInCurrentFolder;
        set => SearchInCurrentFolder = !value;
    }

    /// <summary>Why the last Search did nothing; empty when it went ahead.</summary>
    [ObservableProperty] private string _problem = string.Empty;

    /// <summary>Raised by <see cref="SearchCommand"/> with a request that is worth running.</summary>
    public event Action<AdvancedSearchRequest>? SearchRequested;

    /// <summary>The query the fields add up to.</summary>
    public MessageSearchQuery BuildQuery()
    {
        var query = MessageSearchQuery.Parse(Words);
        AddField(query, From, SearchField.From);
        AddField(query, To, SearchField.To);
        AddField(query, Cc, SearchField.Cc);
        AddField(query, Subject, SearchField.Subject);
        AddField(query, Body, SearchField.Body);
        AddField(query, Attachment, SearchField.Attachment);
        if (HasAttachments) query.HasAttachment = true;
        if (ReadState.Value.HasValue) query.IsRead = ReadState.Value;
        if (FlagState.Value.HasValue) query.IsFlagged = FlagState.Value;
        if (ReceivedFrom.HasValue) query.After = ReceivedFrom.Value.Date;
        if (ReceivedTo.HasValue) query.Before = ReceivedTo.Value.Date.AddDays(1);
        return query;
    }

    /// <summary>
    /// A field's words each go to that field: "Sam Smith" in From finds a sender with both words in it,
    /// and a quoted phrase stays a phrase. Anything else typed there — <c>is:unread</c>, another field's
    /// prefix — is taken as words of this field too, so the field means what it says.
    /// </summary>
    private static void AddField(MessageSearchQuery query, string text, SearchField field)
        => query.Terms.AddRange(MessageSearchQuery.ParseFieldWords(text, field));

    /// <summary>Spreads a query over the fields — the reverse of <see cref="BuildQuery"/>, for reopening a search.</summary>
    private void Load(MessageSearchQuery query, bool inCurrentFolder)
    {
        var words = new MessageSearchQuery();
        foreach (var f in query.Folders) words.Folders.Add(f);
        foreach (var a in query.Accounts) words.Accounts.Add(a);
        var byField = new Dictionary<SearchField, MessageSearchQuery>();
        foreach (var term in query.Terms)
        {
            if (term.Field == SearchField.Any)
            {
                words.Terms.Add(term);
                continue;
            }
            if (!byField.TryGetValue(term.Field, out var q)) byField[term.Field] = q = new MessageSearchQuery();
            q.Terms.Add(term with { Field = SearchField.Any });
        }
        // A condition with no box of its own goes back into Words anywhere, rather than being lost.
        if (query.HasAttachment == false) words.HasAttachment = false;
        Words = words.ToQueryString();
        From = FieldText(byField, SearchField.From);
        To = FieldText(byField, SearchField.To);
        Cc = FieldText(byField, SearchField.Cc);
        Subject = FieldText(byField, SearchField.Subject);
        Body = FieldText(byField, SearchField.Body);
        Attachment = FieldText(byField, SearchField.Attachment);
        HasAttachments = query.HasAttachment == true;
        ReadState = ReadChoices.First(c => c.Value == query.IsRead);
        FlagState = FlagChoices.First(c => c.Value == query.IsFlagged);
        ReceivedFrom = query.After;
        ReceivedTo = query.Before?.AddDays(-1);
        SearchInCurrentFolder = inCurrentFolder;
    }

    private static string FieldText(Dictionary<SearchField, MessageSearchQuery> byField, SearchField field)
        => byField.TryGetValue(field, out var q) ? MessageSearchQuery.FieldWordsToText(q.Terms) : string.Empty;

    [RelayCommand]
    private void Search()
    {
        var query = BuildQuery();
        if (query.IsEmpty)
        {
            Problem = "Enter something to search for.";
            return;
        }
        if (ReceivedFrom.HasValue && ReceivedTo.HasValue && ReceivedTo.Value.Date < ReceivedFrom.Value.Date)
        {
            Problem = "The Received to date is before the Received from date.";
            return;
        }
        var inFolder = SearchInCurrentFolder && CanSearchCurrentFolder;
        var accounts = Accounts.Where(a => a.IsChosen).Select(a => a.Id).ToList();
        if (!inFolder && accounts.Count == 0)
        {
            Problem = "Choose at least one account to search.";
            return;
        }
        Problem = string.Empty;
        SearchRequested?.Invoke(new AdvancedSearchRequest(query.ToQueryString(), inFolder, accounts));
    }

    /// <summary>Empties every field, keeping where to look.</summary>
    [RelayCommand]
    private void Clear()
    {
        Words = From = To = Cc = Subject = Body = Attachment = string.Empty;
        HasAttachments = false;
        ReadState = ReadChoices[0];
        FlagState = FlagChoices[0];
        ReceivedFrom = ReceivedTo = null;
        Problem = string.Empty;
    }
}

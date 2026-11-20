using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

/// <summary>
/// The address book's People I've Sent To tab (#1039): the people remembered from sent mail who are not
/// in the address book. One can be forgotten, saved as a contact, or added to the message being written;
/// everyone can be forgotten at once. A forgotten person comes back if they are sent to again (Kelly's
/// choice): forgetting clears an entry, it does not block an address.
/// </summary>
public partial class PeopleSentToViewModel : ObservableObject
{
    private readonly IRecipientHistory _history;
    private readonly IContactService _contacts;
    private readonly Func<bool> _rememberingOn;
    private readonly bool _showFieldLabels;

    private Action<ContactModel>? _toInsertAction;
    private Action<ContactModel>? _ccInsertAction;
    private Action<ContactModel>? _bccInsertAction;

    public PeopleSentToViewModel(IRecipientHistory history, IContactService contacts, Func<bool> rememberingOn,
        bool showFieldLabels = false)
    {
        _history = history;
        _contacts = contacts;
        _rememberingOn = rememberingOn;
        _showFieldLabels = showFieldLabels;
    }

    public ObservableCollection<RememberedPerson> People { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedPerson))]
    private RememberedPerson? _selectedPerson;

    public bool HasSelectedPerson => SelectedPerson is not null;

    public bool HasPeople => People.Count > 0;

    /// <summary>
    /// The line above the list: why it is empty, or that remembering is off. Shown even with people in
    /// the list, since someone looking at it while remembering is off should know no one new is added.
    /// </summary>
    [ObservableProperty]
    private string _notice = string.Empty;

    /// <summary>What happened last, under the buttons; also what is announced when focus did not land.</summary>
    [ObservableProperty]
    private string _resultText = string.Empty;

    // ── Events the View answers (the VM never touches a window) ─────────────

    /// <summary>Asks the user to confirm: title, question. False for No, Cancel or Escape.</summary>
    public event Func<string, string, Task<bool>>? ConfirmRequested;

    public event Action<string, AnnouncementCategory>? AnnouncementRequested;

    /// <summary>
    /// Raised just before a row is removed, after <see cref="SelectedPerson"/> has moved to its neighbour:
    /// the View focuses that row synchronously and says whether focus got there (the #265 ordering).
    /// </summary>
    public event Func<bool>? FocusNowRequested;

    /// <summary>Raised after a removal when focus did not land, including when the list is now empty.</summary>
    public event Action? FocusRequested;

    /// <summary>Raised after someone is saved as a contact, so the Contacts tab can show them.</summary>
    public event Func<Task>? ContactsChanged;

    // ── Insert into the message (when opened from compose) ──────────────────

    public bool HasInsertActions => _toInsertAction is not null;

    public void SetInsertActions(Action<ContactModel> to, Action<ContactModel> cc, Action<ContactModel> bcc)
    {
        _toInsertAction = to;
        _ccInsertAction = cc;
        _bccInsertAction = bcc;
        OnPropertyChanged(nameof(HasInsertActions));
    }

    [RelayCommand] private void AddToTo()  => Insert(_toInsertAction);
    [RelayCommand] private void AddToCc()  => Insert(_ccInsertAction);
    [RelayCommand] private void AddToBcc() => Insert(_bccInsertAction);

    private void Insert(Action<ContactModel>? action)
    {
        if (action is null || SelectedPerson is not { } p) return;
        action(new ContactModel { DisplayName = p.DisplayName, EmailAddress = p.EmailAddress });
    }

    // ── Loading ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Fills the list: everyone remembered, most recently sent to first, leaving out anyone in the
    /// address book (they are on the Contacts tab, and suggestions already offer them as the contact).
    /// Keeps the selection on the same person where they are still there.
    /// </summary>
    public async Task LoadAsync()
    {
        var keep = SelectedPerson?.EmailAddress;
        List<ContactModel> remembered;
        HashSet<string> inBook;
        try
        {
            remembered = await _history.ListAsync();
            var contacts = await _contacts.LoadAllContactsAsync();
            inBook = new HashSet<string>(contacts.Select(c => c.EmailAddress.Trim()), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            LogService.Log("People I've Sent To: could not load", ex);
            People.Clear();
            SelectedPerson = null;
            OnPropertyChanged(nameof(HasPeople));
            Notice = "Couldn't load the people you've sent to. " + ex.Message;
            return;
        }

        People.Clear();
        foreach (var p in remembered.Where(p => !inBook.Contains(p.EmailAddress.Trim())))
            People.Add(new RememberedPerson(p.DisplayName, p.EmailAddress, p.LastUsedTicks, _showFieldLabels));
        OnPropertyChanged(nameof(HasPeople));
        SelectedPerson = (keep is null ? null : People.FirstOrDefault(p => p.EmailAddress.Equals(keep, StringComparison.OrdinalIgnoreCase)))
                         ?? People.FirstOrDefault();
        Notice = NoticeFor(_rememberingOn(), People.Count);
    }

    internal static string NoticeFor(bool rememberingOn, int count) => (rememberingOn, count) switch
    {
        (false, 0) => "Remembering is off, so no one is here. To have QuickMail remember the people you send to, turn on Remember people I send to in Settings, General, Composing.",
        (false, _) => "Remembering is off, so no one new is added. Turn on Remember people I send to in Settings, General, Composing.",
        (true, 0)  => "No one yet. People you send to who aren't in your address book appear here.",
        _          => string.Empty,
    };

    // ── Forget one ───────────────────────────────────────────────────────────

    // Set while one of the three actions is running: a second Delete pressed while the first is still
    // asking or writing would otherwise ask again about the same person.
    private bool _busy;

    [RelayCommand]
    private async Task ForgetSelectedAsync()
    {
        if (_busy || SelectedPerson is not { } person) return;
        _busy = true;
        try { await ForgetCoreAsync(person); }
        finally { _busy = false; }
    }

    private async Task ForgetCoreAsync(RememberedPerson person)
    {
        if (!await ConfirmAsync("Forget Person",
                $"Forget {person.ShownName}? They won't be suggested, and will be remembered again if you send to them."))
            return;
        bool removed;
        try
        {
            removed = await _history.RemoveAsync(person.EmailAddress);
        }
        catch (Exception ex)
        {
            LogService.Log("People I've Sent To: could not forget a person", ex);
            Report($"Couldn't forget {person.ShownName}. {ex.Message}", landed: false);
            return;
        }
        var landed = RemoveRow(person);
        Report(removed ? $"Forgot {person.ShownName}." : $"{person.ShownName} was already forgotten.", landed);
    }

    // ── Save as a contact ────────────────────────────────────────────────────

    [RelayCommand]
    private async Task SaveToContactsAsync()
    {
        if (_busy || SelectedPerson is not { } person) return;
        _busy = true;
        try { await SaveCoreAsync(person); }
        finally { _busy = false; }
    }

    private async Task SaveCoreAsync(RememberedPerson person)
    {
        try
        {
            await _contacts.UpsertContactAsync(new ContactModel
            {
                DisplayName = person.DisplayName,
                EmailAddress = person.EmailAddress,
                LastUsedTicks = person.LastSentUtcTicks,
            });
        }
        catch (Exception ex)
        {
            LogService.Log("People I've Sent To: could not save a contact", ex);
            Report($"Couldn't add {person.ShownName} to your contacts. {ex.Message}", landed: false);
            return;
        }
        // Still remembered, which does no harm: suggestions offer them as the contact, and this list
        // leaves out anyone in the address book. Should the contact be deleted, they are back here.
        var landed = RemoveRow(person);
        Report($"Added {person.ShownName} to your contacts.", landed);
        if (ContactsChanged is { } changed) await changed();
    }

    // ── Forget everyone ──────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ForgetAllAsync()
    {
        if (_busy) return;
        _busy = true;
        try { await ForgetAllCoreAsync(); }
        finally { _busy = false; }
    }

    private async Task ForgetAllCoreAsync()
    {
        int count;
        try { count = await _history.CountAsync(); }
        catch (Exception ex) { LogService.Log("People I've Sent To: could not count", ex); count = People.Count; }
        if (count == 0)
        {
            Report("No one is remembered.", landed: false);
            return;
        }
        if (!await ConfirmAsync("Forget Everyone", ForgetAllQuestion(People.Count, count)))
            return;
        try
        {
            await _history.ClearAsync();
        }
        catch (Exception ex)
        {
            LogService.Log("People I've Sent To: could not forget everyone", ex);
            Report($"Couldn't forget the people you've sent to. {ex.Message}", landed: false);
            return;
        }
        People.Clear();
        SelectedPerson = null;
        OnPropertyChanged(nameof(HasPeople));
        Notice = NoticeFor(_rememberingOn(), 0);
        Report(count == 1 ? "Forgot 1 person." : $"Forgot {count} people.", landed: false);
    }

    /// <summary>
    /// The Forget All question, counting what the list shows. The store also remembers people who are
    /// now contacts, whom this list leaves out; they are forgotten too, and the question says so rather
    /// than giving a number that does not match the list just read.
    /// </summary>
    internal static string ForgetAllQuestion(int listed, int remembered)
    {
        var people = listed == 1 ? "the 1 person" : $"all {listed} people";
        var also = remembered - listed;
        var extra = also <= 0 ? string.Empty
            : also == 1 ? " This also forgets 1 person who is in your address book; they stay in it."
            : $" This also forgets {also} people who are in your address book; they stay in it.";
        return listed == 0
            ? $"Forget everyone QuickMail remembered sending to?{extra} Your address book is not changed."
            : $"Forget {people} in this list?{extra} Your address book is not changed.";
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<bool> ConfirmAsync(string title, string question)
        => ConfirmRequested is { } ask && await ask(title, question);

    /// <summary>
    /// Takes <paramref name="person"/>'s row out, leaving selection and focus on the row after it (or
    /// before it, at the end). Returns whether focus landed on a row before the removal, in which case
    /// that row is being read and nothing should be announced over it (#265, #667).
    /// </summary>
    private bool RemoveRow(RememberedPerson person)
    {
        // By address, not by object: if the list was refilled while the store was being written, the
        // row on screen is a new object for the same person.
        var row = People.FirstOrDefault(p => p.EmailAddress.Equals(person.EmailAddress, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            if (People.Count == 0) Notice = NoticeFor(_rememberingOn(), 0);
            return false;
        }
        person = row;
        var landing = AddressBookViewModel.FindLandingRow(People, [person]);
        var landed = false;
        if (landing is not null)
        {
            SelectedPerson = landing;
            landed = FocusNowRequested?.Invoke() == true;
        }
        People.Remove(person);
        if (ReferenceEquals(SelectedPerson, person)) SelectedPerson = null;
        OnPropertyChanged(nameof(HasPeople));
        if (People.Count == 0) Notice = NoticeFor(_rememberingOn(), 0);
        if (!landed) FocusRequested?.Invoke();
        return landed;
    }

    /// <summary>Shows what happened, and says it only when focus did not land on a row being read.</summary>
    private void Report(string text, bool landed)
    {
        ResultText = text;
        if (!landed) AnnouncementRequested?.Invoke(text, AnnouncementCategory.Result);
    }
}

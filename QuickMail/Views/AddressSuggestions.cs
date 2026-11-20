using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using QuickMail.Controls;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Views;

/// <summary>
/// A single item in the autocomplete suggestion list. Wraps either a
/// <see cref="ContactModel"/> (one address) or a <see cref="GroupModel"/>
/// (expands to all member addresses on accept).
/// </summary>
internal sealed class AddressSuggestion
{
    public ContactModel? Contact { get; }
    public GroupModel?   Group   { get; }
    public bool IsGroup => Group is not null;

    /// <summary>
    /// Someone remembered from a sent message rather than saved in the address book (#1037). Shown on
    /// the second line only: what is said for the row is the person, wherever they came from.
    /// </summary>
    public bool IsRecent { get; }

    /// <summary>Bold first line shown in the popup row.</summary>
    public string PrimaryText => IsGroup
        ? Group!.Name
        : string.IsNullOrWhiteSpace(Contact!.DisplayName) ? Contact.EmailAddress : Contact.DisplayName;

    /// <summary>Dimmed second line shown below the primary text.</summary>
    public string SecondaryText => IsGroup
        ? $"{Group!.ResolvedMemberCount} member{(Group!.ResolvedMemberCount == 1 ? "" : "s")} — group"
        : IsRecent ? $"{Contact!.EmailAddress} — sent to before" : Contact!.EmailAddress;

    /// <summary>Full accessible name read by the screen reader for the list item.</summary>
    public string Display => IsGroup
        ? $"{Group!.Name}, group, {Group!.ResolvedMemberCount} member{(Group!.ResolvedMemberCount == 1 ? "" : "s")}"
        : string.IsNullOrWhiteSpace(Contact!.DisplayName)
            ? Contact.EmailAddress
            : $"{Contact.DisplayName} {Contact.EmailAddress}";

    public AddressSuggestion(ContactModel c, bool isRecent = false) { Contact = c; IsRecent = isRecent; }
    public AddressSuggestion(GroupModel g)   { Group   = g; }

    public override string ToString() => Display;
}

/// <summary>
/// The order address suggestions are offered in, and which of them Tab may enter (#1037). The first
/// should be the person the user is most likely typing: the address exactly as typed, then a name, a
/// word of a name or an address that <em>begins</em> with the text, then one that only contains it
/// (<see cref="SuggestionMatch.Strength"/>). Within each, the address book in its own most-recently-used
/// order, then people remembered from sent mail, then groups. Groups come last within a strength because
/// the first suggestion is what one Tab enters: typing "ma" for Mary must not put all twenty members of
/// Marketing in To. A group's name typed in full ranks with an exact address, ahead of everyone. A
/// remembered person who is anywhere in the address book is not offered.
/// </summary>
internal static class AddressSuggestionRanking
{
    /// <param name="addressBook">
    /// Every address in the address book, to leave out remembered people who are in it. The contacts
    /// passed in are only the best few matches, so checking against those alone would let a contact
    /// outside them be offered a second time as "sent to before". Null checks against
    /// <paramref name="contacts"/> only.
    /// </param>
    internal static List<AddressSuggestion> Combine(string typed,
        IEnumerable<GroupModel> groups, IEnumerable<ContactModel> contacts, IEnumerable<ContactModel> recents,
        IReadOnlySet<string>? addressBook = null)
    {
        var contactList = contacts.ToList();
        var known = new HashSet<string>(contactList.Select(c => c.EmailAddress.Trim()), StringComparer.OrdinalIgnoreCase);
        var all = contactList.Select(c => new AddressSuggestion(c))
            .Concat(recents.Where(r => !known.Contains(r.EmailAddress.Trim())
                                    && addressBook?.Contains(r.EmailAddress.Trim()) != true)
                           .Select(r => new AddressSuggestion(r, isRecent: true)))
            .Concat(groups.Select(g => new AddressSuggestion(g)));
        // OrderBy is stable, so each kind keeps its place within a strength.
        return all.OrderBy(s => Strength(s, typed)).ToList();
    }

    internal static int Strength(AddressSuggestion s, string typed)
    {
        if (!s.IsGroup) return SuggestionMatch.Strength(s.Contact!.DisplayName, s.Contact.EmailAddress, typed);
        var name = s.Group!.Name ?? string.Empty;
        if (typed.Trim().Length > 0 && name.Trim().Equals(typed.Trim(), StringComparison.OrdinalIgnoreCase)) return 0;
        return SuggestionMatch.BeginsWith(name, null, typed) ? 1 : 2;
    }

    /// <summary>
    /// Whether Tab enters the first of <paramref name="suggestions"/> for <paramref name="typed"/>. Only
    /// when the user is visibly typing it: never one that merely contains the text (typing "ann" must
    /// not become joann), and never in place of a complete address typed out in full that is not in the
    /// list (typing bob@x.com must not become bob@x.com.au). Otherwise Tab keeps what was typed, as it
    /// always did.
    /// </summary>
    internal static bool TabTakesFirst(IReadOnlyList<AddressSuggestion> suggestions, string typed)
    {
        if (suggestions.Count == 0) return false;
        var strength = Strength(suggestions[0], typed);
        if (strength == 0) return true;
        if (strength == 2) return false;
        return !LooksComplete(typed);
    }

    /// <summary>
    /// Typed text that reads as a whole address: something@domain.tld, with at least two letters after
    /// the last dot. "ann@ex" and "ann@example." are still being typed; "bob@x.com" is finished, even
    /// though bob@x.com.au begins with it.
    /// </summary>
    internal static bool LooksComplete(string typed)
    {
        typed = typed.Trim();
        var at = typed.LastIndexOf('@');
        if (at <= 0 || at == typed.Length - 1) return false;
        var domain = typed[(at + 1)..];
        var dot = domain.LastIndexOf('.');
        return dot > 0 && domain.Length - dot - 1 >= 2 && EmailAddressValidator.IsValid(typed);
    }
}

/// <summary>
/// Where address suggestions come from, as Settings → Composing has it (#1037): the address book
/// (contacts and groups) and the people remembered from sent mail. Both false means no suggestions.
/// </summary>
internal readonly record struct SuggestionSources(bool AddressBook, bool PeopleEmailed)
{
    /// <summary>The settings as they stand: the master switch off turns both sources off.</summary>
    internal static SuggestionSources From(ConfigModel config) => new(
        AddressBook:   config.SuggestAddresses && config.SuggestFromAddressBook,
        PeopleEmailed: config.SuggestAddresses && config.SuggestPeopleEmailed);
}

/// <summary>
/// A field the address suggestions can serve: compose's To/Cc/Bcc chip boxes, or the
/// appointment editor's plain Invite text box (#869). The controller only needs to read what is
/// being typed and to put a chosen address in its place.
/// </summary>
internal interface IAddressEntry
{
    /// <summary>The element whose keys and focus the controller watches.</summary>
    FrameworkElement Element { get; }

    /// <summary>What the field is called when an address is said to have gone into it: "To", "Cc", "Invite".</summary>
    string FieldName { get; }

    /// <summary>The partial address being typed — the search text.</summary>
    string CurrentInput { get; }

    /// <summary>False while the field cannot be edited, so nothing is suggested.</summary>
    bool CanEdit { get; }

    /// <summary>Raised as the user types.</summary>
    event EventHandler? InputChanged;

    /// <summary>Replaces the partial input with a chosen address.</summary>
    void AcceptSuggestion(string displayName, string email);

    /// <summary>Adds a further address after the first (a group's other members).</summary>
    void AddAddress(string displayName, string email);

    /// <summary>Returns keyboard focus to where the user was typing.</summary>
    void FocusInput();

    /// <summary>
    /// The suggestion list showing for this field, or null once it has gone. The field tells UI
    /// Automation it controls that list (<c>ControllerFor</c>), which is how a screen reader knows
    /// suggestions appeared and which list to read the selected one from.
    /// </summary>
    void SetSuggestionList(UIElement? list);

    /// <summary>Reports that the list it controls changed with nothing selected in it (UIA layout invalidated).</summary>
    void ReportSuggestionsChanged();

    /// <summary>
    /// Set while focus is in the suggestion list, so a field that commits what it holds when it
    /// loses focus does not commit the partial text the user is choosing a replacement for.
    /// </summary>
    bool SuppressLostFocusCommit { set; }
}

/// <summary>Compose's chip box as an <see cref="IAddressEntry"/>.</summary>
internal sealed class TokenizedAddressEntry : IAddressEntry
{
    private readonly TokenizedAddressBox _box;
    public TokenizedAddressEntry(TokenizedAddressBox box, string fieldName)
    {
        _box = box;
        FieldName = fieldName;
        _box.InputTextChanged += (_, _) => InputChanged?.Invoke(this, EventArgs.Empty);
    }
    public FrameworkElement Element => _box;
    public string FieldName { get; }
    public string CurrentInput => _box.CurrentInputText;
    public bool CanEdit => true;
    public event EventHandler? InputChanged;
    public void AcceptSuggestion(string displayName, string email) => _box.AcceptSuggestion(displayName, email);
    public void AddAddress(string displayName, string email) => _box.AddAddress(displayName, email);
    public void FocusInput() => _box.FocusInput();
    public void SetSuggestionList(UIElement? list) => _box.SetSuggestionList(list);
    public void ReportSuggestionsChanged() => _box.ReportSuggestionsChanged();
    public bool SuppressLostFocusCommit { set => _box.SuppressLostFocusCommit = value; }
}

/// <summary>
/// A plain text box holding addresses separated by semicolons or commas, as the appointment
/// editor's Invite field does. The partial input is the entry the caret is in — the user may be
/// correcting one in the middle, which is where a refused save puts them — and a chosen address
/// replaces just that entry. At the end of the field it is followed by "; " so the next one can
/// be typed straight away.
/// </summary>
internal sealed class TextBoxAddressEntry : IAddressEntry
{
    private readonly TextBox _box;
    public TextBoxAddressEntry(TextBox box, string fieldName)
    {
        _box = box;
        FieldName = fieldName;
        _box.TextChanged += (_, _) => InputChanged?.Invoke(this, EventArgs.Empty);
    }
    public FrameworkElement Element => _box;
    public string FieldName { get; }
    public string CurrentInput
    {
        get
        {
            var (start, end) = EntryBounds(_box.Text, _box.CaretIndex);
            return _box.Text[start..end].Trim();
        }
    }
    public bool CanEdit => !_box.IsReadOnly && _box.IsEnabled;
    public event EventHandler? InputChanged;

    public void AcceptSuggestion(string displayName, string email)
    {
        var text = _box.Text;
        var (start, end) = EntryBounds(text, _box.CaretIndex);
        // Keep a single space after the separator the entry follows, if there was one.
        var head = text[..start].TrimEnd();
        if (head.Length > 0) head += " ";
        var tail = text[end..];
        var inserted = Formatted(displayName, email);
        if (tail.Trim().Length == 0)
        {
            // The last entry: leave the field ready for the next address.
            _box.Text = head + inserted + "; ";
            _box.CaretIndex = _box.Text.Length;
        }
        else
        {
            // An entry in the middle: the separator after it is still there in the tail.
            _box.Text = head + inserted + tail;
            _box.CaretIndex = head.Length + inserted.Length;
        }
    }

    /// <summary>Adds an address after the entry the caret is at the end of (a group's next member).</summary>
    public void AddAddress(string displayName, string email)
    {
        var text = _box.Text;
        var caret = _box.CaretIndex;
        var inserted = Formatted(displayName, email);
        if (text[caret..].Trim().Length == 0)
        {
            var head = text.TrimEnd();
            if (head.Length > 0 && !head.EndsWith(';') && !head.EndsWith(',')) head += ";";
            if (head.Length > 0) head += " ";
            _box.Text = head + inserted + "; ";
            _box.CaretIndex = _box.Text.Length;
        }
        else
        {
            var insert = "; " + inserted;
            _box.Text = text[..caret] + insert + text[caret..];
            _box.CaretIndex = caret + insert.Length;
        }
    }

    // The caret stays where accepting left it, so the user carries on from the address chosen.
    public void FocusInput() => _box.Focus();

    // Only a ControllingTextBox can say what it controls; the Invite field is one.
    public void SetSuggestionList(UIElement? list) => (_box as ControllingTextBox)?.SetControls(list);
    public void ReportSuggestionsChanged() => (_box as ControllingTextBox)?.RaiseControlsLayoutInvalidated();

    public bool SuppressLostFocusCommit { set { } } // a text box commits nothing on losing focus

    private static string Formatted(string displayName, string email)
        => EventAttendees.Format([new EventAttendee { Email = email, Name = displayName ?? string.Empty }]);

    /// <summary>
    /// The entry containing <paramref name="caret"/>: from just after the separator before it to
    /// the next separator (or the end), counting only separators outside quotes, so a name such as
    /// "Lee, Ann" stays one entry.
    /// </summary>
    internal static (int Start, int End) EntryBounds(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var start = 0;
        var end = text.Length;
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '"') quoted = !quoted;
            else if (!quoted && (text[i] == ';' || text[i] == ','))
            {
                if (i < caret) start = i + 1;
                else { end = i; break; }
            }
        }
        return (start, end);
    }
}

/// <summary>
/// Address book suggestions for one or more address fields in a window: as the user types, the
/// contacts and groups matching what they typed appear in a popup list below the field. Down
/// moves into the list, Enter or Tab chooses, Escape or Up from the top returns to the field.
/// A group expands to its members. Shared by the compose window and the appointment editor
/// (#869), each of which declares its own <see cref="Popup"/> and <see cref="ListBox"/> in XAML
/// so the list is themed and named like the rest of that window.
/// </summary>
internal sealed class AddressSuggestionController : IDisposable
{
    private readonly Window _owner;
    private readonly IContactService _contacts;
    private readonly Popup _popup;
    private readonly ListBox _list;
    private readonly IRecipientHistory? _recents;
    private readonly Func<SuggestionSources> _sources;
    private readonly ISoundService? _sounds;
    private IAddressEntry? _active;
    private CancellationTokenSource? _searchCts;
    private Task? _searchTask;

    /// <summary>The typed text the showing list answers; null while no list is showing.</summary>
    private string? _shownFor;

    /// <summary>The field that tells UI Automation it controls the list, while one is showing.</summary>
    private IAddressEntry? _controlling;

    /// <summary>
    /// Whether QuickMail also says the list in words when it appears or changes ("Ann Lee
    /// ann@example.com, 3 suggestions"). Off: the fields expose the list through UI Automation
    /// (<c>ControllerFor</c>, the selected first suggestion, the list's layout), which screen
    /// readers report themselves, and saying it as well says it twice, as the command palette
    /// showed. A field rather than a constant so tests can exercise both.
    /// </summary>
    internal bool AnnounceList { get; set; }

    /// <summary>At most this many remembered people are offered alongside the address book (#1037).</summary>
    internal const int MaxRecentSuggestions = 10;

    /// <summary>Raised just before focus moves into the list, so the window can tell its own
    /// focus handlers that the trip back is not a fresh arrival in the field.</summary>
    public event Action? EnteringSuggestions;

    /// <param name="recents">People remembered from sent mail (#1037), or null for none.</param>
    /// <param name="sources">
    /// Read at each keystroke, so a change in Settings applies to a window already open: which of the
    /// address book and the remembered people to suggest from (<see cref="SuggestionSources.From"/>).
    /// Null suggests from the address book only.
    /// </param>
    /// <param name="sounds">
    /// Plays "Address suggestions appeared" and "Address inserted from suggestions", or null for no
    /// sounds. Both answer a key press, so each plays at once or not at all (<see cref="ISoundService.TryPlayNow"/>).
    /// </param>
    public AddressSuggestionController(Window owner, IContactService contacts, Popup popup, ListBox list,
        IRecipientHistory? recents = null, Func<SuggestionSources>? sources = null, ISoundService? sounds = null)
    {
        _owner = owner;
        _contacts = contacts;
        _popup = popup;
        _list = list;
        _recents = recents;
        _sounds = sounds;
        _sources = sources ?? (() => new SuggestionSources(AddressBook: true, PeopleEmailed: false));
        _list.PreviewKeyDown += List_PreviewKeyDown;
        _list.MouseLeftButtonUp += (_, _) =>
        {
            if (_list.SelectedItem is AddressSuggestion s) Accept(s, fromField: false);
        };
        // Focus leaving the list for anywhere but the field it serves closes it: left open, a
        // stale list would swallow the next Enter in the field and make Escape mean Cancel.
        _list.IsKeyboardFocusWithinChanged += (_, e) =>
        {
            if ((bool)e.NewValue) return;
            _owner.Dispatcher.InvokeAsync(() =>
            {
                if (!_list.IsKeyboardFocusWithin && _active?.Element.IsKeyboardFocusWithin != true)
                    _popup.IsOpen = false;
            }, DispatcherPriority.Background);
        };
        // However the popup closes, the field goes back to committing normally on losing focus,
        // and stops telling UI Automation it controls a list that is no longer there.
        _popup.Closed += (_, _) =>
        {
            _shownFor = null;
            if (_active != null) _active.SuppressLostFocusCommit = false;
            Control(null);
        };
    }

    /// <summary>True while the suggestion list is showing — the window's Escape handling steps aside.</summary>
    public bool IsOpen => _popup.IsOpen;

    public void Attach(IAddressEntry entry)
    {
        // Kept so a Tab pressed before the search for the last letter finishes can wait for it.
        entry.InputChanged += (_, _) => _searchTask = SearchAsync(entry);
        entry.Element.PreviewKeyDown += (_, e) => Field_PreviewKeyDown(entry, e);
        entry.Element.IsKeyboardFocusWithinChanged += (_, e) =>
        {
            if ((bool)e.NewValue) return;
            // Focus left the field: close the list, unless the list is what took focus.
            _owner.Dispatcher.InvokeAsync(() =>
            {
                if (!_list.IsKeyboardFocusWithin) _popup.IsOpen = false;
            }, DispatcherPriority.Background);
        };
    }

    private async Task SearchAsync(IAddressEntry entry)
    {
        _active = entry;
        var searchToken = entry.CanEdit ? entry.CurrentInput.Trim() : string.Empty;
        var sources = _sources();
        var fromRecents = sources.PeopleEmailed && _recents != null;
        // Nothing to search — suggestions turned off, or both sources — means no list, and with no list
        // Tab and Enter keep what was typed, exactly as before suggestions existed.
        if (searchToken.Length < 1 || (!sources.AddressBook && !fromRecents))
        {
            // Cancel a search still running for an earlier keystroke, or it could finish after this and
            // open the list again for text the field no longer holds.
            try { _searchCts?.Cancel(); } catch (ObjectDisposedException) { /* already superseded */ }
            _popup.IsOpen = false;
            return;
        }

        // Cancel and dispose the superseded search (its token was already captured by the
        // in-flight search, which unwinds via OperationCanceledException).
        var previous = _searchCts;
        _searchCts = new CancellationTokenSource();
        try { previous?.Cancel(); previous?.Dispose(); } catch { /* best effort */ }

        try
        {
            var ct = _searchCts.Token;
            // All three searches are cache-backed, so running them together is cheap.
            var contactTask = sources.AddressBook
                ? _contacts.SearchContactsAsync(searchToken, ct) : Task.FromResult(new List<ContactModel>());
            var groupTask   = sources.AddressBook
                ? _contacts.SearchGroupsAsync(searchToken, ct) : Task.FromResult(new List<GroupModel>());
            var recentTask  = fromRecents
                ? _recents!.SearchAsync(searchToken, MaxRecentSuggestions, ct)
                : Task.FromResult(new List<ContactModel>());
            await Task.WhenAll(contactTask, groupTask, recentTask);

            // Only when someone remembered matched, and the address book is being suggested too: the
            // whole address book, so a remembered person who is a contact outside the few best contact
            // matches is not offered twice. With the address book off, the remembered copy is the only
            // way to reach them, so it stays.
            HashSet<string>? addressBook = null;
            if (recentTask.Result.Count > 0 && sources.AddressBook)
            {
                var all = await _contacts.LoadAllContactsAsync();
                ct.ThrowIfCancellationRequested();
                addressBook = new HashSet<string>(all.Select(c => c.EmailAddress.Trim()), StringComparer.OrdinalIgnoreCase);
            }

            var combined = AddressSuggestionRanking.Combine(searchToken,
                groupTask.Result, contactTask.Result, recentTask.Result, addressBook);

            if (combined.Count == 0) { _popup.IsOpen = false; return; }

            // The sound marks the list appearing, not each keystroke that changes it while it shows.
            var appearing = !_popup.IsOpen;
            _list.ItemsSource      = combined;
            _shownFor              = searchToken;
            _popup.PlacementTarget = entry.Element;
            _popup.Placement       = PlacementMode.Bottom;
            _popup.IsOpen          = true;
            if (appearing) _sounds?.TryPlayNow(SoundEvent.AddressSuggestions);
            Control(entry);
            ExposeFirst(combined, searchToken);

            // Interrupting: each keystroke replaces the list, so a slower announcement of the list
            // before it is stale and should not be finished.
            if (AnnounceList)
                AccessibilityHelper.Announce(_owner, AnnouncementFor(combined, searchToken), interrupt: true,
                    category: AnnouncementCategory.Result);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer keystroke.
        }
        catch (Exception ex)
        {
            // The search is no longer awaited by an async void handler, so nothing else would see
            // this. No suggestions is the right outcome: the user can still type the address.
            LogService.Log("AddressSuggestions: searching for suggestions failed", ex);
            _popup.IsOpen = false;
        }
    }

    /// <summary>Moves the "controls this list" relationship to <paramref name="entry"/>, or clears it with null.</summary>
    private void Control(IAddressEntry? entry)
    {
        if (ReferenceEquals(_controlling, entry)) return;
        _controlling?.SetSuggestionList(null);
        _controlling = entry;
        entry?.SetSuggestionList(_list);
    }

    /// <summary>
    /// Tells UI Automation what the list now offers, with focus left in the field. When Tab would
    /// enter the first suggestion, that one is selected, which is what Windows' suggestion handling
    /// expects of a list with a default choice: the selection event names it, and its place in the
    /// list gives the count. When Tab would keep what was typed instead, nothing is selected, so
    /// nothing heard is a promise Tab does not keep, and the list reports that its contents changed.
    /// Selected after layout: the row's container has to exist for its selection to be reported.
    /// </summary>
    private void ExposeFirst(IReadOnlyList<AddressSuggestion> suggestions, string typed)
    {
        var tabTakesFirst = AddressSuggestionRanking.TabTakesFirst(suggestions, typed);
        _list.SelectedIndex = -1;
        _owner.Dispatcher.InvokeAsync(() =>
        {
            // A later keystroke may have replaced or closed the list since.
            if (!_popup.IsOpen || !ReferenceEquals(_list.ItemsSource, suggestions) || _list.IsKeyboardFocusWithin) return;
            if (tabTakesFirst)
            {
                _list.SelectedIndex = 0;
                _list.ScrollIntoView(_list.SelectedItem);
            }
            else
            {
                _controlling?.ReportSuggestionsChanged();
            }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// What is said when the list appears or changes (#1037), when <see cref="AnnounceList"/> is on. When Tab would enter the first
    /// suggestion, it is named before the count, since that is what a single key does; when Tab
    /// would keep what was typed instead, only the count is said, so nothing heard is a promise Tab
    /// does not keep.
    /// </summary>
    internal static string AnnouncementFor(IReadOnlyList<AddressSuggestion> suggestions, string typed)
    {
        var count = suggestions.Count == 1 ? "1 suggestion" : $"{suggestions.Count} suggestions";
        return AddressSuggestionRanking.TabTakesFirst(suggestions, typed)
            ? $"{suggestions[0].Display}, {count}"
            : count;
    }

    private void Field_PreviewKeyDown(IAddressEntry entry, KeyEventArgs e)
    {
        if (!_popup.IsOpen || !ReferenceEquals(entry, _active)) return;
        if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None)
        {
            // Tab enters the suggestion that was just announced, and stays in the field for the next
            // address (#1037). A second Tab, with the list gone, moves on as usual. Shift+Tab is left
            // alone so going back a field always works.
            if (IsCurrentFor(entry))
            {
                if (TabTakesShownFirst()) { e.Handled = AcceptFirst(); return; }
                // Nothing the user is typing towards: Tab keeps what was typed and moves on, as before.
                _popup.IsOpen = false;
            }
            else if (_searchTask is { IsCompleted: false })
            {
                // Typed faster than the search: the list still answers fewer letters than the field
                // holds, so its first entry may not even match. Wait for the search the last letter
                // started, then enter its first suggestion if Tab would take it.
                e.Handled = true;
                _ = AcceptFirstWhenCurrentAsync(entry);
            }
            else
            {
                // The list answers something other than what the field holds and no search is coming
                // to replace it — the caret moved to another entry in Invite. Drop it and let Tab move
                // on, so the key can never be trapped here.
                _popup.IsOpen = false;
            }
        }
        else if (e.Key == Key.Down)
        {
            // Suppress the field's lost-focus commit before focus moves into the popup: the
            // popup is a separate HwndSource, so the field loses keyboard focus the moment the
            // list takes it, which would otherwise commit the partial input.
            entry.SuppressLostFocusCommit = true;
            EnteringSuggestions?.Invoke();
            _list.Focus();
            _list.SelectedIndex = 0;
            (_list.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _popup.IsOpen = false;
            e.Handled = true;
        }
    }

    internal void List_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Tab)
        {
            // Handled at once so Enter does not reach a default button (the editor's Save).
            // Accepting is deferred so focus does not move from the popup's HwndSource to the
            // window while the key is still routing — that mid-event move is what triggers the
            // default button.
            e.Handled = true;
            if (_list.SelectedItem is AddressSuggestion s)
                _owner.Dispatcher.InvokeAsync(() => Accept(s, fromField: false), DispatcherPriority.Input);
        }
        else if (e.Key == Key.Escape)
        {
            _popup.IsOpen = false;
            _active?.FocusInput();
            e.Handled = true;
        }
        else if (e.Key == Key.Up && _list.SelectedIndex <= 0)
        {
            _popup.IsOpen = false;
            _active?.FocusInput();
            e.Handled = true;
        }
    }

    /// <summary>True when the list showing was built for exactly what the field now holds.</summary>
    private bool IsCurrentFor(IAddressEntry entry)
        => _shownFor != null && string.Equals(_shownFor, entry.CurrentInput.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether Tab enters the first of the list showing (<see cref="AddressSuggestionRanking.TabTakesFirst"/>).</summary>
    private bool TabTakesShownFirst()
        => _shownFor != null && _list.ItemsSource is IReadOnlyList<AddressSuggestion> shown
           && AddressSuggestionRanking.TabTakesFirst(shown, _shownFor);

    private async Task AcceptFirstWhenCurrentAsync(IAddressEntry entry)
    {
        try
        {
            if (_searchTask is { } pending) await pending;
            if (ReferenceEquals(entry, _active) && IsCurrentFor(entry) && TabTakesShownFirst())
            {
                AcceptFirst();
            }
            else
            {
                // Nothing Tab should enter: do what the held Tab would have done, and move on. Leaving
                // the field commits what was typed in compose, as Tab always did.
                _popup.IsOpen = false;
                if (entry.Element.IsKeyboardFocusWithin && Keyboard.FocusedElement is UIElement focused)
                    focused.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            }
        }
        catch (Exception ex)
        {
            LogService.Log("AddressSuggestions: entering the suggestion Tab waited for failed", ex);
            _popup.IsOpen = false;
        }
    }

    /// <summary>
    /// Chooses the first suggestion without going into the list — for a window where Enter in
    /// the field would otherwise do something else (the editor's Save). False when the list is
    /// not showing, so the key keeps its usual meaning.
    /// </summary>
    public bool AcceptFirst()
    {
        if (!_popup.IsOpen || _list.Items.Count == 0 || _list.Items[0] is not AddressSuggestion first) return false;
        Accept(first, fromField: true);
        return true;
    }

    /// <param name="fromField">
    /// Chosen without leaving the field (Tab, or Enter in the editor's Invite), so nothing else is
    /// said and what is said can cut off the list's now-stale announcement. Chosen in the list
    /// instead, focus goes back to the field, and the screen reader's reading of it is not cut off.
    /// </param>
    private void Accept(AddressSuggestion suggestion, bool fromField)
    {
        if (_active == null) return;
        var target = _active;
        if (suggestion.IsGroup)
        {
            _ = AcceptGroupAsync(target, suggestion.Group!, fromField);
        }
        else
        {
            target.AcceptSuggestion(suggestion.Contact!.DisplayName ?? string.Empty, suggestion.Contact.EmailAddress);
            Inserted(InsertedAnnouncement(suggestion.Contact.EmailAddress, target.FieldName), fromField);
        }
        _popup.IsOpen = false;
        target.FocusInput();
    }

    /// <summary>Says an address went in, and plays its sound. Typing on is silent, so without this nothing told the user.</summary>
    private void Inserted(string announcement, bool fromField)
    {
        _sounds?.TryPlayNow(SoundEvent.AddressInserted);
        AccessibilityHelper.Announce(_owner, announcement, interrupt: fromField, category: AnnouncementCategory.Result);
    }

    /// <summary>What is said when one address goes into a field: "ann@example.com added to To field".</summary>
    internal static string InsertedAnnouncement(string email, string fieldName) => $"{email.Trim()} added to {fieldName} field";

    /// <summary>What is said when a group's members go into a field: "3 addresses from group 'Team' added to To field".</summary>
    internal static string GroupInsertedAnnouncement(int count, string groupName, string fieldName) =>
        $"{(count == 1 ? "1 address" : $"{count} addresses")} from group '{groupName}' added to {fieldName} field";

    /// <summary>
    /// Expands a group into its members' addresses, most recently used first. The first member
    /// replaces the partial input; the rest are added after it.
    /// </summary>
    private async Task AcceptGroupAsync(IAddressEntry target, GroupModel group, bool fromField)
    {
        List<ContactModel> allContacts;
        try
        {
            allContacts = await _contacts.LoadAllContactsAsync();
        }
        catch (Exception ex)
        {
            LogService.Log($"AddressSuggestions: could not load the members of group '{group.Name}'", ex);
            AccessibilityHelper.Announce(_owner, $"Couldn't add the members of group '{group.Name}'.",
                category: AnnouncementCategory.Result);
            return;
        }
        var byId = allContacts.ToDictionary(c => c.Id);
        var members = group.MemberContactIds
            .Where(byId.ContainsKey)
            .Select(id => byId[id])
            .OrderByDescending(c => c.LastUsedTicks)
            .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (members.Count == 0) return;

        target.AcceptSuggestion(members[0].DisplayName ?? string.Empty, members[0].EmailAddress);
        for (var i = 1; i < members.Count; i++)
            target.AddAddress(members[i].DisplayName ?? string.Empty, members[i].EmailAddress);

        _ = _contacts.TouchGroupAsync(group.Id);

        Inserted(GroupInsertedAnnouncement(members.Count, group.Name, target.FieldName), fromField);
    }

    /// <summary>Cancels a search still running. Called from the owning window's OnClosed.</summary>
    public void Dispose()
    {
        try { _searchCts?.Cancel(); _searchCts?.Dispose(); } catch { /* best effort */ }
        _searchCts = null;
    }
}

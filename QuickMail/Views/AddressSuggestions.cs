using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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

    /// <summary>Bold first line shown in the popup row.</summary>
    public string PrimaryText => IsGroup
        ? Group!.Name
        : string.IsNullOrWhiteSpace(Contact!.DisplayName) ? Contact.EmailAddress : Contact.DisplayName;

    /// <summary>Dimmed second line shown below the primary text.</summary>
    public string SecondaryText => IsGroup
        ? $"{Group!.ResolvedMemberCount} member{(Group!.ResolvedMemberCount == 1 ? "" : "s")} — group"
        : Contact!.EmailAddress;

    /// <summary>Full accessible name read by the screen reader for the list item.</summary>
    public string Display => IsGroup
        ? $"{Group!.Name}, group, {Group!.ResolvedMemberCount} member{(Group!.ResolvedMemberCount == 1 ? "" : "s")}"
        : string.IsNullOrWhiteSpace(Contact!.DisplayName)
            ? Contact.EmailAddress
            : $"{Contact.DisplayName} {Contact.EmailAddress}";

    public AddressSuggestion(ContactModel c) { Contact = c; }
    public AddressSuggestion(GroupModel g)   { Group   = g; }

    public override string ToString() => Display;
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
    /// Set while focus is in the suggestion list, so a field that commits what it holds when it
    /// loses focus does not commit the partial text the user is choosing a replacement for.
    /// </summary>
    bool SuppressLostFocusCommit { set; }
}

/// <summary>Compose's chip box as an <see cref="IAddressEntry"/>.</summary>
internal sealed class TokenizedAddressEntry : IAddressEntry
{
    private readonly TokenizedAddressBox _box;
    public TokenizedAddressEntry(TokenizedAddressBox box)
    {
        _box = box;
        _box.InputTextChanged += (_, _) => InputChanged?.Invoke(this, EventArgs.Empty);
    }
    public FrameworkElement Element => _box;
    public string CurrentInput => _box.CurrentInputText;
    public bool CanEdit => true;
    public event EventHandler? InputChanged;
    public void AcceptSuggestion(string displayName, string email) => _box.AcceptSuggestion(displayName, email);
    public void AddAddress(string displayName, string email) => _box.AddAddress(displayName, email);
    public void FocusInput() => _box.FocusInput();
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
    public TextBoxAddressEntry(TextBox box)
    {
        _box = box;
        _box.TextChanged += (_, _) => InputChanged?.Invoke(this, EventArgs.Empty);
    }
    public FrameworkElement Element => _box;
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
    private IAddressEntry? _active;
    private CancellationTokenSource? _searchCts;

    /// <summary>Raised just before focus moves into the list, so the window can tell its own
    /// focus handlers that the trip back is not a fresh arrival in the field.</summary>
    public event Action? EnteringSuggestions;

    public AddressSuggestionController(Window owner, IContactService contacts, Popup popup, ListBox list)
    {
        _owner = owner;
        _contacts = contacts;
        _popup = popup;
        _list = list;
        _list.PreviewKeyDown += List_PreviewKeyDown;
        _list.MouseLeftButtonUp += (_, _) =>
        {
            if (_list.SelectedItem is AddressSuggestion s) Accept(s);
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
        // However the popup closes, the field goes back to committing normally on losing focus.
        _popup.Closed += (_, _) =>
        {
            if (_active != null) _active.SuppressLostFocusCommit = false;
        };
    }

    /// <summary>True while the suggestion list is showing — the window's Escape handling steps aside.</summary>
    public bool IsOpen => _popup.IsOpen;

    public void Attach(IAddressEntry entry)
    {
        entry.InputChanged += async (_, _) => await SearchAsync(entry);
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
        if (searchToken.Length < 1) { _popup.IsOpen = false; return; }

        // Cancel and dispose the superseded search (its token was already captured by the
        // in-flight search, which unwinds via OperationCanceledException).
        var previous = _searchCts;
        _searchCts = new CancellationTokenSource();
        try { previous?.Cancel(); previous?.Dispose(); } catch { /* best effort */ }

        try
        {
            var ct = _searchCts.Token;
            // Both searches are cache-backed, so running them together is cheap.
            var contactTask = _contacts.SearchContactsAsync(searchToken, ct);
            var groupTask   = _contacts.SearchGroupsAsync(searchToken, ct);
            await Task.WhenAll(contactTask, groupTask);

            // Groups first: picking a group is quicker than picking many people. Empty groups
            // are already excluded by SearchGroupsAsync.
            var combined = groupTask.Result.Select(g => new AddressSuggestion(g))
                .Concat(contactTask.Result.Select(c => new AddressSuggestion(c)))
                .ToList();

            if (combined.Count == 0) { _popup.IsOpen = false; return; }

            _list.ItemsSource      = combined;
            _popup.PlacementTarget = entry.Element;
            _popup.Placement       = PlacementMode.Bottom;
            _popup.IsOpen          = true;

            AccessibilityHelper.Announce(_owner,
                combined.Count == 1 ? "1 suggestion" : $"{combined.Count} suggestions",
                category: AnnouncementCategory.Result);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer keystroke.
        }
    }

    private void Field_PreviewKeyDown(IAddressEntry entry, KeyEventArgs e)
    {
        if (!_popup.IsOpen || !ReferenceEquals(entry, _active)) return;
        if (e.Key == Key.Down)
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
                _owner.Dispatcher.InvokeAsync(() => Accept(s), DispatcherPriority.Input);
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

    /// <summary>
    /// Chooses the first suggestion without going into the list — for a window where Enter in
    /// the field would otherwise do something else (the editor's Save). False when the list is
    /// not showing, so the key keeps its usual meaning.
    /// </summary>
    public bool AcceptFirst()
    {
        if (!_popup.IsOpen || _list.Items.Count == 0 || _list.Items[0] is not AddressSuggestion first) return false;
        Accept(first);
        return true;
    }

    private void Accept(AddressSuggestion suggestion)
    {
        if (_active == null) return;
        var target = _active;
        if (suggestion.IsGroup)
            _ = AcceptGroupAsync(target, suggestion.Group!);
        else
            target.AcceptSuggestion(suggestion.Contact!.DisplayName ?? string.Empty, suggestion.Contact.EmailAddress);
        _popup.IsOpen = false;
        target.FocusInput();
    }

    /// <summary>
    /// Expands a group into its members' addresses, most recently used first. The first member
    /// replaces the partial input; the rest are added after it.
    /// </summary>
    private async Task AcceptGroupAsync(IAddressEntry target, GroupModel group)
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

        AccessibilityHelper.Announce(_owner,
            members.Count == 1
                ? $"Inserted 1 address from group '{group.Name}'"
                : $"Inserted {members.Count} addresses from group '{group.Name}'",
            category: AnnouncementCategory.Result);
    }

    /// <summary>Cancels a search still running. Called from the owning window's OnClosed.</summary>
    public void Dispose()
    {
        try { _searchCts?.Cancel(); _searchCts?.Dispose(); } catch { /* best effort */ }
        _searchCts = null;
    }
}

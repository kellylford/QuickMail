using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

/// <summary>
/// Drives the View Manager dialog. Created fresh each time the dialog opens so it
/// always reflects the live application state.
/// </summary>
public partial class ViewManagerViewModel : ObservableObject
{
    private readonly IViewService     _viewService;
    private readonly IConfigService   _configService;
    private readonly ICommandRegistry _registry;

    // ── Current app state (snapshot when dialog opens) ───────────────────────────

    public MailFolderModel?  CurrentFolder  { get; }
    public AccountModel?     CurrentAccount { get; }
    public ViewMode          CurrentViewMode  { get; }
    public MessageFilter     CurrentFilter    { get; }
    public MessageSort       CurrentSort      { get; }

    // ── Saved views list ──────────────────────────────────────────────────────────

    public ObservableCollection<SavedView> SavedViews { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedView))]
    [NotifyPropertyChangedFor(nameof(HasNoSelectedView))]
    [NotifyPropertyChangedFor(nameof(SelectedFoldersSummary))]
    [NotifyPropertyChangedFor(nameof(SelectedModeSummary))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(ShowReadOnly))]
    [NotifyPropertyChangedFor(nameof(ShowEditPanel))]
    [NotifyCanExecuteChangedFor(nameof(ApplySelectedViewCommand))]
    private SavedView? _selectedView;

    public bool HasSelectedView   => SelectedView != null;
    public bool HasNoSelectedView => SelectedView == null;

    // ── Edit mode ─────────────────────────────────────────────────────────────────

    private readonly bool _isCreateMode;
    public bool IsCreateMode => _isCreateMode;
    public bool IsManageMode => !_isCreateMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowReadOnly))]
    [NotifyPropertyChangedFor(nameof(ShowEditPanel))]
    private bool _isEditMode;

    /// <summary>True when a view is selected and we are NOT in edit mode — show read-only summary + Edit/Delete buttons.</summary>
    public bool ShowReadOnly  => HasSelectedView && !IsEditMode;

    /// <summary>True when a view is selected and we ARE in edit mode — show the editing fields.</summary>
    public bool ShowEditPanel => HasSelectedView && IsEditMode;

    /// <summary>Fired when edit mode is entered so the code-behind can land focus on the first edit field.</summary>
    public event EventHandler? EditModeEntered;

    // ── Edit fields ───────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _editName = string.Empty;

    [ObservableProperty]
    private string _editHotkey = string.Empty;

    [ObservableProperty]
    private bool _editIsDefault;

    /// <summary>Bound to the day-limit TextBox. Empty string means no limit.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _editDaysOfMail = string.Empty;

    /// <summary>True when the view should show all mail with no day limit.
    /// When true, the day-limit textbox is disabled and ignored on save.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDayLimitFieldEnabled))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _editUnlimitedDays = true;

    /// <summary>The day-limit textbox is enabled only when the "show all" checkbox is off.</summary>
    public bool IsDayLimitFieldEnabled => !EditUnlimitedDays;

    // ── Auto-name tracking (create mode only) ─────────────────────────────────────

    /// <summary>True while EditName still holds the auto-generated suggestion (not user-edited).</summary>
    private bool _isAutoName;

    /// <summary>Guard that prevents OnEditNameChanged from clearing _isAutoName during a
    /// programmatic update triggered by a day-limit change.</summary>
    private bool _updatingAutoName;

    // ── Derived display for the selected view's saved state ───────────────────────

    public string SelectedFoldersSummary =>
        SelectedView == null ? string.Empty
        : SelectedView.Folders.Count > 0
            ? string.Join(" + ", SelectedView.Folders.Select(f => $"{f.AccountDisplayName} {f.FolderDisplayName}"))
        : !string.IsNullOrEmpty(SelectedView.VirtualFolderKey)
            ? VirtualFolderDisplayName(SelectedView.VirtualFolderKey)
        : "(no folders)";

    private static string VirtualFolderDisplayName(string key) => key switch
    {
        "AllMail"    => "All Mail",
        "AllInboxes" => "All Inboxes",
        "AllDrafts"  => "All Drafts",
        "AllSent"    => "All Sent",
        "AllTrash"   => "All Trash",
        var k when k.StartsWith("AccountMail:") => "Account Mail",
        _            => "Virtual folder",
    };

    public string SelectedModeSummary
    {
        get
        {
            if (SelectedView == null) return string.Empty;
            var s = $"{ModeLabel(SelectedView.ViewMode)}  ·  {FilterLabel(SelectedView.Filter)}  ·  {SortLabel(SelectedView.Sort)}";
            if (SelectedView.DaysOfMail.HasValue)
                s += $"  ·  Last {SelectedView.DaysOfMail} days";
            return s;
        }
    }

    public bool CanSave =>
        HasSelectedView &&
        !string.IsNullOrWhiteSpace(EditName) &&
        (EditUnlimitedDays || IsValidDayLimit(EditDaysOfMail));

    private static bool IsValidDayLimit(string text) =>
        int.TryParse(text?.Trim() ?? string.Empty, out var n) && n > 0;

    /// <summary>Strip any non-digit characters to keep the field numeric-only.
    /// Also triggers a name refresh so the auto-generated suggestion stays in sync.</summary>
    partial void OnEditDaysOfMailChanged(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits != value)
        {
            EditDaysOfMail = digits;   // re-enters this method once; no further change
            return;
        }
        UpdateAutoName();
    }

    /// <summary>When toggling unlimited, clear the textbox or seed it with a sensible default.
    /// Also refreshes the auto-generated name so it reflects the new day scope.</summary>
    partial void OnEditUnlimitedDaysChanged(bool value)
    {
        if (value)
        {
            EditDaysOfMail = string.Empty;
        }
        else if (string.IsNullOrWhiteSpace(EditDaysOfMail))
        {
            EditDaysOfMail = (CurrentDayLimit ?? 30).ToString();
        }
        UpdateAutoName();
    }

    /// <summary>Once the user edits the Name field manually, stop auto-updating it.</summary>
    partial void OnEditNameChanged(string value)
    {
        if (!_updatingAutoName)
            _isAutoName = false;
    }

    // ── Current-state summary shown at the top of the dialog ─────────────────────

    public string CurrentStateSummary
    {
        get
        {
            var sb = new StringBuilder();
            if (CurrentAccount != null)
                sb.Append(CurrentAccount.AccountLabel).Append(' ');
            if (CurrentFolder != null)
                sb.Append(CurrentFolder.DisplayName);
            var extras = new System.Collections.Generic.List<string>();
            if (CurrentViewMode != ViewMode.Messages)
                extras.Add(ModeLabel(CurrentViewMode.ToString().ToLowerInvariant()));
            if (CurrentFilter != MessageFilter.All)
                extras.Add(FilterLabel(CurrentFilter.ToString().ToLowerInvariant()));
            if (CurrentSort != MessageSort.DateDescending)
                extras.Add(SortLabel(CurrentSort.ToString()));
            if (CurrentDayLimit.HasValue)
                extras.Add($"last {CurrentDayLimit} days");
            if (extras.Count > 0)
                sb.Append(", ").Append(string.Join(" ", extras));
            return sb.ToString();
        }
    }

    /// <summary>
    /// True when the dialog's current state folder differs from the selected view's folder(s),
    /// which means a Save will trigger a Replace/Add prompt.
    /// Only meaningful when the current folder is a real IMAP folder (not a virtual sentinel).
    /// </summary>
    public bool CurrentFolderDiffersFromSelected
    {
        get
        {
            if (SelectedView == null || !IsRealImapFolder(CurrentFolder)) return false;
            return !SelectedView.Folders.Any(vf =>
                vf.AccountId == CurrentFolder!.AccountId &&
                string.Equals(vf.FolderFullName, CurrentFolder.FullName, StringComparison.OrdinalIgnoreCase));
        }
    }

    // ── Events raised so the code-behind can open auxiliary dialogs ───────────────

    /// <summary>
    /// Raised when the user clicks Set Hotkey. Args contains the captured gesture (or null on cancel).
    /// Code-behind opens <see cref="Views.KeyCaptureDialog"/> and calls <see cref="ApplyHotkey"/>.
    /// </summary>
    public event EventHandler? SetHotkeyRequested;

    /// <summary>Raised when the folder differs from the selected view and Save is clicked.</summary>
    public event EventHandler<FolderConflictEventArgs>? FolderConflictDetected;

    /// <summary>Raised whenever views are created, updated, or deleted so the VM caller can refresh.</summary>
    public event EventHandler? ViewsChanged;

    /// <summary>
    /// Set when the user presses "Apply View". The caller reads this after ShowDialog() returns
    /// and applies the view. Never set from inside an active event handler so there is no
    /// re-entrant parent-window mutation while the dialog loop is still running.
    /// </summary>
    public SavedView? ViewRequestedToApply { get; private set; }

    /// <summary>Raised to ask the dialog to close itself (see Apply View).</summary>
    public event EventHandler? CloseRequested;

    // ── Constructor ───────────────────────────────────────────────────────────────

    // ── Current day limit (snapshot when dialog opens) ───────────────────────────

    public int? CurrentDayLimit { get; }

    /// <summary>Named-flag sub-filter id from the main VM, captured into saved views.</summary>
    private readonly string? _activeFlagFilterId;

    public ViewManagerViewModel(
        IViewService     viewService,
        IConfigService   configService,
        ICommandRegistry registry,
        IEnumerable<SavedView>  savedViews,
        MailFolderModel? currentFolder,
        AccountModel?    currentAccount,
        ViewMode         currentViewMode,
        MessageFilter    currentFilter,
        MessageSort      currentSort,
        int?             currentDayLimit = null,
        bool             isCreateMode    = false,
        string?          activeFlagFilterId = null)
    {
        _viewService   = viewService;
        _configService = configService;
        _registry      = registry;
        _isCreateMode  = isCreateMode;

        CurrentFolder    = currentFolder;
        CurrentAccount   = currentAccount;
        CurrentViewMode  = currentViewMode;
        CurrentFilter    = currentFilter;
        CurrentSort      = currentSort;
        CurrentDayLimit  = currentDayLimit;
        _activeFlagFilterId = activeFlagFilterId;

        SavedViews = new ObservableCollection<SavedView>(savedViews);
    }

    // ── Commands ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true only for real IMAP folders — not virtual sentinels (All Inboxes, All Mail,
    /// view sentinels, account-mail sentinels, etc.) which all start with the NUL character.
    /// </summary>
    private static bool IsRealImapFolder(MailFolderModel? folder) =>
        folder != null &&
        !folder.IsHeader &&
        folder.AccountId != Guid.Empty &&
        !folder.FullName.StartsWith("\x00", StringComparison.Ordinal);

    [RelayCommand]
    private void SaveAsNew()
    {
        var view = new SavedView
        {
            Name         = GenerateName(),
            ViewMode     = CurrentViewMode.ToString().ToLowerInvariant(),
            Filter       = FilterKey(CurrentFilter),
            Sort         = SortKey(CurrentSort),
            DaysOfMail   = CurrentDayLimit,
            FlagFilterId = _activeFlagFilterId,
        };

        if (IsRealImapFolder(CurrentFolder) && CurrentAccount != null)
        {
            view.Folders.Add(new ViewFolder
            {
                AccountId          = CurrentAccount.Id,
                FolderFullName     = CurrentFolder!.FullName,
                AccountDisplayName = CurrentAccount.AccountLabel,
                FolderDisplayName  = CurrentFolder.DisplayName,
            });
        }
        else if (CurrentFolder != null &&
                 CurrentFolder.FullName.StartsWith("\x00", StringComparison.Ordinal))
        {
            // Strip the NUL sentinel prefix — storing it causes JSON serialization
            // edge cases. ApplyViewAsync prepends it again when looking up the folder.
            view.VirtualFolderKey = CurrentFolder.FullName.Substring(1);
        }

        SavedViews.Add(view);
        SelectedView   = view;
        EditName       = view.Name;
        EditHotkey     = string.Empty;
        EditIsDefault  = false;
        EditUnlimitedDays = !CurrentDayLimit.HasValue;
        EditDaysOfMail = CurrentDayLimit.HasValue ? CurrentDayLimit.Value.ToString() : string.Empty;

        // Mark the name as auto-generated so it updates live when the user changes the day limit
        // before confirming (OnEditNameChanged will clear this once the user types).
        _isAutoName = _isCreateMode;

        // Enter edit mode so the user can configure and name the new view.
        IsEditMode = true;
        EditModeEntered?.Invoke(this, EventArgs.Empty);

        Persist();
        ViewsChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (SelectedView == null) return;

        if (CurrentFolderDiffersFromSelected && CurrentFolder != null && CurrentAccount != null)
        {
            FolderConflictDetected?.Invoke(this, new FolderConflictEventArgs(
                CurrentFolder.DisplayName,
                SelectedView.Folders.Select(f => f.FolderDisplayName).ToList()));
            // The code-behind calls ResolveConflict after the user responds.
            return;
        }

        CommitEdits();
        Persist();
        ViewsChanged?.Invoke(this, EventArgs.Empty);
        // In manage mode, return to read-only view after saving.
        if (IsManageMode) IsEditMode = false;
    }

    /// <summary>Called by the code-behind after the user answers the folder-conflict prompt.</summary>
    public void ResolveConflict(FolderConflictResolution resolution)
    {
        if (SelectedView == null || CurrentFolder == null || !IsRealImapFolder(CurrentFolder) || CurrentAccount == null) return;

        if (resolution == FolderConflictResolution.Replace)
            SelectedView.Folders.Clear();

        if (resolution != FolderConflictResolution.Cancel)
        {
            if (!SelectedView.Folders.Any(vf =>
                    vf.AccountId == CurrentAccount.Id &&
                    string.Equals(vf.FolderFullName, CurrentFolder.FullName, StringComparison.OrdinalIgnoreCase)))
            {
                SelectedView.Folders.Add(new ViewFolder
                {
                    AccountId          = CurrentAccount.Id,
                    FolderFullName     = CurrentFolder.FullName,
                    AccountDisplayName = CurrentAccount.AccountLabel,
                    FolderDisplayName  = CurrentFolder.DisplayName,
                });
            }
            CommitEdits();
            Persist();
            ViewsChanged?.Invoke(this, EventArgs.Empty);
            OnPropertyChanged(nameof(SelectedFoldersSummary));
        }
    }

    [RelayCommand]
    private void Delete()
    {
        if (SelectedView == null) return;
        var view = SelectedView;        // capture before Remove() fires CollectionChanged,
                                        // which nulls SelectedView via the ListBox binding
        SavedViews.Remove(view);
        RemoveViewHotkey(view.Id);
        SelectedView      = null;
        EditName          = string.Empty;
        EditHotkey        = string.Empty;
        EditIsDefault     = false;
        EditUnlimitedDays = true;
        EditDaysOfMail    = string.Empty;
        Persist();
        ViewsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Removes the auto-created phantom view when the Save View dialog is dismissed
    /// without saving (Escape, ✕, Cancel).
    /// <para>
    /// Unlike <see cref="Delete"/>, this method deliberately does <b>not</b> fire
    /// <see cref="ViewsChanged"/>.  Firing it from inside <c>OnClosing</c> causes
    /// a re-entrant UI update on the parent window (menu rebuild, folder-tree sync)
    /// while the dialog's message loop is still unwinding, which results in a COM
    /// apartment violation and an application crash.  The caller
    /// (<c>OpenViewManager</c>) calls <c>UpdateSavedViews()</c> on the main VM after
    /// <c>ShowDialog()</c> returns, which is the safe point to do that work.
    /// </para>
    /// </summary>
    internal void CancelCreate()
    {
        if (SelectedView == null) return;
        var view = SelectedView;        // capture before collection change can null it
        SavedViews.Remove(view);
        RemoveViewHotkey(view.Id);
        Persist();
        // Do NOT fire ViewsChanged here.
    }

    /// <summary>Enter edit mode for the selected view.</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedView))]
    private void StartEdit()
    {
        IsEditMode = true;
        EditModeEntered?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Leave edit mode, restoring the edit fields to the saved view state.</summary>
    [RelayCommand]
    private void CancelEdit()
    {
        if (SelectedView != null)
        {
            EditName       = SelectedView.Name;
            EditHotkey     = GetEffectiveHotkey(SelectedView);
            EditIsDefault  = SelectedView.IsDefault;
            EditUnlimitedDays = !SelectedView.DaysOfMail.HasValue;
            EditDaysOfMail = SelectedView.DaysOfMail.HasValue ? SelectedView.DaysOfMail.Value.ToString() : string.Empty;
        }
        IsEditMode = false;
    }

    /// <summary>
    /// Stores the selected view as the one to apply and asks the dialog to close.
    /// The actual ApplyViewAsync call happens in OpenViewManager after ShowDialog() returns,
    /// so the dialog's message loop is fully dead before the parent window is mutated.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelectedView))]
    private void ApplySelectedView()
    {
        ViewRequestedToApply = SelectedView;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void RequestSetHotkey() => SetHotkeyRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Called by code-behind after KeyCaptureDialog returns a gesture.</summary>
    public void ApplyHotkey(Key key, ModifierKeys modifiers)
    {
        EditHotkey = GestureHelper.Format(key, modifiers);
    }

    [RelayCommand]
    private void ClearHotkey() => EditHotkey = string.Empty;

    // ── Selection sync ────────────────────────────────────────────────────────────

    partial void OnSelectedViewChanged(SavedView? value)
    {
        // Always leave edit mode when the selection changes.
        IsEditMode = false;

        if (value == null)
        {
            EditName          = string.Empty;
            EditHotkey        = string.Empty;
            EditIsDefault     = false;
            EditUnlimitedDays = true;
            EditDaysOfMail    = string.Empty;
        }
        else
        {
            EditName          = value.Name;
            EditHotkey        = GetEffectiveHotkey(value);
            EditIsDefault     = value.IsDefault;
            EditUnlimitedDays = !value.DaysOfMail.HasValue;
            EditDaysOfMail    = value.DaysOfMail.HasValue ? value.DaysOfMail.Value.ToString() : string.Empty;
        }
    }

    // ── Name generation ───────────────────────────────────────────────────────────

    /// <summary>Generates the initial suggested name using the app's current day limit snapshot.</summary>
    public string GenerateName() => BuildName(CurrentDayLimit);

    /// <summary>
    /// Regenerates the suggested name using the current edit-panel day limit values.
    /// Called while the user is still filling in the create-mode form so the name
    /// stays in sync as they change the day scope.
    /// </summary>
    private string GenerateNameWithEditedDays()
    {
        if (EditUnlimitedDays) return BuildName(null);
        if (IsValidDayLimit(EditDaysOfMail) && int.TryParse(EditDaysOfMail.Trim(), out var d))
            return BuildName(d);
        return BuildName(null);
    }

    /// <summary>
    /// If the name is still the auto-generated suggestion (not manually edited) and we are
    /// in create mode, refresh it to reflect the current day-limit edit state.
    /// </summary>
    private void UpdateAutoName()
    {
        if (!_isAutoName || !_isCreateMode) return;
        _updatingAutoName = true;
        EditName = GenerateNameWithEditedDays();
        _updatingAutoName = false;
    }

    /// <summary>Builds a suggested view name using the supplied day limit (or null for unlimited).</summary>
    private string BuildName(int? dayLimit)
    {
        var sb = new StringBuilder();

        // Only prefix the account name for real IMAP folders.
        // Virtual folders (All Mail, All Inboxes, All Drafts, etc.) span multiple
        // accounts, so prefixing one account name is misleading.
        if (CurrentAccount != null && IsRealImapFolder(CurrentFolder))
        {
            // Use first word of account display name if multi-word.
            var words = CurrentAccount.AccountLabel.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            sb.Append(words[0]);
            if (words.Length > 1) sb.Append(' ').Append(words[1]); // e.g. "The Idea" from "The Idea Place"
            sb.Append(' ');
        }

        sb.Append(CurrentFolder?.DisplayName ?? "Mail");

        var extras = new System.Collections.Generic.List<string>();
        if (CurrentViewMode != ViewMode.Messages)
            extras.Add(ModeLabel(CurrentViewMode.ToString().ToLowerInvariant()));
        if (CurrentFilter != MessageFilter.All)
            extras.Add(FilterLabel(CurrentFilter.ToString().ToLowerInvariant()));
        if (CurrentSort != MessageSort.DateDescending)
            extras.Add(SortLabel(CurrentSort.ToString()));
        extras.Add(dayLimit.HasValue ? $"last {dayLimit} days" : "all days");

        if (extras.Count > 0)
            sb.Append(", ").Append(string.Join(" ", extras));

        return sb.ToString();
    }

    // ── Private helpers ───────────────────────────────────────────────────────────

    private void CommitEdits()
    {
        if (SelectedView == null) return;

        // Update name
        SelectedView.Name = string.IsNullOrWhiteSpace(EditName) ? SelectedView.Name : EditName.Trim();

        // Update hotkey — save to view and to hotkeys.json
        var oldHotkey = SelectedView.Hotkey;
        SelectedView.Hotkey = string.IsNullOrWhiteSpace(EditHotkey) ? null : EditHotkey;

        if (SelectedView.Hotkey != oldHotkey)
            PersistHotkey(SelectedView);

        // Update default — clear any other default first
        if (EditIsDefault && !SelectedView.IsDefault)
        {
            foreach (var v in SavedViews.Where(v => v != SelectedView))
                v.IsDefault = false;
        }
        SelectedView.IsDefault = EditIsDefault;

        // Update day limit — "unlimited" checkbox wins; otherwise parse the text box
        SelectedView.DaysOfMail = EditUnlimitedDays
            ? null
            : (int.TryParse(EditDaysOfMail.Trim(), out var days) && days > 0 ? days : null);
    }

    private void Persist()
    {
        _viewService.Save(SavedViews.ToList());
    }

    /// <summary>
    /// Saves the view's hotkey as a user override in hotkeys.json so it appears in
    /// Settings → Keyboard Shortcuts and is picked up by FindByGesture at runtime.
    /// </summary>
    private void PersistHotkey(SavedView view)
    {
        var commandId = $"view.saved.{view.Id}";
        var cfg = _configService.Load();

        // Remove any existing binding for this view command.
        cfg.CustomHotkeys.RemoveAll(h => h.CommandId == commandId);

        // Add the new binding if a hotkey was set. Also remove any *other* binding that
        // claims the same gesture — without this, an orphan binding for a deleted view
        // (matching the same key combo) would win the FindByGesture iteration and silently
        // suppress this new binding. We can't reach those orphans through the SavedViews
        // conflict check in the dialog because the corresponding views no longer exist.
        if (!string.IsNullOrEmpty(view.Hotkey))
        {
            cfg.CustomHotkeys.RemoveAll(h =>
                string.Equals(h.Gesture, view.Hotkey, StringComparison.OrdinalIgnoreCase));
            cfg.CustomHotkeys.Add(new HotkeyBinding
            {
                CommandId = commandId,
                Gesture   = view.Hotkey,
            });
        }

        _configService.Save(cfg);

        // Refresh the registry so the new binding takes effect immediately.
        _registry.ApplyUserOverrides(cfg.CustomHotkeys);
    }

    private void RemoveViewHotkey(Guid viewId)
    {
        var commandId = $"view.saved.{viewId}";
        var cfg = _configService.Load();
        cfg.CustomHotkeys.RemoveAll(h => h.CommandId == commandId);
        _configService.Save(cfg);
        _registry.ApplyUserOverrides(cfg.CustomHotkeys);
    }

    /// <summary>Returns the effective hotkey: user override if present, otherwise the view's stored hotkey.</summary>
    private string GetEffectiveHotkey(SavedView view)
    {
        var commandId = $"view.saved.{view.Id}";
        var cfg       = _configService.Load();
        var binding   = cfg.CustomHotkeys.FirstOrDefault(h => h.CommandId == commandId);
        return binding?.Gesture ?? view.Hotkey ?? string.Empty;
    }

    // ── Label helpers (keep in sync with existing label patterns) ─────────────────

    private static string ModeLabel(string mode) => mode switch
    {
        "conversations" => "Conversations",
        "from"          => "From",
        "to"            => "To",
        _               => "Messages",
    };

    private static string FilterLabel(string filter) => filter switch
    {
        "unread"      => "Unread",
        "read"        => "Read",
        "attachments" => "With Attachments",
        "replied"     => "Replied",
        "forwarded"   => "Forwarded",
        "flagged"     => "Flagged",
        _             => "All",
    };

    private static string SortLabel(string sort) => sort switch
    {
        "dateAsc"   or nameof(MessageSort.DateAscending)   => "Oldest First",
        "alphaAsc"  or nameof(MessageSort.AlphaAscending)  => "A → Z",
        "alphaDesc" or nameof(MessageSort.AlphaDescending) => "Z → A",
        "countDesc" or nameof(MessageSort.CountDescending) => "Most Messages",
        "countAsc"  or nameof(MessageSort.CountAscending)  => "Fewest Messages",
        _                                                   => "Newest First",
    };

    private static string FilterKey(MessageFilter f) => f switch
    {
        MessageFilter.Unread          => "unread",
        MessageFilter.Read            => "read",
        MessageFilter.WithAttachments => "attachments",
        MessageFilter.Replied         => "replied",
        MessageFilter.Forwarded       => "forwarded",
        MessageFilter.ToMe            => "tome",
        MessageFilter.Flagged         => "flagged",
        _                             => "all",
    };

    private static string SortKey(MessageSort s) => s switch
    {
        MessageSort.DateAscending   => "dateAsc",
        MessageSort.AlphaAscending  => "alphaAsc",
        MessageSort.AlphaDescending => "alphaDesc",
        MessageSort.CountDescending => "countDesc",
        MessageSort.CountAscending  => "countAsc",
        _                           => "dateDesc",
    };
}

// ── Event args ────────────────────────────────────────────────────────────────────

public class FolderConflictEventArgs(string newFolder, IReadOnlyList<string> existingFolders) : EventArgs
{
    public string NewFolder { get; } = newFolder;
    public IReadOnlyList<string> ExistingFolders { get; } = existingFolders;
}

public enum FolderConflictResolution { Replace, Add, Cancel }

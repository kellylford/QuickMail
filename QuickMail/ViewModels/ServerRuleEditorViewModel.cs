using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;

namespace QuickMail.ViewModels;

/// <summary>
/// Create/edit form for a single server rule, limited to the editable common subset
/// (<c>docs/planning/server-rules-pm-dev-spec.md</c> §6.3). Rules outside that subset never reach
/// this editor — the list VM blocks Edit — because Graph PATCH replaces conditions/actions wholesale
/// and would drop what we don't model (§16).
/// </summary>
public partial class ServerRuleEditorViewModel : ObservableObject
{
    /// <summary>Identity carried through an edit so the save targets the right rule.</summary>
    private string _ruleId = string.Empty;
    private int _sequence;
    private JsonElement? _rawConditions;
    private JsonElement? _rawActions;
    private JsonElement? _rawExceptions;

    public bool IsNew { get; private init; }

    public string Title => IsNew ? "New server rule" : "Edit server rule";

    // ── Events (View subscribes) ────────────────────────────────────────────

    /// <summary>Ask the View to open the folder picker; returns the chosen folder id (or null).</summary>
    public event Func<(string Id, string Name)?>? PickFolderRequested;

    /// <summary>
    /// Raised on Save with the assembled rule; the owner persists it and returns an error message on
    /// failure (null on success). The editor stays open and shows the error when non-null, so a
    /// rejected save never silently loses the form.
    /// </summary>
    public event Func<ServerRuleModel, Task<string?>>? Saved;

    /// <summary>Raised when the editor window should close (Save or Cancel).</summary>
    public event Action? CloseRequested;

    public event Action<string, AnnouncementCategory>? AnnouncementRequested;

    // ── Factories ───────────────────────────────────────────────────────────

    public static ServerRuleEditorViewModel ForNew() => new() { IsNew = true, Name = string.Empty };

    public static ServerRuleEditorViewModel ForEdit(ServerRuleModel rule)
    {
        var vm = new ServerRuleEditorViewModel
        {
            IsNew = false,
            _ruleId = rule.Id,
            _sequence = rule.Sequence,
            _rawConditions = rule.RawConditions,
            _rawActions = rule.RawActions,
            _rawExceptions = rule.RawExceptions,
            Name = rule.DisplayName,
            IsEnabled = rule.IsEnabled,

            SenderContains = rule.SenderContains ?? string.Empty,
            FromAddresses = string.Join(", ", rule.FromAddresses),
            SentToAddresses = string.Join(", ", rule.SentToAddresses),
            SubjectContains = rule.SubjectContains ?? string.Empty,
            BodyOrSubjectContains = rule.BodyOrSubjectContains ?? string.Empty,
            BodyContains = rule.BodyContains ?? string.Empty,
            SentToMe = rule.SentToMe,
            SentOnlyToMe = rule.SentOnlyToMe,
            HasAttachments = rule.HasAttachments,

            MoveToFolder = !string.IsNullOrWhiteSpace(rule.MoveToFolderId),
            MoveToFolderId = rule.MoveToFolderId,
            MoveToFolderName = rule.MoveToFolderName,
            CopyToFolder = !string.IsNullOrWhiteSpace(rule.CopyToFolderId),
            CopyToFolderId = rule.CopyToFolderId,
            CopyToFolderName = rule.CopyToFolderName,
            MarkAsRead = rule.MarkAsRead,
            Delete = rule.Delete,
            ForwardTo = string.Join(", ", rule.ForwardTo),
            StopProcessingRules = rule.StopProcessingRules,
        };

        vm.SelectedImportance = ImportanceOptions.FirstOrDefault(o =>
            string.Equals(o.Value, rule.Importance, StringComparison.OrdinalIgnoreCase)) ?? ImportanceOptions[0];
        vm.SelectedMarkImportance = ImportanceOptions.FirstOrDefault(o =>
            string.Equals(o.Value, rule.MarkImportance, StringComparison.OrdinalIgnoreCase)) ?? ImportanceOptions[0];
        // If the rule already uses any advanced field, open the Advanced section so editing never
        // hides a populated field. A brand-new rule leaves it collapsed.
        vm.IsAdvancedExpanded = vm.HasAdvancedContent();
        return vm;
    }

    // ── Fields ──────────────────────────────────────────────────────────────

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private bool _isEnabled = true;

    /// <summary>
    /// Whether the Advanced conditions/actions section is expanded. Collapsed for a new rule; opened
    /// automatically when editing a rule that already uses an advanced field (see <see cref="ForEdit"/>).
    /// </summary>
    [ObservableProperty] private bool _isAdvancedExpanded;

    // Conditions
    [ObservableProperty] private string _senderContains = string.Empty;
    [ObservableProperty] private string _fromAddresses = string.Empty;
    [ObservableProperty] private string _sentToAddresses = string.Empty;
    [ObservableProperty] private string _subjectContains = string.Empty;
    [ObservableProperty] private string _bodyOrSubjectContains = string.Empty;
    [ObservableProperty] private string _bodyContains = string.Empty;
    [ObservableProperty] private bool _sentToMe;
    [ObservableProperty] private bool _sentOnlyToMe;
    [ObservableProperty] private bool _hasAttachments;
    [ObservableProperty] private ImportanceOption _selectedImportance = ImportanceOptions[0];

    // Actions
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMoveToFolderSelected))]
    private bool _moveToFolder;
    [ObservableProperty] private string? _moveToFolderId;
    [ObservableProperty] private string? _moveToFolderName;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCopyToFolderSelected))]
    private bool _copyToFolder;
    [ObservableProperty] private string? _copyToFolderId;
    [ObservableProperty] private string? _copyToFolderName;
    [ObservableProperty] private bool _markAsRead;
    [ObservableProperty] private ImportanceOption _selectedMarkImportance = ImportanceOptions[0];
    [ObservableProperty] private bool _delete;
    [ObservableProperty] private string _forwardTo = string.Empty;
    [ObservableProperty] private bool _stopProcessingRules;

    public bool IsMoveToFolderSelected => MoveToFolder;
    public bool IsCopyToFolderSelected => CopyToFolder;

    // Validation surfaces
    [ObservableProperty] private string _nameError = string.Empty;
    [ObservableProperty] private string _folderError = string.Empty;
    [ObservableProperty] private string _actionsError = string.Empty;
    /// <summary>A server-side save failure (e.g. Graph rejected the rule), shown on the form.</summary>
    [ObservableProperty] private string _saveError = string.Empty;

    /// <summary>Importance choices for both the condition and the action ComboBoxes.</summary>
    public static List<ImportanceOption> ImportanceOptions { get; } =
    [
        new() { Value = null, DisplayName = "Not set" },
        new() { Value = "low", DisplayName = "Low" },
        new() { Value = "normal", DisplayName = "Normal" },
        new() { Value = "high", DisplayName = "High" },
    ];

    // ── Commands ────────────────────────────────────────────────────────────

    [RelayCommand]
    private void PickFolder()
    {
        if (PickFolderRequested?.Invoke() is not { } picked) return;
        MoveToFolderId = picked.Id;
        MoveToFolderName = picked.Name;
        MoveToFolder = true;
        FolderError = string.Empty;
    }

    [RelayCommand]
    private void PickCopyFolder()
    {
        if (PickFolderRequested?.Invoke() is not { } picked) return;
        CopyToFolderId = picked.Id;
        CopyToFolderName = picked.Name;
        CopyToFolder = true;
        FolderError = string.Empty;
    }

    [RelayCommand]
    private async Task Save()
    {
        if (!Validate()) return;
        SaveError = string.Empty;

        // The owner persists and returns null on success, or an error to display. Close only on
        // success — a failed save keeps the form (and the user's input) and shows why.
        var error = Saved is null ? null : await Saved.Invoke(ToModel());
        if (string.IsNullOrEmpty(error))
        {
            CloseRequested?.Invoke();
            return;
        }

        SaveError = error;
        AnnouncementRequested?.Invoke(error, AnnouncementCategory.Result);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();

    // ── Assembly & validation ───────────────────────────────────────────────

    public ServerRuleModel ToModel() => new()
    {
        Id = _ruleId,
        Sequence = _sequence,
        DisplayName = Name.Trim(),
        IsEnabled = IsEnabled,

        SenderContains = Blank(SenderContains),
        FromAddresses = SplitAddresses(FromAddresses),
        SentToAddresses = SplitAddresses(SentToAddresses),
        SubjectContains = Blank(SubjectContains),
        BodyOrSubjectContains = Blank(BodyOrSubjectContains),
        BodyContains = Blank(BodyContains),
        SentToMe = SentToMe,
        SentOnlyToMe = SentOnlyToMe,
        HasAttachments = HasAttachments,
        Importance = SelectedImportance?.Value,

        MoveToFolderId = MoveToFolder ? MoveToFolderId : null,
        MoveToFolderName = MoveToFolder ? MoveToFolderName : null,
        CopyToFolderId = CopyToFolder ? CopyToFolderId : null,
        CopyToFolderName = CopyToFolder ? CopyToFolderName : null,
        MarkAsRead = MarkAsRead,
        MarkImportance = SelectedMarkImportance?.Value,
        Delete = Delete,
        ForwardTo = SplitAddresses(ForwardTo),
        StopProcessingRules = StopProcessingRules,

        // Only fully-representable rules ever reach this editor, so the assembled model is safe to
        // PATCH. Raw JSON is carried through unchanged for future merge-based editing.
        IsFullyEditable = true,
        RawConditions = _rawConditions,
        RawActions = _rawActions,
        RawExceptions = _rawExceptions,
    };

    public bool Validate()
    {
        NameError = FolderError = ActionsError = string.Empty;
        var valid = true;

        if (string.IsNullOrWhiteSpace(Name))
        {
            NameError = "Rule name is required.";
            valid = false;
        }

        if (MoveToFolder && string.IsNullOrWhiteSpace(MoveToFolderId))
        {
            FolderError = "Choose a folder for the Move to folder action.";
            valid = false;
        }

        if (CopyToFolder && string.IsNullOrWhiteSpace(CopyToFolderId))
        {
            FolderError = "Choose a folder for the Copy to folder action.";
            valid = false;
        }

        if (!HasAnyAction())
        {
            ActionsError = "Choose at least one action.";
            valid = false;
        }

        if (!valid)
        {
            var errors = new[] { NameError, FolderError, ActionsError }.Where(e => !string.IsNullOrEmpty(e));
            AnnouncementRequested?.Invoke(string.Join(" ", errors), AnnouncementCategory.Result);
        }

        return valid;
    }

    /// <summary>
    /// True when any field that lives in the Advanced section is set — used to auto-expand it when
    /// editing. Keep this list in sync with the Advanced group in ServerRuleEditorWindow.xaml.
    /// </summary>
    private bool HasAdvancedContent()
        => !string.IsNullOrWhiteSpace(SenderContains)
           || !string.IsNullOrWhiteSpace(SentToAddresses)
           || !string.IsNullOrWhiteSpace(BodyOrSubjectContains)
           || !string.IsNullOrWhiteSpace(BodyContains)
           || SentToMe || SentOnlyToMe || HasAttachments
           || !string.IsNullOrWhiteSpace(SelectedImportance?.Value)
           || CopyToFolder
           || !string.IsNullOrWhiteSpace(SelectedMarkImportance?.Value)
           || !string.IsNullOrWhiteSpace(ForwardTo);

    private bool HasAnyAction()
        => (MoveToFolder && !string.IsNullOrWhiteSpace(MoveToFolderId))
           || (CopyToFolder && !string.IsNullOrWhiteSpace(CopyToFolderId))
           || MarkAsRead
           || Delete
           || StopProcessingRules
           || !string.IsNullOrWhiteSpace(SelectedMarkImportance?.Value)
           || SplitAddresses(ForwardTo).Count > 0;

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Parses a free-text recipient field ("a@b.com, c@d.com; e@f.com").</summary>
    private static List<string> SplitAddresses(string text)
        => string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .Where(s => s.Length > 0)
                  .ToList();
}

/// <summary>
/// Importance choice for the condition/action ComboBoxes. <c>ToString()</c> is overridden because a
/// screen reader reads a Selector item's accessible name from it, not from DisplayMemberPath.
/// </summary>
public class ImportanceOption
{
    /// <summary>Graph value ("low"/"normal"/"high"), or null for "not set".</summary>
    public string? Value { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public override string ToString() => DisplayName;
}

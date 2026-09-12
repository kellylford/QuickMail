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

    public string Title => IsNew ? "New rule" : "Edit rule";

    // ── Events (View subscribes) ────────────────────────────────────────────

    /// <summary>
    /// Ask the View to open the folder picker; returns the chosen folder id (or null). The argument is
    /// the folder this action already targets, so the picker opens there — move and copy are separate
    /// fields, and a single parameterless request could not tell the View which one is asking.
    /// </summary>
    public event Func<string?, (string Id, string Name)?>? PickFolderRequested;

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

    /// <summary>A new rule prefilled from a message (create-rule-from-message, Ctrl+Shift+T): carries
    /// the message's From and Subject as conditions. It's still a new rule — the user picks the
    /// action, and it's classified server/client on save like any other New rule.
    ///
    /// Both fields carry their text whatever the template's flags say; the flags drive the matching
    /// condition checkbox instead (#665). Subject arrives switched OFF, because a rule that matches
    /// one sender AND one exact subject line matches, in practice, the single thread it was made
    /// from — which is not what "Rule for &lt;sender&gt;" means. The text is still sitting in the box,
    /// so turning the subject back on is one keystroke rather than retyping it.</summary>
    public static ServerRuleEditorViewModel ForNewFromTemplate(MailRule template)
    {
        var vm = new ServerRuleEditorViewModel
        {
            IsNew = true,
            Name = template.Name ?? string.Empty,
            FromAddresses = template.FromContains ?? string.Empty,
            UseFromAddresses = template.UseFromCondition,
            SubjectContains = template.SubjectContains ?? string.Empty,
            UseSubjectContains = template.UseSubjectCondition,
        };
        vm.IsAdvancedExpanded = vm.HasAdvancedContent();
        return vm;
    }

    public static ServerRuleEditorViewModel ForEdit(ServerRuleModel rule)
    {
        var vm = new ServerRuleEditorViewModel
        {
            IsNew = false,
            IsEditingServerRule = true,
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
        vm.SyncConditionSwitchesToContent();
        // If the rule already uses any advanced field, open the Advanced section so editing never
        // hides a populated field. A brand-new rule leaves it collapsed.
        vm.IsAdvancedExpanded = vm.HasAdvancedContent();
        return vm;
    }

    /// <summary>
    /// Populates the editor from a client-side <see cref="MailRule"/> (the inverse of
    /// <see cref="ToClientRule"/>), so a client rule can be edited in the same unified editor. The
    /// client model's single-value substring conditions map to the corresponding fields; its one
    /// action maps to that action. Editing preserves the rule's kind (client stays client) — the
    /// caller re-persists via the client rule service, it is not re-classified (spec §20.6).
    /// </summary>
    public static ServerRuleEditorViewModel ForEditClient(MailRule rule)
    {
        var vm = new ServerRuleEditorViewModel
        {
            IsNew = false,
            Name = rule.Name,
            IsEnabled = rule.IsEnabled,
            // A client condition is live only when its flag is set AND it has a value; the editor's
            // checkbox carries that same meaning, so the text comes across either way and the flag
            // decides whether the condition is switched on (#665).
            FromAddresses = rule.FromContains ?? string.Empty,
            UseFromAddresses = rule.UseFromCondition,
            SentToAddresses = rule.ToContains ?? string.Empty,
            UseSentToAddresses = rule.UseToCondition,
            SubjectContains = rule.SubjectContains ?? string.Empty,
            UseSubjectContains = rule.UseSubjectCondition,
            BodyContains = rule.BodyContains ?? string.Empty,
            UseBodyContains = rule.UseBodyCondition,
            HasAttachments = rule.MustHaveAttachments,
        };

        switch (rule.Action)
        {
            case RuleAction.MarkAsRead: vm.MarkAsRead = true; break;
            case RuleAction.MarkAsUnread: vm.MarkAsUnread = true; break;
            case RuleAction.MoveToFolder:
                vm.MoveToFolder = true;
                vm.MoveToFolderId = rule.TargetFolder;
                vm.MoveToFolderName = rule.TargetFolder;   // display name resolved by the owner if available
                break;
            case RuleAction.Delete: vm.Delete = true; break;
        }

        vm.SyncConditionSwitchesToContent();
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

    // ── Condition switches (#665) ───────────────────────────────────────────
    //
    // One per free-text condition, matching the checkbox in front of its field. Reported: the editor
    // filled From and Subject from the message and offered no way to say which of them the rule was
    // supposed to use, so it used both. The switch is what says so, and — unlike clearing the box —
    // it leaves the text in place, so a prefilled value can be turned back on with one keystroke.
    //
    // They start ON, matching the client Rules Manager, so a hand-made rule behaves exactly as before:
    // an empty field was, and still is, no condition. Loading an existing rule clears the switch on
    // every empty field (SyncConditionSwitchesToContent), so the editor reads back what the rule does.
    //
    // Everything downstream reads the gated Effective* values below — never the raw text — so a
    // switched-off condition is invisible to saving, classification and the Advanced auto-expand.
    [ObservableProperty] private bool _useSenderContains = true;
    [ObservableProperty] private bool _useFromAddresses = true;
    [ObservableProperty] private bool _useSentToAddresses = true;
    [ObservableProperty] private bool _useSubjectContains = true;
    [ObservableProperty] private bool _useBodyOrSubjectContains = true;
    [ObservableProperty] private bool _useBodyContains = true;

    private string EffectiveSenderContains => UseSenderContains ? SenderContains : string.Empty;
    private string EffectiveFromAddresses => UseFromAddresses ? FromAddresses : string.Empty;
    private string EffectiveSentToAddresses => UseSentToAddresses ? SentToAddresses : string.Empty;
    private string EffectiveSubjectContains => UseSubjectContains ? SubjectContains : string.Empty;
    private string EffectiveBodyOrSubjectContains => UseBodyOrSubjectContains ? BodyOrSubjectContains : string.Empty;
    private string EffectiveBodyContains => UseBodyContains ? BodyContains : string.Empty;

    /// <summary>
    /// Clears the switch on every condition that has no text, so an existing rule opens with exactly
    /// the conditions it actually uses switched on. Never switches one ON — a deliberately-off but
    /// prefilled field (Ctrl+Shift+T's subject) has to stay off.
    /// </summary>
    private void SyncConditionSwitchesToContent()
    {
        if (string.IsNullOrWhiteSpace(SenderContains)) UseSenderContains = false;
        if (string.IsNullOrWhiteSpace(FromAddresses)) UseFromAddresses = false;
        if (string.IsNullOrWhiteSpace(SentToAddresses)) UseSentToAddresses = false;
        if (string.IsNullOrWhiteSpace(SubjectContains)) UseSubjectContains = false;
        if (string.IsNullOrWhiteSpace(BodyOrSubjectContains)) UseBodyOrSubjectContains = false;
        if (string.IsNullOrWhiteSpace(BodyContains)) UseBodyContains = false;
    }

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
    /// <summary>Client-only action — Microsoft 365 server rules have no "mark as unread" (spec §20.2).</summary>
    [ObservableProperty] private bool _markAsUnread;

    /// <summary>True while editing an existing server-side rule. Editing never changes a rule's kind.</summary>
    public bool IsEditingServerRule { get; private set; }

    /// <summary>The account the rule belongs to, set by the owner when it opens the editor. The editor's folder
    /// picker scopes to it, whatever the list behind it has moved on to since (#683).</summary>
    public Guid? AccountId { get; set; }

    /// <summary>Mark as unread can't run in a server-side rule, so it is turned off while editing one (#684).
    /// A new rule keeps it: ticking it there makes the rule client-side.</summary>
    public bool CanMarkAsUnread => !IsEditingServerRule;
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
        if (PickFolderRequested?.Invoke(MoveToFolderId) is not { } picked) return;
        MoveToFolderId = picked.Id;
        MoveToFolderName = picked.Name;
        MoveToFolder = true;
        FolderError = string.Empty;
    }

    [RelayCommand]
    private void PickCopyFolder()
    {
        if (PickFolderRequested?.Invoke(CopyToFolderId) is not { } picked) return;
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

        SenderContains = Blank(EffectiveSenderContains),
        FromAddresses = SplitAddresses(EffectiveFromAddresses),
        SentToAddresses = SplitAddresses(EffectiveSentToAddresses),
        SubjectContains = Blank(EffectiveSubjectContains),
        BodyOrSubjectContains = Blank(EffectiveBodyOrSubjectContains),
        BodyContains = Blank(EffectiveBodyContains),
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

    /// <summary>
    /// Assembles a client-side <see cref="MailRule"/> from the client-representable subset of the
    /// form (spec §20.4). Only valid when <see cref="IsClientRepresentable"/> holds — the caller
    /// guarantees a single From/To value and exactly one action, so the mapping is lossless. The
    /// client engine treats a condition as active only when its flag is set AND it has a value, so
    /// empty conditions are simply switched off.
    /// </summary>
    public MailRule ToClientRule(Guid accountId)
    {
        var fromAddrs = SplitAddresses(EffectiveFromAddresses);
        var from = !string.IsNullOrWhiteSpace(EffectiveSenderContains) ? EffectiveSenderContains.Trim()
                 : fromAddrs.Count == 1 ? fromAddrs[0]
                 : null;
        var to = SplitAddresses(EffectiveSentToAddresses) is { Count: 1 } toList ? toList[0] : null;
        var subject = Blank(EffectiveSubjectContains);
        var body = Blank(EffectiveBodyContains);

        // Which conditions the rule actually matches on — decided before the switched-off text below
        // is carried in, so carrying it can never turn a condition on.
        var useFrom = from is not null;
        var useTo = to is not null;
        var useSubject = subject is not null;
        var useBody = body is not null;

        // A switched-off condition keeps its text in the saved rule with its flag clear, so reopening
        // the rule offers the text back instead of an empty box. That is MailRule's own meaning of the
        // flag — the engine, the row summary and the standalone Rules Manager's validation all require
        // the flag AND text — and it is what the standalone manager has always stored, so the same rule
        // no longer means different things depending on which window saved it. Each line fills only a
        // slot the switched-on conditions left empty: a populated-but-off Sender must never displace
        // the From address that IS in use.
        from ??= OffText(UseSenderContains, SenderContains) ?? OffSingleAddress(UseFromAddresses, FromAddresses);
        to ??= OffSingleAddress(UseSentToAddresses, SentToAddresses);
        subject ??= OffText(UseSubjectContains, SubjectContains);
        body ??= OffText(UseBodyContains, BodyContains);

        return new MailRule
        {
            Name = Name.Trim(),
            IsEnabled = IsEnabled,
            AccountId = accountId,

            UseFromCondition = useFrom, FromContains = from,
            UseToCondition = useTo, ToContains = to,
            UseSubjectCondition = useSubject, SubjectContains = subject,
            UseBodyCondition = useBody, BodyContains = body,
            MustHaveAttachments = HasAttachments,

            Action = ClientAction(),
            TargetFolder = MoveToFolder ? MoveToFolderId : null,
        };
    }

    /// <summary>The single client action in use (IsClientRepresentable guarantees exactly one).</summary>
    private RuleAction ClientAction()
    {
        if (MoveToFolder) return RuleAction.MoveToFolder;
        if (Delete) return RuleAction.Delete;
        if (MarkAsUnread) return RuleAction.MarkAsUnread;
        return RuleAction.MarkAsRead;
    }

    /// <summary>Shown when a Move or Delete rule tests nothing. Public so tests can pin the whole string.</summary>
    public const string NoConditionError =
        "Move and Delete need at least one condition, or the rule acts on every message.";

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

        // A rule that tests nothing matches every message, and rules run on Inbox mail as it arrives
        // and through Run on Existing Mail — so a condition-less Move or Delete empties the Inbox. The
        // client-only rules window refused this; the check did not come with it when every account
        // moved onto this editor (#412 for Microsoft 365, #550 for the rest). Server rules too:
        // Exchange applies a condition-less rule to every message just the same.
        if ((MoveToFolder || Delete) && !HasAnyCondition())
        {
            ActionsError = string.IsNullOrEmpty(ActionsError)
                ? NoConditionError
                : ActionsError + " " + NoConditionError;
            valid = false;
        }

        if (!valid)
        {
            var errors = new[] { NameError, FolderError, ActionsError }.Where(e => !string.IsNullOrEmpty(e));
            AnnouncementRequested?.Invoke(string.Join(" ", errors), AnnouncementCategory.Result);
        }

        return valid;
    }

    // ── Classification: server vs client (spec §20.3) ───────────────────────

    /// <summary>
    /// Decides where the rule runs. A Graph account gets a server rule unless the rule uses a
    /// client-only capability; otherwise (or on a non-Graph account) it's a client rule. A rule that
    /// fits neither — a client-only action combined with a
    /// server-only condition/action — is a conflict the user must resolve. Assumes the rule already
    /// passed <see cref="Validate"/> (so it has at least one action).
    /// </summary>
    public RuleClassification Classify(bool accountSupportsServerRules)
    {
        if (accountSupportsServerRules && IsServerRepresentable)
            return new RuleClassification { Kind = RuleRunsWhere.Server };

        if (IsClientRepresentable)
            return new RuleClassification { Kind = RuleRunsWhere.Client };

        // Representable by neither: a client-only action combined with a server-only condition/action,
        // or a server-only feature on a non-Graph account.
        var serverOnly = ServerOnlyFeaturesUsed();
        var clientOnly = ClientOnlyFeaturesUsed();
        var conflict = accountSupportsServerRules && clientOnly.Count > 0
            ? $"{Join(clientOnly)} only works in a client-side rule, but {Join(serverOnly)} only works in a server-side rule. Remove one to save."
            : $"This account only supports client-side rules, but {Join(serverOnly)} isn't available in a client-side rule. Remove it to save.";
        return new RuleClassification { ConflictError = conflict };
    }

    /// <summary>True when the rule uses no client-only capability, so the server can express it.</summary>
    public bool IsServerRepresentable => ClientOnlyFeaturesUsed().Count == 0;

    /// <summary>Why an edited server rule can't be saved as it stands, or null when it can. Editing keeps a
    /// rule's kind, and a server rule can't carry a client-only action (#684).</summary>
    public string? ServerEditError => IsServerRepresentable
        ? null
        : $"{Join(ClientOnlyFeaturesUsed())} only works in a client-side rule, and this rule runs on the server. Remove it to save.";

    /// <summary>
    /// True when every condition and action fits the client rule model (a near-subset of the server
    /// model): no server-only condition/action, single From/To value, exactly one action.
    /// </summary>
    public bool IsClientRepresentable
        => ServerOnlyFeaturesUsed().Count == 0 && ClientEligibleActionCount() == 1;

    /// <summary>Client-only capabilities in use — the server has no equivalent (spec §20.2). Extend
    /// as more client-only options are added (play sound, notify, …).</summary>
    private List<string> ClientOnlyFeaturesUsed()
    {
        var f = new List<string>();
        if (MarkAsUnread) f.Add("Mark as unread");
        return f;
    }

    /// <summary>
    /// Features only a server rule can express, so any of them blocks representing the rule as a
    /// client rule: conditions with no client equivalent, the client's single-value From/To limits,
    /// server-only actions, and the client's one-action limit.
    /// </summary>
    private List<string> ServerOnlyFeaturesUsed()
    {
        var f = new List<string>();

        // Conditions with no client equivalent.
        if (!string.IsNullOrWhiteSpace(EffectiveBodyOrSubjectContains)) f.Add("the subject-or-body condition");
        if (SentToMe) f.Add("the “sent to me” condition");
        if (SentOnlyToMe) f.Add("the “sent only to me” condition");
        if (SelectedImportance?.Value is not null) f.Add("the importance condition");

        // A client rule has a single From and a single To field.
        var fromAddrs = SplitAddresses(EffectiveFromAddresses);
        if (fromAddrs.Count > 1) f.Add("multiple From addresses");
        if (!string.IsNullOrWhiteSpace(EffectiveSenderContains) && fromAddrs.Count > 0)
            f.Add("both Sender-contains and From-addresses");
        if (SplitAddresses(EffectiveSentToAddresses).Count > 1) f.Add("multiple Sent-to addresses");

        // Actions with no client equivalent.
        if (CopyToFolder) f.Add("Copy to folder");
        if (SelectedMarkImportance?.Value is not null) f.Add("Set importance");
        if (SplitAddresses(ForwardTo).Count > 0) f.Add("Forward");
        if (StopProcessingRules) f.Add("Stop processing more rules");

        // A client rule performs exactly one action.
        if (ClientEligibleActionCount() > 1) f.Add("more than one action");

        return f;
    }

    /// <summary>Count of actions that a client rule could carry (it allows exactly one).</summary>
    private int ClientEligibleActionCount()
    {
        var n = 0;
        if (MarkAsRead) n++;
        if (MarkAsUnread) n++;
        if (MoveToFolder) n++;
        if (Delete) n++;
        return n;
    }

    private static string Join(List<string> items) => string.Join(", ", items);

    /// <summary>
    /// True when any field that lives in the Advanced section is set — used to auto-expand it when
    /// editing. Keep this list in sync with the Advanced group in ServerRuleEditorWindow.xaml.
    ///
    /// Deliberately the RAW text, not the switched-on value: a field holding text the user cannot see
    /// is exactly what "editing never hides a populated field" is here to prevent, and a condition
    /// that is switched off but populated is still something they need to be shown.
    /// </summary>
    private bool HasAdvancedContent()
        => !string.IsNullOrWhiteSpace(SenderContains)
           || !string.IsNullOrWhiteSpace(SentToAddresses)
           || !string.IsNullOrWhiteSpace(BodyOrSubjectContains)
           || !string.IsNullOrWhiteSpace(BodyContains)
           || SentToMe || SentOnlyToMe || HasAttachments
           || !string.IsNullOrWhiteSpace(SelectedImportance?.Value)
           || MarkAsUnread
           || CopyToFolder
           || !string.IsNullOrWhiteSpace(SelectedMarkImportance?.Value)
           || !string.IsNullOrWhiteSpace(ForwardTo);

    /// <summary>
    /// True when the saved rule would test anything at all. Reads the switched-on
    /// <c>Effective*</c> values — the same ones saving and classification read — so a condition
    /// switched on but left empty, and one holding text but switched off (#665), both count as
    /// absent: neither reaches the saved rule.
    /// <para>
    /// The address fields are parsed the way saving parses them, not merely checked for blankness.
    /// Saving splits them and drops empty entries, so a field holding only "," or ";" — or a
    /// separator left behind after deleting the address — is not blank, yet reaches the saved rule
    /// as no address at all. A blankness test let exactly that condition-less Delete through.
    /// </para>
    /// </summary>
    private bool HasAnyCondition()
        => !string.IsNullOrWhiteSpace(EffectiveSenderContains)
           || SplitAddresses(EffectiveFromAddresses).Count > 0
           || SplitAddresses(EffectiveSentToAddresses).Count > 0
           || !string.IsNullOrWhiteSpace(EffectiveSubjectContains)
           || !string.IsNullOrWhiteSpace(EffectiveBodyOrSubjectContains)
           || !string.IsNullOrWhiteSpace(EffectiveBodyContains)
           || SentToMe || SentOnlyToMe || HasAttachments
           || !string.IsNullOrWhiteSpace(SelectedImportance?.Value);

    private bool HasAnyAction()
        => (MoveToFolder && !string.IsNullOrWhiteSpace(MoveToFolderId))
           || (CopyToFolder && !string.IsNullOrWhiteSpace(CopyToFolderId))
           || MarkAsRead
           || MarkAsUnread
           || Delete
           || StopProcessingRules
           || !string.IsNullOrWhiteSpace(SelectedMarkImportance?.Value)
           || SplitAddresses(ForwardTo).Count > 0;

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>The text of a condition that is switched OFF — null when it is on, or has none.</summary>
    private static string? OffText(bool isOn, string raw) => isOn ? null : Blank(raw);

    /// <summary>Same, for an address list the client model can only hold one entry of.</summary>
    private static string? OffSingleAddress(bool isOn, string raw)
        => isOn ? null : SplitAddresses(raw) is { Count: 1 } one ? one[0] : null;

    /// <summary>Parses a free-text recipient field ("a@b.com, c@d.com; e@f.com").</summary>
    private static List<string> SplitAddresses(string text)
        => string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .Where(s => s.Length > 0)
                  .ToList();
}

/// <summary>
/// Result of classifying a rule (spec §20.3). Exactly one of these holds: <see cref="Kind"/> is
/// Server; <see cref="Kind"/> is Client; or <see cref="ConflictError"/> is set (the rule fits neither
/// and must be changed before saving). Client carried a reason string until #550 dropped the modal
/// save dialog that was its only reader.
/// </summary>
public sealed record RuleClassification
{
    public RuleRunsWhere? Kind { get; init; }
    public string? ConflictError { get; init; }
    public bool IsConflict => ConflictError is not null;
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

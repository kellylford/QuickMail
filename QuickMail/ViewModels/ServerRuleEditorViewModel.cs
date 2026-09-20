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
            // A template made from a message carries its sender as Sender contains, because a display
            // name can hold a comma and the address fields would read that as two addresses (#682).
            SenderContains = template.SenderContains ?? string.Empty,
            UseSenderContains = template.UseSenderCondition,
            FromAddresses = template.SenderContains is null ? template.FromContains ?? string.Empty : string.Empty,
            UseFromAddresses = template.SenderContains is null && template.UseFromCondition,
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
    /// client model's substring conditions map to the corresponding fields, and its actions to theirs. Editing preserves the rule's kind (client stays client) — the
    /// caller re-persists via the client rule service, it is not re-classified (spec §20.6).
    /// </summary>
    public static ServerRuleEditorViewModel ForEditClient(MailRule rule)
    {
        var from = ReadFromCondition(rule);
        var vm = new ServerRuleEditorViewModel
        {
            IsNew = false,
            Name = rule.Name,
            IsEnabled = rule.IsEnabled,
            // A client condition is live only when its flag is set AND it has a value; the editor's
            // checkbox carries that same meaning, so the text comes across either way and the flag
            // decides whether the condition is switched on (#665).
            FromAddresses = from.Addresses,
            UseFromAddresses = from.UseAddresses,
            SenderContains = from.Sender,
            UseSenderContains = from.UseSender,
            SentToAddresses = AddressText(rule.SentToAddresses, rule.ToContains),
            UseSentToAddresses = rule.UseToCondition,
            // One subject slot, which "subject or body" widens rather than replaces (#682), so the text
            // goes back to whichever box it came from.
            SubjectContains = rule.SubjectAlsoMatchesBody ? string.Empty : rule.SubjectContains ?? string.Empty,
            UseSubjectContains = !rule.SubjectAlsoMatchesBody && rule.UseSubjectCondition,
            BodyOrSubjectContains = rule.SubjectAlsoMatchesBody ? rule.SubjectContains ?? string.Empty : string.Empty,
            UseBodyOrSubjectContains = rule.SubjectAlsoMatchesBody && rule.UseSubjectCondition,
            BodyContains = rule.BodyContains ?? string.Empty,
            UseBodyContains = rule.UseBodyCondition,
            HasAttachments = rule.MustHaveAttachments,
        };

        foreach (var action in rule.AllActions())
        {
            switch (action)
            {
                case RuleAction.MarkAsRead: vm.MarkAsRead = true; break;
                case RuleAction.MarkAsUnread: vm.MarkAsUnread = true; break;
                case RuleAction.CopyToFolder:
                    vm.CopyToFolder = true;
                    vm.CopyToFolderId = rule.CopyTargetFolder;
                    vm.CopyToFolderName = rule.CopyTargetFolder;   // display name resolved by the owner if available
                    break;
                case RuleAction.MoveToFolder:
                    vm.MoveToFolder = true;
                    vm.MoveToFolderId = rule.TargetFolder;
                    vm.MoveToFolderName = rule.TargetFolder;   // display name resolved by the owner if available
                    break;
                case RuleAction.Delete: vm.Delete = true; break;
            }
        }

        vm.SyncConditionSwitchesToContent();
        vm.IsAdvancedExpanded = vm.HasAdvancedContent();
        return vm;
    }

    /// <summary>The editor text for a client rule's address condition: the list where the rule has one,
    /// otherwise the single value the list would have been written from.</summary>
    private static string AddressText(List<string>? addresses, string? single)
        => addresses is { Count: > 0 } ? string.Join(", ", addresses) : single ?? string.Empty;

    /// <summary>
    /// Which of the editor's two From boxes a client rule's From condition belongs in, and whether each
    /// is switched on. Three shapes reach this:
    /// <list type="bullet">
    /// <item>A rule carrying a sender and no address list keeps that sender in the old field too, for an
    /// older build (<see cref="MailRule.FromContainsMirrorsSender"/>). It belongs in the Sender box
    /// only, or the editor would show it twice.</item>
    /// <item>A rule with an address list shows the list, with any sender beside it.</item>
    /// <item>A rule from before #682 holds its whole From condition in one field, where a comma was
    /// never a separator: <b>Ctrl+Shift+T wrote the sender's DISPLAY NAME there</b>, and an Exchange
    /// address book routinely renders that "Last, First". Offered back as an address list it would be
    /// split in two on the next save, and a rule for one person would become a rule for anyone called
    /// either half. It goes in the Sender box, which is what it has always meant — a substring match on
    /// From — and saving it again writes back exactly the rule that was opened.</item>
    /// </list>
    /// </summary>
    private static (string Addresses, bool UseAddresses, string Sender, bool UseSender) ReadFromCondition(MailRule rule)
    {
        if (rule.FromContainsMirrorsSender)
            return (string.Empty, false, rule.SenderContains ?? string.Empty, rule.UseSenderCondition);

        if (rule.FromAddresses is { Count: > 0 })
            return (AddressText(rule.FromAddresses, rule.FromContains), rule.UseFromCondition,
                    rule.SenderContains ?? string.Empty, rule.UseSenderCondition);

        var single = rule.FromContains ?? string.Empty;
        return SplitAddresses(single).Count > 1
            ? (string.Empty, false, single, rule.UseFromCondition)
            : (single, rule.UseFromCondition, string.Empty, false);
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MoveToFolderButtonName))]
    private string? _moveToFolderName;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCopyToFolderSelected))]
    private bool _copyToFolder;
    [ObservableProperty] private string? _copyToFolderId;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CopyToFolderButtonName))]
    private string? _copyToFolderName;
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

    /// <summary>
    /// Whether the account this rule belongs to can carry server-side rules at all. Set by the owner when
    /// it opens the editor, from the account the editor opened ON — not whatever the list has moved to
    /// since (#683). Defaults to true so an editor opened without one offers everything and refuses on
    /// save, as it did before #682: failing open can never leave a field set that cannot be cleared.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseServerOnlyOptions))]
    [NotifyPropertyChangedFor(nameof(ServerOnlyOptionsDisabledReason))]
    [NotifyPropertyChangedFor(nameof(HasServerOnlyOptionsDisabledReason))]
    private bool _accountSupportsServerRules = true;

    /// <summary>True while editing a rule that already runs in QuickMail. Editing never changes a rule's
    /// kind (spec §20.6), so such a rule cannot take an option only a server rule can carry.</summary>
    private bool IsEditingClientRule => !IsNew && !IsEditingServerRule;

    /// <summary>
    /// Whether the options only a server-side rule can carry are available. False where the rule cannot
    /// become one — an account that has no server-side rules, or an existing client rule — and the editor
    /// turns those options off rather than offering them and refusing them on Save (#682).
    /// <para>
    /// The mirror of <see cref="CanMarkAsUnread"/>, which does the same for the one option only a client
    /// rule can carry (#684). Both fail open: an option that is somehow already set stays clearable.
    /// </para>
    /// </summary>
    public bool CanUseServerOnlyOptions
        => IsEditingServerRule || (AccountSupportsServerRules && !IsEditingClientRule);

    /// <summary>
    /// Why the server-only options are turned off, or empty when they are not. Shown on the form: a
    /// disabled control drops out of the Tab order, so without this the options do not so much read as
    /// unavailable as simply go missing, which is the one real cost of turning them off rather than
    /// labelling them (#682).
    /// </summary>
    public string ServerOnlyOptionsDisabledReason => CanUseServerOnlyOptions
        ? string.Empty
        : (IsEditingClientRule && AccountSupportsServerRules
            ? "This rule runs in QuickMail, and editing it doesn't change that, so these are turned off: "
            : "This account only has client-side rules, so these are turned off: ") + ServerOnlyOptionNames + ".";

    /// <summary>
    /// The options turned off, named. Whoever cannot see which controls are greyed cannot work out what
    /// is missing from a form they can no longer Tab to — and the save-time refusal this replaces DID
    /// name what was in the way. Said once here, which is not the six repeated "(server rules only)"
    /// labels that were turned down.
    /// </summary>
    internal const string ServerOnlyOptionNames =
        "Sent to me, Sent only to me, Importance is, Set importance to, Forward to, and Stop processing more rules";

    /// <summary>Whether there is a reason to show — what the notice's tab stop is gated on, so it is a
    /// stop only when it has something to say.</summary>
    public bool HasServerOnlyOptionsDisabledReason => !CanUseServerOnlyOptions;
    [ObservableProperty] private ImportanceOption _selectedMarkImportance = ImportanceOptions[0];
    [ObservableProperty] private bool _delete;
    [ObservableProperty] private string _forwardTo = string.Empty;
    [ObservableProperty] private bool _stopProcessingRules;

    public bool IsMoveToFolderSelected => MoveToFolder;
    public bool IsCopyToFolderSelected => CopyToFolder;

    /// <summary>
    /// What the move-to folder button is called. It has to carry the folder itself: a fixed
    /// <c>AutomationProperties.Name</c> overrides the button's text, so the button was announced as "Choose
    /// move-to folder" whether or not a folder was chosen, and a saved rule read as though its folder had been
    /// lost — while the text beside it said otherwise, which is why sighted review never caught it. The purpose
    /// stays in the name because the folder alone ("Kept") would not say which of the two folder buttons it is.
    /// </summary>
    public string MoveToFolderButtonName => string.IsNullOrWhiteSpace(MoveToFolderName)
        ? "Choose move-to folder"
        : $"Move to folder: {MoveToFolderName}";

    /// <summary>The copy-to folder button's name, composed as <see cref="MoveToFolderButtonName"/> is.</summary>
    public string CopyToFolderButtonName => string.IsNullOrWhiteSpace(CopyToFolderName)
        ? "Choose copy-to folder"
        : $"Copy to folder: {CopyToFolderName}";

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
    /// guarantees only conditions and actions a client rule can hold, so the mapping is lossless. The
    /// client engine treats a condition as active only when its flag is set AND it has a value, so
    /// empty conditions are simply switched off.
    /// </summary>
    public MailRule ToClientRule(Guid accountId)
    {
        // Each condition is taken twice over. Whether it is live comes from the switched-ON value; its
        // TEXT is carried into the saved rule either way, with the flag clear when it is off, so reopening
        // the rule offers the text back instead of an empty box (#665). That is MailRule's own meaning of
        // the flag — the engine and the row summary both require the flag AND a value — so the same rule
        // means the same thing whichever window saved it. Carried text never displaces a condition that is
        // in use: a populated-but-off field only ever fills a slot the live conditions left empty.
        var sender = Blank(SenderContains);
        var senderOn = !string.IsNullOrWhiteSpace(EffectiveSenderContains);
        var fromAddrs = SplitAddresses(FromAddresses);
        var fromOn = UseFromAddresses && fromAddrs.Count > 0;
        var toAddrs = SplitAddresses(SentToAddresses);
        var toOn = UseSentToAddresses && toAddrs.Count > 0;
        var body = Blank(EffectiveBodyContains) ?? OffText(UseBodyContains, BodyContains);
        var actions = ClientActions();

        var rule = new MailRule
        {
            Name = Name.Trim(),
            IsEnabled = IsEnabled,
            AccountId = accountId,

            UseBodyCondition = !string.IsNullOrWhiteSpace(EffectiveBodyContains), BodyContains = body,
            MustHaveAttachments = HasAttachments,

            Action = MainAction(actions),
            Actions = actions.Count > 1 ? actions : null,
            TargetFolder = MoveToFolder ? MoveToFolderId : null,
            CopyTargetFolder = CopyToFolder ? CopyToFolderId : null,
        };

        // From. The addresses and Sender contains are two conditions now (#682), but only one field in
        // the rule is old enough for a QuickMail from before #682 to read, so whichever of them is LIVE
        // owns it — such a build must never be left testing the From header on nothing at all, which
        // would let a Move rule empty the Inbox. Where the addresses own it they leave the first of
        // them there, which matches less mail than the whole list, never more. A rule using BOTH is the
        // exception, and the only one: such a build tests the addresses without the sender beside them.
        // See MailRule.SenderContains.
        rule.SenderContains = sender;
        rule.UseSenderCondition = senderOn;

        if (fromOn)
        {
            rule.UseFromCondition = true;
            rule.FromContains = fromAddrs[0];
            if (fromAddrs.Count > 1 || sender is not null) rule.FromAddresses = fromAddrs;
        }
        else if (senderOn)
        {
            // Sender contains is the live condition, so the old field mirrors it (FromValues reads
            // SenderContains instead). The switched-off address text has nowhere left to sit and is
            // dropped: the alternative is leaving the old field holding addresses the rule does not use.
            rule.UseFromCondition = true;
            rule.FromContains = sender;
        }
        else
        {
            // Neither is live: keep both texts, inert, so reopening the rule offers them back.
            rule.UseFromCondition = false;
            rule.FromContains = sender ?? fromAddrs.FirstOrDefault();
            if (fromAddrs.Count > 0) rule.FromAddresses = fromAddrs;
        }

        // Sent-to: the same, without a Sender-contains equivalent to share the slot with. A list left
        // switched off is kept whole, and reads as no condition in either build.
        rule.UseToCondition = toOn;
        rule.ToContains = toAddrs.FirstOrDefault();
        if (toAddrs.Count > 1) rule.SentToAddresses = toAddrs;

        ApplySubjectCondition(rule);
        return rule;
    }

    /// <summary>
    /// Fills the rule's one subject condition from the editor's two subject boxes: "Subject or body
    /// contains" is that condition widened to the body (<see cref="MailRule.SubjectAlsoMatchesBody"/>),
    /// not a second one. A rule that uses both at once is refused before it gets here
    /// (<see cref="ClientModelLimits"/>), so at most one of them is live; a switched-off text takes the
    /// slot only when neither is, and the other box's text is not kept — there is one slot, and a
    /// condition in use has first claim on it.
    /// </summary>
    private void ApplySubjectCondition(MailRule rule)
    {
        var subject = Blank(SubjectContains);
        var subjectOn = !string.IsNullOrWhiteSpace(EffectiveSubjectContains);
        var subjectOrBody = Blank(BodyOrSubjectContains);
        var subjectOrBodyOn = !string.IsNullOrWhiteSpace(EffectiveBodyOrSubjectContains);

        var useSubjectOrBody = subjectOrBodyOn || (!subjectOn && subject is null && subjectOrBody is not null);
        rule.SubjectContains = useSubjectOrBody ? subjectOrBody : subject;
        rule.UseSubjectCondition = useSubjectOrBody ? subjectOrBodyOn : subjectOn;
        rule.SubjectAlsoMatchesBody = useSubjectOrBody;
    }

    /// <summary>
    /// The rule's main action: what a QuickMail from before #682 does, since it reads that field alone. The last to
    /// run, so the move where there is one — but never Copy, which such a build doesn't have and would perform as
    /// nothing at all, dropping the marking or filing it could have done instead.
    /// <para>
    /// Nor Delete, when the rule also copies. Binning the message while never making the copy keeps the destructive
    /// half of a "keep a copy, then delete" rule and drops the half it exists for — the same loss the running engine
    /// refuses to allow when a copy fails. Such a rule names the copy, so an older build does nothing with it.
    /// </para>
    /// </summary>
    private static RuleAction MainAction(List<RuleAction> actions)
    {
        var copies = actions.Contains(RuleAction.CopyToFolder);
        var understood = actions
            .Where(a => a != RuleAction.CopyToFolder && !(copies && a == RuleAction.Delete))
            .ToList();
        if (understood.Count > 0) return understood[^1];
        return copies ? RuleAction.CopyToFolder
             : actions.Count > 0 ? actions[^1]
             : RuleAction.MarkAsRead;
    }

    /// <summary>The client actions in use, in the order a client rule runs them (<see cref="MailRule.AllActions"/>).</summary>
    private List<RuleAction> ClientActions()
    {
        var actions = new List<RuleAction>();
        if (MarkAsRead) actions.Add(RuleAction.MarkAsRead);
        if (MarkAsUnread) actions.Add(RuleAction.MarkAsUnread);
        if (CopyToFolder) actions.Add(RuleAction.CopyToFolder);
        if (MoveToFolder) actions.Add(RuleAction.MoveToFolder);
        if (Delete) actions.Add(RuleAction.Delete);
        return actions;
    }

    /// <summary>Shown when a Move, Copy or Delete rule tests nothing. Public so tests can pin the whole string.</summary>
    public const string NoConditionError =
        "Move, Copy and Delete need at least one condition, or the rule acts on every message.";

    /// <summary>Shown when a rule would mark a message both read and unread. Public so tests can pin it.</summary>
    public const string ReadAndUnreadError = "Choose Mark as read or Mark as unread, not both.";

    /// <summary>Shown when a rule would mark a message unread and then move or delete it. Public so tests can pin it.</summary>
    public const string UnreadWithMoveError =
        "Mark as unread only changes this computer's copy of the message, so it can't be combined with Move to folder or Delete.";

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

        // Opposites, which a client rule could carry together once it could have more than one action (#682).
        // Only a client rule can mark unread, so no server-side rule is refused here that wasn't before.
        if (MarkAsRead && MarkAsUnread)
        {
            ActionsError = string.IsNullOrEmpty(ActionsError)
                ? ReadAndUnreadError
                : ActionsError + " " + ReadAndUnreadError;
            valid = false;
        }

        // Marking unread happens only in QuickMail's own copy of the message — there is no server call for it — so a
        // rule that then moves or deletes the message throws that copy away, and the mark with it. The rule would
        // appear to do something and do nothing, so it is refused rather than saved (#682 review).
        //
        // Deliberately "else": every error is spoken as one sentence, and once the unread tick has been called wrong
        // above, saying so a second way in the same breath adds nothing but length.
        else if (MarkAsUnread && (MoveToFolder || Delete))
        {
            ActionsError = string.IsNullOrEmpty(ActionsError)
                ? UnreadWithMoveError
                : ActionsError + " " + UnreadWithMoveError;
            valid = false;
        }

        // A rule that tests nothing matches every message, and rules run on Inbox mail as it arrives
        // and through Run on Existing Mail — so a condition-less Move or Delete empties the Inbox. The
        // client-only rules window refused this; the check did not come with it when every account
        // moved onto this editor (#412 for Microsoft 365, #550 for the rest). Server rules too:
        // Exchange applies a condition-less rule to every message just the same. Copy joined them with #682:
        // it would copy every message that arrives, and every run of Run on Existing Mail would copy the lot again.
        if ((MoveToFolder || CopyToFolder || Delete) && !HasAnyCondition())
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
        // A backstop since #682: the editor turns the server-only options off wherever the rule cannot
        // become a server rule, so on a client-only account they cannot be set to reach here. Kept
        // because the gate fails OPEN — an editor nobody told about the account offers everything — and
        // because nothing but this stands between a hand-made caller and an unsaveable rule.
        var serverOnly = ServerOnlyFeaturesUsed();
        var clientOnly = ClientOnlyFeaturesUsed();
        var runsOnClientBecause = accountSupportsServerRules && clientOnly.Count > 0
            ? $"{Join(clientOnly)} only works in a client-side rule"
            : "This account only supports client-side rules";

        string conflict;
        if (serverOnly.Count > 0)
        {
            conflict = accountSupportsServerRules && clientOnly.Count > 0
                ? $"{Join(clientOnly)} only works in a client-side rule, but {Join(serverOnly)} only works in a server-side rule. Remove one to save."
                : $"This account only supports client-side rules, but {Join(serverOnly)} isn't available in a client-side rule. Remove it to save.";
        }
        else
        {
            // Nothing here is a server feature the user could go and find: the client rule model itself can't hold
            // the combination, so the message says what a client-side rule can't do rather than where it lives.
            //
            // With move+delete the only limit, the "Mark as unread only works in a client-side rule, which …" form is
            // currently unreachable — Validate refuses unread with move or delete before a save gets this far. The
            // composition stands for the limits that follow; don't read the one test here as covering both forms.
            conflict = $"{runsOnClientBecause}, which {Join(ClientModelLimits())}. Change one to save.";
        }
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
    /// Why an edited client rule can't be saved as it stands, or null when it can — the mirror of
    /// <see cref="ServerEditError"/>. It names what is wrong, as the new-rule path's
    /// <see cref="Classify"/> does: "remove what client-side rules don't support" sends the user hunting
    /// for an unsupported condition that may not be there, when the fault is a combination of two
    /// perfectly supported ones (#682).
    /// </summary>
    public string? ClientEditError
    {
        get
        {
            if (IsClientRepresentable) return null;

            // Unreachable through the editor on every path, not merely the usual one: editing a client
            // rule turns these off whatever the account can do (IsEditingClientRule alone closes the
            // gate), and ForEditClient cannot populate a server-only field in the first place — MailRule
            // has none. Kept as the backstop for a caller that assembles a VM by hand.
            var serverOnly = ServerOnlyFeaturesUsed();
            if (serverOnly.Count > 0)
                return $"This rule runs in QuickMail, but {Join(serverOnly)} isn't available in a client-side rule. Remove it to save.";

            var limits = ClientModelLimits();
            if (limits.Count > 0)
                return $"This rule runs in QuickMail, which {Join(limits)}. Change one to save.";

            // Nothing left but an actionless rule, which Validate refuses before this is read.
            return null;
        }
    }

    /// <summary>
    /// True when every condition and action fits the client rule model (a near-subset of the server
    /// model): no server-only condition/action, single From/To value, a combination the client model can
    /// hold, and at least one action a client rule can do.
    /// </summary>
    public bool IsClientRepresentable
        => ServerOnlyFeaturesUsed().Count == 0 && ClientModelLimits().Count == 0 && ClientActions().Count > 0;

    /// <summary>
    /// What the client rule model can't express, phrased to follow "a client-side rule, which …". Unlike
    /// <see cref="ServerOnlyFeaturesUsed"/> these are not features the server has and the client lacks — a server rule
    /// can carry both a move and a delete — so the message must not send the user looking for a setting (#682).
    /// </summary>
    private List<string> ClientModelLimits()
    {
        var f = new List<string>();
        if (MoveToFolder && Delete) f.Add("can't both move and delete a message");
        // A client rule has one subject condition, which "Subject or body contains" widens rather than
        // joins (#682, MailRule.SubjectAlsoMatchesBody), so the two can't both be switched on.
        if (!string.IsNullOrWhiteSpace(EffectiveSubjectContains)
            && !string.IsNullOrWhiteSpace(EffectiveBodyOrSubjectContains))
            f.Add("can't use Subject contains and Subject or body contains at the same time");
        return f;
    }

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
    /// client rule: conditions with no client equivalent, the client's single-value From/To limits, and
    /// server-only actions. A combination the client model can't hold is <see cref="ClientModelLimits"/> instead.
    /// </summary>
    private List<string> ServerOnlyFeaturesUsed()
    {
        var f = new List<string>();

        // Conditions with no client equivalent. Several From or Sent-to addresses, Sender contains
        // alongside them, and the subject-or-body condition were here until #682, which gave the client
        // rule model all four.
        if (SentToMe) f.Add("the “sent to me” condition");
        if (SentOnlyToMe) f.Add("the “sent only to me” condition");
        if (SelectedImportance?.Value is not null) f.Add("the importance condition");

        // Actions with no client equivalent.
        if (SelectedMarkImportance?.Value is not null) f.Add("Set importance");
        if (SplitAddresses(ForwardTo).Count > 0) f.Add("Forward");
        if (StopProcessingRules) f.Add("Stop processing more rules");

        return f;
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
        => !string.IsNullOrWhiteSpace(FromAddresses)
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

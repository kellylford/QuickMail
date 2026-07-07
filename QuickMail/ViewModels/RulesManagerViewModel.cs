using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;
using QuickMail.Resources;
using QuickMail.Services;

namespace QuickMail.ViewModels;

public partial class RulesManagerViewModel : ObservableObject
{
    private readonly IRuleService _ruleService;
    private readonly IEnumerable<AccountModel> _accounts;
    private readonly IEnumerable<MailMessageSummary>? _selectedMessagesForTest;

    // ── Events (View subscribes) ────────────────────────────────────────────

    /// <summary>Raised when the View should show a confirmation dialog.</summary>
    public event Func<string, string, bool>? ConfirmDeleteRequested;

    /// <summary>Raised when the dialog should close.</summary>
    public event Action? CloseRequested;

    /// <summary>Raised for screen-reader announcements.</summary>
    public event Action<string, AnnouncementCategory>? AnnouncementRequested;

    /// <summary>Raised when the View should open a folder picker for the target folder.</summary>
    public event Func<string?>? PickFolderRequested;

    // ── Constructor ─────────────────────────────────────────────────────────

    public RulesManagerViewModel(
        IRuleService ruleService,
        IEnumerable<AccountModel> accounts,
        MailRule? prefillTemplate = null,
        IEnumerable<MailMessageSummary>? selectedMessagesForTest = null)
    {
        _ruleService = ruleService;
        _accounts = accounts;
        _selectedMessagesForTest = selectedMessagesForTest;

        var rules = _ruleService.LoadRules();
        Rules = new ObservableCollection<MailRule>(rules);

        if (prefillTemplate != null)
        {
            Rules.Add(prefillTemplate);
            SelectedRule = prefillTemplate;
            Announce(Strings.RulesManager_Hint_NewRuleFromMessage,
                AnnouncementCategory.Hint);
        }
        else if (Rules.Count > 0)
        {
            SelectedRule = Rules[0];
        }
    }

    // ── Properties ──────────────────────────────────────────────────────────

    public ObservableCollection<MailRule> Rules { get; }

    [ObservableProperty]
    private MailRule? _selectedRule;

    /// <summary>Account options for the ComboBox. First item is "All accounts" (null).</summary>
    public List<AccountOption> AccountOptions =>
        new[] { new AccountOption { Id = null, DisplayName = Strings.RulesManager_Account_AllAccounts } }
        .Concat(_accounts.Select(a => new AccountOption { Id = a.Id, DisplayName = a.AccountLabel }))
        .ToList();

    /// <summary>Action options for the ComboBox.</summary>
    public static List<ActionOption> ActionOptions => new()
    {
        new() { Action = RuleAction.MarkAsRead, DisplayName = Strings.RulesManager_Action_MarkAsRead },
        new() { Action = RuleAction.MarkAsUnread, DisplayName = Strings.RulesManager_Action_MarkAsUnread },
        new() { Action = RuleAction.MoveToFolder, DisplayName = Strings.RulesManager_Action_MoveToFolder },
        new() { Action = RuleAction.Delete, DisplayName = Strings.RulesManager_Action_Delete },
    };

    public bool IsMoveToFolderSelected =>
        SelectedRule?.Action == RuleAction.MoveToFolder;

    /// <summary>Status text shown at the bottom of the dialog.</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>Error message for the Name field.</summary>
    [ObservableProperty]
    private string _nameError = string.Empty;

    /// <summary>Error message for the TargetFolder field.</summary>
    [ObservableProperty]
    private string _folderError = string.Empty;

    /// <summary>Error message for conditions.</summary>
    [ObservableProperty]
    private string _conditionsError = string.Empty;

    // ── Property-change handlers ────────────────────────────────────────────

    partial void OnSelectedRuleChanged(MailRule? value)
    {
        OnPropertyChanged(nameof(IsMoveToFolderSelected));
        OnPropertyChanged(nameof(IsFromEnabled));
        OnPropertyChanged(nameof(IsToEnabled));
        OnPropertyChanged(nameof(IsSubjectEnabled));
        OnPropertyChanged(nameof(IsBodyEnabled));
        ClearErrors();
    }

    // ── Condition checkbox helpers ──────────────────────────────────────────

    public bool IsFromEnabled
    {
        get => SelectedRule?.UseFromCondition ?? false;
        set { if (SelectedRule != null) { SelectedRule.UseFromCondition = value; OnPropertyChanged(); } }
    }

    public bool IsToEnabled
    {
        get => SelectedRule?.UseToCondition ?? false;
        set { if (SelectedRule != null) { SelectedRule.UseToCondition = value; OnPropertyChanged(); } }
    }

    public bool IsSubjectEnabled
    {
        get => SelectedRule?.UseSubjectCondition ?? false;
        set { if (SelectedRule != null) { SelectedRule.UseSubjectCondition = value; OnPropertyChanged(); } }
    }

    public bool IsBodyEnabled
    {
        get => SelectedRule?.UseBodyCondition ?? false;
        set { if (SelectedRule != null) { SelectedRule.UseBodyCondition = value; OnPropertyChanged(); } }
    }

    // ── Commands ────────────────────────────────────────────────────────────

    [RelayCommand]
    private void NewRule()
    {
        var rule = new MailRule { Name = Strings.RulesManager_DefaultName_NewRule };
        Rules.Add(rule);
        SelectedRule = rule;
        Announce(Strings.RulesManager_Hint_NewRuleCreated, AnnouncementCategory.Hint);
    }

    [RelayCommand]
    private void DeleteRule()
    {
        if (SelectedRule == null) return;

        var confirmed = ConfirmDeleteRequested?.Invoke(
            string.Format(Strings.RulesManager_Confirm_DeleteRuleMessage, SelectedRule.Name),
            Strings.RulesManager_Confirm_DeleteRuleTitle) ?? false;

        if (!confirmed) return;

        var name = SelectedRule.Name;
        Rules.Remove(SelectedRule);
        _ruleService.SaveRules(Rules.ToList());
        SelectedRule = Rules.FirstOrDefault();
        Announce(string.Format(Strings.RulesManager_Result_RuleDeleted, name), AnnouncementCategory.Result);
    }

    [RelayCommand]
    private void SaveRule()
    {
        if (SelectedRule == null) return;

        if (!Validate(SelectedRule)) return;

        _ruleService.SaveRules(Rules.ToList());
        StatusText = string.Format(Strings.RulesManager_Result_RuleSaved, SelectedRule.Name);
        Announce(string.Format(Strings.RulesManager_Result_RuleSaved, SelectedRule.Name), AnnouncementCategory.Result);
    }

    [RelayCommand]
    private void TestRule()
    {
        if (SelectedRule == null || _selectedMessagesForTest == null)
        {
            StatusText = Strings.RulesManager_Status_NoMessagesToTest;
            return;
        }

        var messages = _selectedMessagesForTest.ToList();
        if (messages.Count == 0)
        {
            StatusText = Strings.RulesManager_Status_NoMessagesSelected;
            return;
        }

        var matched = _ruleService.TestRule(SelectedRule, messages);
        StatusText = matched.Count == 0
            ? string.Format(Strings.RulesManager_Result_TestMatchedNone, messages.Count)
            : string.Format(Strings.RulesManager_Result_TestMatchedSome, matched.Count, messages.Count);
        Announce(StatusText, AnnouncementCategory.Result);
    }

    [RelayCommand]
    private void Close()
    {
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void PickFolder()
    {
        var folder = PickFolderRequested?.Invoke();
        if (folder != null && SelectedRule != null)
        {
            SelectedRule.TargetFolder = folder;
            OnPropertyChanged(nameof(SelectedRule));
        }
    }

    // ── Validation ──────────────────────────────────────────────────────────

    private bool Validate(MailRule rule)
    {
        ClearErrors();
        bool valid = true;

        if (string.IsNullOrWhiteSpace(rule.Name))
        {
            NameError = Strings.RulesManager_Error_NameRequired;
            valid = false;
        }

        if (rule.Action == RuleAction.MoveToFolder && string.IsNullOrWhiteSpace(rule.TargetFolder))
        {
            FolderError = Strings.RulesManager_Error_TargetFolderRequired;
            valid = false;
        }

        // Require at least one condition for destructive actions
        if (rule.Action is RuleAction.MoveToFolder or RuleAction.Delete)
        {
            bool hasCondition =
                (rule.UseFromCondition && !string.IsNullOrWhiteSpace(rule.FromContains)) ||
                (rule.UseToCondition && !string.IsNullOrWhiteSpace(rule.ToContains)) ||
                (rule.UseSubjectCondition && !string.IsNullOrWhiteSpace(rule.SubjectContains)) ||
                (rule.UseBodyCondition && !string.IsNullOrWhiteSpace(rule.BodyContains)) ||
                rule.MustHaveAttachments;

            if (!hasCondition)
            {
                ConditionsError = Strings.RulesManager_Error_ConditionRequired;
                valid = false;
            }
        }

        if (!valid)
        {
            var errors = new[] { NameError, FolderError, ConditionsError }
                .Where(e => !string.IsNullOrEmpty(e));
            Announce(string.Join(" ", errors), AnnouncementCategory.Result);
        }

        return valid;
    }

    private void ClearErrors()
    {
        NameError = string.Empty;
        FolderError = string.Empty;
        ConditionsError = string.Empty;
    }

    private void Announce(string text, AnnouncementCategory category)
    {
        AnnouncementRequested?.Invoke(text, category);
    }
}

// ── ComboBox option types ───────────────────────────────────────────────────

public class AccountOption
{
    public Guid? Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public override string ToString() => DisplayName;
}

public class ActionOption
{
    public RuleAction Action { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public override string ToString() => DisplayName;
}

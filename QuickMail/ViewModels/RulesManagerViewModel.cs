using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;
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
            Announce("New rule created from message. Fill in the action and save.",
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
        new[] { new AccountOption { Id = null, DisplayName = "All accounts" } }
        .Concat(_accounts.Select(a => new AccountOption { Id = a.Id, DisplayName = a.AccountLabel }))
        .ToList();

    /// <summary>Action options for the ComboBox.</summary>
    public static List<ActionOption> ActionOptions => new()
    {
        new() { Action = RuleAction.MarkAsRead, DisplayName = "Mark as read" },
        new() { Action = RuleAction.MarkAsUnread, DisplayName = "Mark as unread" },
        new() { Action = RuleAction.MoveToFolder, DisplayName = "Move to folder" },
        new() { Action = RuleAction.Delete, DisplayName = "Delete" },
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
        var rule = new MailRule { Name = "New rule" };
        Rules.Add(rule);
        SelectedRule = rule;
        Announce("New rule created. Enter a name and conditions.", AnnouncementCategory.Hint);
    }

    [RelayCommand]
    private void DeleteRule()
    {
        if (SelectedRule == null) return;

        var confirmed = ConfirmDeleteRequested?.Invoke(
            $"Delete rule '{SelectedRule.Name}'?",
            "Delete Rule") ?? false;

        if (!confirmed) return;

        var name = SelectedRule.Name;
        Rules.Remove(SelectedRule);
        _ruleService.SaveRules(Rules.ToList());
        SelectedRule = Rules.FirstOrDefault();
        Announce($"Rule '{name}' deleted.", AnnouncementCategory.Result);
    }

    [RelayCommand]
    private void SaveRule()
    {
        if (SelectedRule == null) return;

        if (!Validate(SelectedRule)) return;

        _ruleService.SaveRules(Rules.ToList());
        StatusText = $"Rule '{SelectedRule.Name}' saved.";
        Announce($"Rule '{SelectedRule.Name}' saved.", AnnouncementCategory.Result);
    }

    [RelayCommand]
    private void TestRule()
    {
        if (SelectedRule == null || _selectedMessagesForTest == null)
        {
            StatusText = "No messages available to test against.";
            return;
        }

        var messages = _selectedMessagesForTest.ToList();
        if (messages.Count == 0)
        {
            StatusText = "No messages selected in the main window.";
            return;
        }

        var matched = _ruleService.TestRule(SelectedRule, messages);
        StatusText = matched.Count == 0
            ? $"Rule would match 0 of {messages.Count} selected messages."
            : $"Rule would match {matched.Count} of {messages.Count} selected messages.";
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
            NameError = "Rule name is required.";
            valid = false;
        }

        if (rule.Action == RuleAction.MoveToFolder && string.IsNullOrWhiteSpace(rule.TargetFolder))
        {
            FolderError = "Target folder is required for Move to folder action.";
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
                ConditionsError = "At least one condition is required for Move and Delete actions.";
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

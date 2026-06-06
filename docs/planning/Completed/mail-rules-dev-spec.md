# Mail Rules — Development Specification

**Status:**  Implemented
**Version:** 1.1  
**Date:** 2026-05-22  
**Based on:** `mail-rules-pm-spec.md` (Approved)  
**Target:** AI coding agent implementation  
**Post-implementation:** See [Appendix C: Lessons Learned](#appendix-c-lessons-learned)

---

## Table of Contents

1. [Overview](#1-overview)
2. [Files to Create](#2-files-to-create)
3. [Files to Modify](#3-files-to-modify)
4. [Implementation Order](#4-implementation-order)
5. [Detailed File Specifications](#5-detailed-file-specifications)
   - [5.1 `MailRule.cs` — Data Model](#51-mailrulecs--data-model)
   - [5.2 `IRuleService.cs` — Service Interface](#52-iruleservicecs--service-interface)
   - [5.3 `RuleService.cs` — Service Implementation](#53-ruleservicecs--service-implementation)
   - [5.4 `RulesManagerViewModel.cs` — Dialog ViewModel](#54-rulesmanagerviewmodelcs--dialog-viewmodel)
   - [5.5 `RulesManagerWindow.xaml` — Dialog UI](#55-rulesmanagerwindowxaml--dialog-ui)
   - [5.6 `RulesManagerWindow.xaml.cs` — Dialog Code-Behind](#56-rulesmanagerwindowxamlcs--dialog-code-behind)
   - [5.7 `SyncService.cs` — Integration Point](#57-syncservicecs--integration-point)
   - [5.8 `MainViewModel.cs` — Commands & Status Bar](#58-mainviewmodelcs--commands--status-bar)
   - [5.9 `MainWindow.xaml` — Menu Items](#59-mainwindowxaml--menu-items)
   - [5.10 `MainWindow.xaml.cs` — Command Registration](#510-mainwindowxamlcs--command-registration)
   - [5.11 `App.xaml.cs` — DI Wiring](#511-appxamlcs--di-wiring)
6. [Tests](#6-tests)
7. [Edge Cases & Error Handling](#7-edge-cases--error-handling)
8. [Accessibility Checklist](#8-accessibility-checklist)
9. [Build Verification](#9-build-verification)

---

## 1. Overview

Mail Rules lets users define automatic actions that run on incoming messages during background sync. Rules are evaluated in `SyncService.SyncFolderAsync()` after new messages are persisted to SQLite but before the `FolderSynced` event fires.

**Key constraints from the codebase:**
- MVVM strictly enforced — no `MessageBox`, `Window`, or `Dispatcher` in ViewModels
- All keyboard shortcuts registered in `CommandRegistry`
- All screen-reader announcements through `AccessibilityHelper.Announce()` with explicit `AnnouncementCategory`
- Passwords never stored in JSON; rules file contains no credentials
- Services follow the pattern of `ContactService` / `ViewService` (JSON file in `%APPDATA%\QuickMail`, Load/Save interface)
- DI is manual in `App.xaml.cs` — no container

---

## 2. Files to Create

| # | File | Purpose |
|---|---|---|
| 1 | `QuickMail/Models/MailRule.cs` | Rule data model + `RuleAction` enum |
| 2 | `QuickMail/Services/IRuleService.cs` | Service interface |
| 3 | `QuickMail/Services/RuleService.cs` | Rule storage, matching engine, execution |
| 4 | `QuickMail/ViewModels/RulesManagerViewModel.cs` | Rules Manager dialog VM |
| 5 | `QuickMail/Views/RulesManagerWindow.xaml` | Rules Manager dialog UI |
| 6 | `QuickMail/Views/RulesManagerWindow.xaml.cs` | Code-behind (focus, keyboard routing, dialog events) |
| 7 | `QuickMail.Tests/RuleServiceTests.cs` | Unit tests for RuleService |
| 8 | `QuickMail.Tests/RulesManagerViewModelTests.cs` | VM construction + command tests |
| 9 | `QuickMail.Tests/RulesManagerXamlParseTests.cs` | XAML parse test |

---

## 3. Files to Modify

| # | File | Change |
|---|---|---|
| 1 | `QuickMail/Services/SyncService.cs` | Accept `IRuleService` in constructor; call `ApplyRulesAsync` after upsert, before `FolderSynced` |
| 2 | `QuickMail/ViewModels/MainViewModel.cs` | Add `RulesManagerCommand`, `CreateRuleFromMessageCommand`; add `RulesStatusText` property; accept `IRuleService` in constructor |
| 3 | `QuickMail/Views/MainWindow.xaml` | Add "Rules…" menu item under Tools; add "Create Rule from Message…" context menu item on message list |
| 4 | `QuickMail/Views/MainWindow.xaml.cs` | Register `mail.rules` and `mail.createRuleFromMessage` in `CommandRegistry`; wire dialog open handlers |
| 5 | `QuickMail/App.xaml.cs` | Create `RuleService`, inject into `SyncService` and `MainViewModel` |
| 6 | `QuickMail.Tests/StubServices.cs` | Add `StubRuleService` |

---

## 4. Implementation Order

Follow this order exactly — each phase depends on the previous:

1. **Data model** — `MailRule.cs`
2. **Service interface + implementation** — `IRuleService.cs`, `RuleService.cs`
3. **Stub + service tests** — `StubServices.cs` (add `StubRuleService`), `RuleServiceTests.cs`
4. **SyncService integration** — Modify `SyncService.cs` constructor and `SyncFolderAsync`
5. **DI wiring** — Modify `App.xaml.cs`
6. **MainViewModel commands** — Add commands and `RulesStatusText` to `MainViewModel.cs`
7. **Rules Manager ViewModel** — `RulesManagerViewModel.cs`
8. **Rules Manager Window** — `RulesManagerWindow.xaml` + `.xaml.cs`
9. **MainWindow menu + command registration** — Modify `MainWindow.xaml` + `.xaml.cs`
10. **VM + XAML tests** — `RulesManagerViewModelTests.cs`, `RulesManagerXamlParseTests.cs`
11. **Build verification** — `dotnet build QuickMail.sln -nologo`

---

## 5. Detailed File Specifications

### 5.1 `MailRule.cs` — Data Model

**Path:** `QuickMail/Models/MailRule.cs`

```csharp
using System;

namespace QuickMail.Models;

/// <summary>
/// A user-defined rule that automatically performs an action on incoming messages
/// that match a set of conditions. All populated conditions are ANDed together.
/// An empty/null condition matches everything.
/// </summary>
public class MailRule
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>User-visible name for this rule. Required — validated on save.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>When false, the rule is skipped during evaluation.</summary>
    public bool IsEnabled { get; set; } = true;

    // ── Conditions (all ANDed) ──────────────────────────────────────────────

    /// <summary>Case-insensitive substring match against MailMessageSummary.From.</summary>
    public string? FromContains { get; set; }

    /// <summary>Case-insensitive substring match against MailMessageSummary.To.</summary>
    public string? ToContains { get; set; }

    /// <summary>Case-insensitive substring match against MailMessageSummary.Subject.</summary>
    public string? SubjectContains { get; set; }

    /// <summary>Case-insensitive substring match against MailMessageSummary.Preview.</summary>
    public string? BodyContains { get; set; }

    /// <summary>When true, only messages with HasAttachments == true match.</summary>
    public bool MustHaveAttachments { get; set; }

    /// <summary>
    /// Scope the rule to one account. Null means the rule applies to all accounts.
    /// </summary>
    public Guid? AccountId { get; set; }

    // ── Action ──────────────────────────────────────────────────────────────

    public RuleAction Action { get; set; } = RuleAction.MarkAsRead;

    /// <summary>
    /// Destination folder full name (e.g. "INBOX/Priority"). Required when
    /// Action == MoveToFolder; ignored otherwise.
    /// </summary>
    public string? TargetFolder { get; set; }
}

public enum RuleAction
{
    /// <summary>Mark the message as read (IMAP \Seen flag).</summary>
    MarkAsRead,

    /// <summary>Mark the message as unread (remove IMAP \Seen flag).</summary>
    MarkAsUnread,

    /// <summary>Move the message to TargetFolder via IMAP MOVE.</summary>
    MoveToFolder,

    /// <summary>Move the message to the Trash folder.</summary>
    Delete,
}
```

**Validation rules (enforced in ViewModel, not in model):**
- `Name` must be non-empty (after trimming whitespace)
- When `Action == MoveToFolder`, `TargetFolder` must be non-null and non-empty
- At least one condition must be populated when `Action` is `MoveToFolder` or `Delete` (prevents accidental mass-move/delete)

---

### 5.2 `IRuleService.cs` — Service Interface

**Path:** `QuickMail/Services/IRuleService.cs`

```csharp
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

public interface IRuleService
{
    /// <summary>Load all rules from rules.json. Returns empty list if file is missing or corrupted.</summary>
    List<MailRule> LoadRules();

    /// <summary>Persist all rules to rules.json. Creates the data directory if needed.</summary>
    void SaveRules(List<MailRule> rules);

    /// <summary>
    /// Apply enabled rules to a batch of incoming messages for a specific account.
    /// Rules are evaluated in list order. Each rule is tested against every message;
    /// matching messages have the rule's action executed.
    /// Returns the number of messages that matched at least one rule.
    /// </summary>
    Task<int> ApplyRulesAsync(
        List<MailMessageSummary> incoming,
        Guid accountId,
        CancellationToken ct);

    /// <summary>
    /// Test a rule against a set of messages without executing any actions.
    /// Returns the subset of messages that would match.
    /// </summary>
    List<MailMessageSummary> TestRule(MailRule rule, IEnumerable<MailMessageSummary> messages);
}
```

---

### 5.3 `RuleService.cs` — Service Implementation

**Path:** `QuickMail/Services/RuleService.cs`

**Design notes:**
- Follows the exact pattern of `ContactService.cs`: private `_filePath`, `_cache` list, `_loadLock` semaphore, `EnsureLoadedAsync()`, atomic write via temp file + rename.
- `ApplyRulesAsync` receives `IImapService` and `ILocalStoreService` via constructor injection (not method parameters) so it can execute IMAP actions directly.
- The `TestRule` method is synchronous and pure — no I/O, no side effects.

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

public class RuleService : IRuleService
{
    private readonly string _filePath;
    private readonly IImapService _imap;
    private readonly ILocalStoreService _store;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private List<MailRule> _cache = [];
    private bool _loaded;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public RuleService(IImapService imap, ILocalStoreService store, string? dataDirectory = null)
    {
        _imap = imap;
        _store = store;
        var dir = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickMail");
        _filePath = Path.Combine(dir, "rules.json");
    }

    // ── Load / Save ─────────────────────────────────────────────────────────

    public List<MailRule> LoadRules()
    {
        if (_loaded) return _cache;

        if (!File.Exists(_filePath))
        {
            _cache = [];
            _loaded = true;
            return _cache;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            _cache = JsonSerializer.Deserialize<List<MailRule>>(json) ?? [];
        }
        catch
        {
            _cache = [];
        }
        _loaded = true;
        return _cache;
    }

    public void SaveRules(List<MailRule> rules)
    {
        _cache = rules;
        var dir = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(dir);

        // Atomic write: write to temp file, then rename.
        var tempPath = _filePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(rules, JsonOptions));
        File.Move(tempPath, _filePath, overwrite: true);
        _loaded = true;
    }

    // ── Rule Execution ──────────────────────────────────────────────────────

    public async Task<int> ApplyRulesAsync(
        List<MailMessageSummary> incoming,
        Guid accountId,
        CancellationToken ct)
    {
        var rules = LoadRules();
        var enabledRules = rules.Where(r => r.IsEnabled).ToList();
        if (enabledRules.Count == 0) return 0;

        int matchedCount = 0;

        foreach (var rule in enabledRules)
        {
            ct.ThrowIfCancellationRequested();

            // Account scope check
            if (rule.AccountId.HasValue && rule.AccountId.Value != accountId)
                continue;

            var matched = incoming.Where(m => MatchesRule(rule, m)).ToList();
            if (matched.Count == 0) continue;

            matchedCount += matched.Count;

            try
            {
                await ExecuteActionAsync(rule, matched, accountId, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Log($"Rule '{rule.Name}' action failed", ex);
            }
        }

        return matchedCount;
    }

    public List<MailMessageSummary> TestRule(MailRule rule, IEnumerable<MailMessageSummary> messages)
    {
        return messages.Where(m => MatchesRule(rule, m)).ToList();
    }

    // ── Condition Matching ──────────────────────────────────────────────────

    private static bool MatchesRule(MailRule rule, MailMessageSummary msg)
    {
        if (!string.IsNullOrEmpty(rule.FromContains)
            && !msg.From.Contains(rule.FromContains, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrEmpty(rule.ToContains)
            && !msg.To.Contains(rule.ToContains, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrEmpty(rule.SubjectContains)
            && !msg.Subject.Contains(rule.SubjectContains, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrEmpty(rule.BodyContains)
            && (msg.Preview == null || !msg.Preview.Contains(rule.BodyContains, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (rule.MustHaveAttachments && !msg.HasAttachments)
            return false;

        return true;
    }

    // ── Action Execution ────────────────────────────────────────────────────

    private async Task ExecuteActionAsync(
        MailRule rule,
        List<MailMessageSummary> matched,
        Guid accountId,
        CancellationToken ct)
    {
        switch (rule.Action)
        {
            case RuleAction.MarkAsRead:
                await MarkAsReadAsync(matched, ct);
                break;

            case RuleAction.MarkAsUnread:
                await MarkAsUnreadAsync(matched, ct);
                break;

            case RuleAction.MoveToFolder:
                if (string.IsNullOrEmpty(rule.TargetFolder)) break;
                await MoveToFolderAsync(matched, rule.TargetFolder, ct);
                break;

            case RuleAction.Delete:
                await DeleteAsync(matched, ct);
                break;
        }
    }

    private async Task MarkAsReadAsync(List<MailMessageSummary> messages, CancellationToken ct)
    {
        foreach (var msg in messages)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await _imap.MarkReadAsync(msg.AccountId, msg.FolderName, msg.UniqueId, ct);
                msg.IsRead = true;
                await _store.UpdateIsReadAsync(msg.AccountId, msg.FolderName, msg.UniqueId, true);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Log($"MarkRead failed for UID {msg.UniqueId}", ex);
            }
        }
    }

    private async Task MarkAsUnreadAsync(List<MailMessageSummary> messages, CancellationToken ct)
    {
        foreach (var msg in messages)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // ImapService doesn't have a dedicated MarkUnreadAsync — we use
                // the IMAP STORE -FLAGS \Seen pattern. For v0.6, we update the
                // local store only. Full IMAP unread will be added in a follow-up.
                msg.IsRead = false;
                await _store.UpdateIsReadAsync(msg.AccountId, msg.FolderName, msg.UniqueId, false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Log($"MarkUnread failed for UID {msg.UniqueId}", ex);
            }
        }
    }

    private async Task MoveToFolderAsync(
        List<MailMessageSummary> messages, string targetFolder, CancellationToken ct)
    {
        // Group messages by (AccountId, FolderName) so we issue one MOVE per source folder.
        var groups = messages.GroupBy(m => (m.AccountId, m.FolderName));
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            var uids = group.Select(m => m.UniqueId).ToList();
            try
            {
                await _imap.MoveMessagesAsync(
                    group.Key.AccountId, group.Key.FolderName, uids, targetFolder, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Log($"MoveToFolder failed for {uids.Count} messages to '{targetFolder}'", ex);
            }
        }
    }

    private async Task DeleteAsync(List<MailMessageSummary> messages, CancellationToken ct)
    {
        var groups = messages.GroupBy(m => (m.AccountId, m.FolderName));
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            var uids = group.Select(m => m.UniqueId).ToList();
            try
            {
                await _imap.MoveToTrashBatchAsync(
                    group.Key.AccountId, group.Key.FolderName, uids, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Log($"Delete (move to trash) failed for {uids.Count} messages", ex);
            }
        }
    }
}
```

**Important notes for the implementer:**
- `MarkAsUnreadAsync` currently only updates the local store. The `IImapService` interface does not have a `MarkUnreadAsync` method. This is acceptable for v0.6 — the message will appear unread in QuickMail. A follow-up task should add `MarkUnreadAsync` to `IImapService`/`ImapService` for full server-side flag sync.
- The `MoveToFolderAsync` and `DeleteAsync` methods group messages by `(AccountId, FolderName)` to minimize IMAP round-trips.
- All IMAP operations use the foreground lease priority implicitly (via the existing `ImapService` methods). Since rules run during background sync, this is acceptable because sync already holds background leases and the foreground pool is separate.

---

### 5.4 `RulesManagerViewModel.cs` — Dialog ViewModel

**Path:** `QuickMail/ViewModels/RulesManagerViewModel.cs`

**MVVM constraints (must follow):**
- No `MessageBox`, no `Window`, no `Dispatcher` references
- Confirmation dialogs via `ConfirmDeleteRequested` event
- Close request via `CloseRequested` event
- All screen-reader announcements via `AnnouncementRequested` event with `AnnouncementCategory`

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

public partial class RulesManagerViewModel : ObservableObject
{
    private readonly IRuleService _ruleService;
    private readonly IImapService _imap;
    private readonly IEnumerable<AccountModel> _accounts;
    private readonly IEnumerable<MailMessageSummary>? _selectedMessagesForTest;

    // ── Events (View subscribes) ────────────────────────────────────────────

    /// <summary>Raised when the View should show a confirmation dialog.</summary>
    public event Func<string, string, bool>? ConfirmDeleteRequested;

    /// <summary>Raised when the dialog should close.</summary>
    public event Action? CloseRequested;

    /// <summary>Raised for screen-reader announcements.</summary>
    public event Action<string, AnnouncementCategory>? AnnouncementRequested;

    // ── Constructor ─────────────────────────────────────────────────────────

    public RulesManagerViewModel(
        IRuleService ruleService,
        IImapService imap,
        IEnumerable<AccountModel> accounts,
        MailRule? prefillTemplate = null,
        IEnumerable<MailMessageSummary>? selectedMessagesForTest = null)
    {
        _ruleService = ruleService;
        _imap = imap;
        _accounts = accounts;
        _selectedMessagesForTest = selectedMessagesForTest;

        var rules = _ruleService.LoadRules();
        Rules = new ObservableCollection<MailRule>(rules);

        if (prefillTemplate != null)
        {
            Rules.Add(prefillTemplate);
            SelectedRule = prefillTemplate;
            Announce($"New rule created from message. Fill in the action and save.",
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

    [ObservableProperty]
    private bool _isMoveToFolderSelected;

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
        ClearErrors();
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
                !string.IsNullOrWhiteSpace(rule.FromContains) ||
                !string.IsNullOrWhiteSpace(rule.ToContains) ||
                !string.IsNullOrWhiteSpace(rule.SubjectContains) ||
                !string.IsNullOrWhiteSpace(rule.BodyContains) ||
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
```

**Notes for the implementer:**
- `AccountOption` and `ActionOption` are simple display types for ComboBox binding. They live in the same file for simplicity.
- The `SelectedRule` property change handler must notify `IsMoveToFolderSelected` so the TargetFolder ComboBox visibility binding updates.
- `IsMoveToFolderSelected` is a computed property: `=> SelectedRule?.Action == RuleAction.MoveToFolder`. Add it as a pass-through property or use `[NotifyPropertyChangedFor]`.

---

### 5.5 `RulesManagerWindow.xaml` — Dialog UI

**Path:** `QuickMail/Views/RulesManagerWindow.xaml`

**Layout:** Two-pane design — rule list on the left, editor on the right, status bar at the bottom.

```xml
<Window x:Class="QuickMail.Views.RulesManagerWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="clr-namespace:QuickMail.ViewModels"
        Title="Rules Manager"
        Width="750" Height="520"
        MinWidth="600" MinHeight="400"
        WindowStartupLocation="CenterOwner"
        ResizeMode="CanResizeWithGrip"
        WindowStyle="ToolWindow">

    <Window.Resources>
        <Style TargetType="TextBox" x:Key="RuleTextBox">
            <Setter Property="Margin" Value="0,2,0,8"/>
            <Setter Property="Padding" Value="4,3"/>
            <Setter Property="Height" Value="26"/>
        </Style>
        <Style TargetType="ComboBox" x:Key="RuleComboBox">
            <Setter Property="Margin" Value="0,2,0,8"/>
            <Setter Property="Height" Value="26"/>
        </Style>
        <Style TargetType="CheckBox" x:Key="RuleCheckBox">
            <Setter Property="Margin" Value="0,4,0,4"/>
        </Style>
    </Window.Resources>

    <Grid Margin="12">
        <Grid.RowDefinitions>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <Grid Grid.Row="0">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="220"/>
                <ColumnDefinition Width="6"/>
                <ColumnDefinition Width="*"/>
            </Grid.ColumnDefinitions>

            <!-- Left pane: Rule list -->
            <Grid Grid.Column="0">
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto"/>
                    <RowDefinition Height="*"/>
                </Grid.RowDefinitions>

                <StackPanel Grid.Row="0" Orientation="Horizontal" Margin="0,0,0,6">
                    <Button Content="New Rule" Command="{Binding NewRuleCommand}"
                            Width="80" Height="26" Margin="0,0,6,0"
                            AutomationProperties.Name="Create new rule"/>
                    <Button Content="Delete" Command="{Binding DeleteRuleCommand}"
                            Width="60" Height="26"
                            AutomationProperties.Name="Delete selected rule"/>
                </StackPanel>

                <ListBox Grid.Row="1"
                         x:Name="RuleListBox"
                         ItemsSource="{Binding Rules}"
                         SelectedItem="{Binding SelectedRule}"
                         DisplayMemberPath="Name"
                         AutomationProperties.Name="Rules list">
                    <ListBox.ItemContainerStyle>
                        <Style TargetType="ListBoxItem">
                            <Setter Property="Padding" Value="6,4"/>
                            <Setter Property="AutomationProperties.Name"
                                    Value="{Binding Name}"/>
                        </Style>
                    </ListBox.ItemContainerStyle>
                </ListBox>
            </Grid>

            <GridSplitter Grid.Column="1" Width="6" HorizontalAlignment="Stretch"
                          Background="Transparent"/>

            <!-- Right pane: Rule editor -->
            <ScrollViewer Grid.Column="2" VerticalScrollBarVisibility="Auto">
                <StackPanel Margin="12,0,0,0">
                    <!-- Rule Name -->
                    <TextBlock Text="Rule Name" FontWeight="SemiBold" Margin="0,8,0,2"/>
                    <TextBox Text="{Binding SelectedRule.Name, UpdateSourceTrigger=PropertyChanged}"
                             Style="{StaticResource RuleTextBox}"
                             AutomationProperties.Name="Rule name"
                             AutomationProperties.LabeledBy="{Binding ElementName=RuleNameLabel}"/>
                    <TextBlock x:Name="RuleNameLabel" Visibility="Collapsed">Rule name</TextBlock>
                    <TextBlock Text="{Binding NameError}" Foreground="Red"
                               AutomationProperties.LiveSetting="Assertive"
                               Visibility="{Binding NameError, Converter={StaticResource StringToVisibilityConverter}, FallbackValue=Collapsed}"/>

                    <!-- Enabled -->
                    <CheckBox Content="Enabled"
                              IsChecked="{Binding SelectedRule.IsEnabled}"
                              Style="{StaticResource RuleCheckBox}"
                              AutomationProperties.Name="Rule enabled"/>

                    <!-- Conditions -->
                    <TextBlock Text="Conditions" FontWeight="SemiBold" Margin="0,12,0,4"/>
                    <Separator Margin="0,0,0,6"/>

                    <TextBlock Text="Account"/>
                    <ComboBox ItemsSource="{Binding AccountOptions}"
                              SelectedValue="{Binding SelectedRule.AccountId}"
                              SelectedValuePath="Id"
                              Style="{StaticResource RuleComboBox}"
                              AutomationProperties.Name="Account scope"
                              AutomationProperties.LabeledBy="{Binding ElementName=AccountLabel}"/>
                    <TextBlock x:Name="AccountLabel" Visibility="Collapsed">Account scope</TextBlock>

                    <TextBlock Text="From contains"/>
                    <TextBox Text="{Binding SelectedRule.FromContains, UpdateSourceTrigger=PropertyChanged}"
                             Style="{StaticResource RuleTextBox}"
                             AutomationProperties.Name="From contains"
                             AutomationProperties.HelpText="e.g., manager@company.com or just company.com"/>

                    <TextBlock Text="To contains"/>
                    <TextBox Text="{Binding SelectedRule.ToContains, UpdateSourceTrigger=PropertyChanged}"
                             Style="{StaticResource RuleTextBox}"
                             AutomationProperties.Name="To contains"
                             AutomationProperties.HelpText="e.g., team@company.com"/>

                    <TextBlock Text="Subject contains"/>
                    <TextBox Text="{Binding SelectedRule.SubjectContains, UpdateSourceTrigger=PropertyChanged}"
                             Style="{StaticResource RuleTextBox}"
                             AutomationProperties.Name="Subject contains"
                             AutomationProperties.HelpText="e.g., Weekly Report"/>

                    <TextBlock Text="Body contains"/>
                    <TextBox Text="{Binding SelectedRule.BodyContains, UpdateSourceTrigger=PropertyChanged}"
                             Style="{StaticResource RuleTextBox}"
                             AutomationProperties.Name="Body contains"
                             AutomationProperties.HelpText="Text to search in message body preview"/>

                    <CheckBox Content="Has attachments"
                              IsChecked="{Binding SelectedRule.MustHaveAttachments}"
                              Style="{StaticResource RuleCheckBox}"
                              AutomationProperties.Name="Must have attachments"/>

                    <TextBlock Text="{Binding ConditionsError}" Foreground="Red"
                               AutomationProperties.LiveSetting="Assertive"
                               Visibility="{Binding ConditionsError, Converter={StaticResource StringToVisibilityConverter}, FallbackValue=Collapsed}"/>

                    <!-- Action -->
                    <TextBlock Text="Action" FontWeight="SemiBold" Margin="0,12,0,4"/>
                    <Separator Margin="0,0,0,6"/>

                    <ComboBox ItemsSource="{Binding ActionOptions}"
                              SelectedValue="{Binding SelectedRule.Action}"
                              SelectedValuePath="Action"
                              DisplayMemberPath="DisplayName"
                              Style="{StaticResource RuleComboBox}"
                              AutomationProperties.Name="Rule action"
                              AutomationProperties.LabeledBy="{Binding ElementName=ActionLabel}"/>
                    <TextBlock x:Name="ActionLabel" Visibility="Collapsed">Rule action</TextBlock>

                    <!-- Target folder (visible only for MoveToFolder) -->
                    <TextBlock Text="Target folder"
                               Visibility="{Binding IsMoveToFolderSelected, Converter={StaticResource BoolToVisibilityConverter}, FallbackValue=Collapsed}"/>
                    <ComboBox x:Name="TargetFolderCombo"
                              Text="{Binding SelectedRule.TargetFolder, UpdateSourceTrigger=PropertyChanged}"
                              IsEditable="True"
                              Style="{StaticResource RuleComboBox}"
                              Visibility="{Binding IsMoveToFolderSelected, Converter={StaticResource BoolToVisibilityConverter}, FallbackValue=Collapsed}"
                              AutomationProperties.Name="Target folder"
                              AutomationProperties.HelpText="Enter folder path, e.g., INBOX/Priority"/>
                    <TextBlock Text="{Binding FolderError}" Foreground="Red"
                               AutomationProperties.LiveSetting="Assertive"
                               Visibility="{Binding FolderError, Converter={StaticResource StringToVisibilityConverter}, FallbackValue=Collapsed}"/>

                    <!-- Buttons -->
                    <StackPanel Orientation="Horizontal" Margin="0,16,0,0">
                        <Button Content="Test Rule" Command="{Binding TestRuleCommand}"
                                Width="80" Height="26" Margin="0,0,8,0"
                                AutomationProperties.Name="Test rule against selected messages"/>
                        <Button Content="Save" Command="{Binding SaveRuleCommand}"
                                Width="60" Height="26" Margin="0,0,8,0"
                                AutomationProperties.Name="Save rule"
                                IsDefault="True"/>
                        <Button Content="Close" Command="{Binding CloseCommand}"
                                Width="60" Height="26"
                                AutomationProperties.Name="Close rules manager"
                                IsCancel="True"/>
                    </StackPanel>
                </StackPanel>
            </ScrollViewer>
        </Grid>

        <!-- Status bar -->
        <StatusBar Grid.Row="1" Margin="0,8,0,0">
            <StatusBarItem>
                <TextBlock Text="{Binding StatusText}"
                           AutomationProperties.LiveSetting="Polite"/>
            </StatusBarItem>
        </StatusBar>
    </Grid>
</Window>
```

**Notes for the implementer:**
- The `StringToVisibilityConverter` and `BoolToVisibilityConverter` may already exist in the project. If not, add them to `QuickMail/Helpers/` or inline in `Window.Resources`.
- The `TargetFolderCombo` is an editable `ComboBox` so users can type a folder path. In v0.7 this should be replaced with a proper folder picker.
- `AutomationProperties.LabeledBy` references hidden `TextBlock` elements — this is the WPF pattern for associating visible labels with inputs for screen readers when the label is a separate `TextBlock` above the control.

---

### 5.6 `RulesManagerWindow.xaml.cs` — Dialog Code-Behind

**Path:** `QuickMail/Views/RulesManagerWindow.xaml.cs`

**Code-behind rules (from CLAUDE.md):**
- Only UI concerns: focus, keyboard routing, dialog events
- No business logic, no direct service calls

```csharp
using System.Windows;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.ViewModels;

namespace QuickMail.Views;

public partial class RulesManagerWindow : Window
{
    private readonly RulesManagerViewModel _vm;

    public RulesManagerWindow(RulesManagerViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        // Wire VM events
        vm.CloseRequested += OnCloseRequested;
        vm.ConfirmDeleteRequested += OnConfirmDeleteRequested;
        vm.AnnouncementRequested += OnAnnouncementRequested;

        // Focus the rule list on open
        Loaded += (_, _) => RuleListBox.Focus();
    }

    private void OnCloseRequested()
    {
        Close();
    }

    private bool OnConfirmDeleteRequested(string message, string title)
    {
        return MessageBox.Show(
            message, title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private void OnAnnouncementRequested(string text, AnnouncementCategory category)
    {
        AccessibilityHelper.Announce(this, text, interrupt: true, category: category);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // Dialog-local shortcuts (not registered in CommandRegistry — these are
        // scoped to this window only, same pattern as other dialogs).
        if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _vm.NewRuleCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None
                 && !RuleListBox.IsKeyboardFocusWithin)
        {
            // Only handle Delete when the list is NOT focused (list handles its own Delete).
            // When list IS focused, let the ListBox handle it natively.
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.CloseRequested -= OnCloseRequested;
        _vm.ConfirmDeleteRequested -= OnConfirmDeleteRequested;
        _vm.AnnouncementRequested -= OnAnnouncementRequested;
        base.OnClosed(e);
    }
}
```

---

### 5.7 `SyncService.cs` — Integration Point

**Path:** `QuickMail/Services/SyncService.cs`

**Changes:**

1. Add `IRuleService` to the constructor:

```csharp
// Existing fields:
private readonly IImapService _imap;
private readonly ILocalStoreService _store;
private readonly IConfigService _config;

// NEW field:
private readonly IRuleService _rules;

public SyncService(IImapService imap, ILocalStoreService store, IConfigService config, IRuleService rules)
{
    _imap   = imap;
    _store  = store;
    _config = config;
    _rules  = rules;  // NEW
}
```

2. Add a new event for rule execution results:

```csharp
// Existing events:
public event Action<IReadOnlyList<MailMessageSummary>>? FolderSynced;
public event Action<IReadOnlyList<MailMessageSummary>>? MessagesRemoved;

// NEW event:
/// <summary>Raised after rules have been applied to incoming messages.
/// The int is the number of messages matched by rules.</summary>
public event Action<int>? RulesApplied;
```

3. In `SyncFolderAsync`, after `UpsertSummariesAsync` and before `FolderSynced`, insert:

```csharp
// Existing code:
if (incoming.Count > 0)
{
    await _store.UpsertSummariesAsync(incoming);

    // NEW: Apply rules before notifying the UI
    int matchedCount = 0;
    try
    {
        matchedCount = await _rules.ApplyRulesAsync(incoming, account.Id, ct);
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex)
    {
        LogService.Log($"Rules execution failed for {account.AccountLabel}", ex);
    }

    // Show messages immediately — don't wait for body preview fetches.
    await Application.Current.Dispatcher.InvokeAsync(() =>
    {
        FolderSynced?.Invoke(incoming);
        if (matchedCount > 0)
            RulesApplied?.Invoke(matchedCount);  // NEW
    });
}
```

**Important:** The `RulesApplied` event fires on the UI thread via `Dispatcher.InvokeAsync`, same pattern as `FolderSynced`. This ensures `MainViewModel` can update `RulesStatusText` without a separate `Dispatcher` call.

---

### 5.8 `MainViewModel.cs` — Commands & Status Bar

**Path:** `QuickMail/ViewModels/MainViewModel.cs`

**Changes:**

1. Add `IRuleService` field and constructor parameter:

```csharp
// New field alongside existing ones:
private readonly IRuleService _ruleService;

// Add to constructor parameters (alphabetical position):
IRuleService ruleService,

// In constructor body:
_ruleService = ruleService;
```

2. Subscribe to `RulesApplied` event in constructor (alongside existing `FolderSynced` and `MessagesRemoved` subscriptions):

```csharp
_syncService.RulesApplied += OnRulesApplied;
```

3. Add `RulesStatusText` property:

```csharp
[ObservableProperty]
private string _rulesStatusText = string.Empty;
```

4. Add `OnRulesApplied` handler:

```csharp
private int _lastRulesMatchCount;
private DateTime _lastRulesRunTime;

private void OnRulesApplied(int matchCount)
{
    _lastRulesMatchCount = matchCount;
    _lastRulesRunTime = DateTime.Now;
    UpdateRulesStatusText();
}

private void UpdateRulesStatusText()
{
    var rules = _ruleService.LoadRules();
    int active = rules.Count(r => r.IsEnabled);
    int disabled = rules.Count(r => !r.IsEnabled);

    if (active == 0)
    {
        RulesStatusText = "No active rules";
        return;
    }

    var timeStr = _lastRulesRunTime == default
        ? "not yet run"
        : _lastRulesRunTime.ToString("h:mm tt");

    RulesStatusText = _lastRulesMatchCount > 0
        ? $"Rules: {active} active, {disabled} disabled — Last run: {_lastRulesMatchCount} matched ({timeStr})"
        : $"Rules: {active} active, {disabled} disabled — Last run: {timeStr}";
}
```

5. Add `RulesManagerCommand`:

```csharp
[RelayCommand]
private void OpenRulesManager()
{
    RulesManagerRequested?.Invoke(this, EventArgs.Empty);
}

/// <summary>Raised when the View should open the Rules Manager dialog.</summary>
public event EventHandler? RulesManagerRequested;
```

6. Add `CreateRuleFromMessageCommand`:

```csharp
[RelayCommand]
private void CreateRuleFromMessage()
{
    if (SelectedMessage == null && SelectedConversation == null && SelectedSenderGroup == null)
        return;

    // Get the representative message
    MailMessageSummary? source = SelectedMessage;
    if (source == null && SelectedConversation != null)
        source = SelectedConversation.Messages.FirstOrDefault();
    if (source == null && SelectedSenderGroup != null)
        source = SelectedSenderGroup.Messages.FirstOrDefault();
    if (source == null) return;

    var template = new MailRule
    {
        Name = $"Rule for {source.From}",
        FromContains = source.From,
        SubjectContains = string.IsNullOrWhiteSpace(source.Subject) ? null : source.Subject,
        AccountId = source.AccountId,
    };

    CreateRuleFromMessageRequested?.Invoke(this, template);
}

/// <summary>Raised when the View should open the Rules Manager with a pre-filled template.</summary>
public event EventHandler<MailRule>? CreateRuleFromMessageRequested;
```

7. Register new commands in `RegisterCommands`:

```csharp
registry.Register(new CommandDefinition(
    id: "mail.rules", category: "Mail", title: "Manage Rules",
    execute: () => OpenRulesManagerCommand.Execute(null),
    defaultKey: Key.R, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift));

registry.Register(new CommandDefinition(
    id: "mail.createRuleFromMessage", category: "Mail", title: "Create Rule from Message",
    execute: () => CreateRuleFromMessageCommand.Execute(null),
    defaultKey: Key.T, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift,
    isAvailable: () => HasSelectedMessage));
```

8. Call `UpdateRulesStatusText()` at the end of the constructor (after `LoadSavedViews()` and `RegisterCommands()`):

```csharp
UpdateRulesStatusText();
```

---

### 5.9 `MainWindow.xaml` — Menu Items

**Path:** `QuickMail/Views/MainWindow.xaml`

**Changes:**

1. Add "Rules…" menu item under the Tools menu (find the existing Tools menu and add):

```xml
<MenuItem Header="Tools">
    <!-- ...existing Tools items... -->
    <Separator/>
    <MenuItem Header="_Rules…"
              InputGestureText="Ctrl+Shift+R"
              Command="{Binding OpenRulesManagerCommand}"
              AutomationProperties.Name="Manage Rules. Ctrl+Shift+R"/>
</MenuItem>
```

2. Add "Create Rule from Message…" to the message list context menu (find the existing message ContextMenu and add):

```xml
<Separator/>
<MenuItem Header="Create _Rule from Message…"
          InputGestureText="Ctrl+Shift+T"
          Command="{Binding CreateRuleFromMessageCommand}"
          AutomationProperties.Name="Create Rule from Message. Ctrl+Shift+T"/>
```

3. Add rules status to the status bar (find the existing `StatusBar` and add a new `StatusBarItem`):

```xml
<StatusBarItem x:Name="RulesStatusItem"
               HorizontalAlignment="Right"
               AutomationProperties.Name="{Binding RulesStatusText}">
    <TextBlock Text="{Binding RulesStatusText, Mode=OneWay}"
               Style="{StaticResource StatusTextBoxStyle}"
               Cursor="Hand"
               AutomationProperties.Name="{Binding RulesStatusText}"/>
</StatusBarItem>
```

The status bar item should be selectable — when the user selects it, it opens the Rules Manager. Wire this in code-behind.

---

### 5.10 `MainWindow.xaml.cs` — Command Registration

**Path:** `QuickMail/Views/MainWindow.xaml.cs`

**Changes:**

1. Subscribe to new VM events in the `MainWindow` constructor (alongside existing subscriptions):

```csharp
vm.RulesManagerRequested += (_, _) => OpenRulesManager();
vm.CreateRuleFromMessageRequested += (_, template) => OpenRulesManager(template);
```

2. Add `OpenRulesManager` methods:

```csharp
private void OpenRulesManager(MailRule? template = null)
{
    var accounts = _vm.Accounts.ToList();
    var selectedMessages = _vm.Messages.ToList();

    var rulesVm = new RulesManagerViewModel(
        _ruleService, _imap, accounts,
        prefillTemplate: template,
        selectedMessagesForTest: selectedMessages);

    var dialog = new RulesManagerWindow(rulesVm) { Owner = this };
    dialog.ShowDialog();
}
```

Wait — `MainWindow` doesn't currently hold `_ruleService`. It needs to accept `IRuleService` in its constructor. Add it:

```csharp
// Add to fields:
private readonly IRuleService _ruleService;

// Add to constructor parameters:
IRuleService ruleService,

// In constructor body:
_ruleService = ruleService;
```

3. Wire the rules status bar item click:

```csharp
// In the constructor, after InitializeComponent:
RulesStatusItem.MouseLeftButtonDown += (_, _) => OpenRulesManager();
RulesStatusItem.KeyDown += (_, e) =>
{
    if (e.Key == Key.Enter || e.Key == Key.Space)
    {
        OpenRulesManager();
        e.Handled = true;
    }
};
```

---

### 5.11 `App.xaml.cs` — DI Wiring

**Path:** `QuickMail/App.xaml.cs`

**Changes:**

After `var contactService = new ContactService();` and before `var syncService = new SyncService(...)`, add:

```csharp
var ruleService = new RuleService(imapService, localStore);
```

Then update the `SyncService` constructor call to include `ruleService`:

```csharp
var syncService = new SyncService(imapService, localStore, configService, ruleService);
```

Update the `MainViewModel` constructor call to include `ruleService`:

```csharp
var mainVm = new MainViewModel(
    imapService, accountService, credentialService, localStore, oauthService,
    syncService, configService, commandRegistry, viewService, ruleService);
```

Update the `MainWindow` constructor call to include `ruleService`:

```csharp
var mainWindow = new MainWindow(mainVm, smtpService, accountService, credentialService,
    imapService, oauthService, commandRegistry, contactService, configService,
    localStore, viewService, ruleService);
```

---

## 6. Tests

### 6.1 `StubServices.cs` — Add `StubRuleService`

**Path:** `QuickMail.Tests/StubServices.cs`

Add after the existing `StubViewService`:

```csharp
sealed class StubRuleService : IRuleService
{
    public List<MailRule> LoadedRules { get; set; } = [];
    public int ApplyRulesReturnValue { get; set; } = 0;

    public List<MailRule> LoadRules() => LoadedRules;
    public void SaveRules(List<MailRule> rules) => LoadedRules = rules;

    public Task<int> ApplyRulesAsync(
        List<MailMessageSummary> incoming, Guid accountId, CancellationToken ct)
        => Task.FromResult(ApplyRulesReturnValue);

    public List<MailMessageSummary> TestRule(MailRule rule, IEnumerable<MailMessageSummary> messages)
        => messages.Where(m => true).ToList(); // Stub matches everything
}
```

### 6.2 `RuleServiceTests.cs`

**Path:** `QuickMail.Tests/RuleServiceTests.cs`

Test cases (use `[Fact]` and `[StaFact]` where WPF thread is needed — `RuleService` itself doesn't need STA):

| Test | What it verifies |
|---|---|
| `LoadRules_EmptyFile_ReturnsEmptyList` | No `rules.json` → empty list |
| `LoadRules_CorruptedFile_ReturnsEmptyList` | Malformed JSON → empty list, no throw |
| `SaveRules_RoundTrip_PreservesAllFields` | Save then Load returns identical rules |
| `SaveRules_AtomicWrite_DoesNotCorruptOnCrash` | Temp file pattern; existing file survives failed save |
| `TestRule_FromContains_MatchesSubstring` | Case-insensitive substring match on From |
| `TestRule_ToContains_MatchesSubstring` | Case-insensitive substring match on To |
| `TestRule_SubjectContains_MatchesSubstring` | Case-insensitive substring match on Subject |
| `TestRule_BodyContains_MatchesPreview` | Case-insensitive substring match on Preview |
| `TestRule_HasAttachments_MatchesOnlyWhenTrue` | `MustHaveAttachments` filters correctly |
| `TestRule_AllConditionsAnded_RequiresAllMatch` | Multiple conditions all must match |
| `TestRule_EmptyConditions_MatchesEverything` | Null/empty conditions don't filter |
| `TestRule_AccountId_ScopesToAccount` | `AccountId` filter works |
| `ApplyRulesAsync_DisabledRule_Skipped` | `IsEnabled = false` → rule not evaluated |
| `ApplyRulesAsync_WrongAccount_Skipped` | `AccountId` mismatch → rule skipped |
| `ApplyRulesAsync_MarkAsRead_UpdatesIsRead` | Action executed, `IsRead` set to true |
| `ApplyRulesAsync_ReturnsMatchCount` | Return value equals number of matched messages |

### 6.3 `RulesManagerViewModelTests.cs`

**Path:** `QuickMail.Tests/RulesManagerViewModelTests.cs`

| Test | What it verifies |
|---|---|
| `Constructor_LoadsRulesFromService` | Rules from `IRuleService.LoadRules()` appear in `Rules` collection |
| `Constructor_NoRules_EmptyCollection` | Empty list → `Rules` is empty, `SelectedRule` is null |
| `Constructor_WithPrefillTemplate_SelectsTemplate` | Template passed → `SelectedRule` is the template |
| `NewRule_AddsRuleAndSelectsIt` | `NewRuleCommand` adds to `Rules` and sets `SelectedRule` |
| `DeleteRule_Confirmed_RemovesRule` | `ConfirmDeleteRequested` returns true → rule removed |
| `DeleteRule_NotConfirmed_KeepsRule` | `ConfirmDeleteRequested` returns false → rule kept |
| `SaveRule_ValidRule_SavesAndAnnounces` | Valid rule → `SaveRules` called, announcement fired |
| `SaveRule_EmptyName_ShowsError` | Empty name → `NameError` populated, save aborted |
| `SaveRule_MoveToFolderNoTarget_ShowsError` | Move action without `TargetFolder` → `FolderError` populated |
| `SaveRule_MoveToFolderNoConditions_ShowsError` | Move action with no conditions → `ConditionsError` populated |
| `TestRule_WithMessages_ShowsMatchCount` | `TestRuleCommand` with messages → status text updated |
| `TestRule_NoMessages_ShowsWarning` | `TestRuleCommand` with null messages → appropriate status |
| `Close_FiresCloseRequested` | `CloseCommand` raises `CloseRequested` event |

### 6.4 `RulesManagerXamlParseTests.cs`

**Path:** `QuickMail.Tests/RulesManagerXamlParseTests.cs`

Follow the pattern from existing `XamlParseTests.cs`:

```csharp
[StaFact]
public void RulesManagerWindow_LoadsWithoutXamlParseException()
{
    var ruleService = new StubRuleService();
    var imap = new StubImapService();
    var accounts = Array.Empty<AccountModel>();
    var vm = new RulesManagerViewModel(ruleService, imap, accounts);
    var window = new RulesManagerWindow(vm);
    // If we get here without XamlParseException, the test passes.
    Assert.NotNull(window);
}
```

---

## 7. Edge Cases & Error Handling

| Scenario | Expected behavior |
|---|---|
| `rules.json` is missing | `LoadRules()` returns empty list. No error. |
| `rules.json` is corrupted JSON | `LoadRules()` returns empty list. Logged at debug level. |
| Rule has `MoveToFolder` action but `TargetFolder` is null | Rule is skipped during execution. Error logged once per sync cycle. |
| Target folder doesn't exist on server | `ImapService.MoveMessagesAsync` throws. Error logged. Message stays in original folder. |
| User creates rule with no conditions and Delete action | Validation in ViewModel rejects it. "At least one condition is required." |
| User creates rule with no conditions and MarkAsRead action | Allowed — this is a valid use case (mark everything as read). |
| 100+ rules defined | All evaluated in order. Performance: 100 rules × 50 messages = 5,000 condition checks, negligible. |
| Rule references a deleted account | `AccountId` won't match any incoming messages. Rule silently does nothing. |
| Sync is cancelled while rules are running | `CancellationToken` is passed through. `OperationCanceledException` propagates to `SyncService`. |
| IMAP move fails for some messages in a batch | The entire batch fails (MailKit behavior). Error logged. Messages stay in original folder. |
| Two rules both match the same message | Both execute (non-exclusive). If both move, the second move will fail because the message already moved. Error logged. |
| Rule name contains only whitespace | Validation rejects: "Rule name is required." |

---

## 8. Accessibility Checklist

Before marking this feature complete, verify every item:

- [ ] All `TextBox` and `ComboBox` controls have `AutomationProperties.Name` set
- [ ] All `Button` controls have `AutomationProperties.Name` set
- [ ] The rule `ListBox` has `AutomationProperties.Name` and each item has a name via `ItemContainerStyle`
- [ ] Error messages use `AutomationProperties.LiveSetting="Assertive"` so screen readers announce them immediately
- [ ] Status bar text uses `AutomationProperties.LiveSetting="Polite"`
- [ ] Tab order is logical: rule list → name → enabled → account → conditions → action → folder → test → save → close
- [ ] `Escape` closes the dialog (via `IsCancel="True"` on Close button)
- [ ] `Enter` saves the rule (via `IsDefault="True"` on Save button)
- [ ] `Ctrl+N` creates a new rule
- [ ] `Delete` key on rule list triggers delete (with confirmation)
- [ ] All announcements use `AccessibilityHelper.Announce()` with explicit `AnnouncementCategory`
- [ ] No `AutomationProperties.Name` contains instructional text (e.g., "Press Tab to…") — use `Hint` announcements instead
- [ ] Color is not the only indicator of state (enabled/disabled uses text in AutomationProperties, not just icon)
- [ ] All interactive elements have minimum 24×24 pixel clickable area
- [ ] Dialog has no animations (respects reduced motion)

---

## 9. Build Verification

After all changes are implemented, run:

```bat
dotnet build QuickMail.sln -nologo
```

Expected: 0 errors. Existing warnings in `MailMessageDetail.cs` and `QuickMail.Tests/UnitTest1.cs` are pre-existing and acceptable.

Then run tests:

```bat
dotnet test QuickMail.Tests/QuickMail.Tests.csproj -c Release
```

All new tests must pass. All existing tests must continue to pass.

---

## Appendix A: Constructor Signature Changes Summary

For quick reference, here are all the constructor signatures that change:

### `SyncService` (before → after)

```csharp
// Before:
public SyncService(IImapService imap, ILocalStoreService store, IConfigService config)

// After:
public SyncService(IImapService imap, ILocalStoreService store, IConfigService config, IRuleService rules)
```

### `MainViewModel` (before → after)

```csharp
// Before (last param was IViewService):
public MainViewModel(
    IImapService imap, IAccountService accountService, ICredentialService credentials,
    ILocalStoreService localStore, IOAuthService oauthService, ISyncService syncService,
    IConfigService configService, ICommandRegistry commandRegistry, IViewService viewService)

// After (add IRuleService):
public MainViewModel(
    IImapService imap, IAccountService accountService, ICredentialService credentials,
    ILocalStoreService localStore, IOAuthService oauthService, ISyncService syncService,
    IConfigService configService, ICommandRegistry commandRegistry, IViewService viewService,
    IRuleService ruleService)
```

### `MainWindow` (before → after)

```csharp
// Before (last param was IViewService):
public MainWindow(
    MainViewModel vm, ISmtpService smtp, IAccountService accountService,
    ICredentialService credentials, IImapService imap, IOAuthService oauth,
    ICommandRegistry registry, IContactService contactService,
    IConfigService configService, ILocalStoreService localStore, IViewService viewService)

// After (add IRuleService):
public MainWindow(
    MainViewModel vm, ISmtpService smtp, IAccountService accountService,
    ICredentialService credentials, IImapService imap, IOAuthService oauth,
    ICommandRegistry registry, IContactService contactService,
    IConfigService configService, ILocalStoreService localStore, IViewService viewService,
    IRuleService ruleService)
```

## Appendix B: New Command Registry Entries

| Command ID | Category | Title | Default Shortcut |
|---|---|---|---|
| `mail.rules` | Mail | Manage Rules | `Ctrl+Shift+L` |
| `mail.createRuleFromMessage` | Mail | Create Rule from Message | `Ctrl+Shift+T` |

> **Note:** The original spec used `Ctrl+Shift+R` for `mail.rules`, but that conflicted with Reply All. Changed to `Ctrl+Shift+L` during implementation.

## Appendix C: Lessons Learned

**Date:** 2026-05-22  
**Author:** Engineering, with PM input

### Bugs found after initial implementation

The initial implementation passed all 181 unit tests but had four integration-level bugs discovered during manual testing:

| # | Bug | Root cause | Fix |
|---|---|---|---|
| 1 | Moved/deleted messages still appeared in UI after sync | `ApplyRulesAsync` removed messages from `incoming` but the `FolderSynced` event still passed the full list | `ApplyRulesAsync` now returns a `(matchedCount, removedMessages)` tuple; `SyncFolderAsync` removes them from `incoming` before raising `FolderSynced` |
| 2 | Messages reappeared after cache reload | `UpsertSummariesAsync` was called *before* rules ran, persisting moved messages to SQLite; `InitialLoadAsync` reloaded them | `SyncFolderAsync` now calls `DeleteSummariesAsync` for rule-moved/deleted messages and raises `MessagesRemoved` |
| 3 | Rules had no visible effect when viewing a regular folder (e.g., INBOX) | `OnFolderSynced` only handled virtual folders (All Mail, per-account All Mail); regular folders returned early | Added a branch in `OnFolderSynced` for regular folders that filters by `AccountId` and `FolderName` |
| 4 | Rules never fired on messages that were already cached before the rule was created | Rules only ran on NEW messages during sync (UID > maxUid) | Added `ApplyRulesToExistingAsync` which loads all cached messages and runs enabled rules against them; called from `MainWindow.OpenRulesManager` after the dialog closes |

### Data-flow diagram (should have been in the original spec)

This diagram traces a single message through the system. Every bug above lives on one of these arrows:

```
IMAP Server
    │
    ▼ GetMessagesSinceAsync()
[incoming: List<MailMessageSummary>]
    │
    ├──► UpsertSummariesAsync(incoming)     ← Bug #2: called before rules
    │
    ├──► ApplyRulesAsync(incoming, accountId)
    │       │
    │       ├── MatchesRule() → matched list
    │       ├── ExecuteActionAsync() → IMAP MOVE/DELETE on server
    │       └── Remove matched from incoming  ← Bug #1: wasn't removing
    │
    ├──► DeleteSummariesAsync(removed)       ← Bug #2 fix: delete from SQLite
    │
    ├──► FolderSynced?.Invoke(incoming)      ← Bug #3: OnFolderSynced ignored regular folders
    │       │
    │       └── OnFolderSynced() → UI inserts
    │
    └──► MessagesRemoved?.Invoke(removed)    ← Bug #2 fix: drop from UI
            │
            └── OnMessagesRemoved() → UI removes

After Rules Manager closes:
    ApplyRulesToExistingAsync(store)          ← Bug #4: didn't exist
        │
        ├── LoadAllSummariesAsync() → all cached messages
        ├── MatchesRule() × all messages
        ├── ExecuteActionAsync() → IMAP actions
        └── DeleteSummariesAsync() → remove from SQLite
            │
            └── RefreshCommand → UI reloads from cache
```

### Integration test that would have caught bugs #1–#3

```csharp
[Fact]
public async Task SyncFolderAsync_RuleDeletesMessage_MessageNotInStoreOrUI()
{
    // Arrange: create a rule that deletes messages from "alice"
    var ruleService = new RuleService(stubImap, realStore, tempDir);
    ruleService.SaveRules(new[] { new MailRule {
        Name = "Delete Alice", FromContains = "alice", Action = RuleAction.Delete }});

    var syncService = new SyncService(stubImap, realStore, stubConfig, ruleService);
    var incoming = new List<MailMessageSummary> { MakeMsg(from: "alice@example.com") };

    // Act: simulate what SyncFolderAsync does
    await realStore.UpsertSummariesAsync(incoming);
    var (count, removed) = await ruleService.ApplyRulesAsync(incoming, accountId, ct);
    await realStore.DeleteSummariesAsync(accountId, "INBOX", removed.Select(m => m.UniqueId));

    // Assert: message is gone from both incoming list and local store
    Assert.Empty(incoming);                          // Bug #1 would fail here
    var cached = await realStore.LoadAllSummariesAsync();
    Assert.Empty(cached);                            // Bug #2 would fail here
}
```

### Process recommendations for future AI-assisted specs

1. **Every dev spec must include a data-flow diagram.** Not optional. One diagram tracing the primary entity (message, event, etc.) through every component. Bugs live on the arrows between boxes.

2. **Every dev spec must specify at least one integration test.** Unit tests verify components. Integration tests verify the seams. The spec should include the test name, arrange/act/assert structure, and which bugs it prevents.

3. **Every dev spec must include a manual test checklist.** Three to five steps a human can run in under two minutes. "Generate mail → create rule → press F5 → verify result."

4. **Spec review and implementation should be done by different agents.** The author's blind spots carry through to the code. A review pass by a separate agent (or a human) against the spec catches boundary issues.

5. **The PM spec must answer "when does this fire?" for ALL states.** The original spec said rules run "on incoming messages as they arrive during background sync." It should have also addressed: What about messages already in the inbox? What about messages that arrive while the app is closed? What about messages in folders other than INBOX? Each unanswered "when" question becomes a potential bug.

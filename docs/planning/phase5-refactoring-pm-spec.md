# Phase 5 Refactoring — PM Specification

**Status:** Ready for Dev
**Date:** May 25, 2026
**Target:** Phase 5 (Refactoring)
**Crew:** Echo (PM → Dev Lead → Test Enforcer)

---

## User Problem

No user-facing features. This is technical debt reduction to make the codebase sustainable. `MainViewModel` is 3,355 lines. `MainWindow.xaml.cs` is 3,010 lines. Both have repeated patterns that make bugs more likely and new features harder to add.

## Refactoring Items

### R1: Extract `RebuildActiveGroupView()` pattern (12 occurrences)
Every action that mutates `Messages` does:
```csharp
if (ViewMode == ViewMode.Conversations) ScheduleConversationRebuild();
else if (ViewMode == ViewMode.From)     ScheduleSenderGroupRebuild();
else if (ViewMode == ViewMode.To)       ScheduleToGroupRebuild();
```
Replace with one method and update all call sites.

### R2: Extract tree-view triplets from MainWindow.xaml.cs
Three tree views (ConversationTree, SenderGroupTree, ToGroupTree) each have their own copy of ~10 event handlers. Extract a `GroupedMessageTreeController` class.

### R3: Fix CTS disposal
`CancellationTokenSource` instances are cancelled but never disposed. Add a `ReplaceCts` helper.

### R4: Centralize ViewMode/Sort serialization
`ConfigModel.ViewMode` and `ConfigModel.Sort` are magic strings converted by switch statements in 6+ places. Centralize the mapping.

## Acceptance Criteria

- [ ] `RebuildActiveGroupView()` method exists and all 12 call sites use it
- [ ] `GroupedMessageTreeController` class exists and all three tree views use it
- [ ] CTS disposal helper exists and all CTS replacements use it
- [ ] ViewMode/Sort mapping is centralized in one place
- [ ] All 341 existing tests still pass
- [ ] Build: 0 warnings, 0 errors

## Files to Modify

| File | Change |
|------|--------|
| `QuickMail/ViewModels/MainViewModel.cs` | Extract R1, R3, R4 |
| `QuickMail/Views/MainWindow.xaml.cs` | Extract R2 |
| `QuickMail/Models/ConfigModel.cs` | R4: enum-based ViewMode/Sort |

## Files to Create

| File | Purpose |
|------|---------|
| `QuickMail/Views/GroupedMessageTreeController.cs` | R2: shared tree-view logic |

---

*This spec is ready for Dev Lead implementation.*

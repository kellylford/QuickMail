# Message Templates — PM Specification

**Status:** Ready for Dev
**Date:** May 24, 2026
**Target:** Phase 4 (Power User)
**Crew:** Delta (PM → Dev Lead → Test Enforcer)

---

## User Problem

Users frequently send the same or similar messages: "Thanks for your email, I'll get back to you soon," meeting follow-ups, support responses, etc. Typing these repeatedly is tedious — especially for screen reader users who can't quickly scan and copy-paste from previous messages.

## User Stories

1. **As a screen reader user**, I want to save common responses as templates and insert them with a few keystrokes.
2. **As a keyboard user**, I want to pick a template from a searchable list without touching the mouse.
3. **As a power user**, I want templates to support placeholders like `{sender}` and `{date}` that auto-fill.

## Acceptance Criteria

- [ ] Templates stored in `%APPDATA%\QuickMail\templates.json`
- [ ] Template model: `Title` (display name), `Subject` (optional, pre-fills compose subject), `Body` (the template text)
- [ ] Placeholder support: `{sender}` → sender's display name, `{date}` → current date, `{time}` → current time
- [ ] "Insert Template" button in compose window toolbar
- [ ] Template picker: searchable list dialog, keyboard navigable, Enter to insert
- [ ] "Save as Template" from compose window (saves current body as new template)
- [ ] "Manage Templates" dialog: add, edit, delete, reorder
- [ ] All new commands registered in CommandRegistry
- [ ] All dialogs fully keyboard accessible with proper AutomationProperties

## Accessibility Requirements

- Template picker announces match count on search: "3 templates matching 'thanks'"
- Template list items have AutomationProperties.Name = template title
- Insert confirmation: "Template 'Meeting Follow-up' inserted"
- Save confirmation: "Template saved as 'My Response'"
- All announcements via AccessibilityHelper.Announce() with AnnouncementCategory.Result

## Technical Notes

- Follow the pattern of `ContactService` / `ViewService` for JSON file storage
- Create `ITemplateService` / `TemplateService`
- Template picker is a simple dialog (like FolderPickerWindow but simpler)
- Wire into DI in App.xaml.cs

## Files to Create

| File | Purpose |
|------|---------|
| `QuickMail/Models/MessageTemplate.cs` | Template data model |
| `QuickMail/Services/ITemplateService.cs` | Service interface |
| `QuickMail/Services/TemplateService.cs` | JSON storage, CRUD |
| `QuickMail/ViewModels/TemplatePickerViewModel.cs` | Picker dialog VM |
| `QuickMail/Views/TemplatePickerWindow.xaml` | Picker dialog UI |
| `QuickMail/Views/TemplatePickerWindow.xaml.cs` | Code-behind |

## Files to Modify

| File | Change |
|------|---------|
| `QuickMail/ViewModels/ComposeViewModel.cs` | Add InsertTemplateCommand, SaveAsTemplateCommand |
| `QuickMail/Views/ComposeWindow.xaml` | Add "Insert Template" button |
| `QuickMail/Views/ComposeWindow.xaml.cs` | Wire template picker dialog |
| `QuickMail/Views/MainWindow.xaml.cs` | Register template commands |
| `QuickMail/App.xaml.cs` | Create TemplateService, inject |

## Tests to Add

| File | Test |
|------|------|
| `QuickMail.Tests/TemplateServiceTests.cs` | CRUD operations, placeholder replacement |
| `QuickMail.Tests/TemplatePickerViewModelTests.cs` | Search filtering, selection |
| `QuickMail.Tests/ViewModelConstructionTests.cs` | TemplatePickerViewModel constructs |

---

*This spec is ready for Dev Lead implementation.*

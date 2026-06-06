# Alt+Enter — View Properties: PM & Dev Specification

**Status:** Implemented
**Date:** June 2, 2026
**Target:** Phase 5 (Power User) — Discoverability & Information
**Crew:** Delta (PM → Dev Lead → Test Enforcer)

> Combined PM + Dev spec. **Sections 1–6 are the PM portion** (problem, users, scope, UX, accessibility). **Sections 7–12 are the Dev portion** (architecture, data model, view models, views, context dispatch, implementation phases). **Sections 13+** are shared (success metrics, open questions, file/test tables, appendices).

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [User Problem & Opportunity](#2-user-problem--opportunity)
3. [Personas & Use Cases](#3-personas--use-cases)
4. [Competitive Landscape](#4-competitive-landscape)
5. [Design Principles](#5-design-principles)
6. [Feature Scope & Acceptance Criteria](#6-feature-scope--acceptance-criteria)
7. [Data Model](#7-data-model)
8. [Context Builders](#8-context-builders)
9. [PropertiesViewModel](#9-propertiesviewmodel)
10. [PropertiesWindow View](#10-propertieswindow-view)
11. [Context Dispatch](#11-context-dispatch)
12. [Command Registry](#12-command-registry)
13. [Accessibility (WCAG 2.2)](#13-accessibility-wcag-22)
14. [Implementation Phases](#14-implementation-phases)
15. [Success Metrics](#15-success-metrics)
16. [Open Questions & Risks](#16-open-questions--risks)
17. [Files to Create](#17-files-to-create)
18. [Files to Modify](#18-files-to-modify)
19. [Tests to Add](#19-tests-to-add)
20. [Appendix A — Property Layouts by Context](#appendix-a--property-layouts-by-context)
21. [Appendix B — Sample User Flows](#appendix-b--sample-user-flows)

---

## 1. Executive Summary

**Alt+Enter** is the Windows standard keyboard shortcut for "Properties." In File Explorer, Windows
Terminal, and most shell utilities it opens a properties dialog for whatever is currently selected.
Power users, sighted and blind alike, have muscle memory for it. QuickMail currently does nothing
with Alt+Enter; pressing it is a dead key.

This feature gives Alt+Enter meaning across every focusable context in QuickMail:

| Focus context | What opens |
|---|---|
| Message selected in message list | Message headers and metadata |
| Folder selected in folder tree | Folder name, path, account, message counts |
| Account selected in account list | Server settings, auth method, sync status |
| Contact selected in address book | Name, email, group memberships, last used |
| Group selected in address book | Name, member count, member list |
| Attachment in a message | Filename, MIME type, size, encoding |
| Reading pane open | Same as message selected |

A single reusable `PropertiesWindow` renders every context. The default presentation is a two-column
**ListView of field/value pairs**, which is maximally keyboard- and screen-reader-accessible. Where
a plain field/value row does not adequately represent the data (attachments list, group members,
raw headers), the design uses a principled deviation described in §5 and §10.

---

## 2. User Problem & Opportunity

### 2.1 Current state

- Alt+Enter is unhandled everywhere in QuickMail. The key falls through to WPF's default behavior
  (nothing, or a system beep).
- Message headers are inaccessible without an external tool. The only way to see a Message-ID,
  review routing headers, or check MIME structure is to view the raw source outside the app.
- Folder statistics (unread count, total, IMAP path) are not exposed except as inline numbers in
  the folder tree — numbers with no context and no way to copy them.
- Account server settings are write-only after setup: visible in the Settings dialog only while
  editing, not inspectable at a glance.
- Contact and group metadata (last-used date, group membership) is not surfaced anywhere.

### 2.2 Why it matters

**For screen reader users**, properties dialogs are especially important. Sighted users can glean
metadata from layouts, icon badges, and column widths. Screen reader users hear only what is
explicitly announced. A dedicated Properties view that reads out "From: Alice Smith, To: Bob Jones,
Date: June 2 2026, Size: 42 KB, Unread: yes, Account: Work" gives them the same situational
awareness a sighted user gets from the message row at a glance.

**For power users**, message headers are diagnostic. Troubleshooting delivery issues, checking
spam scores, verifying DKIM/SPF, or extracting a Message-ID for a support ticket all require
raw header access.

**For support/admin users**, folder statistics (message counts, path) are needed for storage
management and rule debugging.

### 2.3 Non-goals (out of scope for v1)

- **Editing** any property from within the Properties window. Properties are read-only.
- **Folder size** in bytes (requires a `GETQUOTA` IMAP extension; not universally supported).
- **Cryptographic signature / S/MIME / PGP verification details.** Out of scope until a
  crypto feature lands.
- **Contact edit** from the Properties window. Use the address book for that.
- **Exporting properties** to a file. Copy-to-clipboard covers the primary use case.

---

## 3. Personas & Use Cases

| Persona | Need | Use case |
|---|---|---|
| **Screen reader user** (Pat) | "I need to know who this message is actually from, including Reply-To, and what IMAP folder it's in." | Presses Alt+Enter on a selected message; hears the full header list read out by field name and value. |
| **Power user** (Alex) | "I need the raw Message-ID to file a support ticket with our mail server admin." | Opens message properties; navigates to "Message-ID" row; Ctrl+C copies just that value. |
| **New user** (Jordan) | "I forgot what port my IMAP server uses. I don't want to open Settings just to check." | Selects the account in the sidebar; presses Alt+Enter; reads the IMAP host and port. |
| **Heavy address book user** (Riley) | "I want to know which groups Alice is a member of before I remove her from one." | Selects Alice in the address book; presses Alt+Enter; sees "Groups: Project Team, Family, Book Club." |
| **Admin / IT user** (Sam) | "Our Trash folder seems huge. How many messages are in it?" | Selects the Trash folder; presses Alt+Enter; reads "Total messages: 4,312." |
| **Compose user** (Chris) | "This attachment's filename is truncated in the message. What's the full name and size?" | Opens attachment; presses Alt+Enter on it in the attachment list; reads full filename and size. |

---

## 4. Competitive Landscape

| Product | Properties access | Strengths | Weaknesses |
|---|---|---|---|
| **Microsoft Outlook** | Alt+Enter on message → Properties (headers + delivery options). Folder: right-click → Properties. | Consistent with Windows conventions. Full raw headers in a `TextBox`. | Mouse-first discovery; no keyboard shortcut for folder properties. |
| **Thunderbird** | Ctrl+U for message source (raw). No folder properties shortcut. | Raw source is complete. | Different key from Windows convention; no parsed view; inaccessible wall of text. |
| **Windows Mail / New Outlook** | No keyboard shortcut for message properties. Folder info is visual-only. | N/A | Regressed from classic Outlook. |
| **Gmail** | No keyboard shortcut for message headers. "Show original" is a menu item (several clicks). | Web app — no keyboard convention to follow. | Very inaccessible for screen reader users. |
| **QuickMail (current)** | Nothing. | N/A | Alt+Enter is a dead key. |

**QuickMail positioning.** Be the app that follows Windows keyboard conventions faithfully. Alt+Enter
is muscle memory for Windows power users. A structured, accessible field/value presentation beats
Thunderbird's wall of raw text for both sighted and blind users.

---

## 5. Design Principles

1. **Windows-conventional.** Alt+Enter is the system standard for "Properties." We follow it
   everywhere — no exceptions, no "it doesn't make sense here."

2. **Context-aware, single window.** One `PropertiesWindow` class handles all contexts. The title
   changes ("Message Properties", "Folder Properties") but the shell is identical.

3. **Field/value list by default.** A two-column `ListView` (field name, value) is the default
   presentation for every context. It is keyboard-navigable, screen-reader-compatible, and
   copy-friendly.

4. **Principled deviations only.** Where a field/value row is inadequate (a list of attachments,
   a list of group members, a blob of raw headers), a clearly described alternative is used:
   - **Sub-list** (separate `ListView` in its own labeled section) for attachments and group members.
   - **Read-only `TextBox`** inside an `Expander` for raw message headers (copy-paste-friendly,
     searchable with Ctrl+F in a text editor once copied).

5. **Read-only.** No editing. The Properties window closes with Escape or a Close button only.

6. **Copy-friendly.** Ctrl+C on a selected row copies "Field: Value" to the clipboard. A
   "Copy all" button at the bottom copies all visible properties as formatted plain text.

7. **No credentials.** Account properties show auth method ("OAuth2" or "Password, stored in
   Windows Credential Manager") but never display, hint at, or log a password or token.

8. **Screen-reader first.** Every row's `AutomationProperties.Name` is "Field: Value" so it
   reads naturally without column-header preamble. Section headers are announced as region
   landmarks. Every announcement goes through `AccessibilityHelper.Announce()`.

---

## 6. Feature Scope & Acceptance Criteria

### 6.1 In scope (v1)

- [ ] `PropertiesWindow` — single reusable dialog for all contexts.
- [ ] `PropertyItem` record: `{ string Label, string Value }`.
- [ ] `PropertySection` record: `{ string Header, IReadOnlyList<PropertyItem> Items }`.
- [ ] `PropertiesViewModel` — takes a title and ordered list of `PropertySection`.
- [ ] Context builders (static, pure functions, no DI):
  - `MessagePropertiesBuilder` — from `MessageSummary` + optional `MessageDetail`.
  - `FolderPropertiesBuilder` — from `MailFolderModel` + optional message counts.
  - `AccountPropertiesBuilder` — from `AccountModel`.
  - `ContactPropertiesBuilder` — from `ContactModel` + group name list.
  - `GroupPropertiesBuilder` — from `GroupModel` + resolved `ContactModel` list.
  - `AttachmentPropertiesBuilder` — from `AttachmentInfo`.
- [ ] Alt+Enter registered in `CommandRegistry` as `view.showProperties`.
- [ ] Context dispatch in `MainViewModel.ShowPropertiesCommand` and `AddressBookViewModel.ShowPropertiesCommand`.
- [ ] Attachment context: Alt+Enter on a focused attachment in the attachment strip.
- [ ] Ctrl+C on a selected row copies "Field: Value" to clipboard.
- [ ] "Copy all" button copies all sections as formatted plain text.
- [ ] Raw headers section (message context only): collapsible `Expander` with read-only `TextBox`.
- [ ] Sub-list sections for attachments (within Message Properties) and group members (within Group Properties).
- [ ] All controls have `AutomationProperties.Name`; all announcements through `AccessibilityHelper.Announce()`.
- [ ] Unit tests for all context builders and the ViewModel.
- [ ] `XamlParseTests` coverage for `PropertiesWindow`.

### 6.2 Out of scope (v1)

- [ ] Property editing from within the Properties window.
- [ ] Folder size in bytes.
- [ ] S/MIME / PGP / DKIM verification display.
- [ ] Contact editing from the Properties window.
- [ ] Printing properties.

### 6.3 Acceptance criteria

- [ ] Alt+Enter with a message selected opens "Message Properties" with From, To, Subject, Date,
      Message-ID, Account, Folder, and Size all visible within the first two sections.
- [ ] Alt+Enter with a folder selected opens "Folder Properties" showing the IMAP path, account
      name, and message counts.
- [ ] Alt+Enter with an account selected opens "Account Properties" showing IMAP host/port and
      SMTP host/port. No password is shown.
- [ ] Alt+Enter with a contact selected in the address book shows display name, email, and group
      memberships.
- [ ] Alt+Enter with a group selected in the address book shows group name, member count, and
      a member sub-list.
- [ ] Ctrl+C on a selected row puts "Field: Value" on the clipboard.
- [ ] "Copy all" produces a readable multi-line plain-text block with section headers.
- [ ] Escape closes the dialog.
- [ ] All field labels and values are announced by screen readers without needing to read column
      headers first.
- [ ] No password, OAuth token, or credential hint appears in any property value.
- [ ] Alt+Enter with nothing selected (e.g., focus on the toolbar) does nothing (no crash, no
      empty dialog).

---

## 7. Data Model

### 7.1 New types

```csharp
// filepath: QuickMail/Models/PropertyItem.cs
namespace QuickMail.Models;

/// <summary>A single field/value row in a Properties dialog.</summary>
public sealed record PropertyItem(string Label, string Value);
```

```csharp
// filepath: QuickMail/Models/PropertySection.cs
namespace QuickMail.Models;

/// <summary>A named group of PropertyItem rows within a Properties dialog.</summary>
public sealed record PropertySection(
    string Header,
    IReadOnlyList<PropertyItem> Items);
```

These are pure value types with no dependencies. They are populated by the context builders
(§8) and consumed by `PropertiesViewModel` (§9).

### 7.2 No persistence

`PropertyItem` and `PropertySection` are display-only in-memory structures. They are never
written to disk and carry no `[JsonIgnore]` or migration concerns.

### 7.3 No new fields on existing models

The feature reads from `MessageSummary`, `MessageDetail`, `MailFolderModel`, `AccountModel`,
`ContactModel`, and `GroupModel` as-is. No changes to those types are required.

---

## 8. Context Builders

Context builders are **static classes with pure methods** — no DI, no side effects, no async.
They transform existing model objects into the `(title, sections[])` pair that
`PropertiesViewModel` needs. This keeps them trivially testable and decoupled from the UI lifecycle.

### 8.1 `MessagePropertiesBuilder`

```csharp
// filepath: QuickMail/Helpers/MessagePropertiesBuilder.cs
namespace QuickMail.Helpers;

public static class MessagePropertiesBuilder
{
    public static (string Title, IReadOnlyList<PropertySection> Sections)
        Build(MessageSummary summary, MessageDetail? detail, string accountName)
    {
        var headers = new List<PropertyItem>
        {
            new("From",       summary.From   ?? "(unknown)"),
            new("To",         summary.To     ?? "(none)"),
            new("Cc",         NoneIfBlank(summary.Cc)),
            new("Reply-To",   NoneIfBlank(summary.ReplyTo)),
            new("Subject",    summary.Subject ?? "(no subject)"),
            new("Date",       summary.Date.ToLocalTime().ToString("f")),
            new("Message-ID", NoneIfBlank(summary.MessageId)),
        };

        var storage = new List<PropertyItem>
        {
            new("Account",    accountName),
            new("Folder",     summary.FolderFullName),
            new("IMAP UID",   summary.Uid.ToString()),
            new("Size",       FormatBytes(summary.Size)),
            new("Flags",      FormatFlags(summary)),
        };

        var content = BuildContentSection(summary, detail);

        var sections = new List<PropertySection>
        {
            new("Headers",  headers),
            new("Storage",  storage),
        };
        if (content.Items.Count > 0) sections.Add(content);

        return ("Message Properties", sections);
    }

    private static PropertySection BuildContentSection(MessageSummary s, MessageDetail? d)
    {
        var items = new List<PropertyItem>();
        if (d is not null)
        {
            items.Add(new("Format",
                d.HasHtmlBody && d.HasTextBody ? "HTML with plain-text alternative" :
                d.HasHtmlBody                  ? "HTML only" :
                                                 "Plain text"));
            if (d.Attachments.Count > 0)
                items.Add(new("Attachments",
                    $"{d.Attachments.Count} attachment{(d.Attachments.Count == 1 ? "" : "s")}"));
        }
        return new("Content", items);
    }

    private static string NoneIfBlank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? "(none)" : s;

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024        => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _             => $"{bytes / (1024.0 * 1024):F1} MB",
    };

    private static string FormatFlags(MessageSummary s)
    {
        var flags = new List<string>();
        if (!s.IsRead)    flags.Add("Unread");
        if (s.IsFlagged)  flags.Add("Flagged");
        if (s.IsAnswered) flags.Add("Answered");
        return flags.Count > 0 ? string.Join(", ", flags) : "None";
    }
}
```

### 8.2 `FolderPropertiesBuilder`

```csharp
// filepath: QuickMail/Helpers/FolderPropertiesBuilder.cs
public static class FolderPropertiesBuilder
{
    public static (string Title, IReadOnlyList<PropertySection> Sections)
        Build(MailFolderModel folder, string accountName, int totalMessages, int unreadMessages)
    {
        var items = new List<PropertyItem>
        {
            new("Name",              folder.Name),
            new("Full path",         folder.FullName),
            new("Account",           accountName),
            new("Type",              folder.FullName.StartsWith('\x00') ? "Virtual folder" : "IMAP folder"),
            new("Special use",       folder.SpecialUse ?? "(none)"),
            new("Total messages",    totalMessages.ToString("N0")),
            new("Unread messages",   unreadMessages.ToString("N0")),
            new("Excluded from All Mail", folder.ExcludeFromAllMail ? "Yes" : "No"),
        };
        return ("Folder Properties", [new("Folder", items)]);
    }
}
```

### 8.3 `AccountPropertiesBuilder`

```csharp
// filepath: QuickMail/Helpers/AccountPropertiesBuilder.cs
public static class AccountPropertiesBuilder
{
    public static (string Title, IReadOnlyList<PropertySection> Sections)
        Build(AccountModel account, DateTimeOffset? lastSyncedUtc)
    {
        var identity = new List<PropertyItem>
        {
            new("Display name",   account.DisplayName),
            new("Email address",  account.EmailAddress),
        };

        var imap = new List<PropertyItem>
        {
            new("Server",   account.ImapHost),
            new("Port",     account.ImapPort.ToString()),
            new("Security", account.ImapSsl ? "SSL/TLS" : "STARTTLS"),
            new("Username", account.ImapUsername),
        };

        var smtp = new List<PropertyItem>
        {
            new("Server",   account.SmtpHost),
            new("Port",     account.SmtpPort.ToString()),
            new("Security", account.SmtpSsl ? "SSL/TLS" : "STARTTLS"),
            new("Username", account.SmtpUsername),
        };

        var auth = new List<PropertyItem>
        {
            new("Authentication",
                account.UseOAuth
                    ? "OAuth2 (Microsoft 365)"
                    : "Password (Windows Credential Manager)"),
            new("Last synced",
                lastSyncedUtc.HasValue
                    ? lastSyncedUtc.Value.ToLocalTime().ToString("f")
                    : "Not yet synced"),
        };

        return ("Account Properties", [
            new("Identity",        identity),
            new("Incoming (IMAP)", imap),
            new("Outgoing (SMTP)", smtp),
            new("Authentication",  auth),
        ]);
    }
}
```

### 8.4 `ContactPropertiesBuilder`

```csharp
// filepath: QuickMail/Helpers/ContactPropertiesBuilder.cs
public static class ContactPropertiesBuilder
{
    public static (string Title, IReadOnlyList<PropertySection> Sections)
        Build(ContactModel contact, IReadOnlyList<string> groupNames)
    {
        var details = new List<PropertyItem>
        {
            new("Display name",  contact.DisplayName ?? "(none)"),
            new("Email address", contact.EmailAddress),
            new("Last used",     contact.LastUsedTicks == 0
                                     ? "Never"
                                     : new DateTime(contact.LastUsedTicks).ToLocalTime().ToString("D")),
            new("Groups",        groupNames.Count == 0
                                     ? "Not a member of any groups"
                                     : string.Join(", ", groupNames)),
        };
        return ("Contact Properties", [new("Contact", details)]);
    }
}
```

### 8.5 `GroupPropertiesBuilder`

```csharp
// filepath: QuickMail/Helpers/GroupPropertiesBuilder.cs
public static class GroupPropertiesBuilder
{
    public static (string Title, IReadOnlyList<PropertySection> Sections)
        Build(GroupModel group, IReadOnlyList<ContactModel> members)
    {
        var details = new List<PropertyItem>
        {
            new("Group name",        group.Name),
            new("Members",           $"{group.ResolvedMemberCount} of {group.MemberContactIds.Count}"),
            new("Missing contacts",  (group.MemberContactIds.Count - group.ResolvedMemberCount).ToString()),
            new("Last used",         group.LastUsedTicks == 0
                                         ? "Never"
                                         : new DateTime(group.LastUsedTicks).ToLocalTime().ToString("D")),
        };

        // Members are surfaced as a sub-list section (deviation from field/value — see §10.3).
        // PropertiesViewModel recognises a section with Header == "Members" and renders it
        // as a secondary ListView rather than a field/value list.
        var memberItems = members
            .Select(c => new PropertyItem(
                c.DisplayName ?? c.EmailAddress,
                c.EmailAddress))
            .ToList();

        IReadOnlyList<PropertySection> sections = memberItems.Count > 0
            ? [new("Group", details), new("Members", memberItems)]
            : [new("Group", details)];

        return ("Group Properties", sections);
    }
}
```

### 8.6 `AttachmentPropertiesBuilder`

```csharp
// filepath: QuickMail/Helpers/AttachmentPropertiesBuilder.cs
public static class AttachmentPropertiesBuilder
{
    public static (string Title, IReadOnlyList<PropertySection> Sections)
        Build(AttachmentInfo attachment)
    {
        var items = new List<PropertyItem>
        {
            new("File name",         attachment.FileName ?? "(unnamed)"),
            new("MIME type",         attachment.ContentType),
            new("Size",              FormatBytes(attachment.Size)),
            new("Transfer encoding", attachment.Encoding ?? "(default)"),
            new("Content-ID",        string.IsNullOrWhiteSpace(attachment.ContentId)
                                         ? "(none)"
                                         : attachment.ContentId),
            new("Inline",            attachment.IsInline ? "Yes" : "No"),
        };
        return ("Attachment Properties", [new("File", items)]);
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024        => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _             => $"{bytes / (1024.0 * 1024):F1} MB",
    };
}
```

---

## 9. PropertiesViewModel

```csharp
// filepath: QuickMail/ViewModels/PropertiesViewModel.cs
namespace QuickMail.ViewModels;

public partial class PropertiesViewModel : ObservableObject
{
    public string Title    { get; }
    public string? RawHeaders { get; }  // non-null only for message context

    /// <summary>
    /// Sections that render as a standard field/value ListView.
    /// Sections named "Members" or "Attachments" are excluded here
    /// and surfaced via <see cref="SubListSections"/>.
    /// </summary>
    public IReadOnlyList<PropertySection> FieldSections  { get; }

    /// <summary>
    /// Sections rendered as a secondary ListView (Members, Attachments).
    /// </summary>
    public IReadOnlyList<PropertySection> SubListSections { get; }

    public PropertiesViewModel(
        string title,
        IReadOnlyList<PropertySection> sections,
        string? rawHeaders = null)
    {
        Title      = title;
        RawHeaders = rawHeaders;

        var subListNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Members", "Attachments" };

        FieldSections   = sections.Where(s => !subListNames.Contains(s.Header)).ToList();
        SubListSections = sections.Where(s =>  subListNames.Contains(s.Header)).ToList();
    }

    [RelayCommand]
    private void CopyRow(PropertyItem? item)
    {
        if (item is null) return;
        Clipboard.SetText($"{item.Label}: {item.Value}");
        AccessibilityHelper.Announce(
            $"Copied: {item.Label}: {item.Value}",
            AnnouncementCategory.Result);
    }

    [RelayCommand]
    private void CopyAll()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Title);
        sb.AppendLine(new string('─', Title.Length));
        foreach (var section in FieldSections.Concat(SubListSections))
        {
            sb.AppendLine();
            sb.AppendLine(section.Header);
            foreach (var item in section.Items)
                sb.AppendLine($"  {item.Label}: {item.Value}");
        }
        if (RawHeaders is not null)
        {
            sb.AppendLine();
            sb.AppendLine("Raw headers");
            sb.AppendLine(RawHeaders);
        }
        Clipboard.SetText(sb.ToString());
        AccessibilityHelper.Announce("All properties copied", AnnouncementCategory.Result);
    }
}
```

**Notes:**
- `Clipboard.SetText` is called from the ViewModel because it is a pure data operation with no
  window dependency. This matches the precedent set by `GrabAddressesViewModel`, which also writes
  to the clipboard. If the project rule is later tightened to keep clipboard writes in code-behind,
  the commands can become events that the View handles — but that is a later refactor, not a
  requirement here.
- `RawHeaders` is passed in as a pre-fetched string by `MainViewModel` when opening message
  properties. The ViewModel does not fetch data.

---

## 10. PropertiesWindow View

### 10.1 Layout overview

```
┌─────────────────────────────────────────┐
│  Message Properties              [Close] │
├─────────────────────────────────────────┤
│  ┌─ Headers ──────────────────────────┐  │
│  │  From      Alice Smith <...>       │  │
│  │  To        Bob Jones <...>         │  │
│  │  Subject   Re: Q3 Planning         │  │
│  │  Date      Monday, June 2, 2026…   │  │
│  │  Message-ID <abc123@mail.ex…>      │  │
│  └────────────────────────────────────┘  │
│  ┌─ Storage ──────────────────────────┐  │
│  │  Account   Work (alice@work.com)   │  │
│  │  Folder    INBOX                   │  │
│  │  IMAP UID  12345                   │  │
│  │  Size      42 KB                   │  │
│  │  Flags     Unread, Flagged         │  │
│  └────────────────────────────────────┘  │
│  ▼ Raw headers                           │  ← Expander (collapsed by default)
│  ┌─ Attachments ──────────────────────┐  │  ← SubList section (if any)
│  │  Name          Type      Size      │  │
│  │  report.pdf    PDF       245 KB    │  │
│  └────────────────────────────────────┘  │
├─────────────────────────────────────────┤
│  [Copy all]                   [Close]   │
└─────────────────────────────────────────┘
```

### 10.2 XAML structure (`PropertiesWindow.xaml`)

```xml
<!-- filepath: QuickMail/Views/PropertiesWindow.xaml -->
<Window x:Class="QuickMail.Views.PropertiesWindow"
        Title="{Binding Title}"
        Width="520" Height="480" MinWidth="360" MinHeight="280"
        ResizeMode="CanResizeWithGrip"
        WindowStartupLocation="CenterOwner">
    <DockPanel>

        <!-- Bottom button row -->
        <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal"
                    HorizontalAlignment="Right" Margin="8">
            <Button Content="Copy _all"
                    TabIndex="90"
                    Command="{Binding CopyAllCommand}"
                    AutomationProperties.Name="Copy all properties" />
            <Button Content="_Close"
                    TabIndex="91"
                    IsCancel="True"
                    AutomationProperties.Name="Close" />
        </StackPanel>

        <!-- Scrollable content -->
        <ScrollViewer VerticalScrollBarVisibility="Auto">
            <StackPanel Margin="8" KeyboardNavigation.TabNavigation="Local">

                <!-- Field sections (one GroupBox per section) -->
                <ItemsControl ItemsSource="{Binding FieldSections}"
                              KeyboardNavigation.TabNavigation="Continue">
                    <ItemsControl.ItemTemplate>
                        <DataTemplate>
                            <GroupBox Header="{Binding Header}" Margin="0,4">
                                <ListView ItemsSource="{Binding Items}"
                                          SelectionMode="Single"
                                          TabIndex="10"
                                          AutomationProperties.Name="{Binding Header}"
                                          KeyDown="PropertyList_KeyDown">
                                    <ListView.View>
                                        <GridView>
                                            <GridViewColumn Header="Field" Width="140"
                                                DisplayMemberBinding="{Binding Label}" />
                                            <GridViewColumn Header="Value" Width="300"
                                                DisplayMemberBinding="{Binding Value}" />
                                        </GridView>
                                    </ListView.View>
                                    <ListView.ItemContainerStyle>
                                        <Style TargetType="ListViewItem">
                                            <!-- Combined label:value for screen readers -->
                                            <Setter Property="AutomationProperties.Name"
                                                Value="{Binding Converter={StaticResource PropertyItemNameConverter}}" />
                                        </Style>
                                    </ListView.ItemContainerStyle>
                                </ListView>
                            </GroupBox>
                        </DataTemplate>
                    </ItemsControl.ItemTemplate>
                </ItemsControl>

                <!-- Raw headers expander (message context only, hidden otherwise) -->
                <Expander Header="Raw headers"
                          Visibility="{Binding RawHeaders,
                              Converter={StaticResource NullToCollapsedConverter}}"
                          Margin="0,4"
                          AutomationProperties.Name="Raw headers">
                    <TextBox Text="{Binding RawHeaders, Mode=OneWay}"
                             IsReadOnly="True"
                             AcceptsReturn="True"
                             TextWrapping="Wrap"
                             VerticalScrollBarVisibility="Auto"
                             MaxHeight="200"
                             FontFamily="Consolas"
                             TabIndex="50"
                             AutomationProperties.Name="Raw message headers" />
                </Expander>

                <!-- Sub-list sections (Members, Attachments) -->
                <ItemsControl ItemsSource="{Binding SubListSections}"
                              KeyboardNavigation.TabNavigation="Continue">
                    <ItemsControl.ItemTemplate>
                        <DataTemplate>
                            <GroupBox Header="{Binding Header}" Margin="0,4">
                                <ListView ItemsSource="{Binding Items}"
                                          SelectionMode="Single"
                                          TabIndex="60"
                                          AutomationProperties.Name="{Binding Header}"
                                          KeyDown="PropertyList_KeyDown">
                                    <ListView.View>
                                        <GridView>
                                            <GridViewColumn Header="Name" Width="200"
                                                DisplayMemberBinding="{Binding Label}" />
                                            <GridViewColumn Header="Detail" Width="240"
                                                DisplayMemberBinding="{Binding Value}" />
                                        </GridView>
                                    </ListView.View>
                                    <ListView.ItemContainerStyle>
                                        <Style TargetType="ListViewItem">
                                            <Setter Property="AutomationProperties.Name"
                                                Value="{Binding Converter={StaticResource PropertyItemNameConverter}}" />
                                        </Style>
                                    </ListView.ItemContainerStyle>
                                </ListView>
                            </GroupBox>
                        </DataTemplate>
                    </ItemsControl.ItemTemplate>
                </ItemsControl>

            </StackPanel>
        </ScrollViewer>
    </DockPanel>
</Window>
```

### 10.3 Code-behind (`PropertiesWindow.xaml.cs`)

```csharp
// filepath: QuickMail/Views/PropertiesWindow.xaml.cs
public partial class PropertiesWindow : Window
{
    private readonly PropertiesViewModel _vm;

    public PropertiesWindow(PropertiesViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void PropertyList_KeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+C copies the selected row; Enter also triggers copy for discoverability.
        if ((e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            || e.Key == Key.Return)
        {
            if (sender is ListView lv && lv.SelectedItem is PropertyItem item)
            {
                _vm.CopyRowCommand.Execute(item);
                e.Handled = true;
            }
        }
    }
}
```

### 10.4 `PropertyItemNameConverter`

```csharp
// filepath: QuickMail/Converters/PropertyItemNameConverter.cs
[ValueConversion(typeof(PropertyItem), typeof(string))]
public sealed class PropertyItemNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is PropertyItem item ? $"{item.Label}: {item.Value}" : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

This converter is what makes `AutomationProperties.Name` read "From: Alice Smith" as a single
string rather than announcing the field and value columns separately.

### 10.5 Deviations from field/value — rationale

| Context | Deviation | Why |
|---|---|---|
| **Attachments sub-list** | Secondary `ListView` (Name / Detail columns) in its own `GroupBox`. Not a field/value row that says "Attachments: 3 attachments". | A message can have many attachments. A row per attachment lets the user navigate by name and read the size of each. A single "Attachments: 3" row gives no actionable detail. |
| **Group members sub-list** | Same pattern — secondary `ListView` (Name / Email columns). | A group can have tens of members. A comma-joined string in a single value column would be unreadable and un-navigable. |
| **Raw headers** | `Expander` containing a read-only `TextBox` with monospace font. Collapsed by default. | Raw RFC 5322 headers are a wall of text with variable structure. The friendly parsed view is always shown first; raw headers are an advanced escape hatch. A `TextBox` allows Ctrl+A/Ctrl+C to grab the full blob, which is the primary use case (forwarding to a mail admin). |

---

## 11. Context Dispatch

### 11.1 Focus-based routing in `MainWindow`

When Alt+Enter is pressed, `MainWindow` determines context from the currently focused pane and
delegates to `MainViewModel.ShowPropertiesCommand`. The VM handles the heavy lifting; code-behind
is limited to routing the key event.

```csharp
// filepath: QuickMail/Views/MainWindow.xaml.cs  (within PreviewKeyDown handler)
// Alt+Enter is handled through CommandRegistry — no hardcoded branch here.
// Registration in §12 covers the dispatch.
```

The `execute` action registered for `view.showProperties` calls
`_vm.ShowPropertiesAsync()`, which is context-aware:

```csharp
// filepath: QuickMail/ViewModels/MainViewModel.cs
public async Task ShowPropertiesAsync()
{
    if (_focusedPane == FocusedPane.MessageList && SelectedMessage is { } msg)
    {
        var detail  = await _localStore.GetMessageDetailAsync(msg.Uid, msg.FolderFullName, msg.AccountId);
        var raw     = detail?.RawHeaders;
        var account = _accounts.FirstOrDefault(a => a.Id == msg.AccountId);
        var (title, sections) = MessagePropertiesBuilder.Build(msg, detail, account?.DisplayName ?? "Unknown");
        ShowPropertiesWindow(new PropertiesViewModel(title, sections, raw));
    }
    else if (_focusedPane == FocusedPane.FolderTree && SelectedFolder is { } folder)
    {
        var account = _accounts.FirstOrDefault(a => a.Id == folder.AccountId);
        var total   = _localStore.GetFolderMessageCount(folder.FullName, folder.AccountId);
        var unread  = _localStore.GetFolderUnreadCount(folder.FullName, folder.AccountId);
        var (title, sections) = FolderPropertiesBuilder.Build(folder, account?.DisplayName ?? "Unknown", total, unread);
        ShowPropertiesWindow(new PropertiesViewModel(title, sections));
    }
    else if (_focusedPane == FocusedPane.AccountList && SelectedAccount is { } acct)
    {
        var (title, sections) = AccountPropertiesBuilder.Build(acct, _syncService.LastSyncedUtc(acct.Id));
        ShowPropertiesWindow(new PropertiesViewModel(title, sections));
    }
    // ReadingPane: treated as MessageList — same message is already selected.
    // No-op if nothing is selected or focus is elsewhere (toolbar, status bar).
}

private void ShowPropertiesWindow(PropertiesViewModel vm)
{
    PropertiesRequested?.Invoke(vm);
}

public event Action<PropertiesViewModel>? PropertiesRequested;
```

The `MainWindow` subscribes to `PropertiesRequested` in its `Loaded` handler and shows the dialog:

```csharp
// filepath: QuickMail/Views/MainWindow.xaml.cs
_vm.PropertiesRequested += vm =>
{
    var win = new PropertiesWindow(vm) { Owner = this };
    win.ShowDialog();
};
```

Unsubscribe in `Closed` to match the pairing rule.

### 11.2 Attachment context

The attachment strip (`AttachmentStrip` control) adds its own `PreviewKeyDown`:

```csharp
// filepath: QuickMail/Controls/AttachmentStrip.xaml.cs
private void OnPreviewKeyDown(object sender, KeyEventArgs e)
{
    if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Alt) != 0
        && SelectedAttachment is { } att)
    {
        var (title, sections) = AttachmentPropertiesBuilder.Build(att);
        var vm  = new PropertiesViewModel(title, sections);
        var win = new PropertiesWindow(vm) { Owner = Window.GetWindow(this) };
        win.ShowDialog();
        e.Handled = true;
    }
}
```

### 11.3 Address book context

`AddressBookViewModel` gets a `ShowPropertiesCommand`:

```csharp
// filepath: QuickMail/ViewModels/AddressBookViewModel.cs
[RelayCommand]
private async Task ShowPropertiesAsync()
{
    if (SelectedContact is { } contact)
    {
        var groupIds   = await _contactService.ListGroupsForContactAsync(contact.Id);
        var allGroups  = await _contactService.LoadAllGroupsAsync();
        var groupNames = allGroups
            .Where(g => groupIds.Contains(g.Id))
            .Select(g => g.Name)
            .ToList();
        var (title, sections) = ContactPropertiesBuilder.Build(contact, groupNames);
        PropertiesRequested?.Invoke(new PropertiesViewModel(title, sections));
    }
    else if (SelectedGroup is { } group)
    {
        var members   = SelectedGroupMembers.ToList();
        var (title, sections) = GroupPropertiesBuilder.Build(group, members);
        PropertiesRequested?.Invoke(new PropertiesViewModel(title, sections));
    }
}

public event Action<PropertiesViewModel>? PropertiesRequested;
```

`AddressBookWindow.xaml.cs` subscribes to `PropertiesRequested` and shows the dialog, and adds
the Alt+Enter key handler in `PreviewKeyDown`.

---

## 12. Command Registry

### 12.1 New command

| Command ID | Category | Title | Default Key | Available when |
|---|---|---|---|---|
| `view.showProperties` | View | View Properties | `Alt+Enter` | Message, folder, or account selected |

Registration:

```csharp
// filepath: QuickMail/Views/MainWindow.xaml.cs (constructor, with other registrations)
_registry.Register(new CommandDefinition(
    id:               "view.showProperties",
    category:         "View",
    title:            "View Properties",
    defaultKey:       Key.Enter,
    defaultModifiers: ModifierKeys.Alt,
    execute:          async () => await _vm.ShowPropertiesAsync(),
    isAvailable:      () => _vm.SelectedMessage is not null
                          || _vm.SelectedFolder is not null
                          || _vm.SelectedAccount is not null));
```

The address book window does **not** register a duplicate command. It handles Alt+Enter in its own
`PreviewKeyDown` and calls `_vm.ShowPropertiesCommand.Execute(null)`.

### 12.2 No menu item required

Properties dialogs in Windows are conventionally accessed only via Alt+Enter or right-click context
menu. QuickMail does not yet have context menus (see the [Context Menus Plan](context-menus-plan.md)).
When context menus land, a "Properties" item should be added at the bottom of every context menu
as per the Windows convention. That work is out of scope here.

---

## 13. Accessibility (WCAG 2.2)

### 13.1 Automation properties

| Control | `AutomationProperties.Name` | Notes |
|---|---|---|
| `PropertiesWindow` | `"{Title}"` (bound to `Window.Title`) | Announced when dialog opens |
| Each `GroupBox` (section) | `"{section.Header}"` | Announced as a group/region landmark |
| Each field/value `ListView` | `"{section.Header}"` | e.g. "Headers" |
| Each `ListViewItem` | `"{Label}: {Value}"` via `PropertyItemNameConverter` | Reads as a single announcement; column headers are not re-read |
| Raw headers `TextBox` | `"Raw message headers"` | Separate landmark within the `Expander` |
| Raw headers `Expander` | `"Raw headers"` | Collapsed/expanded state announced by WPF |
| Sub-list `ListView` (Members/Attachments) | `"{section.Header}"` | Same pattern as field sections |
| "Copy all" button | `"Copy all properties"` | |
| "Close" button | `"Close"` | |

### 13.2 Why `ListView` (two-column `GridView`) over plain `ListBox`

A two-column `GridView` gives column headers that sighted users can scan, while the
`PropertyItemNameConverter` on `ListViewItem.AutomationProperties.Name` collapses both columns
into a single "Field: Value" string for screen readers. This means a screen reader does not
announce "column 1: From, column 2: Alice Smith" but instead announces "From: Alice Smith" — the
natural English reading of a property row.

A plain `ListBox` with `"Field: Value"` text would also work but loses the sighted-user column
alignment. The `GridView` approach serves both audiences.

### 13.3 Keyboard navigation

| Action | Key |
|---|---|
| Open Properties | `Alt+Enter` |
| Move between sections | `Tab` (section `GroupBox` → `ListView` within, then Tab to next section) |
| Navigate rows within a section | `Up` / `Down` arrow |
| Copy selected row | `Ctrl+C` or `Enter` |
| Copy all properties | `Alt+C` (bound to "Copy _all" via access key) or activate the button |
| Expand/collapse raw headers | `Space` or `Enter` on the `Expander` |
| Close dialog | `Escape` or `Alt+F4` or activate "Close" button |

`Tab` order: first `GroupBox`'s `ListView` → next `GroupBox`'s `ListView` → raw headers
`Expander` (if present) → sub-list `ListView`s → "Copy all" button → "Close" button → wraps.

### 13.4 Announcements

| Event | Text | Category |
|---|---|---|
| Row copied | `"Copied: {Label}: {Value}"` | `Result` |
| All copied | `"All properties copied"` | `Result` |

No `Hint` announcements are added: the dialog is self-explanatory after first use. The
`AutomationProperties.HelpText` on the main `ListView` reads
`"Press Enter or Ctrl+C to copy the selected row."` so screen readers surface it on focus
without requiring a code-level announcement.

### 13.5 Focus management

- When `PropertiesWindow` opens, focus is placed on the first `ListViewItem` of the first
  `GroupBox`. This allows the user to immediately start reading properties with arrow keys,
  without pressing Tab first.
- When the dialog closes (Escape or Close), focus returns to the control that had focus before
  the dialog opened. WPF `ShowDialog()` handles this automatically for modal dialogs.

### 13.6 Inclusive language

- Use "select" / "activate" / "press" in all user-visible strings — never "click."
- Do not name a specific screen reader product. Use "screen readers" generically.
- Property labels use sentence case, not title case: "Display name", not "Display Name."

---

## 14. Implementation Phases

### Phase A — Foundation (models + builders)

- Add `PropertyItem` and `PropertySection` records.
- Implement all six context builders.
- **No UI, no VM.** All builder logic is pure functions; testable immediately.
- **Tests:** `MessagePropertiesBuilderTests`, `FolderPropertiesBuilderTests`,
  `AccountPropertiesBuilderTests`, `ContactPropertiesBuilderTests`,
  `GroupPropertiesBuilderTests`, `AttachmentPropertiesBuilderTests`.

### Phase B — ViewModel

- Implement `PropertiesViewModel` with `CopyRowCommand` and `CopyAllCommand`.
- Implement `PropertyItemNameConverter`.
- **Tests:** `PropertiesViewModelTests` — section split (FieldSections vs SubListSections),
  `CopyAll` text format, clipboard write, announcements.

### Phase C — View

- Implement `PropertiesWindow.xaml` and code-behind.
- **Tests:** `XamlParseTests.PropertiesWindow_XamlParsesWithoutException`.
- Manual smoke: open each context and verify layout, keyboard navigation, and screen reader
  output.

### Phase D — Context dispatch (main window)

- Add `ShowPropertiesAsync()` to `MainViewModel`.
- Add `PropertiesRequested` event and subscribe in `MainWindow`.
- Register `view.showProperties` in `CommandRegistry`.
- Add `RawHeaders` fetch path to `LocalStoreService` if not already present.
- **Tests:** `MainViewModelPropertiesTests` — correct builder called per context; no-op when
  nothing is selected.

### Phase E — Address book and attachment contexts

- Add `ShowPropertiesCommand` and `PropertiesRequested` event to `AddressBookViewModel`.
- Wire in `AddressBookWindow.xaml.cs` (`PreviewKeyDown` + `PropertiesRequested` subscription).
- Add Alt+Enter handler to `AttachmentStrip.xaml.cs`.
- **Tests:** `AddressBookViewModelPropertiesTests`, `AttachmentStripPropertiesTests`.

### Phase F — Polish

- Verify `isAvailable` logic in `CommandRegistry` (no-op when nothing selected).
- Add `AutomationProperties.HelpText` to the main `ListView`.
- `build.bat smoke` passes.
- Update `USERGUIDE.md` with a "Viewing properties (Alt+Enter)" section.

---

## 15. Success Metrics

| Metric | Target | How measured |
|---|---|---|
| Alt+Enter coverage | Opens a non-empty dialog in every documented context | Phase F manual checklist |
| No crash on dead-key contexts | No crash when Alt+Enter is pressed with nothing selected | Phase D test + smoke |
| Screen reader fidelity | Field:Value rows announced as a single string (not column-by-column) | Manual smoke with a screen reader |
| Copy fidelity | Ctrl+C on a row puts exactly "Label: Value" on the clipboard | Phase B unit test |
| Test coverage | All six builders have dedicated unit tests | Phase A CI gate |

---

## 16. Open Questions & Risks

### 16.1 Open questions

1. **`RawHeaders` availability.** Does `LocalStoreService` cache the raw RFC 5322 headers, or only
   the parsed `MessageSummary` fields? If not cached, a background IMAP fetch is needed. The fetch
   can be async with a "Loading…" placeholder row.
   **Recommendation:** Check `MessageDetail` first; if absent, show a "Fetching raw headers…"
   placeholder and fetch in the background. This keeps the dialog snappy for the 95% case.

2. **Folder message counts in virtual folders.** `FolderPropertiesBuilder` receives total/unread
   counts, but virtual folders aggregate across many real folders. Should the counts for e.g.
   `\x00AllMail` reflect the full union, or show "N/A"?
   **Recommendation:** Compute union counts from `LocalStoreService` the same way `MainViewModel`
   does for message list population. Show "N/A" only for virtual folders whose definition makes
   counting ambiguous.

3. **Account "last synced" time.** `SyncService` tracks last-sync per account; `AccountPropertiesBuilder`
   needs it. Does `MainViewModel` already hold this, or does it need a new property on
   `SyncService`?
   **Recommendation:** Add `LastSyncedUtc(Guid accountId)` to `ISyncService` during Phase D.

4. **Context menus.** When the [Context Menus Plan](context-menus-plan.md) is implemented, a
   "Properties" item should appear at the bottom of every context menu. That work should reference
   this spec and reuse `ShowPropertiesAsync()`.

### 16.2 Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| Raw headers not cached, causing a visible lag. | Medium | Placeholder row + async fetch. Dialog opens immediately with cached fields. |
| `PropertyItemNameConverter` not picked up in the `ItemContainerStyle` (XAML data binding quirk). | Low | Test in `XamlParseTests`; verify in Phase C smoke. |
| Alt+Enter conflicts with a WPF internal binding (some controls intercept Alt). | Low | `PreviewKeyDown` with `e.Handled = true`; tested across all focus states in Phase D. |
| Large "To" / "Cc" fields (100+ recipients on a mailing list) overflow the value column. | Medium | `TextBlock` with `TextTrimming="CharacterEllipsis"` in the `DataTemplate`; full value accessible via keyboard focus read. Alternatively, truncate to first N addresses with "and N more…" as the value. |

---

## 17. Files to Create

| File | Purpose |
|---|---|
| `QuickMail/Models/PropertyItem.cs` | Record for a single field/value row (§7.1) |
| `QuickMail/Models/PropertySection.cs` | Record for a named group of rows (§7.1) |
| `QuickMail/Helpers/MessagePropertiesBuilder.cs` | Message context builder (§8.1) |
| `QuickMail/Helpers/FolderPropertiesBuilder.cs` | Folder context builder (§8.2) |
| `QuickMail/Helpers/AccountPropertiesBuilder.cs` | Account context builder (§8.3) |
| `QuickMail/Helpers/ContactPropertiesBuilder.cs` | Contact context builder (§8.4) |
| `QuickMail/Helpers/GroupPropertiesBuilder.cs` | Group context builder (§8.5) |
| `QuickMail/Helpers/AttachmentPropertiesBuilder.cs` | Attachment context builder (§8.6) |
| `QuickMail/ViewModels/PropertiesViewModel.cs` | ViewModel for the Properties window (§9) |
| `QuickMail/Views/PropertiesWindow.xaml` | Properties dialog XAML (§10.2) |
| `QuickMail/Views/PropertiesWindow.xaml.cs` | Code-behind: keyboard handler, focus init (§10.3) |
| `QuickMail/Converters/PropertyItemNameConverter.cs` | `IValueConverter` for screen reader name (§10.4) |
| `QuickMail.Tests/MessagePropertiesBuilderTests.cs` | Builder unit tests |
| `QuickMail.Tests/FolderPropertiesBuilderTests.cs` | Builder unit tests |
| `QuickMail.Tests/AccountPropertiesBuilderTests.cs` | Builder unit tests |
| `QuickMail.Tests/ContactPropertiesBuilderTests.cs` | Builder unit tests |
| `QuickMail.Tests/GroupPropertiesBuilderTests.cs` | Builder unit tests |
| `QuickMail.Tests/AttachmentPropertiesBuilderTests.cs` | Builder unit tests |
| `QuickMail.Tests/PropertiesViewModelTests.cs` | VM unit tests (§9) |
| `QuickMail.Tests/MainViewModelPropertiesTests.cs` | Context dispatch tests (§11.1) |
| `QuickMail.Tests/AddressBookViewModelPropertiesTests.cs` | Address book dispatch tests (§11.3) |

---

## 18. Files to Modify

| File | Change |
|---|---|
| `QuickMail/ViewModels/MainViewModel.cs` | Add `ShowPropertiesAsync()`, `PropertiesRequested` event (§11.1) |
| `QuickMail/Views/MainWindow.xaml.cs` | Subscribe to `PropertiesRequested`; register `view.showProperties` command (§11.1, §12) |
| `QuickMail/ViewModels/AddressBookViewModel.cs` | Add `ShowPropertiesCommand`, `PropertiesRequested` event (§11.3) |
| `QuickMail/Views/AddressBookWindow.xaml.cs` | Subscribe to `PropertiesRequested`; add `PreviewKeyDown` for Alt+Enter (§11.3) |
| `QuickMail/Controls/AttachmentStrip.xaml.cs` | Add `PreviewKeyDown` for Alt+Enter → `AttachmentPropertiesBuilder` (§11.2) |
| `QuickMail/Services/ISyncService.cs` | Add `LastSyncedUtc(Guid accountId)` method (§16.1 open question 3) |
| `QuickMail/Services/SyncService.cs` | Implement `LastSyncedUtc` (§16.1 open question 3) |
| `QuickMail/App.xaml.cs` | No change required — no new DI wiring needed (builders are static; VM is instantiated inline) |
| `QuickMail.Tests/StubServices.cs` | Add stub for `ISyncService.LastSyncedUtc` returning `null` |
| `QuickMail.Tests/XamlParseTests.cs` | Add `PropertiesWindow_XamlParsesWithoutException` |
| `USERGUIDE.md` | Add "Viewing properties (Alt+Enter)" section |
| `CLAUDE.md` | Add `PropertyItem`, `PropertySection`, and context builder pattern to architecture summary |

---

## 19. Tests to Add

| Test class | Test | Covers |
|---|---|---|
| `MessagePropertiesBuilderTests` | `Build_PopulatesFromAndSubject` | §8.1 |
| `MessagePropertiesBuilderTests` | `Build_FormatsDateInLocalTime` | §8.1 |
| `MessagePropertiesBuilderTests` | `Build_FormatsFileSizeAsKb` | §8.1 |
| `MessagePropertiesBuilderTests` | `Build_WithNullDetail_OmitsContentSection` | §8.1 |
| `MessagePropertiesBuilderTests` | `Build_WithAttachments_IncludesCount` | §8.1 |
| `FolderPropertiesBuilderTests` | `Build_VirtualFolder_ShowsVirtualFolderType` | §8.2 |
| `FolderPropertiesBuilderTests` | `Build_RealFolder_ShowsImapFolderType` | §8.2 |
| `FolderPropertiesBuilderTests` | `Build_ExcludedFolder_ShowsYes` | §8.2 |
| `AccountPropertiesBuilderTests` | `Build_OAuthAccount_ShowsOAuthNotPassword` | §8.3 |
| `AccountPropertiesBuilderTests` | `Build_NeverExposeCredential` | §8.3 (no password/token in any value) |
| `AccountPropertiesBuilderTests` | `Build_NullLastSynced_ShowsNotYetSynced` | §8.3 |
| `ContactPropertiesBuilderTests` | `Build_NoGroups_ShowsNotMemberOfAnyGroups` | §8.4 |
| `ContactPropertiesBuilderTests` | `Build_MultipleGroups_JoinsWithComma` | §8.4 |
| `ContactPropertiesBuilderTests` | `Build_NeverUsed_ShowsNever` | §8.4 |
| `GroupPropertiesBuilderTests` | `Build_EmptyGroup_ShowsZeroMembers` | §8.5 |
| `GroupPropertiesBuilderTests` | `Build_MissingContacts_ShowsMissingCount` | §8.5 |
| `GroupPropertiesBuilderTests` | `Build_WithMembers_CreatesSubListSection` | §8.5 |
| `AttachmentPropertiesBuilderTests` | `Build_InlineAttachment_ShowsYes` | §8.6 |
| `AttachmentPropertiesBuilderTests` | `Build_NoContentId_ShowsNone` | §8.6 |
| `PropertiesViewModelTests` | `FieldSections_ExcludesMembersSection` | §9 |
| `PropertiesViewModelTests` | `SubListSections_IncludesMembersSection` | §9 |
| `PropertiesViewModelTests` | `CopyAll_ProducesFormattedText` | §9 |
| `PropertiesViewModelTests` | `CopyRow_PutsLabelColonValueOnClipboard` | §9 |
| `PropertiesViewModelTests` | `CopyAll_IncludesRawHeadersWhenPresent` | §9 |
| `MainViewModelPropertiesTests` | `ShowProperties_MessageFocused_CallsMessageBuilder` | §11.1 |
| `MainViewModelPropertiesTests` | `ShowProperties_FolderFocused_CallsFolderBuilder` | §11.1 |
| `MainViewModelPropertiesTests` | `ShowProperties_AccountFocused_CallsAccountBuilder` | §11.1 |
| `MainViewModelPropertiesTests` | `ShowProperties_NothingSelected_DoesNotRaiseEvent` | §11.1 (no-op check) |
| `AddressBookViewModelPropertiesTests` | `ShowProperties_ContactSelected_CallsContactBuilder` | §11.3 |
| `AddressBookViewModelPropertiesTests` | `ShowProperties_GroupSelected_CallsGroupBuilder` | §11.3 |
| `XamlParseTests` | `PropertiesWindow_XamlParsesWithoutException` | §10.2 |

Total: **~30 new tests.**

---

## Appendix A — Property Layouts by Context

### A.1 Message Properties

```
Message Properties
══════════════════

  Headers
  ───────
  From          Alice Smith <alice@example.com>
  To            Bob Jones <bob@example.com>
  Cc            (none)
  Reply-To      (none)
  Subject       Re: Q3 Planning
  Date          Monday, June 2, 2026 at 10:34 AM
  Message-ID    <abc123@mail.example.com>

  Storage
  ───────
  Account       Work (alice@work.com)
  Folder        INBOX
  IMAP UID      12345
  Size          42 KB
  Flags         Unread, Flagged

  Content
  ───────
  Format        HTML with plain-text alternative
  Attachments   2 attachments

  ▼ Raw headers    [collapsed Expander — activates to show full RFC 5322 source]

  Attachments  [sub-list]
  ───────────
  report.pdf                application/pdf    245 KB
  logo.png                  image/png           34 KB

  [ Copy all ]                              [ Close ]
```

### A.2 Folder Properties

```
Folder Properties
═════════════════

  Folder
  ──────
  Name               INBOX
  Full path          INBOX
  Account            Work (alice@work.com)
  Type               IMAP folder
  Special use        Inbox
  Total messages     1,247
  Unread messages    12
  Excluded from All Mail  No

  [ Copy all ]                              [ Close ]
```

### A.3 Account Properties

```
Account Properties
══════════════════

  Identity
  ────────
  Display name      Work
  Email address     alice@work.com

  Incoming (IMAP)
  ───────────────
  Server            imap.work.com
  Port              993
  Security          SSL/TLS
  Username          alice@work.com

  Outgoing (SMTP)
  ───────────────
  Server            smtp.work.com
  Port              587
  Security          STARTTLS
  Username          alice@work.com

  Authentication
  ──────────────
  Authentication    Password (Windows Credential Manager)
  Last synced       Monday, June 2, 2026 at 10:30 AM

  [ Copy all ]                              [ Close ]
```

### A.4 Contact Properties

```
Contact Properties
══════════════════

  Contact
  ───────
  Display name      Alice Smith
  Email address     alice@example.com
  Last used         Sunday, June 1, 2026
  Groups            Project Team, Family

  [ Copy all ]                              [ Close ]
```

### A.5 Group Properties

```
Group Properties
════════════════

  Group
  ─────
  Group name        Project Team
  Members           5 of 5
  Missing contacts  0
  Last used         Sunday, June 1, 2026

  Members  [sub-list]
  ───────
  Alice Smith       alice@example.com
  Bob Jones         bob@example.com
  Carol Lee         carol@example.com
  Dan Marsh         dan@example.com
  Eve Torres        eve@example.com

  [ Copy all ]                              [ Close ]
```

### A.6 Attachment Properties

```
Attachment Properties
═════════════════════

  File
  ────
  File name         Q3_Report_Final.pdf
  MIME type         application/pdf
  Size              245 KB
  Transfer encoding base64
  Content-ID        (none)
  Inline            No

  [ Copy all ]                              [ Close ]
```

---

## Appendix B — Sample User Flows

### B.1 Check message headers with a screen reader

1. Message list has focus. Use arrow keys to select a message.
2. Press `Alt+Enter`. "Message Properties" dialog opens. Focus is on the first row of the
   "Headers" section.
3. Screen reader announces: "Headers group box. From: Alice Smith <alice@example.com>."
4. Press `Down`. Announces: "To: Bob Jones <bob@example.com>."
5. Continue down through Subject, Date, Message-ID.
6. Press `Tab` to move to the "Storage" section. Screen reader announces: "Storage group box.
   Account: Work (alice@work.com)."
7. Press `Escape` to close. Focus returns to the message list.

### B.2 Copy a Message-ID for a support ticket

1. Press `Alt+Enter` on the selected message.
2. Press `Tab` until the "Headers" ListView is focused; use arrow keys to reach the
   "Message-ID" row.
3. Press `Ctrl+C`. Clipboard contains "Message-ID: <abc123@mail.example.com>".
4. Announcement: "Copied: Message-ID: <abc123@mail.example.com>."
5. Press `Escape`.

### B.3 Check folder message count

1. Focus the folder tree (Ctrl+2 or Ctrl+Y).
2. Arrow to the Trash folder.
3. Press `Alt+Enter`. "Folder Properties" opens.
4. Navigate to "Total messages" row: "Total messages: 4,312."
5. Press `Escape`.

### B.4 Review an account's server settings

1. Focus the account list (Ctrl+1).
2. Arrow to the Work account.
3. Press `Alt+Enter`. "Account Properties" opens at the "Identity" section.
4. Tab to "Incoming (IMAP)" group box. Arrow to "Port": "Port: 993."
5. Tab to "Outgoing (SMTP)" group box. Arrow to "Port": "Port: 587."
6. "Authentication" section: "Authentication: Password (Windows Credential Manager)."
   (No password is shown.)
7. Press `Escape`.

### B.5 See which groups a contact belongs to

1. Open Address Book (`Ctrl+Shift+B`). Focus is in the Contacts tab.
2. Search for "Alice". Arrow to select her.
3. Press `Alt+Enter`. "Contact Properties" opens.
4. Navigate to "Groups" row: "Groups: Project Team, Family."
5. Press `Escape`. Address Book remains open.

---

*This spec is ready for Dev Lead implementation. The PM portion (§1–6) and the Dev portion (§7–13) are self-contained; review can be done in either order. The context builders in §8 are testable immediately without any UI work.*

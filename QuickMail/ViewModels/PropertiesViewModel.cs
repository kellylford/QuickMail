using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;

namespace QuickMail.ViewModels;

public partial class PropertiesViewModel : ObservableObject
{
    public string Title { get; }

    /// <summary>Non-null only for message context when raw headers are available.</summary>
    public string? RawHeaders { get; }

    /// <summary>Sections rendered as a standard field/value list. "Members" and
    /// "Attachments" sections are routed to <see cref="SubListSections"/> instead.</summary>
    public IReadOnlyList<PropertySection> FieldSections { get; }

    /// <summary>Sections rendered as a secondary list (Members, Attachments).</summary>
    public IReadOnlyList<PropertySection> SubListSections { get; }

    /// <summary>Raised when the VM wants to announce text to the screen reader.
    /// The View subscribes and calls AccessibilityHelper.Announce.</summary>
    public event Action<string, AnnouncementCategory>? AnnouncementRequested;

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
        AnnouncementRequested?.Invoke(
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
        AnnouncementRequested?.Invoke("All properties copied", AnnouncementCategory.Result);
    }
}

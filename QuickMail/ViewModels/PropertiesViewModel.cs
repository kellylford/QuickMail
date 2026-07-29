using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

public partial class PropertiesViewModel : ObservableObject
{
    public string Title { get; }

    /// <summary>Non-null only for message context when raw headers are available.</summary>
    public string? RawHeaders { get; }

    /// <summary>
    /// All rows in display order. Section headers (IsHeader = true) are interleaved
    /// with data rows so the entire list is navigable as a single focus sequence.
    /// </summary>
    public IReadOnlyList<FlatRow> Rows { get; }

    private readonly IReadOnlyList<PropertySection> _sections;
    private readonly IClipboardService _clipboard;

    /// <summary>Raised when the VM wants to announce text to the screen reader.
    /// The View subscribes and calls AccessibilityHelper.Announce.</summary>
    public event Action<string, AnnouncementCategory>? AnnouncementRequested;

    /// <param name="clipboard">
    /// Injected so tests need no real clipboard. It defaults to the Windows one, so the many
    /// existing call sites are unaffected — but a VM must not reach into System.Windows itself
    /// (CLAUDE.md MVVM rules), and a test that used the machine-wide clipboard failed whenever any
    /// other process held it.
    /// </param>
    public PropertiesViewModel(
        string title,
        IReadOnlyList<PropertySection> sections,
        string? rawHeaders = null,
        IClipboardService? clipboard = null)
    {
        Title      = title;
        RawHeaders = rawHeaders;
        _sections  = sections;
        _clipboard = clipboard ?? ClipboardService.Default;

        Rows = sections
            .Where(s => s.Items.Count > 0)
            .SelectMany(s => Enumerable.Concat(
                [new FlatRow(s.Header, s.Header, string.Empty, IsHeader: true)],
                s.Items.Select(item => new FlatRow(s.Header, item.Label, item.Value))))
            .ToList();
    }

    [RelayCommand]
    private void CopyRow(FlatRow? row)
    {
        if (row is null) return;
        var text = row.IsHeader ? row.Label : $"{row.Label}: {row.Value}";
        _clipboard.SetText(text);
        AnnouncementRequested?.Invoke($"Copied: {text}", AnnouncementCategory.Result);
    }

    [RelayCommand]
    private void CopyAll()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Title);
        sb.AppendLine(new string('─', Title.Length));
        foreach (var section in _sections)
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
        _clipboard.SetText(sb.ToString());
        AnnouncementRequested?.Invoke("All properties copied", AnnouncementCategory.Result);
    }
}

/// <summary>
/// A single row in the properties ListView. Section header rows (IsHeader = true) act as
/// in-list separators and are focusable but not copyable.
/// </summary>
public record FlatRow(string SectionName, string Label, string Value, bool IsHeader = false);

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using QuickMail.Helpers;
using QuickMail.Models;

namespace QuickMail.Views;

/// <summary>
/// Sizes the calendar agenda's columns to what they hold (issue #547). Fixed widths cut the When column
/// mid-time ("11:00 AM–12:00", a whole and wrong time) and Status at 150% text, while GridView's empty
/// filler header sat at the right with the room they needed. When, Status and Calendar now get their
/// widest value's width at the current font, Subject takes the rest, and the cells trim with an
/// ellipsis (the ListView's TextBlock style) when the window is too narrow even for that.
/// Purely visual: the rows' accessible names are untouched.
/// </summary>
public partial class MainWindow
{
    // GridViewRowPresenter puts 6px either side of every cell, and the themed header pads 6px each side.
    private const double CalendarCellPadding = 12;
    // Rounding slack, so a value measured to the exact width is not trimmed by a sub-pixel.
    private const double CalendarCellSlack = 4;
    // Subject keeps at least this many ems before the other columns start giving up width.
    private const double CalendarSubjectMinEms = 10;

    private static readonly Func<CalendarEvent, string>[] CalendarColumnText =
    [
        e => e.Summary,
        e => e.WhenText,
        e => e.StatusText,
        e => e.CalendarSourceLabel,
    ];

    private static readonly DependencyPropertyDescriptor CalendarListFontSize =
        DependencyPropertyDescriptor.FromProperty(Control.FontSizeProperty, typeof(ListView));
    private static readonly DependencyPropertyDescriptor CalendarListFontFamily =
        DependencyPropertyDescriptor.FromProperty(Control.FontFamilyProperty, typeof(ListView));

    private bool _calendarColumnFitPending;

    private void WireCalendarColumnFit()
    {
        ((INotifyCollectionChanged)CalendarList.Items).CollectionChanged += CalendarList_ItemsChanged;
        CalendarList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(CalendarList_ScrollChanged));
        CalendarList.IsVisibleChanged += CalendarList_IsVisibleChanged;
        CalendarListFontSize.AddValueChanged(CalendarList, CalendarList_FontChanged);
        CalendarListFontFamily.AddValueChanged(CalendarList, CalendarList_FontChanged);
    }

    private void UnwireCalendarColumnFit()
    {
        ((INotifyCollectionChanged)CalendarList.Items).CollectionChanged -= CalendarList_ItemsChanged;
        CalendarList.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(CalendarList_ScrollChanged));
        CalendarList.IsVisibleChanged -= CalendarList_IsVisibleChanged;
        CalendarListFontSize.RemoveValueChanged(CalendarList, CalendarList_FontChanged);
        CalendarListFontFamily.RemoveValueChanged(CalendarList, CalendarList_FontChanged);
    }

    private void CalendarList_ItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleCalendarColumnFit();
    private void CalendarList_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => ScheduleCalendarColumnFit();
    private void CalendarList_FontChanged(object? sender, EventArgs e) => ScheduleCalendarColumnFit();

    // The viewport, not the ListView's own size: it also narrows when the vertical scrollbar appears.
    private void CalendarList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportWidthChange != 0) ScheduleCalendarColumnFit();
    }

    /// <summary>Coalesces a burst of changes (a reload adds rows one by one) into one fit after layout.</summary>
    private void ScheduleCalendarColumnFit()
    {
        if (_calendarColumnFitPending) return;
        _calendarColumnFitPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _calendarColumnFitPending = false;
            FitCalendarColumns();
        }));
    }

    private void FitCalendarColumns()
    {
        if (!CalendarList.IsVisible || CalendarList.View is not GridView grid
            || grid.Columns.Count != CalendarColumnText.Length)
            return;

        double available = FindCalendarScrollViewer()?.ViewportWidth ?? CalendarList.ActualWidth;
        // The rows are inset by the container's padding; the header row is not. Fit the rows.
        if (TryFindResource("Theme.ListRowPadding") is Thickness rowPadding)
            available -= rowPadding.Left + rowPadding.Right;
        if (double.IsNaN(available) || available <= 0) return;

        var typeface = new Typeface(CalendarList.FontFamily, CalendarList.FontStyle,
                                    CalendarList.FontWeight, CalendarList.FontStretch);
        double pixelsPerDip = VisualTreeHelper.GetDpi(CalendarList).PixelsPerDip;
        double Measure(string text) => new FormattedText(
            text, CultureInfo.CurrentCulture, CalendarList.FlowDirection, typeface,
            CalendarList.FontSize, Brushes.Black, pixelsPerDip).WidthIncludingTrailingWhitespace;

        const int fillIndex = 0; // Subject
        var needed = new double[CalendarColumnText.Length];
        for (int i = 0; i < needed.Length; i++)
        {
            if (i == fillIndex) continue;
            double widest = Measure(grid.Columns[i].Header as string ?? string.Empty);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in CalendarList.Items)
            {
                if (item is not CalendarEvent evt) continue;
                var text = CalendarColumnText[i](evt) ?? string.Empty;
                if (seen.Add(text)) widest = Math.Max(widest, Measure(text));
            }
            needed[i] = Math.Ceiling(widest) + CalendarCellPadding + CalendarCellSlack;
        }

        var widths = GridViewColumnFit.Fit(needed, fillIndex, Math.Floor(available),
                                           CalendarSubjectMinEms * CalendarList.FontSize);
        for (int i = 0; i < widths.Length; i++)
        {
            if (Math.Abs(grid.Columns[i].Width - widths[i]) > 0.5)
                grid.Columns[i].Width = widths[i];
        }
    }

    private ScrollViewer? FindCalendarScrollViewer()
    {
        // Breadth-first, so the list's own ScrollViewer is found before any inside a row.
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(CalendarList);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node is ScrollViewer sv) return sv;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                queue.Enqueue(VisualTreeHelper.GetChild(node, i));
        }
        return null;
    }
}

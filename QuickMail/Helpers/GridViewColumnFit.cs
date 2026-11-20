using System;
using System.Collections.Generic;

namespace QuickMail.Helpers;

/// <summary>
/// Shares a GridView's width out among its columns: every column but one gets the width its widest
/// value needs, and the one "fill" column takes whatever is left (issue #547).
///
/// <para>
/// A GridView column has a fixed pixel width unless told otherwise, and the calendar's were sized for
/// 100% text: the When column cut "11:00 AM–12:00 PM" to "11:00 AM–12:00", which reads as a whole and
/// wrong time, while an empty filler header sat beside the last column with the width it needed. Sizing
/// to content gives When and Status their room at any text scale, and handing the remainder to the
/// fill column leaves no empty filler at the right. A pure function, so the arithmetic is testable
/// without a shown window; measuring the text is the window's job.
/// </para>
/// </summary>
public static class GridViewColumnFit
{
    /// <summary>
    /// Widths for each column. <paramref name="needed"/> is each column's content width (the entry at
    /// <paramref name="fillIndex"/> is ignored). The fill column gets the room left over, but never less
    /// than <paramref name="fillMinWidth"/> while the space exists; when there is not room for every
    /// column at its needed width plus that minimum, the other columns give up width in proportion to
    /// their size. The result always sums to <paramref name="available"/> when that is positive.
    /// </summary>
    public static double[] Fit(IReadOnlyList<double> needed, int fillIndex, double available, double fillMinWidth)
    {
        ArgumentNullException.ThrowIfNull(needed);
        if (fillIndex < 0 || fillIndex >= needed.Count)
            throw new ArgumentOutOfRangeException(nameof(fillIndex));

        var widths = new double[needed.Count];
        if (available <= 0) return widths;

        double others = 0;
        for (int i = 0; i < needed.Count; i++)
        {
            if (i == fillIndex) continue;
            widths[i] = Math.Max(0, needed[i]);
            others += widths[i];
        }

        double fillMin = Math.Clamp(fillMinWidth, 0, available);
        double room = available - others;
        if (room >= fillMin)
        {
            widths[fillIndex] = room;
            return widths;
        }

        // Not enough for everything: keep the fill column at its minimum and shrink the rest evenly.
        widths[fillIndex] = fillMin;
        double share = others > 0 ? (available - fillMin) / others : 0;
        for (int i = 0; i < widths.Length; i++)
            if (i != fillIndex) widths[i] *= share;
        return widths;
    }
}

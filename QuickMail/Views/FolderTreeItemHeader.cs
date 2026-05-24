using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Documents;
using System.Windows.Media;

namespace QuickMail.Views;

/// <summary>
/// Renders a folder label and optional unread badge purely via DrawingContext —
/// no UIElement children are created. This prevents screen readers from traversing
/// into the element's visual content; the containing TreeViewItem's
/// AutomationProperties.Name is the sole accessible representation.
/// </summary>
public sealed class FolderTreeItemHeader : FrameworkElement
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(FolderTreeItemHeader),
            new FrameworkPropertyMetadata(string.Empty,
                FrameworkPropertyMetadataOptions.AffectsRender |
                FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty UnreadDisplayProperty =
        DependencyProperty.Register(nameof(UnreadDisplay), typeof(string), typeof(FolderTreeItemHeader),
            new FrameworkPropertyMetadata(string.Empty,
                FrameworkPropertyMetadataOptions.AffectsRender |
                FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty IsHeaderItemProperty =
        DependencyProperty.Register(nameof(IsHeaderItem), typeof(bool), typeof(FolderTreeItemHeader),
            new FrameworkPropertyMetadata(false,
                FrameworkPropertyMetadataOptions.AffectsRender |
                FrameworkPropertyMetadataOptions.AffectsMeasure));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string UnreadDisplay
    {
        get => (string)GetValue(UnreadDisplayProperty);
        set => SetValue(UnreadDisplayProperty, value);
    }

    public bool IsHeaderItem
    {
        get => (bool)GetValue(IsHeaderItemProperty);
        set => SetValue(IsHeaderItemProperty, value);
    }

    // No automation peer → element is invisible to all accessibility APIs.
    protected override AutomationPeer? OnCreateAutomationPeer() => null;

    protected override Size MeasureOverride(Size constraint)
    {
        double dpi = GetPixelsPerDip();
        double fontSize = TextElement.GetFontSize(this);
        var foreground = TextElement.GetForeground(this);

        var labelFt = MakeText(Label, IsHeaderItem ? FontWeights.SemiBold : FontWeights.Normal,
            fontSize, foreground, dpi, double.PositiveInfinity);

        double w = labelFt.Width;
        double h = labelFt.Height;

        if (!string.IsNullOrEmpty(UnreadDisplay))
        {
            var badgeFt = MakeText(UnreadDisplay, FontWeights.Normal,
                Math.Max(8, fontSize - 1), Brushes.DimGray, dpi, double.PositiveInfinity);
            w += 4 + badgeFt.Width;
            h = Math.Max(h, badgeFt.Height);
        }

        return new Size(Math.Min(w, constraint.Width), h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double dpi = GetPixelsPerDip();
        double fontSize = TextElement.GetFontSize(this);
        var foreground = TextElement.GetForeground(this);

        // Reserve space on the right for the badge, then give the rest to the label.
        double badgeWidth = 0;
        FormattedText? badgeFt = null;

        if (!string.IsNullOrEmpty(UnreadDisplay))
        {
            badgeFt = MakeText(UnreadDisplay, FontWeights.Normal,
                Math.Max(8, fontSize - 1), Brushes.DimGray, dpi, double.PositiveInfinity);
            badgeWidth = 4 + badgeFt.Width;
        }

        double labelMaxWidth = Math.Max(1, ActualWidth - badgeWidth);
        var labelFt = MakeText(Label, IsHeaderItem ? FontWeights.SemiBold : FontWeights.Normal,
            fontSize, foreground, dpi, labelMaxWidth);
        labelFt.Trimming = TextTrimming.CharacterEllipsis;

        // Vertically centre each piece on the element height.
        double elementH = ActualHeight;
        dc.DrawText(labelFt, new Point(0, (elementH - labelFt.Height) / 2));

        if (badgeFt != null)
        {
            double badgeX = ActualWidth - badgeFt.Width;
            dc.DrawText(badgeFt, new Point(badgeX, (elementH - badgeFt.Height) / 2));
        }
    }

    private FormattedText MakeText(string text, FontWeight weight, double fontSize,
        Brush brush, double pixelsPerDip, double maxWidth)
    {
        var typeface = new Typeface(
            TextElement.GetFontFamily(this),
            TextElement.GetFontStyle(this),
            weight,
            TextElement.GetFontStretch(this));

        var ft = new FormattedText(
            text ?? string.Empty,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            brush,
            pixelsPerDip);

        if (!double.IsPositiveInfinity(maxWidth))
            ft.MaxTextWidth = maxWidth;

        return ft;
    }

    private double GetPixelsPerDip()
    {
        try { return VisualTreeHelper.GetDpi(this).PixelsPerDip; }
        catch { return 1.0; }
    }
}

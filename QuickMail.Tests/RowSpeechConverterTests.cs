using System.Globalization;
using System.Windows;
using QuickMail.Models;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The WPF adapter that turns a row's bound values into the spoken accessible name. The composition
/// rules live in <see cref="QuickMail.Helpers.RowSpeechBuilder"/> and are tested in
/// <see cref="RowSpeechBuilderTests"/>; this covers the adapter's own contract — the layout arrives
/// in the last slot, everything before it is positional field data, and partial or unset bindings
/// must not produce a half-spoken row.
///
/// <para>#423 (source folder in aggregate views) is pinned here because that behaviour used to live
/// in the converter itself; it is now the "folder" field, enabled last by default.</para>
/// </summary>
public class RowSpeechConverterTests
{
    private static readonly RowSpeechConverter MessageConverter = new(RowKind.Message);

    private static string Convert(string folder, RowSpeechSettings? settings = null) =>
        (string)MessageConverter.Convert(
            [
                "Urgent",              // flag
                "unread",              // status
                false,                 // attachments
                "Goodreads",           // from
                "Updates from Angela", // subject
                "Sneak peek",          // preview
                "12:24 PM",            // date
                true,                  // isUnread
                false,                 // replied
                false,                 // forwarded
                "",                    // to
                folder,                // folder (#423)
                false,                 // mailingList
                false,                 // watched
                settings ?? RowSpeechSettings.Default,
            ],
            typeof(string), parameter: null!, CultureInfo.InvariantCulture);

    [Fact]
    public void AggregateView_AppendsTheSourceFolder()
    {
        var name = Convert("Inbox");
        Assert.EndsWith("12:24 PM. Inbox.", name, System.StringComparison.Ordinal);
        Assert.Contains("Updates from Angela", name, System.StringComparison.Ordinal);
    }

    [Fact]
    public void SingleFolderView_EmptyFolder_OmitsIt()
    {
        var name = Convert(string.Empty);
        Assert.EndsWith("12:24 PM.", name, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Inbox", name, System.StringComparison.Ordinal);
    }

    [Fact]
    public void MissingTrailingValues_DoNotThrow()
    {
        // Bindings still being wired up: say what is known, never crash the row.
        var name = (string)MessageConverter.Convert(
            ["", "read", false, "A", "S", "P", "1:00 PM", RowSpeechSettings.Default],
            typeof(string), parameter: null!, CultureInfo.InvariantCulture);

        Assert.EndsWith("1:00 PM.", name, System.StringComparison.Ordinal);
    }

    [Fact]
    public void NoValuesAtAll_YieldsEmpty()
    {
        Assert.Equal(string.Empty, MessageConverter.Convert(
            [], typeof(string), parameter: null!, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void UnsetBindingValues_AreTreatedAsAbsentNotStringified()
    {
        var name = (string)MessageConverter.Convert(
            [
                DependencyProperty.UnsetValue, "unread", DependencyProperty.UnsetValue,
                "Goodreads", DependencyProperty.UnsetValue, DependencyProperty.UnsetValue,
                "12:24 PM", true, false, false, "", "", false,
                RowSpeechSettings.Default,
            ],
            typeof(string), parameter: null!, CultureInfo.InvariantCulture);

        Assert.Equal("unread. Goodreads. 12:24 PM.", name);
        Assert.DoesNotContain("UnsetValue", name, System.StringComparison.Ordinal);
    }

    [Fact]
    public void MissingSettingsInTheLastSlot_FallsBackToDefaultsRatherThanSilence()
    {
        // A row must never go mute because the layout binding failed to resolve.
        var name = (string)MessageConverter.Convert(
            ["", "unread", false, "Goodreads", "S", "P", "12:24 PM", true, false, false, "", "", false, null!],
            typeof(string), parameter: null!, CultureInfo.InvariantCulture);

        Assert.Contains("Goodreads", name, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ShowLabels_ReachesTheSpokenString()
    {
        var labelled = Convert(string.Empty,
            new RowSpeechSettings(RowFieldCatalog.DefaultLayouts(), ShowLabels: true));

        Assert.Contains("From: Goodreads", labelled, System.StringComparison.Ordinal);
    }

    [Fact]
    public void EachKindReadsItsOwnLayout()
    {
        // The SenderGroup converter must not read the Message layout: same values, different catalog.
        var converter = new RowSpeechConverter(RowKind.SenderGroup);
        var name = (string)converter.Convert(
            ["Chris Lee", 2, null, true, "Preview", "9:00 AM", "Newest", RowSpeechSettings.Default],
            typeof(string), parameter: null!, CultureInfo.InvariantCulture);

        Assert.Equal("Chris Lee. 2 messages. Has unread. Preview. 9:00 AM.", name);
    }
}

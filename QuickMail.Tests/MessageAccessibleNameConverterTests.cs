using System.Globalization;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The message-row accessible name a screen reader speaks. #423: aggregate/virtual views append the
/// source folder so the user can tell where a message lives; single-folder views omit it.
/// </summary>
public class MessageAccessibleNameConverterTests
{
    private static string Convert(string folder) => (string)new MessageAccessibleNameConverter().Convert(
        new object[]
        {
            "Urgent",                 // 0 flagLabel
            "unread",                 // 1 readStatusLabel
            "Goodreads",              // 2 from
            "Updates from Angela",    // 3 subject
            "Sneak peek",             // 4 preview
            "12:24 PM",               // 5 dateDisplay
            false,                    // 6 announceFlag
            false,                    // 7 hasAttachments
            folder,                   // 8 folderDisplayName (#423)
        },
        typeof(string), parameter: null!, CultureInfo.InvariantCulture);

    [Fact]
    public void AggregateView_AppendsTheSourceFolder()
    {
        var name = Convert("Inbox");
        Assert.EndsWith("12:24 PM. Inbox.", name);
        Assert.Contains("Updates from Angela", name);
    }

    [Fact]
    public void SingleFolderView_EmptyFolder_OmitsIt()
    {
        var name = Convert(string.Empty);
        Assert.EndsWith("12:24 PM.", name);
        Assert.DoesNotContain("Inbox", name);
    }

    [Fact]
    public void MissingFolderValue_DoesNotThrow()
    {
        // Older bindings without the 9th value must still work (defensive length check).
        var name = (string)new MessageAccessibleNameConverter().Convert(
            new object[] { "", "read", "A", "S", "P", "1:00 PM", false, false },
            typeof(string), parameter: null!, CultureInfo.InvariantCulture);
        Assert.EndsWith("1:00 PM.", name);
    }
}

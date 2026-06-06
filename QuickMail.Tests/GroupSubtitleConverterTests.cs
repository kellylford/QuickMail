using System.Globalization;
using System.Windows;
using QuickMail.Models;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

public class GroupSubtitleConverterTests
{
    [Fact]
    public void Convert_StripsNamePrefix_FromDisplayString()
    {
        var g = new GroupModel
        {
            Id = 1,
            Name = "Friends",
            MemberContactIds = [1, 2, 3],
            ResolvedMemberCount = 3, // normally set by ContactService on load
        };
        // Display is "Friends, 3 members"
        var result = GroupSubtitleConverter.Instance.Convert(
            g, typeof(object), null, CultureInfo.InvariantCulture);
        Assert.Equal(", 3 members", result);
    }

    [Fact]
    public void Convert_StripsNamePrefix_WithMissingContacts()
    {
        var g = new GroupModel
        {
            Id = 2,
            Name = "Mixed",
            MemberContactIds = [1, 2, 999],
            ResolvedMemberCount = 2,
        };
        // Display is "Mixed, 2 members (1 missing)"
        var result = GroupSubtitleConverter.Instance.Convert(
            g, typeof(object), null, CultureInfo.InvariantCulture);
        Assert.Equal(", 2 members (1 missing)", result);
    }

    [Fact]
    public void Convert_HandlesEmptyGroup()
    {
        var g = new GroupModel { Id = 2, Name = "Empty" };
        var result = GroupSubtitleConverter.Instance.Convert(
            g, typeof(object), null, CultureInfo.InvariantCulture);
        Assert.Equal("group", result);
    }

    [Fact]
    public void Convert_ReturnsEmpty_ForNonGroup()
    {
        var result = GroupSubtitleConverter.Instance.Convert(
            "not a group", typeof(object), null, CultureInfo.InvariantCulture);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void ConvertBack_Throws()
    {
        Assert.Throws<System.NotSupportedException>(() =>
            GroupSubtitleConverter.Instance.ConvertBack(
                "anything", typeof(object), null, CultureInfo.InvariantCulture));
    }
}

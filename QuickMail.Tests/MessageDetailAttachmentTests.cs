// Regression tests for issue #439: "No attachments" in Window mode.
//
// MessageWindow binds its attachment list's Visibility to MessageDetail.HasAttachments, but no
// loader ever set that flag on a detail — IMAP, Graph and the local store all populate the
// Attachments list only. MainViewModel patched the *summary* after loading (summary.HasAttachments
// = detail.Attachments.Count > 0), which is why the reading pane looked right while the standalone
// window kept the list collapsed: Alt+A announced "No attachments." and Shift+Tab from the body
// could never reach it.
//
// The fix makes MailMessageDetail keep the flag in sync with the list, so every producer is
// covered — including any added later.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

public class MessageDetailAttachmentTests
{
    private static AttachmentModel Att(string name = "doc.pdf") =>
        new() { FileName = name, ContentType = "application/pdf", FileSize = 1234, PartSpecifier = "2" };

    [Fact]
    public void AssigningAttachments_SetsHasAttachments()
    {
        var detail = new MailMessageDetail { Attachments = [Att()] };

        Assert.True(detail.HasAttachments);
    }

    [Fact]
    public void AssigningEmptyList_ClearsHasAttachments()
    {
        var detail = new MailMessageDetail { Attachments = [Att()] };

        detail.Attachments = [];

        Assert.False(detail.HasAttachments);
    }

    [Fact]
    public void DetailWithNoAttachments_ReportsNone()
    {
        Assert.False(new MailMessageDetail().HasAttachments);
    }

    [Fact]
    public async Task LoadDetailAsync_ReturnsDetailReportingItsAttachments()
    {
        // The exact #439 path: window mode loads the detail cache-first, then binds
        // MessageDetail.HasAttachments.
        var tempDir = Path.Combine(Path.GetTempPath(), $"QuickMailTests-{Guid.NewGuid():N}");
        var store   = new LocalStoreService(new ProfileContext(tempDir));
        store.Initialize();

        var accountId = Guid.NewGuid();
        await store.UpsertSummariesAsync([new MailMessageSummary
        {
            MessageId  = "11",
            AccountId  = accountId,
            FolderName = "Inbox",
            From       = "x@example.com",
            Subject    = "with attachment",
            Date       = DateTimeOffset.UtcNow,
        }]);
        await store.UpsertDetailAsync(new MailMessageDetail
        {
            MessageId   = "11",
            AccountId   = accountId,
            FolderName  = "Inbox",
            Attachments = new List<AttachmentModel> { Att() },
        });

        var loaded = await store.LoadDetailAsync(accountId, "Inbox", "11");

        Assert.NotNull(loaded);
        Assert.Single(loaded!.Attachments);
        Assert.True(loaded.HasAttachments);

        try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void MessageWindow_AttachmentListVisibility_BindsToAPropertyThatIsTrueWhenThereAreAttachments()
    {
        // Reads the real XAML: whatever property MessageWindow uses to show or hide the
        // attachment list must actually be true for a message that has attachments. Binding it to
        // a flag nobody set (with FallbackValue=Collapsed hiding the mistake) is what made the
        // list unreachable in Window mode.
        var xaml = XDocument.Load(Path.Combine(RepoRoot(), "QuickMail", "Views", "MessageWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var list = xaml.Descendants()
            .FirstOrDefault(e => (string?)e.Attribute(x + "Name") == "AttachmentList");
        Assert.NotNull(list);

        var visibility = (string?)list!.Attribute("Visibility");
        Assert.False(string.IsNullOrWhiteSpace(visibility),
            "AttachmentList has no Visibility binding — did the element move or get renamed?");

        var path = Regex.Match(visibility!, @"\{Binding\s+(?:Path=)?MessageDetail\.(?<prop>[A-Za-z0-9_.]+)").Groups["prop"].Value;
        Assert.False(string.IsNullOrEmpty(path),
            $"Could not read a MessageDetail binding path out of Visibility=\"{visibility}\".");

        var detail = new MailMessageDetail { Attachments = [Att()] };
        object? value = detail;
        foreach (var segment in path.Split('.'))
        {
            var prop = value?.GetType().GetProperty(segment);
            Assert.True(prop != null, $"MailMessageDetail has no '{segment}' in binding path '{path}'.");
            value = prop!.GetValue(value);
        }

        // Either a bool flag (HasAttachments) or a count (Attachments.Count) is fine — it just
        // has to say "yes, there are attachments".
        var showsTheList = value switch
        {
            bool b => b,
            int n  => n > 0,
            _      => throw new Xunit.Sdk.XunitException(
                          $"Binding path '{path}' produced {value?.GetType().Name ?? "null"}; expected a bool or a count."),
        };
        Assert.True(showsTheList,
            $"MessageDetail.{path} is falsy for a message with attachments — the attachment list " +
             "would stay collapsed in Window mode (issue #439).");
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "QuickMail", "Views")))
                return dir.FullName;
        }
        throw new InvalidOperationException($"Repo source tree not found from {AppContext.BaseDirectory}.");
    }
}

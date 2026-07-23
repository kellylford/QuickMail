using System;
using System.IO;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

// Startup folder (issue #328): the folder is persisted as an INI-safe token, since config.ini cannot
// hold the NUL-prefixed virtual-folder sentinels.
public class StartupFolderTests
{
    [Fact]
    public void EncodeToken_VirtualFolder_StripsNulSentinel()
    {
        // AllMailFolder.FullName is "\0AllMail"; the token must be INI-safe ("#AllMail").
        var token = MainViewModel.EncodeStartupFolderToken(MainViewModel.AllMailFolder);
        Assert.Equal("#AllMail", token);
        Assert.DoesNotContain('\0', token);
    }

    [Fact]
    public void EncodeToken_RealFolder_CarriesAccountAndFullName()
    {
        var accountId = Guid.NewGuid();
        var folder = new MailFolderModel { AccountId = accountId, FullName = "INBOX/Projects" };

        var token = MainViewModel.EncodeStartupFolderToken(folder);

        Assert.Equal($"@{accountId:N}|INBOX/Projects", token);
        Assert.DoesNotContain('\0', token);
    }

    [Fact]
    public void ConfigService_RoundTripsStartupFolder()
    {
        var profile = new ProfileContext(Path.Combine(Path.GetTempPath(), $"QM-SF-{Guid.NewGuid():N}"));
        var service = new ConfigService(profile);

        var config = service.Load();
        config.StartupFolder = "#AllInboxes";
        service.Save(config);

        var reloaded = new ConfigService(profile).Load();
        Assert.Equal("#AllInboxes", reloaded.StartupFolder);
    }

    [Fact]
    public void ConfigService_DefaultStartupFolder_IsEmpty()
    {
        var profile = new ProfileContext(Path.Combine(Path.GetTempPath(), $"QM-SF-{Guid.NewGuid():N}"));
        Assert.Equal(string.Empty, new ConfigService(profile).Load().StartupFolder);
    }
}

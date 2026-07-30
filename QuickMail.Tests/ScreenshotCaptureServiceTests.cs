using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Screenshot capture engine (#175). The Win32 pixel grab sits behind the
/// PixelGrabber seam so none of these tests need a real HWND; what is under
/// test is the session policy — foldering, slugs, debounce, the cap, and the
/// off-by-default safety story.
/// </summary>
public class ScreenshotCaptureServiceTests : IDisposable
{
    private readonly string _profileDir =
        Path.Combine(Path.GetTempPath(), $"QM-ShotTests-{Guid.NewGuid():N}");

    private ScreenshotCaptureService NewService() =>
        new(new ProfileContext(_profileDir));

    private static BitmapSource TinyFrame()
    {
        // 2×2 with two colors so the frame never trips single-color detection.
        var pixels = new byte[] { 0, 0, 0, 255, 255, 255, 255, 255, 0, 0, 0, 255, 255, 255, 255, 255 };
        return BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
    }

    public void Dispose()
    {
        try { Directory.Delete(_profileDir, recursive: true); } catch { /* temp cleanup */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void SessionFolder_LivesUnderProfileDebugScreenshots()
    {
        using var svc = NewService();
        Assert.StartsWith(Path.Combine(_profileDir, "debug-screenshots"), svc.SessionFolder, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Enable_CreatesTheSessionFolder_AndRaisesEnabledChanged()
    {
        using var svc = NewService();
        var raised = 0;
        svc.EnabledChanged += (_, _) => raised++;

        svc.Enabled = true;

        Assert.True(svc.Enabled);
        Assert.True(Directory.Exists(svc.SessionFolder));
        Assert.Equal(1, raised);

        svc.Enabled = false;
        Assert.Equal(2, raised);
    }

    [Theory]
    [InlineData("MainWindow", "MainWindow")]
    [InlineData("Reading Pane: Re/Fwd?", "Reading-Pane--Re-Fwd")]
    [InlineData("  ", "window")]
    [InlineData("", "window")]
    public void Slug_IsFilesystemSafe(string label, string expected)
    {
        Assert.Equal(expected, ScreenshotCaptureService.Slug(label));
        Assert.DoesNotContain(ScreenshotCaptureService.Slug(label),
            s => Path.GetInvalidFileNameChars().Contains(s));
    }

    [StaFact]
    public void Capture_WhenDisabled_NeverGrabsPixels()
    {
        using var svc = NewService();
        var grabs = 0;
        svc.PixelGrabber = _ => { grabs++; return TinyFrame(); };

        svc.Capture(new Window(), "AnyWindow");

        Assert.Equal(0, grabs);
        Assert.False(Directory.Exists(Path.Combine(_profileDir, "debug-screenshots")));
    }

    [StaFact]
    public void Capture_WritesOneLabeledPng()
    {
        var svc = NewService();
        svc.PixelGrabber = _ => TinyFrame();
        svc.Enabled = true;

        svc.Capture(new Window(), "ComposeWindow");
        svc.Dispose();   // flushes the background save

        var files = Directory.GetFiles(svc.SessionFolder, "*.png");
        var file = Assert.Single(files);
        Assert.Equal("0001-ComposeWindow.png", Path.GetFileName(file));
    }

    [StaFact]
    public void Capture_DebouncesRepeatsOfTheSameLabel_ButNotOtherLabels()
    {
        using var svc = NewService();
        var grabs = 0;
        svc.PixelGrabber = _ => { grabs++; return null; };
        svc.Enabled = true;
        var window = new Window();

        svc.Capture(window, "ReadingPane");
        svc.Capture(window, "ReadingPane");   // < 750 ms later — suppressed
        svc.Capture(window, "MessageWindow"); // different label — captured

        Assert.Equal(2, grabs);
    }

    [StaFact]
    public void Capture_StopsAtTheSessionCap()
    {
        using var svc = NewService();
        var grabs = 0;
        svc.PixelGrabber = _ => { grabs++; return null; };
        svc.Enabled = true;
        var window = new Window();

        for (var i = 0; i < 520; i++)
            svc.Capture(window, $"Window{i}");   // distinct labels bypass the debounce

        Assert.Equal(500, grabs);
    }

    [StaFact]
    public void Capture_AfterDispose_IsANoOp()
    {
        var svc = NewService();
        var grabs = 0;
        svc.PixelGrabber = _ => { grabs++; return TinyFrame(); };
        svc.Enabled = true;
        svc.Dispose();

        svc.Capture(new Window(), "Late");

        Assert.Equal(0, grabs);
    }

    [Fact]
    public void IsSingleColor_DetectsFlatFrames_AndPassesMixedOnes()
    {
        var flat = new byte[4 * 4];
        for (var i = 0; i < flat.Length; i += 4) { flat[i + 3] = 255; }
        var flatFrame = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, flat, 8);

        Assert.True(ScreenshotCaptureService.IsSingleColor(flatFrame));
        Assert.False(ScreenshotCaptureService.IsSingleColor(TinyFrame()));
    }

    // ── Title suffix ──────────────────────────────────────────────────────────

    [StaFact]
    public void TitleSuffix_AppliedAndRemoved_OnStaticTitles()
    {
        var window = new Window { Title = "Address Book" };

        ScreenshotCaptureService.ApplyTitleSuffix(window, enabled: true);
        Assert.Equal("Address Book" + IScreenshotCaptureService.TitleSuffix, window.Title);

        // Idempotent — applying twice must not double the suffix.
        ScreenshotCaptureService.ApplyTitleSuffix(window, enabled: true);
        Assert.Equal("Address Book" + IScreenshotCaptureService.TitleSuffix, window.Title);

        ScreenshotCaptureService.ApplyTitleSuffix(window, enabled: false);
        Assert.Equal("Address Book", window.Title);
    }

    [StaFact]
    public void TitleSuffix_NeverTouchesDataBoundTitles()
    {
        // MainWindow's Title binds to MainViewModel.WindowTitle, which appends
        // the suffix itself; a direct assignment would detach that binding.
        var window = new Window { DataContext = new { Name = "Inbox - QuickMail" } };
        BindingOperations.SetBinding(window, Window.TitleProperty, new Binding("Name"));
        Assert.Equal("Inbox - QuickMail", window.Title);

        ScreenshotCaptureService.ApplyTitleSuffix(window, enabled: true);

        Assert.Equal("Inbox - QuickMail", window.Title);
        Assert.NotNull(BindingOperations.GetBindingExpression(window, Window.TitleProperty));
    }

    // ── MainViewModel title integration ───────────────────────────────────────

    [Fact]
    public void MainViewModel_WindowTitle_CarriesSuffixOnlyWhileEnabled()
    {
        using var svc = NewService();
        var vm = new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(),
            new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
            new StubRuleService(), new StubSmtpService(), screenshotCapture: svc);

        var titleChanges = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.WindowTitle)) titleChanges++; };

        Assert.False(vm.WindowTitle.EndsWith(IScreenshotCaptureService.TitleSuffix, StringComparison.Ordinal));

        svc.Enabled = true;
        Assert.EndsWith(IScreenshotCaptureService.TitleSuffix, vm.WindowTitle, StringComparison.Ordinal);
        Assert.Equal(1, titleChanges);

        svc.Enabled = false;
        Assert.False(vm.WindowTitle.EndsWith(IScreenshotCaptureService.TitleSuffix, StringComparison.Ordinal));
        Assert.Equal(2, titleChanges);

        vm.Dispose();
    }

    // ── Settings behavior ─────────────────────────────────────────────────────

    [Fact]
    public void Settings_TogglingForwardsToTheService_AndAnnouncesTheMetaChange()
    {
        using var svc = NewService();
        var vm = new SettingsViewModel(new StubConfigService(), new StubCommandRegistry(),
            screenshotCapture: svc);
        var announcements = new System.Collections.Generic.List<string>();
        vm.DiagnosticsAnnouncementRequested += announcements.Add;

        vm.ScreenshotCaptureEnabled = true;
        Assert.True(svc.Enabled);
        vm.ScreenshotCaptureEnabled = false;
        Assert.False(svc.Enabled);

        Assert.Equal(2, announcements.Count);
        Assert.Contains("on", announcements[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("off", announcements[1], StringComparison.OrdinalIgnoreCase);

        // Non-persistence is deliberate: no ConfigModel member exists for this
        // toggle, so nothing can reach config.ini (see spec Decision D).
        Assert.DoesNotContain(typeof(QuickMail.Models.ConfigModel).GetProperties(),
            p => p.Name.Contains("Screenshot", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Settings_DiagnosticsRow_HiddenOutsideDebugMode()
    {
        using var svc = NewService();
        var withService = new SettingsViewModel(new StubConfigService(), new StubCommandRegistry(),
            screenshotCapture: svc);
        var withoutService = new SettingsViewModel(new StubConfigService(), new StubCommandRegistry());

        var originalDebugMode = LogService.DebugMode;
        try
        {
            LogService.DebugMode = false;
            Assert.False(withService.IsDebugDiagnosticsVisible);

            LogService.DebugMode = true;
            Assert.True(withService.IsDebugDiagnosticsVisible);
            Assert.False(withoutService.IsDebugDiagnosticsVisible);
        }
        finally
        {
            LogService.DebugMode = originalDebugMode;
        }
    }

    [Fact]
    public void NullService_IsInertEverywhere()
    {
        using var svc = new NullScreenshotCaptureService();
        svc.Enabled = true;                      // ignored
        Assert.False(svc.Enabled);
        Assert.Equal(string.Empty, svc.SessionFolder);
        svc.Capture(null!, "anything");          // must not throw
        svc.OpenFolder();
    }
}

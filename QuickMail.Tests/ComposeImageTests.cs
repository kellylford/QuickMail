using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MimeKit;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Issue #729 phase 2: pictures in the message body, each with alt text or marked decorative.
/// Covers the editor element (the listening test's option B), the HTML / Markdown / plain-text
/// output, the multipart/related message, the Outbox, reading pictures back out of a stored
/// message, and the compose window's picture commands.
/// </summary>
[Collection("WpfTests")]
public class ComposeImageTests
{
    /// <summary>A real, tiny PNG of the given size.</summary>
    private static byte[] Png(int width = 4, int height = 3)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
            new byte[width * height * 4], width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static InlineUIContainer OnlyPicture(FlowDocument doc) =>
        RichTextBlockEditing.EnumerateBlocks(doc.Blocks).OfType<Paragraph>()
            .SelectMany(p => p.Inlines).OfType<InlineUIContainer>().Single();

    // ── The editor element and the converter ─────────────────────────────────

    [StaFact]
    public void Picture_IsARealImage_NamedByItsAltText()
    {
        var doc = RichTextDocumentConverter.FromHtml("<p>Team <img src=\"cid:a@x\" alt=\"Our team\" /> photo</p>");

        var picture = OnlyPicture(doc);
        var image = Assert.IsType<Image>(picture.Child);
        Assert.Equal("Our team", AutomationProperties.GetName(image));
        Assert.Equal("cid:a@x", RichTextDocumentConverter.ImageOf(picture)!.Src);
    }

    [StaTheory]
    [InlineData("<img src=\"cid:a@x\" alt=\"\" />", "Decorative image")]
    [InlineData("<img src=\"cid:a@x\" />", "Image with no description")]
    public void PictureWithoutDescription_SaysWhichKind(string html, string expectedName)
    {
        var picture = OnlyPicture(RichTextDocumentConverter.FromHtml("<p>" + html + "</p>"));
        Assert.Equal(expectedName, AutomationProperties.GetName(picture.Child));
    }

    [StaFact]
    public void DecorativeAndUndescribed_StayDistinctInHtml()
    {
        var decorative = RichTextDocumentConverter.ToHtml(RichTextDocumentConverter.FromHtml("<p><img src=\"cid:a@x\" alt=\"\" /></p>"));
        var undescribed = RichTextDocumentConverter.ToHtml(RichTextDocumentConverter.FromHtml("<p><img src=\"cid:a@x\" /></p>"));

        Assert.Contains("alt=\"\"", decorative);
        Assert.DoesNotContain("alt=", undescribed);
    }

    [StaFact]
    public void Picture_KeepsItsSize_AndScalesDownForNarrowReaders()
    {
        var html = RichTextDocumentConverter.ToHtml(RichTextDocumentConverter.FromHtml(
            "<p><img src=\"cid:a@x\" alt=\"Chart\" width=\"800\" height=\"600\" /></p>"));
        Assert.Equal(
            "<p><img src=\"cid:a@x\" alt=\"Chart\" width=\"800\" height=\"600\" style=\"max-width: 100%; height: auto;\" /></p>",
            html);
    }

    [StaFact]
    public void Picture_InMarkdown_IsImageSyntax()
    {
        var doc = RichTextDocumentConverter.FromHtml("<p><img src=\"cid:a@x\" alt=\"Our team\" /></p>");
        Assert.Equal("![Our team](cid:a@x)", RichTextDocumentConverter.ToMarkdown(doc));
    }

    [StaFact]
    public void PlainTextPart_LabelsDescribedPictures_AndLeavesOutDecorative()
    {
        var doc = RichTextDocumentConverter.FromHtml(
            "<p>See <img src=\"cid:a@x\" alt=\"Our team\" /> and <img src=\"cid:b@x\" alt=\"\" /> done</p>");
        Assert.Equal("See [Image: Our team] and  done", RichTextDocumentConverter.ToPlainText(doc));
    }

    [StaFact]
    public void Resolver_DrawsThePicture_AndUnknownOnesGetThePlaceholder()
    {
        var editor = new RichTextBox();
        var bytes = Png();
        RichTextDocumentConverter.LoadInto(editor,
            "<p><img src=\"cid:known@x\" alt=\"A\" /><img src=\"https://example.com/b.png\" alt=\"B\" /></p>",
            src => src == "cid:known@x" ? ImageProcessing.ForDisplay(bytes) : null);

        var pictures = editor.Document.Blocks.OfType<Paragraph>().Single().Inlines.OfType<InlineUIContainer>().ToList();
        Assert.NotSame(RichTextDocumentConverter.PlaceholderImage, ((Image)pictures[0].Child).Source);
        Assert.Same(RichTextDocumentConverter.PlaceholderImage, ((Image)pictures[1].Child).Source);
        // A picture by address is kept as it came, never fetched.
        Assert.Contains("src=\"https://example.com/b.png\"", RichTextDocumentConverter.ToHtml(editor.Document));
    }

    [StaFact]
    public void AttributeValues_CannotPickThePicture()
    {
        // #729 security review: "src=" inside another attribute's value chose the part.
        var doc = RichTextDocumentConverter.FromHtml(
            "<p><img alt=\"a src=cid:alt1\" src=\"https://t.invalid/x.png\"></p>");
        var info = RichTextDocumentConverter.ImageOf(OnlyPicture(doc))!;
        Assert.Equal("https://t.invalid/x.png", info.Src);
        Assert.Equal("a src=cid:alt1", info.Alt);
    }

    [StaFact]
    public void AGreaterThanInAQuotedValue_DoesNotEndTheTag()
    {
        var doc = RichTextDocumentConverter.FromHtml("<p><img alt=\"1 > 0\" src=\"cid:a@x\">after</p>");
        Assert.Equal("1 > 0", RichTextDocumentConverter.ImageOf(OnlyPicture(doc))!.Alt);
    }

    [StaFact]
    public void TextThatLooksLikeAMarkdownPicture_StaysText()
    {
        var doc = RichTextDocumentConverter.FromHtml("<p>see ![x](cid:y@x) here</p>");
        var markdown = RichTextDocumentConverter.ToMarkdown(doc);
        Assert.Equal(@"see !\[x](cid:y@x) here", markdown);
        Assert.Empty(InlineImages.ReferencedInMarkdown(markdown));
    }

    [StaFact]
    public void ATinyPicture_IsStillVisibleInTheEditor()
    {
        var picture = OnlyPicture(RichTextDocumentConverter.FromHtml(
            "<p><img src=\"cid:a@x\" alt=\"\" width=\"1\" height=\"1\"></p>"));
        RichTextDocumentConverter.SetImageSource((Image)picture.Child,
            RichTextDocumentConverter.ImageOf(picture)!, ImageProcessing.ForDisplay(Png(1, 1)));
        Assert.True(((Image)picture.Child).MinWidth >= RichTextDocumentConverter.MinEditorImageSize);
    }

    // ── The message ──────────────────────────────────────────────────────────

    private static AccountModel Account() => new() { DisplayName = "Me", Username = "me@example.com" };

    private static AttachmentModel Picture(string cid) => new()
    {
        FileName = "team.png", ContentType = "image/png", Content = Png(), FileSize = 10, ContentId = cid,
    };

    [Fact]
    public void Message_CarriesPicturesInMultipartRelated_MarkedInline()
    {
        var compose = new ComposeModel
        {
            To = "a@b.com",
            Body = "text",
            Mode = ComposeMode.Html,
            HtmlBody = "<html><body><p><img src=\"cid:one@quickmail\" alt=\"Team\" /></p></body></html>",
            InlineImages = [Picture("one@quickmail"), Picture("deleted@quickmail")],
        };

        var message = MimeMessageBuilder.Build(compose, Account());

        var alternative = Assert.IsType<MultipartAlternative>(message.Body);
        var related = Assert.IsType<MultipartRelated>(alternative[1]);
        Assert.IsType<TextPart>(related.Root);
        var part = Assert.Single(related.OfType<MimePart>().Where(p => p.ContentType.MediaType == "image"));
        Assert.Equal("one@quickmail", part.ContentId);
        Assert.Equal(ContentDisposition.Inline, part.ContentDisposition!.Disposition);
        // A picture no longer in the body is not sent; nothing is listed as an attachment.
        Assert.Empty(message.Attachments);
    }

    [Fact]
    public void Message_WithPicturesAndAttachments_NestsRelatedInsideMixed()
    {
        var compose = new ComposeModel
        {
            To = "a@b.com",
            Body = "text",
            HtmlBody = "<p><img src=\"cid:one@quickmail\" alt=\"Team\" /></p>",
            InlineImages = [Picture("one@quickmail")],
            Attachments = [new AttachmentModel { FileName = "a.txt", ContentType = "text/plain", Content = [1] }],
        };

        var message = MimeMessageBuilder.Build(compose, Account());

        var mixed = Assert.IsType<Multipart>(message.Body);
        Assert.Equal("mixed", mixed.ContentType.MediaSubtype);
        Assert.IsType<MultipartAlternative>(mixed[0]);
        Assert.Equal("a.txt", Assert.Single(message.Attachments).ContentDisposition!.FileName);
    }

    [Fact]
    public void FromMime_ReadsPicturesBackOut_WithTheirBytes()
    {
        var compose = new ComposeModel
        {
            To = "a@b.com", Body = "text",
            HtmlBody = "<p><img src=\"cid:one@quickmail\" alt=\"Team\" /></p>",
            InlineImages = [Picture("one@quickmail")],
        };
        var message = MimeMessageBuilder.Build(compose, Account());

        var back = Assert.Single(InlineImages.FromMime(message));
        Assert.Equal("one@quickmail", back.ContentId);
        Assert.Equal(compose.InlineImages[0].Content, back.Content);
    }

    [Fact]
    public void ReferencedContentIds_FindsHtmlAndMarkdownReferences()
    {
        Assert.Equal(["a@x"], InlineImages.ReferencedContentIds("<img src=\"cid:a@x\">", null).ToArray());
        Assert.Equal(["b@x"], InlineImages.ReferencedInMarkdown("![b](cid:b@x)").ToArray());
    }

    [Fact]
    public async Task Outbox_KeepsPicturesApartFromAttachments()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"QuickMailOutbox-{Guid.NewGuid():N}");
        var store = new LocalStoreService(new ProfileContext(dir));
        store.Initialize();
        var compose = new ComposeModel
        {
            AccountId = Guid.NewGuid(), To = "a@b.com", Subject = "Pics",
            HtmlBody = "<p><img src=\"cid:one@quickmail\" alt=\"Team\" /></p>",
            InlineImages = [Picture("one@quickmail")],
            Attachments = [new AttachmentModel { FileName = "a.txt", ContentType = "text/plain", Content = [1] }],
        };
        var item = new OutboxItem { Id = OutboxItem.NewId(), AccountId = compose.AccountId, Kind = OutboxKind.Send };

        await store.UpsertOutboxItemAsync(item, compose);
        var back = await store.LoadOutboxComposeAsync(item.Id);

        Assert.NotNull(back);
        Assert.Equal("a.txt", Assert.Single(back.Attachments).FileName);
        var picture = Assert.Single(back.InlineImages);
        Assert.Equal("one@quickmail", picture.ContentId);
        Assert.Equal(compose.InlineImages[0].Content, picture.Content);
    }

    // ── Image processing ─────────────────────────────────────────────────────

    [StaFact]
    public void Prepare_ReadsSize_AndShrinkKeepsTheShape()
    {
        var prepared = ImageProcessing.Prepare(Png(3200, 1600));
        Assert.NotNull(prepared);
        Assert.Equal(3200, prepared.PixelWidth);

        var small = ImageProcessing.Shrink(prepared, ImageProcessing.ShrinkThreshold);
        Assert.Equal(1600, small.PixelWidth);
        Assert.Equal(800, small.PixelHeight);
        Assert.Equal("image/png", small.ContentType);
    }

    [Fact]
    public void Prepare_RejectsBytesThatAreNotAPicture() =>
        Assert.Null(ImageProcessing.Prepare([1, 2, 3, 4]));

    /// <summary>A PNG whose header claims a size, with no real pixel data — a decompression bomb's shape.</summary>
    private static byte[] PngHeaderOnly(int width, int height)
    {
        static uint Crc(byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (var b in data)
            {
                crc ^= b;
                for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }
            return ~crc;
        }
        static byte[] BigEndian(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
        static byte[] Chunk(string type, byte[] data)
        {
            var typed = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            return [.. BigEndian((uint)data.Length), .. typed, .. BigEndian(Crc(typed))];
        }
        byte[] ihdr = [.. BigEndian((uint)width), .. BigEndian((uint)height), 8, 6, 0, 0, 0];
        return [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Chunk("IHDR", ihdr), .. Chunk("IEND", [])];
    }

    [StaFact]
    public void Prepare_RefusesAPictureThatDeclaresTooManyPixels_WithoutDecodingIt()
    {
        // 30000 by 30000 would be 3.6 GB decoded. The file is refused from its header, before any
        // pixels are allocated. (WIC refuses this particular file for having no image data; a
        // complete one would be refused as TooLarge. Either way nothing is decoded.)
        var bomb = PngHeaderOnly(30000, 30000);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(ImageProcessing.Prepare(bomb, out var why));
        Assert.NotEqual(ImageRejection.None, why);
        Assert.Null(ImageProcessing.ForDisplay(bomb));
        Assert.True(watch.ElapsedMilliseconds < 2000);
    }

    [StaFact]
    public void ForDisplay_DoesNotEnlargeASmallPicture()
    {
        var bitmap = ImageProcessing.ForDisplay(Png(50, 40));
        Assert.NotNull(bitmap);
        Assert.Equal(50, bitmap.PixelWidth);
    }

    /// <summary>A JPEG stored sideways with EXIF orientation 6, as phones save portrait photos.</summary>
    private static byte[] SidewaysJpeg(int storedWidth, int storedHeight)
    {
        var bitmap = BitmapSource.Create(storedWidth, storedHeight, 96, 96, PixelFormats.Bgr32, null,
            new byte[storedWidth * storedHeight * 4], storedWidth * 4);
        var metadata = new BitmapMetadata("jpg");
        metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)6);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, null));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    [StaFact]
    public void PhonePhoto_IsMeasuredAndShrunkUpright()
    {
        var prepared = ImageProcessing.Prepare(SidewaysJpeg(3200, 2400));
        Assert.NotNull(prepared);
        Assert.Equal((2400, 3200), (prepared.PixelWidth, prepared.PixelHeight)); // portrait, as seen

        var small = ImageProcessing.Shrink(prepared, ImageProcessing.ShrinkThreshold);
        Assert.Equal((1600, 2133), (small.PixelWidth, small.PixelHeight));
        // The re-encoded file carries no orientation tag, so its stored pixels are upright.
        var decoded = BitmapDecoder.Create(new MemoryStream(small.Bytes), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Assert.Equal(1600, decoded.PixelWidth);
    }

    [StaFact]
    public void UndescribedPicture_StaysUndescribedThroughMarkdown()
    {
        var svc = new MarkdownService();
        var doc = RichTextDocumentConverter.FromHtml("<p><img src=\"cid:a@x\" /></p>");
        var markdown = RichTextDocumentConverter.ToMarkdown(doc);
        Assert.Equal("![](cid:a@x \"no description\")", markdown);

        var back = RichTextDocumentConverter.FromHtml(svc.ToHtml(markdown));
        Assert.Null(RichTextDocumentConverter.ImageOf(OnlyPicture(back))!.Alt);
        Assert.DoesNotContain("alt=", RichTextDocumentConverter.ToHtml(back));
    }

    // ── The description dialog ───────────────────────────────────────────────

    [StaFact]
    public void Dialog_OkWaitsForADescriptionOrDecorative()
    {
        var dialog = new ImageDescriptionDialog("a.png, 4 by 3 pixels");
        var ok = (Button)dialog.FindName("OkButton");
        var alt = (TextBox)dialog.FindName("AltBox");
        var decorative = (CheckBox)dialog.FindName("DecorativeBox");

        Assert.False(ok.IsEnabled);
        alt.Text = "   ";
        Assert.False(ok.IsEnabled);           // blank is not a description
        alt.Text = "Our team";
        Assert.True(ok.IsEnabled);
        alt.Text = string.Empty;
        decorative.IsChecked = true;
        Assert.True(ok.IsEnabled);
        Assert.False(alt.IsEnabled);          // nothing to describe when decorative
        dialog.Close();
    }

    [StaFact]
    public void Dialog_ReportsItsAnswerOnce_AndNullWhenCancelled()
    {
        ImageDescriptionResult? answer = new("x", false, false, false);
        int calls = 0;
        var cancelled = new ImageDescriptionDialog("a.png");
        cancelled.Completed += r => { answer = r; calls++; };
        cancelled.Show();
        cancelled.Close();
        Assert.Equal(1, calls);
        Assert.Null(answer);

        var described = new ImageDescriptionDialog("a.png", offerShrinkFromWidth: 3000);
        described.Completed += r => answer = r;
        described.Show();
        ((TextBox)described.FindName("AltBox")).Text = " Our team ";
        ((Button)described.FindName("OkButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(new ImageDescriptionResult("Our team", false, Shrink: true, Remove: false), answer);
    }

    [StaFact]
    public void Dialog_ForAnExistingPicture_OffersRemove_AndStartsWithItsDescription()
    {
        var dialog = new ImageDescriptionDialog("Picture", existing: new ComposeImage("cid:a@x", ""));
        Assert.Equal("Image Properties", dialog.Title);
        Assert.Equal(Visibility.Visible, ((Button)dialog.FindName("RemoveButton")).Visibility);
        Assert.True(((CheckBox)dialog.FindName("DecorativeBox")).IsChecked);
        Assert.Equal(Visibility.Collapsed, ((CheckBox)dialog.FindName("ShrinkBox")).Visibility);
        dialog.Close();
    }

    // ── The compose view model ───────────────────────────────────────────────

    private static ComposeViewModel NewVm(IMailService? mail = null) => new(new StubSmtpService(), new StubAccountService(),
        new StubCredentialService(), mail ?? new StubImapMailService(), new StubTemplateService());

    [Fact]
    public void BuildComposeModel_SendsOnlyPicturesTheBodyStillShows()
    {
        var vm = NewVm();
        vm.Seed(new ComposeModel());
        vm.SetMode(ComposeMode.Markdown);
        var kept = vm.AddInlineImage(Png(), "image/png", "kept.png");
        vm.AddInlineImage(Png(), "image/png", "deleted.png");
        vm.Body = $"Hi ![Kept](cid:{kept})";

        var model = vm.BuildComposeModel(Guid.NewGuid());

        Assert.Equal(kept, Assert.Single(model.InlineImages).ContentId);
    }

    [Fact]
    public void SwitchingToPlainText_SaysHowManyPicturesWillGo()
    {
        var vm = NewVm();
        vm.Seed(new ComposeModel());
        vm.SetMode(ComposeMode.Markdown);
        var id = vm.AddInlineImage(Png(), "image/png", "a.png");
        vm.Body = $"![A](cid:{id})";
        string? asked = null;
        vm.ConfirmationRequested = (message, _) => { asked = message; return false; };

        Assert.False(vm.SetMode(ComposeMode.PlainText));
        Assert.Contains("1 picture will be removed", asked);
    }

    private sealed class MailWithOriginal(byte[] original) : StubImapMailServiceBase, IMailService
    {
        public Task CopyOriginalMessageToAsync(Guid accountId, string folderName, string messageId, Stream destination, CancellationToken ct = default) =>
            destination.WriteAsync(original, ct).AsTask();
    }

    [Fact]
    public async Task Forward_FetchesTheOriginalsPictures()
    {
        var original = MimeMessageBuilder.Build(new ComposeModel
        {
            To = "me@example.com", Body = "x",
            HtmlBody = "<p><img src=\"cid:one@sender\" alt=\"Team\" /></p>",
            InlineImages = [Picture("one@sender")],
        }, Account());
        using var raw = new MemoryStream();
        original.WriteTo(raw);

        var vm = NewVm(new MailWithOriginal(raw.ToArray()));
        var detail = new MailMessageDetail
        {
            MessageId = "7", AccountId = Guid.NewGuid(), FolderName = "Inbox", From = "sender@example.com",
            Subject = "Photos", PlainTextBody = "x", HtmlBody = "<p><img src=\"cid:one@sender\" alt=\"Team\" /></p>",
        };
        vm.Seed(ComposeViewModel.CreateForward(detail, detail.AccountId));
        int arrived = 0;
        vm.InlineImagesArrived += () => arrived++;

        await vm.FetchSourcePicturesAsync();

        Assert.Equal(1, arrived);
        Assert.NotNull(vm.GetInlineImageBytes("cid:one@sender"));
    }

    [Fact]
    public async Task Reply_OpenedAsPlainText_FetchesPicturesWhenSwitchedToHtml()
    {
        var original = MimeMessageBuilder.Build(new ComposeModel
        {
            To = "me@example.com", Body = "x",
            HtmlBody = "<p><img src=\"cid:one@sender\" alt=\"Team\" /></p>",
            InlineImages = [Picture("one@sender")],
        }, Account());
        using var raw = new MemoryStream();
        original.WriteTo(raw);

        var vm = NewVm(new MailWithOriginal(raw.ToArray()));
        var detail = new MailMessageDetail
        {
            MessageId = "7", AccountId = Guid.NewGuid(), FolderName = "Inbox", From = "sender@example.com",
            Subject = "Photos", PlainTextBody = "x", HtmlBody = "<p><img src=\"cid:one@sender\" alt=\"Team\" /></p>",
        };
        vm.Seed(ComposeViewModel.CreateReply(detail, detail.AccountId)); // opens as Plain Text
        var arrived = new TaskCompletionSource();
        vm.InlineImagesArrived += () => arrived.TrySetResult();

        vm.SetMode(ComposeMode.Html); // the stored reply HTML, with the picture, reaches the editor

        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.NotNull(vm.GetInlineImageBytes("cid:one@sender"));
    }

    [Fact]
    public void MarkdownMode_SendsAnUndescribedPictureWithNoAlt_NotAsDecorative()
    {
        var vm = NewVm();
        vm.Seed(new ComposeModel());
        vm.SetMode(ComposeMode.Markdown);
        var id = vm.AddInlineImage(Png(), "image/png", "a.png");
        vm.Body = $"![](cid:{id} \"no description\") and ![](cid:{id})";

        var html = vm.BuildComposeModel(Guid.NewGuid()).HtmlBody!;

        Assert.Contains($"<img src=\"cid:{id}\" />", html);            // undescribed: no alt, no title
        Assert.Contains($"<img src=\"cid:{id}\" alt=\"\" />", html);   // decorative stays decorative
        Assert.DoesNotContain("no description", html);
    }

    // ── The compose window ───────────────────────────────────────────────────

    private static (ComposeWindow Window, ComposeViewModel Vm, RichTextBox Editor) HtmlWindow()
    {
        var vm = NewVm();
        var window = new ComposeWindow(vm, new StubContactService(), new StubTemplateService(), new StubConfigService())
        {
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            ConfirmSaveOnClose = null,
        };
        window.Show();
        vm.SetMode(ComposeMode.Html);
        window.UpdateLayout();
        var editor = window.FindName("RichBodyBox") as RichTextBox;
        Assert.NotNull(editor);
        return (window, vm, editor!);
    }

    [StaFact]
    public void ImageAtCaret_FindsThePictureNextToTheCaret()
    {
        var (window, vm, editor) = HtmlWindow();
        try
        {
            var id = vm.AddInlineImage(Png(), "image/png", "a.png");
            RichTextDocumentConverter.LoadInto(editor, $"<p>Before <img src=\"cid:{id}\" alt=\"Team\" /> after</p>");
            var paragraph = editor.Document.Blocks.OfType<Paragraph>().Single();
            var first = paragraph.Inlines.OfType<Run>().First();

            editor.CaretPosition = first.ContentEnd;             // just before the picture
            Assert.NotNull(window.ImageAtCaret());
            Assert.Contains("Image, Team", window.GetFormattingParts());

            editor.CaretPosition = paragraph.ContentStart;       // far from it
            Assert.Null(window.ImageAtCaret());

            // One character away is not "at" the picture: "Befor|e " then the picture.
            editor.CaretPosition = first.ContentEnd.GetPositionAtOffset(-2)!;
            Assert.Null(window.ImageAtCaret());
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void UndoingARemovedPicture_BringsItBackVisible()
    {
        var (window, vm, editor) = HtmlWindow();
        try
        {
            var id = vm.AddInlineImage(Png(), "image/png", "a.png");
            RichTextDocumentConverter.LoadInto(editor, $"<p>A <img src=\"cid:{id}\" alt=\"Team\" /> B</p>");
            window.RepairPictures(); // draws it: LoadInto here was called without the window's resolver
            var picture = OnlyPicture(editor.Document);
            editor.Focus();
            editor.Selection.Select(picture.ElementStart, picture.ElementEnd);
            editor.Selection.Text = string.Empty;
            Assert.Throws<InvalidOperationException>(() => OnlyPicture(editor.Document));

            Assert.True(editor.Undo());

            var restored = (Image)OnlyPicture(editor.Document).Child;
            Assert.NotNull(restored.Source);
            Assert.NotSame(RichTextDocumentConverter.PlaceholderImage, restored.Source);
            Assert.Equal("Team", AutomationProperties.GetName(restored));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void CuttingAPicture_IsRefusedRatherThanLosingIt()
    {
        var (window, vm, editor) = HtmlWindow();
        try
        {
            var id = vm.AddInlineImage(Png(), "image/png", "a.png");
            RichTextDocumentConverter.LoadInto(editor, $"<p>A <img src=\"cid:{id}\" alt=\"Team\" /> B</p>");
            var picture = OnlyPicture(editor.Document);
            editor.Focus();
            editor.Selection.Select(picture.ElementStart, picture.ElementEnd);

            ApplicationCommands.Cut.Execute(null, editor);

            Assert.Contains($"cid:{id}", RichTextDocumentConverter.ToHtml(editor.Document));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void ForwardWindow_DrawsTheOriginalsPictureWhenItArrives()
    {
        var original = MimeMessageBuilder.Build(new ComposeModel
        {
            To = "me@example.com", Body = "x",
            HtmlBody = "<p><img src=\"cid:one@sender\" alt=\"Team\" /></p>",
            InlineImages = [Picture("one@sender")],
        }, Account());
        using var raw = new MemoryStream();
        original.WriteTo(raw);

        var vm = NewVm(new MailWithOriginal(raw.ToArray()));
        var detail = new MailMessageDetail
        {
            MessageId = "7", AccountId = Guid.NewGuid(), FolderName = "Inbox", From = "sender@example.com",
            Subject = "Photos", PlainTextBody = "x", HtmlBody = "<p><img src=\"cid:one@sender\" alt=\"Team\" /></p>",
        };
        vm.Seed(ComposeViewModel.CreateForward(detail, detail.AccountId));
        var window = new ComposeWindow(vm, new StubContactService(), new StubTemplateService(), new StubConfigService())
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, ConfirmSaveOnClose = null,
        };
        try
        {
            window.Show(); // Loaded opens the forward in HTML mode and starts the fetch
            var editor = (RichTextBox)window.FindName("RichBodyBox");
            var image = (Image)OnlyPicture(editor.Document).Child;

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (ReferenceEquals(image.Source, RichTextDocumentConverter.PlaceholderImage) && DateTime.UtcNow < deadline)
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

            Assert.NotSame(RichTextDocumentConverter.PlaceholderImage, image.Source);
            Assert.Equal("Team", AutomationProperties.GetName(image));
        }
        finally { window.Close(); }
    }
}

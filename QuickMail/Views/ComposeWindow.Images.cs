using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Views;

/// <summary>
/// Pictures in the message body (#729 phase 2). A picture is described before it goes in — Insert
/// Image, paste and drop all go through <see cref="ImageDescriptionDialog"/> — and can be
/// described again or removed with Image Properties. In HTML mode a picture is a real image
/// named by its description (the listening test's option B); in Markdown mode it is
/// <c>![alt](cid:…)</c>. Plain Text mode cannot hold one, so Insert Image offers to attach the
/// file instead.
/// </summary>
public partial class ComposeWindow
{
    private ImageDescriptionDialog? _imageDialog;
    private readonly Queue<(PreparedImage Image, string Name)> _pendingImages = new();
    private readonly Dictionary<string, ImageSource?> _displayImages = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"!\[((?:\\\]|[^\]])*)\]\(([^)\s]+)(?:\s+""([^""]*)"")?\)")]
    private static partial Regex MarkdownImage();

    // ── Drawing pictures in the editor ───────────────────────────────────────

    /// <summary>
    /// The picture for an <c>src</c> as the editor draws it, decoded once per picture. Null for a
    /// picture whose bytes are not here (drawn as a placeholder); never fetched from the web.
    /// </summary>
    private ImageSource? ResolveEditorImage(string src)
    {
        if (_displayImages.TryGetValue(src, out var cached) && cached is not null) return cached;
        var bytes = _vm.GetInlineImageBytes(src);
        var bitmap = bytes is null ? null : ImageProcessing.ForDisplay(bytes);
        if (bitmap is not null) _displayImages[src] = bitmap;
        return bitmap;
    }

    /// <summary>Draws pictures that arrived after the document was built — a reply's or forward's quoted pictures.</summary>
    private async void OnInlineImagesArrived()
    {
        // These came from a message someone else sent: decode them on the thread pool (the
        // bitmaps are frozen), so a picture built to be slow to decode cannot freeze the window.
        var waiting = EditorPictures()
            .Where(c => c.Child is Image image && ReferenceEquals(image.Source, RichTextDocumentConverter.PlaceholderImage))
            .Select(c => (Container: c, Info: (ComposeImage)c.Tag))
            .ToList();
        var sources = waiting.Select(w => w.Info.Src).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(src => (Src: src, Bytes: _vm.GetInlineImageBytes(src)))
            .Where(s => s.Bytes is not null)
            .ToList();
        try
        {
            var decoded = await Task.Run(() => sources.ToDictionary(
                s => s.Src, s => (ImageSource?)ImageProcessing.ForDisplay(s.Bytes!), StringComparer.OrdinalIgnoreCase));
            if (!IsLoaded) return;
            foreach (var (container, info) in waiting)
            {
                if (container.Child is not Image image || !decoded.TryGetValue(info.Src, out var source) || source is null)
                    continue;
                _displayImages[info.Src] = source;
                RichTextDocumentConverter.SetImageSource(image, info, source);
            }
        }
        catch (Exception ex)
        {
            LogService.Log("Compose: drawing arrived pictures", ex);
        }
    }

    /// <summary>
    /// WPF's undo restores a removed picture's container with its tag but rebuilds the image
    /// empty — no source, no size — so an undone Remove or Delete would bring back a picture
    /// that is named and sent but invisible. Redraw any picture without a source.
    /// </summary>
    internal void RepairPictures()
    {
        foreach (var container in EditorPictures())
        {
            if (container.Tag is not ComposeImage info) continue;
            if (container.Child is Image { Source: null } image)
            {
                RichTextDocumentConverter.SetImageSource(image, info, ResolveEditorImage(info.Src));
                System.Windows.Automation.AutomationProperties.SetName(image, info.AccessibleName);
            }
            else if (container.Child is not Image)
            {
                var image2 = new Image { Stretch = Stretch.Uniform, MaxWidth = RichTextDocumentConverter.MaxEditorImageWidth };
                RichTextDocumentConverter.SetImageSource(image2, info, ResolveEditorImage(info.Src));
                System.Windows.Automation.AutomationProperties.SetName(image2, info.AccessibleName);
                container.Child = image2;
            }
        }
    }

    /// <summary>The Content-IDs of the pictures in the editor now, for the view model's reply/forward fetch.</summary>
    private HashSet<string> EditorPictureIds() =>
        EditorPictures()
            .Select(c => ((ComposeImage)c.Tag).ContentId)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private IEnumerable<InlineUIContainer> EditorPictures() =>
        RichTextBlockEditing.EnumerateBlocks(RichBodyBox.Document.Blocks)
            .OfType<Paragraph>()
            .SelectMany(p => AllInlines(p.Inlines))
            .OfType<InlineUIContainer>()
            .Where(c => c.Tag is ComposeImage);

    private static IEnumerable<Inline> AllInlines(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            yield return inline;
            if (inline is Span span)
                foreach (var inner in AllInlines(span.Inlines))
                    yield return inner;
        }
    }

    // ── Insert Image ─────────────────────────────────────────────────────────

    /// <summary>Insert → Image (Ctrl+Shift+I): choose picture files, then describe each.</summary>
    internal void InsertImage()
    {
        if (_vm.CurrentMode == ComposeMode.PlainText)
        {
            OfferToAttachInstead();
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Insert Image",
            Filter = ImageProcessing.OpenFileFilter,
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            FocusActiveEditor();
            return;
        }
        _ = QueueImageFilesAsync(dialog.FileNames);
    }

    /// <summary>
    /// Plain text cannot hold a picture. Rather than refuse, offer what plain text can do with
    /// one: attach it.
    /// </summary>
    private void OfferToAttachInstead()
    {
        var answer = MessageBox.Show(this,
            "Pictures can go in the message body only in Markdown or HTML mode. " +
            "Attach the picture as a file instead?",
            "Insert Image", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
            _vm.AddAttachmentsCommand.Execute(null);
        else
            FocusActiveEditor();
    }

    /// <summary>Reads picture files and describes them one at a time. A file that is not a picture is said once and skipped.</summary>
    private async Task QueueImageFilesAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            byte[] bytes;
            try
            {
                if (new FileInfo(path).Length > ImageProcessing.MaxFileBytes)
                {
                    AccessibilityHelper.Announce(this, $"{name} is too large to put in a message.",
                        category: AnnouncementCategory.Result);
                    continue;
                }
                bytes = await File.ReadAllBytesAsync(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AccessibilityHelper.Announce(this, $"Could not read {name}.",
                    category: AnnouncementCategory.Result);
                continue;
            }
            // Decoding a photo can take a moment; keep it off the UI thread.
            var (prepared, rejection) = await Task.Run(() =>
            {
                var p = ImageProcessing.Prepare(bytes, out var r);
                return (p, r);
            });
            if (prepared is not null)
                _pendingImages.Enqueue((prepared, name));
            else
                AccessibilityHelper.Announce(this,
                    rejection == ImageRejection.TooLarge
                        ? $"{name} has too many pixels to put in a message."
                        : $"{name} is not a picture QuickMail can read.",
                    category: AnnouncementCategory.Result);
        }
        ShowNextImageDialog();
    }

    private void QueueImage(PreparedImage image, string name)
    {
        _pendingImages.Enqueue((image, name));
        ShowNextImageDialog();
    }

    /// <summary>One description dialog at a time; the next picture's opens when the last one closes.</summary>
    private void ShowNextImageDialog()
    {
        if (_imageDialog is not null || _pendingImages.Count == 0) return;
        var (image, name) = _pendingImages.Dequeue();

        var dialog = new ImageDescriptionDialog(
            $"{name}, {image.PixelWidth} by {image.PixelHeight} pixels",
            existing: null, offerShrinkFromWidth: image.PixelWidth) { Owner = this };
        _imageDialog = dialog;
        dialog.Completed += result =>
        {
            _imageDialog = null;
            if (result is not null)
                _ = PlaceImageAsync(image, name, result);
            else
                FocusActiveEditor();
            Dispatcher.BeginInvoke(ShowNextImageDialog);
        };
        dialog.Show();
    }

    /// <summary>Puts a described picture at the caret (replacing any selection) in the current mode.</summary>
    private async Task PlaceImageAsync(PreparedImage image, string name, ImageDescriptionResult result)
    {
        if (result.Shrink)
        {
            var original = image;
            try
            {
                image = await Task.Run(() => ImageProcessing.Shrink(original, ImageProcessing.ShrinkThreshold));
            }
            catch (Exception ex)
            {
                LogService.Log($"Compose: shrinking '{name}' failed", ex);
                AnnounceFormatting($"Could not shrink {name}. It was not inserted");
                return;
            }
        }
        if (!IsLoaded) return; // the window closed while the picture was being shrunk

        var contentId = _vm.AddInlineImage(image.Bytes, image.ContentType, name);
        var info = new ComposeImage("cid:" + contentId, result.Alt, image.PixelWidth, image.PixelHeight);
        FocusActiveEditor();

        if (_vm.CurrentMode == ComposeMode.Markdown)
        {
            var markdown = $"![{result.Alt.Replace("]", @"\]")}]({info.Src})";
            BodyBox.SelectedText = markdown;
            BodyBox.Select(BodyBox.SelectionStart + markdown.Length, 0);
        }
        else if (_vm.CurrentMode == ComposeMode.Html)
        {
            RichBodyBox.BeginChange();
            try
            {
                if (!RichBodyBox.Selection.IsEmpty)
                    RichBodyBox.Selection.Text = string.Empty;
                var element = RichTextDocumentConverter.CreateImageElement(info, ResolveEditorImage(info.Src),
                    RichBodyBox.CaretPosition.GetInsertionPosition(LogicalDirection.Forward));
                RichBodyBox.CaretPosition = element.ElementEnd;
            }
            finally { RichBodyBox.EndChange(); }
            _vm.MarkBodyDirty();
        }

        AnnounceFormatting(result.Decorative ? "Decorative image inserted" : "Image inserted");
    }

    // ── Image Properties ─────────────────────────────────────────────────────

    /// <summary>
    /// Image Properties (Alt+Enter): describe the picture at the caret again, mark it decorative,
    /// or remove it. The picture "at the caret" is the one just after it — where the caret sits
    /// when a screen reader reads the picture — or, failing that, the one just before it.
    /// </summary>
    internal void ShowImageProperties()
    {
        if (_imageDialog is not null) { _imageDialog.Activate(); return; }

        if (_vm.CurrentMode == ComposeMode.Markdown)
        {
            ShowMarkdownImageProperties();
            return;
        }

        var element = ImageAtCaret();
        if (element?.Tag is not ComposeImage info)
        {
            AnnounceFormatting("No image at the cursor");
            return;
        }

        var dialog = new ImageDescriptionDialog(DescribeForContext(info), existing: info) { Owner = this };
        _imageDialog = dialog;
        dialog.Completed += result =>
        {
            _imageDialog = null;
            Dispatcher.BeginInvoke(ShowNextImageDialog);
            FocusActiveEditor();
            if (result is null) return;
            // The dialog is modeless: the picture may have been deleted, or the mode switched,
            // while it was open. Never report a change to a picture that is no longer there.
            if (_vm.CurrentMode != ComposeMode.Html || !IsInEditor(element))
            {
                AnnounceFormatting("That image is no longer in the message");
                return;
            }
            if (result.Remove)
            {
                RichBodyBox.BeginChange();
                try
                {
                    var after = element.ElementEnd.GetInsertionPosition(LogicalDirection.Forward);
                    (element.Parent as Paragraph)?.Inlines.Remove(element);
                    (element.Parent as Span)?.Inlines.Remove(element);
                    RichBodyBox.CaretPosition = after;
                }
                finally { RichBodyBox.EndChange(); }
                _vm.MarkBodyDirty();
                AnnounceFormatting("Image removed");
                return;
            }
            RichTextDocumentConverter.UpdateImage(element, info with { Alt = result.Alt });
            _vm.MarkBodyDirty();
            AnnounceFormatting(result.Decorative ? "Marked as decorative" : "Description changed");
        };
        dialog.Show();
    }

    /// <summary>True while the element is still part of the editor's document.</summary>
    private bool IsInEditor(TextElement element)
    {
        DependencyObject? at = element;
        while (at is TextElement te) at = te.Parent;
        return ReferenceEquals(at, RichBodyBox.Document);
    }

    private static string DescribeForContext(ComposeImage info) =>
        info.Width is > 0 && info.Height is > 0
            ? $"Picture, {info.Width} by {info.Height} pixels"
            : "Picture";

    /// <summary>The picture just after the caret, else just before it; null when there is none.</summary>
    internal InlineUIContainer? ImageAtCaret()
    {
        var caret = RichBodyBox.Selection.Start;
        return PictureBeside(caret, LogicalDirection.Forward) ?? PictureBeside(caret, LogicalDirection.Backward);
    }

    /// <summary>
    /// The picture directly beside <paramref name="position"/> in one direction, stepping only
    /// over the edges of runs and spans (formatting boundaries the caret cannot tell apart) —
    /// never over a character, so a picture one letter away is not "at the caret".
    /// </summary>
    private static InlineUIContainer? PictureBeside(TextPointer position, LogicalDirection direction)
    {
        for (var at = position; at is not null;)
        {
            var context = at.GetPointerContext(direction);
            if (context is not (TextPointerContext.ElementStart or TextPointerContext.ElementEnd)) return null;
            var element = at.GetAdjacentElement(direction);
            if (element is InlineUIContainer { Tag: ComposeImage } picture) return picture;
            if (element is not (Run or Span)) return null;
            at = at.GetNextContextPosition(direction);
        }
        return null;
    }

    /// <summary>Markdown mode: the <c>![alt](src)</c> the caret is in or next to, re-described in place.</summary>
    private void ShowMarkdownImageProperties()
    {
        var text = BodyBox.Text;
        var caret = BodyBox.CaretIndex;
        var match = MarkdownImage().Matches(text).FirstOrDefault(m => caret >= m.Index && caret <= m.Index + m.Length);
        if (match is null)
        {
            AnnounceFormatting("No image at the cursor");
            return;
        }

        string? alt = match.Groups[1].Value.Replace(@"\]", "]");
        if (alt.Length == 0 && match.Groups[3].Value == RichTextDocumentConverter.NoDescriptionTitle)
            alt = null; // undescribed, not decorative
        var info = new ComposeImage(match.Groups[2].Value, alt);
        var dialog = new ImageDescriptionDialog("Picture", existing: info) { Owner = this };
        _imageDialog = dialog;
        dialog.Completed += result =>
        {
            _imageDialog = null;
            Dispatcher.BeginInvoke(ShowNextImageDialog);
            FocusActiveEditor();
            if (result is null) return;
            // The text may have changed while the dialog was open; only rewrite what is still there.
            if (BodyBox.Text.Length < match.Index + match.Length
                || BodyBox.Text.Substring(match.Index, match.Length) != match.Value)
                return;
            var replacement = result.Remove ? string.Empty : $"![{result.Alt.Replace("]", @"\]")}]({info.Src})";
            BodyBox.Select(match.Index, match.Length);
            BodyBox.SelectedText = replacement;
            BodyBox.Select(match.Index + replacement.Length, 0);
            AnnounceFormatting(result.Remove ? "Image removed"
                : result.Decorative ? "Marked as decorative" : "Description changed");
        };
        dialog.Show();
    }

    // ── Paste and drop ───────────────────────────────────────────────────────

    /// <summary>
    /// Files pasted or dropped on the message body: in Markdown or HTML mode each picture is
    /// described and put in the body, and anything else is attached; in Plain Text mode
    /// everything is attached, as before.
    /// </summary>
    private async Task TakeFilesIntoBodyAsync(IReadOnlyList<string> files)
    {
        var intoBody = _vm.CurrentMode != ComposeMode.PlainText;
        var pictures = intoBody ? files.Where(ImageProcessing.IsImageFileName).ToList() : [];
        await AttachFilesAsync(files.Except(pictures).ToList());
        if (pictures.Count > 0)
            await QueueImageFilesAsync(pictures);
    }

    private async Task AttachFilesAsync(List<string> files)
    {
        if (files.Count == 0) return;
        int before = _vm.Attachments.Count;
        foreach (var f in files)
            await _vm.AddAttachmentFromPathAsync(f);
        int added = _vm.Attachments.Count - before;
        if (added > 0)
            AccessibilityHelper.Announce(this,
                added == 1 ? "1 file attached" : $"{added} files attached",
                category: AnnouncementCategory.Result);
    }

    /// <summary>
    /// Ctrl+V with a picture on the clipboard and the caret in the body. A picture alone (a
    /// screenshot, "Copy image") is described and put in; clipboard content that also carries
    /// text is left to the editor's own paste (Phase 3).
    /// </summary>
    private bool TryPastePicture()
    {
        if (_vm.CurrentMode == ComposeMode.PlainText) return false;
        if (!(BodyBox.IsKeyboardFocusWithin || RichBodyBox.IsKeyboardFocusWithin)) return false;
        try
        {
            if (!Clipboard.ContainsImage() || Clipboard.ContainsText()) return false;
            var bitmap = Clipboard.GetImage();
            if (bitmap is null) return false;
            QueueImage(ImageProcessing.FromBitmap(bitmap), "Pasted picture");
            return true;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another program is holding the clipboard; let the ordinary paste run.
            return false;
        }
    }

    /// <summary>
    /// Paste that does not come through Ctrl+V — Edit → Paste, the context menu — reaches the rich
    /// editor as a DataObject paste. The same rules apply: a picture alone, or picture files, go
    /// through the description dialog rather than being pasted undescribed.
    /// </summary>
    private void RichBodyBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (_vm.CurrentMode != ComposeMode.Html) return;
        var data = e.DataObject;
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
        {
            e.CancelCommand();
            _ = TakeFilesIntoBodyAsync(files.Where(f => f != null).ToList());
            return;
        }
        if (data.GetDataPresent(DataFormats.Bitmap) && !data.GetDataPresent(DataFormats.UnicodeText)
            && data.GetData(DataFormats.Bitmap) is System.Windows.Media.Imaging.BitmapSource bitmap)
        {
            e.CancelCommand();
            QueueImage(ImageProcessing.FromBitmap(bitmap), "Pasted picture");
        }
    }

    /// <summary>
    /// WPF's own copy of rich text leaves pictures out: pasted back, the picture is gone and only
    /// a space remains, and a cut would lose it for good. Until copy and paste keep pictures
    /// (Phase 3), a copy or cut that includes one is stopped and says why, rather than losing it.
    /// </summary>
    private void RichBodyBox_Copying(object sender, DataObjectCopyingEventArgs e)
    {
        if (_vm.CurrentMode != ComposeMode.Html) return;
        var selection = RichBodyBox.Selection;
        bool hasPicture = EditorPictures().Any(p =>
            selection.Start.CompareTo(p.ElementEnd) < 0 && selection.End.CompareTo(p.ElementStart) > 0);
        if (!hasPicture) return;
        e.CancelCommand();
        AnnounceFormatting("Pictures cannot be copied or cut yet. To move a picture, remove it and insert it again");
    }

    private void Editor_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private async void Editor_PreviewDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        e.Handled = true;
        await TakeFilesIntoBodyAsync(files.Where(f => f != null).ToList());
    }

    // ── Preview ──────────────────────────────────────────────────────────────

    // WebView2's NavigateToString refuses documents over 2 MB, so pictures in the preview are
    // scaled down to what a screen shows and the total is capped; past the cap a picture keeps its
    // cid: address and simply does not appear.
    private const int PreviewImageWidth = 960;
    private const int PreviewPictureBudget = 1_500_000;

    [GeneratedRegex(@"src=""cid:([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex CidSrc();

    /// <summary>
    /// The body HTML with each picture as a data: address, for the F8 preview. The pictures are
    /// looked up on the UI thread, then scaled down on the thread pool.
    /// </summary>
    private async Task<string> WithPreviewPicturesAsync(string html)
    {
        var pictures = CidSrc().Matches(html)
            .Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => (Id: id, Bytes: _vm.GetInlineImageBytes(id)))
            .Where(p => p.Bytes is not null)
            .ToList();
        if (pictures.Count == 0) return html;

        var dataUris = await Task.Run(() =>
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int used = 0;
            foreach (var (id, bytes) in pictures)
            {
                if (ImageProcessing.Prepare(bytes!) is not { } prepared) continue;
                var small = ImageProcessing.Shrink(prepared, PreviewImageWidth);
                var data = Convert.ToBase64String(small.Bytes);
                if (used + data.Length > PreviewPictureBudget) break;
                used += data.Length;
                result[id] = $"data:{small.ContentType};base64,{data}";
            }
            return result;
        });

        return CidSrc().Replace(html, m =>
            dataUris.TryGetValue(System.Net.WebUtility.HtmlDecode(m.Groups[1].Value), out var uri)
                ? $"src=\"{uri}\""
                : m.Value);
    }
}

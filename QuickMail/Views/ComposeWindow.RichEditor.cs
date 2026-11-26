using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickMail.Controls;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

// HTML mode on the hosted WinUI editor (#1099). With the new editor in use, HTML messages are
// edited in a second hosted editor (_newRich) made the first time a message goes into HTML mode,
// and RichBodyBox, the WPF editor, stays collapsed. Each formatting command in the window asks
// NewRich first and falls through to its WPF code when it is null, so the WPF path is untouched
// and remains what runs with the setting off, or if WinUI cannot start.
//
// The body goes in and out as RichParagraph lists: HTML -> FlowDocument (the existing converter)
// -> RichBody.Flatten -> the editor, and back the same way, so what is sent is written by the
// same code as before.
public partial class ComposeWindow
{
    private WinUiEditorHost? _newRich;

    // The HTML last loaded into the rich editor, to hand to the WPF editor if hosting fails.
    private string _newRichLoadedHtml = string.Empty;

    // The rich editor's text as it was after the last load: a text change that leaves this
    // unchanged is the load itself arriving late, not an edit. Null once the user has edited.
    private string? _newRichLoadedText = string.Empty;

    /// <summary>The formatted body in the hosted editor, when HTML mode is using it; otherwise null.</summary>
    private RichEditDocument? NewRich => _newRich?.Rich;

    /// <summary>True when HTML mode is the WPF editor's: the setting is off, or WinUI could not start.</summary>
    private bool UsesWpfRich => _vm.CurrentMode == ComposeMode.Html && _newRich == null;

    /// <summary>True when any of the body editors has keyboard focus.</summary>
    private bool AnyBodyHasFocus =>
        BodyBox.IsKeyboardFocusWithin || RichBodyBox.IsKeyboardFocusWithin || FocusedHost != null;

    /// <summary>The hosted editor Windows' keyboard focus is in, or null. See <see cref="WinUiEditorHost.HasFocus"/>.</summary>
    private WinUiEditorHost? FocusedHost =>
        _newRich is { HasFocus: true } rich ? rich
        : _newEditor is { HasFocus: true } plain ? plain
        : null;

    /// <summary>
    /// WPF sometimes has no focused element while a hosted editor has focus, and then routes key
    /// events to the window alone: the editor's own handlers never see the key. These two window
    /// handlers, added after the window's own, give such a key to the editor's handlers so the
    /// editor behaves the same either way. They do nothing when WPF routed the key properly.
    /// </summary>
    private void HostedFocus_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || FocusedHost is not { } host || IsFromHost(e, host)) return;
        LogService.Debug("[Compose] WPF had no focused element while the message body had focus; routing the key to it.");
        host.Focus(); // puts WPF's record right for the keys that follow; Windows' focus is already there
        if (ReferenceEquals(host, _newRich)) NewRich_PreviewKeyDown(host, e);
        else BodyBox_PreviewKeyDown(host, e);
    }

    private void HostedFocus_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || FocusedHost is not { } host || IsFromHost(e, host)) return;
        host.HandleKeyDown(e);
    }

    private static bool IsFromHost(KeyEventArgs e, WinUiEditorHost host) =>
        ReferenceEquals(e.OriginalSource, host) || ReferenceEquals(e.Source, host);

    /// <summary>
    /// Makes the hosted rich editor if the new editor is in use and it does not exist yet. False
    /// leaves HTML mode to the WPF editor.
    /// </summary>
    private bool EnsureNewRich()
    {
        if (_newRich != null) return true;
        if (!UsesNewEditor) return false;

        WinUiEditorHost host;
        try
        {
            s_windowsSpeller ??= new WindowsSpellChecker();
            var spelling = new PlainTextSpelling(s_windowsSpeller, word => _customDictionary?.Contains(word) == true);
            host = new WinUiEditorHost("Message body", spelling, rich: true,
                resolveImage: src => ResolveEditorImage(src) as BitmapSource)
            {
                Margin = BodyBox.Margin,
                VerticalAlignment = VerticalAlignment.Stretch,
                Visibility = Visibility.Collapsed,
            };
            KeyboardNavigation.SetTabIndex(host, BodyBox.TabIndex);
        }
#pragma warning disable CA1031 // any failure to start WinUI must leave HTML mode to the WPF editor, whatever it throws
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogService.Log("The new message editor could not start for HTML mode; using the standard editor", ex);
            return false;
        }

        System.Windows.Controls.Grid.SetRow(host, System.Windows.Controls.Grid.GetRow(BodyBox));
        BodyEditorGrid.Children.Add(host);
        _newRich = host;

        host.TextChanged += OnNewRichTextChanged;
        host.SelectionChanged += OnNewRichSelectionChanged;
        host.EditorLoaded += OnNewRichEditorLoaded;
        host.PreviewKeyDown += NewRich_PreviewKeyDown;
        host.TabPressed = HandleBodyTab;
        host.FilesDropped += OnNewEditorFilesDropped;
        host.HostingFailed += OnNewRichHostingFailed;
        host.PasteRequested = OnNewRichPaste;
        host.CopyRequested = OnNewRichCopy;
        host.IsSpellCheckEnabled = false; // see the note where the plain editor is made
        ApplyNewEditorTheme();
        return true;
    }

    /// <summary>The body from the hosted rich editor, in the three forms the view model takes.</summary>
    private static RichBodySnapshot SnapshotNewRich(RichEditDocument rich) =>
        RichTextDocumentConverter.Snapshot(RichBody.ToDocument(rich.Read()));

    /// <summary>Loads HTML into the hosted rich editor. Not a user edit.</summary>
    private void LoadHtmlIntoNewRich(RichEditDocument rich, string html)
    {
        _newRichLoadedHtml = html ?? string.Empty;
        _suppressFormattingAnnouncement = true;
        _lastAnnouncedBlockType = null;
        try
        {
            rich.Load(RichBody.Flatten(RichTextDocumentConverter.FromHtml(_newRichLoadedHtml)));
            _newRichLoadedText = _newRich!.Text;
        }
        finally
        {
            _suppressFormattingAnnouncement = false;
        }
    }

    /// <summary>
    /// The control resets fonts and sizes when it first loads, which is after a body loaded as
    /// the window opened (a reply in HTML). If nothing has been edited, the body is written again.
    /// </summary>
    private void OnNewRichEditorLoaded(object? sender, EventArgs e)
    {
        if (NewRich is not { } rich || _newRichLoadedText == null) return;
        // Anything typed in the moment before the control finished loading is an edit, and its
        // report may still be on its way: compare the text, do not trust the flag alone.
        if (!string.Equals(_newRich!.Text, _newRichLoadedText, StringComparison.Ordinal)) return;
        bool hadFocus = _newRich.IsKeyboardFocusWithin;
        LoadHtmlIntoNewRich(rich, _newRichLoadedHtml);
        if (hadFocus) _newRich.FocusEditor();
    }

    private void OnNewRichTextChanged(object? sender, EventArgs e)
    {
        if (_newRich == null) return;
        // WinUI reports a change after the fact, the load's own included; text that still is what
        // was loaded has not been edited. Formatting commands mark the body dirty themselves.
        if (_newRichLoadedText != null && string.Equals(_newRich.Text, _newRichLoadedText, StringComparison.Ordinal)) return;
        _newRichLoadedText = null;
        _vm.MarkBodyDirty();
    }

    private void OnNewRichSelectionChanged(object? sender, EventArgs e)
    {
        NoteEditingActivity();
        if (_vm.CurrentMode != ComposeMode.Html || NewRich is not { } rich) return;
        SyncParagraphStyleCombo();
        if (_suppressFormattingAnnouncement) return;

        var config = _configService.Load();
        if (config.AnnounceFormattingWhileNavigating)
        {
            var label = BlockLabel(rich.BlockAtCaret());
            if (label != _lastAnnouncedBlockType)
            {
                _lastAnnouncedBlockType = label;
                AccessibilityHelper.Announce(this, label, category: AnnouncementCategory.Result);
            }
        }
        if (!_suppressSpellingAnnouncement && !_caretMovedByTyping && config.AnnounceSpellingWhileNavigating)
            AnnounceSpellingAtCurrentPosition();
    }

    /// <summary>The island could not be created: HTML mode goes back to the WPF editor, with what was loaded.</summary>
    private void OnNewRichHostingFailed(Exception ex)
    {
        LogService.Log("The new message editor could not be hosted for HTML mode; using the standard editor", ex);
        if (_newRich is not { } host) return;
        bool hadFocus = host.IsKeyboardFocusWithin;
        host.PreviewKeyDown -= NewRich_PreviewKeyDown;
        host.Close();
        BodyEditorGrid.Children.Remove(host);
        _newRich = null;

        _suppressRichTextChanged = true;
        try { RichTextDocumentConverter.LoadInto(RichBodyBox, _newRichLoadedHtml, ResolveEditorImage); }
        finally { _suppressRichTextChanged = false; }
        RichBodyBox.Visibility = _vm.CurrentMode == ComposeMode.Html ? Visibility.Visible : Visibility.Collapsed;
        if (hadFocus) RichBodyBox.Focus();
    }

    // ── What a paragraph is called ───────────────────────────────────────────

    /// <summary>The spoken name of a paragraph's kind, as <see cref="BlockTypeLabel"/> gives for the WPF editor.</summary>
    internal static string BlockLabel(RichEditDocument.BlockInfo block)
    {
        string label;
        if (block.Heading > 0) label = $"Heading {block.Heading}";
        else if (block.Pre) label = "Code block";
        else if (block.List != RichListKind.None)
        {
            var kind = block.List == RichListKind.Numbered ? "Numbered list item" : "Bullet list item";
            label = block.ListLevel > 0 ? $"{kind}, level {block.ListLevel + 1}" : kind;
        }
        else if (block.InTable) label = "Table cell";
        else label = "Normal text";
        return BlockLabels.WithQuote(label, block.QuoteDepth);
    }

    /// <summary>The style the Paragraph style box shows for a paragraph; null for one it has no entry for.</summary>
    private static ComposeParagraphStyle? StyleOf(RichEditDocument.BlockInfo block)
    {
        if (block.Heading > 0) return (ComposeParagraphStyle)block.Heading;
        if (block.Pre) return null;
        return block.QuoteDepth > 0 ? ComposeParagraphStyle.Quote : ComposeParagraphStyle.Normal;
    }

    /// <summary>After a command changed the caret's paragraph: the box follows, and navigation does not announce it again.</summary>
    private void AfterNewRichBlockEdit(RichEditDocument rich)
    {
        _lastAnnouncedBlockType = BlockLabel(rich.BlockAtCaret());
        SyncParagraphStyleCombo();
        _vm.MarkBodyDirty();
    }

    private void EnsureNewRichFocused()
    {
        if (_newRich is { } host && !(host.HasFocus && host.IsKeyboardFocusWithin)) host.FocusWhenReady();
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    private void NewRichToggle(RichEditDocument rich, Func<bool> toggle, string name)
    {
        EnsureNewRichFocused();
        bool on = toggle();
        _vm.MarkBodyDirty();
        AnnounceFormatting(on ? $"{name} on" : $"{name} off");
    }

    private void NewRichApplyHeading(RichEditDocument rich, int level)
    {
        EnsureNewRichFocused();
        bool on = rich.ToggleHeading(level);
        AfterNewRichBlockEdit(rich);
        AnnounceFormatting(on ? $"Heading {level}" : "Normal text");
    }

    private void NewRichToggleQuote(RichEditDocument rich)
    {
        EnsureNewRichFocused();
        var (depth, turnedOff) = rich.ToggleQuote();
        AfterNewRichBlockEdit(rich);
        AnnounceFormatting(depth == 0 ? "Quote off"
            : !turnedOff && depth == 1 ? "Quote on"
            : !turnedOff ? $"Quote on, level {depth}"
            : $"Quote, level {depth}");
    }

    /// <summary>Sets one style on the selected paragraphs; never toggles, says nothing.</summary>
    private void NewRichApplyParagraphStyle(RichEditDocument rich, ComposeParagraphStyle style)
    {
        switch (style)
        {
            case ComposeParagraphStyle.Normal:
                rich.SetNormal();
                break;
            case ComposeParagraphStyle.Quote:
                rich.SetHeadingLevel(0);
                rich.SetQuoteDepth(1, onlyWhenShallower: true);
                break;
            default:
                rich.SetHeadingLevel((int)style);
                break;
        }
        AfterNewRichBlockEdit(rich);
    }

    private void NewRichToggleList(RichEditDocument rich, bool ordered)
    {
        EnsureNewRichFocused();
        bool on = rich.ToggleList(ordered);
        AfterNewRichBlockEdit(rich);
        AnnounceFormatting($"{(ordered ? "Numbered list" : "Bullet list")} {(on ? "on" : "off")}");
    }

    private void NewRichInsertLink(RichEditDocument rich)
    {
        var dialog = new InsertLinkDialog(rich.SelectedText().Trim()) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            FocusActiveEditor();
            return;
        }
        rich.InsertLink(dialog.DisplayText, dialog.Url);
        FocusActiveEditor();
        _vm.MarkBodyDirty();
        AnnounceFormatting("Link inserted");
    }

    private void NewRichClearFormatting(RichEditDocument rich)
    {
        EnsureNewRichFocused();
        rich.ClearFormatting();
        AfterNewRichBlockEdit(rich);
        AnnounceFormatting("Formatting cleared");
    }

    /// <summary>The formatting at the cursor, one fact per entry, in the order <see cref="GetFormattingParts"/> gives.</summary>
    private static List<string> NewRichFormattingParts(RichEditDocument rich)
    {
        var block = rich.BlockAtCaret();
        var (bold, italic, underline, strike, code) = rich.CharacterState();
        // A heading is bold as a whole; that is the heading, not Bold.
        if (block.Heading > 0) bold = "off";
        List<string> parts =
        [
            BlockLabel(block),
            $"Bold {bold}",
            $"Italic {italic}",
            $"Underline {underline}",
            $"Strikethrough {strike}",
        ];
        if (code && !block.Pre) parts.Add("Code on");
        if (rich.ImageAtCaret() is { } picture)
            parts.Add(picture.Info.Alt is { Length: > 0 } alt ? $"Image, {alt}" : picture.Info.AccessibleName);
        if (rich.LinkAtCaret() is { } address)
            parts.Add(address.Length == 0 ? "Link" : $"Link, {address}");
        return parts;
    }

    // ── Keys ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Keys in the hosted rich editor: everything the plain editor's handler does (typing
    /// detection for spelling, Alt+1/2/3), then Enter where the editor's own break is wrong.
    /// </summary>
    private void NewRich_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        BodyBox_PreviewKeyDown(sender, e);
        if (e.Handled || NewRich is not { } rich) return;
        if (e.Key != Key.Return || Keyboard.Modifiers != ModifierKeys.None) return;

        var result = rich.HandleEnter();
        if (result == RichEditDocument.EnterResult.NotHandled) return;
        e.Handled = true;
        _vm.MarkBodyDirty();
        var block = rich.BlockAtCaret();
        _lastAnnouncedBlockType = BlockLabel(block);
        SyncParagraphStyleCombo();
        switch (result)
        {
            case RichEditDocument.EnterResult.LeftQuote:
                AnnounceFormatting(block.QuoteDepth == 0 ? "Quote off" : BlockLabels.WithQuote("Normal text", block.QuoteDepth));
                break;
            case RichEditDocument.EnterResult.LeftCode:
                AnnounceFormatting("Normal text");
                break;
            case RichEditDocument.EnterResult.HeadingBreak when _configService.Load().AnnounceFormattingWhileNavigating:
                AccessibilityHelper.Announce(this, _lastAnnouncedBlockType, category: AnnouncementCategory.Result);
                break;
        }
    }

    /// <summary>Tab and Shift+Tab in the hosted rich editor: a list item's level, else as in the plain editor.</summary>
    private bool NewRichTab(RichEditDocument rich, bool shift)
    {
        if (rich.IndentListItem(shift ? -1 : 1))
        {
            _vm.MarkBodyDirty();
            var label = BlockLabel(rich.BlockAtCaret());
            _lastAnnouncedBlockType = label;
            AccessibilityHelper.Announce(this, label, category: AnnouncementCategory.Result);
            return true;
        }
        if (shift)
        {
            _newRich!.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous));
            return true;
        }
        _newRich!.SelectedText = ((char)9).ToString();
        _newRich.Select(_newRich.SelectionStart + 1, 0);
        return true;
    }

    // ── Templates ────────────────────────────────────────────────────────────

    /// <summary>Puts a template's lines in as plain paragraphs of their own, as one undo step.</summary>
    private void NewRichInsertText(RichEditDocument rich, string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
        rich.InsertLines(lines);
        _vm.MarkBodyDirty();
        _newRich!.Focus();
    }

    // ── Clipboard ────────────────────────────────────────────────────────────

    /// <summary>
    /// Paste in the hosted rich editor. Files and a picture on its own go the way they do
    /// everywhere else (attached, or described and put in). Formatted text comes in through
    /// the same converter a reply's HTML does, so only what QuickMail can send is kept.
    /// False pastes the clipboard's plain text.
    /// </summary>
    private bool OnNewRichPaste()
    {
        if (NewRich is not { } rich) return false;
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().Where(f => f != null).ToList();
                _ = TakeFilesIntoBodyAsync(files);
                return true;
            }
            if (Clipboard.ContainsImage() && !Clipboard.ContainsText())
            {
                if (Clipboard.GetImage() is { } bitmap)
                    QueueImage(ImageProcessing.FromBitmap(bitmap), "Pasted picture");
                return true;
            }
            if (!Clipboard.ContainsData(DataFormats.Html)) return false;
            var html = ClipboardHtml.ExtractFragment(Clipboard.GetData(DataFormats.Html) as string);
            if (html == null) return false;
            var paragraphs = RichBody.Flatten(RichTextDocumentConverter.FromHtml(html));
            // Nothing but empty paragraphs: the clipboard's text is the better copy.
            if (paragraphs.All(p => p.Runs.Count == 0)) return false;
            rich.InsertAtSelection(paragraphs);
            _vm.MarkBodyDirty();
            return true;
        }
        catch (COMException)
        {
            return false; // another program has the clipboard open
        }
    }

    /// <summary>
    /// Copy and Cut in the hosted rich editor: the selection goes on the clipboard as HTML and as
    /// plain text, written by the same code that writes a message. Pasted back into a message it
    /// keeps its formatting and its pictures.
    /// </summary>
    private bool OnNewRichCopy(bool cut)
    {
        if (NewRich is not { } rich || _newRich is not { } host) return false;
        var paragraphs = rich.ReadSelection();
        if (paragraphs.Count == 0) return false;
        try
        {
            var document = RichBody.ToDocument(paragraphs);
            var data = new DataObject();
            data.SetData(DataFormats.Html, ClipboardHtml.Build(RichTextDocumentConverter.ToHtml(document)));
            data.SetData(DataFormats.UnicodeText, RichTextDocumentConverter.ToPlainText(document));
            Clipboard.SetDataObject(data, copy: true);
        }
        catch (COMException)
        {
            return false; // leave it to the control's own copy
        }
        if (cut)
        {
            host.DeleteSelection();
            _vm.MarkBodyDirty();
        }
        return true;
    }

    // ── Pictures ─────────────────────────────────────────────────────────────

    /// <summary>Image Properties for the picture at the caret in the hosted rich editor.</summary>
    private void NewRichShowImageProperties(RichEditDocument rich)
    {
        if (rich.ImageAtCaret() is not { } picture)
        {
            AnnounceFormatting("No image at the cursor");
            return;
        }
        var (position, info) = picture;
        var dialog = new ImageDescriptionDialog(DescribeForContext(info), existing: info) { Owner = this };
        _imageDialog = dialog;
        dialog.Completed += result =>
        {
            _imageDialog = null;
            Dispatcher.BeginInvoke(ShowNextImageDialog);
            FocusActiveEditor();
            if (result is null) return;
            // The dialog is modeless: the picture may have gone, or moved, while it was open.
            var now = NewRich?.ImageAtCaret();
            if (_vm.CurrentMode != ComposeMode.Html || NewRich is not { } current
                || !(current.IsImageAt(position, info) || (now is { } found && ReferenceEquals(found.Info, info))))
            {
                AnnounceFormatting("That image is no longer in the message");
                return;
            }
            int at = current.IsImageAt(position, info) ? position : now!.Value.Position;
            if (result.Remove)
            {
                current.RemoveImage(at);
                _vm.MarkBodyDirty();
                AnnounceFormatting("Image removed");
                return;
            }
            current.ReplaceImage(at, info with { Alt = result.Alt });
            _vm.MarkBodyDirty();
            AnnounceFormatting(result.Decorative ? "Marked as decorative" : "Description changed");
        };
        dialog.Show();
    }

    /// <summary>Draws pictures that arrived after the body was loaded (a reply's or forward's quoted pictures).</summary>
    private async Task NewRichDrawArrivedPicturesAsync(RichEditDocument rich)
    {
        var sources = rich.Pictures().Select(p => p.Src).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(src => !_displayImages.TryGetValue(src, out var have) || have is null)
            .Select(src => (Src: src, Bytes: _vm.GetInlineImageBytes(src)))
            .Where(s => s.Bytes is not null)
            .ToList();
        if (sources.Count == 0) return;
        try
        {
            // From a message someone else sent: decoded off the UI thread, so a picture built to
            // be slow to decode cannot freeze the window.
            var decoded = await Task.Run(() => sources.ToDictionary(
                s => s.Src, s => (ImageSource?)ImageProcessing.ForDisplay(s.Bytes!), StringComparer.OrdinalIgnoreCase));
            if (!IsLoaded || NewRich is not { } current) return;
            foreach (var (src, source) in decoded)
                if (source is not null) _displayImages[src] = source;
            _suppressFormattingAnnouncement = true;
            try { current.RedrawPictures(info => decoded.TryGetValue(info.Src, out var s) && s is not null); }
            finally { _suppressFormattingAnnouncement = false; }
            // Redrawing is not an edit: if the body was untouched, it still is.
            if (_newRichLoadedText != null && _newRich != null) _newRichLoadedText = _newRich.Text;
        }
#pragma warning disable CA1031 // a picture that will not decode must not take the compose window down
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogService.Log("Compose: drawing arrived pictures", ex);
        }
    }
}

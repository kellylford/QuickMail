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
        _newRich is { IsVisible: true, HasFocus: true } rich ? rich
        : _newEditor is { IsVisible: true, HasFocus: true } plain ? plain
        : null;

    /// <summary>
    /// Alt combinations still come through WPF (access keys and menus need them to). WPF
    /// sometimes has no focused element while a hosted editor has focus, and then raises the key
    /// on the window alone, so the editor's Alt+1, Alt+2 and Alt+3 would never be seen. This
    /// window handler, added after the window's own, gives such a key to the editor's handler.
    /// It never moves focus: doing that from a key path took Windows' focus out of the editor.
    /// </summary>
    private void HostedFocus_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || FocusedHost is not { } host || IsFromHost(e, host)) return;
        BodyBox_PreviewKeyDown(host, e);
    }

    /// <summary>A command run from a key in the body threw. It is in the log; say that it did not work.</summary>
    private void OnBodyKeyFailed(Exception ex) =>
        AccessibilityHelper.Announce(this, "That did not work. The error is in the log.", category: AnnouncementCategory.Result);

    private DateTime _cellEdgeSaid;
    private bool _selectAllJustPressed;

    /// <summary>A key that would have joined two table cells: nothing is deleted, and it says where the caret is.</summary>
    private bool RefuseAtCellEdge(string where)
    {
        // The outcome of the key, so a Result; and once, not on every repeat of a held key.
        if ((DateTime.UtcNow - _cellEdgeSaid).TotalSeconds > 1.5)
            AccessibilityHelper.Announce(this, where, category: AnnouncementCategory.Result);
        _cellEdgeSaid = DateTime.UtcNow;
        return true;
    }

    private static bool IsFromHost(KeyEventArgs e, WinUiEditorHost host) =>
        ReferenceEquals(e.OriginalSource, host) || ReferenceEquals(e.Source, host);

    /// <summary>
    /// A key pressed in a hosted editor, before the editor sees it. Called from the thread's
    /// message filter (<see cref="WinUiEditorHost.TakeKey"/>), so it runs whatever WPF
    /// believes about focus. True takes the key. The order is what the WPF-routed path had: the
    /// window's keys and commands, then keys the editor must not act on, then Enter where the
    /// editor's own paragraph break is wrong. Alt combinations never come here.
    /// </summary>
    private bool RouteBodyKey(WinUiEditorHost host, Key key, ModifierKeys modifiers)
    {
        NoteEditingActivity();
        // Any key may be a command that reads the body (send, save, close).
        FlushNewEditorBody();
        NoteBodyKeyForSpelling(key, modifiers);

        // Was the last key Ctrl+A? (The modifier keys arrive as keys of their own.)
        bool afterSelectAll = _selectAllJustPressed;
        if (key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift))
            _selectAllJustPressed = key == Key.A && modifiers == ModifierKeys.Control;

        if (HandleWindowKey(key, modifiers)) return true;
        if (RunComposeCommand(key, modifiers)) return true;

        // A letter is about to go into the HTML body: what Ctrl+B and the like switched on for
        // it must still be on.
        if (ReferenceEquals(host, _newRich) && (IsTypingKey(key, modifiers) || key == Key.Space))
            NewRich?.EnsureTypingFormat();

        // RichEdit's own formatting keys (alignment, line spacing, lists, superscript and
        // subscript, all caps) are not commands in a message. Not one of QuickMail's commands by
        // this point, they stop here and never reach the control.
        if (WinUiEditorHost.IsRichEditFormattingKey(key, modifiers)) return true;

        if (ReferenceEquals(host, _newRich) && NewRich is { } rich)
        {
            if (key == Key.Return && modifiers == ModifierKeys.None) return NewRichEnter();

            if (key is Key.Back or Key.Delete && modifiers is ModifierKeys.None or ModifierKeys.Control)
            {
                // Deleting the whole body, after Select All, leaves it plain text, in one undo
                // step. Only after Select All: selecting the words of a one-line heading to
                // retype them selects "everything" too, and must leave it a heading.
                if (afterSelectAll && rich.EverythingIsSelected())
                {
                    rich.DeleteEverything();
                    _vm.MarkBodyDirty();
                    _lastAnnouncedBlockType = BlockLabel(rich.BlockAtCaret());
                    SyncParagraphStyleCombo();
                    return true;
                }

                // Backspace at the start of a table cell and Delete at its end, by character or
                // by word, would join two cells, changing the table's shape with nothing said.
                // They do nothing, and say where.
                int edge = rich.TableCellEdge();
                if (key == Key.Back && edge is -1 or 2) return RefuseAtCellEdge("Start of cell");
                if (key == Key.Delete && edge is 1 or 2) return RefuseAtCellEdge("End of cell");
            }
        }
        return false;
    }

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
            var spelling = new PlainTextSpelling(s_windowsSpeller, IsKnownWord);
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
        host.PreviewKeyDown += BodyBox_PreviewKeyDown; // Alt+1/2/3 only: other keys come through KeyRouter
        host.KeyRouter = (key, modifiers) => RouteBodyKey(host, key, modifiers);
        host.FocusStrayed = FocusActiveEditor;
        host.AltCombinationUsed = () => _suppressNextMenuActivation = true;
        host.KeyFailed = OnBodyKeyFailed;
        host.TabPressed = HandleBodyTab;
        host.FilesDropped += OnNewEditorFilesDropped;
        host.HostingFailed += OnNewRichHostingFailed;
        host.PasteRequested = OnNewRichPaste;
        host.CopyRequested = OnNewRichCopy;
        host.ContextMenuItemsRequested = NewEditorSpellingMenuItems;
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
            // The editor reports the caret's move to the top later, after the flag below is
            // down, and "Normal text" was then spoken as every reply opened. The kind of
            // paragraph the caret starts in is not news; moving to another kind still is.
            _lastAnnouncedBlockType = BlockLabel(rich.BlockAtCaretWithTablePosition());
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
        {
            // A body half loaded is worse than the other editor: HTML mode goes back to WPF's,
            // which loads the same HTML.
            OnNewRichHostingFailed(ex);
        }
        finally
        {
            _suppressFormattingAnnouncement = false;
        }
    }

    /// <summary>
    /// Marks the body edited if the hosted rich editor's text is no longer what was loaded. The
    /// editor's own report of a change arrives later, so anything about to ask "has this message
    /// been edited?" (closing, an auto-save) calls this first.
    /// </summary>
    private void FlushNewRichDirty()
    {
        if (_newRich == null || _newRichLoadedText == null) return;
        if (string.Equals(_newRich.Text, _newRichLoadedText, StringComparison.Ordinal)) return;
        _newRichLoadedText = null;
        _vm.MarkBodyDirty();
    }

    /// <summary>
    /// The control resets fonts and sizes when it first loads, which is after a body loaded as
    /// the window opened (a reply in HTML). If nothing has been edited, the body is written again.
    /// </summary>
    private void OnNewRichEditorLoaded(object? sender, EventArgs e)
    {
        if (NewRich is not { } rich) return;
        // Fonts only, and in place: the caret, the selection, what has been typed and what has
        // been switched on for the next typing (Ctrl+B) all stay as they are.
        _suppressFormattingAnnouncement = true;
        try { rich.MendFonts(); }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
        {
            LogService.Log("New editor: putting fonts back after the control loaded", ex);
        }
        finally { _suppressFormattingAnnouncement = false; }
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
        rich.ForgetTypingFormatIfMoved();
        SyncParagraphStyleCombo();
        if (_suppressFormattingAnnouncement) return;

        var config = _configService.Load();
        if (config.AnnounceFormattingWhileNavigating)
        {
            var label = BlockLabel(rich.BlockAtCaretWithTablePosition());
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
        bool hadFocus = host.HasFocus;
        host.PreviewKeyDown -= BodyBox_PreviewKeyDown;
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
        else if (block.InTable)
            label = block.TableRow > 0 ? $"Table cell, row {block.TableRow}, column {block.TableColumn}" : "Table cell";
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
        if (_newRich is { } host && !host.HasFocus) host.FocusWhenReady();
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
        var block = rich.BlockAtCaretWithTablePosition();
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
    /// Enter in the hosted rich editor, where the editor's own paragraph break is wrong. True
    /// when it was dealt with here.
    /// </summary>
    private bool NewRichEnter()
    {
        if (NewRich is not { } rich) return false;
        var result = rich.HandleEnter();
        if (result == RichEditDocument.EnterResult.NotHandled) return false;
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
            case RichEditDocument.EnterResult.LeftList:
                AnnounceFormatting(_lastAnnouncedBlockType ?? "Normal text");
                break;
            case RichEditDocument.EnterResult.CodeLine or RichEditDocument.EnterResult.CellLine:
                // Same block, new line: nothing has changed that needs saying.
                break;
            case RichEditDocument.EnterResult.HeadingBreak when _configService.Load().AnnounceFormattingWhileNavigating:
                AccessibilityHelper.Announce(this, _lastAnnouncedBlockType, category: AnnouncementCategory.Result);
                break;
        }
        return true;
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
        _newRich!.FocusWhenReady();
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
            var html = Clipboard.ContainsData(DataFormats.Html)
                ? ClipboardHtml.ExtractFragment(Clipboard.GetData(DataFormats.Html) as string)
                : null;
            var paragraphs = html == null ? null : RichBody.Flatten(RichTextDocumentConverter.FromHtml(html));
            // No formatted text, or nothing but empty paragraphs: the clipboard's text is the copy
            // to take. In a table cell its lines go in as lines of the cell, since a paragraph
            // break there would be a new cell; anywhere else the editor pastes it.
            if (paragraphs == null || paragraphs.All(p => p.Runs.Count == 0))
            {
                if (!rich.BlockAtCaret().InTable || !Clipboard.ContainsText()) return false;
                var lines = Clipboard.GetText().Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
                    .TrimEnd('\n').Split('\n');
                if (lines.Length < 2) return false;
                paragraphs = lines.Select(line =>
                {
                    var paragraph = new RichParagraph();
                    if (line.Length > 0) paragraph.Runs.Add(new RichRun(RichBody.Clean(line)));
                    return paragraph;
                }).ToList();
            }
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
        LogService.Debug($"New editor: {(cut ? "cut" : "copy")} of {paragraphs.Count} paragraphs.");
        if (paragraphs.Count == 0) return false;
        try
        {
            var document = RichBody.ToDocument(paragraphs);
            var data = new DataObject();
            data.SetData(DataFormats.Html, ClipboardHtml.Build(RichTextDocumentConverter.ToHtml(document)));
            data.SetData(DataFormats.UnicodeText, RichTextDocumentConverter.ToPlainText(document));
            Clipboard.SetDataObject(data, copy: true);
        }
        catch (COMException ex)
        {
            LogService.Debug($"New editor: the clipboard could not be written ({ex.Message}).");
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

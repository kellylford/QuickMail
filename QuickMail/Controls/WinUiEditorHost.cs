using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using QuickMail.Helpers;
using WinUiAutomation = Microsoft.UI.Xaml.Automation.AutomationProperties;

namespace QuickMail.Controls;

/// <summary>
/// The #1099 experiment: WinUI 3's <c>RichEditBox</c> as a plain-text editor inside a WPF window.
/// The editor lives in a WinUI XAML island (<c>DesktopWindowXamlSource</c>) whose window is a child
/// of a plain container window this <see cref="HwndHost"/> owns.
///
/// <para><b>Why:</b> the reading bugs fixed in #1096 and the typed-word problem behind the NVDA
/// Typing Fix (#300) are in WPF's own text provider. RichEditBox is the one editor that read
/// correctly with every screen reader Kelly tried.</para>
///
/// <para><b>Keys and focus</b> cross between WPF and the island in four places, each of which was a
/// real failure in the Stage 1 spike (Tools/EditorHostSpike/README.md):
/// <see cref="TranslateCharCore"/>, <see cref="OnKeyDown"/>, <see cref="FocusEditor"/> and
/// <see cref="WndProc"/>. The window's own <c>PreviewKeyDown</c> still sees every key first, with
/// this element as the focused one, so command keys work from inside the editor.</para>
///
/// <para><b>Plain text.</b> RichEditBox is a rich editor; for the Plain Text and Markdown body its
/// formatting keys are off, paste takes text only, and <see cref="Text"/> is the document's text
/// with "\r\n" line breaks.</para>
///
/// <para><b>Rich.</b> For HTML messages the same control is made with <c>rich: true</c>. The
/// formatting is then driven through <see cref="Rich"/>, paste and copy are the owner's
/// (<see cref="PasteRequested"/>, <see cref="CopyRequested"/>), and <see cref="Text"/> is the
/// text with everything that is not the user's words blanked, for spelling.</para>
/// </summary>
internal sealed class WinUiEditorHost : HwndHost, IPlainTextEditor
{
    private readonly RichEditBox _editor;
    private readonly PlainTextSpelling? _spelling;
    private IntPtr _container;
    private DesktopWindowXamlSource? _source;

    // The document's text as QuickMail sees it. Dropped on every change and read again on demand,
    // because WinUI raises TextChanged after the fact and callers read Text straight after an edit.
    private string? _text;

    /// <param name="spelling">Where the misspellings are; null leaves every spelling query empty.</param>
    /// <param name="rich">True for the HTML body: formatted text, driven through <see cref="Rich"/>.</param>
    /// <param name="resolveImage">Rich only: the picture for a <c>src</c>, or null for a placeholder.</param>
    public WinUiEditorHost(string accessibleName, PlainTextSpelling? spelling = null, bool rich = false,
        Func<string, System.Windows.Media.Imaging.BitmapSource?>? resolveImage = null)
    {
        WinUiRuntime.EnsureStarted();
        _spelling = spelling;
        _editor = new RichEditBox
        {
            AcceptsReturn = true,
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            // Plain text: no Ctrl+B / Ctrl+I / Ctrl+U of the control's own, and nothing but text
            // on the clipboard when copying.
            DisabledFormattingAccelerators = DisabledFormattingAccelerators.All,
            ClipboardCopyFormat = RichEditClipboardFormat.PlainText,
            IsSpellCheckEnabled = false,
            AllowDrop = true,
        };
        WinUiAutomation.SetName(_editor, accessibleName);
        // The control's own menus offer Bold, Italic and Underline, which a plain-text body has
        // no use for and would lose on send. The pop-up over a selection goes; the context menu
        // (Shift+F10, the Applications key) keeps the clipboard commands only.
        _editor.SelectionFlyout = null;
        _editor.ContextFlyout = BuildContextMenu();
        _editor.TextChanging += (_, _) => _text = null;
        _editor.TextChanged += (_, _) =>
        {
            _text = null;
            TextChanged?.Invoke(this, EventArgs.Empty);
        };
        _editor.SelectionChanged += (_, _) => SelectionChanged?.Invoke(this, EventArgs.Empty);
        _editor.Loaded += (_, _) => EditorLoaded?.Invoke(this, EventArgs.Empty);
        // Tab reaches the island before WPF sees it, and would move focus out of the editor.
        _editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Tab || TabPressed == null) return;
            var modifiers = Keyboard.Modifiers;
            if (modifiers != ModifierKeys.None && modifiers != ModifierKeys.Shift) return;
            if (TabPressed(modifiers == ModifierKeys.Shift)) e.Handled = true;
        };
        _editor.Paste += OnPaste;
        _editor.DragOver += OnDragOver;
        _editor.Drop += OnDrop;
        if (rich)
        {
            Rich = new RichEditDocument(_editor, resolveImage ?? (_ => null));
            _editor.CopyingToClipboard += (_, e) => { if (CopyRequested?.Invoke(false) == true) e.Handled = true; };
            _editor.CuttingToClipboard += (_, e) => { if (CopyRequested?.Invoke(true) == true) e.Handled = true; };
        }
        Focusable = true;
    }

    /// <summary>The formatted body, when this editor was made for HTML messages; otherwise null.</summary>
    public RichEditDocument? Rich { get; }

    /// <summary>
    /// Rich only. Paste was asked for (Ctrl+V, the context menu). Return true to say it has been
    /// dealt with; false pastes the clipboard's plain text.
    /// </summary>
    public Func<bool>? PasteRequested { get; set; }

    /// <summary>
    /// Rich only. Copy (false) or Cut (true) was asked for. Return true to say it has been dealt
    /// with, and the control's own copy is skipped.
    /// </summary>
    public Func<bool, bool>? CopyRequested { get; set; }

    /// <summary>Takes the selection out, as Cut does once its content is on the clipboard.</summary>
    public void DeleteSelection() => _editor.Document.Selection.SetText(TextSetOptions.None, string.Empty);

    /// <summary>
    /// The text changed, by the user or the program, <see cref="SetText"/> included. WinUI raises
    /// it after the fact, from its own queue, so a listener compares texts and does not count on
    /// a flag raised around the call that made the change.
    /// </summary>
    public event EventHandler? TextChanged;

    public event EventHandler? SelectionChanged;

    /// <summary>
    /// The control has loaded in its island. When it does, it sets every character to its own
    /// font and size, so formatted text written before this (a code font, a heading's size) is
    /// written again by the owner.
    /// </summary>
    public event EventHandler? EditorLoaded;

    /// <summary>
    /// Tab (false) or Shift+Tab (true) was pressed in the editor. Return true to take the key;
    /// otherwise it moves focus out of the editor, as Tab does anywhere else.
    /// </summary>
    public Func<bool, bool>? TabPressed { get; set; }

    /// <summary>Files were dropped on the editor.</summary>
    public event Action<IReadOnlyList<string>>? FilesDropped;

    /// <summary>
    /// The island could not be created when the window was first laid out, which is after the
    /// constructor has succeeded. The owner puts its other editor back: this element is empty.
    /// </summary>
    public event Action<Exception>? HostingFailed;

    /// <summary>
    /// Lets go of the owner when its window has closed. WPF disposes an HwndHost only when its
    /// dispatcher shuts down, so without this every closed message would keep its editor, its
    /// window and its view model alive until QuickMail exits.
    ///
    /// <para>The island itself is released earlier, while the container window is being destroyed
    /// (<see cref="WndProc"/>, WM_DESTROY). Disposing it here, after the window is gone, ended
    /// the process with an access violation.</para>
    /// </summary>
    public void Close()
    {
        TextChanged = null;
        SelectionChanged = null;
        EditorLoaded = null;
        FilesDropped = null;
        HostingFailed = null;
        TabPressed = null;
        PasteRequested = null;
        CopyRequested = null;
        Dispose();
    }

    private void ReleaseIsland()
    {
        if (_source == null) return;
        var source = _source;
        _source = null;
        source.TakeFocusRequested -= OnTakeFocusRequested;
        try
        {
            source.Dispose();
        }
        catch (Exception ex) when (ex is COMException or ObjectDisposedException or InvalidOperationException)
        {
            Services.LogService.Debug($"Releasing the editor island: {ex.Message}");
        }
    }

    /// <summary>
    /// True while the editor's context menu is open. WPF sees Escape before the island does and
    /// knows nothing of a WinUI menu, so the owner asks before treating Escape as "close".
    /// </summary>
    public bool IsMenuOpen { get; private set; }

    private MenuFlyout BuildContextMenu()
    {
        var menu = new MenuFlyout();
        menu.Opened += (_, _) => IsMenuOpen = true;
        menu.Closed += (_, _) => IsMenuOpen = false;
        void Add(string text, Action run)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => run();
            menu.Items.Add(item);
        }
        Add("Cut", () => _editor.Document.Selection.Cut());
        Add("Copy", () => _editor.Document.Selection.Copy());
        Add("Paste", Paste);
        Add("Select All", () => Select(0, Text.Length));
        return menu;
    }

    // ── IPlainTextEditor ─────────────────────────────────────────────────────

    public string Text
    {
        get
        {
            if (_text == null)
            {
                string? raw;
                if (Rich != null) raw = Rich.MaskedText();
                else _editor.Document.GetText(TextGetOptions.None, out raw);
                _text = RichEditOffsets.ToText(raw ?? string.Empty);
            }
            return _text;
        }
    }

    /// <summary>
    /// Replaces the whole text: loading a draft, a reply or a template that sets the body. Clears
    /// the undo history, as assigning a WPF TextBox's Text does.
    /// </summary>
    public void SetText(string text)
    {
        _editor.Document.SetText(TextSetOptions.None, RichEditOffsets.ToRaw(text ?? string.Empty));
        _text = null;
    }

    public int SelectionStart
    {
        get => RichEditOffsets.ToIndex(Text, _editor.Document.Selection.StartPosition);
        set => Select(value, SelectionLength);
    }

    public int SelectionLength
    {
        get
        {
            var text = Text;
            var selection = _editor.Document.Selection;
            return Math.Max(0, RichEditOffsets.ToIndex(text, selection.EndPosition)
                               - RichEditOffsets.ToIndex(text, selection.StartPosition));
        }
        set => Select(SelectionStart, value);
    }

    /// <summary>The start of the selection, as <c>TextBox.CaretIndex</c> is.</summary>
    public int CaretIndex
    {
        get => SelectionStart;
        set => Select(value, 0);
    }

    public string SelectedText
    {
        get
        {
            int start = SelectionStart, length = SelectionLength;
            return length > 0 ? Text.Substring(start, Math.Min(length, Text.Length - start)) : string.Empty;
        }
        set
        {
            _editor.Document.Selection.SetText(TextSetOptions.None, RichEditOffsets.ToRaw(value ?? string.Empty));
            _text = null;
        }
    }

    public void Select(int start, int length)
    {
        var text = Text;
        start = Math.Clamp(start, 0, text.Length);
        int end = Math.Clamp(start + Math.Max(0, length), start, text.Length);
        _editor.Document.Selection.SetRange(RichEditOffsets.ToPosition(text, start), RichEditOffsets.ToPosition(text, end));
    }

    public void ScrollTo(int index)
    {
        int position = RichEditOffsets.ToPosition(Text, index);
        _editor.Document.GetRange(position, position).ScrollIntoView(PointOptions.None);
    }

    public int GetNextSpellingErrorCharacterIndex(int charIndex, LogicalDirection direction) =>
        _spelling?.NextErrorStart(Text, charIndex, direction == LogicalDirection.Forward) ?? -1;

    public int GetSpellingErrorStart(int charIndex) => _spelling?.ErrorAt(Text, charIndex)?.Start ?? -1;

    public int GetSpellingErrorLength(int charIndex) => _spelling?.ErrorAt(Text, charIndex)?.Length ?? 0;

    public IReadOnlyList<string>? GetSpellingSuggestions(int charIndex)
    {
        if (_spelling?.ErrorAt(Text, charIndex) is not { } error) return null;
        return _spelling.Suggestions(Text.Substring(error.Start, error.Length));
    }

    // ── Appearance ───────────────────────────────────────────────────────────

    /// <summary>Windows' wavy underlines under misspellings.</summary>
    public bool IsSpellCheckEnabled
    {
        get => _editor.IsSpellCheckEnabled;
        set => _editor.IsSpellCheckEnabled = value;
    }

    /// <summary>Reported to screen readers as the editor's shortcut key, e.g. "Alt+Y".</summary>
    public void SetAcceleratorKey(string? key) => WinUiAutomation.SetAcceleratorKey(_editor, key ?? string.Empty);

    public void SetFont(string familyName, double size)
    {
        _editor.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(familyName);
        if (size > 0) _editor.FontSize = size;
    }

    /// <summary>
    /// QuickMail's theme colors. The editor's brushes are WinUI theme resources, so they are
    /// overridden on the control, and its theme is flipped to make it look them up again.
    /// </summary>
    public void ApplyColors(System.Windows.Media.Color background, System.Windows.Media.Color foreground,
        System.Windows.Media.Color border, System.Windows.Media.Color accent)
    {
        static Microsoft.UI.Xaml.Media.SolidColorBrush Brush(System.Windows.Media.Color c) =>
            new(Windows.UI.Color.FromArgb(c.A, c.R, c.G, c.B));

        var resources = _editor.Resources;
        foreach (var state in new[] { "", "PointerOver", "Focused", "Disabled" })
        {
            resources["TextControlBackground" + state] = Brush(background);
            resources["TextControlForeground" + state] = Brush(foreground);
            resources["TextControlBorderBrush" + state] = Brush(state == "Focused" ? accent : border);
        }
        resources["TextControlSelectionHighlightColor"] = Brush(accent);

        // The control's own parts (scroll bar, context menu) follow its light or dark theme.
        double luminance = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255;
        var theme = luminance < 0.5 ? Microsoft.UI.Xaml.ElementTheme.Dark : Microsoft.UI.Xaml.ElementTheme.Light;
        _editor.RequestedTheme = theme == Microsoft.UI.Xaml.ElementTheme.Dark
            ? Microsoft.UI.Xaml.ElementTheme.Light : Microsoft.UI.Xaml.ElementTheme.Dark;
        _editor.RequestedTheme = theme;
    }

    // ── Clipboard and drop ───────────────────────────────────────────────────

    /// <summary>Paste takes the clipboard's text only: this is a plain-text editor.</summary>
    private void OnPaste(object sender, TextControlPasteEventArgs e)
    {
        e.Handled = true;
        Paste();
    }

    private void Paste()
    {
        if (PasteRequested?.Invoke() == true) return;
        PasteText();
    }

    private void PasteText()
    {
        string text;
        try
        {
            if (!Clipboard.ContainsText()) return;
            text = Clipboard.GetText();
        }
        catch (COMException)
        {
            return; // another program has the clipboard open
        }
        if (text.Length == 0) return;
        SelectedText = text;
        int end = SelectionStart + SelectionLength;
        Select(end, 0);
    }

    /// <summary>
    /// Files may be dropped (the owner attaches them). Anything else is refused: left to the
    /// control, dragged text arrives with its formatting and a dragged picture as an object, and
    /// neither survives in a plain-text body.
    /// </summary>
    private static void OnDragOver(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        bool files = e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems);
        e.AcceptedOperation = files
            ? Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy
            : Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        e.Handled = true;
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            if (paths.Count > 0) FilesDropped?.Invoke(paths);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            Services.LogService.Log("Dropping files on the message body failed", ex);
        }
        finally
        {
            deferral.Complete();
        }
    }

    // ── Hosting ──────────────────────────────────────────────────────────────

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _container = CreateWindowEx(0, "Static", "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
            0, 0, 100, 100, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_container == IntPtr.Zero)
            throw new InvalidOperationException("The message editor's container window could not be created.");
        try
        {
            _source = new DesktopWindowXamlSource();
            _source.Initialize(new WindowId((ulong)_container));
            _source.Content = _editor;
            _source.TakeFocusRequested += OnTakeFocusRequested;
            Resize();
        }
#pragma warning disable CA1031 // whatever WinUI throws here, the window must get its other editor back, not lose its body
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // This runs at the window's first layout, long after the constructor returned, so the
            // owner's fallback has to be asked for. The empty container is still a valid child.
            ReleaseIsland();
            var failed = HostingFailed;
            Dispatcher.BeginInvoke(() => failed?.Invoke(ex));
        }
        return new HandleRef(this, _container);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        // Usually a no-op by now: the window's closing destroyed the container already, and
        // WM_DESTROY released the island. This is the path for an element removed from a live
        // window (the fallback after a hosting failure).
        if (IsWindow(hwnd.Handle))
        {
            ReleaseIsland();
            DestroyWindow(hwnd.Handle);
        }
        _source = null;
        _container = IntPtr.Zero;
    }

    /// <summary>Tab or Shift+Tab out of the island: carry on through WPF's tab order.</summary>
    private void OnTakeFocusRequested(DesktopWindowXamlSource sender, DesktopWindowXamlSourceTakeFocusRequestedEventArgs e)
    {
        var direction = e.Request.Reason == XamlSourceFocusNavigationReason.Last
            ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next;
        Dispatcher.BeginInvoke(() => MoveFocus(new TraversalRequest(direction)));
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Resize();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Resize();
    }

    /// <summary>The island is sized in physical pixels.</summary>
    private void Resize()
    {
        if (_source?.SiteBridge == null) return;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        _source.SiteBridge.MoveAndResize(new Windows.Graphics.RectInt32(0, 0,
            Math.Max(1, (int)(ActualWidth * dpi.DpiScaleX)), Math.Max(1, (int)(ActualHeight * dpi.DpiScaleY))));
    }

    /// <summary>
    /// Puts focus in the editor. Deferred, so it runs after WPF has finished moving its own focus
    /// to this element, which puts Win32 focus on the container window.
    /// <c>NavigateFocus(Programmatic)</c> alone left focus on the island's pane, and after a dialog
    /// closes WinUI still counts the editor as focused, so <c>Focus</c> alone does nothing: Win32
    /// focus goes to the island's window first.
    /// </summary>
    public void FocusEditor() => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
    {
        // Queued before the window closed: there is nothing left to focus. Closing destroys the
        // container (a child window) before WPF disposes this element, and the island's bridge
        // goes with it while _source is still set.
        if (_container == IntPtr.Zero || !IsWindow(_container)) return;
        if (_source?.SiteBridge is not { } bridge) return;
        SetFocus(Win32Interop.GetWindowFromWindowId(bridge.WindowId));
        if (!_editor.Focus(Microsoft.UI.Xaml.FocusState.Keyboard))
            _source.NavigateFocus(new XamlSourceFocusNavigationRequest(XamlSourceFocusNavigationReason.First));
    });

    /// <summary>
    /// Gives this editor keyboard focus, now if it can take it, or as soon as it has been laid
    /// out. An editor made a moment ago (HTML mode's, on the first switch to it) is not yet in
    /// the window's layout, and WPF refuses focus to an element it has not arranged; without
    /// this WPF would go on counting the editor it replaced as the focused one.
    /// </summary>
    public void FocusWhenReady()
    {
        if (TryFocus()) return;
        // Not laid out yet: try again each time layout settles, a few times at most.
        int attempts = 0;
        EventHandler? retry = null;
        retry = (_, _) =>
        {
            if (TryFocus() || ++attempts >= 20) LayoutUpdated -= retry;
        };
        LayoutUpdated += retry;
    }

    private bool TryFocus()
    {
        if (!IsVisible || _source == null) return false;
        if (!Focus()) return false;
        FocusEditor();
        return true;
    }

    /// <summary>
    /// True when Windows' keyboard focus is in this editor. This is the truth to go by: WPF's
    /// own record of the focused element is sometimes empty while the island has focus (seen
    /// after the window is activated again), and then <c>IsKeyboardFocusWithin</c> is false and
    /// WPF sends its key events to the window, not to this element.
    /// </summary>
    public bool HasFocus
    {
        get
        {
            if (_container == IntPtr.Zero) return false;
            var focus = GetFocus();
            return focus != IntPtr.Zero && (focus == _container || IsChild(_container, focus));
        }
    }

    protected override bool TabIntoCore(TraversalRequest request)
    {
        // Through WPF's own focus, so it knows this element is the focused one;
        // OnGotKeyboardFocus then puts focus in the editor.
        if (!Focus()) FocusEditor();
        return true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (e.NewFocus == this) FocusEditor();
    }

    /// <summary>
    /// Whenever Win32 focus arrives on the container (WPF restoring focus after a dialog, or the
    /// window activated again), pass it on to the editor. The container itself must never hold
    /// focus: a screen reader would be left on a nameless pane.
    /// </summary>
    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_SETFOCUS && hwnd == _container) FocusEditor();
        // The container is going (its window is closing). Release the island now, while the
        // container and the island's own window still exist.
        else if (msg == WM_DESTROY && hwnd == _container) ReleaseIsland();
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    /// <summary>
    /// Typed characters belong to the editor. Without this, WPF offers every WM_CHAR to its access
    /// keys first: typing "t" in the body moved focus to the To field. Alt+letter (WM_SYSCHAR) is
    /// left alone, so access keys and menus still work from the body.
    /// </summary>
    protected override bool TranslateCharCore(ref MSG msg, ModifierKeys modifiers)
    {
        if (msg.message is WM_CHAR or WM_DEADCHAR)
        {
            DispatchMessage(ref msg);
            return true;
        }
        return base.TranslateCharCore(ref msg, modifiers);
    }

    /// <summary>
    /// The caret keys and Enter belong to the editor, with any modifier but Alt. WPF raises its own
    /// key events before the hosted window gets the message, and its keyboard navigation takes
    /// Ctrl+arrow as "move focus that way": Ctrl+Down in the body moved focus to the Send button.
    /// By the time the event bubbles to here the window's PreviewKeyDown (the command keys) has had
    /// its turn, so the message goes straight to the editor.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        HandleKeyDown(e);
        if (!e.Handled) base.OnKeyDown(e);
    }

    /// <summary>
    /// What this element does with a key on its way up from the editor. Public because the
    /// owning window calls it too, for a key WPF routed to the window while the editor had
    /// focus (see <see cref="HasFocus"/>).
    /// </summary>
    public void HandleKeyDown(KeyEventArgs e)
    {
        // RichEdit's own formatting keys (alignment, line spacing, lists, superscript and
        // subscript, all caps) are not commands in a plain-text body. Unhandled by QuickMail's
        // commands by this point, they stop here and never reach the control.
        if (!e.Handled && IsRichEditFormattingKey(e.Key, Keyboard.Modifiers))
        {
            e.Handled = true;
            return;
        }

        // With the editor's context menu open, every key is the menu's. Left to bubble, Escape
        // reached the window's Cancel button (IsCancel) and closed the message.
        bool editorKey = IsMenuOpen || e.Key is Key.Left or Key.Right or Key.Up or Key.Down
            or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Enter;
        if (editorKey && !e.Handled && (Keyboard.Modifiers & ModifierKeys.Alt) == 0)
        {
            var msg = ComponentDispatcher.CurrentKeyboardMessage;
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
            e.Handled = true;
        }
    }

    internal static bool IsRichEditFormattingKey(Key key, ModifierKeys modifiers)
    {
        if (modifiers == ModifierKeys.Control)
            return key is Key.E or Key.L or Key.R or Key.J or Key.D1 or Key.D2 or Key.D5 or Key.OemPlus;
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            return key is Key.L or Key.A or Key.OemPlus or Key.OemComma or Key.OemPeriod;
        return false;
    }

    private const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000;
    private const int WM_DESTROY = 0x0002;
    private const int WM_SETFOCUS = 0x0007, WM_CHAR = 0x0102, WM_DEADCHAR = 0x0103;

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, int style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

    [DllImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32")]
    private static extern IntPtr SetFocus(IntPtr hwnd);

    [DllImport("user32")]
    private static extern IntPtr GetFocus();

    [DllImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr parent, IntPtr child);

    [DllImport("user32")]
    private static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG msg);
}

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
/// <para><b>Plain text.</b> RichEditBox is a rich editor; here its formatting keys are off, paste
/// takes text only, and <see cref="Text"/> is the document's text with "\r\n" line breaks.</para>
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
    public WinUiEditorHost(string accessibleName, PlainTextSpelling? spelling = null)
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
        _editor.TextChanging += (_, _) => _text = null;
        _editor.TextChanged += (_, _) =>
        {
            _text = null;
            TextChanged?.Invoke(this, EventArgs.Empty);
        };
        _editor.SelectionChanged += (_, _) => SelectionChanged?.Invoke(this, EventArgs.Empty);
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
        Focusable = true;
    }

    /// <summary>
    /// The text changed, by the user or the program, <see cref="SetText"/> included. WinUI raises
    /// it after the fact, from its own queue, so a listener compares texts and does not count on
    /// a flag raised around the call that made the change.
    /// </summary>
    public event EventHandler? TextChanged;

    public event EventHandler? SelectionChanged;

    /// <summary>
    /// Tab (false) or Shift+Tab (true) was pressed in the editor. Return true to take the key;
    /// otherwise it moves focus out of the editor, as Tab does anywhere else.
    /// </summary>
    public Func<bool, bool>? TabPressed { get; set; }

    /// <summary>Files were dropped on the editor.</summary>
    public event Action<IReadOnlyList<string>>? FilesDropped;

    // ── IPlainTextEditor ─────────────────────────────────────────────────────

    public string Text
    {
        get
        {
            if (_text == null)
            {
                _editor.Document.GetText(TextGetOptions.None, out var raw);
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

    private static void OnDragOver(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        e.Handled = true;
    }

    private async void OnDrop(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;
        e.Handled = true;
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
        _source = new DesktopWindowXamlSource();
        _source.Initialize(new WindowId((ulong)_container));
        _source.Content = _editor;
        _source.TakeFocusRequested += OnTakeFocusRequested;
        Resize();
        return new HandleRef(this, _container);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (_source != null)
        {
            _source.TakeFocusRequested -= OnTakeFocusRequested;
            _source.Dispose();
            _source = null;
        }
        DestroyWindow(hwnd.Handle);
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
        bool editorKey = e.Key is Key.Left or Key.Right or Key.Up or Key.Down
            or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Enter;
        if (editorKey && !e.Handled && (Keyboard.Modifiers & ModifierKeys.Alt) == 0)
        {
            var msg = ComponentDispatcher.CurrentKeyboardMessage;
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000;
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
    private static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG msg);
}

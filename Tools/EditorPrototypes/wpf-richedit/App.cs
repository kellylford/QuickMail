// Prototype for QuickMail #1099: a compose-like window whose message body is one of:
//
//     RichEditProto.exe wpf        WPF TextBox, as WPF ships it
//     RichEditProto.exe richedit   RichEdit through Windows Forms (WinForms RichTextBox in a WindowsFormsHost)
//     RichEditProto.exe native     RichEdit hosted directly (RICHEDIT50W from msftedit.dll in an HwndHost),
//                                  with no Windows Forms layer in between
//
// Build 3: both RichEdit bodies take Ctrl+B, Ctrl+I and Ctrl+U (RichEdit has no built-in keys for
// them), and every body starts with the same first line, formatted in the RichEdit ones, so
// format reporting can be tried without typing. A "To" box comes first, for Tab and Shift+Tab.
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Input;
using System.Windows.Interop;

static class Program
{
    const string Before = "This line has a ", BoldWord = "bold", Middle = " word and an ", ItalicWord = "italic", After = " word.";

    [STAThread]
    static void Main(string[] args)
    {
        System.Windows.Forms.Application.EnableVisualStyles();
        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "wpf";
        var app = new Application();
        var window = new Window
        {
            Title = mode switch { "richedit" => "Prototype - RichEdit body", "notepad" => "Prototype - Notepad RichEdit body", "native" => "Prototype - native RichEdit body", _ => "Prototype - WPF body" },
            Width = 700, Height = 500, WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var toLabel = new Label { Content = "_To:", Margin = new Thickness(4, 4, 4, 0) };
        var to = new TextBox { Margin = new Thickness(4, 0, 4, 4) };
        toLabel.Target = to;
        AutomationProperties.SetName(to, "To");
        Grid.SetRow(toLabel, 0);
        Grid.SetRow(to, 1);
        grid.Children.Add(toLabel);
        grid.Children.Add(to);

        UIElement body;
        Action focusBody;
        switch (mode)
        {
            case "richedit":
            case "notepad":
            {
                var rich = mode == "notepad" ? new NotepadRichTextBox() : new System.Windows.Forms.RichTextBox();
                rich.Multiline = true;
                rich.AcceptsTab = false;
                rich.WordWrap = true;
                rich.DetectUrls = false;
                rich.ScrollBars = System.Windows.Forms.RichTextBoxScrollBars.Vertical;
                rich.AccessibleName = "Message body";
                rich.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
                rich.Font = new System.Drawing.Font("Segoe UI", 11f);
                FillWinForms(rich);
                rich.KeyDown += (_, e) =>
                {
                    if (!e.Control || e.Alt || e.Shift) return;
                    var style = e.KeyCode switch
                    {
                        System.Windows.Forms.Keys.B => System.Drawing.FontStyle.Bold,
                        System.Windows.Forms.Keys.I => System.Drawing.FontStyle.Italic,
                        System.Windows.Forms.Keys.U => System.Drawing.FontStyle.Underline,
                        _ => System.Drawing.FontStyle.Regular,
                    };
                    if (style == System.Drawing.FontStyle.Regular) return;
                    var font = rich.SelectionFont ?? rich.Font;
                    rich.SelectionFont = new System.Drawing.Font(font, font.Style ^ style);
                    e.SuppressKeyPress = true;
                    e.Handled = true;
                };
                var host = new WindowsFormsHost { Child = rich, Margin = new Thickness(4) };
                body = host;
                focusBody = () => { host.Focus(); rich.Focus(); };
                break;
            }
            case "native":
            {
                var host = new NativeRichEdit { Margin = new Thickness(4) };
                body = host;
                focusBody = host.FocusEditor;
                break;
            }
            default:
            {
                var box = new TextBox
                {
                    AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 15,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(4),
                    Text = Before + BoldWord + Middle + ItalicWord + After + Environment.NewLine,
                };
                box.CaretIndex = box.Text.Length;
                AutomationProperties.SetName(box, "Message body");
                body = box;
                focusBody = () => box.Focus();
                break;
            }
        }
        Grid.SetRow(body, 2);
        grid.Children.Add(body);
        window.Content = grid;
        window.Loaded += (_, _) => focusBody();
        app.Run(window);
    }

    /// <summary>
    /// The RichEdit variant Windows 11 Notepad is built on: the same msftedit.dll, window class
    /// "RichEditD2DPT" (Direct2D drawing, the current text services) instead of the "RichEdit50W"
    /// Windows Forms creates by default. Windows Forms still does the keyboard and focus work.
    /// </summary>
    sealed class NotepadRichTextBox : System.Windows.Forms.RichTextBox
    {
        protected override System.Windows.Forms.CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams; // loads msftedit.dll
                cp.ClassName = "RichEditD2DPT";
                return cp;
            }
        }
    }

    static void FillWinForms(System.Windows.Forms.RichTextBox rich)
    {
        void Add(string text, System.Drawing.FontStyle style)
        {
            rich.SelectionStart = rich.TextLength;
            rich.SelectionLength = 0;
            rich.SelectionFont = new System.Drawing.Font(rich.Font, style);
            rich.SelectedText = text;
        }
        Add(Before, System.Drawing.FontStyle.Regular);
        Add(BoldWord, System.Drawing.FontStyle.Bold);
        Add(Middle, System.Drawing.FontStyle.Regular);
        Add(ItalicWord, System.Drawing.FontStyle.Italic);
        Add(After + "\n", System.Drawing.FontStyle.Regular);
        rich.SelectionStart = rich.TextLength;
    }

    /// <summary>
    /// RichEdit 5 (msftedit.dll's RICHEDIT50W) as a child window of the WPF window, with no Windows
    /// Forms layer: the control's own accessibility is what screen readers get. Its name is set with
    /// Dynamic Annotation (IAccPropServices), the supported way to name a window without one.
    /// </summary>
    sealed class NativeRichEdit : HwndHost
    {
        IntPtr _edit;

        public void FocusEditor()
        {
            if (_edit != IntPtr.Zero) Win32.SetFocus(_edit);
        }

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            Win32.LoadLibrary("Msftedit.dll");
            _edit = Win32.CreateWindowEx(0, "RICHEDIT50W", "",
                Win32.WS_CHILD | Win32.WS_VISIBLE | Win32.WS_VSCROLL | Win32.WS_TABSTOP | Win32.WS_BORDER |
                Win32.ES_MULTILINE | Win32.ES_AUTOVSCROLL | Win32.ES_WANTRETURN,
                0, 0, 100, 100, hwndParent.Handle, (IntPtr)100, IntPtr.Zero, IntPtr.Zero);
            // Plain-text paste would be the email default; keep rich so formatting can be tried.
            SetFormat(Win32.SCF_ALL, 0, 0, 220, "Segoe UI");
            Append(Before, 0);
            Append(BoldWord, Win32.CFE_BOLD);
            Append(Middle, 0);
            Append(ItalicWord, Win32.CFE_ITALIC);
            Append(After + "\r", 0);
            Win32.SetName(_edit, "Message body");
            return new HandleRef(this, _edit);
        }

        void Append(string text, int effects)
        {
            int end = Win32.SendMessage(_edit, Win32.WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero).ToInt32();
            Win32.SendMessage(_edit, Win32.EM_SETSEL, (IntPtr)end, (IntPtr)end);
            SetFormat(Win32.SCF_SELECTION, Win32.CFM_BOLD | Win32.CFM_ITALIC | Win32.CFM_UNDERLINE, effects, 0, null);
            Win32.SendMessage(_edit, Win32.EM_REPLACESEL, IntPtr.Zero, text);
        }

        void SetFormat(int scope, int mask, int effects, int twips, string? face)
        {
            var cf = new Win32.CHARFORMAT2W { cbSize = Marshal.SizeOf<Win32.CHARFORMAT2W>(), dwMask = mask, dwEffects = effects };
            if (twips > 0) { cf.dwMask |= Win32.CFM_SIZE; cf.yHeight = twips; }
            if (face != null) { cf.dwMask |= Win32.CFM_FACE; cf.szFaceName = face; }
            Win32.SendMessage(_edit, Win32.EM_SETCHARFORMAT, (IntPtr)scope, ref cf);
        }

        void Toggle(int mask)
        {
            var cf = new Win32.CHARFORMAT2W { cbSize = Marshal.SizeOf<Win32.CHARFORMAT2W>() };
            Win32.SendMessage(_edit, Win32.EM_GETCHARFORMAT, (IntPtr)Win32.SCF_SELECTION, ref cf);
            bool on = (cf.dwEffects & mask) != 0 && (cf.dwMask & mask) != 0;
            SetFormat(Win32.SCF_SELECTION, mask, on ? 0 : mask, 0, null);
        }

        protected override void DestroyWindowCore(HandleRef hwnd) => Win32.DestroyWindow(hwnd.Handle);

        protected override bool TabIntoCore(TraversalRequest request)
        {
            FocusEditor();
            return true;
        }

        protected override bool TranslateAcceleratorCore(ref MSG msg, ModifierKeys modifiers)
        {
            if (msg.message != Win32.WM_KEYDOWN) return base.TranslateAcceleratorCore(ref msg, modifiers);
            int key = msg.wParam.ToInt32();
            if (key == Win32.VK_TAB && (modifiers & ModifierKeys.Control) == 0)
            {
                // Tab and Shift+Tab leave the body, as in the other two.
                var direction = (modifiers & ModifierKeys.Shift) != 0 ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next;
                return ((IKeyboardInputSink)this).KeyboardInputSite.OnNoMoreTabStops(new TraversalRequest(direction));
            }
            if (modifiers == ModifierKeys.Control)
            {
                int mask = key switch { 'B' => Win32.CFM_BOLD, 'I' => Win32.CFM_ITALIC, 'U' => Win32.CFM_UNDERLINE, _ => 0 };
                if (mask != 0) { Toggle(mask); return true; }
            }
            return base.TranslateAcceleratorCore(ref msg, modifiers);
        }
    }
}

static class Win32
{
    public const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_VSCROLL = 0x00200000, WS_TABSTOP = 0x00010000, WS_BORDER = 0x00800000;
    public const int ES_MULTILINE = 0x0004, ES_AUTOVSCROLL = 0x0040, ES_WANTRETURN = 0x1000;
    public const int WM_KEYDOWN = 0x0100, WM_GETTEXTLENGTH = 0x000E, VK_TAB = 0x09;
    public const int EM_SETSEL = 0x00B1, EM_REPLACESEL = 0x00C2, EM_SETCHARFORMAT = 0x0444, EM_GETCHARFORMAT = 0x043A;
    public const int SCF_SELECTION = 0x0001, SCF_ALL = 0x0004;
    public const int CFM_BOLD = 0x1, CFM_ITALIC = 0x2, CFM_UNDERLINE = 0x4, CFM_SIZE = unchecked((int)0x80000000), CFM_FACE = 0x20000000;
    public const int CFE_BOLD = 0x1, CFE_ITALIC = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct CHARFORMAT2W
    {
        public int cbSize, dwMask, dwEffects, yHeight, yOffset, crTextColor;
        public byte bCharSet, bPitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szFaceName;
        public short wWeight, sSpacing;
        public int crBackColor, lcid, dwReserved;
        public short sStyle, wKerning;
        public byte bUnderlineType, bAnimation, bRevAuthor, bUnderlineColor;
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern IntPtr LoadLibrary(string name);
    [DllImport("user32", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32")] public static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32")] public static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32")] public static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, string l);
    [DllImport("user32")] public static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, ref CHARFORMAT2W l);

    // Dynamic Annotation: IAccPropServices.SetHwndPropStr names a window for MSAA and UI Automation.
    static readonly Guid PROPID_ACC_NAME = new("608d3df8-8128-4aa7-a428-f55e49267291");
    const int OBJID_CLIENT = unchecked((int)0xFFFFFFFC), CHILDID_SELF = 0;

    [ComImport, Guid("6E26E776-04F0-495D-80E4-3330352E3169"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAccPropServices
    {
        void SetPropValue(IntPtr pIDString, int dwIDStringLen, Guid idProp, object var);
        void SetPropServer(IntPtr pIDString, int dwIDStringLen, IntPtr paProps, int cProps, IntPtr pServer, int annoScope);
        void ClearProps(IntPtr pIDString, int dwIDStringLen, IntPtr paProps, int cProps);
        void SetHwndProp(IntPtr hwnd, int idObject, int idChild, Guid idProp, object var);
        void SetHwndPropStr(IntPtr hwnd, int idObject, int idChild, Guid idProp, [MarshalAs(UnmanagedType.LPWStr)] string str);
    }

    [ComImport, Guid("b5f8350b-0548-48b1-a6ee-88bd00b4a5e7")] class CAccPropServices { }

    public static void SetName(IntPtr hwnd, string name)
    {
        try { ((IAccPropServices)new CAccPropServices()).SetHwndPropStr(hwnd, OBJID_CLIENT, CHILDID_SELF, PROPID_ACC_NAME, name); }
        catch (Exception) { /* unnamed is still testable */ }
    }
}

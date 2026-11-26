using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.UI.Text;

namespace EditorHostSpike;

/// <summary>
/// Shaped like QuickMail's compose window: a menu, To and Subject, the body, and Send. Every way of
/// moving focus or running a command that QuickMail relies on is here, to see whether it still works
/// while focus is in the hosted WinUI editor.
/// </summary>
sealed class ComposeWindow : Window
{
    readonly TextBox _to = new() { Margin = new Thickness(4, 0, 4, 4) };
    readonly TextBox _subject = new() { Margin = new Thickness(4, 0, 4, 4) };
    readonly RichEditBoxHost _body = new("Message body") { Margin = new Thickness(4) };
    readonly TextBlock _status = new() { Margin = new Thickness(4) };

    public ComposeWindow()
    {
        Title = "Editor host spike - New Message";
        Width = 800;
        Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        AutomationProperties.SetName(_to, "To");
        AutomationProperties.SetName(_subject, "Subject");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);

        var menu = new Menu();
        var file = new MenuItem { Header = "_File" };
        file.Items.Add(Item("_Send", "Ctrl+Enter", (_, _) => Send()));
        file.Items.Add(Item("Open a _dialog", "Ctrl+D", (_, _) => OpenDialog()));
        file.Items.Add(new Separator());
        file.Items.Add(Item("_Close", "Escape", (_, _) => Close()));
        var format = new MenuItem { Header = "F_ormat" };
        format.Items.Add(Item("_Bold", "Ctrl+B", (_, _) => ToggleBold()));
        var view = new MenuItem { Header = "_View" };
        view.Items.Add(Item("_Light theme", "", (_, _) => ApplyTheme("Light")));
        view.Items.Add(Item("_Dark theme", "", (_, _) => ApplyTheme("Dark")));
        view.Items.Add(Item("_Parchment theme", "", (_, _) => ApplyTheme("Parchment")));
        view.Items.Add(new Separator());
        view.Items.Add(Item("_Command palette", "Ctrl+Shift+P", (_, _) => OpenPalette()));
        menu.Items.Add(file);
        menu.Items.Add(format);
        menu.Items.Add(view);

        var toLabel = new Label { Content = "_To:", Target = _to, Margin = new Thickness(4, 4, 4, 0) };
        var subjectLabel = new Label { Content = "S_ubject:", Target = _subject, Margin = new Thickness(4, 4, 4, 0) };
        var send = new Button { Content = "_Send", Margin = new Thickness(4), Padding = new Thickness(12, 2, 12, 2), HorizontalAlignment = HorizontalAlignment.Left };
        send.Click += (_, _) => Send();

        var grid = new Grid();
        for (int i = 0; i < 6; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = i == 4 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Place(grid, toLabel, 0); Place(grid, _to, 1); Place(grid, subjectLabel, 2); Place(grid, _subject, 3);
        Place(grid, _body, 4); Place(grid, send, 5); Place(grid, _status, 6);

        var dock = new DockPanel();
        DockPanel.SetDock(menu, Dock.Top);
        dock.Children.Add(menu);
        dock.Children.Add(grid);
        Content = dock;

        // QuickMail-style keys: Alt+Y to the body, F6 between the fields, Ctrl+Enter to send,
        // Ctrl+D for a dialog, Escape to close.
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) =>
        {
            var doc = _body.Editor.Document;
            doc.SetText(TextSetOptions.None, "This line has a bold word and an italic word.\r");
            doc.GetRange(16, 20).CharacterFormat.Bold = FormatEffect.On;
            doc.GetRange(33, 39).CharacterFormat.Italic = FormatEffect.On;
            doc.Selection.SetRange(46, 46);
            _to.Focus();
        };
    }

    static void Place(Grid grid, UIElement element, int row)
    {
        Grid.SetRow(element, row);
        grid.Children.Add(element);
    }

    static MenuItem Item(string header, string key, RoutedEventHandler click)
    {
        var item = new MenuItem { Header = header, InputGestureText = key };
        AutomationProperties.SetAcceleratorKey(item, key);
        item.Click += click;
        return item;
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.Alt && key == Key.Y) { _body.Focus(); e.Handled = true; }
        else if (mods == ModifierKeys.Control && key == Key.Enter) { Send(); e.Handled = true; }
        else if (mods == ModifierKeys.Control && key == Key.D) { OpenDialog(); e.Handled = true; }
        else if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.P) { OpenPalette(); e.Handled = true; }
        // Escape closes an open menu first; only otherwise the window.
        else if (mods == ModifierKeys.None && key == Key.Escape && Keyboard.FocusedElement is not MenuItem) { Close(); e.Handled = true; }
        else if (key == Key.F6 && (mods == ModifierKeys.None || mods == ModifierKeys.Shift))
        {
            CycleFields(mods == ModifierKeys.Shift ? -1 : 1);
            e.Handled = true;
        }
    }

    void CycleFields(int step)
    {
        UIElement[] fields = { _to, _subject, _body };
        int at = Array.FindIndex(fields, f => f.IsKeyboardFocusWithin);
        var next = fields[(at + step + fields.Length) % fields.Length];
        next.Focus();
    }

    void Send()
    {
        _body.Editor.Document.GetText(TextGetOptions.None, out var text);
        Say($"Send pressed. The body has {text.TrimEnd('\r').Length} characters.");
    }

    void ToggleBold()
    {
        var format = _body.Editor.Document.Selection.CharacterFormat;
        format.Bold = format.Bold == FormatEffect.On ? FormatEffect.Off : FormatEffect.On;
        _body.FocusEditor();
    }

    void OpenDialog()
    {
        bool bodyHadFocus = _body.IsKeyboardFocusWithin;
        MessageBox.Show(this, "A dialog over the compose window. Close it to go back.", "Spike dialog");
        if (bodyHadFocus) _body.FocusEditor();
    }

    /// <summary>
    /// A modeless WPF window with a list, like QuickMail's command palette. Enter runs the chosen
    /// command, Escape closes; either way focus goes back to where it was, the body included.
    /// </summary>
    void OpenPalette()
    {
        bool bodyHadFocus = _body.IsKeyboardFocusWithin;
        var before = Keyboard.FocusedElement;
        var list = new ListBox { Margin = new Thickness(4) };
        AutomationProperties.SetName(list, "Commands");
        var commands = new (string Title, Action Run)[]
        {
            ("Send", Send), ("Bold", ToggleBold),
            ("Light theme", () => ApplyTheme("Light")), ("Dark theme", () => ApplyTheme("Dark")),
            ("Parchment theme", () => ApplyTheme("Parchment")),
        };
        foreach (var c in commands) list.Items.Add(c.Title);
        list.SelectedIndex = 0;
        var palette = new Window
        {
            Title = "Command palette", Owner = this, Width = 320, Height = 240, Content = list,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        Action? chosen = null;
        palette.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { palette.Close(); e.Handled = true; }
            else if (e.Key == Key.Enter && list.SelectedIndex >= 0)
            {
                chosen = commands[list.SelectedIndex].Run;
                palette.Close();
                e.Handled = true;
            }
        };
        palette.Loaded += (_, _) => (list.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
        palette.Closed += (_, _) =>
        {
            Activate();
            if (bodyHadFocus) _body.FocusEditor(); else before?.Focus();
            chosen?.Invoke();
        };
        palette.Show();
    }

    /// <summary>
    /// Light and Dark use WinUI's own themes. Parchment sets explicit colors, as QuickMail's own
    /// themes would need: the editor's brushes are theme resources, overridden on the control.
    /// </summary>
    void ApplyTheme(string name)
    {
        var (back, fore) = name switch
        {
            "Dark" => (System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20), System.Windows.Media.Color.FromRgb(0xF0, 0xF0, 0xF0)),
            "Parchment" => (System.Windows.Media.Color.FromRgb(0xF4, 0xEC, 0xD8), System.Windows.Media.Color.FromRgb(0x3B, 0x2F, 0x20)),
            _ => (System.Windows.Media.Colors.White, System.Windows.Media.Colors.Black),
        };
        Background = new System.Windows.Media.SolidColorBrush(back);
        Foreground = new System.Windows.Media.SolidColorBrush(fore);

        var editor = _body.Editor;
        var resources = editor.Resources;
        resources.Clear();
        if (name == "Parchment")
        {
            var b = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, back.R, back.G, back.B));
            var f = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, fore.R, fore.G, fore.B));
            foreach (var state in new[] { "", "PointerOver", "Focused", "Disabled" })
            {
                resources["TextControlBackground" + state] = b;
                resources["TextControlForeground" + state] = f;
            }
        }
        // Flip the theme so the control looks its brushes up again.
        var target = name == "Dark" ? Microsoft.UI.Xaml.ElementTheme.Dark : Microsoft.UI.Xaml.ElementTheme.Light;
        editor.RequestedTheme = target == Microsoft.UI.Xaml.ElementTheme.Dark ? Microsoft.UI.Xaml.ElementTheme.Light : Microsoft.UI.Xaml.ElementTheme.Dark;
        editor.RequestedTheme = target;
        Say($"{name} theme.");
    }

    void Say(string text)
    {
        _status.Text = text;
        if (UIElementAutomationPeerFor(_status) is { } peer)
            peer.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

    static System.Windows.Automation.Peers.AutomationPeer? UIElementAutomationPeerFor(UIElement element) =>
        System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(element)
        ?? System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(element);
}

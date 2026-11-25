// Prototype for QuickMail #1099, build 4: the message body as WinUI 3's RichEditBox, in a small
// standalone WinUI window with the same To box and first line as the WPF prototype. Ctrl+B,
// Ctrl+I and Ctrl+U are the RichEditBox's own.
using System;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(callbackParams =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }
}

public sealed class App : Application, Microsoft.UI.Xaml.Markup.IXamlMetadataProvider
{
    Window? _window;

    // With no App.xaml, the app supplies WinUI's control metadata itself, or creating the
    // controls' styles (XamlControlsResources) fails at start-up.
    readonly Microsoft.UI.Xaml.XamlTypeInfo.XamlControlsXamlMetaDataProvider _metadata = new();
    public Microsoft.UI.Xaml.Markup.IXamlType GetXamlType(Type type) => _metadata.GetXamlType(type);
    public Microsoft.UI.Xaml.Markup.IXamlType GetXamlType(string fullName) => _metadata.GetXamlType(fullName);
    public Microsoft.UI.Xaml.Markup.XmlnsDefinition[] GetXmlnsDefinitions() => _metadata.GetXmlnsDefinitions();

    static readonly string ErrorLog = System.IO.Path.Combine(AppContext.BaseDirectory, "startup-error.txt");

    public App()
    {
        UnhandledException += (_, e) => System.IO.File.AppendAllText(ErrorLog, "Unhandled: " + e.Exception + Environment.NewLine);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try { Launch(); }
        catch (Exception e)
        {
            System.IO.File.AppendAllText(ErrorLog, "OnLaunched: " + e + Environment.NewLine);
            throw;
        }
    }

    void Launch()
    {
        Resources.MergedDictionaries.Add(new XamlControlsResources());

        var to = new TextBox { Header = "To:", Margin = new Thickness(8, 8, 8, 4) };
        AutomationProperties.SetName(to, "To");

        var body = new RichEditBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 4, 8, 8) };
        AutomationProperties.SetName(body, "Message body");
        Fill(body);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(to, 0);
        Grid.SetRow(body, 1);
        grid.Children.Add(to);
        grid.Children.Add(body);

        _window = new Window { Title = "Prototype - WinUI RichEditBox body", Content = grid };
        _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 650));
        grid.Loaded += (_, _) => body.Focus(FocusState.Programmatic);
        _window.Activate();
    }

    static void Fill(RichEditBox box)
    {
        var doc = box.Document;
        doc.SetText(TextSetOptions.None, "This line has a bold word and an italic word.\r");
        var text = "This line has a bold word and an italic word.";
        int bold = text.IndexOf("bold", StringComparison.Ordinal);
        int italic = text.IndexOf("italic", StringComparison.Ordinal);
        doc.GetRange(bold, bold + 4).CharacterFormat.Bold = FormatEffect.On;
        doc.GetRange(italic, italic + 6).CharacterFormat.Italic = FormatEffect.On;
        int end = text.Length + 1;
        doc.Selection.SetRange(end, end);
    }
}

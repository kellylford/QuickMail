using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace QuickMail.Helpers;

/// <summary>
/// Makes Space and Shift+Space in every <see cref="TextBoxBase"/> arrive as an ordinary typed
/// character, so a screen reader's typed-word echo sees the space that ends each word (issue #300).
///
/// <para>WPF's text editor binds Space and Shift+Space to an internal editing command. The
/// command inserts the space itself and marks the KeyDown handled, and a handled KeyDown is
/// never passed to <c>TranslateMessage</c>, so Windows never generates the <c>WM_CHAR 0x20</c>
/// that a screen reader watching the message stream relies on to end a word. Letters still
/// produce WM_CHAR, so the screen reader hears one long word, spoken only when a punctuation
/// mark finally arrives ("hellothisisatest"). Measured in the test VM: no WM_CHAR for Space in
/// multiline, rich and single-line WPF boxes, with or without spell check or the input method.
/// This is nvaccess/nvda#16952 and affects every WPF app.</para>
///
/// <para>The fix gives each editor its own <see cref="KeyBinding"/> for those gestures to
/// <see cref="ApplicationCommands.NotACommand"/>. Instance bindings are consulted before class
/// bindings, and NotACommand stops the search without handling the key, so KeyDown stays
/// unhandled, WM_CHAR is generated, and the space is inserted by the normal text-input path, the
/// same way as a letter.</para>
///
/// <para>Controlled by <c>ConfigModel.NvdaTypingFix</c>, applied live: <see cref="SetEnabled"/>
/// updates every editor already loaded, and a class handler on <c>Loaded</c> covers each new one.</para>
/// </summary>
public static class TypedSpaceFix
{
    private static readonly (Key Key, ModifierKeys Modifiers)[] Gestures =
    [
        (Key.Space, ModifierKeys.None),
        (Key.Space, ModifierKeys.Shift),
    ];

    private static bool _registered;

    /// <summary>Whether editors get the bindings. Set through <see cref="SetEnabled"/>.</summary>
    public static bool Enabled { get; private set; }

    /// <summary>
    /// Registers the class handler that applies the current setting to each editor as it loads.
    /// Call once, on the UI thread, before any window is created. Safe to call again.
    /// </summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        EventManager.RegisterClassHandler(typeof(TextBoxBase), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => { if (s is TextBoxBase box) Apply(box, Enabled); }));
    }

    /// <summary>
    /// Turns the fix on or off and applies that to every editor in every open window.
    /// </summary>
    public static void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        var app = Application.Current;
        if (app is null) return;
        foreach (Window window in app.Windows)
            ApplyToTree(window, enabled);
    }

    /// <summary>
    /// Adds the bindings to one editor, or removes the ones this class added. Never touches a
    /// binding it did not add, and never adds a second copy.
    /// </summary>
    public static void Apply(TextBoxBase box, bool enabled)
    {
        foreach (var (key, modifiers) in Gestures)
        {
            var existing = Find(box, key, modifiers);
            if (enabled && existing is null)
                box.InputBindings.Add(new KeyBinding(ApplicationCommands.NotACommand, key, modifiers));
            else if (!enabled && existing is not null)
                box.InputBindings.Remove(existing);
        }
    }

    /// <summary>Whether the editor currently carries the fix for both gestures.</summary>
    public static bool IsApplied(TextBoxBase box) =>
        Gestures.All(g => Find(box, g.Key, g.Modifiers) is not null);

    private static KeyBinding? Find(TextBoxBase box, Key key, ModifierKeys modifiers) =>
        box.InputBindings.OfType<KeyBinding>().FirstOrDefault(b =>
            b.Command == ApplicationCommands.NotACommand && b.Key == key && b.Modifiers == modifiers);

    private static void ApplyToTree(DependencyObject root, bool enabled)
    {
        if (root is TextBoxBase box) Apply(box, enabled);
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
            ApplyToTree(VisualTreeHelper.GetChild(root, i), enabled);
    }
}

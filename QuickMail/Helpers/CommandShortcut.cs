using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using QuickMail.Services;

namespace QuickMail.Helpers;

/// <summary>
/// Shows a registered command's shortcut on the control that runs it, taken from the
/// <see cref="ICommandRegistry"/> so a key rebound in Settings is shown everywhere (#849).
///
/// <para>
/// <c>CommandShortcut.Id="mail.reply"</c> on a <see cref="MenuItem"/> or a button sets two things:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="MenuItem.InputGestureText"/>, the text drawn at the right of a
///   menu item. That is all it is: WPF's <c>MenuItemAutomationPeer</c> never reports it, so a
///   screen reader is not told the shortcut from it.</description></item>
///   <item><description><see cref="AutomationProperties.AcceleratorKeyProperty"/>, the UI Automation
///   property a screen reader reads after the name ("Reply R Ctrl+R"). Whether it is spoken is the
///   user's own screen reader setting, so the app has no switch of its own for it.</description></item>
/// </list>
///
/// <para>
/// <c>CommandShortcut.ToolTip="Reply"</c> adds the visible tooltip "Reply (Ctrl+R)", written each time it
/// opens. Its text sits in a <see cref="Border"/>, never directly in the tooltip: WPF reports a
/// tooltip's plain text as the control's help text when it has none of its own, and a toolbar button
/// was read as "New Message button New Message (Ctrl+N) Ctrl+N". A Border has no plain text, so the
/// help text stays empty even while the tooltip is open, which keyboard focus also does. Where the
/// tooltip says more than the name, give that as <c>AutomationProperties.HelpText</c>, without the key.
/// </para>
///
/// <para>
/// The registry is found through <c>CommandShortcut.Registry</c>, which each window sets on itself
/// and which every element inside inherits. The compose and message windows have their own
/// registries. A context menu is not inside its window's tree, so a menu item there uses the
/// registry of the control the menu was opened from. Everything is re-read whenever the element
/// is loaded (each time a menu opens) and whenever the registry raises
/// <see cref="ICommandRegistry.BindingsChanged"/> while it is. A change that arrives while the
/// element's window is not active waits for the window to be activated again: the View Manager
/// rebinds keys from inside its own ShowDialog loop, and the parent window's controls must not be
/// changed while a modal loop runs over them (CLAUDE.md, Modal Dialog Rules).
/// </para>
/// </summary>
public static class CommandShortcut
{
    public static readonly DependencyProperty RegistryProperty = DependencyProperty.RegisterAttached(
        "Registry", typeof(ICommandRegistry), typeof(CommandShortcut),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits, OnRegistryChanged));

    public static ICommandRegistry? GetRegistry(DependencyObject d) => (ICommandRegistry?)d.GetValue(RegistryProperty);
    public static void SetRegistry(DependencyObject d, ICommandRegistry? value) => d.SetValue(RegistryProperty, value);

    public static readonly DependencyProperty IdProperty = DependencyProperty.RegisterAttached(
        "Id", typeof(string), typeof(CommandShortcut), new PropertyMetadata(null, OnIdChanged));

    public static string? GetId(DependencyObject d) => (string?)d.GetValue(IdProperty);
    public static void SetId(DependencyObject d, string? value) => d.SetValue(IdProperty, value);

    public static readonly DependencyProperty ToolTipProperty = DependencyProperty.RegisterAttached(
        "ToolTip", typeof(string), typeof(CommandShortcut), new PropertyMetadata(null, OnToolTipChanged));

    public static string? GetToolTip(DependencyObject d) => (string?)d.GetValue(ToolTipProperty);
    public static void SetToolTip(DependencyObject d, string? value) => d.SetValue(ToolTipProperty, value);

    private static readonly DependencyProperty SubscriptionProperty = DependencyProperty.RegisterAttached(
        "Subscription", typeof(Subscription), typeof(CommandShortcut), new PropertyMetadata(null));

    private sealed class Subscription(ICommandRegistry registry, EventHandler handler)
    {
        public ICommandRegistry Registry { get; } = registry;
        public EventHandler Handler { get; } = handler;

        // Set while a change waits for the window to be activated.
        public Window? WaitingOn { get; set; }
        public EventHandler? OnActivated { get; set; }
    }

    /// <summary>"Reply (Ctrl+R)", or just the title when the command has no shortcut.</summary>
    public static string FormatToolTip(string title, string gesture) =>
        string.IsNullOrEmpty(gesture) ? title : $"{title} ({gesture})";

    /// <summary>The shortcut shown for <paramref name="element"/> now, or empty if it has none.</summary>
    public static string CurrentGesture(DependencyObject element)
    {
        var id = GetId(element);
        if (string.IsNullOrEmpty(id)) return string.Empty;
        return ResolveRegistry(element)?.GetGestureText(id) ?? string.Empty;
    }

    /// <summary>Re-reads the shortcut from the registry and shows it on <paramref name="element"/>.</summary>
    public static void Refresh(DependencyObject element)
    {
        if (string.IsNullOrEmpty(GetId(element)) || ResolveRegistry(element) is null) return;

        var gesture = CurrentGesture(element);
        if (element is MenuItem item)
            item.InputGestureText = gesture;
        AutomationProperties.SetAcceleratorKey(element, gesture);
        if (element is FrameworkElement fe)
            UpdateToolTip(fe);
    }

    private static ICommandRegistry? ResolveRegistry(DependencyObject element)
    {
        if (GetRegistry(element) is { } own) return own;

        // A context menu lives in its own popup, outside the window's tree, so nothing is
        // inherited into it. Its items take the registry of the control it was opened on.
        for (DependencyObject? d = element; d is not null; d = LogicalTreeHelper.GetParent(d))
        {
            if (d is ContextMenu menu)
                return menu.PlacementTarget is { } target ? GetRegistry(target) : null;
        }
        return null;
    }

    private static void OnIdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement fe)
        {
            fe.Loaded -= OnLoaded;
            fe.Unloaded -= OnUnloaded;
            fe.Loaded += OnLoaded;
            fe.Unloaded += OnUnloaded;
        }
        Refresh(d);
    }

    private static void OnRegistryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(GetId(d))) return;
        Refresh(d);
        if (d is FrameworkElement { IsLoaded: true } fe)
            Subscribe(fe);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var fe = (FrameworkElement)sender;
        Refresh(fe);
        Subscribe(fe);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e) => Unsubscribe((FrameworkElement)sender);

    // Subscribed only while loaded, so a window's elements never keep a longer-lived registry
    // holding them, and a context menu's items let go when it closes.
    private static void Subscribe(FrameworkElement fe)
    {
        Unsubscribe(fe);
        if (ResolveRegistry(fe) is not { } registry) return;

        Subscription? subscription = null;
        EventHandler handler = (_, _) =>
        {
            if (fe.Dispatcher.CheckAccess()) RefreshWhenActive(fe, subscription!);
            else fe.Dispatcher.BeginInvoke(() => RefreshWhenActive(fe, subscription!));
        };
        subscription = new Subscription(registry, handler);
        registry.BindingsChanged += handler;
        fe.SetValue(SubscriptionProperty, subscription);
    }

    // See the class remarks: no change to a window that a modal dialog is running over. A context
    // menu's items have no window of their own and change at once; the menu is open and in front.
    private static void RefreshWhenActive(FrameworkElement fe, Subscription s)
    {
        var window = Window.GetWindow(fe);
        if (window is null || window.IsActive)
        {
            Refresh(fe);
            return;
        }
        if (s.WaitingOn is not null) return;

        s.WaitingOn = window;
        s.OnActivated = (_, _) =>
        {
            StopWaiting(s);
            Refresh(fe);
        };
        window.Activated += s.OnActivated;
    }

    private static void StopWaiting(Subscription s)
    {
        if (s.WaitingOn is { } window && s.OnActivated is { } onActivated)
            window.Activated -= onActivated;
        s.WaitingOn = null;
        s.OnActivated = null;
    }

    private static void Unsubscribe(FrameworkElement fe)
    {
        if (fe.GetValue(SubscriptionProperty) is Subscription s)
        {
            s.Registry.BindingsChanged -= s.Handler;
            StopWaiting(s);
            fe.ClearValue(SubscriptionProperty);
        }
    }

    private static void OnToolTipChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;

        fe.ToolTipOpening -= OnToolTipOpening;
        if (e.NewValue is null)
        {
            fe.ClearValue(FrameworkElement.ToolTipProperty);
            return;
        }

        // The text inside a Border: see the class remarks on help text.
        if (fe.ToolTip is not ToolTip { Content: Border { Child: TextBlock } })
            fe.ToolTip = new ToolTip { Content = new Border { Child = new TextBlock() } };
        UpdateToolTip(fe);
        fe.ToolTipOpening += OnToolTipOpening;
    }

    private static void OnToolTipOpening(object sender, ToolTipEventArgs e) => UpdateToolTip((FrameworkElement)sender);

    /// <summary>Writes the tooltip's text with the key the command has now.</summary>
    public static void UpdateToolTip(FrameworkElement fe)
    {
        if (fe.ToolTip is ToolTip { Content: Border { Child: TextBlock text } })
            text.Text = FormatToolTip(GetToolTip(fe) ?? string.Empty, CurrentGesture(fe));
    }
}

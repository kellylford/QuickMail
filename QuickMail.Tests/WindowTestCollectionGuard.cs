using System;
using System.Reflection;
using System.Threading;
using System.Windows;
using Xunit;
using Xunit.v3;

// Applied to the whole assembly, so every test is checked, including ones written after this.
[assembly: QuickMail.Tests.WindowTestCollectionGuard]

namespace QuickMail.Tests;

/// <summary>
/// Fails any test that shows a window from a collection that runs alongside the "WpfTests"
/// collection.
///
/// <para><b>Why.</b> xUnit runs test collections in parallel, and a class without a
/// <c>[Collection]</c> is a collection of its own. The focus and tab-order tests in "WpfTests" read
/// <see cref="System.Windows.Input.Keyboard.FocusedElement"/>, walk <c>MoveFocus</c>, or open
/// context menus, and every one of those needs the test's window to keep Windows' keyboard focus.
/// All test threads share one process, so a window shown on ANY other test thread at the same
/// moment can take the foreground; the WpfTests window is then deactivated, WPF drops its keyboard
/// focus to null, and a context menu closes. The test that happened to be running fails with
/// "focus is null", a tab walk that stops part-way, or a menu that never opened. It was a different
/// test each time, about one CI run in five, and a rerun passed. Classes that showed activated
/// windows outside the collection were the cause (SoundTutorialTests, AddressSuggestionTests,
/// TreeViewItemRealizerTests); the rate rose when SoundTutorialTests landed.</para>
///
/// <para>Keeping every window-showing test in "WpfTests" (which runs its classes one at a time),
/// or in a collection with <c>DisableParallelization = true</c> (which runs on its own), means only
/// one test at a time can own a window. This attribute enforces that deterministically: it does not
/// wait for the race to happen, it fails the test that showed the window in the wrong place, every
/// run, naming the fix.</para>
///
/// <para>A window counts once it is loaded, which is what <c>Show()</c> does; constructing one
/// without showing it, as the XAML parse tests do, is fine.</para>
/// </summary>
public sealed class WindowTestCollectionGuardAttribute : BeforeAfterTestAttribute
{
    private static int _registered;

    /// <summary>Set on the thread that loaded a window during the current test.</summary>
    [ThreadStatic] private static bool _windowLoaded;

    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
        {
            EventManager.RegisterClassHandler(
                typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((_, _) => _windowLoaded = true),
                handledEventsToo: true);
        }
        _windowLoaded = false;
    }

    public override void After(MethodInfo methodUnderTest, IXunitTest test)
    {
        if (!_windowLoaded) return;
        _windowLoaded = false;

        if (RunsAloneOrInWpfTests(test.TestCase.TestCollection.CollectionDefinition)) return;

        throw new InvalidOperationException(
            $"{test.TestCase.TestClassName}.{methodUnderTest.Name} showed a window, but its class is not in " +
            "the \"WpfTests\" collection. Add [Collection(\"WpfTests\")] to the class. A window shown from a " +
            "collection that runs in parallel with WpfTests takes keyboard focus from whichever focus test is " +
            "running there, and that test fails at random (see WindowTestCollectionGuard.cs).");
    }

    /// <summary>
    /// True for the WpfTests collection, and for a collection that runs on its own
    /// (<c>DisableParallelization = true</c>), since nothing else is running to lose focus to it.
    /// </summary>
    internal static bool RunsAloneOrInWpfTests(Type? collectionDefinition)
    {
        if (collectionDefinition is null) return false;
        if (collectionDefinition == typeof(WpfTestsCollection)) return true;
        return collectionDefinition.GetCustomAttribute<CollectionDefinitionAttribute>()?.DisableParallelization == true;
    }
}

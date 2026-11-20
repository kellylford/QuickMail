using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using QuickMail.Helpers;

namespace QuickMail.Tests;

/// <summary>
/// Installs the app-wide input behaviour that <c>App.OnStartup</c> installs, before any test runs.
/// <see cref="ContextMenuInitialFocus"/> is a class handler, so once any test installs it, it stays
/// for the rest of the run. Installed by whichever test happened to run first, every other test
/// that opens a menu would see production behaviour or not depending on test order. Installed
/// here, every one of them sees what the app does.
/// </summary>
internal static class AppWideInputBehaviour
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification = "This is the test assembly, not a library anyone references: the initializer exists to give every test the app's startup state regardless of the order tests run in.")]
    internal static void Install() => ContextMenuInitialFocus.Install();
}

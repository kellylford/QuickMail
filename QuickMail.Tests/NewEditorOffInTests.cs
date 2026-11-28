using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using QuickMail.Views;

namespace QuickMail.Tests;

/// <summary>
/// Keeps every compose window in this test run on the WPF editors (#1099). The new message
/// editor is on by default in the app, but these tests drive <c>BodyBox</c> and
/// <c>RichBodyBox</c> by name, which is still exactly what runs with the setting off, and a test
/// host is no place to start WinUI: it would load the Windows App SDK into the process for every
/// compose test. The new editor itself is checked in the test VM, where it can be driven and
/// listened to.
/// </summary>
internal static class NewEditorOffInTests
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification = "This is the test assembly, not a library anyone references: the initializer gives every test the same editor regardless of the order tests run in.")]
    internal static void Install() => ComposeWindow.NewEditorAllowed = false;
}

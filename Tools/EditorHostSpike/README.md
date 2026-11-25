# Editor host spike (#1099, Stage 1)

**State: written, never built or run.** Started 2026-11-25 and stopped at Kelly's request, so a new
session can pick it up. Nothing here is part of `QuickMail.sln`, and nothing on this branch merges to
main without Kelly's decision.

## What it is

A compose-shaped WPF window (menu, To, Subject, body, Send, status line) whose message body is
WinUI 3's `RichEditBox`, hosted in a WinUI XAML island (`DesktopWindowXamlSource`) inside an
`HwndHost`, which is how QuickMail would host it. It follows Microsoft's Islands sample,
WindowsAppSDK-Samples `Samples/Islands/cs-winforms-unpackaged`:

- `Program.cs` creates a dispatcher queue on the UI thread and a WinUI `Application` (`XamlApp`, which
  supplies `XamlControlsXamlMetaDataProvider` and `XamlControlsResources`). It also passes every
  message through `ContentPreTranslateMessage` (from `Microsoft.UI.Windowing.Core.dll`) via
  `ComponentDispatcher.ThreadFilterMessage` before WPF sees it.
- `RichEditBoxHost.cs` is the `HwndHost`. A plain "Static" child window is the island's parent.
  Focus goes in with `NavigateFocus` (`TabIntoCore`, `OnGotKeyboardFocus`, `FocusEditor()`) and
  comes out on `TakeFocusRequested` (WPF `MoveFocus`). The site bridge is sized in physical pixels.
- `ComposeWindow.cs` adds QuickMail-style keys: Alt+Y to the body, F6/Shift+F6 between To, Subject
  and body, Ctrl+Enter to send, Ctrl+D to open a dialog (focus should come back to the body),
  Escape to close, menu access keys, and Format > Bold.

## What Stage 1 has to show

- Tab and Shift+Tab into and out of the body, and F6.
- Alt+Y, the Alt+letter menu and label access keys, Ctrl+Enter and Escape, all while focus is in
  the body.
- Focus coming back to the body after the dialog.
- Screen readers treating the body as part of the window, named "Message body".
- High Contrast and dark themes.
- Packaging: a single-file exe and a Velopack installer, x64 and Arm64, with the size measured.
  WinUI may not support single-file; find out and measure.

## Things already learned the hard way

- **WinUI's control styles need the app's `.pri` file beside the exe.** The standalone prototype's
  `dotnet publish` left `<AssemblyName>.pri` out, and the app died with "Cannot locate resource
  from ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml". Copying it from `bin\` fixed it.
  `EnableMsixTooling` is set here to try to make publish include it. Check.
- **A WinUI app with no App.xaml must provide `IXamlMetadataProvider`** with
  `XamlControlsXamlMetaDataProvider`, or creating `XamlControlsResources` fails at start-up.
- **Hosting RichEdit directly in an `HwndHost` without the keyboard plumbing broke Enter**, and letters
  triggered WPF access keys (the prototype's "Direct RichEdit body"). The island version relies on
  `ContentPreTranslateMessage` to avoid that. Check Enter and plain letters first.
- See `Tools/EditorPrototypes/` for the earlier prototypes' source and the NVDA steps files, and #1099
  for the results.

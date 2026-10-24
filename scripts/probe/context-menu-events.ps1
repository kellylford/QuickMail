# Records what an assistive technology is told when a context menu opens from the keyboard
# in the real app: every UI Automation focus change and menu opened/closed event, with time,
# while real keystrokes are sent. Throwaway diagnostic for the #693 / #861 question; runs on a
# CI runner (no screen reader) rather than anyone's own desktop.
param(
    [Parameter(Mandatory)] [string]$Exe,
    [Parameter(Mandatory)] [string]$ProfileDir,
    [Parameter(Mandatory)] [string]$OutDir
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null
$log = Join-Path $OutDir 'uia-events.log'

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Kb {
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint f, int x, int y, uint d, UIntPtr e);
    const uint UP = 2;
    public static void Down(byte vk) { keybd_event(vk, 0, 0, UIntPtr.Zero); }
    public static void Up(byte vk)   { keybd_event(vk, 0, UP, UIntPtr.Zero); }
    public static void Press(byte vk) { Down(vk); Up(vk); }
    public static void Chord(byte mod, byte vk) { Down(mod); Press(vk); Up(mod); }
    public static void Click(int x, int y) { SetCursorPos(x, y); mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero); }
}
'@
$VK_SHIFT = 0x10; $VK_CONTROL = 0x11; $VK_F10 = 0x79; $VK_APPS = 0x5D; $VK_ESC = 0x1B
$VK_DOWN = 0x28; $VK_3 = 0x33

# The UIA callbacks arrive on UI Automation's own threads, where a PowerShell scriptblock has no
# runspace and silently never runs, so the recorder is compiled C#.
Add-Type -ReferencedAssemblies UIAutomationClient, UIAutomationTypes, WindowsBase @'
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows.Automation;
public static class Rec {
    public static readonly Stopwatch Clock = Stopwatch.StartNew();
    public static readonly ConcurrentQueue<string> Lines = new ConcurrentQueue<string>();
    public static void Note(string text) { Lines.Enqueue(string.Format("{0,8:N0} ms  {1}", Clock.Elapsed.TotalMilliseconds, text)); }
    static string Describe(object o) {
        var el = o as AutomationElement;
        if (el == null) return "(not an element)";
        try {
            var c = el.Current;
            string sel = "";
            object p;
            if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out p) && ((SelectionItemPattern)p).Current.IsSelected) sel = " [selected]";
            return string.Format("{0} \"{1}\" id={2} class={3} pid={4}{5}",
                c.ControlType.ProgrammaticName.Replace("ControlType.", ""), c.Name, c.AutomationId, c.ClassName, c.ProcessId, sel);
        } catch (Exception ex) { return "(element gone: " + ex.GetType().Name + ")"; }
    }
    public static void Start() {
        Automation.AddAutomationFocusChangedEventHandler((s, e) => Note("FOCUS        " + Describe(s)));
        foreach (var ev in new[] { AutomationElement.MenuOpenedEvent, AutomationElement.MenuClosedEvent })
            Automation.AddAutomationEventHandler(ev, AutomationElement.RootElement, TreeScope.Subtree,
                (s, e) => Note(string.Format("{0,-12} ", e.EventId == AutomationElement.MenuOpenedEvent ? "MENU OPENED" : "MENU CLOSED") + Describe(s)));
    }
}
'@
function Note([string]$text) { [Rec]::Note($text) }
[Rec]::Start()

function Shot([string]$name) {
    $b = [Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object Drawing.Bitmap $b.Width, $b.Height
    $g = [Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($b.Location, [Drawing.Point]::Empty, $b.Size)
    $bmp.Save((Join-Path $OutDir "$name.png")); $g.Dispose(); $bmp.Dispose()
}
function Step([string]$title, [scriptblock]$act, [int]$waitMs = 1500) {
    Note "===== $title"
    & $act
    Start-Sleep -Milliseconds $waitMs
}

Note "launching $Exe"
$proc = Start-Process -FilePath $Exe -ArgumentList @('/debug', '--profileDir', ('"{0}"' -f $ProfileDir)) -PassThru
try {
    $deadline = (Get-Date).AddSeconds(90)
    do { Start-Sleep -Milliseconds 500; $proc.Refresh() } until ($proc.MainWindowHandle -ne 0 -or (Get-Date) -gt $deadline)
    if ($proc.MainWindowHandle -eq 0) { throw 'QuickMail showed no main window within 90 s.' }
    Start-Sleep -Seconds 12   # let the offline load settle
    [void][Kb]::SetForegroundWindow($proc.MainWindowHandle)
    Start-Sleep -Milliseconds 800
    Shot '0-start'

    Step 'Ctrl+3: focus the message list' { [Kb]::Chord($VK_CONTROL, $VK_3) }
    Step 'A. Shift+F10, first time since launch' { [Kb]::Chord($VK_SHIFT, $VK_F10) }
    Shot 'A-open'
    Step 'A. Escape' { [Kb]::Press($VK_ESC) }
    Step 'B. Shift+F10 again, straight after closing' { [Kb]::Chord($VK_SHIFT, $VK_F10) }
    Shot 'B-open'
    Step 'B. Escape' { [Kb]::Press($VK_ESC) }
    Step 'C. Applications key' { [Kb]::Press($VK_APPS) }
    Step 'C. Escape' { [Kb]::Press($VK_ESC) }
    Step 'D. Down to the next message' { [Kb]::Press($VK_DOWN) }
    Step 'D. Shift+F10 on the new row' { [Kb]::Chord($VK_SHIFT, $VK_F10) }
    Step 'D. Escape' { [Kb]::Press($VK_ESC) }

    # E: make the mouse the most recent input device, then use the keyboard.
    $r = [Windows.Automation.AutomationElement]::FocusedElement.Current.BoundingRectangle
    Step 'E. Left-click the focused row (mouse is now the last device)' {
        [Kb]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
    }
    Step 'E. Shift+F10 after the mouse' { [Kb]::Chord($VK_SHIFT, $VK_F10) }
    Shot 'E-open'
    Step 'E. Escape' { [Kb]::Press($VK_ESC) }
    Step 'F. Shift+F10, Escape, Shift+F10 quickly (300 ms apart)' {
        [Kb]::Chord($VK_SHIFT, $VK_F10); Start-Sleep -Milliseconds 300
        [Kb]::Press($VK_ESC); Start-Sleep -Milliseconds 300
        [Kb]::Chord($VK_SHIFT, $VK_F10)
    }
    Shot 'F-open'
    Step 'F. Escape' { [Kb]::Press($VK_ESC) }
    Note 'done'
}
finally {
    [Windows.Automation.Automation]::RemoveAllEventHandlers()
    try { $proc.Kill() } catch {}
    $out = @(); $item = $null
    while ([Rec]::Lines.TryDequeue([ref]$item)) { $out += $item }
    $out | Set-Content -Encoding utf8 $log
    Get-Content $log
}

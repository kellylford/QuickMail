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

$clock = [Diagnostics.Stopwatch]::StartNew()
$lines = [Collections.Concurrent.ConcurrentQueue[string]]::new()
function Note([string]$text) { $lines.Enqueue(('{0,8:N0} ms  {1}' -f $clock.Elapsed.TotalMilliseconds, $text)) }
function Describe($el) {
    try {
        $c = $el.Current
        $sel = ''
        try {
            $p = $el.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)
            if ($p.Current.IsSelected) { $sel = ' [selected]' }
        } catch {}
        '{0} "{1}" id={2} pid={3}{4}' -f $c.ControlType.ProgrammaticName.Replace('ControlType.', ''), $c.Name, $c.AutomationId, $c.ProcessId, $sel
    } catch { '(element gone)' }
}

$root = [Windows.Automation.AutomationElement]::RootElement
$focusHandler = [Windows.Automation.AutomationFocusChangedEventHandler]{
    param($s, $e) Note ('FOCUS        ' + (Describe $s))
}
$menuHandler = [Windows.Automation.AutomationEventHandler]{
    param($s, $e)
    $name = $e.EventId.ProgrammaticName -replace '^.*Identifiers\.', '' -replace 'Event$', ''
    Note (('{0,-12} ' -f $name) + (Describe $s))
}
[Windows.Automation.Automation]::AddAutomationFocusChangedEventHandler($focusHandler)
foreach ($ev in [Windows.Automation.AutomationElement]::MenuOpenedEvent, [Windows.Automation.AutomationElement]::MenuClosedEvent) {
    [Windows.Automation.Automation]::AddAutomationEventHandler($ev, $root, [Windows.Automation.TreeScope]::Subtree, $menuHandler)
}

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
    while ($lines.TryDequeue([ref]$item)) { $out += $item }
    $out | Set-Content -Encoding utf8 $log
    Get-Content $log
}

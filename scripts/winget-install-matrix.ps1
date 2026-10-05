<#
.SYNOPSIS
  Measures QuickMail's installer behaviour across every install/upgrade path winget can
  put a machine through, and writes the results as Markdown.

.DESCRIPTION
  Written for issue #536 (winget distribution). The winget plan's Phase 1 was done by hand
  on one ARM64 machine and left three gaps: x64 was never tested, Setup.exe over an
  MSI-installed copy -- the path every existing user takes -- was never tested at all, and
  the winget correlation claims were inferred from registry state rather than measured.

  This script closes those gaps on a CI runner, so the answers are reproducible and nobody
  has to lend a machine to find out. It takes two already-packed Velopack output folders
  (an "old" version and a "new" one) and walks a fixed list of scenarios, snapshotting
  Add/Remove Programs rows, Windows Installer product registrations, install directories,
  Start Menu shortcuts, and winget's own view of the machine after each step.

  Every scenario starts from a verified-clean machine (Reset-Machine below) so the
  scenarios cannot contaminate each other.

.PARAMETER OldDir
  Velopack output folder for the older version (must contain its MSI and Setup.exe).

.PARAMETER NewDir
  Velopack output folder for the newer version.

.PARAMETER Report
  Path to write the Markdown report to.

.PARAMETER EmittedListing
  Optional file listing the pack folder's contents as `vpk pack` emitted them, captured by
  the caller BEFORE it renames anything. Scenario 0 reports vpk's own filenames; without
  this it can only report the post-rename ones, which is not the same claim.

.PARAMETER Force
  Run outside CI. Required there because Reset-Machine deletes %APPDATA%\QuickMail.

.NOTES
  This is destructive well beyond "it removes QuickMail". It uninstalls QuickMail
  repeatedly, deletes its install directories, force-deletes its registry keys, and
  deletes %APPDATA%\QuickMail -- the profile directory holding accounts, settings, rules,
  templates and cached mail. It is meant for a throwaway CI runner and refuses to start
  anywhere else unless -Force says the machine is disposable.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OldDir,
    [Parameter(Mandatory)][string]$NewDir,
    [Parameter(Mandatory)][string]$OldVersion,
    [Parameter(Mandatory)][string]$NewVersion,
    [Parameter(Mandatory)][ValidateSet('x64', 'arm64')][string]$Arch,
    [string]$Report = 'winget-matrix-report.md',
    [string]$EmittedListing,
    # A SHIPPED release packed with vpk 1.2.158 or later, for scenario 7's real winget
    # install. A real release rather than this run's synthetic packs because winget needs an
    # https InstallerUrl, and GitHub Releases is where the manifest points anyway.
    [string]$ShippedNew = '0.8.52',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $Force -and -not $env:GITHUB_ACTIONS -and -not $env:CI) {
    throw ('Refusing to run outside CI. This script deletes %APPDATA%\QuickMail, which on a ' +
           'developer machine is the real profile directory -- accounts, settings, rules, ' +
           'templates, cached mail. Pass -Force only if this machine is genuinely disposable.')
}

# Scenario failures must not abort the run: a scenario that misbehaves is a finding, and
# the scenarios after it are still worth measuring. Only harness errors should be fatal.
$script:Findings = [System.Collections.Generic.List[string]]::new()
$script:Sections = [System.Collections.Generic.List[string]]::new()
# A scenario that aborted measured nothing. Counted so the exit code can say the matrix has
# a hole in it, rather than reporting success for a run that half happened.
$script:Aborted = 0

function Write-Section { param([string]$Text) $script:Sections.Add($Text) | Out-Null }
function Add-Finding { param([string]$Text) $script:Findings.Add($Text) | Out-Null; Write-Host "FINDING: $Text" }

# --- state capture -------------------------------------------------------------------

$UninstallRoots = @(
    @{ Scope = 'HKCU'; Path = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' }
    @{ Scope = 'HKLM'; Path = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' }
    @{ Scope = 'HKLM-Wow'; Path = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall' }
)

function Get-ArpRows {
    <#  Every Add/Remove Programs row that mentions QuickMail, from all three hives.
        SystemComponent is carried through because an MSI row with SystemComponent=1 is
        hidden from Settings > Apps but is still a real product registration -- the
        distinction the migration question turns on. #>
    $rows = @()
    foreach ($root in $UninstallRoots) {
        if (-not (Test-Path $root.Path)) { continue }
        foreach ($key in Get-ChildItem $root.Path -ErrorAction SilentlyContinue) {
            $p = Get-ItemProperty $key.PSPath -ErrorAction SilentlyContinue
            if (-not $p) { continue }
            $display = if ($p.PSObject.Properties['DisplayName']) { $p.DisplayName } else { $null }
            if (($key.PSChildName -notlike '*QuickMail*') -and ($display -notlike '*QuickMail*')) { continue }
            $rows += [pscustomobject]@{
                Scope               = $root.Scope
                KeyName             = $key.PSChildName
                DisplayName         = $display
                DisplayVersion      = if ($p.PSObject.Properties['DisplayVersion']) { $p.DisplayVersion } else { $null }
                Publisher           = if ($p.PSObject.Properties['Publisher']) { $p.Publisher } else { $null }
                SystemComponent     = if ($p.PSObject.Properties['SystemComponent']) { $p.SystemComponent } else { 0 }
                InstallLocation     = if ($p.PSObject.Properties['InstallLocation']) { $p.InstallLocation } else { $null }
                UninstallString     = if ($p.PSObject.Properties['UninstallString']) { $p.UninstallString } else { $null }
                QuietUninstallString = if ($p.PSObject.Properties['QuietUninstallString']) { $p.QuietUninstallString } else { $null }
            }
        }
    }
    return @($rows)
}

function Get-MsiProductRows {
    <#  Windows Installer's own product registration, which survives independently of the
        ARP row. This is what would be orphaned if Setup.exe overwrites an MSI install
        without going through msiexec. #>
    $rows = @()
    $roots = @(
        @{ Scope = 'HKCU-Installer'; Path = 'HKCU:\Software\Microsoft\Installer\Products' }
        @{ Scope = 'HKLM-Installer'; Path = 'HKLM:\SOFTWARE\Classes\Installer\Products' }
    )
    foreach ($root in $roots) {
        if (-not (Test-Path $root.Path)) { continue }
        foreach ($key in Get-ChildItem $root.Path -ErrorAction SilentlyContinue) {
            $p = Get-ItemProperty $key.PSPath -ErrorAction SilentlyContinue
            if (-not $p -or -not $p.PSObject.Properties['ProductName']) { continue }
            if ($p.ProductName -notlike '*QuickMail*') { continue }
            $rows += [pscustomobject]@{
                Scope         = $root.Scope
                PackedCode    = $key.PSChildName
                ProductName   = $p.ProductName
            }
        }
    }
    return @($rows)
}

function Get-InstallDirs {
    # Every fixed drive's root, not just C:. A silent MSI install takes the Directory
    # table's TARGETDIR default, which Windows Installer resolves to the drive with the
    # most free space -- on a GitHub runner that is D:, and hardcoding C: reported "no
    # install directories" for an install that had plainly happened.
    $candidates = @(
        (Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue |
            Where-Object { $null -ne $_.Free } | ForEach-Object { "$($_.Name):\QuickMail" })
        (Join-Path $env:LOCALAPPDATA 'QuickMail')
        (Join-Path $env:ProgramFiles 'QuickMail')
    ) | Select-Object -Unique
    $out = @()
    foreach ($c in $candidates) {
        if (-not (Test-Path $c)) { continue }
        $current = Join-Path $c 'current'
        $exe = Join-Path $current 'QuickMail.exe'
        $out += [pscustomobject]@{
            Path       = $c
            HasCurrent = Test-Path $current
            ExeVersion = if (Test-Path $exe) { (Get-Item $exe).VersionInfo.FileVersion } else { $null }
            Entries    = ((Get-ChildItem $c -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name) -join ', ')
        }
    }
    return @($out)
}

function Get-Shortcuts {
    $roots = @(
        (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs')
        (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs')
    )
    $out = @()
    foreach ($r in $roots) {
        if (-not (Test-Path $r)) { continue }
        $out += Get-ChildItem $r -Recurse -Filter '*QuickMail*.lnk' -ErrorAction SilentlyContinue |
            ForEach-Object { $_.FullName.Replace($env:USERPROFILE, '~') }
    }
    return @($out)
}

function Get-WingetView {
    <#  winget's own opinion, when it is usable. Reported verbatim rather than filtered:
        the question is what a user sees, and an empty filtered result is indistinguishable
        from a command that refused to run. --accept-source-agreements matters on a fresh
        machine -- without it, and with interactivity disabled, winget declines rather than
        prompting, which is exactly how the first run of this probe recorded nothing at
        all for every scenario. #>
    if (-not $script:WingetAvailable) { return '(winget not available on this runner)' }
    $out = @()
    foreach ($cmd in @('list --accept-source-agreements', 'upgrade --accept-source-agreements')) {
        $text = (& cmd /c "winget $cmd --disable-interactivity 2>&1" | Out-String)
        $lines = @($text -split "`r?`n" | Where-Object { $_.Trim() })
        $hits  = @($lines | Where-Object { $_ -match 'QuickMail' })
        $body = if ($hits.Count) {
            ($hits -join "`n")
        } else {
            # No QuickMail row: show the head of the raw output so a refusal, an error, or a
            # genuinely empty list can be told apart.
            "(no QuickMail row; raw output follows)`n" + (($lines | Select-Object -First 8) -join "`n")
        }
        $out += "`$ winget $cmd`n$body"
    }
    return ($out -join "`n`n")
}

function Get-Snapshot {
    param([string]$Label)
    Get-Process QuickMail -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    # @() on every collection is load-bearing, not decoration: a function that returns one
    # object returns it unwrapped, and under Set-StrictMode -Version Latest the resulting
    # `.Count` on a bare PSCustomObject is a terminating error. One ARP row is the normal
    # case here, so without these the whole probe reports nothing.
    return [pscustomobject]@{
        Label       = $Label
        Arp         = @(Get-ArpRows)
        MsiProducts = @(Get-MsiProductRows)
        Dirs        = @(Get-InstallDirs)
        Shortcuts   = @(Get-Shortcuts)
        Winget      = Get-WingetView
    }
}

function Format-Snapshot {
    param([pscustomobject]$Snap)
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine("**State: $($Snap.Label)**")
    [void]$sb.AppendLine()

    [void]$sb.AppendLine("Add/Remove Programs rows: **$($Snap.Arp.Count)**")
    [void]$sb.AppendLine()
    if ($Snap.Arp.Count) {
        # Publisher is printed, not just captured. A winget manifest's AppsAndFeaturesEntries
        # must match the ARP row on EVERY field it names, and the manifest in #557 names
        # DisplayName, Publisher and ProductCode. Two of those were verifiable from this table
        # and Publisher was not, so a manifest asserting a publisher string Velopack does not
        # write would fail correlation outright -- winget would treat the package as not
        # installed after installing it. Do not remove this column to save width.
        [void]$sb.AppendLine('| Hive | Key | DisplayName | Publisher | DisplayVersion | Hidden | InstallLocation | QuietUninstallString |')
        [void]$sb.AppendLine('| --- | --- | --- | --- | --- | --- | --- | --- |')
        foreach ($r in $Snap.Arp) {
            $hidden = if ($r.SystemComponent -eq 1) { 'yes' } else { 'no' }
            $loc = if ($r.InstallLocation) { $r.InstallLocation.Replace($env:LOCALAPPDATA, '%LocalAppData%') } else { '' }
            $q = if ($r.QuietUninstallString) { '`' + $r.QuietUninstallString.Replace($env:LOCALAPPDATA, '%LocalAppData%') + '`' } else { '--' }
            $pub = if ($r.Publisher) { $r.Publisher } else { '*(not set)*' }
            [void]$sb.AppendLine("| $($r.Scope) | ``$($r.KeyName)`` | $($r.DisplayName) | $pub | $($r.DisplayVersion) | $hidden | $loc | $q |")
        }
        [void]$sb.AppendLine()
    }

    [void]$sb.AppendLine("Windows Installer product registrations: **$($Snap.MsiProducts.Count)**" +
        $(if ($Snap.MsiProducts.Count) { ' -- ' + (($Snap.MsiProducts | ForEach-Object { "$($_.Scope) $($_.ProductName)" }) -join '; ') } else { '' }))
    [void]$sb.AppendLine()

    [void]$sb.AppendLine('Install directories:')
    [void]$sb.AppendLine()
    if ($Snap.Dirs.Count) {
        foreach ($d in $Snap.Dirs) {
            $p = $d.Path.Replace($env:LOCALAPPDATA, '%LocalAppData%')
            [void]$sb.AppendLine("- ``$p`` -- exe version $($d.ExeVersion); contains: $($d.Entries)")
        }
    } else { [void]$sb.AppendLine('- (none)') }
    [void]$sb.AppendLine()

    [void]$sb.AppendLine("Start Menu shortcuts: " + $(if ($Snap.Shortcuts.Count) { ($Snap.Shortcuts -join ', ') } else { '(none)' }))
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('winget:')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('```')
    [void]$sb.AppendLine($Snap.Winget)
    [void]$sb.AppendLine('```')
    return $sb.ToString()
}

# --- machine reset -------------------------------------------------------------------

# --- the uninstall hook's own evidence (#245) ----------------------------------------------
# QuickMail's uninstall hook decides, in a detached PowerShell process, whether an uninstall is
# really the first half of an MSI upgrade (Helpers/UninstallDataPrompt.cs), and carries the
# start-at-sign-in entries across one (Services/StartupEntryHandoff.cs). Both write to
# quickmail-uninstall.log in the hook's temp folder. The hook runs inside a Windows Installer
# custom-action process, so read both candidate temp folders rather than assume which it got.
$HookLogs = @((Join-Path $env:TEMP 'quickmail-uninstall.log'), (Join-Path $env:WINDIR 'Temp\quickmail-uninstall.log')) | Select-Object -Unique
$HookHandoffs = @((Join-Path $env:TEMP 'quickmail-startup-handoff.json'), (Join-Path $env:WINDIR 'Temp\quickmail-startup-handoff.json')) | Select-Object -Unique
$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$ApprovedKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'

function Clear-HookState {
    Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match 'quickmail-uninstall-prompt' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    foreach ($f in @($HookLogs) + @($HookHandoffs)) { Remove-Item $f -Force -ErrorAction SilentlyContinue }
    foreach ($k in $RunKey, $ApprovedKey) {
        if (Test-Path $k) { Remove-ItemProperty -Path $k -Name 'QuickMail' -ErrorAction SilentlyContinue }
    }
}

function Read-HookLog { (@($HookLogs | Where-Object { Test-Path $_ } | ForEach-Object { Get-Content $_ })) -join "`n" }

function Wait-HookLog([string]$Pattern, [int]$Seconds) {
    $until = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $until) {
        if ((Read-HookLog) -match $Pattern) { return $true }
        Start-Sleep -Seconds 1
    }
    return $false
}

function Set-StartupEntryOff([string]$Exe) {
    # Start at sign-in turned on, then off again in Task Manager: both the Run value and
    # Windows' off mark (first byte odd) must survive an upgrade. Never New-Item -Force on these
    # keys -- on an existing key that recreates it, wiping every other app's entry.
    foreach ($k in $RunKey, $ApprovedKey) { if (-not (Test-Path $k)) { New-Item -Path $k | Out-Null } }
    Set-ItemProperty -Path $RunKey -Name 'QuickMail' -Value "`"$Exe`" --startup"
    Set-ItemProperty -Path $ApprovedKey -Name 'QuickMail' -Value ([byte[]](3,0,0,0,0,0,0,0,0,0,0,0)) -Type Binary
}

function Get-StartupEntry {
    $run = (Get-ItemProperty -Path $RunKey -Name 'QuickMail' -ErrorAction SilentlyContinue).QuickMail
    $mark = (Get-ItemProperty -Path $ApprovedKey -Name 'QuickMail' -ErrorAction SilentlyContinue).QuickMail
    [pscustomobject]@{ Run = $run; FirstMarkByte = $(if ($mark) { $mark[0] } else { $null }) }
}

function Reset-Machine {
    <#  Return to a clean state so the next scenario measures only its own actions.
        Uninstalls through the product's own strings first (the supported path), then
        removes anything left behind by force -- a scenario that leaves residue is itself a
        finding, recorded by the caller's post-uninstall snapshot, not here.

        Returns what survived. "Every scenario starts from a verified-clean machine" is a
        claim the report makes; the caller turns this object into the evidence for it, or
        into a finding when it is not true. A reset that silently fails is the one way this
        probe can report a previous scenario's residue as the current scenario's result. #>
    Get-Process QuickMail -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Clear-HookState

    foreach ($row in Get-ArpRows) {
        try {
            if ($row.KeyName -match '^\{[0-9A-Fa-f-]+\}$') {
                Start-Process msiexec -ArgumentList "/x $($row.KeyName) /qn" -Wait -ErrorAction SilentlyContinue
            } elseif ($row.QuietUninstallString) {
                # "path\Update.exe" --uninstall --silent  ->  split exe from arguments
                if ($row.QuietUninstallString -match '^"([^"]+)"\s*(.*)$') {
                    $exe = $Matches[1]; $argline = $Matches[2]
                    if (Test-Path $exe) {
                        Start-Process $exe -ArgumentList $argline -Wait -ErrorAction SilentlyContinue
                    }
                }
            }
        } catch { Write-Host "reset: uninstall of $($row.KeyName) threw: $_" }
    }

    Start-Sleep -Seconds 2
    Get-Process QuickMail -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

    foreach ($root in $UninstallRoots) {
        if (-not (Test-Path $root.Path)) { continue }
        Get-ChildItem $root.Path -ErrorAction SilentlyContinue |
            Where-Object {
                $p = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
                $_.PSChildName -like '*QuickMail*' -or ($p -and $p.PSObject.Properties['DisplayName'] -and $p.DisplayName -like '*QuickMail*')
            } |
            ForEach-Object { Remove-Item $_.PSPath -Recurse -Force -ErrorAction SilentlyContinue }
    }

    # Windows Installer's own product registration outlives the ARP row and is NOT removed by
    # deleting it. When msiexec /x succeeds it goes with the product, but scenario 2 leaves an
    # MSI registration pointing at files Setup.exe has overwritten, and an /x against that can
    # fail -- after which the stale key would be counted as the NEXT scenario's MSI
    # registration. Scenario 2's second finding is exactly a count of these, so a leak here
    # fabricates that finding for a scenario that never installed an MSI.
    foreach ($prod in Get-MsiProductRows) {
        $installerRoot = if ($prod.Scope -eq 'HKCU-Installer') {
            'HKCU:\Software\Microsoft\Installer'
        } else {
            'HKLM:\SOFTWARE\Classes\Installer'
        }
        foreach ($sub in 'Products', 'Features') {
            Remove-Item (Join-Path $installerRoot "$sub\$($prod.PackedCode)") -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    foreach ($d in (@(Get-InstallDirs | ForEach-Object { $_.Path }) + (Join-Path $env:APPDATA 'QuickMail'))) {
        Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue
    }
    # Both Start Menu roots, not just the per-user one: Get-Shortcuts reports the machine-wide
    # root too, so cleaning only one leaves residue this function would then report as
    # unclearable forever.
    foreach ($menu in @((Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'),
                        (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'))) {
        if (-not (Test-Path $menu)) { continue }
        Get-ChildItem $menu -Recurse -Filter '*QuickMail*.lnk' -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }

    $residue = [pscustomobject]@{
        Arp         = @(Get-ArpRows).Count
        MsiProducts = @(Get-MsiProductRows).Count
        Dirs        = @(Get-InstallDirs).Count
        Shortcuts   = @(Get-Shortcuts).Count
    }
    $residue | Add-Member -NotePropertyName Total `
        -NotePropertyValue ($residue.Arp + $residue.MsiProducts + $residue.Dirs + $residue.Shortcuts)
    if ($residue.Total -ne 0) {
        Write-Host ("reset: warning, QuickMail traces survived: {0} ARP, {1} MSI registration, {2} dir, {3} shortcut" -f
            $residue.Arp, $residue.MsiProducts, $residue.Dirs, $residue.Shortcuts)
    }
    return $residue
}

# --- install primitives --------------------------------------------------------------

function Invoke-Installer {
    param([string]$Path, [string[]]$Arguments)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process -FilePath $Path -ArgumentList $Arguments -Wait -PassThru
    $sw.Stop()
    Get-Process QuickMail -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    return [pscustomobject]@{ ExitCode = $p.ExitCode; Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1) }
}

function Get-PackedFile {
    param([string]$Dir, [string]$Pattern)
    $f = @(Get-ChildItem $Dir -Filter $Pattern -ErrorAction SilentlyContinue)
    if ($f.Count -ne 1) { throw "Expected exactly one '$Pattern' in $Dir, found $($f.Count): $(($f | Select-Object -ExpandProperty Name) -join ', ')" }
    return $f[0].FullName
}

# --- winget availability -------------------------------------------------------------

$script:WingetAvailable = $null -ne (Get-Command winget -ErrorAction SilentlyContinue)
if ($script:WingetAvailable) {
    $wingetVersion = (& winget --version) 2>&1 | Out-String
    Write-Host "winget available: $wingetVersion"
} else {
    Write-Host 'winget not available on this runner; winget-specific checks will report as unavailable.'
}

# --- resolve inputs ------------------------------------------------------------------

$suffix = if ($Arch -eq 'arm64') { 'win-arm64' } else { 'win' }
$oldMsi   = Get-PackedFile $OldDir "QuickMail-$OldVersion-$suffix.msi"
$oldSetup = Get-PackedFile $OldDir "QuickMail-$OldVersion-$suffix-Setup.exe"
$newMsi   = Get-PackedFile $NewDir "QuickMail-$NewVersion-$suffix.msi"
$newSetup = Get-PackedFile $NewDir "QuickMail-$NewVersion-$suffix-Setup.exe"

Write-Section "# Installer path matrix -- $Arch"
Write-Section ''
Write-Section "Old version **$OldVersion**, new version **$NewVersion**, both packed locally by this run -- neither is a shipped QuickMail artifact."
Write-Section ''
# Record the architecture the probe actually ran on. The whole value of the ARM64 leg is
# that it is native; "runs-on: windows-11-arm" is an assertion about a runner label, and the
# report is where it has to become a measurement. ProcessArchitecture matters separately:
# a non-native PowerShell would read HKLM:\SOFTWARE through the WOW64 view.
$osArch   = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
$procArch = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture
Write-Section "Runner: $((Get-CimInstance Win32_OperatingSystem).Caption) build $([Environment]::OSVersion.Version), OS architecture **$osArch**, probe process **$procArch**. vpk: $(if ($env:VPK_VERSION) { $env:VPK_VERSION } else { 'unrecorded' }). winget: $(if ($script:WingetAvailable) { (& winget --version) } else { 'not available' })."
Write-Section ''

$expectedOsArch = if ($Arch -eq 'arm64') { 'Arm64' } else { 'X64' }
if ("$osArch" -ne $expectedOsArch) {
    Add-Finding "The $Arch leg ran on a $osArch runner, so it is not measuring $Arch behaviour at all. Every result below is mislabelled."
} elseif ("$procArch" -ne $expectedOsArch) {
    Add-Finding "The $Arch leg's PowerShell is a $procArch process on a $osArch OS. Registry reads under HKLM:\SOFTWARE go through the WOW64 view, so the ARP snapshots may be incomplete."
}

# --- scenario 0: what vpk emitted, and what architecture it is -----------------------

function Get-PEMachine {
    param([string]$Path)
    $fs = [System.IO.File]::OpenRead($Path)
    try {
        $br = New-Object System.IO.BinaryReader($fs)
        $fs.Position = 0x3c
        $fs.Position = $br.ReadInt32() + 4
        return $br.ReadUInt16()
    } finally { $fs.Dispose() }
}
$machineNames = @{ 0x8664 = 'x64'; 0xAA64 = 'ARM64'; 0x14C = 'x86' }

Write-Section '## Scenario 0 -- what `vpk pack` emits'
Write-Section ''
Write-Section 'Filenames from the same `vpk pack` arguments the release workflow uses (`--msi --instLocation PerUser`).'
Write-Section ''
if ($EmittedListing -and (Test-Path $EmittedListing)) {
    # Read the pre-rename listing the caller captured. Listing the pack folder here instead
    # would print names this workflow assigned, while calling them vpk's -- and #555 hardcodes
    # vpk's names, so that is precisely the claim that must not be second-hand.
    Write-Section 'As `vpk pack` wrote them, before this workflow renames the MSI and Setup.exe to carry a version:'
    Write-Section ''
    Write-Section '```'
    Write-Section (((Get-Content $EmittedListing) | Where-Object { $_.Trim() }) -join "`n")
    Write-Section '```'
    Write-Section ''
    Write-Section 'After the rename (what the scenarios below install):'
} else {
    Write-Section '**The caller did not record the pre-rename listing**, so these are the names *after* this workflow renamed the MSI and Setup.exe. They are not evidence of what `vpk pack` emits.'
}
Write-Section ''
Write-Section '```'
Write-Section (Get-ChildItem $NewDir | Select-Object -ExpandProperty Name | Sort-Object | Out-String).Trim()
Write-Section '```'
Write-Section ''
Write-Section 'PE machine type of each installer (this is what komac infers `Architecture:` from when the filename carries no architecture token):'
Write-Section ''
Write-Section '| File | PE machine |'
Write-Section '| --- | --- |'
foreach ($f in @($newSetup, $newMsi)) {
    $name = Split-Path $f -Leaf
    if ($f -like '*.msi') { Write-Section "| ``$name`` | (MSI, not a PE image) |"; continue }
    $m = Get-PEMachine $f
    $shown = if ($machineNames.ContainsKey([int]$m)) { $machineNames[[int]$m] } else { "unknown (0x$('{0:X4}' -f $m))" }
    Write-Section "| ``$name`` | $shown |"
    # Emit this on BOTH legs. Gating it on arm64 meant the x64 report -- the leg where the
    # filename carries no architecture token and the PE header is therefore the ONLY thing
    # komac can read -- printed "x86" in the table and listed no finding at all.
    $expectedMachine = if ($Arch -eq 'arm64') { 'ARM64' } else { 'x64' }
    if ($shown -ne $expectedMachine) {
        $consequence = if ($Arch -eq 'arm64') {
            "The ARM64 asset's filename does carry an architecture token, so komac has something other than this header to go on."
        } else {
            "The x64 asset ships as ``QuickMail-<v>-win-Setup.exe``, whose filename carries NO architecture token, so this header is all komac has: it would infer ``Architecture: x86``. Check the first automated winget-pkgs PR before letting it merge."
        }
        Add-Finding "The $Arch channel's Setup.exe is a $shown PE image, not $expectedMachine. $consequence"
    }
}
Write-Section ''

# --- scenario runner -----------------------------------------------------------------

function Invoke-Scenario {
    param([string]$Title, [string]$Question, [scriptblock]$Body)
    Write-Host "`n=== $Title ==="
    Write-Section "## $Title"
    Write-Section ''
    Write-Section "*Question: $Question*"
    Write-Section ''
    try {
        # Inside the try: a reset that cannot clean the machine is this scenario's failure
        # to report, not a reason to abandon every scenario after it.
        $residue = Reset-Machine
        if ($residue.Total -ne 0) {
            Write-Section "**Start state NOT clean:** $($residue.Arp) ARP row(s), $($residue.MsiProducts) Windows Installer registration(s), $($residue.Dirs) install director(ies) and $($residue.Shortcuts) shortcut(s) survived the reset. Everything below may be the previous scenario's residue."
            Add-Finding "$Title started from a machine the reset could not clean ($($residue.Total) trace(s) left behind). Its measurements may be contaminated by the scenario before it."
        } else {
            Write-Section '*Start state: verified clean -- no QuickMail ARP rows, Windows Installer registrations, install directories or shortcuts.*'
        }
        Write-Section ''
        & $Body
    } catch {
        Write-Section "**Scenario aborted:** ``$_``"
        Add-Finding "$Title aborted: $_"
        $script:Aborted++
    }
    Write-Section ''
}

Invoke-Scenario 'Scenario 1 -- silent MSI, fresh machine' `
    'Does `msiexec /qn` land in %LocalAppData%\QuickMail, as the wizard does? vpk 1.2.0 put it in a drive root (#554); vpk 1.2.158 carries the upstream fix (velopack#945). This is the install `winget install` runs when the package points at the MSI.' {
    $r = Invoke-Installer 'msiexec.exe' @('/i', "`"$newMsi`"", '/qn', '/l*v', "$PWD\msi-fresh.log")
    Write-Section "``msiexec /i ... /qn`` -> exit $($r.ExitCode) in $($r.Seconds)s"
    Write-Section ''
    $snap = Get-Snapshot 'after silent MSI install'
    Write-Section (Format-Snapshot $snap)
    if ($r.ExitCode -ne 0) {
        Add-Finding "On $Arch the silent MSI install exited $($r.ExitCode); everything measured for this scenario is the state after a failed install. See msi-fresh.log."
    }
    # Read the answer off the directories actually measured, rather than testing C:\ by name.
    # Windows Installer resolves TARGETDIR to a drive root -- whichever fixed drive it picks --
    # and the x64 runner picked D:. A C:-only test therefore matched neither branch and left
    # the x64 report with NO finding for this scenario, while its own table showed
    # D:\QuickMail. The defect was measured and then not reported.
    $perUser = Join-Path $env:LOCALAPPDATA 'QuickMail'
    $stray = @($snap.Dirs | Where-Object { $_.Path -ne $perUser })
    if ($stray.Count) {
        Add-Finding ("Confirmed on ${Arch}: a silent MSI install lands in " +
            (($stray | ForEach-Object { $_.Path }) -join ', ') +
            ", not %LocalAppData%. Issue #554 is neither architecture-specific nor specific to C: -- Windows Installer resolves TARGETDIR to a drive root, and which drive is the runner's choice.")
    } elseif ($snap.Dirs.Count) {
        # The expected result since vpk 1.2.158 -- reported as a pass in the scenario body, not
        # as a finding, so the findings list stays a list of things that are wrong.
        Write-Section "**Pass:** the silent MSI install landed in ``%LocalAppData%\QuickMail`` and nowhere else."
    } else {
        Add-Finding "On $Arch a silent MSI install produced no QuickMail install directory anywhere this probe looks. The scenario measured nothing; do not read its silence as a pass."
    }
}

Invoke-Scenario 'Scenario 2 -- Setup.exe over an MSI install (the migration path)' `
    'Every existing user installed from the MSI wizard into %LocalAppData%. What does `winget install`/`upgrade` -- that is, `Setup.exe --silent` -- do to that machine?' {
    # Reproduce the wizard's install location without driving the wizard: VELOPACK_INSTALLDIR
    # is exactly what the Next button sets, per the #554 investigation. It must be passed as
    # an msiexec PROPERTY, not an environment variable -- the install runs in the Windows
    # Installer service, which does not inherit this process's environment. Setting it as an
    # env var silently did nothing, and the first run of this probe measured an MSI install
    # sitting on D:\ while claiming to be the wizard-equivalent %LocalAppData% one.
    $target = Join-Path $env:LOCALAPPDATA 'QuickMail'
    $r1 = Invoke-Installer 'msiexec.exe' @('/i', "`"$oldMsi`"", '/qn', "VELOPACK_INSTALLDIR=`"$target`"")
    Write-Section "Step 1 -- MSI $OldVersion into ``%LocalAppData%\QuickMail`` (wizard-equivalent): exit $($r1.ExitCode) in $($r1.Seconds)s"
    Write-Section ''
    $before = Get-Snapshot "after MSI $OldVersion (wizard-equivalent)"
    Write-Section (Format-Snapshot $before)

    # Assert the premise before measuring anything on top of it. If the MSI did not land
    # where the wizard would have put it, this scenario is not the migration path and its
    # result must not be read as one. "Exactly one directory" rather than "the right one is
    # among them": a second install elsewhere means the starting state is not a wizard
    # install either, and the ARP rows counted below would include its row.
    if ($r1.ExitCode -ne 0) {
        throw "Premise failed: the MSI install exited $($r1.ExitCode), so there is no wizard-equivalent starting state to migrate from."
    }
    if (@($before.Dirs).Count -ne 1 -or $before.Dirs[0].Path -ne $target) {
        throw "Premise failed: the MSI did not install to $target and nowhere else, so this is not the wizard-equivalent starting state. Directories found: $(($before.Dirs | ForEach-Object { $_.Path }) -join ', ')"
    }

    $r2 = Invoke-Installer $newSetup @('--silent')
    Write-Section "Step 2 -- ``Setup.exe --silent`` $NewVersion over it: exit $($r2.ExitCode) in $($r2.Seconds)s"
    Write-Section ''
    $after = Get-Snapshot "after Setup.exe $NewVersion over the MSI install"
    Write-Section (Format-Snapshot $after)

    $visibleAfter = @($after.Arp | Where-Object { $_.SystemComponent -ne 1 })
    if ($visibleAfter.Count -gt 1) {
        Add-Finding "Setup.exe over an MSI install leaves $($visibleAfter.Count) visible Add/Remove Programs rows ($(($visibleAfter | ForEach-Object { $_.KeyName }) -join ', ')). Users see QuickMail more than once in Settings > Apps, and winget has more than one row to correlate against."
    }
    if ($after.MsiProducts.Count -gt 0) {
        Add-Finding "Setup.exe over an MSI install leaves the Windows Installer product registration in place ($(($after.MsiProducts | ForEach-Object { $_.Scope }) -join ', ')). The machine still believes an MSI product is installed at a location Setup.exe has overwritten."
    }
    if ($r2.ExitCode -ne 0) {
        Add-Finding "Setup.exe over an MSI install exited $($r2.ExitCode). winget would report the upgrade as failed."
    }
}

Invoke-Scenario 'Scenario 3 -- Setup.exe over Setup.exe (the steady-state upgrade)' `
    'Phase 1 measured this by hand on ARM64. Does it hold on this runner, and does winget see the version advance?' {
    $r1 = Invoke-Installer $oldSetup @('--silent')
    Write-Section "Step 1 -- ``Setup.exe --silent`` $OldVersion, fresh: exit $($r1.ExitCode) in $($r1.Seconds)s"
    Write-Section ''
    Write-Section (Format-Snapshot (Get-Snapshot "after Setup.exe $OldVersion"))

    $r2 = Invoke-Installer $newSetup @('--silent')
    Write-Section "Step 2 -- ``Setup.exe --silent`` $NewVersion over it: exit $($r2.ExitCode) in $($r2.Seconds)s"
    Write-Section ''
    $after = Get-Snapshot "after Setup.exe $NewVersion over Setup.exe $OldVersion"
    Write-Section (Format-Snapshot $after)

    $visible = @($after.Arp | Where-Object { $_.SystemComponent -ne 1 })
    if ($visible.Count -ne 1) {
        Add-Finding "Setup-over-Setup left $($visible.Count) visible ARP rows; exactly one was expected."
    } else {
        if ($visible[0].DisplayVersion -ne $NewVersion) {
            Add-Finding "After upgrading to $NewVersion the ARP DisplayVersion still reads '$($visible[0].DisplayVersion)'. winget correlates on this value, so it would keep offering an upgrade that has already been applied."
        }
        # This is the row the winget manifest (#557) is written against, in its steady state.
        # That manifest's AppsAndFeaturesEntries names DisplayName, Publisher and ProductCode,
        # and winget requires EVERY field a manifest names to match: one wrong value is not a
        # weaker match, it is no match, after which winget treats QuickMail as not installed
        # immediately after installing it. Only ProductCode had been checked -- indirectly, by
        # the key name appearing in `winget list`. Mirror the manifest's values here so a
        # change on either side shows up as a finding rather than as a support ticket.
        $manifestEntries = @{ DisplayName = 'QuickMail'; Publisher = 'Kelly Ford'; KeyName = 'QuickMail' }
        foreach ($field in @($manifestEntries.Keys)) {
            $actual = $visible[0].$field
            if ($actual -ne $manifestEntries[$field]) {
                $named = if ($field -eq 'KeyName') { 'ProductCode' } else { $field }
                Add-Finding "Velopack's Add/Remove Programs row has $field '$actual', but the winget manifest's AppsAndFeaturesEntries names $named '$($manifestEntries[$field])'. winget requires every field the manifest names to match, so correlation would fail outright."
            }
        }
    }
}

Invoke-Scenario 'Scenario 4 -- uninstall through the quiet string winget uses' `
    'winget uninstall runs QuietUninstallString. Does it complete unattended, and what does it leave behind?' {
    $r1 = Invoke-Installer $newSetup @('--silent')
    Write-Section "Step 1 -- install ${NewVersion}: exit $($r1.ExitCode) in $($r1.Seconds)s"
    $installed = Get-Snapshot "installed $NewVersion"
    $row = @($installed.Arp | Where-Object { $_.QuietUninstallString })
    if (-not $row.Count) { throw 'No QuietUninstallString on any ARP row; winget uninstall would have nothing to run.' }
    Write-Section ''
    Write-Section "QuietUninstallString: ``$($row[0].QuietUninstallString)``"
    Write-Section ''

    $ran = $false
    if ($row[0].QuietUninstallString -match '^"([^"]+)"\s*(.*)$') {
        # Copy out of $Matches at once: any later -match in this scope replaces it. The
        # Where-Object drops the empty element -split yields for an argument-less string.
        $exe  = $Matches[1]
        $argv = @($Matches[2] -split '\s+' | Where-Object { $_ })
        $r2 = Invoke-Installer $exe $argv
        $ran = $true
        Write-Section "Step 2 -- running it: exit $($r2.ExitCode) in $($r2.Seconds)s"
        if ($r2.ExitCode -ne 0) {
            Add-Finding "The quiet uninstall string exited $($r2.ExitCode). ``winget uninstall`` runs exactly this and would report a failure."
        }
    } else {
        Add-Finding "QuietUninstallString did not parse as a quoted path plus arguments: $($row[0].QuietUninstallString)"
    }
    Write-Section ''
    $after = Get-Snapshot 'after quiet uninstall'
    Write-Section (Format-Snapshot $after)
    # Only judge the leftovers when the uninstall actually ran. Otherwise the app is simply
    # still installed, and the three checks below would report a healthy install as three
    # separate uninstall defects.
    if (-not $ran) {
        Write-Section '*The uninstall was never run, so the state above is the install, not what an uninstall leaves behind.*'
    } else {
        if ($after.Arp.Count -gt 0) {
            Add-Finding "The quiet uninstall left $($after.Arp.Count) Add/Remove Programs row(s) behind."
        }
        if ($after.Dirs.Count -gt 0) {
            Add-Finding "The quiet uninstall left directories behind: $(($after.Dirs | ForEach-Object { $_.Path }) -join ', ')."
        }
        # Snapshotted since the first run and never judged. A leftover Start Menu entry
        # pointing at a deleted exe is exactly the kind of thing this scenario exists to catch.
        if ($after.Shortcuts.Count -gt 0) {
            Add-Finding "The quiet uninstall left Start Menu shortcuts behind: $(($after.Shortcuts) -join ', ')."
        }
    }
}

Invoke-Scenario 'Scenario 5 -- MSI over a Setup.exe install (the reverse migration)' `
    'A user who installs from winget and later downloads the MSI from the release page ends up here. Is it survivable?' {
    $r1 = Invoke-Installer $oldSetup @('--silent')
    Write-Section "Step 1 -- ``Setup.exe --silent`` ${OldVersion}: exit $($r1.ExitCode) in $($r1.Seconds)s"
    Write-Section ''
    $target = Join-Path $env:LOCALAPPDATA 'QuickMail'
    # Show the starting state and assert it, exactly as scenario 2 does. Without this the
    # scenario asserts "over it" in prose only: if VELOPACK_INSTALLDIR stopped taking effect
    # the MSI would land on a drive root beside the Setup install, and the two ARP rows that
    # produces would still be reported as "the reverse migration is as messy as the forward
    # one" -- a correct-looking finding about a completely different machine state.
    $before = Get-Snapshot "after Setup.exe $OldVersion"
    Write-Section (Format-Snapshot $before)
    if (@($before.Dirs).Count -ne 1 -or $before.Dirs[0].Path -ne $target) {
        throw "Premise failed: Setup.exe $OldVersion did not install to $target and nowhere else. Directories found: $(($before.Dirs | ForEach-Object { $_.Path }) -join ', ')"
    }

    $r2 = Invoke-Installer 'msiexec.exe' @('/i', "`"$newMsi`"", '/qn', "VELOPACK_INSTALLDIR=`"$target`"")
    Write-Section "Step 2 -- MSI $NewVersion over it (wizard-equivalent location): exit $($r2.ExitCode) in $($r2.Seconds)s"
    Write-Section ''
    $after = Get-Snapshot "after MSI $NewVersion over Setup.exe $OldVersion"
    Write-Section (Format-Snapshot $after)
    if ($r2.ExitCode -ne 0) {
        Add-Finding "MSI over a Setup.exe install exited $($r2.ExitCode) on $Arch."
    }
    $onTop = @($after.Dirs | Where-Object { $_.Path -eq $target }).Count -eq 1 -and @($after.Dirs).Count -eq 1
    if (-not $onTop) {
        Add-Finding "MSI over a Setup.exe install did not land on top of it: directories after the MSI are $(($after.Dirs | ForEach-Object { $_.Path }) -join ', '). This measured two side-by-side installs, not an overwrite, so read the ARP rows below accordingly."
    }
    $visible = @($after.Arp | Where-Object { $_.SystemComponent -ne 1 })
    if ($visible.Count -gt 1 -and $onTop) {
        Add-Finding "MSI over a Setup.exe install leaves $($visible.Count) visible ARP rows ($(($visible | ForEach-Object { $_.KeyName }) -join ', ')) over a single install directory -- the reverse migration is as messy as the forward one."
    }
}

Invoke-Scenario 'Scenario 6 -- silent MSI over a silent MSI install (the winget upgrade path)' `
    'With the winget package pointing at the MSI, `winget upgrade` runs a newer MSI over the older one -- with /passive by default (winget''s silentWithProgress), /quiet only under -h. Does it stay in %LocalAppData%, leave one visible row at the new version, and run the old copy''s uninstall hook (which is what raises the "remove your data?" prompt, #245)?' {
    $target = Join-Path $env:LOCALAPPDATA 'QuickMail'
    # Step 1 stands in for any earlier silent install; step 2 uses /passive because that is
    # winget's default. Both are UI level < 5, which is the condition on Velopack's
    # quiet-install default location, so both should land in the same place.
    $r1 = Invoke-Installer 'msiexec.exe' @('/i', "`"$oldMsi`"", '/quiet', '/norestart')
    Write-Section "Step 1 -- MSI $OldVersion, silent, fresh: exit $($r1.ExitCode) in $($r1.Seconds)s"
    Write-Section ''
    $before = Get-Snapshot "after silent MSI $OldVersion"
    Write-Section (Format-Snapshot $before)
    if ($r1.ExitCode -ne 0) {
        throw "Premise failed: the first silent MSI install exited $($r1.ExitCode), so there is nothing to upgrade."
    }
    if (@($before.Dirs).Count -ne 1 -or $before.Dirs[0].Path -ne $target) {
        throw "Premise failed: the first silent MSI did not install to $target and nowhere else (scenario 1 says why). Directories found: $(($before.Dirs | ForEach-Object { $_.Path }) -join ', ')"
    }

    # What a real machine has and CI does not: a profile (without one the hook does not offer
    # to delete anything) and a start-at-sign-in entry. Both are what the upgrade must not
    # disturb (#245, #770).
    $exePath = Join-Path $target 'current\QuickMail.exe'
    Clear-HookState
    New-Item -ItemType Directory -Force (Join-Path $env:APPDATA 'QuickMail') | Out-Null
    Set-StartupEntryOff $exePath

    # Timeline of QuickMail's exe against Windows Installer's Global\_MSIExecute mutex through
    # the upgrade, sampled every 100 ms. The prompt script waits for the mutex to be gone for
    # UninstallDataPrompt.DefaultQuietSeconds before deciding "uninstalled"; this measures
    # whether the mutex ever lapses between the old copy's removal and the new copy's install.
    $sampler = Start-Job -ArgumentList $exePath -ScriptBlock {
        param($Exe)
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $last = ''
        while ($sw.Elapsed.TotalSeconds -lt 90) {
            $m = $null
            $busy = try { $f = [System.Threading.Mutex]::TryOpenExisting('Global\_MSIExecute', [System.Security.AccessControl.MutexRights]::Synchronize, [ref]$m); if ($m) { $m.Dispose() }; $f } catch [System.UnauthorizedAccessException] { $true } catch { $false }
            $state = "exe=$([int](Test-Path -LiteralPath $Exe)) msi=$([int]$busy)"
            if ($state -ne $last) { "{0,6} ms  {1}" -f $sw.ElapsedMilliseconds, $state; $last = $state }
            Start-Sleep -Milliseconds 100
        }
    }
    Start-Sleep -Seconds 2

    $log = "$PWD\msi-upgrade.log"
    $r2 = Invoke-Installer 'msiexec.exe' @('/i', "`"$newMsi`"", '/passive', '/norestart', '/l*v', "`"$log`"")
    Write-Section "Step 2 -- MSI $NewVersion, ``/passive``, over it: exit $($r2.ExitCode) in $($r2.Seconds)s"
    Write-Section ''
    $after = Get-Snapshot "after silent MSI $NewVersion over silent MSI $OldVersion"
    Write-Section (Format-Snapshot $after)

    if ($r2.ExitCode -ne 0) {
        Add-Finding "On $Arch a silent MSI upgrade exited $($r2.ExitCode). winget would report the upgrade as failed. See msi-upgrade.log."
    }
    if (@($after.Dirs).Count -ne 1 -or $after.Dirs[0].Path -ne $target) {
        Add-Finding "On $Arch a silent MSI upgrade moved the install: directories afterwards are $(($after.Dirs | ForEach-Object { $_.Path }) -join ', '), not $target alone. That is the relocation half of #554."
    }
    $visible = @($after.Arp | Where-Object { $_.SystemComponent -ne 1 })
    if ($visible.Count -ne 1) {
        Add-Finding "On $Arch a silent MSI upgrade left $($visible.Count) visible Add/Remove Programs rows ($(($visible | ForEach-Object { $_.KeyName }) -join ', ')); exactly one was expected."
    } elseif ($visible[0].DisplayVersion -ne $NewVersion) {
        Add-Finding "On $Arch after a silent MSI upgrade to $NewVersion the visible row's DisplayVersion reads '$($visible[0].DisplayVersion)'. winget correlates on it, so it would keep offering an upgrade already applied."
    }
    if (@($after.MsiProducts).Count -ne 1) {
        Add-Finding "On $Arch a silent MSI upgrade left $(@($after.MsiProducts).Count) Windows Installer product registrations; a major upgrade should leave exactly one."
    }

    # Whether the old copy was uninstalled first, read from the verbose log rather than
    # inferred. NOT from "Doing action: RemoveExistingProducts": that action runs, and returns
    # 1, on a fresh install with nothing to remove (Scenario 1's log has it too), so it proves
    # nothing. Two lines only an upgrade writes: the nested uninstall's command line, which
    # carries UPGRADINGPRODUCTCODE and REMOVE=ALL, and Velopack's uninstall hook action
    # running -- the hook that raises the "remove your data?" prompt (#245) and deletes the
    # start-at-sign-in Run entry (#770). A CI machine has no profile, so the prompt returns
    # early and is not observed here; the hook running is the evidence it would appear.
    if (Test-Path $log) {
        # msiexec writes verbose logs as UTF-16LE with a byte-order mark; let it decide.
        $text = Get-Content $log -Raw
        $nested = $text -match 'UPGRADINGPRODUCTCODE=\{[^}]+\}[^\r\n]*REMOVE=ALL'
        $hook   = $text -match 'Doing action: UninstallHookDeferred'
        Write-Section "Verbose log: nested uninstall of the old product $(if ($nested) { 'ran' } else { 'NOT found' }); Velopack's uninstall hook (``UninstallHookDeferred``) $(if ($hook) { 'ran' } else { 'NOT found' })."
        if ($nested -and $hook) {
            Write-Section "**Expected (#245, accepted):** the upgrade uninstalls $OldVersion, hook included, before installing $NewVersion."
        } elseif ($nested -or $hook) {
            Add-Finding "On $Arch a silent MSI upgrade's log has $(if ($nested) { 'the nested uninstall but not the uninstall hook' } else { 'the uninstall hook but not the nested uninstall' }). One without the other means the log markers this scenario relies on have changed; re-read msi-upgrade.log."
        } else {
            Add-Finding "On $Arch a silent MSI upgrade did not uninstall $OldVersion first. That would be a change from #245 -- check msi-upgrade.log before relying on it."
        }
    } else {
        Add-Finding "On $Arch the silent MSI upgrade wrote no verbose log, so whether it uninstalled the old copy first is unmeasured."
    }

    # The hook's decisions. The prompt script logs its decision once Windows Installer has been
    # quiet for its window, so give it time.
    $decided = Wait-HookLog 'not asking|asking' 60
    Start-Sleep -Seconds 3
    Stop-Job $sampler -ErrorAction SilentlyContinue
    $timeline = @(Receive-Job $sampler -ErrorAction SilentlyContinue)
    Remove-Job $sampler -Force -ErrorAction SilentlyContinue
    Write-Section 'Timeline through step 2 (exe = QuickMail''s current\QuickMail.exe present, msi = Windows Installer executing):'
    Write-Section ''
    Write-Section '```'
    Write-Section ($timeline -join "`n")
    Write-Section '```'
    Write-Section ''
    $hookLog = Read-HookLog
    Write-Section 'Hook log (quickmail-uninstall.log):'
    Write-Section ''
    Write-Section '```'
    Write-Section $hookLog
    Write-Section '```'
    Write-Section ''
    if (-not $decided) {
        Add-Finding "On $Arch the uninstall hook's prompt script logged no decision within 60 s of the MSI upgrade. See the hook log in Scenario 6."
    } elseif ($hookLog -match 'installed again \(an upgrade\); not asking' -and $hookLog -notmatch '(?m)T\d\d:\d\d:\d\d asking$') {
        Write-Section '**Pass:** the data prompt recognised the upgrade and did not ask.'
    } else {
        Add-Finding "On $Arch the uninstall hook's prompt script did NOT recognise the MSI upgrade -- it would have offered to delete the user's data mid-upgrade (#245). See the hook log in Scenario 6."
    }
    $entry = Get-StartupEntry
    if ($entry.Run -eq "`"$exePath`" --startup" -and $entry.FirstMarkByte -eq 3) {
        Write-Section '**Pass:** the start-at-sign-in entry came through the upgrade, Task Manager''s off mark included.'
    } else {
        Add-Finding "On $Arch the start-at-sign-in entry did not come through the MSI upgrade intact (Run value: '$($entry.Run)', off-mark first byte: '$($entry.FirstMarkByte)'; expected the original command and 3). #770's setting would change on upgrade."
    }
}

Invoke-Scenario 'Scenario 6b -- a real MSI uninstall, with a profile and start at sign-in' `
    'The other half of the hook''s decision: a genuine uninstall must still offer to remove the data, and must remove the start-at-sign-in entry and leave it removed.' {
    $target = Join-Path $env:LOCALAPPDATA 'QuickMail'
    $exePath = Join-Path $target 'current\QuickMail.exe'
    $r1 = Invoke-Installer 'msiexec.exe' @('/i', "`"$newMsi`"", '/quiet', '/norestart')
    if ($r1.ExitCode -ne 0) { throw "Premise failed: the silent MSI install exited $($r1.ExitCode)." }
    if (-not (Test-Path $exePath)) { throw "Premise failed: no $exePath after the install." }
    Clear-HookState
    New-Item -ItemType Directory -Force (Join-Path $env:APPDATA 'QuickMail') | Out-Null
    Set-StartupEntryOff $exePath

    $hidden = @(Get-ArpRows | Where-Object { $_.KeyName -match '^\{[0-9A-Fa-f-]+\}$' })
    if ($hidden.Count -ne 1) { throw "Premise failed: expected one Windows Installer product row, found $($hidden.Count)." }
    $r2 = Invoke-Installer 'msiexec.exe' @('/x', $hidden[0].KeyName, '/qn', '/norestart')
    Write-Section "``msiexec /x $($hidden[0].KeyName) /qn`` -> exit $($r2.ExitCode) in $($r2.Seconds)s"
    Write-Section ''

    # The real prompt would now be on screen -- on a runner, on a desktop nobody sees, so the
    # script logs "asking" and waits; Clear-HookState stops it.
    $decided = Wait-HookLog 'not asking|asking' 60
    $hookLog = Read-HookLog
    Write-Section 'Hook log (quickmail-uninstall.log):'
    Write-Section ''
    Write-Section '```'
    Write-Section $hookLog
    Write-Section '```'
    Write-Section ''
    if ($decided -and $hookLog -match '(?m)T\d\d:\d\d:\d\d asking$' -and $hookLog -notmatch 'not asking') {
        Write-Section '**Pass:** a genuine uninstall still offers to remove the data.'
    } else {
        Add-Finding "On $Arch a genuine MSI uninstall did not reach the data prompt (decided: $decided). The offer to remove data would silently never appear. See the hook log in Scenario 6b."
    }
    $entry = Get-StartupEntry
    if ($null -eq $entry.Run) {
        Write-Section '**Pass:** the start-at-sign-in entry is gone after the uninstall.'
    } else {
        Add-Finding "On $Arch a genuine MSI uninstall left the start-at-sign-in entry behind: '$($entry.Run)'."
    }
    Clear-HookState
}

Invoke-Scenario "Scenario 7 -- ``winget install --manifest`` on a clean machine, from a shipped release" `
    "What a new user runs: winget installs the shipped $ShippedNew MSI from installer/winget's template, from a local manifest. Does SmartScreen engage, and is that QuickMail's file reputation or the local manifest's download zone? (Upgrade is not measurable here: ``winget upgrade --manifest`` finds the installed copy through the package's catalog Id, which does not exist until the package is published -- see the plan, Phase 1d.)" {
    if (-not $script:WingetAvailable) { throw 'winget is not available on this runner.' }
    $work = Join-Path $PWD 'shipped'
    New-Item -ItemType Directory -Force $work | Out-Null
    $msi = Join-Path $work "QuickMail-$ShippedNew-$suffix.msi"
    if (-not (Test-Path $msi)) {
        Invoke-WebRequest "https://github.com/kellylford/QuickMail/releases/download/v$ShippedNew/QuickMail-$ShippedNew-$suffix.msi" -OutFile $msi -UseBasicParsing
    }
    $hash = (Get-FileHash $msi -Algorithm SHA256).Hash
    # Both installer entries need a hash to validate; only this leg's is real, and winget only
    # ever downloads this leg's.
    $other = '0' * 64
    $dir = Join-Path $work 'manifest'
    New-Item -ItemType Directory -Force $dir | Out-Null
    foreach ($f in Get-ChildItem (Join-Path $PSScriptRoot '..\installer\winget') -Filter '*.yaml') {
        $t = [IO.File]::ReadAllText($f.FullName)
        $t = $t.Replace('<VERSION>', $ShippedNew).Replace('<RELEASE-DATE>', (Get-Date -Format 'yyyy-MM-dd'))
        $t = $t.Replace('<X64-SHA256>', $(if ($Arch -eq 'x64') { $hash } else { $other }))
        $t = $t.Replace('<ARM64-SHA256>', $(if ($Arch -eq 'arm64') { $hash } else { $other }))
        [IO.File]::WriteAllText((Join-Path $dir $f.Name), $t, (New-Object Text.UTF8Encoding $false))
    }
    $null = & winget settings --enable LocalManifestFiles 2>&1

    # Each attempt is bounded, and run as its own process so a hang is a measurement rather
    # than a lost job: the first attempt at this scenario sat for 50 minutes inside winget
    # until the job timeout killed it, taking the whole report with it. On a timeout winget is
    # stopped but msiexec is NOT -- killing it mid-transaction wedges the Windows Installer
    # service for every later step (#536's harness history) -- and if one is still running the
    # final reset is skipped.
    #
    # What earlier runs of this scenario established, both architectures:
    # - Launching QuickMail's MSI from a local-manifest install, Windows starts SmartScreen
    #   (smartscreen.exe / CHXSmartScreen.exe) and msiexec never starts: winget waits until
    #   stopped. /passive and /quiet alike. Nobody on a runner can see whether a dialog is up;
    #   that SmartScreen started and msiexec did not is what is measured.
    # - Node.js LTS from the real catalog installed without that. But its download was moved
    #   to zone 2 (winget treats its catalog as trusted) while QuickMail's local-manifest
    #   download stayed in zone 3, so that control varied the zone and the file's reputation
    #   at once.
    # So the control here holds the zone equal: Node.js LTS installed from a LOCAL manifest,
    # a copy of its catalog manifest -- the same zone-3 path as QuickMail, with a file that
    # has plenty of reputation. If it waits on SmartScreen too, the zone is the trigger and a
    # catalog install of QuickMail is expected not to prompt. If it gets past SmartScreen
    # (any exit, even the 1603 Node's machine-wide install hits on the ARM64 runner),
    # QuickMail's own reputation is what SmartScreen is acting on.
    $diag = Join-Path $env:LOCALAPPDATA 'Packages\Microsoft.DesktopAppInstaller_8wekyb3d8bbwe\LocalState\DiagOutputDir'
    $logOut = Join-Path $PWD 'winget-logs'
    New-Item -ItemType Directory -Force $logOut | Out-Null
    $winget = (Get-Command winget).Source
    $common = @('--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity', '--verbose-logs')
    # The control's manifest, fetched from microsoft/winget-pkgs at its newest version.
    $nodeDir = Join-Path $work 'node-manifest'
    New-Item -ItemType Directory -Force $nodeDir | Out-Null
    $api = 'https://api.github.com/repos/microsoft/winget-pkgs/contents/manifests/o/OpenJS/NodeJS/LTS'
    $nodeVersion = (Invoke-RestMethod $api -UseBasicParsing | Where-Object { $_.type -eq 'dir' -and $_.name -match '^\d+(\.\d+)+$' } | ForEach-Object { $_.name } | Sort-Object { [version]$_ } | Select-Object -Last 1)
    foreach ($item in (Invoke-RestMethod "$api/$nodeVersion" -UseBasicParsing | Where-Object { $_.name -like '*.yaml' })) {
        Invoke-WebRequest $item.download_url -OutFile (Join-Path $nodeDir $item.name) -UseBasicParsing
    }
    if (-not (Test-Path (Join-Path $nodeDir 'OpenJS.NodeJS.LTS.installer.yaml'))) { throw "Fetching the Node.js LTS $nodeVersion manifest failed." }
    Write-Section "Control: Node.js LTS $nodeVersion, manifest copied from microsoft/winget-pkgs and installed with ``--manifest``."
    Write-Section ''

    $attempts = [ordered]@{
        'a -- QuickMail, local manifest' = @{ Args = @('install', '--manifest', "`"$dir`"") + $common; Seconds = 180 }
        'd -- control: Node.js LTS, local manifest copied from the catalog' = @{ Args = @('install', '--manifest', "`"$nodeDir`"") + $common; Seconds = 300 }
    }
    $target = Join-Path $env:LOCALAPPDATA 'QuickMail'
    $n = 0
    foreach ($label in $attempts.Keys) {
        $n++
        $spec = $attempts[$label]
        $isQuickMail = $label -like '*QuickMail*'
        if ($isQuickMail) { $null = Reset-Machine }
        $logsBefore = @(Get-ChildItem $diag -Filter '*.log' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)
        $stdout = Join-Path $work "winget-attempt$n.out.txt"
        $began = Get-Date
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $proc = Start-Process -FilePath $winget -ArgumentList $spec.Args -PassThru -NoNewWindow -RedirectStandardOutput $stdout
        # Touch the handle at once: without it, Start-Process -PassThru leaves ExitCode empty
        # after a timed WaitForExit (checked: `winget --version` reported no exit code at all).
        $null = $proc.Handle
        $finished = $proc.WaitForExit($spec.Seconds * 1000)
        if ($finished) { $proc.WaitForExit() }
        $sw.Stop()
        $spawned = @()
        if (-not $finished) {
            # Everything started since this attempt began: whatever winget is waiting on, a
            # prompt or a child, is in here.
            $spawned = @(Get-CimInstance Win32_Process | Where-Object { $_.CreationDate -ge $began } | ForEach-Object { "$($_.ProcessId) (parent $($_.ParentProcessId)) $($_.Name) $($_.CommandLine)" })
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
            # The unanswered prompt outlives winget and blocked the next attempt's launch on an
            # earlier run (and, on ARM64, the rest of the job). Clear it.
            Get-Process smartscreen, CHXSmartScreen -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
            if (Get-Process msiexec -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -ge $began }) { $script:SkipFinalReset = $true }
        }
        Get-Process QuickMail -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        $code = if ($finished) { $proc.ExitCode } else { $null }

        $newLogs = @(Get-ChildItem $diag -Filter '*.log' -ErrorAction SilentlyContinue | Where-Object { $logsBefore -notcontains $_.FullName })
        foreach ($l in $newLogs) { Copy-Item $l.FullName (Join-Path $logOut "attempt$n-$($l.Name)") }
        if (Test-Path $stdout) { Copy-Item $stdout (Join-Path $logOut "attempt$n.out.txt") }
        $raw = if (Test-Path $stdout) { Get-Content $stdout -Raw } else { '' }
        # winget redraws progress with carriage returns; keep each line's last state, and only
        # lines with something to read in them (the spinner and bar are punctuation).
        $clean = (("$raw" -split "`r?`n") | ForEach-Object { ($_ -split "`r")[-1].Trim() } | Where-Object { $_ -match '[A-Za-z0-9]' }) -join "`n"

        Write-Section "### Attempt $label"
        Write-Section ''
        if ($finished) {
            Write-Section "Exit $code (0x$('{0:X8}' -f $code)) in $([math]::Round($sw.Elapsed.TotalSeconds, 1))s."
        } else {
            Write-Section "**Did not finish in $($spec.Seconds) seconds** and was stopped. Processes started during the attempt:"
            Write-Section ''
            Write-Section '```'
            Write-Section ($spawned -join "`n")
            Write-Section '```'
        }
        Write-Section ''
        Write-Section '```'
        Write-Section $clean
        Write-Section '```'
        Write-Section ''
        $cmdLines = @($newLogs | ForEach-Object { Get-Content $_.FullName } | Where-Object { $_ -match 'Installer args|Starting:|Installer \[.*best choice|Successfully installed|failed|exit code|ShellExecute' } | Select-Object -Last 10)
        if ($cmdLines.Count) {
            Write-Section 'From winget''s verbose log (full logs in the `winget-logs` artifact):'
            Write-Section ''
            Write-Section '```'
            Write-Section ($cmdLines -join "`n")
            Write-Section '```'
            Write-Section ''
        }

        if ($isQuickMail) {
            $after = Get-Snapshot "after attempt $label"
            Write-Section (Format-Snapshot $after)
            $vis = @($after.Arp | Where-Object { $_.SystemComponent -ne 1 })
            $smartScreenRan = @($spawned | Where-Object { $_ -match 'smartscreen' }).Count -gt 0
            if (-not $finished -and $smartScreenRan) {
                Write-Section "**Expected:** waiting on a SmartScreen prompt, as on every earlier run. Not a finding by itself; see the plan's Phase 1d."
            } elseif (-not $finished) {
                Add-Finding "On $Arch, ``winget install --manifest`` attempt $label hung for $($spec.Seconds) seconds$(if ($smartScreenRan) { ' (SmartScreen ran)' } else { ' with no SmartScreen process -- a different hang from the known one' }). See Scenario 7."
            } elseif ($code -ne 0) {
                Add-Finding "On $Arch, ``winget install --manifest`` attempt $label exited 0x$('{0:X8}' -f $code)."
            } elseif ($vis.Count -ne 1 -or $vis[0].KeyName -ne 'MSI:QuickMail' -or $vis[0].DisplayVersion -ne $ShippedNew -or @($after.Dirs).Count -ne 1 -or $after.Dirs[0].Path -ne $target) {
                Add-Finding "On $Arch, attempt $label exited 0 but left visible rows [$(($vis | ForEach-Object { "$($_.KeyName) $($_.DisplayVersion)" }) -join ', ')] and directories [$(($after.Dirs | ForEach-Object { $_.Path }) -join ', ')]; expected one MSI:QuickMail $ShippedNew row and $target alone."
            } else {
                Write-Section "**Pass ($label):** one visible ``MSI:QuickMail`` row at $ShippedNew, installed in ``%LocalAppData%\QuickMail`` only."
            }
        } else {
            $controlSmartScreen = @($spawned | Where-Object { $_ -match 'smartscreen' }).Count -gt 0
            $verdict = if (-not $finished -and $controlSmartScreen) {
                'Node.js from a local manifest ALSO waited on SmartScreen -- with the zone held equal, a popular file is treated the same, so the zone is the trigger, and a catalog install of QuickMail (moved to zone 2 like the catalog Node.js was) is expected not to prompt'
            } elseif (-not $finished) {
                'Node.js from a local manifest hung WITHOUT SmartScreen starting -- a different hang; this control says nothing about the zone'
            } else {
                "Node.js from a local manifest got past SmartScreen (exit 0x$('{0:X8}' -f $code)) -- with the zone held equal, QuickMail's own file reputation is what SmartScreen acts on, so a catalog install may well prompt too"
            }
            Write-Section "**Control:** $verdict."
            if (-not $finished -and -not $controlSmartScreen) { Add-Finding "On $Arch the control: $verdict." }
        }
        Write-Section ''
    }
}

# --- report --------------------------------------------------------------------------

# Skipped after a stopped winget: an msiexec may still hold the Windows Installer service,
# and a reset that blocks on it would lose this report to the job timeout.
if (-not (Get-Variable SkipFinalReset -Scope Script -ErrorAction SilentlyContinue)) { $null = Reset-Machine }

$header = @()
$header += "# QuickMail installer path matrix -- $Arch"
$header += ''
$header += "Generated by ``scripts/winget-install-matrix.ps1`` on a GitHub-hosted runner. Each scenario resets the machine first and states, in its own section, whether that reset actually left it clean."
$header += ''
if ($script:Aborted -gt 0) {
    $header += "**$($script:Aborted) scenario(s) aborted.** The matrix below is incomplete; do not cite it as covering every path."
    $header += ''
}
if ($script:Findings.Count) {
    $header += '## Findings'
    $header += ''
    foreach ($f in $script:Findings) { $header += "- $f" }
} else {
    $header += '## Findings'
    $header += ''
    $header += '- None. Every scenario behaved as the winget plan assumes.'
}
$header += ''
$header += '---'
$header += ''

$body = ($header + $script:Sections) -join "`n"
$body | Out-File -FilePath $Report -Encoding utf8
Write-Host "`nReport written to $Report"
if ($env:GITHUB_STEP_SUMMARY) { $body | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Encoding utf8 -Append }

# Decide the exit code explicitly, and never let it be decided for us. GitHub's pwsh handler
# appends `exit $LASTEXITCODE`, and the last thing to set $LASTEXITCODE here is
# `cmd /c winget ...` -- winget exits non-zero when a list comes back empty. Leaving it
# implicit means the step's pass/fail is settled by whether some unrelated package on the
# runner happened to have an update available, which has already failed one run.
#
# Findings are data, not failures: recording misbehaviour is the entire job. An aborted
# scenario is different -- it measured nothing, and a green step would advertise a matrix
# with a hole in it.
if ($script:Aborted -gt 0) {
    Write-Host "$($script:Aborted) scenario(s) aborted; failing the step so the gap is not mistaken for coverage."
    exit 1
}
exit 0

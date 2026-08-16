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
        [void]$sb.AppendLine('| Hive | Key | DisplayName | DisplayVersion | Hidden | InstallLocation | QuietUninstallString |')
        [void]$sb.AppendLine('| --- | --- | --- | --- | --- | --- | --- |')
        foreach ($r in $Snap.Arp) {
            $hidden = if ($r.SystemComponent -eq 1) { 'yes' } else { 'no' }
            $loc = if ($r.InstallLocation) { $r.InstallLocation.Replace($env:LOCALAPPDATA, '%LocalAppData%') } else { '' }
            $q = if ($r.QuietUninstallString) { '`' + $r.QuietUninstallString.Replace($env:LOCALAPPDATA, '%LocalAppData%') + '`' } else { '--' }
            [void]$sb.AppendLine("| $($r.Scope) | ``$($r.KeyName)`` | $($r.DisplayName) | $($r.DisplayVersion) | $hidden | $loc | $q |")
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
    'Does `msiexec /qn` land in C:\QuickMail on this architecture too (issue #554 was only ever measured on ARM64)?' {
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
        Add-Finding "On $Arch a silent MSI install landed in %LocalAppData% -- the opposite of the ARM64 Phase 1 result. Issue #554 may be fixed in this vpk, or architecture-specific."
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
    } elseif ($visible[0].DisplayVersion -ne $NewVersion) {
        Add-Finding "After upgrading to $NewVersion the ARP DisplayVersion still reads '$($visible[0].DisplayVersion)'. winget correlates on this value, so it would keep offering an upgrade that has already been applied."
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

# --- report --------------------------------------------------------------------------

$null = Reset-Machine

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

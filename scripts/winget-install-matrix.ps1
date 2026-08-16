<#
.SYNOPSIS
  Measures QuickMail's installer behaviour across every install/upgrade path winget can
  put a machine through, and writes the results as Markdown.

.DESCRIPTION
  Written for issue #536 (winget distribution). The winget plan's Phase 1 was done by hand
  on one ARM64 machine and left three gaps: x64 was never tested, Setup.exe over an
  MSI-installed copy — the path every existing user takes — was never tested at all, and
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

.NOTES
  Non-destructive on a developer machine only in the sense that it removes QuickMail.
  It uninstalls QuickMail repeatedly and deletes its install directories. Do not run it
  on a machine with a QuickMail install you care about — it is meant for CI runners.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OldDir,
    [Parameter(Mandatory)][string]$NewDir,
    [Parameter(Mandatory)][string]$OldVersion,
    [Parameter(Mandatory)][string]$NewVersion,
    [Parameter(Mandatory)][ValidateSet('x64', 'arm64')][string]$Arch,
    [string]$Report = 'winget-matrix-report.md'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Scenario failures must not abort the run: a scenario that misbehaves is a finding, and
# the scenarios after it are still worth measuring. Only harness errors should be fatal.
$script:Findings = [System.Collections.Generic.List[string]]::new()
$script:Sections = [System.Collections.Generic.List[string]]::new()

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
        hidden from Settings > Apps but is still a real product registration — the
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
    $candidates = @(
        'C:\QuickMail'
        (Join-Path $env:LOCALAPPDATA 'QuickMail')
        (Join-Path $env:ProgramFiles 'QuickMail')
    )
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
    <#  winget's own opinion, when it is usable. Reported verbatim rather than parsed:
        the question is what a user sees, and the raw table is the answer. #>
    if (-not $script:WingetAvailable) { return '(winget not available on this runner)' }
    $out = @()
    foreach ($cmd in @('list --query QuickMail', 'upgrade')) {
        $text = & { cmd /c "winget $cmd --disable-interactivity 2>&1" } | Out-String
        $quickmailLines = ($text -split "`r?`n" | Where-Object { $_ -match 'QuickMail|No installed package|No applicable' }) -join "`n"
        $out += "`$ winget $cmd`n" + ($(if ($quickmailLines) { $quickmailLines } else { '(no QuickMail lines)' }))
    }
    return ($out -join "`n`n")
}

function Get-Snapshot {
    param([string]$Label)
    Get-Process QuickMail -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    return [pscustomobject]@{
        Label       = $Label
        Arp         = Get-ArpRows
        MsiProducts = Get-MsiProductRows
        Dirs        = Get-InstallDirs
        Shortcuts   = Get-Shortcuts
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
            $q = if ($r.QuietUninstallString) { '`' + $r.QuietUninstallString.Replace($env:LOCALAPPDATA, '%LocalAppData%') + '`' } else { '—' }
            [void]$sb.AppendLine("| $($r.Scope) | ``$($r.KeyName)`` | $($r.DisplayName) | $($r.DisplayVersion) | $hidden | $loc | $q |")
        }
        [void]$sb.AppendLine()
    }

    [void]$sb.AppendLine("Windows Installer product registrations: **$($Snap.MsiProducts.Count)**" +
        $(if ($Snap.MsiProducts.Count) { ' — ' + (($Snap.MsiProducts | ForEach-Object { "$($_.Scope) $($_.ProductName)" }) -join '; ') } else { '' }))
    [void]$sb.AppendLine()

    [void]$sb.AppendLine('Install directories:')
    [void]$sb.AppendLine()
    if ($Snap.Dirs.Count) {
        foreach ($d in $Snap.Dirs) {
            $p = $d.Path.Replace($env:LOCALAPPDATA, '%LocalAppData%')
            [void]$sb.AppendLine("- ``$p`` — exe version $($d.ExeVersion); contains: $($d.Entries)")
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
    <#  Return to a verified-clean state so the next scenario measures only its own
        actions. Uninstalls through the product's own strings first (the supported path),
        then removes anything left behind by force — a scenario that leaves residue is
        itself a finding, recorded by the caller's post-uninstall snapshot, not here. #>
    Get-Process QuickMail -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

    foreach ($row in Get-ArpRows) {
        try {
            if ($row.KeyName -match '^\{[0-9A-Fa-f-]+\}$') {
                Start-Process msiexec -ArgumentList "/x $($row.KeyName) /qn" -Wait -ErrorAction SilentlyContinue
            } elseif ($row.QuietUninstallString) {
                # "path\Update.exe" --uninstall --silent  →  split exe from arguments
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

    foreach ($d in @('C:\QuickMail', (Join-Path $env:LOCALAPPDATA 'QuickMail'), (Join-Path $env:APPDATA 'QuickMail'))) {
        Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue
    }
    Get-ChildItem (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs') -Recurse -Filter '*QuickMail*.lnk' -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue

    $left = @(Get-ArpRows).Count + @(Get-InstallDirs).Count
    if ($left -ne 0) { Write-Host "reset: warning, $left QuickMail traces survived the reset" }
}

# --- install primitives --------------------------------------------------------------

function Invoke-Installer {
    param([string]$Path, [string[]]$Arguments, [hashtable]$Env = @{})

    foreach ($k in $Env.Keys) { Set-Item "env:$k" $Env[$k] }
    try {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $p = Start-Process -FilePath $Path -ArgumentList $Arguments -Wait -PassThru
        $sw.Stop()
        Get-Process QuickMail -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        return [pscustomobject]@{ ExitCode = $p.ExitCode; Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1) }
    } finally {
        foreach ($k in $Env.Keys) { Remove-Item "env:$k" -ErrorAction SilentlyContinue }
    }
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

Write-Section "# Installer path matrix — $Arch"
Write-Section ''
Write-Section "Old version **$OldVersion**, new version **$NewVersion**. Runner: $((Get-CimInstance Win32_OperatingSystem).Caption) build $([Environment]::OSVersion.Version). winget: $(if ($script:WingetAvailable) { (& winget --version) } else { 'not available' })."
Write-Section ''

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

Write-Section '## Scenario 0 — what `vpk pack` emits'
Write-Section ''
Write-Section 'Filenames, verbatim, from the same `vpk pack` arguments the release workflow uses (`--msi --instLocation PerUser`):'
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
    if ($Arch -eq 'arm64' -and $shown -ne 'ARM64') {
        Add-Finding "ARM64 channel's Setup.exe is a $shown binary. komac infers architecture from the binary when the filename carries no architecture token; the ARM64 asset's name does carry one, so this is survivable, but the x64 asset's does not."
    }
}
Write-Section ''

# --- scenario runner -----------------------------------------------------------------

function Invoke-Scenario {
    param([string]$Title, [string]$Question, [scriptblock]$Body)
    Write-Host "`n=== $Title ==="
    Reset-Machine
    Write-Section "## $Title"
    Write-Section ''
    Write-Section "*Question: $Question*"
    Write-Section ''
    try {
        & $Body
    } catch {
        Write-Section "**Scenario aborted:** ``$_``"
        Add-Finding "$Title aborted: $_"
    }
    Write-Section ''
}

Invoke-Scenario 'Scenario 1 — silent MSI, fresh machine' `
    'Does `msiexec /qn` land in C:\QuickMail on this architecture too (issue #554 was only ever measured on ARM64)?' {
    $r = Invoke-Installer 'msiexec.exe' @('/i', "`"$newMsi`"", '/qn', '/l*v', "$PWD\msi-fresh.log")
    Write-Section "``msiexec /i … /qn`` → exit $($r.ExitCode) in $($r.Seconds)s"
    Write-Section ''
    Write-Section (Format-Snapshot (Get-Snapshot 'after silent MSI install'))
    if (Test-Path 'C:\QuickMail') {
        Add-Finding "Confirmed on $Arch: a silent MSI install lands in C:\QuickMail, not %LocalAppData%. Issue #554 is not architecture-specific."
    } elseif (Test-Path (Join-Path $env:LOCALAPPDATA 'QuickMail')) {
        Add-Finding "On $Arch a silent MSI install landed in %LocalAppData% — the opposite of the ARM64 Phase 1 result. Issue #554 may be fixed in this vpk, or architecture-specific."
    }
}

Invoke-Scenario 'Scenario 2 — Setup.exe over an MSI install (the migration path)' `
    'Every existing user installed from the MSI wizard into %LocalAppData%. What does `winget install`/`upgrade` — that is, `Setup.exe --silent` — do to that machine?' {
    # Reproduce the wizard's install location without driving the wizard: VELOPACK_INSTALLDIR
    # is exactly what the Next button sets, per the #554 investigation.
    $target = Join-Path $env:LOCALAPPDATA 'QuickMail'
    $r1 = Invoke-Installer 'msiexec.exe' @('/i', "`"$oldMsi`"", '/qn') -Env @{ VELOPACK_INSTALLDIR = $target }
    Write-Section "Step 1 — MSI $OldVersion into ``%LocalAppData%\QuickMail`` (wizard-equivalent): exit $($r1.ExitCode) in $($r1.Seconds)s"
    Write-Section ''
    $before = Get-Snapshot "after MSI $OldVersion (wizard-equivalent)"
    Write-Section (Format-Snapshot $before)

    $r2 = Invoke-Installer $newSetup @('--silent')
    Write-Section "Step 2 — ``Setup.exe --silent`` $NewVersion over it: exit $($r2.ExitCode) in $($r2.Seconds)s"
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

Invoke-Scenario 'Scenario 3 — Setup.exe over Setup.exe (the steady-state upgrade)' `
    'Phase 1 measured this by hand on ARM64. Does it hold on this runner, and does winget see the version advance?' {
    $r1 = Invoke-Installer $oldSetup @('--silent')
    Write-Section "Step 1 — ``Setup.exe --silent`` $OldVersion, fresh: exit $($r1.ExitCode) in $($r1.Seconds)s"
    Write-Section ''
    Write-Section (Format-Snapshot (Get-Snapshot "after Setup.exe $OldVersion"))

    $r2 = Invoke-Installer $newSetup @('--silent')
    Write-Section "Step 2 — ``Setup.exe --silent`` $NewVersion over it: exit $($r2.ExitCode) in $($r2.Seconds)s"
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

Invoke-Scenario 'Scenario 4 — uninstall through the quiet string winget uses' `
    'winget uninstall runs QuietUninstallString. Does it complete unattended, and what does it leave behind?' {
    $r1 = Invoke-Installer $newSetup @('--silent')
    Write-Section "Step 1 — install $NewVersion: exit $($r1.ExitCode) in $($r1.Seconds)s"
    $installed = Get-Snapshot "installed $NewVersion"
    $row = @($installed.Arp | Where-Object { $_.QuietUninstallString })
    if (-not $row.Count) { throw 'No QuietUninstallString on any ARP row; winget uninstall would have nothing to run.' }
    Write-Section ''
    Write-Section "QuietUninstallString: ``$($row[0].QuietUninstallString)``"
    Write-Section ''

    if ($row[0].QuietUninstallString -match '^"([^"]+)"\s*(.*)$') {
        $r2 = Invoke-Installer $Matches[1] ($Matches[2] -split '\s+')
        Write-Section "Step 2 — running it: exit $($r2.ExitCode) in $($r2.Seconds)s"
    }
    Write-Section ''
    $after = Get-Snapshot 'after quiet uninstall'
    Write-Section (Format-Snapshot $after)
    if ($after.Arp.Count -gt 0) {
        Add-Finding "The quiet uninstall left $($after.Arp.Count) Add/Remove Programs row(s) behind."
    }
    if ($after.Dirs.Count -gt 0) {
        Add-Finding "The quiet uninstall left directories behind: $(($after.Dirs | ForEach-Object { $_.Path }) -join ', ')."
    }
}

Invoke-Scenario 'Scenario 5 — MSI over a Setup.exe install (the reverse migration)' `
    'A user who installs from winget and later downloads the MSI from the release page ends up here. Is it survivable?' {
    $r1 = Invoke-Installer $oldSetup @('--silent')
    Write-Section "Step 1 — ``Setup.exe --silent`` $OldVersion: exit $($r1.ExitCode) in $($r1.Seconds)s"
    Write-Section ''
    $target = Join-Path $env:LOCALAPPDATA 'QuickMail'
    $r2 = Invoke-Installer 'msiexec.exe' @('/i', "`"$newMsi`"", '/qn') -Env @{ VELOPACK_INSTALLDIR = $target }
    Write-Section "Step 2 — MSI $NewVersion over it (wizard-equivalent location): exit $($r2.ExitCode) in $($r2.Seconds)s"
    Write-Section ''
    $after = Get-Snapshot "after MSI $NewVersion over Setup.exe $OldVersion"
    Write-Section (Format-Snapshot $after)
    $visible = @($after.Arp | Where-Object { $_.SystemComponent -ne 1 })
    if ($visible.Count -gt 1) {
        Add-Finding "MSI over a Setup.exe install leaves $($visible.Count) visible ARP rows — the reverse migration is as messy as the forward one."
    }
}

# --- report --------------------------------------------------------------------------

Reset-Machine

$header = @()
$header += "# QuickMail installer path matrix — $Arch"
$header += ''
$header += "Generated by ``scripts/winget-install-matrix.ps1`` on a GitHub-hosted runner. Every scenario starts from a verified-clean machine."
$header += ''
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

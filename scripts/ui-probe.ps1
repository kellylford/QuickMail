<#
.SYNOPSIS
  UI probe orchestrator (#180 Phase 3): seed a fixture profile, launch QuickMail
  once per probe-plan entry to capture a screenshot of each surface, and collect
  the PNGs into a run folder.

.DESCRIPTION
  Steps:
    1. Build (unless -NoBuild) the app and the fixture generator.
    2. Generate a deterministic fixture profile into a temp dir (or -ProfileDir).
    3. For each (surface, theme, scale) in the plan, run
         QuickMail.exe --ui-probe <surface> --theme <theme> --text-scale <scale>
                       --profileDir <fixture> --capture-dir <run>
       with a bounded timeout. A hang or non-zero exit fails that entry, not the run.
    4. Print a summary and exit non-zero if any entry failed or produced no PNG.

  AI review (#180 Phase 4) runs over the run folder afterwards using
  scripts/ui-review-prompt.md — from Claude Code:  "review the run folder at <path>
  with scripts/ui-review-prompt.md". This script stops at the collection step so the
  orchestration stays deterministic and the judgment stays in the model.

.PARAMETER Plan     Path to a probe plan JSON (default scripts/ui-probe-plan.json).
.PARAMETER RunDir   Output folder for PNGs (default <temp>\qm-ui-probe\<timestamp>).
.PARAMETER ProfileDir  Existing fixture profile to reuse (default: generate fresh).
.PARAMETER TimeoutSeconds  Per-probe timeout (default 45).
.PARAMETER NoBuild  Skip dotnet build (use existing Debug binaries).
#>
[CmdletBinding()]
param(
    [string]$Plan = "$PSScriptRoot\ui-probe-plan.json",
    [string]$RunDir = "",
    [string]$ProfileDir = "",
    [int]$TimeoutSeconds = 45,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$exe      = Join-Path $repoRoot 'QuickMail\bin\Debug\QuickMail.exe'
$fixtures = Join-Path $repoRoot 'Tools\QuickMail.Fixtures\QuickMail.Fixtures.csproj'

if (-not $RunDir) {
    $RunDir = Join-Path $env:TEMP ("qm-ui-probe\" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
New-Item -ItemType Directory -Force $RunDir | Out-Null

if (-not $NoBuild) {
    Write-Host "Building QuickMail (Debug) and the fixture generator..."
    dotnet build (Join-Path $repoRoot 'QuickMail\QuickMail.csproj') -c Debug --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "QuickMail build failed." }
    dotnet build $fixtures -c Debug --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Fixture generator build failed." }
}
if (-not (Test-Path $exe)) { throw "Not found: $exe (build first or drop -NoBuild)" }

if (-not $ProfileDir) {
    $ProfileDir = Join-Path $RunDir 'profile'
    Write-Host "Seeding fixture profile at $ProfileDir ..."
    dotnet run --project $fixtures -c Debug --no-build -- --out $ProfileDir
    if ($LASTEXITCODE -ne 0) { throw "Fixture generation failed." }
}
if (-not (Test-Path (Join-Path $ProfileDir 'mail.db'))) {
    throw "Fixture profile at $ProfileDir has no mail.db."
}

# A locked session means DWM never composites new windows: every capture would
# come out white. Fail fast with a clear message instead of a folder of blanks.
if (Get-Process LogonUI -ErrorAction SilentlyContinue) {
    throw "The desktop session is locked. WPF windows cannot render on the secure desktop, so probe captures would be blank. Unlock the machine (or use an unlocked/virtual desktop session) and retry."
}

$planEntries = (Get-Content $Plan -Raw | ConvertFrom-Json).entries
Write-Host ("Probe plan: {0} entries -> {1}" -f $planEntries.Count, $RunDir)

$failed = @()
$index  = 0
foreach ($entry in $planEntries) {
    $index++
    $tag = "{0:D2}-{1}-{2}-{3}" -f $index, $entry.surface, $entry.theme, $entry.scale
    Write-Host ("[{0}/{1}] {2}" -f $index, $planEntries.Count, $tag)

    # Quote path arguments explicitly: Windows PowerShell 5.1's Start-Process
    # joins the array without quoting, so a space in TEMP would split the args.
    $args = @(
        '--ui-probe', $entry.surface,
        '--theme', $entry.theme,
        '--text-scale', "$($entry.scale)",
        '--profileDir', ('"{0}"' -f $ProfileDir),
        '--capture-dir', ('"{0}"' -f $RunDir),
        '--capture-tag', $tag
    )
    $proc = Start-Process -FilePath $exe -ArgumentList $args -PassThru
    $exited = $proc.WaitForExit($TimeoutSeconds * 1000)
    if (-not $exited) {
        try { $proc.Kill() } catch {}
        $failed += "$tag (timeout after ${TimeoutSeconds}s)"
        continue
    }
    if ($proc.ExitCode -ne 0) {
        $failed += "$tag (exit code $($proc.ExitCode))"
        continue
    }
    $png = Get-ChildItem $RunDir -Filter "$tag*.png" -ErrorAction SilentlyContinue
    if (-not $png) {
        $failed += "$tag (exited 0 but produced no PNG)"
    }
}

$shots = (Get-ChildItem $RunDir -Filter '*.png').Count
Write-Host ""
Write-Host ("Run complete: {0}/{1} entries produced shots ({2} PNGs) in {3}" -f `
    ($planEntries.Count - $failed.Count), $planEntries.Count, $shots, $RunDir)
if ($failed.Count -gt 0) {
    Write-Host "FAILED entries:"
    $failed | ForEach-Object { Write-Host "  $_" }
    exit 1
}
Write-Host "Next: AI review — run the checklist in scripts/ui-review-prompt.md over $RunDir"
exit 0

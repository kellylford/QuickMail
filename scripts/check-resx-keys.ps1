<#
.SYNOPSIS
  Resource parity check: diffs the key set of every Strings.<culture>.resx satellite
  file against the neutral Strings.resx, so you can see what's missing/extra before
  enabling a language in the Settings picker (see localization-pm-dev-spec.md, Section 11).

.EXAMPLE
  .\scripts\check-resx-keys.ps1
#>
param(
    [string]$ResourcesDir = (Join-Path $PSScriptRoot "..\QuickMail\Resources")
)

$ErrorActionPreference = 'Stop'

function Get-ResxKeys([string]$Path) {
    [xml]$resx = Get-Content -Path $Path -Raw -Encoding UTF8
    $keys = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($node in $resx.root.data) {
        if ($node.name) { [void]$keys.Add($node.name) }
    }
    return $keys
}

$neutralPath = Join-Path $ResourcesDir "Strings.resx"
if (-not (Test-Path $neutralPath)) {
    Write-Error "Neutral catalog not found: $neutralPath"
    exit 1
}

$neutralKeys = Get-ResxKeys $neutralPath
Write-Host "Neutral (Strings.resx): $($neutralKeys.Count) keys`n"

$languageFiles = Get-ChildItem -Path $ResourcesDir -Filter "Strings.*.resx" | Sort-Object Name
if ($languageFiles.Count -eq 0) {
    Write-Host "No language satellite files found (Strings.<culture>.resx)."
    exit 0
}

$anyProblems = $false

foreach ($file in $languageFiles) {
    $culture = $file.BaseName -replace '^Strings\.', ''
    $langKeys = Get-ResxKeys $file.FullName

    $missingSet = [System.Collections.Generic.HashSet[string]]::new([string[]]@($neutralKeys))
    $missingSet.ExceptWith([string[]]@($langKeys))
    $missing = @(@($missingSet) | Sort-Object)

    $extraSet = [System.Collections.Generic.HashSet[string]]::new([string[]]@($langKeys))
    $extraSet.ExceptWith([string[]]@($neutralKeys))
    $extra = @(@($extraSet) | Sort-Object)

    Write-Host "=== $culture ($($file.Name)) : $($langKeys.Count) keys ==="
    if ($missing.Count -eq 0 -and $extra.Count -eq 0) {
        Write-Host "  OK - full parity with neutral catalog."
    } else {
        $anyProblems = $true
        if ($missing.Count -gt 0) {
            Write-Host "  MISSING ($($missing.Count)) - present in neutral, absent here (falls back to English at runtime):"
            $missing | ForEach-Object { Write-Host "    - $_" }
        }
        if ($extra.Count -gt 0) {
            Write-Host "  EXTRA ($($extra.Count)) - present here but not in neutral (stale/orphaned key):"
            $extra | ForEach-Object { Write-Host "    - $_" }
        }
    }
    Write-Host ""
}

if ($anyProblems) {
    Write-Host "Result: parity issues found. A language with missing keys still runs safely (falls back to English per-key), but should not be enabled in the Settings picker until reviewed."
    exit 1
} else {
    Write-Host "Result: all language files have full key parity with the neutral catalog."
    exit 0
}

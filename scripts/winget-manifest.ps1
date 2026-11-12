<#
.SYNOPSIS
    Writes the three winget manifests for one QuickMail release from the template in
    installer/winget/.

.DESCRIPTION
    The template is the vetted record of every choice in QuickMail's winget package (see
    installer/winget/README.md). This fills its placeholders, strips its explanatory comments
    (keeping each file's schema line, as the published manifests do), and refuses to write
    anything that breaks the rules those choices depend on: no UpgradeCode, no
    installer-level ProductCode, no Setup.exe, RequireExplicitUpgrade on, the MSI installer.

    Deliberately not wingetcreate or Komac: both analyse the MSI and add an UpgradeCode and a
    ProductCode, which make winget offer an upgrade that self-update applied long ago.

    Used by .github/workflows/winget-submit.yml on every release, and by hand the same way.

.EXAMPLE
    pwsh scripts/winget-manifest.ps1 -Version 0.8.71 -X64Sha256 dd7e... -Arm64Sha256 cfc7... -ReleaseDate 2026-11-11 -OutDir out/0.8.71
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $X64Sha256,
    [Parameter(Mandatory)] [string] $Arm64Sha256,
    [Parameter(Mandatory)] [string] $ReleaseDate,
    [Parameter(Mandatory)] [string] $OutDir,
    [string] $TemplateDir = (Join-Path $PSScriptRoot '..' 'installer' 'winget')
)

$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version '$Version' is not X.Y.Z (no leading v)." }
foreach ($h in @($X64Sha256, $Arm64Sha256)) {
    if ($h -notmatch '^[0-9A-Fa-f]{64}$') { throw "'$h' is not a SHA256 hash." }
}
if ($X64Sha256 -eq $Arm64Sha256) { throw 'The x64 and arm64 hashes are identical; one MSI was hashed twice.' }
if ($ReleaseDate -notmatch '^\d{4}-\d{2}-\d{2}$') { throw "ReleaseDate '$ReleaseDate' is not YYYY-MM-DD." }

$files = 'KellyLford.QuickMail.yaml', 'KellyLford.QuickMail.installer.yaml', 'KellyLford.QuickMail.locale.en-US.yaml'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

foreach ($name in $files) {
    $source = Join-Path $TemplateDir $name
    $kept = foreach ($line in (Get-Content -LiteralPath $source -Encoding utf8)) {
        # Keep the schema line; drop every other comment line. No value in these manifests
        # contains a line that starts with '#', so this cannot cut into a value.
        if ($line -match '^\s*#' -and $line -notmatch '^# yaml-language-server:') { continue }
        $line.TrimEnd()
    }
    $text = ($kept -join "`n") + "`n"
    $text = $text.Replace('<VERSION>', $Version).
                  Replace('<X64-SHA256>', $X64Sha256.ToUpperInvariant()).
                  Replace('<ARM64-SHA256>', $Arm64Sha256.ToUpperInvariant()).
                  Replace('<RELEASE-DATE>', $ReleaseDate)

    if ($text -match '<[A-Z0-9-]+>') { throw "$name still has a placeholder: $($Matches[0])" }

    if ($name -like '*.installer.yaml') {
        # The rules installer/winget/README.md explains. Breaking any of them gives users
        # endless phantom upgrades or a duplicate install, so fail rather than submit.
        if ($text -match 'UpgradeCode') { throw 'Installer manifest has an UpgradeCode.' }
        if ($text -match '(?i)setup\.exe') { throw 'Installer manifest points at Setup.exe.' }
        $productCodes = [regex]::Matches($text, '(?m)^\s*ProductCode:.*$') | ForEach-Object { $_.Value.Trim() }
        if (@($productCodes).Count -ne 1 -or $productCodes -ne 'ProductCode: MSI:QuickMail') {
            throw "Installer manifest must have exactly one ProductCode, MSI:QuickMail, in AppsAndFeaturesEntries; found: $($productCodes -join ', ')"
        }
        foreach ($required in 'InstallerType: wix', 'Scope: user', 'RequireExplicitUpgrade: true', 'UpgradeBehavior: install') {
            if ($text -notmatch "(?m)^$([regex]::Escape($required))$") { throw "Installer manifest is missing '$required'." }
        }
        foreach ($asset in "QuickMail-$Version-win.msi", "QuickMail-$Version-win-arm64.msi") {
            $url = "https://github.com/kellylford/QuickMail/releases/download/v$Version/$asset"
            if (-not $text.Contains("InstallerUrl: $url")) { throw "Installer manifest has no InstallerUrl $url." }
        }
    }

    $target = Join-Path $OutDir $name
    [System.IO.File]::WriteAllText($target, $text, [System.Text.UTF8Encoding]::new($false))
    Write-Host "Wrote $target"
}

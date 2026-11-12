<#
.SYNOPSIS
    Checks scripts/winget-manifest.ps1 and the installer/winget template on every pull
    request, so a template or script change that breaks the winget submission fails here and
    not after a release is already out. Run by the winget-manifest job in quickmail.yml.

    pwsh scripts/winget-manifest.tests.ps1     # exits with the number of failures
#>
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'winget-manifest.ps1'
$template = Join-Path $PSScriptRoot '..' 'installer' 'winget'
$root = Join-Path ([IO.Path]::GetTempPath()) "winget-manifest-tests-$PID"
$failures = 0

function Pass($label) { Write-Host "ok   $label" }
function Fail($label) { Write-Host "FAIL $label"; $script:failures++ }

function New-Params {
    @{ Version = '1.2.3'; X64Sha256 = ('a' * 64); Arm64Sha256 = ('b' * 64); ReleaseDate = '2026-11-12'
       OutDir = (Join-Path $root "out-$([guid]::NewGuid())") }
}

# A copy of the template with one edit to one file.
function New-Template([string] $File, [scriptblock] $Edit) {
    $dir = Join-Path $root "tpl-$([guid]::NewGuid())"
    New-Item -ItemType Directory -Force $dir | Out-Null
    Copy-Item (Join-Path $template '*.yaml') $dir
    $path = Join-Path $dir $File
    [IO.File]::WriteAllText($path, (& $Edit ([IO.File]::ReadAllText($path))))
    return $dir
}

function Expect-Refused([string] $Label, [hashtable] $Params) {
    try { & $script @Params *> $null; Fail "accepted: $Label" }
    catch { Pass "refused: $Label ($($_.Exception.Message))" }
}

$installer = 'KellyLford.QuickMail.installer.yaml'

try {
    # The real template, filled, is accepted and comes out with no comments or placeholders.
    $p = New-Params
    & $script @p | Out-Null
    $files = Get-ChildItem $p.OutDir -Filter *.yaml
    if ($files.Count -eq 3) { Pass 'writes three manifests' } else { Fail "wrote $($files.Count) manifests" }
    $all = ($files | ForEach-Object { [IO.File]::ReadAllText($_.FullName) }) -join "`n"
    if ($all -notmatch '(?m)^#(?! yaml-language-server:)') { Pass 'strips comments, keeps schema lines' } else { Fail 'a comment survived' }
    if ($all -notmatch '<[A-Z0-9-]+>') { Pass 'fills every placeholder' } else { Fail 'a placeholder survived' }
    if ($all.Contains('InstallerSha256: ' + ('A' * 64))) { Pass 'upper-cases hashes' } else { Fail 'hash not upper-cased' }

    # Bad inputs.
    $p = New-Params; $p.Version = 'v1.2.3'; Expect-Refused 'version with a leading v' $p
    $p = New-Params; $p.X64Sha256 = 'xyz'; Expect-Refused 'a malformed hash' $p
    $p = New-Params; $p.Arm64Sha256 = $p.X64Sha256; Expect-Refused 'the same hash for both MSIs' $p
    $p = New-Params; $p.ReleaseDate = '11/12/2026'; Expect-Refused 'a date not in YYYY-MM-DD' $p

    # Templates that break the package's rules (installer/winget/README.md).
    $p = New-Params; $p.TemplateDir = New-Template $installer { param($t) $t -replace 'ProductCode: MSI:QuickMail', "ProductCode: MSI:QuickMail`n  UpgradeCode: '{1234}'" }
    Expect-Refused 'an UpgradeCode' $p
    $p = New-Params; $p.TemplateDir = New-Template $installer { param($t) $t -replace 'Installers:', "ProductCode: '{ABCD}'`nInstallers:" }
    Expect-Refused 'an installer-level ProductCode' $p
    $p = New-Params; $p.TemplateDir = New-Template $installer { param($t) $t -replace 'RequireExplicitUpgrade: true', 'RequireExplicitUpgrade: false' }
    Expect-Refused 'RequireExplicitUpgrade off' $p
    $p = New-Params; $p.TemplateDir = New-Template $installer { param($t) $t -replace 'QuickMail-<VERSION>-win\.msi', 'QuickMail-win-Setup.exe' }
    Expect-Refused 'a Setup.exe installer' $p
    $p = New-Params; $p.TemplateDir = New-Template $installer { param($t) $t -replace 'ReleaseDate: <RELEASE-DATE>', 'ReleaseDate: <NEW-THING>' }
    Expect-Refused 'an unknown placeholder' $p

    # An indented '#' line is text inside a block scalar, not a comment, and must survive.
    $p = New-Params; $p.TemplateDir = New-Template 'KellyLford.QuickMail.locale.en-US.yaml' { param($t) $t -replace '(?m)^(Description: \|-\r?\n)', "`$1  # Features`n" }
    & $script @p | Out-Null
    if ([IO.File]::ReadAllText((Join-Path $p.OutDir 'KellyLford.QuickMail.locale.en-US.yaml')).Contains("  # Features")) {
        Pass 'keeps an indented # line inside Description'
    } else { Fail 'dropped an indented # line inside Description' }
}
finally {
    Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue
}

Write-Host "$failures failure(s)"
exit $failures

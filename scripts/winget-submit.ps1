<#
.SYNOPSIS
    Opens the microsoft/winget-pkgs pull request for one QuickMail version, from the
    manifests scripts/winget-manifest.ps1 wrote. Called by .github/workflows/winget-submit.yml.

.DESCRIPTION
    Needs GH_TOKEN set to a classic personal access token with the public_repo scope.

    Everything goes through the GitHub API: nothing is cloned and the fork is never synced.
    Syncing a fork whose upstream changed .github/workflows needs the `workflow` scope, which
    this token deliberately lacks, and winget-pkgs changes its workflows about monthly. A fork
    shares its upstream's object network, so a branch in the fork can point straight at a
    commit built on upstream's current master. wingetcreate and Komac work the same way.

.PARAMETER NoPullRequest
    Stop after pushing the branch. For testing the API steps.

.EXAMPLE
    pwsh scripts/winget-submit.ps1 -Version 0.8.72 -ManifestDir out/0.8.72
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $ManifestDir,
    [switch] $NoPullRequest
)

$ErrorActionPreference = 'Stop'
# Also what makes $branch safe to put inside the jq filter below.
if ($Version -notmatch '^\d+\.\d+\.\d+(-test)?$') { throw "Version '$Version' is not X.Y.Z." }
$upstream = 'microsoft/winget-pkgs'
$pkgDir = "manifests/k/KellyLford/QuickMail/$Version"
$branch = "KellyLford.QuickMail-$Version"

function Invoke-Gh {
    $out = & gh @args 2>&1
    if ($LASTEXITCODE -ne 0) { throw "gh $($args -join ' ') failed: $out" }
    return $out
}

# True if the API path exists, false on 404, and an error for anything else, so a rate
# limit or an outage is never mistaken for "not there".
function Test-GhPath([string] $Path) {
    $out = & gh api $Path --silent 2>&1
    if ($LASTEXITCODE -eq 0) { return $true }
    if ("$out" -match 'HTTP 404') { return $false }
    throw "gh api $Path failed: $out"
}

# POST/PATCH a JSON body; returns the response's field.
function Send-GhJson([string] $Method, [string] $Path, $Body, [string] $Field) {
    $file = New-TemporaryFile
    try {
        [IO.File]::WriteAllText($file, ($Body | ConvertTo-Json -Depth 5 -Compress), [Text.UTF8Encoding]::new($false))
        # @() around the whole if: a one-element array would otherwise unroll to a string,
        # and splatting a string passes it one character at a time.
        [string[]] $jq = @(if ($Field) { '--jq', ".$Field" } else { '--silent' })
        return Invoke-Gh api -X $Method $Path --input $file @jq
    } finally { Remove-Item $file -ErrorAction SilentlyContinue }
}

$files = @(Get-ChildItem -LiteralPath $ManifestDir -Filter '*.yaml')
if ($files.Count -ne 3) { throw "Expected 3 manifests in $ManifestDir, found $($files.Count)." }

$login = Invoke-Gh api user --jq .login
$fork = "$login/winget-pkgs"

# An open pull request from this branch means a run for this version already got that far,
# and the branch may since carry fixes a moderator asked for. Never push over it. An exact
# check on the head branch, unlike the workflow's title search, which lags behind pull
# requests opened minutes ago.
# --head alone is a prefix search (0.8.7 finds 0.8.71's), so compare the name exactly.
$existing = Invoke-Gh pr list -R $upstream --author $login --head $branch --state open --json 'url,headRefName' `
    --jq "[.[] | select(.headRefName == `"$branch`")][0].url // empty"
if ($existing) {
    Write-Host "::notice::A pull request is already open for ${branch}: $existing"
    return
}

if (-not (Test-GhPath "repos/$fork")) {
    Invoke-Gh repo fork $upstream --clone=false --default-branch-only | Out-Null
    foreach ($i in 1..30) { if (Test-GhPath "repos/$fork") { break }; Start-Sleep -Seconds 5 }
}

$base = Invoke-Gh api "repos/$upstream/git/ref/heads/master" --jq .object.sha
$baseTree = Invoke-Gh api "repos/$upstream/git/commits/$base" --jq .tree.sha

# One commit holding the three files, on top of upstream's master.
$entries = foreach ($f in $files) {
    @{ path = "$pkgDir/$($f.Name)"; mode = '100644'; type = 'blob'
       content = [IO.File]::ReadAllText($f.FullName) }
}
$tree = Send-GhJson POST "repos/$fork/git/trees" @{ base_tree = $baseTree; tree = @($entries) } 'sha'
$commit = Send-GhJson POST "repos/$fork/git/commits" @{
    message = "New version: KellyLford.QuickMail version $Version"; tree = $tree; parents = @($base) } 'sha'

# The branch is new, or left over from a run that failed before opening its pull request
# (checked above), so moving it is safe.
if (Test-GhPath "repos/$fork/git/ref/heads/$branch") {
    Send-GhJson PATCH "repos/$fork/git/refs/heads/$branch" @{ sha = $commit; force = $true } | Out-Null
} else {
    Send-GhJson POST "repos/$fork/git/refs" @{ ref = "refs/heads/$branch"; sha = $commit } | Out-Null
}
Write-Host "Pushed $commit to ${fork}:$branch"

if ($NoPullRequest) { return }

$body = @"
## 📖 Description

Update KellyLford.QuickMail to $Version. Submitted by QuickMail's release workflow from the same template as every earlier version, changing only the version, the release date, the two MSI URLs and their SHA256 hashes, and ReleaseNotesUrl.

## ✅ Checklist

- [x] Signed the [Contributor License Agreement](https://cla.opensource.microsoft.com)
- [ ] Linked to an issue (if applicable)

## 📦 Manifest Checklist

- [x] Checked that there aren't other open [pull requests](https://github.com/microsoft/winget-pkgs/pulls) for the same manifest update/change
- [x] This PR only modifies one (1) manifest
- [ ] Validated manifest locally with ``winget validate --manifest <path>``: the workflow runs on Linux; the template it fills passes ``winget validate``.
- [ ] Tested manifest locally with ``winget install --manifest <path>``: not for this version.
- [x] Manifest conforms to the [1.28 schema](https://github.com/microsoft/winget-pkgs/tree/master/doc/manifest/schema/1.28.0)
"@
$bodyFile = New-TemporaryFile
[IO.File]::WriteAllText($bodyFile, $body, [Text.UTF8Encoding]::new($false))
$url = Invoke-Gh pr create -R $upstream --base master --head "${login}:$branch" `
    --title "Update: KellyLford.QuickMail to $Version" --body-file $bodyFile
Write-Host "::notice::Opened $url"
if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value "winget pull request: $url" }

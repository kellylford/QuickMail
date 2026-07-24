# Starts (or stops) the local protocol servers used by QuickMail.IntegrationTests.
#
# Currently: GreenMail (IMAP on 3143, SMTP on 3025, bound to 127.0.0.1, auth disabled so any
# user/password works and mailboxes auto-create). Radicale (CalDAV/CardDAV) is added in a later
# phase of the live-content testing plan.
#
# Requirements: Java 11+ on PATH, in JAVA_HOME, or passed via -JavaExe.
# The GreenMail jar is downloaded once from Maven Central into .testservers\ (gitignored).
#
# Usage:
#   .\scripts\start-test-servers.ps1              # download if needed, start, wait until ready
#   .\scripts\start-test-servers.ps1 -Stop        # stop servers started by this script
#
# See docs/TESTING-INTEGRATION.md for the full local workflow.

[CmdletBinding()]
param(
    [switch]$Stop,
    [string]$GreenMailVersion = "2.1.11",
    [string]$JavaExe = ""
)

$ErrorActionPreference = "Stop"
$repoRoot   = Split-Path -Parent $PSScriptRoot
$serversDir = Join-Path $repoRoot ".testservers"
$pidFile    = Join-Path $serversDir "greenmail.pid"
$logFile    = Join-Path $serversDir "greenmail.log"

if ($Stop) {
    if (Test-Path $pidFile) {
        $procId = Get-Content $pidFile
        try {
            Stop-Process -Id $procId -Force -Confirm:$false -ErrorAction Stop
            Write-Host "Stopped GreenMail (pid $procId)."
        } catch {
            Write-Host "GreenMail (pid $procId) was not running."
        }
        Remove-Item $pidFile -Force
    } else {
        Write-Host "No pid file at $pidFile - nothing to stop."
    }
    exit 0
}

New-Item -ItemType Directory -Force $serversDir | Out-Null

# ── Locate Java ────────────────────────────────────────────────────────────────
if (-not $JavaExe) {
    if ($env:JAVA_HOME -and (Test-Path (Join-Path $env:JAVA_HOME "bin\java.exe"))) {
        $JavaExe = Join-Path $env:JAVA_HOME "bin\java.exe"
    } else {
        $cmd = Get-Command java -ErrorAction SilentlyContinue
        if ($cmd) { $JavaExe = $cmd.Source }
    }
}
if (-not $JavaExe -or -not (Test-Path $JavaExe)) {
    Write-Error ("Java not found. Install a JRE (11+), set JAVA_HOME, or pass -JavaExe. " +
                 "See docs/TESTING-INTEGRATION.md.")
}

# ── Download GreenMail standalone jar (cached) ─────────────────────────────────
# SHA-256 of the pinned default version; CI executes this jar, so the download is
# integrity-checked. Bumping -GreenMailVersion requires updating the hash (compute it and
# cross-check against Maven Central's published .sha1 for the new jar).
$knownJarHashes = @{
    "2.1.11" = "DB075010CD803CF936051C5BF4D7457126E7E9E0D0CC114BAA2E97222FC2B732"
}
$jar = Join-Path $serversDir "greenmail-standalone-$GreenMailVersion.jar"
if (-not (Test-Path $jar)) {
    $url = "https://repo1.maven.org/maven2/com/icegreen/greenmail-standalone/$GreenMailVersion/greenmail-standalone-$GreenMailVersion.jar"
    Write-Host "Downloading GreenMail $GreenMailVersion from Maven Central..."
    Invoke-WebRequest -Uri $url -OutFile $jar -UseBasicParsing
}
if ($knownJarHashes.ContainsKey($GreenMailVersion)) {
    $actual = (Get-FileHash $jar -Algorithm SHA256).Hash
    if ($actual -ne $knownJarHashes[$GreenMailVersion]) {
        Remove-Item $jar -Force -Confirm:$false
        Write-Error ("GreenMail jar checksum mismatch for $GreenMailVersion " +
                     "(got $actual). Deleted the file; re-run to retry the download.")
    }
} else {
    Write-Warning "No pinned SHA-256 for GreenMail $GreenMailVersion - skipping integrity check."
}

# ── Start ──────────────────────────────────────────────────────────────────────
if (Test-Path $pidFile) {
    $existing = Get-Content $pidFile
    if (Get-Process -Id $existing -ErrorAction SilentlyContinue) {
        Write-Host "GreenMail already running (pid $existing)."
        exit 0
    }
    Remove-Item $pidFile -Force
}

# greenmail.setup.test.smtp / .imap bind 127.0.0.1 at the test-profile ports (3025 / 3143).
# greenmail.auth.disabled accepts any credentials and auto-creates mailboxes, so tests can
# invent per-run users without any server-side user list.
$gmArgs = @(
    "-Dgreenmail.setup.test.smtp",
    "-Dgreenmail.setup.test.imap",
    "-Dgreenmail.auth.disabled",
    "-Dgreenmail.verbose",
    "-jar", $jar
)
$proc = Start-Process -FilePath $JavaExe -ArgumentList $gmArgs -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput $logFile -RedirectStandardError "$logFile.err"
Set-Content $pidFile $proc.Id

# ── Wait until ready ───────────────────────────────────────────────────────────
$deadline = (Get-Date).AddSeconds(60)
$ready = $false
while ((Get-Date) -lt $deadline) {
    if ($proc.HasExited) {
        Write-Error ("GreenMail exited immediately (code $($proc.ExitCode)). See $logFile / $logFile.err")
    }
    $smtpOk = (Test-NetConnection 127.0.0.1 -Port 3025 -WarningAction SilentlyContinue).TcpTestSucceeded
    $imapOk = (Test-NetConnection 127.0.0.1 -Port 3143 -WarningAction SilentlyContinue).TcpTestSucceeded
    if ($smtpOk -and $imapOk) { $ready = $true; break }
    Start-Sleep -Milliseconds 500
}

if (-not $ready) {
    Write-Error "GreenMail did not open ports 3025/3143 within 60s. See $logFile / $logFile.err"
}
Write-Host "GreenMail ready: SMTP 127.0.0.1:3025, IMAP 127.0.0.1:3143 (pid $($proc.Id))."
Write-Host "Stop with: .\scripts\start-test-servers.ps1 -Stop"

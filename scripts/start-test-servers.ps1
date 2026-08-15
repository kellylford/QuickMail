# Starts (or stops) the local protocol servers used by QuickMail.IntegrationTests.
#
# - GreenMail: IMAP on 3143, SMTP on 3025, bound to 127.0.0.1, auth disabled so any
#   user/password works and mailboxes auto-create.
# - Radicale (CalDAV + CardDAV): port 5232, auth 'none' (any credentials accepted; each user
#   owns /<user>/ collections), filesystem storage under .testservers\radicale-storage.
#   Launched via scripts/radicale-launcher.py, which works around Radicale's Windows
#   symlink-probe startup crash (WinError 1314 escapes its PermissionError handler).
#
# Requirements: Java 11+ (PATH, JAVA_HOME, or -JavaExe) and Python 3 with pip.
# The GreenMail jar is downloaded once from Maven Central into .testservers\ (gitignored);
# Radicale is pip-installed once (pinned version).
#
# Usage:
#   .\scripts\start-test-servers.ps1              # install/download if needed, start, wait until ready
#   .\scripts\start-test-servers.ps1 -Stop        # stop servers started by this script
#
# See docs/TESTING-INTEGRATION.md for the full local workflow.

[CmdletBinding()]
param(
    [switch]$Stop,
    [string]$GreenMailVersion = "2.1.11",
    [string]$RadicaleVersion = "3.7.7",
    [string]$JavaExe = ""
)

$ErrorActionPreference = "Stop"
# Windows PowerShell 5.1 may default to TLS 1.0/1.1, which Maven Central rejects.
if ($PSVersionTable.PSVersion.Major -lt 6) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
}
$repoRoot        = Split-Path -Parent $PSScriptRoot
$serversDir      = Join-Path $repoRoot ".testservers"
$pidFile         = Join-Path $serversDir "greenmail.pid"
$logFile         = Join-Path $serversDir "greenmail.log"
$radicalePidFile = Join-Path $serversDir "radicale.pid"
$radicaleLog     = Join-Path $serversDir "radicale.log"
$radicaleStorage = Join-Path $serversDir "radicale-storage"

function Stop-ByPidFile([string]$file, [string]$name) {
    if (Test-Path $file) {
        $procId = Get-Content $file
        try {
            Stop-Process -Id $procId -Force -Confirm:$false -ErrorAction Stop
            Write-Host "Stopped $name (pid $procId)."
        } catch {
            Write-Host "$name (pid $procId) was not running."
        }
        Remove-Item $file -Force
    } else {
        Write-Host "No pid file at $file - $name not started by this script."
    }
}

if ($Stop) {
    Stop-ByPidFile $pidFile "GreenMail"
    Stop-ByPidFile $radicalePidFile "Radicale"
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

# ── Start GreenMail ────────────────────────────────────────────────────────────
$gmAlreadyRunning = $false
if (Test-Path $pidFile) {
    $existing = Get-Content $pidFile
    if (Get-Process -Id $existing -ErrorAction SilentlyContinue) {
        Write-Host "GreenMail already running (pid $existing)."
        $gmAlreadyRunning = $true
    } else {
        Remove-Item $pidFile -Force
    }
}

if (-not $gmAlreadyRunning) {
    # greenmail.setup.test.smtp / .imap / .pop3 bind 127.0.0.1 at the test-profile ports
    # (3025 / 3143 / 3110). greenmail.auth.disabled accepts any credentials and auto-creates
    # mailboxes, so tests can invent per-run users without any server-side user list.
    # POP3 serves the same INBOX as IMAP, which is what lets a POP3 test seed over SMTP (#128).
    $gmArgs = @(
        "-Dgreenmail.setup.test.smtp",
        "-Dgreenmail.setup.test.imap",
        "-Dgreenmail.setup.test.pop3",
        "-Dgreenmail.auth.disabled",
        "-Dgreenmail.verbose",
        "-jar", $jar
    )
    $proc = Start-Process -FilePath $JavaExe -ArgumentList $gmArgs -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $logFile -RedirectStandardError "$logFile.err"
    Set-Content $pidFile $proc.Id

    $deadline = (Get-Date).AddSeconds(60)
    $ready = $false
    while ((Get-Date) -lt $deadline) {
        if ($proc.HasExited) {
            Write-Error ("GreenMail exited immediately (code $($proc.ExitCode)). See $logFile / $logFile.err")
        }
        $smtpOk = (Test-NetConnection 127.0.0.1 -Port 3025 -WarningAction SilentlyContinue).TcpTestSucceeded
        $imapOk = (Test-NetConnection 127.0.0.1 -Port 3143 -WarningAction SilentlyContinue).TcpTestSucceeded
        $popOk  = (Test-NetConnection 127.0.0.1 -Port 3110 -WarningAction SilentlyContinue).TcpTestSucceeded
        if ($smtpOk -and $imapOk -and $popOk) { $ready = $true; break }
        Start-Sleep -Milliseconds 500
    }

    if (-not $ready) {
        Write-Error "GreenMail did not open ports 3025/3143/3110 within 60s. See $logFile / $logFile.err"
    }
    Write-Host "GreenMail ready: SMTP 127.0.0.1:3025, IMAP 127.0.0.1:3143, POP3 127.0.0.1:3110 (pid $($proc.Id))."
}

# ── Radicale (CalDAV + CardDAV) ────────────────────────────────────────────────
$python = (Get-Command python -ErrorAction SilentlyContinue)
if (-not $python) {
    Write-Error "Python not found on PATH. Install Python 3 (needed for Radicale). See docs/TESTING-INTEGRATION.md."
}

# EAP guard: under Windows PowerShell 5.1, EAP=Stop turns a native command's redirected
# stderr into a terminating NativeCommandError — the probe's ModuleNotFoundError traceback
# would kill the script HERE, before the pip install it exists to trigger. pip likewise
# writes warnings to stderr, so both run under Continue; $LASTEXITCODE still decides.
$prevEap = $ErrorActionPreference
$ErrorActionPreference = "Continue"
$installed = & python -c "import radicale; print(radicale.VERSION)" 2>$null
if ($LASTEXITCODE -ne 0 -or $installed -ne $RadicaleVersion) {
    Write-Host "Installing Radicale $RadicaleVersion via pip..."
    & python -m pip install --user --quiet "radicale==$RadicaleVersion"
    if ($LASTEXITCODE -ne 0) {
        $ErrorActionPreference = $prevEap
        Write-Error "pip install radicale==$RadicaleVersion failed."
    }
}
$ErrorActionPreference = $prevEap

$alreadyRunning = $false
if (Test-Path $radicalePidFile) {
    $existing = Get-Content $radicalePidFile
    if (Get-Process -Id $existing -ErrorAction SilentlyContinue) {
        Write-Host "Radicale already running (pid $existing)."
        $alreadyRunning = $true
    } else {
        Remove-Item $radicalePidFile -Force
    }
}

if (-not $alreadyRunning) {
    # auth-type none: any username/password authenticates; each user owns /<user>/ collections,
    # so tests invent per-run users just like they do against GreenMail. The launcher shim works
    # around Radicale's Windows symlink-probe crash (see scripts/radicale-launcher.py).
    New-Item -ItemType Directory -Force $radicaleStorage | Out-Null
    $radArgs = @(
        (Join-Path $PSScriptRoot "radicale-launcher.py"),
        "--server-hosts", "127.0.0.1:5232",
        "--auth-type", "none",
        "--storage-filesystem-folder", $radicaleStorage
    )
    $radProc = Start-Process -FilePath python -ArgumentList $radArgs -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $radicaleLog -RedirectStandardError "$radicaleLog.err"
    Set-Content $radicalePidFile $radProc.Id

    $deadline = (Get-Date).AddSeconds(60)
    $ready = $false
    while ((Get-Date) -lt $deadline) {
        if ($radProc.HasExited) {
            Write-Error ("Radicale exited immediately (code $($radProc.ExitCode)). See $radicaleLog / $radicaleLog.err")
        }
        if ((Test-NetConnection 127.0.0.1 -Port 5232 -WarningAction SilentlyContinue).TcpTestSucceeded) { $ready = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) {
        Write-Error "Radicale did not open port 5232 within 60s. See $radicaleLog / $radicaleLog.err"
    }
    Write-Host "Radicale ready: CalDAV/CardDAV 127.0.0.1:5232 (pid $($radProc.Id))."
}

Write-Host "Stop with: .\scripts\start-test-servers.ps1 -Stop"

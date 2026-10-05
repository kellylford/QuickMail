namespace QuickMail.Helpers;

/// <summary>
/// The uninstall-time offer to remove user data, run by the uninstall hook in a detached
/// PowerShell process (Update.exe and the MSI kill hook processes after 30-60 seconds — far too
/// short to leave a question pending).
///
/// The hook cannot tell an uninstall from the first half of an upgrade. A Windows Installer major
/// upgrade removes the old copy before installing the new one (#245), and that removal runs the
/// hook exactly as a real uninstall does — Velopack's MSI conditions it on REMOVE="ALL" alone. Run
/// unconditionally, the prompt told people mid-upgrade that QuickMail had been uninstalled and
/// offered to delete the data of a copy that was about to be working again.
///
/// So the script decides before it asks. It waits for QuickMail's files to go (if they never do,
/// nothing was uninstalled), then for Windows Installer to go quiet — the Global\_MSIExecute mutex
/// exists while any installation is executing — and looks again. Files back means an upgrade:
/// nothing to ask. Still gone, but QuickMail still in Add/Remove Programs, means it was installed
/// somewhere else (an upgrade to a different folder) or another copy remains: the data is still
/// in use, so nothing to ask either. Otherwise it was an uninstall: ask. A Velopack uninstall (a
/// Setup.exe install) involves no Windows Installer, so it asks once the files are gone and the
/// quiet window passes.
///
/// Everything the script needs is a parameter, so the tests can run it against a scratch folder,
/// a scratch Uninstall key and a mutex of their own, with short waits and <c>-DryRun</c>, which
/// logs the decision instead of showing the question.
/// </summary>
internal static class UninstallDataPrompt
{
    /// <summary>
    /// How long Windows Installer must stay idle, in seconds, before "still gone" is believed.
    /// It covers any gap between the old copy's removal and the new copy's install inside one
    /// upgrade; the install-path matrix (scenario 6) measures that gap.
    /// </summary>
    internal const int DefaultQuietSeconds = 10;

    internal const string Script = """
        param(
            [Parameter(Mandatory)][string]$Exe,
            [Parameter(Mandatory)][string]$DataDir,
            [Parameter(Mandatory)][string]$Log,
            [int]$RemovalWaitSeconds = 120,
            [int]$QuietSeconds = 10,
            [int]$MaxWaitSeconds = 600,
            [string]$MutexName = 'Global\_MSIExecute',
            [string]$UninstallRoot = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
            [switch]$DryRun
        )
        function Write-Log([string]$Text) { Add-Content -Path $Log -Value "$(Get-Date -Format s) $Text" }
        function Test-InstallerBusy {
            # The mutex exists while Windows Installer is executing an installation. Opening it may
            # be refused to a standard user; refused still means it exists.
            $m = $null
            try {
                $found = [System.Threading.Mutex]::TryOpenExisting($MutexName,
                    [System.Security.AccessControl.MutexRights]::Synchronize, [ref]$m)
                if ($m) { $m.Dispose() }
                return $found
            } catch [System.UnauthorizedAccessException] { return $true }
            catch { return $false }
        }
        Write-Log 'prompt script started'
        try {
            $deadline = (Get-Date).AddSeconds($RemovalWaitSeconds)
            while ((Test-Path -LiteralPath $Exe) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
            if (Test-Path -LiteralPath $Exe) { Write-Log 'QuickMail is still installed; not asking'; exit }

            $deadline = (Get-Date).AddSeconds($MaxWaitSeconds)
            $quiet = 0
            while ($quiet -lt $QuietSeconds -and (Get-Date) -lt $deadline) {
                if (Test-Path -LiteralPath $Exe) { Write-Log 'QuickMail was installed again (an upgrade); not asking'; exit }
                if (Test-InstallerBusy) { $quiet = 0 } else { $quiet++ }
                Start-Sleep -Seconds 1
            }
            if (Test-Path -LiteralPath $Exe) { Write-Log 'QuickMail was installed again (an upgrade); not asking'; exit }
            # Velopack's own Add/Remove Programs entries: MSI:QuickMail for an MSI install,
            # QuickMail for a Setup.exe one. Either still there means a copy that uses this data.
            foreach ($row in 'MSI:QuickMail', 'QuickMail') {
                if (Test-Path -LiteralPath "$UninstallRoot\$row") {
                    Write-Log "QuickMail is still installed ($row); not asking"; exit
                }
            }
            if (-not (Test-Path -LiteralPath $DataDir)) { Write-Log 'no data folder; not asking'; exit }
            if ($DryRun) { Write-Log 'would ask'; exit }

            Add-Type -AssemblyName System.Windows.Forms
            $msg = "QuickMail has been uninstalled.`n`n" +
                   "Do you also want to remove your QuickMail data? This permanently deletes all accounts, settings, contacts, rules, templates, saved views, and cached mail stored under:`n$DataDir`n`n" +
                   "It also removes QuickMail's saved passwords and sign-ins from Windows Credential Manager.`n`n" +
                   "Choose No to keep everything, so a future install picks up exactly where you left off."
            $owner = New-Object System.Windows.Forms.Form -Property @{ TopMost = $true }
            Write-Log 'asking'
            $r = [System.Windows.Forms.MessageBox]::Show($owner, $msg, 'QuickMail Uninstall',
                [System.Windows.Forms.MessageBoxButtons]::YesNo,
                [System.Windows.Forms.MessageBoxIcon]::Question,
                [System.Windows.Forms.MessageBoxDefaultButton]::Button2)
            Write-Log "user answered: $r"
            if ($r -eq [System.Windows.Forms.DialogResult]::Yes) {
                Remove-Item -LiteralPath $DataDir -Recurse -Force -ErrorAction SilentlyContinue
                (cmdkey /list) | ForEach-Object {
                    if ($_ -match 'target=(QuickMail\S*)') { cmdkey /delete:$($Matches[1]) | Out-Null }
                }
                Write-Log 'data removal completed'
            }
        } finally {
            # The script deletes itself on every way out, exit included: PowerShell runs finally on exit.
            Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
        }
        """;

    /// <summary>The powershell.exe argument string that runs <paramref name="scriptPath"/> for real.</summary>
    internal static string Arguments(string scriptPath, string exe, string dataDir, string log) =>
        $"-NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File \"{scriptPath}\" " +
        $"-Exe \"{exe}\" -DataDir \"{dataDir}\" -Log \"{log}\" -QuietSeconds {DefaultQuietSeconds}";
}

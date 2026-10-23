# Writes the two gitignored credential partial classes QuickMail.csproj needs to compile:
#   QuickMail/Services/GoogleOAuthService.Credentials.cs  (Google OAuth client id/secret)
#   QuickMail/Services/BugReportService.Credentials.cs    (bug-report relay URL/key)
#
# Two modes, and the default is the safe one:
#
#   (no switch)  Empty placeholders. Environment variables are ignored on purpose, so a build
#                that does not need the real values cannot pick them up by accident. This is
#                what pull requests, main pushes, CodeQL, the flaky hunt and the live smoke
#                test use: none of them sign anyone in to Google or file a bug report, and
#                none of them may read a secret, because any collaborator with write access
#                can push a branch that runs them.
#
#   -Release     The real values, from GOOGLE_CLIENT_ID, GOOGLE_CLIENT_SECRET,
#                BUG_REPORT_RELAY_URL and BUG_REPORT_RELAY_KEY. Only jobs that run inside the
#                `azure-signing` environment (a v* tag build, the on-demand signed installer)
#                pass this, and only they can see those secrets. An empty value FAILS the
#                build: a release compiled with blank credentials installs and runs, but
#                Google sign-in and in-app bug reports silently stop working for everyone
#                who installs it, and nothing else in the pipeline would notice.
#
# Values are escaped for a C# string literal, so a quote or backslash in a secret cannot
# break the build or inject code.
param(
    [switch]$Release
)

$ErrorActionPreference = 'Stop'

function ConvertTo-CSharpLiteral([string]$value) {
    return $value.Replace('\', '\\').Replace('"', '\"')
}

$names = 'GOOGLE_CLIENT_ID', 'GOOGLE_CLIENT_SECRET', 'BUG_REPORT_RELAY_URL', 'BUG_REPORT_RELAY_KEY'
$values = @{}
foreach ($name in $names) {
    $values[$name] = if ($Release) { [Environment]::GetEnvironmentVariable($name) } else { '' }
}

if ($Release) {
    $missing = @($names | Where-Object { [string]::IsNullOrEmpty($values[$_]) })
    if ($missing.Count -gt 0) {
        throw ("Release build is missing: $($missing -join ', '). The secrets live in the " +
               "'azure-signing' environment (BUG_REPORT_RELAY_URL is a repository variable); " +
               "check that this job runs in that environment and that each one is set there.")
    }
    Write-Host "Writing the real credentials (release build)."
} else {
    Write-Host "Writing empty placeholder credentials (no secrets in this build)."
}

$v = @{}
foreach ($name in $names) { $v[$name] = ConvertTo-CSharpLiteral $values[$name] }

$google = @"
namespace QuickMail.Services;
public partial class GoogleOAuthService
{
    private const string ClientId     = "$($v['GOOGLE_CLIENT_ID'])";
    private const string ClientSecret = "$($v['GOOGLE_CLIENT_SECRET'])";
}
"@

$bugReport = @"
namespace QuickMail.Services;
public partial class BugReportService
{
    private const string RelayUrl = "$($v['BUG_REPORT_RELAY_URL'])";
    private const string RelayKey = "$($v['BUG_REPORT_RELAY_KEY'])";
}
"@

$root = Split-Path -Parent $PSScriptRoot
Set-Content -Path (Join-Path $root 'QuickMail/Services/GoogleOAuthService.Credentials.cs') -Value $google -Encoding UTF8
Set-Content -Path (Join-Path $root 'QuickMail/Services/BugReportService.Credentials.cs') -Value $bugReport -Encoding UTF8

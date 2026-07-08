@echo off
setlocal

set CONFIG=Debug
if /i "%1"=="release" set CONFIG=Release
if /i "%1"=="publish"   goto publish
if /i "%1"=="installer" goto installer
if /i "%1"=="run"     goto run
if /i "%1"=="clean"   goto clean
if /i "%1"=="smoke"   goto smoke

:build
echo Building QuickMail (%CONFIG%)...
dotnet build QuickMail\QuickMail.csproj -c %CONFIG%
goto end

:run
echo Running QuickMail (%CONFIG%)...
dotnet run --project QuickMail\QuickMail.csproj -c %CONFIG%
goto end

:publish
echo Publishing QuickMail — single-file self-contained win-x64...
if exist publish\ rmdir /s /q publish\
dotnet publish QuickMail\QuickMail.csproj -c Release -o publish\
echo.
echo Output: publish\QuickMail.exe
goto end

:installer
echo Publishing QuickMail — single-file self-contained win-x64...
if exist publish\ rmdir /s /q publish\
dotnet publish QuickMail\QuickMail.csproj -c Release -o publish\
if errorlevel 1 (
    echo INSTALLER FAILED: publish errors.
    exit /b 1
)
echo.
echo Locating vpk (Velopack CLI)...
where vpk >nul 2>nul
if errorlevel 1 (
    echo INSTALLER FAILED: vpk not found. Install it with: dotnet tool install -g vpk
    exit /b 1
)
echo Reading version from QuickMail\QuickMail.csproj...
set "VERSION="
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "(Select-Xml -Path QuickMail\QuickMail.csproj -XPath '/Project/PropertyGroup/Version').Node.InnerText"`) do set "VERSION=%%v"
if not defined VERSION (
    echo INSTALLER FAILED: could not read ^<Version^> from QuickMail\QuickMail.csproj.
    exit /b 1
)
echo Packing Velopack release v%VERSION%...
:: --packVersion must be SemVer (3-part, or a prerelease tag like 0.8.1-1 for a hotfix);
:: a 4-part version is rejected by vpk. --shortcuts StartMenuRoot matches the old Inno
:: behavior (Start Menu always, no desktop shortcut by default).
:: Note: local packs are full-only. CI runs `vpk download github` first so packs there
:: also produce a delta package against the previous release.
vpk pack --packId QuickMail --packVersion %VERSION% --packDir publish ^
  --mainExe QuickMail.exe --packTitle QuickMail --packAuthors "Kelly Ford" ^
  --shortcuts StartMenuRoot --outputDir installer\Output\Releases
if errorlevel 1 (
    echo INSTALLER FAILED: vpk pack errors.
    exit /b 1
)
echo.
echo Output: installer\Output\Releases\QuickMail-win-Setup.exe (plus update packages)
goto end

:clean
echo Cleaning...
dotnet clean QuickMail\QuickMail.csproj
if exist publish\ rmdir /s /q publish\
goto end

:smoke
echo Building QuickMail (%CONFIG%)...
dotnet build QuickMail\QuickMail.csproj -c %CONFIG%
if errorlevel 1 (
    echo SMOKE FAILED: build errors.
    exit /b 1
)
echo.
echo Smoke-testing: launching app and waiting 6 seconds...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$p = Start-Process dotnet -ArgumentList 'run','--project','QuickMail/QuickMail.csproj','-c','%CONFIG%','--no-build' -PassThru; Start-Sleep 6; if ($p.HasExited) { Write-Host 'SMOKE FAILED: app exited within 6 seconds.'; exit 1 } else { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; Write-Host 'SMOKE PASSED: app started and stayed alive.'; exit 0 }"
if errorlevel 1 exit /b 1
goto end

:end
endlocal

@echo off
setlocal

set CONFIG=Debug
if /i "%1"==""        goto publish
if /i "%1"=="publish" goto publish
if /i "%1"=="build"   goto build
if /i "%1"=="release" set CONFIG=Release & goto build
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

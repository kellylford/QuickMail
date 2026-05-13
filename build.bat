@echo off
setlocal

set CONFIG=Debug
if /i "%1"=="release" set CONFIG=Release
if /i "%1"=="publish" goto publish
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
echo Launching app for smoke test (5 s)...
start "" /B dotnet run --project QuickMail\QuickMail.csproj -c %CONFIG% --no-build
set SMOKE_PID=

:: Give the process a moment to start, then grab its PID via WMIC
timeout /t 1 /nobreak >nul
for /f "tokens=2" %%i in ('wmic process where "CommandLine like '%%QuickMail%%' and not CommandLine like '%%build.bat%%'" get ProcessId /value 2^>nul ^| findstr "ProcessId"') do set SMOKE_PID=%%i

timeout /t 5 /nobreak >nul

:: Check if the process is still running
if defined SMOKE_PID (
    wmic process where "ProcessId=%SMOKE_PID%" get ProcessId /value >nul 2>&1
    if errorlevel 1 (
        echo SMOKE FAILED: app exited within 5 seconds.
        exit /b 1
    )
    :: App is still alive - kill it cleanly
    taskkill /PID %SMOKE_PID% /F >nul 2>&1
    :: Also kill any child dotnet processes that were spawned
    for /f "tokens=2" %%i in ('wmic process where "CommandLine like '%%QuickMail.dll%%'" get ProcessId /value 2^>nul ^| findstr "ProcessId"') do taskkill /PID %%i /F >nul 2>&1
    echo SMOKE PASSED: app started and stayed alive for 5 seconds.
) else (
    echo SMOKE FAILED: could not find app process (may have crashed immediately).
    exit /b 1
)
goto end

:end
endlocal

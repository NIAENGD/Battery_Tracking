@echo off
setlocal EnableDelayedExpansion

set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR%"=="" set "SCRIPT_DIR=.\"
set "ROOT_DIR=%SCRIPT_DIR%"
set "CLI_PROJECT=%ROOT_DIR%src\BatteryTracker.Cli\BatteryTracker.Cli.csproj"
set "PUBLISH_DIR=%ROOT_DIR%artifacts\win-arm64"
set "CLI_EXE=%PUBLISH_DIR%\BatteryTracker.Cli.exe"

if "%~1"=="" goto usage
set "COMMAND=%~1"
shift

if /I "%COMMAND%"=="build" goto build
if /I "%COMMAND%"=="compile" goto build
if /I "%COMMAND%"=="start" goto start
if /I "%COMMAND%"=="stop" goto stop
if /I "%COMMAND%"=="status" goto status
if /I "%COMMAND%"=="test" goto test

goto usage

:ensureBuild
if exist "%CLI_EXE%" (
    exit /b 0
)
echo CLI executable not found. Publishing BatteryTracker CLI...
call :build
exit /b %ERRORLEVEL%

:build
echo Publishing BatteryTracker CLI (Release, win-arm64)...
dotnet publish "%CLI_PROJECT%" -c Release -r win-arm64 --self-contained true -o "%PUBLISH_DIR%"
if ERRORLEVEL 1 (
    echo Failed to publish BatteryTracker CLI.
    exit /b 1
)
echo Publish complete. Executable located at "%CLI_EXE%".
exit /b 0

:start
call :ensureBuild
if ERRORLEVEL 1 exit /b %ERRORLEVEL%
"%CLI_EXE%" start %*
exit /b %ERRORLEVEL%

:stop
call :ensureBuild
if ERRORLEVEL 1 exit /b %ERRORLEVEL%
"%CLI_EXE%" stop %*
exit /b %ERRORLEVEL%

:status
call :ensureBuild
if ERRORLEVEL 1 exit /b %ERRORLEVEL%
"%CLI_EXE%" status %*
exit /b %ERRORLEVEL%

:test
call :ensureBuild
if ERRORLEVEL 1 exit /b %ERRORLEVEL%
for /f %%i in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd-HHmmss"') do set "timestamp=%%i"
set "DIAG_DIR=%ROOT_DIR%artifacts\diagnostics"
if not exist "%DIAG_DIR%" mkdir "%DIAG_DIR%" >nul 2>&1
set "LOG_FILE=%DIAG_DIR%\telemetry-selftest-!timestamp!.log"
echo Running telemetry diagnostics self-test (20 seconds)...
"%CLI_EXE%" selftest --data-directory "%DIAG_DIR%" --output "%LOG_FILE%"
set "EXITCODE=%ERRORLEVEL%"
if "%EXITCODE%"=="0" (
    echo Self-test completed successfully. Summary written to "%LOG_FILE%".
) else (
    echo Self-test encountered errors. Review "%LOG_FILE%" for details.
)
exit /b %EXITCODE%

:usage
echo BatteryTracker bootstrapper
set "SCRIPT_NAME=%~nx0"
echo Usage: %SCRIPT_NAME% ^<command^> [options]
echo.
echo Commands:
echo    build   Publish the CLI for win-arm64 (self-contained)
echo    start   Start a telemetry session (forward additional arguments)
echo    stop    Signal the active telemetry session to flush and exit
echo    status  Show the most recent session state
echo    test    Run a 20-second diagnostics capture and write a summary log
exit /b 1

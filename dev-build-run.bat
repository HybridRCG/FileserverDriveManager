@echo off
REM ============================================================
REM  Dev build+run shortcut for FileserverDriveManager
REM  Lives on the Mac share, mapped as Y: on the dev VM.
REM  Usage: just run  Y:\dev-build-run.bat
REM ============================================================

setlocal

set SHARE_UNC=\\192.168.1.200\FileserverDriveManager
set SHARE_USER=riaangrobler
REM Password intentionally not stored here - see note at bottom.

echo.
echo === Checking Y: drive mapping ===
if exist Y:\FileserverDriveManager.csproj (
    echo Y: is already mapped and reachable.
) else (
    echo Y: missing or share unreachable - attempting remap...
    net use Y: /delete >nul 2>&1
    net use Y: %SHARE_UNC% /user:%SHARE_USER% /persistent:yes
    if errorlevel 1 (
        echo.
        echo FAILED to remap Y:. Common causes:
        echo   - Mac is asleep / File Sharing toggled off
        echo   - Wrong password ^(this script does not store one - you'll be prompted^)
        echo Check the Mac side, then re-run this script.
        pause
        exit /b 1
    )
)

echo.
echo === Switching to Y: and building ===
Y:
cd \

dotnet build
if errorlevel 1 (
    echo.
    echo BUILD FAILED - see errors above. Not launching the app.
    pause
    exit /b 1
)

echo.
echo === Build succeeded - launching app ===
dotnet run

endlocal

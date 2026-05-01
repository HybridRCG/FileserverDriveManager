# ===================================================================
# FILESERVER DRIVE MANAGER - BUILD AND RELEASE SCRIPT
# ===================================================================
# This script auto-detects version from Program.cs and builds the app
# Run this on your Windows PC: .\build.ps1
# ===================================================================

# STEP 1: Navigate to project directory
cd C:\FileserverDriveManager

# STEP 2: Kill any running instances of FileserverDriveManager.exe
Write-Host "==> Checking for running instances..." -ForegroundColor Cyan
$processes = Get-Process -Name "FileserverDriveManager" -ErrorAction SilentlyContinue
if ($processes) {
    Write-Host "==> Stopping $($processes.Count) running instance(s)..." -ForegroundColor Yellow
    $processes | Stop-Process -Force
    Start-Sleep -Seconds 2
}

# STEP 3: Auto-detect version from Program.cs
Write-Host "==> Detecting version from Program.cs..." -ForegroundColor Cyan
$versionLine = Get-Content "Program.cs" | Select-String 'APP_VERSION = "v.*"' | Select-Object -First 1
if ($versionLine -match 'APP_VERSION = "v([0-9.]+)"') {
    $VERSION = $matches[1]
    Write-Host "==> Detected version: v$VERSION" -ForegroundColor Green
} else {
    Write-Host "==> ERROR: Could not detect version from Program.cs!" -ForegroundColor Red
    exit 1
}

# STEP 4: Update installer.nsi with detected version
Write-Host "==> Updating installer.nsi with version $VERSION..." -ForegroundColor Cyan
$nsiContent = Get-Content "installer.nsi" -Raw
$nsiContent = $nsiContent -replace '!define APP_VERSION ".*"', "!define APP_VERSION `"$VERSION`""
$nsiContent = $nsiContent -replace 'VIProductVersion ".*"', "VIProductVersion `"$VERSION.0.0`""
Set-Content "installer.nsi" -Value $nsiContent -NoNewline
Write-Host "==> installer.nsi updated" -ForegroundColor Green

# STEP 5: Clean previous builds
Write-Host "==> Cleaning previous builds..." -ForegroundColor Cyan
dotnet clean

# STEP 6: Build the project
Write-Host "==> Building project..." -ForegroundColor Cyan
dotnet build --configuration Release

# STEP 7: Publish self-contained executable
Write-Host "==> Publishing self-contained executable..." -ForegroundColor Cyan
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true

if ($LASTEXITCODE -ne 0) {
    Write-Host "==> ERROR: Publish failed!" -ForegroundColor Red
    exit 1
}

# STEP 8: Wait for file to unlock (prevents NSIS access errors)
Write-Host "==> Waiting for executable to unlock..." -ForegroundColor Cyan
Start-Sleep -Seconds 5

# STEP 9: Verify executable exists
$exePath = "bin\Release\net8.0-windows\win-x64\publish\FileserverDriveManager.exe"
if (-not (Test-Path $exePath)) {
    Write-Host "==> ERROR: Executable not found at $exePath" -ForegroundColor Red
    exit 1
}

Write-Host "==> Executable built successfully!" -ForegroundColor Green
Write-Host "Location: $exePath" -ForegroundColor Cyan
$fileInfo = Get-Item $exePath
Write-Host "Size: $([math]::Round($fileInfo.Length / 1MB, 2)) MB" -ForegroundColor Cyan

# STEP 10: Embed icon into executable (Resource Hacker)
Write-Host "==> Embedding icon into executable..." -ForegroundColor Cyan
& "C:\Program Files (x86)\Resource Hacker\ResourceHacker.exe" -open $exePath -save $exePath -action addoverwrite -res icon.ico -mask ICONGROUP,1,1033

# STEP 11: Wait for Resource Hacker to complete and unlock file
Write-Host "==> Waiting for Resource Hacker to complete..." -ForegroundColor Cyan
Start-Sleep -Seconds 3

# STEP 12: Build NSIS installer
Write-Host "==> Building NSIS installer..." -ForegroundColor Cyan
& "C:\Program Files (x86)\NSIS\makensis.exe" installer.nsi

if ($LASTEXITCODE -ne 0) {
    Write-Host "==> ERROR: NSIS installer build failed!" -ForegroundColor Red
    exit 1
}

# STEP 13: Verify installer was created
$installerName = "Drive Manager V$VERSION.exe"
if (Test-Path $installerName) {
    $installerInfo = Get-Item $installerName
    Write-Host "==> SUCCESS! Installer created: $installerName" -ForegroundColor Green
    Write-Host "Size: $([math]::Round($installerInfo.Length / 1MB, 2)) MB" -ForegroundColor Cyan
} else {
    Write-Host "==> ERROR: Installer was not created!" -ForegroundColor Red
    Write-Host "Expected: $installerName" -ForegroundColor Red
    exit 1
}

Write-Host "`n==> Build process complete!" -ForegroundColor Green
Write-Host "==> Installer location: .\$installerName" -ForegroundColor Cyan

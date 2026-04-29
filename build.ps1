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
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

# STEP 8: Navigate to published executable location
Write-Host "==> Executable built successfully!" -ForegroundColor Green
Write-Host "Location: bin\Release\net8.0-windows\win-x64\publish\FileserverDriveManager.exe" -ForegroundColor Cyan

# STEP 9: Ask to test (optional)
$testExe = Read-Host "Do you want to test the executable? (y/n)"
if ($testExe -eq 'y' -or $testExe -eq 'Y') {
    Write-Host "==> Launching executable for testing..." -ForegroundColor Yellow
    Write-Host "Close the application window when done testing" -ForegroundColor Yellow
    Start-Process -FilePath "bin\Release\net8.0-windows\win-x64\publish\FileserverDriveManager.exe" -Wait
}

# STEP 10: Embed icon into executable (Resource Hacker)
Write-Host "==> Embedding icon into executable..." -ForegroundColor Cyan
$exePath = "bin\Release\net8.0-windows\win-x64\publish\FileserverDriveManager.exe"
& "C:\Program Files (x86)\Resource Hacker\ResourceHacker.exe" -open $exePath -save $exePath -action addoverwrite -res icon.ico -mask ICONGROUP,1,1033

# STEP 11: Build NSIS installer
Write-Host "==> Building NSIS installer..." -ForegroundColor Cyan
& "C:\Program Files (x86)\NSIS\makensis.exe" installer.nsi

# STEP 12: Verify installer was created
$installerName = "Drive Manager V$VERSION.exe"
if (Test-Path $installerName) {
    Write-Host "==> SUCCESS! Installer created: $installerName" -ForegroundColor Green
    
    # STEP 13: Optional - Test installer
    $testInstall = Read-Host "Do you want to test the installer? (y/n)"
    if ($testInstall -eq 'y' -or $testInstall -eq 'Y') {
        Write-Host "==> Running installer..." -ForegroundColor Cyan
        & ".\$installerName"
    }
    
    # STEP 14: Create GitHub release
    $createRelease = Read-Host "Do you want to create GitHub release? (y/n)"
    if ($createRelease -eq 'y' -or $createRelease -eq 'Y') {
        Write-Host "==> Creating GitHub release v$VERSION..." -ForegroundColor Cyan
        
        # Create release notes
        $releaseNotes = @"
## Fileserver Drive Manager v$VERSION

### 🎉 New Features
- **Multi-VPN Support**: Choose between Tailscale or NetBird in Settings
- **Enhanced Status Bar**: Shows Network IP, Tailscale IP, and NetBird IP simultaneously
- **NetBird Integration**: Full support for NetBird VPN alongside Tailscale
- **VPN Install Buttons**: Quick links to install Tailscale or NetBird from Settings

### 🔧 Improvements
- Individual drive management with Add/Remove buttons
- Authentication gates on drive controls
- Share access filtering (3-second timeout per share)
- Real-time network status updates every 5 seconds
- Improved Settings dialog with VPN provider selection

### 📦 Installation
1. Download **$installerName**
2. Run the installer
3. Follow the installation wizard
4. Launch from Start Menu or Desktop shortcut

### 🔐 Security
- AES-encrypted password storage
- Credential validation before saving
- Single-instance mutex (prevents multiple copies)

### 📝 Requirements
- Windows 10/11 (64-bit)
- .NET 8.0 Runtime (included in installer)
- Tailscale or NetBird installed (optional, can install from Settings)
"@
        
        # Create GitHub release with gh CLI
        gh release create "v$VERSION" `
            --title "Fileserver Drive Manager v$VERSION" `
            --notes $releaseNotes `
            "$installerName#Windows Installer (x64)"
        
        Write-Host "==> GitHub release v$VERSION created successfully!" -ForegroundColor Green
    }
} else {
    Write-Host "==> ERROR: Installer was not created!" -ForegroundColor Red
    Write-Host "Expected: $installerName" -ForegroundColor Red
    Write-Host "Check for errors in the NSIS build output above" -ForegroundColor Red
}

Write-Host "`n==> Build process complete!" -ForegroundColor Green

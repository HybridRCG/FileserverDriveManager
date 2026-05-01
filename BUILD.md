# Fileserver Drive Manager - Build Instructions

## 📋 Complete Build and Release Process

### **STEP 1: Archive Current Version (Mac)**
Before copying new code, archive the current Program.cs:
```bash
cd /Users/riaangrobler/FileserverDriveManager
./archive-before-build.sh
```

### **STEP 2: Copy Files to Windows PC**
Copy these files from Mac to Windows:
- `Program.cs` → `C:\FileserverDriveManager\Program.cs`
- `installer.nsi` → `C:\FileserverDriveManager\installer.nsi` (optional - only if updated)
- `build.ps1` → `C:\FileserverDriveManager\build.ps1` (optional - only if updated)

### **STEP 3: Run Build Script (Windows)**
Open PowerShell as Administrator and run:
```powershell
cd C:\FileserverDriveManager
.\build.ps1
```

**That's it!** The script automatically:
1. ✅ Detects version from Program.cs (APP_VERSION)
2. ✅ Cleans previous builds
3. ✅ Builds the project
4. ✅ Publishes self-contained executable
5. ✅ Tests the executable (you verify it works)
6. ✅ Embeds icon with Resource Hacker
7. ✅ Builds NSIS installer → `Drive Manager V{version}.exe`
8. ✅ Optionally tests the installer
9. ✅ Optionally creates GitHub release

---

## 🎯 Version Management

The build system automatically detects the version from `Program.cs`:
```csharp
private const string APP_VERSION = "v3.4";
```

**To create a new version:**
1. Change `APP_VERSION` in `Program.cs` (e.g., `"v3.4"` → `"v3.5"`)
2. Run `.\build.ps1`
3. Done! Installer will be named `Drive Manager V3.5.exe`

---

## 🔧 Manual Build Commands (Alternative)

If you prefer to run commands manually instead of using the script:

### **Clean and Build**
```powershell
cd C:\FileserverDriveManager
dotnet clean
dotnet build --configuration Release
dotnet publish --configuration Release --self-contained true --runtime win-x64
```

### **Test the Executable**
```powershell
cd bin\Release\net8.0-windows\win-x64\publish
.\FileserverDriveManager.exe
```
**Press Ctrl+C after verifying the app works!**

### **Embed Icon (Resource Hacker)**
```powershell
cd C:\FileserverDriveManager
$exePath = "bin\Release\net8.0-windows\win-x64\publish\FileserverDriveManager.exe"
& "C:\Program Files (x86)\Resource Hacker\ResourceHacker.exe" -open $exePath -save $exePath -action addoverwrite -res icon.ico -mask ICONGROUP,1,1033
```

### **Build Installer (NSIS)**
```powershell
& "C:\Program Files (x86)\NSIS\makensis.exe" installer.nsi
```

Output: **`Drive Manager V{version}.exe`** (version auto-detected)

### **Test Installer (Optional)**
```powershell
& ".\Drive Manager V3.4.exe"  # Replace 3.4 with your version
```

---

## 🚀 GitHub Release

The build script (`build.ps1`) includes an optional GitHub release step.

### **Automatic Release (via build.ps1)**
When prompted during the build:
```
Do you want to create GitHub release? (y/n)
```
Type `y` and press Enter. The script will:
- Auto-detect the version
- Create release notes
- Upload the installer
- Tag the release

### **Manual Release**
```powershell
# Replace {version} with your actual version (e.g., 3.4)
gh release create "v{version}" `
    --title "Fileserver Drive Manager v{version}" `
    --notes-file release-notes.md `
    "Drive Manager V{version}.exe#Windows Installer (x64)"
```

---

## 📁 File Locations

### **Mac (Development)**
- Project: `/Users/riaangrobler/FileserverDriveManager/`
- Archive script: `/Users/riaangrobler/FileserverDriveManager/archive-before-build.sh`
- Archives: `/Users/riaangrobler/FileserverDriveManager/archive/`

### **Windows (Build)**
- Project: `C:\FileserverDriveManager\`
- Source: `C:\FileserverDriveManager\Program.cs`
- Installer script: `C:\FileserverDriveManager\installer.nsi`
- Build script: `C:\FileserverDriveManager\build.ps1`
- Output executable: `C:\FileserverDriveManager\bin\Release\net8.0-windows\win-x64\publish\FileserverDriveManager.exe`
- Output installer: `C:\FileserverDriveManager\Drive Manager V{version}.exe`

### **User Data**
- Settings: `%APPDATA%\FileserverDriveManager-settings.json`
- Logs: `%APPDATA%\FileserverDriveManager.log`
- Custom logo: `%APPDATA%\FileserverDriveManager\logo.png`
- Custom icon: `%APPDATA%\FileserverDriveManager\icon.png`

---

## 🎯 Quick Reference

### **Typical Workflow**
```bash
# On Mac - Archive current version
cd /Users/riaangrobler/FileserverDriveManager
./archive-before-build.sh

# Copy Program.cs to Windows PC
# Then on Windows:
```

```powershell
# On Windows - Build everything
cd C:\FileserverDriveManager
.\build.ps1
```

**That's it! One command builds everything with auto-detected version.**

---

## ✅ Verification Checklist

Before releasing:
- [ ] `APP_VERSION` updated in Program.cs
- [ ] Program.cs compiles without errors
- [ ] Executable launches and UI loads correctly
- [ ] Authentication works with test credentials
- [ ] Share dropdown populates after authentication
- [ ] Add/Remove buttons work correctly
- [ ] Mount Drives button mounts all configured drives
- [ ] Settings dialog opens and saves changes
- [ ] VPN provider selection works (Tailscale/NetBird)
- [ ] Status bar shows all IPs correctly (LAN, Tailscale, NetBird)
- [ ] Icon embedded correctly in executable
- [ ] Installer creates shortcuts on Desktop and Start Menu
- [ ] Uninstaller removes all files and registry entries
- [ ] GitHub release created with correct version and assets

---

## 🐛 Troubleshooting

### **Build Errors**
- Ensure .NET 8.0 SDK is installed
- Check Program.cs for syntax errors
- Run `dotnet clean` before rebuilding

### **Version Detection Fails**
- Verify `APP_VERSION = "v3.4"` exists in Program.cs
- Check exact format: `private const string APP_VERSION = "v3.4";`
- Version must be in format `vX.X` (e.g., v3.4, v4.0, v10.5)

### **Icon Not Embedded**
- Verify `icon.ico` exists in project root
- Check Resource Hacker path: `C:\Program Files (x86)\Resource Hacker\`

### **Installer Fails**
- Verify NSIS is installed: `C:\Program Files (x86)\NSIS\`
- Check `installer.nsi` for correct file paths
- Ensure all required files exist (logo.png, icon.png, icon.ico)

### **GitHub Release Fails**
- Verify GitHub CLI is authenticated: `gh auth status`
- Check repository name matches: `HybridRCG/FileserverDriveManager`
- Ensure installer file exists: `Drive Manager V{version}.exe`

---

## 🔄 Updating to a New Version

**Simple 3-step process:**

1. **Update version in Program.cs:**
   ```csharp
   private const string APP_VERSION = "v3.5";  // Changed from v3.4
   ```

2. **Run build script:**
   ```powershell
   .\build.ps1
   ```

3. **Done!**
   - Installer created: `Drive Manager V3.5.exe`
   - GitHub release: `v3.5`
   - All version numbers automatically updated

---

**Version:** Auto-detected from Program.cs  
**Last Updated:** 2026-04-29  
**Author:** Riaan Grobler  
**Company:** Groblers CSS

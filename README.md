# Fileserver Drive Manager v3.2

A Windows desktop application (.NET 8.0) that automatically mounts network drives from your fileserver when connected via VPN.

## Features

✨ **Multi-VPN Support** - Choose between Headscale, NetBird, or Tailscale
🚀 **Auto-Mount** - Automatically mounts drives when VPN is connected
💾 **Persistent Settings** - Saves credentials and preferences
🔒 **Secure** - Credentials stored locally
🎯 **Auto-Startup** - Launches on Windows startup (optional)
📡 **Smart Detection** - Auto-detects VPN connection (100.64.0.0/10 range)

## Supported Drives

- **S:** → \\192.168.1.26\shared
- **T:** → \\192.168.1.26\torrents
- **U:** → \\192.168.1.26\usenet
- **V:** → \\192.168.1.26\videos
- **P:** → \\192.168.1.26\pictures
- **W:** → \\192.168.1.26\backups

## Requirements

- Windows 10/11
- .NET 8.0 Runtime
- One of: Headscale, NetBird, or Tailscale installed

## Installation

1. Download `Drive Manager V3.2.exe` from [Releases](https://github.com/HybridRCG/FileserverDriveManager/releases)
2. Run the installer
3. Launch "Fileserver Drive Manager"
4. Configure settings (IP, username, password, VPN provider)
5. Click "Launch VPN" and connect
6. Click "Mount All Drives"

## Building from Source

```powershell
cd FileserverDriveManager
dotnet restore
dotnet build --configuration Release
dotnet publish --configuration Release
```

## Creating Installer

Requires [NSIS](https://nsis.sourceforge.io/)

```powershell
makensis installer.nsi
```

## Version History

### v3.2 (Current)
- Added Headscale and NetBird VPN support
- VPN provider selection in Settings
- Auto-detection of installed VPN providers
- Quick install links for missing providers
- Enhanced VPN IP detection (100.64.0.0/10 range)

### v3.1
- Initial release with Tailscale support
- Auto-mount functionality
- Settings persistence
- Tray icon integration

## License

© 2025 Hybrid RCG. All rights reserved.

## Support

For issues or questions, please open an issue on GitHub.

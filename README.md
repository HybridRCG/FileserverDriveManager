# Fileserver Drive Manager v5.3.1

A Windows desktop application (.NET 8.0) that maps and manages SMB network drives from your fileserver, with VPN-aware auto-mounting over Tailscale or NetBird.

## Features

✨ **Dynamic Drive Mapping** - Add/remove any number of drive letter → share name pairs, not a fixed list
🚀 **Auto-Mount on Startup** - Automatically mounts your saved drives once a VPN connection is detected
🔀 **Multi-VPN Support** - Works with Tailscale or NetBird automatically - no manual provider selection needed, the app checks both and uses whichever is connected
💾 **Persistent Settings** - Saves credentials, drive mappings, and preferences locally
🔒 **Secure Storage** - Credentials encrypted at rest
🎯 **Auto-Startup** - Optional launch on Windows startup, minimizes to tray
📡 **Reachability Testing** - Settings includes a lightweight "Test Connection" check (TCP port 445) independent of credentials or share names
🔧 **VPN Adapter Detection** - Detects Tailscale (100.64.0.0/10 CGNAT range) and NetBird (adapter name `wt0` / description "WireGuard Tunnel", any valid IPv4 - NetBird's address range is configurable per network)

## Drives

Drive letters and share names are fully user-configurable in the app — add as many mappings as you need via the main window (e.g. drive letter `G:` → share `General`). There's no fixed drive list; what's mapped depends on what you've added and saved in Settings.

## Requirements

- Windows 10/11
- .NET 8.0 Runtime (bundled in self-contained release builds - no separate install needed)
- Tailscale or NetBird installed and connected to reach the fileserver over VPN (or the fileserver must be reachable directly, e.g. on the same LAN)

## Installation

1. Download the latest release `.exe` from [Releases](https://github.com/HybridRCG/FileserverDriveManager/releases)
2. Run the installer
3. Launch "Fileserver Drive Manager"
4. In Settings: set the fileserver IP, enter credentials (VPN provider is auto-detected - no need to select one)
5. Use "Test Connection" in Settings to confirm the fileserver is reachable before authenticating
6. Add your drive letter → share mappings on the main window
7. Connect Tailscale or NetBird (either one, the app finds whichever is active), then "Mount All Drives" (or enable Auto-Mount on Startup in Settings)

## Building from Source

```powershell
cd FileserverDriveManager
dotnet restore
dotnet build --configuration Release
dotnet publish --configuration Release
```

For local dev/testing on a separate VM without a full IDE setup, see `dev-build-run.bat` and `Directory.Build.props` - they support building the project directly off an SMB-mounted dev share (handles NuGet's atomic-rename issue over SMB automatically).

## Creating Installer

Requires [NSIS](https://nsis.sourceforge.io/)

```powershell
makensis installer.nsi
```

## Releasing

```bash
./release.sh <version>     # e.g. ./release.sh 5.4
```

Bumps the version in the `.csproj`, commits, tags, and pushes — GitHub Actions builds and publishes the release automatically.

## Version History

### v5.3.1 (Current)
- Removed the manual VPN Provider selection from Settings - the app now auto-detects Tailscale/NetBird by checking both, instead of requiring you to pick one first
- Simplified the Tailscale/NetBird launch buttons on the main window (no more "this isn't the selected provider, launch anyway?" prompt)

### v5.3
- Fixed NetBird VPN IP detection - adapter is named `wt0` with description "WireGuard Tunnel" on Windows (never contains the literal word "netbird"), and its IPv4 range is not the 100.x CGNAT range Tailscale uses
- Settings "Test Connection" now does a genuine lightweight reachability check (TCP port 445) against whatever IP is currently typed, instead of requiring a hardcoded share and valid credentials to succeed
- Added local dev workflow support (`Directory.Build.props`, `dev-build-run.bat`) for building/testing off an SMB-mounted share on a separate VM

### v5.2
- Allow Tailscale and NetBird to coexist (both can be installed; one is selected as the active detection source)
- Smart button state management for VPN and Mount All actions

### v5.1
- Fixed UI contrast and drive status detection
- Version now read automatically from assembly metadata (single source of truth via `.csproj`)

### v5.0
- Added button hover/press/disabled states and DPI scaling improvements

### v4.0 - v4.8
- Modernized GUI (Segoe UI, contemporary colors, improved DPI scaling)
- Multiple self-contained single-file publish pipeline fixes (framework-dependent artifact issues, trimming, debug symbols)

### v3.2
- Added Headscale and NetBird VPN support (initial multi-provider groundwork)
- VPN provider selection in Settings

### v3.1
- Initial release with Tailscale support
- Auto-mount functionality, Settings persistence, tray icon integration

## License

© 2026 Hybrid RCG. All rights reserved.

## Support

For issues or questions, please open an issue on GitHub.

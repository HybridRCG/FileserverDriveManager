# Fileserver Drive Manager v7.3.1

A Windows desktop application (.NET 8.0) that maps and manages SMB network drives from your fileserver, with VPN-aware auto-mounting across LAN, Tailscale, and NetBird.

## Features

✨ **Dynamic Drive Mapping** - Add/remove any number of drive letter → share name pairs, not a fixed list, each with its own per-row unmount/remove button
🏁 **Multi-Path Racing** - Configure LAN, Tailscale, and NetBird IPs for the fileserver; Authenticate races all three concurrently and uses whichever responds fastest
🚀 **Auto-Mount on Startup** - Automatically races and mounts your saved drives once any VPN connection is detected
🔀 **Multi-VPN Support** - Works with Tailscale or NetBird automatically - no manual provider selection needed, the app checks both and uses whichever is connected
🌓 **Dark Mode** - Full dark/light theme toggle in Settings (applies on next launch)
🎨 **Modern Rounded UI** - Owner-drawn rounded buttons, cards, and pill-shaped status badges instead of default WinForms chrome
💾 **Persistent Settings** - Saves credentials, drive mappings, and preferences locally
🔒 **Secure Storage** - Credentials encrypted at rest
🎯 **Auto-Startup** - Optional launch on Windows startup, minimizes to tray
📡 **Reachability Testing** - Settings' "Test Connection" races all three configured IPs and shows each result ranked by speed, independent of credentials or share names
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
4. In Settings: set the fileserver's LAN, Tailscale, and NetBird IPs (any/all can be left blank if not applicable), enter credentials
5. Use "Test Connection" in Settings to race all three and confirm at least one is reachable before authenticating
6. Add your drive letter → share mappings on the main window
7. Click "Authenticate" - it races LAN/Tailscale/NetBird automatically and uses whichever responds fastest, then "Mount All Drives" (or enable Auto-Mount on Startup in Settings)

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

### v7.3.1 (Current)
- Fixed the "Using {Provider}" status message vanishing before it could be read - the whole Authenticate → connect → list shares sequence can complete in under a second on a fast network, so the final status now permanently shows which provider was used ("Ready via Tailscale - 2 shares available") instead of a transient message that gets overwritten
- Manual Authenticate now logs its provider race result (`Authenticate using {Provider} ({IP}, {ms}ms)`), matching what auto-connect-on-startup already logged, so the choice is always verifiable in View Logs afterward

### v7.3.0
- Replaced the single fixed Fileserver IP with three candidate paths: LAN, Tailscale, NetBird
- Authenticate (and auto-mount on startup) now races all three concurrently via a timed TCP connect to port 445 and uses whichever responds fastest
- Settings' Test Connection races all three currently-typed IPs and shows each ranked by speed
- Old single `fileserverIP` setting is migrated automatically into the new LAN field on first launch after upgrading

### v7.2.0
- Each drive row now has its own "Unmount" button, replacing the old shared "Remove" button that required selecting a row first
- Unmount now genuinely releases the mapped drive letter (`net use X: /delete`) before removing it from the saved list - previously "Remove" only edited the saved list without ever unmounting anything

### v7.1.1
- Fixed the DataGridView row selection rendering solid black in both themes - `SelectionBackColor` used an alpha-transparent color that DataGridView's GDI-based rendering doesn't composite reliably, especially with double buffering enabled
- Fixed the alternating-row shade being hardcoded near-white even in dark mode

### v7.1.0
- Added a Dark Mode toggle in Settings (applies on next launch - WinForms owner-drawn controls can't safely re-theme live)
- Centralized every remaining hardcoded color into `AppTheme` so the whole app switches as one unit

### v7.0.1
- Fixed pill status badges "ghosting"/stacking during a window resize (DataGridView wasn't double-buffered)
- Fixed a cut-off checkbox in Settings after the card-style redesign didn't leave enough row height

### v7.0.0 - "Full tier" GUI
- Rounded buttons, cards, and pill-shaped status badges via custom owner-drawn `Paint` handlers, replacing v6's flat rectangles
- Replaced `GroupBox` sections with rounded-border panels + separate header labels

### v6.0.0 - "Cheap tier" GUI
- Replaced six competing button colors (purple/pink/red/green/blue/gray) with a single accent color for primary actions and an outline/secondary style for everything else
- Drive status shown as color-coded text (later upgraded to pill badges in v7.0.0)

### v5.3.1
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

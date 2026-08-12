# Fileserver Drive Manager v7.5.15

A Windows desktop application (.NET 8.0) that maps and manages SMB network drives from your fileserver, with VPN-aware auto-mounting across LAN, Tailscale, and NetBird.

## Features

✨ **Dynamic Drive Mapping** - Add/remove any number of drive letter → share name pairs, not a fixed list, each with its own per-row unmount/remove button
🏁 **Multi-Path Racing** - Configure any combination of LAN, Tailscale, and NetBird IPs for the fileserver (leave any blank if not applicable, e.g. VPN-only machines with no LAN); Authenticate races all configured paths concurrently and uses whichever responds fastest
🚀 **Auto-Mount on Startup** - Automatically races and mounts your saved drives once any VPN connection is detected
🔀 **Multi-VPN Support** - Works with Tailscale or NetBird automatically - no manual provider selection needed, the app checks both and uses whichever is connected
🌓 **Dark Mode** - Full dark/light theme toggle in Settings (applies on next launch)
🎨 **Modern Rounded UI** - Owner-drawn rounded buttons, cards, and pill-shaped status badges instead of default WinForms chrome
💾 **Persistent Settings** - Saves credentials, drive mappings, and preferences locally
🔒 **Secure Storage** - Credentials encrypted at rest
🎯 **Auto-Startup** - Optional launch on Windows startup, minimizes to tray
📡 **Reachability Testing** - Settings' "Test Connection" races all three configured IPs and shows each result ranked by speed, independent of credentials or share names
🔧 **VPN Adapter Detection** - Detects Tailscale (100.64.0.0/10 CGNAT range) and NetBird (adapter name `wt0` / description "WireGuard Tunnel", any valid IPv4 - NetBird's address range is configurable per network)
🔄 **Live Failover** - Monitors the active connection every 5 seconds and automatically switches to the next-fastest reachable path if the current one genuinely stops responding, remounting drives against it
🔔 **Disconnect Notifications** - Configurable tray notification if a drive stays unavailable past a threshold (default 30 min)

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

### v7.5.15 (Current)
- Fixed a race condition in v7.5.14's background retry that could make auto-mount succeed or fail intermittently on restart, even with VPN already connected - the initial startup connection attempt wasn't guarded the same way the periodic retries were, so overlapping concurrent attempts could step on each other (one attempt's stale-session cleanup killing a session another was mid-authenticating with, concurrent mount loops racing against the same drive list)

### v7.5.14
- Auto-mount no longer gives up permanently if the VPN isn't up yet at the fixed 10s startup check - the VPN check now polls for up to 60s, and if that's still not enough, the app keeps retrying quietly in the background with capped backoff (5s→10s→20s→40s→60s) until it connects. Removes the need for any boot-ordering trick against Tailscale/NetBird - the app just waits the VPN out, however long it takes

### v7.5.13
- Auto-startup registry path now self-heals on every launch instead of being written once and never revisited - if the install location ever moves (e.g. upgrading between installer generations with different install folders), the stale `HKCU...\Run` entry is detected and corrected automatically rather than silently pointing at a now-missing exe

### v7.5.12
- Fixed a settings-load bug where a `settings.json` missing the (transient, non-persisted-in-spirit) drive `Status` field - e.g. from an older build, or any hand-edit - would throw and silently wipe the entire saved drive list, even though everything else (credentials, IPs, preferences) loaded fine
- Auto-connect now falls through every TCP-reachable candidate (not just the fastest) before giving up on startup - a fast port-445 reachability check doesn't guarantee the SMB auth layer is actually ready yet, which was causing auto-authentication to fail right after a race pick even though a manual retry moments later would succeed
- Added best-effort stale-session cleanup (`net use \\ip /delete`) before every authentication/mount attempt, preventing "System error 1219" (multiple connections using different usernames) and mount hangs caused by leftover sessions from prior failed attempts
- Live failover checks now back off (up to 30s, capped) instead of re-racing all three paths on every single 5s timer tick when nothing is reachable - cuts log noise and network churn during a real outage without slowing recovery once a path comes back

### v7.5.11
- Renamed "Save IP" to "Save IP's" in Settings

### v7.5.10
- Fixed the "Notify after (min):" field stretching to fill the whole column - a 4-digit max value doesn't need a full-width text box, so it's now a compact 60px field instead

### v7.5.9
- Fixed the "Notify if disconnected for (minutes):" label wrapping to two lines and misaligning with its input field - shortened to "Notify after (min):"

### v7.5.8
- Cleaned up the remaining 6 nullable-reference compiler warnings (down from 54 total across v7.5.7 and this release) - null-coalescing on `ComboBox.SelectedItem.ToString()` and two icon-loading path variables

### v7.5.7
- Cleaned up 48 of the app's 54 nullable-reference compiler warnings: `DriveMapping` properties now use `required` instead of leaving them silently non-nullable, all `MainForm` UI fields (populated in `InitializeComponents()`, not the constructor) are explicitly annotated, all 9 event handler signatures now match `EventHandler`'s nullable `sender`, and JSON settings loading no longer silently assumes `GetString()` can't return null

### v7.5.6 - Dark mode audit, visual consistency, disconnect notifications
- **Dark mode**: fixed 8 controls that were never wired to `AppTheme` and stayed white-on-white in dark mode - username/password fields, drive letter/share dropdowns, all 3 fileserver IP fields, and the tray icon's right-click menu
- **Visual consistency**: the per-row "Unmount" button is now a rounded outline button matching the rest of v7's design instead of a flat system-drawn `DataGridViewButtonColumn` rectangle
- **New feature**: configurable disconnect notifications - Settings now has a "Notify after (min):" field (default 30); if a mounted drive stays unavailable that long, the app shows a Windows tray balloon notification. Fires once per outage and resets automatically once the drive reconnects.

### v7.5.5
- Fixed the window flashing visibly for the full 10-second startup delay before hiding to tray - it now hides immediately on launch instead of waiting until after the delay, so the app starts tray-only from the first instant like a proper background utility
- Fixed a related bug where manually clicking "Show" from the tray during that startup delay would get silently undone the moment the delay finished, since the old hide-to-tray code ran unconditionally afterward

### v7.5.4
- Fixed the Tailscale IP/NetBird IP/Network status labels staying blank for a long time on startup - `UpdateNetworkStatus()` was only ever wired to the 5-second status timer, which didn't start until the entire race+authenticate+mount sequence finished. If mounting took a while, the labels sat empty that whole time even though the actual connection succeeded quickly. The timer (and an immediate first status update) now starts right after the initial 10s startup delay, independent of how long the rest of the sequence takes.

### v7.5.3
- Fixed the "Network:" status label picking up a VPN tunnel adapter's IP and mislabeling it as a physical LAN connection - it only excluded Tailscale's 100.x range before, never NetBird's (whose range varies per network), so on a machine with no physical LAN, NetBird's own address showed up as "(Ethernet)"
- The fileserver race now shows **"LAN (via VPN route)"** instead of plain "LAN" when a configured LAN IP is only reachable because a VPN is advertising an exit-node route for that subnet, rather than a genuine direct connection - a raw TCP check can't tell the difference on its own, so this cross-checks against the client's own detected local subnet

### v7.5.2
- Converted every remaining blocking call in the startup/mount/authenticate path to genuinely async (`Process.WaitForExitAsync`, `Task.Delay` instead of `Thread.Sleep`) - even with v7.5.1's bounded timeouts, a synchronous block on the UI thread for several seconds still made the app appear frozen (ignoring right-clicks, showing "Not Responding") during that window. Startup, Authenticate, Mount All, and failover now all properly yield the UI thread instead of blocking it.

### v7.5.1
- Fixed a real UI-freeze bug: `MountAllDrives()` had one `Process.WaitForExit()` call with no timeout at all. If `net use` itself hung (observed on a machine with only Tailscale/NetBird and no LAN, where an exit-node routing quirk let the lightweight port-445 race check succeed but the actual mount command stall), this blocked the UI thread indefinitely - including the tray icon context menu, since auto-mount-on-startup runs on the UI thread. Now bounded to 15s with a "Timeout" status on failure instead of hanging forever.

### v7.5.0 - Live failover
- The app now monitors the active fileserver connection every 5 seconds (piggybacking on the existing status timer) and automatically fails over to the next-fastest reachable path (LAN/Tailscale/NetBird) if the current one genuinely stops responding
- Deliberately does NOT switch just because a marginally faster path exists while the current one still works - avoids disruptive flapping between providers with similar latency
- On failover: unmounts all drives from the dead IP, re-races the remaining candidates, remounts against the winner, and logs the whole sequence

### v7.3.1
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

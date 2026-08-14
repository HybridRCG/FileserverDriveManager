using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Net.Http;
using Microsoft.Win32;

namespace FileserverDriveManager
{
    public class DriveMapping
    {
        // v7.5.7: 'required' tells the compiler every instantiation MUST set
        // these (which was always true via object initializers throughout
        // this codebase) - removes the CS8618 warnings correctly instead of
        // just suppressing them, since the actual guarantee already existed.
        //
        // v7.5.12: Status is deliberately NOT required. It's transient runtime
        // state (recomputed on every mount attempt), not meaningful persisted
        // data - but System.Text.Json enforces 'required' on deserialize too,
        // so a settings.json written by an older build (or hand-edited/missing
        // the field for any reason) threw "missing required properties" and
        // wiped out the entire drives list on load. DriveLetter/ShareName stay
        // required since those ARE meaningful stored data worth failing loud on.
        public required string DriveLetter { get; set; }
        public required string ShareName { get; set; }
        public string Status { get; set; } = "Not Mounted";

        // v7.5.6: runtime-only tracking for disconnect notifications - not
        // persisted, since a fresh launch shouldn't remember a prior outage.
        [System.Text.Json.Serialization.JsonIgnore]
        public DateTime? UnavailableSince { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public bool DisconnectNotified { get; set; }
    }

    // ========================================================================
    // AppTheme - single source of truth for the color palette.
    // v7.1: fields are now mutable (not readonly) and grouped under
    // ApplyLightMode/ApplyDarkMode so the whole app's colors can switch as a
    // unit. Windows Forms owner-drawn controls (v7's rounded buttons/cards/
    // pills) capture colors into Paint-event closures at construction time,
    // so switching modes mid-session would require re-running every one of
    // those closures - not worth the complexity for a small utility app.
    // Instead: ApplyMode() runs once at startup (before InitializeComponents
    // builds any control), and toggling in Settings just saves the
    // preference + prompts a restart to apply it.
    // ========================================================================
    public static class AppTheme
    {
        public static bool IsDark { get; private set; } = false;

        public static Color Accent;         // Primary actions only (one per view)
        public static Color Danger;         // Destructive actions (Remove, Exit)
        public static Color SuccessText;    // "Mounted" / connected status text
        public static Color DangerText;     // "Failed" status text
        public static Color MutedText;      // "Not Mounted" / not-connected status text
        public static Color SuccessBg;      // pill fill behind SuccessText
        public static Color DangerBg;       // pill fill behind DangerText
        public static Color MutedBg;        // pill fill behind MutedText
        public static Color TextPrimary;
        public static Color TextSecondary;
        public static Color BorderGray;
        public static Color BgLight;        // window/control background (behind cards)
        public static Color BgWhite;        // card/surface background
        public static Color Disabled;
        public static Color DisabledText;
        public static Color SelectionBg;    // DataGridView row selection highlight - must be fully opaque
        public static Color AlternateRowBg; // DataGridView alternating row shade

        static AppTheme()
        {
            ApplyMode(false);
        }

        public static void ApplyMode(bool dark)
        {
            IsDark = dark;
            if (!dark)
            {
                Accent = Color.FromArgb(0, 120, 212);
                Danger = Color.FromArgb(196, 43, 28);
                SuccessText = Color.FromArgb(16, 124, 16);
                DangerText = Color.FromArgb(196, 43, 28);
                MutedText = Color.FromArgb(96, 94, 92);
                SuccessBg = Color.FromArgb(223, 244, 223);
                DangerBg = Color.FromArgb(250, 224, 220);
                MutedBg = Color.FromArgb(233, 232, 231);
                TextPrimary = Color.FromArgb(50, 49, 48);
                TextSecondary = Color.FromArgb(96, 94, 92);
                BorderGray = Color.FromArgb(200, 200, 200);
                BgLight = Color.FromArgb(230, 230, 230);
                BgWhite = Color.FromArgb(255, 255, 255);
                Disabled = Color.FromArgb(200, 198, 196);
                DisabledText = Color.FromArgb(150, 148, 146);
                // Fully opaque - DataGridView's GDI-based rendering doesn't
                // reliably composite alpha-transparent SelectionBackColor
                // (especially with DoubleBuffered enabled), producing a solid
                // black selected row instead of a subtle tint.
                SelectionBg = Color.FromArgb(214, 232, 248);
                AlternateRowBg = Color.FromArgb(250, 249, 248);
            }
            else
            {
                Accent = Color.FromArgb(60, 160, 240);
                Danger = Color.FromArgb(232, 100, 85);
                SuccessText = Color.FromArgb(110, 200, 110);
                DangerText = Color.FromArgb(232, 100, 85);
                MutedText = Color.FromArgb(160, 158, 156);
                SuccessBg = Color.FromArgb(40, 60, 40);
                DangerBg = Color.FromArgb(65, 40, 38);
                MutedBg = Color.FromArgb(60, 60, 60);
                TextPrimary = Color.FromArgb(235, 235, 233);
                TextSecondary = Color.FromArgb(180, 178, 176);
                BorderGray = Color.FromArgb(80, 80, 80);
                BgLight = Color.FromArgb(30, 30, 30);
                BgWhite = Color.FromArgb(45, 45, 45);
                Disabled = Color.FromArgb(70, 70, 70);
                DisabledText = Color.FromArgb(110, 108, 106);
                SelectionBg = Color.FromArgb(35, 65, 95);
                AlternateRowBg = Color.FromArgb(38, 38, 38);
            }
        }
    }

    public partial class MainForm : Form
    {
        // Version is read from assembly metadata (set in .csproj <Version> tag)
        // This way release.sh automatically updates it and we never have two places to maintain
        private static readonly string APP_VERSION = "v" + (System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version?.ToString(3) ?? "0.0.0")
#if DEBUG
            + "-dev";
#else
            ;
#endif
        
        private List<DriveMapping> drives = new List<DriveMapping>();
        // v7.5.7: all of these are populated inside InitializeComponents()
        // (called from the constructor), not via field initializers - the
        // compiler's null-flow analysis can't see that far, so it flags them
        // as possibly-null even though they're always set before use. `= null!`
        // is the standard, honest way to express "this is definitely set
        // elsewhere, trust me" for this exact WinForms pattern, rather than
        // disabling nullable checking or making every field nullable (which
        // would just push '?' null-checks onto every single usage site).
        private TextBox usernameBox = null!;
        private TextBox passwordBox = null!;
        private ComboBox driveLetterBox = null!;
        private ComboBox shareNameBox = null!;
        private Button authenticateButton = null!;
        private DataGridView drivesGrid = null!;
        private Button addDriveButton = null!;
        private Button mountDrivesButton = null!;
        private Button settingsButton = null!;
        // v7.5.28: viewLogsButton removed from here - the field is now local
        // to the Settings dialog where the button lives (see
        // SettingsButton_Click), since it's no longer a persistent
        // main-window control referenced elsewhere.
        private Button tailscaleButton = null!;
        private Button netbirdButton = null!;
        private Button exitButton = null!;
        private Label statusLabel = null!;
        private Label tailscaleIPLabel = null!;
        private Label netbirdIPLabel = null!;
        private Label lanIPLabel = null!;
        private NotifyIcon notifyIcon = null!;
        private PictureBox logoPicture = null!;
        private System.Windows.Forms.Timer statusTimer = null!;
        private bool isExiting = false;
        private bool isAuthenticating = false;
        private bool autoMountOnStartup = true;
        private bool darkModeEnabled = false;
        private bool isCheckingFailover = false;
        // v7.5.12: backoff state for when NO configured provider is reachable.
        // Without this, the 5s status timer re-raced all three candidates on
        // every single tick while fully down, producing a fresh TCP-connect
        // burst and 3 log lines roughly every 5-10s indefinitely. Backing off
        // (capped) while down cuts that noise and network churn without
        // slowing recovery once a path comes back (reset to 0 on any success).
        private int consecutiveFailoverMisses = 0;
        private DateTime lastFailoverAttempt = DateTime.MinValue;
        // v7.5.14: tracks whether auto-connect has EVER succeeded this
        // session. The HKCU Run key gives no guarantee the app starts after
        // Tailscale/NetBird are actually up - Windows starts Run entries
        // early in logon with no dependency ordering, and VPN clients
        // routinely take longer than that to establish a tunnel. If the
        // initial startup attempt (see TryAutoConnectOnce) never manages to
        // connect, the status timer keeps retrying with backoff until it
        // does, instead of requiring the user to manually click Authenticate
        // or fuss with Task Scheduler dependency ordering.
        private bool hasEverAutoConnected = false;
        private bool isRetryingStartupConnect = false;
        private int consecutiveStartupRetryMisses = 0;
        private DateTime lastStartupRetryAttempt = DateTime.MinValue;
        private int disconnectNotifyMinutes = 30;
        private string username = "";
        private string password = "";
        // v7.3: three possible paths to the fileserver instead of one fixed
        // IP. "fileserverIP" (below) stays as-is throughout the codebase -
        // it now means "the currently resolved/active IP for this session",
        // set by RaceFileserverIPs() whenever Authenticate is clicked, rather
        // than a fixed user-entered value. Every existing mount/test/share-
        // listing call site keeps working unchanged since they all just read
        // fileserverIP as before.
        private string fileserverLanIP = "192.168.1.26";
        private string fileserverTailscaleIP = "100.64.0.2";
        private string fileserverNetbirdIP = "10.64.75.22";
        private string fileserverIP = "192.168.1.26";
        private string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileserverDriveManager.log");

        // v7.5.18: update check against GitHub Releases. Repo is public, so
        // an unauthenticated API call works fine from any client machine
        // (60 req/hour per IP - a manual/settings-open check is nowhere near
        // that). GitHub's API rejects requests with no User-Agent header,
        // hence setting one below.
        private static readonly HttpClient updateHttpClient = new HttpClient() { Timeout = TimeSpan.FromSeconds(8) };
        // v7.5.20: separate client for the actual installer download - the
        // 8s timeout above is right for a small JSON API check (fail fast if
        // GitHub is unreachable) but nowhere near enough for a real ~100MB
        // self-contained single-file exe download, which was aborting with
        // "The request was canceled due to the configured HttpClient.Timeout
        // of 8 seconds elapsing" on a real machine. HttpClient.Timeout is
        // per-client (applies to every request through that instance,
        // overriding any per-call CancellationToken), so this needs its own
        // instance rather than a per-request override.
        private static readonly HttpClient downloadHttpClient = new HttpClient() { Timeout = TimeSpan.FromMinutes(5) };
        private const string UPDATE_CHECK_URL = "https://api.github.com/repos/HybridRCG/FileserverDriveManager/releases/latest";

        public MainForm()
        {
            // v7.1: dark mode must be resolved BEFORE InitializeComponents()
            // builds any control, since local color variables (declared at the
            // top of InitializeComponents) capture AppTheme's colors at that
            // point in time. This is a lightweight peek at just the darkMode
            // key - LoadSavedSettings() (called later) re-reads the full
            // settings file and syncs the Settings dialog's checkbox from the
            // same value, so this isn't duplicating the source of truth, just
            // reading it earlier than the rest of settings can be applied.
            AppTheme.ApplyMode(PeekDarkModePreference());
            darkModeEnabled = AppTheme.IsDark;

            // v7 fix: the window is resizable and now has multiple custom-drawn
            // rounded panels/buttons (Paint event owner-draw). Without double
            // buffering the whole form, resizing can show the same stale-frame
            // ghosting as the DataGridView pills did. Form subclasses can set
            // this directly (it's protected on Control but this class inherits it).
            this.DoubleBuffered = true;
            InitializeComponents();
            LoadDrives();
            RefreshStatus();
            
            // Auto-enable startup on first run
            this.Load += async (s, e) => 
            {
                // v7.5.5: previously the window stayed visible for the full
                // 10s startup delay (inside CheckAndAutoConnect) before
                // hiding to tray - a visible "flash then vanish" on every
                // launch, which is confusing and made the window briefly
                // interactive before yanking it away. Hiding immediately on
                // Load means the app starts tray-only from the first instant,
                // matching how a background auto-mount utility should behave.
                this.WindowState = FormWindowState.Minimized;
                this.ShowInTaskbar = false;
                notifyIcon.Visible = true;

                EnableAutoStartup();
                await CheckAndAutoConnect();
            };
        }

        private void ShowFromTray()
        {
            this.WindowState = FormWindowState.Normal;
            this.ShowInTaskbar = true;
            notifyIcon.Visible = false;
            this.Activate();
        }

        // v7.5.25: now returns a result string so it can be called from a
        // user-facing button (Settings > "Add to Windows Startup") in
        // addition to its original silent call at launch. This is the same
        // self-heal logic as before - not a new mechanism - just exposed as
        // something the user can trigger and get direct confirmation from,
        // for cases where the implicit launch-time registration either
        // hasn't had a chance to run yet (e.g. an install that's never
        // actually been manually launched even once) or isn't sticking for
        // some other reason on a particular machine.
        // v7.5.27: writes/corrects the "StartupApproved" binary flag that
        // backs Task Manager's Startup Apps enable/disable toggle for this
        // app's Run-key entry. Format is Windows' own internal convention
        // (undocumented officially, but well-established): a 12-byte value
        // where byte[0] == 0x02 means enabled, anything else (0x03 is what
        // Task Manager writes when a user clicks Disable) means disabled.
        // Returns a short note ONLY when something was actually changed (an
        // empty string means nothing needed fixing), so the caller can
        // append it to the main result message without always being noisy.
        private string EnsureStartupApproved()
        {
            const string startupApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
            try
            {
                using (var approvedKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(startupApprovedPath))
                {
                    if (approvedKey == null) return "";

                    object? existing = approvedKey.GetValue("FileserverDriveManager");
                    byte[]? existingBytes = existing as byte[];
                    bool isDisabled = existingBytes != null && existingBytes.Length > 0 && existingBytes[0] != 0x02;

                    if (existingBytes == null || isDisabled)
                    {
                        byte[] enabledBytes = new byte[12];
                        enabledBytes[0] = 0x02;
                        approvedKey.SetValue("FileserverDriveManager", enabledBytes, Microsoft.Win32.RegistryValueKind.Binary);
                        Log(isDisabled
                            ? "Startup entry was disabled in Windows' Startup Apps list - re-enabled it"
                            : "Enabled in Windows' Startup Apps list");
                        return isDisabled
                            ? "It was also switched off in Windows' Startup Apps list (Task Manager) - re-enabled that too."
                            : "";
                    }
                    return "";
                }
            }
            catch (Exception ex)
            {
                Log("Couldn't check/update Windows Startup Apps enabled state: " + ex.Message);
                return "";
            }
        }

        private string EnableAutoStartup()
        {
            try
            {
                string? currentExePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(currentExePath)) return "Couldn't determine this app's own exe path.";

                // v7.5.16: skip auto-startup registration entirely (don't
                // read OR write the registry key) when running from a raw
                // build-output folder rather than an actual install. Every
                // MSBuild output path contains "\bin\Debug\" or "\bin\Release\"
                // - the NSIS installer never produces that pattern, it always
                // deploys straight into "...\FileserverDriveManager\
                // FileserverDriveManager.exe". Without this check, running a
                // dev build (e.g. via dev-build-run.bat on the test VM) would
                // hijack the machine's permanent Windows boot-time auto-start
                // slot away from any real installed copy, pointing it at an
                // ephemeral debug path that can vanish or change on the very
                // next build. A dev build launched for testing has no
                // business claiming that slot at all.
                if (currentExePath.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase)
                    || currentExePath.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase))
                {
                    Log($"Skipping auto-startup registration - running from a build-output folder, not an install: {currentExePath}");
                    return "Skipped - this is a dev/test build, not an installed copy. Auto-start isn't registered for build-output folders.";
                }

                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key == null) return "Couldn't open the Windows startup registry key.";

                    string registryValue = $"\"{currentExePath}\"";
                    string baseResult;

                    // v7.5.13: was "only write if the key doesn't exist yet",
                    // which meant a stale entry from an older install
                    // location (e.g. the install directory changed between
                    // installer versions - confirmed on a real machine
                    // still pointing at an old x86 "Drive Manager" path
                    // while the current installer.nsi targets a different
                    // 64-bit "FileserverDriveManager" path) would NEVER get
                    // corrected. If that old exe is later removed by an
                    // uninstall/reinstall, auto-startup silently stops
                    // working with no error and no log, since this code
                    // path never runs again for that machine. Now it
                    // compares against the currently running exe's actual
                    // path and re-writes whenever they differ, so an
                    // upgrade or a moved install self-heals on next launch.
                    object? existingValue = key.GetValue("FileserverDriveManager");
                    string? existingString = existingValue as string;
                    if (existingString == null)
                    {
                        key.SetValue("FileserverDriveManager", registryValue);
                        Log($"Auto-startup enabled with path: {registryValue}");
                        baseResult = $"Added to Windows startup:\n{currentExePath}";
                    }
                    else if (!string.Equals(existingString, registryValue, StringComparison.OrdinalIgnoreCase))
                    {
                        key.SetValue("FileserverDriveManager", registryValue);
                        Log($"Auto-startup path was stale ({existingString}) - corrected to: {registryValue}");
                        baseResult = $"Startup entry was pointing at an old location - corrected to:\n{currentExePath}";
                    }
                    else
                    {
                        baseResult = $"Already set to start with Windows:\n{currentExePath}";
                    }

                    // v7.5.27: the Run key existing and pointing at the right
                    // path isn't the whole story - Windows 10/11's Task
                    // Manager "Startup Apps" tab has its OWN separate enabled/
                    // disabled toggle per entry, stored in a completely
                    // different registry location. If a user (or Windows
                    // itself, which sometimes auto-disables startup items it
                    // flags as slow or unsigned) toggles that off, the Run
                    // key is untouched and looks perfectly correct, but
                    // Windows won't actually launch the app at logon.
                    // Confirmed real-world: "Add to Startup" reported success
                    // and the Run key was fine, but Task Manager showed
                    // "Disabled". Re-enabling it here means every launch (and
                    // every manual "Add to Startup" click) self-heals this
                    // too, not just the Run key path.
                    string startupApprovedNote = EnsureStartupApproved();
                    return string.IsNullOrEmpty(startupApprovedNote) ? baseResult : $"{baseResult}\n\n{startupApprovedNote}";
                }
            }
            catch (Exception ex)
            {
                Log("Error enabling auto-startup: " + ex.Message);
                return "Couldn't update the Windows startup entry: " + ex.Message;
            }
        }

        private async Task CheckAndAutoConnect()
        {
            // v7.5.2: was Thread.Sleep(10000) - a synchronous block directly on
            // the UI thread. That starves the Windows message pump for the
            // full 10 seconds on every launch, which is exactly what makes an
            // app show "Not Responding" and ignore right-clicks on its tray
            // icon - not a true hang, but indistinguishable from one to the
            // user. Task.Delay yields the thread instead of blocking it.
            await Task.Delay(10000);

            // v7.5.5: hiding to tray now happens immediately on Load (see
            // constructor) instead of here. Removed the duplicate
            // WindowState/ShowInTaskbar/notifyIcon lines that used to be
            // here - besides being redundant, they had a real bug: if the
            // user manually clicked "Show" from the tray during this 10s
            // delay, this code would have forced the window back into
            // hiding again the moment the delay finished, undoing their
            // action.

            // v7.5.4: UpdateNetworkStatus() (which populates the Tailscale
            // IP/NetBird IP/Network labels) was only ever wired to the 5s
            // statusTimer, which didn't Start() until this ENTIRE method
            // finished - meaning the labels stayed blank through the full
            // race+authenticate+mount sequence (which can take a while if
            // any step is slow), even though the underlying connection
            // itself succeeded quickly. Starting the timer here instead
            // decouples the cosmetic status display from how long the rest
            // of startup takes.
            UpdateNetworkStatus();
            statusTimer.Start();
            
            if (!autoMountOnStartup)
            {
                Log("Auto-mount disabled - staying minimized");
                return;
            }

            // v7.5.15: real bug in the v7.5.14 retry logic - statusTimer was
            // already running (started above) while this initial call could
            // itself take 60s+ (VPN poll loop + race + auth + mount), but
            // isRetryingStartupConnect was only ever set inside the PERIODIC
            // retry block below, not around this initial call. Every 5s tick
            // during that whole window saw hasEverAutoConnected still false
            // and isRetryingStartupConnect still false, and launched ANOTHER
            // concurrent TryAutoConnectOnce() on top of this one - multiple
            // overlapping race/auth/mount attempts stepping on each other
            // (one's CleanupStaleSession killing a session another was
            // mid-authenticating with, two MountAllDrives() loops racing
            // against the same drive list). This is what made auto-mount
            // intermittent - whether it broke depended on how many ticks
            // landed inside the startup window before this first call
            // finished. Now guarded exactly like the retry block is.
            isRetryingStartupConnect = true;
            lastStartupRetryAttempt = DateTime.Now;
            try
            {
                hasEverAutoConnected = await TryAutoConnectOnce();
            }
            finally
            {
                isRetryingStartupConnect = false;
            }
        }

        // v7.5.14: extracted from CheckAndAutoConnect so the exact same
        // VPN-check + race + auth + mount sequence can be reused both for
        // the initial startup attempt AND for periodic retries (fired from
        // the status timer) if that initial attempt never managed to
        // connect. Returns true only on a fully successful auto-mount.
        private async Task<bool> TryAutoConnectOnce()
        {
            // v7.5.17: was a hard gate - polled GetVPNIP() for up to 60s and
            // returned false (never even attempting the race) if neither
            // Tailscale nor NetBird reported connected. That completely broke
            // auto-mount for anyone reaching the fileserver purely over LAN
            // (e.g. users physically at the office) with no VPN client
            // installed or running at all - GetVPNIP() would never find
            // anything, every single attempt would give up before ever
            // testing the LAN IP, forever, even though RaceFileserverIPs()
            // below already tests LAN/Tailscale/NetBird independently and
            // gracefully skips whichever aren't configured or reachable.
            // The VPN check added no value the race doesn't already provide
            // (an unreachable VPN IP just shows up as "unreachable" in the
            // race, exactly like an absent LAN connection does) - it only
            // ever hurt LAN-only users by blocking on something they don't
            // use. Now goes straight to the race; VPN readiness naturally
            // sorts itself out because a VPN-reliant candidate simply isn't
            // reachable yet until the tunnel is up, and the caller's retry
            // loop with backoff (see statusTimer tick) keeps trying either
            // way until something responds.
            if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
            {
                Log("Auto-mount enabled - checking fileserver reachability...");
                try
                {
                    // v7.3: race the same LAN/Tailscale/NetBird candidates the
                    // manual Authenticate button uses, rather than mounting
                    // against whatever fileserverIP happens to hold from a
                    // previous session (which could be stale if the network
                    // situation changed between launches).
                    var raceResults = await RaceFileserverIPs();
                    var reachableCandidates = raceResults.Where(r => r.Success).ToList();
                    if (reachableCandidates.Count == 0)
                    {
                        string vpnIP = GetVPNIP();
                        string vpnNote = (string.IsNullOrEmpty(vpnIP) || vpnIP.Contains("Not Connected"))
                            ? " (no VPN detected either - if this machine relies on Tailscale/NetBird rather than LAN, check it's connected)"
                            : "";
                        Log($"Auto-connect: fileserver not reachable on LAN, Tailscale, or NetBird{vpnNote}");
                        statusLabel.Text = "Fileserver not reachable - will keep retrying";
                        return false;
                    }

                    // v7.5.12: try every TCP-reachable candidate, not just the
                    // fastest one, before giving up. A port-445 TCP connect
                    // succeeding (the race test) doesn't guarantee the SMB
                    // auth layer is actually ready on that path yet - this was
                    // observed repeatedly right after a VPN reconnect, where
                    // the "fastest" candidate's TCP port was open but SMB auth
                    // against it failed, while a manual retry a minute later
                    // (often against a DIFFERENT candidate) succeeded fine.
                    // Falling through the ranked list gives auto-mount the
                    // same resilience the user was getting manually anyway.
                    //
                    // v7.5.23: stale-session cleanup moved to ONCE here,
                    // before trying any candidate, covering all three
                    // configured paths (not just whichever one is about to be
                    // tried) - a session left open via a DIFFERENT path to
                    // the same physical server can block/hang a fresh
                    // connection via this candidate too, so clearing only the
                    // current candidate's IP per-attempt wasn't enough.
                    await CleanupAllStaleSessions();
                    bool authSucceeded = false;
                    foreach (var candidate in reachableCandidates)
                    {
                        fileserverIP = candidate.IP;
                        Log($"Auto-connect using {candidate.Label} ({candidate.IP}, {candidate.ElapsedMs}ms)");

                        if (await TestFileserverConnection(username, password))
                        {
                            authSucceeded = true;
                            break;
                        }
                        Log($"Auto-authentication via {candidate.Label} failed - trying next candidate");
                    }

                    if (authSucceeded)
                    {
                        statusLabel.Text = "Auto-authenticated on startup";
                        Log("Auto-authentication successful");
                        
                        Log("Auto-mounting drives on startup...");
                        await MountAllDrives();
                        
                        await Task.Delay(3000);
                        
                        Log("All drives mounted - staying minimized in tray");
                        return true;
                    }
                    else
                    {
                        Log("Auto-authentication failed on all reachable candidates");
                        statusLabel.Text = "Authentication failed - check credentials or VPN";
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    Log("Auto-authentication error: " + ex.Message);
                    return false;
                }
            }
            else
            {
                Log("No saved credentials for auto-mount");
                return false;
            }
        }

        private void LoadDrives()
        {
            LoadSavedSettings();
        }
        private void InitializeComponents()
        {
            // === MODERN DESIGN CONSTANTS ===
            Font modernFont = new Font("Segoe UI", 9.5F, FontStyle.Regular);
            Font modernFontBold = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            Font headerFont = new Font("Segoe UI", 11F, FontStyle.Bold);
            Font statusFont = new Font("Segoe UI", 8.5F, FontStyle.Regular);
            
            // v7.1: pulled from AppTheme (not separate literals) so dark mode,
            // applied earlier in the constructor, actually takes effect here.
            Color primaryBlue = AppTheme.Accent;
            Color successGreen = AppTheme.SuccessText;
            Color dangerRed = AppTheme.Danger;
            Color neutralGray = AppTheme.TextSecondary;
            Color bgLight = AppTheme.BgLight;
            Color bgWhite = AppTheme.BgWhite;
            Color borderGray = AppTheme.BorderGray;
            Color textPrimary = AppTheme.TextPrimary;
            Color textSecondary = AppTheme.TextSecondary;
            
            this.Text = $"Dyna Training - Fileserver Drive Manager {APP_VERSION}";
            this.Size = new Size(950, 580);
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.MaximizeBox = true;
            this.MinimizeBox = true;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.WindowState = FormWindowState.Normal;
            this.BackColor = bgLight;
            this.ForeColor = textPrimary;
            this.Font = modernFont;
            
            // Load favicon
            try
            {
                string? faviconPath = null;
                string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string userIconPath = Path.Combine(appDataPath, "FileserverDriveManager", "icon.png");
                if (File.Exists(userIconPath))
                {
                    faviconPath = userIconPath;
                }
                else
                {
                    string appDir = AppContext.BaseDirectory;
                    string defaultIconPath = Path.Combine(appDir, "icon.png");
                    if (File.Exists(defaultIconPath))
                    {
                        faviconPath = defaultIconPath;
                    }
                }
                
                if (faviconPath != null)
                {
                    using (Bitmap bmp = new Bitmap(faviconPath))
                    {
                        this.Icon = Icon.FromHandle(bmp.GetHicon());
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Error loading favicon: {ex.Message}");
            }
            
            // Setup system tray icon
            notifyIcon = new NotifyIcon();
            notifyIcon.Icon = this.Icon ?? SystemIcons.Application;
            notifyIcon.Visible = false;
            notifyIcon.Text = "Fileserver Drive Manager";
            notifyIcon.ContextMenuStrip = new ContextMenuStrip() { BackColor = AppTheme.BgWhite, ForeColor = AppTheme.TextPrimary };
            notifyIcon.ContextMenuStrip.Items.Add("Show", null, (s, e) => ShowFromTray());
            notifyIcon.ContextMenuStrip.Items.Add("Exit", null, (s, e) =>
            {
                // v7.5.26: same confirmation as the main window's Exit
                // button, for consistency - this menu item also fully closes
                // the app (stopping auto-mount/monitoring), not just hides it.
                var result = MessageBox.Show(
                    "This will fully close Fileserver Drive Manager, stopping auto-mount and drive monitoring until you start it again.\n\nExit anyway?",
                    "Confirm Exit", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (result == DialogResult.Yes) Application.Exit();
            });
            notifyIcon.DoubleClick += (s, e) => ShowFromTray();
            
            this.FormClosing += (s, e) =>
            {
                if (e.CloseReason == CloseReason.UserClosing && !isExiting)
                {
                    e.Cancel = true;
                    this.WindowState = FormWindowState.Minimized;
                    this.ShowInTaskbar = false;
                    notifyIcon.Visible = true;
                }
            };

            this.Resize += (s, e) =>
            {
                if (this.WindowState == FormWindowState.Minimized)
                {
                    this.ShowInTaskbar = false;
                    notifyIcon.Visible = true;
                }
            };

            TableLayoutPanel mainLayout = new TableLayoutPanel() { Dock = DockStyle.Fill, Padding = new Padding(12) };
            mainLayout.ColumnCount = 1;
            mainLayout.RowCount = 5;
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 75));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));

            // ===== CREDENTIALS & LOGO SECTION =====
            Panel credLogoPanel = new Panel() { Dock = DockStyle.Fill, BackColor = bgWhite, Margin = new Padding(0, 0, 0, 8) };
            credLogoPanel.ApplyCardStyle(borderGray);
            
            TableLayoutPanel credLogoTable = new TableLayoutPanel() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(16), BackColor = bgWhite };
            credLogoTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            credLogoTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280));
            credLogoTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            
            // Left: Credentials
            TableLayoutPanel credStackTable = new TableLayoutPanel() { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, BackColor = bgWhite };
            credStackTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            credStackTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            credStackTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            credStackTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            credStackTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            
            Label usernameLabel = new Label() { Text = "Username:", TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Fill, Padding = new Padding(0, 0, 10, 0), Font = modernFont, ForeColor = textPrimary };
            usernameBox = new TextBox() { Dock = DockStyle.Fill, Font = modernFont, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 4, 8, 4), BackColor = AppTheme.BgWhite, ForeColor = AppTheme.TextPrimary };
            
            Label passwordLabel = new Label() { Text = "Password:", TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Fill, Padding = new Padding(0, 0, 10, 0), Font = modernFont, ForeColor = textPrimary };
            passwordBox = new TextBox() { PasswordChar = '●', Dock = DockStyle.Fill, Font = modernFont, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 4, 8, 4), BackColor = AppTheme.BgWhite, ForeColor = AppTheme.TextPrimary };
            
            authenticateButton = new Button() { 
                Text = "Authenticate", 
                BackColor = primaryBlue, 
                ForeColor = Color.White, 
                Dock = DockStyle.Fill, 
                Font = modernFontBold, 
                Margin = new Padding(0, 4, 0, 4), 
                Cursor = Cursors.Hand, 
                FlatStyle = FlatStyle.Flat,
                TabStop = true
            };
            authenticateButton.FlatAppearance.BorderSize = 0;
            authenticateButton.ApplyRoundedFilledStyle(primaryBlue, Color.White);
            authenticateButton.Click += AuthenticateButton_Click;
            
            credStackTable.Controls.Add(usernameLabel, 0, 0);
            credStackTable.Controls.Add(usernameBox, 1, 0);
            credStackTable.Controls.Add(authenticateButton, 2, 0);
            credStackTable.Controls.Add(passwordLabel, 0, 1);
            credStackTable.Controls.Add(passwordBox, 1, 1);
            credStackTable.SetRowSpan(authenticateButton, 2);
            
            // Right: Logo
            Panel logoPanel = new Panel() { Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = new Padding(12, 0, 0, 0) };
            logoPicture = new PictureBox() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent };
            
            try
            {
                string? logoPath = null;
                string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string userLogoPath = Path.Combine(appDataPath, "FileserverDriveManager", "logo.png");
                if (File.Exists(userLogoPath))
                {
                    logoPath = userLogoPath;
                }
                else
                {
                    string appDir = AppContext.BaseDirectory;
                    string defaultLogoPath = Path.Combine(appDir, "logo.png");
                    if (File.Exists(defaultLogoPath))
                    {
                        logoPath = defaultLogoPath;
                    }
                }
                
                if (logoPath != null)
                {
                    logoPicture.Image = new Bitmap(logoPath);
                }
            }
            catch (Exception ex)
            {
                Log("Error loading logo: " + ex.Message);
            }
            
            logoPanel.Controls.Add(logoPicture);
            
            credLogoTable.Controls.Add(credStackTable, 0, 0);
            credLogoTable.Controls.Add(logoPanel, 1, 0);
            credLogoPanel.Controls.Add(credLogoTable);
            mainLayout.Controls.Add(credLogoPanel, 0, 0);

            // ===== ADD DRIVE SECTION =====
            Panel addDrivePanel = new Panel() { Dock = DockStyle.Fill, BackColor = bgWhite, Margin = new Padding(0, 0, 0, 8) };
            addDrivePanel.ApplyCardStyle(borderGray);
            
            FlowLayoutPanel addFlow = new FlowLayoutPanel() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(16, 12, 16, 12), BackColor = bgWhite };

            Label driveLetterLabel = new Label() { Text = "Drive Letter:", AutoSize = true, Font = modernFont, Margin = new Padding(0, 8, 8, 0), ForeColor = textPrimary };
            driveLetterBox = new ComboBox() { Width = 70, Font = modernFont, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 4, 20, 0), Enabled = false, FlatStyle = FlatStyle.Standard, BackColor = AppTheme.BgWhite, ForeColor = AppTheme.TextPrimary };
            
            Label shareNameLabel = new Label() { Text = "Share Name:", AutoSize = true, Font = modernFont, Margin = new Padding(0, 8, 8, 0), ForeColor = textPrimary };
            shareNameBox = new ComboBox() { Width = 280, Font = modernFont, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 4, 20, 0), Enabled = false, FlatStyle = FlatStyle.Standard, BackColor = AppTheme.BgWhite, ForeColor = AppTheme.TextPrimary };
            
            addDriveButton = new Button() { 
                Text = "Add Drive", 
                BackColor = neutralGray, 
                ForeColor = Color.White, 
                Width = 90, 
                Height = 32, 
                Font = modernFontBold, 
                Margin = new Padding(0, 2, 10, 0), 
                Cursor = Cursors.Hand, 
                FlatStyle = FlatStyle.Flat, 
                Enabled = false 
            };
            addDriveButton.FlatAppearance.BorderSize = 0;
            addDriveButton.ApplyRoundedOutlineStyle(AppTheme.Accent);
            addDriveButton.Click += AddDriveButton_Click;
            
            addFlow.Controls.Add(driveLetterLabel);
            addFlow.Controls.Add(driveLetterBox);
            addFlow.Controls.Add(shareNameLabel);
            addFlow.Controls.Add(shareNameBox);
            addFlow.Controls.Add(addDriveButton);
            addDrivePanel.Controls.Add(addFlow);
            mainLayout.Controls.Add(addDrivePanel, 0, 1);

            // ===== DRIVES GRID =====
            Panel gridPanel = new Panel() { Dock = DockStyle.Fill, BackColor = bgWhite, Margin = new Padding(0, 0, 0, 8), Padding = new Padding(2) };
            gridPanel.ApplyCardStyle(borderGray);
            
            drivesGrid = new DataGridView() { 
                Dock = DockStyle.Fill, 
                AutoGenerateColumns = false, 
                AllowUserToAddRows = false, 
                AllowUserToDeleteRows = false, 
                ReadOnly = true, 
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, 
                RowHeadersVisible = false,
                BackgroundColor = bgWhite,
                BorderStyle = BorderStyle.None,
                GridColor = borderGray,
                Font = modernFont,
                ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle() { 
                    BackColor = bgLight, 
                    ForeColor = textPrimary, 
                    Font = modernFontBold,
                    Padding = new Padding(8, 4, 8, 4)
                },
                DefaultCellStyle = new DataGridViewCellStyle() { 
                    BackColor = bgWhite, 
                    ForeColor = textPrimary,
                    SelectionBackColor = AppTheme.SelectionBg,
                    SelectionForeColor = textPrimary,
                    Padding = new Padding(8, 4, 8, 4)
                },
                RowTemplate = { Height = 36 },
                Margin = new Padding(1)
            };
            drivesGrid.Columns.Add(new DataGridViewTextBoxColumn() { HeaderText = "Drive", DataPropertyName = "DriveLetter", Width = 100 });
            drivesGrid.Columns.Add(new DataGridViewTextBoxColumn() { HeaderText = "Share Name", DataPropertyName = "ShareName", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            drivesGrid.Columns.Add(new DataGridViewTextBoxColumn() { HeaderText = "Status", DataPropertyName = "Status", Width = 140 });
            // v7.2: per-row unmount+remove button, replacing the single shared
            // "Remove" button that required selecting a row first. Each row
            // now handles its own removal independently.
            DataGridViewButtonColumn removeColumn = new DataGridViewButtonColumn()
            {
                HeaderText = "",
                Text = "Unmount",
                UseColumnTextForButtonValue = true,
                Width = 100,
                FlatStyle = FlatStyle.Flat,
                DefaultCellStyle = new DataGridViewCellStyle()
                {
                    BackColor = AppTheme.BgWhite,
                    ForeColor = AppTheme.Danger,
                    SelectionBackColor = AppTheme.SelectionBg,
                    SelectionForeColor = AppTheme.Danger
                }
            };
            drivesGrid.Columns.Add(removeColumn);
            drivesGrid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle() {
                BackColor = AppTheme.AlternateRowBg,
                ForeColor = textPrimary,
                SelectionBackColor = AppTheme.SelectionBg,
                SelectionForeColor = textPrimary,
                Padding = new Padding(8, 4, 8, 4)
            };
            drivesGrid.EnableHeadersVisualStyles = false;
            // v7 fix: DataGridView.DoubleBuffered is protected/inaccessible
            // directly - without it, rapid repaints during a form resize leave
            // stale frames un-cleared, causing the pill badges (owner-drawn via
            // CellPainting) to visibly "ghost"/stack on top of each other while
            // resizing. Reflection is the standard workaround without subclassing.
            typeof(DataGridView).InvokeMember(
                "DoubleBuffered",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.SetProperty,
                null, drivesGrid, new object[] { true });
            // v7 "full tier": pill-shaped status badges instead of plain
            // colored text. CellPainting lets us fully custom-draw the cell -
            // paint the background/selection as normal, suppress the default
            // text draw, then draw our own rounded pill centered in the cell.
            drivesGrid.CellPainting += (s, e) =>
            {
                if (e.RowIndex < 0) return;

                if (drivesGrid.Columns[e.ColumnIndex] == removeColumn)
                {
                    // v7.5.6: fully custom-drawn to match the rounded outline
                    // style used everywhere else - see PaintOutlineButtonCell.
                    e.PaintBackground(e.CellBounds, true);
                    RoundedRenderer.PaintOutlineButtonCell(e.Graphics!, e.CellBounds, "Unmount", AppTheme.Danger, AppTheme.BgWhite, modernFontBold);
                    e.Handled = true;
                    return;
                }

                if (drivesGrid.Columns[e.ColumnIndex].DataPropertyName != "Status" || e.Value == null)
                    return;
                e.PaintBackground(e.CellBounds, true);
                string status = e.Value.ToString() ?? "";
                (Color bg, Color fg) = status switch
                {
                    "Mounted" => (AppTheme.SuccessBg, AppTheme.SuccessText),
                    "Failed" or "Error" or "Timeout" => (AppTheme.DangerBg, AppTheme.DangerText),
                    _ => (AppTheme.MutedBg, AppTheme.MutedText)
                };
                RoundedRenderer.PaintStatusPill(e.Graphics!, e.CellBounds, status, bg, fg, modernFontBold);
                e.Handled = true;
            };
            drivesGrid.CellContentClick += (s, e) =>
            {
                if (e.RowIndex < 0 || drivesGrid.Columns[e.ColumnIndex] != removeColumn) return;
                var drive = (DriveMapping)drivesGrid.Rows[e.RowIndex].DataBoundItem;
                DismountAndRemoveDrive(drive);
            };
            gridPanel.Controls.Add(drivesGrid);
            mainLayout.Controls.Add(gridPanel, 0, 2);

            // ===== ACTION BUTTONS =====
            // v7.5.28: was 6 columns including "View Logs" - moved to
            // Settings instead (see brandingPanel below), since it's purely
            // a troubleshooting tool, not something used in day-to-day
            // operation like the remaining 5 buttons here.
            TableLayoutPanel buttonPanel = new TableLayoutPanel() { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, Margin = new Padding(0, 0, 0, 8) };
            for (int i = 0; i < 5; i++)
                buttonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));

            mountDrivesButton = new Button() { 
                Text = "Mount All", 
                Dock = DockStyle.Fill, 
                BackColor = neutralGray, 
                ForeColor = Color.White, 
                Font = modernFontBold,
                Margin = new Padding(0, 0, 4, 0), 
                Enabled = false, 
                Cursor = Cursors.Hand, 
                FlatStyle = FlatStyle.Flat 
            };
            mountDrivesButton.FlatAppearance.BorderSize = 0;
            mountDrivesButton.ApplyRoundedFilledStyle(AppTheme.Accent, Color.White);
            mountDrivesButton.Click += MountDrivesButton_Click;
            
            settingsButton = new Button() { 
                Text = "Settings", 
                Dock = DockStyle.Fill, 
                BackColor = primaryBlue, 
                ForeColor = Color.White, 
                Font = modernFontBold,
                Margin = new Padding(2, 0, 2, 0), 
                Cursor = Cursors.Hand, 
                FlatStyle = FlatStyle.Flat 
            };
            settingsButton.FlatAppearance.BorderSize = 1;
            settingsButton.ApplyRoundedOutlineStyle(AppTheme.Accent);
            settingsButton.Click += SettingsButton_Click;
            
            tailscaleButton = new Button() { 
                Text = "Tailscale", 
                Dock = DockStyle.Fill, 
                BackColor = bgLight, 
                ForeColor = textPrimary, 
                Font = modernFontBold,
                Margin = new Padding(2, 0, 2, 0), 
                Cursor = Cursors.Hand, 
                FlatStyle = FlatStyle.Flat 
            };
            tailscaleButton.FlatAppearance.BorderColor = borderGray;
            tailscaleButton.ApplyRoundedOutlineStyle(AppTheme.Accent);
            tailscaleButton.Click += TailscaleButton_Click;
            
            netbirdButton = new Button() { 
                Text = "NetBird", 
                Dock = DockStyle.Fill, 
                BackColor = bgLight, 
                ForeColor = textPrimary, 
                Font = modernFontBold,
                Margin = new Padding(2, 0, 2, 0), 
                Cursor = Cursors.Hand, 
                FlatStyle = FlatStyle.Flat 
            };
            netbirdButton.FlatAppearance.BorderColor = borderGray;
            netbirdButton.ApplyRoundedOutlineStyle(AppTheme.Accent);
            netbirdButton.Click += NetBirdButton_Click;
            
            exitButton = new Button() { 
                Text = "Exit", 
                Dock = DockStyle.Fill, 
                BackColor = dangerRed, 
                ForeColor = Color.White, 
                Font = modernFontBold,
                Margin = new Padding(4, 0, 0, 0), 
                Cursor = Cursors.Hand, 
                FlatStyle = FlatStyle.Flat 
            };
            exitButton.FlatAppearance.BorderSize = 0;
            exitButton.ApplyRoundedFilledStyle(AppTheme.Danger, Color.White);
            exitButton.Click += (s, e) =>
            {
                // v7.5.26: was an immediate, unconfirmed hard exit - the
                // native window X button correctly just minimizes to tray
                // (see FormClosing above), but this button bypassed that
                // entirely via isExiting, with no visual distinction to warn
                // the user it does something different. Confirmed real-world:
                // users clicking Exit expected the same minimize-to-tray
                // behavior as the X button, and were surprised the app (and
                // therefore auto-mount/monitoring) actually stopped running.
                var result = MessageBox.Show(
                    "This will fully close Fileserver Drive Manager, stopping auto-mount and drive monitoring until you start it again.\n\n" +
                    "To keep it running in the background, close this window with the X button instead.\n\n" +
                    "Exit anyway?",
                    "Confirm Exit", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (result == DialogResult.Yes)
                {
                    isExiting = true;
                    this.Close();
                }
            };

            buttonPanel.Controls.Add(mountDrivesButton, 0, 0);
            buttonPanel.Controls.Add(settingsButton, 1, 0);
            buttonPanel.Controls.Add(tailscaleButton, 2, 0);
            buttonPanel.Controls.Add(netbirdButton, 3, 0);
            buttonPanel.Controls.Add(exitButton, 4, 0);
            mainLayout.Controls.Add(buttonPanel, 0, 3);

            // ===== STATUS BAR =====
            Panel statusBarPanel = new Panel() { Dock = DockStyle.Fill, BackColor = bgLight };
            statusBarPanel.Paint += (s, e) => {
                e.Graphics.DrawLine(new Pen(borderGray), 0, 0, statusBarPanel.Width, 0);
            };
            
            TableLayoutPanel statusPanel = new TableLayoutPanel() { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, BackColor = bgLight, Padding = new Padding(0, 8, 0, 0) };
            statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
            statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
            statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
            statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
            
            statusLabel = new Label() { 
                Dock = DockStyle.Fill, 
                Text = "Ready", 
                TextAlign = ContentAlignment.MiddleLeft, 
                Padding = new Padding(4, 0, 0, 0), 
                BackColor = bgLight, 
                ForeColor = primaryBlue, 
                Font = statusFont,
                AutoEllipsis = true
            };
            
            lanIPLabel = new Label() { 
                Dock = DockStyle.Fill, 
                Text = "Network: Detecting...", 
                TextAlign = ContentAlignment.MiddleCenter, 
                BackColor = bgLight, 
                ForeColor = textSecondary, 
                Font = statusFont,
                AutoEllipsis = true
            };
            
            tailscaleIPLabel = new Label() { 
                Dock = DockStyle.Fill, 
                Text = "Tailscale: Not Connected", 
                TextAlign = ContentAlignment.MiddleCenter, 
                BackColor = bgLight, 
                ForeColor = textSecondary, 
                Font = statusFont,
                AutoEllipsis = true
            };
            
            netbirdIPLabel = new Label() { 
                Dock = DockStyle.Fill, 
                Text = "NetBird: Not Connected", 
                TextAlign = ContentAlignment.MiddleCenter, 
                BackColor = bgLight, 
                ForeColor = textSecondary, 
                Font = statusFont,
                AutoEllipsis = true
            };
            
            Label versionLabel = new Label() { 
                Dock = DockStyle.Fill, 
                Text = APP_VERSION, 
                TextAlign = ContentAlignment.MiddleRight, 
                Padding = new Padding(0, 0, 4, 0), 
                BackColor = bgLight, 
                ForeColor = textSecondary, 
                Font = statusFont 
            };
            
            statusPanel.Controls.Add(statusLabel, 0, 0);
            statusPanel.Controls.Add(lanIPLabel, 1, 0);
            statusPanel.Controls.Add(tailscaleIPLabel, 2, 0);
            statusPanel.Controls.Add(netbirdIPLabel, 3, 0);
            statusPanel.Controls.Add(versionLabel, 4, 0);
            statusBarPanel.Controls.Add(statusPanel);
            mainLayout.Controls.Add(statusBarPanel, 0, 4);

            this.Controls.Add(mainLayout);
            
            // Update status periodically - but NOT during startup!
            statusTimer = new System.Windows.Forms.Timer();
            statusTimer.Interval = 5000;
            statusTimer.Tick += async (s, e) => {
                UpdateNetworkStatus();
                RefreshStatus();  // Also check drive mount status and update Mount All button
                // v7.5.6: track how long each drive has been continuously
                // unavailable and notify once it crosses the configured
                // threshold. Runs right after RefreshStatus so it sees
                // current mount state.
                CheckDisconnectNotifications();
                // v7.5.0: live failover check - only does real work when
                // authenticated with drives active, and only switches when
                // the current provider genuinely stops responding.
                await CheckFileserverFailover();
                // v7.5.14: if the initial startup attempt never managed to
                // connect (VPN wasn't up yet, fileserver unreachable, etc.),
                // keep retrying here in the background with capped backoff
                // (5s/10s/20s/40s/60s, holds at 60s) instead of requiring a
                // manual Authenticate click. This is what removes the need
                // for any boot-ordering trick against Tailscale/NetBird -
                // the app just keeps waiting, however long it takes, without
                // hammering the network every 5s tick.
                if (!hasEverAutoConnected && !isRetryingStartupConnect && autoMountOnStartup
                    && !string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
                {
                    double backoffSeconds = consecutiveStartupRetryMisses == 0
                        ? 0
                        : Math.Min(60, 5 * Math.Pow(2, consecutiveStartupRetryMisses - 1));
                    if ((DateTime.Now - lastStartupRetryAttempt).TotalSeconds >= backoffSeconds)
                    {
                        // v7.5.14: TryAutoConnectOnce can run for 60s+ (its
                        // own internal VPN poll loop, plus race/auth/mount) -
                        // this Timer.Tick handler isn't awaited by the Timer
                        // itself, so without this guard a new tick firing
                        // every 5s during that window would kick off
                        // overlapping retry attempts stacking on top of each
                        // other, racing/mounting concurrently against the
                        // same drives.
                        isRetryingStartupConnect = true;
                        lastStartupRetryAttempt = DateTime.Now;
                        try
                        {
                            bool succeeded = await TryAutoConnectOnce();
                            if (succeeded)
                            {
                                hasEverAutoConnected = true;
                                consecutiveStartupRetryMisses = 0;
                            }
                            else
                            {
                                consecutiveStartupRetryMisses++;
                            }
                        }
                        finally
                        {
                            isRetryingStartupConnect = false;
                        }
                    }
                }
            };
            // Timer will be started AFTER CheckAndAutoConnect completes
        }

        // v7.5.6: notifies (via tray balloon) the first time a drive has been
        // continuously unavailable for at least disconnectNotifyMinutes.
        // Only fires once per outage (DisconnectNotified guards repeat
        // notifications every 5s tick); resets automatically once the drive
        // is mounted again, so a future outage notifies again.
        private void CheckDisconnectNotifications()
        {
            foreach (var drive in drives)
            {
                if (drive.Status == "Mounted")
                {
                    drive.UnavailableSince = null;
                    drive.DisconnectNotified = false;
                    continue;
                }

                if (drive.UnavailableSince == null)
                {
                    drive.UnavailableSince = DateTime.Now;
                    continue;
                }

                if (drive.DisconnectNotified) continue;

                var elapsed = DateTime.Now - drive.UnavailableSince.Value;
                if (elapsed.TotalMinutes >= disconnectNotifyMinutes)
                {
                    drive.DisconnectNotified = true;
                    Log($"Disconnect notification: {drive.DriveLetter} ({drive.ShareName}) unavailable for {(int)elapsed.TotalMinutes} min");
                    notifyIcon.ShowBalloonTip(10000, "Drive disconnected",
                        $"{drive.DriveLetter} ({drive.ShareName}) has been unavailable for {disconnectNotifyMinutes}+ minutes.",
                        ToolTipIcon.Warning);
                }
            }
        }

        private async void AuthenticateButton_Click(object? sender, EventArgs e)
        {
            username = usernameBox.Text.Trim();
            password = passwordBox.Text;

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                statusLabel.Text = "Please enter username and password";
                return;
            }

            isAuthenticating = true;
            authenticateButton.Enabled = false;

            // v7.3: race LAN/Tailscale/NetBird paths to the fileserver and use
            // whichever responds fastest, instead of a single fixed IP. This
            // is what makes Authenticate work correctly regardless of which
            // network(s) happen to be up right now.
            statusLabel.Text = "Checking LAN, Tailscale, and NetBird...";
            var raceResults = await RaceFileserverIPs();
            var reachableCandidates = raceResults.Where(r => r.Success).ToList();
            if (reachableCandidates.Count == 0)
            {
                statusLabel.Text = "Fileserver not reachable on LAN, Tailscale, or NetBird";
                authenticateButton.Enabled = true;
                isAuthenticating = false;
                return;
            }

            try
            {
                // v7.5.22: was "try only the fastest candidate, show
                // 'Authentication failed' and stop if it doesn't work" - the
                // exact same TCP-open-but-SMB-not-ready-yet race condition
                // fixed for auto-connect back in v7.5.12 was never applied
                // here, so a manual Authenticate click could fail even with
                // the correct saved password, misleadingly making it look
                // like a credentials/storage problem when clicking
                // Authenticate again (which just re-races and gets a fresh
                // attempt) was what actually fixed it. Now falls through
                // every reachable candidate the same way auto-connect does.
                //
                // v7.5.23: stale-session cleanup moved to ONCE here, before
                // trying any candidate, covering all three configured paths -
                // a session left open via a DIFFERENT path to the same
                // physical server can block/hang a fresh connection via this
                // candidate too.
                await CleanupAllStaleSessions();
                bool authSucceeded = false;
                (string Label, string IP, long ElapsedMs, bool Success) successfulCandidate = default;
                foreach (var candidate in reachableCandidates)
                {
                    fileserverIP = candidate.IP;
                    Log($"Authenticate using {candidate.Label} ({candidate.IP}, {candidate.ElapsedMs}ms)");
                    statusLabel.Text = $"Using {candidate.Label} ({candidate.IP}, {candidate.ElapsedMs}ms) - Authenticating...";

                    if (await TestFileserverConnection(username, password))
                    {
                        authSucceeded = true;
                        successfulCandidate = candidate;
                        break;
                    }
                    Log($"Authenticate via {candidate.Label} failed - trying next candidate");
                }

                if (!authSucceeded)
                {
                    statusLabel.Text = "Authentication failed on all reachable paths - check credentials";
                    authenticateButton.Enabled = true;
                    isAuthenticating = false;
                    return;
                }

                statusLabel.Text = $"Connected via {successfulCandidate.Label} - Loading shares...";

                PopulateAvailableDriveLetters();
                
                List<string> shares = GetAvailableShares();
                
                shareNameBox.Items.Clear();
                foreach (string share in shares)
                {
                    shareNameBox.Items.Add(share);
                }

                if (shareNameBox.Items.Count > 0)
                {
                    shareNameBox.SelectedIndex = 0;
                }

                driveLetterBox.Enabled = true;
                shareNameBox.Enabled = true;
                // Just toggle Enabled - ApplySecondaryStyle/ApplyModernStyle's own
                // EnabledChanged handlers restore the correct color automatically.
                // (Previously this manually overwrote BackColor here, which desynced
                // from the hover/press color closures captured back when the style
                // was first applied, causing wrong colors on hover after unlocking.)
                addDriveButton.Enabled = true;
                mountDrivesButton.Enabled = true;

                statusLabel.Text = $"Ready via {successfulCandidate.Label} - {shareNameBox.Items.Count} shares available";
                SaveCurrentSettings();
            }
            catch (Exception ex)
            {
                statusLabel.Text = "Error: " + ex.Message;
                Log("Authentication error: " + ex.Message);
            }
            finally
            {
                authenticateButton.Enabled = true;
                isAuthenticating = false;
            }
        }

        private void AddDriveButton_Click(object? sender, EventArgs e)
        {
            if (driveLetterBox.SelectedItem == null || shareNameBox.SelectedItem == null)
            {
                statusLabel.Text = "Please select drive letter and share name";
                return;
            }

            string driveLetter = driveLetterBox.SelectedItem.ToString() ?? "";
            string shareName = shareNameBox.SelectedItem.ToString() ?? "";

            if (drives.Any(d => d.DriveLetter == driveLetter))
            {
                statusLabel.Text = $"Drive {driveLetter} already added";
                return;
            }

            drives.Add(new DriveMapping { DriveLetter = driveLetter, ShareName = shareName, Status = "Not Mounted" });
            drivesGrid.DataSource = null;
            drivesGrid.DataSource = drives;

            PopulateAvailableDriveLetters();
            SaveCurrentSettings();
            statusLabel.Text = $"Added {driveLetter} -> {shareName}";
        }

        // v7.2: replaces the old RemoveDriveButton_Click (which required
        // selecting a row via the grid's own selection first, and never
        // actually unmounted the drive - just removed it from the saved
        // list). Called per-row from the grid's own "Unmount" button, and
        // now genuinely unmounts (net use X: /delete) before removing.
        private void DismountAndRemoveDrive(DriveMapping drive)
        {
            if (drive == null) return;

            // Best-effort unmount - don't block removal if this fails (e.g.
            // drive was already "Not Mounted"/"Failed" and there's nothing
            // to actually release).
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "net",
                    Arguments = $"use {drive.DriveLetter} /delete",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (Process process = new Process())
                {
                    process.StartInfo = psi;
                    process.Start();
                    process.WaitForExit(5000);
                    Log(process.ExitCode == 0
                        ? $"Unmounted {drive.DriveLetter}"
                        : $"Unmount {drive.DriveLetter} returned exit code {process.ExitCode} (may not have been mounted)");
                }
            }
            catch (Exception ex)
            {
                Log($"Error unmounting {drive.DriveLetter}: {ex.Message}");
            }

            drives.Remove(drive);
            drivesGrid.DataSource = null;
            drivesGrid.DataSource = drives;

            PopulateAvailableDriveLetters();
            SaveCurrentSettings();
            statusLabel.Text = $"Removed {drive.DriveLetter}";
        }

        private async void MountDrivesButton_Click(object? sender, EventArgs e)
        {
            await MountAllDrives();
        }

        private void SettingsButton_Click(object? sender, EventArgs e)
        {
            Form settingsForm = new Form();
            settingsForm.Text = "Settings";
            settingsForm.Size = new Size(600, 665);
            settingsForm.StartPosition = FormStartPosition.CenterParent;
            settingsForm.FormBorderStyle = FormBorderStyle.FixedDialog;
            settingsForm.MaximizeBox = false;
            settingsForm.MinimizeBox = false;
            settingsForm.BackColor = AppTheme.BgLight;
            settingsForm.ForeColor = AppTheme.TextPrimary;

            TableLayoutPanel mainLayout = new TableLayoutPanel() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(10) };
            // v7 fix: was 160 - too tight once the card header Label (24px)
            // and its top padding (8px) were added on top of what GroupBox
            // used to absorb more compactly via its built-in title, which cut
            // off the auto-mount checkbox row at the bottom of the card.
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 355));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            // v7.5.18: dedicated row for the update-check panel, between the
            // branding buttons and the Information card.
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            // Network Settings Section
            // v7 "full tier": Panel with a rounded custom-drawn border + a
            // separate header Label, replacing GroupBox's boxy embedded-title
            // border. Padding leaves room for the rounded corners/border.
            Panel networkBox = new Panel() { Dock = DockStyle.Fill, Padding = new Padding(14, 12, 14, 14), BackColor = AppTheme.BgWhite };
            networkBox.ApplyCardStyle(AppTheme.BorderGray);
            Label networkHeader = new Label() { Text = "Network settings", Dock = DockStyle.Top, Height = 24, Font = new Font("Segoe UI", 10F, FontStyle.Bold), ForeColor = AppTheme.TextPrimary };
            TableLayoutPanel networkLayout = new TableLayoutPanel() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 7, Padding = new Padding(0, 8, 0, 0) };
            networkLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            networkLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            for (int i = 0; i < 7; i++)
                networkLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

            // v7.3: three candidate IPs instead of one - LAN, Tailscale, and
            // NetBird. Authenticate races all three (see RaceFileserverIPs)
            // and uses whichever responds fastest, rather than a fixed IP.
            Label lanIPFieldLabel = new Label() { Text = "LAN IP:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            TextBox lanIPBox = new TextBox() { Text = fileserverLanIP, Dock = DockStyle.Fill, BackColor = AppTheme.BgWhite, ForeColor = AppTheme.TextPrimary };
            networkLayout.Controls.Add(lanIPFieldLabel, 0, 0);
            networkLayout.Controls.Add(lanIPBox, 1, 0);

            Label tailscaleIPFieldLabel = new Label() { Text = "Tailscale IP:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            TextBox tailscaleIPFieldBox = new TextBox() { Text = fileserverTailscaleIP, Dock = DockStyle.Fill, BackColor = AppTheme.BgWhite, ForeColor = AppTheme.TextPrimary };
            networkLayout.Controls.Add(tailscaleIPFieldLabel, 0, 1);
            networkLayout.Controls.Add(tailscaleIPFieldBox, 1, 1);

            Label netbirdIPFieldLabel = new Label() { Text = "NetBird IP:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            TextBox netbirdIPFieldBox = new TextBox() { Text = fileserverNetbirdIP, Dock = DockStyle.Fill, BackColor = AppTheme.BgWhite, ForeColor = AppTheme.TextPrimary };
            networkLayout.Controls.Add(netbirdIPFieldLabel, 0, 2);
            networkLayout.Controls.Add(netbirdIPFieldBox, 1, 2);

            Button testButton = new Button() { Text = "Test Connection", Dock = DockStyle.Fill, Cursor = Cursors.Hand, FlatStyle = FlatStyle.Flat };
            testButton.FlatAppearance.BorderSize = 1;
            testButton.ApplyRoundedOutlineStyle(AppTheme.Accent);
            testButton.Click += async (s, ev) =>
            {
                // Tests reachability of whatever is currently typed in all three
                // boxes (not necessarily saved values yet), races them the same
                // way Authenticate does, and shows each result ranked by speed.
                string prevLan = fileserverLanIP, prevTs = fileserverTailscaleIP, prevNb = fileserverNetbirdIP;
                fileserverLanIP = lanIPBox.Text.Trim();
                fileserverTailscaleIP = tailscaleIPFieldBox.Text.Trim();
                fileserverNetbirdIP = netbirdIPFieldBox.Text.Trim();
                testButton.Enabled = false;
                testButton.Text = "Testing...";
                var results = await RaceFileserverIPs();
                testButton.Enabled = true;
                testButton.Text = "Test Connection";
                fileserverLanIP = prevLan; fileserverTailscaleIP = prevTs; fileserverNetbirdIP = prevNb;

                if (results.Count == 0)
                {
                    MessageBox.Show("No IPs entered to test.", "Nothing to test", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                string summary = string.Join("\n", results.Select(r =>
                    $"{r.Label} ({r.IP}): {(r.Success ? $"{r.ElapsedMs}ms" : "unreachable")}"));
                bool anySuccess = results.Any(r => r.Success);
                MessageBox.Show(summary, anySuccess ? "Results (fastest first)" : "All unreachable",
                    MessageBoxButtons.OK, anySuccess ? MessageBoxIcon.Information : MessageBoxIcon.Error);
            };

            Button saveIPButton = new Button() { Text = "Save IP's", Dock = DockStyle.Fill, Cursor = Cursors.Hand, FlatStyle = FlatStyle.Flat };
            saveIPButton.FlatAppearance.BorderSize = 0;
            saveIPButton.ApplyRoundedFilledStyle(AppTheme.Accent, Color.White);
            saveIPButton.Click += (s, ev) =>
            {
                fileserverLanIP = lanIPBox.Text.Trim();
                fileserverTailscaleIP = tailscaleIPFieldBox.Text.Trim();
                fileserverNetbirdIP = netbirdIPFieldBox.Text.Trim();
                SaveCurrentSettings();
                MessageBox.Show("IPs saved!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            networkLayout.Controls.Add(testButton, 0, 3);
            networkLayout.Controls.Add(saveIPButton, 1, 3);

            CheckBox autoMountCheckbox = new CheckBox() { Text = "Auto-mount drives when VPN IP is active", Dock = DockStyle.Fill, Checked = autoMountOnStartup };
            autoMountCheckbox.CheckedChanged += (s, ev) =>
            {
                autoMountOnStartup = autoMountCheckbox.Checked;
                SaveCurrentSettings();
            };
            networkLayout.SetColumnSpan(autoMountCheckbox, 2);
            networkLayout.Controls.Add(autoMountCheckbox, 0, 4);

            CheckBox darkModeCheckbox = new CheckBox() { Text = "Dark mode (restart to apply)", Dock = DockStyle.Fill, Checked = darkModeEnabled };
            darkModeCheckbox.CheckedChanged += (s, ev) =>
            {
                darkModeEnabled = darkModeCheckbox.Checked;
                SaveCurrentSettings();
                MessageBox.Show(
                    "Dark mode will apply the next time you launch the app.\n\nWindows Forms owner-drawn controls can't safely re-theme while the app is running, so this is saved for next launch rather than applied live.",
                    "Restart required", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            networkLayout.SetColumnSpan(darkModeCheckbox, 2);
            networkLayout.Controls.Add(darkModeCheckbox, 0, 5);

            // v7.5.6: notify (via a tray balloon) if a mounted drive has been
            // unavailable continuously for this many minutes. See
            // CheckDisconnectNotifications(), called from the same 5s status
            // timer tick that already refreshes drive mount state.
            //
            // v7.5.21: label+box were previously in separate 50%-width grid
            // cells like the IP rows above, but that left a large visual gap
            // here specifically - the label's short text sat left-aligned in
            // a wide Dock=Fill cell, while the numeric box started at the
            // column boundary far to the right. Wrapped in a FlowLayoutPanel
            // instead so the box sits directly next to the label's actual
            // text width rather than at the underlying grid's column edge.
            FlowLayoutPanel notifyRow = new FlowLayoutPanel() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = false, BackColor = AppTheme.BgWhite };
            Label notifyMinutesLabel = new Label() { Text = "Notify after (min):", AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 11, 8, 0), ForeColor = AppTheme.TextPrimary };
            NumericUpDown notifyMinutesBox = new NumericUpDown() { Minimum = 1, Maximum = 1440, Value = disconnectNotifyMinutes, Width = 60, Margin = new Padding(0, 8, 0, 0), BackColor = AppTheme.BgWhite, ForeColor = AppTheme.TextPrimary };
            notifyMinutesBox.ValueChanged += (s, ev) =>
            {
                disconnectNotifyMinutes = (int)notifyMinutesBox.Value;
                SaveCurrentSettings();
            };
            notifyRow.Controls.Add(notifyMinutesLabel);
            notifyRow.Controls.Add(notifyMinutesBox);
            networkLayout.SetColumnSpan(notifyRow, 2);
            networkLayout.Controls.Add(notifyRow, 0, 6);

            networkBox.Controls.Add(networkLayout);
            networkBox.Controls.Add(networkHeader);
            mainLayout.Controls.Add(networkBox, 0, 0);

            // Branding Buttons
            // v7.5.25: 4 columns now (was 3) - added "Add to Startup" so
            // users can directly trigger/confirm Windows auto-start
            // registration on demand, rather than relying purely on the
            // implicit launch-time self-heal (EnableAutoStartup() already
            // runs automatically on every launch - this just exposes the
            // same logic as something the user can invoke and get visible
            // confirmation from, e.g. for a machine where the app has never
            // actually been manually launched even once yet).
            // v7.5.28: 5 columns now (was 4) - "View Logs" moved here from
            // the main window, since it's purely a troubleshooting tool
            // rather than something used in day-to-day operation.
            TableLayoutPanel brandingPanel = new TableLayoutPanel() { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, Padding = new Padding(0) };
            brandingPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
            brandingPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
            brandingPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
            brandingPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
            brandingPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));

            Button logoButton = new Button() { Text = "Change Logo", Dock = DockStyle.Fill, Cursor = Cursors.Hand, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 5, 0) };
            logoButton.FlatAppearance.BorderSize = 1;
            logoButton.ApplyRoundedOutlineStyle(AppTheme.Accent);
            logoButton.Click += LogoPicture_Click;

            Button iconButton = new Button() { Text = "Change Icon", Dock = DockStyle.Fill, Cursor = Cursors.Hand, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 5, 0) };
            iconButton.FlatAppearance.BorderSize = 1;
            iconButton.ApplyRoundedOutlineStyle(AppTheme.Accent);
            iconButton.Click += FaviconButton_Click;

            Button startupButton = new Button() { Text = "Add to Startup", Dock = DockStyle.Fill, Cursor = Cursors.Hand, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 5, 0) };
            startupButton.FlatAppearance.BorderSize = 1;
            startupButton.ApplyRoundedOutlineStyle(AppTheme.Accent);
            startupButton.Click += (s, ev) =>
            {
                string result = EnableAutoStartup();
                MessageBox.Show(result, "Windows Startup", MessageBoxButtons.OK,
                    result.StartsWith("Couldn't") ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            };

            Button viewLogsButton = new Button() { Text = "View Logs", Dock = DockStyle.Fill, Cursor = Cursors.Hand, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 5, 0) };
            viewLogsButton.FlatAppearance.BorderSize = 1;
            viewLogsButton.ApplyRoundedOutlineStyle(AppTheme.Accent);
            viewLogsButton.Click += ViewLogsButton_Click;

            Button closeButton = new Button() { Text = "Close", Dock = DockStyle.Fill, Cursor = Cursors.Hand, FlatStyle = FlatStyle.Flat, Margin = new Padding(0) };
            closeButton.FlatAppearance.BorderSize = 1;
            closeButton.ApplyRoundedOutlineStyle(AppTheme.TextSecondary);
            closeButton.Click += (s, ev) => settingsForm.Close();

            brandingPanel.Controls.Add(logoButton, 0, 0);
            brandingPanel.Controls.Add(iconButton, 1, 0);
            brandingPanel.Controls.Add(startupButton, 2, 0);
            brandingPanel.Controls.Add(viewLogsButton, 3, 0);
            brandingPanel.Controls.Add(closeButton, 4, 0);
            mainLayout.Controls.Add(brandingPanel, 0, 1);

            // Update Check Section
            // v7.5.18: checks GitHub Releases for a newer version. The status
            // label and button both live in one panel so their state stays in
            // sync: "Check for Updates" -> "Checking..." -> either "Up to
            // date" (disabled) or "Update to vX.Y.Z" (accent-filled, clicking
            // downloads + launches the installer). Fired automatically as
            // soon as Settings opens (fire-and-forget, non-blocking - the
            // dialog itself doesn't wait on the network call) as well as via
            // manual re-click.
            TableLayoutPanel updatePanel = new TableLayoutPanel() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(0) };
            updatePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
            updatePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));

            Label updateStatusLabel = new Label() { Text = $"Current version: {APP_VERSION}", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = AppTheme.TextSecondary, AutoEllipsis = true };
            Button updateButton = new Button() { Text = "Check for Updates", Dock = DockStyle.Fill, Cursor = Cursors.Hand, FlatStyle = FlatStyle.Flat, Margin = new Padding(5, 0, 0, 0) };
            updateButton.FlatAppearance.BorderSize = 1;
            updateButton.ApplyRoundedOutlineStyle(AppTheme.Accent);

            string? pendingUpdateUrl = null;
            string pendingUpdateVersion = "";

            async Task RunUpdateCheck()
            {
                updateButton.Enabled = false;
                updateButton.Text = "Checking...";
                var (available, latestVersion, downloadUrl) = await CheckForUpdateAsync();
                if (available && !string.IsNullOrEmpty(downloadUrl))
                {
                    pendingUpdateUrl = downloadUrl;
                    pendingUpdateVersion = latestVersion;
                    updateStatusLabel.Text = $"Update available: v{latestVersion} (current: {APP_VERSION})";
                    updateButton.Text = $"Update to v{latestVersion}";
                    updateButton.ApplyRoundedFilledStyle(AppTheme.Accent, Color.White);
                    updateButton.Enabled = true;
                }
                else if (available)
                {
                    // Newer tag found but no matching installer asset - don't
                    // silently do nothing, point the user at the releases
                    // page instead of guessing at a download URL.
                    updateStatusLabel.Text = $"Update available: v{latestVersion}, but no installer asset was found";
                    updateButton.Text = "Open Releases Page";
                    updateButton.Enabled = true;
                    updateButton.Click += (s2, ev2) => Process.Start(new ProcessStartInfo { FileName = "https://github.com/HybridRCG/FileserverDriveManager/releases/latest", UseShellExecute = true });
                }
                else if (string.IsNullOrEmpty(latestVersion))
                {
                    // CheckForUpdateAsync returns "" for latestVersion only on
                    // a failed check (no network, GitHub unreachable, etc.) -
                    // distinguishes that from a genuine "you're up to date".
                    updateStatusLabel.Text = "Update check failed - see View Logs for details";
                    updateButton.Text = "Retry Check";
                    updateButton.Enabled = true;
                }
                else
                {
                    updateStatusLabel.Text = $"Up to date ({APP_VERSION})";
                    updateButton.Text = "Up to Date";
                    updateButton.Enabled = false;
                }
            }

            updateButton.Click += async (s, ev) =>
            {
                if (!string.IsNullOrEmpty(pendingUpdateUrl))
                {
                    updateButton.Enabled = false;
                    updateButton.Text = "Downloading...";
                    await DownloadAndLaunchUpdate(pendingUpdateUrl, pendingUpdateVersion);
                    // The installer (once launched) will taskkill and replace
                    // this running instance - leave the button as-is rather
                    // than re-enabling it, since a second click while the
                    // installer is up would just redownload the same thing.
                }
                else
                {
                    await RunUpdateCheck();
                }
            };

            updatePanel.Controls.Add(updateStatusLabel, 0, 0);
            updatePanel.Controls.Add(updateButton, 1, 0);
            mainLayout.Controls.Add(updatePanel, 0, 2);

            // Fire-and-forget: check automatically as soon as Settings opens,
            // so the button already reflects the real state without the user
            // needing to click it first. Errors are already swallowed inside
            // CheckForUpdateAsync/RunUpdateCheck, so this is safe to not await.
            _ = RunUpdateCheck();

            // Information Section
            // v7 "full tier": Panel with rounded border + separate header, same
            // treatment as networkBox above.
            Panel infoBox = new Panel() { Dock = DockStyle.Fill, Padding = new Padding(14, 12, 14, 14), BackColor = AppTheme.BgWhite };
            infoBox.ApplyCardStyle(AppTheme.BorderGray);
            Label infoHeader = new Label() { Text = "Information", Dock = DockStyle.Top, Height = 24, Font = new Font("Segoe UI", 10F, FontStyle.Bold), ForeColor = AppTheme.TextPrimary };
            Label infoText = new Label()
            {
                Text = "⚠️ Disclaimer: This tool manages network drive connections. Always verify credentials before saving.\n\n" +
                       "© 2026 Groblers CSS. All rights reserved.\n" +
                       "This application is provided as-is for authorized users only.\n" +
                       "Unauthorized access or distribution is prohibited.",
                Dock = DockStyle.Fill,
                Font = new Font("Arial", 8),
                ForeColor = AppTheme.TextSecondary
            };
            infoBox.Controls.Add(infoText);
            infoBox.Controls.Add(infoHeader);
            mainLayout.Controls.Add(infoBox, 0, 3);

            settingsForm.Controls.Add(mainLayout);
            settingsForm.ShowDialog();
        }

        private void ViewLogsButton_Click(object? sender, EventArgs e)
        {
            if (File.Exists(logPath))
            {
                Process.Start(new ProcessStartInfo { FileName = logPath, UseShellExecute = true });
            }
            else
            {
                MessageBox.Show("No log file found", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void TailscaleButton_Click(object? sender, EventArgs e)
        {
            LaunchTailscale();
        }

        private void NetBirdButton_Click(object? sender, EventArgs e)
        {
            LaunchNetBird();
        }

        private void LogoPicture_Click(object? sender, EventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog();
            dialog.Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp";
            dialog.Title = "Select Logo Image";

            if (dialog.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    string appDataDir = Path.Combine(appDataPath, "FileserverDriveManager");
                    if (!Directory.Exists(appDataDir))
                    {
                        Directory.CreateDirectory(appDataDir);
                    }

                    string destPath = Path.Combine(appDataDir, "logo.png");
                    File.Copy(dialog.FileName, destPath, true);

                    logoPicture.Image?.Dispose();
                    logoPicture.Image = new Bitmap(destPath);

                    MessageBox.Show("Logo updated! Restart the application to see changes.", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Error updating logo: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void FaviconButton_Click(object? sender, EventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog();
            dialog.Filter = "Image Files|*.png;*.ico";
            dialog.Title = "Select Icon Image";

            if (dialog.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    string appDataDir = Path.Combine(appDataPath, "FileserverDriveManager");
                    if (!Directory.Exists(appDataDir))
                    {
                        Directory.CreateDirectory(appDataDir);
                    }

                    string destPath = Path.Combine(appDataDir, "icon.png");
                    File.Copy(dialog.FileName, destPath, true);

                    MessageBox.Show("Icon updated! Restart the application to see changes.", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Error updating icon: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private List<string> GetAvailableShares()
        {
            List<string> availableShares = new List<string>();
            // Updated share list based on your actual fileserver
            List<string> allShares = new List<string> { "DynaBackup", "General", "Thomas", "Estelle", "Daniela", "Archives", "Proxmox", "IT", "Media", "SpinData" };

            int total = allShares.Count;
            int current = 0;

            foreach (string share in allShares)
            {
                current++;
                if (!isAuthenticating)
                {
                    break;
                }

                statusLabel.Text = $"Testing share access... ({current}/{total})";
                statusLabel.Refresh();

                if (TestShareAccess(share))
                {
                    availableShares.Add(share);
                    Log($"Share accessible: {share}");
                }
                else
                {
                    Log($"Share not accessible: {share}");
                }
            }

            return availableShares;
        }

        private bool TestShareAccess(string shareName)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "net",
                    Arguments = $"use \\\\{fileserverIP}\\{shareName} /user:{username} {password}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (Process process = new Process())
                {
                    process.StartInfo = psi;
                    process.Start();

                    if (!process.WaitForExit(3000))
                    {
                        process.Kill();
                        return false;
                    }

                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();

                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "net",
                        Arguments = $"use \\\\{fileserverIP}\\{shareName} /delete",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });

                    return process.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private void PopulateAvailableDriveLetters()
        {
            driveLetterBox.Items.Clear();
            string[] letters = { "E:", "F:", "G:", "H:", "I:", "J:", "K:", "L:", "M:", "N:", "O:", "P:", "Q:", "R:", "S:", "T:", "U:", "V:", "W:", "X:", "Y:", "Z:" };

            // Show ALL drive letters - user can choose any letter
            foreach (string letter in letters)
            {
                driveLetterBox.Items.Add(letter);
            }

            if (driveLetterBox.Items.Count > 0)
            {
                driveLetterBox.SelectedIndex = 0;
            }
        }

        private void RefreshStatus()
        {
            foreach (var drive in drives)
            {
                drive.Status = IsDriveMounted(drive.DriveLetter) ? "Mounted" : "Not Mounted";
            }

            drivesGrid.DataSource = null;
            drivesGrid.DataSource = drives;
            
            // Disable "Mount All" button if all drives are already mounted
            bool allMounted = drives.Count > 0 && drives.All(d => d.Status == "Mounted");
            bool authenticated = driveLetterBox.Enabled;  // Enabled only after successful auth
            
            if (authenticated)
            {
                mountDrivesButton.Enabled = !allMounted;
            }
        }

        // v7.5.0: unmounts every currently-tracked drive without removing it
        // from the saved list (unlike DismountAndRemoveDrive). Used internally
        // by failover to release drives mapped against a now-dead IP before
        // remounting them against the new one.
        private void UnmountAllDrivesQuiet()
        {
            foreach (var drive in drives)
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = "net",
                        Arguments = $"use {drive.DriveLetter} /delete",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (Process process = new Process())
                    {
                        process.StartInfo = psi;
                        process.Start();
                        process.WaitForExit(5000);
                    }
                }
                catch (Exception ex)
                {
                    Log($"Failover: error unmounting {drive.DriveLetter}: {ex.Message}");
                }
                drive.Status = "Not Mounted";
            }
            drivesGrid.DataSource = null;
            drivesGrid.DataSource = drives;
        }

        // v7.5.0: live failover. Runs on every 5-second status tick, but only
        // does real work (the actual TCP check) when there's something to
        // protect - an authenticated session with drives mounted against a
        // specific fileserverIP. Only switches when the CURRENT provider
        // genuinely stops responding, never just because a technically
        // faster path exists while everything's still working - this avoids
        // disruptive flapping between providers with similar latency.
        private async Task CheckFileserverFailover()
        {
            if (isCheckingFailover) return;
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) return;
            if (string.IsNullOrEmpty(fileserverIP)) return;
            if (drives.Count == 0) return;

            // v7.5.12: while every provider has been unreachable, skip ticks
            // according to a capped backoff (5s, 10s, 20s, 30s, then holds at
            // 30s) instead of re-racing on every single 5s tick. Only applies
            // once we've already seen at least one full miss - the first
            // check after a genuine disconnect still fires immediately.
            if (consecutiveFailoverMisses > 0)
            {
                double backoffSeconds = Math.Min(30, 5 * Math.Pow(2, consecutiveFailoverMisses - 1));
                if ((DateTime.Now - lastFailoverAttempt).TotalSeconds < backoffSeconds) return;
            }

            isCheckingFailover = true;
            lastFailoverAttempt = DateTime.Now;
            try
            {
                bool stillReachable = await Task.Run(() => TestFileserverReachability(fileserverIP));
                if (stillReachable)
                {
                    // v7.5.25: was silent here - only ever WROTE the negative
                    // "unreachable" message below, never a corresponding
                    // positive one on recovery. Confirmed real-world: a
                    // laptop booting with no WiFi/VPN yet would get this
                    // "unreachable" message set (correctly, at that moment),
                    // but once WiFi+VPN connected and drives mounted
                    // successfully (via the separate startup-retry path),
                    // this check could still run afterward, silently see
                    // "yes, reachable now", and just return - leaving the
                    // stale error message on screen indefinitely even though
                    // everything was actually working. Now explicitly
                    // reasserts a current, accurate status whenever recovering
                    // from a prior miss (not on every single healthy tick,
                    // to avoid needlessly overwriting more specific messages
                    // set elsewhere, e.g. right after a fresh manual mount).
                    if (consecutiveFailoverMisses > 0)
                    {
                        int recoveredMountedCount = drives.Count(d => d.Status == "Mounted");
                        statusLabel.Text = $"Connected via {fileserverIP} - {recoveredMountedCount} drives mounted";
                    }
                    consecutiveFailoverMisses = 0;
                    return;
                }

                Log($"Failover: current provider ({fileserverIP}) no longer reachable - re-racing...");
                var raceResults = await RaceFileserverIPs();
                var fastest = raceResults.FirstOrDefault(r => r.Success);
                if (fastest.IP == null)
                {
                    consecutiveFailoverMisses++;
                    Log($"Failover: no configured provider is currently reachable (miss #{consecutiveFailoverMisses}, next check in up to {Math.Min(30, 5 * Math.Pow(2, consecutiveFailoverMisses - 1))}s)");
                    statusLabel.Text = "Fileserver unreachable on all configured paths";
                    return;
                }
                consecutiveFailoverMisses = 0;
                if (fastest.IP == fileserverIP) return; // shouldn't normally happen, but guard anyway

                string oldIP = fileserverIP;
                Log($"Failover: switching from {oldIP} to {fastest.Label} ({fastest.IP}, {fastest.ElapsedMs}ms)");
                statusLabel.Text = $"Connection lost - switching to {fastest.Label}...";

                UnmountAllDrivesQuiet();
                fileserverIP = fastest.IP;
                await CleanupStaleSession(oldIP);
                await MountAllDrives();

                int mountedCount = drives.Count(d => d.Status == "Mounted");
                statusLabel.Text = $"Failed over to {fastest.Label} - {mountedCount} drives mounted";
                Log($"Failover complete - now using {fastest.Label} ({fastest.IP}), {mountedCount} drives mounted");
            }
            finally
            {
                isCheckingFailover = false;
            }
        }

        private async Task MountAllDrives()
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                statusLabel.Text = "Please authenticate first";
                return;
            }

            // v7.5.12: clear any stale session against this server before
            // mounting. Observed in the wild: after a string of failed
            // auto-connect/auth retries against the same fileserverIP, the
            // subsequent "net use <letter> \\ip\share ..." mount calls hung
            // for the full 15s timeout on every drive even though a fresh
            // authenticate + share-accessibility check had just succeeded
            // seconds earlier. A leftover half-open session from the earlier
            // churn is the most likely explanation - net use blocking while
            // waiting on an existing session to that server. This is a no-op
            // if there's nothing stale to clean up.
            // v7.5.23: broadened to check ALL configured paths (LAN/Tailscale/
            // NetBird), not just fileserverIP - a stale session via a
            // DIFFERENT path to the same physical server can also block/hang
            // a fresh mount via this path (confirmed real-world report: "net
            // use did not respond within 15s" on LAN despite auth having just
            // succeeded moments earlier).
            await CleanupAllStaleSessions();

            // v7.5.24: also clear each configured drive letter's own
            // mapping/reconnect registration, not just the server-level
            // session above. Machines that had a drive mapped previously
            // (older app version, this app before today's /persistent:no
            // fix, or even a manual map) can have Windows' own "reconnect at
            // sign-in" registration for that exact drive letter still
            // sitting around, which silently races against this app's own
            // net use call for the same letter at every logon. Best-effort/
            // bounded, same as CleanupAllStaleSessions - a drive letter with
            // nothing to clean up is not an error.
            await Task.WhenAll(drives.Select(d => CleanupDriveLetterMapping(d.DriveLetter)));

            int success = 0;
            int failed = 0;

            foreach (var drive in drives)
            {
                statusLabel.Text = $"Mounting {drive.DriveLetter}...";
                statusLabel.Refresh();

                try
                {
                    // v7.5.24: was /persistent:yes - this registers the drive
                    // letter with Windows for automatic reconnect at every
                    // future logon, entirely independent of this app. That's
                    // been directly implicated in mount hangs/failures on
                    // machines that had previously mapped a drive (via an
                    // older app version, this app before, or even manually):
                    // Windows' own silent background reconnect attempt at
                    // logon can race against this app's own explicit net use
                    // call for the SAME drive letter, and the app has no
                    // visibility into or control over that competing attempt.
                    // This app already re-establishes mappings on its own via
                    // auto-mount-on-startup (with far more robust retry/race/
                    // failover logic than Windows' native reconnect has), so
                    // there's no need for Windows to also try - /persistent:no
                    // means ending this app process cleanly releases the
                    // mapping rather than leaving it for the OS to fight over
                    // at next logon.
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = "net",
                        Arguments = $"use {drive.DriveLetter} \\\\{fileserverIP}\\{drive.ShareName} /user:{username} {password} /persistent:no",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using (Process process = new Process())
                    {
                        process.StartInfo = psi;
                        process.Start();
                        // v7.5.2: was the synchronous WaitForExit(15000). Still
                        // bounded (v7.5.1 fix), but a synchronous 15s block on
                        // the UI thread still makes the app look/act frozen
                        // (ignores right-clicks, shows "Not Responding") for
                        // that whole window if net use is slow. WaitForExitAsync
                        // yields the thread instead of blocking it.
                        bool exited = true;
                        using (var cts = new System.Threading.CancellationTokenSource(15000))
                        {
                            try { await process.WaitForExitAsync(cts.Token); }
                            catch (OperationCanceledException) { exited = false; }
                        }
                        if (!exited)
                        {
                            try { process.Kill(); } catch { }
                            drive.Status = "Timeout";
                            failed++;
                            Log($"Timed out mounting {drive.DriveLetter} (net use did not respond within 15s)");
                            continue;
                        }

                        // Exit code 0 = success, Exit code 2 = already mapped (also success)
                        // System error 85 (ERROR_ALREADY_ASSIGNED) appears in stderr but drive IS mounted
                        if (process.ExitCode == 0 || process.ExitCode == 2)
                        {
                            drive.Status = "Mounted";
                            success++;
                            Log($"Mounted {drive.DriveLetter} -> {drive.ShareName}");
                        }
                        else
                        {
                            string errorOutput = process.StandardError.ReadToEnd();
                            
                            // System error 85 = drive already assigned = actually mounted, not failed
                            if (errorOutput.Contains("System error 85") || errorOutput.Contains("already"))
                            {
                                drive.Status = "Mounted";
                                success++;
                                Log($"{drive.DriveLetter} already mounted -> {drive.ShareName}");
                            }
                            else
                            {
                                drive.Status = "Failed";
                                failed++;
                                Log($"Failed to mount {drive.DriveLetter}: {errorOutput}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    drive.Status = "Error";
                    failed++;
                    Log($"Error mounting {drive.DriveLetter}: {ex.Message}");
                }
            }

            drivesGrid.DataSource = null;
            drivesGrid.DataSource = drives;

            statusLabel.Text = $"Mounted {success} drives, {failed} failed";
        }

        private string GetVPNIP()
        {
            // No manual VPN provider selection anymore - both Tailscale and NetBird
            // detection are reliable now, so just check both and return whichever
            // is actually connected. Tailscale checked first (arbitrary, no real
            // preference between the two); NetBird as fallback.
            string tsIP = GetTailscaleIP();
            if (!string.IsNullOrEmpty(tsIP) && !tsIP.Contains("Not Connected"))
            {
                return tsIP;
            }
            return GetNetBirdIP();
        }

        private void LaunchVPN()
        {
            // Unused by any caller currently (no provider-selection UI to trigger
            // it from), kept for potential future use - launches Tailscale first.
            LaunchTailscale();
        }

        private void LaunchTailscale()
        {
            try
            {
                string tailscalePath = @"C:\Program Files\Tailscale\tailscale.exe";
                if (File.Exists(tailscalePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = tailscalePath,
                        Arguments = "up",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    Log("Launched Tailscale");
                    statusLabel.Text = "Tailscale launched";
                }
                else
                {
                    MessageBox.Show("Tailscale not found. Please install Tailscale first.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                Log("Error launching Tailscale: " + ex.Message);
                MessageBox.Show("Error launching Tailscale: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void LaunchNetBird()
        {
            try
            {
                string netbirdPath = @"C:\Program Files\Netbird\netbird.exe";
                if (File.Exists(netbirdPath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = netbirdPath,
                        Arguments = "up",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    Log("Launched NetBird");
                    statusLabel.Text = "NetBird launched";
                }
                else
                {
                    MessageBox.Show("NetBird not found. Please install NetBird first.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                Log("Error launching NetBird: " + ex.Message);
                MessageBox.Show("Error launching NetBird: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void UpdateNetworkStatus()
        {
            if (isAuthenticating) return;

            string lanIP = GetNetworkInfo();
            string tailscaleIP = GetTailscaleIP();
            string netbirdIP = GetNetBirdIP();

            lanIPLabel.Text = string.IsNullOrEmpty(lanIP) ? "Network: Not Connected" : $"Network: {lanIP}";
            lanIPLabel.ForeColor = string.IsNullOrEmpty(lanIP) ? AppTheme.MutedText : AppTheme.TextPrimary;

            tailscaleIPLabel.Text = tailscaleIP.Contains("Not Connected") ? "Tailscale: Not Connected" : $"Tailscale IP: {tailscaleIP}";
            tailscaleIPLabel.ForeColor = tailscaleIP.Contains("Not Connected") ? AppTheme.MutedText : AppTheme.SuccessText;

            netbirdIPLabel.Text = netbirdIP.Contains("Not Connected") ? "NetBird: Not Connected" : $"NetBird IP: {netbirdIP}";
            netbirdIPLabel.ForeColor = netbirdIP.Contains("Not Connected") ? AppTheme.MutedText : AppTheme.SuccessText;
            
            // ===== BUTTON STATE LOGIC =====
            bool tailscaleConnected = !tailscaleIP.Contains("Not Connected");
            bool netbirdConnected = !netbirdIP.Contains("Not Connected");
            
            // Each VPN button is only disabled if THAT VPN is already connected
            // Both can run simultaneously on different IP ranges (Tailscale: 100.x, NetBird: 10.x)
            tailscaleButton.Enabled = !tailscaleConnected;
            netbirdButton.Enabled = !netbirdConnected;
        }

        private string GetNetworkInfo()
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus == OperationalStatus.Up && 
                        ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    {
                        // v7.5.3: this used to only exclude Tailscale's 100.x
                        // CGNAT range via IP prefix - but NetBird's range is
                        // configurable per network (we've seen 10.64.x.x here),
                        // so on a machine with no physical LAN, NetBird's own
                        // tunnel adapter was getting picked up and mislabeled
                        // as "(Ethernet)"/"(WiFi)" LAN. Explicitly exclude
                        // known VPN adapters by name/description instead of
                        // guessing from the IP range, matching the same checks
                        // GetTailscaleIP()/GetNetBirdIP() already use.
                        string niName = ni.Name.ToLower();
                        string niDesc = ni.Description.ToLower();
                        bool isKnownVpnAdapter = niName.Contains("tailscale") || niDesc.Contains("tailscale")
                            || niName.Contains("netbird") || niDesc.Contains("netbird")
                            || niName == "wt0" || niDesc.Contains("wireguard tunnel")
                            || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel;
                        if (isKnownVpnAdapter) continue;

                        foreach (UnicastIPAddressInformation ip in ni.GetIPProperties().UnicastAddresses)
                        {
                            if (ip.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            {
                                string ipStr = ip.Address.ToString();
                                if (!ipStr.StartsWith("100.") && !ipStr.StartsWith("127."))
                                {
                                    string interfaceType = ni.Name.Contains("WiFi") || ni.Name.Contains("Wi-Fi") ? "(WiFi)" : "(Ethernet)";
                                    return $"{ipStr} {interfaceType}";
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return "";
        }

        private string GetTailscaleIP()
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    // Check both Name and Description: Tailscale's WinTun adapter often gets
                    // a generic OS-assigned Name (e.g. "Ethernet 5") while the identifying
                    // "Tailscale" text is actually in Description, not Name.
                    if ((ni.Name.ToLower().Contains("tailscale") || ni.Description.ToLower().Contains("tailscale"))
                        && ni.OperationalStatus == OperationalStatus.Up)
                    {
                        foreach (UnicastIPAddressInformation ip in ni.GetIPProperties().UnicastAddresses)
                        {
                            if (ip.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            {
                                string ipStr = ip.Address.ToString();
                                if (ipStr.StartsWith("100."))
                                {
                                    return ipStr;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return "Not Connected";
        }

        private string GetNetBirdIP()
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    // NetBird's Windows adapter never contains the literal word "netbird" -
                    // it shows up as Name="wt0", Description="WireGuard Tunnel". Match on
                    // those specifically (plus a literal "netbird" check as a fallback for
                    // future client versions that might name it differently).
                    string niNameLower = ni.Name.ToLower();
                    string niDescLower = ni.Description.ToLower();
                    bool looksLikeNetBird = niNameLower.Contains("netbird")
                        || niDescLower.Contains("netbird")
                        || niNameLower == "wt0"
                        || niDescLower.Contains("wireguard tunnel");
                    // Not gating on OperationalStatus == Up here: WinTun-based virtual
                    // adapters (which NetBird uses) are known to sometimes report
                    // OperationalStatus as Unknown via .NET's API even when actually
                    // functional and shown as "Up" in Get-NetAdapter/Windows itself.
                    // A valid 100.x address is sufficient proof of a working connection.
                    if (looksLikeNetBird)
                    {
                        foreach (UnicastIPAddressInformation ip in ni.GetIPProperties().UnicastAddresses)
                        {
                            if (ip.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            {
                                // Unlike Tailscale (fixed 100.x CGNAT range), NetBird's IPv4
                                // range is configurable per-network and was observed issuing
                                // 10.64.x.x here, not 100.x. Since this adapter is already
                                // confirmed to be NetBird by name/description above, any
                                // valid IPv4 on it is correct - no range filter needed.
                                return ip.Address.ToString();
                            }
                        }
                    }
                }
            }
            catch { }
            return "Not Connected";
        }

        // Lightweight peek at just the darkMode preference, safe to call
        // before any UI exists. Separate from LoadSavedSettings() because that
        // one runs after InitializeComponents and touches controls that don't
        // exist yet at this point in startup.
        private static bool PeekDarkModePreference()
        {
            try
            {
                string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileserverDriveManager-settings.json");
                if (!File.Exists(settingsPath)) return false;
                string json = File.ReadAllText(settingsPath);
                var settings = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(json);
                if (settings != null && settings.ContainsKey("darkMode") && settings["darkMode"].ValueKind == System.Text.Json.JsonValueKind.True)
                    return true;
            }
            catch { }
            return false;
        }

        private void LoadSavedSettings()
        {
            try
            {
                string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileserverDriveManager-settings.json");

                if (File.Exists(settingsPath))
                {
                    string json = File.ReadAllText(settingsPath);
                    var settings = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(json);
                    if (settings == null) return;

                    if (settings.ContainsKey("username") && settings["username"].ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        username = settings["username"].GetString() ?? "";
                        usernameBox.Text = username;
                    }

                    if (settings.ContainsKey("password") && settings["password"].ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        password = DecryptPassword(settings["password"].GetString() ?? "");
                        passwordBox.Text = password;
                    }

                    if (settings.ContainsKey("fileserverIP") && settings["fileserverIP"].ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        fileserverIP = settings["fileserverIP"].GetString() ?? fileserverIP;
                        // v7.3 migration: older settings files only have this
                        // single legacy key. Treat it as the LAN candidate so
                        // existing users' saved IP isn't lost when upgrading.
                        if (!settings.ContainsKey("fileserverLanIP"))
                        {
                            fileserverLanIP = fileserverIP;
                        }
                    }
                    if (settings.ContainsKey("fileserverLanIP") && settings["fileserverLanIP"].ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        fileserverLanIP = settings["fileserverLanIP"].GetString() ?? fileserverLanIP;
                    }
                    if (settings.ContainsKey("fileserverTailscaleIP") && settings["fileserverTailscaleIP"].ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        fileserverTailscaleIP = settings["fileserverTailscaleIP"].GetString() ?? fileserverTailscaleIP;
                    }
                    if (settings.ContainsKey("fileserverNetbirdIP") && settings["fileserverNetbirdIP"].ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        fileserverNetbirdIP = settings["fileserverNetbirdIP"].GetString() ?? fileserverNetbirdIP;
                    }

                    if (settings.ContainsKey("autoMountOnStartup"))
                    {
                        if (settings["autoMountOnStartup"].ValueKind == System.Text.Json.JsonValueKind.True)
                        {
                            autoMountOnStartup = true;
                        }
                        else if (settings["autoMountOnStartup"].ValueKind == System.Text.Json.JsonValueKind.False)
                        {
                            autoMountOnStartup = false;
                        }
                    }

                    // v7.1: darkModeEnabled reflects the saved preference here for
                    // syncing the Settings dialog checkbox. AppTheme.IsDark (already
                    // applied at startup via PeekDarkModePreference) is what actually
                    // determines the colors already baked into this session's UI.
                    if (settings.ContainsKey("darkMode"))
                    {
                        darkModeEnabled = settings["darkMode"].ValueKind == System.Text.Json.JsonValueKind.True;
                    }
                    if (settings.ContainsKey("disconnectNotifyMinutes") && settings["disconnectNotifyMinutes"].ValueKind == System.Text.Json.JsonValueKind.Number)
                    {
                        disconnectNotifyMinutes = settings["disconnectNotifyMinutes"].GetInt32();
                    }

                    if (settings.ContainsKey("drives"))
                    {
                        // v7.5.12: isolated in its own try/catch. This used to
                        // be inside the same block as everything above, so a
                        // single malformed/legacy drive entry (e.g. a required
                        // property genuinely absent from the JSON) threw and
                        // was caught by the OUTER catch below - which logged
                        // "Error loading settings" but by that point username,
                        // password, IPs, and other prefs had already been
                        // applied successfully. The user just lost their drive
                        // list silently while everything else looked fine.
                        // Now a drives-specific failure only drops the drives,
                        // logs clearly why, and lets the rest of settings load.
                        try
                        {
                            var drivesJson = settings["drives"].GetRawText();
                            drives = System.Text.Json.JsonSerializer.Deserialize<List<DriveMapping>>(drivesJson) ?? new List<DriveMapping>();
                            drivesGrid.DataSource = drives;
                        }
                        catch (Exception drivesEx)
                        {
                            Log("Could not load saved drive mappings (settings file may be from an older version): " + drivesEx.Message);
                            drives = new List<DriveMapping>();
                            drivesGrid.DataSource = drives;
                        }
                    }

                    Log("Settings loaded successfully");
                }
            }
            catch (Exception ex)
            {
                Log("Error loading settings: " + ex.Message);
            }
        }

        private void SaveCurrentSettings()
        {
            try
            {
                string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileserverDriveManager-settings.json");

                var settings = new Dictionary<string, object>
                {
                    { "username", username },
                    { "password", EncryptPassword(password) },
                    { "fileserverIP", fileserverIP },
                    { "fileserverLanIP", fileserverLanIP },
                    { "fileserverTailscaleIP", fileserverTailscaleIP },
                    { "fileserverNetbirdIP", fileserverNetbirdIP },
                    { "autoMountOnStartup", autoMountOnStartup },
                    { "darkMode", darkModeEnabled },
                    { "disconnectNotifyMinutes", disconnectNotifyMinutes },
                    { "drives", drives }
                };

                string json = System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(settingsPath, json);

                Log("Settings saved successfully");
            }
            catch (Exception ex)
            {
                Log("Error saving settings: " + ex.Message);
            }
        }

        // v7.3: races all three configured fileserver paths (LAN, Tailscale,
        // NetBird) concurrently via a timed TCP connect to port 445, and
        // returns them ranked fastest-first. Candidates with an empty IP are
        // skipped. Run concurrently (not sequentially) so this doesn't cost
        // 3x the per-candidate timeout - the whole race takes as long as the
        // slowest candidate's timeout, not the sum of all three.
        private async Task<List<(string Label, string IP, long ElapsedMs, bool Success)>> RaceFileserverIPs()
        {
            var candidates = new List<(string Label, string IP)>();
            if (!string.IsNullOrWhiteSpace(fileserverLanIP)) candidates.Add(("LAN", fileserverLanIP.Trim()));
            if (!string.IsNullOrWhiteSpace(fileserverTailscaleIP)) candidates.Add(("Tailscale", fileserverTailscaleIP.Trim()));
            if (!string.IsNullOrWhiteSpace(fileserverNetbirdIP)) candidates.Add(("NetBird", fileserverNetbirdIP.Trim()));

            var tasks = candidates.Select(async c =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool success = false;
                try
                {
                    using (var client = new System.Net.Sockets.TcpClient())
                    {
                        var connectTask = client.ConnectAsync(c.IP, 445);
                        var completed = await Task.WhenAny(connectTask, Task.Delay(3000));
                        success = completed == connectTask && client.Connected;
                    }
                }
                catch { success = false; }
                sw.Stop();
                return (c.Label, c.IP, sw.ElapsedMilliseconds, success);
            });

            var results = (await Task.WhenAll(tasks)).ToList();

            // v7.5.3: a successful "LAN" result doesn't necessarily mean a
            // real physical LAN link - if a VPN advertises an exit-node route
            // for that subnet, the exact same TCP connect succeeds via the
            // tunnel instead. A raw reachability check can't tell those apart
            // on its own, so cross-check: if the client's own local adapters
            // aren't actually on that subnet, this "LAN" success is being
            // routed through VPN, and the label should say so rather than
            // implying a direct connection that isn't really there.
            for (int i = 0; i < results.Count; i++)
            {
                if (results[i].Label == "LAN" && results[i].success && !IsClientOnSameLanSubnet(results[i].IP))
                {
                    results[i] = ("LAN (via VPN route)", results[i].IP, results[i].ElapsedMilliseconds, results[i].success);
                }
            }

            // Successful candidates first (fastest first), unreachable ones after
            results = results.OrderBy(r => r.success ? 0 : 1).ThenBy(r => r.ElapsedMilliseconds).ToList();
            foreach (var r in results)
                Log($"[FileserverRace] {r.Label} ({r.IP}): {(r.success ? $"{r.ElapsedMilliseconds}ms" : "unreachable")}");
            return results;
        }

        // v7.5.3: heuristic /24 subnet check - true if any of the client's own
        // non-loopback, non-VPN adapters has an IPv4 address sharing the same
        // first three octets as targetIp. Used to distinguish a genuine LAN
        // connection from a VPN-tunneled route reaching the same subnet.
        private bool IsClientOnSameLanSubnet(string targetIp)
        {
            try
            {
                var targetParts = targetIp.Split('.');
                if (targetParts.Length != 4) return false;
                string targetPrefix = $"{targetParts[0]}.{targetParts[1]}.{targetParts[2]}.";

                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;
                    string niName = ni.Name.ToLower();
                    string niDesc = ni.Description.ToLower();
                    bool isKnownVpnAdapter = niName.Contains("tailscale") || niDesc.Contains("tailscale")
                        || niName.Contains("netbird") || niDesc.Contains("netbird")
                        || niName == "wt0" || niDesc.Contains("wireguard tunnel")
                        || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel;
                    if (isKnownVpnAdapter) continue;

                    foreach (UnicastIPAddressInformation ip in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ip.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                            && ip.Address.ToString().StartsWith(targetPrefix))
                        {
                            return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        // v7.5.18: checks GitHub Releases for a newer tagged version than
        // this build. Returns available=false (with no exception surfaced to
        // the caller) on any network failure - an update check failing
        // silently is fine, it just means the Settings button stays on
        // "Check for Updates" rather than blocking anything. latestVersion
        // is the bare "X.Y.Z" (no leading 'v'); downloadUrl points at the
        // first .exe release asset whose name contains "Drive" (the NSIS
        // installer, e.g. "Drive.Manager.V7.5.18.exe"), falling back to
        // whatever .exe asset appears first if that name pattern ever
        // changes.
        private async Task<(bool available, string latestVersion, string? downloadUrl)> CheckForUpdateAsync()
        {
            try
            {
                if (!updateHttpClient.DefaultRequestHeaders.UserAgent.Any())
                {
                    updateHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("FileserverDriveManager-UpdateCheck");
                }

                string json = await updateHttpClient.GetStringAsync(UPDATE_CHECK_URL);
                using var doc = System.Text.Json.JsonDocument.Parse(json);

                string tagName = doc.RootElement.TryGetProperty("tag_name", out var tagEl) ? (tagEl.GetString() ?? "") : "";
                string latestVersionStr = tagName.TrimStart('v', 'V');

                // APP_VERSION is "v7.5.17" in Release builds, "v7.5.17-dev"
                // in Debug builds - strip both the leading 'v' and any
                // trailing "-dev" suffix so Version.TryParse succeeds either
                // way.
                string currentVersionStr = APP_VERSION.TrimStart('v', 'V');
                int dashIndex = currentVersionStr.IndexOf('-');
                if (dashIndex >= 0) currentVersionStr = currentVersionStr.Substring(0, dashIndex);

                string? downloadUrl = null;
                if (doc.RootElement.TryGetProperty("assets", out var assets))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        string name = asset.TryGetProperty("name", out var nameEl) ? (nameEl.GetString() ?? "") : "";
                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                            name.Contains("Drive", StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = asset.TryGetProperty("browser_download_url", out var urlEl) ? urlEl.GetString() : null;
                            break;
                        }
                    }
                    if (downloadUrl == null)
                    {
                        foreach (var asset in assets.EnumerateArray())
                        {
                            string name = asset.TryGetProperty("name", out var nameEl) ? (nameEl.GetString() ?? "") : "";
                            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            {
                                downloadUrl = asset.TryGetProperty("browser_download_url", out var urlEl) ? urlEl.GetString() : null;
                                break;
                            }
                        }
                    }
                }

                bool isNewer;
                if (Version.TryParse(latestVersionStr, out var latestVer) && Version.TryParse(currentVersionStr, out var currentVer))
                {
                    isNewer = latestVer > currentVer;
                }
                else
                {
                    // Fallback if either string isn't a parseable Version
                    // (shouldn't normally happen given the tagging scheme) -
                    // treat any non-matching string as "different", so the
                    // user at least sees something changed rather than the
                    // check silently doing nothing.
                    isNewer = !string.Equals(latestVersionStr, currentVersionStr, StringComparison.OrdinalIgnoreCase);
                }

                return (isNewer, latestVersionStr, downloadUrl);
            }
            catch (Exception ex)
            {
                Log("Update check failed: " + ex.Message);
                return (false, "", null);
            }
        }

        // v7.5.18: downloads the release installer to %TEMP% and launches it
        // (UseShellExecute so Windows handles any UAC elevation prompt the
        // installer needs). Deliberately does NOT kill or close the running
        // app itself - installer.nsi already does "taskkill /F /IM
        // FileserverDriveManager.exe" as its first install step, so the
        // running instance gets closed by the installer at the right moment
        // instead of leaving a gap where the user has no app open at all if
        // they cancel partway through the installer.
        private async Task DownloadAndLaunchUpdate(string downloadUrl, string version)
        {
            try
            {
                string tempPath = Path.Combine(Path.GetTempPath(), $"FileserverDriveManager-Update-v{version}.exe");
                // v7.5.20: was GetByteArrayAsync via the 8s-timeout client
                // (buffering the whole ~100MB exe in memory before writing
                // it out) - now uses the long-timeout client and streams
                // straight to disk, so memory use stays flat regardless of
                // installer size and there's no chance of the same
                // wrong-client mistake recurring here.
                using (var response = await downloadHttpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    using (var contentStream = await response.Content.ReadAsStreamAsync())
                    using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await contentStream.CopyToAsync(fileStream);
                    }
                }
                Log($"Downloaded update installer v{version} to {tempPath}");
                Process.Start(new ProcessStartInfo { FileName = tempPath, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log("Update download/launch failed: " + ex.Message);
                MessageBox.Show(
                    $"Couldn't download or start the update installer.\n\n{ex.Message}\n\nYou can download it manually from the GitHub Releases page instead.",
                    "Update failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // v7.5.12: best-effort cleanup of any existing "net use" session against
        // this server IP before (re)authenticating. Repeated auto-connect
        // attempts across VPN reconnects/failovers could leave a half-open or
        // stale session behind, which then surfaces later as either "System
        // error 1219" (multiple connections using more than one username) on
        // the next auth attempt, or a hung "net use" on mount because it's
        // waiting on the existing session. Deliberately silent/short-timeout -
        // this is just tidying up before we try again, not a critical step.
        private async Task CleanupStaleSession(string ip)
        {
            try
            {
                using (Process process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = "net",
                        Arguments = $"use \\\\{ip} /delete /y",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    process.Start();
                    using (var cts = new System.Threading.CancellationTokenSource(3000))
                    {
                        try { await process.WaitForExitAsync(cts.Token); }
                        catch (OperationCanceledException) { try { process.Kill(); } catch { } }
                    }
                }
            }
            catch { /* best-effort - a missing session to delete is not an error */ }
        }

        // v7.5.24: sibling of CleanupStaleSession above, but for a DRIVE
        // LETTER's mapping/reconnect registration rather than a server-level
        // session. Deliberately separate rather than reusing
        // CleanupStaleSession with a different argument shape - that method
        // always prepends "\\" (correct for a server path like \\192.168.1.26,
        // wrong for a drive letter like G:), so passing a drive letter
        // through it would build a malformed "net use \\G: /delete /y".
        private async Task CleanupDriveLetterMapping(string driveLetter)
        {
            try
            {
                using (Process process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = "net",
                        Arguments = $"use {driveLetter} /delete /y",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    process.Start();
                    using (var cts = new System.Threading.CancellationTokenSource(3000))
                    {
                        try { await process.WaitForExitAsync(cts.Token); }
                        catch (OperationCanceledException) { try { process.Kill(); } catch { } }
                    }
                }
            }
            catch { /* best-effort - a missing mapping to delete is not an error */ }
        }

        // v7.5.23: broader version of the above - clears stale sessions
        // against ALL THREE configured fileserver paths (LAN, Tailscale,
        // NetBird), not just whichever one the current attempt is using.
        // The single-IP cleanup above only guards against a stale session to
        // the exact path being retried, but Windows only allows one
        // credentialed SMB session per PHYSICAL server per client at a time -
        // if an earlier attempt this session connected via a DIFFERENT path
        // to the SAME server (e.g. VPN was up earlier, now LAN is being
        // used), that now-stale session can block or hang a fresh connection
        // via the current path even though the two IPs are literally
        // different strings to Windows' net use. Confirmed pattern from an
        // earlier real case (System error 1219, "multiple connections...
        // different user name") and matches a report of "net use did not
        // respond within 15s" on LAN despite auth having just succeeded.
        // Runs the three cleanups concurrently since each is independently
        // bounded (3s) and skips any IP that's blank/not configured.
        private async Task CleanupAllStaleSessions()
        {
            var ips = new[] { fileserverLanIP, fileserverTailscaleIP, fileserverNetbirdIP }
                .Where(ip => !string.IsNullOrWhiteSpace(ip))
                .Select(ip => ip.Trim())
                .Distinct()
                .ToList();
            await Task.WhenAll(ips.Select(CleanupStaleSession));
        }

        private bool TestFileserverReachability(string ip)
        {
            // Pure reachability check: attempts a raw TCP connect to the SMB port
            // (445). No credentials, no share name, no mounting - just "is this
            // host up and listening for SMB on the network/VPN path right now".
            // Distinct from TestFileserverConnection(), which actually
            // authenticates and mounts a real share (used by Authenticate).
            try
            {
                using (var client = new System.Net.Sockets.TcpClient())
                {
                    var connectTask = client.ConnectAsync(ip, 445);
                    bool completedInTime = connectTask.Wait(3000);
                    return completedInTime && client.Connected;
                }
            }
            catch (Exception ex)
            {
                Log($"Reachability test error for {ip}: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> TestFileserverConnection(string testUsername, string testPassword)
        {
            try
            {
                // Test connection by trying to access a known share
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "net",
                    Arguments = $"use \\\\{fileserverIP}\\General /user:{testUsername} {testPassword}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (Process process = new Process())
                {
                    process.StartInfo = psi;
                    process.Start();
                    // v7.5.2: was the synchronous WaitForExit(5000), which blocks
                    // the UI thread for up to 5s even though it's bounded.
                    // WaitForExitAsync yields instead of blocking, keeping the
                    // app responsive (tray icon, right-click) throughout.
                    using (var cts = new System.Threading.CancellationTokenSource(5000))
                    {
                        try { await process.WaitForExitAsync(cts.Token); }
                        catch (OperationCanceledException) { try { process.Kill(); } catch { } }
                    }

                    bool success = process.ExitCode == 0;

                    // v7.5.25: was silently discarding net use's actual output on
                    // failure, so every failed auth just logged "failed" with no
                    // way to tell a bad password (1326) apart from the legacy-
                    // install session collision (1219) or anything else. Confirmed
                    // real-world case: TCP reachable, saved+typed credentials both
                    // "failing" for 18+ minutes straight - was actually error 1219
                    // from a stale session held open by an old legacy x86 install.
                    if (!success)
                    {
                        string stdErr = (await process.StandardError.ReadToEndAsync()).Trim();
                        string stdOut = (await process.StandardOutput.ReadToEndAsync()).Trim();
                        string detail = !string.IsNullOrWhiteSpace(stdErr) ? stdErr
                                       : !string.IsNullOrWhiteSpace(stdOut) ? stdOut
                                       : $"exit code {process.ExitCode}, no output";
                        Log($"net use to {fileserverIP} failed: {detail}");
                    }

                    // Clean up the test connection
                    if (success)
                    {
                        var cleanupProcess = Process.Start(new ProcessStartInfo
                        {
                            FileName = "net",
                            Arguments = $"use \\\\{fileserverIP}\\General /delete",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        });
                        if (cleanupProcess != null)
                        {
                            using (var cts2 = new System.Threading.CancellationTokenSource(2000))
                            {
                                try { await cleanupProcess.WaitForExitAsync(cts2.Token); }
                                catch (OperationCanceledException) { try { cleanupProcess.Kill(); } catch { } }
                            }
                        }
                    }

                    return success;
                }
            }
            catch (Exception ex)
            {
                Log($"Test connection error: {ex.Message}");
                return false;
            }
        }

        private string EncryptPassword(string plainText)
        {
            try
            {
                byte[] key = System.Text.Encoding.UTF8.GetBytes("FileserverDriveManager2024Key!!");
                byte[] iv = System.Text.Encoding.UTF8.GetBytes("InitializationV!");

                using (Aes aes = Aes.Create())
                {
                    aes.Key = key;
                    aes.IV = iv;

                    ICryptoTransform encryptor = aes.CreateEncryptor(aes.Key, aes.IV);

                    using (MemoryStream ms = new MemoryStream())
                    {
                        using (CryptoStream cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                        {
                            using (StreamWriter sw = new StreamWriter(cs))
                            {
                                sw.Write(plainText);
                            }
                            return Convert.ToBase64String(ms.ToArray());
                        }
                    }
                }
            }
            catch
            {
                return plainText;
            }
        }

        private string DecryptPassword(string cipherText)
        {
            try
            {
                byte[] key = System.Text.Encoding.UTF8.GetBytes("FileserverDriveManager2024Key!!");
                byte[] iv = System.Text.Encoding.UTF8.GetBytes("InitializationV!");
                byte[] buffer = Convert.FromBase64String(cipherText);

                using (Aes aes = Aes.Create())
                {
                    aes.Key = key;
                    aes.IV = iv;

                    ICryptoTransform decryptor = aes.CreateDecryptor(aes.Key, aes.IV);

                    using (MemoryStream ms = new MemoryStream(buffer))
                    {
                        using (CryptoStream cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read))
                        {
                            using (StreamReader sr = new StreamReader(cs))
                            {
                                return sr.ReadToEnd();
                            }
                        }
                    }
                }
            }
            catch
            {
                return cipherText;
            }
        }

        private bool IsDriveMounted(string driveLetter)
        {
            return Directory.Exists(driveLetter);
        }

        private void Log(string message)
        {
            try
            {
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                string logMessage = $"[{timestamp}] {message}";
                File.AppendAllText(logPath, logMessage + Environment.NewLine);
            }
            catch { }
        }
    }

    // ========================================================================
    // RoundedRenderer - v7 "full tier" owner-drawn rounded controls.
    // Kept as a SEPARATE class from ModernButtonExtensions (v6 "cheap tier")
    // rather than replacing it, so v6's flat-rectangle styling stays intact
    // and easy to restore (see archive/Program_v6.0.0_*.cs, or git commit
    // f4d16c3) if a full rollback to v6 is ever needed - swapping between
    // tiers is just changing which extension method a control calls.
    // ========================================================================
    public static class RoundedRenderer
    {
        public const int ButtonRadius = 8;
        public const int CardRadius = 12;
        public const int PillRadius = 10;

        public static GraphicsPath GetRoundedRectPath(Rectangle bounds, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            if (d > bounds.Width) d = bounds.Width;
            if (d > bounds.Height) d = bounds.Height;
            path.StartFigure();
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // Filled rounded button (primary actions - Authenticate, Mount All, Save IP, Exit).
        public static void ApplyRoundedFilledStyle(this Button btn, Color fillColor, Color textColor)
        {
            Color hoverColor = LightenColor(fillColor, 0.15f);
            Color pressColor = DarkenColor(fillColor, 0.10f);
            // v7.5.19: was hardcoded Color.FromArgb(220,218,216) / (150,148,146)
            // - fixed light-theme-assuming grays that never adapted for dark
            // mode, making disabled buttons (e.g. "Up to Date") hard to read
            // there. Now derives from AppTheme so disabled fill/text stay
            // theme-appropriate: BorderGray is already tuned per-mode (light
            // gray on light theme, dark gray on dark theme) and TextSecondary
            // already has adequate contrast against it in both.
            Color disabledFill = AppTheme.BorderGray;
            Color disabledText = AppTheme.TextSecondary;
            Color currentFill = fillColor;

            btn.FlatStyle = FlatStyle.Flat;
            btn.FlatAppearance.BorderSize = 0;
            btn.BackColor = Color.Transparent;
            btn.Cursor = Cursors.Hand;

            btn.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var eraseBrush = new SolidBrush(btn.Parent?.BackColor ?? SystemColors.Control))
                    g.FillRectangle(eraseBrush, new Rectangle(0, 0, btn.Width, btn.Height));
                var rect = new Rectangle(0, 0, btn.Width - 1, btn.Height - 1);
                Color activeFill = btn.Enabled ? currentFill : disabledFill;
                Color activeText = btn.Enabled ? textColor : disabledText;
                using (var path = GetRoundedRectPath(rect, ButtonRadius))
                using (var brush = new SolidBrush(activeFill))
                {
                    g.FillPath(brush, path);
                }
                TextRenderer.DrawText(g, btn.Text, btn.Font, rect, activeText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            };

            btn.MouseEnter += (s, e) => { if (btn.Enabled) { currentFill = hoverColor; btn.Invalidate(); } };
            btn.MouseLeave += (s, e) => { if (btn.Enabled) { currentFill = fillColor; btn.Invalidate(); } };
            btn.MouseDown += (s, e) => { if (btn.Enabled && e.Button == MouseButtons.Left) { currentFill = pressColor; btn.Invalidate(); } };
            btn.MouseUp += (s, e) =>
            {
                if (!btn.Enabled) return;
                bool stillHovered = btn.ClientRectangle.Contains(btn.PointToClient(Cursor.Position));
                currentFill = stillHovered ? hoverColor : fillColor;
                btn.Invalidate();
            };
            btn.EnabledChanged += (s, e) => { currentFill = fillColor; btn.Invalidate(); };
        }

        // Outline rounded button (everything that isn't the primary action of its screen).
        public static void ApplyRoundedOutlineStyle(this Button btn, Color accentColor)
        {
            Color hoverBg = LightenColor(accentColor, 0.92f);
            // v7.5.19: was hardcoded Color.FromArgb(180,178,176) for the
            // disabled border/text color, and Color.White (below) for the
            // button's own background - both fixed regardless of theme. The
            // white background is the real problem: in dark mode this drew a
            // literal white box while the text/border color correctly
            // switched to the LIGHT gray meant to sit on a DARK background
            // (see AppTheme.TextSecondary), so light-on-white read as washed
            // out. Both now derive from AppTheme so they track whichever
            // theme is actually active.
            Color disabledColor = AppTheme.BorderGray;
            Color currentBg = AppTheme.BgWhite;

            btn.FlatStyle = FlatStyle.Flat;
            btn.FlatAppearance.BorderSize = 0;
            btn.BackColor = Color.Transparent;
            btn.Cursor = Cursors.Hand;

            btn.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var eraseBrush = new SolidBrush(btn.Parent?.BackColor ?? SystemColors.Control))
                    g.FillRectangle(eraseBrush, new Rectangle(0, 0, btn.Width, btn.Height));
                var rect = new Rectangle(0, 0, btn.Width - 1, btn.Height - 1);
                Color activeAccent = btn.Enabled ? accentColor : disabledColor;
                using (var path = GetRoundedRectPath(rect, ButtonRadius))
                {
                    using (var brush = new SolidBrush(currentBg))
                        g.FillPath(brush, path);
                    using (var pen = new Pen(activeAccent, 1))
                        g.DrawPath(pen, path);
                }
                TextRenderer.DrawText(g, btn.Text, btn.Font, rect, activeAccent,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            };

            btn.MouseEnter += (s, e) => { if (btn.Enabled) { currentBg = hoverBg; btn.Invalidate(); } };
            btn.MouseLeave += (s, e) => { if (btn.Enabled) { currentBg = AppTheme.BgWhite; btn.Invalidate(); } };
            btn.EnabledChanged += (s, e) => { currentBg = AppTheme.BgWhite; btn.Invalidate(); };
        }

        // Rounded "card" panel to replace a GroupBox - draws a subtle rounded
        // border, no embedded title cutout. Pair with a separate Label placed
        // above/inside for the card's heading.
        public static void ApplyCardStyle(this Panel panel, Color borderColor)
        {
            panel.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new Rectangle(0, 0, panel.Width - 1, panel.Height - 1);
                using (var path = GetRoundedRectPath(rect, CardRadius))
                using (var pen = new Pen(borderColor, 1))
                {
                    g.DrawPath(pen, path);
                }
            };
        }

        // Pill-shaped status badge, drawn directly into a DataGridView cell
        // via CellPainting. Caller supplies fill/text colors per status value.
        public static void PaintStatusPill(Graphics g, Rectangle cellBounds, string text, Color fillColor, Color textColor, Font font)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Size textSize = TextRenderer.MeasureText(text, font);
            int pillWidth = textSize.Width + 20;
            int pillHeight = Math.Min(cellBounds.Height - 8, 22);
            int x = cellBounds.X + (cellBounds.Width - pillWidth) / 2;
            int y = cellBounds.Y + (cellBounds.Height - pillHeight) / 2;
            var pillRect = new Rectangle(x, y, pillWidth, pillHeight);
            using (var path = GetRoundedRectPath(pillRect, PillRadius))
            using (var brush = new SolidBrush(fillColor))
            {
                g.FillPath(brush, path);
            }
            TextRenderer.DrawText(g, text, font, pillRect, textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // v7.5.6: matches the rounded outline button style used everywhere
        // else in v7, for a DataGridViewButtonColumn cell (used by the
        // per-row "Unmount" button). DataGridViewButtonColumn's own
        // rendering is system-drawn and can't be rounded via properties -
        // this fully replaces it via CellPainting, same technique as the
        // status pills.
        public static void PaintOutlineButtonCell(Graphics g, Rectangle cellBounds, string text, Color accentColor, Color bgColor, Font font)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = Rectangle.Inflate(cellBounds, -6, -6);
            using (var path = GetRoundedRectPath(rect, ButtonRadius))
            {
                using (var brush = new SolidBrush(bgColor))
                    g.FillPath(brush, path);
                using (var pen = new Pen(accentColor, 1))
                    g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, text, font, rect, accentColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private static Color LightenColor(Color color, float amount)
        {
            return Color.FromArgb(
                color.A,
                Math.Min(255, (int)(color.R + (255 - color.R) * amount)),
                Math.Min(255, (int)(color.G + (255 - color.G) * amount)),
                Math.Min(255, (int)(color.B + (255 - color.B) * amount))
            );
        }

        private static Color DarkenColor(Color color, float amount)
        {
            return Color.FromArgb(
                color.A,
                Math.Max(0, (int)(color.R * (1 - amount))),
                Math.Max(0, (int)(color.G * (1 - amount))),
                Math.Max(0, (int)(color.B * (1 - amount)))
            );
        }
    }

    // ========================================================================
    // ModernButtonExtensions - Adds hover/press/disabled states to buttons
    // ========================================================================
    public static class ModernButtonExtensions
    {
        public static void ApplyModernStyle(this Button btn, Color baseColor)
        {
            Color hoverColor = LightenColor(baseColor, 0.15f);
            Color pressColor = DarkenColor(baseColor, 0.10f);
            Color disabledColor = Color.FromArgb(200, 198, 196);
            Color disabledText = Color.FromArgb(150, 148, 146);
            
            btn.FlatStyle = FlatStyle.Flat;
            btn.FlatAppearance.BorderSize = 0;
            btn.BackColor = baseColor;
            btn.ForeColor = Color.White;
            btn.Cursor = Cursors.Hand;
            
            // Hover
            btn.MouseEnter += (s, e) => {
                if (btn.Enabled) btn.BackColor = hoverColor;
            };
            btn.MouseLeave += (s, e) => {
                if (btn.Enabled) btn.BackColor = baseColor;
            };
            
            // Press
            btn.MouseDown += (s, e) => {
                if (btn.Enabled && e.Button == MouseButtons.Left) btn.BackColor = pressColor;
            };
            btn.MouseUp += (s, e) => {
                if (!btn.Enabled) return;
                bool stillHovered = btn.ClientRectangle.Contains(btn.PointToClient(Cursor.Position));
                btn.BackColor = stillHovered ? hoverColor : baseColor;
            };
            
            // Disabled
            btn.EnabledChanged += (s, e) => {
                if (btn.Enabled)
                {
                    btn.BackColor = baseColor;
                    btn.ForeColor = Color.White;
                }
                else
                {
                    btn.BackColor = disabledColor;
                    btn.ForeColor = disabledText;
                }
            };
            
            if (!btn.Enabled)
            {
                btn.BackColor = disabledColor;
                btn.ForeColor = disabledText;
            }
        }

        // v6 "cheap tier": outline/ghost style for de-emphasized buttons -
        // white background, colored border+text, no fill. Used for anything
        // that isn't the single primary action of its screen (View Logs,
        // Tailscale/NetBird launch, Change Logo/Icon, Close, Test Connection,
        // Add Drive). Keeps AppTheme.Accent as the only filled/attention-
        // grabbing color per view instead of six competing solid colors.
        public static void ApplySecondaryStyle(this Button btn, Color accentColor)
        {
            Color hoverBg = LightenColor(accentColor, 0.92f);
            Color disabledBorder = Color.FromArgb(200, 198, 196);
            Color disabledText = Color.FromArgb(150, 148, 146);

            btn.FlatStyle = FlatStyle.Flat;
            btn.FlatAppearance.BorderSize = 1;
            btn.FlatAppearance.BorderColor = accentColor;
            btn.BackColor = Color.White;
            btn.ForeColor = accentColor;
            btn.Cursor = Cursors.Hand;

            btn.MouseEnter += (s, e) => {
                if (btn.Enabled) btn.BackColor = hoverBg;
            };
            btn.MouseLeave += (s, e) => {
                if (btn.Enabled) btn.BackColor = Color.White;
            };

            btn.EnabledChanged += (s, e) => {
                if (btn.Enabled)
                {
                    btn.BackColor = Color.White;
                    btn.ForeColor = accentColor;
                    btn.FlatAppearance.BorderColor = accentColor;
                }
                else
                {
                    btn.BackColor = Color.White;
                    btn.ForeColor = disabledText;
                    btn.FlatAppearance.BorderColor = disabledBorder;
                }
            };

            if (!btn.Enabled)
            {
                btn.ForeColor = disabledText;
                btn.FlatAppearance.BorderColor = disabledBorder;
            }
        }
        
        private static Color LightenColor(Color color, float amount)
        {
            return Color.FromArgb(
                color.A,
                Math.Min(255, (int)(color.R + (255 - color.R) * amount)),
                Math.Min(255, (int)(color.G + (255 - color.G) * amount)),
                Math.Min(255, (int)(color.B + (255 - color.B) * amount))
            );
        }
        
        private static Color DarkenColor(Color color, float amount)
        {
            return Color.FromArgb(
                color.A,
                Math.Max(0, (int)(color.R * (1 - amount))),
                Math.Max(0, (int)(color.G * (1 - amount))),
                Math.Max(0, (int)(color.B * (1 - amount)))
            );
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool createdNew;
            using (System.Threading.Mutex mutex = new System.Threading.Mutex(true, "FileserverDriveManagerMutex", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("Fileserver Drive Manager is already running.", "Already Running", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.Run(new MainForm());
            }
        }
    }
}

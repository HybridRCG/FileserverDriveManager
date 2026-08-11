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
using Microsoft.Win32;

namespace FileserverDriveManager
{
    public class DriveMapping
    {
        public string DriveLetter { get; set; }
        public string ShareName { get; set; }
        public string Status { get; set; }
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
        private TextBox usernameBox;
        private TextBox passwordBox;
        private ComboBox driveLetterBox;
        private ComboBox shareNameBox;
        private Button authenticateButton;
        private DataGridView drivesGrid;
        private Button addDriveButton;
        private Button mountDrivesButton;
        private Button settingsButton;
        private Button viewLogsButton;
        private Button tailscaleButton;
        private Button netbirdButton;
        private Button exitButton;
        private Label statusLabel;
        private Label tailscaleIPLabel;
        private Label netbirdIPLabel;
        private Label lanIPLabel;
        private NotifyIcon notifyIcon;
        private PictureBox logoPicture;
        private bool isExiting = false;
        private bool isAuthenticating = false;
        private bool autoMountOnStartup = true;
        private bool darkModeEnabled = false;
        private bool isCheckingFailover = false;
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
        private System.Windows.Forms.Timer statusTimer;

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

        private void EnableAutoStartup()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key != null)
                    {
                        object existingValue = key.GetValue("FileserverDriveManager");
                        if (existingValue == null)
                        {
                            string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                            string registryValue = $"\"{exePath}\"";
                            key.SetValue("FileserverDriveManager", registryValue);
                            Log($"Auto-startup enabled with path: {registryValue}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Error enabling auto-startup: " + ex.Message);
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
            
            Log("Auto-mount enabled - checking VPN connection...");
            
            string vpnIP = GetVPNIP();
            if (string.IsNullOrEmpty(vpnIP) || vpnIP.Contains("Not Connected"))
            {
                Log("VPN IP not found (checked Tailscale and NetBird) - VPN may need manual start");
                // Don't auto-launch VPN - let user start it manually
                // This prevents interfering with Windows network initialization
                // Start network status timer now that startup is complete
                statusTimer.Start();
                return;
            }
            else
            {
                Log($"VPN IP already found: {vpnIP}");
            }
            
            if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
            {
                Log("Auto-authenticating with saved credentials...");
                try
                {
                    // v7.3: race the same LAN/Tailscale/NetBird candidates the
                    // manual Authenticate button uses, rather than mounting
                    // against whatever fileserverIP happens to hold from a
                    // previous session (which could be stale if the network
                    // situation changed between launches).
                    var raceResults = await RaceFileserverIPs();
                    var fastest = raceResults.FirstOrDefault(r => r.Success);
                    if (fastest.IP == null)
                    {
                        Log("Auto-connect: fileserver not reachable on LAN, Tailscale, or NetBird");
                        statusLabel.Text = "Fileserver not reachable - VPN may need manual start";
                        statusTimer.Start();
                        return;
                    }
                    fileserverIP = fastest.IP;
                    Log($"Auto-connect using {fastest.Label} ({fastest.IP}, {fastest.ElapsedMs}ms)");

                    if (await TestFileserverConnection(username, password))
                    {
                        statusLabel.Text = "Auto-authenticated on startup";
                        Log("Auto-authentication successful");
                        
                        Log("Auto-mounting drives on startup...");
                        await MountAllDrives();
                        
                        await Task.Delay(3000);
                        
                        Log("All drives mounted - staying minimized in tray");
                        // Start network status timer now that startup is complete
                        statusTimer.Start();
                        return;
                    }
                    else
                    {
                        Log("Auto-authentication failed");
                        // Start network status timer now that startup is complete
                        statusTimer.Start();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Log("Auto-authentication error: " + ex.Message);
                    // Start network status timer now that startup is complete
                    statusTimer.Start();
                    return;
                }
            }
            else
            {
                Log("No saved credentials for auto-mount");
                // Start network status timer now that startup is complete
                statusTimer.Start();
                return;
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
                string faviconPath = null;
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
            notifyIcon.ContextMenuStrip = new ContextMenuStrip();
            notifyIcon.ContextMenuStrip.Items.Add("Show", null, (s, e) => ShowFromTray());
            notifyIcon.ContextMenuStrip.Items.Add("Exit", null, (s, e) => Application.Exit());
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
            usernameBox = new TextBox() { Dock = DockStyle.Fill, Font = modernFont, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 4, 8, 4) };
            
            Label passwordLabel = new Label() { Text = "Password:", TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Fill, Padding = new Padding(0, 0, 10, 0), Font = modernFont, ForeColor = textPrimary };
            passwordBox = new TextBox() { PasswordChar = '●', Dock = DockStyle.Fill, Font = modernFont, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 4, 8, 4) };
            
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
                string logoPath = null;
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
            driveLetterBox = new ComboBox() { Width = 70, Font = modernFont, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 4, 20, 0), Enabled = false, FlatStyle = FlatStyle.Standard };
            
            Label shareNameLabel = new Label() { Text = "Share Name:", AutoSize = true, Font = modernFont, Margin = new Padding(0, 8, 8, 0), ForeColor = textPrimary };
            shareNameBox = new ComboBox() { Width = 280, Font = modernFont, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 4, 20, 0), Enabled = false, FlatStyle = FlatStyle.Standard };
            
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
                if (e.RowIndex < 0 || drivesGrid.Columns[e.ColumnIndex].DataPropertyName != "Status" || e.Value == null)
                    return;
                e.PaintBackground(e.CellBounds, true);
                string status = e.Value.ToString();
                (Color bg, Color fg) = status switch
                {
                    "Mounted" => (AppTheme.SuccessBg, AppTheme.SuccessText),
                    "Failed" or "Error" or "Timeout" => (AppTheme.DangerBg, AppTheme.DangerText),
                    _ => (AppTheme.MutedBg, AppTheme.MutedText)
                };
                RoundedRenderer.PaintStatusPill(e.Graphics, e.CellBounds, status, bg, fg, modernFontBold);
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
            TableLayoutPanel buttonPanel = new TableLayoutPanel() { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 1, Margin = new Padding(0, 0, 0, 8) };
            for (int i = 0; i < 6; i++)
                buttonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f/6f));

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
            
            viewLogsButton = new Button() { 
                Text = "View Logs", 
                Dock = DockStyle.Fill, 
                BackColor = bgLight, 
                ForeColor = textPrimary, 
                Font = modernFontBold,
                Margin = new Padding(2, 0, 2, 0), 
                Cursor = Cursors.Hand, 
                FlatStyle = FlatStyle.Flat 
            };
            viewLogsButton.FlatAppearance.BorderColor = borderGray;
            viewLogsButton.ApplyRoundedOutlineStyle(AppTheme.Accent);
            viewLogsButton.Click += ViewLogsButton_Click;
            
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
            exitButton.Click += (s, e) => { isExiting = true; this.Close(); };

            buttonPanel.Controls.Add(mountDrivesButton, 0, 0);
            buttonPanel.Controls.Add(settingsButton, 1, 0);
            buttonPanel.Controls.Add(viewLogsButton, 2, 0);
            buttonPanel.Controls.Add(tailscaleButton, 3, 0);
            buttonPanel.Controls.Add(netbirdButton, 4, 0);
            buttonPanel.Controls.Add(exitButton, 5, 0);
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
                // v7.5.0: live failover check - only does real work when
                // authenticated with drives active, and only switches when
                // the current provider genuinely stops responding.
                await CheckFileserverFailover();
            };
            // Timer will be started AFTER CheckAndAutoConnect completes
        }

        private async void AuthenticateButton_Click(object sender, EventArgs e)
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
            var fastest = raceResults.FirstOrDefault(r => r.Success);
            if (fastest.IP == null)
            {
                statusLabel.Text = "Fileserver not reachable on LAN, Tailscale, or NetBird";
                authenticateButton.Enabled = true;
                isAuthenticating = false;
                return;
            }
            fileserverIP = fastest.IP;
            Log($"Authenticate using {fastest.Label} ({fastest.IP}, {fastest.ElapsedMs}ms)");
            statusLabel.Text = $"Using {fastest.Label} ({fastest.IP}, {fastest.ElapsedMs}ms) - Authenticating...";

            try
            {
                if (!await TestFileserverConnection(username, password))
                {
                    statusLabel.Text = "Authentication failed";
                    authenticateButton.Enabled = true;
                    isAuthenticating = false;
                    return;
                }

                statusLabel.Text = $"Connected via {fastest.Label} - Loading shares...";

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

                statusLabel.Text = $"Ready via {fastest.Label} - {shareNameBox.Items.Count} shares available";
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

        private void AddDriveButton_Click(object sender, EventArgs e)
        {
            if (driveLetterBox.SelectedItem == null || shareNameBox.SelectedItem == null)
            {
                statusLabel.Text = "Please select drive letter and share name";
                return;
            }

            string driveLetter = driveLetterBox.SelectedItem.ToString();
            string shareName = shareNameBox.SelectedItem.ToString();

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

        private async void MountDrivesButton_Click(object sender, EventArgs e)
        {
            await MountAllDrives();
        }

        private void SettingsButton_Click(object sender, EventArgs e)
        {
            Form settingsForm = new Form();
            settingsForm.Text = "Settings";
            settingsForm.Size = new Size(600, 580);
            settingsForm.StartPosition = FormStartPosition.CenterParent;
            settingsForm.FormBorderStyle = FormBorderStyle.FixedDialog;
            settingsForm.MaximizeBox = false;
            settingsForm.MinimizeBox = false;
            settingsForm.BackColor = AppTheme.BgLight;
            settingsForm.ForeColor = AppTheme.TextPrimary;

            TableLayoutPanel mainLayout = new TableLayoutPanel() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
            // v7 fix: was 160 - too tight once the card header Label (24px)
            // and its top padding (8px) were added on top of what GroupBox
            // used to absorb more compactly via its built-in title, which cut
            // off the auto-mount checkbox row at the bottom of the card.
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 315));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            // Network Settings Section
            // v7 "full tier": Panel with a rounded custom-drawn border + a
            // separate header Label, replacing GroupBox's boxy embedded-title
            // border. Padding leaves room for the rounded corners/border.
            Panel networkBox = new Panel() { Dock = DockStyle.Fill, Padding = new Padding(14, 12, 14, 14), BackColor = AppTheme.BgWhite };
            networkBox.ApplyCardStyle(AppTheme.BorderGray);
            Label networkHeader = new Label() { Text = "Network settings", Dock = DockStyle.Top, Height = 24, Font = new Font("Segoe UI", 10F, FontStyle.Bold), ForeColor = AppTheme.TextPrimary };
            TableLayoutPanel networkLayout = new TableLayoutPanel() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6, Padding = new Padding(0, 8, 0, 0) };
            networkLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            networkLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            for (int i = 0; i < 6; i++)
                networkLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

            // v7.3: three candidate IPs instead of one - LAN, Tailscale, and
            // NetBird. Authenticate races all three (see RaceFileserverIPs)
            // and uses whichever responds fastest, rather than a fixed IP.
            Label lanIPFieldLabel = new Label() { Text = "LAN IP:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            TextBox lanIPBox = new TextBox() { Text = fileserverLanIP, Dock = DockStyle.Fill };
            networkLayout.Controls.Add(lanIPFieldLabel, 0, 0);
            networkLayout.Controls.Add(lanIPBox, 1, 0);

            Label tailscaleIPFieldLabel = new Label() { Text = "Tailscale IP:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            TextBox tailscaleIPFieldBox = new TextBox() { Text = fileserverTailscaleIP, Dock = DockStyle.Fill };
            networkLayout.Controls.Add(tailscaleIPFieldLabel, 0, 1);
            networkLayout.Controls.Add(tailscaleIPFieldBox, 1, 1);

            Label netbirdIPFieldLabel = new Label() { Text = "NetBird IP:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            TextBox netbirdIPFieldBox = new TextBox() { Text = fileserverNetbirdIP, Dock = DockStyle.Fill };
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

            Button saveIPButton = new Button() { Text = "Save IP", Dock = DockStyle.Fill, Cursor = Cursors.Hand, FlatStyle = FlatStyle.Flat };
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

            networkBox.Controls.Add(networkLayout);
            networkBox.Controls.Add(networkHeader);
            mainLayout.Controls.Add(networkBox, 0, 0);

            // Branding Buttons
            TableLayoutPanel brandingPanel = new TableLayoutPanel() { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(0) };
            brandingPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            brandingPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            brandingPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));

            Button logoButton = new Button() { Text = "Change Logo", Dock = DockStyle.Fill, Cursor = Cursors.Hand, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 5, 0) };
            logoButton.FlatAppearance.BorderSize = 1;
            logoButton.ApplyRoundedOutlineStyle(AppTheme.Accent);
            logoButton.Click += LogoPicture_Click;

            Button iconButton = new Button() { Text = "Change Icon", Dock = DockStyle.Fill, Cursor = Cursors.Hand, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 5, 0) };
            iconButton.FlatAppearance.BorderSize = 1;
            iconButton.ApplyRoundedOutlineStyle(AppTheme.Accent);
            iconButton.Click += FaviconButton_Click;

            Button closeButton = new Button() { Text = "Close", Dock = DockStyle.Fill, Cursor = Cursors.Hand, FlatStyle = FlatStyle.Flat, Margin = new Padding(0) };
            closeButton.FlatAppearance.BorderSize = 1;
            closeButton.ApplyRoundedOutlineStyle(AppTheme.TextSecondary);
            closeButton.Click += (s, ev) => settingsForm.Close();

            brandingPanel.Controls.Add(logoButton, 0, 0);
            brandingPanel.Controls.Add(iconButton, 1, 0);
            brandingPanel.Controls.Add(closeButton, 2, 0);
            mainLayout.Controls.Add(brandingPanel, 0, 1);

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
            mainLayout.Controls.Add(infoBox, 0, 2);

            settingsForm.Controls.Add(mainLayout);
            settingsForm.ShowDialog();
        }

        private void ViewLogsButton_Click(object sender, EventArgs e)
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

        private void TailscaleButton_Click(object sender, EventArgs e)
        {
            LaunchTailscale();
        }

        private void NetBirdButton_Click(object sender, EventArgs e)
        {
            LaunchNetBird();
        }

        private void LogoPicture_Click(object sender, EventArgs e)
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

        private void FaviconButton_Click(object sender, EventArgs e)
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

            isCheckingFailover = true;
            try
            {
                bool stillReachable = await Task.Run(() => TestFileserverReachability(fileserverIP));
                if (stillReachable) return;

                Log($"Failover: current provider ({fileserverIP}) no longer reachable - re-racing...");
                var raceResults = await RaceFileserverIPs();
                var fastest = raceResults.FirstOrDefault(r => r.Success);
                if (fastest.IP == null)
                {
                    Log("Failover: no configured provider is currently reachable");
                    statusLabel.Text = "Fileserver unreachable on all configured paths";
                    return;
                }
                if (fastest.IP == fileserverIP) return; // shouldn't normally happen, but guard anyway

                string oldIP = fileserverIP;
                Log($"Failover: switching from {oldIP} to {fastest.Label} ({fastest.IP}, {fastest.ElapsedMs}ms)");
                statusLabel.Text = $"Connection lost - switching to {fastest.Label}...";

                UnmountAllDrivesQuiet();
                fileserverIP = fastest.IP;
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

            int success = 0;
            int failed = 0;

            foreach (var drive in drives)
            {
                statusLabel.Text = $"Mounting {drive.DriveLetter}...";
                statusLabel.Refresh();

                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = "net",
                        Arguments = $"use {drive.DriveLetter} \\\\{fileserverIP}\\{drive.ShareName} /user:{username} {password} /persistent:yes",
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

                    if (settings.ContainsKey("username") && settings["username"].ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        username = settings["username"].GetString();
                        usernameBox.Text = username;
                    }

                    if (settings.ContainsKey("password") && settings["password"].ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        password = DecryptPassword(settings["password"].GetString());
                        passwordBox.Text = password;
                    }

                    if (settings.ContainsKey("fileserverIP") && settings["fileserverIP"].ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        fileserverIP = settings["fileserverIP"].GetString();
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
                        fileserverLanIP = settings["fileserverLanIP"].GetString();
                    }
                    if (settings.ContainsKey("fileserverTailscaleIP") && settings["fileserverTailscaleIP"].ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        fileserverTailscaleIP = settings["fileserverTailscaleIP"].GetString();
                    }
                    if (settings.ContainsKey("fileserverNetbirdIP") && settings["fileserverNetbirdIP"].ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        fileserverNetbirdIP = settings["fileserverNetbirdIP"].GetString();
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

                    if (settings.ContainsKey("drives"))
                    {
                        var drivesJson = settings["drives"].GetRawText();
                        drives = System.Text.Json.JsonSerializer.Deserialize<List<DriveMapping>>(drivesJson);
                        drivesGrid.DataSource = drives;
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
            Color disabledFill = Color.FromArgb(220, 218, 216);
            Color disabledText = Color.FromArgb(150, 148, 146);
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
            Color disabledColor = Color.FromArgb(180, 178, 176);
            Color currentBg = Color.White;

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
            btn.MouseLeave += (s, e) => { if (btn.Enabled) { currentBg = Color.White; btn.Invalidate(); } };
            btn.EnabledChanged += (s, e) => { currentBg = Color.White; btn.Invalidate(); };
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

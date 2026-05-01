using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Drawing;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace FileserverDriveManager
{
    public class DriveMapping
    {
        public string DriveLetter { get; set; }
        public string ShareName { get; set; }
        public string Status { get; set; }
    }

    public partial class MainForm : Form
    {
        private const string APP_VERSION = "v3.8";
        
        private List<DriveMapping> drives = new List<DriveMapping>();
        private TextBox usernameBox;
        private TextBox passwordBox;
        private ComboBox driveLetterBox;
        private ComboBox shareNameBox;
        private Button authenticateButton;
        private DataGridView drivesGrid;
        private Button addDriveButton;
        private Button removeDriveButton;
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
        private bool isAuthenticated = false;
        private string username = "";
        private string password = "";
        private string fileserverIP = "192.168.1.26";
        private string selectedVPNProvider = "tailscale"; // "tailscale" or "netbird"
        private string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileserverDriveManager.log");
        private System.Windows.Forms.Timer statusTimer;

        public MainForm()
        {
            InitializeComponents();
            LoadDrives();
            RefreshStatus();
            
            // Auto-enable startup on first run
            this.Load += (s, e) => 
            {
                EnableAutoStartup();
                CheckAndAutoConnect();
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

        private void CheckAndAutoConnect()
        {
            // Wait 10 seconds for Windows network to fully initialize
            System.Threading.Thread.Sleep(10000);
            
            this.WindowState = FormWindowState.Minimized;
            this.ShowInTaskbar = false;
            notifyIcon.Visible = true;
            
            if (!autoMountOnStartup)
            {
                Log("Auto-mount disabled - staying minimized");
                // Start network status timer now that startup is complete
                statusTimer.Start();
                return;
            }
            
            Log("Auto-mount enabled - checking VPN connection...");
            
            string vpnIP = GetVPNIP();
            if (string.IsNullOrEmpty(vpnIP) || vpnIP.Contains("Not Connected"))
            {
                Log($"{selectedVPNProvider} IP not found - VPN may need manual start");
                // Don't auto-launch VPN - let user start it manually
                // This prevents interfering with Windows network initialization
                // Start network status timer now that startup is complete
                statusTimer.Start();
                return;
            }
            else
            {
                Log($"{selectedVPNProvider} IP already found: {vpnIP}");
            }
            
            if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
            {
                Log("Auto-authenticating with saved credentials...");
                try
                {
                    if (TestFileserverConnection(username, password))
                    {
                        isAuthenticated = true;
                        statusLabel.Text = "Auto-authenticated on startup";
                        Log("Auto-authentication successful");
                        
                        Log("Auto-mounting drives on startup...");
                        MountAllDrives();
                        
                        System.Threading.Thread.Sleep(3000);
                        
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
            this.Text = $"Dyna Training - Fileserver Drive Manager {APP_VERSION}";
            this.Size = new Size(900, 520);
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.MaximizeBox = true;
            this.MinimizeBox = true;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.WindowState = FormWindowState.Normal;
            
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

            TableLayoutPanel mainLayout = new TableLayoutPanel() { Dock = DockStyle.Fill };
            mainLayout.ColumnCount = 1;
            mainLayout.RowCount = 5;
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));

            // ===== CREDENTIALS & LOGO SECTION =====
            TableLayoutPanel credLogoTable = new TableLayoutPanel() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(10, 5, 10, 5), AutoSize = false };
            credLogoTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            credLogoTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
            credLogoTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            
            // Left: Credentials
            TableLayoutPanel credStackTable = new TableLayoutPanel() { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2 };
            credStackTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            credStackTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            credStackTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            credStackTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            credStackTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            
            Label usernameLabel = new Label() { Text = "Username:", TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Fill, Padding = new Padding(0, 0, 5, 0), AutoSize = false, Font = new Font("Arial", 10) };
            usernameBox = new TextBox() { Text = "", Multiline = false, AutoSize = false, BorderStyle = BorderStyle.Fixed3D, Dock = DockStyle.Fill, Font = new Font("Arial", 10) };
            
            Label passwordLabel = new Label() { Text = "Password:", TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Fill, Padding = new Padding(0, 0, 5, 0), AutoSize = false, Font = new Font("Arial", 10) };
            passwordBox = new TextBox() { PasswordChar = '*', Multiline = false, AutoSize = false, BorderStyle = BorderStyle.Fixed3D, Dock = DockStyle.Fill, Font = new Font("Arial", 10) };
            
            authenticateButton = new Button() { Text = "Authenticate", BackColor = Color.FromArgb(76, 175, 80), ForeColor = Color.White, Dock = DockStyle.Fill, Font = new Font("Arial", 10), Margin = new Padding(5, 2, 0, 2), Cursor = Cursors.Hand, FlatStyle = FlatStyle.Flat };
            authenticateButton.Click += AuthenticateButton_Click;
            
            credStackTable.Controls.Add(usernameLabel, 0, 0);
            credStackTable.Controls.Add(usernameBox, 1, 0);
            credStackTable.Controls.Add(authenticateButton, 2, 0);
            credStackTable.Controls.Add(passwordLabel, 0, 1);
            credStackTable.Controls.Add(passwordBox, 1, 1);
            credStackTable.SetRowSpan(authenticateButton, 2);
            
            // Right: Logo
            Panel logoPanel = new Panel() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(240, 240, 240), BorderStyle = BorderStyle.None };
            logoPicture = new PictureBox() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(240, 240, 240) };
            
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
                logoPicture.BackColor = Color.Red;
                logoPicture.Text = "Error: " + ex.Message;
            }
            
            logoPanel.Controls.Add(logoPicture);
            
            credLogoTable.Controls.Add(credStackTable, 0, 0);
            credLogoTable.Controls.Add(logoPanel, 1, 0);
            
            GroupBox credLogoBox = new GroupBox() { Text = "", Dock = DockStyle.Fill, Margin = new Padding(10, 5, 10, 5) };
            credLogoBox.Controls.Add(credLogoTable);
            mainLayout.Controls.Add(credLogoBox, 0, 0);

            // ===== ADD DRIVE SECTION =====
            GroupBox addDriveBox = new GroupBox() { Text = "", Dock = DockStyle.Fill, Margin = new Padding(10, 5, 10, 5) };
            FlowLayoutPanel addFlow = new FlowLayoutPanel() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoScroll = true };

            Label driveLetterLabel = new Label() { Text = "Drive Letter:", AutoSize = true, Font = new Font("Arial", 10), Margin = new Padding(0, 5, 5, 0) };
            driveLetterBox = new ComboBox() { Width = 60, Height = 22, Font = new Font("Arial", 10), DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 2, 20, 0), Enabled = false };
            
            Label shareNameLabel = new Label() { Text = "Share Name:", AutoSize = true, Font = new Font("Arial", 10), Margin = new Padding(0, 5, 5, 0) };
            shareNameBox = new ComboBox() { Width = 250, Height = 22, Font = new Font("Arial", 10), DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 2, 20, 0), Enabled = false };
            
            addDriveButton = new Button() { Text = "Add", BackColor = Color.FromArgb(150, 150, 150), ForeColor = Color.White, Width = 70, Height = 28, Font = new Font("Arial", 10), Margin = new Padding(0, 0, 10, 0), Cursor = Cursors.Hand, FlatStyle = FlatStyle.Flat, Enabled = false };
            addDriveButton.Click += AddDriveButton_Click;
            removeDriveButton = new Button() { Text = "Remove", BackColor = Color.FromArgb(150, 150, 150), ForeColor = Color.White, Width = 90, Height = 28, Font = new Font("Arial", 10), Margin = new Padding(0, 0, 0, 0), Cursor = Cursors.Hand, FlatStyle = FlatStyle.Flat, Enabled = false };
            removeDriveButton.Click += RemoveDriveButton_Click;

            addFlow.Controls.Add(driveLetterLabel);
            addFlow.Controls.Add(driveLetterBox);
            addFlow.Controls.Add(shareNameLabel);
            addFlow.Controls.Add(shareNameBox);
            addFlow.Controls.Add(addDriveButton);
            addFlow.Controls.Add(removeDriveButton);
            addDriveBox.Controls.Add(addFlow);
            mainLayout.Controls.Add(addDriveBox, 0, 1);

            // ===== DRIVES GRID =====
            GroupBox gridBox = new GroupBox() { Text = "", Dock = DockStyle.Fill, Margin = new Padding(10, 5, 10, 5) };
            drivesGrid = new DataGridView() { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false, ReadOnly = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect, RowHeadersVisible = false, Height = 150 };
            drivesGrid.Columns.Add(new DataGridViewTextBoxColumn() { HeaderText = "Drive", DataPropertyName = "DriveLetter", Width = 80, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
            drivesGrid.Columns.Add(new DataGridViewTextBoxColumn() { HeaderText = "Share", DataPropertyName = "ShareName", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            drivesGrid.Columns.Add(new DataGridViewTextBoxColumn() { HeaderText = "Status", DataPropertyName = "Status", Width = 100, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
            gridBox.Controls.Add(drivesGrid);
            mainLayout.Controls.Add(gridBox, 0, 2);

            // ===== ACTION BUTTONS =====
            TableLayoutPanel buttonPanel = new TableLayoutPanel() { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 1, Margin = new Padding(10, 5, 10, 5) };
            buttonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100/6));
            buttonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100/6));
            buttonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100/6));
            buttonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100/6));
            buttonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100/6));
            buttonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100/6));

            mountDrivesButton = new Button() { Text = "Mount Drives", Dock = DockStyle.Fill, BackColor = Color.FromArgb(150, 150, 150), ForeColor = Color.White, Margin = new Padding(2), Enabled = false, Cursor = Cursors.Hand, FlatStyle = FlatStyle.Flat };
            mountDrivesButton.Click += MountDrivesButton_Click;
            settingsButton = new Button() { Text = "Settings", Dock = DockStyle.Fill, BackColor = Color.FromArgb(33, 150, 243), ForeColor = Color.White, Margin = new Padding(2), Cursor = Cursors.Hand, FlatStyle = FlatStyle.Flat };
            settingsButton.Click += SettingsButton_Click;
            viewLogsButton = new Button() { Text = "Logs", Dock = DockStyle.Fill, BackColor = Color.FromArgb(240, 240, 240), ForeColor = Color.Black, Margin = new Padding(2), Cursor = Cursors.Hand, FlatStyle = FlatStyle.Flat };
            viewLogsButton.Click += ViewLogsButton_Click;
            tailscaleButton = new Button() { Text = "Tailscale", Dock = DockStyle.Fill, BackColor = Color.FromArgb(240, 240, 240), ForeColor = Color.Black, Margin = new Padding(2), Cursor = Cursors.Hand, FlatStyle = FlatStyle.Flat };
            tailscaleButton.Click += TailscaleButton_Click;
            netbirdButton = new Button() { Text = "NetBird", Dock = DockStyle.Fill, BackColor = Color.FromArgb(240, 240, 240), ForeColor = Color.Black, Margin = new Padding(2), Cursor = Cursors.Hand, FlatStyle = FlatStyle.Flat };
            netbirdButton.Click += NetBirdButton_Click;
            exitButton = new Button() { Text = "Exit", Dock = DockStyle.Fill, BackColor = Color.FromArgb(229, 57, 53), ForeColor = Color.White, Margin = new Padding(2), Cursor = Cursors.Hand, FlatStyle = FlatStyle.Flat };
            exitButton.Click += (s, e) => { isExiting = true; this.Close(); };

            buttonPanel.Controls.Add(mountDrivesButton, 0, 0);
            buttonPanel.Controls.Add(settingsButton, 1, 0);
            buttonPanel.Controls.Add(viewLogsButton, 2, 0);
            buttonPanel.Controls.Add(tailscaleButton, 3, 0);
            buttonPanel.Controls.Add(netbirdButton, 4, 0);
            buttonPanel.Controls.Add(exitButton, 5, 0);
            mainLayout.Controls.Add(buttonPanel, 0, 3);

            // ===== STATUS BAR =====
            TableLayoutPanel statusPanel = new TableLayoutPanel() { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1 };
            statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            statusPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            
            statusLabel = new Label() { Dock = DockStyle.Fill, Text = "Ready", BorderStyle = BorderStyle.None, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0), BackColor = Color.FromArgb(240, 240, 240), ForeColor = Color.FromArgb(21, 101, 200), Margin = new Padding(0) };
            lanIPLabel = new Label() { Dock = DockStyle.Fill, Text = "Network: Detecting...", BorderStyle = BorderStyle.None, TextAlign = ContentAlignment.MiddleCenter, Padding = new Padding(0), BackColor = Color.FromArgb(240, 240, 240), ForeColor = Color.Gray, Font = new Font("Arial", 8) };
            tailscaleIPLabel = new Label() { Dock = DockStyle.Fill, Text = "Tailscale: Not Connected", BorderStyle = BorderStyle.None, TextAlign = ContentAlignment.MiddleCenter, Padding = new Padding(0), BackColor = Color.FromArgb(240, 240, 240), ForeColor = Color.Gray, Font = new Font("Arial", 8) };
            netbirdIPLabel = new Label() { Dock = DockStyle.Fill, Text = "NetBird: Not Connected", BorderStyle = BorderStyle.None, TextAlign = ContentAlignment.MiddleCenter, Padding = new Padding(0), BackColor = Color.FromArgb(240, 240, 240), ForeColor = Color.Gray, Font = new Font("Arial", 8) };
            Label versionLabel = new Label() { Dock = DockStyle.Fill, Text = APP_VERSION, BorderStyle = BorderStyle.None, TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 10, 0), BackColor = Color.FromArgb(240, 240, 240), ForeColor = Color.Gray, Font = new Font("Arial", 9) };
            
            statusPanel.Controls.Add(statusLabel, 0, 0);
            statusPanel.Controls.Add(lanIPLabel, 1, 0);
            statusPanel.Controls.Add(tailscaleIPLabel, 2, 0);
            statusPanel.Controls.Add(netbirdIPLabel, 3, 0);
            statusPanel.Controls.Add(versionLabel, 4, 0);
            mainLayout.Controls.Add(statusPanel, 0, 4);

            this.Controls.Add(mainLayout);
            
            // Update status periodically - but NOT during startup!
            statusTimer = new System.Windows.Forms.Timer();
            statusTimer.Interval = 5000;
            statusTimer.Tick += (s, e) => UpdateNetworkStatus();
            // Timer will be started AFTER CheckAndAutoConnect completes
        }

        private void AuthenticateButton_Click(object sender, EventArgs e)
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
            statusLabel.Text = "Authenticating...";

            try
            {
                if (!TestFileserverConnection(username, password))
                {
                    statusLabel.Text = "Authentication failed";
                    authenticateButton.Enabled = true;
                    isAuthenticating = false;
                    return;
                }

                statusLabel.Text = "Authentication successful - Loading shares...";
                isAuthenticated = true;

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
                addDriveButton.Enabled = true;
                addDriveButton.BackColor = Color.FromArgb(93, 156, 236);
                removeDriveButton.Enabled = true;
                removeDriveButton.BackColor = Color.FromArgb(211, 47, 47);
                mountDrivesButton.Enabled = true;
                mountDrivesButton.BackColor = Color.FromArgb(33, 150, 243);

                statusLabel.Text = $"Ready - {shareNameBox.Items.Count} shares available";
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

        private void RemoveDriveButton_Click(object sender, EventArgs e)
        {
            if (drivesGrid.SelectedRows.Count == 0)
            {
                statusLabel.Text = "Please select a drive to remove";
                return;
            }

            DriveMapping selected = (DriveMapping)drivesGrid.SelectedRows[0].DataBoundItem;
            drives.Remove(selected);
            drivesGrid.DataSource = null;
            drivesGrid.DataSource = drives;

            PopulateAvailableDriveLetters();
            SaveCurrentSettings();
            statusLabel.Text = $"Removed {selected.DriveLetter}";
        }

        private void MountDrivesButton_Click(object sender, EventArgs e)
        {
            MountAllDrives();
        }

        private void SettingsButton_Click(object sender, EventArgs e)
        {
            Form settingsForm = new Form();
            settingsForm.Text = "Settings";
            settingsForm.Size = new Size(600, 520);
            settingsForm.StartPosition = FormStartPosition.CenterParent;
            settingsForm.FormBorderStyle = FormBorderStyle.FixedDialog;
            settingsForm.MaximizeBox = false;
            settingsForm.MinimizeBox = false;

            TableLayoutPanel mainLayout = new TableLayoutPanel() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(10) };
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 160));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            // Network Settings Section
            GroupBox networkBox = new GroupBox() { Text = "Network Settings", Dock = DockStyle.Fill };
            TableLayoutPanel networkLayout = new TableLayoutPanel() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(10) };
            networkLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            networkLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            networkLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            networkLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            networkLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

            Label ipLabel = new Label() { Text = "Fileserver IP:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            TextBox ipBox = new TextBox() { Text = fileserverIP, Dock = DockStyle.Fill };
            networkLayout.Controls.Add(ipLabel, 0, 0);
            networkLayout.Controls.Add(ipBox, 1, 0);

            Button testButton = new Button() { Text = "Test Connection", Dock = DockStyle.Fill, BackColor = Color.FromArgb(33, 150, 243), ForeColor = Color.White, Cursor = Cursors.Hand, FlatStyle = FlatStyle.Flat };
            testButton.Click += (s, ev) =>
            {
                if (TestFileserverConnection(username, password))
                {
                    MessageBox.Show("Connection successful!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show("Connection failed!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };

            Button saveIPButton = new Button() { Text = "Save IP", Dock = DockStyle.Fill, BackColor = Color.FromArgb(76, 175, 80), ForeColor = Color.White, Cursor = Cursors.Hand, FlatStyle = FlatStyle.Flat };
            saveIPButton.Click += (s, ev) =>
            {
                fileserverIP = ipBox.Text.Trim();
                SaveCurrentSettings();
                MessageBox.Show("IP saved!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            networkLayout.Controls.Add(testButton, 0, 1);
            networkLayout.Controls.Add(saveIPButton, 1, 1);

            CheckBox autoMountCheckbox = new CheckBox() { Text = "Auto-mount drives when VPN IP is active", Dock = DockStyle.Fill, Checked = autoMountOnStartup };
            autoMountCheckbox.CheckedChanged += (s, ev) =>
            {
                autoMountOnStartup = autoMountCheckbox.Checked;
                SaveCurrentSettings();
            };
            networkLayout.SetColumnSpan(autoMountCheckbox, 2);
            networkLayout.Controls.Add(autoMountCheckbox, 0, 2);

            networkBox.Controls.Add(networkLayout);
            mainLayout.Controls.Add(networkBox, 0, 0);

            // VPN Provider Section
            GroupBox vpnBox = new GroupBox() { Text = "VPN Provider", Dock = DockStyle.Fill };
            TableLayoutPanel vpnLayout = new TableLayoutPanel() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(10) };
            vpnLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            vpnLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            vpnLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            vpnLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

            RadioButton tailscaleRadio = new RadioButton() { Text = "Tailscale", Dock = DockStyle.Fill, Checked = (selectedVPNProvider == "tailscale") };
            RadioButton netbirdRadio = new RadioButton() { Text = "NetBird", Dock = DockStyle.Fill, Checked = (selectedVPNProvider == "netbird") };

            tailscaleRadio.CheckedChanged += (s, ev) =>
            {
                if (tailscaleRadio.Checked)
                {
                    selectedVPNProvider = "tailscale";
                    SaveCurrentSettings();
                    UpdateNetworkStatus();
                }
            };

            netbirdRadio.CheckedChanged += (s, ev) =>
            {
                if (netbirdRadio.Checked)
                {
                    selectedVPNProvider = "netbird";
                    SaveCurrentSettings();
                    UpdateNetworkStatus();
                }
            };

            Button installTailscaleButton = new Button() { Text = "Install Tailscale", Dock = DockStyle.Fill, BackColor = Color.FromArgb(93, 156, 236), ForeColor = Color.White, Cursor = Cursors.Hand, FlatStyle = FlatStyle.Flat };
            installTailscaleButton.Click += (s, ev) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "https://tailscale.com/download",
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error opening Tailscale download page: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };

            Button installNetBirdButton = new Button() { Text = "Install NetBird", Dock = DockStyle.Fill, BackColor = Color.FromArgb(93, 156, 236), ForeColor = Color.White, Cursor = Cursors.Hand, FlatStyle = FlatStyle.Flat };
            installNetBirdButton.Click += (s, ev) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "https://pkgs.netbird.io/windows/x64",
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error opening NetBird download page: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };

            vpnLayout.Controls.Add(tailscaleRadio, 0, 0);
            vpnLayout.Controls.Add(installTailscaleButton, 1, 0);
            vpnLayout.Controls.Add(netbirdRadio, 0, 1);
            vpnLayout.Controls.Add(installNetBirdButton, 1, 1);

            vpnBox.Controls.Add(vpnLayout);
            mainLayout.Controls.Add(vpnBox, 0, 1);

            // Branding Buttons
            TableLayoutPanel brandingPanel = new TableLayoutPanel() { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(0) };
            brandingPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            brandingPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            brandingPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));

            Button logoButton = new Button() { Text = "Change Logo", BackColor = Color.FromArgb(156, 39, 176), ForeColor = Color.White, Dock = DockStyle.Fill, Cursor = Cursors.Hand, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 5, 0) };
            logoButton.Click += LogoPicture_Click;

            Button iconButton = new Button() { Text = "Change Icon", BackColor = Color.FromArgb(233, 30, 99), ForeColor = Color.White, Dock = DockStyle.Fill, Cursor = Cursors.Hand, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 5, 0) };
            iconButton.Click += FaviconButton_Click;

            Button closeButton = new Button() { Text = "Close", BackColor = Color.FromArgb(150, 150, 150), ForeColor = Color.White, Dock = DockStyle.Fill, Cursor = Cursors.Hand, FlatStyle = FlatStyle.Flat, Margin = new Padding(0) };
            closeButton.Click += (s, ev) => settingsForm.Close();

            brandingPanel.Controls.Add(logoButton, 0, 0);
            brandingPanel.Controls.Add(iconButton, 1, 0);
            brandingPanel.Controls.Add(closeButton, 2, 0);
            mainLayout.Controls.Add(brandingPanel, 0, 2);

            // Information Section
            GroupBox infoBox = new GroupBox() { Text = "Information", Dock = DockStyle.Fill };
            Label infoText = new Label()
            {
                Text = "⚠️ Disclaimer: This tool manages network drive connections. Always verify credentials before saving.\n\n" +
                       "© 2026 Groblers CSS. All rights reserved.\n" +
                       "This application is provided as-is for authorized users only.\n" +
                       "Unauthorized access or distribution is prohibited.",
                Dock = DockStyle.Fill,
                Font = new Font("Arial", 8),
                ForeColor = Color.Gray
            };
            infoBox.Controls.Add(infoText);
            mainLayout.Controls.Add(infoBox, 0, 3);

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
            if (selectedVPNProvider != "tailscale")
            {
                if (MessageBox.Show("Tailscale is not the selected VPN provider. Launch anyway?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.No)
                {
                    return;
                }
            }
            LaunchTailscale();
        }

        private void NetBirdButton_Click(object sender, EventArgs e)
        {
            if (selectedVPNProvider != "netbird")
            {
                if (MessageBox.Show("NetBird is not the selected VPN provider. Launch anyway?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.No)
                {
                    return;
                }
            }
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
        }

        private void MountAllDrives()
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
                        process.WaitForExit();

                        if (process.ExitCode == 0)
                        {
                            drive.Status = "Mounted";
                            success++;
                            Log($"Mounted {drive.DriveLetter} -> {drive.ShareName}");
                        }
                        else
                        {
                            drive.Status = "Failed";
                            failed++;
                            Log($"Failed to mount {drive.DriveLetter}: {process.StandardError.ReadToEnd()}");
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
            if (selectedVPNProvider == "tailscale")
            {
                return GetTailscaleIP();
            }
            else if (selectedVPNProvider == "netbird")
            {
                return GetNetBirdIP();
            }
            return "Not Connected";
        }

        private void LaunchVPN()
        {
            if (selectedVPNProvider == "tailscale")
            {
                LaunchTailscale();
            }
            else if (selectedVPNProvider == "netbird")
            {
                LaunchNetBird();
            }
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
            lanIPLabel.ForeColor = string.IsNullOrEmpty(lanIP) ? Color.Gray : Color.Black;

            tailscaleIPLabel.Text = tailscaleIP.Contains("Not Connected") ? "Tailscale: Not Connected" : $"Tailscale IP: {tailscaleIP}";
            tailscaleIPLabel.ForeColor = tailscaleIP.Contains("Not Connected") ? Color.Gray : Color.FromArgb(0, 128, 0);

            netbirdIPLabel.Text = netbirdIP.Contains("Not Connected") ? "NetBird: Not Connected" : $"NetBird IP: {netbirdIP}";
            netbirdIPLabel.ForeColor = netbirdIP.Contains("Not Connected") ? Color.Gray : Color.FromArgb(0, 128, 0);
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
                    if (ni.Name.ToLower().Contains("tailscale") && ni.OperationalStatus == OperationalStatus.Up)
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
                    if (ni.Name.ToLower().Contains("netbird") && ni.OperationalStatus == OperationalStatus.Up)
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

                    if (settings.ContainsKey("selectedVPNProvider") && settings["selectedVPNProvider"].ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        selectedVPNProvider = settings["selectedVPNProvider"].GetString();
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
                    { "autoMountOnStartup", autoMountOnStartup },
                    { "selectedVPNProvider", selectedVPNProvider },
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

        private bool TestFileserverConnection(string testUsername, string testPassword)
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
                    process.WaitForExit(5000);

                    bool success = process.ExitCode == 0;

                    // Clean up the test connection
                    if (success)
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "net",
                            Arguments = $"use \\\\{fileserverIP}\\General /delete",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        })?.WaitForExit(2000);
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
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                Application.Run(new MainForm());
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OpenMesh.Gaming.Client
{
    public class ThemeConfig
    {
        public Color FormBack { get; set; }
        public Color CardBack { get; set; }
        public Color CardBorder { get; set; }
        public Color AccentServer { get; set; }
        public Color AccentHost { get; set; }
        public Color AccentClient { get; set; }
        public Color TermBack { get; set; }
        public Color TermBar { get; set; }
        public Color TermBorder { get; set; }
        public Color TermText { get; set; }
        public Color BtnDarkBack { get; set; }
        public Color BtnDarkBorder { get; set; }
        public Color TextPrimary { get; set; }
        public Color TextMuted { get; set; }
        public Color TextDim { get; set; }
        public Color HeaderAccent { get; set; }
        public Color BadgeBack { get; set; }
        public Color BadgeFore { get; set; }
    }

    public class MainForm : Form
    {
        // Runtime Config Defaults (Overridden by openmesh_client_config.json)
        private string serverAddress = "127.0.0.1";
        private int serverWebPort = 8080;
        private bool autoDiscoverLan = true;
        private string networkId = "";
        private string bootstrapToken = "";
        private string serverVirtualIp = "";
        private string peerVirtualIp = "";
        private string currentLanguage = "en"; // "en" or "fa"
        private string currentTheme = "cyber_dark"; // "cyber_dark", "cyber_purple", "clean_light"

        private ThemeConfig activeTheme;

        // UI Components
        private Panel pnlHeader;
        private Label lblLogo;
        private Label lblSubtitle;
        private Label lblBadge;
        private Button btnSettings;

        private Panel cardServer;
        private Label lblSrvTitle;
        private Label lblServerSub;
        private Label lblServerStatus;
        private Label lblServerPing;

        private Panel cardHost;
        private Label lblHstTitle;
        private Label lblHostSub;
        private Label lblHostStatus;
        private Label lblHostPing;

        private Panel cardClient;
        private Label lblCliTitle;
        private Label lblClientStatus;
        private Label lblClientIp;
        private Label lblCopyHint;

        private Button btnConnect;
        private Button btnImportMoon;
        private Button btnPing;
        private Button btnServiceToggle;
        private ProgressBar progressBar;

        private Panel pnlTerminal;
        private Panel pnlTermBar;
        private Label lblTermTitle;
        private Button btnClearLog;
        private RichTextBox txtLog;

        // State Tracking
        private Timer pingTimer;
        private bool isConnected = false;
        private bool isBusy = false;
        private bool isConnecting = false;
        private System.Threading.CancellationTokenSource ctsConnect = null;
        private bool isBanned = false;
        private long lastHandledKickTs = 0;
        private string localNodeId = "";
        private HashSet<string> previousBannedMembers = new HashSet<string>();

        private string serverStatusState = "disconnected"; // "disconnected", "online", "offline", "blocked"
        private long? lastServerPing = null;
        private string hostStatusState = "disconnected"; // "disconnected", "online", "offline", "blocked"
        private long? lastHostPing = null;
        private string clientStatusState = "disconnected"; // "disconnected", "connected", "kicked", "banned", "unbanned"

        public MainForm()
        {
            this.DoubleBuffered = true;
            LoadLocalConfig();
            activeTheme = GetTheme(currentTheme);
            InitializeComponent();
            ApplyTheme();
            ApplyLocalization();

            // The window handle does not exist yet during construction, so the
            // status probe has to wait for it before touching any control.
            this.Shown += (s, e) =>
            {
                try { CheckCurrentStatusAsync(); }
                catch { }
            };

            pingTimer = new Timer();
            pingTimer.Interval = 3000;
            // async void handler: wrap the body so an exception can never
            // escape onto the WinForms message loop (it would resurface as
            // Application.ThreadException on every single tick).
            pingTimer.Tick += async (s, e) =>
            {
                try
                {
                    await RunPeriodicPingAsync();
                }
                catch (Exception ex)
                {
                    try { Log("Ping loop error: " + ex.Message, Color.FromArgb(245, 158, 11)); } catch { }
                }
            };
            pingTimer.Start();
        }

        private ThemeConfig GetTheme(string themeName)
        {
            if (themeName == "cyber_purple")
            {
                return new ThemeConfig
                {
                    FormBack = Color.FromArgb(18, 12, 30),
                    CardBack = Color.FromArgb(28, 18, 48),
                    CardBorder = Color.FromArgb(65, 40, 100),
                    AccentServer = Color.FromArgb(192, 132, 252),
                    AccentHost = Color.FromArgb(236, 72, 153),
                    AccentClient = Color.FromArgb(56, 189, 248),
                    TermBack = Color.FromArgb(12, 8, 22),
                    TermBar = Color.FromArgb(24, 15, 42),
                    TermBorder = Color.FromArgb(60, 35, 95),
                    TermText = Color.FromArgb(235, 220, 255),
                    BtnDarkBack = Color.FromArgb(35, 22, 58),
                    BtnDarkBorder = Color.FromArgb(75, 45, 120),
                    TextPrimary = Color.FromArgb(250, 245, 255),
                    TextMuted = Color.FromArgb(185, 165, 220),
                    TextDim = Color.FromArgb(130, 110, 165),
                    HeaderAccent = Color.FromArgb(217, 70, 239),
                    BadgeBack = Color.FromArgb(40, 16, 50),
                    BadgeFore = Color.FromArgb(244, 114, 182)
                };
            }
            else if (themeName == "clean_light")
            {
                return new ThemeConfig
                {
                    FormBack = Color.FromArgb(241, 245, 249),
                    CardBack = Color.FromArgb(255, 255, 255),
                    CardBorder = Color.FromArgb(203, 213, 225),
                    AccentServer = Color.FromArgb(2, 132, 199),
                    AccentHost = Color.FromArgb(126, 34, 206),
                    AccentClient = Color.FromArgb(16, 185, 129),
                    TermBack = Color.FromArgb(248, 250, 252),
                    TermBar = Color.FromArgb(226, 232, 240),
                    TermBorder = Color.FromArgb(203, 213, 225),
                    TermText = Color.FromArgb(30, 41, 59),
                    BtnDarkBack = Color.FromArgb(255, 255, 255),
                    BtnDarkBorder = Color.FromArgb(203, 213, 225),
                    TextPrimary = Color.FromArgb(15, 23, 42),
                    TextMuted = Color.FromArgb(71, 85, 105),
                    TextDim = Color.FromArgb(100, 116, 139),
                    HeaderAccent = Color.FromArgb(14, 116, 144),
                    BadgeBack = Color.FromArgb(220, 252, 231),
                    BadgeFore = Color.FromArgb(22, 101, 52)
                };
            }
            else // default "cyber_dark"
            {
                return new ThemeConfig
                {
                    FormBack = Color.FromArgb(11, 13, 20),
                    CardBack = Color.FromArgb(18, 22, 34),
                    CardBorder = Color.FromArgb(32, 40, 60),
                    AccentServer = Color.FromArgb(0, 210, 255),
                    AccentHost = Color.FromArgb(168, 85, 247),
                    AccentClient = Color.FromArgb(16, 185, 129),
                    TermBack = Color.FromArgb(7, 9, 14),
                    TermBar = Color.FromArgb(14, 18, 28),
                    TermBorder = Color.FromArgb(30, 38, 56),
                    TermText = Color.FromArgb(215, 225, 245),
                    BtnDarkBack = Color.FromArgb(20, 25, 38),
                    BtnDarkBorder = Color.FromArgb(40, 50, 75),
                    TextPrimary = Color.White,
                    TextMuted = Color.FromArgb(160, 175, 205),
                    TextDim = Color.FromArgb(90, 105, 135),
                    HeaderAccent = Color.FromArgb(0, 229, 255),
                    BadgeBack = Color.FromArgb(16, 36, 30),
                    BadgeFore = Color.FromArgb(0, 230, 118)
                };
            }
        }

        private void InitializeComponent()
        {
            this.ClientSize = new Size(775, 620);
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

            // --- HEADER ---
            pnlHeader = new Panel { Location = new Point(25, 16), Size = new Size(725, 60), BackColor = Color.Transparent };

            lblLogo = new Label
            {
                Text = "⚡ OPENMESH GAMING",
                Font = new Font("Segoe UI", 16f, FontStyle.Bold),
                Location = new Point(0, 4),
                AutoSize = true
            };

            lblSubtitle = new Label
            {
                Text = "High-Performance Peer-to-Peer Virtual LAN Mesh • Offline Autonomous Core",
                Font = new Font("Segoe UI", 9f, FontStyle.Regular),
                Location = new Point(2, 34),
                AutoSize = true
            };

            lblBadge = new Label
            {
                Text = "OFFLINE ENGINE",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                BorderStyle = BorderStyle.FixedSingle,
                TextAlign = ContentAlignment.MiddleCenter,
                Size = new Size(130, 28),
                Location = new Point(595, 14)
            };

            btnSettings = new Button
            {
                Text = "⚙️ Settings",
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                FlatStyle = FlatStyle.Flat,
                Size = new Size(145, 28),
                Location = new Point(440, 14),
                Cursor = Cursors.Hand
            };
            btnSettings.Click += (s, e) => ShowSettingsDialog();

            pnlHeader.Controls.AddRange(new Control[] { lblLogo, lblSubtitle, btnSettings, lblBadge });
            this.Controls.Add(pnlHeader);

            // --- 3 STATUS CARDS ---
            // Card 1: Server Core
            cardServer = new Panel { Location = new Point(25, 84), Size = new Size(230, 105) };
            cardServer.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle rect = new Rectangle(0, 0, cardServer.Width - 1, cardServer.Height - 1);
                using (Pen borderPen = new Pen(activeTheme.CardBorder, 1))
                {
                    e.Graphics.DrawRectangle(borderPen, rect);
                }
                using (SolidBrush accentBrush = new SolidBrush(activeTheme.AccentServer))
                {
                    e.Graphics.FillRectangle(accentBrush, 0, 0, cardServer.Width, 3);
                }
            };
            lblSrvTitle = new Label { Text = "SERVER CORE", Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), Location = new Point(14, 12), AutoSize = true };
            lblServerSub = new Label { Text = "IP: " + FormatIp(serverVirtualIp), Font = new Font("Consolas", 8.5f), Location = new Point(14, 32), AutoSize = true };
            lblServerStatus = new Label { Text = "● Disconnected", ForeColor = Color.FromArgb(130, 140, 160), Font = new Font("Segoe UI", 11f, FontStyle.Bold), Location = new Point(14, 52), AutoSize = true };
            lblServerPing = new Label { Text = "Latency: --", Font = new Font("Segoe UI", 8.5f), Location = new Point(14, 76), AutoSize = true };
            cardServer.Controls.AddRange(new Control[] { lblSrvTitle, lblServerSub, lblServerStatus, lblServerPing });
            this.Controls.Add(cardServer);

            // Card 2: Host / Primary Peer
            cardHost = new Panel { Location = new Point(272, 84), Size = new Size(230, 105) };
            cardHost.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle rect = new Rectangle(0, 0, cardHost.Width - 1, cardHost.Height - 1);
                using (Pen borderPen = new Pen(activeTheme.CardBorder, 1))
                {
                    e.Graphics.DrawRectangle(borderPen, rect);
                }
                using (SolidBrush accentBrush = new SolidBrush(activeTheme.AccentHost))
                {
                    e.Graphics.FillRectangle(accentBrush, 0, 0, cardHost.Width, 3);
                }
            };
            lblHstTitle = new Label { Text = "HOST / PEER", Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), Location = new Point(14, 12), AutoSize = true };
            lblHostSub = new Label { Text = "IP: " + FormatIp(peerVirtualIp), Font = new Font("Consolas", 8.5f), Location = new Point(14, 32), AutoSize = true };
            lblHostStatus = new Label { Text = "● Disconnected", ForeColor = Color.FromArgb(130, 140, 160), Font = new Font("Segoe UI", 11f, FontStyle.Bold), Location = new Point(14, 52), AutoSize = true };
            lblHostPing = new Label { Text = "Latency: --", Font = new Font("Segoe UI", 8.5f), Location = new Point(14, 76), AutoSize = true };
            cardHost.Controls.AddRange(new Control[] { lblHstTitle, lblHostSub, lblHostStatus, lblHostPing });
            this.Controls.Add(cardHost);

            // Card 3: Your Gaming IP
            cardClient = new Panel { Location = new Point(520, 84), Size = new Size(230, 105) };
            cardClient.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle rect = new Rectangle(0, 0, cardClient.Width - 1, cardClient.Height - 1);
                using (Pen borderPen = new Pen(activeTheme.CardBorder, 1))
                {
                    e.Graphics.DrawRectangle(borderPen, rect);
                }
                using (SolidBrush accentBrush = new SolidBrush(activeTheme.AccentClient))
                {
                    e.Graphics.FillRectangle(accentBrush, 0, 0, cardClient.Width, 3);
                }
            };
            lblCliTitle = new Label { Text = "YOUR GAMING IP", Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), Location = new Point(14, 12), AutoSize = true };
            lblClientStatus = new Label { Text = "Status: Disconnected", ForeColor = Color.FromArgb(239, 68, 68), Font = new Font("Segoe UI", 8.5f), Location = new Point(14, 32), AutoSize = true };
            lblClientIp = new Label
            {
                Text = "--.--.--.--",
                Font = new Font("Consolas", 13f, FontStyle.Bold),
                Location = new Point(14, 54),
                AutoSize = true,
                Cursor = Cursors.Hand
            };
            lblClientIp.Click += (s, e) =>
            {
                if (lblClientIp.Text != "--.--.--.--" && lblClientIp.Text != "Assigning..." && !lblClientIp.Text.Contains("DENIED") && !lblClientIp.Text.Contains("مسدود"))
                {
                    Clipboard.SetText(lblClientIp.Text);
                    string msg = (currentLanguage == "fa")
                        ? string.Format("آی‌پی گیمینگ شما ({0}) با موفقیت کپی شد!", lblClientIp.Text)
                        : string.Format("Copied your IP ({0}) to clipboard!", lblClientIp.Text);
                    Log(msg, activeTheme.AccentClient);
                }
            };
            lblCopyHint = new Label { Text = "(Click IP to Copy)", Font = new Font("Segoe UI", 7.5f), Location = new Point(14, 79), AutoSize = true };
            cardClient.Controls.AddRange(new Control[] { lblCliTitle, lblClientStatus, lblClientIp, lblCopyHint });
            this.Controls.Add(cardClient);

            // --- ACTION BUTTONS ---
            // 1. Big Action Button
            btnConnect = new Button();
            btnConnect.Text = "CONNECT TO GAMING LAN";
            btnConnect.Font = new Font("Segoe UI", 11.5f, FontStyle.Bold);
            btnConnect.BackColor = Color.FromArgb(16, 185, 129);
            btnConnect.ForeColor = Color.White;
            btnConnect.FlatStyle = FlatStyle.Flat;
            btnConnect.FlatAppearance.BorderSize = 0;
            btnConnect.Location = new Point(25, 205);
            btnConnect.Size = new Size(310, 50);
            btnConnect.Cursor = Cursors.Hand;
            btnConnect.Click += async (s, e) => await ToggleConnectionAsync();
            this.Controls.Add(btnConnect);

            // 2. Import Moon Button
            btnImportMoon = CreateCyberButton("📂 Import Moon", new Point(350, 205), new Size(130, 50));
            btnImportMoon.Click += async (s, e) => await ImportNewMoonAsync();
            this.Controls.Add(btnImportMoon);

            // 3. Test Ping Button
            btnPing = CreateCyberButton("⚡ Test Ping", new Point(495, 205), new Size(115, 50));
            btnPing.Click += async (s, e) => await RunManualPingAsync();
            this.Controls.Add(btnPing);

            // 4. Service Toggle Button
            btnServiceToggle = CreateCyberButton("⏹ Stop Service", new Point(625, 205), new Size(125, 50));
            btnServiceToggle.Click += async (s, e) => await ToggleServiceAsync();
            this.Controls.Add(btnServiceToggle);

            // Progress Bar
            progressBar = new ProgressBar();
            progressBar.Location = new Point(25, 265);
            progressBar.Size = new Size(725, 4);
            progressBar.Style = ProgressBarStyle.Continuous;
            progressBar.Visible = false;
            this.Controls.Add(progressBar);

            // --- TERMINAL CONTAINER ---
            pnlTerminal = new Panel();
            pnlTerminal.Location = new Point(25, 276);
            pnlTerminal.Size = new Size(725, 320);
            pnlTerminal.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (Pen pen = new Pen(activeTheme.TermBorder, 1))
                {
                    e.Graphics.DrawRectangle(pen, 0, 0, pnlTerminal.Width - 1, pnlTerminal.Height - 1);
                }
            };

            // Terminal Header Bar
            pnlTermBar = new Panel { Location = new Point(1, 1), Size = new Size(723, 30) };

            Panel dotRed = new Panel { Size = new Size(10, 10), Location = new Point(14, 10), BackColor = Color.FromArgb(239, 68, 68) };
            Panel dotYellow = new Panel { Size = new Size(10, 10), Location = new Point(30, 10), BackColor = Color.FromArgb(245, 158, 11) };
            Panel dotGreen = new Panel { Size = new Size(10, 10), Location = new Point(46, 10), BackColor = Color.FromArgb(16, 185, 129) };

            lblTermTitle = new Label
            {
                Text = "REALTIME ENGINE CONSOLE • AUTONOMOUS OFFLINE STACK",
                Font = new Font("Consolas", 8.5f, FontStyle.Bold),
                Location = new Point(70, 7),
                AutoSize = true
            };

            btnClearLog = new Button
            {
                Text = "Clear",
                Font = new Font("Segoe UI", 7.5f),
                BackColor = Color.Transparent,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(60, 22),
                Location = new Point(650, 4),
                Cursor = Cursors.Hand
            };
            btnClearLog.FlatAppearance.BorderSize = 0;
            btnClearLog.Click += (s, e) => txtLog.Clear();

            pnlTermBar.Controls.AddRange(new Control[] { dotRed, dotYellow, dotGreen, lblTermTitle, btnClearLog });
            pnlTerminal.Controls.Add(pnlTermBar);

            // Monospace Log View
            txtLog = new RichTextBox();
            txtLog.Location = new Point(12, 38);
            txtLog.Size = new Size(701, 270);
            txtLog.Font = new Font("Consolas", 9.5f, FontStyle.Regular);
            txtLog.BorderStyle = BorderStyle.None;
            txtLog.ReadOnly = true;
            pnlTerminal.Controls.Add(txtLog);

            this.Controls.Add(pnlTerminal);

            Log(currentLanguage == "fa" ? "آماده به کار. موتور بازی اوپن‌مش راه‌اندازی شد." : "Ready. OpenMesh Gaming Engine initialized.", activeTheme.HeaderAccent);
        }

        private Button CreateCyberButton(string text, Point location, Size size)
        {
            Button btn = new Button();
            btn.Text = text;
            btn.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            btn.FlatStyle = FlatStyle.Flat;
            btn.Location = location;
            btn.Size = size;
            btn.Cursor = Cursors.Hand;
            return btn;
        }

        private void ApplyTheme()
        {
            activeTheme = GetTheme(currentTheme);
            this.BackColor = activeTheme.FormBack;
            this.ForeColor = activeTheme.TextPrimary;

            lblLogo.ForeColor = activeTheme.HeaderAccent;
            lblSubtitle.ForeColor = activeTheme.TextMuted;
            lblBadge.BackColor = activeTheme.BadgeBack;
            lblBadge.ForeColor = activeTheme.BadgeFore;

            btnSettings.BackColor = activeTheme.BtnDarkBack;
            btnSettings.FlatAppearance.BorderColor = activeTheme.BtnDarkBorder;
            btnSettings.FlatAppearance.MouseOverBackColor = activeTheme.CardBack;
            btnSettings.ForeColor = activeTheme.HeaderAccent;

            cardServer.BackColor = activeTheme.CardBack;
            lblSrvTitle.ForeColor = activeTheme.TextMuted;
            lblServerSub.ForeColor = activeTheme.TextDim;

            cardHost.BackColor = activeTheme.CardBack;
            lblHstTitle.ForeColor = activeTheme.TextMuted;
            lblHostSub.ForeColor = activeTheme.TextDim;

            cardClient.BackColor = activeTheme.CardBack;
            lblCliTitle.ForeColor = activeTheme.TextMuted;
            lblClientIp.ForeColor = activeTheme.AccentClient;
            lblCopyHint.ForeColor = activeTheme.TextDim;

            btnImportMoon.BackColor = activeTheme.BtnDarkBack;
            btnImportMoon.FlatAppearance.BorderColor = activeTheme.BtnDarkBorder;
            btnImportMoon.FlatAppearance.MouseOverBackColor = activeTheme.CardBack;
            btnImportMoon.ForeColor = activeTheme.AccentServer;

            btnPing.BackColor = activeTheme.BtnDarkBack;
            btnPing.FlatAppearance.BorderColor = activeTheme.BtnDarkBorder;
            btnPing.FlatAppearance.MouseOverBackColor = activeTheme.CardBack;
            btnPing.ForeColor = activeTheme.HeaderAccent;

            btnServiceToggle.BackColor = activeTheme.BtnDarkBack;
            btnServiceToggle.FlatAppearance.BorderColor = activeTheme.BtnDarkBorder;
            btnServiceToggle.FlatAppearance.MouseOverBackColor = activeTheme.CardBack;
            btnServiceToggle.ForeColor = Color.FromArgb(245, 158, 11);

            pnlTerminal.BackColor = activeTheme.TermBack;
            pnlTermBar.BackColor = activeTheme.TermBar;
            lblTermTitle.ForeColor = activeTheme.TextMuted;
            btnClearLog.ForeColor = activeTheme.TextMuted;
            txtLog.BackColor = activeTheme.TermBack;
            txtLog.ForeColor = activeTheme.TermText;

            cardServer.Invalidate();
            cardHost.Invalidate();
            cardClient.Invalidate();
            pnlTerminal.Invalidate();
        }

        private void ApplyLocalization()
        {
            bool isFa = (currentLanguage == "fa");
            this.Text = isFa ? "اوپن‌مش گیمینگ - شبکه مش مجازی و مستقل LAN" : "OpenMesh Gaming - Autonomous Cyber Mesh LAN";
            lblSubtitle.Text = isFa
                ? "شبکه گیمینگ همتا‌به‌همتا (P2P) با پینگ پایین • هسته مستقل و آفلاین"
                : "High-Performance Peer-to-Peer Virtual LAN Mesh • Offline Autonomous Core";
            lblBadge.Text = isFa ? "موتور آفلاین" : "OFFLINE ENGINE";
            btnSettings.Text = isFa ? "⚙️ تنظیمات" : "⚙️ Settings";

            lblSrvTitle.Text = isFa ? "سرور مرکزی (Core)" : "SERVER CORE";
            lblHstTitle.Text = isFa ? "هاست / بازیکن هدف" : "HOST / PEER";
            lblCliTitle.Text = isFa ? "آی‌پی گیمینگ شما" : "YOUR GAMING IP";
            lblCopyHint.Text = isFa ? "(برای کپی کلیک کنید)" : "(Click IP to Copy)";

            btnImportMoon.Text = isFa ? "📂 فایل Moon" : "📂 Import Moon";
            btnPing.Text = isFa ? "⚡ تست پینگ" : "⚡ Test Ping";
            lblTermTitle.Text = isFa ? "کنسول زنده موتور گیمینگ • ساختار مستقل شبکه" : "REALTIME ENGINE CONSOLE • AUTONOMOUS OFFLINE STACK";
            btnClearLog.Text = isFa ? "پاک‌سازی" : "Clear";

            UpdateConnectButtonText();
            UpdateServiceButtonText();
            UpdateStatusCardsText();
        }

        private void UpdateConnectButtonText()
        {
            bool isFa = (currentLanguage == "fa");
            if (isBanned)
            {
                btnConnect.Text = isFa ? "⛔ مسدود شده از شبکه" : "⛔ BANNED FROM LAN";
                btnConnect.BackColor = Color.FromArgb(185, 28, 28);
                btnConnect.Enabled = false;
            }
            else if (isConnecting)
            {
                btnConnect.Text = isFa ? "❌ لغو فرآیند اتصال" : "❌ CANCEL CONNECTION";
                btnConnect.BackColor = Color.FromArgb(220, 38, 38);
                btnConnect.Enabled = true;
            }
            else if (isConnected)
            {
                btnConnect.Text = isFa ? "قطع اتصال از شبکه" : "DISCONNECT FROM LAN";
                btnConnect.BackColor = Color.FromArgb(239, 68, 68);
                btnConnect.Enabled = true;
            }
            else
            {
                btnConnect.Text = isFa ? "اتصال به شبکه گیمینگ" : "CONNECT TO GAMING LAN";
                btnConnect.BackColor = Color.FromArgb(16, 185, 129);
                btnConnect.Enabled = true;
            }
        }

        private void UpdateServiceButtonText()
        {
            bool isFa = (currentLanguage == "fa");
            if (btnServiceToggle.Text.Contains("▶") || btnServiceToggle.Text.Contains("Start") || btnServiceToggle.Text.Contains("شروع"))
            {
                btnServiceToggle.Text = isFa ? "▶ شروع سرویس" : "▶ Start Service";
            }
            else
            {
                btnServiceToggle.Text = isFa ? "⏹ توقف سرویس" : "⏹ Stop Service";
            }
        }

        private static string FormatIp(string ip)
        {
            return string.IsNullOrEmpty(ip) ? "--" : ip;
        }

        private void UpdateStatusCardsText()
        {
            bool isFa = (currentLanguage == "fa");
            lblServerSub.Text = "IP: " + FormatIp(serverVirtualIp);
            lblHostSub.Text = "IP: " + FormatIp(peerVirtualIp);

            // Server Card
            if (serverStatusState == "online")
            {
                lblServerStatus.Text = isFa ? "● آنلاین" : "● Online";
                lblServerStatus.ForeColor = Color.FromArgb(16, 185, 129);
                lblServerPing.Text = lastServerPing.HasValue
                    ? string.Format(isFa ? "پینگ: {0} میلی‌ثانیه" : "Latency: {0} ms", lastServerPing.Value)
                    : (isFa ? "پینگ: --" : "Latency: --");
                lblServerPing.ForeColor = activeTheme.TextPrimary;
            }
            else if (serverStatusState == "offline")
            {
                lblServerStatus.Text = isFa ? "● آفلاین" : "● Offline";
                lblServerStatus.ForeColor = Color.FromArgb(239, 68, 68);
                lblServerPing.Text = isFa ? "پینگ: تایم‌اوت" : "Latency: Timeout";
                lblServerPing.ForeColor = activeTheme.TextMuted;
            }
            else if (serverStatusState == "blocked")
            {
                lblServerStatus.Text = isFa ? "● مسدود" : "● Blocked";
                lblServerStatus.ForeColor = Color.FromArgb(239, 68, 68);
                lblServerPing.Text = isFa ? "پینگ: مسدود" : "Latency: Blocked";
                lblServerPing.ForeColor = Color.FromArgb(239, 68, 68);
            }
            else
            {
                lblServerStatus.Text = isFa ? "● قطع اتصال" : "● Disconnected";
                lblServerStatus.ForeColor = Color.FromArgb(130, 140, 160);
                lblServerPing.Text = isFa ? "پینگ: --" : "Latency: --";
                lblServerPing.ForeColor = activeTheme.TextMuted;
            }

            // Host Card
            if (hostStatusState == "online")
            {
                lblHostStatus.Text = isFa ? "● آنلاین" : "● Online";
                lblHostStatus.ForeColor = Color.FromArgb(16, 185, 129);
                lblHostPing.Text = lastHostPing.HasValue
                    ? string.Format(isFa ? "پینگ: {0} میلی‌ثانیه" : "Latency: {0} ms", lastHostPing.Value)
                    : (isFa ? "پینگ: --" : "Latency: --");
                lblHostPing.ForeColor = activeTheme.TextPrimary;
            }
            else if (hostStatusState == "offline")
            {
                lblHostStatus.Text = isFa ? "● آفلاین" : "● Offline";
                lblHostStatus.ForeColor = Color.FromArgb(239, 68, 68);
                lblHostPing.Text = isFa ? "پینگ: تایم‌اوت" : "Latency: Timeout";
                lblHostPing.ForeColor = activeTheme.TextMuted;
            }
            else if (hostStatusState == "blocked")
            {
                lblHostStatus.Text = isFa ? "● مسدود" : "● Blocked";
                lblHostStatus.ForeColor = Color.FromArgb(239, 68, 68);
                lblHostPing.Text = isFa ? "پینگ: مسدود" : "Latency: Blocked";
                lblHostPing.ForeColor = Color.FromArgb(239, 68, 68);
            }
            else
            {
                lblHostStatus.Text = isFa ? "● قطع اتصال" : "● Disconnected";
                lblHostStatus.ForeColor = Color.FromArgb(130, 140, 160);
                lblHostPing.Text = isFa ? "پینگ: --" : "Latency: --";
                lblHostPing.ForeColor = activeTheme.TextMuted;
            }

            // Client Card
            if (clientStatusState == "connected")
            {
                lblClientStatus.Text = isFa ? "وضعیت: متصل" : "Status: Connected";
                lblClientStatus.ForeColor = Color.FromArgb(16, 185, 129);
                lblClientIp.ForeColor = activeTheme.AccentClient;
            }
            else if (clientStatusState == "banned")
            {
                lblClientStatus.Text = isFa ? "وضعیت: ⛔ بن شده توسط ادمین" : "Status: ⛔ BANNED BY ADMIN";
                lblClientStatus.ForeColor = Color.FromArgb(239, 68, 68);
                lblClientIp.Text = isFa ? "دسترسی مسدود" : "ACCESS DENIED";
                lblClientIp.ForeColor = Color.FromArgb(239, 68, 68);
            }
            else if (clientStatusState == "kicked")
            {
                lblClientStatus.Text = isFa ? "وضعیت: ⚠️ اخراج توسط ادمین" : "Status: ⚠️ Kicked by Admin";
                lblClientStatus.ForeColor = Color.FromArgb(245, 158, 11);
                lblClientIp.Text = "--.--.--.--";
                lblClientIp.ForeColor = Color.FromArgb(245, 158, 11);
            }
            else if (clientStatusState == "unbanned")
            {
                lblClientStatus.Text = isFa ? "وضعیت: قطع اتصال (رفع بن)" : "Status: Disconnected (Unbanned)";
                lblClientStatus.ForeColor = Color.FromArgb(16, 185, 129);
                lblClientIp.Text = "--.--.--.--";
                lblClientIp.ForeColor = activeTheme.AccentClient;
            }
            else
            {
                lblClientStatus.Text = isFa ? "وضعیت: قطع اتصال" : "Status: Disconnected";
                lblClientStatus.ForeColor = Color.FromArgb(239, 68, 68);
                lblClientIp.Text = "--.--.--.--";
                lblClientIp.ForeColor = activeTheme.AccentClient;
            }
        }

        /// <summary>
        /// Marshals an action onto the UI thread without ever throwing.
        /// Invoke blocks the calling thread and throws when the handle is
        /// gone, so we use BeginInvoke and swallow anything that escapes.
        /// </summary>
        private void PostToUi(Action action)
        {
            if (action == null) return;
            if (this.IsDisposed || !this.IsHandleCreated) return;
            try
            {
                if (this.InvokeRequired)
                {
                    this.BeginInvoke(action);
                }
                else
                {
                    action();
                }
            }
            catch { }
        }

        private void Log(string message, Color? color = null)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;
            if (this.InvokeRequired)
            {
                PostToUi(() => Log(message, color));
                return;
            }

            txtLog.SelectionStart = txtLog.TextLength;
            txtLog.SelectionLength = 0;
            txtLog.SelectionColor = color ?? activeTheme.TermText;
            txtLog.AppendText(string.Format("[{0:HH:mm:ss}] {1}\n", DateTime.Now, message));
            txtLog.ScrollToCaret();
        }

        private string GetZtCliPath()
        {
            string p1 = @"C:\Program Files (x86)\ZeroTier\One\zerotier-cli.bat";
            string p2 = @"C:\Program Files\ZeroTier\One\zerotier-cli.bat";
            if (File.Exists(p1)) return p1;
            if (File.Exists(p2)) return p2;
            return null;
        }

        private async Task CheckCurrentStatusAsync()
        {
            await Task.Run(() =>
            {
                GetLocalNodeId();
                string cli = GetZtCliPath();
                if (cli != null && !string.IsNullOrEmpty(networkId))
                {
                    string output = RunCommand(cli, "listnetworks");
                    if (output.Contains(networkId))
                    {
                        if (output.Contains("ACCESS_DENIED"))
                        {
                            if (VerifyServerBannedState())
                            {
                                SetBannedState(true, "ZeroTier Controller reports ACCESS_DENIED");
                                return;
                            }
                        }

                        var match = Regex.Match(output, @"(\d+\.\d+\.\d+\.\d+)/\d+");
                        PostToUi(() =>
                        {
                            isConnected = true;
                            clientStatusState = "connected";
                            UpdateConnectButtonText();
                            lblClientIp.Text = match.Success ? match.Groups[1].Value : "--";
                            UpdateStatusCardsText();
                            string msg = (currentLanguage == "fa")
                                ? "به شبکه متصل هستید: " + networkId
                                : "Already joined network " + networkId;
                            Log(msg, activeTheme.AccentClient);
                        });
                    }
                }
            });
            await RunPeriodicPingAsync();
        }

        private async Task ToggleConnectionAsync()
        {
            bool isFa = (currentLanguage == "fa");
            if (isBanned)
            {
                bool srvBanned = await Task.Run(() => VerifyServerBannedState());
                if (srvBanned)
                {
                    string banMsg = isFa
                        ? "دسترسی شما توسط ادمین مسدود (بن) شده است.\nجهت رفع مسدودی با مدیر شبکه تماس بگیرید."
                        : "You have been banned by the network administrator.\nPlease contact the administrator to unban your node.";
                    MessageBox.Show(this, banMsg, isFa ? "⛔ دسترسی مسدود" : "⛔ Access Denied", MessageBoxButtons.OK, MessageBoxIcon.Stop);
                    return;
                }
                else
                {
                    SetBannedState(false, "Verified not banned");
                }
            }

            // 1. If currently connecting, user clicked CANCEL!
            if (isConnecting)
            {
                string cancelLog = isFa ? "در حال لغو فرآیند اتصال..." : "Cancelling connection attempt...";
                Log(cancelLog, Color.FromArgb(245, 158, 11));
                btnConnect.Text = isFa ? "در حال لغو..." : "CANCELLING...";
                // The button stays enabled so the user is never stuck with a
                // dead control; the in-flight task restores the normal state.
                if (ctsConnect != null)
                {
                    try { ctsConnect.Cancel(); } catch { }
                }
                return;
            }

            // 2. If already busy with disconnect, ignore
            if (isBusy) return;

            // 3. If connected, perform Disconnect
            if (isConnected)
            {
                isBusy = true;
                btnConnect.Enabled = false;
                progressBar.Visible = true;
                progressBar.Style = ProgressBarStyle.Marquee;

                try
                {
                    Log(isFa ? "در حال قطع اتصال از شبکه گیمینگ..." : "Disconnecting from LAN...", Color.FromArgb(245, 158, 11));
                    await Task.Run(() =>
                    {
                        string myId = GetLocalNodeId();
                        try
                        {
                            using (TimeoutWebClient wc = new TimeoutWebClient(2000, bootstrapToken))
                            {
                                wc.DownloadString("http://" + serverAddress + ":" + serverWebPort + "/api/client_disconnect?id=" + myId);
                            }
                        }
                        catch { }

                        string cli = GetZtCliPath();
                        if (cli != null && !string.IsNullOrEmpty(networkId))
                        {
                            RunCommand(cli, "leave " + networkId);
                        }
                    });

                    isConnected = false;
                    clientStatusState = "disconnected";
                    serverStatusState = "disconnected";
                    hostStatusState = "disconnected";
                    lastServerPing = null;
                    lastHostPing = null;
                    UpdateConnectButtonText();
                    UpdateStatusCardsText();
                    Log(isFa ? "اتصال با موفقیت قطع شد." : "Disconnected successfully.", activeTheme.AccentClient);
                }
                catch (Exception ex)
                {
                    Log((isFa ? "خطا در قطع اتصال: " : "Disconnect error: ") + ex.Message, Color.FromArgb(239, 68, 68));
                }
                finally
                {
                    isBusy = false;
                    btnConnect.Enabled = true;
                    progressBar.Visible = false;
                }
                await RunPeriodicPingAsync();
                return;
            }

            // 4. Otherwise, start Connecting with instant cancel capability
            isBusy = true;
            isConnecting = true;
            UpdateConnectButtonText();
            progressBar.Visible = true;
            progressBar.Style = ProgressBarStyle.Marquee;

            ctsConnect = new System.Threading.CancellationTokenSource();
            var cancelToken = ctsConnect.Token;

            try
            {
                Log(isFa ? "شروع فرآیند اتصال به شبکه..." : "Starting connection process...", activeTheme.HeaderAccent);
                // Do NOT pass the token to Task.Run: that makes the task
                // complete as "Canceled" while the delegate is still running,
                // which would dispose the token underneath it. The delegate
                // observes cancellation itself.
                await Task.Run(() => PerformFullConnect(cancelToken));

                isConnected = true;
                clientStatusState = "connected";
                UpdateConnectButtonText();
                UpdateStatusCardsText();
            }
            catch (OperationCanceledException)
            {
                Log(isFa ? "فرآیند اتصال توسط کاربر لغو گردید." : "Connection cancelled by user. Resetting interface.", Color.FromArgb(245, 158, 11));
                isConnected = false;
                clientStatusState = "disconnected";
                UpdateConnectButtonText();
                UpdateStatusCardsText();
            }
            catch (Exception ex)
            {
                Log((isFa ? "خطا: " : "Error: ") + ex.Message, Color.FromArgb(239, 68, 68));
                isConnected = false;
                clientStatusState = "disconnected";
                UpdateConnectButtonText();
                UpdateStatusCardsText();
            }
            finally
            {
                isBusy = false;
                isConnecting = false;
                btnConnect.Enabled = true;
                progressBar.Visible = false;
                if (ctsConnect != null)
                {
                    try { ctsConnect.Dispose(); } catch { }
                    ctsConnect = null;
                }
            }
            await RunPeriodicPingAsync();
        }

        private string AutoDiscoverServerLanIp(bool silent = false)
        {
            bool isFa = (currentLanguage == "fa");
            if (!silent) Log(isFa ? "در حال جستجوی سرور اوپن‌مش در شبکه محلی..." : "Searching for OpenMesh Server on local network...", activeTheme.HeaderAccent);

            if (TestTcpPort(serverAddress, serverWebPort, 400))
            {
                if (!silent) Log(string.Format(isFa ? "سرور در آدرس فعلی پیدا شد: {0}:{1}" : "Found server at current address: {0}:{1}", serverAddress, serverWebPort), activeTheme.AccentClient);
                return serverAddress;
            }

            string localIp = GetPrimaryLocalIp();
            if (!string.IsNullOrEmpty(localIp))
            {
                int lastDot = localIp.LastIndexOf('.');
                if (lastDot > 0)
                {
                    string prefix = localIp.Substring(0, lastDot + 1);
                    if (!silent) Log(string.Format(isFa ? "در حال اسکن رنج {0}0/24 روی پورت {1}..." : "Scanning subnet {0}0/24 on port {1}...", prefix, serverWebPort), Color.FromArgb(245, 158, 11));

                    string foundIp = null;
                    Parallel.For(1, 255, (i, loopState) =>
                    {
                        string target = prefix + i;
                        if (target != localIp && TestTcpPort(target, serverWebPort, 350))
                        {
                            foundIp = target;
                            loopState.Stop();
                        }
                    });

                    if (foundIp != null)
                    {
                        if (!silent) Log(string.Format(isFa ? "سرور در شبکه محلی کشف شد: {0}:{1}" : "Discovered server on LAN: {0}:{1}", foundIp, serverWebPort), activeTheme.AccentClient);
                        serverAddress = foundIp;
                        SaveLocalConfig();
                        return foundIp;
                    }
                }
            }

            int[] fallbackPorts = new int[] { 8080, 8888, 9090, 8000, 80 };
            foreach (int testPort in fallbackPorts)
            {
                if (testPort == serverWebPort) continue;
                if (TestTcpPort(serverAddress, testPort, 350))
                {
                    if (!silent) Log(string.Format(isFa ? "پورت فعال سرور شناسایی شد: {0}!" : "Auto-detected server on port: {0}!", testPort), activeTheme.AccentClient);
                    serverWebPort = testPort;
                    SaveLocalConfig();
                    return serverAddress;
                }
            }

            if (!silent) Log((isFa ? "استفاده از آدرس تنظیم شده: " : "Using configured server address: ") + serverAddress, Color.FromArgb(245, 158, 11));
            return serverAddress;
        }

        private bool TestTcpPort(string host, int port, int timeoutMs)
        {
            TcpClient client = null;
            try
            {
                client = new TcpClient();
                var result = client.BeginConnect(host, port, null, null);
                // The wait handle must be disposed or every probe leaks a
                // kernel handle (254 probes per LAN scan).
                using (result.AsyncWaitHandle)
                {
                    bool success = result.AsyncWaitHandle.WaitOne(timeoutMs);
                    if (success && client.Connected)
                    {
                        client.EndConnect(result);
                        return true;
                    }
                }
            }
            catch { }
            finally
            {
                if (client != null)
                {
                    try { client.Close(); } catch { }
                }
            }
            return false;
        }

        private string GetPrimaryLocalIp()
        {
            // Prefer an operational interface address: it works on air-gapped
            // hosts where the 8.8.8.8 trick fails.
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            string ip = ua.Address.ToString();
                            if (!ip.StartsWith("127.") && !ip.StartsWith("169.254."))
                                return ip;
                        }
                    }
                }
            }
            catch { }

            // Fall back to the default-route trick.
            try
            {
                using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
                {
                    socket.Connect("8.8.8.8", 65530);
                    IPEndPoint endPoint = socket.LocalEndPoint as IPEndPoint;
                    return endPoint.Address.ToString();
                }
            }
            catch
            {
                return "";
            }
        }

        private void PerformFullConnect(System.Threading.CancellationToken ct)
        {
            bool isFa = (currentLanguage == "fa");
            string cli = GetZtCliPath();
            ct.ThrowIfCancellationRequested();

            // 1. Install ZeroTier if missing
            if (cli == null)
            {
                string localMsi = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ZeroTierOne.msi");
                string targetMsi = localMsi;

                if (!File.Exists(localMsi))
                {
                    targetMsi = Path.Combine(Path.GetTempPath(), "ZeroTierOne.msi");
                    Log(isFa ? "سرویس ZeroTier یافت نشد. در حال دانلود فایل نصب رسمی..." : "ZeroTier not found. Downloading official installer...", Color.FromArgb(245, 158, 11));
                    using (WebClient dl = new WebClient())
                    {
                        dl.DownloadFile("https://download.zerotier.com/dist/ZeroTier%20One.msi", targetMsi);
                    }
                }

                ct.ThrowIfCancellationRequested();
                Log(isFa ? "در حال نصب خودکار ZeroTier (حدود ۱۵ ثانیه زمان می‌برد)..." : "Installing ZeroTier silently (please wait ~15s)...", Color.FromArgb(245, 158, 11));
                Process msiProc = Process.Start(new ProcessStartInfo
                {
                    FileName = "msiexec.exe",
                    Arguments = string.Format("/i \"{0}\" /passive /norestart", targetMsi),
                    UseShellExecute = true
                });
                msiProc.WaitForExit();
                System.Threading.Thread.Sleep(4000);

                cli = GetZtCliPath();
                if (cli == null) throw new Exception(isFa ? "خطا در نصب ZeroTier." : "Failed to locate ZeroTier after installation.");
                Log(isFa ? "سرویس ZeroTier با موفقیت نصب شد!" : "ZeroTier installed successfully!", activeTheme.AccentClient);
            }

            ct.ThrowIfCancellationRequested();

            // 2. Resolve Server & Fetch Dynamic Network Info
            string targetHost = serverAddress;
            bool serverReachable = false;
            if (!string.IsNullOrEmpty(serverAddress))
            {
                if (TestTcpPort(serverAddress, serverWebPort, 600))
                {
                    Log(string.Format(isFa ? "سرور در دسترس است: {0}:{1}" : "Target server reachable at: {0}:{1}", serverAddress, serverWebPort), activeTheme.AccentClient);
                    serverReachable = true;
                }
            }

            if (!serverReachable)
            {
                ct.ThrowIfCancellationRequested();
                if (autoDiscoverLan)
                {
                    targetHost = AutoDiscoverServerLanIp();
                }
                else
                {
                    Log(string.Format(isFa ? "هشدار: سرور هدف {0}:{1} پاسخ نمی‌دهد." : "Warning: Target server {0}:{1} is unreachable.", serverAddress, serverWebPort), Color.FromArgb(239, 68, 68));
                }
            }

            ct.ThrowIfCancellationRequested();
            string myNodeId = GetLocalNodeId();

            Log(isFa ? "در حال تبادل اطلاعات اولیه با API سرور..." : "Handshaking with server API...", activeTheme.HeaderAccent);
            try
            {
                string checkUrl = string.Format("http://{0}:{1}/api/client_check{2}", targetHost, serverWebPort, string.IsNullOrEmpty(myNodeId) ? "" : "?id=" + myNodeId);
                using (TimeoutWebClient wc = new TimeoutWebClient(8000))
                {
                    string json = wc.DownloadString(checkUrl);
                    var mNet = Regex.Match(json, @"""networkId"":\s*""([0-9a-fA-F]{16})""");
                    if (mNet.Success)
                    {
                        networkId = mNet.Groups[1].Value.ToLower();
                    }
                    // The server emits "serverVirtualIp", not "serverIp".
                    var mSrvIp = Regex.Match(json, @"""serverVirtualIp"":\s*""([^""]+)""");
                    if (mSrvIp.Success && !string.IsNullOrEmpty(mSrvIp.Groups[1].Value))
                    {
                        serverVirtualIp = mSrvIp.Groups[1].Value;
                    }
                    SaveLocalConfig();
                }
            }
            catch (Exception ex)
            {
                string detail = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                Log((isFa ? "اطلاعیه هندشیک API: " : "API Handshake notice: ") + detail, Color.FromArgb(245, 158, 11));
            }

            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(networkId))
            {
                throw new Exception(isFa ? "شناسه شبکه دریافت نشد. آدرس و پورت سرور یا فایروال را بررسی کنید." : "Could not retrieve Network ID from server. Check server address/port or firewall.");
            }

            try
            {
                using (TimeoutWebClient wc = new TimeoutWebClient(5000, bootstrapToken))
                {
                    wc.DownloadString(string.Format("http://{0}:{1}/api/client_connect?id={2}", targetHost, serverWebPort, myNodeId));
                }
            }
            catch { }

            ct.ThrowIfCancellationRequested();

            // 3. Download & Sync Moon Root
            string moonsDir = @"C:\ProgramData\ZeroTier\One\moons.d";
            if (!Directory.Exists(moonsDir)) Directory.CreateDirectory(moonsDir);

            string moonFile = Path.Combine(moonsDir, "openmesh_root.moon");
            string moonTmp = moonFile + ".download";
            bool downloadedMoon = false;
            try
            {
                // Download to a temp file first: WebClient.DownloadFile
                // truncates (and on failure deletes) the destination, which
                // would destroy a working moon root.
                using (TimeoutWebClient wc = new TimeoutWebClient(8000, bootstrapToken))
                {
                    wc.DownloadFile(string.Format("http://{0}:{1}/download/moon", targetHost, serverWebPort), moonTmp);
                }
                // Only replace the live moon once the new one is on disk.
                if (File.Exists(moonTmp) && new FileInfo(moonTmp).Length > 0)
                {
                    if (File.Exists(moonFile)) File.Delete(moonFile);
                    File.Move(moonTmp, moonFile);
                    downloadedMoon = true;
                    Log(isFa ? "فایل Moon سرور با موفقیت همگام‌سازی و دانلود شد." : "Downloaded latest synced Moon root from server.", activeTheme.AccentClient);
                }
                else
                {
                    Log(isFa ? "فایل Moon دانلود شد اما خالی بود؛ فایل قبلی حفظ شد." : "Downloaded Moon was empty; keeping the previous moon.", Color.FromArgb(245, 158, 11));
                }
            }
            catch (Exception ex)
            {
                Log((isFa ? "اطلاعیه دانلود Moon: " : "Moon download notice: ") + ex.Message, Color.FromArgb(245, 158, 11));
            }
            finally
            {
                try { if (File.Exists(moonTmp)) File.Delete(moonTmp); } catch { }
            }

            ct.ThrowIfCancellationRequested();

            // 4. Restart ZeroTier Service to load new moon root
            if (downloadedMoon)
            {
                Log(isFa ? "راه‌اندازی مجدد سرویس جهت اعمال ساختار Moon..." : "Restarting ZeroTier service to apply Moon root...", activeTheme.HeaderAccent);
                RestartService();
                System.Threading.Thread.Sleep(3000);
            }

            ct.ThrowIfCancellationRequested();

            // 5. Join Network
            Log((isFa ? "در حال اتصال به شبکه اوپن‌مش: " : "Joining OpenMesh Network (") + networkId + (isFa ? "..." : ")..."), activeTheme.HeaderAccent);
            RunCommand(cli, "join " + networkId);

            // 6. Poll for Assigned IP
            Log(isFa ? "در حال دریافت آی‌پی اختصاصی شبکه..." : "Waiting for virtual IP assignment...", Color.FromArgb(245, 158, 11));
            string assignedIp = isFa ? "در حال تخصیص..." : "Assigning...";
            for (int i = 0; i < 15; i++)
            {
                if (ct.IsCancellationRequested)
                {
                    if (cli != null && !string.IsNullOrEmpty(networkId)) RunCommand(cli, "leave " + networkId);
                    ct.ThrowIfCancellationRequested();
                }
                System.Threading.Thread.Sleep(1000);
                string outNet = RunCommand(cli, "listnetworks");
                if (outNet.Contains("ACCESS_DENIED"))
                {
                    if (VerifyServerBannedState())
                    {
                        SetBannedState(true, "ZeroTier Controller reports ACCESS_DENIED");
                        throw new Exception(isFa ? "دسترسی شما توسط ادمین مسدود شده است." : "You are BANNED from this network by the administrator.");
                    }
                    else
                    {
                        Log(isFa ? "در حال احراز هویت با کنترلر..." : "Authorizing with controller...", Color.FromArgb(245, 158, 11));
                        continue;
                    }
                }
                var m = Regex.Match(outNet, @"(\d+\.\d+\.\d+\.\d+)/\d+");
                if (m.Success)
                {
                    assignedIp = m.Groups[1].Value;
                    break;
                }
            }

            try
            {
                using (TimeoutWebClient wc = new TimeoutWebClient(1500, bootstrapToken))
                {
                    wc.DownloadString(string.Format("http://{0}:{1}/api/client_connect?id={2}", targetHost, serverWebPort, GetLocalNodeId()));
                }
            }
            catch { }

            PostToUi(() =>
            {
                lblClientIp.Text = assignedIp;
                UpdateStatusCardsText();
                Log((isFa ? "آی‌پی مجازی گیمینگ شما: " : "Assigned Virtual IP: ") + assignedIp, activeTheme.AccentClient);
            });
        }

        private async Task ImportNewMoonAsync()
        {
            bool isFa = (currentLanguage == "fa");
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Title = isFa ? "انتخاب فایل ZeroTier Moon" : "Select ZeroTier Moon File";
                ofd.Filter = "ZeroTier Moon Files (*.moon)|*.moon|All Files (*.*)|*.*";
                ofd.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    string selectedPath = ofd.FileName;
                    string fileName = Path.GetFileName(selectedPath);

                    Log((isFa ? "در حال وارد کردن فایل Moon: " : "Importing new Moon: ") + fileName + "...", activeTheme.AccentServer);
                    progressBar.Visible = true;
                    progressBar.Style = ProgressBarStyle.Marquee;

                    try
                    {
                        await Task.Run(() =>
                        {
                            string moonsDir = @"C:\ProgramData\ZeroTier\One\moons.d";
                            if (!Directory.Exists(moonsDir)) Directory.CreateDirectory(moonsDir);

                            string target = Path.Combine(moonsDir, fileName);
                            File.Copy(selectedPath, target, true);

                            Log(isFa ? "راه‌اندازی مجدد سرویس جهت فعال‌سازی Moon..." : "Restarting ZeroTier service to apply new Moon...", Color.FromArgb(245, 158, 11));
                            RestartService();
                            System.Threading.Thread.Sleep(2500);
                        });

                        Log((isFa ? "فایل Moon با موفقیت فعال شد: " : "Moon imported and activated: ") + fileName, activeTheme.AccentClient);
                        string infoMsg = isFa
                            ? "فایل Moon جدید با موفقیت اعمال شد!\n\nفایل: " + fileName
                            : "New Moon file imported and applied successfully!\n\nFile: " + fileName;
                        MessageBox.Show(infoMsg, isFa ? "ایمپورت موفق" : "Moon Imported", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        Log((isFa ? "خطا در ایمپورت Moon: " : "Import error: ") + ex.Message, Color.FromArgb(239, 68, 68));
                        MessageBox.Show((isFa ? "خطا در وارد کردن فایل: " : "Error importing Moon file: ") + ex.Message,
                            isFa ? "خطای ایمپورت" : "Import Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    finally
                    {
                        progressBar.Visible = false;
                    }
                    await RunPeriodicPingAsync();
                }
            }
        }

        private static string EscapeJsonValue(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");
        }

        private string GetConfigFilePath()
        {
            // Prefer %AppData% so the config stays writable when the exe lives
            // in a read-only location such as Program Files.
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (!string.IsNullOrEmpty(appData))
                {
                    string dir = Path.Combine(appData, "OpenMeshGaming");
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    return Path.Combine(dir, "openmesh_client_config.json");
                }
            }
            catch { }

            try
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (string.IsNullOrEmpty(dir)) dir = AppDomain.CurrentDomain.BaseDirectory;
                return Path.Combine(dir, "openmesh_client_config.json");
            }
            catch
            {
                return "openmesh_client_config.json";
            }
        }

        private void LoadLocalConfig()
        {
            try
            {
                string path = GetConfigFilePath();
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var mPort = Regex.Match(json, @"""serverPort"":\s*(\d+)");
                    if (mPort.Success)
                    {
                        int p;
                        if (int.TryParse(mPort.Groups[1].Value, out p) && p >= 1 && p <= 65535)
                        {
                            serverWebPort = p;
                        }
                    }
                    var mHost = Regex.Match(json, @"""serverAddress"":\s*""([^""]+)""");
                    if (mHost.Success && !string.IsNullOrEmpty(mHost.Groups[1].Value))
                    {
                        serverAddress = mHost.Groups[1].Value.Trim();
                    }
                    var mNet = Regex.Match(json, @"""networkId"":\s*""([^""]+)""");
                    if (mNet.Success && !string.IsNullOrEmpty(mNet.Groups[1].Value))
                    {
                        networkId = mNet.Groups[1].Value.Trim();
                    }
                    var mSrvIp = Regex.Match(json, @"""serverVirtualIp"":\s*""([^""]+)""");
                    if (mSrvIp.Success && !string.IsNullOrEmpty(mSrvIp.Groups[1].Value))
                    {
                        serverVirtualIp = mSrvIp.Groups[1].Value.Trim();
                    }
                    var mPeerIp = Regex.Match(json, @"""peerVirtualIp"":\s*""([^""]+)""");
                    if (mPeerIp.Success && !string.IsNullOrEmpty(mPeerIp.Groups[1].Value))
                    {
                        peerVirtualIp = mPeerIp.Groups[1].Value.Trim();
                    }
                    var mAuto = Regex.Match(json, @"""autoDiscover"":\s*(true|false)");
                    if (mAuto.Success)
                    {
                        autoDiscoverLan = (mAuto.Groups[1].Value.ToLower() == "true");
                    }
                    var mLang = Regex.Match(json, @"""language"":\s*""([^""]+)""");
                    if (mLang.Success)
                    {
                        string l = mLang.Groups[1].Value.Trim().ToLower();
                        if (l == "fa" || l == "en") currentLanguage = l;
                    }
                    var mBoot = Regex.Match(json, @"""bootstrapToken"":\s*""([^""]+)""");
                    if (mBoot.Success)
                    {
                        bootstrapToken = mBoot.Groups[1].Value.Trim();
                    }
                    var mTheme = Regex.Match(json, @"""theme"":\s*""([^""]+)""");
                    if (mTheme.Success)
                    {
                        string t = mTheme.Groups[1].Value.Trim().ToLower();
                        if (t == "cyber_dark" || t == "cyber_purple" || t == "clean_light") currentTheme = t;
                    }
                }
            }
            catch { }
        }

        private void SaveLocalConfig()
        {
            try
            {
                string path = GetConfigFilePath();
                string json = string.Format(
                    "{{\n  \"serverAddress\": \"{0}\",\n  \"serverPort\": {1},\n  \"autoDiscover\": {2},\n  \"networkId\": \"{3}\",\n  \"serverVirtualIp\": \"{4}\",\n  \"peerVirtualIp\": \"{5}\",\n  \"language\": \"{6}\",\n  \"theme\": \"{7}\",\n  \"bootstrapToken\": \"{8}\"\n}}",
                    EscapeJsonValue(serverAddress),
                    serverWebPort,
                    autoDiscoverLan ? "true" : "false",
                    EscapeJsonValue(networkId),
                    EscapeJsonValue(serverVirtualIp),
                    EscapeJsonValue(peerVirtualIp),
                    EscapeJsonValue(currentLanguage),
                    EscapeJsonValue(currentTheme),
                    EscapeJsonValue(bootstrapToken)
                );
                // Atomic write: concurrent saves from the ping timer and the
                // discovery thread must not truncate the file.
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch { }
        }

        private void ShowSettingsDialog()
        {
            bool isFa = (currentLanguage == "fa");
            using (Form dlg = new Form())
            {
                dlg.Text = isFa ? "تنظیمات کلاینت اوپن‌مش" : "OpenMesh Gaming - Settings";
                dlg.Size = new Size(500, 560);
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                dlg.BackColor = activeTheme.FormBack;
                dlg.ForeColor = activeTheme.TextPrimary;
                dlg.Font = new Font("Segoe UI", 9.5f);

                Label lblTitle = new Label
                {
                    Text = isFa ? "⚙️ تنظیمات برنامه و شبکه" : "⚙️ Preferences & Network Settings",
                    Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                    ForeColor = activeTheme.HeaderAccent,
                    Location = new Point(22, 16),
                    AutoSize = true
                };

                // 1. Language
                Label lblLang = new Label
                {
                    Text = isFa ? "زبان برنامه (Interface Language):" : "Interface Language / زبان:",
                    ForeColor = activeTheme.TextMuted,
                    Location = new Point(22, 52),
                    AutoSize = true,
                    Font = new Font("Segoe UI", 9f, FontStyle.Bold)
                };

                ComboBox cmbLang = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Location = new Point(24, 74),
                    Size = new Size(436, 26),
                    BackColor = activeTheme.CardBack,
                    ForeColor = activeTheme.TextPrimary,
                    FlatStyle = FlatStyle.Flat
                };
                cmbLang.Items.AddRange(new object[] { "English", "فارسی (Persian)" });
                cmbLang.SelectedIndex = (currentLanguage == "fa") ? 1 : 0;

                // 2. Theme
                Label lblTheme = new Label
                {
                    Text = isFa ? "تم ظاهری (Visual Theme):" : "Visual Theme / تم ظاهری:",
                    ForeColor = activeTheme.TextMuted,
                    Location = new Point(22, 110),
                    AutoSize = true,
                    Font = new Font("Segoe UI", 9f, FontStyle.Bold)
                };

                ComboBox cmbTheme = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Location = new Point(24, 132),
                    Size = new Size(436, 26),
                    BackColor = activeTheme.CardBack,
                    ForeColor = activeTheme.TextPrimary,
                    FlatStyle = FlatStyle.Flat
                };
                cmbTheme.Items.AddRange(new object[] {
                    "Cyber Dark (تاریک سایبری)",
                    "Cyberpunk Purple (بنفش نئونی)",
                    "Clean Light (روشن مینیمال)"
                });
                if (currentTheme == "cyber_purple") cmbTheme.SelectedIndex = 1;
                else if (currentTheme == "clean_light") cmbTheme.SelectedIndex = 2;
                else cmbTheme.SelectedIndex = 0;

                // Divider Panel
                Panel pnlDivider = new Panel
                {
                    Location = new Point(24, 172),
                    Size = new Size(436, 1),
                    BackColor = activeTheme.CardBorder
                };

                // 3. Server Address
                Label lblHost = new Label
                {
                    Text = isFa ? "آدرس سرور مرکزی (IP پابلیک یا محلی):" : "Server Address (Public IP or LAN):",
                    ForeColor = activeTheme.TextMuted,
                    Location = new Point(22, 186),
                    AutoSize = true,
                    Font = new Font("Segoe UI", 9f, FontStyle.Bold)
                };
                TextBox txtHost = new TextBox
                {
                    Text = serverAddress,
                    Location = new Point(24, 208),
                    Size = new Size(436, 25),
                    BackColor = activeTheme.CardBack,
                    ForeColor = activeTheme.AccentClient,
                    Font = new Font("Consolas", 10.5f, FontStyle.Bold),
                    BorderStyle = BorderStyle.FixedSingle
                };

                // 4. Web Port
                Label lblPort = new Label
                {
                    Text = isFa ? "پورت وب / API سرور:" : "Dashboard Web / API Port:",
                    ForeColor = activeTheme.TextMuted,
                    Location = new Point(22, 242),
                    AutoSize = true,
                    Font = new Font("Segoe UI", 9f, FontStyle.Bold)
                };
                TextBox txtPort = new TextBox
                {
                    Text = serverWebPort.ToString(),
                    Location = new Point(24, 264),
                    Size = new Size(436, 25),
                    BackColor = activeTheme.CardBack,
                    ForeColor = activeTheme.AccentServer,
                    Font = new Font("Consolas", 10.5f, FontStyle.Bold),
                    BorderStyle = BorderStyle.FixedSingle
                };

                // 5. Peer IP
                Label lblPeer = new Label
                {
                    Text = isFa ? "آی‌پی مجازی هاست یا بازیکن هدف (جهت تست پینگ):" : "Host / Target Peer Virtual IP (for latency card):",
                    ForeColor = activeTheme.TextMuted,
                    Location = new Point(22, 298),
                    AutoSize = true,
                    Font = new Font("Segoe UI", 9f, FontStyle.Bold)
                };
                TextBox txtPeer = new TextBox
                {
                    Text = peerVirtualIp,
                    Location = new Point(24, 320),
                    Size = new Size(436, 25),
                    BackColor = activeTheme.CardBack,
                    ForeColor = activeTheme.AccentHost,
                    Font = new Font("Consolas", 10.5f, FontStyle.Bold),
                    BorderStyle = BorderStyle.FixedSingle
                };

                // 6. Bootstrap Token
                Label lblBoot = new Label
                {
                    Text = isFa ? "توکن Bootstrap (از سرور مرکزی):" : "Bootstrap Token (shown by the server on install):",
                    ForeColor = activeTheme.TextMuted,
                    Location = new Point(22, 356),
                    AutoSize = true,
                    Font = new Font("Segoe UI", 9f, FontStyle.Bold)
                };
                TextBox txtBoot = new TextBox
                {
                    Text = bootstrapToken,
                    Location = new Point(24, 378),
                    Size = new Size(436, 25),
                    BackColor = activeTheme.CardBack,
                    ForeColor = activeTheme.TextPrimary,
                    Font = new Font("Consolas", 9.5f),
                    BorderStyle = BorderStyle.FixedSingle
                };

                // 7. Auto-discover
                CheckBox chkAuto = new CheckBox
                {
                    Text = isFa ? "اسکن خودکار شبکه محلی در صورت در دسترس نبودن آدرس سرور" : "Auto-scan LAN if server address is unreachable",
                    Checked = autoDiscoverLan,
                    ForeColor = activeTheme.TextMuted,
                    Location = new Point(24, 414),
                    AutoSize = true,
                    Font = new Font("Segoe UI", 8.5f)
                };

                // Buttons
                Button btnOk = new Button
                {
                    Text = isFa ? "ذخیره تنظیمات" : "Save Settings",
                    DialogResult = DialogResult.OK,
                    Location = new Point(210, 452),
                    Size = new Size(135, 36),
                    BackColor = Color.FromArgb(16, 185, 129),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Cursor = Cursors.Hand,
                    Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
                };
                btnOk.FlatAppearance.BorderSize = 0;

                Button btnCancel = new Button
                {
                    Text = isFa ? "انصراف" : "Cancel",
                    DialogResult = DialogResult.Cancel,
                    Location = new Point(355, 452),
                    Size = new Size(105, 36),
                    BackColor = activeTheme.BtnDarkBack,
                    ForeColor = activeTheme.TextMuted,
                    FlatStyle = FlatStyle.Flat,
                    Cursor = Cursors.Hand,
                    Font = new Font("Segoe UI", 9.5f)
                };
                btnCancel.FlatAppearance.BorderColor = activeTheme.BtnDarkBorder;

                dlg.Controls.AddRange(new Control[] {
                    lblTitle, lblLang, cmbLang, lblTheme, cmbTheme, pnlDivider,
                    lblHost, txtHost, lblPort, txtPort, lblPeer, txtPeer,
                    lblBoot, txtBoot, chkAuto, btnOk, btnCancel
                });
                dlg.AcceptButton = btnOk;
                dlg.CancelButton = btnCancel;

                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    int val;
                    if (int.TryParse(txtPort.Text.Trim(), out val) && val >= 1 && val <= 65535)
                    {
                        string host = txtHost.Text.Trim();
                        if (string.IsNullOrEmpty(host)) host = "127.0.0.1";
                        serverAddress = host;
                        serverWebPort = val;
                        peerVirtualIp = txtPeer.Text.Trim();
                        bootstrapToken = txtBoot.Text.Trim();
                        autoDiscoverLan = chkAuto.Checked;

                        // Language
                        currentLanguage = (cmbLang.SelectedIndex == 1) ? "fa" : "en";

                        // Theme
                        if (cmbTheme.SelectedIndex == 1) currentTheme = "cyber_purple";
                        else if (cmbTheme.SelectedIndex == 2) currentTheme = "clean_light";
                        else currentTheme = "cyber_dark";

                        SaveLocalConfig();
                        ApplyTheme();
                        ApplyLocalization();

                        string okMsg = (currentLanguage == "fa")
                            ? string.Format("تنظیمات با موفقیت ذخیره شد (سرور: {0}:{1} | تم: {2} | زبان: {3})", serverAddress, serverWebPort, currentTheme, currentLanguage)
                            : string.Format("Settings saved successfully (Server: {0}:{1} | Theme: {2} | Lang: {3})", serverAddress, serverWebPort, currentTheme, currentLanguage);
                        Log(okMsg, activeTheme.AccentClient);

                        if (isConnected)
                        {
                            Task.Run(async () => await RunPeriodicPingAsync());
                        }
                    }
                    else
                    {
                        string errPort = isFa
                            ? "لطفاً یک شماره پورت معتبر بین ۱ تا ۶۵۵۳۵ وارد نمایید."
                            : "Please enter a valid port number between 1 and 65535.";
                        MessageBox.Show(this, errPort, isFa ? "پورت نامعتبر" : "Invalid Port", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            }
        }

        private string GetLocalNodeId()
        {
            if (!string.IsNullOrEmpty(localNodeId)) return localNodeId;
            string cli = GetZtCliPath();
            if (cli == null) return "";
            string outInfo = RunCommand(cli, "info");
            var m = Regex.Match(outInfo, @"200 (?:info|status) ([0-9a-fA-F]{10})");
            if (m.Success)
            {
                localNodeId = m.Groups[1].Value.ToLower();
                return localNodeId;
            }
            return "";
        }

        private bool VerifyServerBannedState()
        {
            try
            {
                string myId = GetLocalNodeId();
                string url = string.Format("http://{0}:{1}/api/client_check{2}", serverAddress, serverWebPort, string.IsNullOrEmpty(myId) ? "" : "?id=" + myId);
                using (TimeoutWebClient wc = new TimeoutWebClient(1500))
                {
                    string json = wc.DownloadString(url);
                    return json.Contains("\"isBanned\": true");
                }
            }
            catch
            {
                return false;
            }
        }

        private async Task CheckKickAndNetworkStatusAsync()
        {
            if (!isConnected) return;

            await Task.Run(() =>
            {
                string myId = GetLocalNodeId();
                if (string.IsNullOrEmpty(serverAddress))
                {
                    string recovered = AutoDiscoverServerLanIp(silent: true);
                    if (!string.IsNullOrEmpty(recovered)) serverAddress = recovered;
                }
                if (string.IsNullOrEmpty(serverAddress)) return;

                try
                {
                    string url = string.Format("http://{0}:{1}/api/client_check{2}", serverAddress, serverWebPort, string.IsNullOrEmpty(myId) ? "" : "?id=" + myId);
                    using (TimeoutWebClient wc = new TimeoutWebClient(2500))
                    {
                        string json = wc.DownloadString(url);
                        bool banned = json.Contains("\"isBanned\": true");
                        bool kicked = json.Contains("\"isKicked\": true");

                        long kickTs = 0;
                        var mKickTs = Regex.Match(json, @"""kickTimestamp"":\s*(\d+)");
                        if (mKickTs.Success) long.TryParse(mKickTs.Groups[1].Value, out kickTs);

                        var mBanned = Regex.Match(json, @"""bannedMembers"":\s*\[(.*?)\]");
                        if (mBanned.Success)
                        {
                            string rawList = mBanned.Groups[1].Value;
                            var matches = Regex.Matches(rawList, @"""([0-9a-fA-F]+)""");
                            HashSet<string> currentBanned = new HashSet<string>();
                            foreach (Match match in matches)
                            {
                                string id = match.Groups[1].Value.ToLower();
                                currentBanned.Add(id);
                                if (!previousBannedMembers.Contains(id) && id != myId)
                                {
                                    PostToUi(() => Log("📢 [ADMIN] Player (" + id + ") was BANNED by administrator.", Color.FromArgb(239, 68, 68)));
                                }
                            }
                            foreach (string prevId in previousBannedMembers)
                            {
                                if (!currentBanned.Contains(prevId) && prevId != myId)
                                {
                                    PostToUi(() => Log("📢 [ADMIN] Player (" + prevId + ") was UNBANNED by administrator.", activeTheme.AccentClient));
                                }
                            }
                            previousBannedMembers = currentBanned;
                        }

                        if (banned)
                        {
                            SetBannedState(true, "Permanently Banned by administrator");
                            return;
                        }
                        else if (isBanned && !banned)
                        {
                            SetBannedState(false, "Unbanned by administrator");
                        }

                        if (kicked && kickTs > lastHandledKickTs && !isBanned)
                        {
                            lastHandledKickTs = kickTs;
                            HandleKickedByAdmin();
                            return;
                        }
                    }
                }
                catch
                {
                    string recoveredIp = AutoDiscoverServerLanIp(silent: true);
                    if (!string.IsNullOrEmpty(recoveredIp)) serverAddress = recoveredIp;
                }

                try
                {
                    string cli = GetZtCliPath();
                    if (cli != null && !string.IsNullOrEmpty(networkId))
                    {
                        string outNet = RunCommand(cli, "listnetworks");
                        if (outNet.Contains(networkId))
                        {
                            if (outNet.Contains("ACCESS_DENIED"))
                            {
                                if (VerifyServerBannedState())
                                {
                                    SetBannedState(true, "ZeroTier Controller reports ACCESS_DENIED");
                                    return;
                                }
                            }
                        }
                    }
                }
                catch { }
            });
        }

        private void HandleKickedByAdmin()
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;
            PostToUi(() =>
            {
                bool isFa = (currentLanguage == "fa");
                isConnected = false;
                clientStatusState = "kicked";
                serverStatusState = "disconnected";
                hostStatusState = "disconnected";
                lastServerPing = null;
                lastHostPing = null;

                UpdateConnectButtonText();
                UpdateStatusCardsText();

                Log("⚠️ ==================================================", Color.FromArgb(245, 158, 11));
                Log(isFa ? "⚠️ [هشدار] شما توسط مدیر سرور از سشن بازی اخراج شدید!" : "⚠️ [ALERT] YOU WERE KICKED FROM THE GAME SESSION BY ADMINISTRATOR!", Color.FromArgb(245, 158, 11));
                Log(isFa ? "⚠️ ارتباط شما با سشن فعلی قطع شد. شما بن نشده‌اید و می‌توانید مجدداً متصل شوید." : "⚠️ You were disconnected from current session. You are not banned and may reconnect.", Color.FromArgb(245, 158, 11));
                Log("⚠️ ==================================================", Color.FromArgb(245, 158, 11));

                Task.Run(() =>
                {
                    string cli = GetZtCliPath();
                    if (cli != null && !string.IsNullOrEmpty(networkId)) RunCommand(cli, "leave " + networkId);
                });

                string msgKicked = isFa
                    ? "شما توسط مدیر شبکه از سشن بازی اخراج شدید.\n\nارتباط شما پایان یافت، اما شما بن نشده‌اید و هر زمان بخواهید می‌توانید دوباره متصل شوید."
                    : "You were kicked from the gaming session by the network administrator.\n\nYour session was terminated, but you are NOT banned and may reconnect whenever you are ready.";

                MessageBox.Show(this, msgKicked, isFa ? "⚠️ اخراج از سشن بازی" : "⚠️ Kicked from Session", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            });
        }

        private void SetBannedState(bool banned, string reason)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;
            PostToUi(() =>
            {
                bool isFa = (currentLanguage == "fa");
                if (banned)
                {
                    if (!isBanned)
                    {
                        isBanned = true;
                        isConnected = false;
                        clientStatusState = "banned";
                        serverStatusState = "blocked";
                        hostStatusState = "blocked";
                        lastServerPing = null;
                        lastHostPing = null;

                        UpdateConnectButtonText();
                        UpdateStatusCardsText();

                        Log("⛔ ==================================================", Color.FromArgb(239, 68, 68));
                        Log(isFa ? "⛔ [هشدار] دسترسی شما توسط مدیر سرور به طور دائم مسدود (بن) شد!" : "⛔ [ALERT] YOU HAVE BEEN BANNED BY THE NETWORK ADMINISTRATOR!", Color.FromArgb(239, 68, 68));
                        Log((isFa ? "⛔ دلیل: " : "⛔ Reason: ") + reason, Color.FromArgb(245, 158, 11));
                        Log(isFa ? "⛔ دسترسی به شبکه قطع گردید. جهت رفع بن با هاست تماس بگیرید." : "⛔ Your network access has been blocked. Contact host to unban.", Color.FromArgb(239, 68, 68));
                        Log("⛔ ==================================================", Color.FromArgb(239, 68, 68));

                        Task.Run(() =>
                        {
                            string cli = GetZtCliPath();
                            if (cli != null && !string.IsNullOrEmpty(networkId)) RunCommand(cli, "leave " + networkId);
                        });

                        string msgBan = isFa
                            ? "شما توسط مدیر سرور مسدود (بن) شدید!\n\nدسترسی شما به شبکه مسدود شده است. لطفاً جهت رفع بن با مدیر سرور تماس بگیرید."
                            : "You have been banned by the network administrator!\n\nYour network access is blocked. Please contact the administrator to unban your node.";

                        MessageBox.Show(this, msgBan, isFa ? "⛔ دسترسی مسدود" : "⛔ Access Blocked", MessageBoxButtons.OK, MessageBoxIcon.Stop);
                    }
                }
                else
                {
                    if (isBanned)
                    {
                        isBanned = false;
                        clientStatusState = "unbanned";
                        serverStatusState = "disconnected";
                        hostStatusState = "disconnected";
                        lastServerPing = null;
                        lastHostPing = null;

                        UpdateConnectButtonText();
                        UpdateStatusCardsText();

                        string unbanLog = isFa
                            ? "✅ [اطلاعیه] مسدودیت شما توسط مدیر برطرف شد! اکنون می‌توانید به شبکه متصل شوید."
                            : "✅ [NOTICE] You have been unbanned by administrator! You can now reconnect to LAN.";
                        Log(unbanLog, activeTheme.AccentClient);
                    }
                }
            });
        }

        private async Task RunPeriodicPingAsync()
        {
            if (!isConnected)
            {
                if (this.IsDisposed || !this.IsHandleCreated) return;
                try
                {
                    PostToUi(() =>
                    {
                        serverStatusState = "disconnected";
                        hostStatusState = "disconnected";
                        lastServerPing = null;
                        lastHostPing = null;
                        UpdateStatusCardsText();
                    });
                }
                catch { }
                return;
            }

            if (isBanned) return;

            await CheckKickAndNetworkStatusAsync();

            try
            {
                long? pingServer = await PingHostAsync(serverVirtualIp);
                long? pingPeer = await PingHostAsync(peerVirtualIp);

                if (this.IsDisposed || !this.IsHandleCreated) return;

                PostToUi(() =>
                {
                    lastServerPing = pingServer;
                    if (string.IsNullOrEmpty(serverVirtualIp))
                        serverStatusState = "disconnected";
                    else
                        serverStatusState = pingServer.HasValue ? "online" : "offline";

                    lastHostPing = pingPeer;
                    // No peer address configured means "unknown", not
                    // "unreachable" - reporting it as offline would pin the
                    // HOST card to a permanent false negative.
                    if (string.IsNullOrEmpty(peerVirtualIp))
                        hostStatusState = "disconnected";
                    else
                        hostStatusState = pingPeer.HasValue ? "online" : "offline";

                    clientStatusState = "connected";
                    UpdateStatusCardsText();
                });
            }
            catch { }
        }

        private async Task RunManualPingAsync()
        {
            bool isFa = (currentLanguage == "fa");
            if (!isConnected)
            {
                string msgDisc = isFa
                    ? "در حال حاضر از شبکه قطع هستید. برای بررسی پینگ واقعی ابتدا به شبکه متصل شوید."
                    : "You are currently disconnected from LAN. Connect first to test real network ping.";
                Log(msgDisc, Color.FromArgb(245, 158, 11));
                return;
            }

            Log(isFa ? "در حال ارسال درخواست تست پینگ..." : "Testing live ping...", activeTheme.HeaderAccent);
            long? p1 = await PingHostAsync(serverVirtualIp);
            long? p2 = await PingHostAsync(peerVirtualIp);

            string srvResult = p1.HasValue ? p1.Value + " ms (OK)" : (isFa ? "تایم‌اوت" : "Timeout");
            Log((isFa ? "سرور مرکزی (" : "Server Core (") + serverVirtualIp + "): " + srvResult,
                p1.HasValue ? activeTheme.AccentClient : Color.FromArgb(239, 68, 68));

            string peerResult = p2.HasValue ? p2.Value + " ms (OK)" : (isFa ? "تایم‌اوت" : "Timeout");
            Log((isFa ? "هاست / بازیکن هدف (" : "Host / Peer (") + peerVirtualIp + "): " + peerResult,
                p2.HasValue ? activeTheme.AccentClient : Color.FromArgb(239, 68, 68));
        }

        private async Task<long?> PingHostAsync(string ip)
        {
            // No address configured means "unknown", not "unreachable". The
            // caller distinguishes the two so the HOST card is not pinned to
            // a permanent false "Offline".
            if (string.IsNullOrEmpty(ip)) return null;
            return await Task.Run(() =>
            {
                try
                {
                    if (string.IsNullOrEmpty(ip)) return (long?)null;
                    using (Ping p = new Ping())
                    {
                        PingReply reply = p.Send(ip, 1200);
                        if (reply.Status == IPStatus.Success) return (long?)reply.RoundtripTime;
                    }
                }
                catch { }
                return null;
            });
        }

        private async Task ToggleServiceAsync()
        {
            bool isFa = (currentLanguage == "fa");
            await Task.Run(() =>
            {
                try
                {
                    using (ServiceController sc = new ServiceController("ZeroTierOneService"))
                    {
                        if (sc.Status == ServiceControllerStatus.Running)
                        {
                            Log(isFa ? "در حال توقف سرویس ZeroTier..." : "Stopping ZeroTier service...", Color.FromArgb(245, 158, 11));
                            sc.Stop();
                            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                            Log(isFa ? "سرویس متوقف شد. مصرف پس‌زمینه صفر گردید." : "ZeroTier service stopped. 0 background usage.", activeTheme.AccentClient);
                            isConnected = false;
                            clientStatusState = "disconnected";
                            serverStatusState = "disconnected";
                            hostStatusState = "disconnected";
                            lastServerPing = null;
                            lastHostPing = null;
                            PostToUi(() =>
                            {
                                btnServiceToggle.Text = isFa ? "▶ شروع سرویس" : "▶ Start Service";
                                UpdateConnectButtonText();
                                UpdateStatusCardsText();
                            });
                        }
                        else
                        {
                            Log(isFa ? "در حال شروع سرویس ZeroTier..." : "Starting ZeroTier service...", activeTheme.HeaderAccent);
                            sc.Start();
                            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                            Log(isFa ? "سرویس ZeroTier فعال گردید." : "ZeroTier service started.", activeTheme.AccentClient);
                            PostToUi(() =>
                            {
                                btnServiceToggle.Text = isFa ? "⏹ توقف سرویس" : "⏹ Stop Service";
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log((isFa ? "خطای سرویس: " : "Service Error: ") + ex.Message, Color.FromArgb(239, 68, 68));
                }
            });
        }

        private void RestartService()
        {
            try
            {
                using (ServiceController sc = new ServiceController("ZeroTierOneService"))
                {
                    if (sc.Status == ServiceControllerStatus.Running)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(8));
                    }
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(8));
                }
            }
            catch
            {
                Process pStop = Process.Start(new ProcessStartInfo { FileName = "net", Arguments = "stop ZeroTierOneService", WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true });
                if (pStop != null) pStop.WaitForExit();
                Process pStart = Process.Start(new ProcessStartInfo { FileName = "net", Arguments = "start ZeroTierOneService", WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true });
                if (pStart != null) pStart.WaitForExit();
            }
        }

        private string RunCommand(string file, string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = file,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (Process p = Process.Start(psi))
                {
                    string outText = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(8000);
                    return outText;
                }
            }
            catch (Exception ex)
            {
                return "Error: " + ex.Message;
            }
        }

        [STAThread]
        public static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    public class TimeoutWebClient : WebClient
    {
        private int timeout;
        private string bootstrapToken;

        public TimeoutWebClient(int timeoutMs) : this(timeoutMs, null) { }

        public TimeoutWebClient(int timeoutMs, string bootstrapToken)
        {
            this.timeout = timeoutMs;
            this.bootstrapToken = bootstrapToken;
            this.Proxy = null; // Bypass system/VPN proxy for direct connection
            if (!string.IsNullOrEmpty(bootstrapToken))
            {
                this.Headers.Add("X-OpenMesh-Bootstrap", bootstrapToken);
            }
        }

        protected override WebRequest GetWebRequest(Uri address)
        {
            WebRequest w = base.GetWebRequest(address);
            w.Timeout = timeout;
            w.Proxy = null;
            if (w is HttpWebRequest)
            {
                ((HttpWebRequest)w).ReadWriteTimeout = timeout;
            }
            return w;
        }
    }
}

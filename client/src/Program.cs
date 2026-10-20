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
    public class MainForm : Form
    {
        // Runtime Config Defaults (Overridden by openmesh_client_config.json)
        private string serverAddress = "127.0.0.1";
        private int serverWebPort = 8080;
        private bool autoDiscoverLan = true;
        private string networkId = "";
        private string serverVirtualIp = "10.147.20.1";
        private string peerVirtualIp = "10.147.20.2";

        // UI Components
        private Panel cardServer;
        private Panel cardHost;
        private Panel cardClient;

        private Label lblServerStatus;
        private Label lblServerPing;
        private Label lblServerSub;
        private Label lblHostStatus;
        private Label lblHostPing;
        private Label lblHostSub;
        private Label lblClientIp;
        private Label lblClientStatus;

        private Button btnConnect;
        private Button btnImportMoon;
        private Button btnPortConfig;
        private Button btnPing;
        private Button btnServiceToggle;
        private Panel pnlTerminal;
        private RichTextBox txtLog;
        private ProgressBar progressBar;

        // State & Background Loop
        private Timer pingTimer;
        private bool isConnected = false;
        private bool isBusy = false;
        private bool isBanned = false;
        private long lastHandledKickTs = 0;
        private string localNodeId = "";
        private HashSet<string> previousBannedMembers = new HashSet<string>();

        public MainForm()
        {
            this.DoubleBuffered = true;
            LoadLocalConfig();
            InitializeComponent();
            var _ = CheckCurrentStatusAsync();

            pingTimer = new Timer();
            pingTimer.Interval = 3000;
            pingTimer.Tick += async (s, e) => await RunPeriodicPingAsync();
            pingTimer.Start();
        }

        private void InitializeComponent()
        {
            this.Text = "OpenMesh Gaming - Autonomous Cyber Mesh LAN";
            this.ClientSize = new Size(775, 620);
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(11, 13, 20); // Deep Space Cyber Canvas
            this.ForeColor = Color.White;
            this.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

            // --- HEADER ---
            Panel pnlHeader = new Panel { Location = new Point(25, 16), Size = new Size(725, 60), BackColor = Color.Transparent };

            Label lblLogo = new Label
            {
                Text = "⚡ OPENMESH GAMING",
                Font = new Font("Segoe UI", 16f, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 229, 255), // Neon Cyan
                Location = new Point(0, 4),
                AutoSize = true
            };

            Label lblSubtitle = new Label
            {
                Text = "High-Performance Peer-to-Peer Virtual LAN Mesh • Offline Autonomous Core",
                Font = new Font("Segoe UI", 9f, FontStyle.Regular),
                ForeColor = Color.FromArgb(120, 135, 165),
                Location = new Point(2, 34),
                AutoSize = true
            };

            Label lblBadge = new Label
            {
                Text = "OFFLINE ENGINE",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 230, 118),
                BackColor = Color.FromArgb(16, 36, 30),
                BorderStyle = BorderStyle.FixedSingle,
                TextAlign = ContentAlignment.MiddleCenter,
                Size = new Size(130, 28),
                Location = new Point(595, 14)
            };

            btnPortConfig = new Button
            {
                Text = "⚙️ " + serverAddress + ":" + serverWebPort,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248),
                BackColor = Color.FromArgb(18, 26, 42),
                FlatStyle = FlatStyle.Flat,
                Size = new Size(165, 28),
                Location = new Point(420, 14),
                Cursor = Cursors.Hand
            };
            btnPortConfig.FlatAppearance.BorderColor = Color.FromArgb(40, 60, 95);
            btnPortConfig.FlatAppearance.MouseOverBackColor = Color.FromArgb(28, 40, 66);
            btnPortConfig.Click += (s, e) => ShowPortConfigDialog();

            pnlHeader.Controls.AddRange(new Control[] { lblLogo, lblSubtitle, btnPortConfig, lblBadge });
            this.Controls.Add(pnlHeader);

            // --- 3 STATUS CARDS ---
            // Card 1: Server Core
            cardServer = CreateCyberCard(25, 84, 230, 105, Color.FromArgb(0, 210, 255));
            Label lblSrvTitle = new Label { Text = "SERVER CORE", ForeColor = Color.FromArgb(160, 175, 205), Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), Location = new Point(14, 12), AutoSize = true };
            lblServerSub = new Label { Text = "IP: " + serverVirtualIp, ForeColor = Color.FromArgb(90, 105, 135), Font = new Font("Consolas", 8.5f), Location = new Point(14, 32), AutoSize = true };
            lblServerStatus = new Label { Text = "● Disconnected", ForeColor = Color.FromArgb(130, 140, 160), Font = new Font("Segoe UI", 11f, FontStyle.Bold), Location = new Point(14, 52), AutoSize = true };
            lblServerPing = new Label { Text = "Latency: --", ForeColor = Color.FromArgb(180, 195, 220), Font = new Font("Segoe UI", 8.5f), Location = new Point(14, 76), AutoSize = true };
            cardServer.Controls.AddRange(new Control[] { lblSrvTitle, lblServerSub, lblServerStatus, lblServerPing });
            this.Controls.Add(cardServer);

            // Card 2: Host / Primary Peer
            cardHost = CreateCyberCard(272, 84, 230, 105, Color.FromArgb(168, 85, 247));
            Label lblHstTitle = new Label { Text = "HOST / PEER", ForeColor = Color.FromArgb(160, 175, 205), Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), Location = new Point(14, 12), AutoSize = true };
            lblHostSub = new Label { Text = "IP: " + peerVirtualIp, ForeColor = Color.FromArgb(90, 105, 135), Font = new Font("Consolas", 8.5f), Location = new Point(14, 32), AutoSize = true };
            lblHostStatus = new Label { Text = "● Disconnected", ForeColor = Color.FromArgb(130, 140, 160), Font = new Font("Segoe UI", 11f, FontStyle.Bold), Location = new Point(14, 52), AutoSize = true };
            lblHostPing = new Label { Text = "Latency: --", ForeColor = Color.FromArgb(180, 195, 220), Font = new Font("Segoe UI", 8.5f), Location = new Point(14, 76), AutoSize = true };
            cardHost.Controls.AddRange(new Control[] { lblHstTitle, lblHostSub, lblHostStatus, lblHostPing });
            this.Controls.Add(cardHost);

            // Card 3: Your Gaming IP
            cardClient = CreateCyberCard(520, 84, 230, 105, Color.FromArgb(16, 185, 129));
            Label lblCliTitle = new Label { Text = "YOUR GAMING IP", ForeColor = Color.FromArgb(160, 175, 205), Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), Location = new Point(14, 12), AutoSize = true };
            lblClientStatus = new Label { Text = "Status: Disconnected", ForeColor = Color.FromArgb(239, 68, 68), Font = new Font("Segoe UI", 8.5f), Location = new Point(14, 32), AutoSize = true };
            lblClientIp = new Label
            {
                Text = "--.--.--.--",
                ForeColor = Color.FromArgb(16, 185, 129),
                Font = new Font("Consolas", 13f, FontStyle.Bold),
                Location = new Point(14, 54),
                AutoSize = true,
                Cursor = Cursors.Hand
            };
            lblClientIp.Click += (s, e) =>
            {
                if (lblClientIp.Text != "--.--.--.--" && lblClientIp.Text != "Assigning...")
                {
                    Clipboard.SetText(lblClientIp.Text);
                    Log("Copied your IP (" + lblClientIp.Text + ") to clipboard!", Color.FromArgb(16, 185, 129));
                }
            };
            Label lblCopyHint = new Label { Text = "(Click IP to Copy)", ForeColor = Color.FromArgb(75, 85, 110), Font = new Font("Segoe UI", 7.5f), Location = new Point(14, 79), AutoSize = true };
            cardClient.Controls.AddRange(new Control[] { lblCliTitle, lblClientStatus, lblClientIp, lblCopyHint });
            this.Controls.Add(cardClient);

            // --- ACTION BUTTONS ---
            // 1. Big Action Button
            btnConnect = new Button();
            btnConnect.Text = "CONNECT TO GAMING LAN";
            btnConnect.Font = new Font("Segoe UI", 11.5f, FontStyle.Bold);
            btnConnect.BackColor = Color.FromArgb(16, 185, 129); // Emerald Green
            btnConnect.ForeColor = Color.White;
            btnConnect.FlatStyle = FlatStyle.Flat;
            btnConnect.FlatAppearance.BorderSize = 0;
            btnConnect.Location = new Point(25, 205);
            btnConnect.Size = new Size(310, 50);
            btnConnect.Cursor = Cursors.Hand;
            btnConnect.Click += async (s, e) => await ToggleConnectionAsync();
            this.Controls.Add(btnConnect);

            // 2. Import Moon Button
            btnImportMoon = CreateCyberButton("📂 Import Moon", Color.FromArgb(56, 189, 248), new Point(350, 205), new Size(130, 50));
            btnImportMoon.Click += async (s, e) => await ImportNewMoonAsync();
            this.Controls.Add(btnImportMoon);

            // 3. Test Ping Button
            btnPing = CreateCyberButton("⚡ Test Ping", Color.FromArgb(0, 229, 255), new Point(495, 205), new Size(115, 50));
            btnPing.Click += async (s, e) => await RunManualPingAsync();
            this.Controls.Add(btnPing);

            // 4. Service Toggle Button
            btnServiceToggle = CreateCyberButton("⏹ Stop Service", Color.FromArgb(245, 158, 11), new Point(625, 205), new Size(125, 50));
            btnServiceToggle.Click += async (s, e) => await ToggleServiceAsync();
            this.Controls.Add(btnServiceToggle);

            // Progress Bar
            progressBar = new ProgressBar();
            progressBar.Location = new Point(25, 265);
            progressBar.Size = new Size(725, 4);
            progressBar.Style = ProgressBarStyle.Continuous;
            progressBar.Visible = false;
            this.Controls.Add(progressBar);

            // --- CYBER TERMINAL CONTAINER ---
            pnlTerminal = new Panel();
            pnlTerminal.Location = new Point(25, 276);
            pnlTerminal.Size = new Size(725, 320);
            pnlTerminal.BackColor = Color.FromArgb(7, 9, 14);
            pnlTerminal.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (Pen pen = new Pen(Color.FromArgb(30, 38, 56), 1))
                {
                    e.Graphics.DrawRectangle(pen, 0, 0, pnlTerminal.Width - 1, pnlTerminal.Height - 1);
                }
            };

            // Terminal Header Bar
            Panel pnlTermBar = new Panel { Location = new Point(1, 1), Size = new Size(723, 30), BackColor = Color.FromArgb(14, 18, 28) };

            Panel dotRed = new Panel { Size = new Size(10, 10), Location = new Point(14, 10), BackColor = Color.FromArgb(239, 68, 68) };
            Panel dotYellow = new Panel { Size = new Size(10, 10), Location = new Point(30, 10), BackColor = Color.FromArgb(245, 158, 11) };
            Panel dotGreen = new Panel { Size = new Size(10, 10), Location = new Point(46, 10), BackColor = Color.FromArgb(16, 185, 129) };

            Label lblTermTitle = new Label
            {
                Text = "REALTIME ENGINE CONSOLE • AUTONOMOUS OFFLINE STACK",
                Font = new Font("Consolas", 8.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(110, 125, 155),
                Location = new Point(70, 7),
                AutoSize = true
            };

            Button btnClearLog = new Button
            {
                Text = "Clear",
                Font = new Font("Segoe UI", 7.5f),
                ForeColor = Color.FromArgb(120, 135, 160),
                BackColor = Color.Transparent,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(50, 22),
                Location = new Point(660, 4),
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
            txtLog.BackColor = Color.FromArgb(7, 9, 14);
            txtLog.ForeColor = Color.FromArgb(215, 225, 245);
            txtLog.Font = new Font("Consolas", 9.5f, FontStyle.Regular);
            txtLog.BorderStyle = BorderStyle.None;
            txtLog.ReadOnly = true;
            pnlTerminal.Controls.Add(txtLog);

            this.Controls.Add(pnlTerminal);

            Log("Ready. OpenMesh Gaming Engine initialized.", Color.FromArgb(0, 229, 255));
        }

        private Panel CreateCyberCard(int x, int y, int w, int h, Color accentColor)
        {
            Panel p = new Panel();
            p.Location = new Point(x, y);
            p.Size = new Size(w, h);
            p.BackColor = Color.FromArgb(18, 22, 34);
            p.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle rect = new Rectangle(0, 0, p.Width - 1, p.Height - 1);
                using (Pen borderPen = new Pen(Color.FromArgb(32, 40, 60), 1))
                {
                    e.Graphics.DrawRectangle(borderPen, rect);
                }
                using (SolidBrush accentBrush = new SolidBrush(accentColor))
                {
                    e.Graphics.FillRectangle(accentBrush, 0, 0, p.Width, 3);
                }
            };
            return p;
        }

        private Button CreateCyberButton(string text, Color accentColor, Point location, Size size)
        {
            Button btn = new Button();
            btn.Text = text;
            btn.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            btn.BackColor = Color.FromArgb(20, 25, 38);
            btn.ForeColor = accentColor;
            btn.FlatStyle = FlatStyle.Flat;
            btn.FlatAppearance.BorderColor = Color.FromArgb(40, 50, 75);
            btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(30, 38, 58);
            btn.Location = location;
            btn.Size = size;
            btn.Cursor = Cursors.Hand;
            return btn;
        }

        private void Log(string message, Color? color = null)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => Log(message, color)));
                return;
            }

            txtLog.SelectionStart = txtLog.TextLength;
            txtLog.SelectionLength = 0;
            txtLog.SelectionColor = color ?? Color.FromArgb(215, 225, 245);
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
                        this.Invoke(new Action(() =>
                        {
                            isConnected = true;
                            btnConnect.Text = "DISCONNECT FROM LAN";
                            btnConnect.BackColor = Color.FromArgb(239, 68, 68);
                            lblClientStatus.Text = "Status: Connected";
                            lblClientStatus.ForeColor = Color.FromArgb(16, 185, 129);
                            lblClientIp.Text = match.Success ? match.Groups[1].Value : "10.147.20.xxx";
                            Log("Already joined network " + networkId, Color.FromArgb(16, 185, 129));
                        }));
                    }
                }
            });
            await RunPeriodicPingAsync();
        }

        private async Task ToggleConnectionAsync()
        {
            if (isBusy) return;
            if (isBanned)
            {
                bool srvBanned = await Task.Run(() => VerifyServerBannedState());
                if (srvBanned)
                {
                    MessageBox.Show(this, "You have been banned by the network administrator.\nPlease contact the administrator to unban your node.", "⛔ Access Denied", MessageBoxButtons.OK, MessageBoxIcon.Stop);
                    return;
                }
                else
                {
                    SetBannedState(false, "Verified not banned");
                }
            }
            isBusy = true;
            btnConnect.Enabled = false;
            progressBar.Visible = true;
            progressBar.Style = ProgressBarStyle.Marquee;

            try
            {
                if (isConnected)
                {
                    Log("Disconnecting from LAN...", Color.FromArgb(245, 158, 11));
                    await Task.Run(() =>
                    {
                        string myId = GetLocalNodeId();
                        try
                        {
                            using (TimeoutWebClient wc = new TimeoutWebClient(1500))
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
                    btnConnect.Text = "CONNECT TO GAMING LAN";
                    btnConnect.BackColor = Color.FromArgb(16, 185, 129);
                    lblClientStatus.Text = "Status: Disconnected";
                    lblClientStatus.ForeColor = Color.FromArgb(239, 68, 68);
                    lblClientIp.Text = "--.--.--.--";
                    lblServerStatus.Text = "● Disconnected";
                    lblServerStatus.ForeColor = Color.FromArgb(130, 140, 160);
                    lblServerPing.Text = "Latency: --";
                    lblHostStatus.Text = "● Disconnected";
                    lblHostStatus.ForeColor = Color.FromArgb(130, 140, 160);
                    lblHostPing.Text = "Latency: --";
                    Log("Disconnected successfully.", Color.FromArgb(16, 185, 129));
                }
                else
                {
                    Log("Starting connection process...", Color.FromArgb(0, 229, 255));
                    await Task.Run(() => PerformFullConnect());

                    isConnected = true;
                    btnConnect.Text = "DISCONNECT FROM LAN";
                    btnConnect.BackColor = Color.FromArgb(239, 68, 68);
                    lblClientStatus.Text = "Status: Connected";
                    lblClientStatus.ForeColor = Color.FromArgb(16, 185, 129);
                }
            }
            catch (Exception ex)
            {
                Log("Error: " + ex.Message, Color.FromArgb(239, 68, 68));
            }
            finally
            {
                isBusy = false;
                btnConnect.Enabled = true;
                progressBar.Visible = false;
            }
            await RunPeriodicPingAsync();
        }

        private string AutoDiscoverServerLanIp(bool silent = false)
        {
            if (!silent) Log("Searching for OpenMesh Server on local network...", Color.FromArgb(0, 229, 255));

            // 1. Check current configured address with port
            if (TestTcpPort(serverAddress, serverWebPort, 400))
            {
                if (!silent) Log(string.Format("Found server at current address: {0}:{1}", serverAddress, serverWebPort), Color.FromArgb(16, 185, 129));
                return serverAddress;
            }

            // 2. Fast Subnet Scan on local /24 using configured port
            string localIp = GetPrimaryLocalIp();
            if (!string.IsNullOrEmpty(localIp))
            {
                int lastDot = localIp.LastIndexOf('.');
                if (lastDot > 0)
                {
                    string prefix = localIp.Substring(0, lastDot + 1);
                    if (!silent) Log(string.Format("Scanning subnet {0}0/24 on port {1}...", prefix, serverWebPort), Color.FromArgb(245, 158, 11));

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
                        if (!silent) Log(string.Format("Discovered server on LAN: {0}:{1}", foundIp, serverWebPort), Color.FromArgb(16, 185, 129));
                        serverAddress = foundIp;
                        SaveLocalConfig();
                        return foundIp;
                    }
                }
            }

            // 3. Common fallback ports check
            int[] fallbackPorts = new int[] { 8080, 8888, 9090, 8000, 80 };
            foreach (int testPort in fallbackPorts)
            {
                if (testPort == serverWebPort) continue;
                if (TestTcpPort(serverAddress, testPort, 350))
                {
                    if (!silent) Log(string.Format("Auto-detected server on port: {0}!", testPort), Color.FromArgb(16, 185, 129));
                    serverWebPort = testPort;
                    UpdatePortButtonText();
                    SaveLocalConfig();
                    return serverAddress;
                }
            }

            if (!silent) Log("Using configured server address: " + serverAddress, Color.FromArgb(245, 158, 11));
            return serverAddress;
        }

        private bool TestTcpPort(string host, int port, int timeoutMs)
        {
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    var result = client.BeginConnect(host, port, null, null);
                    bool success = result.AsyncWaitHandle.WaitOne(timeoutMs);
                    if (success && client.Connected)
                    {
                        client.EndConnect(result);
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private string GetPrimaryLocalIp()
        {
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

        private void PerformFullConnect()
        {
            string cli = GetZtCliPath();

            // 1. Install ZeroTier if missing
            if (cli == null)
            {
                string localMsi = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ZeroTierOne.msi");
                string targetMsi = localMsi;

                if (!File.Exists(localMsi))
                {
                    targetMsi = Path.Combine(Path.GetTempPath(), "ZeroTierOne.msi");
                    Log("ZeroTier not found. Downloading official installer...", Color.FromArgb(245, 158, 11));
                    using (WebClient dl = new WebClient())
                    {
                        dl.DownloadFile("https://download.zerotier.com/dist/ZeroTier%20One.msi", targetMsi);
                    }
                }

                Log("Installing ZeroTier silently (please wait ~15s)...", Color.FromArgb(245, 158, 11));
                Process msiProc = Process.Start(new ProcessStartInfo
                {
                    FileName = "msiexec.exe",
                    Arguments = string.Format("/i \"{0}\" /passive /norestart", targetMsi),
                    UseShellExecute = true
                });
                msiProc.WaitForExit();
                System.Threading.Thread.Sleep(4000);

                cli = GetZtCliPath();
                if (cli == null) throw new Exception("Failed to locate ZeroTier after installation.");
                Log("ZeroTier installed successfully!", Color.FromArgb(16, 185, 129));
            }

            // 2. Resolve Server & Fetch Dynamic Network Info
            string targetHost = serverAddress;
            bool serverReachable = false;
            if (!string.IsNullOrEmpty(serverAddress))
            {
                if (TestTcpPort(serverAddress, serverWebPort, 600))
                {
                    Log(string.Format("Target server reachable at: {0}:{1}", serverAddress, serverWebPort), Color.FromArgb(16, 185, 129));
                    serverReachable = true;
                }
            }

            if (!serverReachable)
            {
                if (autoDiscoverLan)
                {
                    targetHost = AutoDiscoverServerLanIp();
                }
                else
                {
                    Log(string.Format("Warning: Target server {0}:{1} is unreachable.", serverAddress, serverWebPort), Color.FromArgb(239, 68, 68));
                }
            }

            string myNodeId = GetLocalNodeId();

            // Handshake with server API to retrieve network ID and dynamic settings
            Log("Handshaking with server API...", Color.FromArgb(0, 229, 255));
            try
            {
                string checkUrl = string.Format("http://{0}:{1}/api/client_check{2}", targetHost, serverWebPort, string.IsNullOrEmpty(myNodeId) ? "" : "?id=" + myNodeId);
                using (TimeoutWebClient wc = new TimeoutWebClient(3000))
                {
                    string json = wc.DownloadString(checkUrl);
                    var mNet = Regex.Match(json, @"""networkId"":\s*""([0-9a-fA-F]{16})""");
                    if (mNet.Success)
                    {
                        networkId = mNet.Groups[1].Value.ToLower();
                    }
                    var mSrvIp = Regex.Match(json, @"""serverIp"":\s*""([^""]+)""");
                    if (mSrvIp.Success && !string.IsNullOrEmpty(mSrvIp.Groups[1].Value))
                    {
                        serverVirtualIp = mSrvIp.Groups[1].Value;
                    }
                    SaveLocalConfig();
                }
            }
            catch (Exception ex)
            {
                Log("API Handshake notice: " + ex.Message, Color.FromArgb(245, 158, 11));
            }

            if (string.IsNullOrEmpty(networkId))
            {
                throw new Exception("Could not retrieve Network ID from server. Check server address/port or firewall.");
            }

            // Notify server of connection attempt
            try
            {
                using (TimeoutWebClient wc = new TimeoutWebClient(1500))
                {
                    wc.DownloadString(string.Format("http://{0}:{1}/api/client_connect?id={2}", targetHost, serverWebPort, myNodeId));
                }
            }
            catch { }

            // 3. Download & Sync Moon Root
            string moonsDir = @"C:\ProgramData\ZeroTier\One\moons.d";
            if (!Directory.Exists(moonsDir)) Directory.CreateDirectory(moonsDir);

            string moonFile = Path.Combine(moonsDir, "openmesh_root.moon");
            bool downloadedMoon = false;
            try
            {
                using (WebClient wc = new WebClient())
                {
                    wc.DownloadFile(string.Format("http://{0}:{1}/download/moon", targetHost, serverWebPort), moonFile);
                    downloadedMoon = true;
                    Log("Downloaded latest synced Moon root from server.", Color.FromArgb(16, 185, 129));
                }
            }
            catch (Exception ex)
            {
                Log("Moon download notice: " + ex.Message, Color.FromArgb(245, 158, 11));
            }

            // 4. Restart ZeroTier Service to load new moon root
            if (downloadedMoon)
            {
                Log("Restarting ZeroTier service to apply Moon root...", Color.FromArgb(0, 229, 255));
                RestartService();
                System.Threading.Thread.Sleep(3000);
            }

            // 5. Join Network
            Log("Joining OpenMesh Network (" + networkId + ")...", Color.FromArgb(0, 229, 255));
            RunCommand(cli, "join " + networkId);

            // 6. Poll for Assigned IP
            Log("Waiting for virtual IP assignment...", Color.FromArgb(245, 158, 11));
            string assignedIp = "Assigning...";
            for (int i = 0; i < 15; i++)
            {
                System.Threading.Thread.Sleep(1000);
                string outNet = RunCommand(cli, "listnetworks");
                if (outNet.Contains("ACCESS_DENIED"))
                {
                    if (VerifyServerBannedState())
                    {
                        SetBannedState(true, "ZeroTier Controller reports ACCESS_DENIED");
                        throw new Exception("You are BANNED from this network by the administrator.");
                    }
                    else
                    {
                        Log("Authorizing with controller...", Color.FromArgb(245, 158, 11));
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

            // Confirm connection with server
            try
            {
                using (TimeoutWebClient wc = new TimeoutWebClient(1500))
                {
                    wc.DownloadString(string.Format("http://{0}:{1}/api/client_connect?id={2}", targetHost, serverWebPort, GetLocalNodeId()));
                }
            }
            catch { }

            this.Invoke(new Action(() =>
            {
                lblClientIp.Text = assignedIp;
                lblServerSub.Text = "IP: " + serverVirtualIp;
                lblHostSub.Text = "IP: " + peerVirtualIp;
                Log("Assigned Virtual IP: " + assignedIp, Color.FromArgb(16, 185, 129));
            }));
        }

        private async Task ImportNewMoonAsync()
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Title = "Select ZeroTier Moon File";
                ofd.Filter = "ZeroTier Moon Files (*.moon)|*.moon|All Files (*.*)|*.*";
                ofd.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    string selectedPath = ofd.FileName;
                    string fileName = Path.GetFileName(selectedPath);

                    Log("Importing new Moon: " + fileName + "...", Color.FromArgb(56, 189, 248));
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

                            Log("Restarting ZeroTier service to apply new Moon...", Color.FromArgb(245, 158, 11));
                            RestartService();
                            System.Threading.Thread.Sleep(2500);
                        });

                        Log("Moon imported and activated: " + fileName, Color.FromArgb(16, 185, 129));
                        MessageBox.Show("New Moon file imported and applied successfully!\n\nFile: " + fileName,
                            "Moon Imported", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        Log("Import error: " + ex.Message, Color.FromArgb(239, 68, 68));
                        MessageBox.Show("Error importing Moon file: " + ex.Message,
                            "Import Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    finally
                    {
                        progressBar.Visible = false;
                    }
                    await RunPeriodicPingAsync();
                }
            }
        }

        private string GetConfigFilePath()
        {
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
                    "{{\n  \"serverAddress\": \"{0}\",\n  \"serverPort\": {1},\n  \"autoDiscover\": {2},\n  \"networkId\": \"{3}\",\n  \"serverVirtualIp\": \"{4}\",\n  \"peerVirtualIp\": \"{5}\"\n}}",
                    serverAddress,
                    serverWebPort,
                    autoDiscoverLan ? "true" : "false",
                    networkId,
                    serverVirtualIp,
                    peerVirtualIp
                );
                File.WriteAllText(path, json);
            }
            catch { }
        }

        private void UpdatePortButtonText()
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(UpdatePortButtonText));
                return;
            }
            if (btnPortConfig != null)
            {
                string display = serverAddress + ":" + serverWebPort;
                if (display.Length > 20) display = display.Substring(0, 17) + "...";
                btnPortConfig.Text = "⚙️ " + display;
            }
        }

        private void ShowPortConfigDialog()
        {
            using (Form dlg = new Form())
            {
                dlg.Text = "OpenMesh - Server & Network Configuration";
                dlg.Size = new Size(460, 390);
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                dlg.BackColor = Color.FromArgb(14, 18, 28);
                dlg.ForeColor = Color.White;
                dlg.Font = new Font("Segoe UI", 9.5f);

                Label lblTitle = new Label { Text = "Server Core & Network Settings", Font = new Font("Segoe UI", 11f, FontStyle.Bold), ForeColor = Color.FromArgb(0, 229, 255), Location = new Point(20, 15), AutoSize = true };

                Label lblHost = new Label { Text = "Server Address (Public IP, DDNS, or LAN IP):", ForeColor = Color.FromArgb(200, 215, 235), Location = new Point(20, 44), AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
                TextBox txtHost = new TextBox { Text = serverAddress, Location = new Point(22, 68), Size = new Size(400, 25), BackColor = Color.FromArgb(7, 9, 14), ForeColor = Color.FromArgb(16, 185, 129), Font = new Font("Consolas", 10.5f, FontStyle.Bold), BorderStyle = BorderStyle.FixedSingle };

                Label lblPort = new Label { Text = "Dashboard Web / API Port:", ForeColor = Color.FromArgb(200, 215, 235), Location = new Point(20, 102), AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
                TextBox txtPort = new TextBox { Text = serverWebPort.ToString(), Location = new Point(22, 124), Size = new Size(400, 25), BackColor = Color.FromArgb(7, 9, 14), ForeColor = Color.FromArgb(56, 189, 248), Font = new Font("Consolas", 10.5f, FontStyle.Bold), BorderStyle = BorderStyle.FixedSingle };

                Label lblPeer = new Label { Text = "Host / Primary Peer Virtual IP (for Latency Card):", ForeColor = Color.FromArgb(200, 215, 235), Location = new Point(20, 158), AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
                TextBox txtPeer = new TextBox { Text = peerVirtualIp, Location = new Point(22, 180), Size = new Size(400, 25), BackColor = Color.FromArgb(7, 9, 14), ForeColor = Color.FromArgb(168, 85, 247), Font = new Font("Consolas", 10.5f, FontStyle.Bold), BorderStyle = BorderStyle.FixedSingle };

                CheckBox chkAuto = new CheckBox { Text = "Auto-scan LAN if server address is unreachable", Checked = autoDiscoverLan, ForeColor = Color.FromArgb(160, 175, 200), Location = new Point(22, 216), AutoSize = true, Font = new Font("Segoe UI", 8.5f) };

                Button btnOk = new Button { Text = "Save Settings", DialogResult = DialogResult.OK, Location = new Point(205, 290), Size = new Size(115, 34), BackColor = Color.FromArgb(16, 185, 129), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand };
                btnOk.FlatAppearance.BorderSize = 0;

                Button btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(330, 290), Size = new Size(92, 34), BackColor = Color.FromArgb(30, 40, 60), ForeColor = Color.FromArgb(200, 210, 230), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand };
                btnCancel.FlatAppearance.BorderSize = 0;

                dlg.Controls.AddRange(new Control[] { lblTitle, lblHost, txtHost, lblPort, txtPort, lblPeer, txtPeer, chkAuto, btnOk, btnCancel });
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
                        autoDiscoverLan = chkAuto.Checked;
                        UpdatePortButtonText();
                        lblHostSub.Text = "IP: " + peerVirtualIp;
                        SaveLocalConfig();
                        Log(string.Format("Target server set to {0}:{1} (LAN Scan: {2})", serverAddress, serverWebPort, autoDiscoverLan ? "ON" : "OFF"), Color.FromArgb(16, 185, 129));
                        if (isConnected)
                        {
                            Task.Run(async () => await RunPeriodicPingAsync());
                        }
                    }
                    else
                    {
                        MessageBox.Show(this, "Please enter a valid port number between 1 and 65535.", "Invalid Port", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

                // 1. Check server client_check endpoint
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

                        // Check notifications for other players being banned
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
                                    this.Invoke(new Action(() => Log("📢 [ADMIN] Player (" + id + ") was BANNED by administrator.", Color.FromArgb(239, 68, 68))));
                                }
                            }
                            foreach (string prevId in previousBannedMembers)
                            {
                                if (!currentBanned.Contains(prevId) && prevId != myId)
                                {
                                    this.Invoke(new Action(() => Log("📢 [ADMIN] Player (" + prevId + ") was UNBANNED by administrator.", Color.FromArgb(16, 185, 129))));
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

                // 2. Also check local ZeroTier CLI status
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
            this.Invoke(new Action(() =>
            {
                isConnected = false;

                lblClientStatus.Text = "Status: ⚠️ Kicked by Admin";
                lblClientStatus.ForeColor = Color.FromArgb(245, 158, 11);

                lblClientIp.Text = "--.--.--.--";
                lblClientIp.ForeColor = Color.FromArgb(245, 158, 11);

                btnConnect.Text = "CONNECT TO GAMING LAN";
                btnConnect.BackColor = Color.FromArgb(16, 185, 129);
                btnConnect.Enabled = true;

                lblServerStatus.Text = "● Disconnected";
                lblServerStatus.ForeColor = Color.FromArgb(130, 140, 160);
                lblServerPing.Text = "Latency: --";

                lblHostStatus.Text = "● Disconnected";
                lblHostStatus.ForeColor = Color.FromArgb(130, 140, 160);
                lblHostPing.Text = "Latency: --";

                Log("⚠️ ==================================================", Color.FromArgb(245, 158, 11));
                Log("⚠️ [ALERT] YOU WERE KICKED FROM THE GAME SESSION BY ADMINISTRATOR!", Color.FromArgb(245, 158, 11));
                Log("⚠️ You were disconnected from the current session. You can reconnect.", Color.FromArgb(245, 158, 11));
                Log("⚠️ ==================================================", Color.FromArgb(245, 158, 11));

                Task.Run(() =>
                {
                    string cli = GetZtCliPath();
                    if (cli != null && !string.IsNullOrEmpty(networkId)) RunCommand(cli, "leave " + networkId);
                });

                MessageBox.Show(this,
                    "You were kicked from the gaming session by the network administrator.\n\nYour session was terminated, but you are NOT banned and may reconnect whenever you are ready.",
                    "⚠️ Kicked from Session",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }));
        }

        private void SetBannedState(bool banned, string reason)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;
            this.Invoke(new Action(() =>
            {
                if (banned)
                {
                    if (!isBanned)
                    {
                        isBanned = true;
                        isConnected = false;

                        lblClientStatus.Text = "Status: ⛔ BANNED BY ADMIN";
                        lblClientStatus.ForeColor = Color.FromArgb(239, 68, 68);

                        lblClientIp.Text = "ACCESS DENIED";
                        lblClientIp.ForeColor = Color.FromArgb(239, 68, 68);

                        btnConnect.Text = "⛔ BANNED FROM LAN";
                        btnConnect.BackColor = Color.FromArgb(185, 28, 28);
                        btnConnect.Enabled = false;

                        lblServerStatus.Text = "● Blocked";
                        lblServerStatus.ForeColor = Color.FromArgb(239, 68, 68);
                        lblServerPing.Text = "Latency: Blocked";

                        lblHostStatus.Text = "● Blocked";
                        lblHostStatus.ForeColor = Color.FromArgb(239, 68, 68);
                        lblHostPing.Text = "Latency: Blocked";

                        Log("⛔ ==================================================", Color.FromArgb(239, 68, 68));
                        Log("⛔ [ALERT] YOU HAVE BEEN BANNED BY THE NETWORK ADMINISTRATOR!", Color.FromArgb(239, 68, 68));
                        Log("⛔ Reason: " + reason, Color.FromArgb(245, 158, 11));
                        Log("⛔ Your network access has been blocked. Contact host to unban.", Color.FromArgb(239, 68, 68));
                        Log("⛔ ==================================================", Color.FromArgb(239, 68, 68));

                        Task.Run(() =>
                        {
                            string cli = GetZtCliPath();
                            if (cli != null && !string.IsNullOrEmpty(networkId)) RunCommand(cli, "leave " + networkId);
                        });

                        MessageBox.Show(this,
                            "You have been banned by the network administrator!\n\nYour network access is blocked. Please contact the administrator to unban your node.",
                            "⛔ Access Blocked",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Stop);
                    }
                }
                else
                {
                    if (isBanned)
                    {
                        isBanned = false;
                        lblClientStatus.Text = "Status: Disconnected (Unbanned)";
                        lblClientStatus.ForeColor = Color.FromArgb(16, 185, 129);

                        lblClientIp.Text = "--.--.--.--";
                        lblClientIp.ForeColor = Color.FromArgb(16, 185, 129);

                        btnConnect.Text = "CONNECT TO GAMING LAN";
                        btnConnect.BackColor = Color.FromArgb(16, 185, 129);
                        btnConnect.Enabled = true;

                        Log("✅ [NOTICE] You have been unbanned by administrator! You can now reconnect to LAN.", Color.FromArgb(16, 185, 129));
                    }
                }
            }));
        }

        private async Task RunPeriodicPingAsync()
        {
            if (!isConnected)
            {
                if (this.IsDisposed || !this.IsHandleCreated) return;
                try
                {
                    this.Invoke(new Action(() =>
                    {
                        lblServerStatus.Text = "● Disconnected";
                        lblServerStatus.ForeColor = Color.FromArgb(130, 140, 160);
                        lblServerPing.Text = "Latency: --";

                        lblHostStatus.Text = "● Disconnected";
                        lblHostStatus.ForeColor = Color.FromArgb(130, 140, 160);
                        lblHostPing.Text = "Latency: --";
                    }));
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

                this.Invoke(new Action(() =>
                {
                    if (pingServer.HasValue)
                    {
                        lblServerStatus.Text = "● Online";
                        lblServerStatus.ForeColor = Color.FromArgb(16, 185, 129);
                        lblServerPing.Text = "Latency: " + pingServer.Value + " ms";
                    }
                    else
                    {
                        lblServerStatus.Text = "● Offline";
                        lblServerStatus.ForeColor = Color.FromArgb(239, 68, 68);
                        lblServerPing.Text = "Latency: Timeout";
                    }

                    if (pingPeer.HasValue)
                    {
                        lblHostStatus.Text = "● Online";
                        lblHostStatus.ForeColor = Color.FromArgb(16, 185, 129);
                        lblHostPing.Text = "Latency: " + pingPeer.Value + " ms";
                    }
                    else
                    {
                        lblHostStatus.Text = "● Offline";
                        lblHostStatus.ForeColor = Color.FromArgb(239, 68, 68);
                        lblHostPing.Text = "Latency: Timeout";
                    }
                }));
            }
            catch { }
        }

        private async Task RunManualPingAsync()
        {
            if (!isConnected)
            {
                Log("You are currently disconnected from LAN. Connect first to test real network ping.", Color.FromArgb(245, 158, 11));
                return;
            }

            Log("Testing live ping...", Color.FromArgb(0, 229, 255));
            long? p1 = await PingHostAsync(serverVirtualIp);
            long? p2 = await PingHostAsync(peerVirtualIp);

            Log("Server Core (" + serverVirtualIp + "): " + (p1.HasValue ? p1.Value + " ms (OK)" : "Timeout"),
                p1.HasValue ? Color.FromArgb(16, 185, 129) : Color.FromArgb(239, 68, 68));

            Log("Host / Peer (" + peerVirtualIp + "): " + (p2.HasValue ? p2.Value + " ms (OK)" : "Timeout"),
                p2.HasValue ? Color.FromArgb(16, 185, 129) : Color.FromArgb(239, 68, 68));
        }

        private async Task<long?> PingHostAsync(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return null;
            return await Task.Run(() =>
            {
                try
                {
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
            await Task.Run(() =>
            {
                try
                {
                    using (ServiceController sc = new ServiceController("ZeroTierOneService"))
                    {
                        if (sc.Status == ServiceControllerStatus.Running)
                        {
                            Log("Stopping ZeroTier service...", Color.FromArgb(245, 158, 11));
                            sc.Stop();
                            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                            Log("ZeroTier service stopped. 0 background usage.", Color.FromArgb(16, 185, 129));
                            isConnected = false;
                            this.Invoke(new Action(() =>
                            {
                                btnServiceToggle.Text = "▶ Start Service";
                                btnConnect.Text = "CONNECT TO GAMING LAN";
                                btnConnect.BackColor = Color.FromArgb(16, 185, 129);
                                lblClientStatus.Text = "Status: Disconnected";
                                lblClientStatus.ForeColor = Color.FromArgb(239, 68, 68);
                                lblClientIp.Text = "--.--.--.--";
                                lblServerStatus.Text = "● Disconnected";
                                lblServerStatus.ForeColor = Color.FromArgb(130, 140, 160);
                                lblServerPing.Text = "Latency: --";
                                lblHostStatus.Text = "● Disconnected";
                                lblHostStatus.ForeColor = Color.FromArgb(130, 140, 160);
                                lblHostPing.Text = "Latency: --";
                            }));
                        }
                        else
                        {
                            Log("Starting ZeroTier service...", Color.FromArgb(0, 229, 255));
                            sc.Start();
                            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                            Log("ZeroTier service started.", Color.FromArgb(16, 185, 129));
                            this.Invoke(new Action(() => btnServiceToggle.Text = "⏹ Stop Service"));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log("Service Error: " + ex.Message, Color.FromArgb(239, 68, 68));
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
        public TimeoutWebClient(int timeoutMs)
        {
            this.timeout = timeoutMs;
        }

        protected override WebRequest GetWebRequest(Uri address)
        {
            WebRequest w = base.GetWebRequest(address);
            w.Timeout = timeout;
            return w;
        }
    }
}

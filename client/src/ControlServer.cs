using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace OpenMesh.Gaming.Client
{
    /// <summary>
    /// A small, loopback-only HTTP control channel so the client can be
    /// driven by scripts (curl, PowerShell, CI) instead of by hand.
    ///
    /// It binds to 127.0.0.1 only and every request must carry the shared
    /// token, so nothing outside this machine can reach it. The token is
    /// generated on first run and stored next to the client config.
    ///
    /// Endpoints:
    ///   GET  /status        - current state as JSON
    ///   GET  /log           - the recent client log as plain text
    ///   POST /connect       - start the connect flow
    ///   POST /disconnect    - disconnect
    ///   POST /cancel        - cancel an in-flight connect
    ///   POST /ping          - run a manual ping
    ///   POST /service       - start/stop the ZeroTier service
    ///   POST /settings      - set server address / port / token, then save
    ///   POST /quit          - close the client
    /// </summary>
    public class ControlServer
    {
        private readonly MainForm form;
        private readonly int port;
        private string token;
        private Thread listener;
        private volatile bool running = false;
        private readonly object tokenLock = new object();

        public ControlServer(MainForm form, int port)
        {
            this.form = form;
            this.port = port;
            this.token = LoadOrCreateToken();
        }

        public string Token { get { lock (tokenLock) { return token; } } }
        public int Port { get { return port; } }
        public bool IsRunning { get { return running; } }

        // ---------------- token storage ----------------

        private static string ConfigDir()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenMeshGaming");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return dir;
        }

        private static string TokenPath()
        {
            return Path.Combine(ConfigDir(), "control_token.txt");
        }

        private static string LoadOrCreateToken()
        {
            try
            {
                if (File.Exists(TokenPath()))
                {
                    string t = File.ReadAllText(TokenPath()).Trim();
                    if (t.Length >= 16) return t;
                }
            }
            catch { }

            byte[] buf = new byte[24];
            using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(buf);
            }
            string token = "omc_" + BitConverter.ToString(buf).Replace("-", "").ToLowerInvariant();
            try
            {
                File.WriteAllText(TokenPath(), token);
                // File.SetFileAttributes only exists from .NET 4.5 onwards;
                // FileInfo works on every 4.x runtime.
                FileInfo fi = new FileInfo(TokenPath());
                fi.Attributes = fi.Attributes | FileAttributes.Hidden;
            }
            catch { }
            return token;
        }

        // ---------------- lifecycle ----------------

        public void Start()
        {
            if (running) return;
            running = true;
            listener = new Thread(ListenLoop);
            listener.IsBackground = true;
            listener.Name = "OpenMeshControlServer";
            listener.Start();
        }

        public void Stop()
        {
            running = false;
            try
            {
                // Unblock the accept by connecting to ourselves.
                using (TcpClient t = new TcpClient())
                {
                    t.Connect(IPAddress.Loopback, port);
                }
            }
            catch { }
        }

        private void ListenLoop()
        {
            TcpListener listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
            }
            catch (Exception ex)
            {
                running = false;
                form.AppendLog("Control channel failed to bind: " + ex.Message, Color.FromArgb(239, 68, 68));
                return;
            }

            while (running)
            {
                try
                {
                    using (TcpClient client = listener.AcceptTcpClient())
                    {
                        client.ReceiveTimeout = 10000;
                        client.SendTimeout = 15000;
                        HandleClient(client);
                    }
                }
                catch (SocketException)
                {
                    if (!running) break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    form.AppendLog("Control channel error: " + ex.Message, Color.FromArgb(239, 68, 68));
                }
            }

            try { listener.Stop(); } catch { }
        }

        // ---------------- request handling ----------------

        private void HandleClient(TcpClient client)
        {
            try
            {
                NetworkStream ns = client.GetStream();
                string raw = ReadRequest(ns);
                if (raw == null) return;

                string[] lines = raw.Split('\n');
                string[] first = lines[0].Trim().Split(' ');
                if (first.Length < 2) { WriteResponse(ns, 400, "Bad Request", "text/plain"); return; }

                string method = first[0].ToUpperInvariant();
                string path = first[1];
                int q = path.IndexOf('?');
                string query = q >= 0 ? path.Substring(q + 1) : "";
                if (q >= 0) path = path.Substring(0, q);

                Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i < lines.Length; i++)
                {
                    string ln = lines[i].Trim();
                    if (ln.Length == 0) break;
                    int c = ln.IndexOf(':');
                    if (c > 0) headers[ln.Substring(0, c).Trim()] = ln.Substring(c + 1).Trim();
                }

                // body
                string body = "";
                if (headers.ContainsKey("Content-Length"))
                {
                    int len = 0;
                    int.TryParse(headers["Content-Length"], out len);
                    if (len > 0 && len < 1024 * 1024) body = ReadBody(ns, len);
                }

                // token check
                string provided = "";
                if (headers.ContainsKey("X-OpenMesh-Control")) provided = headers["X-OpenMesh-Control"];
                else if (headers.ContainsKey("Authorization") && headers["Authorization"].StartsWith("Bearer "))
                    provided = headers["Authorization"].Substring(7).Trim();
                else if (query.Contains("token="))
                {
                    int ti = query.IndexOf("token=");
                    string v = query.Substring(ti + 6);
                    int amp = v.IndexOf('&');
                    provided = amp >= 0 ? v.Substring(0, amp) : v;
                }

                if (!TokenEquals(provided, Token))
                {
                    WriteResponse(ns, 401, "Unauthorized - bad or missing control token", "text/plain");
                    return;
                }

                Route(ns, method, path, body);
            }
            catch (Exception ex)
            {
                try
                {
                    using (TcpClient c = new TcpClient())
                    {
                    }
                }
                catch { }
            }
        }

        private bool TokenEquals(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            byte[] ba = Encoding.UTF8.GetBytes(a);
            byte[] bb = Encoding.UTF8.GetBytes(b);
            if (ba.Length != bb.Length) return false;
            int diff = 0;
            for (int i = 0; i < ba.Length; i++) diff |= ba[i] ^ bb[i];
            return diff == 0;
        }

        private void Route(NetworkStream ns, string method, string path, string body)
        {
            string p = path.TrimEnd('/').ToLowerInvariant();
            if (p.Length == 0) p = "/";

            if (p == "/status" && method == "GET")
            {
                WriteResponse(ns, 200, form.BuildStatusJson(), "application/json");
                return;
            }

            if (p == "/log" && method == "GET")
            {
                WriteResponse(ns, 200, form.GetRecentLog(), "text/plain; charset=utf-8");
                return;
            }

            if (p == "/token" && method == "GET")
            {
                WriteResponse(ns, 200, Token, "text/plain");
                return;
            }

            if (p == "/connect" && method == "POST")
            {
                form.RunOnUi(delegate { form.TriggerConnect(); });
                WriteResponse(ns, 200, "connect requested", "text/plain");
                return;
            }

            if (p == "/disconnect" && method == "POST")
            {
                form.RunOnUi(delegate { form.TriggerDisconnect(); });
                WriteResponse(ns, 200, "disconnect requested", "text/plain");
                return;
            }

            if (p == "/cancel" && method == "POST")
            {
                form.RunOnUi(delegate { form.TriggerCancel(); });
                WriteResponse(ns, 200, "cancel requested", "text/plain");
                return;
            }

            if (p == "/ping" && method == "POST")
            {
                form.RunOnUi(delegate { form.TriggerManualPing(); });
                WriteResponse(ns, 200, "ping requested", "text/plain");
                return;
            }

            if (p == "/service" && method == "POST")
            {
                string action = "toggle";
                if (body != null && body.Contains("action="))
                {
                    int ai = body.IndexOf("action=");
                    string v = body.Substring(ai + 7);
                    int amp = v.IndexOf('&');
                    action = amp >= 0 ? v.Substring(0, amp) : v;
                }
                form.RunOnUi(delegate { form.TriggerService(action); });
                WriteResponse(ns, 200, "service action requested: " + action, "text/plain");
                return;
            }

            if (p == "/settings" && method == "POST")
            {
                string result = form.ApplyControlSettings(body);
                WriteResponse(ns, 200, result, "text/plain");
                return;
            }

            if (p == "/quit" && method == "POST")
            {
                WriteResponse(ns, 200, "closing", "text/plain");
                form.RunOnUi(delegate { form.TriggerQuit(); });
                return;
            }

            WriteResponse(ns, 404, "Unknown control endpoint: " + path, "text/plain");
        }

        // ---------------- plumbing ----------------

        private string ReadRequest(NetworkStream ns)
        {
            MemoryStream ms = new MemoryStream();
            byte[] buf = new byte[1];
            int state = 0;      // 0 = reading line, 1 = reading headers
            int contentLength = 0;
            string line = "";
            while (ms.Length < 64 * 1024)
            {
                int n = ns.Read(buf, 0, 1);
                if (n <= 0) break;
                ms.Write(buf, 0, 1);
                if (buf[0] == 10)
                {
                    string l = line.TrimEnd('\r');
                    line = "";
                    if (l.Length == 0) break;      // end of headers
                    if (l.ToLowerInvariant().StartsWith("content-length:"))
                    {
                        int cl;
                        if (int.TryParse(l.Substring(15).Trim(), out cl)) contentLength = cl;
                    }
                }
                else if (buf[0] != 13)
                {
                    line += (char)buf[0];
                }
            }
            if (ms.Length == 0) return null;
            string head = Encoding.UTF8.GetString(ms.ToArray());
            if (contentLength > 0 && contentLength < 1024 * 1024)
            {
                byte[] bodyBuf = new byte[contentLength];
                int got = 0;
                while (got < contentLength)
                {
                    int n = ns.Read(bodyBuf, got, contentLength - got);
                    if (n <= 0) break;
                    got += n;
                }
                return head + "\r\n\r\n" + Encoding.UTF8.GetString(bodyBuf, 0, got);
            }
            return head;
        }

        private string ReadBody(NetworkStream ns, int len)
        {
            byte[] buf = new byte[len];
            int got = 0;
            while (got < len)
            {
                int n = ns.Read(buf, got, len - got);
                if (n <= 0) break;
                got += n;
            }
            return Encoding.UTF8.GetString(buf, 0, got);
        }

        private void WriteResponse(NetworkStream ns, int code, string body, string contentType)
        {
            byte[] data = Encoding.UTF8.GetBytes(body ?? "");
            string reason = code == 200 ? "OK" : (code == 401 ? "Unauthorized" : (code == 404 ? "Not Found" : "Bad Request"));
            StringBuilder head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(code).Append(" ").Append(reason).Append("\r\n");
            head.Append("Content-Type: ").Append(contentType).Append("\r\n");
            head.Append("Content-Length: ").Append(data.Length).Append("\r\n");
            head.Append("Cache-Control: no-store\r\n");
            head.Append("Connection: close\r\n\r\n");
            byte[] headBytes = Encoding.UTF8.GetBytes(head.ToString());
            ns.Write(headBytes, 0, headBytes.Length);
            if (data.Length > 0) ns.Write(data, 0, data.Length);
            ns.Flush();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Installer
{
    /// <summary>
    /// 桌面（3D）视图的宿主。
    ///
    /// 为什么不在 WinForms 里自己画：3D 桌面要的是透视相机、材质、阴影、光照，
    /// GDI+ 做不出来（能做的只有「90 年代」那种四边形贴图）。所以画面交给浏览器里的
    /// WebGL，这边只做三件事 —— 供数据、收操作、开窗口。
    ///
    /// 为什么用裸 TcpListener 而不是 HttpListener：后者走 http.sys，非管理员跑
    /// http://127.0.0.1:port/ 需要先开 URL ACL（netsh），U 盘插到别人电脑上就废了。
    /// 这里自己收发几十行 HTTP，不需要任何权限。
    /// </summary>
    internal sealed class DeskServer : IDisposable
    {
        private readonly MainForm mf;
        private readonly CatTree tree;

        private TcpListener lis;
        private Thread th;
        private Process bro;
        private volatile bool stop;
        private int port;

        private static string _html, _js;
        private static readonly object _lock = new object();

        public int Port { get { return port; } }
        public string Url { get { return "http://127.0.0.1:" + port + "/"; } }
        public bool Running { get { return lis != null && !stop; } }

        /// <summary>桌面窗口那个浏览器进程还活着吗（自检用）。</summary>
        public bool BrowserAlive
        {
            get { try { return bro != null && !bro.HasExited; } catch { return false; } }
        }

        public DeskServer(MainForm owner, CatTree treeView)
        {
            mf = owner;
            tree = treeView;
        }

        // ---------------------------------------------------------------- 资源

        private static void LoadRes()
        {
            if (_html != null && _js != null) return;
            lock (_lock)
            {
                if (_html == null) _html = ReadRes("desk.html");
                if (_js == null) _js = ReadRes("three.js");
            }
        }

        private static string ReadRes(string name)
        {
            try
            {
                using (Stream s = typeof(DeskServer).Assembly.GetManifestResourceStream(name))
                {
                    if (s == null) { Core.Log("FAIL", "内嵌资源缺失：" + name); return ""; }
                    using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                        return r.ReadToEnd();
                }
            }
            catch (Exception ex)
            {
                Core.Log("FAIL", "读内嵌资源失败 " + name + "：" + ex.Message);
                return "";
            }
        }

        // ---------------------------------------------------------------- 启动

        public void Start(bool openWindow = true)
        {
            if (Running) return;
            LoadRes();
            if (_html.Length == 0)
            {
                Core.Log("FAIL", "desk.html 没打进 exe，桌面打不开");
                return;
            }

            port = FreePort();
            lis = new TcpListener(IPAddress.Loopback, port);
            lis.Start();
            stop = false;
            th = new Thread(Loop);
            th.IsBackground = true;
            th.Name = "desk-http";
            th.Start();
            Core.Log("INFO", "桌面服务已起：" + Url);

            if (openWindow) LaunchBrowser();

            // 用户直接点掉窗口（不按「方案树」）时，服务也得跟着收摊
            Thread watch = new Thread(delegate ()
            {
                while (!stop)
                {
                    Thread.Sleep(700);
                    try
                    {
                        if (bro != null && bro.HasExited)
                        {
                            Core.Log("INFO", "桌面窗口被关掉了，服务跟着停");
                            stop = true;
                            try { if (lis != null) lis.Stop(); } catch { }
                            lis = null;
                            break;
                        }
                    }
                    catch { }
                }
            });
            watch.IsBackground = true;
            watch.Start();
        }

        private static int FreePort()
        {
            TcpListener t = new TcpListener(IPAddress.Loopback, 0);
            t.Start();
            int p = ((IPEndPoint)t.LocalEndpoint).Port;
            t.Stop();
            return p;
        }

        private void LaunchBrowser()
        {
            string exe = FindBrowser();
            if (exe.Length == 0)
            {
                Core.Log("FAIL", "找不到 Edge / Chrome，桌面起不来");
                MessageBox.Show(
                    "桌面视图需要本机的 Edge 或 Chrome（Win10 之后都自带 Edge）。\r\n\r\n没找到浏览器，桌面打不开。",
                    "打不开桌面", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 独立 profile：不复用用户日常那个，避免和已开的浏览器抢进程，
            // 关的时候也能安全地只杀掉我们自己这一个。
            string profile = Path.Combine(Path.GetTempPath(), "l4d2desk-profile");
            try { Directory.CreateDirectory(profile); } catch { }

            ProcessStartInfo psi = new ProcessStartInfo(exe,
                "--app=" + Url
                + " --user-data-dir=\"" + profile + "\""
                + " --window-size=1600,1000"
                + " --no-first-run --no-default-browser-check"
                + " --disable-features=Translate,MediaRouter"
                + " --autoplay-policy=no-user-gesture-required");
            psi.UseShellExecute = false;
            psi.WorkingDirectory = Path.GetDirectoryName(exe);
            try
            {
                bro = Process.Start(psi);
                Core.Log("INFO", "桌面窗口已开：" + exe);
            }
            catch (Exception ex)
            {
                Core.Log("FAIL", "开浏览器失败：" + ex.Message);
            }
        }

        private static string FindBrowser()
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] tries = new string[]
            {
                Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(pf, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(pf86, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(local, @"Google\Chrome\Application\chrome.exe")
            };
            foreach (string p in tries) { try { if (File.Exists(p)) return p; } catch { } }
            return "";
        }

        /// <summary>桌面窗口已经开着时，把它拎到前面来（而不是关掉再开一个新的）。</summary>
        public void Focus()
        {
            try
            {
                if (bro == null || bro.HasExited) return;
                bro.Refresh();
                IntPtr h = bro.MainWindowHandle;
                if (h == IntPtr.Zero) return;
                SetForegroundWindow(h);
            }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        public void Stop()
        {
            stop = true;
            try { if (lis != null) lis.Stop(); } catch { }
            lis = null;
            try { if (bro != null && !bro.HasExited) bro.CloseMainWindow(); } catch { }
            try { if (bro != null && !bro.HasExited) bro.Kill(); } catch { }
            bro = null;
            Core.Log("INFO", "桌面服务已停");
        }

        public void Dispose() { Stop(); }

        // ---------------------------------------------------------------- HTTP

        private void Loop()
        {
            while (!stop)
            {
                TcpClient c = null;
                try { c = lis.AcceptTcpClient(); }
                catch { break; }
                try { Handle(c); }
                catch (Exception ex) { Core.Log("WARN", "桌面请求出错：" + ex.Message); }
                finally { try { c.Close(); } catch { } }
            }
        }

        private void Handle(TcpClient c)
        {
            c.ReceiveTimeout = 5000;
            c.SendTimeout = 5000;
            NetworkStream ns = c.GetStream();

            string head = ReadHead(ns);
            if (head == null) return;
            string[] lines = head.Split(new string[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0) return;

            string[] first = lines[0].Split(' ');
            if (first.Length < 2) return;
            string method = first[0].ToUpperInvariant();
            string path = first[1];
            int q = path.IndexOf('?');
            if (q >= 0) path = path.Substring(0, q);

            int contentLen = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                if (lines[i].Substring(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(lines[i].Substring(colon + 1).Trim(), out contentLen);
                }
            }

            string body = "";
            if (contentLen > 0 && contentLen < 1 << 20)
            {
                byte[] buf = new byte[contentLen];
                int got = 0;
                while (got < contentLen)
                {
                    int n = ns.Read(buf, got, contentLen - got);
                    if (n <= 0) break;
                    got += n;
                }
                body = Encoding.UTF8.GetString(buf, 0, got);
            }

            if (path == "/" || path.Length == 0) { Text(ns, 200, "text/html; charset=utf-8", _html); return; }
            if (path == "/three.min.js") { Text(ns, 200, "application/javascript; charset=utf-8", _js); return; }
            if (path == "/api/state") { Text(ns, 200, "application/json; charset=utf-8", StateJson()); return; }
            if (path == "/favicon.ico") { Text(ns, 204, "text/plain", ""); return; }

            if (method == "POST") { Api(ns, path, body); return; }
            Text(ns, 404, "text/plain; charset=utf-8", "not found");
        }

        private static string ReadHead(NetworkStream ns)
        {
            MemoryStream ms = new MemoryStream();
            byte[] one = new byte[1];
            int match = 0;
            while (ms.Length < 64 * 1024)
            {
                int n;
                try { n = ns.Read(one, 0, 1); } catch { return null; }
                if (n <= 0) return null;
                ms.WriteByte(one[0]);
                // \r\n\r\n
                if (one[0] == (match % 2 == 0 ? (byte)'\r' : (byte)'\n')) { match++; if (match == 4) break; }
                else match = one[0] == (byte)'\r' ? 1 : 0;
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        private static void Text(NetworkStream ns, int code, string type, string body)
        {
            byte[] b = Encoding.UTF8.GetBytes(body ?? "");
            StringBuilder h = new StringBuilder();
            h.Append("HTTP/1.1 ").Append(code).Append(' ').Append(code == 200 ? "OK" : (code == 204 ? "No Content" : "Not Found")).Append("\r\n");
            h.Append("Content-Type: ").Append(type).Append("\r\n");
            h.Append("Content-Length: ").Append(b.Length).Append("\r\n");
            h.Append("Cache-Control: no-store\r\n");
            h.Append("Connection: close\r\n\r\n");
            byte[] hb = Encoding.ASCII.GetBytes(h.ToString());
            ns.Write(hb, 0, hb.Length);
            if (b.Length > 0) ns.Write(b, 0, b.Length);
            ns.Flush();
        }

        // ---------------------------------------------------------------- 接口

        private string StateJson()
        {
            try
            {
                return (string)mf.Invoke(new Func<string>(delegate
                {
                    List<DeskCard> cards = DeskModel.Build(tree);
                    string json = DeskModel.ToJson(cards, tree, mf);
                    json = json.Substring(0, json.Length - 1);          // 去掉末尾 }
                    StringBuilder sb = new StringBuilder(json);
                    sb.Append(",\"layout\":{");
                    bool firstPt = true;
                    foreach (DeskCard c in cards)
                    {
                        PointF pt;
                        if (!DeskLayout.TryGet(c.Key, out pt)) continue;
                        if (!firstPt) sb.Append(',');
                        firstPt = false;
                        sb.Append('"').Append(Esc(c.Key)).Append("\":{");
                        sb.Append("\"x\":").Append(pt.X.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                        sb.Append(",\"z\":").Append(pt.Y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                        sb.Append('}');
                    }
                    sb.Append("}}");
                    return sb.ToString();
                }));
            }
            catch (Exception ex)
            {
                Core.Log("FAIL", "导出桌面数据失败：" + ex.Message);
                return "{\"cards\":[],\"stats\":{}}";
            }
        }

        private void Api(NetworkStream ns, string path, string body)
        {
            Dictionary<string, string> d = ParseJson(body);
            string ok = "{\"ok\":true}";

            try
            {
                if (path == "/api/pick")
                {
                    string ck, ri;
                    d.TryGetValue("card", out ck);
                    d.TryGetValue("row", out ri);
                    int idx = 0; int.TryParse(ri, out idx);
                    mf.Invoke(new Action(delegate
                    {
                        List<DeskCard> cards = DeskModel.Build(tree);
                        DeskCard card = null;
                        foreach (DeskCard c in cards) if (c.Key == ck) { card = c; break; }
                        if (card == null) return;
                        // 只有非分隔行才计入下标，和前端保持一致
                        int seen = -1;
                        foreach (DeskRow r in card.Rows)
                        {
                            if (r.IsSub) continue;
                            seen++;
                            if (seen != idx) continue;
                            ModGroup g = DeskModel.GroupOf(r, tree);
                            if (g == null) return;
                            if (mf.CountChecked(g) > 0) mf.UncheckGroup(g);
                            else mf.CheckGroupDefault(g);
                            mf.SyncPicks();
                            return;
                        }
                    }));
                }
                else if (path == "/api/move")
                {
                    string ck, sx, sz;
                    d.TryGetValue("card", out ck);
                    d.TryGetValue("x", out sx);
                    d.TryGetValue("z", out sz);
                    float x, z;
                    float.TryParse(sx, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x);
                    float.TryParse(sz, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out z);
                    DeskLayout.Put(ck, x, z);
                }
                else if (path == "/api/tidy")
                {
                    DeskLayout.Clear();
                }
                else if (path == "/api/install")
                {
                    mf.Invoke(new Action(delegate { mf.InstallFromDesk(); }));
                }
                else if (path == "/api/back")
                {
                    Text(ns, 200, "application/json; charset=utf-8", "{\"ok\":true}");
                    // 得等这一口气把响应吐完再杀浏览器，否则前端拿到的是「连接被重置」
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        Thread.Sleep(250);
                        Stop();
                    });
                    return;
                }
                else
                {
                    Text(ns, 404, "text/plain; charset=utf-8", "no api");
                    return;
                }
            }
            catch (Exception ex)
            {
                Core.Log("FAIL", "桌面操作失败 " + path + "：" + ex.Message);
                Text(ns, 500, "application/json; charset=utf-8", "{\"ok\":false}");
                return;
            }

            Text(ns, 200, "application/json; charset=utf-8", ok);
        }

        private static string Esc(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        /// <summary>够用的 JSON 取值：只认平铺的 "k":"v" 和 "k":数字。</summary>
        private static Dictionary<string, string> ParseJson(string s)
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(s)) return d;
            int i = 0;
            while (i < s.Length)
            {
                int q1 = s.IndexOf('"', i);
                if (q1 < 0) break;
                int q2 = s.IndexOf('"', q1 + 1);
                if (q2 < 0) break;
                string k = s.Substring(q1 + 1, q2 - q1 - 1);
                int colon = s.IndexOf(':', q2);
                if (colon < 0) break;
                int v = colon + 1;
                while (v < s.Length && (s[v] == ' ' || s[v] == '\t')) v++;
                string val;
                if (v < s.Length && s[v] == '"')
                {
                    int e = s.IndexOf('"', v + 1);
                    if (e < 0) break;
                    val = s.Substring(v + 1, e - v - 1);
                    i = e + 1;
                }
                else
                {
                    int e = v;
                    while (e < s.Length && s[e] != ',' && s[e] != '}') e++;
                    val = s.Substring(v, e - v).Trim();
                    i = e;
                }
                d[k] = val;
            }
            return d;
        }
    }
}

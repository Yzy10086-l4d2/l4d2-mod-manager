using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Installer
{
    public static class Work
    {
        /// <summary>
        /// 已装的东西怎么办。false（默认）= 一样就跳过，快；true = 全部重写一遍，修被改坏/被删一半的情况。
        /// 每个安装函数都要照顾这个开关，日志里也要说清楚"跳过了"还是"覆盖了"。
        /// </summary>
        public static bool Overwrite;

        /// <summary>
        /// 用户请求停止后续安装。复制在文件边界安全停下，已经写完的文件保留；
        /// 不在这里做回滚，界面必须明确告诉用户“停止不等于还原”。
        /// </summary>
        public static volatile bool CancelRequested;

        public static int SelfTest(string gameRootHint)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== L4D2 MOD 管理工具 自检 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===");
            sb.AppendLine("程序位置 : " + Core.Here);
            sb.AppendLine("U 盘根   : " + Core.UsbRoot);
            sb.AppendLine("工作模式 : " + (Core.IsLocal ? "本地模式（只扫游戏目录 addons）" : "U 盘模式（主库 + 附加目录）"));
            sb.AppendLine("MOD 主库 : " + Core.ModRoot);
            bool okPack;
            sb.AppendLine("--- 包检查 ---");
            foreach (string l in Core.CheckPackages(out okPack)) sb.AppendLine("  " + l);
            sb.AppendLine("包齐全   : " + (okPack ? "是" : "否"));
            sb.AppendLine("--- 找游戏 ---");
            List<string> games = Core.FindGames();
            if (games.Count == 0) sb.AppendLine("  没找到");
            foreach (string g in games) sb.AppendLine("  " + g);
            sb.AppendLine("--- MOD 库 ---");
            Core.ScanMods();
            long total = 0;
            foreach (ModItem it in Core.ModList) total += it.Size;
            sb.AppendLine("  合计 " + Core.ModList.Count + " 个 vpk, " + Core.Human(total));
            sb.AppendLine("--- Steam 用户配置 ---");
            foreach (string f in SteamLocalConfigs()) sb.AppendLine("  " + f);
            File.WriteAllText(Path.Combine(Core.LogDir, "selftest.txt"), sb.ToString(), new UTF8Encoding(true));
            return 0;
        }

        // ---------- 通用 ----------
        public static void CopyTree(string src, string dst, Action<string> onFile)
        {
            foreach (string f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(src.Length).TrimStart('\\');
                string d = Path.Combine(dst, rel);
                if (Core.SameFile(f, d)) continue;
                Core.CopyOne(f, d);
                if (onFile != null) onFile(rel);
            }
        }

        public static void Unzip(string zip, string dst)
        {
            if (Directory.Exists(dst)) Directory.Delete(dst, true);
            Directory.CreateDirectory(dst);
            try
            {
                System.IO.Compression.ZipFile.ExtractToDirectory(zip, dst);
            }
            catch (Exception ex)
            {
                Core.Log("WARN", "内置解压不可用（" + ex.GetType().Name + "），改用系统解压");
                ExtractWithShell(zip, dst);
            }
        }

        // 系统解压兜底：老系统 / 缺 .NET 4.5 时用资源管理器那套
        private static void ExtractWithShell(string zip, string dst)
        {
            Type shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) throw new Exception("系统解压也不可用");
            object shell = Activator.CreateInstance(shellType);
            object folder = shellType.InvokeMember("NameSpace", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { dst });
            object zipNs = shellType.InvokeMember("NameSpace", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { zip });
            object items = zipNs.GetType().InvokeMember("Items", System.Reflection.BindingFlags.InvokeMethod, null, zipNs, null);
            folder.GetType().InvokeMember("CopyHere", System.Reflection.BindingFlags.InvokeMethod, null, folder, new object[] { items, 0x14 });
            System.Threading.Thread.Sleep(1500);
        }

        // 解压出来的第一层如果只是个壳目录（里面才是 bin），就往下钻一层
        public static string ResolvePayloadRoot(string dir)
        {
            string hit = FindPayload(dir, 0);
            if (hit != null)
            {
                if (!string.Equals(hit, dir, StringComparison.OrdinalIgnoreCase))
                    Core.Log("INFO", "包里套了一层: " + Path.GetFileName(hit) + "，从这层开始覆盖");
                return hit;
            }
            return dir;
        }

        private static string FindPayload(string dir, int depth)
        {
            if (depth > 3) return null;
            if (File.Exists(Path.Combine(dir, "bin\\left4neko.dll"))) return dir;
            try
            {
                foreach (string s in Directory.GetDirectories(dir))
                {
                    string r = FindPayload(s, depth + 1);
                    if (r != null) return r;
                }
            }
            catch { }
            return null;
        }

        // ---------- L4N ----------
        public static bool InstallL4N(string gameRoot, Action<int, int> progress)
        {
            string zip = Core.FirstFile(Core.PkgDir, "L4N_v*.zip");
            string dir = Path.Combine(Core.PkgDir, "L4N平台\\L4N");
            try
            {
                if (zip != null)
                {
                    string tmp = Path.Combine(Path.GetTempPath(), "l4n_" + DateTime.Now.Ticks);
                    Core.Log("INFO", "解压 " + Path.GetFileName(zip) + " …");
                    Unzip(zip, tmp);
                    string payload = ResolvePayloadRoot(tmp);
                    Core.Log("INFO", "解压到: " + tmp + "  实际内容根: " + payload);
                    CopyPayloadWithProgress(payload, gameRoot, progress);
                    try { Directory.Delete(tmp, true); } catch { }
                }
                else
                {
                    Core.Log("INFO", "用解压好的 L4N平台\\L4N 覆盖");
                    CopyPayloadWithProgress(dir, gameRoot, progress);
                }
                bool ok = File.Exists(Path.Combine(gameRoot, "bin\\left4neko.dll"));
                Core.Log(ok ? "OK" : "FAIL", ok ? "L4N 核心 bin\\left4neko.dll 到位" : "没看到 bin\\left4neko.dll");
                return ok;
            }
            catch (Exception ex) { Core.Log("FAIL", "L4N 装失败: " + ex.Message); return false; }
        }

        private static void CopyPayloadWithProgress(string payload, string gameRoot, Action<int, int> progress)
        {
            string[] files = Directory.GetFiles(payload, "*", SearchOption.AllDirectories);
            int total = files.Length, done = 0, skip = 0, write = 0;
            foreach (string f in files)
            {
                if (CancelRequested) { Core.Log("WARN", "收到停止请求：平台文件在本文件结束后停止复制"); break; }
                try
                {
                    string rel = f.Substring(payload.Length).TrimStart('\\');
                    string dst = Path.Combine(gameRoot, rel);
                    if (!Core.SameFile(f, dst)) { Core.CopyOne(f, dst); write++; }
                    else if (Overwrite) { Core.CopyOne(f, dst); write++; }   // 一样也照写：覆盖模式就是不信现状
                    else skip++;
                }
                catch (Exception ex) { Core.Log("WARN", "复制 " + Path.GetFileName(f) + " 失败: " + ex.Message); }
                done++;
                if (progress != null && (done % 10 == 0 || done == total)) progress(done, total);
            }
            Core.Log("INFO", Overwrite
                ? ("平台文件：写入 " + write + " 个（覆盖模式，全部重写）")
                : ("平台文件：写入 " + write + " 个，已是最新跳过 " + skip + " 个"));
        }

        // ---------- dxvk ----------
        public static bool InstallDxvk(string gameRoot)
        {
            try
            {
                string srcDir = Path.Combine(Core.PkgDir, "防闪退");
                foreach (string n in new string[] { "d3d9.dll", "dxvk.conf" })
                {
                    string s = Path.Combine(srcDir, n);
                    string d = Path.Combine(gameRoot, n);
                    if (!File.Exists(s)) { Core.Log("FAIL", "缺 " + s); return false; }
                    File.Copy(s, d, true);
                    if (new FileInfo(d).Length != new FileInfo(s).Length) { Core.Log("FAIL", n + " 大小不对"); return false; }
                    Core.Log("OK", n + " 到位");
                }
                return true;
            }
            catch (Exception ex) { Core.Log("FAIL", "dxvk 失败: " + ex.Message); return false; }
        }

        // ---------- Skeeto ----------
        public static bool InstallSkeeto(string gameRoot)
        {
            string ps1 = Path.Combine(Core.PkgDir, "Skeeto\\Skeeto\\Skeeto\\Maintain.ps1");
            string dir = Path.Combine(Core.PkgDir, "Skeeto\\Skeeto");
            bool already = File.Exists(Path.Combine(gameRoot, "skeeto.dll"));
            if (already && !Overwrite)
            {
                Core.Log("OK", "Skeeto 已经装过（skeeto.dll 在），跳过。想重装就在「安装选项」里选「覆盖重装」。");
                if (!File.Exists(Path.Combine(gameRoot, "bin\\wininet.dll")))
                    Core.Log("WARN", "bin\\wininet.dll 不见了 —— 勾「覆盖重装」能把平台文件补回来");
                return true;
            }
            bool ok = false;
            if (File.Exists(ps1))
            {
                foreach (string mode in new string[] { "Migrate", "Apply" })
                {
                    if (ok) break;
                    Core.Log("INFO", "调用 Skeeto 官方安装器（" + mode + "）…");
                    string output = RunCapture("powershell.exe",
                        "-NoLogo -NoProfile -ExecutionPolicy Bypass -File \"" + ps1 + "\" -Mode " + mode +
                        " -GameRoot \"" + gameRoot + "\" -PayloadRoot \"" + dir + "\" -NonInteractive",
                        dir, 240000);
                    foreach (string line in output.Split('\n'))
                    {
                        string s2 = line.Trim();
                        if (s2.Length > 0) Core.Log("INFO", "  | " + s2);
                    }
                    if (File.Exists(Path.Combine(gameRoot, "skeeto.dll"))) ok = true;
            if (already) Core.Log("INFO", "覆盖模式：Skeeto 又完整装了一遍");
                    else Core.Log("WARN", mode + " 跑完但 skeeto.dll 还是没有");
                }
            }
            if (!ok)
            {
                Core.Log("WARN", "改用整包复制 Skeeto");
                try { CopyTree(dir, gameRoot, null); } catch (Exception ex) { Core.Log("FAIL", "Skeeto 复制失败: " + ex.Message); }
            }
            bool has = File.Exists(Path.Combine(gameRoot, "skeeto.dll"));
            Core.Log(has ? "OK" : "FAIL", has ? "skeeto.dll 到位" : "没看到 skeeto.dll");
            if (File.Exists(Path.Combine(gameRoot, "bin\\wininet.dll"))) Core.Log("OK", "bin\\wininet.dll（代理）到位");
            else Core.Log("WARN", "没看到 bin\\wininet.dll，Skeeto 可能不会加载");
            return has;
        }

        // ---------- 素材包 ----------
        public static string InstallAssetPack(string gameRoot)
        {
            string vpk = Core.FirstFile(Path.Combine(Core.PkgDir, "Skeeto\\素材包"), "*.vpk");
            if (vpk == null) { Core.Log("FAIL", "找不到素材包 vpk"); return null; }
            string addons = Path.Combine(Path.Combine(gameRoot, "left4dead2"), "addons");
            if (!Directory.Exists(addons)) Directory.CreateDirectory(addons);
            string dst = Path.Combine(addons, Path.GetFileName(vpk));
            long len = new FileInfo(vpk).Length;
            if (!Overwrite && Core.SameFile(vpk, dst)) { Core.Log("OK", "素材包已存在且大小一致，跳过"); return Path.GetFileName(vpk); }
            Core.Log("INFO", "复制素材包 " + Core.Human(len) + " …");
            Core.CopyOne(vpk, dst);
            if (new FileInfo(dst).Length != len) { Core.Log("FAIL", "素材包大小不对"); return null; }
            Core.Log("OK", "素材包到位（" + Core.Human(len) + "）");
            return Path.GetFileName(vpk);
        }

        // ---------- autoexec.cfg ----------
        public static bool InstallCfg(string gameRoot)
        {
            try
            {
                string src = Path.Combine(Core.ModRoot, "autoexec.cfg");
                if (!File.Exists(src)) { Core.Log("WARN", "U 盘上没有 autoexec.cfg，跳过"); return false; }
                string dir = Path.Combine(Path.Combine(gameRoot, "left4dead2"), "cfg");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string dst = Path.Combine(dir, "autoexec.cfg");
                if (!Overwrite && Core.SameFile(src, dst)) { Core.Log("OK", "autoexec.cfg 已是最新，跳过"); return true; }
                Core.CopyOne(src, dst);
                Core.Log("OK", "autoexec.cfg 已放进 left4dead2\\cfg");
                return true;
            }
            catch (Exception ex) { Core.Log("FAIL", "autoexec.cfg 搬运失败: " + ex.Message); return false; }
        }

        // ---------- MQCam ----------
        public static bool InstallMqcam(string gameRoot)
        {
            string dir = Path.Combine(Core.PkgDir, "MQCam\\MQCam");
            string tool = Path.Combine(dir, "mqcore_package_tool.exe");
            string dll = Path.Combine(Path.Combine(Path.Combine(gameRoot, "bin"), "neko"), "mqcore\\mqcam.dll");
            if (File.Exists(dll) && !Overwrite) { Core.Log("OK", "MQCam 已经装过（mqcam.dll 在），跳过，不动相机设置"); return true; }
            if (!File.Exists(tool)) { Core.Log("FAIL", "缺 mqcore_package_tool.exe"); return false; }
            foreach (string cmd in new string[] { "install plan", "install apply" })
            {
                string output = RunCapture(tool, cmd + " \"" + gameRoot + "\"", dir, 120000);
                foreach (string line in output.Split('\n'))
                {
                    string s2 = line.Trim();
                    if (s2.Length > 0) Core.Log("INFO", "  | " + s2);
                }
            }
            bool ok = File.Exists(dll);
            Core.Log(ok ? "OK" : "FAIL", ok ? "mqcam.dll 到位" : "没看到 bin\\neko\\mqcore\\mqcam.dll");
            if (ok) ResetCamState(gameRoot);
            return ok;
        }

        private static void ResetCamState(string gameRoot)
        {
            try
            {
                string clean = Path.Combine(Core.PkgDir, "MQCam\\mqcam_state_clean.ini");
                string dst = Path.Combine(Path.Combine(Path.Combine(gameRoot, "bin\\neko"), "mqcore"), "mqcam_state.ini");
                if (!File.Exists(clean)) { Core.Log("WARN", "没有 mqcam_state_clean.ini，跳过相机归零"); return; }
                if (!Directory.Exists(Path.GetDirectoryName(dst))) return;
                File.Copy(clean, dst, true);
                Core.Log("OK", "相机偏移已归零（F2/F3 从默认位置开始）");
            }
            catch (Exception ex) { Core.Log("WARN", "相机配置写入失败: " + ex.Message); }
        }

        // ---------- Steam 启动项 ----------
        public static List<string> SteamLocalConfigs()
        {
            List<string> found = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> roots = new List<string>();
            string[] regs = new string[] {
                "HKEY_CURRENT_USER\\Software\\Valve\\Steam",
                "HKEY_LOCAL_MACHINE\\SOFTWARE\\WOW6432Node\\Valve\\Steam",
                "HKEY_LOCAL_MACHINE\\SOFTWARE\\Valve\\Steam"
            };
            foreach (string r in regs)
            {
                foreach (string n in new string[] { "SteamPath", "InstallPath" })
                {
                    try
                    {
                        object v = Registry.GetValue(r, n, null);
                        if (v == null) continue;
                        string s = v.ToString().Replace('/', '\\').TrimEnd('\\');
                        if (Directory.Exists(s) && !roots.Contains(s)) roots.Add(s);
                    }
                    catch { }
                }
            }
            foreach (string root in roots)
            {
                string ud = Path.Combine(root, "userdata");
                if (!Directory.Exists(ud)) continue;
                foreach (string u in Directory.GetDirectories(ud))
                {
                    string cfg = Path.Combine(Path.Combine(u, "config"), "localconfig.vdf");
                    if (File.Exists(cfg))
                    {
                        string full = Path.GetFullPath(cfg);
                        if (seen.Add(full)) found.Add(full);
                    }
                }
            }
            return found;
        }

        public static string WriteLaunchOptions(string value)
        {
            List<string> cfgs = SteamLocalConfigs();
            try
            {
                bool running = Process.GetProcessesByName("steam").Length > 0;
                if (running) Core.Log("WARN", "Steam 正在运行，它退出时可能把配置写回去；最稳是退出 Steam 再写、然后重开");
            }
            catch { }
            if (cfgs.Count == 0)
            {
                try { System.Windows.Forms.Clipboard.SetText(value); Core.Log("WARN", "没找到 Steam 用户配置；启动项已复制到剪贴板，请到 Steam 里粘贴"); }
                catch { Core.Log("WARN", "没找到 Steam 用户配置，启动项是: " + value); }
                return "没找到 Steam 用户配置，改用手动粘贴";
            }
            int okCount = 0;
            foreach (string cfg in cfgs)
            {
                try
                {
                    if (PatchVdf(cfg, value)) { okCount++; Core.Log("OK", "启动项已写: " + cfg); }
                    else Core.Log("WARN", "这个文件里找不到 550 或 apps 块: " + cfg);
                }
                catch (Exception ex) { Core.Log("WARN", "写 " + cfg + " 失败: " + ex.Message); }
            }
            if (okCount == 0) return "启动项没写成功，请在 Steam 里手动填";
            return "启动项已写入 " + okCount + " 个 Steam 账号配置（Steam 退出后重开生效）";
        }

        private static bool PatchVdf(string path, string value)
        {
            string text = File.ReadAllText(path, Encoding.UTF8);
            int idx = text.IndexOf("\"550\"", StringComparison.Ordinal);
            bool changed = false;
            if (idx < 0)
            {
                int apps = text.IndexOf("\"apps\"", StringComparison.Ordinal);
                if (apps < 0) return false;
                int open = text.IndexOf('{', apps);
                if (open < 0) return false;
                string ins = "\r\n\t\t\t\t\t\"550\"\r\n\t\t\t\t\t{\r\n\t\t\t\t\t\t\"LaunchOptions\"\t\t\"" + value + "\"\r\n\t\t\t\t\t}";
                text = text.Insert(open + 1, ins);
                changed = true;
            }
            else
            {
                int open = text.IndexOf('{', idx);
                if (open < 0) return false;
                int depth = 0, close = -1;
                for (int i = open; i < text.Length; i++)
                {
                    if (text[i] == '{') depth++;
                    else if (text[i] == '}')
                    {
                        depth--;
                        if (depth == 0) { close = i; break; }
                    }
                }
                if (close < 0) return false;
                string block = text.Substring(open, close - open);
                Match lo = Regex.Match(block, "\"LaunchOptions\"\\s*\"[^\"]*\"");
                if (lo.Success)
                {
                    string nb = block.Substring(0, lo.Index) + "\"LaunchOptions\"\t\t\"" + value + "\"" + block.Substring(lo.Index + lo.Length);
                    text = text.Substring(0, open) + nb + text.Substring(close);
                }
                else
                {
                    text = text.Insert(open + 1, "\r\n\t\t\t\t\t\t\"LaunchOptions\"\t\t\"" + value + "\"");
                }
                changed = true;
            }
            if (changed) File.WriteAllText(path, text, new UTF8Encoding(false));
            return changed;
        }

        // ---------- 校验 ----------
        public static List<string> Verify(string gameRoot)
        {
            List<string> list = new List<string>();
            string[] items = new string[] {
                "left4dead2.exe", "bin\\left4neko.dll", "skeeto.dll", "bin\\wininet.dll",
                "bin\\neko\\mqcore\\mqcam.dll", "d3d9.dll", "dxvk.conf"
            };
            foreach (string it in items)
            {
                bool ok = File.Exists(Path.Combine(gameRoot, it));
                list.Add((ok ? "[OK] " : "[缺] ") + it);
                Core.Log(ok ? "OK" : "WARN", (ok ? "[OK] " : "[缺] ") + it);
            }
            return list;
        }

        // ---------- 铺 MOD ----------
        public static long CopyMods(string gameRoot, List<ModItem> sel, Action<long, long> progress, out int copied, out int skipped, out List<string> added, out List<string> failed)
        {
            int c = 0, s = 0;
            List<string> okList = new List<string>();
            List<string> badList = new List<string>();
            string addons = Path.Combine(Path.Combine(gameRoot, "left4dead2"), "addons");
            if (!Directory.Exists(addons)) Directory.CreateDirectory(addons);

            long total = 0;
            foreach (ModItem it in sel) total += it.Size;
            long done = 0;
            List<ModItem> todo = new List<ModItem>();
            HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ModItem it in sel)
            {
                // 游戏目录里的本来就装着：不用复制，翻开关就行
                if (it.FromGame) { s++; done += it.Size; continue; }
                // 不同分类下可能有同名 vpk：只铺第一个，否则并行时两个线程会同时写同一个目标文件
                if (!used.Add(it.Name))
                {
                    s++; done += it.Size;
                    Core.Log("WARN", "同名跳过（本次已装第一个）: " + it.Name);
                    continue;
                }
                string d = Path.Combine(addons, it.Name);
                // 判「已装」只看同名 + 同字节大小，和列表「已装」列同一套判定。
                // 以前用 Core.SameFile（还比 mtime），从别处拷来的同名同大小文件会被当成没装、白拷一遍；
                // 已装的 MOD 只该动 addonlist 的开关，不该把文件重新搬一次。
                // 勾了「覆盖重装」才强制重写，那是修坏文件的路径。
                bool same = false;
                try { same = File.Exists(d) && new FileInfo(d).Length == it.Size; }
                catch { same = false; }
                if (same && !Overwrite) { s++; done += it.Size; }
                else todo.Add(it);
            }
            if (progress != null) progress(done, total);

            object gate = new object();
            int degree = DegreeFor(todo);
            long todoSize = 0;
            foreach (ModItem it in todo) todoSize += it.Size;
            Stopwatch sw = Stopwatch.StartNew();
            long lastReport = 0;
            if (todo.Count > 0)
            {
                System.Threading.Tasks.Parallel.ForEach(todo,
                    new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = degree },
                    it =>
                    {
                        if (CancelRequested)
                        {
                            bool reportCancel = false;
                            lock (gate)
                            {
                                s++;
                                done += it.Size;
                                long tnow = sw.ElapsedMilliseconds;
                                if (tnow - lastReport >= 120) { lastReport = tnow; reportCancel = true; }
                            }
                            if (reportCancel && progress != null) progress(done, total);
                            return;
                        }
                        string d = Path.Combine(addons, it.Name);
                        string why;
                        bool good = it.IsArchive
                            ? ExtractOne(it, d, out why)
                            : Core.CopyOneVerified(it.FullPath, d, out why);
                        bool report = false;
                        lock (gate)
                        {
                            if (good) { c++; okList.Add(it.Name); }
                            else { badList.Add(it.Name); Core.Log("FAIL", "复制/校验失败 " + it.Name + "：" + why); }
                            done += it.Size;
                            long now = sw.ElapsedMilliseconds;
                            if (now - lastReport >= 120) { lastReport = now; report = true; }
                        }
                        if (report && progress != null) progress(done, total);   // 别每个文件都跨线程刷一次进度条
                    });
            }
            sw.Stop();
            if (progress != null) progress(done, total);
            double sec = sw.Elapsed.TotalSeconds;
            if (sec > 0.5 && todoSize > 0)
            {
                double rate = todoSize / 1048576.0 / sec;
                if (rate > 0.2) Core.LastRateMBps = Math.Round(rate, 1);
            }
            copied = c; skipped = s; added = okList; failed = badList;
            string tail = "共 " + Core.Human(total) + "，" + Math.Round(sec, 1) + " 秒（" + Core.LastRateMBps + " MB/s）";
            Core.Log("OK", Overwrite
                ? ("MOD: 覆盖重写 " + c + " 个（并行 " + degree + " 路" + (Core.StrictVerify ? " + 全量 SHA256" : " + 大小校验") + "），同名去重跳过 " + s + " 个，" + tail)
                : ("MOD: 新装 " + c + " 个（并行 " + degree + " 路" + (Core.StrictVerify ? " + 全量 SHA256" : " + 大小校验") + "），已装跳过 " + s + " 个，" + tail));
            return total;
        }

        /// <summary>从压缩包里只取出这一个 vpk 放到 addons：不整包解压，也不动 U 盘上的包。</summary>
        public static bool ExtractOne(ModItem it, string dst, out string why)
        {
            // 解包实现只有一份，在 Catalog 里 —— 识别层和判重层也要用，不能再各写各的
            return Catalog.ExtractEntryTo(it.FullPath, it.Inner, dst, out why);
        }

        // U 盘上并行读会互相打断顺序读，反而更慢；网络共享才需要多路掩盖延迟
        private static int DegreeFor(List<ModItem> todo)
        {
            int cpu = Environment.ProcessorCount;
            DriveType t = DriveType.Fixed;
            try { if (todo.Count > 0) t = new DriveInfo(Path.GetPathRoot(todo[0].FullPath)).DriveType; }
            catch { }
            if (t == DriveType.Removable || t == DriveType.CDRom) return Math.Min(2, Math.Max(1, cpu));
            if (t == DriveType.Network) return Math.Min(4, Math.Max(2, cpu));
            return Math.Min(3, Math.Max(2, cpu));
        }

        // ---------- MOD 库维护 ----------
        /// <summary>从库里删掉一个 vpk（真删，不进回收目录）。返回 null = 成功，否则是失败原因。</summary>
        public static string DeleteMod(ModItem m)
        {
            // U 盘模式：游戏 addons 里的文件不归库管（那是已装的，不是库里的）。
            // 本地模式：addons 就是库，删它 = 从游戏里卸掉，允许。
            if (m.FromGame && !Core.IsLocal) return "这个 MOD 在游戏目录里，U 盘模式不动它";
            return DeleteModFile(m, m.FromGame ? Core.Cfg.GameRoot : null);
        }

        /// <summary>
        /// 把游戏 addons 里的一个 vpk 直接删掉（不管当前工作模式）。
        /// 「已装 MOD 开关」窗口用它做批量删除；addonlist 里那条登记一并摘掉。
        /// </summary>
        public static string DeleteGameMod(ModItem m, string gameRoot)
        {
            return DeleteModFile(m, gameRoot);
        }

        static string DeleteModFile(ModItem m, string addonListRoot)
        {
            try { File.Delete(m.FullPath); }
            catch (Exception ex) { return ex.Message; }
            if (addonListRoot != null && addonListRoot.Length > 0)
            {
                try { AddonList.Remove(addonListRoot, new string[] { m.Name }); }
                catch (Exception ex) { Core.Log("WARN", "从 addonlist 摘掉 " + m.Name + " 失败: " + ex.Message); }
            }
            return null;
        }

        public static string RenameMod(ModItem m, string newName)
        {
            if (m.FromGame) return "这个 MOD 在游戏目录里，不能在这里改名";
            newName = Core.SafeName(newName);
            if (string.IsNullOrEmpty(newName)) return "名字不能为空";
            if (!newName.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase)) newName = newName + ".vpk";
            string dst = Path.Combine(Path.GetDirectoryName(m.FullPath), newName);
            if (File.Exists(dst)) return "同目录已经有 " + newName;
            File.Move(m.FullPath, dst);
            return null;
        }

        public static List<string> CleanDuplicateMods()
        {
            List<string> moved = new List<string>();
            Core.ScanMods();
            Dictionary<string, List<ModItem>> byName = new Dictionary<string, List<ModItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (ModItem m in Core.ModList)
            {
                if (m.FromGame) continue;   // 游戏目录里的不参与清重：它和库里同名的那份是「已装的一份」，不能回收
                if (!byName.ContainsKey(m.Name)) byName[m.Name] = new List<ModItem>();
                byName[m.Name].Add(m);
            }
            List<ModItem> gone = new List<ModItem>();
            foreach (KeyValuePair<string, List<ModItem>> kv in byName)
            {
                if (kv.Value.Count < 2) continue;
                for (int i = 1; i < kv.Value.Count; i++)
                {
                    try
                    {
                        string err = DeleteMod(kv.Value[i]);
                        if (err != null) throw new Exception(err);
                        moved.Add(kv.Value[i].Name + "  (" + kv.Value[i].Cat + " · " + kv.Value[i].Sub + ")");
                        gone.Add(kv.Value[i]);
                    }
                    catch (Exception ex) { Core.Log("WARN", "清理 " + kv.Value[i].Name + " 失败: " + ex.Message); }
                }
            }
            // 文件已经挪走，直接把它从内存列表里摘掉，不再花几秒重扫全库
            foreach (ModItem g in gone) { Core.ModList.Remove(g); Core.ByRel.Remove(g.Key); }
            return moved;
        }

        // ---------- 换整套：只翻 addonlist 的开关，文件一个都不动 ----------
        /// <summary>
        /// 按方案把 addonlist 翻一遍：这一套打开、上一套关上。
        /// 不动 addons 里的文件 —— 切回来只需把开关翻回去，不用重新复制。
        /// 只认 U 盘库里存在的 vpk；用户自己丢进 addons 的不碰。
        /// </summary>
        public static void SwitchAddons(string gameRoot, List<ModItem> sel, string assetName,
                                        out int on, out int off, out int untouched)
        {
            on = 0; off = 0; untouched = 0;
            string addons = Path.Combine(Path.Combine(gameRoot, "left4dead2"), "addons");
            Dictionary<string, bool> st = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> want = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (sel != null)
                foreach (ModItem m in sel)
                {
                    if (m == null || string.IsNullOrEmpty(m.Name)) continue;
                    if (!want.Add(m.Name)) continue;
                    st[m.Name] = true;
                }
            if (!string.IsNullOrEmpty(assetName) && want.Add(assetName)) st[assetName] = true;

            HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Core.ModList != null)
                foreach (ModItem m in Core.ModList) if (!string.IsNullOrEmpty(m.Name)) known.Add(m.Name);

            if (Directory.Exists(addons))
                foreach (string f in Directory.GetFiles(addons, "*.vpk"))
                {
                    string n = Path.GetFileName(f);
                    if (want.Contains(n)) continue;
                    if (!known.Contains(n)) { untouched++; continue; }   // 不认识的：不是我们装的，别管
                    st[n] = false;
                }

            if (st.Count == 0) return;
            AddonList.Write(gameRoot, st);
            foreach (KeyValuePair<string, bool> kv in st) { if (kv.Value) on++; else off++; }
            Core.Log("OK", "整套开关：打开 " + on + " 个、关闭 " + off + " 个"
                + (untouched > 0 ? "、不认识的 " + untouched + " 个没动" : "")
                + "（addons 里的文件一个都没动，切回来直接开）");
        }

        // ---------- 回收新 MOD ----------
        public static List<string> CollectNewMods(string gameRoot)
        {
            List<string> got = new List<string>();
            string addons = Path.Combine(Path.Combine(gameRoot, "left4dead2"), "addons");
            if (!Directory.Exists(addons)) return got;
            Core.ScanMods();
            HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ModItem m in Core.ModList) known.Add(m.Name);
            string dst = Path.Combine(Core.ModRoot, "addons回收");
            foreach (string f in Directory.GetFiles(addons, "*.vpk"))
            {
                string name = Path.GetFileName(f);
                if (known.Contains(name)) continue;
                try
                {
                    if (!Directory.Exists(dst)) Directory.CreateDirectory(dst);
                    string target = Path.Combine(dst, name);
                    if (Core.SameFile(f, target)) continue;
                    Core.CopyOne(f, target);
                    got.Add(name);
                    Core.Log("OK", "已收回: " + name);
                }
                catch (Exception ex) { Core.Log("WARN", "回收 " + name + " 失败: " + ex.Message); }
            }
            return got;
        }

        // ---------- 彻底还原 ----------
        public static List<string> FullRestore(string gameRoot)
        {
            List<string> report = new List<string>();
            List<string> targets = new List<string>();
            targets.AddRange(L4NFiles());
            targets.AddRange(PackageFiles(Path.Combine(Core.PkgDir, "Skeeto\\Skeeto")));
            string vpk = Core.FirstFile(Path.Combine(Core.PkgDir, "Skeeto\\素材包"), "*.vpk");
            if (vpk != null) targets.Add("left4dead2\\addons\\" + Path.GetFileName(vpk));
            targets.Add("d3d9.dll");
            targets.Add("dxvk.conf");
            targets.Add("bin\\neko\\mqcore\\mqcam.dll");
            targets.Add("bin\\neko\\mqcore\\mqcam.ini");
            targets.Add("bin\\neko\\mqcore\\mqcam_state.ini");

            HashSet<string> uniq = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int del = 0;
            foreach (string rel in targets)
            {
                if (string.IsNullOrEmpty(rel)) continue;
                string key = rel.TrimStart('\\');
                if (!uniq.Add(key)) continue;
                try { if (File.Exists(Path.Combine(gameRoot, key))) { File.Delete(Path.Combine(gameRoot, key)); del++; } } catch { }
            }
            report.Add("删掉平台文件 " + del + " 个（L4N / Skeeto / 素材包 / dxvk / MQCam）");

            foreach (string d in new string[] { "bin\\neko\\mqcore", "bin\\neko", "Skeeto", "skeeto_kbact", "skeeto_update" })
            {
                try
                {
                    string full = Path.Combine(gameRoot, d);
                    if (Directory.Exists(full) && Directory.GetFileSystemEntries(full).Length == 0) Directory.Delete(full);
                }
                catch { }
            }

            string mf = Path.Combine(Core.LogDir, "最近一次.ini");
            List<string> added = new List<string>();
            if (File.Exists(mf))
            {
                foreach (string raw in File.ReadAllLines(mf, Encoding.UTF8))
                {
                    string t = raw.Trim();
                    if (t.StartsWith("Added=")) added.Add(t.Substring(6));
                }
            }

            string l4d2 = Path.Combine(gameRoot, "left4dead2");
            string addons = Path.Combine(l4d2, "addons");
            int moved = 0;
            foreach (string a in added)
            {
                try { string p = Path.Combine(addons, a); if (File.Exists(p)) { File.Delete(p); moved++; } } catch { }
            }
            if (added.Count > 0) report.Add("本次铺的 MOD 已删除 " + moved + " 个");
            report.Add("原版 exe / addonlist / autoexec 没有备份可还原，需要原版请用 Steam 校验文件");
            return report;
        }

        private static List<string> PackageFiles(string pkgDir)
        {
            List<string> rels = new List<string>();
            try
            {
                if (!Directory.Exists(pkgDir)) return rels;
                foreach (string f in Directory.GetFiles(pkgDir, "*", SearchOption.AllDirectories))
                    rels.Add(f.Substring(pkgDir.Length).TrimStart('\\'));
            }
            catch { }
            return rels;
        }

        private static List<string> L4NFiles()
        {
            List<string> rels = new List<string>();
            string zip = Core.FirstFile(Core.PkgDir, "L4N_v*.zip");
            try
            {
                if (zip != null)
                {
                    using (System.IO.Compression.ZipArchive za = System.IO.Compression.ZipFile.OpenRead(zip))
                    {
                        Dictionary<string, bool> names = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                        foreach (System.IO.Compression.ZipArchiveEntry e in za.Entries) names[e.FullName] = true;
                        string prefix = null;
                        foreach (string n in names.Keys)
                        {
                            if (n.EndsWith("/bin/left4neko.dll", StringComparison.OrdinalIgnoreCase)) { prefix = n.Substring(0, n.Length - "bin/left4neko.dll".Length); break; }
                            if (string.Equals(n, "bin/left4neko.dll", StringComparison.OrdinalIgnoreCase)) { prefix = ""; break; }
                        }
                        if (prefix == null) return rels;
                        foreach (string n in names.Keys)
                        {
                            if (n.EndsWith("/")) continue;
                            if (!n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                            rels.Add(n.Substring(prefix.Length).Replace('/', '\\'));
                        }
                    }
                }
                else rels.AddRange(PackageFiles(Path.Combine(Core.PkgDir, "L4N平台\\L4N")));
            }
            catch (Exception ex) { Core.Log("WARN", "读 L4N 包清单失败: " + ex.Message); }
            return rels;
        }
        // ---------- 批量导入：把散落的 vpk / 压缩包收进库并按名字归类 --------
        // move=true 时，复制校验通过后删掉源文件（"收进库"，不占双份体积）
        public static List<string> ImportVpks(string sourceDir, bool move, out int imported, out int skipped)
        {
            imported = 0; skipped = 0;
            List<string> log = new List<string>();
            if (!Directory.Exists(sourceDir)) return log;
            Core.ScanMods();
            HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ModItem m in Core.ModList) known.Add(m.Name);
            // 库里已有的压缩包（按文件名去重）
            HashSet<string> knownArc = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string a in Directory.GetFiles(Core.ModRoot, "*", SearchOption.AllDirectories))
                {
                    string e = Path.GetExtension(a).ToLowerInvariant();
                    if (e == ".rar" || e == ".zip" || e == ".7z") knownArc.Add(Path.GetFileName(a));
                }
            }
            catch { }

            foreach (string f in Directory.GetFiles(sourceDir, "*.vpk", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(f);
                if (known.Contains(name)) { skipped++; continue; }
                string relDir = ClassifyByName(name);
                if (PutIntoLib(f, name, relDir, move, log)) { imported++; known.Add(name); }
            }

            foreach (string pat in new string[] { "*.rar", "*.zip" })
            {
                foreach (string f in Directory.GetFiles(sourceDir, pat, SearchOption.AllDirectories))
                {
                    string name = Path.GetFileName(f);
                    if (knownArc.Contains(name)) { skipped++; continue; }
                    string relDir = ClassifyArchive(f, name);
                    if (PutIntoLib(f, name, relDir, move, log)) { imported++; knownArc.Add(name); }
                }
            }
            return log;
        }

        // 复制进库（带校验），move=true 时删源文件
        private static bool PutIntoLib(string srcPath, string name, string relDir, bool move, List<string> log)
        {
            try
            {
                string dstDir = Path.Combine(Core.ModRoot, relDir);
                Directory.CreateDirectory(dstDir);
                string dst = Path.Combine(dstDir, name);
                if (File.Exists(dst)) { log.Add("已存在，跳过 " + relDir + "\\" + name); return false; }
                string why;
                if (!Core.CopyOneVerified(srcPath, dst, out why)) { log.Add("失败 " + name + "：" + why); return false; }
                if (move)
                {
                    try { File.Delete(srcPath); }
                    catch (Exception ex) { log.Add("已复制但源文件删不掉（" + name + "）：" + ex.Message); }
                }
                bool isArc = IsArchiveName(name);
                if (!isArc) AddToMem(dst);
                log.Add((isArc ? "入库（压缩包）" : "入库") + relDir + "\\" + name);
                return true;
            }
            catch (Exception ex) { log.Add("失败 " + name + "：" + ex.Message); return false; }
        }

        private static bool IsArchiveName(string name)
        {
            string e = Path.GetExtension(name).ToLowerInvariant();
            return e == ".rar" || e == ".zip" || e == ".7z";
        }

        // 压缩包先按包名分类；分类落进「杂项」且包里只有一个 vpk 时，改用包里那个 vpk 的名字再猜一次
        public static string ClassifyArchive(string path, string name)
        {
            string rel = ClassifyByName(name);
            if (rel.IndexOf("杂项功能") < 0) return rel;
            try
            {
                string err;
                List<ArcEntry> list = Catalog.ListArchive(path, out err);
                if (list != null && list.Count > 0 && !string.IsNullOrEmpty(list[0].Inner))
                {
                    string inner = ClassifyByName(Path.GetFileName(list[0].Inner));
                    if (inner.IndexOf("杂项功能") < 0) return inner;
                }
            }
            catch { }
            return rel;
        }

        // 新入库的文件直接补进内存列表，省掉一次全库重扫
        private static void AddToMem(string fullPath)
        {
            try
            {
                ModItem it = new ModItem();
                it.Name = Path.GetFileName(fullPath);
                it.FullPath = fullPath;
                it.RelPath = fullPath.Substring(Core.ModRoot.Length).TrimStart('\\');
                it.Key = it.RelPath;
                string rd = Core.RelDir(it.RelPath);
                it.Size = new FileInfo(fullPath).Length;
                Catalog.Classify(it);
                Core.ModList.Add(it);
                Core.ByRel[it.Key] = it;
                if (rd.Length > 0)
                {
                    Core.BumpDir(rd);
                    int bs = rd.IndexOf('\\');
                    if (bs > 0) Core.BumpDir(rd.Substring(0, bs));
                }
            }
            catch { }
        }

        // 按文件名猜它该放哪个文件夹（只猜常见枪械，猜不到进「杂项功能」）
        public static string ClassifyByName(string name)
        {
            string n = name.ToLowerInvariant();
            string[][] map = new string[][]
            {
                new string[] { "ak47", "AK47" },
                new string[] { "mp5", "MP5" },
                new string[] { "m16", "M16" },
                new string[] { "scar", "scar" },
                new string[] { "awp", "AWP" },
                new string[] { "鸟狙", "鸟狙" }, new string[] { "scout", "鸟狙" },
                new string[] { "木喷", "木喷" },
                new string[] { "铁喷", "铁喷" },
                new string[] { "一代连喷", "一代连喷" }, new string[] { "二代连喷", "二代连喷" },
                new string[] { "15连", "15发连狙" }, new string[] { "30连", "30发连狙" },
                new string[] { "sg552", "sg552" }, new string[] { "uzi", "uzi" }, new string[] { "mac-10", "MAC-10" },
                new string[] { "m60", "M60" },
                new string[] { "改视角", "改视角" },
                new string[] { "火光", "火光消除" },
                new string[] { "药", "医疗物品\\药" }, new string[] { "针", "医疗物品\\针" }, new string[] { "医疗", "医疗物品\\包" },
                new string[] { "喷漆", "addons喷漆扩展" }, new string[] { "喷图", "addons喷漆扩展" },
                new string[] { "地图", "addons三方地图" }, new string[] { "战役", "addons三方地图" }
            };
            foreach (string[] kv in map) if (n.Contains(kv[0])) return kv[1].StartsWith("addons") ? kv[1] : ("addons各类物品储藏库\\" + kv[1]);
            return "addons各类物品储藏库\\杂项功能";
        }

        // 脚本库冲突已并入 ConflictCheck.DeepAll：同一批 vpk 只解析一遍

        // ---------- 装机记录 ----------
        public static void WriteManifest(string gameRoot, List<string> added)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("GameRoot=" + gameRoot);
                sb.AppendLine("Time=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                foreach (string a in added) sb.AppendLine("Added=" + a);
                File.WriteAllText(Path.Combine(Core.LogDir, "最近一次.ini"), sb.ToString(), new UTF8Encoding(true));
            }
            catch (Exception ex) { Core.Log("WARN", "装机记录写失败: " + ex.Message); }
        }

        // ---------- 自测：换整套只翻开关，文件不动 ----------
        /// <summary>
        /// 拿个假游戏目录跑一遍 SwitchAddons，检查：
        ///   这次选的 → 1；上一套（库里有的）→ 0；用户自己放的 → 一个字节都没被改；文件数量不变。
        /// </summary>
        public static int SelfTestAddons()
        {
            int bad = 0;
            string tmp = Path.Combine(Path.GetTempPath(), "l4d2-addonlist-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            string l4d2 = Path.Combine(tmp, "left4dead2");
            string addons = Path.Combine(l4d2, "addons");
            try
            {
                Directory.CreateDirectory(addons);
                Core.ScanMods();
                List<ModItem> pool = new List<ModItem>();
                foreach (ModItem m in Core.ModList)
                {
                    if (string.IsNullOrEmpty(m.Name)) continue;
                    pool.Add(m);
                    if (pool.Count >= 3) break;
                }
                if (pool.Count < 3) { Console.WriteLine("ADDON 库里不到 3 个 MOD，测不了"); return 1; }

                foreach (ModItem m in pool) File.WriteAllText(Path.Combine(addons, m.Name), "x");
                string mine = "我自己放的.vpk";
                File.WriteAllText(Path.Combine(addons, mine), "x");

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("\"AddonList\"");
                sb.AppendLine("{");
                foreach (ModItem m in pool) sb.AppendLine("\t\"" + m.Name + "\"\t\t\"1\"");
                sb.AppendLine("\t\"" + mine + "\"\t\t\"1\"");
                sb.AppendLine("}");
                File.WriteAllText(AddonList.FileOf(tmp), sb.ToString(), Encoding.Default);

                int on, off, keep;
                SwitchAddons(tmp, new List<ModItem> { pool[0] }, null, out on, out off, out keep);
                Dictionary<string, bool> st = AddonList.Read(tmp);

                bool a = st.ContainsKey(pool[0].Name) && st[pool[0].Name];
                bool b = st.ContainsKey(pool[1].Name) && !st[pool[1].Name];
                bool c = st.ContainsKey(pool[2].Name) && !st[pool[2].Name];
                bool d = st.ContainsKey(mine) && st[mine];           // 用户自己放的：没被关
                int files = Directory.GetFiles(addons, "*.vpk").Length;
                bool e = files == pool.Count + 1;                    // 文件一个没少

                if (!a) { bad++; Console.WriteLine("[FAIL] 这次选的没被打开: " + pool[0].Name); }
                if (!b) { bad++; Console.WriteLine("[FAIL] 上一套的没被关掉: " + pool[1].Name); }
                if (!c) { bad++; Console.WriteLine("[FAIL] 上一套的没被关掉: " + pool[2].Name); }
                if (!d) { bad++; Console.WriteLine("[FAIL] 用户自己放的被改动了: " + mine); }
                if (!e) { bad++; Console.WriteLine("[FAIL] addons 里文件数变了: " + files + " 个"); }
                Console.WriteLine("ADDON 挑一个装：打开 " + on + " / 关闭 " + off + " / 不认识的 " + keep
                    + "，文件 " + files + " 个（原 " + (pool.Count + 1) + "）");

                // 一个都不选 + 换整套 = 全关（用户自己的仍然不碰）
                SwitchAddons(tmp, new List<ModItem>(), null, out on, out off, out keep);
                st = AddonList.Read(tmp);
                bool g1, g2, g3;
                st.TryGetValue(pool[0].Name, out g1);
                st.TryGetValue(pool[1].Name, out g2);
                st.TryGetValue(pool[2].Name, out g3);
                bool f1 = !g1 && !g2 && !g3;
                bool f2;
                st.TryGetValue(mine, out f2);
                if (!f1) { bad++; Console.WriteLine("[FAIL] 一个都不选时，库里的没全关"); }
                if (!f2) { bad++; Console.WriteLine("[FAIL] 一个都不选时，用户自己放的被关了"); }
                int files2 = Directory.GetFiles(addons, "*.vpk").Length;
                if (files2 != files) { bad++; Console.WriteLine("[FAIL] 全关时文件数变了"); }
                Console.WriteLine("ADDON 一个都不选：全关=" + f1 + "，用户自己的还开着=" + f2 + "，文件 " + files2 + " 个");

                // 本地模式：从游戏 addons 删一项 → 文件真删掉、addonlist 里那条登记一起摘掉
                string savedMode = Core.Cfg.Mode, savedLog = Core.LogDir, savedGame = Core.Cfg.GameRoot;
                try
                {
                    Core.Cfg.Mode = "local";
                    Core.Cfg.GameRoot = tmp;
                    Core.LogDir = Path.Combine(tmp, "_装机记录");
                    Directory.CreateDirectory(Core.LogDir);
                    ModItem victim = pool[0];
                    victim.FullPath = Path.Combine(addons, victim.Name);
                    victim.FromGame = true;
                    string err = DeleteMod(victim);
                    bool moved = err == null && !File.Exists(victim.FullPath);
                    Dictionary<string, bool> st2 = AddonList.Read(tmp);
                    bool dropped = !st2.ContainsKey(victim.Name);
                    if (!moved) { bad++; Console.WriteLine("[FAIL] 本地模式删除没删掉文件: " + victim.Name + " " + err); }
                    if (!dropped) { bad++; Console.WriteLine("[FAIL] 删除后 addonlist 里还留着: " + victim.Name); }
                    Console.WriteLine("ADDON 本地模式删除：文件已删=" + moved + "，addonlist 摘掉=" + dropped);
                }
                finally
                {
                    Core.Cfg.Mode = savedMode;
                    Core.Cfg.GameRoot = savedGame;
                    Core.LogDir = savedLog;
                }
            }
            catch (Exception ex)
            {
                bad++;
                Console.WriteLine("[FAIL] ADDON 自测抛异常: " + ex.Message);
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
            return bad;
        }

        /// <summary>
        /// 手动腾地方：把 addonlist 里明确「关闭」的 MOD 挪回 U 盘（addons回收），再从 addons 删掉。
        /// 只动「关着 + 库里认识」的；开着的一概不碰。复制校验通过才删源文件。
        /// </summary>
        public static int ParkDisabled(string gameRoot)
        {
            Dictionary<string, bool> st = AddonList.Read(gameRoot);
            string addons = Path.Combine(Path.Combine(gameRoot, "left4dead2"), "addons");
            if (!Directory.Exists(addons)) return 0;
            HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Core.ModList != null)
                foreach (ModItem m in Core.ModList) if (!string.IsNullOrEmpty(m.Name)) known.Add(m.Name);
            string dst = Path.Combine(Core.ModRoot, "addons回收");
            int n = 0;
            foreach (string f in Directory.GetFiles(addons, "*.vpk"))
            {
                string name = Path.GetFileName(f);
                bool on;
                if (!st.TryGetValue(name, out on) || on) continue;      // 只动明确关掉的
                if (!known.Contains(name)) continue;                     // 不认识的别碰
                try
                {
                    if (!Directory.Exists(dst)) Directory.CreateDirectory(dst);
                    string target = Path.Combine(dst, name);
                    if (!Core.SameFile(f, target)) Core.CopyOne(f, target);
                    if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(f).Length)
                        throw new Exception("复制后大小对不上");
                    File.Delete(f);
                    n++;
                }
                catch (Exception ex) { Core.Log("WARN", "腾地方失败 " + name + ": " + ex.Message); }
            }
            Core.Log(n > 0 ? "OK" : "INFO", "清理关着的 MOD：" + (n > 0 ? "挪回 U 盘 " + n + " 个" : "没有可清理的"));
            return n;
        }

        // ---------- 跑外部程序并抓输出 ----------
        private static string RunCapture(string exe, string args, string workDir, int timeoutMs)
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.WorkingDirectory = workDir;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                using (Process p = Process.Start(psi))
                {
                    p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) sb.AppendLine(e.Data); };
                    p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) sb.AppendLine(e.Data); };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } }
                    else p.WaitForExit();
                }
            }
            catch (Exception ex) { sb.AppendLine("调用失败: " + ex.Message); }
            return sb.ToString();
        }
    }
}
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
        /// <summary>平台文件复制的结果：失败几个是判定「这次到底装成没有」的唯一依据。</summary>
        private class CopyStat
        {
            public int Total, Written, Skipped, Failed;
            public bool Cancelled;
        }

        public static bool InstallL4N(string gameRoot, Action<int, int> progress)
        {
            string zip = Core.FirstFile(Core.PkgDir, "L4N_v*.zip");
            string dir = Path.Combine(Core.PkgDir, "L4N平台\\L4N");
            try
            {
                CopyStat st;
                if (zip != null)
                {
                    string tmp = Path.Combine(Path.GetTempPath(), "l4n_" + DateTime.Now.Ticks);
                    Core.Log("INFO", "解压 " + Path.GetFileName(zip) + " …");
                    Unzip(zip, tmp);
                    string payload = ResolvePayloadRoot(tmp);
                    Core.Log("INFO", "解压到: " + tmp + "  实际内容根: " + payload);
                    st = CopyPayloadWithProgress(payload, gameRoot, progress);
                    try { Directory.Delete(tmp, true); } catch { }
                }
                else
                {
                    Core.Log("INFO", "用解压好的 L4N平台\\L4N 覆盖");
                    st = CopyPayloadWithProgress(dir, gameRoot, progress);
                }
                string dll = Path.Combine(gameRoot, "bin\\left4neko.dll");
                bool has = File.Exists(dll);
                // 光看「文件在不在」等于没判：覆盖重装时它本来就在，写失败了也照样算成功。
                // 现在要求：核心文件在 + 这一次没有一个文件复制失败 + 没被用户中途停掉。
                bool ok = has && st.Failed == 0 && !st.Cancelled;
                if (ok) Core.Log("OK", "L4N 核心 bin\\left4neko.dll 到位（写入 " + st.Written + " 个，跳过 " + st.Skipped + " 个）");
                else if (!has) Core.Log("FAIL", "没看到 bin\\left4neko.dll");
                else if (st.Cancelled) Core.Log("FAIL", "已停止：L4N 只写了一部分，算没装完");
                else Core.Log("FAIL", "L4N 有 " + st.Failed + " 个文件没写进去（游戏多半还开着），算没装好");
                return ok;
            }
            catch (Exception ex) { Core.Log("FAIL", "L4N 装失败: " + ex.Message); return false; }
        }

        private static CopyStat CopyPayloadWithProgress(string payload, string gameRoot, Action<int, int> progress)
        {
            CopyStat st = new CopyStat();
            string[] files = Directory.GetFiles(payload, "*", SearchOption.AllDirectories);
            st.Total = files.Length;
            int done = 0;
            foreach (string f in files)
            {
                if (CancelRequested) { st.Cancelled = true; Core.Log("WARN", "收到停止请求：平台文件在本文件结束后停止复制"); break; }
                try
                {
                    string rel = f.Substring(payload.Length).TrimStart('\\');
                    string dst = Path.Combine(gameRoot, rel);
                    if (!Core.SameFile(f, dst)) { Core.CopyOne(f, dst); st.Written++; }
                    else if (Overwrite) { Core.CopyOne(f, dst); st.Written++; }   // 一样也照写：覆盖模式就是不信现状
                    else st.Skipped++;
                }
                catch (Exception ex) { st.Failed++; Core.Log("WARN", "复制 " + Path.GetFileName(f) + " 失败: " + ex.Message); }
                done++;
                if (progress != null && (done % 10 == 0 || done == st.Total)) progress(done, st.Total);
            }
            Core.Log(st.Failed == 0 ? "INFO" : "WARN", "平台文件：写入 " + st.Written + " 个，已是最新跳过 " + st.Skipped + " 个"
                + (st.Failed > 0 ? "，失败 " + st.Failed + " 个" : "")
                + (Overwrite ? "（覆盖模式，全部重写）" : ""));
            return st;
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
            string dll = Path.Combine(gameRoot, "skeeto.dll");
            bool already = File.Exists(dll);
            if (already && !Overwrite)
            {
                Core.Log("OK", "Skeeto 已经装过（skeeto.dll 在），跳过。想重装就在「安装选项」里选「覆盖重装」。");
                if (!File.Exists(Path.Combine(gameRoot, "bin\\wininet.dll")))
                    Core.Log("WARN", "bin\\wininet.dll 不见了 —— 勾「覆盖重装」能把平台文件补回来");
                return true;
            }

            // 覆盖模式下 skeeto.dll 本来就在，光看「文件在不在」永远算成功：
            // 安装器一次都没写成，也会被记成「又完整装了一遍」。装之前先记一份基线。
            long baseLen = 0;
            DateTime baseTime = DateTime.MinValue;
            if (already)
            {
                try { FileInfo bf = new FileInfo(dll); baseLen = bf.Length; baseTime = bf.LastWriteTimeUtc; }
                catch { }
            }

            bool wrote = false;
            if (File.Exists(ps1))
            {
                foreach (string mode in new string[] { "Migrate", "Apply" })
                {
                    if (wrote) break;
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
                    wrote = SkeetoWritten(dll, already, baseLen, baseTime);
                    if (wrote)
                        Core.Log("OK", mode + " 跑完，skeeto.dll "
                            + (already ? "已重写（" + Core.Human(baseLen) + " → " + Core.Human(new FileInfo(dll).Length) + "）" : "到位"));
                    else
                        Core.Log("WARN", mode + " 跑完但 skeeto.dll "
                            + (File.Exists(dll) ? "和装之前一模一样，也和包里那份对不上" : "还是没有"));
                }
            }
            if (!wrote)
            {
                Core.Log("WARN", "改用整包复制 Skeeto");
                try { CopyTree(dir, gameRoot, null); } catch (Exception ex) { Core.Log("FAIL", "Skeeto 复制失败: " + ex.Message); }
                wrote = SkeetoWritten(dll, already, baseLen, baseTime);
            }

            if (!File.Exists(dll))
            {
                Core.Log("FAIL", "没看到 skeeto.dll");
                return false;
            }
            if (already && !wrote)
            {
                // 装之前就在、装完还是那份 —— 这次覆盖重装等于没做，不能报成功
                Core.Log("FAIL", "Skeeto 没装成功：skeeto.dll 还是装之前那份（" + Core.Human(baseLen) + "）");
                return false;
            }
            Core.Log("OK", already ? "skeeto.dll 与包里那份一致（这次没有重写）" : "skeeto.dll 到位");
            if (File.Exists(Path.Combine(gameRoot, "bin\\wininet.dll"))) Core.Log("OK", "bin\\wininet.dll（代理）到位");
            else Core.Log("WARN", "没看到 bin\\wininet.dll，Skeeto 可能不会加载");
            return true;
        }

        /// <summary>
        /// skeeto.dll 这次是不是真的到位：本来没有 → 在就行；
        /// 本来就有（覆盖重装）→ 要么大小/时间变了，要么内容已经和包里那份一致。
        /// 只判「文件在不在」的话，安装器一次没写成也会被算成功（v2.51 就是这么误报的）。
        /// </summary>
        static bool SkeetoWritten(string dll, bool already, long baseLen, DateTime baseTime)
        {
            try
            {
                FileInfo f = new FileInfo(dll);
                if (!f.Exists) return false;
                if (!already) return true;
                if (f.Length != baseLen || f.LastWriteTimeUtc != baseTime) return true;
                return SkeetoPayloadMatches(dll);
            }
            catch { return false; }
        }

        static bool SkeetoPayloadMatches(string dll)
        {
            try
            {
                string payload = Path.Combine(Path.Combine(Core.PkgDir, "Skeeto\\Skeeto"), "skeeto.dll");
                if (!File.Exists(payload)) return false;
                string a = Core.HashOf(dll), b = Core.HashOf(payload);
                return a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
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
                // 主库里没有这份 cfg 就是「这次不用搬」，不是失败。
                // 以前返回 false，换过主库的人每次装机都被记成「部分完成」。
                if (!File.Exists(src)) { Core.Log("INFO", "主库里没有 autoexec.cfg，这步跳过（不算失败）"); return true; }
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
            string manifest = Path.Combine(dir, "payload.sha256");
            string dll = Path.Combine(Path.Combine(Path.Combine(gameRoot, "bin"), "neko"), "mqcore\\mqcam.dll");
            if (File.Exists(dll) && !Overwrite)
            {
                // 「已经装过」也得核一遍：mqcam.dll 在，旁边的 plugins / 脚本可能早就没了。
                // 包里有 payload.sha256（哈希 + 相对路径），照着点一遍再说话。
                int total, bad; string detail;
                bool haveManifest = VerifyPayload(manifest, gameRoot, out total, out bad, out detail);
                if ((haveManifest && bad == 0) || (!haveManifest && File.Exists(dll)))
                {
                    Core.Log("OK", "MQCam 已经装过"
                        + (haveManifest ? "（" + total + " 个文件核对通过）" : "（没有清单，只看文件在不在）")
                        + "，跳过，不动相机设置");
                    return true;
                }
                Core.Log("WARN", "MQCam 文件对不上 " + bad + "/" + total + " 个：" + detail
                    + "；勾「覆盖重装」可以补回来");
                return false;
            }
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
            // 原来只判 mqcam.dll 在不在 —— 覆盖重装时它本来就在，装失败也照样报成功。
            // 有清单就按清单核内容；没有清单退回「文件在不在」并明说这是弱判定。
            int nTotal, nBad; string why;
            bool ok;
            if (VerifyPayload(manifest, gameRoot, out nTotal, out nBad, out why))
            {
                ok = nBad == 0;
                Core.Log(ok ? "OK" : "FAIL", ok
                    ? ("MQCam 装好，清单 " + nTotal + " 个文件全部核对通过")
                    : ("MQCam 有 " + nBad + "/" + nTotal + " 个文件不对：" + why));
            }
            else
            {
                ok = File.Exists(dll);
                Core.Log(ok ? "OK" : "FAIL", ok ? "mqcam.dll 到位（没有清单，只看文件在不在）" : "没看到 bin\\neko\\mqcore\\mqcam.dll");
            }
            if (ok) ResetCamState(gameRoot);
            return ok;
        }

        /// <summary>
        /// 按 payload.sha256 核对装到游戏里的文件（清单格式：SHA256 + TAB + 相对路径，路径用 /）。
        /// 配置文件（.ini）只要求存在：里面的值游戏/工具自己会改，按哈希比对必然误报。
        /// 返回 false 表示没有清单可读。
        /// </summary>
        private static bool VerifyPayload(string manifest, string gameRoot, out int total, out int bad, out string detail)
        {
            total = 0; bad = 0; detail = "";
            if (string.IsNullOrEmpty(manifest) || !File.Exists(manifest)) return false;
            List<string> missing = new List<string>();
            List<string> wrong = new List<string>();
            try
            {
                foreach (string raw in File.ReadAllLines(manifest))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    string[] p = line.Split(new char[] { '\t', ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length < 2) continue;
                    string hash = p[0].Trim();
                    string rel = p[1].Trim().Replace('/', '\\');
                    if (hash.Length != 64 || rel.Length == 0) continue;
                    total++;
                    string dst = Path.Combine(gameRoot, rel);
                    if (!File.Exists(dst)) { bad++; missing.Add(rel); continue; }
                    if (rel.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) continue;
                    string got = Core.HashOf(dst);
                    if (!string.Equals(got, hash, StringComparison.OrdinalIgnoreCase)) { bad++; wrong.Add(rel); }
                }
            }
            catch (Exception ex) { detail = "清单读不了：" + ex.Message; return false; }
            List<string> bits = new List<string>();
            if (missing.Count > 0) bits.Add("缺 " + string.Join("、", missing.ToArray()));
            if (wrong.Count > 0) bits.Add("内容不符 " + string.Join("、", wrong.ToArray()));
            detail = string.Join("；", bits.ToArray());
            return true;
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
                if (Process.GetProcessesByName("steam").Length > 0)
                {
                    // Steam 退出时会把 localconfig.vdf 整个写回去：现在写等于白写，
                    // 界面上还会显示「已写入」，用户以为成了。干脆不写，让他关掉再来。
                    Core.Log("WARN", "Steam 正在运行，这次不写启动项（它退出时会把配置覆盖回去）");
                    try { System.Windows.Forms.Clipboard.SetText(value); } catch { }
                    return "Steam 正在运行，没写成功：退出 Steam 后再点一次（启动项已复制到剪贴板）";
                }
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

        /// <summary>VDF 字符串里的转义。值里带引号/反斜杠时不转义，整份配置就解不开了。</summary>
        private static string VdfEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        /// <summary>从 open（必须指向 '{'）找配对的 '}'；字符串里的括号不算。找不到返回 -1。</summary>
        private static int MatchBrace(string text, int open)
        {
            if (open < 0 || open >= text.Length || text[open] != '{') return -1;
            int depth = 0;
            bool inStr = false;
            for (int i = open; i < text.Length; i++)
            {
                char ch = text[i];
                if (inStr)
                {
                    if (ch == '\\') { i++; continue; }   // 跳过 \" 这种转义
                    if (ch == '"') inStr = false;
                    continue;
                }
                if (ch == '"') { inStr = true; continue; }
                if (ch == '{') depth++;
                else if (ch == '}')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        private static bool PatchVdf(string path, string value)
        {
            string text = File.ReadAllText(path, Encoding.UTF8);
            // 只在 "apps" 块里找 550。以前是在整份文件里找第一个 "550"：
            // RecentApps / apptickets 里也可能出现 550，认错了就把启动项写进无关的块。
            int apps = text.IndexOf("\"apps\"", StringComparison.Ordinal);
            if (apps < 0) return false;
            int appsOpen = text.IndexOf('{', apps);
            if (appsOpen < 0) return false;
            int appsClose = MatchBrace(text, appsOpen);
            if (appsClose < 0) return false;

            string esc = VdfEscape(value);
            bool changed = false;
            int idx = text.IndexOf("\"550\"", appsOpen, StringComparison.Ordinal);
            if (idx < 0 || idx > appsClose)
            {
                string ins = "\r\n\t\t\t\t\t\"550\"\r\n\t\t\t\t\t{\r\n\t\t\t\t\t\t\"LaunchOptions\"\t\t\"" + esc + "\"\r\n\t\t\t\t\t}";
                text = text.Insert(appsOpen + 1, ins);
                changed = true;
            }
            else
            {
                int open = text.IndexOf('{', idx);
                if (open < 0 || open > appsClose) return false;
                int close = MatchBrace(text, open);
                if (close < 0) return false;
                string block = text.Substring(open, close - open);
                // 值里可能有 \" 转义：要按字符串规则吃完整段，不能停在第一个引号上
                Match lo = Regex.Match(block, "\"LaunchOptions\"\\s*\"(?:[^\"\\\\]|\\\\.)*\"");
                if (lo.Success)
                {
                    string nb = block.Substring(0, lo.Index) + "\"LaunchOptions\"\t\t\"" + esc + "\"" + block.Substring(lo.Index + lo.Length);
                    text = text.Substring(0, open) + nb + text.Substring(close);
                }
                else
                {
                    text = text.Insert(open + 1, "\r\n\t\t\t\t\t\t\"LaunchOptions\"\t\t\"" + esc + "\"");
                }
                changed = true;
            }
            if (!changed) return false;
            // 改之前留一份原样（只留第一份，不覆盖已有备份）：写坏了还能手工换回去
            string bak = path + ".bak";
            try
            {
                if (!File.Exists(bak)) File.Copy(path, bak, false);
            }
            catch (Exception ex) { Core.Log("WARN", "localconfig.vdf 备份失败（继续写）: " + ex.Message); }
            Core.WriteAllTextAtomic(path, text, new UTF8Encoding(false));
            // 写成功 ≠ 写对了：读回来核一遍，别出现「日志说写好了，文件里其实没有」。
            // 核对的是转义之后的串 —— 值里有引号时，文件里存的本来就不是原样。
            string back = File.ReadAllText(path, Encoding.UTF8);
            if (back.IndexOf(esc, StringComparison.Ordinal) < 0)
                throw new Exception("写完读回来找不到新启动项");
            return true;
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

        // ---------- 自测：localconfig.vdf 改写（play 时真 Steam 配置一个字节都不碰） ----------
        /// <summary>
        /// --launchself 的一部分。三种情况各造一个假 vdf：
        ///   有 550 块 → 只换 LaunchOptions 的值，别的键和结构不许动
        ///   有 apps 没 550 → 插一个 550 块进去
        ///   连 apps 都没有 → 返回 false，文件原样不动
        /// </summary>
        public static int SelfTestLaunchVdf()
        {
            int bad = 0;
            string dir = Path.Combine(Path.GetTempPath(), "l4d2-vdf-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Action<bool, string> chk = delegate(bool ok, string name)
            {
                if (ok) Core.Log("OK", "VDFSELF " + name);
                else { bad++; Core.Log("FAIL", "VDFSELF " + name); }
            };
            try
            {
                Directory.CreateDirectory(dir);

                // A. 已有 550 块 + 已有 LaunchOptions：只换值
                string a = Path.Combine(dir, "a.vdf");
                string aText = "\"UserLocalConfigStore\"\r\n{\r\n\t\"Software\"\r\n\t{\r\n\t\t\"Valve\"\r\n\t\t{\r\n"
                             + "\t\t\t\"Steam\"\r\n\t\t\t{\r\n\t\t\t\t\"apps\"\r\n\t\t\t\t{\r\n"
                             + "\t\t\t\t\t\"550\"\r\n\t\t\t\t\t{\r\n"
                             + "\t\t\t\t\t\t\"LaunchOptions\"\t\t\"-old\"\r\n"
                             + "\t\t\t\t\t\t\"LastPlayed\"\t\t\"123\"\r\n"
                             + "\t\t\t\t\t}\r\n\t\t\t\t}\r\n\t\t\t}\r\n\t\t}\r\n\t}\r\n}\r\n";
                File.WriteAllText(a, aText, new UTF8Encoding(false));
                bool okA = PatchVdf(a, "-new -novid");
                string backA = File.ReadAllText(a, Encoding.UTF8);
                chk(okA, "有 550 块时返回 true");
                chk(backA.Contains("\"-new -novid\""), "启动项已换成新值");
                chk(!backA.Contains("\"-old\""), "旧启动项已被替换");
                chk(backA.Contains("\"LastPlayed\"") && backA.Contains("\"UserLocalConfigStore\""), "其它键和结构没被动");
                chk(File.Exists(a + ".bak"), "改写前留了 .bak");
                chk(File.ReadAllText(a + ".bak", Encoding.UTF8) == aText, ".bak 是改写前的原样");

                // B. 有 apps、没有 550：插一个 550 块
                string b = Path.Combine(dir, "b.vdf");
                File.WriteAllText(b, "\"root\"\r\n{\r\n\t\"apps\"\r\n\t{\r\n\t}\r\n}\r\n", new UTF8Encoding(false));
                bool okB = PatchVdf(b, "-new");
                chk(okB && File.ReadAllText(b, Encoding.UTF8).Contains("\"-new\""), "没有 550 时能插进去");

                // C. 连 apps 都没有：不写、不改
                string c = Path.Combine(dir, "c.vdf");
                string cText = "\"root\"\r\n{\r\n}\r\n";
                File.WriteAllText(c, cText, new UTF8Encoding(false));
                bool okC = PatchVdf(c, "-new");
                chk(!okC, "找不到 apps 时返回 false");
                chk(File.ReadAllText(c, Encoding.UTF8) == cText, "找不到 apps 时文件一个字没动");

                // D. 值里有引号和反斜杠：写进去必须转义，否则整份 vdf 解不开
                string d = Path.Combine(dir, "d.vdf");
                File.WriteAllText(d, aText, new UTF8Encoding(false));
                string tricky = "+exec \"D:\\l4d2 cfg\\auto.cfg\" -novid";
                bool okD = PatchVdf(d, tricky);
                string backD = File.ReadAllText(d, Encoding.UTF8);
                chk(okD, "带引号的值能写");
                chk(backD.Contains("+exec \\\"D:\\\\l4d2 cfg\\\\auto.cfg\\\""), "引号和反斜杠已转义（" + backD.Length + " 字节）");
                chk(!backD.Contains("\"D:\\l4d2 cfg\\auto.cfg\""), "引号没有被裸写进配置");
                chk(backD.Contains("\"LastPlayed\"") && backD.Contains("\t\"550\"") , "写带引号的值也没破坏结构");
                // 再写一遍普通值：从「值里带转义」改回普通值，不能残留旧的那半截
                bool okD2 = PatchVdf(d, "-again");
                string backD2 = File.ReadAllText(d, Encoding.UTF8);
                chk(okD2 && backD2.Contains("\"-again\"") && !backD2.Contains("auto.cfg"), "带转义的值能被整段替换掉");
                chk(CountOf(backD2, "\"LaunchOptions\"") == 1, "替换后只剩一条 LaunchOptions");

                // E. 550 在 apps 之外也出现过（RecentApps）：不能写进那个块
                string e = Path.Combine(dir, "e.vdf");
                string eText = "\"UserLocalConfigStore\"\r\n{\r\n\t\"Software\"\r\n\t{\r\n\t\t\"Valve\"\r\n\t\t{\r\n"
                             + "\t\t\t\"Steam\"\r\n\t\t\t{\r\n\t\t\t\t\"RecentApps\"\r\n\t\t\t\t{\r\n"
                             + "\t\t\t\t\t\"550\"\r\n\t\t\t\t\t{\r\n\t\t\t\t\t\t\"LastPlayed\"\t\t\"1\"\r\n\t\t\t\t\t}\r\n"
                             + "\t\t\t\t}\r\n\t\t\t\t\"apps\"\r\n\t\t\t\t{\r\n\t\t\t\t}\r\n\t\t\t}\r\n\t\t}\r\n\t}\r\n}\r\n";
                File.WriteAllText(e, eText, new UTF8Encoding(false));
                bool okE = PatchVdf(e, "-scoped");
                string backE = File.ReadAllText(e, Encoding.UTF8);
                int recentAt = backE.IndexOf("\"RecentApps\"", StringComparison.Ordinal);
                int loAt = backE.IndexOf("\"LaunchOptions\"", StringComparison.Ordinal);
                int appsAt = backE.IndexOf("\"apps\"", StringComparison.Ordinal);
                chk(okE, "550 只在 apps 外时也能写");
                chk(loAt > appsAt, "启动项写在 apps 块里（不是写进 RecentApps）");
                chk(recentAt >= 0, "RecentApps 那段还在");
                chk(CountOf(backE, "\"LaunchOptions\"") == 1, "只插了一条 LaunchOptions");
            }
            catch (Exception ex)
            {
                Core.Log("FAIL", "VDFSELF 自检本身出错: " + ex.Message);
                bad++;
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
            return bad;
        }

        /// <summary>数一个子串出现几次（自测用；IndexOf 循环，不引入正则）。</summary>
        private static int CountOf(string text, string needle)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(needle)) return 0;
            int n = 0, i = 0;
            while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
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
            // 最后一道防线：压缩包里的条目 FullPath 指向的是那个 rar/zip 本身，
            // 删「一条」等于删整包（包里其它 MOD 一起没了）。界面上都拦过，这里再拦一次。
            if (m.IsArchive) return "「" + m.Name + "」在压缩包里（" + m.Container + "），本工具不删压缩包";
            try { File.Delete(m.FullPath); }
            catch (Exception ex)
            {
                Core.Log("WARN", "删除 " + m.FullPath + " 失败: " + ex.Message);
                return ex.Message;
            }
            Core.Log("OK", "已删除 " + m.FullPath);
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

        /// <summary>清重计划：先算清楚要删哪些、留哪些，再让界面照着清单确认。</summary>
        public class DupPlan
        {
            /// <summary>内容与另一份完全一致，可以删。</summary>
            public List<ModItem> Drop = new List<ModItem>();
            /// <summary>每个内容组留下的那一份。</summary>
            public List<ModItem> Keep = new List<ModItem>();
            /// <summary>同名但内容不一样（或读不出来）：一律不动，只在清单里列出来。</summary>
            public List<ModItem> Differ = new List<ModItem>();
            public int ArchiveSkipped;
            public bool NothingToDo { get { return Drop.Count == 0 && Differ.Count == 0; } }
        }

        /// <summary>扫库并算出清重清单（不删任何东西）。</summary>
        public static DupPlan FindDuplicateMods()
        {
            Core.ScanMods();
            return PlanDuplicates(Core.ModList);
        }

        /// <summary>
        /// 按「同名 + 同大小 + 同内容」分组。
        /// 原来只按文件名分组、留第一个删其余 —— 两个来源的同名 vpk 内容根本不一样，
        /// 照样被当重复删掉一份，而且留哪份只看扫描顺序。
        ///
        /// 现在的内容判定和「重复嫌疑」窗口共用同一个指纹函数（DupVerify.OfPath，CRC32），
        /// 不再是一套 SHA256、一套 CRC32 —— 两边结论不一样，用户会以为哪个在骗他。
        /// 顺序：同名 → 体积必须一致（不一致一定不是同一个文件，连读都不用读）→ CRC32 → 分桶 → 删前 SHA256 复核。
        /// </summary>
        public static DupPlan PlanDuplicates(List<ModItem> items)
        {
            DupPlan plan = new DupPlan();
            Dictionary<string, List<ModItem>> byName = new Dictionary<string, List<ModItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (ModItem m in items)
            {
                if (m.FromGame) continue;   // 游戏目录里的不参与清重：它和库里同名的那份是「已装的一份」
                // 压缩包里的条目共用同一个容器文件：删掉一条 = 删掉整个包，包里其它 MOD 全陪葬。
                if (m.IsArchive) { plan.ArchiveSkipped++; continue; }
                if (!byName.ContainsKey(m.Name)) byName[m.Name] = new List<ModItem>();
                byName[m.Name].Add(m);
            }

            foreach (KeyValuePair<string, List<ModItem>> kv in byName)
            {
                if (kv.Value.Count < 2) continue;

                // 体积不同的同名文件：不可能同内容，直接列为「内容不同，自己看着办」
                Dictionary<long, List<ModItem>> bySize = new Dictionary<long, List<ModItem>>();
                foreach (ModItem m in kv.Value)
                {
                    if (!bySize.ContainsKey(m.Size)) bySize[m.Size] = new List<ModItem>();
                    bySize[m.Size].Add(m);
                }

                List<ModItem> unknown = new List<ModItem>();
                List<List<ModItem>> kinds = new List<List<ModItem>>();
                foreach (List<ModItem> sameSize in bySize.Values)
                {
                    if (sameSize.Count < 2) { kinds.Add(sameSize); plan.Keep.Add(sameSize[0]); continue; }   // 只有一份这种体积，单独算一种
                    Dictionary<string, List<ModItem>> byCrc = new Dictionary<string, List<ModItem>>(StringComparer.OrdinalIgnoreCase);
                    foreach (ModItem m in sameSize)
                    {
                        string why;
                        string fp = DupVerify.OfPath(m.FullPath, out why);
                        if (string.IsNullOrEmpty(fp)) { unknown.Add(m); continue; }   // 读不出来就不碰
                        if (!byCrc.ContainsKey(fp)) byCrc[fp] = new List<ModItem>();
                        byCrc[fp].Add(m);
                    }
                    foreach (List<ModItem> g in byCrc.Values)
                    {
                        kinds.Add(g);
                        plan.Keep.Add(g[0]);          // 每种内容留一份，Keep 就是这份
                        if (g.Count < 2) continue;
                        // CRC32 只有 32 位。分组用它（快），真要删之前用 SHA256 复核一遍：
                        // 万一撞上碰撞，宁可留着让用户自己看，也不能删错文件。
                        string keepSha = Core.HashOf(g[0].FullPath);
                        for (int i = 1; i < g.Count; i++)
                        {
                            string sha = keepSha == null ? null : Core.HashOf(g[i].FullPath);
                            if (sha == null || !string.Equals(sha, keepSha, StringComparison.OrdinalIgnoreCase))
                            {
                                unknown.Add(g[i]);
                                Core.Log("WARN", "清重：CRC 相同但 SHA256 不符（或复核读不出），未删除 —— " + g[i].Name);
                                continue;
                            }
                            plan.Drop.Add(g[i]);
                        }
                    }
                }

                if (kinds.Count > 1)
                {
                    // 同名的几种内容都留着，让人自己决定删哪个
                    foreach (List<ModItem> g in kinds) plan.Differ.Add(g[0]);
                    Core.Log("WARN", "清重：同名但内容不同，未删除 —— " + kv.Key + "（" + kinds.Count + " 种内容）");
                }
                if (unknown.Count > 0)
                {
                    plan.Differ.AddRange(unknown);
                    Core.Log("WARN", "清重：同名文件读不出来（可能被占用），未删除 —— " + kv.Key);
                }
            }
            return plan;
        }

        /// <summary>按清单真删。返回删掉的条目（给人的一句话说明）。</summary>
        public static List<string> DeleteDuplicates(DupPlan plan)
        {
            List<string> done = new List<string>();
            List<ModItem> gone = new List<ModItem>();
            foreach (ModItem m in plan.Drop)
            {
                try
                {
                    string err = DeleteMod(m);
                    if (err != null) throw new Exception(err);
                    done.Add(m.Name + "  (" + m.Cat + " · " + m.Sub + ")");
                    gone.Add(m);
                }
                catch (Exception ex) { Core.Log("WARN", "清理 " + m.Name + " 失败: " + ex.Message); }
            }
            // 文件已经删掉，直接把它从内存列表里摘掉，不再花几秒重扫全库
            foreach (ModItem g in gone) { Core.ModList.Remove(g); Core.ByRel.Remove(g.Key); }
            return done;
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
            // 删掉的那些 vpk 名：还要从 addonlist 里摘掉登记。
            // 不摘的话下次装回同名 MOD，会继承上次那条「0 = 关闭」，装上却玩不到。
            List<string> goneAddons = new List<string>();
            int del = 0;
            foreach (string rel in targets)
            {
                if (string.IsNullOrEmpty(rel)) continue;
                string key = rel.TrimStart('\\');
                if (!uniq.Add(key)) continue;
                // 清单来自安装包/压缩包，可能带 ../ ：越界的路径一律不删
                string full = Path.Combine(gameRoot, key);
                if (!Core.IsUnder(gameRoot, full)) { Core.Log("WARN", "包清单里有越界路径，已跳过: " + key); continue; }
                try
                {
                    if (File.Exists(full)) { File.Delete(full); del++; }
                    if (key.StartsWith("left4dead2\\addons\\", StringComparison.OrdinalIgnoreCase))
                        goneAddons.Add(Path.GetFileName(key));
                }
                catch (Exception ex) { Core.Log("WARN", "删除 " + key + " 失败: " + ex.Message); }
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
            int moved = 0, bad = 0, failed = 0;
            foreach (string a in added)
            {
                // 装机记录是纯文本，可能被手改过：只认「直接躺在 addons 下的单个文件名」。
                // 带目录分隔符 / 盘符 / .. 的一律不删 —— 否则记录里写一行 ..\..\ 就能删到游戏目录外。
                string name = (a ?? "").Trim();
                if (name.Length == 0) continue;
                if (name.IndexOfAny(new char[] { '\\', '/', ':' }) >= 0 || name.IndexOf("..", StringComparison.Ordinal) >= 0)
                {
                    bad++;
                    Core.Log("WARN", "装机记录里有可疑路径，已忽略: " + name);
                    continue;
                }
                string p = Path.Combine(addons, name);
                if (!Core.IsUnder(addons, p)) { bad++; Core.Log("WARN", "装机记录里的路径越界，已忽略: " + name); continue; }
                try
                {
                    if (File.Exists(p)) { File.Delete(p); moved++; }
                    goneAddons.Add(name);
                }
                catch (Exception ex) { failed++; Core.Log("WARN", "删除 " + name + " 失败: " + ex.Message); }
            }
            // 删掉的文件在 addonlist 里留了一条指向空气的开关：游戏读不到文件不会报错，
            // 但同一名字下次装回来时，这条旧的 0 会把它默默关掉。既然文件是我们删的，登记一起摘。
            if (goneAddons.Count > 0)
            {
                try
                {
                    int cleared = AddonList.Remove(gameRoot, goneAddons);
                    if (cleared > 0) report.Add("addonlist 里指向这些文件的登记一并摘掉 " + cleared + " 条");
                }
                catch (Exception ex) { Core.Log("WARN", "还原时清理 addonlist 失败: " + ex.Message); }
            }
            if (added.Count > 0)
                report.Add("本次铺的 MOD 已删除 " + moved + " 个"
                    + (failed > 0 ? "，失败 " + failed + " 个（详见日志）" : "")
                    + (bad > 0 ? "，跳过可疑记录 " + bad + " 条" : ""));
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

            // 7z 也要收：识别、装机、冲突检查都支持 7z 了，只有这里还只扫 rar/zip，
            // 结果「批量导入」对着一个 7z 文件夹一个字都不收，也不说为什么。
            foreach (string pat in new string[] { "*.rar", "*.zip", "*.7z" })
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
                foreach (string a in added)
                {
                    string name = Path.GetFileName((a ?? "").Trim());
                    // 只记文件名：这份记录后面会被「一键还原」逐行拿来删文件，不能写进任何带路径的东西
                    if (name.Length > 0) sb.AppendLine("Added=" + name);
                }
                Core.WriteAllTextAtomic(Path.Combine(Core.LogDir, "最近一次.ini"), sb.ToString(), new UTF8Encoding(true));
            }
            catch (Exception ex) { Core.Log("WARN", "装机记录写失败: " + ex.Message); }
        }

        // ---------- 自测：写/删的底线（在真正放数据的那个卷上跑） ----------
        /// <summary>
        /// --safeself：验证「写要整份落地、删不能删整包、路径不能越界」这三条。
        /// 特意建在 _装机记录 下面 —— 那是装 MOD 的同一个卷（U 盘多半是 exFAT），
        /// 换名/覆盖的行为跟系统盘 NTFS 不一样，只有在真盘上跑才算数。
        /// </summary>
        public static int SelfTestSafe()
        {
            int bad = 0;
            string dir = Path.Combine(Core.LogDir, "safeself-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Console.WriteLine("SAFE 测试目录: " + dir);
            try
            {
                Directory.CreateDirectory(dir);
                byte[] data = new byte[512 * 1024];
                for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 7);
                string src = Path.Combine(dir, "src.bin");
                File.WriteAllBytes(src, data);

                // 0) 写盘互斥：同一时间只允许一件事在跑，释放必须按名字
                SafeCheck(ref bad, OpGuard.Enter("selftest-A"), "互斥：第一个操作能拿到写盘权");
                SafeCheck(ref bad, !OpGuard.Enter("selftest-B"), "互斥：已有操作在跑时，第二个必须被拒");
                OpGuard.Exit("selftest-B");                 // 名字不对：不能把 A 的锁放掉
                SafeCheck(ref bad, !OpGuard.Enter("selftest-B"), "互斥：别人的 Exit 不能误放行");
                OpGuard.Exit("selftest-A");
                SafeCheck(ref bad, OpGuard.Enter("selftest-B"), "互斥：正确释放后能再进入");
                OpGuard.Exit("selftest-B");

                // 1) 覆盖写：目标已存在也要整份换掉，且不留 .part
                string dst = Path.Combine(dir, "a.vpk");
                File.WriteAllBytes(dst, new byte[] { 1, 2, 3 });
                string h;
                Core.CopyOneHash(src, dst, out h);
                SafeCheck(ref bad, new FileInfo(dst).Length == data.Length, "覆盖写后大小正确");
                SafeCheck(ref bad, string.Equals(Core.HashOf(dst), h, StringComparison.OrdinalIgnoreCase), "覆盖写后内容哈希一致");

                // 2) 目标被占用：必须报错（不能假装成功），旧文件一个字节都不许动
                File.WriteAllBytes(dst, new byte[] { 9, 9 });
                bool threw = false;
                using (FileStream hold = new FileStream(dst, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    try { Core.CopyOneHash(src, dst, out h); }
                    catch { threw = true; }
                }
                SafeCheck(ref bad, threw, "目标被占用时复制会失败");
                SafeCheck(ref bad, new FileInfo(dst).Length == 2, "复制失败后旧文件原样保留");

                // 3) 从 zip 里取一个条目
                string arc = Path.Combine(dir, "pack.zip");
                using (System.IO.Compression.ZipArchive za = System.IO.Compression.ZipFile.Open(arc, System.IO.Compression.ZipArchiveMode.Create))
                {
                    System.IO.Compression.ZipArchiveEntry e = za.CreateEntry("ak47.vpk");
                    using (Stream s = e.Open()) s.Write(data, 0, 4096);
                }
                string ex = Path.Combine(dir, "ex.vpk");
                string why;
                bool got = Catalog.ExtractEntryTo(arc, "ak47.vpk", ex, out why);
                SafeCheck(ref bad, got && new FileInfo(ex).Length == 4096, "zip 条目解出成功且大小正确" + (got ? "" : "：" + why));

                // 4) 压缩包里的条目：绝不能删整包
                ModItem it = new ModItem();
                it.Name = "ak47.vpk";
                it.FullPath = arc;
                it.Container = "pack.zip";
                it.Inner = "ak47.vpk";
                string err = DeleteMod(it);
                SafeCheck(ref bad, err != null, "压缩包内条目删除被拒");
                SafeCheck(ref bad, File.Exists(arc), "压缩包本体没被删");

                // 5) 清重只删「同名且内容一样」的：同名不同内容必须留着
                string dA = Path.Combine(dir, "dupA"), dB = Path.Combine(dir, "dupB"), dC = Path.Combine(dir, "dupC");
                Directory.CreateDirectory(dA); Directory.CreateDirectory(dB); Directory.CreateDirectory(dC);
                File.WriteAllBytes(Path.Combine(dA, "ak47.vpk"), data);
                File.WriteAllBytes(Path.Combine(dB, "ak47.vpk"), data);
                byte[] other = new byte[data.Length];
                Array.Copy(data, other, data.Length);
                other[0] ^= 0xFF;                                   // 同名，内容差一个字节
                File.WriteAllBytes(Path.Combine(dC, "ak47.vpk"), other);
                List<ModItem> fake = new List<ModItem>();
                foreach (string p in new string[] { Path.Combine(dA, "ak47.vpk"), Path.Combine(dB, "ak47.vpk"), Path.Combine(dC, "ak47.vpk") })
                {
                    ModItem mi = new ModItem();
                    mi.Name = "ak47.vpk";
                    mi.FullPath = p;
                    mi.RelPath = p;
                    fake.Add(mi);
                }
                DupPlan plan = PlanDuplicates(fake);
                SafeCheck(ref bad, plan.Drop.Count == 1, "清重：3 个同名文件里只挑出 1 个真重复（实际 " + plan.Drop.Count + "）");
                SafeCheck(ref bad, plan.Drop.Count == 1 && plan.Drop[0].FullPath.IndexOf("dupB", StringComparison.OrdinalIgnoreCase) >= 0,
                    "清重：留第一份（dupA），删第二份（dupB）");
                SafeCheck(ref bad, plan.Differ.Count == 2, "清重：同名但内容不同的两个都留下（实际 " + plan.Differ.Count + "）");
                SafeCheck(ref bad, File.Exists(Path.Combine(dC, "ak47.vpk")) && File.Exists(Path.Combine(dA, "ak47.vpk")),
                    "清重清单只算不删，文件一个没动");

                // 5b) 指纹缓存不能把「换过内容的同名文件」当成同一份
                string crcA1, crcA2;
                string whyCrc;
                crcA1 = DupVerify.OfPath(Path.Combine(dC, "ak47.vpk"), out whyCrc);
                File.WriteAllBytes(Path.Combine(dC, "ak47.vpk"), data);        // 内容换成和 A 一样
                crcA2 = DupVerify.OfPath(Path.Combine(dC, "ak47.vpk"), out whyCrc);
                SafeCheck(ref bad, crcA1 != null && crcA2 != null && crcA1 != crcA2,
                    "清重：文件被换掉后指纹跟着变（不能拿旧结论当新结论）");

                // 5c) 同名但体积不同：连读都不用读，直接判「不是同一个文件」
                string dD = Path.Combine(dir, "dupD"), dE = Path.Combine(dir, "dupE");
                Directory.CreateDirectory(dD); Directory.CreateDirectory(dE);
                File.WriteAllBytes(Path.Combine(dD, "ak47.vpk"), new byte[data.Length + 4096]);
                File.WriteAllBytes(Path.Combine(dE, "ak47.vpk"), new byte[data.Length + 8192]);
                List<ModItem> fake2 = new List<ModItem>();
                foreach (string p in new string[] { Path.Combine(dA, "ak47.vpk"), Path.Combine(dD, "ak47.vpk"), Path.Combine(dE, "ak47.vpk") })
                {
                    ModItem mi = new ModItem();
                    mi.Name = "ak47.vpk";
                    mi.FullPath = p;
                    mi.RelPath = p;
                    mi.Size = new FileInfo(p).Length;
                    fake2.Add(mi);
                }
                DupPlan plan2 = PlanDuplicates(fake2);
                SafeCheck(ref bad, plan2.Drop.Count == 0, "清重：同名但体积不同的一个都不删（实际删了 " + plan2.Drop.Count + "）");
                SafeCheck(ref bad, plan2.Differ.Count == 3, "清重：三种体积各留一份报给人（实际 " + plan2.Differ.Count + "）");

                // 6) MQCam 那种「按清单核内容」：dll 必须哈希一致，.ini 只看在不在（里面的值会被游戏改）
                string gt = Path.Combine(dir, "fakeroot");
                string mqDir = Path.Combine(gt, "bin\\neko\\mqcore");
                Directory.CreateDirectory(mqDir);
                string mqDll = Path.Combine(mqDir, "mqcam.dll");
                string mqIni = Path.Combine(mqDir, "mqcam.ini");
                File.WriteAllBytes(mqDll, data);
                File.WriteAllText(mqIni, "fov=90");
                string mf = Path.Combine(dir, "payload.sha256");
                File.WriteAllText(mf,
                    Core.HashOf(mqDll) + "\tbin/neko/mqcore/mqcam.dll\r\n"
                    + new string('A', 64) + "\tbin/neko/mqcore/mqcam.ini\r\n"
                    + new string('B', 64) + "\tbin/neko/plugins/mqcore.dll\r\n",
                    new UTF8Encoding(false));
                int mt, mb; string mdetail;
                bool gotMf = VerifyPayload(mf, gt, out mt, out mb, out mdetail);
                SafeCheck(ref bad, gotMf && mt == 3, "清单核对：解析出 3 条（实际 " + mt + "）");
                SafeCheck(ref bad, gotMf && mb == 1, "清单核对：缺 1 个文件只算 1 个不对（实际 " + mb + "）");
                SafeCheck(ref bad, mdetail.IndexOf("mqcore.dll", StringComparison.OrdinalIgnoreCase) >= 0, "清单核对：报出缺的是哪个文件");
                File.WriteAllBytes(mqDll, other);          // 内容被换掉
                VerifyPayload(mf, gt, out mt, out mb, out mdetail);
                SafeCheck(ref bad, mb == 2 && mdetail.IndexOf("mqcam.dll", StringComparison.OrdinalIgnoreCase) >= 0,
                    "清单核对：内容不符也算不对（实际 " + mb + "）");
                File.WriteAllBytes(mqDll, data);
                File.WriteAllText(mqIni, "fov=120");       // 用户改过 ini
                VerifyPayload(mf, gt, out mt, out mb, out mdetail);
                SafeCheck(ref bad, mb == 1, "清单核对：ini 被改过不算错（实际 " + mb + "）");

                // 6) 并发扫库：两条线程同时扫，不能把自己的列表清成半截
                Core.ScanMods();
                int baseline = Core.ModList.Count;
                int raceErrors = 0;
                int[] seen = new int[3];
                System.Threading.Thread[] ths = new System.Threading.Thread[3];
                for (int ti = 0; ti < ths.Length; ti++)
                {
                    int idx = ti;
                    ths[ti] = new System.Threading.Thread(delegate()
                    {
                        try
                        {
                            Core.ScanMods();
                            seen[idx] = Core.ModList.Count;
                            if (seen[idx] <= 0) System.Threading.Interlocked.Increment(ref raceErrors);
                        }
                        catch (Exception exs)
                        {
                            System.Threading.Interlocked.Increment(ref raceErrors);
                            Console.WriteLine("RACE 扫描异常: " + exs.Message);
                        }
                    });
                    ths[ti].IsBackground = true;
                    ths[ti].Start();
                }
                bool allDone = true;
                foreach (System.Threading.Thread t in ths) if (!t.Join(180000)) allDone = false;
                SafeCheck(ref bad, allDone, "并发扫描都在 3 分钟内结束（没有互相等死）");
                SafeCheck(ref bad, raceErrors == 0, "并发扫描没有异常、没有空表");
                Core.ScanMods();
                if (baseline > 0)
                    SafeCheck(ref bad, Core.ModList.Count == baseline,
                        "并发扫描后条目数不变（" + Core.ModList.Count + " / " + baseline + "）");
                else Console.WriteLine("SKIP  库是空的，并发扫描只验证了不炸");

                // 只报数字不断言：压缩包里的包要等包变过或用户点详情才补上标题，
                // 这里断言「必须 N 个」等于把库的内容写死在自测里。
                int titled = 0;
                foreach (ModItem mi in Core.ModList) if (!string.IsNullOrEmpty(mi.AddonTitle)) titled++;
                Console.WriteLine("INFO  addoninfo：库内 " + Core.ModList.Count + " 个条目，其中 "
                    + titled + " 个带包内标题");

                // 7) 中途失败不留垃圾
                SafeCheck(ref bad, Directory.GetFiles(dir, "*.part", SearchOption.TopDirectoryOnly).Length == 0, "没有残留 .part");
                SafeCheck(ref bad, Directory.GetDirectories(dir, "~l4d2-*").Length == 0, "没有残留解包暂存目录");

                // 8) 路径校验
                SafeCheck(ref bad, Core.IsUnder(dir, Path.Combine(dir, "left4dead2\\addons\\x.vpk")), "子路径判定在目录内");
                SafeCheck(ref bad, !Core.IsUnder(dir, Path.Combine(dir, "..\\x.vpk")), ".. 往上跳判定在目录外");

                // 9) 包内 addoninfo：游戏 addons 菜单显示的是这里的 addonTitle，和文件名常常不是一回事。
                //    读取器要是认错了，详情里写的就是别的 MOD 的名字，比没有更糟。
                string vp1 = Path.Combine(dir, "info-preload.vpk");
                File.WriteAllBytes(vp1, BuildTinyVpk("\"AddonInfo\"\r\n{\r\n\tAddonTitle \"MWII RPK Macabre(AK47)\"\r\n\tAddonAuthor \"芬吸员\"\r\n}\r\n", true));
                Vpk.AddonMeta i1 = Vpk.ReadAddonInfo(vp1);
                SafeCheck(ref bad, i1.Found && i1.Readable && i1.Title == "MWII RPK Macabre(AK47)",
                    "addoninfo：目录表预载里的标题读对了（实际「" + i1.Title + "」）");
                SafeCheck(ref bad, i1.Author == "芬吸员", "addoninfo：作者读对了（实际「" + i1.Author + "」）");

                string vp2 = Path.Combine(dir, "info-data.vpk");
                File.WriteAllBytes(vp2, BuildTinyVpk("\"AddonInfo\"\r\n{\r\n\t\"addonTitle\"\t\t\"CSO 白虎  mp5 \"\r\n}\r\n", false));
                SafeCheck(ref bad, Vpk.ReadAddonInfo(vp2).Title == "CSO 白虎  mp5",
                    "addoninfo：数据段里带引号、带多余空格的标题读对了");

                string vp3 = Path.Combine(dir, "info-odd.vpk");
                File.WriteAllBytes(vp3, BuildTinyVpk("addontitle CODOL自由霰弹(pumpshot)\"\r\n", true));
                SafeCheck(ref bad, Vpk.ReadAddonInfo(vp3).Title == "CODOL自由霰弹(pumpshot)",
                    "addoninfo：漏写前引号的野格式也能读");

                string vp4 = Path.Combine(dir, "info-none.vpk");
                File.WriteAllBytes(vp4, BuildTinyVpk(null, false));
                Vpk.AddonMeta i4 = Vpk.ReadAddonInfo(vp4);
                SafeCheck(ref bad, !i4.Found && i4.Title.Length == 0, "addoninfo：包里没有就留空，不瞎编");

                string vp5 = Path.Combine(dir, "info-broken.vpk");
                File.WriteAllBytes(vp5, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 });
                SafeCheck(ref bad, Vpk.ReadAddonInfo(vp5).Title.Length == 0, "addoninfo：怪文件不崩、返回空");

                SafeCheck(ref bad, ModIdentify.Analyze(vp1).Title == "MWII RPK Macabre(AK47)",
                    "addoninfo：识别结果里带上了标题（详情和搜索都读它）");

                // 压缩包里的 vpk 扫库时只拿得到目录表，走的是另一条路
                byte[] tiny = BuildTinyVpk("\"AddonInfo\"\r\n{\r\n\tAddonTitle \"压缩包里的标题\"\r\n}\r\n", true);
                int treeLen = (int)BitConverter.ToUInt32(tiny, 8);
                byte[] onlyTree = new byte[treeLen];
                Array.Copy(tiny, 12, onlyTree, 0, treeLen);
                SafeCheck(ref bad, Vpk.InfoFromTreeBuffer(onlyTree).Title == "压缩包里的标题",
                    "addoninfo：只有目录表也能读预载里的标题（压缩包条目走这条路）");

                byte[] dataOne = BuildTinyVpk("\"AddonInfo\"\r\n{\r\n\tAddonTitle \"在数据段\"\r\n}\r\n", false);
                int tl2 = (int)BitConverter.ToUInt32(dataOne, 8);
                byte[] onlyTree2 = new byte[tl2];
                Array.Copy(dataOne, 12, onlyTree2, 0, tl2);
                Vpk.AddonMeta i6 = Vpk.InfoFromTreeBuffer(onlyTree2);
                SafeCheck(ref bad, i6.Found && i6.Title.Length == 0,
                    "addoninfo：正文在数据段时只有目录表读不到，但不瞎编（Found=" + i6.Found + "）");

                // 压缩包里的 vpk：实测 addoninfo 大多在数据段，得顺着流读到那个位置
                string zp = Path.Combine(dir, "arc.zip");
                using (System.IO.Compression.ZipArchive za =
                       System.IO.Compression.ZipFile.Open(zp, System.IO.Compression.ZipArchiveMode.Create))
                {
                    System.IO.Compression.ZipArchiveEntry ze = za.CreateEntry("inner.vpk");
                    using (Stream zs = ze.Open())
                    {
                        byte[] zb = BuildTinyVpk("\"AddonInfo\"\r\n{\r\n\tAddonTitle \"压缩包数据段标题\"\r\n}\r\n", false);
                        zs.Write(zb, 0, zb.Length);
                    }
                }
                SafeCheck(ref bad, Vpk.ReadAddonInfoFromArchive(zp, "inner.vpk").Title == "压缩包数据段标题",
                    "addoninfo：zip 里数据段的标题也能读（压缩包条目靠这条补）");

                // 10) 搜索别名：标题/作者只在 addoninfo 里时，按那个词也得搜得到
                ModGroup fg = new ModGroup();
                fg.Key = "test|search";
                fg.Name = "科技之光SD 替换M16";
                ModItem fp = new ModItem();
                fp.Key = "test|search|file";
                fp.Name = "科技之光SD 替换M16.vpk";
                fp.AddonTitle = "M4A1Tech科技之光";
                fp.AddonAuthor = "安妮某栓栓";
                fg.Parts.Add(fp);
                SafeCheck(ref bad, MainForm.MatchGroupCore(fg, "M4A1"), "搜索：只在包内标题里的词也能搜到");
                SafeCheck(ref bad, MainForm.MatchGroupCore(fg, "安妮"), "搜索：作者名也能搜");
                SafeCheck(ref bad, MainForm.MatchGroupCore(fg, "科技之光"), "搜索：文件名照旧能搜");
                SafeCheck(ref bad, !MainForm.MatchGroupCore(fg, "完全不相干的词"), "搜索：不相干的词不会误命中");
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL  自检本身出错: " + ex.Message);
                bad++;
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
            Console.WriteLine("safeself failures = " + bad);
            return bad;
        }

        static void SafeCheck(ref int bad, bool ok, string what)
        {
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what);
            if (!ok) bad++;
        }

        /// <summary>
        /// 造一个最小可读的 vpk：目录表里只放一条 addoninfo.txt（info 为 null 时什么都不放）。
        /// preload=true 时内容紧跟在目录表条目后面（V 社工具的常规写法），false 时放进数据段 ——
        /// 库里两种包都有，读标题这两条路都得走通。
        /// </summary>
        private static byte[] BuildTinyVpk(string info, bool preload)
        {
            byte[] body = info == null ? new byte[0] : Encoding.UTF8.GetBytes(info);
            List<byte> tree = new List<byte>();
            if (info != null)
            {
                tree.AddRange(Encoding.UTF8.GetBytes("txt")); tree.Add(0);
                tree.AddRange(Encoding.UTF8.GetBytes(" ")); tree.Add(0);      // 根目录：V 社工具写的是一个空格，不是空串
                tree.AddRange(Encoding.UTF8.GetBytes("addoninfo")); tree.Add(0);
                byte[] e = new byte[18];
                BitConverter.GetBytes(0u).CopyTo(e, 0);                        // crc
                BitConverter.GetBytes((ushort)(preload ? body.Length : 0)).CopyTo(e, 4);
                BitConverter.GetBytes((ushort)0x7FFF).CopyTo(e, 6);            // 数据就在本文件
                BitConverter.GetBytes(0u).CopyTo(e, 8);                        // 偏移：数据段从 0 起
                BitConverter.GetBytes((uint)(preload ? 0 : body.Length)).CopyTo(e, 12);
                BitConverter.GetBytes((ushort)0xFFFF).CopyTo(e, 16);
                tree.AddRange(e);
                if (preload) tree.AddRange(body);
                tree.Add(0);                                                   // 文件名层结束
                tree.Add(0);                                                   // 路径层结束
            }
            tree.Add(0);                                                       // 扩展名层结束（整棵树到头）

            List<byte> all = new List<byte>();
            all.AddRange(BitConverter.GetBytes(0x55AA1234u));
            all.AddRange(BitConverter.GetBytes(1u));
            all.AddRange(BitConverter.GetBytes((uint)tree.Count));
            all.AddRange(tree);
            if (info != null && !preload) all.AddRange(body);
            return all.ToArray();
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

                // 「已装 MOD 开关」窗口：只改开关不能重排顺序（顺序 = 覆盖优先级），真拖过才重排
                string tmp2 = Path.Combine(Path.GetTempPath(), "l4d2-order-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                try
                {
                    string a2 = Path.Combine(Path.Combine(tmp2, "left4dead2"), "addons");
                    Directory.CreateDirectory(a2);
                    foreach (string n in new string[] { "c.vpk", "a.vpk", "b.vpk" })
                        File.WriteAllText(Path.Combine(a2, n), "x");
                    File.WriteAllText(AddonList.FileOf(tmp2),
                        "\"AddonList\"\n{\n\t\"b.vpk\"\t\t\"1\"\n\t\"c.vpk\"\t\t\"1\"\n\t\"a.vpk\"\t\t\"1\"\n"
                        + "\t\"workshop\\999.vpk\"\t\t\"1\"\n}\n", Encoding.Default);

                    List<string> got = AddonList.OrderOf(tmp2);
                    bool ord = got.Count == 4 && got[0] == "b.vpk" && got[1] == "c.vpk" && got[2] == "a.vpk";
                    if (!ord) { bad++; Console.WriteLine("[FAIL] 读不出 addonlist 的顺序: " + string.Join(",", got.ToArray())); }

                    Dictionary<string, bool> ch = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    ch["b.vpk"] = true; ch["c.vpk"] = false; ch["a.vpk"] = true;
                    AddonSwitchForm.WriteSwitches(tmp2, new List<string> { "b.vpk", "c.vpk", "a.vpk" }, ch, false);
                    List<string> after = AddonList.OrderOf(tmp2);
                    Dictionary<string, bool> st3 = AddonList.Read(tmp2);
                    bool keepOrder = after.Count >= 3 && after[0] == "b.vpk" && after[1] == "c.vpk" && after[2] == "a.vpk";
                    bool changed = st3.ContainsKey("c.vpk") && !st3["c.vpk"];
                    if (!keepOrder) { bad++; Console.WriteLine("[FAIL] 只改开关却把顺序改了: " + string.Join(",", after.ToArray())); }
                    if (!changed) { bad++; Console.WriteLine("[FAIL] 开关没写进 addonlist"); }

                    AddonSwitchForm.WriteSwitches(tmp2, new List<string> { "a.vpk", "c.vpk", "b.vpk" }, ch, true);
                    List<string> moved2 = AddonList.OrderOf(tmp2);
                    bool reordered = moved2.Count >= 3 && moved2[0] == "a.vpk" && moved2[1] == "c.vpk" && moved2[2] == "b.vpk";
                    if (!reordered) { bad++; Console.WriteLine("[FAIL] 拖过之后没按新顺序写: " + string.Join(",", moved2.ToArray())); }
                    Console.WriteLine("ADDON 开关窗口：读顺序=" + ord + "，只改开关不动顺序=" + keepOrder
                        + "，真拖过才重排=" + reordered);
                }
                finally { try { Directory.Delete(tmp2, true); } catch { } }

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

                // 彻底还原：删掉的 vpk 不能把 addonlist 里的登记留成「幽灵开关」
                // （留着的 0 会在下次装回同名 MOD 时把它默默关掉，用户以为装了却没生效）
                try
                {
                    Core.Cfg.Mode = "local";
                    Core.Cfg.GameRoot = tmp;
                    Core.LogDir = Path.Combine(tmp, "_装机记录");
                    Directory.CreateDirectory(Core.LogDir);

                    string ghost = "还原测试.vpk";
                    File.WriteAllText(Path.Combine(addons, ghost), "x");
                    Dictionary<string, bool> swOff = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    swOff[ghost] = false;                            // 先登记成「关闭」
                    AddonList.Write(tmp, swOff);
                    File.WriteAllText(Path.Combine(Core.LogDir, "最近一次.ini"), "Added=" + ghost + "\r\n", new UTF8Encoding(true));

                    FullRestore(tmp);

                    bool fileGone = !File.Exists(Path.Combine(addons, ghost));
                    bool regGone = !AddonList.Read(tmp).ContainsKey(ghost);
                    if (!fileGone) { bad++; Console.WriteLine("[FAIL] 彻底还原没删掉铺进去的 vpk: " + ghost); }
                    if (!regGone) { bad++; Console.WriteLine("[FAIL] 彻底还原后 addonlist 里还留着幽灵登记: " + ghost); }
                    Console.WriteLine("ADDON 彻底还原：文件已删=" + fileGone + "，addonlist 幽灵登记已清=" + regGone);
                }
                finally
                {
                    Core.Cfg.Mode = savedMode;
                    Core.Cfg.GameRoot = savedGame;
                    Core.LogDir = savedLog;
                }

                // 批量导入：*.7z 也要收 —— 识别、装机、冲突检查都支持 7z，只有「导入」原来只扫 rar/zip，
                // 对着一个装着 7z 的文件夹点导入会一个都不收，还不说为什么。
                string savedModRoot = Core.ModRoot;
                try
                {
                    string lib = Path.Combine(tmp, "lib");
                    string srcDir = Path.Combine(tmp, "待导入");
                    Directory.CreateDirectory(lib);
                    Directory.CreateDirectory(srcDir);
                    // 内容不重要：导入只负责搬运，7z 读不读得了是别处的事
                    File.WriteAllText(Path.Combine(srcDir, "ak47测试.7z"), "x");
                    File.WriteAllText(Path.Combine(srcDir, "ak47测试.zip"), "x");
                    Core.ModRoot = lib;
                    int imp, skp;
                    ImportVpks(srcDir, false, out imp, out skp);
                    string box = Path.Combine(Path.Combine(lib, "addons各类物品储藏库"), "AK47");
                    bool gotZip = File.Exists(Path.Combine(box, "ak47测试.zip"));
                    bool got7z = File.Exists(Path.Combine(box, "ak47测试.7z"));
                    if (!gotZip) { bad++; Console.WriteLine("[FAIL] 批量导入：zip 没被收进库"); }
                    if (!got7z) { bad++; Console.WriteLine("[FAIL] 批量导入：7z 没被收进库（导入只扫 rar/zip）"); }
                    Console.WriteLine("ADDON 批量导入：zip=" + gotZip + "，7z=" + got7z
                        + "（入库 " + imp + " 个，跳过 " + skp + " 个）");
                }
                finally
                {
                    Core.ModRoot = savedModRoot;
                    try { Core.ScanMods(); } catch { }   // 库根换回来了，把内存里的表重建回去
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
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace Installer
{
    // 压缩包里的一个 vpk 条目
    public class ArcEntry
    {
        public string Inner;
        public long Size;
        public string Crc;      // rar 的 CRC32；zip 及旧缓存里为空（判重时用得到）
    }

    // 列表里的一行：一个 mod，可能由多个 vpk 组成
    public class ModGroup
    {
        public string Key;
        public string Name;
        public string Cat = "杂项";     // 大类：地图/人物/枪械/药品/杂项/喷漆（人工改过的会覆盖它）
        public string AutoCat = "";     // 识别出来的原始大类：人工改了分类还能改回来
        public string Sub = "其他";     // 细分：AWP / 一代 · 佐伊Zoey / 启示录系列
        public List<ModItem> Parts = new List<ModItem>();
        public long Size;
        public string Subject = "";      // 改了什么（取主体部件的识别结果）
        public string SubjectLevel = "";
        public string Evidence = "";     // 归组依据：包内资源 / 名称兜底 / 单文件

        public bool IsSet { get { return Parts.Count > 1; } }

        public ModItem Main
        {
            get
            {
                foreach (ModItem it in Parts) if (it.Role == "主体") return it;
                return Parts.Count > 0 ? Parts[0] : null;
            }
        }

        public string Where { get { return Cat + " · " + Sub; } }
    }

    /// <summary>
    /// 把 U 盘上的东西整理成「一个 mod 一行」：
    /// 独立 vpk、压缩包里的 vpk，统一分类，再把同一个 mod 的多个 vpk 聚成一套。
    /// </summary>
    public static class Catalog
    {
        public const string CAT_MAP = "地图";
        public const string CAT_CHAR = "人物";
        public const string CAT_INFECTED = "特感";
        public const string CAT_GUN = "枪械";
        public const string CAT_MED = "药品";
        public const string CAT_MISC = "杂项";
        public const string CAT_SPRAY = "喷漆";

        // 大类的顺序只有一份：CatPrefs 里那份（方案树按它排）。
        // 内建六类永远在里面，用户新建的分类跟在后面，可以上下移。
        public static string[] Cats { get { return CatPrefs.CatsArray(); } }

        public static List<ModGroup> Groups = new List<ModGroup>();
        public static Dictionary<string, ModGroup> GroupByKey = new Dictionary<string, ModGroup>(StringComparer.OrdinalIgnoreCase);

        // ---------------------------------------------------------------- 压缩包

        static string _rarExe;
        public static string RarExe()
        {
            if (_rarExe != null) return _rarExe;
            List<string> cand = new List<string>();
            cand.Add(@"C://Program Files//WinRAR//Rar.exe");
            cand.Add(@"C://Program Files (x86)//WinRAR//Rar.exe");
            // 注册表里的 exe64 是 WinRAR.exe（图形版），拿它跑命令行会弹窗挂死 —— 只借它所在的目录
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WinRAR"))
                {
                    if (k != null)
                        foreach (string v in new string[] { "exe64", "exe32" })
                        {
                            object o = k.GetValue(v);
                            if (o == null) continue;
                            string d = Path.GetDirectoryName(o.ToString());
                            if (!string.IsNullOrEmpty(d)) cand.Add(Path.Combine(d, "Rar.exe"));
                        }
                }
            }
            catch { }
            try { if (Core.UsbRoot != null) cand.Add(Path.Combine(Core.UsbRoot, @"_装机工具\Rar.exe")); } catch { }

            foreach (string c in cand)
            {
                try
                {
                    if (string.IsNullOrEmpty(c)) continue;
                    if (!string.Equals(Path.GetFileName(c), "Rar.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (File.Exists(c)) { _rarExe = c; return c; }
                }
                catch { }
            }
            _rarExe = "";
            return "";
        }

        static string _tarExe;
        /// <summary>
        /// Windows 自带的 bsdtar（Win10 1803 起）。Rar.exe 不认 7z，只有它能读出 7z 的目录表，
        /// 用来列包里的 vpk。解压仍走 WinRAR（见 WinRarExe），因为 bsdtar 对 LZMA 支持不全。
        /// </summary>
        public static string TarExe()
        {
            if (_tarExe != null) return _tarExe;
            List<string> cand = new List<string>();
            try
            {
                string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (!string.IsNullOrEmpty(win)) cand.Add(Path.Combine(win, "System32\\tar.exe"));
            }
            catch { }
            cand.Add(@"C:\Windows\System32\tar.exe");
            foreach (string c in cand)
            {
                try { if (File.Exists(c)) { _tarExe = c; return c; } } catch { }
            }
            _tarExe = "";
            return "";
        }

        static string _winRarExe;
        /// <summary>图形版 WinRAR.exe：7z 只有它解得动（Rar.exe 只处理 rar）。</summary>
        public static string WinRarExe()
        {
            if (_winRarExe != null) return _winRarExe;
            string rar = RarExe();
            if (rar.Length > 0)
            {
                try
                {
                    string w = Path.Combine(Path.GetDirectoryName(rar), "WinRAR.exe");
                    if (File.Exists(w)) { _winRarExe = w; return w; }
                }
                catch { }
            }
            _winRarExe = "";
            return "";
        }

        public static bool IsArchive(string path)
        {
            string e = Path.GetExtension(path).ToLowerInvariant();
            return e == ".rar" || e == ".zip" || e == ".7z";
        }

        /// <summary>
        /// 从压缩包里只取出一个条目写到 dst：不整包解压，也不动盘上的包。
        /// 安装、识别、判重都走这一份实现 —— 以前 Work 里有一份，7z 补识别时不能各写各的。
        /// zip 走 .NET；rar 走 Rar.exe；7z 只有图形版 WinRAR.exe 认（Rar.exe 只处理 rar）。
        /// </summary>
        public static bool ExtractEntryTo(string arc, string inner, string dst, out string why)
        {
            why = null;
            if (string.IsNullOrEmpty(arc) || !File.Exists(arc)) { why = "压缩包不存在"; return false; }
            if (string.IsNullOrEmpty(inner)) { why = "包内路径为空"; return false; }
            if (inner.IndexOf('*') >= 0 || inner.IndexOf('?') >= 0) { why = "文件名带通配符，暂不支持"; return false; }

            if (Path.GetExtension(arc).ToLowerInvariant() == ".zip")
            {
                try
                {
                    using (System.IO.Compression.ZipArchive za = System.IO.Compression.ZipFile.OpenRead(arc))
                    {
                        System.IO.Compression.ZipArchiveEntry e = za.GetEntry(inner.Replace('\\', '/'));
                        if (e == null) { why = "包里找不到 " + inner; return false; }
                        // 先写到同目录的 .part，完整解出来才换名：解到一半失败不会在 addons 里留半截 vpk
                        string part = Core.TempPathFor(dst);
                        try
                        {
                            using (Stream src = e.Open())
                            using (FileStream fs = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
                                src.CopyTo(fs, 1 << 20);
                            Core.Swap(part, dst);
                        }
                        catch
                        {
                            try { if (File.Exists(part)) File.Delete(part); } catch { }
                            throw;
                        }
                    }
                    return true;
                }
                catch (Exception ex) { why = ex.Message; return false; }
            }

            // 7z 只有图形版 WinRAR.exe 认（Rar.exe 只处理 rar），其余都走 Rar.exe
            bool is7z = string.Equals(Path.GetExtension(arc), ".7z", StringComparison.OrdinalIgnoreCase);
            string rar = is7z ? WinRarExe() : RarExe();
            if (rar.Length == 0)
            {
                why = is7z ? "找不到 WinRAR（WinRAR.exe），7z 解不开" : "找不到 WinRAR（Rar.exe）";
                return false;
            }
            // 暂存目录建在目标同一个卷上：最后一步是纯改名，不是跨盘复制，失败不会留半截文件
            string dstDir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dstDir) && !Directory.Exists(dstDir)) Directory.CreateDirectory(dstDir);
            string stage = Path.Combine(string.IsNullOrEmpty(dstDir) ? Path.GetTempPath() : dstDir,
                                        "~l4d2-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(stage);
                ProcessStartInfo psi = new ProcessStartInfo(rar, "e -y -idq \"" + arc + "\" \"" + inner + "\" \"" + stage + "\\\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                using (Process p = Process.Start(psi))
                {
                    if (!p.WaitForExit(300000)) { try { p.Kill(); } catch { } why = "解压超时"; return false; }
                    if (p.ExitCode != 0) { why = "WinRAR 返回 " + p.ExitCode; return false; }
                }
                string got = Path.Combine(stage, Path.GetFileName(inner));
                if (!File.Exists(got)) { why = "没解出文件"; return false; }
                Core.Swap(got, dst);
                return true;
            }
            catch (Exception ex) { why = ex.Message; return false; }
            finally { try { Directory.Delete(stage, true); } catch { } }
        }

        /// <summary>列出压缩包里的 vpk（带原始大小）。zip 走 .NET，rar 走 WinRAR 命令行。</summary>
        public static List<ArcEntry> ListArchive(string arcPath, out string err)
        {
            err = null;
            List<ArcEntry> list = new List<ArcEntry>();
            string ext = Path.GetExtension(arcPath).ToLowerInvariant();
            if (ext == ".zip")
            {
                try
                {
                    using (System.IO.Compression.ZipArchive za = System.IO.Compression.ZipFile.OpenRead(arcPath))
                        foreach (System.IO.Compression.ZipArchiveEntry e in za.Entries)
                            if (e.FullName.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
                                list.Add(new ArcEntry { Inner = e.FullName.Replace('/', '\\'), Size = e.Length });
                }
                catch (Exception ex) { err = ex.Message; }
                return list;
            }
            if (ext == ".7z") return ListArchive7z(arcPath, out err);

            string rar = RarExe();
            if (rar.Length == 0) { err = "找不到 WinRAR（Rar.exe）"; return list; }
            string text;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(rar, "vt -idc \"" + arcPath + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                try { psi.StandardOutputEncoding = Encoding.GetEncoding(936); } catch { }   // WinRAR 输出是 GBK
                using (Process p = Process.Start(psi))
                {
                    // stderr 必须一起读掉：WinRAR 对某些包会刷一堆警告，缓冲满了两个进程一起卡死
                    string devNull = null;
                    System.Threading.Thread th = new System.Threading.Thread(delegate()
                    {
                        try { devNull = p.StandardError.ReadToEnd(); } catch { }
                    });
                    th.IsBackground = true;
                    th.Start();
                    text = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } }
                    th.Join(2000);
                }
            }
            catch (Exception ex) { err = ex.Message; return list; }

            // vt 的字段名跟 WinRAR 语言走：中文版「名称/大小」，英文版 Name/Size
            string curName = null;
            ArcEntry last = null;      // CRC32 在 大小 之后才出现，先记着刚加的那条
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r').Trim();
                string name = Field(line, "名称:", "Name:");
                // 每条记录一开头就把 last 断掉：不然紧跟在 vpk 后面的 jpg 会把它的 CRC 写进 vpk 那条
                if (name != null) { curName = name; last = null; continue; }
                string crc = Field(line, "CRC32:", "CRC32:");
                if (crc != null) { if (last != null) last.Crc = crc; continue; }
                string size = Field(line, "大小:", "Size:");
                if (size != null && curName != null)
                {
                    if (curName.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
                    {
                        long n = 0;
                        long.TryParse(size, out n);
                        last = new ArcEntry { Inner = curName.Replace('/', '\\'), Size = n };
                        list.Add(last);
                    }
                    curName = null;
                }
            }
            if (list.Count == 0 && err == null) err = "包里没有 vpk";
            return list;
        }

        /// <summary>
        /// 7z 的目录表：Rar.exe 不认这个格式，用系统自带的 tar.exe 读。
        /// 输出照系统代码页走（中文机是 GBK），必须按 936 解，否则中文名全变问号。
        /// </summary>
        static List<ArcEntry> ListArchive7z(string arcPath, out string err)
        {
            err = null;
            List<ArcEntry> list = new List<ArcEntry>();
            string tar = TarExe();
            if (tar.Length == 0) { err = "找不到 tar.exe（需要 Win10 1803 以上），7z 读不了"; return list; }
            string text;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(tar, "-tvf \"" + arcPath + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                try { psi.StandardOutputEncoding = Encoding.GetEncoding(936); } catch { }
                using (Process p = Process.Start(psi))
                {
                    // stderr 一起读掉，不然缓冲区满了两个进程互相等
                    string devNull = null;
                    System.Threading.Thread th = new System.Threading.Thread(delegate()
                    {
                        try { devNull = p.StandardError.ReadToEnd(); } catch { }
                    });
                    th.IsBackground = true;
                    th.Start();
                    text = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(60000)) { try { p.Kill(); } catch { } }
                    th.Join(2000);
                }
            }
            catch (Exception ex) { err = ex.Message; return list; }

            // 每行形如：-rw-rw-rw- 0 0 0 165109710 9月 17 20:47 名字.vpk
            // 前 5 段固定（权限/链接/属主/属组/大小），之后是日期（中文英文都是 3 段），再往后是文件名
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r').Trim();
                if (line.Length == 0) continue;
                string[] p = line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 8) continue;
                long size = 0;
                if (!long.TryParse(p[4], out size)) continue;
                string name = p.Length > 8 ? string.Join(" ", p, 8, p.Length - 8) : p[p.Length - 1];
                // 日期不吃 3 段时（ISO 那种）会多啃掉文件名的头一个字，退回最后一段再试
                if (!name.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
                {
                    string alt = p[p.Length - 1];
                    if (alt.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase)) name = alt;
                    else continue;
                }
                list.Add(new ArcEntry { Inner = name.Replace('/', '\\'), Size = size });
            }
            if (list.Count == 0 && err == null) err = "包里没有 vpk";
            return list;
        }

        private static string Field(string line, string cn, string en)
        {
            if (line.StartsWith(cn, StringComparison.Ordinal)) return line.Substring(cn.Length).Trim();
            if (line.StartsWith(en, StringComparison.OrdinalIgnoreCase)) return line.Substring(en.Length).Trim();
            return null;
        }

        /// <summary>压缩包清单缓存：容器大小+时间没变就不重复调 Rar.exe。</summary>
        static Dictionary<string, List<ArcEntry>> _arcCache;
        static bool _arcCacheDirty = false;       // 有新清单才落盘
        static string CachePath { get { return Path.Combine(Core.LogDir, "archive-cache-v1.tsv"); } }

        static void LoadArcCache()
        {
            if (_arcCache != null) return;
            _arcCache = new Dictionary<string, List<ArcEntry>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(CachePath)) return;
                foreach (string raw in File.ReadAllLines(CachePath, Encoding.UTF8))
                {
                    string line = raw.TrimEnd('\r');
                    if (line.Length == 0) continue;
                    string[] p = line.Split('\t');
                    if (p.Length < 4) continue;
                    string sig = p[1] + "|" + p[2];
                    List<ArcEntry> l;
                    if (!_arcCache.TryGetValue(sig, out l)) { l = new List<ArcEntry>(); _arcCache[sig] = l; }
                    long n = 0; long.TryParse(p[3], out n);
                    l.Add(new ArcEntry { Inner = p[0], Size = n });
                }
            }
            catch { }
        }

        static void SaveArcCache()
        {
            if (!_arcCacheDirty) return;           // 没变化不重写
            _arcCacheDirty = false;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<string, List<ArcEntry>> kv in _arcCache)
                {
                    string[] sig = kv.Key.Split('|');
                    foreach (ArcEntry e in kv.Value)
                        sb.Append(e.Inner).Append('\t').Append(sig[0]).Append('\t').Append(sig.Length > 1 ? sig[1] : "0").Append('\t').Append(e.Size).Append("\r\n");
                }
                // 先写临时文件再整体换名：写到一半被强退/断电不会留一个截断的缓存，
                // 也不会出现「旧的删了新的还没到」的空窗（原来中间还有一步 Delete）
                Core.WriteAllTextAtomic(CachePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
            Core.Log("INFO", "压缩包缓存落盘: " + sw.ElapsedMilliseconds + " ms，" + _arcCache.Count + " 包");
        }

        static List<ArcEntry> ListArchiveCached(string arcPath, out bool hit)
        {
            hit = false;
            LoadArcCache();
            string sig;
            try
            {
                FileInfo fi = new FileInfo(arcPath);
                sig = fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
            }
            catch { sig = "0|0"; }
            List<ArcEntry> l;
            if (_arcCache.TryGetValue(sig, out l)) { hit = true; return l; }
            string err;
            l = ListArchive(arcPath, out err);
            if (err != null) Core.Log("WARN", "读压缩包失败 " + Path.GetFileName(arcPath) + ": " + err);
            if (l.Count > 0 || err == null) { _arcCache[sig] = l; _arcCacheDirty = true; }
            return l;
        }

        // ---------------------------------------------------------------- 扫描

        // ---------- 压缩包内容分类缓存（rar 只能靠 Rar.exe 流式读前缀，很慢，必须缓存） ----------
        static Dictionary<string, string> _arcContent;                 // key = rel|inner|size|ticks -> "cat|sub"
        static readonly Dictionary<string, string> _arcSigCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // 7z 补上内容识别（v2.45）时同样不升版本：v1 里 7z 存的是「读不出」的空结论，
        // 在 LoadArcContent 里按 key 丢掉即可；rar 的结论仍然有效，不能跟着一起作废。
        static string ArcContentPath { get { return Path.Combine(Core.LogDir, "archive-content-v1.tsv"); } }

        static string ArcSig(string fullPath)
        {
            string v;
            if (_arcSigCache.TryGetValue(fullPath, out v)) return v;
            try
            {
                FileInfo fi = new FileInfo(fullPath);
                v = fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
            }
            catch { v = "0|0"; }
            _arcSigCache[fullPath] = v;
            return v;
        }

        static string ArcKey(string fullPath, string inner)
        {
            return RelOf(fullPath) + "|" + inner + "|" + ArcSig(fullPath);
        }

        static void LoadArcContent()
        {
            if (_arcContent != null) return;
            _arcContent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(ArcContentPath)) return;
                foreach (string raw in File.ReadAllLines(ArcContentPath, Encoding.UTF8))
                {
                    string[] p = raw.TrimEnd((char)13).Split((char)9);
                    if (p.Length < 4) continue;
                    // 同上：7z 以前一律记成「读不出」（值是 "|"），现在能读了，这些作废；读到过结论的留着
                    if (p[0].EndsWith(".7z", StringComparison.OrdinalIgnoreCase) && p[3] == "|") continue;
                    _arcContent[p[0] + "|" + p[1] + "|" + p[2]] = p[3];
                }
            }
            catch { }
        }

        public static void SaveArcContent()
        {
            try
            {
                if (_arcContent == null) return;
                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<string, string> kv in _arcContent)
                {
                    string[] k = kv.Key.Split('|');
                    if (k.Length < 4) continue;
                    int n = k.Length;
                    string ticks = k[n - 1];
                    string size = k[n - 2];
                    string rel = k[0];
                    string inner = string.Join("|", k, 1, n - 3);
                    sb.Append(rel).Append((char)9).Append(inner).Append((char)9).Append(size).Append('|').Append(ticks).Append((char)9).Append(kv.Value).Append(Environment.NewLine);
                }
                Core.WriteAllTextAtomic(ArcContentPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        static bool TryArcContent(string fullPath, string inner, out string cat, out string sub)
        {
            cat = ""; sub = "";
            LoadArcContent();
            string v;
            if (!_arcContent.TryGetValue(ArcKey(fullPath, inner), out v)) return false;
            int bar = v.IndexOf('|');
            if (bar <= 0) return false;
            cat = v.Substring(0, bar);
            sub = v.Substring(bar + 1);
            return cat.Length > 0;
        }

        /// <summary>把所有还没缓存的 rar / 7z 逐个探一遍（只读前几百 KB 的目录表）。返回新识别成功的数量。</summary>
        public static int ProbeArchives(Action<string> progress)
        {
            LoadArcContent();
            int done = 0;
            try
            {
                foreach (string dir in Directory.GetDirectories(Core.ModRoot))
                {
                    foreach (string arc in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        // rar / 7z 都要探；zip 在扫描时已经读过目录表，这里不重复解
                        if (!IsArchive(arc) || arc.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                        string rel = RelOf(arc);
                        string sig = ArcSig(arc);
                        bool hit;
                        List<ArcEntry> list = ListArchiveCached(arc, out hit);
                        foreach (ArcEntry e in list)
                        {
                            string key = rel + "|" + e.Inner + "|" + sig;
                            if (_arcContent.ContainsKey(key)) continue;
                            if (progress != null) progress("识别 " + Path.GetFileName(arc) + " → " + Path.GetFileName(e.Inner));
                            string c, s;
                            if (ModIdentify.ClassifyByContentArchive(arc, e.Inner, out c, out s))
                                _arcContent[key] = c + "|" + s;
                            else
                                _arcContent[key] = "|";
                            done++;
                        }
                    }
                }
            }
            catch { }
            SaveArcContent();
            return done;
        }

        public static void ScanMods()
        {
            Core.ModList.Clear();
            Core.ByRel.Clear();
            Core.DirCount.Clear();
            Groups.Clear();
            GroupByKey.Clear();
            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                List<string> vpkFiles = new List<string>();
                List<string> arcs = new List<string>();

                // 1) 主库：老结构 <库根>\游戏内容\求生之路2各项MOD\addons*（U 盘布局）
                if (Core.IsLocal)
                {
                    // 本地模式只认游戏根目录：主库、附加目录都不参与，避免把 U 盘上的东西混进来
                    Core.Log("INFO", "本地模式：只扫游戏目录 addons，不扫主库和附加目录");
                }
                else
                {
                    if (Directory.Exists(Core.ModRoot))
                    {
                        // 库根下的目录全都翻，不再只认 addons 前缀：
                        // 按自己的习惯命名（skeeto素材、新地图…）也能被认到。
                        // 唯一的例外是程序自己那一摊（扩展平台：安装包 / 日志 / 源码），
                        // 里面的 vpk 是平台 payload，不是库里的 MOD。
                        // 主库根目录直接平铺的文件也要认：用户选盘后经常直接指到一个装 vpk 的文件夹
                        foreach (string f in Directory.GetFiles(Core.ModRoot, "*.vpk", SearchOption.TopDirectoryOnly))
                            vpkFiles.Add(f);
                        foreach (string f in Directory.GetFiles(Core.ModRoot, "*", SearchOption.TopDirectoryOnly))
                            if (IsArchive(f)) arcs.Add(f);

                        int scanned = 0;
                        foreach (string dir in Directory.GetDirectories(Core.ModRoot))
                        {
                            if (IsSelfDir(dir)) continue;
                            CollectDir(dir, vpkFiles, arcs);
                            scanned++;
                        }
                        Core.Log("INFO", "扫库: 主库 " + Core.ModRoot + " 根目录文件 + " + scanned + " 个子目录（程序目录已跳过）");
                    }
                    else Core.Log("WARN", "MOD 主库不存在: " + Core.ModRoot + "（可在设置里换盘，或用「库文件夹管理…」添加目录）");

                    // 2) 自设的库文件夹：只在 U 盘模式生效，整棵树都扫
                    foreach (string d in Core.Cfg.ModDirs) CollectDir(d, vpkFiles, arcs);
                }

                // 附加库目录允许和主库重叠（只挡了完全相同的路径），同一个文件会被收两遍、
                // 列表里多出一条同名项。这里按完整路径去重，收两遍也只算一次。
                vpkFiles = DedupPaths(vpkFiles, "vpk");
                arcs = DedupPaths(arcs, "压缩包");

                foreach (string f in vpkFiles) AddVpk(f);

                int arcHit = 0, arcMiss = 0;
                foreach (string a in arcs)
                {
                    bool hit;
                    List<ArcEntry> list = ListArchiveCached(a, out hit);
                    if (hit) arcHit++; else arcMiss++;
                    string rel = RelOf(a);
                    foreach (ArcEntry e in list)
                    {
                        ModItem it = new ModItem();
                        it.Name = Path.GetFileName(e.Inner);
                        it.Container = rel;
                        it.Inner = e.Inner;
                        it.RelPath = rel + "\\" + e.Inner;
                        it.FullPath = a;
                        it.Size = e.Size;
                        it.Category = Path.GetFileName(Path.GetDirectoryName(a));   // 兼容旧字段
                        it.Key = "#arc#" + rel + "#" + e.Inner;
                        Classify(it);
                        // 压缩包里的 vpk 也要认一遍：以前只认散装的，人物/喷漆全被漏掉
                        ModIdentify.Result ir = ModIdentify.AnalyzeArchiveCached(it.RelPath, a, e.Inner);
                        it.Subject = ir.Subject;
                        it.SubjectLevel = ir.Level;
                        it.SubjectKey = ir.Key;
                        it.Kinds = ir.Kinds;
                        it.Only1P = ir.Only1P;
                        it.AnimOnly = ir.AnimOnly;
                        it.AssetPaths = ir.Assets;
                        it.AddonTitle = ir.Title;
                        it.AddonAuthor = ir.Author;
                        RefineBySubject(it);
                        Add(it);
                    }
                }

                // 3) 游戏目录里已装的 vpk：也认出来进列表（FromGame 标记，安装时不再复制）
                int gameN = ScanGameAddons(sw);

                Core.Log("OK", "扫库: 独立 vpk " + vpkFiles.Count + " 个, 压缩包 " + arcs.Count + " 个（缓存命中 " + arcHit + "/" + (arcHit + arcMiss) + "），游戏目录 " + gameN + " 个，共 " + Core.ModList.Count + " 个 vpk，" + sw.ElapsedMilliseconds + " ms");
            }
            catch (Exception ex) { Core.Log("FAIL", "扫 MOD 出错: " + ex.Message); }
            ModIdentify.SaveCache();
            SaveArcCache();
        }

        /// <summary>按完整路径去重（不区分大小写）。附加库和主库重叠时，同一个文件只会留一条。</summary>
        static List<string> DedupPaths(List<string> list, string what)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> one = new List<string>(list.Count);
            foreach (string p in list)
            {
                string k;
                try { k = Path.GetFullPath(p); } catch { k = p; }
                if (seen.Add(k)) one.Add(p);
            }
            if (one.Count != list.Count)
                Core.Log("INFO", "扫库：" + what + " 去掉 " + (list.Count - one.Count) + " 个重复路径（附加库和主库重叠了）");
            return one;
        }

        /// <summary>是不是程序自己的目录（扩展平台：安装包、日志、源码都在这儿）。</summary>
        static bool IsSelfDir(string dir)
        {
            try
            {
                string a = Path.GetFullPath(dir).TrimEnd('\\');
                string b = Path.GetFullPath(Core.PkgDir).TrimEnd('\\');
                if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
                return a.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>把一个目录树里的 vpk / 压缩包都收进候选（主库和自设库目录共用）。</summary>
        static void CollectDir(string root, List<string> vpkFiles, List<string> arcs)
        {
            try
            {
                if (root == null || root.Trim().Length == 0 || !Directory.Exists(root))
                {
                    if (root != null && root.Trim().Length > 0) Core.Log("WARN", "库目录不存在，跳过: " + root);
                    return;
                }
                foreach (string f in Directory.GetFiles(root, "*.vpk", SearchOption.AllDirectories)) vpkFiles.Add(f);
                foreach (string f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                    if (IsArchive(f)) arcs.Add(f);
            }
            catch (Exception ex) { Core.Log("WARN", "扫不了目录 " + root + ": " + ex.Message); }
        }

        /// <summary>
        /// 游戏目录里已装的 vpk：addonlist.txt 里登记的 + 手动丢进 addons 根目录的。
        /// 官方文件（pak01_dir.vpk / dlc1_*.vpk 这些）按名字排除，绝不能当成 MOD 列出来。
        /// 标 FromGame：装的时候不用复制（文件本来就在），删除的时候也不许动。
        /// </summary>
        static int ScanGameAddons(Stopwatch sw)
        {
            string addons = Core.GameAddonsDir();
            if (addons == null) return 0;

            // addonlist 登记的（游戏认识、能开关的）
            // 顺手把每条的开关状态留下来：文件在 addons 里只说明「装过」，开没开还得看 addonlist 的 1/0
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, bool> addonState = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            try
            {
                addonState = AddonList.Read(Core.Cfg.GameRoot);
                foreach (KeyValuePair<string, bool> kv in addonState)
                    if (kv.Key.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(addons, kv.Key)))
                        names.Add(kv.Key);
            }
            catch { }

            // 手动丢进去、还没登记的：addons 根目录下的 vpk，排除官方文件名
            try
            {
                foreach (string f in Directory.GetFiles(addons, "*.vpk", SearchOption.TopDirectoryOnly))
                {
                    string n = Path.GetFileName(f);
                    if (IsOfficialAddonName(n)) continue;
                    names.Add(n);
                }
            }
            catch { }

            // U 盘模式：库里已经有同名同大小的一份 → 游戏目录这份就是「已装」，合并进库条目、不再重复列
            // 压缩包里的条目（zip/rar/7z）也算：它们的 Name 是包内文件名、Size 是包内原始大小，
            // 判定口径和散装 vpk 完全一样。以前把它们排除在外，导致从包里装的 MOD 在游戏里是一条
            // 「仅游戏内」、在库里还是一条「未装」，同一个 MOD 分裂成两条互相矛盾记录。
            Dictionary<string, List<ModItem>> libByName = null;
            if (!Core.IsLocal)
            {
                libByName = new Dictionary<string, List<ModItem>>(StringComparer.OrdinalIgnoreCase);
                foreach (ModItem m in Core.ModList)
                {
                    if (m.FromGame) continue;   // 只跳过游戏目录自己那份，别把自己跟自己配对
                    List<ModItem> l;
                    if (!libByName.TryGetValue(m.Name, out l)) { l = new List<ModItem>(); libByName[m.Name] = l; }
                    l.Add(m);
                }
            }
            int merged = 0;

            int n2 = 0;
            foreach (string key in names)
            {
                string f = Path.Combine(addons, key);
                if (!File.Exists(f)) continue;
                ModItem it = new ModItem();
                it.Name = key;
                it.FullPath = f;
                it.RelPath = "@游戏\\addons\\" + key;
                it.Key = "#game#" + key;
                it.Category = "已装";
                it.FromGame = true;
                bool on;
                it.AddonOn = !addonState.TryGetValue(key, out on) || on;   // 没登记 = 游戏默认启用
                try { it.Size = new FileInfo(f).Length; } catch { it.Size = 0; }
                if (libByName != null && it.Size > 0)
                {
                    List<ModItem> l;
                    if (libByName.TryGetValue(key, out l))
                    {
                        bool dup = false;
                        foreach (ModItem m in l)
                            if (m.Size == it.Size) { m.Installed = true; m.AddonOn = it.AddonOn; dup = true; }
                        if (dup) { merged++; continue; }   // 同一份，省掉识别、也不再重复显示
                    }
                }
                Classify(it);
                ModIdentify.Result ir = ModIdentify.AnalyzeCached(it.RelPath, f);
                it.Subject = ir.Subject;
                it.SubjectLevel = ir.Level;
                it.SubjectKey = ir.Key;
                it.Kinds = ir.Kinds;
                it.Only1P = ir.Only1P;
                it.AnimOnly = ir.AnimOnly;
                it.AssetPaths = ir.Assets;
                it.AddonTitle = ir.Title;
                it.AddonAuthor = ir.Author;
                RefineBySubject(it);
                Add(it);
                n2++;
            }
            if (merged > 0) Core.Log("INFO", "游戏目录: " + merged + " 个和库里的同名同大小，已合并到库条目（不再重复列）");
            if (n2 > 0) Core.Log("INFO", "游戏目录: addons 里认出 " + n2 + " 个已装 MOD（库外·已装，装机时不再复制）");
            return n2;
        }

        /// <summary>官方自带文件：pak01_dir.vpk / pak000_000.vpk / dlc1_xxx.vpk 这类，永远不当 MOD。</summary>
        static bool IsOfficialAddonName(string name)
        {
            string n = name.ToLowerInvariant();
            if (n.StartsWith("pak", StringComparison.Ordinal)) return true;      // pak01_dir.vpk / pak000_000.vpk
            if (n.StartsWith("dlc", StringComparison.Ordinal)) return true;      // dlc1_xxx.vpk
            if (n.StartsWith("update_", StringComparison.Ordinal)) return true;
            return false;
        }

        static string RelOf(string full)
        {
            try
            {
                if (full.StartsWith(Core.ModRoot, StringComparison.OrdinalIgnoreCase))
                    return full.Substring(Core.ModRoot.Length).TrimStart('\\');
            }
            catch { }
            return full;    // 库外的（自设目录/游戏目录）：用完整路径当相对路径
        }

        static void AddVpk(string f)
        {
            ModItem it = new ModItem();
            it.Name = Path.GetFileName(f);
            it.FullPath = f;
            it.RelPath = RelOf(f);
            it.Key = it.RelPath;
            it.Category = Core.RelDir(it.RelPath).Split('\\')[0];
            if (it.Category.Length == 0) it.Category = "未分类";
            try { it.Size = new FileInfo(f).Length; } catch { it.Size = 0; }
            Classify(it);
            ModIdentify.Result ir = ModIdentify.AnalyzeCached(it.RelPath, f);
            it.Subject = ir.Subject;
            it.SubjectLevel = ir.Level;
            it.SubjectKey = ir.Key;
            it.Kinds = ir.Kinds;
            it.Only1P = ir.Only1P;
            it.AnimOnly = ir.AnimOnly;
            it.AssetPaths = ir.Assets;
            it.AddonTitle = ir.Title;
            it.AddonAuthor = ir.Author;
            RefineBySubject(it);
            Add(it);
        }

        /// <summary>
        /// 识别完才知道改的是哪把枪 —— 扫描时 Classify 先跑、识别后跑，所以这里补一刀：
        /// 「改视角 / 火光 / 杂项」目录里如果指名道姓改的是一把具体武器（UZI 开火音效、
        /// AK 第一人称视角），就挂到那把枪下面，在方案树里当可叠加的扩展。
        /// 认不出具体型号的（手电、通用火光、血液）仍旧留在杂项。
        /// </summary>
        static void RefineBySubject(ModItem it)
        {
            if (it.Cat != CAT_MISC) return;
            string gun = SpecificWeapon(it);
            if (gun.Length == 0) return;
            it.Cat = CAT_GUN;
            it.Sub = SlotIndex.GunKindOf(gun, it.SubjectKey ?? "");
            it.Category = it.Cat;
        }

        static void Add(ModItem it)
        {
            Core.ModList.Add(it);
            Core.ByRel[it.Key] = it;
            string rd = Core.RelDir(it.RelPath);
            if (rd.Length > 0)
            {
                Core.BumpDir(rd);
                int bs = rd.IndexOf('\\');
                if (bs > 0) Core.BumpDir(rd.Substring(0, bs));
            }
        }

        // ---------------------------------------------------------------- 分类

        /// <summary>一个 mod 归到哪：先看它放在哪（确定性强），再看 vpk 里改了什么（兜底）。</summary>
        public static void Classify(ModItem it)
        {
            string[] segs0 = DirSegs(it);

            // 0) 放在「三方地图」目录下的一律算地图：路径比包里的东西可靠
            //    （有个「叛乱」地图包里带了人物模型，之前被判成人物，进不了「地图」那一堆）
            int iMap0 = IndexOfContain(segs0, "三方地图");
            if (iMap0 >= 0)
            {
                string[] cs0 = ContainerSegs(it);
                int cm0 = IndexOfContain(cs0, "三方地图");
                it.Cat = CAT_MAP;
                it.Sub = (cm0 >= 0 && cm0 + 1 < cs0.Length) ? cs0[cm0 + 1] : "单张地图";
                it.Category = it.Cat;
                return;
            }

            // 1) 内容优先：有把握才用（散装 vpk 直接读；zip 里的读目录表前缀）
            string ccat = "", csub = "";
            bool hitContent = false;
            try
            {
                if (string.IsNullOrEmpty(it.Inner))
                {
                    if (it.FullPath != null && it.FullPath.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
                        hitContent = ModIdentify.ClassifyByContent(it.FullPath, out ccat, out csub);
                }
                else if (it.FullPath != null && it.FullPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    hitContent = ModIdentify.ClassifyByContentZip(it.FullPath, it.Inner, out ccat, out csub);
                }
                else if (it.FullPath != null && IsArchive(it.FullPath)
                         && !it.FullPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    // rar / 7z 共用一份内容分类缓存（由 ProbeArchives 填好）。
                    // 扫描阶段只读缓存，绝不在这里解包 —— 一个 7z 里十几条 vpk，边扫边解会卡死启动。
                    hitContent = TryArcContent(it.FullPath, it.Inner, out ccat, out csub);
                }
            }
            catch { hitContent = false; }
            if (hitContent)
            {
                it.Cat = ccat;
                it.Sub = csub;
                it.Category = it.Cat;
                return;
            }

            string[] segs = DirSegs(it);

            // 地图只看「包放在哪个系列目录下」，不看包里面套的目录名，
            // 否则每个地图包都会变成自己的一个系列（Dry County、五号城市…），下拉里能列出一百项。
            int iMap = IndexOfContain(segs, "三方地图");
            if (iMap >= 0)
            {
                string[] cs = ContainerSegs(it);
                int cm = IndexOfContain(cs, "三方地图");
                it.Cat = CAT_MAP;
                it.Sub = (cm >= 0 && cm + 1 < cs.Length) ? cs[cm + 1] : "单张地图";
                it.Category = it.Cat;
                return;
            }

            int iChar = IndexOfContain(segs, "人物");
            if (iChar >= 0)
            {
                it.Cat = CAT_CHAR;
                string gen = "";
                int g = -1;
                for (int i = 0; i < segs.Length; i++)
                {
                    if (segs[i].IndexOf("一代", StringComparison.Ordinal) >= 0) { gen = "一代"; g = i; break; }
                    if (segs[i].IndexOf("二代", StringComparison.Ordinal) >= 0) { gen = "二代"; g = i; break; }
                }
                if (gen.Length == 0) it.Sub = "其他";
                else if (g + 1 < segs.Length) it.Sub = gen + " · " + segs[g + 1];
                else it.Sub = gen;
                it.Category = it.Cat;
                return;
            }

            if (IndexOfContain(segs, "喷漆") >= 0)
            {
                it.Cat = CAT_SPRAY; it.Sub = "喷漆"; it.Category = it.Cat;
                return;
            }

            int iMed = IndexOfContain(segs, "医疗物品");
            if (iMed >= 0)
            {
                it.Cat = CAT_MED;
                string s = (iMed + 1 < segs.Length) ? segs[iMed + 1] : "";
                if (s == "包") it.Sub = "医疗包";
                else if (s == "药") it.Sub = "止痛药";
                else if (s == "针") it.Sub = "肾上腺素";
                else it.Sub = "其他";
                it.Category = it.Cat;
                return;
            }

            int iInf = IndexOfContain(segs, "特感");
            if (iInf >= 0)
            {
                it.Cat = CAT_INFECTED;
                it.Sub = (iInf + 1 < segs.Length) ? segs[iInf + 1] : "特感";
                it.Category = it.Cat;
                return;
            }

            int iStore = IndexOfContain(segs, "物品储藏库");
            if (iStore >= 0)
            {
                string s = (iStore + 1 < segs.Length) ? segs[iStore + 1] : "";
                if (s.Length == 0) { GuessBySubject(it); return; }
                if (s == "改视角") { it.Cat = CAT_MISC; it.Sub = "改视角"; }
                else if (s.IndexOf("火光", StringComparison.Ordinal) >= 0) { it.Cat = CAT_MISC; it.Sub = "火光"; }
                else if (s.IndexOf("杂项", StringComparison.Ordinal) >= 0) { it.Cat = CAT_MISC; it.Sub = "特效杂项"; }
                else if (s.IndexOf("特感", StringComparison.Ordinal) >= 0) { it.Cat = CAT_INFECTED; it.Sub = "特感"; }
                else { it.Cat = CAT_GUN; it.Sub = s; }
                it.Category = it.Cat;
                return;
            }

            GuessBySubject(it);
        }

        /// <summary>
        /// 识别结果是不是「指名道姓的一把枪」。泛称（手枪 / 步枪 / 冲锋枪）和「4 件」这种都不算 ——
        /// 那种挂到具体武器下面反而错位，留在杂项更老实。
        /// </summary>
        private static string SpecificWeapon(ModItem it)
        {
            string s = (it.Subject ?? "").Trim();
            if (s.Length == 0 || s.IndexOf('/') >= 0) return "";
            if (SlotIndex.Vague(s)) return "";
            string kind = SlotIndex.GunKindOf(s, it.SubjectKey ?? "");
            if (kind.Length == 0 || kind == "其他枪械") return "";
            return s;
        }

        // 路径里没有可用信息时，退回看识别结果
        private static void GuessBySubject(ModItem it)
        {
            string s = (it.Subject ?? "") + " " + (it.SubjectKey ?? "");
            it.Cat = CAT_MISC; it.Sub = "其他";
            if (ContainsAny(s, "医疗包", "medkit", "止痛药", "pill", "肾上腺素", "adrenaline", "defib"))
            { it.Cat = CAT_MED; it.Sub = "医疗包"; }
            else if (ContainsAny(s, "Tank", "Hunter", "Smoker", "Boomer", "Spitter", "Jockey", "Charger", "Witch", "特感"))
            { it.Cat = CAT_INFECTED; it.Sub = "特感"; }
            else if (ContainsAny(s, "武士刀", "katana", "斧", "锤", "砍刀", "吉他", "球棒", "machete", "近战"))
            { it.Cat = CAT_GUN; it.Sub = "近战"; }
            else if (ContainsAny(s, "步枪", "冲锋枪", "霰弹枪", "狙击", "手枪", "机枪", "rifle", "smg", "shotgun", "sniper", "pistol", "枪"))
            { it.Cat = CAT_GUN; it.Sub = "其他枪"; }
            it.Category = it.Cat;
        }

        private static bool ContainsAny(string s, params string[] keys)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (string k in keys) if (s.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static int IndexOfContain(string[] segs, string key)
        {
            for (int i = 0; i < segs.Length; i++) if (segs[i].IndexOf(key, StringComparison.Ordinal) >= 0) return i;
            return -1;
        }

        /// <summary>只看容器（U 盘上那个文件）所在目录，不含压缩包内部的目录。</summary>
        static string[] ContainerSegs(ModItem it)
        {
            List<string> segs = new List<string>();
            string p = it.IsArchive ? it.Container : it.RelPath;
            string[] a = p.Split('\\');
            for (int i = 0; i < a.Length - 1; i++) if (a[i].Length > 0) segs.Add(a[i]);
            return segs.ToArray();
        }

        /// <summary>这个 mod 落在哪些目录里（独立 vpk 看自己，压缩包看 容器目录 + 包内目录）。</summary>
        static string[] DirSegs(ModItem it)
        {
            List<string> segs = new List<string>();
            string p = it.IsArchive ? it.Container : it.RelPath;
            string[] a = p.Split('\\');
            for (int i = 0; i < a.Length - 1; i++) if (a[i].Length > 0) segs.Add(a[i]);
            if (it.IsArchive)
            {
                string[] b = it.Inner.Split('\\');
                for (int i = 0; i < b.Length - 1; i++) if (b[i].Length > 0) segs.Add(b[i]);
            }
            return segs.ToArray();
        }

        // ---------------------------------------------------------------- 套装

        /// <summary>把 vpk 聚成「一个 mod 一行」。</summary>
        public static System.Diagnostics.Stopwatch BuildGroups()
        {
            System.Diagnostics.Stopwatch swg = System.Diagnostics.Stopwatch.StartNew();
            contentFacts.Clear();
            Groups.Clear();
            GroupByKey.Clear();
            Dictionary<string, List<ModItem>> buckets = new Dictionary<string, List<ModItem>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (ModItem it in Core.ModList)
            {
                string key, name;
                BucketOf(it, out key, out name);
                if (!buckets.ContainsKey(key)) { buckets[key] = new List<ModItem>(); names[key] = name; }
                buckets[key].Add(it);
            }
            long tBuckets = swg.ElapsedMilliseconds;
            Core.Log("INFO", "归组开始: " + Core.ModList.Count + " 个 vpk，" + buckets.Count + " 桶");
            foreach (KeyValuePair<string, List<ModItem>> kv in buckets)
            {
                System.Diagnostics.Stopwatch bsw = System.Diagnostics.Stopwatch.StartNew();
                foreach (List<ModItem> c in Cluster(kv.Value, names[kv.Key]))
                    MakeGroup(c);
                // 单桶超过 300ms 要点名：两两比对是 O(n²)，一个坏桶能拖住整次启动
                if (bsw.ElapsedMilliseconds > 300)
                    Core.Log("INFO", "归组慢桶: 「" + kv.Key + "」" + kv.Value.Count + " 项，" + bsw.ElapsedMilliseconds + " ms");
            }

            long tCluster = swg.ElapsedMilliseconds;
            ApplyCatsCore();                       // 人工归过类的，先按人工的算
            SortGroups();
            Core.Log("INFO", "归组分相: 分桶 " + tBuckets + " ms, 聚组 " + (tCluster - tBuckets) + " ms, 收尾 " + (swg.ElapsedMilliseconds - tCluster) + " ms");
            return swg;
        }

        /// <summary>
        /// 人工分类落到数据上：改过分类的 MOD 直接换 Cat。
        /// 方案树、搜索都读 Cat，所以一处改完全局跟着走 —— 不再另有一套「标签」。
        /// </summary>
        public static void ApplyCats()
        {
            if (ApplyCatsCore()) SortGroups();
        }

        private static bool ApplyCatsCore()
        {
            bool any = false;
            foreach (ModGroup g in Groups)
            {
                ModItem m = g.Main;
                if (m == null) continue;
                string auto = g.AutoCat.Length > 0 ? g.AutoCat : g.Cat;   // 基准永远是识别结果，不是上次改过的值
                string c = CatPrefs.CatOf(m, auto);
                if (c.Length == 0 || c == g.Cat) { CatPrefs.EnsureCat(g.Cat); continue; }
                CatPrefs.EnsureCat(c);
                g.Cat = c;
                foreach (ModItem p in g.Parts) p.Cat = c;
                any = true;
            }
            return any;
        }

        /// <summary>
        /// 大类改名：原来归在这一类的 MOD 一起搬过去。
        /// 不搬的话，自动分类的那批（没人指定过的）Cat 还是旧名，旧名又已从分类表里删掉 —— 它们会掉出树。
        /// </summary>
        public static void AdoptCatRename(string oldCat, string newCat)
        {
            if (string.IsNullOrEmpty(oldCat) || string.IsNullOrEmpty(newCat) || oldCat == newCat) return;
            bool any = false;
            foreach (ModGroup g in Groups)
            {
                if (g.Cat != oldCat) continue;
                foreach (ModItem p in g.Parts) CatPrefs.SetCat(p.Key, newCat);
                any = true;
            }
            CatPrefs.Save();
            if (any) ApplyCats();
        }

        public static void SortGroups()
        {
            Groups.Sort(delegate(ModGroup a, ModGroup b)
            {
                int x = Array.IndexOf(Cats, a.Cat), y = Array.IndexOf(Cats, b.Cat);
                if (x < 0) x = 99; if (y < 0) y = 99;
                if (x != y) return x.CompareTo(y);
                int r = string.Compare(a.Sub, b.Sub, StringComparison.CurrentCulture);
                if (r != 0) return r;
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
            });
        }

        static void BucketOf(ModItem it, out string key, out string name)
        {
            if (!it.IsArchive)
            {
                string dir = Core.RelDir(it.RelPath);
                key = dir;
                name = LastSeg(dir);
                return;
            }
            int i = it.Inner.LastIndexOf('\\');
            string innerDir = i > 0 ? it.Inner.Substring(0, i) : "";
            key = it.Container + "\0" + innerDir;
            name = innerDir.Length > 0 ? LastSeg(innerDir) : Path.GetFileNameWithoutExtension(it.Container);
        }

        static string LastSeg(string p)
        {
            int i = p.LastIndexOf('\\');
            return i >= 0 ? p.Substring(i + 1) : p;
        }

        /// <summary>
        /// 同一个目录/压缩包内目录里怎么切：先看包内资源关系，名字只做兜底。
        /// 官方模型路径本身不算同套证据（所有同枪皮肤都会写它）；共享私有资源或材质命名空间才算。
        /// </summary>
        static List<List<ModItem>> Cluster(List<ModItem> items, string bucketName)
        {
            List<List<ModItem>> res = new List<List<ModItem>>();
            if (items.Count == 0) return res;
            if (items.Count == 1) { res.Add(items); return res; }

            int n = items.Count;
            int[] p = new int[n];
            bool[] contentLinked = new bool[n];
            for (int i = 0; i < n; i++) p[i] = i;

            // 第一遍：只有整组资源路径完全相同的包，才直接按内容判为互斥部件。
            // 共享一部分附件/官方模型不算：不同皮肤常会携带同一套附件资源。
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                    if (ContentRelation(items[i], items[j]) == 3)
                    {
                        Union(p, i, j);
                        contentLinked[i] = contentLinked[j] = true;
                    }

            // 具体文件夹名是组织证据：例如一个 MOD 自己占一个文件夹，里面放主体和可选模块。
            // 大类目录（15发连狙、MAC-10、药）不会满足「文件夹名像某个部件名」。
            string bn = Norm(bucketName);
            if (bn.Length >= 6)
            {
                bool namedFolder = false;
                foreach (ModItem it in items)
                    if (Similar(bn, Norm(Path.GetFileNameWithoutExtension(it.Name)))) { namedFolder = true; break; }
                if (namedFolder && FolderCompatible(items))
                    for (int i = 1; i < n; i++) Union(p, 0, i);
            }

            // 第二遍：名称规则仅作兼容兜底。名字已经强匹配时，个别部件识别成相邻型号不阻止归组。
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    string a = Norm(Path.GetFileNameWithoutExtension(items[i].Name));
                    string b = Norm(Path.GetFileNameWithoutExtension(items[j].Name));
                    if (Similar(a, b)) Union(p, i, j);
                }
            Dictionary<int, List<ModItem>> map = new Dictionary<int, List<ModItem>>();
            for (int i = 0; i < n; i++)
            {
                int r = Find(p, i);
                if (!map.ContainsKey(r)) map[r] = new List<ModItem>();
                map[r].Add(items[i]);
            }
            foreach (KeyValuePair<int, List<ModItem>> kv in map) res.Add(kv.Value);
            return res;
        }

        /// <summary>
        /// 归组要反复比较资源路径。大地图包可能带几千条路径，不能在两两比较里反复 Split；
        /// 每个 vpk 只解析一次，后面所有关系判断复用这份事实。
        /// </summary>
        sealed class ContentFacts
        {
            public readonly HashSet<string> Full = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> Private = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> Scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public bool HasModel;
        }

        static readonly Dictionary<ModItem, ContentFacts> contentFacts = new Dictionary<ModItem, ContentFacts>();

        static ContentFacts FactsOf(ModItem it)
        {
            if (it == null) return new ContentFacts();
            ContentFacts f;
            if (contentFacts.TryGetValue(it, out f)) return f;
            f = new ContentFacts();
            string raw = it.AssetPaths ?? "";
            foreach (string p in raw.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                f.Full.Add(p);
                if (p.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)) f.HasModel = true;
                if (!ModIdentify.IsStockAsset(p)) f.Private.Add(p);
            }
            foreach (string p in f.Private)
            {
                if (!p.StartsWith("materials/", StringComparison.Ordinal)) continue;
                string[] seg = p.Substring(10).Split('/');
                if (seg.Length < 2) continue;
                int take = 1;
                if (seg[0] == "models") take = 2;
                if (seg.Length >= 3 && (seg[1] == "weapons" || seg[1] == "survivors" || seg[1] == "infected")) take = 3;
                take = Math.Min(take, seg.Length - 1);
                f.Scopes.Add(string.Join("/", seg, 0, take));
            }
            contentFacts[it] = f;
            return f;
        }

        static HashSet<string> AssetSet(ModItem it, bool privateOnly)
        {
            return privateOnly ? FactsOf(it).Private : FactsOf(it).Full;
        }

        static bool HasModel(ModItem it)
        {
            return FactsOf(it).HasModel;
        }

        static bool SameTarget(ModItem a, ModItem b)
        {
            if (a.Cat == CAT_MAP || b.Cat == CAT_MAP) return false;
            if (a.Cat != b.Cat && a.Cat != CAT_MISC && b.Cat != CAT_MISC) return false;
            string sa = (a.Subject ?? "").Trim(), sb = (b.Subject ?? "").Trim();
            if (sa.Length > 0 && sb.Length > 0 && sa != sb) return false;
            string ua = (a.Sub ?? "").Trim(), ub = (b.Sub ?? "").Trim();
            if (ua.Length > 0 && ub.Length > 0 && ua != "其他" && ub != "其他" && ua != ub
                && a.Cat != CAT_MISC && b.Cat != CAT_MISC) return false;
            return true;
        }

        /// <summary>具体文件夹整组归并前的目标检查；识别不出来的可选模块不拿目录别名卡掉。</summary>
        static bool FolderCompatible(List<ModItem> items)
        {
            for (int i = 0; i < items.Count; i++)
                for (int j = i + 1; j < items.Count; j++)
                {
                    ModItem a = items[i], b = items[j];
                    if (a.Cat == CAT_MAP || b.Cat == CAT_MAP) return false;
                    if (a.Cat != b.Cat && a.Cat != CAT_MISC && b.Cat != CAT_MISC) return false;
                    string sa = (a.Subject ?? "").Trim(), sb = (b.Subject ?? "").Trim();
                    if (sa.Length == 0 || sb.Length == 0) continue;   // 一边没认出内容时，Sub 多半只是文件夹名
                    if (sa != sb) return false;
                    string ua = (a.Sub ?? "").Trim(), ub = (b.Sub ?? "").Trim();
                    if (ua.Length > 0 && ub.Length > 0 && ua != "其他" && ub != "其他" && ua != ub) return false;
                }
            return true;
        }

        static HashSet<string> MaterialScopes(ModItem it)
        {
            return FactsOf(it).Scopes;
        }

        static bool SameSet(HashSet<string> a, HashSet<string> b)
        {
            if (a.Count == 0 || a.Count != b.Count) return false;
            foreach (string p in a) if (!b.Contains(p)) return false;
            return true;
        }

        static bool Overlaps(HashSet<string> a, HashSet<string> b)
        {
            foreach (string p in a) if (b.Contains(p)) return true;
            return false;
        }

        /// <summary>0=无内部证据；2=同一私有材质命名空间；3=整组资源路径完全相同。</summary>
        static int ContentRelation(ModItem a, ModItem b)
        {
            if (!SameTarget(a, b)) return 0;
            HashSet<string> pa = AssetSet(a, true), pb = AssetSet(b, true);
            if (pa.Count == 0 || pb.Count == 0) return 0;
            HashSet<string> aa = AssetSet(a, false), ab = AssetSet(b, false);
            if (SameSet(aa, ab)) return 3;
            return Overlaps(MaterialScopes(a), MaterialScopes(b)) ? 2 : 0;
        }

        static int Find(int[] p, int x) { while (p[x] != x) { p[x] = p[p[x]]; x = p[x]; } return x; }
        static void Union(int[] p, int a, int b) { a = Find(p, a); b = Find(p, b); if (a != b) p[b] = a; }

        /// <summary>两个名字是不是同一个 mod 的不同部件（编号、"partN"、"(2)"、颜色后缀都算）。</summary>
        static bool Similar(string a, string b)
        {
            if (a.Length == 0 || b.Length == 0) return false;
            if (a == b) return true;
            string s = a.Length <= b.Length ? a : b;
            string l = a.Length <= b.Length ? b : a;
            if (s.Length >= 6 && l.StartsWith(s, StringComparison.Ordinal)) return true;
            if (s.Length >= 8 && l.IndexOf(s, StringComparison.Ordinal) >= 0) return true;
            int lcp = 0;
            while (lcp < s.Length && s[lcp] == l[lcp]) lcp++;
            return lcp >= 8 && lcp * 10 >= s.Length * 6;
        }

        /// <summary>名字归一化：去掉各种编号，只留下「这是个什么东西」。</summary>
        public static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Trim();
            s = System.Text.RegularExpressions.Regex.Replace(s, @"^[\s\(\[]*\d+[\s\)\]]*[\.\-_]?\s*", "");      // 1 xxx / 01. xxx
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*[\(\（]\s*\d+\s*[\)\）]\s*$", "");           // xxx (2)
            s = System.Text.RegularExpressions.Regex.Replace(s, @"[\s_\-]*part\s*\d+$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=[\u4e00-\u9fa5])\d+$", "");                // 冷锋1
            s = System.Text.RegularExpressions.Regex.Replace(s, @"[\s_\-]+\d+$", "");                            // xxx -1
            s = System.Text.RegularExpressions.Regex.Replace(s, @"[\s\.\-_\u3000]+", "");
            return s.ToLowerInvariant();
        }

        static void MakeGroup(List<ModItem> parts)
        {
            ModGroup g = new ModGroup();
            g.Parts = parts;
            g.Key = parts[0].Key;
            g.Cat = parts[0].Cat;
            g.AutoCat = g.Cat;
            g.Sub = parts[0].Sub;

            foreach (ModItem it in parts)
            {
                it.GroupKey = g.Key;
                it.Role = RoleOf(it.Name);
                g.Size += it.Size;
            }
            ApplyContentRoles(parts);
            // 主体排最前，其次必装、选配、可选
            parts.Sort(delegate(ModItem a, ModItem b)
            {
                int x = RoleRank(a.Role), y = RoleRank(b.Role);
                if (x != y) return x.CompareTo(y);
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
            });

            // 分类和显示主体必须来自真正的主体，不能取决于文件枚举顺序；组 Key 保持原规则，兼容已存方案。
            ModItem groupMain = g.Main;
            if (groupMain != null)
            {
                g.Cat = groupMain.Cat;
                g.AutoCat = g.Cat;
                g.Sub = groupMain.Sub;
            }
            g.Evidence = GroupEvidence(parts);

            string baseName;
            if (parts.Count == 1) baseName = CleanName(parts[0].Name);
            else baseName = SetName(parts);
            g.Name = baseName.Length > 0 ? baseName : Path.GetFileNameWithoutExtension(parts[0].Name);

            ModItem main = g.Main;
            if (main != null) { g.Subject = main.Subject; g.SubjectLevel = main.SubjectLevel; }

            Groups.Add(g);
            GroupByKey[g.Key] = g;
        }

        /// <summary>文件名随便起也能认角色：唯一带模型的是主体；内容完全相同的非模型包是互斥选配。</summary>
        static void ApplyContentRoles(List<ModItem> parts)
        {
            if (parts.Count < 2) return;
            bool namedMain = false;
            List<ModItem> models = new List<ModItem>();
            foreach (ModItem it in parts)
            {
                if (it.Role == "主体") namedMain = true;
                if (HasModel(it)) models.Add(it);
            }
            if (!namedMain && models.Count == 1) models[0].Role = "主体";

            for (int i = 0; i < parts.Count; i++)
            {
                if (HasModel(parts[i]) || parts[i].Role != "必装") continue;
                HashSet<string> a = AssetSet(parts[i], false);
                for (int j = 0; j < parts.Count; j++)
                {
                    if (i == j || HasModel(parts[j])) continue;
                    if (SameSet(a, AssetSet(parts[j], false)) && ContentRelation(parts[i], parts[j]) == 3)
                    { parts[i].Role = "选配"; break; }
                }
            }
        }

        static string GroupEvidence(List<ModItem> parts)
        {
            if (parts.Count <= 1) return "单文件";
            bool exact = false, scoped = false;
            for (int i = 0; i < parts.Count; i++)
                for (int j = i + 1; j < parts.Count; j++)
                {
                    int r = ContentRelation(parts[i], parts[j]);
                    if (r == 3) exact = true;
                    else if (r == 2 && Similar(Norm(Path.GetFileNameWithoutExtension(parts[i].Name)),
                                               Norm(Path.GetFileNameWithoutExtension(parts[j].Name)))) scoped = true;
                }
            if (exact) return "包内资源关系";
            if (scoped) return "名称+资源命名空间";
            return "名称/目录兜底";
        }

        static int RoleRank(string role)
        {
            if (role == "主体") return 0;
            if (role == "必装") return 1;
            if (role == "选配") return 2;
            return 3;
        }

        /// <summary>
        /// 组合装的显示名。文件夹名经常是「含可替换部件」这类说明文字，
        /// 部件名里共用的那一段才是 mod 的真名（主体部件名）——用它。
        /// 部件名没有公共段（认不出是一套）才退回文件夹名。
        /// </summary>
        static string SetName(List<ModItem> parts)
        {
            string main = CleanName(parts[0].Name);
            if (CommonStem(parts).Length >= 4)
            {
                // 尾巴上的角色词（「模型主体」「主体」）是文件用途说明，不是名字
                foreach (string w in new string[] { "模型主体", "主文件", "模型", "主体" })
                    if (main.EndsWith(w, StringComparison.Ordinal) && main.Length > w.Length)
                    { main = main.Substring(0, main.Length - w.Length).Trim(); break; }
                if (main.Length > 0) return main;
            }
            string bn = Norm(LastSeg(Core.RelDir(parts[0].RelPath)));
            return (bn.Length > 0 && Similar(bn, Norm(Path.GetFileNameWithoutExtension(parts[0].Name))))
                ? CleanName(LastSeg(Core.RelDir(parts[0].RelPath)))
                : main;
        }

        /// <summary>一套部件的文件名共用前缀（归一化后），长说明这真是一套。</summary>
        static string CommonStem(List<ModItem> parts)
        {
            string stem = null;
            foreach (ModItem it in parts)
            {
                string n = Norm(Path.GetFileNameWithoutExtension(it.Name));
                if (n.Length == 0) return "";
                if (stem == null) { stem = n; continue; }
                int i = 0;
                while (i < stem.Length && i < n.Length && stem[i] == n[i]) i++;
                if (i < stem.Length) stem = stem.Substring(0, i);
                if (stem.Length == 0) return "";
            }
            return stem ?? "";
        }

        /// <summary>部件角色：主体 / 必装 / 选配（颜色这种互斥的）/ 可选。</summary>
        public static string RoleOf(string name)
        {
            string n = (name ?? "").ToLowerInvariant();
            if (ContainsAny(n, "主体", "主文件", "主件", "main", "core")) return "主体";
            if (ContainsAny(n, "颜色", "配色", "变体", "α", "花纹", "color")) return "选配";
            if (ContainsAny(n, "可选", "选装", "影藏", "隐藏", "配件", "附加", "模块", "扩展", "extra", "optional")) return "可选";
            return "必装";
        }

        /// <summary>显示用的干净名字：去扩展名、去开头编号、去结尾 (2)。</summary>
        public static string CleanName(string fileName)
        {
            string s = Path.GetFileNameWithoutExtension(fileName);
            // 开头的编号要「编号 + 分隔符」才洗掉。
            // 原来的写法遇到「(15连) 卡提加兰德」会把「(15」啃掉，剩个「连) 」；
            // 「30连 星野」这种编号本身带意思（30 发连狙），也不该动。
            s = System.Text.RegularExpressions.Regex.Replace(s, @"^[\s\(\[]*\d+[\s\)\]]*(?:[\.\-_]|\s)+", "");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*[\(\（]\s*\d+\s*[\)\）]\s*$", "");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*[-–—]+\s*$", "");   // 名字尾巴上的孤立短横
            return s.Trim();
        }

        /// <summary>勾这一行时默认装哪些部件：主体 + 必装，选配只取第一个，可选不装。</summary>
        public static List<ModItem> DefaultParts(ModGroup g)
        {
            List<ModItem> list = new List<ModItem>();
            bool picked = false;
            foreach (ModItem it in g.Parts)
            {
                if (it.Role == "可选") continue;
                if (it.Role == "选配")
                {
                    if (!picked) { list.Add(it); picked = true; }
                    continue;
                }
                list.Add(it);
            }
            if (list.Count == 0 && g.Parts.Count > 0) list.Add(g.Parts[0]);
            return list;
        }

        /// <summary>内容归组确定性自测：不读真实库、不写用户配置，文件名与内部证据分开验证。</summary>
        public static int SelfTestRecognition()
        {
            List<ModItem> oldItems = Core.ModList;
            List<ModGroup> oldGroups = Groups;
            Dictionary<string, ModGroup> oldByKey = GroupByKey;
            int bad = 0;
            try
            {
                Core.ModList = new List<ModItem>();
                Groups = new List<ModGroup>();
                GroupByKey = new Dictionary<string, ModGroup>(StringComparer.OrdinalIgnoreCase);

                // 完全无意义的外部文件名：只有整组资源路径完全相同的两个包，直接判为互斥颜色。
                Core.ModList.Add(TestItem("test\\colors\\a.vpk", "医疗包", "材质", "materials/tpp_wing_medkit/color/halo.vtf"));
                Core.ModList.Add(TestItem("test\\colors\\b.vpk", "医疗包", "材质", "materials/tpp_wing_medkit/color/halo.vtf"));

                // 具体文件夹 + 唯一模型 + 识别不出来的模块：按目录组织归成一套，唯一模型成为主体。
                Core.ModList.Add(TestItem("test\\爱丽丝主题皮肤\\爱丽丝主题皮肤.vpk", "医疗包", "模型", "models/v_models/v_medkit.mdl"));
                Core.ModList.Add(TestItem("test\\爱丽丝主题皮肤\\x7.vpk", "医疗包", "材质", "materials/tpp_wing_medkit/screen.vtf"));

                // 同枪不同皮肤：共享官方模型但私有材质不同，必须各自独立。
                Core.ModList.Add(TestItem("test\\skins\\a.vpk", "AK-47", "模型,材质",
                    "models/v_models/v_rifle_ak47.mdl;materials/models/weapons/skin_a/body.vtf"));
                Core.ModList.Add(TestItem("test\\skins\\b.vpk", "AK-47", "模型,材质",
                    "models/v_models/v_rifle_ak47.mdl;materials/models/weapons/skin_b/body.vtf"));

                // 只有官方模型交集、名字又无关系：证据不足，宁可不合并。
                Core.ModList.Add(TestItem("test\\ambiguous\\a.vpk", "AK-47", "模型", "models/v_models/v_rifle_ak47.mdl"));
                Core.ModList.Add(TestItem("test\\ambiguous\\b.vpk", "AK-47", "模型", "models/v_models/v_rifle_ak47.mdl"));

                // 共享一套附件资源但主体材质不同：不是同一套，宁可拆开。
                Core.ModList.Add(TestItem("test\\shared\\a.vpk", "AK-47", "模型,材质",
                    "models/v_models/v_rifle_ak47.mdl;materials/models/boki/shared.vtf;materials/models/skin_a/body.vtf"));
                Core.ModList.Add(TestItem("test\\shared\\b.vpk", "AK-47", "模型,材质",
                    "models/v_models/v_rifle_ak47.mdl;materials/models/boki/shared.vtf;materials/models/skin_b/body.vtf"));

                // 没有私有资源可读时，原有长公共名称仍能兼容旧包。
                Core.ModList.Add(TestItem("test\\names\\【魔女之旅魔女の旅々】伊蕾娜 替换 弗朗西斯.vpk", "弗朗西斯", "模型", "models/survivors/survivor_biker.mdl"));
                Core.ModList.Add(TestItem("test\\names\\【魔女之旅魔女の旅々】伊蕾娜 可选影藏帽子.vpk", "弗朗西斯", "模型", "models/survivors/survivor_biker.mdl"));

                BuildGroups();
                ModGroup colors = null, folderSet = null;
                int skinGroups = 0, ambiguousGroups = 0, sharedGroups = 0, nameGroups = 0;
                foreach (ModGroup g in Groups)
                {
                    string path = g.Parts[0].RelPath;
                    if (path.StartsWith("test\\colors\\", StringComparison.OrdinalIgnoreCase)) colors = g;
                    else if (path.StartsWith("test\\爱丽丝主题皮肤\\", StringComparison.OrdinalIgnoreCase)) folderSet = g;
                    else if (path.StartsWith("test\\skins\\", StringComparison.OrdinalIgnoreCase)) skinGroups++;
                    else if (path.StartsWith("test\\ambiguous\\", StringComparison.OrdinalIgnoreCase)) ambiguousGroups++;
                    else if (path.StartsWith("test\\shared\\", StringComparison.OrdinalIgnoreCase)) sharedGroups++;
                    else if (path.StartsWith("test\\names\\", StringComparison.OrdinalIgnoreCase)) nameGroups++;
                }
                bad += RecogCheck(colors != null && colors.Parts.Count == 2 && colors.Evidence == "包内资源关系", "任意文件名：整组资源相同才认出颜色部件");
                bad += RecogCheck(colors != null && colors.Parts[0].Role == "选配" && colors.Parts[1].Role == "选配", "相同资源路径的颜色包自动成为互斥选配");
                bad += RecogCheck(folderSet != null && folderSet.Parts.Count == 2 && folderSet.Main != null
                    && folderSet.Main.Name == "爱丽丝主题皮肤.vpk" && folderSet.Cat == "药品" && folderSet.Sub == "医疗包", "具体文件夹中的唯一模型成为主体并继承分类");
                bad += RecogCheck(skinGroups == 2, "同枪不同皮肤不因官方模型相同而误合并");
                bad += RecogCheck(ambiguousGroups == 2, "证据不足时不猜、不合并");
                bad += RecogCheck(sharedGroups == 2, "共享附件资源的不同皮肤不误合并");
                bad += RecogCheck(nameGroups == 1, "无资源证据时保留旧名称兼容兜底");

                bad += RecogCheck(ExtraOf("音效", false), "纯音效可叠加");
                bad += RecogCheck(ExtraOf("HUD", false), "纯 HUD 可叠加");
                bad += RecogCheck(ExtraOf("特效", false), "纯特效可叠加");
                bad += RecogCheck(ExtraOf("脚本", false), "纯脚本可叠加");
                bad += RecogCheck(ExtraOf("模型", true), "只改第一人称模型可叠加");
                bad += RecogCheck(!ExtraOf("模型,音效", false), "完整模型即使带音效仍是本体");
                bad += RecogCheck(!ExtraOf("材质", false), "材质替换不是可叠加扩展");
                bad += RecogCheck(!ExtraOf("地图", false), "地图不是扩展");
                bad += RecogCheck(!ExtraOf("", false), "读不出内容时不乱当扩展");
            }
            catch (Exception ex) { bad++; Core.Log("FAIL", "RECOGSELF 异常: " + ex.Message); }
            finally
            {
                Core.ModList = oldItems;
                Groups = oldGroups;
                GroupByKey = oldByKey;
            }
            Core.Log(bad == 0 ? "OK" : "FAIL", "RECOGSELF 完成，失败 " + bad + " 项");
            return bad;
        }

        static ModItem TestItem(string rel, string subject, string kinds, string assets)
        {
            ModItem it = new ModItem();
            it.RelPath = rel;
            it.Key = rel;
            it.Name = Path.GetFileName(rel);
            it.Subject = subject;
            it.Sub = subject;
            it.Kinds = kinds;
            it.AssetPaths = assets;
            if (subject == "医疗包") it.Cat = CAT_MED;
            else if (subject == "弗朗西斯") it.Cat = CAT_CHAR;
            else it.Cat = CAT_GUN;
            return it;
        }

        static int RecogCheck(bool ok, string name)
        {
            Core.Log(ok ? "OK" : "FAIL", "RECOGSELF " + name);
            return ok ? 0 : 1;
        }

        static bool ExtraOf(string kinds, bool only1P)
        {
            ModGroup g = new ModGroup();
            g.Parts.Add(new ModItem { Kinds = kinds, Only1P = only1P, Role = "必装" });
            return SlotIndex.IsExtra(g);
        }

        /// <summary>自测用：把每个 MOD 的识别结果铺成一张表，人眼核错用。</summary>
        public static string DumpIdentify()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("大类\t小分类\t名字\t识别结果\t键\t部件\t路径\t归组依据");
            foreach (ModGroup g in Groups)
            {
                ModItem m = g.Main;
                sb.Append(g.Cat).Append('\t')
                  .Append(g.Sub).Append('\t')
                  .Append(g.Name).Append('\t')
                  .Append(g.Subject).Append('\t')
                  .Append(m == null ? "" : (m.SubjectKey ?? "")).Append('\t')
                  .Append(g.Parts.Count).Append('\t')
                  .Append(m == null ? "" : m.RelPath).Append('\t')
                  .Append(g.Evidence).AppendLine();
            }
            string f = Path.Combine(Core.LogDir, "识别清单.tsv");
            Core.WriteAllTextAtomic(f, sb.ToString(), new UTF8Encoding(true));

            // 明细：每个 vpk 单独一行，看套装里有没有部件被认错
            StringBuilder d = new StringBuilder();
            d.AppendLine("所在MOD\t部件角色\t部件名\t大类\t小分类\t识别结果\t键\t路径");
            foreach (ModGroup g in Groups)
                foreach (ModItem it in g.Parts)
                    d.Append(g.Name).Append('\t')
                     .Append(it.Role).Append('\t')
                     .Append(it.Name).Append('\t')
                     .Append(it.Cat).Append('\t')
                     .Append(it.Sub).Append('\t')
                     .Append(it.Subject).Append('\t')
                     .Append(it.SubjectKey ?? "").Append('\t')
                     .Append(it.RelPath).AppendLine();
            Core.WriteAllTextAtomic(Path.Combine(Core.LogDir, "识别明细.tsv"), d.ToString(), new UTF8Encoding(true));
            return f;
        }
    }
}

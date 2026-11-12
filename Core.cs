using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Installer
{
    public class ModItem
    {
        public string Key;                // 唯一标识：独立 vpk = RelPath；压缩包内 = #arc#容器#包内路径
        public string Category;           // 大类（兼容旧字段，等于 Cat）
        public string Cat = "杂项";        // 地图/人物/枪械/药品/杂项/喷漆
        public string Sub = "其他";        // 细分：AWP / 一代 · 佐伊Zoey / 启示录系列
        public string Name;
        public long Size;
        public string RelPath;
        public string FullPath;           // 独立 vpk = 文件本身；压缩包内 = 压缩包
        public string Container;          // 压缩包相对路径（空 = 独立文件）
        public string Inner;              // 压缩包内路径
        public string GroupKey;           // 所属套装
        public string Role = "必装";       // 主体/必装/选配/可选
        public string Type;
        public string TypeDetail;
        public string Subject = "";       // 改了什么（从 vpk 内容反推，不是看文件夹名）
        public string SubjectLevel = "";  // 依据：模型 / HUD图标 / 音效 / 材质
        public string SubjectKey = "";    // 被替换的模型名，用于同枪冲突检测
        public string Kinds = "";         // 影响面：模型,音效,HUD...
        public bool Only1P;               // 模型全是第一人称 v_，世界模型 w_ 一个没动（能和模型叠加）
        public bool AnimOnly;             // 模型全是 anim_/gestures_ 表情骨架（KSEP 这类陪套，能和模型叠加）
        public bool FromGame;             // 这个 vpk 本来就在游戏 addons 里（库外·已装），装的时候不用复制
        public bool Installed;            // 游戏 addons 里有同名同大小的另一份（U 盘模式合并后不再重复列）
        public bool AddonOn = true;       // addonlist.txt 里这条是 "1"（开着）；未登记的按游戏默认算「开」
        public string AssetPaths = "";    // 包内模型/材质路径（分号分隔），只用于同套部件归组
        // 包内 addoninfo.txt 的原文：Title 是游戏 addons 菜单里显示的名字，和文件名常常不是一回事。
        // 只拿来当搜索别名和「游戏里叫什么」的参考，不参与命名/归类 —— 实测不少包的标题
        // 写的是被拆出去的公共材质包、粒子包甚至 debug demo 的名字。
        public string AddonTitle = "";
        public string AddonAuthor = "";

        public bool IsArchive { get { return Container != null && Container.Length > 0; } }
    }

    public class Config
    {
        public string GameRoot = "";
        public string LastRun = "";
        public bool L4N = true;
        public bool Skeeto = true;
        public bool AssetPack = true;
        public bool Mqcam = true;
        public bool Dxvk = true;
        public bool Launch = true;
        public string LaunchOptions = LaunchArgs.Default;   // 默认值只在 Launch.cs 里写一次
        public bool CfgFile = true;
        public bool Overwrite = false;      // false = 已装的跳过；true = 全部重装一遍
        public string TweakSet = "";        // 套用哪套参数存档（空 = 不套）
        public bool ReduceMotion = false;   // true = 关闭轻量过渡，直接显示最终状态
        public bool DpiAware = true;        // true = 自己按 DPI 缩放界面（高 DPI 下不再模糊）；false = 老样子交给系统拉伸
        public string Mode = "usb";         // 工作模式：usb = U 盘库是主（默认）；local = 游戏目录 addons 当库
        public string Appearance = "system";  // 外观：system = 跟随系统（默认）/ light / dark
        public string LibraryRoot = "";     // U 盘模式的主库根；空 = 跟随程序所在盘
        public bool DrawerOpen = true;      // 左栏（组件 / 安装选项）展开状态；首次进来看得到勾选项
        public string WorkshopDir = "";     // 工坊下载位置；空 = 跟随主库（U 盘库/_创意工坊 或 游戏 addons/_创意工坊）
        public List<string> ModDirs = new List<string>();   // 额外的 MOD 库文件夹（任意电脑上的任意目录）
        public List<string> Recent = new List<string>();
        public List<string> Mods = new List<string>();
    }

    public static class Core
    {
        public static string Here;
        // 诊断参数 --mode= 只是「这一次这么跑」，不能落进配置。
        // 记下原来的值，保存配置时照原样写回去（否则跑一次自检就把用户的工作模式改了）。
        public static string ModeLock = null;
        // 自检期间一律不落盘：自检会真的去点界面（切模式、勾选、拖动），
        // 跑完用户的配置就被改成测试时的样子了。SaveConfig 直接跳过最省事。
        public static bool SelfTesting = false;
        public static string UsbRoot;
        public static string ModRoot;
        public static string DefaultModRoot;
        public static string PkgDir;
        public static string CfgPath;
        public static string LogDir;
        public static string LogFile;
        public static Config Cfg = new Config();

        /// <summary>
        /// 当前正在做的事（"正在扫描 MOD 库…"这种）。
        /// 启动占位靠它显示真实步骤 —— 骨架卡那种"猜内容形状"的占位在启动阶段是假的：
        /// 那时候连最后会显示成方案树还是卡片都不知道。
        /// </summary>
        public static volatile string Phase = "";
        public static List<ModItem> ModList = new List<ModItem>();
        public static Dictionary<string, ModItem> ByRel = new Dictionary<string, ModItem>(StringComparer.OrdinalIgnoreCase);
        public static Dictionary<string, int> DirCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public static Action<string> Sink;

        // 复制完是否回读目标再算一遍 SHA256。默认关：只比大小，装 30 GB 能省掉一半 I/O。
        public static bool StrictVerify = false;
        // 上次实测的落地速度（MB/s），用于估算剩余时间
        public static double LastRateMBps = 20.0;

        [ThreadStatic] private static byte[] _copyBuf;
        private static bool _swapFallbackTold;   // 「本卷不支持 File.Replace」只提示一次
        private static DateTime _lastFlush;
        private static StreamWriter _logWriter;
        private static readonly object _logGate = new object();

        // 扫库互斥：ModList / ByRel / DirCount / Groups 只在扫描里整体清空重填，
        // 两条线程同时进来就会各清一半、拼成一份缺胳膊少腿的列表。
        private static readonly object _scanGate = new object();
        private static volatile bool _scanBusy;
        /// <summary>此刻是不是有扫描在跑（界面据此避免拿正在重建的表做判断）。</summary>
        public static bool ScanInProgress { get { return _scanBusy; } }

        public static void Init()
        {
            Here = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            PkgDir = ResolvePkgDir(Here);
            UsbRoot = WalkUp(Here);
            ModRoot = Path.Combine(Path.Combine(UsbRoot, "游戏内容"), "求生之路2各项MOD");
            DefaultModRoot = ModRoot;
            CfgPath = Path.Combine(PkgDir, "L4D2MOD管理工具.配置.ini");
            LogDir = Path.Combine(PkgDir, "_装机记录");
            if (!Directory.Exists(LogDir)) Directory.CreateDirectory(LogDir);
            LogFile = Path.Combine(LogDir, "log-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
            // 日志改成攒批落了，正常退出时也要把最后几行写下去（以前只有致命异常那条路会关日志）
            try { AppDomain.CurrentDomain.ProcessExit += delegate { CloseLog(); }; } catch { }
            PruneLogs();
            SweepTemps();
            MigrateLegacyConfig();
            LoadConfig();
            ApplyConfiguredLibraryRoot();
            // 得等配置读完才知道游戏目录在哪。装机/解包中断留下的 .part 和 ~l4d2-* 暂存目录
            // 以前只清 _装机记录 一处，addons 和库里的一直攒着，扫库时还会被当成怪文件。
            SweepGameTemps(Cfg.GameRoot);
            SweepGameTemps(ModRoot);
        }

        // v2.39 换了产品名，配置文件跟着改名。老文件还在就搬过去，用户原来的设置不丢；
        // 搬不动（被占用、只读）就退回默认值，至少程序还能起来。
        private static void MigrateLegacyConfig()
        {
            try
            {
                string legacy = Path.Combine(PkgDir, "装机台.配置.ini");
                if (File.Exists(CfgPath) || !File.Exists(legacy)) return;
                File.Move(legacy, CfgPath);
                Log("INFO", "配置已改名为 " + Path.GetFileName(CfgPath) + "（沿用原有设置）");
            }
            catch (Exception ex) { Log("WARN", "老配置改名失败，改用新文件重建默认值: " + ex.Message); }
        }

        // 包目录：exe 旁边就有包就用旁边；否则在 U 盘里找带「防闪退」或「Skeeto+MQCam」的目录
        public static string ResolvePkgDir(string start)
        {
            if (LooksLikePkg(start)) return start;
            string root = WalkUp(start);
            string guess = Path.Combine(Path.Combine(root, "游戏内容"), "求生之路2各项MOD\\扩展平台");
            if (Directory.Exists(guess) && LooksLikePkg(guess)) return guess;

            // 只有 U 盘/网络盘才值得三层穷举。exe 被拷到桌面时 root 会是 C:\，
            // 三层扫下去要摸几万个目录，启动直接卡死几十秒。
            try
            {
                DriveInfo di = new DriveInfo(Path.GetPathRoot(root));
                if (di.DriveType != DriveType.Removable && di.DriveType != DriveType.Network)
                {
                    Log("WARN", "exe 不在 U 盘包里，包目录按 exe 所在目录算: " + start);
                    return start;
                }
            }
            catch { }

            try
            {
                foreach (string d in Directory.GetDirectories(root))
                {
                    if (LooksLikePkg(d)) return d;
                    foreach (string d2 in Directory.GetDirectories(d))
                    {
                        if (LooksLikePkg(d2)) return d2;
                        foreach (string d3 in Directory.GetDirectories(d2))
                        {
                            if (LooksLikePkg(d3)) return d3;
                        }
                    }
                }
            }
            catch { }
            return start;
        }

        private static bool LooksLikePkg(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return false;
                if (Directory.Exists(Path.Combine(dir, "防闪退"))) return true;
                if (Directory.Exists(Path.Combine(dir, "Skeeto"))) return true;
                if (Directory.Exists(Path.Combine(dir, "MQCam"))) return true;
                return false;
            }
            catch { return false; }
        }

        private static string WalkUp(string start)
        {
            string p = start;
            while (p != null && p.Length > 3)
            {
                string leaf = Path.GetFileName(p);
                if (leaf == "游戏内容" || leaf == "工作区" || leaf == "编译用") return Path.GetDirectoryName(p);
                p = Path.GetDirectoryName(p);
            }
            return Path.GetPathRoot(start);
        }

        public static void Log(string level, string text)
        {
            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + level.PadRight(4) + " " + text;
            try
            {
                lock (_logGate)
                {
                    if (_logWriter == null)
                    {
                        _logWriter = new StreamWriter(new FileStream(LogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
                    }
                    _logWriter.WriteLine(line);
                    // U 盘上每行 flush 一次就是一次设备写，装机时会攒出几百次小 IO。
                    // 普通行 500ms 落一次；WARN/FAIL 立刻落 —— 真出事时最需要看的就是那几行。
                    if (level == "FAIL" || level == "WARN"
                        || (DateTime.UtcNow - _lastFlush).TotalMilliseconds > 500)
                    {
                        _logWriter.Flush();
                        _lastFlush = DateTime.UtcNow;
                    }
                }
            }
            catch { }
            if (Sink != null) { try { Sink(line); } catch { } }
        }

        public static void CloseLog()
        {
            try { lock (_logGate) { if (_logWriter != null) { _logWriter.Dispose(); _logWriter = null; } } } catch { }
        }

        // 日志只留最近 40 个，不然 _装机记录 会一直涨
        private static void PruneLogs()
        {
            try
            {
                string[] fs = Directory.GetFiles(LogDir, "log-*.txt");
                if (fs.Length <= 40) return;
                Array.Sort(fs, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < fs.Length - 40; i++) { try { File.Delete(fs[i]); } catch { } }
            }
            catch { }
        }

        /// <summary>
        /// 清掉被打断时留下的临时文件。程序被强关 / 拔 U 盘时，正在写的 *.part
        /// （识别缓存那种有好几 MB）会留在 _装机记录 里，越攒越多；一小时前的肯定没人再写了，删掉。
        /// </summary>
        private static void SweepTemps()
        {
            try
            {
                DateTime cutoff = DateTime.UtcNow.AddHours(-1);
                int n = 0;
                foreach (string f in Directory.GetFiles(LogDir, "*.part"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(f) > cutoff) continue;   // 可能是别的实例正在写
                        File.Delete(f);
                        n++;
                    }
                    catch { }
                }
                if (n > 0) Log("INFO", "清掉上次中断留下的临时文件 " + n + " 个（*.part）");
            }
            catch { }
        }

        /// <summary>
        /// 清掉装机 / 解包中断留在游戏目录和 MOD 库里的临时件：
        ///  · addons（或库根）下写了一半的 *.part —— 装机中途强退、拔盘就会留下
        ///  · 解包用的 ~l4d2-xxxxxxxx 暂存目录
        /// 只碰一小时以前的：可能还有别的实例正在写。
        /// </summary>
        internal static void SweepGameTemps(string root)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
            int files = 0, dirs = 0;
            try
            {
                string addons = Path.Combine(Path.Combine(root, "left4dead2"), "addons");
                string where = Directory.Exists(addons) ? addons : root;   // 库根就是一堆 vpk 的情况
                DateTime cutoff = DateTime.UtcNow.AddHours(-1);
                foreach (string f in Directory.GetFiles(where, "*.part"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(f) > cutoff) continue;
                        File.Delete(f); files++;
                    }
                    catch { }
                }
                foreach (string d in Directory.GetDirectories(where, "~l4d2-*"))
                {
                    try
                    {
                        if (Directory.GetLastWriteTimeUtc(d) > cutoff) continue;
                        Directory.Delete(d, true); dirs++;
                    }
                    catch { }
                }
            }
            catch { }
            if (files + dirs > 0)
                Log("INFO", "清掉中断留下的临时件 " + files + " 个文件 / " + dirs + " 个暂存目录（" + root + "）");
        }

        public static string Human(long b)
        {
            if (b >= 1073741824L) return (b / 1073741824.0).ToString("N2") + " GB";
            if (b >= 1048576L) return (b / 1048576.0).ToString("N1") + " MB";
            return (b / 1024.0).ToString("N0") + " KB";
        }

        // ---------------- 配置 ----------------
        public static void LoadConfig()
        {
            try
            {
                if (!File.Exists(CfgPath)) return;
                foreach (string raw in File.ReadAllLines(CfgPath, Encoding.UTF8))
                {
                    string s = raw.Trim();
                    if (s.Length == 0 || s.StartsWith("#") || s.StartsWith("[")) continue;
                    int eq = s.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = s.Substring(0, eq).Trim();
                    string v = s.Substring(eq + 1).Trim();
                    if (k == "GameRoot") Cfg.GameRoot = v;
                    else if (k == "LastRun") Cfg.LastRun = v;
                    else if (k == "L4N") Cfg.L4N = (v == "1");
                    else if (k == "Skeeto") Cfg.Skeeto = (v == "1");
                    else if (k == "AssetPack") Cfg.AssetPack = (v == "1");
                    else if (k == "Mqcam") Cfg.Mqcam = (v == "1");
                    else if (k == "Dxvk") Cfg.Dxvk = (v == "1");
                    else if (k == "Launch") Cfg.Launch = (v == "1");
                    else if (k == "LaunchOptions")
                    {
                        // 老配置里若还是 v1.7 的默认值，说明没人动过 —— 直接升到新默认，
                        // 否则换了新 exe 还是按旧参数写，等于没改。
                        if (v.Length == 0 || v == LaunchArgs.Legacy)
                        {
                            Cfg.LaunchOptions = LaunchArgs.Default;
                            if (v.Length > 0) Log("INFO", "启动项是旧默认值（" + v + "），已换成新版默认参数");
                        }
                        else Cfg.LaunchOptions = v;
                    }
                    else if (k == "CfgFile") Cfg.CfgFile = (v == "1");
                    else if (k == "Overwrite") Cfg.Overwrite = (v == "1");
                    else if (k == "TweakSet") Cfg.TweakSet = v;
                    else if (k == "ReduceMotion") Cfg.ReduceMotion = (v == "1");
                    else if (k == "DpiAware") Cfg.DpiAware = (v != "0");
                    else if (k == "Mode") Cfg.Mode = v;
                    else if (k == "Appearance") Cfg.Appearance = v;
                    else if (k == "LibraryRoot") Cfg.LibraryRoot = v;
                    else if (k == "Drawer") Cfg.DrawerOpen = (v != "0");
                    else if (k == "WorkshopDir") Cfg.WorkshopDir = v;
                    else if (k == "ModDir") Cfg.ModDirs.Add(v);
                    else if (k == "Recent") Cfg.Recent.Add(v);
                    else if (k.StartsWith("Mod:")) Cfg.Mods.Add(k.Substring(4));
                }
            }
            catch { }
        }

        public static void SaveConfig()
        {
            if (SelfTesting) return;      // 自检不留痕
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# L4D2 MOD 管理工具 配置（勾选记忆）。删掉这个文件 = 恢复默认。");
                sb.AppendLine("[general]");
                sb.AppendLine("GameRoot=" + Cfg.GameRoot);
                sb.AppendLine("LastRun=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("[platform]");
                sb.AppendLine("L4N=" + (Cfg.L4N ? "1" : "0"));
                sb.AppendLine("Skeeto=" + (Cfg.Skeeto ? "1" : "0"));
                sb.AppendLine("AssetPack=" + (Cfg.AssetPack ? "1" : "0"));
                sb.AppendLine("Mqcam=" + (Cfg.Mqcam ? "1" : "0"));
                sb.AppendLine("Dxvk=" + (Cfg.Dxvk ? "1" : "0"));
                sb.AppendLine("Launch=" + (Cfg.Launch ? "1" : "0"));
                sb.AppendLine("LaunchOptions=" + Cfg.LaunchOptions);
                sb.AppendLine("CfgFile=" + (Cfg.CfgFile ? "1" : "0"));
                sb.AppendLine("Overwrite=" + (Cfg.Overwrite ? "1" : "0"));
                sb.AppendLine("TweakSet=" + Cfg.TweakSet);
                sb.AppendLine("ReduceMotion=" + (Cfg.ReduceMotion ? "1" : "0"));
                sb.AppendLine("DpiAware=" + (Cfg.DpiAware ? "1" : "0"));
                sb.AppendLine("Mode=" + (ModeLock ?? Cfg.Mode));
                sb.AppendLine("Appearance=" + Cfg.Appearance);
                sb.AppendLine("LibraryRoot=" + Cfg.LibraryRoot);
                sb.AppendLine("Drawer=" + (Cfg.DrawerOpen ? "1" : "0"));
                sb.AppendLine("WorkshopDir=" + Cfg.WorkshopDir);
                foreach (string d in Cfg.ModDirs) sb.AppendLine("ModDir=" + d);
                foreach (string rp in Cfg.Recent) sb.AppendLine("Recent=" + rp);
                sb.AppendLine("[mods]");
                foreach (string m in Cfg.Mods) sb.AppendLine("Mod:" + m + "=1");
                WriteAllTextAtomic(CfgPath, sb.ToString(), new UTF8Encoding(true));
            }
            catch (Exception ex) { Log("WARN", "配置写不进去: " + ex.Message); }
        }

        /// <summary>启动时套用配置里的主库；盘符暂时不在就回退到程序所在盘，不把配置抹掉。</summary>
        private static void ApplyConfiguredLibraryRoot()
        {
            if (string.IsNullOrEmpty(Cfg.LibraryRoot)) return;
            string p = Cfg.LibraryRoot.Trim().TrimEnd('\\');
            if (Directory.Exists(p))
            {
                ModRoot = p;
                return;
            }
            Log("WARN", "指定的 MOD 主库当前不可用: " + p + "；本次先跟随程序使用 " + DefaultModRoot);
        }

        /// <summary>
        /// 切换 U 盘模式的主库。传程序所在盘的默认库时会清空覆盖项，
        /// 这样 U 盘换盘符后仍能自动跟随，而不是记死旧盘符。
        /// </summary>
        public static bool SetLibraryRoot(string path, bool save)
        {
            string p = string.IsNullOrEmpty(path) ? DefaultModRoot : path.Trim().TrimEnd('\\');
            if (!Directory.Exists(p)) return false;

            if (string.Equals(p, DefaultModRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                ModRoot = DefaultModRoot;
                Cfg.LibraryRoot = "";
            }
            else
            {
                ModRoot = p;
                Cfg.LibraryRoot = p;
            }
            if (save) SaveConfig();
            return true;
        }

        /// <summary>
        /// 工坊下载位置。传空 = 恢复「跟随主库」。
        /// 目录不存在就建：用户常直接填 U 盘上的新文件夹，不该因为"还没建"就被拒。
        /// </summary>
        public static bool SetWorkshopDir(string path, bool save)
        {
            string p = (path ?? "").Trim().TrimEnd('\\');
            if (p.Length == 0)
            {
                Cfg.WorkshopDir = "";
                if (save) SaveConfig();
                return true;
            }
            try
            {
                if (!Directory.Exists(p)) Directory.CreateDirectory(p);
            }
            catch (Exception ex)
            {
                Log("WARN", "工坊下载位置不可用: " + p + "（" + ex.Message + "）");
                return false;
            }
            Cfg.WorkshopDir = p;
            if (save) SaveConfig();
            return true;
        }

        // ---------------- 找游戏 ----------------
        // 本地模式：游戏目录 addons 直接当库用，删除放行、装机只翻开关
        public static bool IsLocal { get { return Cfg.Mode == "local"; } }


        public static bool TestGame(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                string p = path.Trim().Trim('"').TrimEnd('\\');
                return File.Exists(Path.Combine(p, "left4dead2.exe")) && Directory.Exists(Path.Combine(p, "left4dead2"));
            }
            catch { return false; }
        }

        /// <summary>游戏目录下的 addons 文件夹；游戏还没找到或目录不存在返回 null。</summary>
        public static string GameAddonsDir()
        {
            try
            {
                if (!TestGame(Cfg.GameRoot)) return null;
                string d = Path.Combine(Path.Combine(Cfg.GameRoot, "left4dead2"), "addons");
                return Directory.Exists(d) ? d : null;
            }
            catch { return null; }
        }

        public static List<string> FindGames()
        {
            Phase = "正在找游戏目录…";
            List<string> hits = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<string> add = delegate(string p)
            {
                if (string.IsNullOrEmpty(p)) return;
                string t = p.Trim().Trim('"').TrimEnd('\\');
                if (TestGame(t) && seen.Add(t)) hits.Add(t);
            };

            if (TestGame(Cfg.GameRoot)) add(Cfg.GameRoot);

            List<string> steamLibs = new List<string>();
            string[] regs = new string[] {
                "HKEY_CURRENT_USER\\Software\\Valve\\Steam",
                "HKEY_LOCAL_MACHINE\\SOFTWARE\\WOW6432Node\\Valve\\Steam",
                "HKEY_LOCAL_MACHINE\\SOFTWARE\\Valve\\Steam"
            };
            foreach (string r in regs)
            {
                foreach (string valName in new string[] { "SteamPath", "InstallPath" })
                {
                    try
                    {
                        object v = Registry.GetValue(r, valName, null);
                        if (v != null && v.ToString().Length > 0)
                        {
                            string s = v.ToString().Replace('/', '\\').TrimEnd('\\');
                            if (Directory.Exists(s) && !steamLibs.Contains(s)) steamLibs.Add(s);
                        }
                    }
                    catch { }
                }
            }
            foreach (string lib in steamLibs)
            {
                add(Path.Combine(lib, "steamapps\\common\\Left 4 Dead 2"));
                try
                {
                    string vdf = Path.Combine(lib, "steamapps\\libraryfolders.vdf");
                    if (File.Exists(vdf))
                    {
                        string txt = File.ReadAllText(vdf);
                        foreach (Match m in Regex.Matches(txt, "\"path\"\\s*\"([^\"]+)\""))
                        {
                            string d = m.Groups[1].Value.Replace("\\\\", "\\").TrimEnd('\\');
                            if (Directory.Exists(d)) add(Path.Combine(d, "steamapps\\common\\Left 4 Dead 2"));
                        }
                    }
                }
                catch { }
            }

            string[] bases = new string[] { "Steam", "SteamLibrary", "storygames", "Games", "" };
            foreach (DriveInfo di in DriveInfo.GetDrives())
            {
                try
                {
                    if (!di.IsReady) continue;
                    string root = di.RootDirectory.FullName;
                    foreach (string b in bases)
                    {
                        string p1 = (b.Length == 0) ? root : Path.Combine(root, b);
                        add(Path.Combine(p1, "steamapps\\common\\Left 4 Dead 2"));
                        add(Path.Combine(p1, "Left 4 Dead 2"));
                    }
                    // 网维：storygames\<编号>\steamapps\common\...
                    string sg = Path.Combine(root, "storygames");
                    if (Directory.Exists(sg))
                    {
                        foreach (string sub in Directory.GetDirectories(sg))
                        {
                            add(Path.Combine(sub, "steamapps\\common\\Left 4 Dead 2"));
                            foreach (string sub2 in Directory.GetDirectories(sub))
                                add(Path.Combine(sub2, "steamapps\\common\\Left 4 Dead 2"));
                        }
                    }
                }
                catch { }
            }
            return hits;
        }

        // ---------------- MOD 库 ----------------
        public static void BumpDir(string key)
        {
            int n;
            DirCount.TryGetValue(key, out n);
            DirCount[key] = n + 1;
        }

        public static void ScanMods()
        {
            Phase = "正在扫描 MOD 库…";
            try { ScanModsCore(); } finally { Phase = ""; }
        }

        private static void ScanModsCore()
        {
            // 同一时刻只允许一次扫描在跑。
            // 原来没有这道门：启动扫描还在后台跑，用户在界面上点删除 / 清理同名 / 改名，
            // UI 线程会再调一次，两条线程同时 Clear + 重填 ModList、ByRel、Groups ——
            // 列表可能只剩半截（按那次扫描的进度拼出来的），枚举时还会抛「集合已修改」。
            // 后到的那次在这里排队（最多等 60 s），拿到的永远是完整的一份。
            if (!System.Threading.Monitor.TryEnter(_scanGate, 60000))
            {
                Log("WARN", "已有一次扫描超过 60 s 没结束，这次跳过扫描（列表可能不是最新的）");
                return;
            }
            _scanBusy = true;
            try
            {
                try
                {
                    System.Diagnostics.Stopwatch swg = System.Diagnostics.Stopwatch.StartNew();
                    Catalog.ScanMods();
                    Log("INFO", "扫库阶段完成（含缓存落盘），开始归组");
                    Catalog.BuildGroups();
                    Log("INFO", "归组计时: 扫库后归组 " + swg.ElapsedMilliseconds + " ms");
                }
                catch (Exception ex)
                {
                    Log("FAIL", "整理 MOD 出错: " + ex);
                    throw;
                }
                Log("INFO", "整理成 " + Catalog.Groups.Count + " 个 MOD（其中 " + CountSets() + " 个是多 vpk 套装）");
            }
            finally
            {
                _scanBusy = false;
                System.Threading.Monitor.Exit(_scanGate);
            }
        }

        private static int CountSets()
        {
            int n = 0;
            foreach (ModGroup g in Catalog.Groups) if (g.IsSet) n++;
            return n;
        }

        // 文件所在的相对目录（用于“位置”列）
        public static string RelDir(string relPath)
        {
            int i = relPath.LastIndexOf('\\');
            return i > 0 ? relPath.Substring(0, i) : "";
        }

        // 文件夹名是给资源管理器看的（addons各类物品储藏库\AK47），界面上只说「是什么东西」。
        // 去掉 addons 前缀 + 别名，多级用「 · 」连起来：枪械 · AK47
        public static string PrettyCat(string folder)
        {
            if (folder == null || folder.Length == 0) return "";
            string[] parts = folder.Split('\\');
            List<string> outs = new List<string>();
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i];
                if (p.Length == 0) continue;
                if (i == 0 && p.StartsWith("addons", StringComparison.OrdinalIgnoreCase)) p = p.Substring(6);
                p = CatAlias(p);
                if (p.Length > 0) outs.Add(p);
            }
            return string.Join(" · ", outs.ToArray());
        }

        // 单层目录名 → 人话。认不出来的就不动，别硬编一个错的名字上去。
        public static string CatAlias(string n)
        {
            if (n == null) return "";
            if (n.StartsWith("addons", StringComparison.OrdinalIgnoreCase)) n = n.Substring(6);
            switch (n)
            {
                case "各类物品储藏库": return "物品库";
                case "武器类": return "枪械";
                case "枪械类": return "枪械";
                case "喷漆扩展": return "喷漆";
                case "喷图扩展": return "喷漆";
                case "三方地图": return "地图";
                case "医疗物品": return "医疗";
                case "杂项功能": return "杂项";
                // 工坊下载落的目录：下划线只是避免和用户的目录重名，界面上不该看见它
                case "_创意工坊": return "创意工坊";
            }
            return n;
        }

        // ---------------- 包检查 ----------------
        public static List<string> CheckPackages(out bool allOk)
        {
            Phase = "正在检查组件包…";
            List<string> lines = new List<string>();
            allOk = true;
            string l4nZip = FirstFile(PkgDir, "L4N_v*.zip");
            string l4nDir = Path.Combine(PkgDir, "L4N平台\\L4N");
            bool hasL4N = (l4nZip != null) || File.Exists(Path.Combine(l4nDir, "left4dead2.exe"));
            lines.Add(hasL4N ? ("L4N      OK  " + (l4nZip != null ? Path.GetFileName(l4nZip) : "L4N平台\\L4N")) : "L4N      缺");
            if (!hasL4N) allOk = false;

            string skDll = Path.Combine(PkgDir, "Skeeto\\Skeeto\\skeeto.dll");
            string skPs1 = Path.Combine(PkgDir, "Skeeto\\Skeeto\\Skeeto\\Maintain.ps1");
            bool hasSk = File.Exists(skDll) || File.Exists(skPs1);
            lines.Add(hasSk ? ("Skeeto   OK  " + SkeetoVersion()) : "Skeeto   缺");
            if (!hasSk) allOk = false;

            string vpk = FirstFile(Path.Combine(PkgDir, "Skeeto\\素材包"), "*.vpk");
            lines.Add(vpk != null ? ("素材包   OK  " + Human(new FileInfo(vpk).Length)) : "素材包   缺");
            if (vpk == null) allOk = false;

            string mq = Path.Combine(PkgDir, "MQCam\\MQCam\\mqcore_package_tool.exe");
            lines.Add(File.Exists(mq) ? ("MQCam    OK  " + File.GetLastWriteTime(mq).ToString("yyyy-MM-dd")) : "MQCam    缺");
            if (!File.Exists(mq)) allOk = false;

            string dx1 = Path.Combine(PkgDir, "防闪退\\d3d9.dll");
            string dx2 = Path.Combine(PkgDir, "防闪退\\dxvk.conf");
            bool hasDx = File.Exists(dx1) && File.Exists(dx2);
            lines.Add(hasDx ? ("dxvk     OK  " + (new FileInfo(dx1).Length / 1024).ToString("N0") + " KB, " + File.GetLastWriteTime(dx1).ToString("yyyy-MM-dd")) : "dxvk     缺");
            if (!hasDx) allOk = false;

            foreach (string l in lines) Log("INFO", l);
            return lines;
        }

        private static string SkeetoVersion()
        {
            try
            {
                string v = Path.Combine(Core.PkgDir, "Skeeto\\Skeeto\\VERSION.txt");
                if (File.Exists(v))
                {
                    string[] lines = File.ReadAllLines(v);
                    if (lines.Length > 0) return lines[0].Trim();
                }
            }
            catch { }
            return "";
        }

        public static string FirstFile(string dir, string pattern)
        {
            try
            {
                if (!Directory.Exists(dir)) return null;
                string[] f = Directory.GetFiles(dir, pattern);
                if (f.Length == 0) return null;
                Array.Sort(f);
                return f[f.Length - 1];
            }
            catch { return null; }
        }

        // ---------------- 复制（带进度、可跳过） ----------------
        public static bool SameFile(string a, string b)
        {
            try
            {
                if (!File.Exists(b)) return false;
                FileInfo fa = new FileInfo(a);
                FileInfo fb = new FileInfo(b);
                return fa.Length == fb.Length && Math.Abs((fa.LastWriteTimeUtc - fb.LastWriteTimeUtc).TotalSeconds) < 3;
            }
            catch { return false; }
        }

        // ---------------- 原子落地：先写临时文件，成功后再换名 ----------------
        /// <summary>
        /// 和目标同目录的临时名。后缀不是 .vpk，扫库不会把它当成 MOD；
        /// 同一目录 = 同一卷，后面的换名是原子改名，不会跨盘复制。
        /// </summary>
        public static string TempPathFor(string dst)
        {
            return dst + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".part";
        }

        /// <summary>
        /// 把写完的临时文件换成正式文件。
        /// 目标已存在时先用 File.Replace（一次系统调用换名，保留原文件的属性，exFAT 上也成立）；
        /// 它失败就把旧文件改名成 .old 留一手，再移新文件 —— 全程不删旧文件，断在中间也能退回去。
        /// </summary>
        public static void Swap(string tmp, string dst)
        {
            if (!File.Exists(dst)) { File.Move(tmp, dst); return; }
            string first;
            try { File.Replace(tmp, dst, null, true); return; }
            catch (Exception ex) { first = ex.Message; }

            // 这个卷不支持整体换名（少见），或者目标正被别的程序占着。
            // 早先是 File.Delete(dst) → File.Move(tmp, dst)：两步之间断电 / 拔盘 / 强退，
            // 旧文件已经没了、新文件还没到位，用户手里就是一个文件都不剩。
            // 现在先把旧文件挪到同目录的 .old-xxxx，新文件落位成功后才删它；失败就把 .old 挪回来。
            string bak = dst + ".old-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            bool oldMoved = false;
            try
            {
                File.Move(dst, bak);
                oldMoved = true;
                File.Move(tmp, dst);
            }
            catch (Exception ex)
            {
                if (oldMoved)
                {
                    try
                    {
                        // 新文件可能已经落到 dst 上（极少见），先清掉占位再把旧文件挪回去
                        if (File.Exists(dst)) File.Delete(dst);
                        File.Move(bak, dst);
                        oldMoved = false;
                    }
                    catch (Exception back)
                    {
                        Log("FAIL", "换名失败后旧文件没能挪回原位，已保留在 " + bak + ": " + back.Message);
                    }
                }
                throw new IOException("覆盖写入失败，旧文件仍保留（" + (oldMoved ? bak : dst) + "）：" + ex.Message, ex);
            }
            // 新文件已经完整落位，旧的那份这时候才可以删；删不掉也只是留个 .old，不影响使用
            try { if (File.Exists(bak)) File.Delete(bak); }
            catch (Exception ex) { Log("WARN", "旧文件暂未删除（可手动清理）: " + bak + "，" + ex.Message); }

            if (!_swapFallbackTold)
            {
                _swapFallbackTold = true;
                Log("INFO", "覆盖写改用「旧文件改名保留 + 移入新文件」: " + first);
            }
        }

        /// <summary>写文本配置用：要么整份换成新的，要么原文件一个字都不动。</summary>
        public static void WriteAllTextAtomic(string path, string text, Encoding enc)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string tmp = TempPathFor(path);
            try
            {
                File.WriteAllText(tmp, text, enc);
                Swap(tmp, path);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }

        /// <summary>full 是否真的落在 root 下面（防止记录里的相对路径写到游戏目录外）。</summary>
        public static bool IsUnder(string root, string full)
        {
            try
            {
                string r = Path.GetFullPath(root).TrimEnd('\\') + "\\";
                string f = Path.GetFullPath(full);
                return f.Length > r.Length && f.StartsWith(r, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static void CopyOne(string src, string dst)
        {
            string h;
            CopyOneHash(src, dst, out h);
        }

        // 不算哈希的纯复制：大包搬运（L4N / Skeeto / 素材包）不需要哈希
        public static void CopyOnePlain(string src, string dst)
        {
            string d = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(d) && !Directory.Exists(d)) Directory.CreateDirectory(d);
            if (_copyBuf == null) _copyBuf = new byte[2 * 1024 * 1024];
            byte[] buf = _copyBuf;
            string tmp = TempPathFor(dst);
            try
            {
                using (FileStream fi = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
                using (FileStream fo = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024))
                {
                    int n;
                    while ((n = fi.Read(buf, 0, buf.Length)) > 0) fo.Write(buf, 0, n);
                }
                try { File.SetLastWriteTimeUtc(tmp, File.GetLastWriteTimeUtc(src)); } catch { }
                Swap(tmp, dst);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }

        // 文件名里不能出现的字符换成下划线，顺带掐掉路径分隔符，防止写到别的目录
        public static string SafeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            const string bad = "\\/:*?\"<>|";
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char ch in s) sb.Append(bad.IndexOf(ch) >= 0 ? '_' : ch);
            string r = sb.ToString().Trim().TrimEnd('.');
            if (r.Length == 0) r = "_";
            return r;
        }

        // 复制一遍的同时把源文件 SHA256 算掉（不额外读第二遍），并保留源文件的修改时间
        // 写的是同目录临时文件，整份写完才换名 —— 中断只会在 addons 里留个 .part，不会留半截 vpk
        public static void CopyOneHash(string src, string dst, out string hash)
        {
            hash = null;
            string d = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(d) && !Directory.Exists(d)) Directory.CreateDirectory(d);
            if (_copyBuf == null) _copyBuf = new byte[2 * 1024 * 1024];
            byte[] buf = _copyBuf;
            string tmp = TempPathFor(dst);
            try
            {
                using (SHA256 sha = SHA256.Create())
                using (FileStream fi = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
                using (FileStream fo = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024))
                {
                    int n;
                    while ((n = fi.Read(buf, 0, buf.Length)) > 0)
                    {
                        fo.Write(buf, 0, n);
                        sha.TransformBlock(buf, 0, n, null, 0);
                    }
                    sha.TransformFinalBlock(buf, 0, 0);
                    hash = ToHex(sha.Hash);
                }
                try { File.SetLastWriteTimeUtc(tmp, File.GetLastWriteTimeUtc(src)); } catch { }
                Swap(tmp, dst);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }

        private static string ToHex(byte[] h)
        {
            if (h == null) return null;
            StringBuilder sb = new StringBuilder(h.Length * 2);
            foreach (byte b in h) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public static string HashOf(string path)
        {
            try
            {
                using (SHA256 sha = SHA256.Create())
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    byte[] h = sha.ComputeHash(fs);
                    StringBuilder sb = new StringBuilder(h.Length * 2);
                    foreach (byte b in h) sb.Append(b.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return null; }
        }

        // 校验：大小必查；StrictVerify 打开时才回读目标算一遍 SHA256
        public static bool CopyOneVerified(string src, string dst, out string why)
        {
            why = null;
            try
            {
                string hs;
                CopyOneHash(src, dst, out hs);
                long a = new FileInfo(src).Length, b = new FileInfo(dst).Length;
                if (a != b) { why = "大小不符 " + b + "/" + a; return false; }
                if (StrictVerify)
                {
                    string hd = HashOf(dst);
                    if (hd == null) { why = "目标哈希算不出来"; return false; }
                    if (!string.Equals(hs, hd, StringComparison.OrdinalIgnoreCase)) { why = "SHA256 不一致"; return false; }
                }
                return true;
            }
            catch (Exception ex) { why = ex.Message; return false; }
        }

        public static void SelfTestCore(string gameRoot)
        {
        }
    }

    /// <summary>
    /// 写盘操作的全局互斥。装机、还原、清重、只装某个位置、应用开关这些流程里都会
    /// Application.DoEvents() 泵消息（为了刷新进度和响应「停止」），泵消息就意味着用户能
    /// 在流程中途再点一次菜单、快捷键、方案树或桌面卡片。两件事同时往同一批文件上写，
    /// 轻则日志对不上，重则把刚写完的文件按旧基线判成冲突。
    ///
    /// 放在全局而不是某个窗体上：桌面卡片走 Socket、方案树走 UserControl，
    /// 各拿各的锁等于没锁。
    /// </summary>
    public static class OpGuard
    {
        private static string current;
        private static readonly object gate = new object();

        public static string Current { get { lock (gate) return current; } }

        /// <summary>占住写盘权。已经有别的操作在跑就返回 false（调用方只需 return）。</summary>
        public static bool Enter(string what)
        {
            lock (gate)
            {
                if (current != null) return false;
                current = what;
                return true;
            }
        }

        /// <summary>按名字释放：防止 A 的收尾把 B 的锁放掉。</summary>
        public static void Exit(string what)
        {
            lock (gate) { if (current == what) current = null; }
        }
    }
}
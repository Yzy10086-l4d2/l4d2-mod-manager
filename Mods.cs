using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Installer
{
    // ---------- addonlist.txt 读写（游戏用它记录“哪些 MOD 开着”） ----------
    public static class AddonList
    {
        private static Regex LineRe = new Regex("^\\s*\"([^\"]+)\"\\s+\"([01])\"\\s*$", RegexOptions.Compiled);

        public static string FileOf(string gameRoot)
        {
            return Path.Combine(Path.Combine(gameRoot, "left4dead2"), "addonlist.txt");
        }

        public static void WriteOrdered(string gameRoot, List<string> order, Dictionary<string, bool> states)
        {
            string p = FileOf(gameRoot);
            List<string> lines = new List<string>();
            if (File.Exists(p)) lines.AddRange(File.ReadAllLines(p, Encoding.Default));
            if (lines.Count == 0) { lines.Add("\"AddonList\""); lines.Add("{"); lines.Add("}"); }
            int open = lines.FindIndex(delegate(string s) { return s.Trim() == "{"; });
            int close = lines.FindLastIndex(delegate(string s) { return s.Trim() == "}"; });
            if (open < 0 || close < 0 || close <= open) { Write(gameRoot, states); return; }
            List<string> head = lines.GetRange(0, open + 1);
            List<string> tail = lines.GetRange(close, lines.Count - close);
            HashSet<string> written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> body = new List<string>();
            foreach (string name in order)
            {
                bool on = states.ContainsKey(name) ? states[name] : true;
                body.Add("\t\"" + name + "\"\t\t\"" + (on ? "1" : "0") + "\"");
                written.Add(name);
            }
            foreach (string raw in lines.GetRange(open + 1, close - open - 1))
            {
                Match mm = LineRe.Match(raw);
                if (mm.Success && written.Contains(mm.Groups[1].Value)) continue;
                body.Add(raw);
            }
            List<string> all = new List<string>();
            all.AddRange(head); all.AddRange(body); all.AddRange(tail);
            WriteKeepEol(p, all);
        }

        /// <summary>
        /// 按原文件的行尾写回。游戏自己写出来的是 LF；以前一律用 File.WriteAllLines（Windows 下是 CRLF），
        /// 结果只是「点一下开关」，整份 addonlist 的每一行都被改了 —— 用对比工具看像是全文件都动过，
        /// 也说不清到底改的是哪几条。现在沿用原文的行尾和文件末尾是否有换行，只动该动的那几行。
        /// </summary>
        private static void WriteKeepEol(string path, List<string> lines)
        {
            string nl = "\r\n", tail = "";
            try
            {
                if (File.Exists(path))
                {
                    string raw = File.ReadAllText(path, Encoding.Default);
                    nl = raw.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
                    tail = raw.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n"
                         : raw.EndsWith("\n", StringComparison.Ordinal) ? "\n" : "";
                }
            }
            catch { }
            // 原子写：写一半被打断（拔 U 盘 / 空间满）会把 addonlist 留成空文件，游戏里所有开关就全丢了
            Core.WriteAllTextAtomic(path, string.Join(nl, lines.ToArray()) + tail, Encoding.Default);
        }

        public static Dictionary<string, bool> Read(string gameRoot)
        {
            Dictionary<string, bool> d = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            string p = FileOf(gameRoot);
            if (!File.Exists(p)) return d;
            foreach (string raw in File.ReadAllLines(p, Encoding.Default))
            {
                Match m = LineRe.Match(raw);
                if (m.Success) d[m.Groups[1].Value] = (m.Groups[2].Value == "1");
            }
            return d;
        }

        public static void Write(string gameRoot, Dictionary<string, bool> changes)
        {
            string p = FileOf(gameRoot);
            List<string> lines = new List<string>();
            if (File.Exists(p)) lines.AddRange(File.ReadAllLines(p, Encoding.Default));
            if (lines.Count == 0)
            {
                lines.Add("\"AddonList\"");
                lines.Add("{");
                lines.Add("}");
            }
            HashSet<string> done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < lines.Count; i++)
            {
                Match m = LineRe.Match(lines[i]);
                if (!m.Success) continue;
                string name = m.Groups[1].Value;
                if (changes.ContainsKey(name))
                {
                    lines[i] = "\t\"" + name + "\"\t\t\"" + (changes[name] ? "1" : "0") + "\"";
                    done.Add(name);
                }
            }
            List<string> add = new List<string>();
            foreach (KeyValuePair<string, bool> kv in changes)
            {
                if (!done.Contains(kv.Key)) add.Add("\t\"" + kv.Key + "\"\t\t\"" + (kv.Value ? "1" : "0") + "\"");
            }
            if (add.Count > 0)
            {
                int close = lines.FindLastIndex(delegate(string s) { return s.Trim() == "}"; });
                if (close < 0) close = lines.Count;
                lines.InsertRange(close, add);
            }
            WriteKeepEol(p, lines);
        }

        /// <summary>从 addonlist 里删掉这些名字（文件被移走后，别留一条指向不存在文件的开关）。</summary>
        public static int Remove(string gameRoot, IEnumerable<string> names)
        {
            string p = FileOf(gameRoot);
            if (!File.Exists(p)) return 0;
            HashSet<string> drop = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            if (drop.Count == 0) return 0;
            List<string> lines = new List<string>(File.ReadAllLines(p, Encoding.Default));
            int n = 0;
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                Match m = LineRe.Match(lines[i]);
                if (m.Success && drop.Contains(m.Groups[1].Value)) { lines.RemoveAt(i); n++; }
            }
            if (n > 0) WriteKeepEol(p, lines);
            return n;
        }
    }

    // ---------- 方案预设 ----------
    public class Profile
    {
        public string Name = "";
        public DateTime Saved = DateTime.MinValue;
        public List<string> Mods = new List<string>();
        public bool Dxvk = true;
        public string Plat = "";
        public bool HasOpt = false;      // 有 Opt= 行：才知道 换整套/覆盖/参数存档 这些选项
        public bool Swap = false;
        public bool Overwrite = false;
        public string Tweak = "";
        public string Launch = "";
        public string SelfTest = "";     // 空=没跑过；否则 PASS|120|时间 或 FAIL|24|时间

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Name);
            sb.Append(" · ");
            sb.Append(Mods.Count);
            sb.Append(" 个");
            if (Saved != DateTime.MinValue) { sb.Append(" · "); sb.Append(Saved.ToString("MM-dd HH:mm")); }
            if (!string.IsNullOrEmpty(SelfTest)) sb.Append(SelfTest.StartsWith("FAIL") ? " · ⚠崩过" : " · 自检过");
            return sb.ToString();
        }
    }

    public static class ProfileStore
    {
        public static string DirOf()
        {
            return Path.Combine(Core.LogDir, "方案");
        }

        public static List<string> ListProfiles()
        {
            List<string> names = new List<string>();
            try
            {
                string d = DirOf();
                if (!Directory.Exists(d)) return names;
                foreach (string f in Directory.GetFiles(d, "*.txt"))
                    names.Add(Path.GetFileNameWithoutExtension(f));
            }
            catch { }
            names.Sort();
            return names;
        }

        public static List<Profile> LoadAll()
        {
            List<Profile> list = new List<Profile>();
            try
            {
                string d = DirOf();
                if (!Directory.Exists(d)) return list;
                foreach (string f in Directory.GetFiles(d, "*.txt"))
                {
                    Profile p = Parse(f);
                    if (p != null) list.Add(p);
                }
            }
            catch { }
            list.Sort(delegate(Profile a, Profile b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            return list;
        }

        public static Profile Load(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string f = Path.Combine(DirOf(), Core.SafeName(name) + ".txt");
            if (!File.Exists(f)) return null;
            return Parse(f);
        }

        private static Profile Parse(string file)
        {
            Profile p = new Profile();
            p.Name = Path.GetFileNameWithoutExtension(file);
            try { p.Saved = File.GetLastWriteTime(file); } catch { }
            string[] lines;
            try { lines = File.ReadAllLines(file, Encoding.UTF8); } catch { return p; }
            foreach (string raw in lines)
            {
                string s = raw.Trim();
                if (s.StartsWith("Dxvk=")) p.Dxvk = (s.Substring(5) == "1");
                else if (s.StartsWith("Plat=")) p.Plat = s.Substring(5);
                else if (s.StartsWith("Mod=")) p.Mods.Add(s.Substring(4));
                else if (s.StartsWith("Launch=")) p.Launch = s.Substring(7);
                else if (s.StartsWith("SelfTest=")) p.SelfTest = s.Substring(9);
                else if (s.StartsWith("Opt="))
                {
                    p.HasOpt = true;
                    foreach (string part in s.Substring(4).Split(';'))
                    {
                        string[] kv = part.Split('=');
                        if (kv.Length != 2) continue;
                        if (kv[0] == "Swap") p.Swap = (kv[1] == "1");
                        else if (kv[0] == "Over") p.Overwrite = (kv[1] == "1");
                        else if (kv[0] == "Tweak") p.Tweak = kv[1];
                    }
                }
            }
            return p;
        }

        // 写方案：同名直接覆盖，不留副本
        public static void Save(Profile p)
        {
            p.Name = Core.SafeName(p.Name);
            string d = DirOf();
            if (!Directory.Exists(d)) Directory.CreateDirectory(d);
            string f = Path.Combine(d, p.Name + ".txt");
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# 方案 " + p.Name + "  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            sb.AppendLine("Dxvk=" + (p.Dxvk ? "1" : "0"));
            if (!string.IsNullOrEmpty(p.Plat)) sb.AppendLine("Plat=" + p.Plat);
            if (p.HasOpt)
            {
                sb.AppendLine("Opt=Swap=" + (p.Swap ? "1" : "0") + ";Over=" + (p.Overwrite ? "1" : "0") + ";Tweak=" + (p.Tweak == null ? "" : p.Tweak));
                if (!string.IsNullOrEmpty(p.Launch)) sb.AppendLine("Launch=" + p.Launch);
            }
            foreach (string m in p.Mods) sb.AppendLine("Mod=" + m);
            if (!string.IsNullOrEmpty(p.SelfTest)) sb.AppendLine("SelfTest=" + p.SelfTest);
            File.WriteAllText(f, sb.ToString(), new UTF8Encoding(true));
        }

        // 把自检结果写回方案（kind = PASS / FAIL）
        public static void SetSelfTest(string name, string kind, int seconds)
        {
            Profile p = Load(name);
            if (p == null) return;
            p.SelfTest = kind + "|" + seconds + "|" + DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            Save(p);
        }

        public static void Delete(string name)
        {
            string f = Path.Combine(DirOf(), Core.SafeName(name) + ".txt");
            if (File.Exists(f)) File.Delete(f);
        }

        public static string Rename(string oldName, string newName)
        {
            newName = Core.SafeName(newName);
            if (string.IsNullOrEmpty(newName)) return "名字不能为空";
            string a = Path.Combine(DirOf(), Core.SafeName(oldName) + ".txt");
            string b = Path.Combine(DirOf(), newName + ".txt");
            if (!File.Exists(a)) return "原方案不存在";
            if (File.Exists(b)) return "已经有叫这个名字的方案了";
            File.Move(a, b);
            return null;
        }

        // 另存为：复制一份，不动原件
        public static string CopyAs(string from, string to)
        {
            to = Core.SafeName(to);
            if (string.IsNullOrEmpty(to)) return "名字不能为空";
            string a = Path.Combine(DirOf(), Core.SafeName(from) + ".txt");
            string b = Path.Combine(DirOf(), to + ".txt");
            if (!File.Exists(a)) return "原方案不存在";
            if (File.Exists(b)) return "已经有叫这个名字的方案了";
            File.Copy(a, b, false);
            return null;
        }
    }

    // 一组「体积完全相同」的 vpk。体积相同只是嫌疑，内容是否一样要算过才知道（DupVerify）。
    public class DupGroup
    {
        public long Size;
        public List<ModItem> Items = new List<ModItem>();
        /// <summary>核对结果：""=没查过，"same"=内容完全相同，"diff"=内容不同</summary>
        public string Verdict = "";
        public string VerdictNote = "";
    }

    // ---------- 冲突提示（只做便宜的三类） ----------
    public static class ConflictCheck
    {
        public static List<string> Run(List<ModItem> mods, string gameRoot)
        {
            return Run(mods, gameRoot, true);
        }

        // checkExisting=false 用于启动阶段：游戏目录常在网络共享上，逐个 File.Exists 很贵
        public static List<string> Run(List<ModItem> mods, string gameRoot, bool checkExisting)
        {
            List<string> msgs = new List<string>();
            Dictionary<string, List<ModItem>> byName = new Dictionary<string, List<ModItem>>(StringComparer.OrdinalIgnoreCase);
            int already = 0;
            string addons = Path.Combine(Path.Combine(gameRoot, "left4dead2"), "addons");
            foreach (ModItem m in mods)
            {
                if (!byName.ContainsKey(m.Name)) byName[m.Name] = new List<ModItem>();
                byName[m.Name].Add(m);
                if (checkExisting)
                {
                    try { if (Directory.Exists(addons) && File.Exists(Path.Combine(addons, m.Name))) already++; } catch { }
                }
            }
            foreach (KeyValuePair<string, List<ModItem>> kv in byName)
            {
                if (DistinctGroups(kv.Value) < 2) continue;          // 同一个套装的部件同名不算冲突
                List<string> cats = new List<string>();
                foreach (ModItem m in kv.Value)
                {
                    string pc = m.Cat + " · " + m.Sub;
                    if (!cats.Contains(pc)) cats.Add(pc);
                }
                msgs.Add("同名文件 " + kv.Value.Count + " 份: " + kv.Key + "（" + string.Join("、", cats.ToArray()) + "），铺进去会互相覆盖");
            }
            foreach (DupGroup g in DupGroups(mods))
            {
                List<string> names = new List<string>();
                foreach (ModItem m in g.Items) names.Add(m.Name);
                msgs.Add("体积完全相同（可能重复）: " + string.Join("、", names.ToArray()));
            }
            if (already > 0) msgs.Add("游戏 addons 里已存在 " + already + " 个同名文件（会被这次的覆盖）");
            return msgs;
        }

        /// <summary>
        /// 体积完全相同（≥1 MB）但不是同一个套装的 vpk —— 只是「嫌疑」，
        /// 真重复要算过内容才知道。重复对话框和日志都用这一份数据。
        /// </summary>
        public static List<DupGroup> DupGroups(List<ModItem> mods)
        {
            List<DupGroup> res = new List<DupGroup>();
            Dictionary<long, List<ModItem>> bySize = new Dictionary<long, List<ModItem>>();
            foreach (ModItem m in mods)
            {
                if (m.Size < 1048576L) continue;                  // 小文件同大小太常见，报了也是噪音
                if (!bySize.ContainsKey(m.Size)) bySize[m.Size] = new List<ModItem>();
                bySize[m.Size].Add(m);
            }
            foreach (KeyValuePair<long, List<ModItem>> kv in bySize)
            {
                if (DistinctGroups(kv.Value) < 2) continue;        // 同一套装的部件不算
                DupGroup g = new DupGroup();
                g.Size = kv.Key;
                g.Items = kv.Value;
                res.Add(g);
            }
            // 浪费空间大的排前面：先处理值得处理的
            res.Sort(delegate(DupGroup a, DupGroup b)
            {
                long wa = a.Size * (a.Items.Count - 1), wb = b.Size * (b.Items.Count - 1);
                return wb.CompareTo(wa);
            });
            return res;
        }

        private static int DistinctGroups(List<ModItem> list)
        {
            List<string> g = new List<string>();
            foreach (ModItem m in list)
            {
                string k = m.GroupKey == null ? m.Key : m.GroupKey;
                if (!g.Contains(k)) g.Add(k);
            }
            return g.Count;
        }

        // 深度检查：只读 VPK 目录表，看这些 MOD 之间有没有同路径文件
        public static List<string> Deep(List<ModItem> sel)
        {
            return DeepAll(sel);
        }

        // 一次遍历同时给出「路径重叠」和「脚本库冲突」，同一批 vpk 不再解析两遍。
        // 只记“第一次出现是谁的”，出现第二次才建列表——否则每个路径都 new 一个 List，
        // 选一大套三方图就能把内存吃满。
        public static List<string> DeepAll(List<ModItem> sel)
        {
            List<string> msgs = new List<string>();
            if (sel == null || sel.Count < 2) return msgs;

            // 同一把枪被多个 MOD 替换 —— 最容易装错的一类，而且文件重叠检查抓不到：
            // 一个改 v_rifle、另一个改 w_rifle_m16a2，文件名完全不同，抢的却是同一把枪。
            // 换整套模型的、只换第一人称的分开记账：只换第一人称是叠在模型上的，两类之间不算冲突。
            Dictionary<string, List<ModItem>> bySubject = new Dictionary<string, List<ModItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (ModItem m in sel)
            {
                if (m.Subject.Length == 0) continue;
                if (m.SubjectLevel != "模型") continue;
                string k = (m.Only1P ? "1P" : "ALL") + "\u0001" + m.Subject;
                if (!bySubject.ContainsKey(k)) bySubject[k] = new List<ModItem>();
                bySubject[k].Add(m);
            }
            foreach (KeyValuePair<string, List<ModItem>> kv in bySubject)
            {
                if (kv.Value.Count < 2) continue;
                List<string> names = new List<string>();
                foreach (ModItem m in kv.Value)
                {
                    string nm = m.Name;
                    if (nm.Length > 20) nm = nm.Substring(0, 19) + "…";
                    names.Add(nm);
                }
                string[] kp = kv.Key.Split('\u0001');
                string act = kp[0] == "1P" ? "都在改第一人称模型" : "都在换";
                msgs.Add(kv.Value.Count + " 个 MOD " + act + " " + kp[1] + "："
                         + string.Join("、", names.ToArray()) + "（同时装只有一个会生效）");
            }

            const int Cap = 1500000;                  // 最多跟踪 150 万个不同路径
            Dictionary<string, int> firstOwner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<int>> dup = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            int[] tracked = new int[1];
            int[] capped = new int[1];
            Stopwatch sw = Stopwatch.StartNew();
            for (int idx = 0; idx < sel.Count; idx++)
            {
                if (sel[idx].IsArchive) continue;      // 压缩包里的 vpk 要先解压才能读目录表，这里不做
                int owner = idx;
                Vpk.Walk(sel[idx].FullPath, 300000, delegate(string p)
                {
                    // 每个 addon 都带的“身份证”文件，互相重叠属正常，不算冲突
                    if (p.EndsWith("/addoninfo.txt") || p == "addoninfo.txt") return;
                    if (p.EndsWith("/addonimage.jpg") || p == "addonimage.jpg") return;
                    if (p.EndsWith("/sound.cache") || p == "sound/sound.cache") return;

                    List<int> owners;
                    if (dup.TryGetValue(p, out owners))
                    {
                        if (!owners.Contains(owner)) owners.Add(owner);
                        return;
                    }
                    int first;
                    if (firstOwner.TryGetValue(p, out first))
                    {
                        firstOwner.Remove(p);
                        owners = new List<int>();
                        owners.Add(first);
                        owners.Add(owner);
                        dup[p] = owners;
                        return;
                    }
                    if (tracked[0] >= Cap) { capped[0]++; return; }
                    firstOwner[p] = owner;
                    tracked[0]++;
                });
            }
            sw.Stop();
            int overlap = 0, scriptHit = 0;
            List<string> ex1 = new List<string>();
            List<string> ex2 = new List<string>();
            HashSet<int> involved = new HashSet<int>();
            foreach (KeyValuePair<string, List<int>> kv in dup)
            {
                if (SameGroupOnly(sel, kv.Value)) continue;   // 一套里的几个分片共用文件属正常
                bool isScript = kv.Key.StartsWith("scripts/vscripts/", StringComparison.OrdinalIgnoreCase);
                if (isScript)
                {
                    // 官方允许每个 addon 各带一份的入口脚本，不算冲突
                    if (kv.Key.EndsWith("/director_base_addon.nut", StringComparison.OrdinalIgnoreCase)) continue;
                    if (kv.Key.EndsWith("/scriptedmode_addon.nut", StringComparison.OrdinalIgnoreCase)) continue;
                    if (kv.Key.EndsWith("/mapspawn_addon.nut", StringComparison.OrdinalIgnoreCase)) continue;
                    scriptHit++;
                    if (ex2.Count < 3) ex2.Add(kv.Key + "（" + NamesOf(sel, kv.Value) + "）");
                }
                else
                {
                    overlap++;
                    if (ex1.Count < 3) ex1.Add(kv.Key + "（" + NamesOf(sel, kv.Value) + "）");
                }
                foreach (int i in kv.Value) involved.Add(i);
            }
            if (overlap > 0)
            {
                msgs.Add("这 " + sel.Count + " 个 MOD 之间有 " + overlap + " 个文件路径重叠，会互相覆盖");
                msgs.Add("涉及 " + involved.Count + " 个 MOD");
                foreach (string e in ex1) msgs.Add("例: " + e);
            }
            if (scriptHit > 0)
            {
                msgs.Add("另有 " + scriptHit + " 个 scripts/vscripts 脚本被多个 MOD 同时提供，这类冲突会让进图直接崩");
                foreach (string e in ex2) msgs.Add("例: " + e);
            }
            if (capped[0] > 0) msgs.Add("（路径太多，只比对了前 " + Cap + " 个，剩余 " + capped[0] + " 条未比对）");
            Core.Log("INFO", "深度冲突检查 " + Math.Round(sw.Elapsed.TotalSeconds, 1) + " 秒，唯一路径 " + tracked[0] + " 条");
            return msgs;
        }

        private static bool SameGroupOnly(List<ModItem> sel, List<int> idx)
        {
            string first = null;
            foreach (int i in idx)
            {
                string k = sel[i].GroupKey == null ? sel[i].Key : sel[i].GroupKey;
                if (first == null) first = k;
                else if (k != first) return false;
            }
            return true;
        }

        private static string NamesOf(List<ModItem> sel, List<int> idx)
        {
            List<string> names = new List<string>();
            foreach (int i in idx) if (i >= 0 && i < sel.Count) names.Add(sel[i].Name);
            return string.Join(" / ", names.ToArray());
        }
    }

    // ---------- 按顺序写 addonlist（顺序=覆盖优先级） ----------    // ---------- VPK 目录表解析（只读文件表，不读内容） ----------
    public static class Vpk
    {
        public static List<string> Paths(string file, int maxEntries)
        {
            List<string> list = new List<string>();
            Walk(file, maxEntries, delegate(string p) { list.Add(p); });
            return list;
        }

        // 流式版：目录树一次读进内存再扫（不再逐字节 ReadByte），也不把几万条路径全堆在 List 里
        // 从内存里的目录表走一遍（zip / rar 里解出的那一小段用得上）
        // 结构与 Vpk.Walk 完全一致：扩展名 → 目录 → 文件名 三层，每条后面跟 18 字节元数据
        public static void WalkBuffer(byte[] t, int maxEntries, Action<string> onPath)
        {
            int i = 0, count = 0;
            while (i < t.Length)
            {
                string ext = ReadStr(t, ref i);
                if (ext.Length == 0) break;
                while (i < t.Length)
                {
                    string path = ReadStr(t, ref i);
                    if (path.Length == 0) break;
                    while (i < t.Length)
                    {
                        string name = ReadStr(t, ref i);
                        if (name.Length == 0) break;
                        i += 18;
                        if (i > t.Length) return;
                        string full = ((path.Length > 0 ? path + "/" : "") + name + "." + ext).TrimStart('/');
                        onPath(full.ToLowerInvariant());
                        if (++count >= maxEntries) return;
                    }
                }
            }
        }

        public static void Walk(string file, int maxEntries, Action<string> onPath)
        {
            int count = 0;
            try
            {
                using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
                using (BinaryReader br = new BinaryReader(fs))
                {
                    if (fs.Length < 12) return;
                    uint sig = br.ReadUInt32();
                    if (sig != 0x55AA1234) return;
                    uint ver = br.ReadUInt32();
                    int treeSize = (int)br.ReadUInt32();
                    if (ver == 2) br.ReadBytes(16);
                    if (treeSize <= 0) return;
                    long avail = fs.Length - fs.Position;
                    if (treeSize > avail) treeSize = (int)avail;
                    if (treeSize > 32 * 1024 * 1024) treeSize = 32 * 1024 * 1024;
                    byte[] t = br.ReadBytes(treeSize);
                    if (t == null || t.Length == 0) return;
                    int i = 0;
                    while (i < t.Length)
                    {
                        string ext = ReadStr(t, ref i);
                        if (ext.Length == 0) break;                  // 空扩展名 = 目录树结束
                        while (i < t.Length)
                        {
                            string path = ReadStr(t, ref i);
                            if (path.Length == 0) break;             // 空目录 = 这个扩展名结束
                            while (i < t.Length)
                            {
                                string name = ReadStr(t, ref i);
                                if (name.Length == 0) break;         // 空文件名 = 这个目录结束
                                i += 18;   // crc4 + 预载2 + 归档号2 + 偏移4 + 长度4 + 结尾2
                                if (i > t.Length) return;
                                string full = ((path.Length > 0 ? path + "/" : "") + name + "." + ext).TrimStart('/');
                                onPath(full.ToLowerInvariant());
                                if (++count >= maxEntries) return;
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private static string ReadStr(byte[] t, ref int i)
        {
            int start = i;
            while (i < t.Length && t[i] != 0) i++;
            int len = i - start;
            if (i < t.Length) i++;           // 吃掉结尾的 0
            if (len > 512) len = 512;        // 坏文件保护
            return Encoding.UTF8.GetString(t, start, len);
        }
    }

    // ---------- 「已装 MOD 开关」窗口 ----------
    public class AddonSwitchForm : Form
    {
        private readonly string gameRoot;
        private ListView lv;
        private Label lbl;
        private Dictionary<string, bool> state = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        public int Deleted;                 // 这个窗口里删掉（真删）的个数，关窗后主窗口据此决定要不要重扫

        public AddonSwitchForm(string root)
        {
            gameRoot = root;
            Text = "已装 MOD 开关 —— 勾上=进游戏时启用";
            ClientSize = new Size(560, 480);
            Font = new Font("Microsoft YaHei UI", 9F);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;

            state = AddonList.Read(gameRoot);
            lv = new ListView();
            lv.SetBounds(12, 12, 536, 380);
            lv.View = View.Details;
            lv.CheckBoxes = true;
            lv.FullRowSelect = true;
            lv.Columns.Add("MOD 文件", 380);
            lv.Columns.Add("状态", 120);
            lv.ItemChecked += delegate(object s, ItemCheckedEventArgs e)
            {
                if (e.Item.SubItems.Count > 1)
                    e.Item.SubItems[1].Text = e.Item.Checked ? "启用" : "关闭";
            };
            Controls.Add(lv);
            ContextMenuStrip cms = new ContextMenuStrip();
            cms.Items.Add("删除勾中的", null, delegate { DeleteChecked(); });
            lv.ContextMenuStrip = cms;

            Button up = new Button(); up.Text = "上移 ↑"; up.SetBounds(12, 360, 90, 28); up.Click += delegate { MoveItem(-1); }; Controls.Add(up);
            Button dn = new Button(); dn.Text = "下移 ↓"; dn.SetBounds(108, 360, 90, 28); dn.Click += delegate { MoveItem(1); }; Controls.Add(dn);
            Label tip = new Label(); tip.Text = "顺序 = 覆盖优先级（下面的覆盖上面的）"; tip.SetBounds(210, 364, 330, 20); Controls.Add(tip);
            Button all = new Button(); all.Text = "全开"; all.SetBounds(12, 402, 80, 30);
            all.Click += delegate { SetAll(true); }; Controls.Add(all);
            Button none = new Button(); none.Text = "全关"; none.SetBounds(98, 402, 80, 30);
            none.Click += delegate { SetAll(false); }; Controls.Add(none);
            Button collect = new Button(); collect.Text = "收回新 MOD"; collect.SetBounds(184, 402, 110, 30);
            collect.Click += delegate { Collect(); }; Controls.Add(collect);
            Button park = new Button(); park.Text = "清理关着的"; park.SetBounds(300, 402, 104, 30);
            park.Click += delegate { Park(); }; Controls.Add(park);
            Button ok = new Button(); ok.Text = "写入并关闭"; ok.SetBounds(410, 402, 138, 30);
            ok.Click += delegate { Apply(); }; Controls.Add(ok);
            lbl = new Label(); lbl.SetBounds(12, 436, 536, 20); lbl.Text = ""; Controls.Add(lbl);

            LoadItems();
        }

        /// <summary>批量删除：把勾中的 vpk 从游戏 addons 直接删掉，并摘掉 addonlist 登记。</summary>
        private void DeleteChecked()
        {
            List<string> names = new List<string>();
            foreach (ListViewItem li in lv.CheckedItems) names.Add(li.Tag.ToString());
            if (names.Count == 0)
                foreach (ListViewItem li in lv.SelectedItems) names.Add(li.Tag.ToString());
            if (names.Count == 0) { MessageBox.Show("先勾选要删的 MOD（或选中几行）。"); return; }
            if (MessageBox.Show("把这 " + names.Count + " 个 MOD 从游戏里删掉？\r\n\r\n"
                + "· vpk 直接从 left4dead2\\addons 删除，删了就是没了\r\n"
                + "· addonlist 里那条登记也会摘掉\r\n\r\n确定？",
                "删除已装 MOD", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            string addons = Path.Combine(Path.Combine(gameRoot, "left4dead2"), "addons");
            int n = 0;
            foreach (string name in names)
            {
                try
                {
                    ModItem m = new ModItem();
                    m.Name = name;
                    m.FullPath = Path.Combine(addons, name);
                    m.FromGame = true;
                    if (Work.DeleteGameMod(m, gameRoot) == null) n++;
                }
                catch (Exception ex) { Core.Log("WARN", "删除 " + name + " 失败: " + ex.Message); }
            }
            Deleted += n;
            Core.Log("OK", "已装 MOD 开关：从游戏 addons 删掉 " + n + " 个");
            state = AddonList.Read(gameRoot);
            LoadItems();
            lbl.Text = "已删除 " + n + " 个";
        }

        private void LoadItems()
        {
            lv.BeginUpdate();
            lv.Items.Clear();
            string addons = Path.Combine(Path.Combine(gameRoot, "left4dead2"), "addons");
            if (!Directory.Exists(addons)) { lv.EndUpdate(); lbl.Text = "游戏里没有 addons 目录"; return; }
            string[] files = Directory.GetFiles(addons, "*.vpk");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string f in files)
            {
                string name = Path.GetFileName(f);
                bool on = false;
                if (state.ContainsKey(name)) on = state[name];
                ListViewItem li = new ListViewItem(name);
                li.SubItems.Add(on ? "启用" : "关闭");
                li.Checked = on;
                li.Tag = name;
                lv.Items.Add(li);
            }
            lv.EndUpdate();
            lbl.Text = "共 " + files.Length + " 个已装 MOD";
        }

        private void MoveItem(int dir)
        {
            if (lv.SelectedIndices.Count == 0) return;
            int i = lv.SelectedIndices[0];
            int j = i + dir;
            if (j < 0 || j >= lv.Items.Count) return;
            ListViewItem it = lv.Items[i];
            lv.Items.RemoveAt(i);
            lv.Items.Insert(j, it);
            it.Selected = true;
            lv.Focus();
        }

        private void SetAll(bool on)
        {
            lv.BeginUpdate();
            foreach (ListViewItem li in lv.Items)
            {
                li.Checked = on;
                li.SubItems[1].Text = on ? "启用" : "关闭";
            }
            lv.EndUpdate();
        }

        private void Collect()
        {
            List<string> got = Work.CollectNewMods(gameRoot);
            if (got.Count == 0) { MessageBox.Show("没有新 MOD：游戏里的都在 U 盘库里。"); return; }
            MessageBox.Show("已收回 " + got.Count + " 个到 U 盘 addons回收 目录：\r\n" + string.Join("\r\n", got.ToArray()));
        }

        /// <summary>把「关闭」的 MOD 挪回 U 盘，给游戏盘腾地方（开着的一概不碰）。</summary>
        private void Park()
        {
            if (MessageBox.Show(
                "把「关闭」状态的 MOD 从游戏 addons 里挪回 U 盘？\r\n\r\n"
                + "· 只动已经关掉的\r\n"
                + "· 开着的、以及不是本工具装的，一概不碰\r\n"
                + "· 挪走的以后要用会自动重新复制\r\n\r\n"
                + "先把上面的开关写进去，再清理。", "清理关着的 MOD",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            if (!ApplyQuiet()) return;          // 写入失败就不继续清理，避免按旧状态挪文件
            int n = Work.ParkDisabled(gameRoot);
            state = AddonList.Read(gameRoot);
            LoadItems();
            MessageBox.Show(n > 0
                ? ("已挪回 U 盘 " + n + " 个。\r\n下次装到这套时会重新复制回来。")
                : "没有可清理的：关着的 MOD 本来就不在 addons 里。");
        }

        private bool ApplyQuiet()
        {
            Dictionary<string, bool> changes = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            List<string> order = new List<string>();
            foreach (ListViewItem li in lv.Items) { changes[(string)li.Tag] = li.Checked; order.Add((string)li.Tag); }
            try
            {
                AddonList.WriteOrdered(gameRoot, order, changes);
                int on = 0;
                foreach (KeyValuePair<string, bool> kv in changes) if (kv.Value) on++;
                Core.Log("OK", "addonlist 已更新：" + on + " 个启用 / " + (changes.Count - on) + " 个关闭");
                return true;
            }
            catch (Exception ex)
            {
                Core.Log("FAIL", "addonlist 写入失败: " + ex.Message);
                MessageBox.Show("写入失败: " + ex.Message + "\r\n\r\n窗口不会关闭，避免看起来已经保存成功。", "写入失败");
                return false;
            }
        }

        private void Apply()
        {
            if (!ApplyQuiet()) return;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Installer
{
    // ============================================================
    //  方案树的人工归类
    //  库是自动认的，认错了在这儿手工改一次就记住：
    //    · 大类排第几（新建的分类也能排）
    //    · 这个 MOD 算哪一类
    //    · 它替换的是哪个位置（AK-47 / 医疗包 …）
    //  只影响方案树的分组 —— 分类是数据里那几张表，
    //  树才是「一个位置一个 MOD」那张表。
    // ============================================================

    internal static class CatPrefs
    {
        // 默认顺序：树里看得见的四类在前，地图/喷漆不进树、排最后
        public static readonly string[] DefaultCats = { "枪械", "近战", "投掷物", "弹药升级", "人物", "特感", "药品", "杂项", "地图", "喷漆" };

        private static bool loaded;
        private static int ver;                    // 每次改都 +1，Catalog.Cats 的缓存靠它失效
        private static string[] catCache;
        private static int catCacheVer = -1;
        private static readonly List<string> cats = new List<string>();
        private static readonly Dictionary<string, List<string>> slotsOfCat =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> modCat =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> modSlot =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 拖动排出来的顺序。三张表都只在「用户真的拖过」之后才写：
        // 没拖过的大类完全照原来的自动顺序（PosOrder / 识别结果），老用户打开还是熟悉的样子。
        private static readonly Dictionary<string, List<string>> subOrder =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);            // 大类 → 小分类顺序
        private static readonly Dictionary<string, List<string>> posOrder =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);            // 大类␁小分类 → 位置顺序
        private static readonly Dictionary<string, string> slotSub =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);        // 大类␁位置 → 小分类

        static string PosKey(string cat, string sub) { return (cat ?? "") + "\u0001" + (sub ?? ""); }

        public static string FileOf()
        {
            return string.IsNullOrEmpty(Core.PkgDir) ? "" : Path.Combine(Core.PkgDir, "方案树分类.ini");
        }

        public static void Ensure() { if (!loaded) Load(); }

        /// <summary>自测用：把文件重新读一遍（外部还原了文件之后要重载）。</summary>
        public static void Reload() { loaded = false; Ensure(); }

        // ---------------------------------------------------------- 读写

        private static void Load()
        {
            loaded = true;
            cats.Clear(); slotsOfCat.Clear(); modCat.Clear(); modSlot.Clear();
            subOrder.Clear(); posOrder.Clear(); slotSub.Clear();
            try
            {
                string f = FileOf();
                if (File.Exists(f))
                    foreach (string raw in File.ReadAllLines(f, Encoding.UTF8))
                    {
                        string s = raw.TrimEnd();
                        if (s.Length == 0 || s[0] == '#') continue;
                        if (s.StartsWith("cat=", StringComparison.Ordinal))
                        {
                            string v = s.Substring(4).Trim();
                            if (v.Length > 0 && !cats.Contains(v)) cats.Add(v);
                        }
                        else if (s.StartsWith("slot=", StringComparison.Ordinal))
                        {
                            string[] p = s.Substring(5).Split('\t');
                            if (p.Length < 2) continue;
                            string c = p[0].Trim(), nm = p[1].Trim();
                            if (c.Length == 0 || nm.Length == 0) continue;
                            List<string> l = SlotList(c);
                            if (!l.Contains(nm)) l.Add(nm);
                        }
                        else if (s.StartsWith("mod=", StringComparison.Ordinal))
                        {
                            string[] p = s.Substring(4).Split('\t');
                            if (p.Length < 2) continue;
                            string k = p[0].Trim();
                            if (k.Length == 0) continue;
                            string c = p[1].Trim();
                            string sl = p.Length > 2 ? p[2].Trim() : "-";
                            if (c.Length > 0 && c != "-")
                            {
                                modCat[k] = c;
                                if (!cats.Contains(c)) cats.Add(c);   // 手改 ini 漏了 cat= 也不会让 MOD 掉出树
                            }
                            if (sl.Length > 0 && sl != "-") modSlot[k] = sl;
                        }
                        else if (s.StartsWith("suborder=", StringComparison.Ordinal))
                        {
                            string[] p = s.Substring(9).Split('\t');
                            if (p.Length < 2) continue;
                            string c = p[0].Trim(), sb = p[1].Trim();
                            if (c.Length == 0 || sb.Length == 0) continue;
                            List<string> l = OrderList(subOrder, c);
                            if (!l.Contains(sb)) l.Add(sb);
                        }
                        else if (s.StartsWith("posorder=", StringComparison.Ordinal))
                        {
                            string[] p = s.Substring(9).Split('\t');
                            if (p.Length < 3) continue;
                            string c = p[0].Trim(), sb = p[1].Trim(), nm = p[2].Trim();
                            if (c.Length == 0 || nm.Length == 0) continue;
                            List<string> l = OrderList(posOrder, PosKey(c, sb));
                            if (!l.Contains(nm)) l.Add(nm);
                        }
                        else if (s.StartsWith("slotsub=", StringComparison.Ordinal))
                        {
                            string[] p = s.Substring(8).Split('\t');
                            if (p.Length < 3) continue;
                            string c = p[0].Trim(), nm = p[1].Trim(), sb = p[2].Trim();
                            if (c.Length == 0 || nm.Length == 0) continue;
                            slotSub[c + "\u0001" + nm] = sb;
                        }
                    }
            }
            catch { }
            // 缺了的默认大类插到它的默认位置（而不是一律追加在最后）：
            // 老用户配置里没有「近战 / 投掷物 / 弹药升级」，得让它们落在枪械后面，而不是排到喷漆后头
            for (int i = 0; i < DefaultCats.Length; i++)
            {
                string d = DefaultCats[i];
                if (cats.Contains(d)) continue;
                cats.Insert(Math.Min(i, cats.Count), d);
            }
            // 2026-11-09：「武器升级」改叫「弹药升级」。老配置里还写着旧名，
            // 就地换掉并回写一次 —— 不然左栏会同时冒出旧名和默认的新名两个类。
            bool renamed = false;
            int oldCat = cats.IndexOf("武器升级");
            if (oldCat >= 0)
            {
                if (cats.Contains("弹药升级")) { cats.RemoveAt(oldCat); renamed = true; }
                else { cats[oldCat] = "弹药升级"; renamed = true; }
            }
            foreach (string k in new List<string>(modCat.Keys))
                if (modCat[k] == "武器升级") { modCat[k] = "弹药升级"; renamed = true; }
            List<string> oldSlots;
            if (slotsOfCat.TryGetValue("武器升级", out oldSlots))
            {
                slotsOfCat.Remove("武器升级");
                if (!slotsOfCat.ContainsKey("弹药升级")) slotsOfCat["弹药升级"] = oldSlots;
                renamed = true;
            }
            if (renamed) Save();
            Touch();
        }

        public static void Save()
        {
            Ensure();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# 方案树分类：大类顺序 / 每个 MOD 的人工归属（本工具自己写，手改也行）");
            foreach (string c in cats) sb.AppendLine("cat=" + c);
            foreach (KeyValuePair<string, List<string>> kv in slotsOfCat)
                foreach (string s in kv.Value) sb.AppendLine("slot=" + kv.Key + "\t" + s);
            foreach (KeyValuePair<string, string> kv in modCat)
            {
                string sl;
                modSlot.TryGetValue(kv.Key, out sl);
                sb.AppendLine("mod=" + kv.Key + "\t" + kv.Value + "\t" + (string.IsNullOrEmpty(sl) ? "-" : sl));
            }
            foreach (KeyValuePair<string, List<string>> kv in subOrder)
                foreach (string s in kv.Value) sb.AppendLine("suborder=" + kv.Key + "\t" + s);
            foreach (KeyValuePair<string, List<string>> kv in posOrder)
            {
                string c = kv.Key, sub = "";
                int u = kv.Key.IndexOf('\u0001');
                if (u >= 0) { c = kv.Key.Substring(0, u); sub = kv.Key.Substring(u + 1); }
                foreach (string s in kv.Value) sb.AppendLine("posorder=" + c + "\t" + sub + "\t" + s);
            }
            foreach (KeyValuePair<string, string> kv in slotSub)
            {
                int u = kv.Key.IndexOf('\u0001');
                if (u < 0) continue;
                sb.AppendLine("slotsub=" + kv.Key.Substring(0, u) + "\t" + kv.Key.Substring(u + 1) + "\t" + kv.Value);
            }
            try { File.WriteAllText(FileOf(), sb.ToString(), new UTF8Encoding(true)); }
            catch (Exception ex) { Core.Log("WARN", "方案树分类保存失败: " + ex.Message); }
        }

        private static List<string> SlotList(string cat)
        {
            List<string> l;
            if (!slotsOfCat.TryGetValue(cat, out l)) { l = new List<string>(); slotsOfCat[cat] = l; }
            return l;
        }

        private static List<string> OrderList(Dictionary<string, List<string>> map, string key)
        {
            List<string> l;
            if (!map.TryGetValue(key, out l)) { l = new List<string>(); map[key] = l; }
            return l;
        }

        // ---------------------------------------------------------- 查询

        public static List<string> Cats()
        {
            Ensure();
            return new List<string>(cats);
        }

        /// <summary>给 Catalog.Cats 用：整套分类顺序（树按这个走）。</summary>
        public static string[] CatsArray()
        {
            Ensure();
            if (catCache == null || catCacheVer != ver)
            {
                catCache = cats.ToArray();
                catCacheVer = ver;
            }
            return (string[])catCache.Clone();
        }

        private static void Touch() { ver++; }

        public static List<string> Slots(string cat)
        {
            Ensure();
            return cat == null || cat.Length == 0 ? new List<string>() : new List<string>(SlotList(cat));
        }

        /// <summary>人工指定的大类；没指定就返回自动识别的那一类。</summary>
        public static string CatOf(ModItem m, string auto)
        {
            Ensure();
            if (m == null) return auto;
            string v;
            return modCat.TryGetValue(m.Key, out v) ? v : auto;
        }

        /// <summary>人工指定的位置名；没指定返回空串。</summary>
        public static string SlotOf(ModItem m)
        {
            Ensure();
            if (m == null) return "";
            string v;
            return modSlot.TryGetValue(m.Key, out v) ? v : "";
        }

        public static bool HasManual(ModItem m)
        {
            Ensure();
            if (m == null) return false;
            return modCat.ContainsKey(m.Key) || modSlot.ContainsKey(m.Key);
        }

        /// <summary>人工改过大类没有？</summary>
        public static bool HasCat(ModItem m)
        {
            Ensure();
            return m != null && modCat.ContainsKey(m.Key);
        }

        /// <summary>按 Key 直接指定大类（大类改名时批量搬运用）。</summary>
        public static void SetCat(string key, string cat)
        {
            Ensure();
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(cat)) return;
            modCat[key] = cat;
            if (!cats.Contains(cat)) { cats.Add(cat); Touch(); }
        }

        /// <summary>兜底：分类表里没有这一栏就补上，别让 MOD 掉出树。</summary>
        public static void EnsureCat(string cat)
        {
            Ensure();
            if (string.IsNullOrEmpty(cat) || cats.Contains(cat)) return;
            cats.Add(cat);
            Touch();
        }

        /// <summary>位置在这个大类里排第几（没列过的排最后）。</summary>
        public static int SlotRank(string cat, string name)
        {
            Ensure();
            List<string> l;
            if (!slotsOfCat.TryGetValue(cat ?? "", out l)) return 1000;
            int i = l.IndexOf(name);
            return i < 0 ? 1000 : i;
        }

        // ---------------------------------------------------------- 改

        /// <summary>把一个 MOD 归到「大类 + 位置」。slot 传空 = 位置仍用自动识别。</summary>
        public static void Assign(ModGroup g, string cat, string slot)
        {
            Ensure();
            if (g == null) return;
            string c = (cat ?? "").Trim();
            string s = (slot ?? "").Trim();
            foreach (ModItem p in g.Parts)
            {
                if (c.Length > 0)
                {
                    modCat[p.Key] = c;
                    if (!cats.Contains(c)) { cats.Add(c); Touch(); }
                }
                else modCat.Remove(p.Key);
                if (c.Length > 0 && s.Length > 0)
                {
                    modSlot[p.Key] = s;
                    List<string> l = SlotList(c);
                    if (!l.Contains(s)) l.Add(s);
                }
                else modSlot.Remove(p.Key);
            }
            Save();
        }

        /// <summary>回到全自动（自动大类 + 自动位置 + 自动小分类）。</summary>
        public static void Reset(ModGroup g)
        {
            Ensure();
            if (g == null) return;
            foreach (ModItem p in g.Parts)
            {
                string sl;
                modSlot.TryGetValue(p.Key, out sl);
                modCat.Remove(p.Key);
                modSlot.Remove(p.Key);
                // 拖进别的小分类留下的归属也一并清掉，不然「恢复自动」之后它还赖在那一类里
                if (!string.IsNullOrEmpty(sl))
                    foreach (string cat in cats) slotSub.Remove(cat + "\u0001" + sl);
            }
            Save();
        }

        public static void AddCat(string name)
        {
            Ensure();
            name = (name ?? "").Trim();
            if (name.Length == 0 || cats.Contains(name)) return;
            cats.Add(name);
            Touch();
            Save();
        }

        public static void RenameCat(string oldName, string newName)
        {
            Ensure();
            oldName = (oldName ?? "").Trim();
            newName = (newName ?? "").Trim();
            if (oldName.Length == 0 || newName.Length == 0 || oldName == newName) return;
            int i = cats.IndexOf(oldName);
            if (i >= 0)
            {
                if (cats.Contains(newName)) cats.RemoveAt(i);
                else cats[i] = newName;
            }
            List<string> l;
            if (slotsOfCat.TryGetValue(oldName, out l)) { slotsOfCat.Remove(oldName); if (!slotsOfCat.ContainsKey(newName)) slotsOfCat[newName] = l; }
            List<string> keys = new List<string>(modCat.Keys);
            foreach (string k in keys) if (modCat[k] == oldName) modCat[k] = newName;
            Touch();
            Save();
        }

        public static void MoveCat(string name, int delta)
        {
            Ensure();
            int i = cats.IndexOf(name);
            int j = i + delta;
            if (i < 0 || j < 0 || j >= cats.Count) return;
            string t = cats[i]; cats[i] = cats[j]; cats[j] = t;
            Touch();
            Save();
        }

        /// <summary>拖动大类排序：移到 target 前面；after=true 则移到它后面。</summary>
        public static bool MoveCatTo(string name, string target, bool after)
        {
            Ensure();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(target) || name == target) return false;
            int from = cats.IndexOf(name);
            if (from < 0 || cats.IndexOf(target) < 0) return false;
            cats.RemoveAt(from);
            int at = cats.IndexOf(target);
            if (at < 0) { cats.Insert(Math.Min(from, cats.Count), name); return false; }
            cats.Insert(at + (after ? 1 : 0), name);
            Touch();
            Save();
            return true;
        }

        public static void AddSlot(string cat, string name)
        {
            Ensure();
            cat = (cat ?? "").Trim();
            name = (name ?? "").Trim();
            if (cat.Length == 0 || name.Length == 0) return;
            List<string> l = SlotList(cat);
            if (!l.Contains(name)) l.Add(name);
            Save();
        }

        public static void MoveSlot(string cat, string name, int delta)
        {
            Ensure();
            List<string> l = SlotList(cat ?? "");
            int i = l.IndexOf(name), j = i + delta;
            if (i < 0 || j < 0 || j >= l.Count) return;
            string t = l[i]; l[i] = l[j]; l[j] = t;
            Save();
        }

        // ---------------------------------------------------------- 拖动排序（小分类 / 位置）

        /// <summary>这个大类的小分类顺序被手工排过吗。没排过 = 走自动顺序。</summary>
        public static bool HasSubOrder(string cat) { Ensure(); return subOrder.ContainsKey(cat ?? ""); }

        /// <summary>手工小分类顺序；没排过返回 null。</summary>
        public static List<string> SubOrderOf(string cat)
        {
            Ensure();
            List<string> l;
            return subOrder.TryGetValue(cat ?? "", out l) ? new List<string>(l) : null;
        }

        /// <summary>小分类排序键：没手工排过返回 -1（交给自动顺序）；排过但不在表里 = 排到最后。</summary>
        public static int SubRankOf(string cat, string sub)
        {
            Ensure();
            List<string> l;
            if (!subOrder.TryGetValue(cat ?? "", out l)) return -1;
            int i = l.IndexOf(sub ?? "");
            return i < 0 ? 9000 : i;
        }

        /// <summary>这个小分类里的位置顺序被手工排过吗。</summary>
        public static bool HasPosOrder(string cat, string sub) { Ensure(); return posOrder.ContainsKey(PosKey(cat, sub)); }

        /// <summary>手工位置顺序；没排过返回 null。</summary>
        public static List<string> PosOrderOf(string cat, string sub)
        {
            Ensure();
            List<string> l;
            return posOrder.TryGetValue(PosKey(cat, sub), out l) ? new List<string>(l) : null;
        }

        /// <summary>位置排序键：没手工排过返回 -1；排过但不在表里 = 排到最后。</summary>
        public static int PosRankOf(string cat, string sub, string name)
        {
            Ensure();
            List<string> l;
            if (!posOrder.TryGetValue(PosKey(cat, sub), out l)) return -1;
            int i = l.IndexOf(name ?? "");
            return i < 0 ? 9000 : i;
        }

        /// <summary>位置被拖到别的小分类后记在这里；空 = 按自动识别归小分类。</summary>
        public static string SubOverride(string cat, string name)
        {
            Ensure();
            string v;
            return slotSub.TryGetValue((cat ?? "") + "\u0001" + (name ?? ""), out v) ? v : "";
        }

        /// <summary>把当前的小分类顺序落成底稿 —— 第一次在这个大类里拖小分类时调用。</summary>
        public static void SetSubOrder(string cat, List<string> subs)
        {
            Ensure();
            cat = (cat ?? "").Trim();
            if (cat.Length == 0) return;
            subOrder[cat] = new List<string>(subs ?? new List<string>());
            Touch();
            Save();
        }

        /// <summary>小分类换顺序：移到 target 前面；after=true 移到它后面。</summary>
        public static bool MoveSubTo(string cat, string sub, string target, bool after)
        {
            Ensure();
            if (string.IsNullOrEmpty(sub) || string.IsNullOrEmpty(target) || sub == target) return false;
            List<string> l;
            if (!subOrder.TryGetValue(cat ?? "", out l)) return false;
            int from = l.IndexOf(sub);
            if (from < 0 || l.IndexOf(target) < 0) return false;
            l.RemoveAt(from);
            int at = l.IndexOf(target);
            if (at < 0) { l.Insert(Math.Min(from, l.Count), sub); return false; }
            l.Insert(at + (after ? 1 : 0), sub);
            Touch();
            Save();
            return true;
        }

        /// <summary>把某个小分类里的位置顺序落成底稿。</summary>
        public static void SetPosOrder(string cat, string sub, List<string> names)
        {
            Ensure();
            if (string.IsNullOrEmpty(cat)) return;
            posOrder[PosKey(cat, sub)] = new List<string>(names ?? new List<string>());
            Save();
        }

        /// <summary>
        /// 位置换顺序 / 换小分类。target 为空 = 落到目标小分类的开头（after=false）或末尾（after=true）。
        /// 两个小分类的顺序表都必须已经存在（调用方先用 SetPosOrder 落底稿）。
        /// </summary>
        public static bool MovePosTo(string cat, string fromSub, string name, string toSub, string target, bool after)
        {
            Ensure();
            if (string.IsNullOrEmpty(cat) || string.IsNullOrEmpty(name)) return false;
            string fk = PosKey(cat, fromSub), tk = PosKey(cat, toSub);
            List<string> fl, tl;
            if (!posOrder.TryGetValue(tk, out tl)) return false;
            if (fk == tk) fl = tl;
            else if (!posOrder.TryGetValue(fk, out fl)) return false;
            int from = fl.IndexOf(name);
            if (from < 0) return false;
            if (fk == tk && string.Equals(name, target, StringComparison.OrdinalIgnoreCase)) return false;
            fl.RemoveAt(from);
            int at;
            if (string.IsNullOrEmpty(target)) at = after ? tl.Count : 0;
            else
            {
                at = tl.IndexOf(target);
                if (at < 0) { fl.Insert(Math.Min(from, fl.Count), name); return false; }
                if (after) at++;
            }
            tl.Insert(Math.Min(at, tl.Count), name);
            Save();
            return true;
        }

        /// <summary>把某个位置改挂到别的小分类；sub 传空 = 恢复自动识别。</summary>
        public static void SetSlotSub(string cat, string name, string sub)
        {
            Ensure();
            if (string.IsNullOrEmpty(cat) || string.IsNullOrEmpty(name)) return;
            string k = cat + "\u0001" + name;
            if (string.IsNullOrEmpty(sub)) slotSub.Remove(k); else slotSub[k] = sub;
            Save();
        }
    }

    // ============================================================
    //  「设置 MOD 分类」独立窗口
    //  左：大类（顺序就是树里的顺序，可新建 / 改名 / 上移下移）
    //  右：这一类下「替换什么」（可新建 / 排序，也可并到自动认出来的位置）
    //  点左边某一行 = 这个 MOD 归到这一类；点右边某一行 = 它替换那个位置。
    // ============================================================

    public class ModCatForm : Form
    {
        private const string AUTO = "按识别结果自动：{0}";

        private readonly ModGroup g;
        private readonly string autoCat;
        private readonly string autoSlot;
        private readonly ListBox lbCat, lbSlot;
        private readonly Label lblTop, lblTip, lblCatHd, lblSlotHd;
        private List<Slot> snapshot;      // 打开时算一次，切换大类不再重扫

        private string curCat = "";
        private string curSlot = "";
        private bool resetMode;

        public string ResultCat = "";
        public string ResultSlot = "";

        public ModCatForm(ModGroup group)
        {
            g = group;
            autoCat = group.Cat;
            autoSlot = (group.Main != null && group.Main.Subject != null) ? group.Main.Subject.Trim() : "";
            if (IsCountName(autoSlot)) autoSlot = "";        // 「4 件」是上报格式，不是位置名
            curCat = CatPrefs.CatOf(group.Main, group.Cat);
            curSlot = CatPrefs.SlotOf(group.Main);

            Text = "设置 MOD 分类 — " + group.Name;
            ClientSize = new Size(680, 540);
            Font = Theme.UI;
            BackColor = Theme.Bg;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;

            lblTop = new Label();
            lblTop.SetBounds(12, 10, 656, 40);
            lblTop.Font = Theme.UISmall;
            lblTop.ForeColor = Theme.Ink2;
            StringBuilder sb = new StringBuilder();
            sb.Append("「").Append(group.Name).Append("」");
            if (group.IsSet) sb.Append("（").Append(group.Parts.Count).Append(" 个 vpk）");
            sb.AppendLine();
            sb.Append("识别结果：").Append(autoCat);
            if (autoSlot.Length > 0) sb.Append(" · ").Append(autoSlot);
            else sb.Append(" · 位置认不出来");
            sb.Append("　·　左边点一行 = 归到这一类；右边点一行 = 它替换那个位置");
            lblTop.Text = sb.ToString();
            Controls.Add(lblTop);

            lblCatHd = new Label();
            lblCatHd.SetBounds(12, 56, 240, 18);
            lblCatHd.Text = "大类（树里从上到下的顺序）";
            lblCatHd.Font = Theme.UIBold;
            lblCatHd.ForeColor = Theme.Ink;
            Controls.Add(lblCatHd);

            lbCat = new ListBox();
            lbCat.SetBounds(12, 78, 232, 320);
            lbCat.BorderStyle = BorderStyle.FixedSingle;
            lbCat.IntegralHeight = false;
            lbCat.BackColor = Theme.Surface;
            lbCat.ForeColor = Theme.Ink;
            lbCat.Font = Theme.UI;
            Controls.Add(lbCat);

            lblSlotHd = new Label();
            lblSlotHd.SetBounds(262, 56, 300, 18);
            lblSlotHd.Text = "替换什么（这一类的具体位置）";
            lblSlotHd.Font = Theme.UIBold;
            lblSlotHd.ForeColor = Theme.Ink;
            Controls.Add(lblSlotHd);

            lbSlot = new ListBox();
            lbSlot.SetBounds(262, 78, 300, 320);
            lbSlot.BorderStyle = BorderStyle.FixedSingle;
            lbSlot.IntegralHeight = false;
            lbSlot.BackColor = Theme.Surface;
            lbSlot.ForeColor = Theme.Ink;
            lbSlot.Font = Theme.UI;
            Controls.Add(lbSlot);

            Small("新建分类…", 12, 404, 110).Click += delegate { NewCat(); };
            Small("改名…", 128, 404, 60).Click += delegate { RenameCat(); };
            Small("↑", 194, 404, 24).Click += delegate { MoveCat(-1); };
            Small("↓", 222, 404, 22).Click += delegate { MoveCat(1); };

            Small("新建位置…", 262, 404, 110).Click += delegate { NewSlot(); };
            Small("↑", 378, 404, 24).Click += delegate { MoveSlot(-1); };
            Small("↓", 406, 404, 24).Click += delegate { MoveSlot(1); };
            Small("并到上面的位置", 436, 404, 126).Click += delegate { HintSlot(); };

            lblTip = new Label();
            lblTip.SetBounds(12, 436, 656, 54);
            lblTip.Font = Theme.UISmall;
            lblTip.ForeColor = Theme.Ink3;
            lblTip.Text = "分类只有这一套：方案树、搜索都按它走，改一处两处一起变。\r\n"
                        + "新建分类 → 树上会多一层；地图 / 喷漆归「一堆」（它们能叠好几张）。\r\n"
                        + "位置选「自动」= 只改分类，位置仍按识别结果。";
            Controls.Add(lblTip);

            FlatButton b = new FlatButton();
            b.Text = "恢复自动";
            b.SetBounds(12, 496, 96, Theme.H);
            b.Click += delegate { ResetAll(); };
            Controls.Add(b);

            FlatButton ok = new FlatButton();
            ok.Text = "确定"; ok.Primary = true;
            ok.SetBounds(478, 496, 92, Theme.H);
            ok.Click += delegate { Accept(); };
            Controls.Add(ok);

            FlatButton no = new FlatButton();
            no.Text = "取消";
            no.SetBounds(580, 496, 88, Theme.H);
            no.DialogResult = DialogResult.Cancel;
            Controls.Add(no);
            AcceptButton = ok;
            CancelButton = no;

            lbCat.SelectedIndexChanged += delegate
            {
                if (lbCat.SelectedItem == null) return;
                string c = lbCat.SelectedItem.ToString();
                if (c == curCat) return;
                curCat = c;
                curSlot = "";
                resetMode = false;
                FillSlots();
            };
            lbSlot.SelectedIndexChanged += delegate
            {
                if (lbSlot.SelectedItem == null) return;
                string v = lbSlot.SelectedItem.ToString();
                curSlot = v.StartsWith("（", StringComparison.Ordinal) ? "" : v;
                resetMode = false;
                RefreshTip();
            };

            snapshot = SlotIndex.Build();
            FillCats();
            FillSlots();
        }

        /// <summary>「4 件」「12 件」这种是「一包改了好几样」的上报格式，不是位置名。</summary>
        private static bool IsCountName(string s)
        {
            if (s == null || s.Length < 2 || s[s.Length - 1] != '件') return false;
            for (int i = 0; i < s.Length - 1; i++)
                if (!char.IsDigit(s[i]) && s[i] != ' ') return false;
            return true;
        }

        private FlatButton Small(string text, int x, int y, int w)        {
            FlatButton b = new FlatButton();
            b.Text = text;
            b.SetBounds(x, y, w, Theme.H);
            Controls.Add(b);
            return b;
        }

        // ---------------------------------------------------------- 列表

        private void FillCats()
        {
            lbCat.BeginUpdate();
            lbCat.Items.Clear();
            foreach (string c in CatPrefs.Cats()) lbCat.Items.Add(c);
            lbCat.EndUpdate();
            int i = lbCat.Items.IndexOf(curCat);
            if (i < 0 && lbCat.Items.Count > 0) i = 0;
            if (i >= 0) { lbCat.SelectedIndex = i; curCat = lbCat.Items[i].ToString(); }
            RefreshTip();
        }

        private void FillSlots()
        {
            string keep = curSlot;
            lbSlot.BeginUpdate();
            lbSlot.Items.Clear();
            lbSlot.Items.Add(string.Format(AUTO, autoSlot.Length > 0 ? autoSlot : "认不出来"));
            List<string> names = new List<string>(CatPrefs.Slots(curCat));
            // 这一类里已经认出来的位置也列上：新 MOD 可以并到现成的位置，而不是又开一个。
            // 顺序跟方案树里一致（步枪 → AK-47 → M16 → …），不然这里是乱序，找位置很费眼。
            List<Slot> mine = new List<Slot>();
            if (snapshot != null)
                foreach (Slot s in snapshot) if (s.Cat == curCat) mine.Add(s);
            mine.Sort(delegate(Slot x, Slot y)
            {
                int a = SlotIndex.SubRank(x.Sub), b = SlotIndex.SubRank(y.Sub);
                if (a != b) return a.CompareTo(b);
                int c = SlotIndex.NameRank(x.Sub, x.Name), d = SlotIndex.NameRank(y.Sub, y.Name);
                if (c != d) return c.CompareTo(d);
                return string.Compare(x.Name, y.Name, StringComparison.CurrentCulture);
            });
            foreach (Slot s in mine) if (!names.Contains(s.Name)) names.Add(s.Name);
            foreach (string n in names) lbSlot.Items.Add(n);
            lbSlot.EndUpdate();
            int i = keep.Length > 0 ? lbSlot.Items.IndexOf(keep) : 0;
            lbSlot.SelectedIndex = i >= 0 ? i : 0;
            RefreshTip();
        }

        private void RefreshTip()
        {
            string slot = curSlot.Length > 0 ? curSlot : "自动（" + (autoSlot.Length > 0 ? autoSlot : "认不出来") + "）";
            lblSlotHd.Text = "替换什么 · " + curCat;
            lblTip.Text = "这个 MOD：" + (resetMode ? "恢复全自动" : curCat + " · " + slot) + "\r\n"
                        + "分类只有这一套：方案树 / 搜索一起变；新建分类树上会多一层。\r\n"
                        + "位置决定它和谁「只能留一个」，同名的位置会并成一行。";
        }

        // ---------------------------------------------------------- 大类操作

        private void NewCat()
        {
            string n = PromptForm.Ask("新分类的名字。加在树的末尾，再用 ↑ ↓ 调位置。", "新建分类");
            if (n == null) return;
            n = n.Trim();
            if (n.Length == 0) return;
            CatPrefs.AddCat(n);
            curCat = n; curSlot = ""; resetMode = false;
            FillCats();
            FillSlots();
            int i = lbCat.Items.IndexOf(n);
            if (i >= 0) lbCat.SelectedIndex = i;
        }

        private void RenameCat()
        {
            if (lbCat.SelectedItem == null) return;
            string old = lbCat.SelectedItem.ToString();
            string n = PromptForm.Ask("改名后，归在这一类的 MOD 一起跟着走。", "重命名分类「" + old + "」", old);
            if (n == null) return;
            n = n.Trim();
            if (n.Length == 0 || n == old) return;
            CatPrefs.RenameCat(old, n);
            Catalog.AdoptCatRename(old, n);      // 原来在这一类的 MOD 跟着搬，不然会掉出树
            curCat = n;
            FillCats();
            FillSlots();
            int i = lbCat.Items.IndexOf(n);
            if (i >= 0) lbCat.SelectedIndex = i;
        }

        private void MoveCat(int delta)
        {
            if (lbCat.SelectedItem == null) return;
            string n = lbCat.SelectedItem.ToString();
            CatPrefs.MoveCat(n, delta);
            FillCats();
            int i = lbCat.Items.IndexOf(n);
            if (i >= 0) lbCat.SelectedIndex = i;
        }

        private void NewSlot()
        {
            string n = PromptForm.Ask("新位置的名字，写被替换的那件东西（例：AK-47 / 医疗包 / 教练）。\r\n"
                                     + "填成现成的名字就会并到那一行。", "在「" + curCat + "」下新建位置");
            if (n == null) return;
            n = n.Trim();
            if (n.Length == 0) return;
            CatPrefs.AddSlot(curCat, n);
            curSlot = n; resetMode = false;
            FillSlots();
            int i = lbSlot.Items.IndexOf(n);
            if (i >= 0) lbSlot.SelectedIndex = i;
        }

        private void MoveSlot(int delta)
        {
            if (lbSlot.SelectedItem == null) return;
            string n = lbSlot.SelectedItem.ToString();
            if (n.StartsWith("（", StringComparison.Ordinal)) return;   // 自动那一行不参与排序
            CatPrefs.MoveSlot(curCat, n, delta);
            FillSlots();
            int i = lbSlot.Items.IndexOf(n);
            if (i >= 0) lbSlot.SelectedIndex = i;
        }

        private void HintSlot()
        {
            MessageBox.Show("右边列表里排在下面是这一类「已经认出来的位置」，点谁就和谁并成一行。\r\n"
                          + "想并的名字不在列表里，就用「新建位置…」直接写那个名字。", "怎么并");
        }

        private void ResetAll()
        {
            resetMode = true;
            curCat = autoCat;
            curSlot = "";
            FillCats();
            FillSlots();
            RefreshTip();
        }

        private void Accept()
        {
            ResultCat = curCat;
            ResultSlot = curSlot;
            if (resetMode) CatPrefs.Reset(g);
            else CatPrefs.Assign(g, curCat, curSlot);
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}

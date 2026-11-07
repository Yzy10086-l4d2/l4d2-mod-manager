using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;

namespace Installer
{
    /// <summary>
    /// 桌面（3D）上一张卡里的一行。要么是小分类分隔行，要么是一个位置 / 一个 MOD。
    /// 卡片本身不画，只描述内容：真正画的人在浏览器里（desk\index.html）。
    /// </summary>
    internal sealed class DeskRow
    {
        public bool IsSub;          // 小分类分隔行（步枪 / 霰弹枪…），不可点
        public string Sub;          // 分隔行文字，或者这一行所属的小分类
        public Slot Slot;           // 位置行
        public ModGroup Group;      // 分支卡 / 未归类卡的行
    }

    internal sealed class DeskCard
    {
        public string Key;          // cat|枪械 / mine|音效 / misc|unmatched
        public string Title;
        public string Cat;
        public bool Branch;         // 「我的分支」里的一个分支
        public string BranchName;
        public bool NewBranch;      // ＋新建分支
        public bool Note;           // 未归类
        public readonly List<DeskRow> Rows = new List<DeskRow>();
    }

    /// <summary>
    /// 把方案树的数据整理成桌面上的一叠卡。
    /// 这里只负责「一张卡上有什么」，摆在哪、画成什么样都归前端。
    /// </summary>
    internal static class DeskModel
    {
        public static List<DeskCard> Build(CatTree tree)
        {
            List<DeskCard> cards = new List<DeskCard>();
            if (tree == null) return cards;
            List<Slot> slots = tree.Slots;
            if (slots == null) return cards;

            Dictionary<string, List<Slot>> byCat = new Dictionary<string, List<Slot>>(StringComparer.Ordinal);
            foreach (Slot s in slots)
            {
                if (s == null) continue;
                string cat = string.IsNullOrEmpty(s.Cat) ? "杂项" : s.Cat;
                List<Slot> list;
                if (!byCat.TryGetValue(cat, out list)) { list = new List<Slot>(); byCat[cat] = list; }
                list.Add(s);
            }

            List<string> order = SlotIndex.CatOrder(slots);
            foreach (string cat in order)
            {
                List<Slot> list;
                if (!byCat.TryGetValue(cat, out list) || list.Count == 0) continue;
                if (string.Equals(cat, "我的分支", StringComparison.Ordinal))
                {
                    BuildBranchCards(cards, list);
                    continue;
                }
                DeskCard c = new DeskCard();
                c.Key = "cat|" + cat;
                c.Title = cat;
                c.Cat = cat;
                BuildSlotRows(c, list);
                cards.Add(c);
            }

            bool hasNew = false;
            foreach (DeskCard c in cards) if (c.NewBranch) hasNew = true;
            if (!hasNew)
            {
                DeskCard n = new DeskCard();
                n.Key = "mine|+new";
                n.Title = "＋ 新建分支";
                n.Cat = "我的分支";
                n.NewBranch = true;
                cards.Add(n);
            }

            // 认不出改的是什么的那批，方案树里有一行入口，桌面上也得有
            if (tree.Unmatched > 0)
            {
                DeskCard u = new DeskCard();
                u.Key = "misc|unmatched";
                u.Title = "未归类";
                u.Cat = "未归类";
                u.Note = true;
                foreach (ModGroup g in tree.UnmatchedGroups)
                {
                    DeskRow r = new DeskRow();
                    r.Group = g;
                    u.Rows.Add(r);
                }
                cards.Add(u);
            }

            return cards;
        }

        private static void BuildBranchCards(List<DeskCard> cards, List<Slot> mineSlots)
        {
            foreach (Slot s in mineSlots)
            {
                if (s.Key != null && s.Key == "#mine+new") continue;
                DeskCard c = new DeskCard();
                c.Key = "mine|" + s.Name;
                c.Title = s.Name;
                c.Cat = "我的分支";
                c.Branch = true;
                c.BranchName = s.Name;
                foreach (ModGroup g in s.Items)
                {
                    DeskRow r = new DeskRow();
                    r.Group = g;
                    c.Rows.Add(r);
                }
                cards.Add(c);
            }
            DeskCard add = new DeskCard();
            add.Key = "mine|+new";
            add.Title = "＋ 新建分支";
            add.Cat = "我的分支";
            add.NewBranch = true;
            cards.Add(add);
        }

        private static void BuildSlotRows(DeskCard c, List<Slot> list)
        {
            string sub = null;
            foreach (Slot s in list)
            {
                string cur = string.IsNullOrEmpty(s.Sub) ? "" : s.Sub;
                if (sub == null || !string.Equals(cur, sub, StringComparison.Ordinal))
                {
                    sub = cur;
                    if (cur.Length > 0)
                    {
                        DeskRow h = new DeskRow();
                        h.IsSub = true;
                        h.Sub = cur;
                        c.Rows.Add(h);
                    }
                }
                DeskRow r = new DeskRow();
                r.Slot = s;
                r.Sub = sub;
                c.Rows.Add(r);
            }
        }

        /// <summary>一行对应的 MOD 组合（位置行先看当前选中的，分支行按名字回查）。</summary>
        public static ModGroup GroupOf(DeskRow row, CatTree tree)
        {
            if (row == null) return null;
            if (row.Group != null) return row.Group;
            if (row.Slot != null)
            {
                ModGroup g = tree == null ? null : tree.ChosenOf(row.Slot);
                if (g == null && row.Slot.Items.Count > 0) g = row.Slot.Items[0];
                return g;
            }
            return null;
        }

        public static string StatusText(ModGroup g, bool local)
        {
            if (g == null) return local ? "关闭" : "未装";
            Color ink, bg;
            string s = MainForm.StatusOf(g, out ink, out bg);
            // 卡片窄，状态写全会被截成「部分已…」
            if (!string.IsNullOrEmpty(s) && s.StartsWith("部分已装", StringComparison.Ordinal))
                s = "部分" + s.Substring(4).Trim();
            return s ?? "";
        }

        // ---------------------------------------------------------------- JSON

        public static string ToJson(List<DeskCard> cards, CatTree tree, MainForm mf)
        {
            int mods = 0, sel = 0, miss = 0;
            StringBuilder sb = new StringBuilder(64 * 1024);
            sb.Append("{\"mode\":\"").Append(Core.IsLocal ? "local" : "usb").Append("\"");
            sb.Append(",\"version\":\"").Append(Esc(Ver())).Append("\"");
            sb.Append(",\"cards\":[");

            for (int ci = 0; ci < cards.Count; ci++)
            {
                DeskCard c = cards[ci];
                if (ci > 0) sb.Append(',');

                int total = 0, picked = 0, inst = 0;
                StringBuilder rows = new StringBuilder();
                for (int ri = 0; ri < c.Rows.Count; ri++)
                {
                    DeskRow r = c.Rows[ri];
                    if (ri > 0) rows.Append(',');
                    if (r.IsSub)
                    {
                        rows.Append("{\"sub\":true,\"name\":\"").Append(Esc(r.Sub)).Append("\"}");
                        continue;
                    }
                    ModGroup g = GroupOf(r, tree);
                    bool on = g != null && mf != null && mf.CountChecked(g) > 0;
                    string st = StatusText(g, Core.IsLocal);
                    total++;
                    if (on) picked++;
                    if (!string.IsNullOrEmpty(st) && !st.StartsWith("未装", StringComparison.Ordinal)
                        && !st.StartsWith("关闭", StringComparison.Ordinal)) inst++;
                    mods++;
                    if (on) sel++;
                    if (st.StartsWith("未装", StringComparison.Ordinal)) miss++;

                    rows.Append("{\"name\":\"").Append(Esc(g != null ? g.Name : "—")).Append("\"");
                    rows.Append(",\"status\":\"").Append(Esc(st)).Append("\"");
                    rows.Append(",\"checked\":").Append(on ? "true" : "false");
                    rows.Append(",\"sub\":\"").Append(Esc(r.Sub)).Append("\"");
                    rows.Append('}');
                }

                sb.Append("{\"key\":\"").Append(Esc(c.Key)).Append("\"");
                sb.Append(",\"title\":\"").Append(Esc(c.Title)).Append("\"");
                sb.Append(",\"cat\":\"").Append(Esc(c.Cat)).Append("\"");
                sb.Append(",\"kind\":\"").Append(c.NewBranch ? "new" : (c.Note ? "note" : (c.Branch ? "branch" : "cat"))).Append("\"");
                sb.Append(",\"picked\":").Append(picked);
                sb.Append(",\"summary\":\"").Append(Esc(summaryOf(c, total, inst, picked))).Append("\"");
                sb.Append(",\"count\":\"").Append(Esc(total > 0 ? "共 " + total + " 个" : "")).Append("\"");
                sb.Append(",\"hint\":\"").Append(Esc(picked > 0 ? "已选 " + picked : "")).Append("\"");
                sb.Append(",\"rows\":[").Append(rows).Append("]}");
            }

            sb.Append("],\"stats\":{\"mods\":").Append(mods)
              .Append(",\"selected\":").Append(sel)
              .Append(",\"missing\":").Append(miss)
              .Append(",\"slots\":").Append(tree == null ? 0 : tree.SlotCount)
              .Append(",\"slotsel\":").Append(tree == null ? 0 : tree.SlotCheckedCount)
              .Append(",\"unmatched\":").Append(tree == null ? 0 : tree.Unmatched)
              .Append("}}");
            return sb.ToString();
        }

        private static string Ver()
        {
            Version v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "" : v.ToString(3);
        }

        private static string summaryOf(DeskCard c, int total, int inst, int picked)
        {
            if (c.NewBranch) return "空";
            if (c.Note) return total + " 个认不出改的是什么";
            if (total == 0) return "这一格是空的";
            return total + " 个 · 已装 " + inst;
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder b = new StringBuilder(s.Length + 8);
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '"': b.Append("\\\""); break;
                    case '\\': b.Append("\\\\"); break;
                    case '\n': b.Append("\\n"); break;
                    case '\r': b.Append("\\r"); break;
                    case '\t': b.Append("\\t"); break;
                    default:
                        if (ch < 32) b.Append(' ');
                        else b.Append(ch);
                        break;
                }
            }
            return b.ToString();
        }
    }

    /// <summary>
    /// 桌面上的摆位。存在 扩展平台\桌面布局.ini，按卡片的 key 记，换机跟着 U 盘走。
    /// </summary>
    internal static class DeskLayout
    {
        private static readonly Dictionary<string, PointF> map =
            new Dictionary<string, PointF>(StringComparer.OrdinalIgnoreCase);
        private static bool loaded;

        public static string FileOf()
        {
            return Path.Combine(Core.ModRoot, "桌面布局.ini");
        }

        private static void Ensure()
        {
            if (loaded) return;
            loaded = true;
            map.Clear();
            try
            {
                string f = FileOf();
                if (!File.Exists(f)) return;
                foreach (string raw in File.ReadAllLines(f, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] p = line.Split('\t');
                    if (p.Length < 3) continue;
                    float x, z;
                    if (!float.TryParse(p[1], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out x)) continue;
                    if (!float.TryParse(p[2], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out z)) continue;
                    map[p[0]] = new PointF(x, z);
                }
            }
            catch (Exception ex) { Core.Log("WARN", "读桌面布局失败：" + ex.Message); }
        }

        public static bool TryGet(string key, out PointF pt)
        {
            Ensure();
            return map.TryGetValue(key ?? "", out pt);
        }

        public static void Put(string key, float x, float z)
        {
            Ensure();
            if (string.IsNullOrEmpty(key)) return;
            map[key] = new PointF(x, z);
            Save();
        }

        public static void Clear()
        {
            Ensure();
            map.Clear();
            Save();
        }

        public static void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# 桌面卡片摆位：key<TAB>横向<TAB>前后（单位：米）");
                foreach (KeyValuePair<string, PointF> kv in map)
                    sb.AppendLine(kv.Key + "\t"
                        + kv.Value.X.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "\t"
                        + kv.Value.Y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                File.WriteAllText(FileOf(), sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Core.Log("WARN", "写桌面布局失败：" + ex.Message); }
        }
    }
}

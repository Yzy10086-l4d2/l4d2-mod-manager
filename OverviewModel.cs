using System;
using System.Collections.Generic;

namespace Installer
{
    // ============================================================
    //  总览：只回答一个问题 —— 当前这套里，每个位置装的是什么？
    //
    //  和方案树的区别：方案树能改（挑候选、勾选、拖分类），这里只能看。
    //  所以这里不建「候选」这一层，也不带勾选框 —— 一个位置一行，右边写它现在装的是哪个。
    // ============================================================

    /// <summary>总览里的一行：一个位置，以及这一格现在装的是什么。</summary>
    internal sealed class OvSlot
    {
        public Slot Slot;
        public string Cat = "";
        public string Sub = "";              // 小分类：步枪 / 霰弹枪 …（明细里当分隔标题）
        public string Name = "";

        public readonly List<ModGroup> On = new List<ModGroup>();     // 勾着的本体（Pile 位置可能好几个）
        public readonly List<ModGroup> Extra = new List<ModGroup>();  // 勾着的叠加扩展（音效 / 第一人称 / HUD）

        public bool Empty;                   // 库里压根没有这个位置的 MOD
        public bool Pile;                    // 能同时装好几个（地图 / 自建分支）
        public int Parts;                    // 勾中的 vpk 个数
        public int AllParts;                 // 这些套装一共几个 vpk
        public long Size;                    // 勾中的 vpk 合计字节
        public bool Partial;                 // 套装只勾了一部分 vpk

        public string ModText = "";          // 右侧那行字

        // 排版产物（OvLayout 写，画布读）
        public float Y, H;

        public bool HasMod { get { return On.Count > 0 || Extra.Count > 0; } }
    }

    /// <summary>左栏的一个大类。</summary>
    internal sealed class OvCat
    {
        public string Title = "";
        public readonly List<OvSlot> Slots = new List<OvSlot>();
        public int On, Total;                // 这个类里有几个位置装了 MOD / 一共几个位置

        // 排版产物
        public float Y, H;

        public bool Empty { get { return Slots.Count == 0; } }
    }

    /// <summary>明细里的小分类标题。</summary>
    internal sealed class OvHead
    {
        public string Text = "";
        public float Y;
    }

    /// <summary>整套的账：几个位置、几个 vpk、多大。</summary>
    internal sealed class OvModel
    {
        public string Profile = "";          // 当前下拉里的方案名；空 = 没套方案，看的就是当前勾选
        public int DiffAdd, DiffDel;         // 勾选和方案对不上：多了几个、少了几个

        public int SlotsOn, SlotsTotal, Parts;
        public long Size;

        public readonly List<OvCat> Cats = new List<OvCat>();
        public readonly List<OvHead> Heads = new List<OvHead>();   // 每次排版重填

        public static OvModel Build(List<Slot> slots, Func<ModGroup, int> counted,
                                    Func<ModItem, bool> partOn, string profile, int diffAdd, int diffDel)
        {
            OvModel m = new OvModel();
            m.Profile = profile == null ? "" : profile;
            m.DiffAdd = diffAdd;
            m.DiffDel = diffDel;
            if (slots == null) return m;

            // 大类顺序跟方案树同一份（CatOrder），两边永远对得上
            Dictionary<string, OvCat> map = new Dictionary<string, OvCat>(StringComparer.Ordinal);
            foreach (string c in SlotIndex.CatOrder(slots))
            {
                OvCat oc = new OvCat();
                oc.Title = c;
                map[c] = oc;
                m.Cats.Add(oc);
            }

            foreach (Slot s in slots)
            {
                if (s == null) continue;
                string cat = string.IsNullOrEmpty(s.Cat) ? "杂项" : s.Cat;
                OvCat oc;
                if (!map.TryGetValue(cat, out oc))
                {
                    oc = new OvCat();
                    oc.Title = cat;
                    map[cat] = oc;
                    m.Cats.Add(oc);
                }

                OvSlot o = new OvSlot();
                o.Slot = s;
                o.Cat = cat;
                o.Sub = string.IsNullOrEmpty(s.Sub) ? "其他" : s.Sub;
                o.Name = s.Name;
                o.Empty = s.Empty;
                o.Pile = s.Pile;

                foreach (ModGroup g in s.Items) if (counted(g) > 0) o.On.Add(g);
                foreach (ModGroup g in s.Extras) if (counted(g) > 0) o.Extra.Add(g);
                foreach (ModGroup g in o.On) AddParts(o, g, counted, partOn);
                foreach (ModGroup g in o.Extra) AddParts(o, g, counted, partOn);
                o.ModText = ModTextOf(o);

                oc.Slots.Add(o);
                oc.Total++;
                if (o.HasMod) oc.On++;

                m.SlotsTotal++;
                m.Parts += o.Parts;
                m.Size += o.Size;
                if (o.HasMod) m.SlotsOn++;
            }

            // 分类表里有、但一个位置都没有的类（比如新加还没归类的）不占左栏
            m.Cats.RemoveAll(delegate(OvCat c) { return c.Slots.Count == 0; });
            return m;
        }

        private static void AddParts(OvSlot o, ModGroup g, Func<ModGroup, int> counted, Func<ModItem, bool> partOn)
        {
            int n = counted(g);
            if (n <= 0) return;
            o.Parts += n;
            o.AllParts += g.Parts.Count;
            if (n < g.Parts.Count) o.Partial = true;
            foreach (ModItem p in g.Parts) if (partOn(p)) o.Size += p.Size;
        }

        /// <summary>右侧那行字：装了哪个 / 原版 / 库里还没有。</summary>
        private static string ModTextOf(OvSlot o)
        {
            if (o.On.Count == 0 && o.Extra.Count == 0)
                return o.Empty ? "库里还没有" : "原版";

            if (o.On.Count == 0)                              // 只勾了叠加扩展
            {
                string e = "扩展：" + o.Extra[0].Name;
                if (o.Extra.Count > 1) e += " +" + (o.Extra.Count - 1);
                return e;
            }

            string t = o.On[0].Name;
            if (o.On.Count > 1) t += " +" + (o.On.Count - 1);  // Pile 位置：地图 / 分支能同时开好几个
            if (o.Extra.Count > 0) t += " ＋扩展" + o.Extra.Count;
            return t;
        }

        /// <summary>自检 / 导出用：摊成文本，核对数字和归属。</summary>
        public string Dump()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("方案：").Append(Profile.Length == 0 ? "（当前勾选）" : Profile).Append("\r\n");
            sb.Append("位置 ").Append(SlotsOn).Append(" / ").Append(SlotsTotal)
              .Append("，vpk ").Append(Parts).Append("，大小 ").Append(Core.Human(Size)).Append("\r\n");
            foreach (OvCat c in Cats)
            {
                sb.Append("\r\n【").Append(c.Title).Append("】 ").Append(c.On).Append("/").Append(c.Total).Append("\r\n");
                string prev = null;
                foreach (OvSlot s in c.Slots)
                {
                    if (s.Sub != prev) { sb.Append("  〈").Append(s.Sub).Append("〉\r\n"); prev = s.Sub; }
                    sb.Append("    ").Append(s.Name).Append("  →  ").Append(s.ModText);
                    if (s.Partial) sb.Append("（部分 vpk）");
                    if (s.Size > 0) sb.Append("  ").Append(Core.Human(s.Size));
                    sb.Append("\r\n");
                }
            }
            return sb.ToString();
        }
    }
}

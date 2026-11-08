using System;
using System.Collections.Generic;
using System.Drawing;

namespace Installer
{
    /// <summary>
    /// 总览的排版：只算坐标，不画。左栏是大类，右栏是选中大类的明细。
    /// 两栏各自滚动：左栏 11 个类一般在窗口里放得下，明细长的（枪械 19 行）要滚。
    /// </summary>
    internal static class OvLayout
    {
        public const float SidePad = 34f;
        public const float HeadH = 112f;         // 顶上：标题 + 一句汇总
        public const float FootH = 38f;          // 底下：一行操作提示
        public const float CapH = 26f;           // 内容区顶部那行小标题（大类 · 11 / 枪械 · 19 个位置）
        public const float LeftW = 250f;
        public const float ColGap = 28f;

        public const float CatH = 42f, CatGap = 7f;
        public const float RowH = 37f, RowGap = 3f;
        public const float SubH = 25f;           // 小分类标题占的高度

        // ---- Arrange 的输出 ----
        public static float ListX, ListY, ListW, ListBottom;
        public static float DetailX, DetailY, DetailW, DetailBottom;
        public static float ListContentH, DetailContentH;

        public static void Arrange(OvModel m, int sel, Size view, float scrollList, float scrollDetail)
        {
            float w = Math.Max(560f, view.Width);
            float h = Math.Max(200f, view.Height);

            ListX = SidePad;
            ListW = Math.Min(LeftW, Math.Max(180f, w * 0.3f));
            ListY = HeadH;
            ListBottom = Math.Max(ListY + 80f, h - FootH);

            DetailX = ListX + ListW + ColGap;
            DetailW = Math.Max(240f, w - DetailX - SidePad);
            DetailY = ListY;
            DetailBottom = ListBottom;

            // ---- 左栏：大类 ----
            float y = ListY + CapH - scrollList;
            foreach (OvCat c in m.Cats)
            {
                c.Y = y;
                c.H = CatH;
                y += CatH + CatGap;
            }
            ListContentH = m.Cats.Count == 0 ? 0f : (y + scrollList) - ListY - CatGap - CapH;

            // ---- 右栏：选中大类的明细 ----
            m.Heads.Clear();
            OvCat cat = CatAt(m, sel);
            float yy = DetailY + CapH - scrollDetail;
            if (cat != null)
            {
                bool multiSub = HasMultipleSubs(cat);
                string prev = null;
                foreach (OvSlot s in cat.Slots)
                {
                    if (multiSub && s.Sub != prev)
                    {
                        OvHead hd = new OvHead();
                        hd.Text = s.Sub;
                        hd.Y = yy;
                        m.Heads.Add(hd);
                        yy += SubH;
                        prev = s.Sub;
                    }
                    s.Y = yy;
                    s.H = RowH;
                    yy += RowH + RowGap;
                }
            }
            DetailContentH = cat == null ? 0f : Math.Max(0f, (yy + scrollDetail) - DetailY - RowGap - CapH);
        }

        public static OvCat CatAt(OvModel m, int sel)
        {
            if (m == null || m.Cats.Count == 0) return null;
            if (sel < 0) sel = 0;
            if (sel >= m.Cats.Count) sel = m.Cats.Count - 1;
            return m.Cats[sel];
        }

        private static bool HasMultipleSubs(OvCat c)
        {
            string first = null;
            foreach (OvSlot s in c.Slots)
            {
                if (first == null) { first = s.Sub; continue; }
                if (s.Sub != first) return true;
            }
            return false;
        }

        /// <summary>明细最多能滚到哪儿（负值当 0）。</summary>
        public static float MaxScrollDetail(Size view)
        {
            float viewH = Math.Max(60f, DetailBottom - DetailY);
            return Math.Max(0f, DetailContentH - viewH);
        }

        public static float MaxScrollList(Size view)
        {
            float viewH = Math.Max(60f, ListBottom - ListY);
            return Math.Max(0f, ListContentH - viewH);
        }
    }
}

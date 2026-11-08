using System;
using System.Collections.Generic;
using System.Drawing;

namespace Installer
{
    /// <summary>
    /// 科技树的排版：只算坐标，不画。窗口变宽变窄都从这里过，
    /// 画的人（TreeCanvas / 白模）拿到的永远是一组矩形。
    ///
    /// 只有「一条路径」是展开的：根 → 选中的大类 → 它的位置 → 选中位置的候选。
    /// 其余大类仍然排好位（好切换），但画布不画它们的下级。
    /// </summary>
    internal static class TreeLayout
    {
        // 窗口宽度分档的基准（逻辑像素）
        public const float SidePad = 36f;      // 左右留白
        public const float TopPad = 62f;       // 顶上给列标题留的位置
        public const float BottomPad = 44f;      // 底下那行操作提示的位置也要留出来

        public const float CatH = 44f, CatGap = 10f;
        public const float SlotH = 26f, SlotGap = 6f;
        public const float GroupCap = 22f, GroupGap = 10f;
        public const float CandH = 46f, CandGap = 9f;

        // ---- Arrange 的输出：画布拿去算滚动、命中、悬浮提示 ----
        public static bool HasRoot;                     // 太窄就不画根节点那一列
        public static float RootX, RootW;
        public static float CatX, CatW;
        public static float SlotX, SlotW;
        public static float CandX, CandW;
        public static float ViewTop, ViewBottom;
        public static float SlotContentH;               // 选中大类的位置列总高
        public static float CandContentH;               // 选中位置的候选列总高

        public static void Arrange(TreeNode root, TreeNode selCat, TreeNode selSlot,
                                   Size view, float scrollSlot, float scrollCand)
        {
            float availW = Math.Max(260f, view.Width - SidePad * 2f);

            // 四列的自然宽度（根 / 大类 / 位置 / 候选）
            float wRoot = 128f, wCat = 190f, wSlot = 300f, wCand = 440f;
            float g1 = 46f, g2 = 52f, g3 = 58f;
            const float need4 = 128f + 190f + 300f + 440f + 46f + 52f + 58f;   // 1214
            const float need3 = 190f + 300f + 440f + 52f + 58f;                 // 1040

            HasRoot = availW >= need4;
            if (HasRoot)
            {
                // 宽屏：多出来的全给候选列，长名字少截几个字
                wCand += Math.Min(availW - need4 - 40f, 240f);
            }
            else
            {
                float k = Math.Min(1f, availW / need3);
                k = Math.Max(k, 0.72f);
                wCat *= k; wSlot *= k; wCand *= k; g2 *= k; g3 *= k;
                float total0 = wCat + wSlot + wCand + g2 + g3;
                if (total0 > availW)
                    wCand = Math.Max(210f, wCand - (total0 - availW));
            }

            float total = (HasRoot ? wRoot + g1 : 0f) + wCat + wSlot + wCand + g2 + g3;
            float x = SidePad + Math.Max(0f, (availW - total) * 0.5f);

            if (HasRoot)
            {
                root.X = x; root.W = wRoot; x += wRoot + g1;
            }
            CatX = x; CatW = wCat; x += wCat + g2;
            SlotX = x; SlotW = wSlot; x += wSlot + g3;
            CandX = x; CandW = wCand;

            float top = TopPad;
            float bottom = view.Height - BottomPad;
            if (bottom - top < 120f) bottom = top + 120f;
            ViewTop = top; ViewBottom = bottom;
            float viewH = bottom - top;
            float midY = (top + bottom) * 0.5f;

            // ---- 根 ----
            root.H = 78f;
            root.Y = midY - root.H * 0.5f;

            // ---- 大类：也按窗口高度压行高（11 个 × 44px 在最小窗口里放不下），
            // 压到 0.72 还不够就以选中的那个为中心，不能让它被切在窗口外 ----
            int n1 = root.Kids.Count;
            float natural1 = n1 * CatH + Math.Max(0, n1 - 1) * CatGap;
            float k1 = natural1 > viewH ? Math.Max(0.72f, viewH / natural1) : 1f;
            float ch = CatH * k1, cg = CatGap * k1;
            float h1 = n1 * ch + Math.Max(0, n1 - 1) * cg;

            float y1 = midY - h1 * 0.5f;
            if (h1 > viewH && n1 > 0)
            {
                int si = root.Kids.IndexOf(selCat);
                if (si < 0) si = 0;
                float want = top + si * (ch + cg) + ch * 0.5f;   // 选中那个在 top 坐标系下的位置
                y1 = top + (midY - want);
                if (y1 > top) y1 = top;
                if (y1 + h1 < bottom) y1 = bottom - h1;
            }
            if (y1 < top) y1 = top;

            for (int i = 0; i < n1; i++)
            {
                TreeNode c = root.Kids[i];
                c.X = CatX; c.W = CatW; c.H = ch;
                c.Y = y1 + i * (ch + cg);
            }

            // ---- 位置：先按 0 起点排出自然高度，再决定这一列从哪儿开始 ----
            // 19 个位置 + 6 个分组在 748px 里放不下，直接滚会让最后一个位置贴着窗口边被切掉。
            // 所以先试着把行高压一点（最多压到 0.85），压完还放不下才真滚动。
            foreach (TreeNode c in root.Kids)
            {
                int n = c.Kids.Count;
                if (n == 0) { continue; }

                int groups = 0;
                string lastSub = null;
                for (int i = 0; i < n; i++)
                {
                    string sub = c.Kids[i].Sub;
                    if (sub != lastSub && !string.IsNullOrEmpty(sub)) { groups++; lastSub = sub; }
                }

                float natural = n * SlotH + Math.Max(0, n - 1) * SlotGap
                                + Math.Max(0, groups) * GroupCap
                                + Math.Max(0, groups - 1) * GroupGap;
                float k = natural > viewH ? Math.Max(0.80f, viewH / natural) : 1f;
                float sh = SlotH * k, sg = SlotGap * k, gc = GroupCap * k, gg = GroupGap * k;

                string prev = null;
                float yy = 0f;
                foreach (TreeNode s in c.Kids)
                {
                    if (s.Sub != prev && !string.IsNullOrEmpty(s.Sub))
                    {
                        if (prev != null) yy += gg;
                        yy += gc;
                        prev = s.Sub;
                    }
                    s.X = SlotX; s.W = SlotW; s.H = sh;
                    s.CapH = gc;
                    s.Y = yy;                       // 暂存成相对位移
                    yy += sh + sg;
                }

                float h2 = yy > 0 ? yy - sg : 0f;
                float start = h2 > viewH ? top - scrollSlot : midY - h2 * 0.5f;
                if (h2 <= viewH && start < top) start = top;
                foreach (TreeNode s in c.Kids) s.Y = start + s.Y;
            }

            SlotContentH = 0f;
            if (selCat != null && selCat.Kids.Count > 0)
            {
                float lo = float.MaxValue, hi = 0f;
                foreach (TreeNode s in selCat.Kids)
                {
                    if (s.Y < lo) lo = s.Y;
                    if (s.Y + s.H > hi) hi = s.Y + s.H;
                }
                // 第一行上面还有一个分组标题，它也算这列的一部分
                float cap = selCat.Kids[0].CapH > 1f ? selCat.Kids[0].CapH : GroupCap;
                SlotContentH = hi - (selCat.Kids[0].Y - cap);
            }

            // ---- 候选：跟着父节点居中；比一屏还高就从顶上排、自己滚 ----
            foreach (TreeNode c in root.Kids)
                foreach (TreeNode s in c.Kids)
                {
                    int n3 = s.Kids.Count;
                    if (n3 == 0) continue;
                    float h3 = n3 * CandH + (n3 - 1) * CandGap;
                    float y;
                    if (h3 > viewH) y = top - scrollCand;
                    else
                    {
                        y = s.Y + s.H * 0.5f - h3 * 0.5f;
                        if (y < top) y = top;
                        if (y + h3 > bottom) y = bottom - h3;
                    }
                    for (int i = 0; i < n3; i++)
                    {
                        TreeNode k = s.Kids[i];
                        k.X = CandX; k.W = CandW; k.H = CandH;
                        k.Y = y + i * (CandH + CandGap);
                    }
                }

            CandContentH = 0f;
            if (selSlot != null && selSlot.Kids.Count > 0)
            {
                float lo = float.MaxValue, hi = 0f;
                foreach (TreeNode k in selSlot.Kids)
                {
                    if (k.Y < lo) lo = k.Y;
                    if (k.Y + k.H > hi) hi = k.Y + k.H;
                }
                CandContentH = hi - lo;
            }
        }

        /// <summary>父节点右缘 → 子节点左缘的三次贝塞尔控制点（画连线用）。</summary>
        public static void Connector(TreeNode p, TreeNode c,
                                     out PointF a, out PointF b, out PointF c1, out PointF c2)
        {
            a = new PointF(p.X + p.W, p.Y + p.H * 0.5f);
            b = new PointF(c.X, c.Y + c.H * 0.5f);
            float mx = (a.X + b.X) * 0.5f;
            c1 = new PointF(mx, a.Y);
            c2 = new PointF(mx, b.Y);
        }

        /// <summary>位置列里，这一行上面要不要画一行小分类标题（连着的同一个分类只画一次）。</summary>
        public static bool StartsGroup(List<TreeNode> slots, int i)
        {
            if (i <= 0 || i >= slots.Count) return true;
            string a = slots[i - 1].Sub, b = slots[i].Sub;
            return !string.Equals(a, b, StringComparison.Ordinal);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;

namespace Installer
{
    /// <summary>
    /// 科技树的排版：只算坐标，不画。窗口变宽变窄都从这里过，
    /// 画的人（TreeCanvas / 白模）拿到的永远是一组矩形。
    /// </summary>
    internal static class TreeLayout
    {
        // 三档窗口宽度。列宽是上限，窗口更宽就把多出来的空间分给「候选」那一列
        public const float TopPad = 76f;      // 顶栏下面
        public const float BottomPad = 60f;   // 底栏上面
        public const float CatH = 40f, CatGap = 9f;
        public const float SlotH = 24f, SlotGap = 6f, GroupCap = 20f;
        public const float CandH = 44f, CandGap = 9f;

        public static void Arrange(TreeNode root, Size view)
        {
            float availW = view.Width - 96f;                       // 左右各留 48
            float[] w = { 132f, 196f, 306f, 452f };                // 根 / 大类 / 位置 / 候选
            float[] gap = { 52f, 58f, 64f };

            float need = 0f;
            for (int i = 0; i < w.Length; i++) need += w[i];
            for (int i = 0; i < gap.Length; i++) need += gap[i];

            if (availW > need + 80f)
            {
                // 宽屏：多出来的空间全给候选列，长名字能少截几个字
                w[3] += Math.Min(availW - need - 80f, 180f);
            }
            else if (availW < need)
            {
                float k = Math.Max(0.68f, availW / need);
                for (int i = 0; i < w.Length; i++) w[i] *= k;
                for (int i = 0; i < gap.Length; i++) gap[i] *= k;
            }

            float x = 48f;
            float[] colX = new float[4];
            for (int i = 0; i < 4; i++)
            {
                colX[i] = x;
                x += w[i] + (i < 3 ? gap[i] : 0f);
            }

            float top = TopPad;
            float bottom = view.Height - BottomPad;
            float midY = (top + bottom) * 0.5f;

            // 根
            root.X = colX[0]; root.W = w[0]; root.H = 68f;
            root.Y = midY - root.H * 0.5f;

            // 大类：等距排，整体居中
            int n1 = root.Kids.Count;
            float h1 = n1 * CatH + Math.Max(0, n1 - 1) * CatGap;
            float y1 = midY - h1 * 0.5f;
            if (y1 < top) y1 = top;
            for (int i = 0; i < n1; i++)
            {
                TreeNode c = root.Kids[i];
                c.X = colX[1]; c.W = w[1]; c.H = CatH;
                c.Y = y1 + i * (CatH + CatGap);
            }

            // 位置：按小分类分组，组名占一行
            float y2 = 0f;
            for (int i = 0; i < n1; i++)
            {
                TreeNode c = root.Kids[i];
                string prev = null;
                float yy = 0f;
                foreach (TreeNode s in c.Kids)
                {
                    if (s.Sub != prev && !string.IsNullOrEmpty(s.Sub))
                    {
                        if (prev != null) yy += GroupCap - SlotGap;   // 组间多留一点
                        yy += GroupCap;
                        prev = s.Sub;
                    }
                    s.X = colX[2]; s.W = w[2]; s.H = SlotH;
                    s.Y = yy;
                    yy += SlotH + SlotGap;
                }
                // 按列内总高居中
                float h2 = yy - SlotGap;
                float start = midY - h2 * 0.5f;
                if (start < top) start = top;
                foreach (TreeNode s in c.Kids) s.Y = start + s.Y;
                if (i == 0) y2 = start;
            }

            // 候选：跟着父节点纵向排，超出一屏就整体上移
            foreach (TreeNode c in root.Kids)
                foreach (TreeNode s in c.Kids)
                {
                    int n3 = s.Kids.Count;
                    float h3 = n3 * CandH + Math.Max(0, n3 - 1) * CandGap;
                    float y = s.Y + s.H * 0.5f - h3 * 0.5f;
                    if (y < top) y = top;
                    if (y + h3 > bottom) y = Math.Max(top, bottom - h3);
                    for (int i = 0; i < n3; i++)
                    {
                        TreeNode k = s.Kids[i];
                        k.X = colX[3]; k.W = w[3]; k.H = CandH;
                        k.Y = y + i * (CandH + CandGap);
                    }
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
    }
}

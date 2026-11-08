using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Installer
{
    /// <summary>
    /// 科技树：整窗口一块深色画板，从左往右四列 —— 根 / 大类 / 位置 / 候选。
    /// 只展开一条路径，其余大类灰着但不消失（点一下就换分支，不用退回上一层）。
    ///
    /// 画是 GDI+，命中是矩形包含，没有浏览器、没有 3D、没有端口。
    /// </summary>
    internal sealed class TreeCanvas : Control
    {
        private readonly MainForm mf;
        private TreeNode root;
        private TreeNode selCat, selSlot, selCand;

        private TreeNode hover, hoverPrev;
        private float hoverT = 1f;          // 0→1，120ms
        private TreeNode[] pathPrev = new TreeNode[0];
        private float pathT = 1f;

        private float scrollSlot, scrollCand;
        private TreeNode reveal;            // 下一次排版完要把这一行滚进视野
        private readonly Timer anim;
        private readonly ToolTip tip;

        // 固定的一套暖色，跟浅色/深色主题都不打架：这是「画板」，不是表单
        private static readonly Color BoardTop = Color.FromArgb(40, 30, 21);
        private static readonly Color BoardBot = Color.FromArgb(11, 7, 4);
        private static readonly Color Ink1 = Color.FromArgb(240, 230, 211);
        private static readonly Color Ink2 = Color.FromArgb(168, 155, 133);
        private static readonly Color Ink3 = Color.FromArgb(122, 112, 96);
        private static readonly Color Accent = Color.FromArgb(224, 138, 74);
        private static readonly Color Good = Color.FromArgb(111, 191, 139);

        private readonly Font fRoot = new Font("Microsoft YaHei", 13f, FontStyle.Bold);
        private readonly Font fRootSub = new Font("Microsoft YaHei", 9.5f);
        private readonly Font fCat = new Font("Microsoft YaHei", 12f);
        private readonly Font fCatSub = new Font("Microsoft YaHei", 8.8f);
        private readonly Font fSlot = new Font("Microsoft YaHei", 10.5f);
        private readonly Font fCand = new Font("Microsoft YaHei", 10.8f);
        private readonly Font fSub = new Font("Microsoft YaHei", 8.8f);
        private readonly Font fCap = new Font("Microsoft YaHei", 9f);

        internal TreeCanvas(MainForm form)
        {
            mf = form;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = BoardBot;
            TabStop = true;

            anim = new Timer();
            anim.Interval = 16;
            anim.Tick += delegate { StepAnim(); };

            tip = new ToolTip();
            tip.InitialDelay = 500;
            tip.ReshowDelay = 200;
            tip.AutoPopDelay = 20000;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                anim.Dispose();
                tip.Dispose();
                fRoot.Dispose(); fRootSub.Dispose(); fCat.Dispose(); fCatSub.Dispose();
                fSlot.Dispose(); fCand.Dispose(); fSub.Dispose(); fCap.Dispose();
            }
            base.Dispose(disposing);
        }

        // ================= 数据 =================

        /// <summary>从方案树重建模型，尽量把选中路径留住（按名字定位）。</summary>
        internal void Rebuild()
        {
            string catName = selCat == null ? null : selCat.Title;
            string slotKey = selSlot == null ? null : selSlot.Title;
            string candName = selCand == null ? null : selCand.Title;

            List<Slot> slots = mf.Tree == null ? null : mf.Tree.Slots;
            root = TreeModel.Build(slots, mf.CountChecked);

            selCat = Find(root.Kids, catName);
            if (selCat == null && root.Kids.Count > 0) selCat = root.Kids[0];
            selSlot = selCat == null ? null : Find(selCat.Kids, slotKey);
            if (selSlot == null && selCat != null && selCat.Kids.Count > 0) selSlot = selCat.Kids[0];
            selCand = selSlot == null ? null : Find(selSlot.Kids, candName);

            if (scrollSlot < 0f) scrollSlot = 0f;
            if (scrollCand < 0f) scrollCand = 0f;
            reveal = selSlot;
            Relayout();
        }

        /// <summary>截图 / 自检用：直接把选中路径挪到某个大类下的某个位置。</summary>
        internal void SelectForShot(string cat, string slot)
        {
            if (root == null) return;
            TreeNode c = Find(root.Kids, cat);
            if (c == null) return;
            selCat = c;
            selSlot = string.IsNullOrEmpty(slot) ? FirstUseful(c) : Find(c.Kids, slot);
            if (selSlot == null) selSlot = FirstUseful(c);
            selCand = selSlot == null ? null : PickDefault(selSlot);
            focus = 2;
            scrollSlot = 0f;
            scrollCand = 0f;
            RestartPath();
            Relayout();
        }

        private static TreeNode Find(List<TreeNode> list, string title)
        {
            if (string.IsNullOrEmpty(title)) return null;
            foreach (TreeNode n in list)
                if (string.Equals(n.Title, title, StringComparison.Ordinal)) return n;
            return null;
        }

        private void Relayout()
        {
            if (root == null) return;
            TreeLayout.Arrange(root, selCat, selSlot, ClientSize, scrollSlot, scrollCand);
            if (reveal != null)
            {
                TreeNode n = reveal;
                reveal = null;
                float before = scrollSlot;
                KeepVisible(n);
                if (scrollSlot != before)
                    TreeLayout.Arrange(root, selCat, selSlot, ClientSize, scrollSlot, scrollCand);
            }
            Invalidate();
        }

        // ================= 绘制 =================

        protected override void OnPaint(PaintEventArgs e)
        {
            // 自绘画布一旦抛异常，WinForms 会画一张红叉盖上来，问题就被藏住了 —— 记进日志
            try { PaintAll(e); }
            catch (Exception ex) { Core.Log("FAIL", "TREEPAINT " + ex.ToString()); }
        }

        private void PaintAll(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            Board(g);

            if (root == null)
            {
                using (StringFormat sf = new StringFormat())
                using (SolidBrush b = new SolidBrush(Ink3))
                {
                    sf.Alignment = StringAlignment.Center;
                    g.DrawString("正在读库…", fCat, b,
                        new RectangleF(0, ClientSize.Height * 0.45f, ClientSize.Width, 30), sf);
                }
                return;
            }

            Caps(g);

            // 列内容只许画在视口里：滚动时多出来的部分要被切掉，
            // 不然会一路画到底部那行操作提示上，两行字叠在一起
            GraphicsState save = g.Save();
            g.SetClip(new RectangleF(0f, TreeLayout.ViewTop, ClientSize.Width,
                                     Math.Max(1f, TreeLayout.ViewBottom - TreeLayout.ViewTop)));

            Lines(g);

            if (TreeLayout.HasRoot) Node(g, root, IsPath(root), 0f);
            foreach (TreeNode c in root.Kids)
                Node(g, c, IsPath(c), Hot(c));

            if (selCat != null) Slots(g, selCat);
            if (selSlot != null) Cands(g, selSlot);

            Bands(g);
            g.Restore(save);

            Hint(g);
        }

        /// <summary>底部一行操作提示：空白那么大，不如把「怎么用」写在那儿。</summary>
        private void Hint(Graphics g)
        {
            string s = "点候选就换掉这个位置 · 点「留原版」清空 · ↑↓ 换一个 · ←→ 进出 · 滚轮翻长列表";
            using (SolidBrush b = new SolidBrush(Color.FromArgb(96, 200, 186, 162)))
                g.DrawString(s, fCap, b,
                    new RectangleF(TreeLayout.SidePad, ClientSize.Height - 25f,
                                   ClientSize.Width - TreeLayout.SidePad * 2f, 18f));
        }

        private void Board(Graphics g)
        {
            using (LinearGradientBrush b = new LinearGradientBrush(
                       new Rectangle(0, 0, Math.Max(2, ClientSize.Width), Math.Max(2, ClientSize.Height)),
                       BoardTop, BoardBot, LinearGradientMode.Vertical))
                g.FillRectangle(b, ClientRectangle);

            // 中间一团暖光，让画板不是一块死平的黑
            using (GraphicsPath p = new GraphicsPath())
            {
                float cx = ClientSize.Width * 0.5f, cy = ClientSize.Height * 0.38f;
                float rx = ClientSize.Width * 0.52f, ry = ClientSize.Height * 0.62f;
                p.AddEllipse(cx - rx, cy - ry, rx * 2, ry * 2);
                using (PathGradientBrush pb = new PathGradientBrush(p))
                {
                    pb.CenterColor = Color.FromArgb(26, 120, 78, 44);
                    pb.SurroundColors = new Color[] { Color.FromArgb(0, 0, 0, 0) };
                    g.FillPath(pb, p);
                }
            }
        }

        private void Caps(Graphics g)
        {
            float y = TreeLayout.TopPad - 26f;
            using (SolidBrush b = new SolidBrush(Ink3))
            {
                if (TreeLayout.HasRoot) g.DrawString("根", fCap, b, TreeLayout.RootX, y);
                g.DrawString("大类 · " + root.Kids.Count, fCap, b, TreeLayout.CatX, y);
                if (selCat != null)
                    g.DrawString("位置 · " + selCat.Kids.Count, fCap, b, TreeLayout.SlotX, y);
                if (selSlot != null)
                    g.DrawString("候选 · " + (selSlot.Kids.Count - 1) + " 个备选", fCap, b, TreeLayout.CandX, y);
            }
        }

        private void Lines(Graphics g)
        {
            // 先画没点亮的，再画点亮的 —— 亮线盖在上面
            bool fromRoot = TreeLayout.HasRoot && root.W >= 2f;
            if (fromRoot)
                foreach (TreeNode c in root.Kids)
                    if (c != selCat) Line(g, root, c, Color.FromArgb(9, 255, 235, 205), 1f);

            if (selCat != null)
                foreach (TreeNode s in selCat.Kids)
                    if (s != selSlot) Line(g, selCat, s, Color.FromArgb(11, 255, 235, 205), 1f);

            if (selSlot != null)
                foreach (TreeNode k in selSlot.Kids)
                    if (k != selCand) Line(g, selSlot, k, Color.FromArgb(13, 255, 235, 205), 1f);

            float a = 200f * pathT;
            if (fromRoot && selCat != null) Line(g, root, selCat, Color.FromArgb((int)(a * 0.75f), Accent), 1.6f);
            if (selSlot != null && selCat != null) Line(g, selCat, selSlot, Color.FromArgb((int)(a * 0.75f), Accent), 1.6f);
            if (selCand != null && selSlot != null && !selCand.IsNone)
                Line(g, selSlot, selCand, Color.FromArgb((int)a, Good), 1.6f);

            // 换路径时旧的那条淡出
            if (pathT < 1f && pathPrev.Length >= 2)
                for (int i = 0; i + 1 < pathPrev.Length; i++)
                {
                    TreeNode p = pathPrev[i], c = pathPrev[i + 1];
                    if (p == null || c == null || p.W < 2f || c.W < 2f) continue;
                    if (c == selCat || c == selSlot || c == selCand) continue;
                    Line(g, p, c, Color.FromArgb((int)(200f * (1f - pathT) * 0.7f), Accent), 1.6f);
                }
        }

        private static void Line(Graphics g, TreeNode p, TreeNode c, Color color, float w)
        {
            if (color.A <= 1) return;
            PointF a, b, c1, c2;
            TreeLayout.Connector(p, c, out a, out b, out c1, out c2);
            using (Pen pen = new Pen(color, w))
                g.DrawBezier(pen, a, c1, c2, b);
        }

        // ================= 自检 =================

        internal int CatCount { get { return root == null ? 0 : root.Kids.Count; } }

        internal int SlotNodeCount
        {
            get
            {
                if (root == null) return 0;
                int n = 0;
                foreach (TreeNode c in root.Kids) n += c.Kids.Count;
                return n;
            }
        }

        internal string[] CatTitles
        {
            get
            {
                if (root == null) return new string[0];
                string[] a = new string[root.Kids.Count];
                for (int i = 0; i < a.Length; i++) a[i] = root.Kids[i].Title;
                return a;
            }
        }

        /// <summary>自检用：点当前位置的第 index 个候选（「留原版」不算），返回是否真的选上了。</summary>
        internal bool TestPick(int index)
        {
            if (selSlot == null) return false;
            int seen = -1;
            foreach (TreeNode k in selSlot.Kids)
            {
                if (k.IsNone) continue;
                seen++;
                if (seen != index) continue;
                Pick(k);
                return mf.CountChecked(k.Group) > 0;
            }
            return false;
        }

        /// <summary>把某个大类排一遍，返回第一个排版问题；没问题返回 null。</summary>
        internal string LayoutProblem(string catTitle)
        {
            SelectForShot(catTitle, selSlot == null ? null : selSlot.Title);
            if (selCat == null) return "找不到大类「" + catTitle + "」";

            float viewH = TreeLayout.ViewBottom - TreeLayout.ViewTop;
            bool fits = TreeLayout.SlotContentH <= viewH + 1f;

            foreach (TreeNode s in selCat.Kids)
            {
                if (s.X < 0f || s.X + s.W > ClientSize.Width + 0.5f)
                    return catTitle + " / " + s.Title + " 横向出界";
                if (s.Y < TreeLayout.ViewTop - 1f)
                    return catTitle + " / " + s.Title + " 压到列标题上面";
                if (fits && s.Y + s.H > TreeLayout.ViewBottom + 1f)
                    return catTitle + " / " + s.Title + " 被底边切掉（放得下却没放下：列高 "
                         + TreeLayout.SlotContentH.ToString("0.0") + " / 视口 " + viewH.ToString("0.0")
                         + "，底边 " + (s.Y + s.H).ToString("0.0") + " / " + TreeLayout.ViewBottom.ToString("0.0")
                         + "，首行 " + selCat.Kids[0].Y.ToString("0.0") + " 行数 " + selCat.Kids.Count
                         + " 画布 " + ClientSize.Height + "）";
                if (s.H < 16f)
                    return catTitle + " / " + s.Title + " 行高被压到 " + s.H.ToString("0.0") + "，字放不下";
            }

            if (selSlot != null)
                foreach (TreeNode k in selSlot.Kids)
                {
                    if (k.X < 0f || k.X + k.W > ClientSize.Width + 0.5f)
                        return catTitle + " / " + k.Title + " 候选横向出界";
                }
            return null;
        }

        private void Slots(Graphics g, TreeNode cat)
        {
            foreach (TreeNode s in cat.Kids) Node(g, s, IsPath(s), Hot(s));
        }

        private void Cands(Graphics g, TreeNode slot)
        {
            foreach (TreeNode k in slot.Kids) Node(g, k, IsPath(k), Hot(k));
        }

        private void Node(Graphics g, TreeNode n, bool onPath, float hot)
        {
            // 窄窗口下根节点整列省掉，它的 X/W 根本没赋值 —— 退化矩形不能画，
            // 否则 AddArc 拿到 0 尺寸会抛 ArgumentException，整张画布变红叉
            if (n.W < 2f || n.H < 2f) return;
            RectangleF r = new RectangleF(n.X, n.Y, n.W, n.H);
            float rad = n.Level == 0 ? 12f : 7f;

            using (GraphicsPath p = Round(r, rad))
            {
                int fillA = (int)((onPath ? 24 : 8) + hot * 22);
                if (n.On) fillA += 6;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(fillA, 255, 240, 215)))
                    g.FillPath(b, p);

                Color bc;
                float bw;
                if (onPath) { bc = Color.FromArgb(130 + (int)(hot * 70) + (int)(pathT * 20), Accent); bw = 1.5f; }
                else { bc = Color.FromArgb(16 + (int)(hot * 26), 255, 235, 205); bw = 1f; }
                using (Pen pen = new Pen(bc, bw)) g.DrawPath(pen, p);
            }

            // 已装：左边一条绿杠（不抢标题的注意力）
            if (n.On && n.Level > 0)
            {
                using (GraphicsPath p = Round(new RectangleF(r.X + 1.5f, r.Y + 6f, 2.5f, r.Height - 12f), 1.2f))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(200, Good)))
                    g.FillPath(b, p);
            }

            if (n.Level == 0) DrawRoot(g, n);
            else if (n.Level == 1) DrawCat(g, n);
            else if (n.Level == 2) DrawSlot(g, n);
            else DrawCand(g, n);
        }

        private void DrawRoot(Graphics g, TreeNode n)
        {
            float x = n.X + 16f;
            int positions = 0;
            foreach (TreeNode c in n.Kids) positions += c.Kids.Count;
            using (SolidBrush b1 = new SolidBrush(Ink1))
            using (SolidBrush b2 = new SolidBrush(Ink3))
            using (SolidBrush b3 = new SolidBrush(Color.FromArgb(150, Good)))
            {
                g.DrawString("MOD 库", fRoot, b1, x, n.Y + 11f);
                g.DrawString(n.Sub ?? "", fRootSub, b2, x, n.Y + 37f);
                g.DrawString(positions + " 个位置 · " + n.Kids.Count + " 个大类", fRootSub, b3, x, n.Y + 55f);
            }
        }

        private void DrawCat(Graphics g, TreeNode n)
        {
            float avail = n.W - 26f;
            string badge = n.Badge ?? "";
            if (badge.Length > 0) avail -= Measure(g, badge, fCatSub) + 10f;

            using (SolidBrush b = new SolidBrush(n.On || IsPath(n) ? Ink1 : Color.FromArgb(232, 221, 202)))
            using (StringFormat sf = Ellipsis())
                g.DrawString(n.Title ?? "", fCat, b,
                    new RectangleF(n.X + 15f, n.Y + (n.H - 20f) * 0.5f, avail, 20f), sf);

            if (badge.Length > 0)
                using (StringFormat sf = Ellipsis())
                using (SolidBrush b = new SolidBrush(n.On ? Color.FromArgb(190, Good) : Ink3))
                {
                    sf.Alignment = StringAlignment.Far;
                    g.DrawString(badge, fCatSub, b,
                        new RectangleF(n.X + n.W - 82f, n.Y + (n.H - 15f) * 0.5f, 70f, 15f), sf);
                }
        }

        private void DrawSlot(Graphics g, TreeNode n)
        {
            float avail = n.W - 26f;
            string badge = n.Badge ?? "";
            if (badge.Length > 0) avail -= Measure(g, badge, fCatSub) + 10f;

            using (SolidBrush b = new SolidBrush(IsPath(n) ? Ink1 : Color.FromArgb(214, 201, 180)))
            using (StringFormat sf = Ellipsis())
                g.DrawString(n.Title ?? "", fSlot, b,
                    new RectangleF(n.X + 15f, n.Y + (n.H - 17f) * 0.5f, avail, 17f), sf);

            if (badge.Length > 0)
            {
                bool empty = badge == "空";
                using (StringFormat sf = Ellipsis())
                using (SolidBrush b = new SolidBrush(n.On ? Color.FromArgb(190, Good)
                                                          : (empty ? Color.FromArgb(120, Ink3) : Ink3)))
                {
                    sf.Alignment = StringAlignment.Far;
                    g.DrawString(badge, fCatSub, b,
                        new RectangleF(n.X + n.W - 78f, n.Y + (n.H - 14f) * 0.5f, 66f, 14f), sf);
                }
            }
        }

        private void DrawCand(Graphics g, TreeNode n)
        {
            float x = n.X + 15f;
            float avail = n.W - 30f;

            string tag = n.IsNone ? "" : (n.On ? "已装" : "未装");
            if (tag.Length > 0) avail -= 40f;

            bool hot = Hot(n) > 0.5f || IsPath(n);
            using (SolidBrush b1 = new SolidBrush(n.IsNone ? Ink3 : (n.On ? Ink1 : (hot ? Ink1 : Color.FromArgb(221, 208, 186)))))
            using (SolidBrush b2 = new SolidBrush(n.IsNone ? Color.FromArgb(110, Ink3) : Ink3))
            using (StringFormat sf = Ellipsis())
            {
                g.DrawString(n.Title ?? "", fCand, b1, new RectangleF(x, n.Y + 8f, avail, 18f), sf);
                if (!string.IsNullOrEmpty(n.Sub))
                    g.DrawString(n.Sub, fSub, b2, new RectangleF(x, n.Y + 26f, avail, 14f), sf);
            }

            if (tag.Length > 0)
                using (StringFormat sf = Ellipsis())
                using (SolidBrush b = new SolidBrush(n.On ? Color.FromArgb(215, Good) : Color.FromArgb(150, Ink3)))
                {
                    sf.Alignment = StringAlignment.Far;
                    g.DrawString(tag, fSub, b, new RectangleF(n.X + n.W - 58f, n.Y + 8f, 42f, 15f), sf);
                }
        }

        /// <summary>小分类标题 + 滚动条：这两个得压在节点上面又画在最后，单独一遍。</summary>
        private void Bands(Graphics g)
        {
            if (selCat != null && selCat.Kids.Count > 1)
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(178, 201, 186, 158)))
                using (Font f = new Font("Microsoft YaHei", 8.5f))
                {
                    for (int i = 0; i < selCat.Kids.Count; i++)
                    {
                        if (!TreeLayout.StartsGroup(selCat.Kids, i)) continue;
                        TreeNode s = selCat.Kids[i];
                        if (string.IsNullOrEmpty(s.Sub)) continue;
                        float capH = s.CapH > 1f ? s.CapH : TreeLayout.GroupCap;
                        if (s.Y - capH < TreeLayout.ViewTop - 4f) continue;
                        g.DrawString(s.Sub, f, b, s.X + 2f, s.Y - capH + 3f);
                    }
                }
            }

            Scroll(g, TreeLayout.SlotX, TreeLayout.SlotW, TreeLayout.SlotContentH, scrollSlot);
            Scroll(g, TreeLayout.CandX, TreeLayout.CandW, TreeLayout.CandContentH, scrollCand);

            Fade(g, TreeLayout.SlotX, TreeLayout.SlotW, TreeLayout.SlotContentH);
            Fade(g, TreeLayout.CandX, TreeLayout.CandW, TreeLayout.CandContentH);
        }

        /// <summary>列底一截渐隐：告诉人「下面还有，滚才有」。</summary>
        private void Fade(Graphics g, float x, float w, float contentH)
        {
            float viewH = TreeLayout.ViewBottom - TreeLayout.ViewTop;
            if (contentH <= viewH + 0.5f) return;

            float h = Math.Min(46f, viewH * 0.18f);
            Rectangle r = new Rectangle((int)x - 4, (int)(TreeLayout.ViewBottom - h), (int)w + 8, (int)h);
            if (r.Height <= 0) return;
            using (LinearGradientBrush b = new LinearGradientBrush(
                       r, Color.FromArgb(0, 11, 7, 4), Color.FromArgb(230, 11, 7, 4), LinearGradientMode.Vertical))
                g.FillRectangle(b, r);
        }

        private void Scroll(Graphics g, float x, float w, float contentH, float offset)
        {
            float viewH = TreeLayout.ViewBottom - TreeLayout.ViewTop;
            if (contentH <= viewH + 0.5f || viewH <= 1f) return;

            float trackX = x + w + 7f;
            float ratio = viewH / contentH;
            float barH = Math.Max(26f, viewH * ratio);
            float maxScroll = contentH - viewH;
            float t = maxScroll <= 0f ? 0f : Math.Min(1f, offset / maxScroll);
            float barY = TreeLayout.ViewTop + (viewH - barH) * t;

            using (Pen p = new Pen(Color.FromArgb(26, 255, 235, 205), 4f))
                g.DrawLine(p, trackX, TreeLayout.ViewTop, trackX, TreeLayout.ViewBottom);
            using (Pen p = new Pen(Color.FromArgb(120, 255, 235, 205), 4f))
                g.DrawLine(p, trackX, barY, trackX, barY + barH);
        }

        private static StringFormat Ellipsis()
        {
            StringFormat sf = new StringFormat();
            sf.Trimming = StringTrimming.EllipsisCharacter;
            sf.FormatFlags = StringFormatFlags.NoWrap;
            return sf;
        }

        private float Measure(Graphics g, string s, Font f)
        {
            if (string.IsNullOrEmpty(s)) return 0f;
            return g.MeasureString(s, f, 1000, StringFormat.GenericTypographic).Width;
        }

        private static GraphicsPath Round(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            if (r.Width < rad * 2f) rad = r.Width * 0.5f;
            if (r.Height < rad * 2f) rad = r.Height * 0.5f;
            float d = rad * 2f;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ================= 状态 =================

        private bool IsPath(TreeNode n)
        {
            return n != null && (n == selCat || n == selSlot || n == selCand);
        }

        private float Hot(TreeNode n)
        {
            if (n == null) return 0f;
            if (n == hover) return hoverT;
            if (n == hoverPrev) return 1f - hoverT;
            return 0f;
        }

        private void SetCursorNode(TreeNode n)
        {
            if (n == hover) return;
            hoverPrev = hover;
            hover = n;
            hoverT = 0f;
            anim.Start();
            tip.SetToolTip(this, n == null ? "" : (n.Title ?? ""));
        }

        private void StepAnim()
        {
            bool busy = false;
            if (hoverT < 1f) { hoverT = Math.Min(1f, hoverT + 0.075f); busy = true; }
            if (pathT < 1f) { pathT = Math.Min(1f, pathT + 0.09f); busy = true; }
            if (!busy) anim.Stop();
            Invalidate();
        }

        private void RestartPath()
        {
            List<TreeNode> old = new List<TreeNode>();
            if (selCat != null) old.Add(selCat);
            if (selSlot != null) old.Add(selSlot);
            if (selCand != null) old.Add(selCand);
            pathPrev = old.ToArray();
            pathT = 0f;
            anim.Start();
        }

        // ================= 命中 =================

        private TreeNode Hit(Point pt)
        {
            if (root == null) return null;
            if (Has(root, pt)) return root;
            foreach (TreeNode c in root.Kids) if (Has(c, pt)) return c;
            if (selCat != null)
                foreach (TreeNode s in selCat.Kids) if (Has(s, pt)) return s;
            if (selSlot != null)
                foreach (TreeNode k in selSlot.Kids) if (Has(k, pt)) return k;
            return null;
        }

        private static bool Has(TreeNode n, Point p)
        {
            return p.X >= n.X && p.X <= n.X + n.W && p.Y >= n.Y && p.Y <= n.Y + n.H;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            TreeNode n = Hit(e.Location);
            SetCursorNode(n);
            Cursor = n == null ? Cursors.Default : Cursors.Hand;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            SetCursorNode(null);
            Cursor = Cursors.Default;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (root == null || e.Button != MouseButtons.Left) return;

            TreeNode n = Hit(e.Location);
            if (n == null) return;

            if (n.Level == 3)
            {
                focus = 3;
                Pick(n);
                return;
            }

            if (n.Level == 1)
            {
                focus = 1;
                if (n != selCat) { selCat = n; selSlot = FirstUseful(n); selCand = null; scrollSlot = 0f; scrollCand = 0f; RestartPath(); }
                else if (n.Kids.Count > 0) { selSlot = FirstUseful(n); selCand = null; scrollSlot = 0f; RestartPath(); }
                reveal = selSlot;
            }
            else if (n.Level == 2)
            {
                focus = 2;
                if (n != selSlot) { selSlot = n; selCand = null; scrollCand = 0f; RestartPath(); }
                else if (n.Kids.Count > 0) { selCand = PickDefault(n); RestartPath(); }
                reveal = selSlot;
            }
            Relayout();
        }

        /// <summary>选中大类时优先落在「已装的那个位置」，没有就第一个。</summary>
        private static TreeNode FirstUseful(TreeNode cat)
        {
            if (cat == null || cat.Kids.Count == 0) return null;
            foreach (TreeNode s in cat.Kids) if (s.On) return s;
            return cat.Kids[0];
        }

        /// <summary>已经装的候选排前面，选中有好处：一眼看到「现在是哪个」。</summary>
        private static TreeNode PickDefault(TreeNode slot)
        {
            foreach (TreeNode k in slot.Kids) if (k.On) return k;
            return null;
        }

        /// <summary>点候选：清掉这个位置原来的，再勾这个（点「留原版」＝全清）。</summary>
        private void Pick(TreeNode n)
        {
            if (n.Group == null && !n.IsNone) return;
            Slot slot = selSlot == null ? null : selSlot.Slot;
            if (slot == null) return;

            bool wasOn = n.On;
            mf.ChooseInSlot(slot, n.IsNone ? null : n.Group);
            selCand = wasOn ? null : n;
            RestartPath();
            // SyncPicks 会回头调 RefreshPicks()，模型里的 On 和数字一起重算
            mf.SyncPicks();
        }

        /// <summary>外部改了勾选（方案树那边点的、装完了）：只重算模型，不动选中路径。</summary>
        internal void RefreshPicks()
        {
            Rebuild();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            float viewH = TreeLayout.ViewBottom - TreeLayout.ViewTop;
            float step = e.Delta * 0.55f;

            if (e.X >= TreeLayout.CandX - 8f)
            {
                float max = Math.Max(0f, TreeLayout.CandContentH - viewH);
                scrollCand = Math.Max(0f, Math.Min(max, scrollCand - step));
            }
            else if (e.X >= TreeLayout.SlotX - 8f)
            {
                float max = Math.Max(0f, TreeLayout.SlotContentH - viewH);
                scrollSlot = Math.Max(0f, Math.Min(max, scrollSlot - step));
            }
            Relayout();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Relayout();
        }

        // ================= 键盘 =================
        // focus 是「现在停在那一列」：1 大类 / 2 位置 / 3 候选。
        // 光靠 selCat/selSlot 判断会绕回去（候选可能是 null），不如记住位置本身。

        private int focus = 1;

        protected override bool IsInputKey(Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            if (k == Keys.Up || k == Keys.Down || k == Keys.Left || k == Keys.Right
                || k == Keys.Enter || k == Keys.Space) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (root == null) return;

            if (e.KeyCode == Keys.Escape) { mf.HideTreeMap(); e.Handled = true; return; }
            if (e.KeyCode == Keys.Left) { Back(); e.Handled = true; return; }
            if (e.KeyCode == Keys.Right) { Deeper(); e.Handled = true; return; }

            if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)
            {
                Move(e.KeyCode == Keys.Up ? -1 : 1);
                e.Handled = true;
                return;
            }

            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
            {
                if (focus >= 3 && selCand != null) Pick(selCand);
                else Deeper();
                e.Handled = true;
            }
        }

        private List<TreeNode> ColumnAt(int level, out int idx)
        {
            idx = -1;
            if (level >= 3 && selSlot != null) { idx = selSlot.Kids.IndexOf(selCand); return selSlot.Kids; }
            if (level >= 2 && selCat != null) { idx = selCat.Kids.IndexOf(selSlot); return selCat.Kids; }
            idx = root.Kids.IndexOf(selCat);
            return root.Kids;
        }

        private void Move(int d)
        {
            if (focus > 3) focus = 3;
            if (focus < 1) focus = 1;
            int idx;
            List<TreeNode> list = ColumnAt(focus, out idx);
            if (list == null || list.Count == 0) return;

            int n = idx < 0 ? (d > 0 ? 0 : list.Count - 1) : idx + d;
            if (n < 0) n = list.Count - 1;
            if (n >= list.Count) n = 0;

            TreeNode pick = list[n];
            if (focus == 1) { selCat = pick; selSlot = FirstUseful(pick); selCand = null; scrollSlot = 0f; scrollCand = 0f; }
            else if (focus == 2) { selSlot = pick; selCand = null; scrollCand = 0f; }
            else { selCand = pick; }

            RestartPath();
            KeepVisible(pick);
            Relayout();
        }

        private void Deeper()
        {
            if (focus == 1)
            {
                if (selCat == null || selCat.Kids.Count == 0) return;
                selSlot = selSlot != null && selCat.Kids.Contains(selSlot) ? selSlot : FirstUseful(selCat);
                focus = 2;
            }
            else if (focus == 2)
            {
                if (selSlot == null) return;
                selCand = PickDefault(selSlot);
                focus = 3;
            }
            RestartPath();
            Relayout();
        }

        private void Back()
        {
            if (focus == 3) { focus = 2; selCand = null; }
            else if (focus == 2) { focus = 1; }
            else return;
            RestartPath();
            Relayout();
        }

        private void KeepVisible(TreeNode n)
        {
            if (n == null || n.Level != 2) return;
            float viewH = TreeLayout.ViewBottom - TreeLayout.ViewTop;
            float max = Math.Max(0f, TreeLayout.SlotContentH - viewH);
            if (n.Y < TreeLayout.ViewTop)
                scrollSlot = Math.Max(0f, Math.Min(max, scrollSlot - (TreeLayout.ViewTop - n.Y)));
            else if (n.Y + n.H > TreeLayout.ViewBottom)
                scrollSlot = Math.Max(0f, Math.Min(max, scrollSlot + (n.Y + n.H - TreeLayout.ViewBottom)));
        }
    }
}

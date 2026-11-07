using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Installer
{
    // ============================================================
    //  台面（整窗画布）
    //  方案树是一棵强制缩进的树；台面是同一份数据的另一种摆法：
    //  一张卡 = 一个大类（或「我的分支」里的一个分支），卡里列这个类的位置，
    //  卡随便拖到哪儿就停在哪儿，位置记在 U 盘的 台面布局.ini 里。
    //  拖的是「摆放」，点的是「选不选它」——和树里的勾选是同一份状态。
    // ============================================================

    /// <summary>台面上一张卡里的一行：小分类标题，或者一个位置。</summary>
    internal sealed class DeskRow
    {
        public bool IsSub;
        public string Sub;
        public Slot Slot;
        public int Top;          // 相对卡片顶端
        public int H;
    }

    /// <summary>台面上的一张卡。</summary>
    internal sealed class DeskCard
    {
        public string Key;       // cat|枪械 / mine|音效 / mine|+new
        public string Title;
        public string Cat;
        public bool Branch;      // 「我的分支」里的一个分支
        public string BranchName; // 分支名（Branch 为真时有效）
        public bool NewBranch;   // 尾卡：新建分支
        public bool Note;        // 「未归类」：认不出改的是什么的那些 MOD
        public int X, Y, W, H;
        public bool Folded;
        public bool Saved;       // 位置是拖出来的（不是自动排的）
        public readonly List<DeskRow> Rows = new List<DeskRow>();

        public Rectangle Bounds { get { return new Rectangle(X, Y, W, H); } }
    }

    /// <summary>台面布局的存取：只记拖过的卡，没拖过的每次都按顺序自动排。</summary>
    internal sealed class DeskPos
    {
        public int X, Y, W;
        public bool Folded;
    }

    internal static class DeskPrefs
    {
        private static readonly Dictionary<string, DeskPos> map =
            new Dictionary<string, DeskPos>(StringComparer.OrdinalIgnoreCase);
        private static bool loaded;

        public static string FileOf()
        {
            return string.IsNullOrEmpty(Core.PkgDir) ? "" : Path.Combine(Core.PkgDir, "台面布局.ini");
        }

        public static void Ensure() { if (!loaded) Load(); }

        public static void Reload() { loaded = false; Ensure(); }

        public static DeskPos Of(string key)
        {
            Ensure();
            if (string.IsNullOrEmpty(key)) return null;
            DeskPos p;
            return map.TryGetValue(key, out p) ? p : null;
        }

        public static void Put(string key, int x, int y, int w, bool folded)
        {
            if (string.IsNullOrEmpty(key)) return;
            Ensure();
            DeskPos p;
            if (!map.TryGetValue(key, out p)) { p = new DeskPos(); map[key] = p; }
            p.X = x; p.Y = y; p.W = w; p.Folded = folded;
        }

        public static void Clear()
        {
            Ensure();
            map.Clear();
            Save();
        }

        private static void Load()
        {
            loaded = true;
            map.Clear();
            string f = FileOf();
            if (f.Length == 0 || !File.Exists(f)) return;
            try
            {
                foreach (string raw in File.ReadAllLines(f, Encoding.UTF8))
                {
                    string line = raw.TrimEnd((char)13);
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] p = line.Split('\t');
                    if (p.Length < 5) continue;
                    if (!p[0].StartsWith("card=", StringComparison.Ordinal)) continue;
                    DeskPos d = new DeskPos();
                    int.TryParse(p[1], out d.X);
                    int.TryParse(p[2], out d.Y);
                    int.TryParse(p[3], out d.W);
                    d.Folded = p[4] == "1";
                    map[p[0].Substring(5)] = d;
                }
            }
            catch { }
        }

        public static void Save()
        {
            Ensure();
            string f = FileOf();
            if (f.Length == 0) return;
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# 台面布局：每张卡停在哪儿。只记拖过的卡，没拖过的照自动顺序排。");
                foreach (KeyValuePair<string, DeskPos> kv in map)
                    sb.AppendLine("card=" + kv.Key + "\t" + kv.Value.X + "\t" + kv.Value.Y + "\t" + kv.Value.W + "\t" + (kv.Value.Folded ? "1" : "0"));
                File.WriteAllText(f, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex) { Core.Log("WARN", "台面布局写不进去: " + ex.Message); }
        }
    }

    internal sealed class DeskView : Control
    {
        private readonly MainForm mf;
        private readonly CatTree tree;
        private readonly List<DeskCard> cards = new List<DeskCard>();

        private int scrollY;
        private DeskCard dragCard;
        private Point dragStart;
        private Point dragFrom;
        private bool dragMoved;
        private DeskCard pressCard;
        private DeskRow pressRow;
        private DeskCard hoverCard;
        private DeskRow hoverRow;
        private string foot = "";

        internal const int CardW = 252;
        private const int HeaderH = 30;
        private const int RowH = 22;
        private const int SubH = 19;
        private const int Gap = 14;
        private const int Edge = 14;
        private const int CardPad = 6;

        private static readonly StringFormat SfLeft = new StringFormat();
        private static readonly StringFormat SfRight = new StringFormat();
        private static readonly StringFormat SfCenter = new StringFormat();

        static DeskView()
        {
            SfLeft.Trimming = StringTrimming.EllipsisCharacter;
            SfLeft.FormatFlags = StringFormatFlags.NoWrap;
            SfLeft.LineAlignment = StringAlignment.Center;
            SfRight.Trimming = StringTrimming.EllipsisCharacter;
            SfRight.FormatFlags = StringFormatFlags.NoWrap;
            SfRight.Alignment = StringAlignment.Far;
            SfRight.LineAlignment = StringAlignment.Center;
            SfCenter.Trimming = StringTrimming.EllipsisCharacter;
            SfCenter.FormatFlags = StringFormatFlags.NoWrap;
            SfCenter.Alignment = StringAlignment.Center;
            SfCenter.LineAlignment = StringAlignment.Center;
        }

        public DeskView(MainForm owner, CatTree model)
        {
            mf = owner;
            tree = model;
            BackColor = Theme.TreeBg;
            Font = Theme.UI;
            TabStop = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        internal int CardCount { get { return cards.Count; } }
        internal List<DeskCard> Cards { get { return cards; } }

        /// <summary>换主题后重画（颜色都在绘制时取，不用重建位图）。</summary>
        internal void OnThemeChanged()
        {
            BackColor = Theme.TreeBg;
            Invalidate();
        }

        // ---------------------------------------------------------------- 建卡

        public void Rebuild()
        {
            cards.Clear();
            if (tree == null) return;
            List<Slot> slots = tree.Slots;
            if (slots == null) return;

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
                    BuildBranchCards(list);
                    continue;
                }
                DeskCard c = new DeskCard();
                c.Key = "cat|" + cat;
                c.Title = cat;
                c.Cat = cat;
                BuildSlotRows(c, list);
                cards.Add(c);
            }

            // 「我的分支」一张都没有的时候，也要给个入口，不然台面上看不见这个功能
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

            // 认不出改的什么的那些 MOD 在树里有一行入口，台面也得有，否则它们在这边等于消失了
            if (tree.Unmatched > 0)
            {
                DeskCard u = new DeskCard();
                u.Key = "misc|unmatched";
                u.Title = "未归类";
                u.Cat = "未归类";
                u.Note = true;
                cards.Add(u);
            }

            LayoutAll();
            Invalidate();
        }

        private void BuildBranchCards(List<Slot> mineSlots)
        {
            foreach (Slot s in mineSlots)
            {
                if (s.Key != null && s.Key == "#mine+new") continue;
                DeskCard c = new DeskCard();
                c.Key = "mine|" + s.Name;
                c.Title = s.Name;
                c.Cat = "我的分支";
                c.Branch = true;
                foreach (ModGroup g in s.Items)
                {
                    DeskRow r = new DeskRow();
                    r.Slot = null;
                    r.Sub = g.Name;
                    c.Rows.Add(r);
                }
                c.BranchName = s.Name;
                cards.Add(c);
            }
            DeskCard add = new DeskCard();
            add.Key = "mine|+new";
            add.Title = "＋ 新建分支";
            add.Cat = "我的分支";
            add.NewBranch = true;
            cards.Add(add);
        }

        private void BuildSlotRows(DeskCard c, List<Slot> list)
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
                c.Rows.Add(r);
            }
        }

        // ---------------------------------------------------------------- 排布

        private void LayoutAll()
        {
            // 1) 宽度、折叠、高度：先按存的来，没存过的用默认宽
            foreach (DeskCard c in cards)
            {
                DeskPos p = DeskPrefs.Of(c.Key);
                c.Saved = p != null;
                c.W = (p != null && p.W >= 160) ? p.W : CardW;
                c.Folded = p != null && p.Folded;
                if (c.NewBranch) c.Folded = false;
                if (p != null) { c.X = p.X; c.Y = p.Y; }
                CalcHeight(c);
            }

            // 2) 没拖过的卡：排在所有拖过的卡下面，按最短列往下摆
            int startY = Edge;
            foreach (DeskCard c in cards) if (c.Saved) startY = Math.Max(startY, c.Y + c.H + Gap);
            int span = Math.Max(CardW + Gap, ClientSize.Width - Edge * 2);
            int cols = Math.Max(1, (span + Gap) / (CardW + Gap));
            int[] colY = new int[cols];
            for (int i = 0; i < cols; i++) colY[i] = startY;
            foreach (DeskCard c in cards)
            {
                if (c.Saved) continue;
                int pick = 0;
                for (int i = 1; i < cols; i++) if (colY[i] < colY[pick]) pick = i;
                c.X = Edge + pick * (CardW + Gap);
                c.Y = colY[pick];
                colY[pick] += c.H + Gap;
            }

            ClampCards();
        }

        private void CalcHeight(DeskCard c)
        {
            if (c.NewBranch) { c.H = 46; return; }
            if (c.Note) { c.H = HeaderH + 26; return; }
            int y = HeaderH;
            if (!c.Folded)
            {
                foreach (DeskRow r in c.Rows)
                {
                    r.H = r.IsSub ? SubH : RowH;
                    r.Top = y;
                    y += r.H;
                }
                y += CardPad;
            }
            c.H = y;
        }

        /// <summary>把卡夹回看得见的地方：横向不越界，纵向不跑到负区。</summary>
        private void ClampCards()
        {
            int visW = ClientSize.Width;
            foreach (DeskCard c in cards)
            {
                int maxX = Math.Max(Edge, visW - c.W - Edge);
                if (c.X < Edge) c.X = Edge;
                if (c.X > maxX) c.X = maxX;
                if (c.Y < 0) c.Y = 0;
            }
        }

        private int ContentH()
        {
            int bottom = 0;
            foreach (DeskCard c in cards) bottom = Math.Max(bottom, c.Y + c.H);
            return bottom + Edge;
        }

        private void ClampScroll()
        {
            int max = Math.Max(0, ContentH() - ClientSize.Height);
            if (scrollY > max) scrollY = max;
            if (scrollY < 0) scrollY = 0;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (cards.Count == 0) return;
            // 窗口变窄时把卡夹回可视区；已经拖过的卡不改存的位置，只是画的时候不越界
            ClampCards();
            ClampScroll();
            Invalidate();
        }

        // ---------------------------------------------------------------- 画

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.TreeBg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            DrawDots(g);

            g.TranslateTransform(0, -scrollY);
            foreach (DeskCard c in cards)
            {
                if (c.Y - scrollY > Height) continue;
                if (c.Y + c.H - scrollY < -8) continue;
                DrawCard(g, c);
            }
            g.ResetTransform();

            DrawScroll(g);
            if (foot.Length > 0) DrawFoot(g);
        }

        /// <summary>台面比窗口高的时候给条细滚动条 —— 没有它，人不会知道下面还有卡。</summary>
        private void DrawScroll(Graphics g)
        {
            int content = ContentH();
            if (content <= ClientSize.Height) return;
            int track = Height - 8;
            if (track <= 0) return;
            int th = Math.Max(36, track * Height / content);
            int max = content - Height;
            int pos = max <= 0 ? 0 : (track - th) * scrollY / max;
            Rectangle r = new Rectangle(Width - 8, 4 + pos, 4, th);
            using (GraphicsPath p = Theme.Round(r, 2))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(120, Theme.Ink3)))
                g.FillPath(b, p);
        }

        private void DrawDots(Graphics g)
        {
            // 台面点阵：压得很淡，只是为了让人看出来「这块是可以摆东西的」
            using (SolidBrush b = new SolidBrush(Color.FromArgb(26, Theme.Ink3)))
            {
                int step = 26;
                int y0 = 6 - (scrollY % step);
                for (int y = y0; y < Height; y += step)
                    for (int x = 6; x < Width; x += step)
                        g.FillRectangle(b, x, y, 1, 1);
            }
        }

        private void DrawCard(Graphics g, DeskCard c)
        {
            Rectangle r = c.Bounds;
            using (GraphicsPath p = Theme.Round(r, 10))
            {
                using (SolidBrush b = new SolidBrush(c.NewBranch ? Theme.SurfaceAlt : Theme.Surface))
                    g.FillPath(b, p);
                using (Pen pen = new Pen(dragCard == c ? Theme.Primary : Theme.CardEdge, 1f))
                {
                    if (c.NewBranch) pen.DashStyle = DashStyle.Dash;
                    g.DrawPath(pen, p);
                }
            }

            if (c.NewBranch)
            {
                using (SolidBrush b = new SolidBrush(Theme.Ink3))
                    g.DrawString(c.Title, Theme.UISmall, b, r, SfCenter);
                return;
            }

            // 标题行：折叠箭头 + 名字 + 右侧计数
            int cy = c.Y + HeaderH / 2;
            DrawFoldArrow(g, c.X + 12, cy, c.Folded);
            Rectangle tr = new Rectangle(c.X + 24, c.Y + 4, Math.Max(40, c.W - 24 - 96), HeaderH - 8);
            using (SolidBrush b = new SolidBrush(Theme.Ink))
                g.DrawString(c.Title, Theme.UIBold, b, tr, SfLeft);

            string cnt = CountText(c);
            if (cnt.Length > 0)
            {
                Rectangle cr = new Rectangle(c.X + c.W - 96, c.Y + 4, 84, HeaderH - 8);
                using (SolidBrush b = new SolidBrush(Theme.Ink3))
                    g.DrawString(cnt, Theme.UISmall, b, cr, SfRight);
            }

            if (c.Folded) return;

            using (Pen pen = new Pen(Theme.LineSoft))
                g.DrawLine(pen, c.X + 10, c.Y + HeaderH, c.X + c.W - 10, c.Y + HeaderH);

            if (c.Note)
            {
                using (SolidBrush b = new SolidBrush(Theme.Ink3))
                    g.DrawString("点一下去指定它算哪类、替换什么", Theme.UISmall, b,
                        new Rectangle(c.X + 12, c.Y + HeaderH + 2, c.W - 24, 22), SfLeft);
                return;
            }

            foreach (DeskRow row in c.Rows) DrawRow(g, c, row);
        }

        private void DrawFoldArrow(Graphics g, int x, int cy, bool folded)
        {
            using (SolidBrush b = new SolidBrush(Theme.Ink3))
            {
                if (folded) g.FillPolygon(b, new Point[] { new Point(x, cy - 4), new Point(x + 6, cy), new Point(x, cy + 4) });
                else g.FillPolygon(b, new Point[] { new Point(x - 1, cy - 2), new Point(x + 6, cy - 2), new Point(x + 2, cy + 4) });
            }
        }

        private string CountText(DeskCard c)
        {
            if (c.Note) return tree.Unmatched + " 个";
            int n = 0, k = 0;
            if (c.Branch)
            {
                n = c.Rows.Count;
                foreach (DeskRow r in c.Rows) if (r.Slot == null && GroupOf(r) != null && mf.CountChecked(GroupOf(r)) > 0) k++;
            }
            else
            {
                foreach (DeskRow r in c.Rows)
                {
                    if (r.IsSub || r.Slot == null) continue;
                    n++;
                    if (tree.HasChecked(r.Slot)) k++;
                }
            }
            if (n == 0) return "空";
            return k > 0 ? (n + " · 已选 " + k) : (n + " 个位置");
        }

        private ModGroup GroupOf(DeskRow r)
        {
            if (r == null) return null;
            if (r.Slot != null) return tree.ChosenOf(r.Slot);
            if (r.Sub == null) return null;
            // 分支卡的行：按名字回查这个分支里的 MOD
            foreach (ModGroup g in Catalog.Groups)
                if (g.Main != null && string.Equals(g.Name, r.Sub, StringComparison.Ordinal)) return g;
            return null;
        }

        private void DrawRow(Graphics gr, DeskCard c, DeskRow row)
        {
            Rectangle rr = new Rectangle(c.X + 1, c.Y + row.Top, c.W - 2, row.H);

            if (row.IsSub)
            {
                using (Pen pen = new Pen(Theme.LineSoft))
                    gr.DrawLine(pen, c.X + 10, rr.Y, c.X + c.W - 10, rr.Y);
                Rectangle st = new Rectangle(rr.X + 10, rr.Y, rr.Width - 20, rr.Height);
                using (SolidBrush b = new SolidBrush(Theme.Ink3))
                    gr.DrawString(row.Sub, Theme.UISmall, b, st, SfLeft);
                return;
            }

            ModGroup grp = GroupOf(row);
            bool on = grp != null && mf.CountChecked(grp) > 0;

            if (on)
            {
                // 选中的：左边一条竖条 + 淡底，扫一眼就知道这套方案里有什么
                using (SolidBrush b = new SolidBrush(Color.FromArgb(22, Theme.Primary)))
                    gr.FillRectangle(b, rr.X + 3, rr.Y + 2, rr.Width - 6, rr.Height - 4);
                using (SolidBrush b = new SolidBrush(Theme.Primary))
                    gr.FillRectangle(b, rr.X + 3, rr.Y + 4, 3, rr.Height - 8);
            }
            else if (hoverRow == row && hoverCard == c)
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(18, Theme.Ink3)))
                    gr.FillRectangle(b, rr.X + 3, rr.Y + 2, rr.Width - 6, rr.Height - 4);
            }

            string name = row.Slot != null ? row.Slot.Name : (row.Sub == null ? "" : row.Sub);
            string status; Color ink;
            RowStatus(row, out status, out ink);

            int sw = 88;
            Rectangle nr = new Rectangle(rr.X + 12, rr.Y, Math.Max(30, rr.Width - 12 - sw - 8), rr.Height);
            using (SolidBrush b = new SolidBrush(on ? Theme.Ink : Theme.Ink2))
                gr.DrawString(name, Theme.UI, b, nr, SfLeft);

            if (status.Length > 0)
            {
                Rectangle sr = new Rectangle(rr.Right - sw - 8, rr.Y, sw, rr.Height);
                using (SolidBrush b = new SolidBrush(ink))
                    gr.DrawString(status, Theme.UISmall, b, sr, SfRight);
            }
        }

        private void RowStatus(DeskRow row, out string txt, out Color ink)
        {
            txt = "";
            ink = Theme.Ink3;
            ModGroup grp = GroupOf(row);
            if (grp == null)
            {
                txt = Core.IsLocal ? "关闭" : "未装";
                return;
            }
            Color bg;
            txt = ShortStatus(MainForm.StatusOf(grp, out ink, out bg));
        }

        /// <summary>卡片窄，状态写全会被截成「部分已…」，这里换成短的。</summary>
        private static string ShortStatus(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.StartsWith("部分已装", StringComparison.Ordinal)) return "部分" + s.Substring(4).Trim();
            return s;
        }

        private void DrawFoot(Graphics g)
        {
            Rectangle r = new Rectangle(12, Height - 26, Math.Min(Width - 24, 12 + foot.Length * 13), 20);
            using (GraphicsPath p = Theme.Round(r, 10))
            using (SolidBrush b = new SolidBrush(Theme.Surface))
                g.FillPath(b, p);
            using (SolidBrush b = new SolidBrush(Theme.Ink2))
                g.DrawString(foot, Theme.UISmall, b, new Rectangle(r.X + 10, r.Y, r.Width - 20, r.Height), SfLeft);
        }

        // ---------------------------------------------------------------- 命中

        private DeskCard CardAt(Point pt)
        {
            for (int i = cards.Count - 1; i >= 0; i--)
            {
                DeskCard c = cards[i];
                if (pt.X >= c.X && pt.X <= c.X + c.W && pt.Y >= c.Y && pt.Y <= c.Y + c.H) return c;
            }
            return null;
        }

        private DeskRow RowAt(DeskCard c, Point pt)
        {
            if (c == null || c.Folded || c.NewBranch) return null;
            if (pt.Y < c.Y + HeaderH) return null;
            foreach (DeskRow r in c.Rows)
                if (pt.Y >= c.Y + r.Top && pt.Y < c.Y + r.Top + r.H) return r;
            return null;
        }

        private Point ToCanvas(Point p) { return new Point(p.X, p.Y + scrollY); }

        // ---------------------------------------------------------------- 交互

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            Point pt = ToCanvas(e.Location);
            DeskCard c = CardAt(pt);

            if (e.Button == MouseButtons.Right) { ShowMenu(pt, c); return; }
            if (c == null) return;

            if (c.NewBranch)
            {
                pressCard = c;
                return;
            }
            if (c.Note)
            {
                pressCard = c;
                return;
            }

            if (pt.Y < c.Y + HeaderH)
            {
                if (pt.X < c.X + 22) { ToggleFold(c); return; }
                dragCard = c;
                dragMoved = false;
                dragStart = pt;
                dragFrom = new Point(c.X, c.Y);
                return;
            }

            DeskRow r = RowAt(c, pt);
            if (r != null && !r.IsSub) { pressCard = c; pressRow = r; }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Point pt = ToCanvas(e.Location);

            if (dragCard != null)
            {
                if (!dragMoved && (Math.Abs(pt.X - dragStart.X) + Math.Abs(pt.Y - dragStart.Y) > 3)) dragMoved = true;
                if (dragMoved)
                {
                    int nx = dragFrom.X + (pt.X - dragStart.X);
                    int ny = Math.Max(0, dragFrom.Y + (pt.Y - dragStart.Y));
                    int maxX = Math.Max(Edge, ClientSize.Width - dragCard.W - Edge);
                    dragCard.X = Math.Min(Math.Max(Edge, nx), maxX);
                    dragCard.Y = ny;
                    Cursor = Cursors.SizeAll;
                    Invalidate();
                }
                return;
            }

            DeskCard hc = CardAt(pt);
            DeskRow hr = hc == null ? null : RowAt(hc, pt);
            if (hr != null && hr.IsSub) hr = null;
            if (hc != hoverCard || hr != hoverRow)
            {
                hoverCard = hc;
                hoverRow = hr;
                Cursor = hc == null ? Cursors.Default : (hr != null ? Cursors.Hand : Cursors.SizeAll);
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (dragCard != null)
            {
                DeskCard c = dragCard;
                dragCard = null;
                Cursor = Cursors.Default;
                if (dragMoved)
                {
                    DeskPrefs.Put(c.Key, c.X, c.Y, c.W, c.Folded);
                    DeskPrefs.Save();
                    foot = "「" + c.Title + "」停在 " + c.X + ", " + c.Y + " · 位置已记进 U 盘";
                }
                else ToggleFold(c);
                Invalidate();
                return;
            }

            if (pressCard != null)
            {
                DeskCard c = pressCard;
                DeskRow r = pressRow;
                pressCard = null;
                pressRow = null;
                if (c.NewBranch) { if (tree != null) tree.NewBranch(); return; }
                if (c.Note) { mf.ShowTreeView(); if (tree != null) tree.SelectUnmatched(); return; }
                if (r != null) ToggleRow(r);
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            Point pt = ToCanvas(e.Location);
            DeskCard c = CardAt(pt);
            DeskRow r = RowAt(c, pt);
            if (r == null || r.Slot == null) return;
            // 台面看全局，挑候选还是回树里（那边有完整列表和套装展开）
            mf.ShowSlotInTree(r.Slot);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int max = Math.Max(0, ContentH() - ClientSize.Height);
            if (max <= 0) { if (scrollY != 0) { scrollY = 0; Invalidate(); } return; }
            int v = scrollY - Math.Sign(e.Delta) * 72;
            v = Math.Max(0, Math.Min(max, v));
            if (v == scrollY) return;
            scrollY = v;
            Invalidate();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Up || keyData == Keys.Down || keyData == Keys.PageUp || keyData == Keys.PageDown || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            int max = Math.Max(0, ContentH() - ClientSize.Height);
            int step = e.KeyCode == Keys.PageDown || e.KeyCode == Keys.PageUp ? ClientSize.Height - 40 : 60;
            if (e.KeyCode == Keys.Down || e.KeyCode == Keys.PageDown) scrollY = Math.Min(max, scrollY + step);
            else if (e.KeyCode == Keys.Up || e.KeyCode == Keys.PageUp) scrollY = Math.Max(0, scrollY - step);
            else if (e.KeyCode == Keys.Home) scrollY = 0;
            else if (e.KeyCode == Keys.End) scrollY = max;
            else return;
            Invalidate();
        }

        private void ToggleFold(DeskCard c)
        {
            c.Folded = !c.Folded;
            CalcHeight(c);
            DeskPrefs.Put(c.Key, c.X, c.Y, c.W, c.Folded);
            DeskPrefs.Save();
            foot = "「" + c.Title + "」" + (c.Folded ? "收起" : "展开");
            Invalidate();
        }

        /// <summary>点一行 = 选 / 不选它。和树里的勾选是同一份状态，这边点了那边也跟着变。</summary>
        private void ToggleRow(DeskRow row)
        {
            ModGroup g = GroupOf(row);
            if (g == null && row.Slot != null && row.Slot.Items.Count > 0) g = row.Slot.Items[0];
            if (g == null) { foot = "这个位置库里还没有 MOD，右键「指定位置」才能填"; Invalidate(); return; }

            if (mf.CountChecked(g) > 0) { mf.UncheckGroup(g); foot = "已取消选择「" + g.Name + "」"; }
            else { mf.CheckGroupDefault(g); foot = "已选中「" + g.Name + "」（补丁件按默认全勾，要细调按双击回树里）"; }

            mf.SyncPicks();
            Invalidate();
        }

        private void ShowMenu(Point pt, DeskCard c)
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Font = Theme.UI;
            if (c != null && c.Branch)
            {
                Slot bs = tree == null ? null : tree.BranchByName(c.BranchName);
                if (bs != null)
                    m.Items.Add("重命名分支…").Click += delegate { tree.RenameBranch(bs); };
                m.Items.Add(new ToolStripSeparator());
            }
            m.Items.Add("整理台面（按顺序重排）").Click += delegate
            {
                DeskPrefs.Clear();
                Rebuild();
                foot = "台面已重排 —— 想自己摆就拖卡片标题";
            };
            m.Items.Add("全部折叠").Click += delegate { SetAllFolded(true); };
            m.Items.Add("全部展开").Click += delegate { SetAllFolded(false); };
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("新建分支…").Click += delegate { if (tree != null) tree.NewBranch(); };
            m.Items.Add("切回方案树").Click += delegate { mf.ShowTreeView(); };
            m.Show(this, new Point(Math.Min(pt.X, Math.Max(0, Width - 160)), Math.Min(pt.Y - scrollY, Height - 10)));
        }

        private void SetAllFolded(bool folded)
        {
            foreach (DeskCard c in cards)
            {
                if (c.NewBranch) continue;
                c.Folded = folded;
                CalcHeight(c);
                DeskPrefs.Put(c.Key, c.X, c.Y, c.W, c.Folded);
            }
            DeskPrefs.Save();
            foot = folded ? "全部收起" : "全部展开";
            Invalidate();
        }

        /// <summary>勾选变了以后刷新状态（树那边同步过来时调）。</summary>
        internal void Sync()
        {
            if (cards.Count == 0) return;
            Invalidate();
        }

        // ---------------------------------------------------------------- 自检

        /// <summary>
        /// 台面自检：建卡、拖动落位、折叠、布局存取、还原。
        /// 结束会把 台面布局.ini 逐字节还原，不污染用户摆好的台面。
        /// </summary>
        internal int SelfTest()
        {
            int bad = 0;
            string f = DeskPrefs.FileOf();
            string backup = null;
            bool had = false;
            try { had = File.Exists(f); if (had) backup = File.ReadAllText(f, Encoding.UTF8); }
            catch { }

            try
            {
                DeskPrefs.Clear();
                Rebuild();

                if (cards.Count == 0) { bad++; Core.Log("FAIL", "DESK 台面一张卡都没建出来"); }
                else Core.Log("INFO", "DESK 建卡 " + cards.Count + " 张");

                // 内容行必须挂在卡里，不能整张空卡
                int rows = 0;
                foreach (DeskCard c in cards) rows += c.Rows.Count;
                if (rows == 0) { bad++; Core.Log("FAIL", "DESK 所有卡都是空卡"); }
                else Core.Log("INFO", "DESK 卡内行合计 " + rows + " 行");

                // 拖动：把第一张卡搬到 (240, 180)，位置必须落进文件
                DeskCard first = cards[0];
                first.X = 240; first.Y = 180;
                DeskPrefs.Put(first.Key, first.X, first.Y, first.W, false);
                DeskPrefs.Save();
                DeskPrefs.Reload();
                DeskPos p = DeskPrefs.Of(first.Key);
                if (p == null || p.X != 240 || p.Y != 180) { bad++; Core.Log("FAIL", "DESK 拖动的落位没存进 台面布局.ini"); }
                else Core.Log("INFO", "DESK 拖动落位已存: " + first.Key + " → 240,180");

                // 折叠：高度必须真的变矮
                int hOpen = first.H;
                ToggleFold(first);
                if (first.Folded && first.H >= hOpen) { bad++; Core.Log("FAIL", "DESK 折叠后卡片没变矮"); }
                ToggleFold(first);
                if (first.Folded) { bad++; Core.Log("FAIL", "DESK 再点一次没收回来"); }

                // 重新读档后位置还在（重启程序那条路径）
                DeskPrefs.Reload();
                Rebuild();
                DeskCard again = null;
                foreach (DeskCard c in cards) if (c.Key == first.Key) again = c;
                if (again == null) { bad++; Core.Log("FAIL", "DESK 重建后找不到刚才那张卡"); }
                else if (!again.Saved) { bad++; Core.Log("FAIL", "DESK 重建后没认出这张卡是拖过的"); }
                else Core.Log("INFO", "DESK 重建后仍记得落位: " + again.X + "," + again.Y);

                // 命中：点标题行不该被当成行；点第一行必须命中那一行
                DeskCard any = null;
                foreach (DeskCard c in cards) if (!c.NewBranch && c.Rows.Count > 0 && !c.Folded) { any = c; break; }
                if (any != null)
                {
                    DeskRow r0 = any.Rows[0];
                    DeskRow hit = RowAt(any, new Point(any.X + 20, any.Y + r0.Top + 4));
                    if (hit != r0) { bad++; Core.Log("FAIL", "DESK 行命中错了"); }
                    DeskRow hdr = RowAt(any, new Point(any.X + 20, any.Y + 6));
                    if (hdr != null) { bad++; Core.Log("FAIL", "DESK 标题行被当成了内容行"); }
                }
            }
            catch (Exception ex)
            {
                bad++;
                Core.Log("FAIL", "DESK 自检抛异常: " + ex.Message);
            }
            finally
            {
                try
                {
                    if (had && backup != null) File.WriteAllText(f, backup, Encoding.UTF8);
                    else if (File.Exists(f)) File.Delete(f);
                }
                catch { }
                DeskPrefs.Reload();
                try { Rebuild(); } catch { }
            }
            return bad;
        }
    }
}

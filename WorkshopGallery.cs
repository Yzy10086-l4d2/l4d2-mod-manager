using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Installer
{
    /// <summary>
    /// 卡片网格：一张图 + 一个名字，按窗口宽度自动决定列数。
    ///
    /// 为什么自绘而不是 ListView：ListView 做不出等高卡片、图片裁切、悬停描边这些，
    /// 硬塞大图标模式又会把标题挤成一行省略号。这里一屏最多几十张卡，自绘更省事也更像样。
    ///
    /// 缩略图一律异步：先画面的壳，图到了再补，绝不让滚动卡在网络上。
    /// 只给「当前视口里」的卡片排队，翻到哪儿下到哪儿 —— 一页 30 个全下会白烧流量。
    /// </summary>
    internal sealed class WsCardGrid : Control
    {
        const int ThumbW = 480;        // 统一按这个尺寸下载，缩放交给 DrawImage，窗口拉伸不用重下
        const int ThumbH = 270;

        private List<WsItem> items = new List<WsItem>();
        private readonly Dictionary<string, Image> imgs = new Dictionary<string, Image>(StringComparer.Ordinal);
        private readonly HashSet<string> pending = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<WsItem> queue = new Queue<WsItem>();
        private readonly object gate = new object();   // imgs / pending / queue 共用：跨线程读写必须同一把锁
        private int hover = -1;
        // 卡片位图缓存：每张卡渲染一次成位图，滚动/重绘时只贴图。
        // 没有它，每次滚一格要把可见的十几张卡全部现场重画（圆角路径 + 图片裁切 + 两段
        // WordBreak 文字排版），这就是滚动发卡/卡顿的主因。
        private readonly Dictionary<string, Bitmap> cardCache = new Dictionary<string, Bitmap>(StringComparer.Ordinal);
        private int cacheW = -1;                // 缓存生成时的卡宽：宽度变了全部作废
        private int cols = 1, cardW = 240, cardH = 200, gap = 14, imgH = 135;
        private int lastW = -1;

        /// <summary>点中一张卡。</summary>
        public event Action<WsItem> Activated;

        /// <summary>快滑到底了 —— 调用方据此自动加载下一页（只报一次，翻页后重新计）。</summary>
        public event Action NearEnd;

        /// <summary>点了底部的「加载更多」。</summary>
        public event Action FooterClicked;

        /// <summary>卡片墙最下面那一行提示；空串 = 不占位。</summary>
        public string FooterText = "";
        /// <summary>这一行提示能不能点（加载更多 = 能，正在加载 / 已到底 = 不能）。</summary>
        public bool FooterClickable;

        /// <summary>样例模式：不联网，用代码画的色块占位，截图自检才能看出卡片排得对不对。</summary>
        public bool SampleMode;

        /// <summary>骨架屏：>0 时在真实卡片后面铺这么多张占位卡（搜索中 / 翻页中）。</summary>
        public int SkeletonCount;

        /// <summary>本地库里已经下载过的工坊 ID —— 卡片标「已下载」用，打开窗口时预扫 idmap 填进来。</summary>
        public readonly HashSet<string> DownloadedIds = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>真实卡片 + 骨架卡的总数（排版和视口判断都用它）。</summary>
        private int TotalCount { get { return items.Count + SkeletonCount; } }

        /// <summary>
        /// 预扫：把还没滑到的卡片缩略图先排进下载队列（排在队尾，不抢视口内的）。
        /// 用户滑到时图已在缓存里，卡片秒出图。
        /// </summary>
        public void PrimeThumbs(List<WsItem> list)
        {
            if (SampleMode || list == null) return;
            bool added = false;
            foreach (WsItem it in list)
            {
                if (it == null || it.Preview.Length == 0) continue;
                lock (gate)
                {
                    if (imgs.ContainsKey(it.Id) || pending.Contains(it.Id)) continue;
                    queue.Enqueue(it);
                    pending.Add(it.Id);
                }
                added = true;
            }
            if (added) EnsureWorker();
        }

        /// <summary>当前墙上所有的条目（调用方别再自己存一份，容易和追加对不上）。</summary>
        public IList<WsItem> Items { get { return items; } }

        private int nearRow = -1;         // 上一次报过「快到底」时的总行数：行数没变就不重复报

        public WsCardGrid()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Bg;
        }

        public void SetItems(List<WsItem> list)
        {
            items = list == null ? new List<WsItem>() : list;
            hover = -1;
            nearRow = -1;
            SkeletonCount = 0;
            Pulse.Forget(this);
            ClearCardCache();
            lock (gate)
            {
                foreach (Image im in imgs.Values) if (im != null) im.Dispose();
                imgs.Clear();
                pending.Clear();
                queue.Clear();
            }
            Relayout();
        }

        /// <summary>
        /// 往后追加一页。**不清图缓存** —— 已经下过的缩略图要留着，否则往下滑一页就把上面的图全丢了，
        /// 滑回去又得重下。这是 SetItems（换搜索结果）和 AppendItems（翻页）最关键的区别。
        /// </summary>
        public void AppendItems(List<WsItem> more)
        {
            if (more == null || more.Count == 0) return;
            items.AddRange(more);
            hover = -1;
            SkeletonCount = 0;
            Pulse.Forget(this);
            nearRow = -1;                       // 行数变了，允许再报一次「快到底」
            Relayout();
            ScrollHost h = Parent as ScrollHost;
            if (h != null) h.Relayout();
            Invalidate();
        }

        /// <summary>铺骨架占位卡（搜索开始时调用）。</summary>
        public void ShowSkeleton(int n)
        {
            SkeletonCount = Math.Max(0, n);
            hover = -1;
            if (SkeletonCount > 0) Pulse.Watch(this); else Pulse.Forget(this);
            Relayout();
            ScrollHost h = Parent as ScrollHost;
            if (h != null) h.Relayout();
            Invalidate();
        }

        /// <summary>撤掉骨架（结果到了调用）。</summary>
        public void HideSkeleton()
        {
            if (SkeletonCount == 0) return;
            SkeletonCount = 0;
            Pulse.Forget(this);
            Relayout();
            ScrollHost h = Parent as ScrollHost;
            if (h != null) h.Relayout();
            Invalidate();
        }

        /// <summary>按当前宽度重算列数、卡片尺寸和自己的总高度（ScrollHost 靠这个高度决定能不能滚）。</summary>
        public void Relayout()
        {
            int w = Math.Max(Dpi.S(200), Width);
            gap = Dpi.S(14);
            int minW = Dpi.S(210);
            cols = Math.Max(1, (w + gap) / (minW + gap));
            cardW = (w - gap * (cols - 1)) / cols;
            imgH = cardW * 9 / 16;
            cardH = imgH + Dpi.S(8) + Dpi.S(40) + Dpi.S(18);
            if (cardW != cacheW) ClearCardCache();      // 卡宽变了，旧位图全作废
            int rows = (TotalCount + cols - 1) / cols;
            int footH = FooterText.Length > 0 ? Dpi.S(64) : 0;
            int h = Math.Max(Dpi.S(60), rows * (cardH + gap) + gap + footH);
            if (Height != h) Height = h;
            Invalidate();
        }

        private void ClearCardCache()
        {
            foreach (Bitmap b in cardCache.Values) if (b != null) b.Dispose();
            cardCache.Clear();
            cacheW = -1;
        }

        /// <summary>单卡缓存失效 + 只重画那一格（缩略图到达 / 标记已下载用，别全墙重绘）。</summary>
        private void InvalidateCardById(string id)
        {
            cardCache.Remove(id);
            for (int i = 0; i < items.Count; i++)
                if (items[i].Id == id) { Invalidate(CardRect(i)); return; }
        }

        /// <summary>预扫/本地标记：把某个 ID 标成已下载并刷新那一格。</summary>
        public void MarkDownloaded(string id)
        {
            if (!DownloadedIds.Add(id)) return;
            InvalidateCardById(id);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Width == lastW) return;          // 只认宽度变化：设 Height 也会进这里，别打架
            lastW = Width;
            Relayout();
            ScrollHost h = Parent as ScrollHost;
            if (h != null) h.Relayout();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            if (k == Keys.Up || k == Keys.Down || k == Keys.PageUp || k == Keys.PageDown
                || k == Keys.Home || k == Keys.End) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            ScrollHost p = Parent as ScrollHost;
            if (p != null && p.CanScroll)
            {
                if (e.KeyCode == Keys.Down) { p.ScrollTo(p.Offset + Dpi.S(64)); e.Handled = true; return; }
                if (e.KeyCode == Keys.Up) { p.ScrollTo(p.Offset - Dpi.S(64)); e.Handled = true; return; }
                if (e.KeyCode == Keys.PageDown || e.KeyCode == Keys.PageUp
                    || e.KeyCode == Keys.Home || e.KeyCode == Keys.End)
                { p.ScrollByKey(e.KeyCode); e.Handled = true; return; }
            }
            base.OnKeyDown(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            ScrollHost p = Parent as ScrollHost;
            if (p != null && p.CanScroll) { p.ScrollTo(p.Offset - e.Delta); return; }
            base.OnMouseWheel(e);
        }

        private Rectangle CardRect(int i)
        {
            int r = i / cols, c = i % cols;
            return new Rectangle(c * (cardW + gap), gap + r * (cardH + gap), cardW, cardH);
        }

        /// <summary>底部那一行「加载更多 / 正在加载 / 已到底」的位置：最后一行卡片下面。</summary>
        private Rectangle FooterRect()
        {
            int rows = (TotalCount + cols - 1) / cols;
            int y = gap + rows * (cardH + gap);
            return new Rectangle(gap, y, Math.Max(0, Width - gap * 2), Dpi.S(48));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool overFooter = FooterText.Length > 0 && FooterClickable && FooterRect().Contains(e.Location);
            int hit = overFooter ? -2 : HitTest(e.Location);
            if (hit != hover)
            {
                hover = hit;
                Cursor = (hit >= 0 || overFooter) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hover != -1) { hover = -1; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();                                   // 点了就把焦点拿过来，滚轮/键盘才有落点
            if (FooterText.Length > 0 && FooterClickable && FooterRect().Contains(e.Location))
            {
                if (FooterClicked != null) FooterClicked();
                return;
            }
            int hit = HitTest(e.Location);
            if (hit >= 0 && Activated != null) Activated(items[hit]);
        }

        private int HitTest(Point p)
        {
            for (int i = 0; i < items.Count; i++)          // 骨架卡点不了
                if (CardRect(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Bg);

            // 只画视口内的：可视区外面那些画了也看不见，白耗 GDI。
            // 视口起点是【自己】的 Top —— ScrollHost 把它设成 -offset；画在宿主身上是错的：
            // 宿主固定不动（Top=56），用它算出来的视口永远停在墙的顶部，
            // 往下滚之后新区域根本不在「待画名单」里，滑出来的就是一段空白。
            int top = -Top;
            int viewH = Parent != null ? Parent.ClientSize.Height : Height;

            int total = TotalCount;
            for (int i = 0; i < total; i++)
            {
                Rectangle r = CardRect(i);
                if (r.Bottom < top - Dpi.S(40) || r.Top > top + viewH + Dpi.S(40)) continue;
                if (i >= items.Count) DrawSkeletonCard(g, r);
                else DrawCard(g, i, r);
            }

            DrawFooter(g, top, viewH);
            QueueVisible(top, viewH);
            CheckNearEnd(top, viewH);
        }

        /// <summary>底部那一行：加载更多 / 正在加载 / 已到底。滚到看不见就不画。</summary>
        private void DrawFooter(Graphics g, int top, int viewH)
        {
            if (FooterText.Length == 0) return;
            Rectangle r = FooterRect();
            if (r.Bottom < top || r.Top > top + viewH) return;

            bool hot = hover == -2;
            using (GraphicsPath p = Theme.Round(r, Dpi.S(10)))
            {
                using (SolidBrush b = new SolidBrush(hot ? Theme.SurfaceAlt : Theme.Surface)) g.FillPath(b, p);
                using (Pen pen = new Pen(hot ? Theme.Accent : Theme.CardEdge))
                {
                    if (!FooterClickable) pen.DashStyle = DashStyle.Dash;
                    g.DrawPath(pen, p);
                }
            }
            TextRenderer.DrawText(g, FooterText, FooterClickable ? Theme.UIBold : Theme.UISmall, r,
                FooterClickable ? Theme.Ink : Theme.Ink2,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        /// <summary>
        /// 快滑到底就通知调用方去取下一页。用 BeginInvoke 抛出去：这里还在 OnPaint 里，
        /// 直接同步回调会在绘制过程中改 items / 改高度，容易画出半张墙或递归重绘。
        /// </summary>
        private void CheckNearEnd(int top, int viewH)
        {
            if (NearEnd == null || items.Count == 0 || !IsHandleCreated) return;
            int rows = (TotalCount + cols - 1) / cols;
            if (rows <= 0) return;
            int seenRows = (top + viewH) / Math.Max(1, cardH + gap);
            if (seenRows < rows - 2) return;          // 还没滑到倒数第二行
            if (nearRow == rows) return;              // 这一屏已经报过了
            nearRow = rows;
            try { BeginInvoke((MethodInvoker)delegate { if (NearEnd != null) NearEnd(); }); }
            catch { }
        }

        /// <summary>骨架卡：灰底封面块 + 三条占位灰条，再加一道扫光。没有内容、点不了，只负责告诉用户「在加载」。</summary>
        private void DrawSkeletonCard(Graphics g, Rectangle r)
        {
            using (GraphicsPath p = Theme.Round(r, Dpi.S(10)))
            {
                using (SolidBrush b = new SolidBrush(Theme.Surface)) g.FillPath(b, p);
                using (Pen pen = new Pen(Theme.CardEdge)) g.DrawPath(pen, p);
            }
            Rectangle ir = new Rectangle(r.X + 1, r.Y + 1, r.Width - 2, imgH);
            using (SolidBrush b = new SolidBrush(Theme.SurfaceAlt)) g.FillRectangle(b, ir);
            using (SolidBrush b = new SolidBrush(Theme.Chip))
            {
                int tx = r.X + Dpi.S(10);
                int w = r.Width - Dpi.S(20);
                g.FillRectangle(b, tx, ir.Bottom + Dpi.S(10), (int)(w * 0.72), Dpi.S(11));
                g.FillRectangle(b, tx, ir.Bottom + Dpi.S(27), (int)(w * 0.46), Dpi.S(11));
                g.FillRectangle(b, tx, ir.Bottom + Dpi.S(46), (int)(w * 0.3), Dpi.S(9));
            }
            // 扫光：静态骨架看起来像"卡住了"，一道走动的光才像"在加载"
            Region old = g.Clip;
            using (GraphicsPath p = Theme.Round(r, Dpi.S(10)))
            {
                g.SetClip(p);
                Pulse.PaintSheen(g, r);
            }
            g.Clip = old;
        }

        private void DrawCard(Graphics g, int i, Rectangle r)
        {
            WsItem it = items[i];
            bool hot = i == hover;

            // 卡片主体走位图缓存：渲染一次，之后每帧只贴图
            Bitmap b = GetCardBitmap(it);
            if (b != null) g.DrawImage(b, r.Location);

            // 边框现场画（悬停高亮是易变状态，不进缓存）
            using (GraphicsPath p = Theme.Round(r, Dpi.S(10)))
            using (Pen pen = new Pen(hot ? Theme.Accent : Theme.CardEdge))
                g.DrawPath(pen, p);
        }

        /// <summary>取一张卡的缓存位图；没有就现场渲染并缓存。</summary>
        private Bitmap GetCardBitmap(WsItem it)
        {
            if (cardW != cacheW) ClearCardCache();
            Bitmap b;
            if (cardCache.TryGetValue(it.Id, out b) && b != null) return b;

            int w = Math.Max(1, cardW), h = Math.Max(1, cardH);
            b = new Bitmap(w, h);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                PaintCard(g, it, w, h);
            }
            cardCache[it.Id] = b;
            if (cardCache.Count > 400) ClearCardCache();    // 防涨：翻太多页时整体重来
            return b;
        }

        /// <summary>把一张卡画到 (0,0) 起点的 w×h 画布上（供缓存与预览共用）。边框不在这里画。</summary>
        private void PaintCard(Graphics g, WsItem it, int w, int h)
        {
            using (GraphicsPath p = Theme.Round(new Rectangle(0, 0, w, h), Dpi.S(10)))
            using (SolidBrush b = new SolidBrush(Theme.Surface))
                g.FillPath(b, p);

            Rectangle ir = new Rectangle(1, 1, w - 2, imgH);
            using (SolidBrush b = new SolidBrush(Theme.SurfaceAlt)) g.FillRectangle(b, ir);   // 信箱底色
            Image img = ThumbOf(it.Id);
            if (img != null)
            {
                // 完整显示：等比缩到能整个放进卡里（用户明确要看到完整封面，不裁切）
                double k = Math.Min((double)ir.Width / img.Width, (double)ir.Height / img.Height);
                int iw = Math.Max(1, (int)(img.Width * k)), ih = Math.Max(1, (int)(img.Height * k));
                Rectangle dst = new Rectangle(ir.X + (ir.Width - iw) / 2, ir.Y + (ir.Height - ih) / 2, iw, ih);
                Region old = g.Clip;
                g.SetClip(ir);
                g.DrawImage(img, dst);
                g.Clip = old;
            }
            else
            {
                using (SolidBrush b = new SolidBrush(Theme.SurfaceAlt)) g.FillRectangle(b, ir);
                TextRenderer.DrawText(g, it.Preview.Length == 0 ? "无预览图" : "加载中…",
                    Theme.UISmall, ir, Theme.Ink3,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            int ty = ir.Bottom + Dpi.S(6);
            Rectangle tr = new Rectangle(Dpi.S(10), ty, w - Dpi.S(20), Dpi.S(36));
            TextRenderer.DrawText(g, it.Title.Length == 0 ? "(无标题)" : it.Title, Theme.RowTitle, tr, Theme.Ink,
                TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            string meta = "";
            if (it.Subs > 0) meta += "订阅 " + WsText.Compact(it.Subs);
            if (it.Size > 0) meta += (meta.Length > 0 ? "  ·  " : "") + Core.Human(it.Size);
            if (meta.Length == 0) meta = it.IsCollection ? "合集" : "ID " + it.Id;
            bool owned = DownloadedIds.Contains(it.Id);
            if (owned) meta += (meta.Length > 0 ? "  ·  " : "") + "已下载";
            TextRenderer.DrawText(g, meta, Theme.UISmall,
                new Rectangle(Dpi.S(10), tr.Bottom, w - Dpi.S(20), Dpi.S(16)),
                owned ? Theme.Ok : Theme.Ink3,
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        /// <summary>取已缓存的缩略图；没有返回 null。锁住读，避免撞上后台线程正在写。</summary>
        private Image ThumbOf(string id)
        {
            lock (gate)
            {
                Image img;
                return imgs.TryGetValue(id, out img) ? img : null;
            }
        }

        /// <summary>把视口内的卡片排进下载队列（一次只跑一个线程，图上不来就画占位）。</summary>
        private void QueueVisible(int top, int viewH)
        {
            if (SampleMode) return;
            bool added = false;
            for (int i = 0; i < items.Count; i++)
            {
                Rectangle r = CardRect(i);
                if (r.Bottom < top - Dpi.S(80) || r.Top > top + viewH + Dpi.S(80)) continue;
                WsItem it = items[i];
                if (it.Preview.Length == 0) continue;
                lock (gate)
                {
                    if (imgs.ContainsKey(it.Id) || pending.Contains(it.Id)) continue;
                    queue.Enqueue(it);
                    pending.Add(it.Id);
                }
                added = true;
            }
            if (added) EnsureWorker();
        }

        private const int WorkerCount = 6;      // 并行取图：30 张从 ~15 秒缩到 ~3 秒（CDN 扛得住 6 连）
        private int workers;                    // 存活的取图线程数

        private void EnsureWorker()
        {
            lock (gate)
            {
                if (queue.Count == 0) return;
                while (workers < WorkerCount)
                {
                    workers++;
                    Thread th = new Thread(WorkerLoop);
                    th.IsBackground = true;
                    th.Priority = ThreadPriority.BelowNormal;
                    th.Start();
                }
            }
        }

        private void WorkerLoop()
        {
            while (true)
            {
                WsItem it = null;
                lock (gate)
                {
                    if (queue.Count == 0) { workers--; return; }
                    it = queue.Dequeue();
                }
                Image img = null;
                try { img = WsThumb.Load(it.Preview, it.Id, ThumbW, ThumbH); }
                catch { }
                lock (gate)
                {
                    pending.Remove(it.Id);
                    if (img != null) imgs[it.Id] = img;
                    else imgs[it.Id] = null;      // 记成「下过了但没有」，别无限重试
                }
                try { BeginInvoke(new MethodInvoker(delegate { InvalidateCardById(it.Id); })); }
                catch { workers--; return; }
            }
        }

        /// <summary>样例模式：造几张带序号的色块当缩略图，截图上能看出比例与裁切对不对。</summary>
        public void SeedSampleImages()
        {
            SampleMode = true;
            Color[] cs = new Color[] { Color.FromArgb(64, 96, 140), Color.FromArgb(120, 78, 70),
                                       Color.FromArgb(70, 110, 92), Color.FromArgb(96, 74, 130) };
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Preview.Length == 0) continue;   // 本来就没预览图的，让它走空态，别拿色块盖住
                Bitmap b = new Bitmap(ThumbW, ThumbH);
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.Clear(cs[i % cs.Length]);
                    using (SolidBrush ib = new SolidBrush(Color.FromArgb(220, 255, 255, 255)))
                        g.DrawString("预览图 " + (i + 1), new Font("Microsoft YaHei UI", 22F, FontStyle.Bold),
                            ib, new PointF(24, ThumbH / 2 - 24));
                }
                lock (gate) { imgs[items[i].Id] = b; }
            }
            Invalidate();
        }
    }
}

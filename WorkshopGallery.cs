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
        private Thread worker;
        private int hover = -1;
        private int cols = 1, cardW = 240, cardH = 200, gap = 14, imgH = 135;
        private int lastW = -1;

        /// <summary>点中一张卡。</summary>
        public event Action<WsItem> Activated;

        /// <summary>样例模式：不联网，用代码画的色块占位，截图自检才能看出卡片排得对不对。</summary>
        public bool SampleMode;

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
            foreach (Image im in imgs.Values) if (im != null) im.Dispose();
            imgs.Clear();
            pending.Clear();
            lock (queue) queue.Clear();
            Relayout();
        }

        /// <summary>按当前宽度重算列数、卡片尺寸和自己的总高度（ScrollHost 靠这个高度决定能不能滚）。</summary>
        public void Relayout()
        {
            int w = Math.Max(Dpi.S(200), Width);
            gap = Dpi.S(14);
            int minW = Dpi.S(230);
            cols = Math.Max(1, (w + gap) / (minW + gap));
            cardW = (w - gap * (cols - 1)) / cols;
            imgH = cardW * 9 / 16;
            cardH = imgH + Dpi.S(8) + Dpi.S(40) + Dpi.S(18);
            int rows = (items.Count + cols - 1) / cols;
            int h = Math.Max(Dpi.S(60), rows * (cardH + gap) + gap);
            if (Height != h) Height = h;
            Invalidate();
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

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hit = HitTest(e.Location);
            if (hit != hover)
            {
                hover = hit;
                Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
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
            int hit = HitTest(e.Location);
            if (hit >= 0 && Activated != null) Activated(items[hit]);
        }

        private int HitTest(Point p)
        {
            for (int i = 0; i < items.Count; i++)
                if (CardRect(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Bg);

            // 只画视口内的：可视区外面那些画了也看不见，白耗 GDI
            int top = Parent != null ? -Parent.Top : 0;
            int viewH = Parent != null ? Parent.ClientSize.Height : Height;

            for (int i = 0; i < items.Count; i++)
            {
                Rectangle r = CardRect(i);
                if (r.Bottom < top - Dpi.S(40) || r.Top > top + viewH + Dpi.S(40)) continue;
                DrawCard(g, i, r);
            }

            QueueVisible(top, viewH);
        }

        private void DrawCard(Graphics g, int i, Rectangle r)
        {
            WsItem it = items[i];
            bool hot = i == hover;

            using (GraphicsPath p = Theme.Round(r, Dpi.S(10)))
            {
                using (SolidBrush b = new SolidBrush(Theme.Surface)) g.FillPath(b, p);
                using (Pen pen = new Pen(hot ? Theme.Accent : Theme.CardEdge))
                    g.DrawPath(pen, p);
            }

            Rectangle ir = new Rectangle(r.X + 1, r.Y + 1, r.Width - 2, imgH);
            Image img;
            if (imgs.TryGetValue(it.Id, out img) && img != null)
            {
                // 等比填满并居中裁切：封面比例五花八门，直接拉伸会变形
                double k = Math.Max((double)ir.Width / img.Width, (double)ir.Height / img.Height);
                int w = (int)(img.Width * k), h = (int)(img.Height * k);
                Rectangle dst = new Rectangle(ir.X + (ir.Width - w) / 2, ir.Y + (ir.Height - h) / 2, w, h);
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
            Rectangle tr = new Rectangle(r.X + Dpi.S(10), ty, r.Width - Dpi.S(20), Dpi.S(36));
            TextRenderer.DrawText(g, it.Title.Length == 0 ? "(无标题)" : it.Title, Theme.RowTitle, tr, Theme.Ink,
                TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            string meta = "";
            if (it.Subs > 0) meta += "订阅 " + WsText.Compact(it.Subs);
            if (it.Size > 0) meta += (meta.Length > 0 ? "  ·  " : "") + Core.Human(it.Size);
            if (meta.Length == 0) meta = it.IsCollection ? "合集" : "ID " + it.Id;
            TextRenderer.DrawText(g, meta, Theme.UISmall,
                new Rectangle(r.X + Dpi.S(10), tr.Bottom, r.Width - Dpi.S(20), Dpi.S(16)),
                Theme.Ink3, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
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
                if (it.Preview.Length == 0 || imgs.ContainsKey(it.Id) || pending.Contains(it.Id)) continue;
                lock (queue) { queue.Enqueue(it); pending.Add(it.Id); }
                added = true;
            }
            if (added) EnsureWorker();
        }

        private void EnsureWorker()
        {
            lock (queue)
            {
                if (worker != null && worker.IsAlive) return;
                worker = new Thread(delegate()
                {
                    while (true)
                    {
                        WsItem it = null;
                        lock (queue)
                        {
                            if (queue.Count == 0) { worker = null; return; }
                            it = queue.Dequeue();
                        }
                        Image img = null;
                        try { img = WsThumb.Load(it.Preview, it.Id, ThumbW, ThumbH); }
                        catch { }
                        lock (queue)
                        {
                            pending.Remove(it.Id);
                            if (img != null) imgs[it.Id] = img;
                            else imgs[it.Id] = null;      // 记成「下过了但没有」，别无限重试
                        }
                        try { BeginInvoke(new MethodInvoker(Invalidate)); }
                        catch { return; }
                    }
                });
                worker.IsBackground = true;
                worker.Priority = ThreadPriority.BelowNormal;
                worker.Start();
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
                Bitmap b = new Bitmap(ThumbW, ThumbH);
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.Clear(cs[i % cs.Length]);
                    using (SolidBrush ib = new SolidBrush(Color.FromArgb(220, 255, 255, 255)))
                        g.DrawString("预览图 " + (i + 1), new Font("Microsoft YaHei UI", 22F, FontStyle.Bold),
                            ib, new PointF(24, ThumbH / 2 - 24));
                }
                imgs[items[i].Id] = b;
            }
            Invalidate();
        }
    }
}

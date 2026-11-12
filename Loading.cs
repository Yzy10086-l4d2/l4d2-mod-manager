using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Installer
{
    /// <summary>
    /// 一张"内容正在路上"的占位：骨架块 + 扫光。所有动画共用一个 33ms 定时器，
    /// 谁要动就 Watch 一下，隐藏时 Forget —— 不给每个控件各开一个 Timer。
    /// </summary>
    internal static class Pulse
    {
        private static readonly List<Control> targets = new List<Control>();
        private static readonly Timer timer = new Timer();
        private static int tick;

        static Pulse()
        {
            timer.Interval = 33;
            timer.Tick += delegate
            {
                tick++;
                for (int i = targets.Count - 1; i >= 0; i--)
                {
                    Control c = targets[i];
                    if (c == null || c.IsDisposed) { targets.RemoveAt(i); continue; }
                    if (!c.Visible) continue;
                    c.Invalidate();
                }
                if (targets.Count == 0) timer.Stop();
            };
        }

        /// <summary>关掉"减少动效"时只画静态骨架，不扫光。</summary>
        public static bool Animating { get { return !Core.Cfg.ReduceMotion; } }

        public static void Watch(Control c)
        {
            if (c == null) return;
            if (!targets.Contains(c)) targets.Add(c);
            if (Animating && !timer.Enabled) timer.Start();
        }

        public static void Forget(Control c)
        {
            if (c == null) return;
            targets.Remove(c);
            if (targets.Count == 0) timer.Stop();
        }

        /// <summary>0..1 循环，一次扫光约 1.4 秒。</summary>
        public static float Phase { get { return (tick % 42) / 42f; } }

        /// <summary>在给定矩形上刷一道斜向高光（调用方负责裁剪，卡片才不会画出圆角）。</summary>
        public static void PaintSheen(Graphics g, Rectangle r)
        {
            if (!Animating || r.Width <= 0 || r.Height <= 0) return;
            int band = Math.Max(48, r.Width / 2);
            int x = r.X - band + (int)((r.Width + band * 2) * Phase);
            Color c = Theme.IsDark ? Color.FromArgb(26, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0);
            try
            {
                using (LinearGradientBrush br = new LinearGradientBrush(
                    new Rectangle(x, r.Y, Math.Max(1, band), Math.Max(1, r.Height)),
                    Color.Transparent, Color.Transparent, LinearGradientMode.Horizontal))
                {
                    ColorBlend blend = new ColorBlend(3);
                    blend.Colors = new Color[] { Color.Transparent, c, Color.Transparent };
                    blend.Positions = new float[] { 0f, 0.5f, 1f };
                    br.InterpolationColors = blend;
                    g.FillRectangle(br, x, r.Y, band, r.Height);
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// 右栏的加载占位。两种形态：
    ///  · 全屏（内容还是空的）：标题 + 骨架块，告诉用户"在读了，别急着关"；
    ///  · 细条（已经有内容，只是在重扫）：顶部一条进度光带，不遮住现有内容。
    /// </summary>
    internal sealed class LoadingPane : Control
    {
        private string title = "";
        private string hint = "";
        private bool fullMode = true;
        private int startedAt;

        public LoadingPane()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
            Visible = false;
        }

        public bool IsFullMode { get { return fullMode; } }

        /// <summary>显示占位。fullMode=true 时铺满父容器，false 时只占顶部细条。</summary>
        public void ShowLoad(string text, string sub, bool fullMode)
        {
            title = text ?? "";
            hint = sub ?? "";
            this.fullMode = fullMode;
            startedAt = Environment.TickCount;
            Control p = Parent;
            if (fullMode)
            {
                Dock = DockStyle.Fill;
            }
            else
            {
                Dock = DockStyle.None;
                SetBounds(0, 0, p != null ? p.ClientSize.Width : Width, Dpi.S(26));
            }
            Visible = true;
            BringToFront();
            Pulse.Watch(this);
            Invalidate();
        }

        public void HideLoad()
        {
            if (!Visible) return;
            Visible = false;
            Pulse.Forget(this);
        }

        /// <summary>不转圈的静态说明（失败/空库这类终态）：占满面板，但没有"还在加载"的暗示。</summary>
        public void ShowMessage(string text, string sub)
        {
            title = text ?? "";
            hint = sub ?? "";
            fullMode = true;
            startedAt = Environment.TickCount;
            Dock = DockStyle.Fill;
            Visible = true;
            BringToFront();
            Pulse.Forget(this);
            Invalidate();
        }

        /// <summary>换文案但不重启动画（进度往前走时用）。</summary>
        public void UpdateText(string text, string sub)
        {
            title = text ?? "";
            if (sub != null) hint = sub;
            if (Visible) Invalidate();
        }

        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            if (Parent != null) Parent.SizeChanged += ParentResized;
        }

        private void ParentResized(object sender, EventArgs e)
        {
            if (!Visible || fullMode || Parent == null) return;
            SetBounds(0, 0, Parent.ClientSize.Width, Dpi.S(26));
        }

        /// <summary>画一行字：GDI+ 的 DrawString，不受 TextRenderer 在本条路径上的裁切影响。</summary>
        private static void DrawText(Graphics g, string text, Font font, Rectangle rect, Color color,
            StringAlignment align, StringAlignment line)
        {
            if (string.IsNullOrEmpty(text) || rect.Width <= 0 || rect.Height <= 0) return;
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = align;
                sf.LineAlignment = line;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                using (SolidBrush b = new SolidBrush(color))
                    g.DrawString(text, font, b, rect, sf);
            }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) Pulse.Watch(this); else Pulse.Forget(this);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Render(e.Graphics);
        }

        /// <summary>把整块占位画到给定 Graphics 上。OnPaint 和诊断截图共用这一份。</summary>
        internal void Render(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // GDI+ 画字：TextRenderer 走 GDI，在这种"直接画进位图"的路径上会被上下裁掉
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Theme.Surface);

            int elapsed = Math.Max(0, (Environment.TickCount - startedAt) / 1000);
            string clock = elapsed >= 2 ? " · " + elapsed + " s" : "";

            if (!fullMode)
            {
                PaintStrip(g, clock);
                return;
            }
            PaintFull(g, clock);
        }

        /// <summary>细条：左边一句「在做什么」，底部一条走动的光带。</summary>
        private void PaintStrip(Graphics g, string clock)
        {
            Rectangle r = ClientRectangle;
            DrawText(g, title + clock, Theme.UISmall,
                new Rectangle(Dpi.S(2), 0, Math.Max(0, r.Width - Dpi.S(4)), Math.Max(0, r.Height - Dpi.S(4))),
                Theme.Ink3, StringAlignment.Near, StringAlignment.Center);

            Rectangle track = new Rectangle(0, r.Height - Dpi.S(2), r.Width, Dpi.S(2));
            using (SolidBrush b = new SolidBrush(Theme.Chip)) g.FillRectangle(b, track);
            int seg = Math.Max(Dpi.S(60), r.Width / 4);
            int x = (int)((r.Width + seg) * (Pulse.Animating ? Pulse.Phase : 0.5f)) - seg;
            using (SolidBrush b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, x, track.Y, seg, track.Height);
        }

        /// <summary>
        /// 启动占位：品牌 + 真实步骤 + 不确定进度条。
        ///
        /// 这里以前铺的是骨架卡 + 扫光 —— 那套是给"内容形状已经确定、只等填数据"的场景（比如工坊卡片墙）。
        /// 启动阶段连最后显示成方案树还是卡片墙都不知道，铺一墙卡片形状是假信息：
        /// 用户会以为马上要出一堆卡。所以改成"说清正在做什么"，进度不可测就用不确定进度条。
        /// </summary>
        private void PaintFull(Graphics g, string clock)
        {
            Rectangle r = ClientRectangle;
            int pad = Dpi.S(24);
            int w = Math.Max(0, r.Width - pad * 2);

            string step = (Core.Phase ?? "").Trim();
            if (step.Length == 0) step = title;      // 还没进到具体阶段：退回调用方给的标题

            int hBrand = Dpi.S(24);
            int hStep = Dpi.S(22);
            int hBar = Dpi.S(3);
            int hHint = hint.Length > 0 || clock.Length > 0 ? Dpi.S(18) : 0;
            int gapBrand = Dpi.S(12), gapBar = Dpi.S(18), gapHint = Dpi.S(16);

            int blockH = hBrand + gapBrand + hStep + gapBar + hBar + (hHint > 0 ? gapHint + hHint : 0);
            int top = Math.Max(pad, (r.Height - blockH) / 2 - Dpi.S(36));   // 略高于正中：视觉重心更稳

            // 信息优先级：正在做什么 > 提示 > 应用名。
            // 品牌是标识不是标题，用小字；步骤才是这段时间里唯一在变的东西，放大。
            string ver = VersionLabel();
            string brandText = ver.Length > 0 ? "L4D2 MOD 管理工具  ·  " + ver : "L4D2 MOD 管理工具";
            DrawText(g, brandText, Theme.UISmall, new Rectangle(pad, top, w, Dpi.S(16)), Theme.Ink3,
                StringAlignment.Center, StringAlignment.Near);

            int y = top + hBrand + gapBrand;
            DrawText(g, step, Theme.H2, new Rectangle(pad, y - Dpi.S(2), w, hStep + Dpi.S(4)), Theme.Ink,
                StringAlignment.Center, StringAlignment.Near);

            // 不确定进度：一段亮条在轨道里来回走。进度本来就测不出来，就不假装有百分比。
            y += hStep + gapBar;
            int barW = Math.Min(Dpi.S(340), Math.Max(Dpi.S(180), w / 3));
            Rectangle track = new Rectangle((r.Width - barW) / 2, y, barW, hBar);
            using (GraphicsPath p = Theme.Round(track, Dpi.S(2)))
            using (SolidBrush b = new SolidBrush(Theme.Chip))
                g.FillPath(b, p);

            float trip = Pulse.Animating ? Trip() : 0.5f;
            int segW = Math.Max(Dpi.S(48), barW / 3);
            int sx = track.X + (int)((barW - segW) * trip);
            using (GraphicsPath p = Theme.Round(new Rectangle(sx, track.Y, segW, track.Height), Dpi.S(2)))
            using (SolidBrush b = new SolidBrush(Theme.Accent))
                g.FillPath(b, p);

            if (hHint > 0)
            {
                string sub = hint + clock;
                DrawText(g, sub, Theme.UISmall,
                    new Rectangle(pad, y + hBar + gapHint, w, hHint), Theme.Ink3,
                    StringAlignment.Center, StringAlignment.Near);
            }
        }

        /// <summary>来回往返：0→1→0，避免光条"跳回左边"。</summary>
        private static float Trip()
        {
            float t = Pulse.Phase;
            return t < 0.5f ? t * 2f : (1f - t) * 2f;
        }

        /// <summary>标题旁边的小版本号，和主窗口标题同源，不另写一份。</summary>
        private static string VersionLabel()
        {
            try
            {
                Version v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return v.Build > 0 ? "v" + v.Major + "." + v.Minor + "." + v.Build
                                   : "v" + v.Major + "." + v.Minor;
            }
            catch { return ""; }
        }
    }
}

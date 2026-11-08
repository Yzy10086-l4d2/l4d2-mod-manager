using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Installer
{
    /// <summary>
    /// 套装总览：一块深色看板，左边选大类，右边列这个类里每个位置装的是什么。
    ///
    /// 这里**不能改**：没有候选列、没有勾选框、没有「装这个」按钮。
    /// 要换 MOD 就点一行，跳回方案树 —— 改只在一个地方改，两边不会各说各话。
    /// </summary>
    internal sealed class OverviewCanvas : Control
    {
        private readonly MainForm mf;
        private OvModel model;
        private int selCat;
        private float scrollList, scrollDetail;

        private OvSlot hover;
        private int hoverCat = -1;
        private readonly ToolTip tip;

        // 沿用原来那块画板的暖色：这是「看板」，不跟浅色/深色表单主题走
        private static readonly Color BoardTop = Color.FromArgb(40, 30, 21);
        private static readonly Color BoardBot = Color.FromArgb(11, 7, 4);
        private static readonly Color Ink1 = Color.FromArgb(240, 230, 211);
        private static readonly Color Ink2 = Color.FromArgb(168, 155, 133);
        private static readonly Color Ink3 = Color.FromArgb(122, 112, 96);
        private static readonly Color Accent = Color.FromArgb(224, 138, 74);
        private static readonly Color Good = Color.FromArgb(111, 191, 139);
        private static readonly Color Amber = Color.FromArgb(226, 176, 96);

        private readonly Font fTitle = new Font("Microsoft YaHei", 15.5f, FontStyle.Bold);
        private readonly Font fBig = new Font("Microsoft YaHei", 14f, FontStyle.Bold);
        private readonly Font fSub = new Font("Microsoft YaHei", 9.3f);
        private readonly Font fCat = new Font("Microsoft YaHei", 12f);
        private readonly Font fNum = new Font("Microsoft YaHei", 9.8f);
        private readonly Font fRow = new Font("Microsoft YaHei", 11.2f);
        private readonly Font fMod = new Font("Microsoft YaHei", 10.4f);
        private readonly Font fSmall = new Font("Microsoft YaHei", 8.8f);

        internal OverviewCanvas(MainForm form)
        {
            mf = form;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = BoardBot;
            TabStop = true;

            tip = new ToolTip();
            tip.InitialDelay = 450;
            tip.ReshowDelay = 150;
            tip.AutoPopDelay = 30000;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                tip.Dispose();
                fTitle.Dispose(); fBig.Dispose(); fSub.Dispose(); fCat.Dispose();
                fNum.Dispose(); fRow.Dispose(); fMod.Dispose(); fSmall.Dispose();
            }
            base.Dispose(disposing);
        }

        // ================= 数据 =================

        /// <summary>从方案树重建。选中的大类按名字留住，滚动位置不动。</summary>
        internal void Rebuild()
        {
            string keep = (model != null && selCat >= 0 && selCat < model.Cats.Count)
                        ? model.Cats[selCat].Title : null;

            List<Slot> slots = mf.Tree == null ? null : mf.Tree.Slots;
            int add, del;
            mf.SchemeDiff(out add, out del);
            model = OvModel.Build(slots, mf.CountChecked,
                                  delegate(ModItem p) { return mf.IsChecked(p.Key); },
                                  mf.SchemeName, add, del);

            selCat = 0;
            if (keep != null)
                for (int i = 0; i < model.Cats.Count; i++)
                    if (model.Cats[i].Title == keep) { selCat = i; break; }

            if (scrollList < 0f) scrollList = 0f;
            if (scrollDetail < 0f) scrollDetail = 0f;
            hover = null;
            hoverCat = -1;
            Relayout();
        }

        /// <summary>勾选变了但树没重建：只刷新数字，滚动和选中都不动。</summary>
        internal void RefreshPicks()
        {
            Rebuild();
        }

        private void Relayout()
        {
            if (model == null) return;
            OvLayout.Arrange(model, selCat, ClientSize, scrollList, scrollDetail);
            Invalidate();
        }

        /// <summary>截图 / 自检用：选中某个大类。</summary>
        internal void SelectCatForShot(string title)
        {
            if (model == null) return;
            for (int i = 0; i < model.Cats.Count; i++)
                if (model.Cats[i].Title == title) { selCat = i; scrollDetail = 0f; break; }
            Relayout();
        }

        // ================= 绘制 =================

        protected override void OnPaint(PaintEventArgs e)
        {
            // 自绘画布抛异常会被 WinForms 盖一张红叉，问题就藏住了 —— 记进日志
            try { PaintAll(e); }
            catch (Exception ex) { Core.Log("FAIL", "OVPAINT " + ex.ToString()); }
        }

        private void PaintAll(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            Board(g);

            if (model == null || model.Cats.Count == 0)
            {
                Center(g, "正在读库…");
                return;
            }

            Header(g);

            GraphicsState save = g.Save();
            g.SetClip(new RectangleF(0f, OvLayout.ListY, ClientSize.Width,
                                     Math.Max(1f, OvLayout.ListBottom - OvLayout.ListY)));
            LeftList(g);
            Detail(g);
            g.Restore(save);

            ScrollBars(g);
            Hint(g);
        }

        private void Center(Graphics g, string s)
        {
            using (StringFormat sf = new StringFormat())
            using (SolidBrush b = new SolidBrush(Ink3))
            {
                sf.Alignment = StringAlignment.Center;
                g.DrawString(s, fCat, b, new RectangleF(0, ClientSize.Height * 0.45f, ClientSize.Width, 30), sf);
            }
        }

        private void Board(Graphics g)
        {
            using (LinearGradientBrush b = new LinearGradientBrush(
                       new Rectangle(0, 0, Math.Max(2, ClientSize.Width), Math.Max(2, ClientSize.Height)),
                       BoardTop, BoardBot, LinearGradientMode.Vertical))
                g.FillRectangle(b, ClientRectangle);

            using (GraphicsPath p = new GraphicsPath())
            {
                float cx = ClientSize.Width * 0.5f, cy = ClientSize.Height * 0.32f;
                float rx = ClientSize.Width * 0.52f, ry = ClientSize.Height * 0.62f;
                p.AddEllipse(cx - rx, cy - ry, rx * 2, ry * 2);
                using (PathGradientBrush pb = new PathGradientBrush(p))
                {
                    pb.CenterColor = Color.FromArgb(24, 120, 78, 44);
                    pb.SurroundColors = new Color[] { Color.FromArgb(0, 0, 0, 0) };
                    g.FillPath(pb, p);
                }
            }
        }

        private void Header(Graphics g)
        {
            float x = OvLayout.SidePad;
            string title = model.Profile.Length == 0 ? "当前勾选" : "「" + model.Profile + "」";

            using (SolidBrush b = new SolidBrush(Ink1))
                g.DrawString(title, fTitle, b, new RectangleF(x, 22f, ClientSize.Width - x * 2f - 240f, 30f));

            // 一句汇总：几个位置有 MOD、几个 vpk、多大
            float pct = model.SlotsTotal == 0 ? 0f : (float)model.SlotsOn / model.SlotsTotal;
            string sub = "这套：" + model.SlotsOn + " / " + model.SlotsTotal + " 个位置有 MOD"
                       + "　·　" + model.Parts + " 个 vpk"
                       + "　·　" + Core.Human(model.Size);
            using (SolidBrush b = new SolidBrush(Ink2))
                g.DrawString(sub, fSub, b, new RectangleF(x, 58f, ClientSize.Width - x * 2f - 240f, 20f));

            if (model.Profile.Length > 0 && (model.DiffAdd > 0 || model.DiffDel > 0))
            {
                string d = "和方案对不上：多 " + model.DiffAdd + " / 少 " + model.DiffDel;
                using (SolidBrush b = new SolidBrush(Amber))
                    g.DrawString(d, fSub, b, new RectangleF(x, 58f + 20f, ClientSize.Width - x * 2f, 18f));
            }

            // 右侧：覆盖率
            float bw = 200f, bx = ClientSize.Width - OvLayout.SidePad - bw;
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Far;
                using (SolidBrush b = new SolidBrush(pct > 0f ? Ink1 : Ink3))
                    g.DrawString((int)Math.Round(pct * 100f) + "%", fBig, b,
                                 new RectangleF(bx, 26f, bw, 24f), sf);
            }
            float by = 58f;
            using (GraphicsPath p = Round(new RectangleF(bx, by, bw, 5f), 2.5f))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(26, 255, 240, 215)))
                g.FillPath(b, p);
            float fill = Math.Max(0f, Math.Min(1f, pct)) * bw;
            if (fill > 3f)
                using (GraphicsPath p = Round(new RectangleF(bx, by, fill, 5f), 2.5f))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(190, Good)))
                    g.FillPath(b, p);
        }

        private void LeftList(Graphics g)
        {
            using (SolidBrush cap = new SolidBrush(Ink3))
                g.DrawString("大类 · " + model.Cats.Count, fSmall, cap, OvLayout.ListX + 2f, OvLayout.ListY + 3f);

            for (int i = 0; i < model.Cats.Count; i++)
            {
                OvCat c = model.Cats[i];
                if (c.Y + c.H < OvLayout.ListY - 2f || c.Y > OvLayout.ListBottom + 2f) continue;

                RectangleF r = new RectangleF(OvLayout.ListX, c.Y, OvLayout.ListW, c.H);
                bool sel = i == selCat;
                bool hot = i == hoverCat;

                using (GraphicsPath p = Round(r, 8f))
                {
                    int a = sel ? 26 : (hot ? 12 : 6);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(a, 255, 240, 215)))
                        g.FillPath(b, p);
                    Color bc = sel ? Color.FromArgb(200, Accent) : Color.FromArgb(hot ? 40 : 18, 255, 235, 205);
                    using (Pen pen = new Pen(bc, sel ? 1.4f : 1f))
                        g.DrawPath(pen, p);
                }
                if (sel)
                    using (GraphicsPath p = Round(new RectangleF(r.X + 1.5f, r.Y + 8f, 2.6f, r.Height - 16f), 1.2f))
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(220, Accent)))
                        g.FillPath(b, p);

                // 类名
                using (SolidBrush b = new SolidBrush(sel ? Ink1 : (c.On > 0 ? Ink2 : Ink3)))
                    g.DrawString(c.Title, fCat, b, new RectangleF(r.X + 14f, r.Y + 9f, r.Width - 88f, 20f));

                // 8/19
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Far;
                    using (SolidBrush b = new SolidBrush(c.On > 0 ? Good : Ink3))
                        g.DrawString(c.On + "/" + c.Total, fNum, b,
                                     new RectangleF(r.X, r.Y + 11f, r.Width - 14f, 18f), sf);
                }

                // 覆盖率细条
                float trackW = r.Width - 28f, ty = r.Y + r.Height - 11f;
                using (GraphicsPath p = Round(new RectangleF(r.X + 14f, ty, trackW, 3f), 1.5f))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(20, 255, 240, 215)))
                    g.FillPath(b, p);
                float k = c.Total == 0 ? 0f : (float)c.On / c.Total;
                float fw = trackW * k;
                if (fw > 2f)
                    using (GraphicsPath p = Round(new RectangleF(r.X + 14f, ty, fw, 3f), 1.5f))
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(170, Good)))
                        g.FillPath(b, p);
            }
        }

        private void Detail(Graphics g)
        {
            OvCat cat = OvLayout.CatAt(model, selCat);
            if (cat == null) return;

            using (SolidBrush cap = new SolidBrush(Ink3))
            {
                string t = cat.Title + " · " + cat.Slots.Count + " 个位置 · 装了 " + cat.On;
                g.DrawString(t, fSmall, cap, OvLayout.DetailX + 2f, OvLayout.DetailY + 3f);
            }

            // 小分类标题
            foreach (OvHead hd in model.Heads)
            {
                if (hd.Y + 18f < OvLayout.DetailY - 2f || hd.Y > OvLayout.DetailBottom) continue;
                using (SolidBrush b = new SolidBrush(Ink3))
                {
                    SizeF sz = g.MeasureString(hd.Text, fSmall);
                    g.DrawString(hd.Text, fSmall, b, OvLayout.DetailX + 2f, hd.Y + 3f);
                    float lx = OvLayout.DetailX + 2f + sz.Width + 10f;
                    using (Pen pen = new Pen(Color.FromArgb(16, 255, 240, 215), 1f))
                        g.DrawLine(pen, lx, hd.Y + 11f, OvLayout.DetailX + OvLayout.DetailW, hd.Y + 11f);
                }
            }

            foreach (OvSlot s in cat.Slots)
            {
                if (s.Y + s.H < OvLayout.DetailY - 2f || s.Y > OvLayout.DetailBottom) continue;
                Row(g, s, s == hover);
            }
        }

        private void Row(Graphics g, OvSlot s, bool hot)
        {
            RectangleF r = new RectangleF(OvLayout.DetailX, s.Y, OvLayout.DetailW, s.H);

            if (hot)
                using (GraphicsPath p = Round(r, 7f))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(14, 255, 240, 215)))
                    g.FillPath(b, p);

            // 已装：左边一条绿杠，扫一眼就知道这格有东西
            if (s.HasMod)
                using (GraphicsPath p = Round(new RectangleF(r.X + 1f, r.Y + 8f, 2.4f, r.Height - 16f), 1.2f))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(170, Good)))
                    g.FillPath(b, p);

            // 状态点
            float dx = r.X + 16f, dy = r.Y + r.Height * 0.5f;
            if (s.HasMod)
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(230, Good)))
                    g.FillEllipse(b, dx - 3.5f, dy - 3.5f, 7f, 7f);
            }
            else if (s.Empty)
            {
                using (Pen pen = new Pen(Color.FromArgb(90, Ink3), 1.2f))
                    g.DrawEllipse(pen, dx - 3.5f, dy - 3.5f, 7f, 7f);
            }
            else
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(70, Ink3)))
                    g.FillEllipse(b, dx - 2.2f, dy - 2.2f, 4.4f, 4.4f);
            }

            float nameW = OvLayout.DetailW >= 460f ? 168f : 128f;
            float sizeW = 92f, stateW = 84f;

            // 位置名
            using (SolidBrush b = new SolidBrush(s.HasMod ? Ink1 : (s.Empty ? Ink3 : Ink2)))
                g.DrawString(s.Name, fRow, b,
                    new RectangleF(r.X + 30f, r.Y + 8f, nameW, 20f), Ellipsis());

            // 右边到左：大小 / 状态 / MOD 名
            float sizeX = r.X + r.Width - sizeW;
            float stateX = sizeX - stateW;
            float modX = r.X + 30f + nameW + 12f;
            float modW = Math.Max(60f, stateX - modX - 10f);

            string sizeText = "";
            if (s.Size > 0) sizeText = Core.Human(s.Size);
            else if (s.Partial) sizeText = s.Parts + "/" + s.AllParts + " vpk";
            if (sizeText.Length > 0)
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Far;
                    using (SolidBrush b = new SolidBrush(Ink3))
                        g.DrawString(sizeText, fSmall, b, new RectangleF(sizeX, r.Y + 11f, sizeW, 16f), sf);
                }

            string st = "";
            if (s.On.Count > 0)
            {
                Color ink, bg;
                st = MainForm.StatusOf(s.On[0], out ink, out bg);
            }
            if (st.Length > 0)
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Far;
                    using (SolidBrush b = new SolidBrush(StateInk(st)))
                        g.DrawString(st, fSmall, b, new RectangleF(stateX, r.Y + 11f, stateW - 6f, 16f), sf);
                }

            // MOD 名
            string mt = s.ModText;
            if (s.Partial && s.On.Count > 0) mt += "（" + s.Parts + "/" + s.AllParts + "）";
            Color mc = s.HasMod ? Color.FromArgb(238, 224, 200) : Ink3;
            using (SolidBrush b = new SolidBrush(mc))
                g.DrawString(mt, fMod, b, new RectangleF(modX, r.Y + 8f, modW, 20f), Ellipsis());
        }

        private static StringFormat Ellipsis()
        {
            StringFormat sf = new StringFormat();
            sf.Trimming = StringTrimming.EllipsisCharacter;
            sf.FormatFlags = StringFormatFlags.NoWrap;
            return sf;
        }

        private static Color StateInk(string st)
        {
            if (st.IndexOf("部分") >= 0) return Accent;
            if (st.IndexOf("未装") >= 0) return Ink3;
            if (st.IndexOf("关") >= 0) return Amber;
            if (st.IndexOf("装") >= 0 || st.IndexOf("开启") >= 0) return Good;
            return Ink3;
        }

        private void ScrollBars(Graphics g)
        {
            DetailBar(g);
            ListBar(g);
        }

        private void DetailBar(Graphics g)
        {
            float max = OvLayout.MaxScrollDetail(ClientSize);
            if (max <= 1f) return;
            float viewH = Math.Max(20f, OvLayout.DetailBottom - OvLayout.DetailY);
            float h = Math.Max(24f, viewH * viewH / (viewH + max));
            float t = scrollDetail / max;
            float y = OvLayout.DetailY + (viewH - h) * t;
            float x = OvLayout.DetailX + OvLayout.DetailW + 10f;
            using (GraphicsPath p = Round(new RectangleF(x, OvLayout.DetailY, 3.5f, viewH), 1.75f))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(14, 255, 240, 215)))
                g.FillPath(b, p);
            using (GraphicsPath p = Round(new RectangleF(x, y, 3.5f, h), 1.75f))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(70, 255, 240, 215)))
                g.FillPath(b, p);
        }

        private void ListBar(Graphics g)
        {
            float max = OvLayout.MaxScrollList(ClientSize);
            if (max <= 1f) return;
            float viewH = Math.Max(20f, OvLayout.ListBottom - OvLayout.ListY);
            float h = Math.Max(24f, viewH * viewH / (viewH + max));
            float t = scrollList / max;
            float y = OvLayout.ListY + (viewH - h) * t;
            float x = OvLayout.ListX + OvLayout.ListW + 6f;
            using (GraphicsPath p = Round(new RectangleF(x, y, 3.5f, h), 1.75f))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(60, 255, 240, 215)))
                g.FillPath(b, p);
        }

        private void Hint(Graphics g)
        {
            string s = "点一行 → 回方案树改这个位置　·　↑↓ 换大类　·　滚轮翻明细　·　灰点=原版，空心=库里还没有";
            using (SolidBrush b = new SolidBrush(Color.FromArgb(96, 200, 186, 162)))
                g.DrawString(s, fSmall, b,
                    new RectangleF(OvLayout.SidePad, ClientSize.Height - 26f,
                                   ClientSize.Width - OvLayout.SidePad * 2f, 18f));
        }

        private static GraphicsPath Round(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            if (r.Width < 2f || r.Height < 2f) { p.AddRectangle(new RectangleF(r.X, r.Y, Math.Max(2f, r.Width), Math.Max(2f, r.Height))); return p; }
            float d = Math.Min(rad * 2f, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ================= 命中 =================

        private int HitCat(Point p)
        {
            if (model == null || p.X < OvLayout.ListX || p.X > OvLayout.ListX + OvLayout.ListW) return -1;
            for (int i = 0; i < model.Cats.Count; i++)
            {
                OvCat c = model.Cats[i];
                if (p.Y >= c.Y && p.Y <= c.Y + c.H) return i;
            }
            return -1;
        }

        private OvSlot HitSlot(Point p)
        {
            if (model == null || p.X < OvLayout.DetailX || p.X > OvLayout.DetailX + OvLayout.DetailW) return null;
            OvCat cat = OvLayout.CatAt(model, selCat);
            if (cat == null) return null;
            foreach (OvSlot s in cat.Slots)
                if (p.Y >= s.Y && p.Y <= s.Y + s.H) return s;
            return null;
        }

        // ================= 交互 =================

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            OvSlot h = HitSlot(e.Location);
            int hc = HitCat(e.Location);
            if (h != hover || hc != hoverCat)
            {
                hover = h;
                hoverCat = hc;
                tip.SetToolTip(this, h == null ? "" : TipFor(h));
                Cursor = (h != null) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hover != null || hoverCat >= 0)
            {
                hover = null;
                hoverCat = -1;
                tip.SetToolTip(this, "");
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            int ci = HitCat(e.Location);
            if (ci >= 0)
            {
                if (ci != selCat) { selCat = ci; scrollDetail = 0f; hover = null; Relayout(); }
                return;
            }
            OvSlot s = HitSlot(e.Location);
            if (s != null) mf.ShowSlotInTree(s.Slot);       // 看板只负责看：点一行就回方案树去改
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int delta = e.Delta;
            if (e.X < OvLayout.ListX + OvLayout.ListW + 10f)
            {
                scrollList -= delta;
                float mx = OvLayout.MaxScrollList(ClientSize);
                if (scrollList < 0f) scrollList = 0f;
                if (scrollList > mx) scrollList = mx;
            }
            else
            {
                scrollDetail -= delta;
                float mx = OvLayout.MaxScrollDetail(ClientSize);
                if (scrollDetail < 0f) scrollDetail = 0f;
                if (scrollDetail > mx) scrollDetail = mx;
            }
            Relayout();
        }

        protected override bool IsInputKey(Keys key)
        {
            if (key == Keys.Up || key == Keys.Down || key == Keys.PageUp || key == Keys.PageDown
                || key == Keys.Home || key == Keys.End || key == Keys.Enter) return true;
            return base.IsInputKey(key);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (model == null || model.Cats.Count == 0) return;
            bool moved = false;
            if (e.KeyCode == Keys.Up) { selCat = Math.Max(0, selCat - 1); scrollDetail = 0f; moved = true; }
            else if (e.KeyCode == Keys.Down) { selCat = Math.Min(model.Cats.Count - 1, selCat + 1); scrollDetail = 0f; moved = true; }
            else if (e.KeyCode == Keys.PageUp || e.KeyCode == Keys.PageDown)
            {
                scrollDetail += (e.KeyCode == Keys.PageUp ? -1 : 1) * (OvLayout.DetailBottom - OvLayout.DetailY) * 0.8f;
                float mx = OvLayout.MaxScrollDetail(ClientSize);
                if (scrollDetail < 0f) scrollDetail = 0f;
                if (scrollDetail > mx) scrollDetail = mx;
                moved = true;
            }
            else if (e.KeyCode == Keys.Home) { scrollDetail = 0f; moved = true; }
            else if (e.KeyCode == Keys.End) { scrollDetail = OvLayout.MaxScrollDetail(ClientSize); moved = true; }
            else if (e.KeyCode == Keys.Enter)
            {
                OvCat c = OvLayout.CatAt(model, selCat);
                if (c != null && c.Slots.Count > 0) mf.ShowSlotInTree(c.Slots[0].Slot);
                return;
            }
            if (moved) Relayout();
        }

        private string TipFor(OvSlot s)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append(s.Cat).Append(" · ").Append(s.Sub).Append(" · ").Append(s.Name).Append("\r\n");

            if (!s.HasMod)
            {
                sb.Append(s.Empty ? "库里还没有这个位置的 MOD。" : "这个位置用的是原版（没装 MOD）。");
                return sb.ToString();
            }

            foreach (ModGroup g in s.On)
            {
                sb.Append(g.Name).Append("（").Append(g.Parts.Count).Append(" 个 vpk）\r\n");
                foreach (ModItem p in g.Parts) sb.Append("　· [").Append(p.Role).Append("] ").Append(p.Name).Append("\r\n");
            }
            foreach (ModGroup g in s.Extra)
                sb.Append("叠加扩展：").Append(g.Name).Append("（").Append(g.Parts.Count).Append(" 个 vpk）\r\n");

            Color ink, bg;
            sb.Append("游戏里：").Append(MainForm.StatusOf(s.On.Count > 0 ? s.On[0] : s.Extra[0], out ink, out bg));
            sb.Append("　合计 ").Append(Core.Human(s.Size));
            return sb.ToString();
        }

        // ================= 自检 =================

        internal int CatCount { get { return model == null ? 0 : model.Cats.Count; } }

        internal int SlotCount
        {
            get
            {
                if (model == null) return 0;
                int n = 0;
                foreach (OvCat c in model.Cats) n += c.Slots.Count;
                return n;
            }
        }

        internal int SlotsOn { get { return model == null ? 0 : model.SlotsOn; } }

        internal string[] CatTitles
        {
            get
            {
                if (model == null) return new string[0];
                string[] a = new string[model.Cats.Count];
                for (int i = 0; i < a.Length; i++) a[i] = model.Cats[i].Title;
                return a;
            }
        }

        /// <summary>自检：这个大类排得对不对。没问题返回 null。</summary>
        internal string LayoutProblem(string catTitle)
        {
            SelectCatForShot(catTitle);
            OvCat c = OvLayout.CatAt(model, selCat);
            if (c == null) return "找不到大类「" + catTitle + "」";

            float viewH = OvLayout.DetailBottom - OvLayout.DetailY;
            float maxScroll = OvLayout.MaxScrollDetail(ClientSize);
            foreach (OvSlot s in c.Slots)
            {
                if (s.H < 20f) return catTitle + " / " + s.Name + " 行高压到 " + s.H.ToString("0.0") + "，字放不下";
                if (OvLayout.DetailX + OvLayout.DetailW > ClientSize.Width + 1f)
                    return catTitle + " / 明细列横向出界";
                // 放得下却还滚（或者内容比视口矮却算出了滚动量）说明算错了
                if (maxScroll <= 0.5f && s.Y + s.H > OvLayout.DetailBottom + 1f)
                    return catTitle + " / " + s.Name + " 被底边切掉（内容 "
                         + OvLayout.DetailContentH.ToString("0") + " / 视口 " + viewH.ToString("0") + "）";
            }
            return null;
        }

        /// <summary>自检：按坐标点一行，返回它会跳到哪个位置（不真的切界面）。</summary>
        internal Slot SlotAtPoint(float x, float y)
        {
            OvSlot s = HitSlot(new Point((int)x, (int)y));
            return s == null ? null : s.Slot;
        }

        /// <summary>自检用：把当前大类的第 n 行拿出来的坐标。</summary>
        internal RectangleF RowRect(int n)
        {
            OvCat c = OvLayout.CatAt(model, selCat);
            if (c == null || n < 0 || n >= c.Slots.Count) return RectangleF.Empty;
            OvSlot s = c.Slots[n];
            return new RectangleF(OvLayout.DetailX, s.Y, OvLayout.DetailW, s.H);
        }

        /// <summary>自检用：当前大类第 n 行是哪个位置。</summary>
        internal Slot RowSlot(int n)
        {
            OvCat c = OvLayout.CatAt(model, selCat);
            if (c == null || n < 0 || n >= c.Slots.Count) return null;
            return c.Slots[n].Slot;
        }

        /// <summary>自检用：整套摊成文本。</summary>
        internal string Dump()
        {
            return model == null ? "" : model.Dump();
        }
    }
}

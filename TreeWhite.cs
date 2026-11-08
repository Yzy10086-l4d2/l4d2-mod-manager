using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;

namespace Installer
{
    /// <summary>
    /// 白模：只验证结构，不做配色。黑框灰底、细连线，
    /// 用来核对「11 个大类 / 19 个位置 / 4 个候选」这种数字和排版对不对。
    /// 真正的样子由 TreeCanvas 画，这里不参与。
    /// </summary>
    internal static class TreeWhite
    {
        public static string Render(TreeNode root, Size size)
        {
            TreeNode selCat = root.Kids.Count > 0 ? root.Kids[0] : null;
            TreeNode selSlot = (selCat != null && selCat.Kids.Count > 0) ? selCat.Kids[0] : null;

            // 白模自己也走一遍排版，等于顺手验了 TreeLayout
            TreeLayout.Arrange(root, selCat, selSlot, size, 0f, 0f);

            using (Bitmap bmp = new Bitmap(size.Width, size.Height))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                g.Clear(Color.White);

                DrawCap(g, root, selCat, selSlot);

                // 连线先画，节点后画，压在下面
                foreach (TreeNode c in root.Kids)
                {
                    bool hot = c == selCat;
                    Line(g, root, c, hot ? Color.FromArgb(90, 90, 90) : Color.FromArgb(215, 215, 215), hot);
                }
                if (selCat != null)
                    foreach (TreeNode s in selCat.Kids)
                    {
                        bool hot = s == selSlot;
                        Line(g, selCat, s, hot ? Color.FromArgb(90, 90, 90) : Color.FromArgb(222, 222, 222), hot);
                    }
                if (selSlot != null)
                    for (int i = 0; i < selSlot.Kids.Count; i++)
                    {
                        bool hot = i == 0;
                        Line(g, selSlot, selSlot.Kids[i], hot ? Color.FromArgb(90, 90, 90) : Color.FromArgb(228, 228, 228), hot);
                    }

                // 节点（根也要画：之前漏了，只剩连线往左汇成一个尖）
                Node(g, root, false);
                foreach (TreeNode c in root.Kids)
                {
                    Node(g, c, c == selCat);
                    if (c != selCat) continue;
                    foreach (TreeNode s in c.Kids)
                    {
                        Node(g, s, s == selSlot);
                        if (s != selSlot) continue;
                        for (int i = 0; i < s.Kids.Count; i++) Node(g, s.Kids[i], i == 0);
                    }
                }

                g.DrawString("白模 · 只验结构，不做配色　" + DateTime.Now.ToString("HH:mm:ss"),
                    new Font("Microsoft YaHei", 9f), Brushes.Silver, 48, size.Height - 34);

                string path = Path.Combine(Core.LogDir, "tree-white.png");
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                return path;
            }
        }

        private static void DrawCap(Graphics g, TreeNode root, TreeNode selCat, TreeNode selSlot)
        {
            using (Font f = new Font("Microsoft YaHei", 8.5f))
            {
                TreeNode[] col = { root, selCat, selSlot, (selSlot != null && selSlot.Kids.Count > 0) ? selSlot.Kids[0] : null };
                string[] cap = { "根", "大类 · " + root.Kids.Count, "位置", "候选" };
                for (int i = 0; i < col.Length; i++)
                {
                    if (col[i] == null) continue;
                    g.DrawString(cap[i], f, Brushes.Gray, col[i].X, TreeLayout.TopPad - 24f);
                }
            }
        }

        private static void Line(Graphics g, TreeNode p, TreeNode c, Color color, bool hot)
        {
            PointF a, b, c1, c2;
            TreeLayout.Connector(p, c, out a, out b, out c1, out c2);
            using (Pen pen = new Pen(color, hot ? 1.3f : 0.9f))
                g.DrawBezier(pen, a, c1, c2, b);
        }

        private static void Node(Graphics g, TreeNode n, bool strong)
        {
            RectangleF r = new RectangleF(n.X, n.Y, n.W, n.H);
            using (GraphicsPath path = Round(r, 6f))
            using (SolidBrush fill = new SolidBrush(n.On ? Color.FromArgb(228, 228, 228) : Color.FromArgb(250, 250, 250)))
            using (Pen pen = new Pen(strong ? Color.FromArgb(40, 40, 40) : Color.FromArgb(190, 190, 190), strong ? 1.4f : 0.9f))
            {
                if (n.On) pen.Color = Color.FromArgb(70, 70, 70);
                if (strong) pen.Width = 1.6f;
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }

            float fs = n.Level == 1 ? 12f : (n.Level == 2 ? 10.5f : 9.5f);
            using (Font f = new Font("Microsoft YaHei", fs))
            using (Font fb = new Font("Microsoft YaHei", 8f))
            using (StringFormat sf = new StringFormat())
            {
                sf.Trimming = StringTrimming.EllipsisCharacter;
                sf.FormatFlags = StringFormatFlags.NoWrap;

                float textW = n.W - 22;
                if (!string.IsNullOrEmpty(n.Badge)) textW -= 44;

                float ty = n.Y + (n.H - fs * 1.5f) * 0.5f;
                if (!string.IsNullOrEmpty(n.Sub)) ty = n.Y + 5f;

                RectangleF tr = new RectangleF(n.X + 11, ty, textW, fs * 1.6f);
                g.DrawString(n.Title ?? "", f, n.On ? Brushes.Black : Brushes.DimGray, tr, sf);

                if (!string.IsNullOrEmpty(n.Sub))
                {
                    RectangleF sr = new RectangleF(n.X + 11, n.Y + n.H - 15f, n.W - 22, 13f);
                    g.DrawString(n.Sub, fb, Brushes.Gray, sr, sf);
                }
                if (!string.IsNullOrEmpty(n.Badge))
                {
                    RectangleF br = new RectangleF(n.X + n.W - 50, n.Y + (n.H - 14f) * 0.5f, 40, 14);
                    sf.Alignment = StringAlignment.Far;
                    g.DrawString(n.Badge, fb, Brushes.Gray, br, sf);
                }
            }
        }

        private static GraphicsPath Round(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}

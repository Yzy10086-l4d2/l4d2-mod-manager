using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Installer
{
    /// <summary>
    /// MOD 分类的单色剪影图标。
    ///
    /// 照 L4D2 HUD 那套做法：只有轮廓、不用颜色，所以浅色深色都能直接套同一个形状，
    /// 颜色由调用方传。全部在 16x16 的网格里搭路径，画的时候缩放到 14~15px。
    ///
    /// 小尺寸下的取舍：凡是 1px 以下的细节都会被抗锯齿磨成灰雾，所以这里一律只保留
    /// 「一眼能读出来的大块」——枪就是横机匣 + 明显下垂的弹匣/握把，斧就是竖柄 + 大三角。
    /// 细枝末节（准星、扳机护圈、护木纹路）全部砍掉，宁可少画也不要糊。
    /// </summary>
    internal static class Glyph
    {
        public const int Grid = 16;

        /// <summary>大类 → 图标。树的第一层。</summary>
        public static string ForCat(string cat)
        {
            switch (cat)
            {
                case "枪械": return "gun";
                case "近战": return "melee";
                case "投掷物": return "bottle";
                case "弹药升级": return "ammo";
                case "人物": return "person";
                case "药品": return "medkit";
                case "地图":
                case "三方图": return "map";
                case "喷漆": return "spray";
                case "我的分支": return "branch";
                case "未归类": return "unknown";
                default: return "misc";
            }
        }

        /// <summary>小分类 → 图标。认不出来的退回大类那一个，保证每行都有东西。</summary>
        public static string ForSub(string sub, string cat)
        {
            switch (sub)
            {
                case "步枪": return "rifle";
                case "冲锋枪": return "smg";
                case "霰弹枪": return "shotgun";
                case "狙击枪": return "sniper";
                case "手枪": return "pistol";
                case "特殊武器": return "launcher";
                case "近战": return "melee";
                case "投掷物": return "bottle";
                case "弹药升级": return "ammo";
                case "其他枪械": return "gun";
                case "一代": return "survivor1";
                case "二代": return "survivor2";
                case "特感": return "claw";
                case "普通感染者": return "horde";
                case "其他人物": return "person";
                case "治疗与复活": return "medkit";
                case "暂时增益": return "syringe";
                case "其他药品": return "pills";
                default: return ForCat(cat);
            }
        }

        /// <summary>在 rect 里画一个图标。颜色通常给 Theme.Ink2。</summary>
        public static void Draw(Graphics g, string key, Rectangle rect, Color color)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return;
            using (GraphicsPath p = Build(key))
            {
                if (p == null) return;
                GraphicsState st = g.Save();
                try
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TranslateTransform(rect.X, rect.Y);
                    g.ScaleTransform(rect.Width / (float)Grid, rect.Height / (float)Grid);
                    using (SolidBrush b = new SolidBrush(color)) g.FillPath(b, p);
                }
                finally { g.Restore(st); }
            }
        }

        // ---------------------------------------------------------------- 形状

        /// <summary>
        /// 要镂空的地方（医疗包的十字、药瓶刻度、杂项的叠片）用 Alternate；
        /// 其余一律 Winding，部件之间重叠一点不会被误挖出洞。
        /// </summary>
        private static GraphicsPath New(FillMode mode)
        {
            GraphicsPath p = new GraphicsPath();
            p.FillMode = mode;
            return p;
        }

        private static GraphicsPath Build(string key)
        {
            GraphicsPath p = New(FillMode.Winding);
            switch (key)
            {
                // ---- 大类
                case "gun":
                    Gun(p);
                    return p;

                case "person":
                    Person(p, 1f);
                    return p;

                // 普通感染者：一大一小两个人影叠着 = 一群，跟「特感」的爪子区分开。
                // 17px 下只敢用两个实心块，三个头会糊成一团。
                case "horde":
                    Person(p, 0.66f);
                    p.AddEllipse(9.2f, 6.6f, 4.4f, 4.4f);
                    p.AddPolygon(new PointF[]
                    {
                        new PointF(7.8f, 15.0f), new PointF(15.2f, 15.0f),
                        new PointF(13.8f, 11.4f), new PointF(9.6f, 11.4f)
                    });
                    return p;

                case "medkit":
                    p.Dispose();
                    p = New(FillMode.Alternate);
                    Medkit(p);
                    return p;

                case "map":
                    p.AddPolygon(new PointF[] { new PointF(1.4f, 4.2f), new PointF(5.1f, 2.6f), new PointF(5.1f, 11.4f), new PointF(1.4f, 13.0f) });
                    p.AddPolygon(new PointF[] { new PointF(5.7f, 2.6f), new PointF(10.3f, 4.2f), new PointF(10.3f, 13.0f), new PointF(5.7f, 11.4f) });
                    p.AddPolygon(new PointF[] { new PointF(10.9f, 4.2f), new PointF(14.6f, 2.6f), new PointF(14.6f, 11.4f), new PointF(10.9f, 13.0f) });
                    return p;

                case "spray":
                    p.AddRectangle(new RectangleF(3.0f, 4.8f, 10.0f, 9.6f));
                    p.AddPolygon(new PointF[] { new PointF(3.0f, 4.8f), new PointF(13.0f, 4.8f), new PointF(11.6f, 2.6f), new PointF(4.4f, 2.6f) });
                    p.AddRectangle(new RectangleF(6.2f, 0.8f, 3.2f, 2.2f));
                    p.AddRectangle(new RectangleF(9.2f, 1.2f, 2.8f, 1.2f));
                    return p;

                case "branch":
                    p.AddRectangle(new RectangleF(7.1f, 6.2f, 1.8f, 8.0f));
                    p.AddPolygon(new PointF[] { new PointF(7.1f, 7.8f), new PointF(7.1f, 10.0f), new PointF(2.4f, 6.2f), new PointF(3.4f, 4.6f) });
                    p.AddPolygon(new PointF[] { new PointF(8.9f, 7.8f), new PointF(8.9f, 10.0f), new PointF(13.6f, 6.2f), new PointF(12.6f, 4.6f) });
                    p.AddEllipse(1.2f, 3.0f, 3.4f, 3.4f);
                    p.AddEllipse(11.4f, 3.0f, 3.4f, 3.4f);
                    p.AddEllipse(6.3f, 0.8f, 3.4f, 3.4f);
                    p.AddRectangle(new RectangleF(6.1f, 13.8f, 3.8f, 1.6f));
                    return p;

                case "unknown":
                    p.AddRectangle(new RectangleF(1.4f, 1.4f, 13.2f, 1.6f));
                    p.AddRectangle(new RectangleF(1.4f, 13.0f, 13.2f, 1.6f));
                    p.AddRectangle(new RectangleF(1.4f, 1.4f, 1.6f, 13.2f));
                    p.AddRectangle(new RectangleF(13.0f, 1.4f, 1.6f, 13.2f));
                    p.AddRectangle(new RectangleF(6.9f, 6.9f, 2.2f, 2.2f));
                    return p;

                case "misc":
                    p.Dispose();
                    p = New(FillMode.Alternate);
                    p.AddRectangle(new RectangleF(1.4f, 2.2f, 9.0f, 9.0f));
                    p.AddRectangle(new RectangleF(5.6f, 5.6f, 9.0f, 9.0f));
                    return p;

                // ---- 枪械小类
                case "rifle":
                    Gun(p);
                    p.AddRectangle(new RectangleF(4.0f, 2.6f, 5.8f, 2.1f));
                    p.AddRectangle(new RectangleF(5.2f, 4.6f, 1.3f, 1.6f));
                    p.AddRectangle(new RectangleF(7.4f, 4.6f, 1.3f, 1.6f));
                    return p;

                case "smg":
                    p.AddRectangle(new RectangleF(2.4f, 6.0f, 7.8f, 3.2f));
                    p.AddRectangle(new RectangleF(10.0f, 6.9f, 4.0f, 1.5f));
                    p.AddRectangle(new RectangleF(4.0f, 8.9f, 2.8f, 5.4f));
                    p.AddPolygon(new PointF[] { new PointF(7.8f, 8.9f), new PointF(9.8f, 8.9f), new PointF(9.0f, 12.6f), new PointF(7.6f, 12.6f) });
                    p.AddRectangle(new RectangleF(0.9f, 6.6f, 1.8f, 2.6f));
                    return p;

                case "shotgun":
                    p.AddRectangle(new RectangleF(0.8f, 5.4f, 14.4f, 2.8f));
                    p.AddRectangle(new RectangleF(5.4f, 8.2f, 5.8f, 3.2f));
                    p.AddRectangle(new RectangleF(1.4f, 8.2f, 4.2f, 2.4f));
                    p.AddPolygon(new PointF[] { new PointF(0.8f, 5.4f), new PointF(0.0f, 11.2f), new PointF(2.4f, 11.2f), new PointF(3.2f, 5.4f) });
                    return p;

                case "sniper":
                    p.AddRectangle(new RectangleF(0.5f, 6.9f, 15.0f, 1.5f));
                    p.AddRectangle(new RectangleF(4.6f, 2.2f, 6.8f, 2.4f));
                    p.AddRectangle(new RectangleF(5.9f, 4.5f, 1.3f, 1.7f));
                    p.AddRectangle(new RectangleF(9.5f, 4.5f, 1.3f, 1.7f));
                    p.AddPolygon(new PointF[] { new PointF(0.5f, 6.9f), new PointF(0.0f, 11.2f), new PointF(2.2f, 11.2f), new PointF(2.7f, 6.9f) });
                    p.AddPolygon(new PointF[] { new PointF(7.3f, 8.2f), new PointF(9.2f, 8.2f), new PointF(8.7f, 12.2f), new PointF(7.1f, 12.2f) });
                    return p;

                case "pistol":
                    p.AddRectangle(new RectangleF(2.8f, 4.6f, 9.4f, 3.0f));
                    p.AddRectangle(new RectangleF(12.0f, 5.6f, 2.9f, 1.5f));
                    p.AddPolygon(new PointF[] { new PointF(4.2f, 7.4f), new PointF(8.2f, 7.4f), new PointF(7.5f, 14.2f), new PointF(4.6f, 14.2f) });
                    return p;

                case "launcher":
                    p.AddRectangle(new RectangleF(1.4f, 6.0f, 10.0f, 4.2f));
                    p.AddPolygon(new PointF[] { new PointF(11.2f, 5.2f), new PointF(14.8f, 6.6f), new PointF(14.8f, 9.6f), new PointF(11.2f, 11.0f) });
                    p.AddPolygon(new PointF[] { new PointF(5.6f, 10.0f), new PointF(8.2f, 10.0f), new PointF(7.8f, 13.8f), new PointF(5.8f, 13.8f) });
                    p.AddRectangle(new RectangleF(0.2f, 6.4f, 1.6f, 3.4f));
                    return p;

                case "melee":
                    p.AddRectangle(new RectangleF(6.6f, 4.6f, 1.9f, 10.6f));
                    p.AddPolygon(new PointF[] { new PointF(8.5f, 5.0f), new PointF(14.7f, 4.0f), new PointF(14.7f, 8.4f), new PointF(8.5f, 9.2f) });
                    return p;

                case "bottle":
                    p.AddPolygon(new PointF[] { new PointF(4.8f, 8.0f), new PointF(11.2f, 8.0f), new PointF(10.4f, 14.4f), new PointF(5.6f, 14.4f) });
                    p.AddRectangle(new RectangleF(6.9f, 4.8f, 2.2f, 3.4f));
                    p.AddPolygon(new PointF[] { new PointF(8.0f, 0.6f), new PointF(10.2f, 4.4f), new PointF(5.8f, 4.4f) });
                    return p;

                case "ammo":
                    // 两发竖着的子弹：尖头 + 直筒。和横放的手枪剪影不会混。
                    p.AddPolygon(Bullet(3.6f));
                    p.AddPolygon(Bullet(9.0f));
                    return p;

                // ---- 人物小类
                case "survivor1":
                    Person(p, 0.8f);
                    p.AddRectangle(new RectangleF(10.6f, 6.2f, 2.4f, 2.4f));
                    return p;

                case "survivor2":
                    Person(p, 0.8f);
                    p.AddRectangle(new RectangleF(10.6f, 5.0f, 2.4f, 2.4f));
                    p.AddRectangle(new RectangleF(10.6f, 9.2f, 2.4f, 2.4f));
                    return p;

                case "claw":
                    p.AddPolygon(new PointF[] { new PointF(1.8f, 3.6f), new PointF(4.0f, 2.2f), new PointF(12.8f, 12.2f), new PointF(10.6f, 13.6f) });
                    p.AddPolygon(new PointF[] { new PointF(6.2f, 2.0f), new PointF(8.4f, 1.2f), new PointF(15.6f, 10.4f), new PointF(13.4f, 11.2f) });
                    p.AddPolygon(new PointF[] { new PointF(0.2f, 7.2f), new PointF(2.0f, 5.8f), new PointF(9.0f, 14.4f), new PointF(7.2f, 15.2f) });
                    return p;

                // ---- 药品小类
                case "syringe":
                    p.AddRectangle(new RectangleF(4.2f, 5.0f, 7.2f, 3.4f));
                    p.AddRectangle(new RectangleF(2.4f, 5.6f, 2.2f, 2.2f));
                    p.AddRectangle(new RectangleF(0.6f, 4.6f, 1.9f, 1.0f));
                    p.AddRectangle(new RectangleF(0.6f, 7.8f, 1.9f, 1.0f));
                    p.AddRectangle(new RectangleF(1.9f, 4.6f, 1.0f, 4.2f));
                    p.AddRectangle(new RectangleF(11.2f, 6.0f, 3.6f, 1.4f));
                    p.AddPolygon(new PointF[] { new PointF(14.6f, 5.7f), new PointF(15.8f, 6.7f), new PointF(14.6f, 7.7f) });
                    return p;

                case "pills":
                    p.Dispose();
                    p = New(FillMode.Alternate);
                    p.AddPolygon(new PointF[] { new PointF(4.2f, 6.2f), new PointF(11.8f, 6.2f), new PointF(10.8f, 14.4f), new PointF(5.2f, 14.4f) });
                    p.AddRectangle(new RectangleF(5.4f, 3.0f, 5.2f, 3.0f));
                    p.AddRectangle(new RectangleF(6.2f, 8.2f, 3.6f, 1.2f));
                    p.AddRectangle(new RectangleF(6.4f, 10.8f, 3.2f, 1.2f));
                    return p;

                default:
                    p.Dispose();
                    return null;
            }
        }

        /// <summary>共用件：横放的步枪剪影（托 + 机匣 + 管 + 下垂的弹匣与握把）。</summary>
        private static void Gun(GraphicsPath p)
        {
            p.AddPolygon(new PointF[] { new PointF(1.2f, 5.8f), new PointF(0.2f, 10.0f), new PointF(2.2f, 10.0f), new PointF(3.0f, 5.8f) });
            p.AddRectangle(new RectangleF(1.2f, 5.8f, 9.8f, 3.6f));
            p.AddRectangle(new RectangleF(10.8f, 6.7f, 4.4f, 1.6f));
            p.AddPolygon(new PointF[] { new PointF(4.4f, 9.2f), new PointF(7.4f, 9.2f), new PointF(6.9f, 13.8f), new PointF(4.8f, 13.8f) });
            p.AddPolygon(new PointF[] { new PointF(8.8f, 9.2f), new PointF(10.6f, 9.2f), new PointF(10.0f, 13.0f), new PointF(8.6f, 13.0f) });
        }

        /// <summary>共用件：一发竖着的子弹（尖头 + 直筒）。x 是左边缘，宽 2.8。</summary>
        private static PointF[] Bullet(float x)
        {
            return new PointF[]
            {
                new PointF(x + 1.4f, 1.2f),
                new PointF(x + 2.8f, 5.2f),
                new PointF(x + 2.8f, 14.4f),
                new PointF(x, 14.4f),
                new PointF(x, 5.2f)
            };
        }

        /// <summary>共用件：半身人形。scale 给「一代/二代」缩小用，右边腾地方放竖条。</summary>
        private static void Person(GraphicsPath p, float scale)
        {
            p.AddEllipse(5.4f * scale, 1.4f * scale, 5.2f * scale, 5.2f * scale);
            p.AddRectangle(new RectangleF(7.0f * scale, 6.0f * scale, 2.0f * scale, 2.4f * scale));
            p.AddPolygon(new PointF[]
            {
                new PointF(2.4f * scale, 14.4f * scale),
                new PointF(13.6f * scale, 14.4f * scale),
                new PointF(11.6f * scale, 7.4f * scale),
                new PointF(4.4f * scale, 7.4f * scale)
            });
        }

        /// <summary>医疗包：箱体挖出十字，提手是个环。必须 Alternate 填充。</summary>
        private static void Medkit(GraphicsPath p)
        {
            p.AddRectangle(new RectangleF(1.2f, 5.4f, 13.6f, 8.8f));
            p.AddRectangle(new RectangleF(5.8f, 2.8f, 4.4f, 2.8f));
            p.AddRectangle(new RectangleF(6.8f, 3.7f, 2.4f, 1.2f));
            p.AddRectangle(new RectangleF(6.9f, 7.2f, 2.2f, 5.4f));
            p.AddRectangle(new RectangleF(5.3f, 8.5f, 5.4f, 2.2f));
        }
    }
}

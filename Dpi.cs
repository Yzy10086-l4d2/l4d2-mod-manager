using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace Installer
{
    /// <summary>
    /// 高 DPI 支持。
    ///
    /// 这个程序的每个坐标都是按 96dpi 手写的绝对值（SetBounds(14, 10, 792, 36) 这种），
    /// 所以只有两条路：让系统继续把整个窗口当位图拉伸（字糊、边缘糊），或者自己声明
    /// 「我知道 DPI」再按比例把设计值放大。这里走第二条。
    ///
    /// 只声明 System DPI aware，不做 per-monitor：窗口拖到另一块不同缩放的屏上时
    /// 系统会按位图缩放它（继续清晰的那块不受影响），代价是拖动跨屏那一下不重排 ——
    /// 换来的是不用处理 WM_DPICHANGED 和一堆 .NET Framework 下的兼容坑。
    ///
    /// 关键性质：<see cref="Factor"/> 为 1 时所有换算原样返回，行为和关掉这个功能完全一致。
    /// </summary>
    internal static class Dpi
    {
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

        /// <summary>当前缩放比例：96dpi = 1.0，150% = 1.5。</summary>
        public static float Factor = 1f;

        /// <summary>真的在缩放（100% 缩放下为 false，所有 S() 走原值）。</summary>
        public static bool Scaled { get { return Factor > 1.001f; } }

        /// <summary>任何窗口创建之前调用一次。enabled=false 时一个字节都不动系统设置。</summary>
        public static void Init(bool enabled)
        {
            Factor = 1f;
            if (!enabled) return;
            try
            {
                if (!SetProcessDPIAware()) return;      // 已经被 manifest 设过也当作成功
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    float f = g.DpiX / 96f;
                    if (f < 1f) f = 1f;
                    if (f > 4f) f = 4f;                 // 400% 以上没见过，防手滑
                    Factor = f;
                }
            }
            catch { Factor = 1f; }
        }

        /// <summary>设计值 → 实际像素。100% 缩放下就是原值。</summary>
        public static int S(int v)
        {
            if (Factor <= 1.001f) return v;
            return (int)Math.Round(v * Factor);
        }

        /// <summary>实际像素 → 设计值。排版时先把窗口尺寸换回设计坐标，算完再一次性放大。</summary>
        public static int Un(int v)
        {
            if (Factor <= 1.001f) return v;
            return (int)Math.Round(v / Factor);
        }

        public static Size Sz(int w, int h) { return new Size(S(w), S(h)); }
    }
}

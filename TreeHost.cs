using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace Installer
{
    // 两副面孔，同一份数据：
    //   · 桌面：第一人称视角看一张真桌子，一张卡 = 一个大类/分支，卡可以拖到任意位置。
    //     画面在浏览器里用 WebGL 画（见 desk\index.html），这边只供数据、收操作。
    //   · 方案树：细到「一个位置一个 MOD」，挑候选、展开套装在那边。
    // 两边共用同一个 CatTree 模型，所以勾选状态永远是同一份。
    public partial class MainForm
    {
        private DeskServer desk;

        /// <summary>方案树：按「能替换的东西」看这套方案，右边是候选 MOD。</summary>
        internal void ShowTree()
        {
            EnsureTree();
            DropLoading();
            viewHost.Visible = true;
            treeView.Visible = true;
            treeView.SetBounds(0, 0, Math.Max(200, viewHost.ClientSize.Width), Math.Max(120, viewHost.ClientSize.Height));
            treeView.BringToFront();
            treeView.Rebuild();
            UpdateModCaption();
        }

        /// <summary>切回方案树（台面右键 / 顶部「视图」按钮都走它）。</summary>
        internal void ShowTreeView()
        {
            ShowTree();
            if (treeView != null) treeView.Focus();
        }

        /// <summary>桌面：开一个第一人称的 3D 桌面窗口（同一个 exe，画面在浏览器里）。</summary>
        internal void ShowDesk()
        {
            EnsureTree();                 // 桌面上的卡是从树的位置表建的，模型得先有
            DropLoading();
            treeView.Rebuild();           // 位置表先排好（顺序、手工拖动都在里面）
            viewHost.Visible = true;
            treeView.Visible = true;

            if (desk != null && desk.Running)
            {
                // 已经开着就别再开一个窗口，桌面自己会轮询最新的数据
                if (treeView != null) treeView.KeepFoot("桌面已经开着了 —— 在另一个窗口里，关掉它就回到这里");
                return;
            }
            desk = new DeskServer(this, treeView);
            desk.Start();
            if (treeView != null) treeView.KeepFoot("桌面已开在另一个窗口：走到桌前看一眼，关掉就回来");
            UpdateModCaption();
        }

        internal DeskServer Desk { get { return desk; } }
        internal CatTree Tree { get { return treeView; } }

        /// <summary>自检 / 截图用：只把服务起起来（不弹窗口），守着跑指定秒数。</summary>
        internal string DeskServe(int seconds)
        {
            EnsureTree();
            treeView.Rebuild();
            if (desk != null) { desk.Stop(); desk = null; }
            desk = new DeskServer(this, treeView);
            desk.Start(false);
            try
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(Core.LogDir, "desk-url.txt"), desk.Url,
                    new System.Text.UTF8Encoding(false));
            }
            catch { }
            Console.WriteLine("desk url = " + desk.Url);
            DateTime until = DateTime.Now.AddSeconds(seconds);
            while (DateTime.Now < until) { Application.DoEvents(); Thread.Sleep(50); }
            return desk.Url;
        }

        internal void CloseDesk()
        {
            if (desk == null) return;
            desk.Stop();
            desk = null;
        }

        /// <summary>台面里双击一行：回树里定位到那个位置，候选列表就在右边。</summary>
        internal void ShowSlotInTree(Slot s)
        {
            if (s == null) return;
            ShowTree();
            if (treeView != null) treeView.SelectSlot(s.Key);
        }

        /// <summary>数据变了（搜索、切模式、库变了）：树重建；桌面那边自己轮询，会跟着变。</summary>
        internal void RebuildViews()
        {
            if (treeView != null) treeView.Rebuild();
        }

        private void DropLoading()        {
            if (lblLoading == null) return;
            viewHost.Controls.Remove(lblLoading);
            lblLoading.Dispose();
            lblLoading = null;
        }

        private void EnsureTree()
        {
            if (treeView != null) return;
            treeView = new CatTree(this);
            treeView.SetBounds(0, 0, Math.Max(200, viewHost.ClientSize.Width), Math.Max(120, viewHost.ClientSize.Height));
            viewHost.Controls.Add(treeView);
        }

        /// <summary>勾选变了：树的勾、套装计数、底部已选数一起对齐（不重建，滚动位置不动）。</summary>
        internal void SyncPicks()
        {
            if (treeView != null && treeView.Visible) treeView.Sync();
            UpdateSel();
            UpdateSchemeLabel();
            UpdateModCaption();          // 台面标题上的「已选 N」也要跟着动
        }

        /// <summary>右栏标题栏：几个位置、已选几个。</summary>
        internal void UpdateModCaption()
        {
            if (gMod == null) return;
            int sc = treeView == null ? 0 : treeView.SlotCount;
            int sk = treeView == null ? 0 : treeView.SlotCheckedCount;
            string tail = treeView != null && treeView.Unmatched > 0 ? " · " + treeView.Unmatched + " 个未归类" : "";
            gMod.Caption = "方案树 · " + sc + " 个位置 · 已选 " + sk + tail;
        }
    }
}

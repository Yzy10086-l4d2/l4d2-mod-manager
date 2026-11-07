using System;
using System.Drawing;
using System.Windows.Forms;

namespace Installer
{
    // 右栏有两副面孔，同一份数据：
    //   · 台面（默认）：一张卡 = 一个大类/分支，卡随便拖，位置记在 U 盘
    //   · 方案树：细到「一个位置一个 MOD」，挑候选、展开套装在那边
    // 两边共用同一个 CatTree 模型，所以勾选状态永远是同一份。
    public partial class MainForm
    {
        private DeskView deskView;

        /// <summary>方案树：按「能替换的东西」看这套方案，右边是候选 MOD。</summary>
        internal void ShowTree()
        {
            EnsureTree();
            DropLoading();
            viewHost.Visible = true;
            if (deskView != null) deskView.Visible = false;
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

        /// <summary>台面：整个右栏铺成一张可摆放的画布。</summary>
        internal void ShowDesk()
        {
            EnsureTree();                 // 台面的卡是从树的位置表建的，模型得先有
            DropLoading();
            viewHost.Visible = true;
            treeView.Visible = false;

            if (deskView == null)
            {
                deskView = new DeskView(this, treeView);
                deskView.SetBounds(0, 0, Math.Max(200, viewHost.ClientSize.Width), Math.Max(120, viewHost.ClientSize.Height));
                viewHost.Controls.Add(deskView);
            }
            deskView.Visible = true;
            deskView.BringToFront();
            treeView.Rebuild();           // 位置表先排好（顺序、手工拖动都在里面）
            deskView.Rebuild();
            UpdateModCaption();
        }

        /// <summary>台面里双击一行：回树里定位到那个位置，候选列表就在右边。</summary>
        internal void ShowSlotInTree(Slot s)
        {
            if (s == null) return;
            ShowTree();
            if (treeView != null) treeView.SelectSlot(s.Key);
        }

        /// <summary>数据变了（搜索、切模式、库变了）：树和台面一起重建，别让台面停在旧内容上。</summary>
        internal void RebuildViews()
        {
            if (treeView != null) treeView.Rebuild();
            if (deskView != null && deskView.Visible) deskView.Rebuild();
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
            if (deskView != null && deskView.Visible) deskView.Sync();
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
            if (deskView != null && deskView.Visible)
                gMod.Caption = "台面 · " + deskView.CardCount + " 张卡 · 已选 " + sk + tail;
            else
                gMod.Caption = "方案树 · " + sc + " 个位置 · 已选 " + sk + tail;
        }
    }
}

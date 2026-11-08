using System;
using System.Collections.Generic;
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
        private TreeCanvas map;
        private bool mapHooked;

        /// <summary>科技树：整窗口一块画板，盖在左栏和方案树上面（顶栏和底栏留着）。</summary>
        internal void ShowTreeMap()
        {
            EnsureTree();
            DropLoading();
            treeView.Rebuild();

            if (map == null)
            {
                map = new TreeCanvas(this);
                Controls.Add(map);
            }
            if (!mapHooked)
            {
                mapHooked = true;
                Resize += delegate { if (MapOpen) FitMap(); };
            }

            FitMap();
            map.Rebuild();
            map.Visible = true;
            map.BringToFront();
            if (btnView != null) btnView.Text = "方案树";
            map.Focus();
            UpdateModCaption();
        }

        private void FitMap()
        {
            if (map == null) return;
            const int top = 56;                    // 顶栏下面
            int bottom = 104;                      // 底栏（开始安装那一排）上面
            map.SetBounds(0, top, ClientSize.Width,
                Math.Max(140, ClientSize.Height - top - bottom));
        }

        internal void HideTreeMap()
        {
            if (map != null) map.Visible = false;
            if (btnView != null) btnView.Text = "科技树";
            ShowTree();
        }

        internal bool MapOpen { get { return map != null && map.Visible; } }

        /// <summary>给某个位置换候选（科技树点候选走这里）。</summary>
        internal void ChooseInSlot(Slot s, ModGroup g)
        {
            if (treeView != null) treeView.ChooseInSlot(s, g);
        }

        /// <summary>截图自检：把科技树画布存成 PNG（只抓画布，不抓整窗 —— 屏幕上可能盖着别的窗口）。</summary>
        internal void TreeMapShot()
        {
            EnsureTree();
            ShowTreeMap();
            Pump(700);
            ShotCanvas("treemap");

            if (map != null) map.SelectForShot("近战", null);
            Pump(500);
            ShotCanvas("treemap2");

            if (map != null) map.SelectForShot("枪械", "AK-47");
            Pump(500);
            ShotCanvas("treemap3");
        }

        private static void Pump(int ms)
        {
            DateTime until = DateTime.Now.AddMilliseconds(ms);
            while (DateTime.Now < until) { Application.DoEvents(); Thread.Sleep(15); }
        }

        private void ShotCanvas(string tag)
        {
            if (map == null) return;
            using (Bitmap b = new Bitmap(Math.Max(2, map.Width), Math.Max(2, map.Height)))
            {
                map.DrawToBitmap(b, new Rectangle(0, 0, map.Width, map.Height));
                b.Save(System.IO.Path.Combine(Core.LogDir, tag + ".png"),
                       System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        /// <summary>科技树自检：数字对得上、每个大类都排得下、点候选真的换得到。返回失败条数。</summary>
        internal int TreeMapSelfTest()
        {
            int bad = 0;
            EnsureTree();
            treeView.Rebuild();
            ShowTreeMap();
            Pump(300);

            if (map == null) { Core.Log("FAIL", "treeself 画布没建起来"); return 1; }

            Core.Log("INFO", "TREESELF 大类 " + map.CatCount + " / 位置 " + map.SlotNodeCount
                            + "（方案树 " + treeView.SlotCount + "）");
            if (map.SlotNodeCount != treeView.SlotCount)
            {
                Core.Log("FAIL", "TREESELF 位置数对不上：" + map.SlotNodeCount + " vs " + treeView.SlotCount);
                bad++;
            }

            // 每个大类都排一遍：不许有节点跑出画布、压在列标题上，或行高压到字放不下
            string[] cats = map.CatTitles;
            int fit = 0;
            foreach (string cat in cats)
            {
                string why = map.LayoutProblem(cat);
                if (why != null) { Core.Log("FAIL", "TREESELF 排版 " + why); bad++; }
                else fit++;
            }
            Core.Log("INFO", "TREESELF 排版检查：" + fit + "/" + cats.Length + " 个大类通过");

            // 点一个候选：原来那件要下来，新的要上去，这个位置只剩一件
            Slot probe = null;
            foreach (Slot s in treeView.Slots)
                if (s.Items.Count >= 2) { probe = s; break; }

            if (probe == null) Core.Log("INFO", "TREESELF 没有两个以上候选的位置，跳过点选");
            else
            {
                List<string> backup = new List<string>(checkedKeys);
                int total0 = checkedKeys.Count;
                ModGroup a0 = probe.Items[0], a1 = probe.Items[1];

                // 先把这个位置归零，再点第 2 个候选 —— 直接点一个已经开着的候选是幂等的，验不出东西
                treeView.ChooseInSlot(probe, null);
                Pump(40);
                map.SelectForShot(probe.Cat, probe.Name);
                bool picked = map.TestPick(1);
                int on0 = CountChecked(a0), on1 = CountChecked(a1);
                Core.Log("INFO", "TREESELF 点第 2 个候选「" + a1.Name + "」：第 1 件 " + on0
                                + " 件，第 2 件 " + on1 + " 件，点中了=" + picked);
                if (!picked) { Core.Log("FAIL", "TREESELF 点了候选但没选上"); bad++; }
                if (on1 == 0) { Core.Log("FAIL", "TREESELF 点了候选但它没被勾上"); bad++; }
                if (on0 > 0) { Core.Log("FAIL", "TREESELF 换候选后原来那件还开着"); bad++; }

                checkedKeys.Clear();
                foreach (string k in backup) checkedKeys.Add(k);
                AfterTreePick();
                if (treeView != null) treeView.Sync();
                Pump(60);
                Core.Log("INFO", "TREESELF 还原勾选：" + checkedKeys.Count + " 件（原 " + total0 + "）");
                if (checkedKeys.Count != total0) { Core.Log("FAIL", "TREESELF 还原后勾选数对不上"); bad++; }
            }

            HideTreeMap();
            Console.WriteLine("treeself failures = " + bad);
            return bad;
        }

        /// <summary>扫库竞态自检：伪造「扫描中」，切目录应被记账，扫完自动补切，不炸不丢。</summary>
        internal int ScanRaceTest()
        {
            int bad = 0;
            string root = Core.Cfg.GameRoot;
            if (string.IsNullOrEmpty(root) || !Core.TestGame(root))
            {
                Core.Log("INFO", "SCANRACE 没有可用的游戏目录，跳过");
                return 0;
            }

            scanning = true;                        // 伪造：后台正在扫库
            SwitchGameRoot(root, "SCANRACE");
            if (pendingRoot == null)
            {
                Core.Log("FAIL", "SCANRACE 扫库期间切目录没有被记账（会当场枚举正被改的表）");
                bad++;
            }
            else Core.Log("INFO", "SCANRACE 扫库期间切目录已记账：" + pendingRoot);

            scanning = false;                       // 扫完了
            RunPendingRoot();
            if (pendingRoot != null) { Core.Log("FAIL", "SCANRACE 补切没跑掉"); bad++; }
            else Core.Log("INFO", "SCANRACE 补切完成，目录 = " + Core.Cfg.GameRoot);

            Console.WriteLine("scanrace failures = " + bad);
            return bad;
        }

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

        /// <summary>
        /// 自检：真开一次桌面窗口，确认浏览器起来了、重复点「桌面」不会再开一个、
        /// 关掉之后进程真的退了。返回失败条数。
        /// </summary>
        internal int DeskLaunchTest()
        {
            int bad = 0;
            CloseDesk();
            ShowDesk();
            DeskServer d = desk;
            DateTime until = DateTime.Now.AddSeconds(25);
            while (d != null && !d.BrowserAlive && DateTime.Now < until)
            {
                Application.DoEvents();
                Thread.Sleep(100);
            }
            if (d == null || !d.BrowserAlive) { Core.Log("FAIL", "desklaunch 浏览器没起来"); bad++; }
            else Core.Log("INFO", "desklaunch 桌面窗口已开：" + d.Url);

            // 再点一次「桌面」：应该把原来的窗口拎到前面，而不是换一个新的
            string u1 = d == null ? "" : d.Url;
            ToggleView();
            if (d == null || !d.Running || d.Url != u1)
            {
                Core.Log("FAIL", "desklaunch 重复点「桌面」把窗口换掉了（" + u1 + " → " + (desk == null ? "无" : desk.Url) + "）");
                bad++;
            }
            Application.DoEvents();

            CloseDesk();
            for (int i = 0; i < 80 && d != null && d.BrowserAlive; i++)
            {
                Application.DoEvents();
                Thread.Sleep(100);
            }
            if (d != null && d.BrowserAlive) { Core.Log("FAIL", "desklaunch 关掉之后浏览器还活着"); bad++; }
            else Core.Log("INFO", "desklaunch 关掉桌面后窗口已退出");

            Console.WriteLine("desklaunch failures = " + bad);
            return bad;
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
            if (MapOpen) map.RefreshPicks();
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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Installer
{
    /// <summary>
    /// 工坊的「下载列表」：在这次下过什么、下到哪、成了还是失败，都看得见；
    /// 保存位置也在这里改 —— 下载这件事的两个疑问（"东西在哪"和"以后存哪"）放在同一个窗口里。
    ///
    /// 非模态：下载还在跑也能打开看进度，关掉不影响下载。
    /// </summary>
    internal sealed class DownloadListForm : Form
    {
        private readonly ListView lv = new ListView();
        private readonly Label lblEmpty = new Label();
        private readonly Label lblSaveTo = new Label();
        private readonly Label lblDir = new Label();
        private readonly Label lblStatus = new Label();
        private readonly FlatButton btnDir = new FlatButton();
        private readonly FlatButton btnDefault = new FlatButton();
        private readonly FlatButton btnOpenDir = new FlatButton();
        private readonly FlatButton btnAgain = new FlatButton();
        private readonly FlatButton btnLocate = new FlatButton();
        private readonly FlatButton btnDelete = new FlatButton();
        private readonly FlatButton btnClear = new FlatButton();
        private readonly FlatButton btnClose = new FlatButton();

        private readonly System.Windows.Forms.Timer msgTimer = new System.Windows.Forms.Timer();   // 与 System.Threading.Timer 区分
        private string summary = "";      // 底部常驻统计；临时消息 3 秒后回落到它

        private const int M = 16;
        private static int S(int v) { return Dpi.S(v); }

        public DownloadListForm()
        {
            Text = Lang.S("工坊 · 下载列表");
            Theme.UseDpi(this);
            ClientSize = Dpi.Sz(780, 560);
            MinimumSize = Dpi.Sz(640, 420);
            Font = Theme.UI;
            BackColor = Theme.Bg;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = true;

            // ---- 顶部：保存位置 ----
            lblSaveTo.Text = Lang.S("保存到");
            lblSaveTo.Font = Theme.UISmall;
            lblSaveTo.ForeColor = Theme.Ink2;
            lblSaveTo.AutoSize = false;
            lblSaveTo.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(lblSaveTo);

            lblDir.Font = Theme.UISmall;
            lblDir.ForeColor = Theme.Ink;
            lblDir.AutoSize = false;
            lblDir.AutoEllipsis = true;
            lblDir.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(lblDir);

            btnDir.Text = Lang.S("改位置");
            btnDir.Click += delegate { PickDir(); };
            Controls.Add(btnDir);

            btnDefault.Text = Lang.S("用默认");
            btnDefault.Click += delegate { UseDefaultDir(); };
            Controls.Add(btnDefault);

            btnOpenDir.Text = Lang.S("打开文件夹");
            btnOpenDir.Click += delegate { OpenFolder(SafeDir()); };
            Controls.Add(btnOpenDir);

            // ---- 中间：记录 ----
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.MultiSelect = false;
            lv.HideSelection = false;
            lv.Columns.Add("MOD", S(300));
            lv.Columns.Add(Lang.S("大小"), S(130));
            lv.Columns.Add(Lang.S("状态"), S(190));
            lv.Columns.Add(Lang.S("时间"), S(120));
            lv.DoubleClick += delegate { Locate(); };
            lv.SelectedIndexChanged += delegate { UpdateButtons(); };
            Controls.Add(lv);

            lblEmpty.Font = Theme.UISmall;
            lblEmpty.ForeColor = Theme.Ink3;
            lblEmpty.AutoSize = false;
            lblEmpty.BackColor = Theme.Bg;          // 实心遮罩：不然 ListView 的列头会从背后透出来
            lblEmpty.TextAlign = ContentAlignment.MiddleCenter;
            lblEmpty.Cursor = Cursors.Default;
            lblEmpty.Text = Lang.S(Lang.S("还没从工坊下过东西。\r\n在左边卡片上点「下载这个 MOD」，下完就会出现在这里。"));
            Controls.Add(lblEmpty);

            // ---- 底部 ----
            btnAgain.Text = Lang.S("重新下载");
            btnAgain.Click += delegate { Redownload(); };
            Controls.Add(btnAgain);

            btnLocate.Text = Lang.S("打开所在位置");
            btnLocate.Click += delegate { Locate(); };
            Controls.Add(btnLocate);

            btnDelete.Text = Lang.S("从磁盘删除");
            btnDelete.Danger = true;
            btnDelete.Click += delegate { DeleteFile(); };
            Controls.Add(btnDelete);

            btnClear.Text = Lang.S("清空历史");
            btnClear.Click += delegate { ClearHistory(); };
            Controls.Add(btnClear);

            btnClose.Text = Lang.S("关闭");
            btnClose.DialogResult = DialogResult.Cancel;
            btnClose.Click += delegate { Close(); };
            Controls.Add(btnClose);
            CancelButton = btnClose;

            lblStatus.Font = Theme.UISmall;
            lblStatus.ForeColor = Theme.Ink3;
            lblStatus.AutoEllipsis = true;
            Controls.Add(lblStatus);

            msgTimer.Interval = 3000;
            msgTimer.Tick += delegate { msgTimer.Stop(); lblStatus.Text = summary; };

            WorkshopDownloads.Changed += OnRecordsChanged;
            FormClosed += delegate { WorkshopDownloads.Changed -= OnRecordsChanged; };
            Shown += delegate
            {
                Theme.ApplyChrome(this);
                Theme.DeepNative(this);
                RefreshDirLabel();
                RefreshList();          // 打开先把已有记录读进来，不然要等下次下载才会显示
            };
            ApplyLayout();
        }

        // ---------------------------------------------------------- 布局

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ApplyLayout();
        }

        private void ApplyLayout()
        {
            int W = ClientSize.Width, H = ClientSize.Height;
            int m = S(M);
            int y = S(14);

            // 顶部一行：左标签，右侧三颗按钮（从右往左排，窗口窄了先挤路径）
            int bx = W - m;
            btnOpenDir.SetBounds(bx - S(96), y, S(96), S(30)); bx -= S(96) + S(8);
            btnDefault.SetBounds(bx - S(76), y, S(76), S(30)); bx -= S(76) + S(8);
            btnDir.SetBounds(bx - S(76), y, S(76), S(30)); bx -= S(76) + S(10);

            lblSaveTo.SetBounds(m, y + S(6), S(48), S(18));
            lblDir.SetBounds(m + S(50), y + S(6), Math.Max(S(80), bx - (m + S(50))), S(18));

            // 中间列表
            int top = S(54);
            int bottom = S(100);
            int listH = Math.Max(S(120), H - top - bottom);
            int listW = W - 2 * m;
            lv.SetBounds(m, top, listW, listH);
            lblEmpty.SetBounds(m, top, listW, listH);

            // 名称列吃掉剩余宽度，其余三列定宽 —— 窗口拉宽时不该只在右边留一条空白
            // 注意：构造里设置 ClientSize 就会触发 OnResize，那时列还没加，得先判个数
            if (lv.Columns.Count >= 4)
            {
                int wSize = S(130), wState = S(190), wTime = S(120);
                lv.Columns[0].Width = Math.Max(S(180), listW - wSize - wState - wTime - S(6));
                lv.Columns[1].Width = wSize;
                lv.Columns[2].Width = wState;
                lv.Columns[3].Width = wTime;
            }

            // 底部：左边三颗操作，右边清空 + 关闭，中间留给状态文字
            int by = H - S(66);
            btnAgain.SetBounds(m, by, S(100), S(32));
            btnLocate.SetBounds(m + S(108), by, S(120), S(32));
            btnDelete.SetBounds(m + S(236), by, S(116), S(32));

            btnClose.SetBounds(W - m - S(76), by, S(76), S(32));
            btnClear.SetBounds(W - m - S(76) - S(8) - S(100), by, S(100), S(32));

            lblStatus.SetBounds(m, H - S(26), W - 2 * m, S(18));
        }

        // ---------------------------------------------------------- 数据

        /// <summary>记录变化可能来自下载线程：一律转回 UI 线程再刷，避免跨线程操作控件。</summary>
        private bool refreshPending;

        private void OnRecordsChanged()
        {
            if (IsDisposed || refreshPending) return;
            refreshPending = true;
            try
            {
                if (!IsHandleCreated) { refreshPending = false; return; }
                BeginInvoke(new MethodInvoker(delegate { refreshPending = false; RefreshList(); }));
            }
            catch { refreshPending = false; }
        }

        private void RefreshList()
        {
            if (IsDisposed) return;
            string keep = Selected() != null ? Selected().Id : "";

            List<WsDownload> all = WorkshopDownloads.All();
            lv.BeginUpdate();
            lv.Items.Clear();
            int keepIdx = -1;
            for (int i = 0; i < all.Count; i++)
            {
                WsDownload d = all[i];
                ListViewItem li = new ListViewItem(d.Title.Length > 0 ? d.Title : ("workshop_" + d.Id));
                li.SubItems.Add(d.SizeText);
                li.SubItems.Add(d.StateText);
                li.SubItems.Add(d.Time.ToString("MM-dd HH:mm"));
                li.Tag = d;
                if (d.Running) li.ForeColor = Theme.Info;
                else if (d.Status == "fail") li.ForeColor = Theme.Accent;
                else if (!d.ExistsOnDisk && d.Status == "ok") li.ForeColor = Theme.Ink3;
                lv.Items.Add(li);
                if (keep.Length > 0 && d.Id == keep && keepIdx < 0) keepIdx = i;
            }
            lv.EndUpdate();
            if (keepIdx >= 0)
            {
                lv.Items[keepIdx].Selected = true;
                lv.Items[keepIdx].Focused = true;
            }

            bool empty = all.Count == 0;
            lv.Visible = true;
            lblEmpty.Visible = empty;
            if (empty) lblEmpty.BringToFront();

            UpdateButtons();
            UpdateFooterCount(all);
        }

        private void UpdateFooterCount(List<WsDownload> all)
        {
            int ok = 0, fail = 0, run = 0;
            foreach (WsDownload d in all)
            {
                if (d.Running) run++;
                else if (d.Status == "ok") ok++;
                else fail++;
            }
            summary = all.Count == 0 ? "" : "共 " + all.Count + " 条 · 成功 " + ok
                + (fail > 0 ? " · 失败/取消 " + fail : "")
                + (run > 0 ? " · 下载中 " + run : "");
            if (!msgTimer.Enabled) lblStatus.Text = summary;
        }

        private void SetMsg(string text)
        {
            lblStatus.Text = text;
            msgTimer.Stop();
            msgTimer.Start();
        }

        private WsDownload Selected()
        {
            if (lv.SelectedItems.Count == 0) return null;
            return lv.SelectedItems[0].Tag as WsDownload;
        }

        private void UpdateButtons()
        {
            WsDownload d = Selected();
            bool has = d != null && !d.Running;
            btnAgain.Enabled = has && d.Url.Length > 0;
            btnLocate.Enabled = d != null && (d.ExistsOnDisk || d.Dir.Length > 0);
            btnDelete.Enabled = d != null && d.ExistsOnDisk;
            btnClear.Enabled = WorkshopDownloads.Count > 0;
        }

        // ---------------------------------------------------------- 保存位置

        private string SafeDir()
        {
            try { return Workshop.DestDir(); }
            catch (Exception ex) { return ex.Message; }
        }

        private void RefreshDirLabel()
        {
            bool custom = (Core.Cfg.WorkshopDir ?? "").Trim().Length > 0;
            string d;
            try { d = Workshop.DestDir(); }
            catch (Exception ex) { d = ex.Message; }
            lblDir.Text = custom ? d : d + Lang.S("    默认位置（跟随主库）");
            btnDefault.Enabled = custom;
        }

        private void PickDir()
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选工坊下载保存到哪个文件夹（U 盘上的目录也行）";
                string cur = SafeDir();
                if (Directory.Exists(cur)) dlg.SelectedPath = cur;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (Core.SetWorkshopDir(dlg.SelectedPath, true))
                {
                    RefreshDirLabel();
                    SetMsg("下载位置已改：之后的下载都放这里");
                    Core.Log("OK", "工坊下载位置 → " + Core.Cfg.WorkshopDir);
                }
                else
                {
                    SetMsg("这个目录建不了（可能没权限或盘没插），换一个试试");
                }
            }
        }

        private void UseDefaultDir()
        {
            Core.SetWorkshopDir("", true);
            RefreshDirLabel();
            SetMsg("已恢复默认：跟随主库");
            Core.Log("OK", "工坊下载位置 → 默认（跟随主库）");
        }

        // ---------------------------------------------------------- 行操作

        private void Locate()
        {
            WsDownload d = Selected();
            if (d == null) return;
            if (d.ExistsOnDisk) RevealInExplorer(d.Path);
            else if (d.Dir.Length > 0) OpenFolder(d.Dir);
            else SetMsg("这条记录没留下位置信息");
        }

        private static void RevealInExplorer(string file)
        {
            try { Process.Start("explorer.exe", "/select,\"" + file + "\""); }
            catch (Exception ex) { Core.Log("WARN", "打开位置失败: " + ex.Message); }
        }

        private void OpenFolder(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) { SetMsg("目录不在了：" + dir); return; }
                Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex) { SetMsg("打不开文件夹：" + ex.Message); }
        }

        private void DeleteFile()
        {
            WsDownload d = Selected();
            if (d == null || !d.ExistsOnDisk) return;
            if (MessageBox.Show(this,
                "把这个文件删掉？\r\n\r\n" + d.Path + "\r\n\r\n"
                + "只删游戏库里的这一份下载，不影响工坊上的物品。删了要再下就点「重新下载」。\r\n"
                + "（删掉没法恢复，会进回收站的是普通文件，U 盘里的不进回收站。）",
                "从磁盘删除", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.OK) return;

            try
            {
                File.Delete(d.Path);
                Core.Log("OK", "工坊：已删除下载文件 " + d.Path);
                WorkshopDownloads.Remove(d);      // 文件都没了，这条记录留着只会误导
                SetMsg("已删除：" + Path.GetFileName(d.Path));
            }
            catch (Exception ex)
            {
                SetMsg("删不掉：" + ex.Message + "（文件可能正被游戏或杀软占用）");
                Core.Log("FAIL", "工坊：删除下载文件失败 " + d.Path + "：" + ex.Message);
            }
        }

        private void ClearHistory()
        {
            if (WorkshopDownloads.Count == 0) return;
            if (MessageBox.Show(this,
                "清空下载记录？\r\n\r\n只清这张列表，磁盘上的文件一个都不动。",
                "清空历史", MessageBoxButtons.OKCancel, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            WorkshopDownloads.ClearFinished();
            SetMsg("记录已清空（文件都还在）");
        }

        private void Redownload()
        {
            WsDownload d = Selected();
            if (d == null) return;
            if (d.Url.Length == 0)
            {
                SetMsg("这条记录没有直链（早期记录没存），去工坊重新搜一次这个 MOD 即可");
                return;
            }
            string dir;
            try { dir = Workshop.DestDir(); }
            catch (Exception ex) { SetMsg(ex.Message); return; }

            string name = Workshop.SafeFileName(d.Title.Length > 0 ? d.Title : ("workshop_" + d.Id), d.Id);
            string dest = Path.Combine(dir, name);
            if (File.Exists(dest) && MessageBox.Show(this,
                "同名文件已经在了：\r\n" + dest + "\r\n\r\n覆盖它？",
                "重新下载", MessageBoxButtons.OKCancel, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.OK) return;

            WorkshopDownloads.Begin(d.Id, d.Title, d.Url, dir);
            SetMsg("重新下载：" + name);
            btnAgain.Enabled = false;

            WsItem it = new WsItem();
            it.Id = d.Id;
            it.Title = d.Title;
            it.Url = d.Url;

            Thread t = new Thread(delegate()
            {
                try
                {
                    Workshop.Download(it, dest, delegate(long got, long total)
                    {
                        WorkshopDownloads.Progress(d.Id, got, total);
                    });
                    string rn = Workshop.RenameToAddonTitle(dest);
                    if (rn != null) dest = Path.Combine(dir, rn);
                    Workshop.RememberDownload(d.Id, Path.GetFileName(dest));
                    long size = 0;
                    try { size = new FileInfo(dest).Length; } catch { }
                    WorkshopDownloads.Finish(d.Id, true, dest, size, "");
                    Core.Log("OK", "工坊：重新下载完成 " + d.Id + " → " + dest);
                }
                catch (Exception ex)
                {
                    WorkshopDownloads.Finish(d.Id, false, dest, 0, ex.Message);
                    Core.Log("FAIL", "工坊：重新下载失败 " + d.Id + "：" + ex.Message);
                }
            });
            t.IsBackground = true;
            t.Priority = ThreadPriority.BelowNormal;
            t.Start();
        }

        // ---------------------------------------------------------- 截图自检用

        /// <summary>不联网的样例：验证列表、空态、按钮使能在真实 DPI 下排得开。</summary>
        internal void FillSample()
        {
            lv.BeginUpdate();
            lv.Items.Clear();
            string[,] rows = new string[,] {
                { "【绝对精品】AWP 皮肤替换", "18.4 MB", "下载中 62%", "11-12 07:30" },
                { "Zoey 更清晰的贴图 · 重新打包", "42.1 MB", "已下载", "11-12 07:22" },
                { "Tank 摇滚音乐包", "6.8 MB", "失败：无法连接到远程服务器", "11-12 07:10" },
                { "旧版武器音效", "3.2 MB", "文件已删除", "11-11 21:04" },
            };
            for (int i = 0; i < rows.GetLength(0); i++)
            {
                ListViewItem li = new ListViewItem(rows[i, 0]);
                li.SubItems.Add(rows[i, 1]);
                li.SubItems.Add(rows[i, 2]);
                li.SubItems.Add(rows[i, 3]);
                li.Tag = new WsDownload { Id = "sample" + i, Title = rows[i, 0], Status = i == 0 ? "downloading" : (i == 2 ? "fail" : "ok") };
                if (i == 0) li.ForeColor = Theme.Info;
                else if (i == 2) li.ForeColor = Theme.Accent;
                lv.Items.Add(li);
            }
            lv.EndUpdate();
            lblEmpty.Visible = false;
            lv.Items[1].Selected = true;
            lblStatus.Text = Lang.S("共 4 条 · 成功 2 · 失败/取消 1 · 下载中 1");
            UpdateButtons();
        }

        internal void ShowEmptyForTest()
        {
            lv.Items.Clear();
            lblEmpty.Visible = true;
            lblEmpty.BringToFront();
            lblStatus.Text = "";
            UpdateButtons();
        }
    }
}

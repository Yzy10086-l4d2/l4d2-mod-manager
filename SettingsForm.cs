using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Installer
{
    /// <summary>
    /// 设置窗口：只放「本机偏好」和路径入口，不重复安装时逐次选择的组件。
    /// 版式参照 iOS 的分组列表：卡片圆角、组标题在卡片外、行内「标题 + 说明」、控件靠右。
    /// U 盘模式可指定主库所在盘；本地模式只扫描游戏目录，忽略所有外部库。
    /// </summary>
    internal sealed class SettingsForm : Form
    {
        private sealed class DriveChoice
        {
            public readonly string Root;
            public readonly string Label;
            public DriveChoice(string root, string label) { Root = root; Label = label; }
            public override string ToString() { return Label; }
        }

        private readonly MainForm owner;
        private Segmented segMode, segAppearance;
        private Toggle tglMotion;
        private ComboBox cboGame, cboLibDrive;
        private FlatButton btnGameBrowse, btnGameFind, btnLibBrowse, btnLibManage, btnSave;
        private Label lblGameState, lblLibMain, lblLibExtra, lblLibHint, lblCfg, lblLog, lblLocalOnly;
        private string pendingRoot;
        private bool syncing = true;

        internal bool WantLocal { get { return segMode != null && segMode.SelectedIndex == 1; } }
        internal bool WantReduceMotion { get { return tglMotion != null && tglMotion.Checked; } }
        internal ThemeMode WantAppearance { get { return segAppearance == null ? Theme.Mode : (ThemeMode)segAppearance.SelectedIndex; } }
        /// <summary>自测用：模拟在设置窗口里点「外观」分段（走的是真实的点击 + SelectedIndexChanged 路径）。</summary>
        internal void PreviewAppearance(ThemeMode m)
        {
            syncing = false;
            segAppearance.PickIndex((int)m);
        }

        internal string WantLibraryRoot { get { return WantLocal ? Core.ModRoot : pendingRoot; } }

        /// <summary>输入框里的游戏根目录（已去空格和尾部反斜杠）。空串表示用户没填。</summary>
        internal string WantGameRoot
        {
            get
            {
                if (cboGame == null) return "";
                string s = cboGame.Text.Trim().Trim('"').TrimEnd('\\');
                return s;
            }
        }

        public SettingsForm(MainForm owner)
        {
            this.owner = owner;
            pendingRoot = Core.ModRoot;

            Text = "设置 · L4D2 MOD 管理工具";
            Theme.UseDpi(this);
            ClientSize = Dpi.Sz(620, 730);
            Font = Theme.UI;
            BackColor = Theme.Bg;
            ForeColor = Theme.Ink;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            DoubleBuffered = true;

            Label title = new Label();
            title.Text = "设置";
            title.SetBounds(20, 14, 300, 30);
            title.Font = Theme.H1;
            title.ForeColor = Theme.Ink;
            title.BackColor = Color.Transparent;
            Controls.Add(title);

            BuildGeneral();
            BuildGameDir();
            BuildLibrary();
            BuildDiagnostics();
            BuildFooter();

            syncing = false;
            SyncModeState();
            UpdateGameState();

            Load += delegate { Theme.ApplyChrome(this); };
            Shown += delegate { Theme.ApplyChrome(this); Theme.DeepNative(this); };
        }

        // ---------------------------------------------------------- 版式零件

        /// <summary>分组标题 + 圆角卡片。标题在卡片外，是 iOS 分组列表的固定写法。</summary>
        private Card AddGroup(string caption, int top, int height)
        {
            Label head = new Label();
            head.Text = caption;
            head.SetBounds(24, top, 400, 16);
            head.Font = Theme.UI;
            head.ForeColor = Theme.Ink3;
            head.BackColor = Color.Transparent;
            Controls.Add(head);

            Card card = new Card();
            card.SetBounds(20, top + 18, ClientSize.Width - 40, height);
            Controls.Add(card);
            return card;
        }

        private Label RowTitle(Control parent, string text, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = text;
            l.SetBounds(x, y, w, 18);
            l.Font = Theme.RowTitle;
            l.ForeColor = Theme.Ink;
            Theme.Dress(l);
            l.ForeColor = Theme.Ink;
            parent.Controls.Add(l);
            return l;
        }

        private Label RowNote(Control parent, string text, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = text;
            l.SetBounds(x, y, w, 16);
            l.Font = Theme.UISmall;
            l.ForeColor = Theme.Ink3;
            l.BackColor = Color.Transparent;
            l.AutoEllipsis = true;
            parent.Controls.Add(l);
            return l;
        }

        private FlatButton SoftButton(Control parent, string text, int x, int y, int w, int h)
        {
            FlatButton b = new FlatButton();
            b.Text = text;
            b.Soft = true;
            b.SetBounds(x, y, w, h);
            parent.Controls.Add(b);
            return b;
        }

        // ---------------------------------------------------------- 分组

        private void BuildGeneral()
        {
            Card s = AddGroup("常规", 56, 150);

            RowTitle(s, "工作模式", 16, 7, 200);
            RowNote(s, "U 盘模式扫所选主库，本地模式只看游戏目录", 16, 25, 300);

            segMode = new Segmented();
            segMode.Items = new string[] { "U 盘模式", "本地模式" };
            segMode.SetBounds(332, 8, 232, 30);
            segMode.SelectedIndex = Core.IsLocal ? 1 : 0;
            segMode.SelectedIndexChanged += delegate { if (!syncing) SyncModeState(); };
            s.Controls.Add(segMode);

            s.Separate(50);

            RowTitle(s, "减少动态效果", 16, 57, 220);
            RowNote(s, "关掉按钮与状态切换的过渡，低配机器更轻快", 16, 75, 320);

            tglMotion = new Toggle();
            tglMotion.SetBounds(520, 65, 44, 26);
            tglMotion.Checked = Core.Cfg.ReduceMotion;
            s.Controls.Add(tglMotion);

            s.Separate(100);

            // 外观：纯粹的显示开关，没有「生效代价」，所以点了就立刻应用并落配置。
            // 不跟着「保存设置」走 —— 用户点完看到颜色变了，关掉窗口却又变回去，会当成 bug。
            RowTitle(s, "外观", 16, 107, 220);
            RowNote(s, "跟随系统 = 跟 Windows 走；选了立即生效", 16, 125, 310);

            segAppearance = new Segmented();
            segAppearance.Items = new string[] { "跟随系统", "浅色", "深色" };
            segAppearance.SetBounds(332, 106, 232, 30);
            segAppearance.SelectedIndex = (int)Theme.Mode;
            segAppearance.SelectedIndexChanged += delegate
            {
                if (syncing) return;
                Theme.SetMode((ThemeMode)segAppearance.SelectedIndex);
                Theme.ApplyToOpenForms();
                try
                {
                    // 模式键一定要落盘：从「跟随系统」切到「浅色」时配色可能一模一样，
                    // 但配置里存的键不一样，下次启动的行为也会不一样。
                    Core.Cfg.Appearance = Theme.ModeKey;
                    Core.SaveConfig();
                    Core.Log("INFO", "设置：外观→" + Theme.ModeLabel);
                }
                catch { }
            };
            s.Controls.Add(segAppearance);
        }

        /// <summary>
        /// 游戏目录：两种模式共用。U 盘模式决定装到哪，本地模式还额外决定扫哪里，
        /// 所以单独成组，不再只藏在主界面顶部那一行。
        /// </summary>
        private void BuildGameDir()
        {
            Card s = AddGroup("游戏目录", 238, 104);

            RowTitle(s, "游戏根目录", 16, 6, 200);

            cboGame = new ComboBox();
            cboGame.SetBounds(16, 26, 348, 24);
            cboGame.DropDownStyle = ComboBoxStyle.DropDown;
            Theme.Dress(cboGame);
            cboGame.Text = Core.Cfg.GameRoot;
            cboGame.TextChanged += delegate { if (!syncing) UpdateGameState(); };
            s.Controls.Add(cboGame);

            btnGameBrowse = SoftButton(s, "浏览…", 374, 26, 76, 24);
            btnGameBrowse.Click += delegate { BrowseGameRoot(); };

            btnGameFind = SoftButton(s, "自动找", 458, 26, 86, 24);
            btnGameFind.Click += delegate { FindGameRoot(); };

            s.Separate(56);

            lblGameState = new Label();
            lblGameState.SetBounds(16, 62, 548, 16);
            lblGameState.Font = Theme.UISmall;
            lblGameState.AutoEllipsis = true;
            s.Controls.Add(lblGameState);

            RowNote(s, "选里面有 left4dead2.exe 的那一层；本地模式只扫这里的 left4dead2\\addons。", 16, 79, 548);
        }

        private void BuildLibrary()
        {
            Card s = AddGroup("MOD 主库", 374, 146);

            RowTitle(s, "库所在盘", 16, 6, 200);

            cboLibDrive = new ComboBox();
            cboLibDrive.SetBounds(16, 26, 240, 24);
            cboLibDrive.DropDownStyle = ComboBoxStyle.DropDownList;
            Theme.Dress(cboLibDrive);
            cboLibDrive.SelectedIndexChanged += delegate { if (!syncing) OnDriveChanged(); };
            s.Controls.Add(cboLibDrive);
            FillDrives();

            btnLibBrowse = SoftButton(s, "选择文件夹…", 264, 26, 110, 24);
            btnLibBrowse.Click += delegate { BrowseLibrary(); };

            // 本地模式下把下拉框整个换掉：禁用的原生下拉框在深色主题里仍是一块白，
            // 而且这时候它本来就不可选，留着只会让人以为能改
            lblLocalOnly = new Label();
            lblLocalOnly.SetBounds(16, 30, 420, 18);
            lblLocalOnly.Font = Theme.UISmall;
            lblLocalOnly.ForeColor = Theme.Ink3;
            lblLocalOnly.BackColor = Color.Transparent;
            lblLocalOnly.Text = "本地模式不使用外部库，只读游戏目录里的 addons";
            lblLocalOnly.Visible = false;
            s.Controls.Add(lblLocalOnly);

            s.Separate(56);

            lblLibMain = new Label();
            lblLibMain.SetBounds(16, 62, 548, 16);
            lblLibMain.Font = Theme.UISmall;
            lblLibMain.ForeColor = Theme.Ink2;
            lblLibMain.AutoEllipsis = true;
            s.Controls.Add(lblLibMain);

            lblLibExtra = new Label();
            lblLibExtra.SetBounds(16, 79, 548, 16);
            lblLibExtra.Font = Theme.UISmall;
            lblLibExtra.ForeColor = Theme.Ink2;
            lblLibExtra.AutoEllipsis = true;
            s.Controls.Add(lblLibExtra);

            s.Separate(100);

            btnLibManage = SoftButton(s, "附加库目录…", 16, 110, 140, 24);
            btnLibManage.Click += delegate { ManageLibrary(); };

            lblLibHint = new Label();
            lblLibHint.SetBounds(164, 115, 396, 16);
            lblLibHint.Font = Theme.UISmall;
            lblLibHint.ForeColor = Theme.Ink3;
            lblLibHint.AutoEllipsis = true;
            lblLibHint.BackColor = Color.Transparent;
            s.Controls.Add(lblLibHint);
        }

        private void BuildDiagnostics()
        {
            Card s = AddGroup("位置与诊断", 544, 98);

            lblCfg = new Label();
            lblCfg.SetBounds(16, 8, 548, 16);
            lblCfg.Font = Theme.UISmall;
            lblCfg.ForeColor = Theme.Ink2;
            lblCfg.AutoEllipsis = true;
            lblCfg.Text = "配置：" + Core.CfgPath;
            s.Controls.Add(lblCfg);

            lblLog = new Label();
            lblLog.SetBounds(16, 26, 548, 16);
            lblLog.Font = Theme.UISmall;
            lblLog.ForeColor = Theme.Ink2;
            lblLog.AutoEllipsis = true;
            lblLog.Text = "日志：" + Core.LogDir;
            s.Controls.Add(lblLog);

            s.Separate(50);

            FlatButton openCfg = SoftButton(s, "打开配置文件夹", 16, 58, 124, 24);
            openCfg.Click += delegate { OpenConfigFolder(); };

            FlatButton openLog = SoftButton(s, "打开日志", 148, 58, 84, 24);
            openLog.Click += delegate { OpenFolder(Core.LogDir); };

            FlatButton env = SoftButton(s, "环境自检", 240, 58, 88, 24);
            env.Click += delegate { if (owner != null) owner.ShowEnv(); };
        }

        private void BuildFooter()
        {
            Panel rule = new Panel();
            rule.SetBounds(20, 676, 580, 1);
            rule.BackColor = Theme.Line;
            Controls.Add(rule);

            Label note = new Label();
            note.Text = "保存后立即生效，不改已经装进游戏的文件。";
            note.SetBounds(20, 682, 360, 20);
            note.Font = Theme.UISmall;
            note.ForeColor = Theme.Ink3;
            note.BackColor = Color.Transparent;
            Controls.Add(note);

            FlatButton cancel = new FlatButton();
            cancel.Text = "取消";
            cancel.Plain = true;
            cancel.SetBounds(408, 682, 88, 32);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            btnSave = new FlatButton();
            btnSave.Text = "保存设置";
            btnSave.SetBounds(504, 682, 96, 32);
            btnSave.Primary = true;
            btnSave.DialogResult = DialogResult.OK;
            // 校验放进点击里：以前是「先关窗、再在 ShowSettings 里发现路径不对」，
            // 用户填的东西跟着窗口一起没了，只能重新打开重填一遍。
            btnSave.Click += delegate
            {
                string err = ValidateBeforeSave();
                if (err == null) return;
                MessageBox.Show(this, err, "设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;      // 关键：不让窗口关掉
            };
            Controls.Add(btnSave);

            AcceptButton = btnSave;
            CancelButton = cancel;
        }

        /// <summary>保存前就地校验：返回 null = 可以存；否则是要弹给用户的提示。</summary>
        private string ValidateBeforeSave()
        {
            if (!WantLocal)
            {
                string root = WantLibraryRoot;
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                    return "没有找到可用的 MOD 主库，请重新选盘或指定文件夹。";
            }
            // 留空 = 不改：没填就保存不该把原来能用的游戏目录清掉
            string game = WantGameRoot;
            if (game.Length > 0 && !Core.TestGame(game))
                return "这个目录里没有 left4dead2.exe，设置没有保存：\r\n" + game;
            return null;
        }

        // ---------------------------------------------------------- 游戏目录

        private void UpdateGameState()
        {
            if (lblGameState == null) return;
            string root = WantGameRoot;
            if (root.Length == 0)
            {
                bool hasOld = Core.TestGame(Core.Cfg.GameRoot ?? "");
                lblGameState.ForeColor = hasOld ? Theme.Ink3 : Theme.Accent;
                lblGameState.Text = hasOld
                    ? "未填写：保存时保持当前目录不变"
                    : "未设置：安装前必须先指定游戏根目录";
            }
            else if (Core.TestGame(root))
            {
                lblGameState.ForeColor = Theme.Ok;
                lblGameState.Text = "已识别：left4dead2.exe 在里面，可以扫描和安装";
            }
            else
            {
                lblGameState.ForeColor = Theme.Accent;
                lblGameState.Text = "没认出：这一层找不到 left4dead2.exe";
            }
            UpdateLibraryState();
        }

        private void BrowseGameRoot()
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = "选游戏根目录（里面有 left4dead2.exe 的那个文件夹）";
                string cur = WantGameRoot;
                if (cur.Length > 0 && Directory.Exists(cur)) d.SelectedPath = cur;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                cboGame.Text = d.SelectedPath.TrimEnd('\\');
                cboGame.SelectionStart = cboGame.Text.Length;
                cboGame.SelectionLength = 0;
            }
        }

        private void FindGameRoot()
        {
            List<string> found = Core.FindGames();
            if (found == null || found.Count == 0)
            {
                lblGameState.ForeColor = Theme.Accent;
                lblGameState.Text = "没自动找到游戏，请用「浏览…」手动选";
                return;
            }
            cboGame.Text = found[0];
            if (found.Count > 1)
            {
                lblGameState.ForeColor = Theme.Warn;
                lblGameState.Text = "找到 " + found.Count + " 个，已填第一个；不对就点「浏览…」改";
            }
        }

        private void FillDrives()
        {
            cboLibDrive.Items.Clear();
            cboLibDrive.Items.Add(new DriveChoice("", "跟随程序所在盘（推荐）"));

            DriveInfo[] drives = null;
            try { drives = DriveInfo.GetDrives(); }
            catch { }

            int selected = 0;
            string currentDrive = "";
            try { currentDrive = Path.GetPathRoot(Core.ModRoot).TrimEnd('\\'); }
            catch { }

            if (drives != null)
            {
                foreach (DriveInfo d in drives)
                {
                    try
                    {
                        if (!d.IsReady || d.DriveType == DriveType.CDRom || d.DriveType == DriveType.NoRootDirectory) continue;
                        string root = d.Name.TrimEnd('\\');
                        string type = DriveTypeText(d.DriveType);
                        string free = "";
                        try { free = " · " + FormatSize(d.AvailableFreeSpace) + " 可用"; }
                        catch { }
                        cboLibDrive.Items.Add(new DriveChoice(root, root + "  " + type + free));
                        if (string.Equals(root, currentDrive, StringComparison.OrdinalIgnoreCase))
                            selected = cboLibDrive.Items.Count - 1;
                    }
                    catch { }
                }
            }

            // 空配置 = 跟随程序；有自定义主库就落在那块盘上，主库路径本身仍按 pendingRoot 显示
            if (string.IsNullOrEmpty(Core.Cfg.LibraryRoot)) selected = 0;
            syncing = true;
            cboLibDrive.SelectedIndex = selected;
            syncing = false;
        }

        private static string DriveTypeText(DriveType t)
        {
            if (t == DriveType.Removable) return "可移动磁盘";
            if (t == DriveType.Fixed) return "本地磁盘";
            if (t == DriveType.Network) return "网络盘";
            if (t == DriveType.Ram) return "内存盘";
            return "磁盘";
        }

        private static string FormatSize(long b)
        {
            if (b >= 1073741824L) return (b / 1073741824.0).ToString("N1") + " GB";
            if (b >= 1048576L) return (b / 1048576.0).ToString("N0") + " MB";
            return (b / 1024.0).ToString("N0") + " KB";
        }

        private void OnDriveChanged()
        {
            DriveChoice c = cboLibDrive.SelectedItem as DriveChoice;
            if (c == null) return;
            pendingRoot = string.IsNullOrEmpty(c.Root) ? Core.DefaultModRoot : FindKnownRoot(c.Root);
            UpdateLibraryState();
        }

        /// <summary>选盘时只认常见的库目录，不整盘递归；找不到就让用户明确选文件夹。</summary>
        private static string FindKnownRoot(string drive)
        {
            string[] candidates = new string[]
            {
                Path.Combine(Path.Combine(drive, "游戏内容"), "求生之路2各项MOD"),
                Path.Combine(drive, "求生之路2各项MOD"),
                Path.Combine(drive, "addons")
            };
            foreach (string p in candidates)
                if (Directory.Exists(p)) return p;
            return "";
        }

        private void BrowseLibrary()
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = "选择 MOD 主库根目录（里面按分类放 vpk / zip / rar）";
                if (Directory.Exists(pendingRoot)) d.SelectedPath = pendingRoot;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                pendingRoot = d.SelectedPath.TrimEnd('\\');
                SelectDriveFor(pendingRoot);
                UpdateLibraryState();
            }
        }

        private void SelectDriveFor(string path)
        {
            string drive = "";
            try { drive = Path.GetPathRoot(path).TrimEnd('\\'); }
            catch { }
            syncing = true;
            for (int i = 0; i < cboLibDrive.Items.Count; i++)
            {
                DriveChoice c = cboLibDrive.Items[i] as DriveChoice;
                if (c != null && !string.IsNullOrEmpty(c.Root)
                    && string.Equals(c.Root, drive, StringComparison.OrdinalIgnoreCase))
                { cboLibDrive.SelectedIndex = i; break; }
            }
            syncing = false;
        }

        private void SyncModeState()
        {
            bool local = WantLocal;
            bool canPick = !local;

            if (cboLibDrive != null) { cboLibDrive.Enabled = canPick; cboLibDrive.Visible = canPick; }
            if (btnLibBrowse != null) { btnLibBrowse.Enabled = canPick; btnLibBrowse.Visible = canPick; }
            if (btnLibManage != null) { btnLibManage.Enabled = canPick; btnLibManage.Visible = canPick; }
            if (lblLocalOnly != null) lblLocalOnly.Visible = !canPick;
            UpdateLibraryState();
        }

        private void UpdateLibraryState()
        {
            if (WantLocal)
            {
                string addons = null;
                try
                {
                    string g = WantGameRoot;
                    if (g.Length > 0 && Core.TestGame(g))
                        addons = Path.Combine(Path.Combine(g, "left4dead2"), "addons");
                }
                catch { }

                lblLibMain.ForeColor = Theme.Ink2;
                lblLibMain.Text = addons != null
                    ? "本地模式：只扫描 " + addons
                    : "本地模式：只扫描游戏目录 addons（先在上方指定游戏目录）";
                lblLibExtra.ForeColor = Theme.Ink3;
                lblLibExtra.Text = "主库和附加目录都不参与扫描";
                lblLibHint.Text = "游戏目录在上方改；切回 U 盘模式后原来的库仍保留。";
                if (btnSave != null) btnSave.Enabled = true;
                return;
            }

            bool valid = !string.IsNullOrEmpty(pendingRoot) && Directory.Exists(pendingRoot);
            lblLibMain.ForeColor = valid ? Theme.Ink2 : Theme.Accent;
            lblLibMain.Text = valid ? "主库：" + pendingRoot : "主库：未找到有效目录，请重新选盘或选择文件夹";
            if (btnSave != null) btnSave.Enabled = valid;

            int n = Core.Cfg.ModDirs == null ? 0 : Core.Cfg.ModDirs.Count;
            lblLibExtra.ForeColor = Theme.Ink2;
            if (n == 0)
            {
                lblLibExtra.Text = "附加目录：无";
            }
            else
            {
                string sample = "";
                for (int i = 0; i < Core.Cfg.ModDirs.Count && i < 2; i++)
                {
                    if (sample.Length > 0) sample += "；";
                    sample += Core.Cfg.ModDirs[i];
                }
                lblLibExtra.Text = "附加目录：" + n + " 个 · " + sample + (n > 2 ? " 等" : "");
            }
            lblLibHint.Text = "主库和附加目录都会扫描；只读，不复制、不移动。";
        }

        private void ManageLibrary()
        {
            if (owner == null || WantLocal) return;
            owner.ManageModDirs();
            UpdateLibraryState();
        }

        private void OpenConfigFolder()
        {
            try
            {
                if (File.Exists(Core.CfgPath))
                    Process.Start("explorer.exe", "/select,\"" + Core.CfgPath + "\"");
                else
                    OpenFolder(Core.PkgDir);
            }
            catch (Exception ex)
            {
                MessageBox.Show("打不开配置文件夹：" + ex.Message, "设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static void OpenFolder(string dir)
        {
            try
            {
                if (!string.IsNullOrEmpty(dir)) Process.Start("explorer.exe", dir);
            }
            catch { }
        }

        internal string LayoutDump()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("设置窗口客户区: " + ClientSize.Width + " x " + ClientSize.Height);
            sb.AppendLine("工作模式: " + (WantLocal ? "本地模式" : "U 盘模式"));
            sb.AppendLine("游戏目录: " + (WantGameRoot.Length == 0 ? "(未填)" : WantGameRoot));
            sb.AppendLine("游戏目录有效: " + Core.TestGame(WantGameRoot));
            sb.AppendLine("主库选择: " + (cboLibDrive.SelectedItem == null ? "(无)" : cboLibDrive.SelectedItem.ToString()));
            sb.AppendLine("主库路径: " + WantLibraryRoot);
            sb.AppendLine("主库有效: " + (WantLocal || (!string.IsNullOrEmpty(pendingRoot) && Directory.Exists(pendingRoot))));
            sb.AppendLine("减少动效: " + (WantReduceMotion ? "开" : "关"));
            sb.AppendLine("外观: " + Theme.ModeLabel + "（当前渲染: " + (Theme.IsDark ? "深色" : "浅色") + "）");
            sb.AppendLine("附加库目录: " + (Core.Cfg.ModDirs == null ? 0 : Core.Cfg.ModDirs.Count) + " 个");
            sb.AppendLine("配置路径: " + Core.CfgPath);
            sb.AppendLine("日志路径: " + Core.LogDir);
            return sb.ToString();
        }
    }

    public partial class MainForm
    {
        /// <summary>自测：把设置窗口放到屏幕外渲染一张，检查布局和当前值。不会保存任何设置。</summary>
        internal void UiTestSettings()
        {
            using (SettingsForm f = new SettingsForm(this))
            {
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-4000, -4000);
                f.Show();
                Application.DoEvents();
                f.PerformLayout();
                Application.DoEvents();

                Size full = f.Size;
                using (Bitmap bmp = new Bitmap(full.Width, full.Height))
                {
                    f.DrawToBitmap(bmp, new Rectangle(0, 0, full.Width, full.Height));
                    bmp.Save(Path.Combine(Core.LogDir, "uitest_settings.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                File.WriteAllText(Path.Combine(Core.LogDir, "uitest_settings.txt"), f.LayoutDump(), new UTF8Encoding(true));
                Core.Log("INFO", "SETTINGS 设置窗口自测完成");
            }
        }

        /// <summary>
        /// 自测：开着设置窗口连续切外观（浅→深→跟随→深→浅），每一步都渲染一张图。
        /// 切换过程中任何异常都记进日志，返回失败条数。
        /// </summary>
        internal int UiTestTheme()
        {
            int bad = 0;
            ThemeMode[] seq = new ThemeMode[]
            {
                ThemeMode.Light, ThemeMode.Dark, ThemeMode.System, ThemeMode.Dark, ThemeMode.Light
            };
            using (SettingsForm f = new SettingsForm(this))
            {
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-4000, -4000);
                f.Show();
                Application.DoEvents();
                for (int i = 0; i < seq.Length; i++)
                {
                    try
                    {
                        Theme.SetMode(seq[i]);
                        Theme.ApplyToOpenForms();
                        Application.DoEvents();
                        PerformLayout();
                        f.PerformLayout();
                        Application.DoEvents();

                        string tag = "theme" + i + "_" + Theme.ModeLabel;
                        Size fs = f.Size;
                        using (Bitmap b = new Bitmap(fs.Width, fs.Height))
                        {
                            f.DrawToBitmap(b, new Rectangle(0, 0, fs.Width, fs.Height));
                            b.Save(Path.Combine(Core.LogDir, tag + "_settings.png"), System.Drawing.Imaging.ImageFormat.Png);
                        }
                        Size ms = Size;
                        using (Bitmap b = new Bitmap(ms.Width, ms.Height))
                        {
                            DrawToBitmap(b, new Rectangle(0, 0, ms.Width, ms.Height));
                            b.Save(Path.Combine(Core.LogDir, tag + "_main.png"), System.Drawing.Imaging.ImageFormat.Png);
                        }
                        Core.Log("INFO", "THEME " + i + " → " + Theme.ModeLabel + "（渲染 " + (Theme.IsDark ? "深色" : "浅色") + "）");
                    }
                    catch (Exception ex)
                    {
                        bad++;
                        Core.Log("FAIL", "THEME 切换失败 " + seq[i] + ": " + ex);
                    }
                }
            }
            Theme.SetMode(Theme.Parse(Core.Cfg.Appearance));
            Theme.ApplyToOpenForms();
            Core.Log("OK", "THEME 自测结束：失败 " + bad + " 条");
            return bad;
        }

        private void ShowSettings()
        {
            bool oldLocal = Core.IsLocal;
            bool oldMotion = Core.Cfg.ReduceMotion;
            string oldRoot = Core.ModRoot;
            string oldGame = Core.Cfg.GameRoot ?? "";

            using (SettingsForm f = new SettingsForm(this))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;

                bool newLocal = f.WantLocal;
                bool newMotion = f.WantReduceMotion;
                string newRoot = f.WantLibraryRoot;
                string newGame = f.WantGameRoot;

                if (!newLocal && (string.IsNullOrEmpty(newRoot) || !Directory.Exists(newRoot)))
                {
                    MessageBox.Show("没有找到可用的 MOD 主库，请重新选盘或指定文件夹。", "设置",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // 留空 = 不改：避免没填就点保存，把原来能用的游戏目录清掉
                if (newGame.Length > 0 && !Core.TestGame(newGame))
                {
                    MessageBox.Show("这个目录里没有 left4dead2.exe，设置没有保存：\r\n" + newGame, "设置",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                bool gameChanged = newGame.Length > 0
                    && !string.Equals(newGame.TrimEnd('\\'), oldGame.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

                bool rootChanged = !newLocal
                    && !string.Equals(newRoot.TrimEnd('\\'), oldRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

                if (gameChanged)
                {
                    Core.Cfg.GameRoot = newGame;
                    Core.SaveConfig();
                    SetGameRootText(newGame);
                    Core.Log("OK", "设置：游戏目录→" + newGame);
                }

                if (rootChanged)
                {
                    if (!Core.SetLibraryRoot(newRoot, true))
                    {
                        MessageBox.Show("这个主库目录不可用，设置没有保存。", "设置",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    Core.Log("OK", "MOD 主库改为：" + Core.ModRoot);
                }

                if (newMotion != oldMotion)
                {
                    Core.Cfg.ReduceMotion = newMotion;
                    Core.SaveConfig();
                    Core.Log("INFO", newMotion ? "设置：已关闭轻量动效" : "设置：已开启轻量动效");
                }

                // 外观在对话框里已经实时套上去了，这里只负责落配置
                if (!string.Equals(Core.Cfg.Appearance, Theme.ModeKey, StringComparison.OrdinalIgnoreCase))
                {
                    Core.Cfg.Appearance = Theme.ModeKey;
                    Core.SaveConfig();
                    Core.Log("INFO", "设置：外观→" + Theme.ModeLabel);
                }

                if (newLocal != oldLocal && cboMode != null)
                {
                    cboMode.SelectedIndex = newLocal ? 1 : 0;   // 复用原有切换逻辑：保存并后台重扫
                }
                else if (gameChanged || rootChanged)
                {
                    ReloadLibrary(gameChanged && !rootChanged ? "正在按新的游戏目录重扫…" : null);
                }
            }
        }

        /// <summary>设置窗口改了游戏目录后，同步顶部输入框，别让界面还显示旧路径。</summary>
        private void SetGameRootText(string root)
        {
            if (txtGame == null) return;
            txtGame.Text = root;
            txtGame.SelectionStart = txtGame.Text.Length;
            txtGame.SelectionLength = 0;
        }

        /// <summary>主库或模式变更后重扫。extra 为空时状态文案由调用场景决定。</summary>
        private void ReloadLibrary(string extra)
        {
            SetBusy(true);
            SetStatus(extra ?? "正在按新的 MOD 主库重扫…");
            lblStatus.ForeColor = Theme.Ink2;
            bar.Style = ProgressBarStyle.Marquee;

            Thread th = new Thread(delegate()
            {
                string err = null;
                try { Core.ScanMods(); }
                catch (Exception ex) { err = ex.Message; Core.Log("FAIL", "切换主库后重扫失败: " + ex.Message); }
                SafeBeginInvoke(delegate()
                {
                    SetBusy(false);
                    bar.Style = ProgressBarStyle.Blocks;
                    RefreshMods();
                    RefreshConflicts(true);
                    if (treeView != null) treeView.Rebuild();
                    SetStatus(err == null ? "就绪" : "主库已切换，但重扫失败；请看日志");
                    if (err == null) lblStatus.ForeColor = Theme.Ink2;
                });
            });
            th.IsBackground = true;
            th.Start();
        }
    }
}

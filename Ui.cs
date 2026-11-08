using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Installer
{
    public partial class MainForm : Form
    {
        private ComboBox txtGame;
        private ComboBox cboMode;          // 工作模式：U 盘模式 / 本地模式
        private bool modeSync;             // 回填下拉时别触发重扫
        private FlatButton btnBrowse, btnFind, btnSettings, btnInstall, btnRestore, btnOpen, btnSwitch, btnCancel, btnClearSearch;
        private FlatButton btnView, btnPanel;     // 顶部：台面/方案树 切换、左栏抽屉
        private bool drawerOpen;                  // 左栏（装什么 / 安装选项 / 日志）是否展开
        private CheckBox chkL4N, chkSkeeto, chkAsset, chkMqcam, chkDxvk, chkLaunch, chkCfg, chkSwap, chkStrict;
        private RadioButton rdSkip, rdOver;
        private ComboBox cboTweak;
        private FlatButton btnTweak;
        private FlatButton btnLaunchEdit, btnLaunchCopy;
        private TextBox txtLaunch;
        private readonly System.Windows.Forms.Timer copyTimer = new System.Windows.Forms.Timer();
        private ComboBox cboProfile;
        // 最近套用/保存过的方案名（空=没套方案）：自检结果写回它
        private string appliedProfile = "";
        private Section gMod, gLog, gPlat, gOpt;
        private Button btnManage;
        private TextBox txtSearch;
        private Button btnSaveProfile, btnApplyProfile;
        private Panel viewHost;                  // 右栏：方案树就嵌在这里
        private Label lblLoading;                // 启动读取期间的提示，读完第一屏就撤
        private CatTree treeView;                // 方案树：按「能替换的东西」看，一个位置只留一个 MOD
        private Label lblSel, lblStatus, lblConflict;
        // 方案标记：当前下拉里那套方案包含哪些文件，用来在列表里一眼看出「这条属不属于方案」
        private Label lblScheme;
        private readonly HashSet<string> schemeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string schemeName = "";
        private ToolTip tips;
        private ProgressBar bar;
        private TextBox txtLog;
        private readonly HashSet<string> checkedKeys = new HashSet<string>();
        // 展开了哪些套装（按 ModGroup.Key）：展开后每个 vpk 单独一行，可以只勾其中一个
        private readonly HashSet<string> expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private List<string> conflictMsgs = new List<string>();
        private bool bulk;                       // 批量勾选时先别逐项刷新
        private bool applying;                   // 正在回填配置：这时的勾选变化不要触发联动
        private int logLines;                    // 日志行数，超了就截断，避免文本框越用越慢
        private readonly System.Windows.Forms.Timer searchTimer = new System.Windows.Forms.Timer();
        private volatile bool bgDone;            // RunBusy 的完成标志
        private int lastRespW, lastRespH = -1;   // 上一次重排用的窗口宽度（拖动时去重，少重排一半以上）
        private object bgResult;
        private Exception bgError;
        private int logWarnings;                 // 日志里的 WARN/FAIL 条数，直接显示在标题上

        // 缩放窗口时闪/错位：整窗走系统合成（WS_EX_COMPOSITED）+ 双缓冲，
        // 子控件不再各自重绘；配合 ApplyResponsive 里的 SuspendLayout，拖动时只排一次
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x02000000;   // WS_EX_COMPOSITED
                return cp;
            }
        }

        public MainForm(string gameRootHint)
        {
            // 版本只写在 AssemblyInfo 里，标题跟着走，省得两处对不上
            Version ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            Text = "L4D2 MOD 管理工具  v" + ver.Major + "." + ver.Minor;
            // 一开始就开够大：右栏要放下方案树（左边位置 + 右边候选），窄了列会挤成一团
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int w0 = Math.Min(1440, Math.Max(1120, wa.Width - 100));
            int h0 = Math.Min(940, Math.Max(740, wa.Height - 80));
            ClientSize = new Size(Math.Min(w0, wa.Width - 20), Math.Min(h0, wa.Height - 20));
            Font = Theme.UI;
            DoubleBuffered = true;
            KeyPreview = true;
            StartPosition = FormStartPosition.CenterScreen;
            BuildUi();
            Core.Sink = AppendLog;

            if (gameRootHint != null && Core.TestGame(gameRootHint)) txtGame.Text = gameRootHint;
            else if (Core.TestGame(Core.Cfg.GameRoot)) txtGame.Text = Core.Cfg.GameRoot;
            // 赋值后文本会整段处于选中态（蓝底），每次开窗都像待输入，光标退回开头
            txtGame.SelectionStart = 0;
            txtGame.SelectionLength = 0;
            // 都没配好也不在这里找：找游戏要遍历所有盘（含只读虚拟卷），放到 BeginInit 的后台线程做

            // 重活放到窗口显示之后做，避免启动干等；标题栏深浅要等窗口句柄存在
            Load += delegate
            {
                Theme.ApplyChrome(this);
                // 表头、滚动条这些由系统绘制的部件，句柄一建好就要打招呼，
                // 拖到 Shown 有些情况下已经画过一帧了
                Theme.DeepNative(this);
                BeginInit();
            };
            // ComboBox 显示后拿到焦点会全选文本（看着像"等你重新输入"），等激活完再把选区收掉
            Shown += delegate
            {
                Theme.ApplyChrome(this);
                Theme.DeepNative(this);
                BeginInvoke((MethodInvoker)delegate
                {
                    txtGame.SelectionStart = txtGame.Text.Length;
                    txtGame.SelectionLength = 0;
                });
            };
            // 主窗口一关，桌面的浏览器窗口和本地服务都跟着收摊，
            // 别留一个孤儿窗口在那儿转圈（它轮询不到数据，看着像卡死）
            FormClosing += delegate { CloseDesk(); };
        }

        private bool initializing = true;
        // 后台扫库进行中：ModList / Catalog.Groups 正被那个线程重建，
        // 这个时候 UI 线程再去枚举（冲突检查、重扫）必撞「集合已修改」。
        private volatile bool scanning;
        private string pendingRoot;                 // 扫库期间用户选的新目录，扫完接着切
        // 扫库心跳：扫库期间每 5 秒往日志报一声、状态栏刷新一次秒数。
        // 「一直在加载」的时候，用户至少看得到程序还活着、进行到第几秒；
        // 日志里最后一个阶段计时 + 心跳秒数也能直接指出卡在哪一步。
        // 注意：Timer 必须活在 UI 线程（后台线程上建的 Timer 没有 Pump，永远不会响）。
        private System.Windows.Forms.Timer scanWatch;
        private int scanWatchSec;

        private void StartScanWatch()
        {
            SafeInvoke(delegate
            {
                if (scanWatch == null)
                {
                    scanWatch = new System.Windows.Forms.Timer();
                    scanWatch.Interval = 5000;
                    scanWatch.Tick += delegate
                    {
                        if (!scanning) { scanWatch.Stop(); if (lblStatus.Text.StartsWith("正在扫库")) SetStatus("就绪"); return; }
                        scanWatchSec += 5;
                        Core.Log("INFO", "扫库心跳: " + scanWatchSec + " s");
                        if (scanWatchSec == 30)
                            Core.Log("WARN", "扫库超过 30 s 还没结束，明显不对劲；对照上面的阶段计时找卡点");
                        lblStatus.Text = "正在扫库… " + scanWatchSec + " s";
                    };
                }
                scanWatchSec = 0;
                scanWatch.Start();
            });
        }

        /// <summary>初始化（扫库、找游戏）跑完了没有。自测截图要等它。</summary>
        internal bool Ready { get { return !initializing; } }

        private void BeginInit()
        {
            lblStatus.Text = Core.IsLocal ? "正在扫描游戏目录…" : "正在读取 U 盘…";
            bool needFind = !Core.TestGame(txtGame.Text);
            System.Threading.Thread th = new System.Threading.Thread(delegate()
            {
                try
                {
                    bool packOk;
                    System.Diagnostics.Stopwatch __sw = System.Diagnostics.Stopwatch.StartNew();
                    List<string> packs = Core.CheckPackages(out packOk);
                    Core.Log("INFO", "启动计时: 包检查 " + __sw.ElapsedMilliseconds + " ms");
                    __sw.Restart();
                    // 先找游戏再扫库：游戏 addons 里的已装 MOD 是扫描范围的一部分，
                    // 第一次在别的电脑上打开也得第一时间认出来，不能等下次启动
                    string found = null;
                    if (needFind)
                    {
                        List<string> g = Core.FindGames();
                        if (g.Count > 0) found = g[0];
                        Core.Log("INFO", "启动计时: 找游戏 " + __sw.ElapsedMilliseconds + " ms（找到 " + g.Count + " 个）");
                        __sw.Restart();
                    }
                    // 自动找到游戏时，先把唯一的权威目录写回配置，再扫描。
                    // 否则 ScanMods 还在读旧目录，界面却显示新路径，已装状态会张冠李戴。
                    if (found != null)
                    {
                        Core.Cfg.GameRoot = found;
                        Core.SaveConfig();
                    }
                    scanning = true;
                    StartScanWatch();
                    Core.ScanMods();
                    Core.Log("INFO", "启动计时: 扫库 " + __sw.ElapsedMilliseconds + " ms（" + Core.ModList.Count + " 个）");

                    SafeInvoke(delegate
                    {
                        System.Diagnostics.Stopwatch __sw2 = System.Diagnostics.Stopwatch.StartNew();
                        if (found != null) { txtGame.Text = found; Core.Log("INFO", "自动找到游戏: " + found); }
                        else if (!Core.TestGame(txtGame.Text)) Core.Log("WARN", "没自动找到游戏，点「自动找」或「浏览…」");
                        ApplyConfig();
                        FillProfiles(null);
                        Core.Log("INFO", "启动计时: 填充列表 " + __sw2.ElapsedMilliseconds + " ms");
                        __sw2.Restart();
                        RefreshConflicts(false);          // 启动只做内存里的同名/同大小判断
                        Core.Log("INFO", "启动计时: 冲突检查 " + __sw2.ElapsedMilliseconds + " ms");
                        scanning = false;
                        initializing = false;
                        ShowPackStatus(packs, packOk);
                        // 扫库期间用户选过目录：现在安全了，接着切
                        if (pendingRoot != null) { string pr = pendingRoot; pendingRoot = null; SwitchGameRoot(pr, "补切（扫库完成）"); }
                        ShowTree();                        // 启动第一屏是方案树；桌面要点按钮才开
                    });
                }
                catch (Exception ex) { Core.Log("FAIL", "初始化失败: " + ex.Message); }
            });
            th.IsBackground = true;
            th.Start();
        }

        // 包不齐就直接顶到状态栏上，不用等装到一半才发现
        private void ShowPackStatus(List<string> packs, bool packOk)
        {
            if (packOk) { lblStatus.Text = "就绪"; lblStatus.ForeColor = Theme.Ink2; return; }
            List<string> miss = new List<string>();
            foreach (string l in packs)
            {
                if (l.IndexOf("缺", StringComparison.Ordinal) >= 0) miss.Add(l.Substring(0, l.IndexOf("缺", StringComparison.Ordinal)).Trim());
            }
            lblStatus.ForeColor = Theme.Accent;
            lblStatus.Text = "⚠ U 盘包不齐：缺 " + string.Join("、", miss.ToArray()) + "（详见日志）";
        }

        private void BuildUi()
        {
            BackColor = Theme.Bg;
            tips = new ToolTip();
            tips.AutoPopDelay = 15000;
            tips.InitialDelay = 400;
            tips.ReshowDelay = 100;

            int W = ClientSize.Width;
            int CH = ClientSize.Height;                        // 布局按实际客户区算，别再写死 700 时代的坐标
            int leftW = 396;
            int rightX = Theme.M + leftW + Theme.G;
            int rightW = W - Theme.M - rightX;

            // ---- 顶部：游戏目录 ----
            Label l1 = new Label(); l1.Text = "游戏目录"; l1.SetBounds(Theme.M, 20, 56, 20); Theme.Dress(l1); Controls.Add(l1);
            const int modeW = 104;
            const int findW = 76;
            const int browseW = 76;
            const int settingsW = 60;
            const int viewW = 56;       // 台面 ⇄ 方案树
            const int panelW = 56;      // 左栏抽屉
            const int topGap = 8;
            int settingsX = W - Theme.M - settingsW;
            int panelX = settingsX - topGap - panelW;
            int viewX = panelX - topGap - viewW;
            int findX = viewX - topGap - findW;
            int browseX = findX - topGap - browseW;
            int modeX = browseX - topGap - modeW;
            txtGame = new ComboBox(); txtGame.SetBounds(76, 18, modeX - 8 - 76, 24);
            txtGame.DropDownStyle = ComboBoxStyle.DropDown; Theme.Dress(txtGame); Controls.Add(txtGame);
            cboMode = new ComboBox(); cboMode.SetBounds(modeX, 18, modeW, 24);
            cboMode.DropDownStyle = ComboBoxStyle.DropDownList; Theme.Dress(cboMode);
            cboMode.Items.Add("U 盘模式"); cboMode.Items.Add("本地模式");
            cboMode.SelectedIndexChanged += delegate { OnModeChanged(); };
            tips.SetToolTip(cboMode, "U 盘模式：所选主库是主，装 MOD = 从库里复制到游戏。\r\n游戏目录里和库里同名同大小的那份会合并显示（标「已装」），不再重复列。\r\n本地模式：只看游戏目录 addons，不扫主库和附加目录；删除 = 从游戏里卸掉（文件真删）。");
            Controls.Add(cboMode);
            btnBrowse = new FlatButton(); btnBrowse.Text = "浏览…"; btnBrowse.SetBounds(browseX, 16, browseW, Theme.H); btnBrowse.Click += delegate { BrowseGame(); }; Controls.Add(btnBrowse);
            btnFind = new FlatButton(); btnFind.Text = "自动找"; btnFind.SetBounds(findX, 16, findW, Theme.H); btnFind.Click += delegate { FindGame(); }; Controls.Add(btnFind);
            btnSettings = new FlatButton(); btnSettings.Text = "设置";
            btnSettings.SetBounds(settingsX, 16, settingsW, Theme.H);
            btnSettings.AccessibleName = "打开设置";
            btnSettings.Click += delegate { ShowSettings(); };
            tips.SetToolTip(btnSettings, "工作模式、动效、MOD 库目录，以及配置和日志位置。");
            Controls.Add(btnSettings);

            // 总览 / 方案树：同一份数据的两种看法，按钮上写的是「点了会去哪种」
            btnView = new FlatButton(); btnView.Text = "总览";
            btnView.SetBounds(viewX, 16, viewW, Theme.H);
            btnView.Click += delegate { ToggleView(); };
            tips.SetToolTip(btnView, "套装总览：这套里每个位置装的是哪个 MOD，一眼看完。\r\n左边点大类，右边列这个类的位置和已装的 MOD；没装的灰着，库里没有的空心点。\r\n点一行就回方案树改那个位置（看板只看不改）。再点一下回到方案树。");
            Controls.Add(btnView);

            btnPanel = new FlatButton(); btnPanel.Text = "面板";
            btnPanel.SetBounds(panelX, 16, panelW, Theme.H);
            btnPanel.Click += delegate { SetDrawer(!drawerOpen); };
            tips.SetToolTip(btnPanel, "展开 / 收起左边那栏（装什么、安装选项、日志）。\r\n收起来以后整个窗口就是一张台面。");
            Controls.Add(btnPanel);

            // ---- 左栏上：装什么 ----
            Section gp = new Section();
            gp.Caption = "装什么"; gp.SetBounds(Theme.M, 56, leftW, 222); Controls.Add(gp);
            chkL4N = AddChk(gp, "L4N 平台（只有 MQCam 相机要用它）", 12, 28, true, true);
            tips.SetToolTip(chkL4N, "L4N 不是强依赖：不装它照样能装 Skeeto / dxvk / MOD。\r\n只有 MQCam 相机要跑在它的 neko 框架里 —— 勾 MQCam 时会自动把 L4N 带上，取消 L4N 会把 MQCam 一起取消。\r\n装过一次之后再装会自动跳过（除非在下面选「覆盖重装」）。");
            chkSkeeto = AddChk(gp, "Skeeto 增强框架（命中反馈 / 准星 / HUD）", 12, 52, true, true);
            chkAsset = AddChk(gp, "Skeeto 素材包 177 MB", 12, 76, true, true);
            chkMqcam = AddChk(gp, "MQCam 相机（F2 / F3 / F4 视角）", 12, 100, true, true);
            tips.SetToolTip(chkMqcam, "相机模组要跑在 L4N 的 neko 框架里：勾它会自动把上面的 L4N 一起勾上。");
            // L4N 和 MQCam 的依赖只在这两格之间：勾相机 → 带上平台；去掉平台 → 相机也跟着去掉
            chkMqcam.CheckedChanged += delegate
            {
                if (applying || !chkMqcam.Checked || chkL4N.Checked) return;
                chkL4N.Checked = true;
                Log("MQCam 相机要用 L4N 的 neko 框架，已自动勾上 L4N");
            };
            chkL4N.CheckedChanged += delegate
            {
                if (applying || chkL4N.Checked || !chkMqcam.Checked) return;
                chkMqcam.Checked = false;
                Log("不装 L4N 的话 MQCam 也跑不起来，已把 MQCam 一起取消（要相机就两个都勾）");
            };
            chkDxvk = AddChk(gp, "dxvk 防闪退（d3d9.dll）", 12, 124, true, true);
            chkLaunch = AddChk(gp, "写 Steam 启动项", 12, 148, true, true);
            chkCfg = AddChk(gp, "写参数配置（L4N 按键 + Skeeto 设置）", 12, 172, true, true);
            tips.SetToolTip(chkCfg, "从 U 盘的参数存档里取出你调好的 autoexec.cfg 和 skeeto_setting.ini，\r\n直接写进这台机器。换机器装机就不用再手调了。\r\n下面那行选套用哪一套；没有存档就退回旧做法（搬 U 盘根目录的 autoexec.cfg）。");
            Label l4 = new Label(); l4.Text = "启动项"; l4.SetBounds(12, 198, 44, 20); Theme.Dress(l4); gp.Controls.Add(l4);
            txtLaunch = new TextBox(); txtLaunch.SetBounds(60, 196, 246, 24); Theme.Dress(txtLaunch); gp.Controls.Add(txtLaunch);
            // 一行输入框放不下 130 个字符：外面只给个预览，真正的编辑交给「…」里的放大窗口
            txtLaunch.TextChanged += delegate { UpdateLaunchTip(); };
            txtLaunch.DoubleClick += delegate { EditLaunch(); };

            // 复制 / 放大：两个按钮都靠右，位置统一由 LayoutLaunchRow 算，拖动窗口也不会错位
            btnLaunchCopy = new FlatButton(); btnLaunchCopy.Text = "复制";
            btnLaunchCopy.SetBounds(310, 196, 44, 24);
            btnLaunchCopy.Click += delegate { CopyLaunch(); };
            tips.SetToolTip(btnLaunchCopy, "把当前启动项整段复制到剪贴板，方便贴到 Steam、记事本或发给别人。");
            gp.Controls.Add(btnLaunchCopy);
            copyTimer.Interval = 1400;
            copyTimer.Tick += delegate(object s2, EventArgs e2)
            {
                copyTimer.Stop();
                if (btnLaunchCopy != null) btnLaunchCopy.Text = "复制";
            };

            btnLaunchEdit = new FlatButton(); btnLaunchEdit.Text = "…"; btnLaunchEdit.Font = Theme.UIBold;
            btnLaunchEdit.AccessibleName = "放大编辑启动项";
            btnLaunchEdit.SetBounds(358, 196, 26, 24);
            btnLaunchEdit.Click += delegate { EditLaunch(); };
            tips.SetToolTip(btnLaunchEdit, "放大编辑启动项（可换行、可恢复默认、可把 0/1 换成 0 或 1）。双击输入框也能打开。");
            gp.Controls.Add(btnLaunchEdit);

            LayoutLaunchRow(leftW);
            UpdateLaunchTip();

            // ---- 左栏中：安装选项（原来挤在 MOD 列表底部，语义上属于"怎么装"，跟"挑什么"分开）----
            gOpt = new Section();
            gOpt.Caption = "安装选项"; gOpt.SetBounds(Theme.M, 286, leftW, 144); Controls.Add(gOpt);
            chkSwap = new CheckBox(); chkSwap.Text = "换整套（把不在这套里的 MOD 关掉）";
            chkSwap.SetBounds(12, 28, leftW - 24, 22); Theme.Dress(chkSwap); gOpt.Controls.Add(chkSwap);
            chkSwap.Checked = true;
            tips.SetToolTip(chkSwap, "切方案时，上一套的 MOD 在 addonlist 里改成「关闭」，文件留在 addons 里不动。\r\n"
                + "切回去直接打开就能用，不用重新复制。\r\n"
                + "只认 U 盘库里有的 vpk —— 你自己丢进 addons 的不会被碰。");
            chkStrict = new CheckBox(); chkStrict.Text = "严格校验（慢一倍，更保险）";
            chkStrict.SetBounds(12, 52, leftW - 24, 22); Theme.Dress(chkStrict); gOpt.Controls.Add(chkStrict);
            tips.SetToolTip(chkStrict, "默认只比文件大小。勾上之后，复制完再回读源文件和目标各算一遍 SHA256 比对。");

            // 已装的怎么办：这是机房里最常遇到的一件事 —— 有的机器上次装过一半，有的要原样重来
            Label l5 = new Label(); l5.Text = "已装的"; l5.SetBounds(12, 80, 56, 20); Theme.Dress(l5); gOpt.Controls.Add(l5);
            rdSkip = new RadioButton(); rdSkip.Text = "跳过"; rdSkip.SetBounds(70, 78, 66, 22); Theme.Dress(rdSkip); gOpt.Controls.Add(rdSkip);
            rdOver = new RadioButton(); rdOver.Text = "覆盖重装"; rdOver.SetBounds(140, 78, 96, 22); Theme.Dress(rdOver); gOpt.Controls.Add(rdOver);
            Label l6 = new Label(); l6.Text = "跳过已有 / 全部重写"; l6.SetBounds(240, 80, leftW - 240 - 12, 18); l6.Font = Theme.UISmall; Theme.Dress(l6); l6.ForeColor = Theme.Ink3; gOpt.Controls.Add(l6);
            tips.SetToolTip(rdSkip, "平台、Skeeto、素材包、MQCam、MOD 只要已经装过且大小一致就跳过。\r\n装得快，适合新机器或者上次已经装好了。");
            tips.SetToolTip(rdOver, "不管装没装过，全都重新写一遍。\r\n用来修「装了一半」「文件被删/被改」的机器，慢一些但最干净。");

            // 参数配置：全套管理在对话框里，外面只留「选哪套 + 还原」
            Label l7 = new Label(); l7.Text = "参数"; l7.SetBounds(12, 110, 56, 20); Theme.Dress(l7); gOpt.Controls.Add(l7);
            cboTweak = new ComboBox(); cboTweak.SetBounds(70, 108, 156, 24); cboTweak.DropDownStyle = ComboBoxStyle.DropDownList; Theme.Dress(cboTweak);
            cboTweak.SelectedIndexChanged += delegate { UpdateTweakTip(); };
            gOpt.Controls.Add(cboTweak);
            tips.SetToolTip(cboTweak, "装机时勾着「写参数配置」，就会把这套 autoexec.cfg + skeeto_setting.ini 推过去。");
            btnTweak = new FlatButton(); btnTweak.Text = "参数配置…"; btnTweak.SetBounds(230, 106, 90, Theme.H); btnTweak.Click += delegate { ManageTweaks(); }; gOpt.Controls.Add(btnTweak);
            tips.SetToolTip(btnTweak, "保存这台机器调好的参数 / 套用存档 / 删除存档。");

            // ---- 左栏下：日志 ----
            Section gl = new Section();
            gl.Caption = "日志"; gl.SetBounds(Theme.M, 438, leftW, CH - 438 - 116); Controls.Add(gl);
            FlatButton btnLog = new FlatButton(); btnLog.Text = "打开日志"; btnLog.SetBounds(leftW - 12 - 68, 1, 68, 24); btnLog.Font = Theme.UISmall; btnLog.Click += delegate { try { System.Diagnostics.Process.Start("explorer.exe", Core.LogDir); } catch { } }; gl.Controls.Add(btnLog);
            txtLog = new TextBox();
            txtLog.SetBounds(12, 30, leftW - 24, Math.Max(60, CH - 438 - 116 - 34));   // 日志框撑满卡片
            txtLog.Multiline = true; txtLog.ScrollBars = ScrollBars.Both; txtLog.WordWrap = false; txtLog.ReadOnly = true;
            txtLog.BorderStyle = BorderStyle.None;
            txtLog.BackColor = Theme.Surface;
            txtLog.ForeColor = Theme.Ink2;
            txtLog.Font = Theme.Mono;      // 日志是数据：等宽才能让时间戳和级别对齐
            gl.Controls.Add(txtLog);

            // ---- 右栏：MOD 库 ----
            // 只有一屏：方案树。左边是「能替换的东西」的位置，右边是它的候选。
            // 不另开窗口，勾选、搜索、方案下拉始终在视线里，关窗口把进程留在后台这种事也不会再发生。
            int modH = CH - 56 - 116;                        // 到下方按钮行为止，中间全给 MOD 库
            Section gm = new Section();
            gm.Caption = "MOD 库（U 盘的 vpk + 压缩包）"; gm.SetBounds(rightX, 56, rightW, modH); Controls.Add(gm);

            Label l8 = new Label(); l8.Text = "方案"; l8.SetBounds(12, 34, 34, 20); Theme.Dress(l8); gm.Controls.Add(l8);
            cboProfile = new ComboBox(); cboProfile.SetBounds(50, 30, 204, 24); cboProfile.DropDownStyle = ComboBoxStyle.DropDownList; Theme.Dress(cboProfile); gm.Controls.Add(cboProfile);
            cboProfile.SelectedIndexChanged += delegate { ProfileSelectionChanged(); };
            tips.SetToolTip(cboProfile, "只选择方案，不会立刻改当前勾选；确认后点「套用」。");
            btnSaveProfile = new FlatButton(); btnSaveProfile.Text = "存为方案"; btnSaveProfile.SetBounds(262, 28, 84, Theme.H); btnSaveProfile.Click += delegate { SaveProfile(); }; gm.Controls.Add(btnSaveProfile);
            btnApplyProfile = new FlatButton(); btnApplyProfile.Text = "套用"; btnApplyProfile.SetBounds(354, 28, 58, Theme.H); btnApplyProfile.Click += delegate { ApplyProfile(); }; gm.Controls.Add(btnApplyProfile);
            tips.SetToolTip(btnApplyProfile, "把选中的方案套用到当前勾选；如果会覆盖现有选择，会先确认。");
            btnManage = new FlatButton(); btnManage.Text = "方案管理…"; btnManage.SetBounds(rightW - 12 - 88, 28, 88, Theme.H); btnManage.Click += delegate { ManageProfiles(); }; gm.Controls.Add(btnManage);

            Label l9 = new Label(); l9.Text = "搜索"; l9.SetBounds(12, 66, 34, 20); Theme.Dress(l9); gm.Controls.Add(l9);
            txtSearch = new TextBox(); txtSearch.SetBounds(50, 62, rightW - 126, 24); txtSearch.AccessibleName = "搜索 MOD"; Theme.Dress(txtSearch); txtSearch.TextChanged += delegate { searchTimer.Stop(); searchTimer.Start(); }; gm.Controls.Add(txtSearch);
            tips.SetToolTip(txtSearch, "搜名字 / 替换了什么 / 位置名。Ctrl+F 快速聚焦，Esc 清空并返回方案树。\r\n停止输入 250 ms 自动筛；点搜索结果可跳回它在树里的位置。");
            btnClearSearch = new FlatButton(); btnClearSearch.Text = "清空"; btnClearSearch.SetBounds(rightW - 68, 62, 56, 24);
            btnClearSearch.AccessibleName = "清空搜索并返回方案树";
            btnClearSearch.Click += delegate { txtSearch.Clear(); RefreshMods(); txtSearch.Focus(); };
            tips.SetToolTip(btnClearSearch, "清空搜索并返回方案树（Esc）");
            gm.Controls.Add(btnClearSearch);

            // 方案分布：一行说完这套在各类里放了几个
            lblScheme = new Label();
            lblScheme.SetBounds(12, 92, rightW - 24, 18);
            lblScheme.Font = Theme.UISmall;
            lblScheme.ForeColor = Theme.Ink3;
            lblScheme.AutoEllipsis = true;
            lblScheme.Text = "";
            gm.Controls.Add(lblScheme);

            int cw = rightW - 24;
            int ch = modH - 118 - 74;

            // 方案树就放在这块矩形里
            viewHost = new Panel();
            viewHost.SetBounds(12, 118, cw, ch);
            viewHost.BackColor = Theme.Surface;
            viewHost.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            viewHost.SizeChanged += delegate
            {
                if (treeView != null)
                    treeView.SetBounds(0, 0, viewHost.ClientSize.Width, viewHost.ClientSize.Height);
            };
            gm.Controls.Add(viewHost);

            // 启动读取提示：扫库没完成前树区是空的，不放这句用户会以为程序坏了
            lblLoading = new Label();
            lblLoading.Dock = DockStyle.Fill;
            lblLoading.BackColor = Theme.Surface;
            lblLoading.Font = Theme.UIBold;
            lblLoading.ForeColor = Theme.Ink2;
            lblLoading.TextAlign = ContentAlignment.MiddleCenter;
            lblLoading.Text = "正在读取 U 盘上的 MOD 库…\r\n第一次启动或刚更新完可能要等几秒，别急着关";
            viewHost.Controls.Add(lblLoading);
            lblLoading.BringToFront();

            lblSel = new Label(); lblSel.SetBounds(12, modH - 64, rightW - 24, 18); lblSel.Text = "已选 0 个"; lblSel.Font = Theme.UISmall; Theme.Dress(lblSel); gm.Controls.Add(lblSel);
            lblConflict = new Label();
            lblConflict.SetBounds(12, modH - 40, rightW - 24, 34);
            lblConflict.Text = "";
            lblConflict.Font = Theme.UISmall;
            lblConflict.BackColor = Color.Transparent;
            lblConflict.ForeColor = Theme.Warn;
            lblConflict.Cursor = Cursors.Hand;
            lblConflict.Click += delegate { ShowConflictDetail(); };
            gm.Controls.Add(lblConflict);

            // ---- 底部动作区：主操作只有一个 ----
            btnInstall = new FlatButton(); btnInstall.Text = "开始安装"; btnInstall.Primary = true;
            btnInstall.AccessibleName = "开始安装所选组件和 MOD";
            btnInstall.Font = new Font(Theme.UI, FontStyle.Bold);
            btnInstall.SetBounds(W - Theme.M - 104 - 8 - 96 - 8 - 116 - 8 - 112, CH - 104, 112, Theme.HP);
            btnInstall.Click += delegate { DoInstall(); }; Controls.Add(btnInstall);
            btnSwitch = new FlatButton(); btnSwitch.Text = "管理已装 MOD";
            btnSwitch.SetBounds(W - Theme.M - 104 - 8 - 96 - 8 - 116, CH - 104, 116, Theme.HP);
            btnSwitch.Click += delegate { DoSwitch(); }; Controls.Add(btnSwitch);
            btnRestore = new FlatButton(); btnRestore.Text = "一键还原"; btnRestore.Danger = true;
            btnRestore.SetBounds(W - Theme.M - 104 - 8 - 96, CH - 104, 96, Theme.HP);
            btnRestore.Click += delegate { DoRestore(); }; Controls.Add(btnRestore);
            tips.SetToolTip(btnRestore, "危险操作：移除本次安装的平台文件和 MOD。执行前会再次确认。");
            btnOpen = new FlatButton(); btnOpen.Text = "打开游戏目录";
            btnOpen.SetBounds(W - Theme.M - 104, CH - 104, 104, Theme.HP);
            btnOpen.Click += delegate { try { System.Diagnostics.Process.Start("explorer.exe", txtGame.Text); } catch { } }; Controls.Add(btnOpen);

            bar = new ProgressBar(); bar.SetBounds(Theme.M, CH - 56, 440, 12); bar.Visible = false; Controls.Add(bar);
            btnCancel = new FlatButton();
            btnCancel.Text = "停止剩余";
            btnCancel.SetBounds(Theme.M + 452, CH - 60, 92, 24);
            btnCancel.Visible = false;
            btnCancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            btnCancel.Click += delegate { RequestStopInstall(); };
            tips.SetToolTip(btnCancel, "在当前步骤或文件结束后停止后续安装。已经写入的文件会保留，不会自动回滚。");
            Controls.Add(btnCancel);
            lblStatus = new Label(); lblStatus.SetBounds(Theme.M, CH - 36, W - 2 * Theme.M, 20); lblStatus.Text = "就绪";
            lblStatus.ForeColor = Theme.Ink2; lblStatus.BackColor = Color.Transparent; Controls.Add(lblStatus);

            // 随窗口缩放：左右拉伸的跟着走，底部按钮贴右下
            gMod = gm; gLog = gl; gPlat = gp;
            // 最小尺寸也守着屏幕：网吧小屏上不能出现「窗口比桌面还宽」
            Rectangle wmin = Screen.PrimaryScreen.WorkingArea;
            MinimumSize = new Size(Math.Min(1120, wmin.Width - 20), Math.Min(740, wmin.Height - 20));
            txtGame.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            cboMode.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnBrowse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnFind.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            gp.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            gOpt.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            gl.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
            gm.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            lblSel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            lblConflict.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            txtLog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            btnInstall.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnSwitch.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnRestore.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnOpen.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            bar.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            btnCancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            lblStatus.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            searchTimer.Interval = 250;
            searchTimer.Tick += delegate(object s2, EventArgs e2) { searchTimer.Stop(); RefreshMods(); };
            SetDrawer(false);                  // 默认收起：整个窗口就是一张台面
            Resize += delegate { ApplyResponsive(); };
            ApplyResponsive();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.F))
            {
                if (txtSearch != null)
                {
                    txtSearch.Focus();
                    txtSearch.SelectAll();
                }
                return true;
            }
            if (keyData == Keys.Escape && txtSearch != null && txtSearch.Text.Length > 0)
            {
                txtSearch.Clear();
                RefreshMods();
                txtSearch.Focus();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private CheckBox AddChk(Section p, string text, int x, int y, bool chk, bool enabled)
        {
            CheckBox c = new CheckBox();
            c.Text = text; c.SetBounds(x, y, p.Width - x - 12, 22); c.Checked = chk; c.Enabled = enabled;
            Theme.Dress(c);
            p.Controls.Add(c);
            return c;
        }

        private void ApplyConfig()
        {
            // 回填配置时别触发「勾相机自动勾平台」这类联动，否则会把存下来的组合改掉
            applying = true;
            try
            {
                chkL4N.Checked = Core.Cfg.L4N;
                chkDxvk.Checked = Core.Cfg.Dxvk;
                chkSkeeto.Checked = Core.Cfg.Skeeto;
                chkAsset.Checked = Core.Cfg.AssetPack;
                chkMqcam.Checked = Core.Cfg.Mqcam;
                chkLaunch.Checked = Core.Cfg.Launch;
            }
            finally { applying = false; }
            txtLaunch.Text = Core.Cfg.LaunchOptions;
            // 第一次发现有参数存档就默认勾上 —— 用户存过参数，摆明了就是下次装机要自动写进去
            bool firstTimeTweak = Core.Cfg.TweakSet.Length == 0 && Tweaks.List().Count > 0;
            chkCfg.Checked = Core.Cfg.CfgFile || firstTimeTweak;
            rdSkip.Checked = !Core.Cfg.Overwrite;
            rdOver.Checked = Core.Cfg.Overwrite;
            modeSync = true;
            try { cboMode.SelectedIndex = Core.IsLocal ? 1 : 0; }
            finally { modeSync = false; }
            FillTweaks(Core.Cfg.TweakSet);
            foreach (string m in Core.Cfg.Mods) checkedKeys.Add(m);
            CheckRowsByKeys();
            UpdateSel();
            Log("已载入上次勾选（" + checkedKeys.Count + " 个 MOD）");
        }

        /// <summary>
        /// 切工作模式：存进配置，再按新模式重扫。
        /// U 盘模式 = U 盘库是主，游戏目录里同名同大小的那份合并显示；本地模式 = 只把游戏 addons 当库。
        /// </summary>
        private void OnModeChanged()
        {
            if (modeSync || cboMode == null || cboMode.SelectedIndex < 0) return;
            string want = cboMode.SelectedIndex == 1 ? "local" : "usb";
            if (string.Equals(want, Core.Cfg.Mode, StringComparison.OrdinalIgnoreCase)) return;
            // 扫库没结束时切模式 = 两个扫库线程并发重建同一张表，
            // 还会两路同时写识别缓存（实测能把 2.7MB 落盘拖到 42 秒，界面就一直「正在扫库」）。
            // 这里直接把下拉弹回去，等扫完再切。
            if (scanning)
            {
                modeSync = true;
                cboMode.SelectedIndex = string.Equals(Core.Cfg.Mode, "local", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                modeSync = false;
                SetStatus("正在扫库，等扫完再切工作模式");
                Core.Log("INFO", "扫库进行中切工作模式被拦下，先记着不动");
                return;
            }
            Core.Cfg.Mode = want;
            Core.SaveConfig();
            Core.Log("OK", "工作模式：" + (want == "local" ? "本地模式（游戏目录 addons 当库）" : "U 盘模式（U 盘库是主，游戏目录重复项合并）"));
            lblStatus.Text = want == "local" ? "正在按「本地模式」重扫游戏目录…" : "正在按「U 盘模式」重扫库…";
            btnInstall.Enabled = false;
            scanning = true;
            StartScanWatch();
            System.Threading.Thread th = new System.Threading.Thread(delegate()
            {
                try { Core.ScanMods(); }
                catch (Exception ex) { Core.Log("FAIL", "切换模式后重扫失败: " + ex.Message); }
                SafeInvoke(delegate()
                {
                    scanning = false;
                    btnInstall.Enabled = true;
                    RefreshMods();
                    RefreshConflicts();
                    if (treeView != null) RebuildViews();
                    RunPendingRoot();
                    lblStatus.Text = "就绪";
                    RefreshMods();
                    RefreshConflicts();
                    if (treeView != null) RebuildViews();
                    lblStatus.Text = "就绪";
                });
            });
            th.IsBackground = true;
            th.Start();
        }

        // ---------------------------------------------------------- 参数配置

        /// <summary>填「参数」下拉：第一项是不套用，其余是 U 盘上的存档。</summary>
        private void FillTweaks(string keep)
        {
            if (keep == null) keep = TweakName();
            Tweaks.ImportLegacy(txtGame == null ? null : txtGame.Text.Trim());
            cboTweak.Items.Clear();
            cboTweak.Items.Add("不套用");
            foreach (string n in Tweaks.List()) cboTweak.Items.Add(n);
            int idx = 0;
            for (int i = 1; i < cboTweak.Items.Count; i++)
                if (string.Equals(cboTweak.Items[i].ToString(), keep, StringComparison.OrdinalIgnoreCase)) idx = i;
            // 从没选过但有存档：默认停在第一套上，让人一眼看到"这台会写哪套参数"
            if (idx == 0 && (keep == null || keep.Length == 0) && cboTweak.Items.Count > 1) idx = 1;
            cboTweak.SelectedIndex = idx;
            UpdateTweakTip();
        }

        /// <summary>当前选的存档名；空串表示不套用。</summary>
        private string TweakName()
        {
            string s2 = cboTweak == null ? null : (cboTweak.SelectedItem as string);
            if (s2 == null || s2 == "(不套用)") return "";
            return s2;
        }

        private void SelectTweak(string name)
        {
            for (int i = 0; i < cboTweak.Items.Count; i++)
                if (string.Equals(cboTweak.Items[i].ToString(), name, StringComparison.OrdinalIgnoreCase)) { cboTweak.SelectedIndex = i; return; }
        }

        private void UpdateTweakTip()
        {
            string n = TweakName();
            tips.SetToolTip(cboTweak, n.Length == 0
                ? "不套用存档。勾着「写参数配置」时会退回旧做法：搬 U 盘根目录的 autoexec.cfg。"
                : ("装机时写入：" + Tweaks.DirOf(n)));
        }

        /// <summary>启动项一行：输入框 + 复制 + 放大编辑。位置只在这里算，构建和重排共用。</summary>
        private void LayoutLaunchRow(int leftW)
        {
            const int editW = 26, copyW = 44, gap = 4;
            int editX = leftW - 12 - editW;
            int copyX = editX - gap - copyW;
            if (btnLaunchEdit != null) btnLaunchEdit.Left = editX;
            if (btnLaunchCopy != null) btnLaunchCopy.Left = copyX;
            if (txtLaunch != null) txtLaunch.Width = Math.Max(80, copyX - gap - 60);
        }

        /// <summary>一键复制启动项：复制清洗后的整段，还带 0/1 占位会额外提醒。</summary>
        private void CopyLaunch()
        {
            string s = LaunchArgs.Clean(txtLaunch.Text);
            if (s.Length == 0)
            {
                FlashCopy("空");
                Log("启动项是空的，没有可复制的内容");
                return;
            }
            try { Clipboard.SetText(s); }
            catch (Exception ex)
            {
                FlashCopy("失败");
                Log("复制启动项失败：" + ex.Message);
                return;
            }
            FlashCopy("已复制");
            Log("启动项已复制（" + LaunchArgs.CountSwitches(s) + " 个开关，" + s.Length + " 个字符）");
            if (LaunchArgs.HasSlot(s))
                Log("WARN", "复制的内容里还有 0/1 占位，贴到 Steam 前要改成 0 或 1");
        }

        /// <summary>按钮上的短反馈，1.4 秒后自动变回「复制」。</summary>
        private void FlashCopy(string text)
        {
            if (btnLaunchCopy == null) return;
            btnLaunchCopy.Text = text;
            copyTimer.Stop();
            copyTimer.Start();
        }

        /// <summary>启动项输入框只有 ~300px，鼠标悬停看全文，改内容进放大窗口。</summary>
        private void UpdateLaunchTip()
        {
            string s = LaunchArgs.Clean(txtLaunch.Text);
            string tip;
            if (s.Length == 0) tip = "现在是空的：不会写启动项，Steam 里已有的启动项也不会被清掉。\r\n右边「复制」可整段拷到剪贴板；点「…」或双击这里编辑。";
            else
            {
                tip = s + "\r\n\r\n" + (LaunchArgs.HasSlot(s)
                        ? "注意：-skeeto_particle_platform 还是 0/1，要改成 0 或 1 才生效。"
                        : "共 " + LaunchArgs.CountSwitches(s) + " 个开关。")
                    + "\r\n右边「复制」可整段拷到剪贴板；点「…」或双击这里放大编辑。";
            }
            tips.SetToolTip(txtLaunch, tip);
        }

        private void EditLaunch()
        {
            using (LaunchForm f = new LaunchForm(txtLaunch.Text))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                txtLaunch.Text = f.Result;
                UpdateLaunchTip();
            }
        }

        private void ManageTweaks()
        {
            string root = txtGame.Text.Trim();
            using (TweakForm f = new TweakForm(root, TweakName()))
            {
                f.ShowDialog(this);
                FillTweaks(f.Chosen);
                if (f.Applied) { chkCfg.Checked = true; Log("参数配置动过了，装机时会写进这台机器"); }
            }
        }

        private void FillProfiles(string keep)
        {
            cboProfile.Items.Clear();
            List<string> ps = ProfileStore.ListProfiles();
            if (ps.Count == 0) { cboProfile.Items.Add("还没有方案"); cboProfile.SelectedIndex = 0; LoadSchemeKeys(); SyncPicks(); return; }
            cboProfile.Items.Add("当前选择");
            foreach (string p in ps) cboProfile.Items.Add(p);
            string want = string.IsNullOrEmpty(keep) ? appliedProfile : keep;
            int idx = 0;
            if (!string.IsNullOrEmpty(want))
                for (int i = 0; i < ps.Count; i++)
                    if (string.Equals(ps[i], want, StringComparison.OrdinalIgnoreCase)) { idx = i + 1; break; }
            cboProfile.SelectedIndex = idx;
            // 下拉现在只代表“选中了哪套”，不再在选择瞬间灌进当前勾选。
            // 启动仍然保留上次工作选择，必须点「套用」才覆盖。
            LoadSchemeKeys();
            RefreshMods();
        }

        /// <summary>
        /// 自测：按真实窗口走一遍外观切换（可见、抓屏幕像素），并把原生控件的实际底色写进 paint.txt。
        /// 原来的主题自检走 DrawToBitmap —— 那是强制全量重绘，会掩盖「切完主题颜色被映射错」这类问题，
        /// 只有抓屏幕才看得见用户看到的那一屏。
        /// </summary>
        /// <summary>
        /// 自测用：把整套分类图标按 32px / 14px 两档排成一张样张，方便一眼核对形状。
        /// 14px 那一行就是树里实际用的尺寸 —— 只在 32px 下好看的图标，缩到 14 常常就糊了。
        /// </summary>
        internal int UiTestGlyphs()
        {
            string[] keys = new string[]
            {
                "gun", "rifle", "smg", "shotgun", "sniper", "pistol", "launcher",
                "melee", "bottle", "ammo", "medkit", "syringe", "pills", "person",
                "survivor1", "survivor2", "claw", "map", "spray", "branch", "unknown", "misc"
            };
            const int cols = 7, cellW = 108, cellH = 128;
            int rows = (keys.Length + cols - 1) / cols;
            int W = cols * cellW + 24, H = rows * cellH + 16;
            using (Bitmap b = new Bitmap(W, H))
            {
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.Clear(Theme.Surface);
                    for (int i = 0; i < keys.Length; i++)
                    {
                        int cx = 12 + (i % cols) * cellW;
                        int cy = 8 + (i / cols) * cellH;
                        Glyph.Draw(g, keys[i], new Rectangle(cx + (cellW - 32) / 2, cy + 6, 32, 32), Theme.Ink);
                        Glyph.Draw(g, keys[i], new Rectangle(cx + (cellW - 14) / 2, cy + 46, 14, 14), Theme.Ink);
                        using (SolidBrush tb = new SolidBrush(Theme.Ink3))
                        {
                            StringFormat f = new StringFormat();
                            f.Alignment = StringAlignment.Center;
                            g.DrawString(keys[i], Theme.UISmall, tb,
                                new RectangleF(cx, cy + 68, cellW, 20), f);
                            f.Dispose();
                        }
                    }
                }
                b.Save(Path.Combine(Core.LogDir, "glyphs.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            return 0;
        }

        internal int UiPaintTest()
        {
            int bad = 0;
            ThemeMode[] seq = new ThemeMode[] { ThemeMode.Light, ThemeMode.Dark, ThemeMode.System, ThemeMode.Dark, ThemeMode.Light };
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            StartPosition = FormStartPosition.Manual;
            Location = new Point(20, 20);
            BringToFront();
            Activate();
            for (int w = 0; w < 6; w++) { Application.DoEvents(); Thread.Sleep(60); }
            for (int i = 0; i < seq.Length; i++)
            {
                try
                {
                    Theme.SetMode(seq[i]);
                    Theme.ApplyToOpenForms();
                    for (int w = 0; w < 8; w++) { Application.DoEvents(); Thread.Sleep(60); }
                    sb.AppendLine("== " + i + "  " + Theme.ModeLabel + "  dark=" + Theme.IsDark);
                    DumpNativeColors(sb, this, "   ");
                    Shot("paint" + i);
                }
                catch (Exception ex) { bad++; sb.AppendLine("FAIL " + i + ": " + ex.Message); }
            }
            // 第二阶段：走用户的真实路径 —— 打开设置窗口，在里面点「深色」，然后直接关掉（不点保存）。
            // 外观是即时开关，点了就落盘、关掉窗口不该退回去（用户报过：换成深色，退出设置又变回浅色）。
            string cfgBefore = Core.Cfg.Appearance;
            try
            {
                using (SettingsForm sf = new SettingsForm(this))
                {
                    sf.StartPosition = FormStartPosition.Manual;
                    sf.Location = new Point(Location.X + 30, Location.Y + 30);
                    System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
                    t.Interval = 400;
                    int step = 0;
                    t.Tick += delegate
                    {
                        step++;
                        if (step == 1) sf.PreviewAppearance(ThemeMode.Dark);
                        else if (step == 2) { t.Stop(); sf.DialogResult = DialogResult.Cancel; }
                    };
                    t.Start();
                    sf.ShowDialog(this);
                    t.Dispose();
                }
                for (int w = 0; w < 12; w++) { Application.DoEvents(); Thread.Sleep(60); }
                sb.AppendLine("== modal 设置里点深色后直接关掉  " + Theme.ModeLabel + "  dark=" + Theme.IsDark
                    + "  cfg=" + Core.Cfg.Appearance);
                DumpNativeColors(sb, this, "   ");
                Shot("paint_modal");
                if (!Theme.IsDark) { bad++; sb.AppendLine("FAIL modal: 关掉设置后外观退回去了"); }
                if (!string.Equals(Core.Cfg.Appearance, "dark", StringComparison.OrdinalIgnoreCase))
                { bad++; sb.AppendLine("FAIL modal: 外观没写进配置，重启会还原"); }
            }
            catch (Exception ex) { bad++; sb.AppendLine("FAIL modal: " + ex.Message); }

            File.WriteAllText(Path.Combine(Core.LogDir, "paint.txt"), sb.ToString(), new System.Text.UTF8Encoding(false));
            // 自检会改真实配置，测完还回去，别把用户选的外观改掉
            Core.Cfg.Appearance = cfgBefore;
            Core.SaveConfig();
            Theme.SetMode(Theme.Parse(cfgBefore));
            Theme.ApplyToOpenForms();
            return bad;
        }

        private void DumpNativeColors(System.Text.StringBuilder sb, Control c, string pad)
        {
            if (c is TextBox || c is ListView || c is TreeView)
                sb.AppendLine(pad + c.GetType().Name + " [" + (c.AccessibleName ?? "") + "] back="
                    + c.BackColor.R + "," + c.BackColor.G + "," + c.BackColor.B
                    + " fore=" + c.ForeColor.R + "," + c.ForeColor.G + "," + c.ForeColor.B);
            for (int i = 0; i < c.Controls.Count; i++) DumpNativeColors(sb, c.Controls[i], pad);
        }

        private void Shot(string tag)
        {
            Rectangle r = Bounds;
            using (Bitmap b = new Bitmap(r.Width, r.Height))
            {
                using (Graphics g = Graphics.FromImage(b)) g.CopyFromScreen(r.Left, r.Top, 0, 0, r.Size);
                b.Save(Path.Combine(Core.LogDir, tag + ".png"), System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        /// <summary>
        /// 自测用：一键复制走一遍真实按钮路径 —— 有内容时原样进剪贴板；
        /// 空内容只提示「空」，不能把用户原来的剪贴板覆盖掉。返回失败条数。
        /// </summary>
        internal int UiTestCopy()
        {
            int bad = 0;
            string keep = txtLaunch.Text;
            string sample = "-novid -windowed";

            // 自测不该留在别人的剪贴板里：先备份，结束时原样放回去
            string backup = "";
            bool hadBackup = false;
            try { backup = Clipboard.GetText(); hadBackup = true; }
            catch { }

            txtLaunch.Text = sample;
            Application.DoEvents();
            CopyLaunch();
            Application.DoEvents();
            string got = "";
            try { got = Clipboard.GetText(); }
            catch { got = "(读不到剪贴板)"; }
            bool ok1 = string.Equals(got, sample, StringComparison.Ordinal);
            Core.Log(ok1 ? "OK" : "FAIL", "COPYTEST 有内容：" + (ok1 ? "剪贴板与输入框一致" : "剪贴板=" + got));
            if (!ok1) bad++;

            txtLaunch.Text = "";
            Application.DoEvents();
            CopyLaunch();
            Application.DoEvents();
            string got2 = "";
            try { got2 = Clipboard.GetText(); } catch { }
            bool ok2 = string.Equals(got2, sample, StringComparison.Ordinal);
            Core.Log(ok2 ? "OK" : "FAIL", "COPYTEST 空内容：" + (ok2 ? "没覆盖剪贴板" : "剪贴板被改成其他内容"));
            if (!ok2) bad++;

            txtLaunch.Text = keep;
            Application.DoEvents();
            UpdateLaunchTip();

            try
            {
                if (hadBackup && backup.Length > 0) Clipboard.SetText(backup);
                else Clipboard.Clear();
            }
            catch { }

            Core.Log("INFO", "COPYTEST 结束：失败 " + bad + " 条（剪贴板已还原）");
            return bad;
        }

        /// <summary>自测用：打开「启动项」放大编辑并截图，同时核对默认值与清洗结果。</summary>
        internal void UiTestLaunch()
        {
            Core.Log("INFO", "LAUNCHTEST 输入框里的值 = " + LaunchArgs.Clean(txtLaunch.Text)
                + " | 默认 = " + LaunchArgs.Default
                + " | 含 0/1 占位 = " + LaunchArgs.HasSlot(txtLaunch.Text)
                + " | 参数个数 = " + LaunchArgs.Clean(txtLaunch.Text).Split(' ').Length);
            using (LaunchForm lf = new LaunchForm(txtLaunch.Text))
            {
                lf.StartPosition = FormStartPosition.Manual;
                lf.Location = new System.Drawing.Point(0, 0);
                lf.Show();
                Application.DoEvents();
                System.Threading.Thread.Sleep(250);
                Application.DoEvents();
                using (System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(lf.Width, lf.Height))
                {
                    lf.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, lf.Width, lf.Height));
                    bmp.Save(Path.Combine(Core.LogDir, "launch.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                lf.Close();
            }
        }

        /// <summary>自测用：打开「重复嫌疑」并核对内容，截图 + 把结论写日志。</summary>
        internal void UiTestDup()
        {
            List<DupGroup> gs = ConflictCheck.DupGroups(Core.ModList);
            List<string> others = new List<string>();
            foreach (string m in conflictMsgs)
                if (!m.StartsWith("体积完全相同", StringComparison.Ordinal)) others.Add(m);
            int files = 0;
            foreach (DupGroup g in gs) files += g.Items.Count;
            Core.Log("INFO", "DUPTEST " + gs.Count + " 组 / " + files + " 个文件，其它提示 " + others.Count + " 条");
            if (gs.Count == 0) { Core.Log("WARN", "DUPTEST 没有体积重复，跳过"); return; }
            using (DupForm f = new DupForm(gs, others))
            {
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new System.Drawing.Point(0, 0);
                f.Show();
                Application.DoEvents();
                System.Threading.Thread.Sleep(200);
                Application.DoEvents();
                f.VerifyAll();                       // 真算一遍内容指纹，验证 rar 的 CRC 和独立 vpk 的 SHA 都走得通
                Application.DoEvents();
                using (System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, f.Width, f.Height));
                    bmp.Save(Path.Combine(Core.LogDir, "dup.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                foreach (DupGroup g in gs)
                    Core.Log("INFO", "DUPTEST " + Core.Human(g.Size) + " x" + g.Items.Count + " → " + g.Verdict
                        + (g.VerdictNote.Length > 0 ? "（" + g.VerdictNote + "）" : ""));
                f.Close();
            }
        }

        /// <summary>自测用：打开「参数配置」对话框并截图。</summary>
        internal void UiTestTweaks()
        {
            using (TweakForm tf = new TweakForm(txtGame.Text.Trim(), TweakName()))
            {
                tf.StartPosition = FormStartPosition.Manual;
                tf.Location = new System.Drawing.Point(0, 0);
                tf.Show();
                Application.DoEvents();
                System.Threading.Thread.Sleep(300);
                Application.DoEvents();
                using (System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(tf.Width, tf.Height))
                {
                    tf.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, tf.Width, tf.Height));
                    bmp.Save(Path.Combine(Core.LogDir, "tweaks.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                string root = txtGame.Text.Trim();
                Core.Log("INFO", "TWEAKTEST 存档 " + Tweaks.List().Count + " 套，当前选 " + TweakName()
                    + "，存档根 " + Tweaks.Root);
                tf.Close();
            }
        }

        /// <summary>自测用：按名字找一个套装，打开「选择部件」并截图，验证对话框排版。</summary>
        internal void UiTestParts(string name)
        {
            foreach (ModGroup g in Catalog.Groups)
            {
                if (!g.IsSet) continue;
                if (g.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                using (PartsForm pf = new PartsForm(g, checkedKeys))
                {
                    pf.StartPosition = FormStartPosition.Manual;
                    pf.Location = new System.Drawing.Point(0, 0);
                    pf.Show();
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(250);
                    Application.DoEvents();
                    using (System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(pf.Width, pf.Height))
                    {
                        pf.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, pf.Width, pf.Height));
                        bmp.Save(Path.Combine(Core.LogDir, "parts.png"), System.Drawing.Imaging.ImageFormat.Png);
                    }
                    Core.Log("INFO", "PARTSTEST " + g.Name + " 共 " + g.Parts.Count + " 件，默认勾 " + Catalog.DefaultParts(g).Count + " 件");
                    pf.Close();
                }
                return;
            }
            Core.Log("WARN", "PARTSTEST 没找到套装: " + name);
        }

        /// <summary>自测用：把搜索框设成某个词，用来验证筛选与空态。</summary>
        internal void UiTestFilter(string kw)
        {
            txtSearch.Text = kw;
            RefreshMods();
        }

        /// <summary>自测用：把「方案管理」窗口画成图片，核对分组和列宽。</summary>
        internal void UiTestProfileForm()
        {
            using (ProfileManageForm f = new ProfileManageForm(this))
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
                    bmp.Save(System.IO.Path.Combine(Core.LogDir, "uitest_profile.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                Core.Log("INFO", "PROFILE 方案数=" + ProfileStore.ListProfiles().Count + " 列表行=" + f.DebugRows());
            }
        }

        /// <summary>自测用：L4N 与 MQCam 的勾选联动（只有相机依赖平台，其他都不依赖）。</summary>
        internal void UiTestDeps()
        {
            chkMqcam.Checked = false; chkL4N.Checked = false;
            Core.Log("INFO", "DEPS 起点：L4N=" + chkL4N.Checked + " MQCam=" + chkMqcam.Checked);
            chkMqcam.Checked = true;
            Core.Log("INFO", "DEPS 勾 MQCam 后：L4N=" + chkL4N.Checked + "（应为 True）MQCam=" + chkMqcam.Checked);
            chkL4N.Checked = false;
            Core.Log("INFO", "DEPS 取消 L4N 后：MQCam=" + chkMqcam.Checked + "（应为 False）");
            chkL4N.Checked = false; chkMqcam.Checked = false;
            chkSkeeto.Checked = true; chkDxvk.Checked = true;
            Core.Log("INFO", "DEPS 只装 Skeeto/dxvk、不装 L4N：L4N=" + chkL4N.Checked + " MQCam=" + chkMqcam.Checked + "（应为 False/False，L4N 不该被逼着勾上）");
        }

        /// <summary>自测用：全选当前可见项并跑深度冲突检查，结果写日志。</summary>
        internal void UiTestDeep()
        {
            // 全库全选（默认部件），再跑深度冲突检查
            foreach (ModGroup g in Catalog.Groups)
                foreach (ModItem p in Catalog.DefaultParts(g)) checkedKeys.Add(p.Key);
            SyncPicks();
            List<ModItem> sel = Selected();
            Core.Log("INFO", "DEEP 选中 " + sel.Count + " 个，开始深度检查");
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            List<string> msgs = ConflictCheck.DeepAll(sel);
            foreach (string m in msgs) Core.Log("INFO", "DEEP " + m);
            Core.Log("INFO", "DEEP 用时 " + sw.ElapsedMilliseconds + " ms，共 " + msgs.Count + " 条");
        }

        internal string SearchText { get { return txtSearch == null ? "" : txtSearch.Text.Trim(); } }

        internal void ClearSearchIfAny()
        {
            if (txtSearch != null && txtSearch.Text.Length > 0) txtSearch.Text = "";   // 会触发一次重新筛选
        }

        /// <summary>行首的展开标记：套装才给三角，单 vpk 的行前面留空。</summary>
        internal string GroupRowText(ModGroup g, int sel)
        {
            if (!g.IsSet) return g.Name;
            return (expanded.Contains(g.Key) ? "▼ " : "▶ ") + g.Name + "（" + (sel > 0 ? sel : 0) + "/" + g.Parts.Count + "）";
        }

        internal string GroupRowText(ModGroup g) { return GroupRowText(g, CountChecked(g)); }

        internal bool IsExpanded(ModGroup g) { return g != null && expanded.Contains(g.Key); }

        /// <summary>双击套装行：就地展开/收起子件，不弹窗、不重建整张表。</summary>
        internal void ToggleExpand(ModGroup g)
        {
            if (g == null || !g.IsSet) return;
            if (!expanded.Remove(g.Key)) expanded.Add(g.Key);
        }

        /// <summary>把一条套装渲染成一行：类窗口和方案窗口共用同一套配色规则。</summary>
        internal ListViewItem MakeRow(ModGroup g)
        {
            int sel = CountChecked(g);
            ListViewItem li = new ListViewItem(GroupRowText(g, sel));
            Color mk;
            li.SubItems.Add(SchemeMark(g, out mk));           // 方案：方案内 / 已取消 / 新增
            li.SubItems[1].ForeColor = mk;
            li.SubItems[1].Font = Theme.UISmall;
            li.SubItems.Add(g.Subject.Length > 0 ? g.Subject : "—");
            if (g.Subject.Length > 0 && g.SubjectLevel == "材质") li.SubItems[2].ForeColor = Theme.Ink3;
            li.SubItems.Add(Core.Human(g.Size));
            li.SubItems.Add(WhereText(g));
            li.SubItems[4].ForeColor = CatInk(g.Cat);
            Color stInk, stBg;
            li.SubItems.Add(StatusOf(g, out stInk, out stBg));      // 状态：已装·开 / 已装·关 / 未装 …
            li.SubItems[5].ForeColor = stInk;
            li.SubItems[5].Font = Theme.UISmall;
            if (stBg != Theme.Surface) li.BackColor = stBg;
            li.Tag = g;
            li.Checked = sel > 0;
            if (g.IsSet) li.ToolTipText = PartsTip(g);
            foreach (ModItem p in g.Parts)
                if (p.Installed)
                {
                    li.ToolTipText = (li.ToolTipText.Length > 0 ? li.ToolTipText + "\r\n" : "")
                        + "游戏 addons 里已经有同名同大小的这一份，安装时会直接跳过复制。\r\n"
                        + "addonlist.txt 里现在是「" + (p.AddonOn ? "1 · 开" : "0 · 关") + "」"
                        + (p.AddonOn ? "，进游戏就能看到。" : "，文件在但没启用 —— 想开就在「已装 MOD 开关」里打开，或勾上它再装一次。");
                    break;
                }
            return li;
        }

        /// <summary>展开后挂在套装下面的一个 vpk：勾这行只装这一个文件。</summary>
        internal ListViewItem MakePartRow(ModGroup g, ModItem p)
        {
            ListViewItem li = new ListViewItem("      └ " + p.Name);
            li.SubItems.Add("");
            li.SubItems.Add(p.Subject.Length > 0 ? p.Subject : "—");
            li.SubItems.Add(Core.Human(p.Size));
            li.SubItems.Add(p.Role + " · 单件");
            li.SubItems[4].ForeColor = Theme.Ink3;
            Color pInk, pBg;
            li.SubItems.Add(PartStatus(p, out pInk, out pBg));
            li.SubItems[5].ForeColor = pInk;
            li.SubItems[5].Font = Theme.UISmall;
            if (pBg != Theme.Surface) li.BackColor = pBg;
            li.Tag = p;
            li.ForeColor = Theme.Ink3;
            li.Checked = checkedKeys.Contains(p.Key);
            li.ToolTipText = "这一个 vpk：" + p.Name + "\r\n勾上 = 只装它（" + p.Role + "）"
                           + "\r\n现在：" + PartStatus(p, out pInk, out pBg);
            return li;
        }

        /// <summary>任何窗口里勾了一行，都走这里：先改 checkedKeys，再把所有窗口同步一遍。</summary>
        internal void OnRowChecked(ListView lv, ItemCheckedEventArgs e)
        {
            // 展开出来的单件行：勾哪个就装哪个，不动同套装的其他 vpk
            ModItem part = e.Item.Tag as ModItem;
            if (part != null)
            {
                // 刷新列表时 ListView 会把勾选状态再补发一遍；状态本来就一致就放过，否则会死循环
                if (e.Item.Checked == checkedKeys.Contains(part.Key)) return;
                if (e.Item.Checked) checkedKeys.Add(part.Key);
                else checkedKeys.Remove(part.Key);

                if (bulk) return;
                // 组行的「已选 2/5」和子行勾状态统一由 SyncPicks → 树的 Sync 更新
                SyncPicks();
                UpdateSchemeLabel();
                UpdateSel();
                return;
            }

            ModGroup g = e.Item.Tag as ModGroup;
            if (g == null) return;
            // 刷新列表时 ListView 会把每一行的勾选状态再补发一遍消息；那时 checkedKeys 本来就是对的，
            // 认了就变成「同步一次 → 又补发 → 再同步」的死循环，所以状态一致就直接放过
            if (e.Item.Checked == (CountChecked(g) > 0)) return;
            if (e.Item.Checked)
            {
                // 勾这一行 = 装默认部件（主体 + 必装 + 第一个选配）；想挑就双击展开
                foreach (ModItem p in Catalog.DefaultParts(g)) checkedKeys.Add(p.Key);
            }
            else
            {
                foreach (ModItem p in g.Parts) checkedKeys.Remove(p.Key);
            }
            if (g.IsSet) e.Item.Text = GroupRowText(g);
            Color c;
            e.Item.SubItems[1].Text = SchemeMark(g, out c);
            e.Item.SubItems[1].ForeColor = c;
            if (bulk) return;
            SyncPicks();
            UpdateSchemeLabel();
            UpdateSel();
        }

        /// 方案树右边那张候选表用的菜单：只留对「一个位置挑一个 MOD」有用的事。
        /// 导入、清重复、展开这些是分类页的活，不往这儿塞。
        /// </summary>
        internal ContextMenuStrip BuildTreeMenu(ListView lv)
        {
            ContextMenuStrip cms = new ContextMenuStrip();
            cms.Font = Theme.UI;

            // 顶层只留当前这行真用得上的动作；低频和危险操作收进「更多操作」。
            ToolStripMenuItem pick = new ToolStripMenuItem("用这个替换", null, delegate { PickInTree(lv); });
            ToolStripMenuItem parts = new ToolStripMenuItem("选择部件…", null, delegate { PickPartsIn(lv); });
            ToolStripMenuItem toggle = new ToolStripMenuItem("开 / 关这个 MOD", null, delegate { ToggleSwitchIn(lv); });
            cms.Items.Add(pick);
            cms.Items.Add(parts);
            cms.Items.Add(toggle);

            ToolStripMenuItem branch = new ToolStripMenuItem("我的分支");
            branch.DropDownItems.Add("放进我的分支…", null, delegate { PutIntoBranchIn(lv); });
            branch.DropDownItems.Add("新建分支…", null, delegate
            {
                if (treeView != null) treeView.NewBranch();
            });
            ToolStripMenuItem subNew = new ToolStripMenuItem("在当前分支下新建子分支…", null, delegate { NewSubBranchIn(); });
            branch.DropDownItems.Add(subNew);
            ToolStripMenuItem take = new ToolStripMenuItem("从当前分支移出", null, delegate { TakeOutOfBranchIn(lv); });
            branch.DropDownItems.Add(take);
            cms.Items.Add(branch);

            ToolStripMenuItem setCat = new ToolStripMenuItem("设置 MOD 分类…", null, delegate { SetModCatIn(lv); });
            ToolStripMenuItem reveal = new ToolStripMenuItem("在资源管理器里定位", null, delegate { RevealIn(lv); });
            cms.Items.Add(setCat);
            cms.Items.Add(reveal);

            ToolStripMenuItem more = new ToolStripMenuItem("更多操作");
            more.DropDownItems.Add("只装这个 MOD", null, delegate { InstallThisOne(lv); });
            more.DropDownItems.Add("重命名 VPK 文件", null, delegate { RenameSelectedIn(lv); });
            more.DropDownItems.Add("删除这个 MOD", null, delegate { DeleteInTree(lv); });
            cms.Items.Add(more);

            // 空白处只保留建分支，不再弹出一排点了也没用的 MOD 操作。
            ToolStripMenuItem newOnly = new ToolStripMenuItem("新建分支…", null, delegate
            {
                if (treeView != null) treeView.NewBranch();
            });
            cms.Items.Add(newOnly);

            cms.Opening += delegate
            {
                ListViewItem li = RowTarget(lv);
                ModGroup g = li == null ? null : li.Tag as ModGroup;
                ModItem one = li == null ? null : li.Tag as ModItem;
                bool group = g != null, row = group || one != null;
                pick.Visible = group;
                parts.Visible = group && g.IsSet;
                toggle.Visible = group;
                branch.Visible = group;
                subNew.Visible = group && treeView != null && treeView.CurrentBranch != null;
                take.Visible = group && treeView != null && treeView.InOpenBranch(g);
                setCat.Visible = group;
                reveal.Visible = row;
                more.Visible = row;
                newOnly.Visible = !row;
            };
            return cms;
        }

        /// <summary>右键「在当前分支下新建子分支…」：正在看哪个分支，就建在它下面。</summary>
        internal void NewSubBranchIn()
        {
            if (treeView == null) return;
            Slot cur = treeView.CurrentBranch;
            if (cur == null) { MessageBox.Show("先在左边方案树里点开一个分支，这一项就会亮起来。"); return; }
            treeView.NewBranch(cur);
        }

        /// <summary>右键「放进我的分支」：把这个 MOD 归到用户自己拉的分组里（不动文件）。</summary>
        internal void PutIntoBranchIn(ListView lv)
        {
            ListViewItem li = RowTarget(lv);
            ModGroup g = li == null ? null : li.Tag as ModGroup;
            if (g == null) { MessageBox.Show("先右键点一行 MOD。"); return; }
            if (treeView == null) return;
            treeView.PutIntoBranch(g);
        }

        /// <summary>右键「放进新分支」：建一个新分支，顺手把这个 MOD 放进去（不用先有分支）。</summary>
        internal void PutIntoNewBranchIn(ListView lv)
        {
            ListViewItem li = RowTarget(lv);
            ModGroup g = li == null ? null : li.Tag as ModGroup;
            if (g == null) { MessageBox.Show("先右键点一行 MOD。"); return; }
            if (treeView == null) return;
            treeView.PutIntoNewBranch(g);
        }

        /// <summary>右键「从这个分支移出」：只把这一条从分组里摘掉，文件和勾选都不动。</summary>
        internal void TakeOutOfBranchIn(ListView lv)
        {
            ListViewItem li = RowTarget(lv);
            ModGroup g = li == null ? null : li.Tag as ModGroup;
            if (g == null) { MessageBox.Show("先右键点一行 MOD。"); return; }
            if (treeView == null) return;
            treeView.TakeOutOfBranch(g);
        }

        /// <summary>
        /// 右键菜单的「开 / 关这个 MOD」：和点列表最右边「开关 ⇄」列是同一个动作
        /// （攒进 pending，最后统一按「应用改动」写 addonlist；文件一个都不动）。
        /// 开关列那一小格不好点中，右键这条是给鼠标手抖的人留的第二条路。
        /// </summary>
        internal void ToggleSwitchIn(ListView lv)
        {
            ListViewItem li = RowTarget(lv);
            if (li == null) { MessageBox.Show("先右键点一行 MOD。"); return; }
            ModGroup g = li.Tag as ModGroup;
            if (g == null) { MessageBox.Show("这一行不是 MOD（标题行或「留原版」没有开关）。"); return; }
            if (treeView == null) return;
            treeView.ToggleFromMenu(g);
        }

        /// <summary>
        /// 方案树候选表里删一个 MOD：文件直接删掉，删完重扫，树留在原来那个位置。
        /// 套装行删整套；展开出来的单件行只删那一个文件。
        /// </summary>
        internal void DeleteInTree(ListView lv)
        {
            ListViewItem li = RowTarget(lv);
            if (li == null) { MessageBox.Show("先右键点一行。"); return; }
            ModGroup g = li.Tag as ModGroup;
            ModItem one = li.Tag as ModItem;
            string name;
            List<ModItem> parts = new List<ModItem>();
            if (g != null) { name = g.Name; parts.AddRange(g.Parts); }
            else if (one != null) { name = one.Name; parts.Add(one); }
            else { MessageBox.Show("这一行没有对应的 vpk。"); return; }

            foreach (ModItem it in parts)
            {
                if (it.IsArchive) { MessageBox.Show("「" + it.Name + "」在压缩包里（" + it.Container + "），\r\n本工具不会去改压缩包。想删请自己在资源管理器里处理。", "不能删除"); return; }
                if (it.FromGame && !Core.IsLocal) { MessageBox.Show("「" + it.Name + "」在游戏目录 addons 里。\r\nU 盘模式不动游戏目录的文件；要删它，把右上角切到「本地模式」再来。", "不能删除"); return; }
            }
            string tail = parts.Count > 1 ? "（整套 " + parts.Count + " 个 vpk）" : "";
            bool fromGame = parts.Exists(delegate(ModItem m) { return m.FromGame; });
            if (MessageBox.Show("删除「" + name + "」" + tail + "？\r\n\r\n· 文件直接从磁盘删掉，删了就是没了"
                + (fromGame ? "\r\n· 这是游戏 addons 里的文件，删掉 = 从游戏里卸掉" : ""),
                "确认删除", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

            int n = 0;
            foreach (ModItem it in parts)
            {
                try { if (Work.DeleteMod(it) == null) { n++; checkedKeys.Remove(it.Key); } }
                catch (Exception ex) { Core.Log("WARN", "删除 " + it.Name + " 失败: " + ex.Message); }
            }
            Core.Log("OK", "方案树里删掉 " + n + " 个 vpk：" + name);
            Core.ScanMods();
            RefreshMods();
            RefreshConflicts();
            if (treeView != null) { RebuildViews(); treeView.Sync(); }
        }

        /// <summary>方案树里的「用这个替换」：交给树自己去换，位置和勾选都按树的规则走。</summary>
        private void PickInTree(ListView lv)        {
            ListViewItem li = RowTarget(lv);
            if (li == null) { MessageBox.Show("先右键点一行。"); return; }
            if (treeView == null) return;
            treeView.PickRow(li);
        }

        /// <summary>设置这个 MOD 算哪一类、替换什么。分类只有一份：方案树跟着变。</summary>
        internal void SetModCatIn(ListView lv)
        {
            ModGroup g = FirstSelectedGroup(lv);
            if (g == null) { MessageBox.Show("先选中一个 MOD。"); return; }
            using (ModCatForm f = new ModCatForm(g))
            {
                if (f.ShowDialog(this) == DialogResult.OK)
                    Core.Log("OK", "分类已保存: " + g.Name + " → " + f.ResultCat
                        + (f.ResultSlot.Length > 0 ? " / " + f.ResultSlot : ""));
            }
            // 窗口里还可能新建/改名/排序过分类，取消也要同步一次
            Catalog.ApplyCats();          // 人工分类落到 ModGroup.Cat 上：树、搜索全跟着走
            if (treeView != null) { RebuildViews(); treeView.Sync(); }
            RefreshMods();
        }

        /// <summary>右键点中的那一行。右键时已经把它选中了，所以取第一行选中项。</summary>
        private ListViewItem RowTarget(ListView lv)
        {
            return lv.SelectedItems.Count > 0 ? lv.SelectedItems[0] : null;
        }

        /// <summary>
        /// 这一行到底是哪几个 vpk。
        /// 展开出来的子件行 = 那一个文件；套装行 = 这套里当前勾着的件，一件都没勾就按默认部件（主体+必装+第一个选配）。
        /// </summary>
        private List<ModItem> RowItems(ListView lv)
        {
            List<ModItem> list = new List<ModItem>();
            ListViewItem li = RowTarget(lv);
            if (li == null) return list;
            ModItem single = li.Tag as ModItem;
            if (single != null) { list.Add(single); return list; }
            ModGroup g = li.Tag as ModGroup;
            if (g == null) return list;
            foreach (ModItem p in g.Parts) if (checkedKeys.Contains(p.Key)) list.Add(p);
            if (list.Count == 0) list.AddRange(Catalog.DefaultParts(g));
            return list;
        }

        /// <summary>只装右键点的那一行，别的什么都不动。</summary>
        internal void InstallThisOne(ListView lv)
        {
            ListViewItem li = RowTarget(lv);
            if (li == null) { MessageBox.Show("先在列表里右键点一行。"); return; }
            List<ModItem> sel = RowItems(lv);
            if (sel.Count == 0) { MessageBox.Show("这一行没有对应的 vpk。"); return; }
            string what = sel.Count == 1
                ? sel[0].Name
                : "「" + ((li.Tag as ModGroup) != null ? ((ModGroup)li.Tag).Name : li.Text) + "」的 " + sel.Count + " 个 vpk";
            InstallMods(sel, "只装这个 MOD", what);
        }

        internal void ManageProfiles()
        {
            using (ProfileManageForm f = new ProfileManageForm(this)) f.ShowDialog(this);
            FillProfiles(null);
            RefreshMods();
        }

        /// <summary>搜索或方案变了：树和标题跟着重排。</summary>
        private void RefreshMods()
        {
            UpdateSchemeLabel();
            // 方案树是常驻第一屏：万一扫库还没完就点进来了，树会是空的，扫完这里补一次；
            // 树有内容时只刷新状态点，不重建（搜索打字不至于反复重排）
            if (treeView != null)
            {
                if (treeView.SlotCount == 0) RebuildViews();
                else if (treeView.Visible) treeView.SyncIfSame();   // 正在看树：重扫后状态列、圆点要立刻对上（换了新对象就整棵重建）
                treeView.RefreshPane();                             // 搜索词变了，右边（结果 / 候选）跟着换
            }
            UpdateSel();
            UpdateModCaption();
        }

        /// <summary>手动重扫：库或游戏目录在别处被改过（别人手动丢过 vpk、装了一半），点一下重读。</summary>
        internal void RescanMods()
        {
            try
            {
                SetBusy(true);
                SetStatus("正在重扫…");
                Core.ScanMods();
                RefreshMods();
                Core.Log("OK", "已重扫：共 " + Core.ModList.Count + " 个 vpk");
            }
            catch (Exception ex) { Core.Log("FAIL", "重扫失败: " + ex.Message); }
            finally { SetBusy(false); SetStatus("就绪"); }
        }

        // 一行说清这套方案在各类里放了几个
        private void UpdateSchemeLabel()
        {
            if (lblScheme == null) return;
            if (schemeName.Length == 0)
            {
                lblScheme.ForeColor = Theme.Ink3;
                lblScheme.Text = "没选方案：挑一套，列表里就会标出它在这一类里装了什么";
                return;
            }
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("「").Append(schemeName).Append("」");
            int mods = 0;
            foreach (string c in Catalog.Cats)
            {
                int n = 0;
                foreach (ModGroup g in Catalog.Groups) if (g.Cat == c && InScheme(g)) n++;
                if (n == 0) continue;
                mods += n;
                sb.Append("  ").Append(c).Append(" ").Append(n);
            }
            sb.Append("　共 ").Append(mods).Append(" 个 / ").Append(schemeKeys.Count).Append(" 文件");
            int add = 0, del = 0;
            foreach (string k in checkedKeys) if (!schemeKeys.Contains(k)) add++;
            foreach (string k in schemeKeys) if (!checkedKeys.Contains(k)) del++;
            if (add > 0 || del > 0) sb.Append("　·　勾选差 多 ").Append(add).Append(" / 少 ").Append(del);
            lblScheme.ForeColor = add == 0 && del == 0 ? Theme.Ink2 : Theme.Warn;
            lblScheme.Text = sb.ToString();
        }

        // 大类各一种颜色。只用来区分，不做装饰：同色系深浅差别够看就行。
        internal static Color CatInk(string cat)
        {
            switch (cat)
            {
                case Catalog.CAT_MAP: return Color.FromArgb(24, 102, 133);
                case Catalog.CAT_CHAR: return Color.FromArgb(107, 71, 148);
                case Catalog.CAT_GUN: return Color.FromArgb(150, 88, 24);
                case Catalog.CAT_MED: return Color.FromArgb(38, 116, 55);
                case Catalog.CAT_SPRAY: return Color.FromArgb(160, 54, 116);
                default: return Theme.Ink2;
            }
        }

        /// <summary>这条在不在当前方案里（套装里任意一个 vpk 在就算在）。</summary>
        internal bool InScheme(ModGroup g)
        {
            if (schemeKeys.Count == 0) return false;
            foreach (ModItem p in g.Parts) if (schemeKeys.Contains(p.Key)) return true;
            return false;
        }

        // 方案列：一句话说清「这条跟当前方案是什么关系」
        private string SchemeMark(ModGroup g, out Color color)
        {
            bool inn = InScheme(g);
            bool on = CountChecked(g) > 0;
            if (inn && on) { color = Theme.Ok; return "方案内"; }    // 方案里有，这次也勾着
            if (inn && !on) { color = Theme.Warn; return "已取消"; }  // 方案里有，这次被取消了
            color = Theme.Accent;
            return on ? "新增" : "";                                  // 方案里没有，这次新勾的
        }

        // 把当前方案读进 schemeKeys（下拉换了 / 方案存过之后都要刷一次）
        private void LoadSchemeKeys()
        {
            schemeKeys.Clear();
            schemeName = "";
            if (cboProfile == null || cboProfile.SelectedItem == null) return;
            string n = cboProfile.SelectedItem.ToString();
            if (n.StartsWith("（")) return;
            Profile p = ProfileStore.Load(n);
            if (p == null) return;
            schemeName = p.Name;
            foreach (string k in p.Mods) schemeKeys.Add(k);
        }

        /// <summary>总览要的：当前下拉里那套方案的名字（空 = 没套方案，看的就是当前勾选）。</summary>
        internal string SchemeName { get { return schemeName; } }

        /// <summary>总览要的：当前勾选和方案差多少（多几个 / 少几个）。</summary>
        internal void SchemeDiff(out int add, out int del)
        {
            add = 0;
            del = 0;
            foreach (string k in checkedKeys) if (!schemeKeys.Contains(k)) add++;
            foreach (string k in schemeKeys) if (!checkedKeys.Contains(k)) del++;
        }

        // 分类列只放得下 130 px：人物的「二代 · 西装Nick」取最后一段，省「人物 · 二代」那截
        // 装没装已经单独有一列（状态），这里不再拼「· 已装」，免得一格里塞两件事
        private static string WhereText(ModGroup g)
        {
            return SubText(g);
        }

        // ---------- 状态：两维 ----------
        // 第一维「文件在不在 addons 里」（Installed / FromGame），第二维「addonlist 里是 1 还是 0」（AddonOn）。
        // 只看第一维会把「装了但被关掉」和「装好了正开着」说成一样，机房里这两种要区别对待。

        /// <summary>这条现在在游戏里是什么状态：一共几个、装了几个、开着几个、几个本来就是游戏里的。</summary>
        internal static void CountState(ModGroup g, out int total, out int inst, out int on, out int game)
        {
            total = g.Parts.Count; inst = 0; on = 0; game = 0;
            foreach (ModItem p in g.Parts)
            {
                if (p.FromGame) game++;
                if (p.FromGame || p.Installed) { inst++; if (p.AddonOn) on++; }
            }
        }

        /// <summary>一行要显示的状态文字、字色、整行底色。</summary>
        internal static string StatusOf(ModGroup g, out Color ink, out Color bg)
        {
            int total, inst, on, game;
            CountState(g, out total, out inst, out on, out game);
            ink = Theme.Ink3; bg = Theme.Surface;
            // 本地模式只有两个状态：addonlist 里开着还是关着。
            // 「已装 / 未装 / 仅游戏内」全都建立在「库 vs 游戏目录」这个对比上，本地模式没有这个对比。
            if (Core.IsLocal)
            {
                if (inst == 0) return "—";
                if (on == inst) { ink = Theme.Ok; bg = Theme.RowOn; return "开启"; }
                if (on == 0) { ink = Theme.Warn; bg = Theme.RowOff; return "关闭"; }
                ink = Theme.Warn; bg = Theme.RowOff;
                return "开启 " + on + "/" + inst;
            }
            if (inst == 0) return "未装";
            if (game == total)                       // 全都在游戏目录里、库里没有
            {
                ink = Theme.Ink2; bg = Theme.RowGame;
                return on > 0 ? "仅游戏内 · 开" : "仅游戏内 · 关";
            }
            if (inst < total)
            {
                ink = Theme.Info; bg = Theme.RowGame;
                return "部分已装 " + inst + "/" + total;
            }
            if (on == inst) { ink = Theme.Ok; bg = Theme.RowOn; return "已装 · 开"; }
            if (on == 0) { ink = Theme.Warn; bg = Theme.RowOff; return "已装 · 关"; }
            ink = Theme.Warn; bg = Theme.RowOff;
            return "已装 · 开 " + on + "/" + inst;
        }

        /// <summary>单个 vpk 的状态（套装展开后的子行用）。</summary>
        internal static string PartStatus(ModItem p, out Color ink, out Color bg)
        {
            bg = Theme.Surface; ink = Theme.Ink3;
            if (p.FromGame)
            {
                // 本地模式没有「库」的概念，游戏里这份开着就是开启、关着就是关闭
                if (Core.IsLocal)
                {
                    ink = p.AddonOn ? Theme.Ok : Theme.Warn;
                    bg = p.AddonOn ? Theme.RowOn : Theme.RowOff;
                    return p.AddonOn ? "开启" : "关闭";
                }
                ink = Theme.Ink2; bg = Theme.RowGame;
                return p.AddonOn ? "游戏内 · 开" : "游戏内 · 关";
            }
            if (p.Installed)
            {
                ink = p.AddonOn ? Theme.Ok : Theme.Warn;
                bg = p.AddonOn ? Theme.RowOn : Theme.RowOff;
                return p.AddonOn ? "已装 · 开" : "已装 · 关";
            }
            return "未装";
        }

        /// <summary>页头「显示」下拉的过滤：0 全部 / 1 已装 / 2 未装 / 3 已装但关闭。</summary>
        internal static bool MatchShow(ModGroup g, int show)
        {
            if (show <= 0) return true;
            int total, inst, on, game;
            CountState(g, out total, out inst, out on, out game);
            if (show == 1) return inst > 0;
            if (show == 2) return inst == 0;
            if (show == 3) return inst > 0 && on == 0;
            return true;
        }

        // 分组标题已经写明大类了，这一列只留细分，别每行重复一遍「地图 ·」
        internal static string SubOf(ModItem it)
        {
            if (it.Cat == it.Sub) return "—";
            string sub = it.Sub == null ? "" : it.Sub;
            int i = sub.LastIndexOf(" · ", StringComparison.Ordinal);
            if (i >= 0) sub = sub.Substring(i + 3);
            return sub.Length == 0 ? "—" : sub;
        }

        private static string SubText(ModGroup g)
        {
            if (g.Cat == g.Sub) return "—";
            string sub = g.Sub;
            int i = sub.LastIndexOf(" · ", StringComparison.Ordinal);
            if (i >= 0) sub = sub.Substring(i + 3);
            return sub;
        }

        // 一行显示什么：套装在后面标「已选/共」，让人知道这行不止一个文件
        private static string RowName(ModGroup g, int sel)
        {
            if (!g.IsSet) return g.Name;
            return g.Name + "（" + (sel > 0 ? sel : 0) + "/" + g.Parts.Count + "）";
        }

        private static string PartsTip(ModGroup g)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("这套由 ").Append(g.Parts.Count).Append(" 个 vpk 组成（双击这一行展开，可以只勾其中一个）：");
            foreach (ModItem p in g.Parts) sb.Append("\r\n· [").Append(p.Role).Append("] ").Append(p.Name);
            return sb.ToString();
        }

        internal int CountChecked(ModGroup g)
        {
            int n = 0;
            foreach (ModItem p in g.Parts) if (checkedKeys.Contains(p.Key)) n++;
            return n;
        }

        /// <summary>整个套装下架：把它的每个 vpk 都从勾选里去掉。</summary>
        internal void UncheckGroup(ModGroup g)
        {
            if (g == null) return;
            foreach (ModItem p in g.Parts) checkedKeys.Remove(p.Key);
        }

        /// <summary>整个套装上架：按默认部件勾（主体 + 必装 + 第一个选配）。</summary>
        internal void CheckGroupDefault(ModGroup g)
        {
            if (g == null) return;
            foreach (ModItem p in Catalog.DefaultParts(g)) checkedKeys.Add(p.Key);
        }

        /// <summary>在方案树里点了一个候选：方案行、底部计数一起对齐。</summary>
        internal void AfterTreePick()
        {
            UpdateSchemeLabel();
            UpdateSel();
            UpdateModCaption();
        }

        // 展开出来的单件行要照这个判断自己该不该打勾
        internal bool IsChecked(string key) { return key != null && checkedKeys.Contains(key); }

        /// <summary>搜名字、搜「替换了什么」、搜大类/位置 —— 分类就是筛选词，不再另有一套标签。</summary>
        internal bool MatchGroup(ModGroup g, string kw)
        {
            if (g.Name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (g.Subject.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (g.Where.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            string slot = SlotIndex.SlotNameOf(g);
            if (slot.Length > 0 && slot.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            foreach (ModItem p in g.Parts)
                if (p.Name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        internal void SetAllIn(ListView lv, bool on)
        {
            lv.BeginUpdate();
            bulk = true;
            try
            {
                foreach (ListViewItem li in lv.Items) li.Checked = on;   // 勾选事件里统一处理部件
            }
            finally { bulk = false; }
            lv.EndUpdate();
            SyncPicks();
            UpdateSel();
        }

        private List<ModItem> Selected()
        {
            // checkedKeys 是「零件级」的：一个套装可能只选了其中几个 vpk
            List<ModItem> sel = new List<ModItem>();
            foreach (string k in checkedKeys)
            {
                ModItem it;
                if (Core.ByRel.TryGetValue(k, out it)) sel.Add(it);
            }
            return sel;
        }

        private void UpdateSel()
        {
            List<ModItem> sel = Selected();
            long total = 0;
            foreach (ModItem it in sel) total += it.Size;
            int mods = 0;
            foreach (ModGroup g in Catalog.Groups) if (CountChecked(g) > 0) mods++;
            double rate = Core.LastRateMBps > 0.5 ? Core.LastRateMBps : 20.0;
            string files = mods == sel.Count ? "" : "（" + sel.Count + " 个文件）";
            lblSel.Text = string.Format("已选 {0} 个 MOD{1}  ·  {2}  ·  约 {3} 秒（按 {4:0} MB/s）", mods, files, Core.Human(total), (int)(total / (rate * 1048576.0)), rate);
            lblSel.ForeColor = sel.Count > 0 ? Theme.Ink : Theme.Ink3;
            UpdateSchemeLabel();
        }

        private void RefreshConflicts()
        {
            RefreshConflicts(false);
        }

        // checkExisting=false：不去逐个 stat 游戏 addons 里的文件（共享盘上很贵），启动时用
        private void RefreshConflicts(bool checkExisting)
        {
            string root = txtGame.Text.Trim();
            if (!Core.TestGame(root)) { lblConflict.Text = ""; conflictMsgs.Clear(); lblConflict.Visible = false; return; }
            List<string> msgs;
            try { msgs = ConflictCheck.Run(Core.ModList, root, checkExisting); }
            catch (Exception ex)
            {
                // 扫库线程正好在改表：这次跳过，扫完的回调还会再来一遍
                Core.Log("WARN", "冲突检查赶上扫库在改表，跳过这一次: " + ex.Message);
                return;
            }
            conflictMsgs = msgs;
            if (msgs.Count == 0) { lblConflict.Text = ""; lblConflict.Visible = false; return; }
            lblConflict.ForeColor = Theme.Warn;
            lblConflict.Visible = true;
            // 数量 + 最多白占多少 + 一个样本 + 下一步；明细点开看，不硬塞进一行
            List<DupGroup> dups = ConflictCheck.DupGroups(Core.ModList);
            if (dups.Count > 0)
            {
                long waste = 0;
                foreach (DupGroup g in dups) waste += g.Size * (g.Items.Count - 1);
                string one = dups[0].Items[0].Name;
                if (one.Length > 26) one = one.Substring(0, 26) + "…";
                lblConflict.Text = "⚠ " + dups.Count + " 组体积重复 · 最多白占 " + Core.Human(waste) + " · " + one + "\r\n"
                                 + "点这里处理：先核对内容是不是同一个文件，再决定这次不装 / 直接删掉";
            }
            else
            {
                lblConflict.Text = "⚠ " + msgs.Count + " 条提示（同名 / 已装）\r\n点这里看清单";
            }
            foreach (string m in msgs) Core.Log("WARN", m);
        }

        // 点「N 条重复嫌疑」：不再是只读文本框 —— 每一组都能核对、能这次不装、能删
        private void ShowConflictDetail()
        {
            List<DupGroup> groups = ConflictCheck.DupGroups(Core.ModList);
            List<string> others = new List<string>();
            foreach (string m in conflictMsgs)
                if (!m.StartsWith("体积完全相同", StringComparison.Ordinal)) others.Add(m);

            if (groups.Count == 0)
            {
                // 没有体积重复，只有同名/已装之类的提示，仍然是文本框
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                for (int i = 0; i < others.Count; i++) sb.AppendLine((i + 1) + ". " + others[i]);
                sb.AppendLine();
                sb.AppendLine("这些不是体积重复，本工具不自动处理：");
                sb.AppendLine("· 同名文件：右键菜单「清理同名重复」可以每个名字只留第一个");
                sb.AppendLine("· 已装文件：勾「覆盖重装」或「换整套」再装一次就清掉了");
                MessageBox.Show(this, sb.ToString(), "其它提示 · " + others.Count + " 条", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (DupForm f = new DupForm(groups, others))
            {
                bool deleted = false;
                f.OnDelete = delegate(ModItem m)
                {
                    string err = DeleteModFromLibrary(m);
                    if (err == null) deleted = true;
                    return err;
                };
                f.OnSkip = delegate(List<ModItem> items)
                {
                    foreach (ModItem it in items)
                    {
                        checkedKeys.Remove(it.Key);
                        Core.Log("INFO", "这次不装（重复嫌疑里排除）：" + it.Name);
                    }
                    CheckRowsByKeys();
                    UpdateSel();
                };
                f.OnLocate = delegate(ModItem it) { RevealModRow(it); };
                f.ShowDialog(this);

                // 列表是按 Catalog.Groups 渲染的，光从 Core.ModList 里摘掉还会留一行幽灵
                if (deleted) Core.ScanMods();
            }
            // 删过/排除过，列表和提示都要跟着变
            RefreshMods();
            RefreshConflicts();
        }

        /// <summary>把库里的一个 vpk 直接删掉。返回 null 表示成功。</summary>
        private string DeleteModFromLibrary(ModItem m)
        {
            if (m.IsArchive) return "在压缩包里，本工具不改压缩包";
            try
            {
                string err = Work.DeleteMod(m);
                if (err != null) return err;
                Core.ModList.Remove(m);
                Core.ByRel.Remove(m.Key);
                checkedKeys.Remove(m.Key);
                Core.Log("OK", "已删除：" + m.Name);
                return null;
            }
            catch (Exception ex)
            {
                Core.Log("WARN", "删除 " + m.Name + " 失败: " + ex.Message);
                return ex.Message;
            }
        }

        /// <summary>在方案树里选中这个 MOD 所在的格子（没有格子就落到「未归类」）。</summary>
        private void RevealModRow(ModItem it)
        {
            ModGroup g;
            if (!Catalog.GroupByKey.TryGetValue(it.GroupKey, out g)) { Core.Log("WARN", "定位不到这一行：" + it.Name); return; }
            ShowTree();
            if (treeView != null) treeView.RevealGroup(g);
        }

        private void BrowseGame()
        {
            FolderBrowserDialog d = new FolderBrowserDialog();
            d.Description = "选游戏根目录（里面有 left4dead2.exe 的那个文件夹）";
            if (d.ShowDialog() == DialogResult.OK) SwitchGameRoot(d.SelectedPath, "浏览选择");
        }

        private void FindGame()
        {
            List<string> g = Core.FindGames();
            if (g.Count == 0) { Log("没找到。点「浏览…」手动选。"); return; }
            if (g.Count == 1) Log("找到: " + g[0]);
            else
            {
                foreach (string s in g) Log("候选: " + s);
                Log("找到多个，先用第一个（不对就用浏览改）");
            }
            SwitchGameRoot(g[0], "自动查找");
        }

        /// <summary>
        /// 切换游戏目录只有这一条路：先更新唯一配置，再后台重扫，最后刷新列表。
        /// 以前浏览/自动找只改输入框，列表还按旧目录显示“已装/未装”，很容易看错。
        /// </summary>
        private void SwitchGameRoot(string path, string source)
        {
            string root = (path == null ? "" : path.Trim().Trim('"').TrimEnd('\\'));
            if (root.Length == 0) return;
            txtGame.Text = root;
            if (!Core.TestGame(root))
            {
                Core.Log("WARN", source + "的目录里没有 left4dead2.exe：" + root);
                lblStatus.Text = "目录不对：里面没有 left4dead2.exe";
                lblStatus.ForeColor = Theme.Accent;
                return;
            }
            // 启动 / 换模式的扫库还在后台跑：这时候枚举 ModList 必撞「集合已修改」，
            // 再起一个扫库线程更是两个线程同时重建一张表。先记下目录，扫完自动接着切。
            if (scanning)
            {
                pendingRoot = root;
                Core.Log("INFO", source + "：后台扫库还没结束，结束后自动切到 " + root);
                SetStatus("正在扫库，结束后自动切换目录…");
                return;
            }

            bool same = string.Equals(root, Core.Cfg.GameRoot, StringComparison.OrdinalIgnoreCase);
            Core.Cfg.GameRoot = root;
            Core.SaveConfig();
            if (same)
            {
                RefreshConflicts(true);
                return;
            }

            Core.Log("OK", source + "游戏目录：" + root);
            SetBusy(true);
            SetStatus("正在按新游戏目录重扫…");
            bar.Style = ProgressBarStyle.Marquee;
            scanning = true;
            StartScanWatch();
            Thread th = new Thread(delegate()
            {
                string err = null;
                try { Core.ScanMods(); }
                catch (Exception ex) { err = ex.Message; Core.Log("FAIL", "切换游戏目录后重扫失败: " + ex.Message); }
                SafeBeginInvoke(delegate()
                {
                    scanning = false;
                    SetBusy(false);
                    bar.Style = ProgressBarStyle.Blocks;
                    RefreshMods();
                    RefreshConflicts(true);
                    if (treeView != null) RebuildViews();
                    SetStatus(err == null ? "就绪" : "目录已切换，但重扫失败；请看日志");
                    if (err == null) lblStatus.ForeColor = Theme.Ink2;
                    RunPendingRoot();
                });
            });
            th.IsBackground = true;
            th.Start();
        }

        /// <summary>扫库结束后接着处理用户期间选下的目录（没有就什么都不做）。</summary>
        private void RunPendingRoot()
        {
            if (pendingRoot == null) return;
            string p = pendingRoot;
            pendingRoot = null;
            SwitchGameRoot(p, "补切（扫库完成）");
        }

        private ModGroup FirstSelectedGroup(ListView lv)
        {
            if (lv.SelectedItems.Count == 0) return null;
            return lv.SelectedItems[0].Tag as ModGroup;
        }

        // 双击一行：套装 → 就地展开看每个 vpk；单件行 → 直接切勾选
        internal void OpenRowAt(ListView lv)
        {
            if (lv.SelectedItems.Count == 0) return;
            ListViewItem li = lv.SelectedItems[0];
            ModItem p = li.Tag as ModItem;
            if (p != null) { li.Checked = !li.Checked; return; }
            ModGroup g = li.Tag as ModGroup;
            if (g == null) return;
            if (g.IsSet) { ToggleExpand(g); return; }
            ShowModInfo(g);
        }

        internal void PickPartsIn(ListView lv)
        {
            ModGroup g = FirstSelectedGroup(lv);
            if (g == null) { MessageBox.Show("先选中一行。"); return; }
            if (!g.IsSet) { MessageBox.Show("这个 MOD 只有一个 vpk，没有可选的部件。"); return; }
            PickParts(g);
        }

        private void PickParts(ModGroup g)
        {
            using (PartsForm f = new PartsForm(g, checkedKeys))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                foreach (ModItem p in g.Parts) checkedKeys.Remove(p.Key);
                foreach (ModItem p in f.Result) checkedKeys.Add(p.Key);
            }
            RefreshMods();
            if (treeView != null && treeView.SlotCount > 0) treeView.Sync();   // 树上的状态点、右侧明细一起对齐
        }

        /// <summary>改名作用在右键点的那一行上，不再看勾选；一次只改一个 vpk。</summary>
        internal void RenameSelectedIn(ListView lv)
        {
            ListViewItem li = RowTarget(lv);
            if (li == null) { MessageBox.Show("先在列表里右键点一行。"); return; }

            ModItem it = li.Tag as ModItem;          // 展开出来的子件行：就是那一个文件
            if (it == null)
            {
                ModGroup g = li.Tag as ModGroup;
                if (g == null) return;
                if (g.IsSet && g.Parts.Count > 1)
                {
                    MessageBox.Show("「" + g.Name + "」这套由 " + g.Parts.Count + " 个 vpk 组成，一次只能改一个。\r\n"
                        + "先双击这一行展开，再对里面具体那一件右键改名。", "重命名 VPK 文件名");
                    return;
                }
                it = g.Main;                          // 只有一个 vpk 的套装，直接改它
            }
            if (it == null) { MessageBox.Show("这一行没有对应的 vpk。"); return; }
            if (it.IsArchive) { MessageBox.Show("压缩包里的 vpk 不能改名。\r\n要用改名后的版本，请先解压出来。", "不能重命名"); return; }
            if (it.FromGame) { MessageBox.Show("这个 MOD 在游戏目录 addons 里，改名会破坏游戏识别。\r\n想改请先把它挪进 MOD 库。", "不能重命名"); return; }

            string nn = PromptForm.Ask("新名字（不用写 .vpk）", "重命名 " + it.Name);
            if (string.IsNullOrEmpty(nn)) return;
            string err = Work.RenameMod(it, nn.Trim());
            if (err != null) { MessageBox.Show(err); return; }
            Core.Log("OK", "已改名: " + it.Name + " -> " + nn.Trim() + ".vpk");
            Core.ScanMods(); RefreshMods();
        }

        internal void CleanDupMods()
        {
            Cursor = Cursors.WaitCursor;
            Work.DupPlan plan;
            try { plan = Work.FindDuplicateMods(); }
            catch (Exception ex) { Cursor = Cursors.Default; MessageBox.Show("扫描失败：" + ex.Message); return; }
            Cursor = Cursors.Default;

            if (plan.NothingToDo)
            {
                MessageBox.Show("没有同名重复。", "清理重复");
                return;
            }

            StringBuilder sb = new StringBuilder();
            if (plan.Drop.Count > 0)
            {
                sb.AppendLine("下面 " + plan.Drop.Count + " 个文件与库里另一份【同名且内容完全一致】，准备删掉（删了没法恢复）：");
                sb.AppendLine();
                int n = Math.Min(15, plan.Drop.Count);
                for (int i = 0; i < n; i++)
                    sb.AppendLine("  · " + plan.Drop[i].RelPath + "    " + Core.Human(plan.Drop[i].Size));
                if (plan.Drop.Count > n) sb.AppendLine("  …… 还有 " + (plan.Drop.Count - n) + " 个（会一并删掉，详见日志）");
                sb.AppendLine();
            }
            if (plan.Differ.Count > 0)
            {
                sb.AppendLine("同名但内容不同，这次一个都不动，请自己判断留哪个：");
                int n = Math.Min(10, plan.Differ.Count);
                for (int i = 0; i < n; i++)
                    sb.AppendLine("  · " + plan.Differ[i].RelPath + "    " + Core.Human(plan.Differ[i].Size));
                if (plan.Differ.Count > n) sb.AppendLine("  …… 还有 " + (plan.Differ.Count - n) + " 个");
            }
            if (plan.ArchiveSkipped > 0)
                sb.AppendLine("\n（压缩包内的 " + plan.ArchiveSkipped + " 条同名条目已跳过：删一条会把整包带走）");

            if (plan.Drop.Count == 0)
            {
                MessageBox.Show(sb.ToString(), "清理重复", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(sb.ToString(), "清理重复（确认删除）", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

            // 清单落到日志里，删完还能查到删了哪些
            foreach (ModItem m in plan.Drop) Core.Log("INFO", "清重删除: " + m.RelPath);
            List<string> done = Work.DeleteDuplicates(plan);
            Core.Log("OK", "清理重复: 删除 " + done.Count + " 个"
                + (plan.Differ.Count > 0 ? "，跳过同名不同内容 " + plan.Differ.Count + " 个" : ""));
            MessageBox.Show("已删除 " + done.Count + " 个重复文件"
                + (plan.Differ.Count > 0 ? "；另有 " + plan.Differ.Count + " 个同名但内容不同，没动。" : "。")
                + "\n详见日志。", "清理重复");
            Core.ScanMods(); RefreshMods(); RefreshConflicts();
        }

        internal void RevealIn(ListView lv)
        {
            ModGroup g = FirstSelectedGroup(lv);
            if (g == null) { MessageBox.Show("先选中一个 MOD。"); return; }
            ModItem m = g.Main;
            try
            {
                // 压缩包里的东西：直接定位到压缩包本身
                string target = m.IsArchive ? Path.Combine(Core.ModRoot, m.Container) : m.FullPath;
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + target + "\"");
            }
            catch { }
        }

        private void ShowModInfo(ModGroup g)
        {
            ModItem m = g.Main;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("名字  : " + g.Name);
            sb.AppendLine("分类  : " + g.Where);
            sb.AppendLine("大小  : " + Core.Human(g.Size));
            if (g.IsSet) sb.AppendLine("组成  : " + g.Parts.Count + " 个 vpk（双击这一行可以挑部件）");
            sb.AppendLine("改了什么: " + (g.Subject.Length == 0 ? "（认不出来）" : g.Subject + "  [" + g.SubjectLevel + "]"));
            sb.AppendLine();
            foreach (ModItem p in g.Parts)
            {
                sb.AppendLine("· [" + p.Role + "] " + p.Name + "  " + Core.Human(p.Size));
                if (p.IsArchive) sb.AppendLine("      在压缩包里：" + p.Container);
                else sb.AppendLine("      路径：" + p.RelPath);
                if (p.Subject.Length > 0) sb.AppendLine("      改了什么：" + p.Subject);
            }
            if (!m.IsArchive)
            {
                Cursor = Cursors.WaitCursor;
                List<string> paths = Vpk.Paths(m.FullPath, 200000);
                Cursor = Cursors.Default;
                sb.AppendLine();
                sb.AppendLine("内部条目: " + paths.Count + " 个");
                int n = Math.Min(6, paths.Count);
                for (int i = 0; i < n; i++) sb.AppendLine("  " + paths[i]);
            }
            MessageBox.Show(sb.ToString(), "MOD 信息");
        }

        /// <summary>
        /// 真正的「只装这几个 vpk」：复制文件 + 写 addonlist，平台/启动项/参数一概不碰。
        /// what 用来在提示里说清楚这次装的是哪一份。
        /// </summary>
        private void InstallMods(List<ModItem> sel, string title, string what)
        {
            string root = txtGame == null ? "" : txtGame.Text.Trim();
            if (root.Length == 0 || !Core.TestGame(root))
            {
                MessageBox.Show("先选对游戏目录（有 left4dead2.exe 的那个）。");
                return;
            }
            if (sel == null || sel.Count == 0)
            {
                MessageBox.Show("这次没有要装的 vpk。");
                return;
            }
            // 压缩包里的 vpk 也能装：CopyMods 会单独抽那一个出来，不解整包、不动 U 盘上的包
            Work.Overwrite = !rdSkip.Checked;
            int copied = 0, skipped = 0;
            List<string> added = null, failed = null;
            try
            {
                RunBusy<int>("正在装 " + what + "…", delegate
                {
                    Work.CopyMods(root, sel, null, out copied, out skipped, out added, out failed);
                    return 0;
                });
            }
            catch (Exception ex)
            {
                Core.Log("FAIL", title + " 出错: " + ex.Message);
                MessageBox.Show("装失败了：" + ex.Message + "\r\n详细信息看下面的日志。", title);
                return;
            }
            if (failed != null && failed.Count > 0)
            {
                Core.Log("FAIL", title + "：失败 " + failed.Count + " 个（" + string.Join("、", failed.ToArray()) + "）");
                MessageBox.Show("有 " + failed.Count + " 个没装进去，多半是游戏正开着（文件被占用）或盘满了。\r\n看下面日志的 FAIL 行。", "没装全", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Dictionary<string, bool> st = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (ModItem it in sel) st[it.Name] = true;
            AddonList.Write(root, st);
            // 状态列是从 addons + addonlist 现读的，装完得重扫一次，否则列表还显示旧状态
            try { Core.ScanMods(); } catch (Exception exs) { Core.Log("WARN", "装完重扫失败: " + exs.Message); }
            RefreshMods();
            if (copied == 0)
            {
                // 全都已经在游戏目录里：这条路上一个文件都不用动，只改开关
                Core.Log("OK", title + "：全部已在游戏目录里，只把开关写成「开」，没有复制文件");
                MessageBox.Show("这些 MOD 本来就在游戏目录里，这次没有复制、移动任何文件。\r\n\r\n"
                    + "只把 addonlist 里它们的开关写成「开」（" + skipped + " 个），进游戏就生效。\r\n"
                    + "其它 MOD 的开关状态没动，平台、启动项、参数也没动。", title);
                return;
            }
            Core.Log("OK", title + "：写入 " + copied + " 个、跳过 " + skipped + " 个（已在游戏里的只切开关，平台/启动项/参数都没动）");
            MessageBox.Show("装好了：" + what + "，共 " + copied + " 个文件"
                + (skipped > 0 ? "（另有 " + skipped + " 个已在游戏里，只切了开关）" : "") + "。\r\n\r\n"
                + "已经写进 addonlist，进游戏就生效。\r\n"
                + "其它 MOD 的开关状态没动，平台、启动项、参数也没动。", title);
        }

        // 压缩包内容识别：第一次把每个 rar 里的 vpk 目录表读一遍，结果落缓存
        internal void ProbeArchivesUi()
        {
            if (Core.IsLocal)
            {
                MessageBox.Show("本地模式只扫描游戏目录 addons，不读取主库里的压缩包。\r\n\r\n要识别 U 盘库里的压缩包，请先切回 U 盘模式。",
                    "识别压缩包内容", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show("会在后台把库里每个压缩包打开看一眼（只读前几百 KB，不解压整包），用来按内容判类型。\r\n\r\n第一次可能要几十秒，之后走缓存不再重复。开始吗？",
                "识别压缩包内容", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            int done = 0;
            try
            {
                RunBusy<int>("正在识别压缩包内容…", delegate
                {
                    done = Catalog.ProbeArchives(delegate(string s) { SetStatus(s); });
                    return done;
                });
            }
            catch (Exception ex)
            {
                Core.Log("FAIL", "识别压缩包出错: " + ex.Message);
                MessageBox.Show("出错了：" + ex.Message, "识别压缩包");
                return;
            }
            Core.ScanMods();
            RefreshMods();
            RefreshConflicts(true);
            Core.Log("OK", "压缩包内容识别完成：新增 " + done + " 条");
            MessageBox.Show("识别完成：新判出 " + done + " 条（其余读不出结论的也记下来了，不会再重算）。\r\n\r\n方案树和分类已刷新，可以直接查看更新后的位置。", "识别压缩包内容");
        }

        internal void BulkImport()
        {
            FolderBrowserDialog d = new FolderBrowserDialog();
            d.Description = "选一个放着 MOD 的文件夹（vpk / rar / zip 都认，会按类型自动建文件夹归类）";
            if (d.ShowDialog() != DialogResult.OK) return;
            string src = d.SelectedPath;
            if (src.TrimEnd('\\').StartsWith(Core.ModRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("这个文件夹已经在 MOD 库里了，换个外面的文件夹。");
                return;
            }
            DialogResult how = MessageBox.Show(
                "导入后，源文件夹里的原文件怎么处理？\r\n\r\n[是] 移动（收进库，不占双份体积）\r\n[否] 复制（原文件留着）\r\n",
                "导入方式", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (how == DialogResult.Cancel) return;
            bool move = (how == DialogResult.Yes);
            int imported = 0, skipped = 0;
            List<string> log = null;
            try
            {
                log = RunBusy<List<string>>("正在导入（文件大，别关窗口）…", delegate { return Work.ImportVpks(src, move, out imported, out skipped); });
            }
            catch (Exception ex)
            {
                Core.Log("FAIL", "导入出错: " + ex.Message);
                MessageBox.Show("导入失败：" + ex.Message, "导入");
                return;
            }
            Core.Log("OK", "导入完成：" + imported + " 个，已存在跳过 " + skipped + " 个，方式=" + (move ? "移动" : "复制"));
            RefreshMods();
            RefreshConflicts(true);
            string sample = "";
            if (log != null)
                for (int i = 0; i < log.Count && i < 6; i++) sample += "\r\n  · " + log[i];
            MessageBox.Show("导入完成：入库 " + imported + " 个，已存在跳过 " + skipped + " 个。" + sample
                + "\r\n\r\n分类文件夹是按文件名猜的；猜不准就在列表里右键「重命名所选」或直接拖到对的文件夹。\r\n"
                + "这些 MOD 现在只在库里，还没有装进游戏。要用的时候在方案树里勾上，或右键选「只装这个 MOD」。", "导入");
        }

        /// <summary>库文件夹管理：把电脑上任意目录加进/移出扫描范围。工具不再只服务 U 盘布局。</summary>
        internal void ManageModDirs()
        {
            if (Core.IsLocal)
            {
                MessageBox.Show("本地模式只扫描游戏目录 addons，附加库目录不参与。\r\n\r\n要管理附加目录，请先在设置里切回 U 盘模式。",
                    "库文件夹管理", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (LibraryDirsForm f = new LibraryDirsForm())
            {
                f.ShowDialog(this);
            }
            if (LibraryDirsForm.Dirty)
            {
                Core.SaveConfig();
                Core.Log("OK", "库目录已改，重新扫描…");
                Core.ScanMods();
                RefreshMods();
                RefreshConflicts();
                if (treeView != null) { RebuildViews(); treeView.Sync(); }
            }
        }

        /// <summary>小窗口：列出现有扫描目录（主库 + 自设目录），可添加 / 移除。</summary>
        private class LibraryDirsForm : Form
        {
            public static bool Dirty = false;      // 关掉时让外面知道要不要重扫
            private readonly ListBox lb = new ListBox();
            private readonly Label lblMain = new Label();

            public LibraryDirsForm()
            {
                Dirty = false;
                Text = "库文件夹管理";
                ClientSize = new Size(560, 380);
                Font = Theme.UI;
                BackColor = Theme.Bg;
                StartPosition = FormStartPosition.CenterParent;
                MaximizeBox = false;
                MinimizeBox = false;
                ShowInTaskbar = false;

                Label top = new Label();
                top.SetBounds(12, 10, 536, 52);
                top.Font = Theme.UISmall;
                top.ForeColor = Theme.Ink2;
                top.Text = "下面的文件夹只在 U 盘模式下扫描（vpk / rar / zip 全认）。游戏目录 addons 里的已装 MOD 自动识别，不用加。\r\n"
                         + "在这里加目录只是「原地扫描」，不复制不移动任何文件；本地模式不会读取它们。";
                Controls.Add(top);

                lblMain.SetBounds(12, 66, 536, 20);
                lblMain.Font = Theme.UIBold;
                lblMain.ForeColor = Theme.Ink2;
                lblMain.Text = "U 盘模式主库：" + Core.ModRoot;
                Controls.Add(lblMain);

                lb.SetBounds(12, 92, 536, 220);
                lb.BorderStyle = BorderStyle.FixedSingle;
                lb.IntegralHeight = false;
                lb.BackColor = Theme.Surface;
                lb.ForeColor = Theme.Ink;
                Controls.Add(lb);

                FlatButton add = new FlatButton();
                add.Text = "添加文件夹…";
                add.SetBounds(12, 326, 120, Theme.H);
                add.Primary = true;
                add.Click += delegate { AddDir(); };
                Controls.Add(add);

                FlatButton del = new FlatButton();
                del.Text = "移除所选";
                del.SetBounds(142, 326, 96, Theme.H);
                del.Click += delegate { RemoveDir(); };
                Controls.Add(del);

                FlatButton close = new FlatButton();
                close.Text = "关闭";
                close.SetBounds(464, 326, 84, Theme.H);
                close.DialogResult = DialogResult.Cancel;
                Controls.Add(close);
                CancelButton = close;

                Fill();
            }

            private void Fill()
            {
                lb.BeginUpdate();
                lb.Items.Clear();
                foreach (string d in Core.Cfg.ModDirs) lb.Items.Add(d);
                lb.EndUpdate();
            }

            private void AddDir()
            {
                FolderBrowserDialog d = new FolderBrowserDialog();
                d.Description = "选要扫描的 MOD 文件夹（里面放 vpk / rar / zip，子文件夹也会翻）";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string p = d.SelectedPath.TrimEnd('\\');
                if (p.Equals(Core.ModRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                { MessageBox.Show("这就是主库本身，不用再加。"); return; }
                foreach (object o in lb.Items)
                    if (string.Equals((string)o, p, StringComparison.OrdinalIgnoreCase))
                    { MessageBox.Show("已经在列表里了。"); return; }
                // 和主库重叠（包含或被包含）只会让同一个文件被扫两遍，列表里多一条同名项。
                // 扫库那边已经按完整路径去重，这里只是提醒一句，让人知道不是白加的。
                if (Core.IsUnder(Core.ModRoot, p) || Core.IsUnder(p, Core.ModRoot))
                {
                    if (MessageBox.Show("这个文件夹和主库（" + Core.ModRoot + "）是包含关系，里面的文件主库已经扫过了。\r\n"
                        + "再加一遍不会出现重复条目（扫库会去重），但也没什么用。还要加吗？",
                        "和主库重叠", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                }
                // 和游戏 addons 重叠的不管：游戏目录已装 MOD 是单独认的，重复扫只会多出同名项
                Core.Cfg.ModDirs.Add(p);
                Dirty = true;
                Fill();
            }

            private void RemoveDir()
            {
                if (lb.SelectedItem == null) { MessageBox.Show("先选中一行。"); return; }
                string p = (string)lb.SelectedItem;
                Core.Cfg.ModDirs.Remove(p);
                Dirty = true;
                Fill();
            }
        }

        private void SaveProfile()
        {
            List<ModItem> sel = Selected();
            if (sel.Count == 0) { MessageBox.Show("先勾一些 MOD 再存方案。"); return; }
            string name = PromptForm.Ask("方案名字（比如：日常 / 打僵尸 / 拍视频）", "存为方案");
            if (string.IsNullOrEmpty(name)) return;
            name = name.Trim();
            Profile old = ProfileStore.Load(name);
            if (old != null)
            {
                string when = old.Saved == DateTime.MinValue ? "未知" : old.Saved.ToString("yyyy-MM-dd HH:mm");
                string q = "已经有「" + name + "」这个方案了（" + old.Mods.Count + " 个 MOD，存于 " + when + "）。\r\n\r\n直接覆盖它？";
                if (MessageBox.Show(q, "覆盖方案", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            }
            Profile p = new Profile();
            p.Name = name;
            p.Dxvk = chkDxvk.Checked;
            p.Plat = PlatFlags();
            p.HasOpt = true;
            p.Swap = chkSwap.Checked;
            p.Overwrite = rdOver != null && rdOver.Checked;
            p.Tweak = TweakName();
            p.Launch = txtLaunch == null ? "" : LaunchArgs.ForWrite(txtLaunch.Text);
            foreach (ModItem it in sel) p.Mods.Add(it.Key);
            ProfileStore.Save(p);                     // 同名就是覆盖，不留副本
            appliedProfile = p.Name;
            FillProfiles(p.Name);
            Core.Log("OK", "方案已存（覆盖式）：" + p.Name + "（" + p.Mods.Count + " 个 MOD）");
        }

        // 把方案灌进界面。withOptions=false 时只勾 MOD 和平台（下拉预览用），不动安装选项
        private void LoadProfileIntoUi(Profile p, bool withOptions, bool quiet)
        {
            if (p == null) { MessageBox.Show("找不到这个方案（文件可能被删了）。"); return; }
            if (p.Mods.Count == 0) { MessageBox.Show("这个方案是空的。"); return; }
            List<string> ok = new List<string>();
            List<string> miss = new List<string>();
            foreach (string r in p.Mods)
            {
                if (Core.ByRel.ContainsKey(r)) ok.Add(r);
                else miss.Add(r);
            }
            checkedKeys.Clear();
            foreach (string r in ok) checkedKeys.Add(r);
            chkDxvk.Checked = p.Dxvk;
            ApplyPlatFlags(p.Plat);
            if (withOptions && p.HasOpt)
            {
                chkSwap.Checked = p.Swap;
                if (rdOver != null) rdOver.Checked = p.Overwrite;
                if (rdSkip != null) rdSkip.Checked = !p.Overwrite;
                if (cboTweak != null) SelectTweak(p.Tweak);
                if (txtLaunch != null && !string.IsNullOrEmpty(p.Launch)) txtLaunch.Text = p.Launch;
            }
            CheckRowsByKeys();
            UpdateSel();
            if (withOptions) appliedProfile = p.Name;
            Core.Log("OK", "已套用方案：" + p.Name + "（" + ok.Count + " 个 MOD）");
            if (miss.Count > 0)
            {
                Core.Log("WARN", "方案「" + p.Name + "」有 " + miss.Count + " 个引用在库里找不到，已跳过");
                string ex = "";
                for (int i = 0; i < miss.Count && i < 5; i++) ex += "\r\n  · " + miss[i];
                if (miss.Count > 5) ex += "\r\n  · …（还有 " + (miss.Count - 5) + " 个）";
                string q = "方案「" + p.Name + "」里有 " + miss.Count + " 个文件在库里已经找不到了，这次会跳过它们（能用的 " + ok.Count + " 个照常勾上）：" + ex
                    + "\r\n\r\n要修：把文件放回 U 盘原来的位置，或者重新存一版方案。";
                if (!quiet) MessageBox.Show(q, "方案有失效引用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }


        private void ProfileSelectionChanged()
        {
            if (initializing || cboProfile.SelectedItem == null) return;
            string name = cboProfile.SelectedItem.ToString();
            LoadSchemeKeys();
            RefreshMods();
            UpdateSchemeLabel();
            if (name.StartsWith("（")) return;
            Log("正在对照方案「" + name + "」，点「套用」才会替换当前勾选");
        }


        private void ApplyProfile()
        {
            if (cboProfile.SelectedItem == null) return;
            string name = cboProfile.SelectedItem.ToString();
            if (name.StartsWith("（")) return;
            Profile p = ProfileStore.Load(name);
            if (p == null) { MessageBox.Show("找不到这个方案（文件可能被删了）。"); return; }

            // 选择本身不再有任何副作用，只有这里才真正替换当前工作选择。
            // 若当前勾选不是这套方案，先明确告诉用户会被覆盖，而不是默默丢掉。
            if (checkedKeys.Count > 0 && CurrentSelectionDiffersFrom(p))
            {
                string q = "当前已经勾了 " + checkedKeys.Count + " 个 MOD，和方案「" + name + "」不一样。\r\n\r\n"
                         + "套用会替换当前勾选；已经存好的方案不会受影响。继续吗？";
                if (MessageBox.Show(q, "套用方案会替换当前选择",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            }
            LoadSchemeKeys();
            LoadProfileIntoUi(p, true, false);
            RefreshMods();
        }

        private bool CurrentSelectionDiffersFrom(Profile p)
        {
            if (p == null) return true;
            HashSet<string> stored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string k in p.Mods) if (Core.ByRel.ContainsKey(k)) stored.Add(k);
            if (!stored.SetEquals(checkedKeys)) return true;
            if (p.HasOpt)
            {
                if (chkDxvk.Checked != p.Dxvk) return true;
                if (chkSwap.Checked != p.Swap) return true;
                if ((rdOver != null && rdOver.Checked) != p.Overwrite) return true;
                if (!TweakMatches(p.Tweak)) return true;
            }
            return false;
        }

        private bool TweakMatches(string tweak)
        {
            string now = TweakName();
            if (string.IsNullOrEmpty(tweak)) return string.IsNullOrEmpty(now);
            return string.Equals(now, tweak, StringComparison.OrdinalIgnoreCase);
        }


        // 方案套用后把行的勾选状态跟 checkedKeys 对齐（不逐个触发部件默认逻辑）
        private void CheckRowsByKeys()
        {
            SyncPicks();
        }

        private string PlatFlags()
        {
            return "Skeeto=" + (chkSkeeto.Checked ? "1" : "0") +
                   ";Asset=" + (chkAsset.Checked ? "1" : "0") +
                   ";Mqcam=" + (chkMqcam.Checked ? "1" : "0") +
                   ";Dxvk=" + (chkDxvk.Checked ? "1" : "0") +
                   ";Launch=" + (chkLaunch.Checked ? "1" : "0") +
                   ";Cfg=" + (chkCfg.Checked ? "1" : "0");
        }

        private void ApplyPlatFlags(string plat)
        {
            if (string.IsNullOrEmpty(plat)) return;
            foreach (string part in plat.Split(';'))
            {
                string[] kv = part.Split('=');
                if (kv.Length != 2) continue;
                bool on = kv[1] == "1";
                switch (kv[0])
                {
                    case "Skeeto": chkSkeeto.Checked = on; break;
                    case "Asset": chkAsset.Checked = on; break;
                    case "Mqcam": chkMqcam.Checked = on; break;
                    case "Dxvk": chkDxvk.Checked = on; break;
                    case "Launch": chkLaunch.Checked = on; break;
                    case "Cfg": chkCfg.Checked = on; break;
                }
            }
        }

        public string LayoutDump()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("窗口客户区: " + ClientSize.Width + " x " + ClientSize.Height);
            sb.AppendLine("游戏路径框: " + txtGame.Width + " x " + txtGame.Height);
            sb.AppendLine("模式下拉  : " + cboMode.Left + "," + cboMode.Top + " " + cboMode.Width + "x" + cboMode.Height
                          + "  文本=" + (cboMode.SelectedIndex >= 0 ? cboMode.SelectedItem.ToString() : "(未选)")
                          + "  可见=" + cboMode.Visible);
            sb.AppendLine("MOD 库分组: " + gMod.Width + " x " + gMod.Height);
            sb.AppendLine("方案树    : 树可见=" + (treeView != null && treeView.Visible) + "，树尺寸="
                          + (treeView == null ? "-" : treeView.Width + "x" + treeView.Height)
                          + "，位置 " + (treeView == null ? "-" : treeView.SlotCount.ToString()));
            sb.AppendLine("日志分组  : " + gLog.Width + " x " + gLog.Height);
            sb.AppendLine("日志框    : " + txtLog.Width + " x " + txtLog.Height);
            sb.AppendLine("进度条    : " + bar.Left + "," + bar.Top + " " + bar.Width + " x " + bar.Height);
            sb.AppendLine("按钮行    : 顶=" + btnInstall.Top + " 左=" + btnInstall.Left);
            sb.AppendLine("启动项框  : " + txtLaunch.Left + "," + txtLaunch.Top + " " + txtLaunch.Width + " x " + txtLaunch.Height
                          + "  复制按钮 " + btnLaunchCopy.Left + "," + btnLaunchCopy.Top + " " + btnLaunchCopy.Width + "x" + btnLaunchCopy.Height
                          + "  编辑按钮 " + btnLaunchEdit.Left + "," + btnLaunchEdit.Top + " " + btnLaunchEdit.Width + "x" + btnLaunchEdit.Height);
            sb.AppendLine("启动项值  : " + LaunchArgs.Clean(txtLaunch.Text)
                          + (LaunchArgs.HasSlot(txtLaunch.Text) ? "   [含 0/1 占位]" : ""));
            sb.AppendLine("启动项默认: " + LaunchArgs.Default);
            sb.AppendLine("开始装按钮: " + btnInstall.Left + "," + btnInstall.Top + " " + btnInstall.Width + " x " + btnInstall.Height);
            return sb.ToString();
        }

        private void ApplyResponsive()
        {
            int W = ClientSize.Width, H = ClientSize.Height;
            if (W == lastRespW && H == lastRespH) return;
            lastRespW = W;
            lastRespH = H;
            SuspendLayout();
            int leftW = 396;                                   // 左栏定宽：够放最长的复选框文案
            // 抽屉收起时右栏直接顶到左边距：整个窗口就剩一条顶栏 + 一张台面
            int rightX = drawerOpen ? Theme.M + leftW + Theme.G : Theme.M;
            int rightW = W - Theme.M - rightX;

            gPlat.Width = leftW;
            gOpt.Width = leftW;
            gLog.Width = leftW;
            txtLog.Width = leftW - 24;
            LayoutLaunchRow(leftW);
            chkSwap.Width = leftW - 24;
            chkStrict.Width = leftW - 24;
            cboTweak.Width = Math.Max(120, leftW - 24 - 70 - 90 - 8);
            btnTweak.Left = cboTweak.Left + cboTweak.Width + 4;

            gMod.Left = rightX;
            gMod.Width = rightW;
            viewHost.Width = rightW - 24;
            lblSel.Width = rightW - 24;
            lblConflict.Width = rightW - 24;

            txtSearch.Width = rightW - 126;
            btnClearSearch.Left = rightW - 68;
            btnManage.Left = rightW - 12 - 88;

            // 顶部：游戏目录 / 模式 / 浏览 / 自动找 / 设置
            const int modeW = 104;
            const int findW = 76;
            const int browseW = 76;
            const int settingsW = 60;
            const int viewW = 56;       // 台面 ⇄ 方案树
            const int panelW = 56;      // 左栏抽屉
            const int topGap = 8;
            int settingsX = W - Theme.M - settingsW;
            int panelX = settingsX - topGap - panelW;
            int viewX = panelX - topGap - viewW;
            int findX = viewX - topGap - findW;
            int browseX = findX - topGap - browseW;
            int modeX = browseX - topGap - modeW;
            txtGame.Width = modeX - 8 - 76;
            cboMode.Left = modeX;
            cboMode.Top = 18;
            btnBrowse.Left = browseX;
            btnFind.Left = findX;
            btnSettings.Left = settingsX;
            btnView.Left = viewX;
            btnPanel.Left = panelX;

            // 底部（按钮整组贴右下）
            btnOpen.Left = W - Theme.M - 104;
            btnRestore.Left = btnOpen.Left - 8 - 96;
            btnSwitch.Left = btnRestore.Left - 8 - 116;
            btnInstall.Left = btnSwitch.Left - 8 - 112;
            lblStatus.Width = W - 2 * Theme.M;
            bar.Width = Math.Max(200, btnInstall.Left - 8 - Theme.M);

            ResumeLayout(true);
        }

        /// <summary>总览 ⇄ 方案树。总览是整窗口一块看板，不是另一个窗口。</summary>
        internal void ToggleView()
        {
            if (OverviewOpen) HideOverview();
            else ShowOverview();
        }

        /// <summary>桌面上的「开始安装」按钮走这里。</summary>
        internal void InstallFromDesk() { DoInstall(); }

        /// <summary>左栏抽屉：收起后右栏顶到左边距，整窗只剩台面。</summary>
        internal void SetDrawer(bool open)
        {
            drawerOpen = open;
            if (gPlat != null) gPlat.Visible = open;
            if (gOpt != null) gOpt.Visible = open;
            if (gLog != null) gLog.Visible = open;
            if (btnPanel != null) btnPanel.Text = open ? "收起" : "面板";
            lastRespW = -1;                    // 强制重排一次（尺寸没变也要排）
            ApplyResponsive();
        }

        /// <summary>桌面自检：--deskself 走这里。不弹浏览器，只验数据与本地服务。</summary>
        internal int DeskSelfTest()
        {
            int bad = 0;
            ShowTree();
            if (treeView == null) { Core.Log("FAIL", "DESK 树没建起来"); return 1; }

            // 1) 建卡
            List<DeskCard> cards = DeskModel.Build(treeView);
            int rows = 0;
            foreach (DeskCard c in cards) rows += c.Rows.Count;
            Core.Log("INFO", "DESK 建卡 " + cards.Count + " 张，行合计 " + rows);
            if (cards.Count == 0) { bad++; Core.Log("FAIL", "DESK 一张卡都没建出来"); }
            if (rows == 0) { bad++; Core.Log("FAIL", "DESK 卡里一行都没有"); }

            // 2) JSON 是不是能被前端直接吃
            string json = DeskModel.ToJson(cards, treeView, this);
            if (!json.StartsWith("{") || !json.EndsWith("}")) { bad++; Core.Log("FAIL", "DESK 导出的 JSON 头尾不对"); }
            if (json.IndexOf("\"cards\":[") < 0) { bad++; Core.Log("FAIL", "DESK JSON 里没有 cards"); }
            if (json.IndexOf("\"rows\":[") < 0) { bad++; Core.Log("FAIL", "DESK JSON 里没有 rows"); }
            if (json.IndexOf('\u0001') >= 0 || json.IndexOf('\0') >= 0) { bad++; Core.Log("FAIL", "DESK JSON 里混进了控制字符"); }

            // 3) 摆位：写进去能读回来，文件落盘，然后原样还原
            string layFile = DeskLayout.FileOf();
            byte[] before = null;
            try { if (File.Exists(layFile)) before = File.ReadAllBytes(layFile); } catch { }
            try
            {
                DeskLayout.Clear();
                DeskLayout.Put("cat|枪械", 0.4f, -0.1f);
                PointF pt;
                if (!DeskLayout.TryGet("cat|枪械", out pt) || Math.Abs(pt.X - 0.4f) > 0.001f || Math.Abs(pt.Y + 0.1f) > 0.001f)
                { bad++; Core.Log("FAIL", "DESK 摆位存取对不上"); }
                else Core.Log("INFO", "DESK 摆位存取正常 (0.4, -0.1)");
                if (!File.Exists(layFile)) { bad++; Core.Log("FAIL", "DESK 摆位文件没写出来"); }
            }
            finally
            {
                try
                {
                    if (before == null) { if (File.Exists(layFile)) File.Delete(layFile); }
                    else File.WriteAllBytes(layFile, before);
                }
                catch { }
                DeskLayout.Clear();
            }

            // 4) 本地服务：起一个不带窗口的，真的发一次 HTTP 请求
            DeskServer srv = new DeskServer(this, treeView);
            try
            {
                srv.Start(false);

                // 必须放到后台线程去请求：服务端要 Invoke 回 UI 线程取数据，
                // 而这里正占着 UI 线程等响应 —— 直接在 UI 线程 DownloadString 会死锁
                // （第一次就是这么卡了 100 秒，报“操作超时”）
                Func<string, string> get = delegate(string url)
                {
                    string res = null; Exception err = null;
                    Thread t = new Thread(delegate()
                    {
                        try
                        {
                            using (System.Net.WebClient wc = new System.Net.WebClient())
                            {
                                wc.Encoding = Encoding.UTF8;
                                res = wc.DownloadString(url);
                            }
                        }
                        catch (Exception ex) { err = ex; }
                    });
                    t.IsBackground = true;
                    t.Start();
                    DateTime until = DateTime.Now.AddSeconds(10);
                    while (t.IsAlive && DateTime.Now < until) { Application.DoEvents(); Thread.Sleep(10); }
                    if (t.IsAlive) throw new Exception("请求超时：" + url);
                    if (err != null) throw err;
                    return res;
                };

                string body = get(srv.Url + "api/state");
                if (string.IsNullOrEmpty(body) || body.IndexOf("\"cards\"") < 0)
                { bad++; Core.Log("FAIL", "DESK 服务端返回的 state 不对"); }
                else Core.Log("INFO", "DESK 服务端 state OK（" + body.Length + " 字节）");

                string page = get(srv.Url);
                if (string.IsNullOrEmpty(page) || page.IndexOf("three.min.js") < 0)
                { bad++; Core.Log("FAIL", "DESK 首页没把 three.js 引进来"); }
                else Core.Log("INFO", "DESK 首页 %OK%（" + page.Length + " 字节）".Replace("%OK%", "OK"));
                if (page == null || page.IndexOf("window.__DESK_TOKEN") < 0)
                { bad++; Core.Log("FAIL", "DESK 首页没注入令牌，页面上的写操作会一律 403"); }
                else Core.Log("INFO", "DESK 首页已注入写操作令牌");

                // 写操作令牌：本机别的网页也能往 127.0.0.1 发 POST，所以没令牌必须 403。
                // 用一个不存在的 api 路径试，不会碰到任何真实数据。
                Func<string, int> post = delegate(string tok)
                {
                    int code = -1; Exception err = null;
                    Thread t = new Thread(delegate()
                    {
                        try
                        {
                            System.Net.HttpWebRequest req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(srv.Url + "api/__tokencheck");
                            req.Method = "POST";
                            req.ContentType = "application/json";
                            if (tok != null) req.Headers["X-Desk-Token"] = tok;
                            byte[] data = Encoding.UTF8.GetBytes("{}");
                            req.ContentLength = data.Length;
                            using (Stream st = req.GetRequestStream()) st.Write(data, 0, data.Length);
                            using (System.Net.HttpWebResponse r = (System.Net.HttpWebResponse)req.GetResponse()) code = (int)r.StatusCode;
                        }
                        catch (System.Net.WebException wex)
                        {
                            System.Net.HttpWebResponse r = wex.Response as System.Net.HttpWebResponse;
                            if (r != null) code = (int)r.StatusCode; else err = wex;
                        }
                        catch (Exception ex) { err = ex; }
                    });
                    t.IsBackground = true;
                    t.Start();
                    DateTime until = DateTime.Now.AddSeconds(10);
                    while (t.IsAlive && DateTime.Now < until) { Application.DoEvents(); Thread.Sleep(10); }
                    if (t.IsAlive) throw new Exception("请求超时：api");
                    if (err != null) throw err;
                    return code;
                };

                if (post(null) != 403) { bad++; Core.Log("FAIL", "DESK 写操作不带令牌也能过（本机网页就能装机）"); }
                else Core.Log("INFO", "DESK 写操作无令牌被拒 403");
                if (post("wrong-token") != 403) { bad++; Core.Log("FAIL", "DESK 写操作令牌错了也能过"); }
                else Core.Log("INFO", "DESK 写操作错令牌被拒 403");
                // 令牌对了就该进路由：这个假路径没有处理分支，会返回 404「no api」——
                // 只要不是 403 就说明令牌这关过了（真接口走各自的 200）。
                int okCode = post(srv.Token);
                if (okCode == 403 || okCode < 0) { bad++; Core.Log("FAIL", "DESK 写操作带对令牌反倒被拒（" + okCode + "）"); }
                else Core.Log("INFO", "DESK 写操作带对令牌放行（路由返回 " + okCode + "，不是 403）");
            }
            catch (Exception ex)
            {
                bad++;
                Core.Log("FAIL", "DESK 本地服务跑不通：" + ex.Message);
            }
            finally { srv.Dispose(); }

            // 5) 抽屉：收起时右栏顶到左边距，展开时让出 396 给左栏
            SetDrawer(true);
            if (gPlat == null || !gPlat.Visible) { bad++; Core.Log("FAIL", "DESK 抽屉展开后左栏没出来"); }
            if (gMod.Left != Theme.M + 396 + Theme.G) { bad++; Core.Log("FAIL", "DESK 抽屉展开后右栏位置不对: " + gMod.Left); }
            SetDrawer(false);
            if (gPlat != null && gPlat.Visible) { bad++; Core.Log("FAIL", "DESK 抽屉收起后左栏还在"); }
            if (gMod.Left != Theme.M) { bad++; Core.Log("FAIL", "DESK 抽屉收起后右栏没顶到左边: " + gMod.Left); }

            Snap("desk-tree");
            return bad;
        }

        private void Snap(string tag)
        {
            try
            {
                PerformLayout();
                Application.DoEvents();
                using (Bitmap b = new Bitmap(Size.Width, Size.Height))
                {
                    DrawToBitmap(b, new Rectangle(0, 0, Size.Width, Size.Height));
                    b.Save(Path.Combine(Core.LogDir, tag + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            catch (Exception ex) { Core.Log("WARN", "截图失败: " + ex.Message); }
        }

        private bool TestWritable(string dir)
        {
            // 只测游戏根是不够的：装机要往 left4dead2\addons 和 bin 里写，
            // 根目录能写、子目录被占用/只读的情况一样会让整次装机写一半。
            foreach (string sub in new string[] { "", "left4dead2\\addons", "bin" })
            {
                string d = sub.Length == 0 ? dir : Path.Combine(dir, sub);
                if (!Directory.Exists(d)) continue;      // 没这个目录（还没装 MOD / 没装平台），不用测
                try
                {
                    string f = Path.Combine(d, "_write_test.tmp");
                    File.WriteAllText(f, "x");
                    File.Delete(f);
                }
                catch (Exception ex)
                {
                    Core.Log("WARN", "写不进 " + d + "：" + ex.Message);
                    return false;
                }
            }
            return true;
        }

        private static bool GameRunning()
        {
            try { return System.Diagnostics.Process.GetProcessesByName("left4dead2").Length > 0; }
            catch { return false; }
        }

        internal void ShowEnv()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("系统      : " + Environment.OSVersion.VersionString + (Environment.Is64BitOperatingSystem ? "  64 位" : "  32 位"));
            sb.AppendLine(".NET      : " + Environment.Version);
            bool admin = false;
            try
            {
                admin = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { }
            sb.AppendLine("管理员    : " + (admin ? "是" : "否（写系统目录、装运行库会受限）"));
            string root = txtGame.Text.Trim();
            sb.AppendLine("游戏可写  : " + (Core.TestGame(root) ? (TestWritable(root) ? "是" : "否（只读/占用/权限不足）") : "（还没选对目录）"));
            try
            {
                DriveInfo di = new DriveInfo(Path.GetPathRoot(root));
                sb.AppendLine("游戏盘剩余: " + (di.AvailableFreeSpace / 1073741824.0).ToString("N1") + " GB");
            }
            catch { }
            sb.AppendLine("U 盘根    : " + Core.UsbRoot);
            sb.AppendLine("日志目录  : " + Core.LogDir);
            sb.AppendLine("MOD 库    : " + Core.ModList.Count + " 个 vpk");
            MessageBox.Show(sb.ToString(), "环境自检");
            Core.Log("INFO", "环境自检：" + Environment.OSVersion.VersionString + " / .NET " + Environment.Version + " / 管理员=" + admin);
        }

        // 方案管理窗口复用主界面的分类清单（同一个 cboCat，显示与主界面完全一致）
        public List<CatChoice> CatChoices()
        {
            // 方案管理窗口要一个「大类 + 细分」的清单，跟以前分类下拉显示的一致
            List<CatChoice> r = new List<CatChoice>();
            r.Add(new CatChoice("", null, "(全部)"));
            foreach (string cat in Catalog.Cats)
            {
                int n = 0;
                Dictionary<string, int> subs = new Dictionary<string, int>(StringComparer.CurrentCulture);
                List<string> order = new List<string>();
                foreach (ModGroup g in Catalog.Groups)
                {
                    if (g.Cat != cat) continue;
                    n++;
                    if (!subs.ContainsKey(g.Sub)) { subs[g.Sub] = 0; order.Add(g.Sub); }
                    subs[g.Sub] = subs[g.Sub] + 1;
                }
                if (n == 0) continue;
                order.Sort(StringComparer.CurrentCulture);
                r.Add(new CatChoice(cat, null, cat + "（" + n + "）"));
                foreach (string sb in order) r.Add(new CatChoice(cat, sb, "    " + sb + "（" + subs[sb] + "）"));
            }
            return r;
        }

        public void ApplyProfileByName(string name)
        {
            Profile p = ProfileStore.Load(name);
            if (p == null) { MessageBox.Show("找不到方案「" + name + "」。" ); return; }
            LoadProfileIntoUi(p, true, false);
        }


        private void AppendLog(string line)
        {
            // 只排队、不等界面线程。
            // 原来用 Invoke：后台线程写一行日志就得等界面线程处理完。
            // 现在扫库有互斥锁（Core.ScanMods），界面线程点删除时要等后台那次扫描结束，
            // 后台又在等界面线程画日志 —— 两边互等，直接卡死。日志本来就不需要同步显示。
            SafePost(delegate
            {
                if (++logLines > 1200) { txtLog.Clear(); logLines = 1; logWarnings = 0; }
                if (line.IndexOf("] WARN", StringComparison.Ordinal) >= 0
                    || line.IndexOf("] FAIL", StringComparison.Ordinal) >= 0) logWarnings++;
                txtLog.AppendText(line + "\r\n");
                if (gLog != null)
                    gLog.Caption = logWarnings > 0 ? "日志 · " + logWarnings + " 条提醒" : "日志";
            });
        }

        /// <summary>把活排到界面线程，不等待（Invoke 是等，BeginInvoke 是排队）。</summary>
        private void SafePost(Action a)
        {
            try
            {
                if (IsDisposed || Disposing || !IsHandleCreated) return;
                if (InvokeRequired) BeginInvoke(a); else a();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        // 统一的“安全”调用：窗口已经关掉 / 句柄没了就直接放弃，别再抛 ObjectDisposedException
        private void SafeInvoke(Action a)
        {
            try
            {
                if (IsDisposed || Disposing || !IsHandleCreated) return;
                if (InvokeRequired) Invoke(a); else a();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void SafeBeginInvoke(Action a)
        {
            try
            {
                if (IsDisposed || Disposing || !IsHandleCreated) return;
                if (InvokeRequired) BeginInvoke(a); else a();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        // 后台跑一段活、同时保持界面能重绘；结果通过字段回传
        private T RunBusy<T>(string status, Func<T> work)
        {
            bgDone = false; bgResult = null; bgError = null;
            SetStatus(status);
            bar.Style = ProgressBarStyle.Marquee;
            Cursor = Cursors.WaitCursor;
            System.Threading.Thread th = new System.Threading.Thread(delegate()
            {
                try { bgResult = work(); }
                catch (Exception ex) { bgError = ex; }
                finally { bgDone = true; }
            });
            th.IsBackground = true;
            th.Start();
            while (!bgDone)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(40);
            }
            Cursor = Cursors.Default;
            bar.Style = ProgressBarStyle.Blocks;
            if (bgError != null)
            {
                Core.Log("FAIL", "后台任务出错: " + bgError.Message);
                // 以前这里只记日志，调用方继续把“空结果”当成成功。
                // 现在把异常交还给调用方，由界面明确显示失败，而不是继续走完成流程。
                throw new InvalidOperationException(bgError.Message, bgError);
            }
            return bgResult == null ? default(T) : (T)bgResult;
        }

        private void Log(string s) { Core.Log("INFO", s); }
        private void Log(string level, string s) { Core.Log(level, s); }

        private void SetBusy(bool busy)
        {
            // 这个方法会被后台线程调用，必须回到 UI 线程再改控件
            SafeInvoke(delegate
            {
                btnInstall.Enabled = !busy;
                btnRestore.Enabled = !busy;
                btnSwitch.Enabled = !busy;
                btnFind.Enabled = !busy;
                btnBrowse.Enabled = !busy;
                // 空闲时不摆一条空进度条：深色主题下它是一道白条，看着像没画完
                if (bar != null) bar.Visible = busy;
            });
        }

        private void RequestStopInstall()
        {
            if (Work.CancelRequested) return;
            Work.CancelRequested = true;
            btnCancel.Enabled = false;
            btnCancel.Text = "正在停止…";
            SetStatus("正在停止 · 当前步骤结束后不再继续");
            Core.Log("WARN", "用户请求停止后续安装；已写入的文件会保留，不会自动回滚");
        }

        private void DoSwitch()
        {
            string root = txtGame.Text.Trim();
            if (!Core.TestGame(root)) { MessageBox.Show("先选对游戏根目录。"); return; }
            AddonSwitchForm f = new AddonSwitchForm(root);
            f.ShowDialog(this);
            // 这个窗口现在也能删 MOD（真删）：删过就重扫，列表/树跟上
            if (f.Deleted > 0)
            {
                Log("OK", "已装 MOD 开关里删掉 " + f.Deleted + " 个，重新扫库");
                Core.ScanMods();
                RefreshMods();
                RefreshConflicts();
                if (treeView != null) RebuildViews();
            }
        }

        private void DoRestore()
        {
            string root = txtGame.Text.Trim();
            if (!Core.TestGame(root)) { MessageBox.Show("先选对游戏根目录。"); return; }
            if (!TestWritable(root))
            {
                MessageBox.Show("这个游戏目录写不进去（可能只读、被占用或权限不足）。\r\n可以试试以管理员身份运行，或换一个有写权限的目录。", "目录不可写");
                return;
            }
            if (GameRunning())
            {
                MessageBox.Show("游戏还开着，先完全退出再还原。", "游戏正在运行");
                return;
            }
            if (MessageBox.Show("彻底还原：删掉 L4N / Skeeto / MQCam / dxvk 写进去的文件，并删掉本次铺进 addons 的 MOD。\r\n\r\n注意：原版 exe / addonlist / autoexec 没有备份了，需要原版请用 Steam 校验文件。\r\n\r\n确定？", "确认彻底还原", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            Log("--- 彻底还原 ---");
            try
            {
                foreach (string l in Work.FullRestore(root)) Log("OK", l);
                Log("OK", "还原完成，现在进的是原版游戏");
            }
            catch (Exception ex) { Log("FAIL", "还原失败: " + ex.Message); }
        }

        /// <summary>
        /// 「应用改动」：把方案树里点出来的开关改动一次性写进 left4dead2\addonlist.txt。
        /// 只改这几行的 1/0，vpk 文件一个都不碰；写之前也不留副本。
        /// 不动内存里的库扫描结果，只把 AddonOn 对齐，所以点完立刻生效、不用重扫。
        /// </summary>
        internal bool ApplySwitches(Dictionary<string, bool> st)
        {
            if (st == null || st.Count == 0) return false;
            string root = txtGame.Text.Trim();
            if (!Core.TestGame(root)) { MessageBox.Show("这个目录里没有 left4dead2.exe，先选对游戏根目录。", "路径不对"); return false; }
            if (!TestWritable(root))
            {
                MessageBox.Show("这个游戏目录写不进去（可能只读、被占用或权限不足）。\r\n可以试试以管理员身份运行。", "目录不可写");
                return false;
            }
            int on = 0;
            foreach (KeyValuePair<string, bool> kv in st) if (kv.Value) on++;

            // 先数一下 addonlist 里原来有多少条、这次命中的有几条 —— 好让日志和状态栏说清楚
            // 「只动了这几条，别的没碰」。Write 本身也只改传进去的名字，不会重排、不会顺手改别的。
            int total = 0, hit = 0;
            try
            {
                Dictionary<string, bool> before = AddonList.Read(root);
                total = before.Count;
                foreach (string k in st.Keys) if (before.ContainsKey(k)) hit++;
            }
            catch { }
            int untouched = Math.Max(0, total - hit);

            try
            {
                AddonList.Write(root, st);
                Core.Log("OK", "应用开关改动 " + st.Count + " 处（开 " + on + "、关 " + (st.Count - on)
                    + "）：改值 " + hit + " 条、新增 " + (st.Count - hit) + " 条；"
                    + "addonlist 原有 " + total + " 条，其余 " + untouched + " 条原样没动。");

                // 只改了开关，文件没动：直接对齐内存里的值，省一次全库重扫
                foreach (ModGroup g in Catalog.Groups)
                    foreach (ModItem p in g.Parts)
                    {
                        bool v;
                        if (st.TryGetValue(p.Name, out v)) p.AddonOn = v;
                    }
                RefreshMods();
                RefreshConflicts(true);
                SetStatus((GameRunning() ? "已应用 " + st.Count + " 处（游戏开着，重启后才生效）" : "已应用 " + st.Count + " 处开关改动")
                    + " · 其它 " + untouched + " 条没动");
                return true;
            }
            catch (Exception ex)
            {
                Core.Log("FAIL", "应用开关改动失败: " + ex.Message);
                MessageBox.Show("写不进 addonlist.txt：" + ex.Message + "\r\n\r\n可以试试以管理员身份运行。", "出错了",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        /// <summary>
        /// 方案树「只装这里」：只把这个分支选中的 vpk 铺进游戏，别的位置一个都不动。
        /// 与整机装机的区别：不碰 L4N / dxvk / 素材包 / 参数配置，不改全局勾选，也不写整机清单。
        /// </summary>
        internal void DeploySlot(List<ModItem> sel, List<ModItem> off, string slotName, Dictionary<string, bool> extra, Action onDone)
        {
            string root = txtGame.Text.Trim();
            if (!Core.TestGame(root)) { MessageBox.Show("这个目录里没有 left4dead2.exe，先选对游戏根目录。", "路径不对"); return; }
            if (!TestWritable(root))
            {
                MessageBox.Show("这个游戏目录写不进去（可能只读、被占用或权限不足）。\r\n可以试试以管理员身份运行，或换一个有写权限的目录。", "目录不可写");
                return;
            }
            // 先分清：哪些已经在游戏根目录里（只切开关就够），哪些确实要拷进去。
            // 判「已装」只看同名 + 同字节大小，和列表「已装」列同一套判定；
            // 不用 Core.SameFile（它连 mtime 一起比），否则时间戳差一点就白拷一遍 ——
            // 已装的 MOD 只该动 addonlist 的 1/0，不该把文件重新搬一次。
            string addonsDir = Path.Combine(Path.Combine(root, "left4dead2"), "addons");
            HashSet<string> selNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<ModItem> inPlace = new List<ModItem>();
            List<ModItem> needCopy = new List<ModItem>();
            foreach (ModItem it in sel)
            {
                if (!selNames.Add(it.Name)) continue;                   // 同名只算一次
                string d = Path.Combine(addonsDir, it.Name);
                bool here = false;
                try { here = it.FromGame || (File.Exists(d) && new FileInfo(d).Length == it.Size); }
                catch { here = it.FromGame; }
                if (here) inPlace.Add(it); else needCopy.Add(it);
            }
            // 要关掉的旧 MOD：本位置里已经进游戏的都算（库里认识的和只在游戏目录里的都一样），
            // 但本次选中的不能关 —— 选中的永远赢。
            List<ModItem> offReal = new List<ModItem>();
            if (off != null)
                foreach (ModItem it in off)
                    if (!selNames.Contains(it.Name)) offReal.Add(it);

            // 纯切开关不动文件，游戏开着也能改（进游戏才读 addonlist）；要拷文件就必须先退游戏
            if (needCopy.Count > 0 && GameRunning())
            {
                MessageBox.Show("这次要往游戏目录里拷 " + needCopy.Count + " 个 vpk，游戏还开着，文件被占用。\r\n\r\n"
                    + "先把《求生之路2》完全退出（不是最小化）。\r\n"
                    + "如果只是想改开关，把勾选改成已经装好的那些，就不用退游戏。", "游戏正在运行");
                return;
            }

            long need = 0;
            foreach (ModItem it in needCopy) need += it.Size;
            string q = "只装「" + slotName + "」这一处：\r\n\r\n";
            if (inPlace.Count > 0)
                q += "· 已在游戏里：" + inPlace.Count + " 个 → 只改开关，不复制、不移动、不删除文件\r\n";
            if (needCopy.Count > 0)
                q += "· 需要拷进去：" + needCopy.Count + " 个，共 " + Core.Human(need) + "\r\n";
            if (offReal.Count > 0)
                q += "· 关掉这个位置之前装的 " + offReal.Count + " 个（文件留在原地，只把开关写成 0）\r\n";
            if (extra != null && extra.Count > 0)
                q += "· 另有 " + extra.Count + " 处开关改动一并写入 addonlist\r\n";
            q += "· 其它位置、L4N / dxvk / 参数配置一律不动\r\n";
            if (needCopy.Count == 0) q += "\r\n这次不复制任何文件，只写 addonlist.txt。";
            q += "\r\n\r\n继续吗？";
            if (MessageBox.Show(q, "只装这个位置", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            bool overwrite = !rdSkip.Checked;
            SetBusy(true);
            bar.Style = ProgressBarStyle.Marquee;
            SetStatus(needCopy.Count == 0 ? "正在改开关「" + slotName + "」…" : "正在装「" + slotName + "」…");
            Core.Log("STEP", "只装位置「" + slotName + "」：已在游戏里 " + inPlace.Count + " 个（只切开关，不动文件）"
                + (needCopy.Count > 0 ? "，需要拷入 " + needCopy.Count + " 个" : "，不复制任何文件")
                + (offReal.Count > 0 ? "，同时关掉旧的 " + offReal.Count + " 个" : ""));

            Thread th = new Thread(delegate()
            {
                try
                {
                    Work.Overwrite = overwrite;
                    // 只把「游戏里还没有的」交给复制；已在游戏里的一个都不碰，省得重新搬一遍
                    int copied = 0, skipped = inPlace.Count;
                    List<string> added = new List<string>(), failed = new List<string>();
                    if (needCopy.Count > 0)
                    {
                        int c2 = 0, s2 = 0;
                        List<string> a2, f2;
                        Work.CopyMods(root, needCopy, delegate(long done, long total)
                        {
                            int pct = total > 0 ? (int)(done * 1000 / total) : 0;
                            SetBar(pct);
                        }, out c2, out s2, out a2, out f2);
                        copied = c2; skipped += s2;
                        if (a2 != null) added.AddRange(a2);
                        if (f2 != null) failed.AddRange(f2);
                    }

                    Dictionary<string, bool> st = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    if (extra != null) foreach (KeyValuePair<string, bool> kv in extra) st[kv.Key] = kv.Value;
                    foreach (ModItem it in offReal) st[it.Name] = false;     // 先关旧的
                    foreach (ModItem it in sel) st[it.Name] = true;          // 选中的最后写，永远赢
                    AddonList.Write(root, st);
                    Core.Log("OK", "启用状态已写入");
                    if (failed.Count > 0)
                        Core.Log("FAIL", "复制失败 " + failed.Count + " 个：" + string.Join("、", failed.ToArray()));

                    SafeBeginInvoke(delegate
                    {
                        // 状态列是现读 addons + addonlist 得出的，装完必须重扫，否则列里还是旧状态
                        try { Core.ScanMods(); } catch (Exception exs) { Core.Log("WARN", "装完重扫失败: " + exs.Message); }
                        if (onDone != null) onDone();      // 界面上攒的开关改动已经连同这次一起写进去了
                        RefreshMods();
                        RefreshConflicts(true);
                        SetBusy(false);
                        SetBar(0);
                        SetStatus("就绪");
                        string tip = "「" + slotName + "」已更新：";
                        if (copied > 0) tip += "拷入 " + copied + " 个";
                        if (skipped > 0) tip += (copied > 0 ? "，" : "") + skipped + " 个已在游戏里，只切了开关";
                        if (offReal.Count > 0) tip += (copied > 0 || skipped > 0 ? "，" : "") + "关掉旧的 " + offReal.Count + " 个";
                        if (copied == 0) tip += "\r\n\r\n没有复制、移动或删除任何文件，只写了 addonlist.txt。";
                        Core.Log(failed.Count == 0 ? "OK" : "WARN", tip);
                        MessageBox.Show(tip
                            + (failed.Count > 0 ? "\r\n\r\n有 " + failed.Count + " 个复制失败，详见日志。" : "")
                            + "\r\n\r\n进游戏就能看到；这个位置的其它 MOD 没动。",
                            failed.Count > 0 ? "部分失败" : "只装这里 · 完成",
                            MessageBoxButtons.OK, failed.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
                    });
                }
                catch (Exception ex)
                {
                    Core.Log("FAIL", "只装位置失败: " + ex.Message);
                    SafeBeginInvoke(delegate
                    {
                        SetBusy(false); SetBar(0); SetStatus("就绪");
                        MessageBox.Show("装失败: " + ex.Message, "出错了", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    });
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        private void DoInstall()
        {
            string root = txtGame.Text.Trim();
            if (!Core.TestGame(root)) { MessageBox.Show("这个目录里没有 left4dead2.exe，先选对游戏根目录。", "路径不对"); return; }
            if (!TestWritable(root))
            {
                MessageBox.Show("这个游戏目录写不进去（可能只读、被占用或权限不足）。\r\n可以试试以管理员身份运行，或换一个有写权限的目录。", "目录不可写");
                return;
            }
            if (GameRunning())
            {
                MessageBox.Show("游戏还开着。先把《求生之路2》完全退出（不是最小化），否则 exe 和 dll 被占用，装不进去。", "游戏正在运行");
                return;
            }
            List<ModItem> sel = Selected();
            bool wantL4N = chkL4N.Checked;
            bool wantDxvk = chkDxvk.Checked;
            bool wantSkeeto = chkSkeeto.Checked;
            bool wantAsset = chkAsset.Checked;
            bool wantMqcam = chkMqcam.Checked;
            // 唯一真依赖：MQCam 跑在 L4N 的 neko 框架里。别的（Skeeto / dxvk / MOD）都不需要 L4N
            if (wantMqcam && !wantL4N)
            {
                wantL4N = true;
                Core.Log("WARN", "MQCam 要用 L4N 的 neko 框架，这次把 L4N 一起装上");
            }
            bool wantLaunch = chkLaunch.Checked;
            bool wantCfg = chkCfg.Checked;
            string launchVal = LaunchArgs.ForWrite(txtLaunch.Text);
            Core.StrictVerify = chkStrict.Checked;
            if (!wantL4N && !wantDxvk && !wantSkeeto && !wantMqcam && sel.Count == 0 && !wantLaunch && !wantAsset && !wantCfg)
            { MessageBox.Show("什么都没勾。"); return; }
            List<string> deep = null;
            if (sel.Count >= 2)
            {
                SetBusy(true);
                try
                {
                    deep = RunBusy<List<string>>("正在检查 " + sel.Count + " 个 MOD 有没有文件重叠…", delegate { return ConflictCheck.DeepAll(sel); });
                }
                catch (Exception ex)
                {
                    Core.Log("FAIL", "冲突检查失败: " + ex.Message);
                    MessageBox.Show("安装前的冲突检查失败，这次没有开始安装。\r\n\r\n"
                        + ex.Message + "\r\n\r\n详细信息见日志。", "冲突检查失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                finally { SetBusy(false); }
            }
            if (deep != null && deep.Count > 0)
            {
                string msg = string.Join("\r\n", deep.ToArray()) + "\r\n\r\n仍然继续装吗？";
                if (MessageBox.Show(msg, "这些 MOD 有文件重叠", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
                foreach (string l in deep) Core.Log("WARN", l);
            }

            // 装之前先看目标盘放不放得下
            long need = 0;
            foreach (ModItem it in sel) need += it.Size;
            try
            {
                DriveInfo di = new DriveInfo(Path.GetPathRoot(root));
                if (di.AvailableFreeSpace < need + 300L * 1024 * 1024)
                {
                    if (MessageBox.Show("目标盘只剩 " + Core.Human(di.AvailableFreeSpace) + "，这次要铺 " + Core.Human(need) + "。\r\n空间可能不够，仍然继续吗？", "空间可能不足", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
                }
            }
            catch { }
            Work.Overwrite = !rdSkip.Checked;          // 所有 Install* 都读这个开关
            Core.Cfg.GameRoot = root;
            Core.Cfg.L4N = wantL4N;
            Core.Cfg.Dxvk = wantDxvk;
            Core.Cfg.Skeeto = wantSkeeto;
            Core.Cfg.AssetPack = wantAsset;
            Core.Cfg.Mqcam = wantMqcam;
            Core.Cfg.Launch = wantLaunch;
            Core.Cfg.CfgFile = wantCfg;
            Core.Cfg.Overwrite = !rdSkip.Checked;
            Core.Cfg.TweakSet = TweakName();
            Core.Cfg.LaunchOptions = launchVal;
            Core.Cfg.Recent.RemoveAll(delegate(string s) { return string.Equals(s, root, StringComparison.OrdinalIgnoreCase); });
            Core.Cfg.Recent.Insert(0, root);
            while (Core.Cfg.Recent.Count > 6) Core.Cfg.Recent.RemoveAt(Core.Cfg.Recent.Count - 1);
            Core.Cfg.Mods = new List<string>();
            foreach (ModItem it in sel) Core.Cfg.Mods.Add(it.Key);
            Core.SaveConfig();

            int stepTotal = (wantL4N ? 1 : 0) + (wantDxvk ? 1 : 0) + (wantSkeeto ? 1 : 0)
                          + (wantAsset ? 1 : 0) + (wantCfg ? 1 : 0) + (wantMqcam ? 1 : 0)
                          + (sel.Count > 0 ? 1 : 0) + (wantLaunch ? 1 : 0);
            int stepDone = 0;
            bool canceled = false;
            List<string> failedSteps = new List<string>();
            Action<string> BeginStep = delegate(string what)
            {
                stepDone++;
                SetStatus("第 " + stepDone + "/" + stepTotal + " 步 · " + what);
            };

            Work.CancelRequested = false;
            btnCancel.Enabled = true;
            btnCancel.Text = "停止剩余";
            btnCancel.Visible = true;
            SetBusy(true);
            bar.Style = ProgressBarStyle.Marquee;
            lblStatus.Text = "正在准备…";
            Thread th = new Thread(delegate()
            {
                try
                {
                    Core.Log("STEP", "开始写入");
                    Core.Log("INFO", Work.Overwrite ? "模式：覆盖重装（已装的也全部重写一遍）" : "模式：跳过已装（只写缺的和不一样的）");
                    if (wantL4N)
                    {
                        Core.Log("STEP", "装 L4N");
                        BeginStep("正在装 L4N…");
                        if (!Work.InstallL4N(root, delegate(int done, int total) { SetBar(total > 0 ? done * 1000 / total : 0); }))
                            failedSteps.Add("L4N 平台");
                        if (Work.CancelRequested) canceled = true;
                    }
                    if (!canceled && wantDxvk)
                    {
                        Core.Log("STEP", "装 dxvk");
                        BeginStep("正在装 dxvk…");
                        if (!Work.InstallDxvk(root)) failedSteps.Add("dxvk 防闪退");
                        if (Work.CancelRequested) canceled = true;
                    }
                    if (!canceled && wantSkeeto)
                    {
                        Core.Log("STEP", "装 Skeeto");
                        BeginStep("正在装 Skeeto…");
                        if (!Work.InstallSkeeto(root)) failedSteps.Add("Skeeto 增强框架");
                        if (Work.CancelRequested) canceled = true;
                    }
                    string assetName = null;
                    if (!canceled && wantAsset)
                    {
                        Core.Log("STEP", "装素材包");
                        BeginStep("正在复制素材包…");
                        assetName = Work.InstallAssetPack(root);
                        if (assetName == null) failedSteps.Add("Skeeto 素材包");
                        if (Work.CancelRequested) canceled = true;
                    }
                    if (!canceled && wantCfg)
                    {
                        string tw = TweakName();
                        if (tw.Length > 0 && Tweaks.Exists(tw))
                        {
                            Core.Log("STEP", "写参数配置「" + tw + "」");
                            BeginStep("正在写参数配置…");
                            List<string> twLines;
                            string twErr = Tweaks.Apply(root, tw, out twLines);
                            if (twErr != null)
                            {
                                Core.Log("FAIL", "参数配置没写进去: " + twErr);
                                failedSteps.Add("参数配置");
                            }
                        }
                        else
                        {
                            Core.Log("STEP", "搬 autoexec.cfg（没选参数存档，走旧做法）");
                            BeginStep("正在搬 autoexec.cfg…");
                            if (!Work.InstallCfg(root)) failedSteps.Add("参数配置（autoexec.cfg）");
                        }
                        if (Work.CancelRequested) canceled = true;
                    }
                    if (!canceled && wantMqcam)
                    {
                        Core.Log("STEP", "装 MQCam");
                        BeginStep("正在装 MQCam…");
                        if (!Work.InstallMqcam(root)) failedSteps.Add("MQCam 相机");
                        if (Work.CancelRequested) canceled = true;
                    }
                    List<string> added = new List<string>();
                    if (!canceled && sel.Count > 0)
                    {
                        Core.Log("STEP", "铺 MOD（" + sel.Count + " 个）");
                        BeginStep("正在铺 MOD…");
                        int finalFailed = 0;
                        List<ModItem> todo = new List<ModItem>(sel);
                        for (int attempt = 0; attempt <= 2; attempt++)
                        {
                            int copied, skipped;
                            List<string> addedThis, failed;
                            Work.CopyMods(root, todo, delegate(long done, long total)
                            {
                                int pct = total > 0 ? (int)(done * 1000 / total) : 0;
                                SetBar(pct);
                            }, out copied, out skipped, out addedThis, out failed);
                            if (addedThis.Count > 0) added.AddRange(addedThis);
                            finalFailed = failed.Count;
                            if (Work.CancelRequested) { canceled = true; break; }
                            if (failed.Count == 0 || attempt == 2) break;
                            List<ModItem> retryList = new List<ModItem>();
                            foreach (ModItem it in todo) if (failed.Contains(it.Name)) retryList.Add(it);
                            if (retryList.Count == 0) break;
                            string q = "有 " + retryList.Count + " 个 MOD 复制失败，重试一次？（最多重试 2 次）";
                            bool yes = false;
                            Invoke(new Action(delegate { yes = MessageBox.Show(q, "复制失败", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes; }));
                            if (!yes) break;
                            Core.Log("WARN", "重试 " + retryList.Count + " 个失败的 MOD");
                            todo = retryList;
                        }
                        if (finalFailed > 0) failedSteps.Add("MOD 复制（" + finalFailed + " 个）");
                        // 停止时不改 addonlist：已经复制完的文件先留在 addons 里，但不把它们突然启用。
                        if (!canceled)
                        {
                            // 换整套：只翻 addonlist 的开关（这次的打开、上一套的关上），文件一个都不动
                            if (chkSwap.Checked)
                            {
                                Core.Log("STEP", "换整套：把不在这套里的 MOD 关掉");
                                SetStatus("正在切换 MOD 开关…");
                                int onN, offN, keepN;
                                Work.SwitchAddons(root, sel, assetName, out onN, out offN, out keepN);
                            }
                            else
                            {
                                Dictionary<string, bool> st = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                                foreach (ModItem it in sel) st[it.Name] = true;
                                if (assetName != null) st[assetName] = true;
                                AddonList.Write(root, st);
                                Core.Log("OK", "启用状态已写入");
                            }
                        }
                    }
                    else if (!canceled && chkSwap.Checked)
                    {
                        // 一个 MOD 都没勾、但开着「换整套」：等于这套不要 MOD，全关掉
                        Core.Log("STEP", "换整套：这套没有 MOD，把旧的都关掉");
                        int onN, offN, keepN;
                        Work.SwitchAddons(root, sel, assetName, out onN, out offN, out keepN);
                    }
                    else if (!canceled && assetName != null)
                    {
                        Dictionary<string, bool> st2 = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                        st2[assetName] = true;
                        AddonList.Write(root, st2);
                    }
                    if (!canceled && wantLaunch)
                    {
                        Core.Log("STEP", "写 Steam 启动项");
                        BeginStep("正在写 Steam 启动项…");
                        string launchMsg = Work.WriteLaunchOptions(launchVal);
                        Core.Log("INFO", launchMsg);
                        if (launchMsg.IndexOf("没写成功", StringComparison.Ordinal) >= 0)
                            failedSteps.Add("Steam 启动项");
                        if (Work.CancelRequested) canceled = true;
                    }
                    if (canceled || Work.CancelRequested)
                    {
                        SetStatus("已停止 · 已复制文件保留，开关未写入");
                        Core.Log("WARN", "安装已停止：没有继续后续步骤，也没有改 addonlist；已写入的文件保留。");
                        SafeBeginInvoke(delegate()
                        {
                            bool summary = MessageBox.Show(
                                "安装已停止。\r\n\r\n"
                                + "· 已经写完的文件保留在游戏目录里\r\n"
                                + "· 后续组件没有继续安装\r\n"
                                + "· addonlist 的开关没有改动\r\n\r\n"
                                + "这不是“还原”。要清理，请用「一键还原」或手动处理。\r\n"
                                + "需要继续时，可以再点一次「开始装」。",
                                "安装已停止", MessageBoxButtons.OK, MessageBoxIcon.Information) == DialogResult.OK;
                            try { Core.ScanMods(); } catch { }
                            RefreshMods();
                            RefreshConflicts(true);
                        });
                        return;
                    }
                    Core.Log("STEP", "逐项核对");
                    Work.Verify(root);
                    Work.WriteManifest(root, added);
                    if (failedSteps.Count == 0)
                    {
                        SetStatus("完成");
                        Core.Log("OK", "全部完成。进游戏即可，开关在「已装 MOD 开关」里调。");
                    }
                    else
                    {
                        SetStatus("部分完成 · " + failedSteps.Count + " 项需要处理");
                        Core.Log("FAIL", "安装结束，但有 " + failedSteps.Count + " 项失败：" + string.Join("、", failedSteps.ToArray()));
                    }
                    SafeBeginInvoke(delegate
                    {
                        UpdateTweakTip();
                        // 装完必须重扫：状态列（已装·开 / 已装·关）是现读 addons 和 addonlist 得出的，
                        // 不重扫的话回到列表看到的还是装机前的旧状态
                        try { Core.ScanMods(); }
                        catch (Exception exs) { Core.Log("WARN", "装完重扫失败: " + exs.Message); }
                        RefreshMods();
                        RefreshConflicts(true);
                        if (failedSteps.Count > 0)
                        {
                            // 有失败就不引导启动：先让用户看到“哪些没成”，而不是一句笼统的“装好了”。
                            MessageBox.Show(
                                "安装结束，但不是全部成功。\r\n\r\n没完成的 " + failedSteps.Count + " 项：\r\n  · "
                                + string.Join("\r\n  · ", failedSteps.ToArray())
                                + "\r\n\r\n已经写入的文件会保留；请查看下面的日志，处理后可以再装一次。\r\n"
                                + "这次不会自动启动游戏。",
                                "部分完成 · 需要处理", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                        bool go = MessageBox.Show(
                            "装好了。\r\n\r\n现在启动游戏吗？\r\n\r\n"
                            + "启动就是校验 —— 不用跑两遍：\r\n"
                            + "· 起游戏后顺带盯 2 分钟的崩溃记录\r\n"
                            + "· 没崩 → 这次装的没问题，游戏留着直接玩\r\n"
                            + "· 崩了 → 弹提示，说明这次至少有一个坏包\r\n\r\n"
                            + "全程不改任何文件。不想现在玩就选「否」，游戏不会开。",
                            "完成 · 启动游戏", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
                        if (!go) return;
                        Core.Log("STEP", "启动游戏（同时盯崩溃记录，不再二次启动）");
                        SetBusy(true);
                        SetStatus("正在启动游戏…");
                        Thread thTest = new Thread(delegate()
                        {
                            SelfTest.Result sr = null;
                            bool released = false;
                            Action<string> onStep = delegate(string s)
                            {
                                SetStatus(s);
                                // 游戏一旦起来就放开按钮，别让它一直锁着
                                if (!released) { released = true; SetBusy(false); }
                            };
                            try
                            {
                                sr = SelfTest.Run(root, 120, sel.Count, onStep, true,
                                    delegate(SelfTest.Result cr)
                                    {
                                        Core.Log("FAIL", "玩着玩着崩了: " + cr.Message.Replace("\r\n", " "));
                                        SafeBeginInvoke(delegate
                                        {
                                            MessageBox.Show(cr.Message, "游戏崩了", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                                        });
                                    });
                            }
                            catch (Exception ex3) { Core.Log("FAIL", "启动出错: " + ex3.Message); }
                            SetBusy(false);
                            SetBar(0);
                            SetStatus("就绪");
                            if (sr == null) return;

                            // 自检前置条件不满足（Steam 没开 / 没 reports 目录 / 起不来）：
                            // 别把「启动游戏」一起吞掉，退化成就直接开
                            if (sr.Kind == "nosteam" || sr.Kind == "noreport" || sr.Kind == "launchfail")
                            {
                                Core.Log("WARN", "这次盯不了崩溃记录（" + sr.Kind + "），改为直接启动游戏");
                                StartGameDirect(root);
                                return;
                            }
                            if (sr.Kind == "busy")
                            {
                                Core.Log("INFO", "游戏已经开着，不用再启动");
                                SetStatus("游戏已经开着");
                                return;
                            }

                            if (appliedProfile.Length > 0 && (sr.Kind == "crash" || sr.Kind == "clean"))
                                ProfileStore.SetSelfTest(appliedProfile, sr.Kind == "crash" ? "FAIL" : "PASS", sr.Seconds);
                            Core.Log(sr.Kind == "crash" ? "FAIL" : "OK", "启动: " + sr.Message.Replace("\r\n", " "));

                            // 自己关掉游戏就别再弹框打扰
                            if (sr.Kind == "exited" || sr.Kind == "clean")
                            {
                                if (sr.Kind == "clean") SetStatus("游戏已启动 · 后台盯着崩溃记录");
                                return;
                            }
                            SafeBeginInvoke(delegate
                            {
                                MessageBox.Show(sr.Message, "启动结果 - " + sr.Kind,
                                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            });
                        });
                        thTest.IsBackground = true;
                        thTest.Start();
                    });
                }
                catch (Exception ex)
                {
                    Core.Log("FAIL", "中断: " + ex.Message);
                    SetStatus("失败 · 查看日志");
                }
                finally
                {
                    Work.CancelRequested = false;
                    SetBusy(false);
                    SetBar(0);
                    SafeBeginInvoke(delegate()
                    {
                        btnCancel.Visible = false;
                        btnCancel.Enabled = true;
                        btnCancel.Text = "停止剩余";
                    });
                    // 不再无条件改回“就绪”：失败、部分完成、完成都应留在状态栏上，
                    // 这是用户离开安装按钮后唯一能确认结果的常驻反馈。
                }
            });
            th.IsBackground = true;
            th.SetApartmentState(ApartmentState.STA);
            th.Start();
        }

        /// <summary>直接开游戏：先走 Steam，起不来就直接跑 exe。盯不了崩溃记录时兜底用。</summary>
        private void StartGameDirect(string root)
        {
            try
            {
                System.Diagnostics.Process.Start("steam://rungameid/550");
                Core.Log("OK", "已通过 Steam 启动游戏（这次没盯崩溃记录）");
                SetStatus("游戏已启动（这次没盯崩溃记录）");
                return;
            }
            catch (Exception ex) { Core.Log("WARN", "steam:// 启动失败: " + ex.Message); }
            try
            {
                string exe = Path.Combine(root, "left4dead2.exe");
                if (!File.Exists(exe))
                {
                    SafeBeginInvoke(delegate
                    {
                        MessageBox.Show("找不到 left4dead2.exe，请在 Steam 里手动开游戏。", "启动失败",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    });
                    return;
                }
                System.Diagnostics.Process.Start(exe, "-novid");
                Core.Log("OK", "已直接启动 left4dead2.exe（这次没盯崩溃记录）");
                SetStatus("游戏已启动（这次没盯崩溃记录）");
            }
            catch (Exception ex2)
            {
                Core.Log("WARN", "直接启动失败: " + ex2.Message);
                SafeBeginInvoke(delegate
                {
                    MessageBox.Show("游戏没启动：\r\n" + ex2.Message + "\r\n\r\n请在 Steam 里手动开。", "启动失败",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                });
            }
        }

        private void SetStatus(string s)
        {
            SafeInvoke(delegate
            {
                lblStatus.Text = s;
                // 结果状态不只靠文字：失败/停止用强调色，部分完成用警告色，过程信息保持次级色。
                if (s.IndexOf("失败", StringComparison.Ordinal) >= 0
                    || s.IndexOf("停止", StringComparison.Ordinal) >= 0
                    || s.IndexOf("目录不对", StringComparison.Ordinal) >= 0)
                    lblStatus.ForeColor = Theme.Accent;
                else if (s.IndexOf("部分完成", StringComparison.Ordinal) >= 0
                    || s.IndexOf("需要处理", StringComparison.Ordinal) >= 0)
                    lblStatus.ForeColor = Theme.Warn;
                else
                    lblStatus.ForeColor = Theme.Ink2;
            });
        }

        private void SetBar(int permille)
        {
            SafeInvoke(delegate
            {
                if (bar.Style != ProgressBarStyle.Continuous) { bar.Style = ProgressBarStyle.Continuous; bar.Maximum = 1000; }
                int v = permille < 0 ? 0 : (permille > 1000 ? 1000 : permille);
                bar.Value = v;
            });
        }
    }

    public class ProfileManageForm : Form
    {
        private ListBox lb;
        private ListView clb;   // 方案内容清单：带勾选框 + 按大类分组
        private TextBox txtSearch;
        private CheckBox chkOnlyPicked;
        private ComboBox cboCat;
        private Label lblTitle2, lblInfo;
        private MainForm owner;

        private Profile current;
        private readonly List<string> rowKeys = new List<string>();
        private readonly Dictionary<string, ListViewGroup> groupOf = new Dictionary<string, ListViewGroup>(StringComparer.Ordinal);
        private HashSet<string> picked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> saved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // 磁盘上那一版，用来判断「有没有改动」
        private bool filling = false;
        private bool dirty = false;
        private bool hintOn = true;
        private bool switchingSelection = false;
        private int lastSelIndex = -1;
        private const string HintText = "搜 MOD 名字或分类…";

        public ProfileManageForm(MainForm o)
        {
            owner = o;
            Text = "方案管理";
            ClientSize = new Size(820, 520);
            MinimumSize = new Size(760, 470);
            BackColor = Theme.Bg;
            Font = Theme.UI;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            SuspendLayout();

            // ---- 左：方案 ----
            Label l1 = new Label(); l1.Text = "方案"; l1.SetBounds(12, 12, 60, 20); Theme.Dress(l1); Controls.Add(l1);
            lb = new ListBox(); lb.SetBounds(12, 34, 220, 386);
            lb.BorderStyle = BorderStyle.FixedSingle; lb.BackColor = Theme.Surface; lb.ForeColor = Theme.Ink;
            lb.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
            lb.SelectedIndexChanged += delegate { GuardedSelect(); };
            lb.DoubleClick += delegate { ApplySel(); };
            Controls.Add(lb);
            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (!dirty) return;
                if (MessageBox.Show(this, "当前方案还有改动没保存。关闭会丢掉这些改动，确定关闭吗？",
                    "未保存的改动", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                { e.Cancel = true; return; }
                dirty = false;
            };

            FlatButton ap = new FlatButton(); ap.Text = "套用"; ap.Primary = true; ap.SetBounds(12, 428, 68, Theme.H);
            ap.Anchor = AnchorStyles.Bottom | AnchorStyles.Left; ap.Click += delegate { ApplySel(); }; Controls.Add(ap);
            FlatButton rn = new FlatButton(); rn.Text = "改名"; rn.SetBounds(86, 428, 68, Theme.H);
            rn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left; rn.Click += delegate { RenameSel(); }; Controls.Add(rn);
            FlatButton de = new FlatButton(); de.Text = "删除"; de.SetBounds(160, 428, 68, Theme.H);
            de.Anchor = AnchorStyles.Bottom | AnchorStyles.Left; de.Click += delegate { DeleteSel(); }; Controls.Add(de);
            FlatButton cp = new FlatButton(); cp.Text = "另存为…"; cp.SetBounds(12, 462, 104, Theme.H);
            cp.Anchor = AnchorStyles.Bottom | AnchorStyles.Left; cp.Click += delegate { CopySel(); }; Controls.Add(cp);
            FlatButton im = new FlatButton(); im.Text = "导入…"; im.SetBounds(124, 462, 96, Theme.H);
            im.Anchor = AnchorStyles.Bottom | AnchorStyles.Left; im.Click += delegate { ImportFile(); }; Controls.Add(im);

            // ---- 右：方案内容 ----
            lblTitle2 = new Label(); lblTitle2.Text = "这套会装什么（勾/取消 = 加减 MOD）"; lblTitle2.SetBounds(248, 12, 320, 20);
            lblTitle2.Anchor = AnchorStyles.Top | AnchorStyles.Left; Theme.Dress(lblTitle2); Controls.Add(lblTitle2);

            cboCat = new ComboBox(); cboCat.SetBounds(248, 34, 160, 24);
            cboCat.DropDownStyle = ComboBoxStyle.DropDownList;
            cboCat.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            Theme.Dress(cboCat);
            foreach (CatChoice cc in owner.CatChoices()) cboCat.Items.Add(cc);
            if (cboCat.Items.Count > 0) cboCat.SelectedIndex = 0;
            cboCat.SelectedIndexChanged += delegate { RebuildRows(); };
            Controls.Add(cboCat);

            txtSearch = new TextBox(); txtSearch.SetBounds(416, 34, 190, 24);
            txtSearch.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Theme.Dress(txtSearch);
            txtSearch.Text = HintText; txtSearch.ForeColor = Theme.Ink3;
            txtSearch.GotFocus += delegate
            {
                if (hintOn) { hintOn = false; txtSearch.Text = ""; txtSearch.ForeColor = Theme.Ink; }
            };
            txtSearch.LostFocus += delegate
            {
                if (txtSearch.Text.Trim().Length == 0) { hintOn = true; txtSearch.Text = HintText; txtSearch.ForeColor = Theme.Ink3; }
            };
            txtSearch.TextChanged += delegate { if (!hintOn) RebuildRows(); };
            Controls.Add(txtSearch);

            FlatButton selAll = new FlatButton(); selAll.Text = "全选"; selAll.SetBounds(248, 62, 64, Theme.H);
            selAll.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            selAll.Click += delegate { SetFiltered(true); }; Controls.Add(selAll);
            FlatButton selNone = new FlatButton(); selNone.Text = "全不选"; selNone.SetBounds(318, 62, 72, Theme.H);
            selNone.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            selNone.Click += delegate { SetFiltered(false); }; Controls.Add(selNone);

            chkOnlyPicked = new CheckBox(); chkOnlyPicked.Text = "只看已选"; chkOnlyPicked.SetBounds(714, 36, 94, 22);
            chkOnlyPicked.Checked = true;        // 先给「这套里有什么」，要加东西再取消勾
            chkOnlyPicked.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            Theme.Dress(chkOnlyPicked);
            chkOnlyPicked.CheckedChanged += delegate { RebuildRows(); };
            Controls.Add(chkOnlyPicked);

            ToolTip tips = new ToolTip();
            tips.SetToolTip(selAll, "把当前筛选出来的行全部勾上（算未保存改动，点「保存」才写回方案）");
            tips.SetToolTip(selNone, "把当前筛选出来的行全部取消（同样要点「保存」）");
            tips.SetToolTip(txtSearch, "按 MOD 文件名或分类筛选；配合「只看已选」可以只看这个方案里的");

            // 方案内容：换成带分组标题的 ListView —— 一长串「名字 · 分类/细分」看着累，
            // 分组后「这套里地图几个、枪械几个」一眼就数得出来
            clb = new ListView();
            clb.SetBounds(248, 94, 560, 326);
            clb.View = View.Details;
            clb.CheckBoxes = true;
            clb.FullRowSelect = true;
            clb.ShowGroups = true;
            clb.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            clb.ShowItemToolTips = true;
            clb.BorderStyle = BorderStyle.FixedSingle; clb.BackColor = Theme.Surface; clb.ForeColor = Theme.Ink;
            clb.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            ImageList pad2 = new ImageList(); pad2.ImageSize = new Size(1, 22);
            clb.SmallImageList = pad2;
            clb.Columns.Add("名称", 300);
            clb.Columns.Add("细分", 128);
            clb.Columns.Add("套装", 108);
            clb.ItemChecked += OnItemChecked;
            Controls.Add(clb);

            lblInfo = new Label(); lblInfo.SetBounds(248, 428, 380, 20); lblInfo.Text = "";
            lblInfo.ForeColor = Theme.Ink3; lblInfo.Font = Theme.UISmall;
            lblInfo.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(lblInfo);

            FlatButton sv = new FlatButton(); sv.Text = "保存"; sv.Primary = true; sv.SetBounds(636, 452, 80, Theme.H);
            sv.Anchor = AnchorStyles.Bottom | AnchorStyles.Right; sv.Click += delegate { SaveScheme(); }; Controls.Add(sv);
            FlatButton rl = new FlatButton(); rl.Text = "还原"; rl.SetBounds(724, 452, 80, Theme.H);
            rl.Anchor = AnchorStyles.Bottom | AnchorStyles.Right; rl.Click += delegate { RestoreSel(); }; Controls.Add(rl);
            FlatButton ex = new FlatButton(); ex.Text = "导出…"; ex.SetBounds(636, 486, 80, Theme.H);
            ex.Anchor = AnchorStyles.Bottom | AnchorStyles.Right; ex.Click += delegate { ExportSel(); }; Controls.Add(ex);
            FlatButton ea = new FlatButton(); ea.Text = "全部导出…"; ea.SetBounds(716, 486, 92, Theme.H);
            ea.Anchor = AnchorStyles.Bottom | AnchorStyles.Right; ea.Click += delegate { ExportAll(); }; Controls.Add(ea);
            FlatButton cl = new FlatButton(); cl.Text = "关闭"; cl.SetBounds(716, 424, 92, Theme.H);
            cl.Anchor = AnchorStyles.Bottom | AnchorStyles.Right; cl.DialogResult = DialogResult.Cancel; Controls.Add(cl);
            CancelButton = cl;

            ResumeLayout(true);
            LoadList();
        }

        private void LoadList()
        {
            Profile keep = Sel();
            string keepName = keep == null ? null : keep.Name;
            lb.Items.Clear();
            List<Profile> items = ProfileStore.LoadAll();
            foreach (Profile p in items) lb.Items.Add(p);
            if (lb.Items.Count == 0)
            {
                lb.Items.Add("还没有方案");
                clb.Items.Clear(); rowKeys.Clear(); lblInfo.Text = ""; current = null;
                return;
            }
            int idx = -1;
            if (keepName != null)
                for (int i = 0; i < items.Count; i++) if (items[i].Name == keepName) { idx = i; break; }
            lb.SelectedIndex = idx < 0 ? 0 : idx;
        }

        private Profile Sel()
        {
            return lb.SelectedItem as Profile;
        }

        private void GuardedSelect()
        {
            if (switchingSelection || lb.SelectedIndex < 0) return;
            if (dirty && lb.SelectedIndex != lastSelIndex)
            {
                if (MessageBox.Show(this, "当前方案还有改动没保存。切换会丢掉这些改动，继续吗？",
                    "未保存的改动", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                {
                    switchingSelection = true;
                    lb.SelectedIndex = lastSelIndex;
                    switchingSelection = false;
                    return;
                }
            }
            LoadSel();
            lastSelIndex = lb.SelectedIndex;
        }

        private void RestoreSel()
        {
            if (dirty && MessageBox.Show(this, "还原会丢掉当前未保存的改动，继续吗？",
                "未保存的改动", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            LoadSel();
            lastSelIndex = lb.SelectedIndex;
        }

        private void LoadSel()
        {
            Profile p = Sel();
            if (p == null) { current = null; clb.Items.Clear(); rowKeys.Clear(); lblInfo.Text = ""; return; }
            Profile fresh = ProfileStore.Load(p.Name);
            current = fresh == null ? p : fresh;
            picked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string k in current.Mods) picked.Add(k);
            saved = new HashSet<string>(current.Mods, StringComparer.OrdinalIgnoreCase);
            dirty = false;
            MarkTitle();
            RebuildRows();
        }

        internal int DebugRows() { return clb.Items.Count; }

        private void MarkTitle()
        {
            Text = "方案管理" + (dirty ? " *" : "") + (current == null ? "" : "  ——  " + current.Name);
        }

        private string TextOf(ModItem it)
        {
            string cat = it.Cat + (string.IsNullOrEmpty(it.Sub) ? "" : "/" + it.Sub);
            ModGroup g;
            if (Catalog.GroupByKey.TryGetValue(it.GroupKey, out g) && g.IsSet)
                return it.Name + "    ·  " + cat + "    ·  套装：" + g.Name;
            return it.Name + "    ·  " + cat;
        }

        private void RebuildRows()
        {
            filling = true;
            try
            {
                clb.BeginUpdate();
                clb.Items.Clear();
                clb.Groups.Clear();
                groupOf.Clear();
                rowKeys.Clear();
                string q = hintOn || txtSearch.Text == null ? "" : txtSearch.Text.Trim();
                CatChoice cc = cboCat.SelectedItem as CatChoice;
                string fcat = cc == null ? "" : cc.Cat;
                string fsub = cc == null ? null : cc.Sub;
                bool only = chkOnlyPicked.Checked;

                List<ModItem> hit = new List<ModItem>();
                foreach (ModItem it in Core.ModList)
                {
                    if (only && !picked.Contains(it.Key)) continue;
                    if (fcat.Length > 0 && !string.Equals(it.Cat, fcat, StringComparison.Ordinal)) continue;
                    if (fsub != null && !string.Equals(it.Sub, fsub, StringComparison.Ordinal)) continue;
                    if (q.Length > 0 && !MatchItem(it, q)) continue;
                    hit.Add(it);
                }
                // 排序按大类固定顺序，别让「地图」跑到「枪械」后面
                hit.Sort(delegate(ModItem a, ModItem b)
                {
                    int x = Array.IndexOf(Catalog.Cats, a.Cat), y = Array.IndexOf(Catalog.Cats, b.Cat);
                    if (x < 0) x = 99; if (y < 0) y = 99;
                    int c = x.CompareTo(y);
                    if (c != 0) return c;
                    c = string.Compare(a.Sub, b.Sub, StringComparison.CurrentCulture);
                    if (c != 0) return c;
                    return string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
                });

                // 分组标题：大类 + 这套选了几个 / 这类一共几个（按整库算，切「只看已选」也不会变成 0 / 0）
                Dictionary<string, int> total = new Dictionary<string, int>(StringComparer.Ordinal);
                Dictionary<string, int> take = new Dictionary<string, int>(StringComparer.Ordinal);
                HashSet<string> showCats = new HashSet<string>(StringComparer.Ordinal);
                foreach (ModItem it in hit) showCats.Add(it.Cat);
                foreach (ModItem it in Core.ModList)
                {
                    if (!total.ContainsKey(it.Cat)) { total[it.Cat] = 0; take[it.Cat] = 0; }
                    total[it.Cat] = total[it.Cat] + 1;
                    if (picked.Contains(it.Key)) take[it.Cat] = take[it.Cat] + 1;
                }
                foreach (string c in Catalog.Cats)
                {
                    if (!total.ContainsKey(c) || !showCats.Contains(c)) continue;
                    ListViewGroup vg = new ListViewGroup(c + "　　" + take[c] + " / " + total[c] + " 个已选");
                    vg.HeaderAlignment = HorizontalAlignment.Left;
                    clb.Groups.Add(vg);
                    groupOf[c] = vg;
                }

                foreach (ModItem it in hit)
                {
                    string showName = it.Name.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase)
                        ? it.Name.Substring(0, it.Name.Length - 4) : it.Name;
                    ListViewItem li = new ListViewItem(showName);
                    li.ToolTipText = it.Name;
                    ListViewGroup vg;
                    if (groupOf.TryGetValue(it.Cat, out vg)) li.Group = vg;
                    li.SubItems.Add(MainForm.SubOf(it));                 // 细分：省掉重复的大类名
                    ModGroup g;
                    if (Catalog.GroupByKey.TryGetValue(it.GroupKey, out g) && g.IsSet) li.SubItems.Add(g.Name);
                    else li.SubItems.Add("—");
                    li.SubItems[1].ForeColor = MainForm.CatInk(it.Cat);   // 细分列跟主界面同色，两处对得上
                    if (picked.Contains(it.Key)) li.SubItems[2].ForeColor = Theme.Ok;
                    li.Tag = it.Key;
                    li.Checked = picked.Contains(it.Key);
                    if (it.SubjectLevel == "材质" || li.SubItems[1].Text == "—") li.SubItems[1].ForeColor = Theme.Ink3;
                    clb.Items.Add(li);
                    rowKeys.Add(it.Key);
                }
                clb.EndUpdate();
            }
            finally { filling = false; }
            dirty = Differs();
            MarkTitle();
            UpdateInfo();
        }

        // 和磁盘上那一版比：多一个少一个都算改动
        private bool Differs()
        {
            if (current == null) return picked.Count > 0;
            if (saved.Count != picked.Count) return true;
            foreach (string k in picked) if (!saved.Contains(k)) return true;
            foreach (string k in saved) if (!picked.Contains(k)) return true;
            return false;
        }

        // 搜索：名字、细分、套装名、标签都算
        private bool MatchItem(ModItem it, string q)
        {
            if (it.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if ((it.Cat + " " + it.Sub).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            ModGroup g;
            if (Catalog.GroupByKey.TryGetValue(it.GroupKey, out g) && g.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private void OnItemChecked(object sender, ItemCheckedEventArgs e)
        {
            if (filling) return;
            string key = e.Item.Tag as string;
            if (string.IsNullOrEmpty(key)) return;
            if (e.Item.Checked) picked.Add(key); else picked.Remove(key);
            bool d = Differs();
            if (d != dirty) { dirty = d; MarkTitle(); }
            UpdateInfo();
        }

        private void UpdateInfo()
        {
            int miss = 0;
            foreach (string k in picked) if (!Core.ByRel.ContainsKey(k)) miss++;
            string t = "已选 " + picked.Count + " 个文件 ｜ 列表显示 " + rowKeys.Count + " 行";
            if (miss > 0) t += " ｜ ⚠ " + miss + " 个库里找不到（保存后仍保留）";
            if (dirty) t += " ｜ 有改动未保存";
            lblInfo.Text = t;
        }

        // 批量：对"当前列表里显示的行"全选 / 全不选
        private void SetFiltered(bool on)
        {
            if (rowKeys.Count == 0) return;
            bool changed = false;
            foreach (string k in rowKeys)
            {
                if (on && !picked.Contains(k)) { picked.Add(k); changed = true; }
                else if (!on && picked.Contains(k)) { picked.Remove(k); changed = true; }
            }
            if (!changed) return;
            filling = true;
            try
            {
                foreach (ListViewItem li in clb.Items) li.Checked = on;
            }
            finally { filling = false; }
            dirty = Differs();
            MarkTitle();
            UpdateInfo();
        }

        private void SaveScheme()
        {
            Profile p = Sel();
            if (p == null || current == null) { MessageBox.Show("先选一个方案。"); return; }
            Profile fresh = ProfileStore.Load(current.Name);
            if (fresh == null) { MessageBox.Show("这个方案的文件不见了。"); return; }
            List<string> mods = new List<string>();
            foreach (ModItem it in Core.ModList) if (picked.Contains(it.Key)) mods.Add(it.Key);
            foreach (string k in picked) if (!mods.Contains(k)) mods.Add(k);
            fresh.Mods = mods;
            ProfileStore.Save(fresh);
            current = fresh;
            saved = new HashSet<string>(fresh.Mods, StringComparer.OrdinalIgnoreCase);
            dirty = false;
            LoadList();
            MessageBox.Show("已保存「" + fresh.Name + "」：" + mods.Count + " 个文件。", "保存方案");
        }

        private void ApplySel()
        {
            Profile p = Sel();
            if (p == null) return;
            if (dirty && MessageBox.Show("这个方案有改过但没保存，套用会丢掉这些改动。继续？", "未保存", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            dirty = false;                 // 已经由用户确认；避免关闭时再问一次
            owner.ApplyProfileByName(p.Name);
            Close();
        }

        private void RenameSel()
        {
            Profile p = Sel();
            if (p == null) return;
            string nn = PromptForm.Ask("新名字（当前：" + p.Name + "）", "给方案改名");
            if (string.IsNullOrEmpty(nn)) return;
            string err = ProfileStore.Rename(p.Name, nn.Trim());
            if (err != null) { MessageBox.Show(err); return; }
            LoadList();
        }

        private void CopySel()
        {
            Profile p = Sel();
            if (p == null) return;
            string nn = PromptForm.Ask("复制成什么名字？（当前：" + p.Name + "）", "另存为");
            if (string.IsNullOrEmpty(nn)) return;
            string err = ProfileStore.CopyAs(p.Name, nn.Trim());
            if (err != null) { MessageBox.Show(err); return; }
            LoadList();
        }

        private void DeleteSel()
        {
            Profile p = Sel();
            if (p == null) return;
            if (MessageBox.Show("删除方案「" + p.Name + "」？", "确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            ProfileStore.Delete(p.Name);
            LoadList();
        }

        private void ExportSel()
        {
            Profile p = Sel();
            if (p == null) { MessageBox.Show("先选一个方案。"); return; }
            SaveFileDialog d = new SaveFileDialog(); d.Filter = "方案文件|*.txt"; d.FileName = p.Name + ".txt";
            if (d.ShowDialog() != DialogResult.OK) return;
            File.Copy(Path.Combine(ProfileStore.DirOf(), p.Name + ".txt"), d.FileName, true);
            MessageBox.Show("已导出: " + d.FileName);
        }

        private void ExportAll()
        {
            List<Profile> all = ProfileStore.LoadAll();
            if (all.Count == 0) { MessageBox.Show("还没有方案可导出。"); return; }
            FolderBrowserDialog d = new FolderBrowserDialog();
            d.Description = "选一个文件夹，把所有方案（" + all.Count + " 个）导出进去";
            if (d.ShowDialog() != DialogResult.OK) return;
            int n = 0;
            foreach (Profile p in all)
            {
                try
                {
                    File.Copy(Path.Combine(ProfileStore.DirOf(), p.Name + ".txt"), Path.Combine(d.SelectedPath, p.Name + ".txt"), true);
                    n++;
                }
                catch { }
            }
            MessageBox.Show("已导出 " + n + " 个方案到：\r\n" + d.SelectedPath);
        }

        private void ImportFile()
        {
            OpenFileDialog d = new OpenFileDialog(); d.Filter = "方案文件|*.txt";
            if (d.ShowDialog() != DialogResult.OK) return;
            string name = Path.GetFileNameWithoutExtension(d.FileName);
            Directory.CreateDirectory(ProfileStore.DirOf());
            string dst = Path.Combine(ProfileStore.DirOf(), name + ".txt");
            if (File.Exists(dst) && MessageBox.Show("已有同名方案，覆盖？", "确认", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            File.Copy(d.FileName, dst, true);
            Profile p = ProfileStore.Load(name);
            LoadList();
            if (p != null && p.Mods.Count == 0)
                MessageBox.Show("已导入方案: " + name + "\r\n\r\n⚠ 但这个文件里一个 MOD 都没读到，可能是别的版本导出的。", "导入完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else
                MessageBox.Show("已导入方案: " + name + (p == null ? "" : "（" + p.Mods.Count + " 个 MOD）"));
        }
    }



    /// <summary>分类下拉的一项：两级（大类 + 细分），屏幕上看人话，筛选用字段。</summary>
    public sealed class CatChoice
    {
        public readonly string Cat;   // "" = 全部；地图/人物/枪械/药品/喷漆/杂项
        public readonly string Sub;   // null = 整个大类
        public readonly string Text;
        public CatChoice(string cat, string sub, string text) { Cat = cat; Sub = sub; Text = text; }
        public override string ToString() { return Text; }
    }

    public class PromptForm : Form
    {
        private TextBox box;
        private PromptForm(string title, string hint) : this(title, hint, "") { }

        private PromptForm(string title, string hint, string def)
        {
            Initialize(title, hint, def);
        }

        private void Initialize(string title, string hint, string def)
        {
            Text = title; ClientSize = new Size(420, 132); Font = Theme.UI; BackColor = Theme.Bg;
            StartPosition = FormStartPosition.CenterParent; MaximizeBox = false; MinimizeBox = false;
            Label l = new Label(); l.Text = hint; l.SetBounds(12, 12, 396, 20); Theme.Dress(l); Controls.Add(l);
            box = new TextBox(); box.SetBounds(12, 38, 396, 24); Theme.Dress(box); box.Text = def ?? ""; Controls.Add(box);
            FlatButton ok = new FlatButton(); ok.Text = "确定"; ok.Primary = true; ok.SetBounds(240, 76, 80, Theme.H); ok.DialogResult = DialogResult.OK; Controls.Add(ok);
            FlatButton no = new FlatButton(); no.Text = "取消"; no.SetBounds(328, 76, 80, Theme.H); no.DialogResult = DialogResult.Cancel; Controls.Add(no);
            AcceptButton = ok; CancelButton = no;
        }
        public static string Ask(string hint, string title)
        {
            return Ask(hint, title, "");
        }

        public static string Ask(string hint, string title, string def)
        {
            PromptForm f = new PromptForm(title, hint, def);
            if (f.ShowDialog() != DialogResult.OK) return null;
            return f.box.Text.Trim();
        }
    }
}
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Installer
{
    /// <summary>
    /// 画廊：用图找 MOD 的页面。一屏铺卡片（预览图 + 名字 + 订阅数），点一张换到详情页看全篇介绍。
    ///
    /// 和「批量下载」那个列表窗口是两种活法：
    ///   · 这里解决「我不知道要装什么，先看看有什么」——靠图扫，靠名字认；
    ///   · 列表那边解决「我知道要哪几个，一次勾完下完」——靠勾选和批量。
    /// 两个入口都在：这个页面右上角有「批量下载」，列表窗口里也有「画廊」。
    /// </summary>
    internal sealed class GalleryForm : Form, IMessageFilter
    {
        const int WM_MOUSEWHEEL = 0x020A;

        private readonly MainForm owner;
        private readonly ScrollHost host = new ScrollHost();
        private readonly WsCardGrid grid = new WsCardGrid();
        private readonly Panel detail = new Panel();

        private readonly PictureBox picHero = new PictureBox();
        private readonly Label lblTitle = new Label();
        private readonly Label lblMeta = new Label();
        private readonly Label lblTags = new Label();
        private readonly Label lblState = new Label();
        private readonly TextBox txtDesc = new TextBox();
        private readonly Panel descBox = new Panel();          // 正文的容器：给 TextBox 补内边距 + 一圈细边
        private readonly Label lblHeroHint = new Label();      // 大图没加载出来时的说明（空白块看不出是没图还是坏了）
        private readonly FlatButton btnBack = new FlatButton();
        private readonly FlatButton btnOpen = new FlatButton();
        private readonly FlatButton btnGet = new FlatButton();
        private readonly FlatButton btnLang = new FlatButton();   // 原文 / 译文 切换

        private readonly ComboBox cboCat = new ComboBox();
        private readonly TextBox txtQuery = new TextBox();
        private readonly ComboBox cboSort = new ComboBox();
        private readonly FlatButton btnSearch = new FlatButton();
        private readonly FlatButton btnList = new FlatButton();
        private readonly FlatButton btnClose = new FlatButton();
        private readonly FlatButton btnTop = new FlatButton();     // 回到顶部（追加模式下「上一页」没意义）
        private readonly FlatButton btnMore = new FlatButton();    // 加载更多 / 已到底
        private readonly Label lblStatus = new Label();
        private readonly Label lblCat = new Label();

        const int PageSize = 30;        // 工坊 browse 一页固定 30 个，用它判断到底
        const int AutoPages = 3;        // 自动连翻的页数上限，再多就等用户点（别一口气拉十页）
        const int SkeletonSearch = 12;  // 搜索中的骨架卡数（约铺满一屏）
        const int SkeletonAppend = 5;   // 翻页中的骨架卡数
        private int pagesLoaded;        // 本次搜索已经加载了几页
        private bool atEnd;             // 已到底
        private readonly HashSet<string> seenIds = new HashSet<string>(StringComparer.Ordinal);
        private List<WsItem> prefetched;    // 预扫好的下一页（含缩略图预热），滑到底直接上墙
        private int prefetchedPage;
        private volatile bool prefetching;
        private int searchGen;              // 搜索条件代数：换条件后，在途的预取作废
        private WsItem detailItem;
        private int page = 1;
        private volatile bool busy;
        private volatile bool cancel;
        private int detailToken;
        private bool showZh = true;
        private bool sampleMode;
        private string detailClean = "";      // 清洗后的原文：切原文/译文时不用重新计算
        private string detailZh;              // 译文（null = 还没翻好）
        private Bitmap sampleHero;            // 样例模式的大图（截图里能看出等比缩放对不对）

        /// <summary>下拉项：跟列表窗口那个私有的 CatChoice 分开，免得撞上命名空间里同名的那个。</summary>
        private sealed class GalChoice
        {
            public string Label = "";
            public string Query = "";
            public string Sort = "";
            public override string ToString() { return Label; }
        }

        private const int M = 16;
        private static int S(int v) { return Dpi.S(v); }

        public GalleryForm(MainForm o)
        {
            owner = o;
            Text = "创意工坊 · 画廊";
            Theme.UseDpi(this);
            ClientSize = Dpi.Sz(1180, 780);
            MinimumSize = Dpi.Sz(900, 620);
            Font = Theme.UI;
            BackColor = Theme.Bg;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            ShowInTaskbar = true;

            // ---- 顶部：找 ----
            lblCat.Text = "分类";
            lblCat.Font = Theme.UISmall;
            lblCat.ForeColor = Theme.Ink2;
            lblCat.AutoSize = false;
            Controls.Add(lblCat);

            cboCat.DropDownStyle = ComboBoxStyle.DropDownList;
            Theme.Dress(cboCat);
            Controls.Add(cboCat);
            FillCats();

            Theme.Dress(txtQuery);
            txtQuery.AccessibleName = "工坊搜索词";
            txtQuery.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                DoSearch(1);
                e.Handled = true;
                e.SuppressKeyPress = true;
            };
            Controls.Add(txtQuery);

            btnSearch.Text = "搜索";
            btnSearch.Click += delegate { DoSearch(1); };
            Controls.Add(btnSearch);

            cboSort.DropDownStyle = ComboBoxStyle.DropDownList;
            Theme.Dress(cboSort);
            cboSort.Items.Add(new GalChoice { Label = "最多订阅", Sort = "mostsubscribed" });
            cboSort.Items.Add(new GalChoice { Label = "最高评价", Sort = "toprated" });
            cboSort.Items.Add(new GalChoice { Label = "最新", Sort = "newestfirst" });
            cboSort.Items.Add(new GalChoice { Label = "趋势", Sort = "trend" });
            cboSort.SelectedIndex = 0;
            Controls.Add(cboSort);

            btnList.Text = "批量下载";
            btnList.Click += delegate { OpenList(); };
            Controls.Add(btnList);

            btnClose.Text = "关闭";
            btnClose.DialogResult = DialogResult.Cancel;
            Controls.Add(btnClose);
            CancelButton = btnClose;

            // ---- 主体：卡片墙 ----
            host.BackColor = Theme.Bg;
            host.Attach(grid);
            Controls.Add(host);
            grid.Activated += delegate(WsItem it) { ShowDetail(it); };
            // 快滑到底：还没到自动翻页上限就自己接着取，到了上限就把「加载更多」摆出来等用户点
            grid.NearEnd += delegate
            {
                if (sampleMode) return;               // 样例没有下一页，别去真搜
                if (busy || atEnd) return;
                if (pagesLoaded < AutoPages) LoadMore();
                else UpdateFooter();
            };
            grid.FooterClicked += delegate { LoadMore(); };

            // ---- 详情页 ----
            detail.BackColor = Theme.Bg;
            detail.Visible = false;
            Controls.Add(detail);

            picHero.SizeMode = PictureBoxSizeMode.Normal;   // 图已经是按框裁好的精确尺寸，交给 Zoom 反而会再缩一次
            picHero.BackColor = Theme.SurfaceAlt;
            picHero.BorderStyle = BorderStyle.FixedSingle;
            detail.Controls.Add(picHero);

            // 说明文字挂成 picHero 的子控件：父控件总把自己的子控件画在自己的图上面，
            // 不依赖 z-order，也不用 BringToFront（列表窗口那次用兄弟控件 + BringToFront 没生效）
            lblHeroHint.Text = "（点卡片看介绍）";
            lblHeroHint.TextAlign = ContentAlignment.MiddleCenter;
            lblHeroHint.Font = Theme.UISmall;
            lblHeroHint.ForeColor = Theme.Ink2;
            lblHeroHint.BackColor = Color.Transparent;
            lblHeroHint.AutoSize = false;
            picHero.Controls.Add(lblHeroHint);

            lblTitle.Font = Theme.UIBold;
            lblTitle.ForeColor = Theme.Ink;
            lblTitle.AutoSize = false;
            detail.Controls.Add(lblTitle);

            lblMeta.Font = Theme.UISmall;
            lblMeta.ForeColor = Theme.Ink2;
            lblMeta.AutoSize = false;
            lblMeta.AutoEllipsis = true;
            detail.Controls.Add(lblMeta);

            lblTags.Font = Theme.UISmall;
            lblTags.ForeColor = Theme.Info;
            lblTags.AutoSize = false;
            lblTags.AutoEllipsis = true;
            detail.Controls.Add(lblTags);

            lblState.Font = Theme.UISmall;
            lblState.ForeColor = Theme.Ink3;
            lblState.AutoSize = false;
            lblState.AutoEllipsis = true;
            detail.Controls.Add(lblState);

            txtDesc.Multiline = true;
            txtDesc.ReadOnly = true;
            txtDesc.ScrollBars = ScrollBars.Vertical;
            txtDesc.WordWrap = true;
            txtDesc.BorderStyle = BorderStyle.None;      // 边和留白交给外面的 descBox
            Theme.Dress(txtDesc);
            txtDesc.Dock = DockStyle.Fill;

            // TextBox 本身没有 Padding，塞进一个带内边距的面板才不至于文字贴边
            descBox.BackColor = Theme.Surface;
            descBox.Padding = new Padding(S(12), S(10), S(4), S(10));
            descBox.Paint += delegate(object s2, PaintEventArgs e2)
            {
                using (Pen p = new Pen(Theme.CardEdge))
                    e2.Graphics.DrawRectangle(p, 0, 0, descBox.Width - 1, descBox.Height - 1);
            };
            descBox.Controls.Add(txtDesc);
            detail.Controls.Add(descBox);

            btnBack.Text = "← 返回";
            btnBack.Click += delegate { ShowGallery(); };
            detail.Controls.Add(btnBack);

            btnGet.Text = "下载这个 MOD";
            btnGet.Primary = true;
            btnGet.Click += delegate { DownloadOne(); };
            detail.Controls.Add(btnGet);

            btnOpen.Text = "在浏览器打开";
            btnOpen.Click += delegate { OpenInBrowser(); };
            detail.Controls.Add(btnOpen);

            btnLang.Text = "看原文";
            btnLang.Click += delegate { showZh = !showZh; RenderDesc(); UpdateLangButton(); };
            detail.Controls.Add(btnLang);

            // ---- 底部 ----
            lblStatus.Font = Theme.UISmall;
            lblStatus.ForeColor = Theme.Ink2;
            lblStatus.AutoSize = false;
            lblStatus.AutoEllipsis = true;
            lblStatus.Text = "挑一个分类，或直接搜关键词；点卡片看完整介绍。";
            Controls.Add(lblStatus);

            btnTop.Text = "回到顶部";
            btnTop.Click += delegate { GoTop(); };
            Controls.Add(btnTop);

            btnMore.Text = "加载更多";
            btnMore.Enabled = false;
            btnMore.Click += delegate { LoadMore(); };
            Controls.Add(btnMore);

            FormClosing += delegate { cancel = true; };
            // 滚轮要能落在卡片墙上：WM_MOUSEWHEEL 只发给有焦点的控件，
            // 搜索完焦点通常在输入框里，光靠子控件的 OnMouseWheel 一辈子收不到。
            Load += delegate { Application.AddMessageFilter(this); };
            FormClosed += delegate { Application.RemoveMessageFilter(this); };
            Load += delegate { Theme.ApplyChrome(this); Theme.DeepNative(this); };
            Shown += delegate { Theme.ApplyChrome(this); Theme.DeepNative(this); };
            Resize += delegate { ApplyLayout(); };

            ApplyLayout();
            if (cboCat.Items.Count > 0) cboCat.SelectedIndex = 0;

            // 预扫本地下载记录：卡片直接标「已下载」，不用等用户点开才发现重复
            foreach (string id in Workshop.LoadDownloadedIds())
                grid.DownloadedIds.Add(id);

            // 打开画廊就预载第一页，别让用户面对一面空墙自己找按钮
            Shown += delegate
            {
                if (!sampleMode && grid.Items.Count == 0 && !busy) DoSearch(1);
            };
        }

        private void ApplyLayout()
        {
            int W = ClientSize.Width, H = ClientSize.Height;
            int m = S(M);

            // 顶部一行：左边找，右边去别处
            int y = S(14);
            lblCat.SetBounds(m, y + S(4), S(36), S(20));
            cboCat.SetBounds(m + S(40), y, S(160), S(26));

            int bx = W - m;
            btnClose.SetBounds(bx - S(72), y, S(72), S(30)); bx -= S(72) + S(8);
            btnList.SetBounds(bx - S(96), y, S(96), S(30)); bx -= S(96) + S(8);
            cboSort.SetBounds(bx - S(104), y + S(2), S(104), S(26)); bx -= S(104) + S(8);
            btnSearch.SetBounds(bx - S(64), y, S(64), S(30)); bx -= S(64) + S(8);
            int qx = m + S(40) + S(160) + S(8);
            txtQuery.SetBounds(qx, y + S(2), Math.Max(S(120), bx - S(8) - qx), S(26));

            int top = S(56);
            int bottom = S(64);
            int bodyH = Math.Max(S(160), H - top - bottom);
            host.SetBounds(m, top, W - 2 * m, bodyH);
            detail.SetBounds(m, top, W - 2 * m, bodyH);

            // 底部状态 + 翻页
            int by = H - bottom + S(6);
            btnTop.SetBounds(W - m - S(84), by, S(84), S(30));
            btnMore.SetBounds(W - m - S(84) - S(8) - S(96), by, S(96), S(30));
            lblStatus.SetBounds(m, by + S(6), Math.Max(S(160), (W - m - S(84) - S(8) - S(96) - S(24)) - m), S(18));

            // 详情页内部
            // 大图高度跟着窗口走：窗口矮的时候（最小 940×600）固定 300 会把正文挤成十几像素、
            // 还会被 Math.Max(80) 撑起来压到按钮上。留出下面这几行的固定开销，剩下的才给大图。
            int dw = detail.ClientSize.Width, dh = detail.ClientSize.Height;
            int heroH = Math.Min(S(300), Math.Max(S(140), dh - S(300)));
            picHero.SetBounds(0, 0, dw, heroH);
            lblHeroHint.SetBounds(0, 0, picHero.ClientSize.Width, picHero.ClientSize.Height);   // 子控件坐标：相对 picHero
            int ty = heroH + S(10);
            lblTitle.SetBounds(0, ty, dw, S(46));
            lblMeta.SetBounds(0, ty + S(48), dw, S(18));
            lblTags.SetBounds(0, ty + S(68), dw, S(18));
            lblState.SetBounds(0, ty + S(88), dw, S(18));
            int descTop = ty + S(112);
            int btnY = dh - S(34);
            descBox.SetBounds(0, descTop, dw, Math.Max(S(60), btnY - S(10) - descTop));
            btnBack.SetBounds(0, btnY, S(88), S(32));
            btnGet.SetBounds(S(96), btnY, S(124), S(32));
            btnOpen.SetBounds(S(228), btnY, S(120), S(32));
            btnLang.SetBounds(dw - S(96), btnY, S(96), S(32));
        }

        // ---------------------------------------------------------- 找

        /// <summary>分类下拉：直接借用方案树的位置名当搜索词，跟列表窗口同一套。</summary>
        private void FillCats()
        {
            cboCat.Items.Clear();
            cboCat.Items.Add(new GalChoice { Label = "全部物品", Query = "" });

            List<Slot> slots = null;
            try { if (owner != null && owner.Tree != null) slots = owner.Tree.Slots; } catch { }
            if (slots == null || slots.Count == 0) return;

            HashSet<string> cats = new HashSet<string>(StringComparer.Ordinal);
            foreach (Slot s in slots)
            {
                if (s == null || string.IsNullOrEmpty(s.Cat)) continue;
                if (cats.Add(s.Cat))
                    cboCat.Items.Add(new GalChoice { Label = "【" + s.Cat + "】", Query = Workshop.CatWord(s.Cat) });
            }
            foreach (Slot s in slots)
            {
                if (s == null || string.IsNullOrEmpty(s.Name)) continue;
                cboCat.Items.Add(new GalChoice
                {
                    Label = (s.Cat.Length > 0 ? s.Cat + " · " : "") + s.Name,
                    Query = Workshop.SearchWord(s.Name, s.Key)
                });
            }
        }

        /// <summary>
        /// 换搜索条件 → 从第 1 页重新来（清空墙、清空去重表）。
        /// </summary>
        private void DoSearch(int p)
        {
            if (busy) return;
            searchGen++;                       // 换条件：在途的预取全部作废
            prefetched = null;
            seenIds.Clear();
            atEnd = false;
            pagesLoaded = 0;
            grid.SetItems(new List<WsItem>()); // 旧结果连图一起清（换搜索了）
            grid.ShowSkeleton(SkeletonSearch); // 立刻铺骨架，别让人对着空墙猜
            grid.FooterText = "";
            grid.FooterClickable = false;
            lblStatus.Text = "正在搜工坊…";
            LoadPage(p, false);
        }

        /// <summary>往后翻一页（追加，不清空）。预扫好的数据直接上墙，不用再等网络。</summary>
        private void LoadMore()
        {
            if (busy || atEnd) return;
            if (prefetched != null && prefetchedPage == page + 1)
            {
                List<WsItem> batch = prefetched;
                prefetched = null;
                page++;
                pagesLoaded++;
                grid.AppendItems(batch);       // AppendItems 会撤掉骨架
                if (batch.Count < PageSize) atEnd = true;
                UpdateFooter();
                KickPrefetch();                // 接着预扫下一页
                return;
            }
            LoadPage(page + 1, true);
        }

        /// <summary>
        /// 取一页。append=false 是换搜索（替换整面墙），append=true 是翻页（往后接）。
        ///
        /// 到底的判断靠「这一页返回的 id 不足 30 个」——原来用 items.Count >= 20 当「还有下一页」，
        /// 第一页正好 20~29 个时会亮着按钮点了却翻出空页，不足 20 个又会误判到底。
        /// </summary>
        private void LoadPage(int p, bool append)
        {
            if (busy) return;
            GalChoice c = cboCat.SelectedItem as GalChoice;
            string q = txtQuery.Text.Trim();
            if (q.Length == 0 && c != null) q = c.Query;
            GalChoice s = cboSort.SelectedItem as GalChoice;
            string sort = s == null ? "mostsubscribed" : s.Sort;

            busy = true;
            btnSearch.Enabled = false;
            btnMore.Enabled = false;
            if (!append) lblStatus.Text = "正在搜工坊…";
            if (append)
            {
                grid.FooterText = "正在加载…";
                grid.FooterClickable = false;
                grid.ShowSkeleton(SkeletonAppend);   // 骨架跟在已有卡片后面，等待看得见
            }

            Thread t = new Thread(delegate()
            {
                List<WsItem> got = null;
                int idCount = 0;
                string err = null;
                try
                {
                    List<string> ids = Workshop.Search(q, sort, p);
                    idCount = ids.Count;
                    got = ids.Count == 0 ? new List<WsItem>() : Workshop.Fetch(ids);
                }
                catch (Exception ex) { err = ex.Message; }

                Ui(delegate()
                {
                    busy = false;
                    btnSearch.Enabled = true;
                    if (err != null)
                    {
                        grid.FooterText = "";
                        RelayoutWall();
                        lblStatus.Text = "搜索失败：" + err;
                        return;
                    }

                    page = p;
                    // 去重：排序变化时同一物品可能在相邻两页都出现，重复卡片比少几张更让人困惑
                    List<WsItem> fresh = new List<WsItem>();
                    foreach (WsItem one in got)
                        if (one != null && seenIds.Add(one.Id)) fresh.Add(one);

                    atEnd = idCount < PageSize || fresh.Count == 0;
                    pagesLoaded = append ? pagesLoaded + 1 : 1;

                    grid.HideSkeleton();
                    if (append) grid.AppendItems(fresh);
                    else { grid.SetItems(fresh); ShowGallery(); host.ScrollTo(0); }

                    UpdateFooter();
                    KickPrefetch();
                    if (grid.Items.Count == 0)
                        lblStatus.Text = "没搜到。换个词试试，或把「分类」切到「全部物品」。";
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>
        /// 底部提示与按钮的状态集中在这里算：
        /// 还能自动翻 → 不显示提示；自动翻到上限 → 显示可点的「加载更多」；到底了 → 显示已到底。
        /// </summary>
        private void UpdateFooter()
        {
            int n = grid.Items.Count;
            if (busy)
            {
                grid.FooterText = "正在加载…";
                grid.FooterClickable = false;
            }
            else if (n == 0)
            {
                grid.FooterText = "";
                grid.FooterClickable = false;
            }
            else if (atEnd)
            {
                grid.FooterText = "— 到底了 · 共 " + n + " 个 —";
                grid.FooterClickable = false;
            }
            else if (pagesLoaded < AutoPages)
            {
                grid.FooterText = "";          // 还能自动往下翻，先不打扰
                grid.FooterClickable = false;
            }
            else
            {
                grid.FooterText = "加载更多（第 " + (page + 1) + " 页）";
                grid.FooterClickable = true;
            }

            btnMore.Text = atEnd ? "已到底" : "加载更多";
            btnMore.Enabled = !atEnd && !busy;
            lblStatus.Text = n == 0 ? lblStatus.Text
                : "已加载 " + n + " 个 · 第 1–" + pagesLoaded + " 页"
                  + (atEnd ? " · 到底了" : pagesLoaded < AutoPages ? " · 继续下滑自动加载" : " · 点底部继续");

            // 底部提示条改高改矮都会影响墙的总高度，必须跟着重算 ——
            // 少了这一步，提示文字就算写好了也会被控件边界裁掉（第一版就是这么不显示的）。
            RelayoutWall();
        }

        /// <summary>
        /// 预扫下一页：数据 + 元数据 + 缩略图全部提前备好。
        /// 只预取一页、失败静默、换搜索条件即作废 —— 克制，不把 Steam 刷狠了。
        /// 滑到底触发加载时，预扫好的直接上墙，0 网络等待。
        /// </summary>
        private void KickPrefetch()
        {
            if (atEnd || busy || prefetching) return;
            if (prefetched != null && prefetchedPage == page + 1) return;   // 已经备好
            int gen = searchGen;
            GalChoice c = cboCat.SelectedItem as GalChoice;
            string q = txtQuery.Text.Trim();
            if (q.Length == 0 && c != null) q = c.Query;
            GalChoice so = cboSort.SelectedItem as GalChoice;
            string sort = so == null ? "mostsubscribed" : so.Sort;
            int p = page + 1;
            prefetching = true;

            Thread t = new Thread(delegate()
            {
                List<WsItem> got = null;
                string err = null;
                try
                {
                    List<string> ids = Workshop.Search(q, sort, p);
                    got = ids.Count == 0 ? new List<WsItem>() : Workshop.Fetch(ids);
                }
                catch (Exception ex) { err = ex.Message; }

                Ui(delegate()
                {
                    prefetching = false;
                    if (gen != searchGen) return;                  // 条件变了，这批扔掉
                    if (err != null) { Core.Log("WARN", "画廊预扫第 " + p + " 页失败（静默）: " + err); return; }
                    List<WsItem> fresh = new List<WsItem>();
                    foreach (WsItem one in got)
                        if (one != null && seenIds.Add(one.Id)) fresh.Add(one);
                    if (fresh.Count == 0) return;
                    prefetched = fresh;
                    prefetchedPage = p;
                    grid.PrimeThumbs(fresh);                       // 缩略图也提前下好
                    Core.Log("INFO", "画廊：预扫第 " + p + " 页完成（" + fresh.Count + " 个，缩略图已预热）");
                });
            });
            t.IsBackground = true;
            t.Priority = ThreadPriority.BelowNormal;
            t.Start();
        }

        private void RelayoutWall()
        {
            grid.Relayout();
            host.Relayout();
        }

        private void GoTop()
        {
            host.ScrollTo(0);
            grid.Focus();
        }

        // ---------------------------------------------------------- 两个页面之间切换

        private void ShowGallery()
        {
            detail.Visible = false;
            host.Visible = true;
            detailItem = null;
            detailToken++;
            // 翻页和「第 N 页」是卡片墙的东西，回详情页时不该跟着亮
            btnTop.Visible = true;
            btnMore.Visible = true;
            lblStatus.Visible = true;
            grid.Relayout();
            host.Relayout();
            grid.Focus();                 // 回到墙上就把焦点交给它，滚轮/键盘立刻可用
        }

        private void ShowDetail(WsItem it)
        {
            if (it == null) return;
            detailItem = it;
            int token = ++detailToken;
            host.Visible = false;
            detail.Visible = true;
            detail.BringToFront();
            btnTop.Visible = false;         // 详情页是一屏独立页面，翻页按钮属于卡片墙
            btnMore.Visible = false;
            lblStatus.Visible = false;

            lblTitle.Text = it.Title.Length > 0 ? it.Title : "(无标题)";
            string meta = "";
            if (it.Subs > 0) meta += "订阅 " + WsText.Compact(it.Subs);
            if (it.Updated > 0) meta += (meta.Length > 0 ? "  ·  " : "") + "更新 " + WsText.Ago(it.Updated);
            if (it.Size > 0) meta += (meta.Length > 0 ? "  ·  " : "") + Core.Human(it.Size);
            if (it.IsCollection) meta += (meta.Length > 0 ? "  ·  " : "") + "这是合集";
            lblMeta.Text = meta.Length == 0 ? "物品 " + it.Id : meta;
            lblTags.Text = it.Tags;
            lblState.Text = it.Where.Length > 0 ? "会装到：" + it.Where : "";
            btnGet.Enabled = !it.IsCollection && it.Url.Length > 0;

            if (picHero.Image != null) { Image old = picHero.Image; picHero.Image = null; old.Dispose(); }
            lblHeroHint.Visible = true;
            lblHeroHint.Text = it.Preview.Length == 0 ? "（这个物品没有预览图）" : "（正在加载预览图…）";

            detailClean = WsText.Clean(it.Desc);
            detailZh = WsTranslate.ReadCache(it.Id, it.Updated);
            RenderDesc();
            UpdateLangButton();

            if (sampleMode)
            {
                // 样例模式不联网：用造出来的大图代替，好让截图能看出填满裁切的效果
                if (sampleHero != null && it.Preview.Length > 0)
                {
                    picHero.Image = CoverFill(sampleHero, picHero.Width, picHero.Height);
                    lblHeroHint.Visible = false;
                }
                return;
            }
            int pw = picHero.Width, ph = picHero.Height;
            if (detailClean.Length > 0 && detailZh == null)
                lblState.Text = "正文是英文，正在翻成中文…（点「看原文」可先看）";
            Thread t = new Thread(delegate()
            {
                Image img = WsThumb.LoadCover(it.Preview, it.Id, pw, ph);
                string zh = detailZh, err = null;
                if (detailClean.Length > 0 && zh == null)
                    zh = WsTranslate.Translate(it.Id, it.Updated, detailClean, out err);
                Ui(delegate()
                {
                    if (token != detailToken) { if (img != null) img.Dispose(); return; }
                    if (img != null)
                    {
                        if (picHero.Image != null) picHero.Image.Dispose();
                        picHero.Image = img;
                        lblHeroHint.Visible = false;
                    }
                    else if (it.Preview.Length > 0) lblHeroHint.Text = "（预览图加载失败，先在浏览器里看）";

                    if (zh != null && zh.Length > 0) { detailZh = zh; RenderDesc(); UpdateLangButton(); }
                    else if (err != null) lblState.Text = err;
                });
            });
            t.IsBackground = true;
            t.Priority = ThreadPriority.BelowNormal;
            t.Start();
        }

        /// <summary>按当前「原文 / 译文」选择把正文填进框里。两处都要走 ToDisplay：TextBox 只认 CRLF。</summary>
        private void RenderDesc()
        {
            if (detailItem == null) { txtDesc.Text = ""; return; }
            if (showZh && detailZh != null && detailZh.Length > 0)
                txtDesc.Text = WsText.ToDisplay(detailZh);
            else if (detailClean.Length > 0)
                txtDesc.Text = WsText.ToDisplay(detailClean);
            else
                txtDesc.Text = WsText.ToDisplay("（这个物品没有写介绍）");
            try { txtDesc.SelectionStart = 0; txtDesc.SelectionLength = 0; } catch { }
        }

        /// <summary>没有译文时按钮就没意义 —— 灰掉，别让人点了没反应。</summary>
        private void UpdateLangButton()
        {
            bool has = detailZh != null && detailZh.Length > 0;
            btnLang.Enabled = has;
            btnLang.Text = showZh ? "看原文" : "看译文";
        }

        // ---------------------------------------------------------- 下载单个

        private void DownloadOne()
        {
            WsItem it = detailItem;
            if (busy || it == null) return;
            if (it.IsCollection || it.Url.Length == 0) { lblState.Text = "这个物品没有直链，只能走 Steam 客户端订阅。"; return; }
            string dir;
            try { dir = Workshop.DestDir(); }
            catch (Exception ex) { lblState.Text = ex.Message; return; }

            busy = true;
            cancel = false;
            btnGet.Enabled = false;
            lblState.Text = "正在下载…";

            Thread t = new Thread(delegate()
            {
                try
                {
                    string dest = Path.Combine(dir, Workshop.SafeFileName(it.Title, it.Id));
                    Workshop.Download(it, dest, delegate(long got, long total)
                    {
                        int pct = total > 0 ? (int)Math.Min(100, got * 100 / total) : 0;
                        Ui(delegate() { lblState.Text = "正在下载 " + pct + "%  ·  " + Core.Human(got) + " / " + Core.Human(total); });
                    });
                    string rn = Workshop.RenameToAddonTitle(dest);
                    if (rn != null) dest = Path.Combine(dir, rn);
                    string name = Path.GetFileName(dest);
                    Workshop.RememberDownload(it.Id, name);          // 记 id→文件名，下次打开画廊卡片直接标「已下载」
                    Ui(delegate()
                    {
                        busy = false;
                        btnGet.Enabled = true;
                        grid.DownloadedIds.Add(it.Id);
                        grid.Invalidate();
                        lblState.Text = "已下载到库里：" + name + "（关窗后在方案树里找它）";
                    });
                    Core.Log("OK", "画廊：下载 " + it.Id + " → " + dest);
                }
                catch (Exception ex)
                {
                    Ui(delegate()
                    {
                        busy = false;
                        btnGet.Enabled = true;
                        lblState.Text = "下载失败：" + ex.Message;
                    });
                    Core.Log("FAIL", "画廊下载失败 " + it.Id + ": " + ex.Message);
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void OpenInBrowser()
        {
            if (detailItem == null) return;
            try
            {
                System.Diagnostics.Process.Start("https://steamcommunity.com/sharedfiles/filedetails/?id=" + detailItem.Id);
            }
            catch (Exception ex) { lblState.Text = "打不开浏览器：" + ex.Message; }
        }

        private void OpenList()
        {
            if (owner == null) return;
            owner.ShowWorkshopList();
        }

        /// <summary>
        /// 光标停在卡片墙上时，把滚轮直接转给滚动容器 —— 不管焦点在哪个控件上。
        /// 光标在墙外（输入框、详情页正文）就放行，让它们自己滚。
        /// </summary>
        bool IMessageFilter.PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_MOUSEWHEEL) return false;
            if (host == null || !host.Visible || host.IsDisposed) return false;
            try
            {
                Point pt = host.PointToClient(Cursor.Position);
                if (!host.ClientRectangle.Contains(pt)) return false;
                int delta = (short)(((long)m.WParam >> 16) & 0xFFFF);
                if (delta == 0) return false;
                host.ScrollTo(host.Offset - delta);
                return true;                       // 吃掉了，别再传下去
            }
            catch { return false; }
        }

        private void Ui(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); } catch { }
        }

        // ---------------------------------------------------------- 截图自检

        /// <summary>样例数据：离线跑截图，看卡片排列、标题换行、详情页版式。</summary>
        internal void FillSample()
        {
            sampleMode = true;
            string[] names = new string[]
            {
                "The After - Tank / Bloater (The Last Of Us)",
                "Chargers get staggered by blunt melee [vscript]",
                "黑色 AK-103 科幻涂装 · 带自定义音效",
                "Outland Necrotics",
                "Zoey 替换为 Ellie（含第一人称手部）",
                "HUD 图标重绘 · 极简版",
                "（拿不到直链的那种条目）"
            };
            List<WsItem> items = new List<WsItem>();
            // 一页 30 个量级的样例：墙要比可视区高出一截，截图才验得了滚动和底部提示
            for (int i = 0; i < 24; i++)
            {
                string name = names[i % names.Length] + (i >= names.Length ? " #" + (i / names.Length + 1) : "");
                WsItem w = new WsItem();
                w.Id = (3832308500 + i).ToString();
                w.Title = name;
                w.Size = 3000000L * (i % names.Length + 1);
                w.Subs = 12400 - (i % names.Length) * 1500;
                w.Updated = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds - 86400 * (3 + (i % names.Length) * 9);
                w.Tags = i % 2 == 0 ? "模型 · 角色" : "vscript · 玩法";
                w.Url = "https://example.invalid/" + i + ".vpk";
                w.Preview = "sample://" + i;      // 有这个标记才会铺样例色块，空的就是真·空态
                w.Desc = "[h1]" + name + "[/h1]\n[b]English description sample.[/b] "
                       + "This is what the author wrote on the workshop page, several lines long, "
                       + "so the detail page has something to wrap and scroll.\n\n"
                       + "[list][*]First feature line\n[*]Second feature line\n[*]Third feature line[/list]\n\n"
                       + "Credits and notes go here.";
                items.Add(w);
            }
            // 最后一条模拟「没有预览图 / 没有介绍 / 没有直链」的脏数据，专门用来验空态
            WsItem last = items[items.Count - 1];
            last.Url = "";
            last.Desc = "";
            last.Preview = "";
            last.Note = "没有直链";

            sampleHero = SampleBanner(960, 540, "预览图大图 16:9");
            grid.SetItems(items);
            grid.SeedSampleImages();
            ShowGallery();
            // 截图要能看到新的底部提示：样例当成「自动翻页已经用满、该点了」的状态
            pagesLoaded = AutoPages;
            atEnd = false;
            UpdateFooter();
        }

        /// <summary>把一张图按「填满 + 居中裁切」画成 w×h（详情页横幅用，避免两侧大片空白）。</summary>
        private static Image CoverFill(Image src, int w, int h)
        {
            if (src == null || w <= 0 || h <= 0) return null;
            Bitmap b = new Bitmap(w, h);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                double k = Math.Max((double)w / src.Width, (double)h / src.Height);
                int sw = (int)Math.Ceiling(src.Width * k), sh = (int)Math.Ceiling(src.Height * k);
                g.DrawImage(src, new Rectangle((w - sw) / 2, (h - sh) / 2, sw, sh));
            }
            return b;
        }

        /// <summary>造一张带文字的横向色块，代替真实预览图（样例模式不联网）。</summary>
        private static Bitmap SampleBanner(int w, int h, string caption)
        {
            Bitmap b = new Bitmap(w, h);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (LinearGradientBrush lb = new LinearGradientBrush(
                    new Rectangle(0, 0, w, h), Color.FromArgb(46, 62, 86), Color.FromArgb(96, 74, 130), 25f))
                    g.FillRectangle(lb, 0, 0, w, h);
                using (SolidBrush tb = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
                    g.DrawString(caption, new Font("Microsoft YaHei UI", 34F, FontStyle.Bold), tb, new PointF(40, h / 2 - 34));
            }
            return b;
        }

        /// <summary>自检用：铺骨架占位卡（验证「搜索中」的状态看得见）。</summary>
        internal void SkeletonForTest(int n)
        {
            grid.SetItems(new List<WsItem>());
            grid.ShowSkeleton(n);
            lblStatus.Text = "正在搜工坊…（骨架态截图）";
        }

        /// <summary>自检用：直接把墙滚到指定位置（验证滚动是否真的移动内容），并把关键几何打进日志。</summary>
        internal void ScrollForTest(int y)
        {
            host.ScrollTo(y);
            int maxOff = grid.Height - host.ClientSize.Height;
            Core.Log("INFO", "GALSCROLL items=" + grid.Items.Count
                + " gridH=" + grid.Height + " hostH=" + host.ClientSize.Height
                + " 请求=" + y + " 实际offset=" + host.Offset + " maxOff=" + maxOff
                + " 可滚=" + host.CanScroll + " footer=" + grid.FooterText);
        }

        /// <summary>自检用：直接打开第 index 个的详情页。超出范围就取最后一个（方便专门去看脏数据那条）。</summary>
        internal void ShowDetailForTest(int index)
        {
            IList<WsItem> all = grid.Items;
            if (all.Count == 0) return;
            if (index < 0) index = 0;
            if (index >= all.Count) index = all.Count - 1;
            ShowDetail(all[index]);
        }
    }
}

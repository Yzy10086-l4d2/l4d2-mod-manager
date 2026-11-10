using System;
using System.Collections.Generic;
using System.Drawing;
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
    internal sealed class GalleryForm : Form
    {
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
        private readonly FlatButton btnBack = new FlatButton();
        private readonly FlatButton btnOpen = new FlatButton();
        private readonly FlatButton btnGet = new FlatButton();

        private readonly ComboBox cboCat = new ComboBox();
        private readonly TextBox txtQuery = new TextBox();
        private readonly ComboBox cboSort = new ComboBox();
        private readonly FlatButton btnSearch = new FlatButton();
        private readonly FlatButton btnList = new FlatButton();
        private readonly FlatButton btnClose = new FlatButton();
        private readonly FlatButton btnPrev = new FlatButton();
        private readonly FlatButton btnNext = new FlatButton();
        private readonly Label lblStatus = new Label();
        private readonly Label lblCat = new Label();

        private List<WsItem> items = new List<WsItem>();
        private WsItem detailItem;
        private int page = 1;
        private volatile bool busy;
        private volatile bool cancel;
        private int detailToken;
        private bool showZh = true;
        private bool sampleMode;

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

            // ---- 详情页 ----
            detail.BackColor = Theme.Bg;
            detail.Visible = false;
            Controls.Add(detail);

            picHero.SizeMode = PictureBoxSizeMode.Zoom;
            picHero.BackColor = Theme.SurfaceAlt;
            picHero.BorderStyle = BorderStyle.FixedSingle;
            detail.Controls.Add(picHero);

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
            txtDesc.BorderStyle = BorderStyle.FixedSingle;
            Theme.Dress(txtDesc);
            detail.Controls.Add(txtDesc);

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

            // ---- 底部 ----
            lblStatus.Font = Theme.UISmall;
            lblStatus.ForeColor = Theme.Ink2;
            lblStatus.AutoSize = false;
            lblStatus.AutoEllipsis = true;
            lblStatus.Text = "挑一个分类，或直接搜关键词；点卡片看完整介绍。";
            Controls.Add(lblStatus);

            btnPrev.Text = "上一页";
            btnPrev.Enabled = false;
            btnPrev.Click += delegate { if (page > 1) DoSearch(page - 1); };
            Controls.Add(btnPrev);

            btnNext.Text = "下一页";
            btnNext.Enabled = false;
            btnNext.Click += delegate { DoSearch(page + 1); };
            Controls.Add(btnNext);

            FormClosing += delegate { cancel = true; };
            Load += delegate { Theme.ApplyChrome(this); Theme.DeepNative(this); };
            Shown += delegate { Theme.ApplyChrome(this); Theme.DeepNative(this); };
            Resize += delegate { ApplyLayout(); };

            ApplyLayout();
            if (cboCat.Items.Count > 0) cboCat.SelectedIndex = 0;
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
            btnNext.SetBounds(W - m - S(56), by, S(56), S(30));
            btnPrev.SetBounds(W - m - S(56) - S(8) - S(56), by, S(56), S(30));
            lblStatus.SetBounds(m, by + S(6), Math.Max(S(160), (W - m - S(56) * 2 - S(24)) - m), S(18));

            // 详情页内部
            int dw = detail.ClientSize.Width, dh = detail.ClientSize.Height;
            picHero.SetBounds(0, 0, dw, S(300));
            lblTitle.SetBounds(0, S(310), dw, S(46));
            lblMeta.SetBounds(0, S(358), dw, S(18));
            lblTags.SetBounds(0, S(378), dw, S(18));
            lblState.SetBounds(0, S(398), dw, S(18));
            int descTop = S(422);
            int btnY = dh - S(34);
            txtDesc.SetBounds(0, descTop, dw, Math.Max(S(80), btnY - S(10) - descTop));
            btnBack.SetBounds(0, btnY, S(88), S(32));
            btnGet.SetBounds(S(96), btnY, S(124), S(32));
            btnOpen.SetBounds(S(228), btnY, S(120), S(32));
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

        private void DoSearch(int p)
        {
            if (busy) return;
            GalChoice c = cboCat.SelectedItem as GalChoice;
            string q = txtQuery.Text.Trim();
            if (q.Length == 0 && c != null) q = c.Query;
            GalChoice s = cboSort.SelectedItem as GalChoice;

            busy = true;
            btnSearch.Enabled = false;
            lblStatus.Text = "正在搜工坊…";
            string sort = s == null ? "mostsubscribed" : s.Sort;

            Thread t = new Thread(delegate()
            {
                List<WsItem> got = null;
                string err = null;
                try
                {
                    List<string> ids = Workshop.Search(q, sort, p);
                    if (ids.Count == 0) { got = new List<WsItem>(); }
                    else got = Workshop.Fetch(ids);
                }
                catch (Exception ex) { err = ex.Message; }

                Ui(delegate()
                {
                    busy = false;
                    btnSearch.Enabled = true;
                    if (err != null)
                    {
                        lblStatus.Text = "搜索失败：" + err;
                        return;
                    }
                    page = p;
                    items = got;
                    grid.SetItems(items);
                    ShowGallery();
                    btnPrev.Enabled = page > 1;
                    btnNext.Enabled = items.Count >= 20;
                    lblStatus.Text = items.Count == 0
                        ? "没搜到。换个词试试，或把「分类」切到「全部物品」。"
                        : "第 " + page + " 页 · " + items.Count + " 个 · 点卡片看完整介绍";
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        // ---------------------------------------------------------- 两个页面之间切换

        private void ShowGallery()
        {
            detail.Visible = false;
            host.Visible = true;
            detailItem = null;
            detailToken++;
            grid.Relayout();
            host.Relayout();
        }

        private void ShowDetail(WsItem it)
        {
            if (it == null) return;
            detailItem = it;
            int token = ++detailToken;
            host.Visible = false;
            detail.Visible = true;
            detail.BringToFront();

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

            string clean = WsText.Clean(it.Desc);
            string cached = WsTranslate.ReadCache(it.Id, it.Updated);
            txtDesc.Text = WsText.ToDisplay(
                (showZh && cached != null) ? cached : (clean.Length > 0 ? clean : "（这个物品没有写介绍）"));

            if (sampleMode) return;
            int pw = picHero.Width, ph = picHero.Height;
            Thread t = new Thread(delegate()
            {
                Image img = WsThumb.Load(it.Preview, it.Id, pw, ph);
                string zh = null, err = null;
                if (clean.Length > 0)
                {
                    zh = WsTranslate.ReadCache(it.Id, it.Updated);
                    if (zh == null) zh = WsTranslate.Translate(it.Id, it.Updated, clean, out err);
                }
                Ui(delegate()
                {
                    if (token != detailToken) { if (img != null) img.Dispose(); return; }
                    if (img != null)
                    {
                        if (picHero.Image != null) picHero.Image.Dispose();
                        picHero.Image = img;
                    }
                    if (showZh && zh != null && zh.Length > 0) txtDesc.Text = WsText.ToDisplay(zh);
                    else if (showZh && err != null) lblState.Text = err;
                });
            });
            t.IsBackground = true;
            t.Priority = ThreadPriority.BelowNormal;
            t.Start();
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
                    Ui(delegate()
                    {
                        busy = false;
                        btnGet.Enabled = true;
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
                "HUD 图标重绘 · 极简版"
            };
            items = new List<WsItem>();
            for (int i = 0; i < names.Length; i++)
            {
                WsItem w = new WsItem();
                w.Id = (3832308500 + i).ToString();
                w.Title = names[i];
                w.Size = 3000000L * (i + 1);
                w.Subs = 12400 - i * 1500;
                w.Updated = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds - 86400 * (3 + i * 9);
                w.Tags = i % 2 == 0 ? "模型 · 角色" : "vscript · 玩法";
                w.Url = "https://example.invalid/" + i + ".vpk";
                w.Desc = "[h1]" + names[i] + "[/h1]\n[b]English description sample.[/b] "
                       + "This is what the author wrote on the workshop page, several lines long, "
                       + "so the detail page has something to wrap and scroll.\n\n"
                       + "[list][*]First feature line\n[*]Second feature line\n[*]Third feature line[/list]\n\n"
                       + "Credits and notes go here.";
                items.Add(w);
            }
            grid.SetItems(items);
            grid.SeedSampleImages();
            lblStatus.Text = "样例数据 · " + items.Count + " 个 · 点卡片看完整介绍";
            ShowGallery();
        }

        /// <summary>自检用：直接打开第 index 个的详情页。</summary>
        internal void ShowDetailForTest(int index)
        {
            if (index < 0 || index >= items.Count) return;
            ShowDetail(items[index]);
        }
    }
}

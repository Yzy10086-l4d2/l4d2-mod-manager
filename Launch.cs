using System;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Installer
{
    /// <summary>
    /// Steam 启动项参数。默认值放这里，Core 和界面都引用同一份，别在两处各写一遍。
    /// 参数串会原样写进 Steam 的 LaunchOptions，所以只做「清洗空白」，不擅自改内容。
    /// </summary>
    internal static class LaunchArgs
    {
        /// <summary>v1.8 起的默认启动项。</summary>
        public const string Default =
            "-heapsize 2096999 -num_edicts 4096 -processheap -high -highpriority " +
            "-l4n_use_neko_engine_post -skeeto_particle_platform 0/1 -novid";

        /// <summary>v1.7 及更早的默认值。老配置里读到这个说明是没人改过的默认值，直接升级。</summary>
        public const string Legacy = "-novid -high";

        /// <summary>换行/多个空格折成一个空格，去首尾 —— Steam 那边只认一行。</summary>
        public static string Clean(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = raw.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
            return Regex.Replace(s, " {2,}", " ").Trim();
        }

        /// <summary>是否还留着 0/1 这种「自己选一个」的占位符。</summary>
        public static bool HasSlot(string s)
        {
            return !string.IsNullOrEmpty(s) && s.IndexOf("0/1", StringComparison.Ordinal) >= 0;
        }

        /// <summary>把占位符换成确定的 0 或 1。</summary>
        public static string Resolve(string s, string pick)
        {
            return string.IsNullOrEmpty(s) ? "" : s.Replace("0/1", pick);
        }

        /// <summary>装机 / 写启动项前统一走这里：清洗 + 提示占位符没落地。</summary>
        public static string ForWrite(string raw)
        {
            string s = Clean(raw);
            if (HasSlot(s))
                Core.Log("WARN", "启动项里的 -skeeto_particle_platform 还是 0/1，按原样写进去了；"
                       + "要去 Steam 里改成 0 或 1 才生效（本工具的「…」按钮可以直接换）");
            return s;
        }

        /// <summary>数几个开关（以 - 开头的段），比数空格段更接近"几个参数"。</summary>
        public static int CountSwitches(string s)
        {
            s = Clean(s);
            if (s.Length == 0) return 0;
            int n = 0;
            foreach (string part in s.Split(' '))
                if (part.StartsWith("-", StringComparison.Ordinal)) n++;
            return n;
        }

        /// <summary>自测：清洗/占位符/默认值。返回失败条数。</summary>
        public static int SelfTest()
        {
            int bad = 0;
            Action<bool, string> chk = delegate(bool ok, string name)
            {
                if (ok) Core.Log("OK", "LAUNCHSELF " + name);
                else { bad++; Core.Log("FAIL", "LAUNCHSELF " + name); }
            };
            chk(Default.Length > 0, "默认值非空");
            chk(Clean("  -a   -b \r\n\t-c  ") == "-a -b -c", "清洗换行和多空格");
            chk(Clean(null) == "" && Clean("   ") == "", "空值不炸");
            chk(HasSlot(Default), "默认值含 0/1 占位");
            chk(Resolve(Default, "1").Contains("-skeeto_particle_platform 1 "), "换成 1");
            chk(Resolve(Default, "0").Contains("-skeeto_particle_platform 0 "), "换成 0");
            chk(!HasSlot(Resolve(Default, "1")) && !HasSlot(Resolve(Default, "0")), "换完没有占位符");
            chk(Default.Contains("-heapsize 2096999") && Default.Contains("-num_edicts 4096")
                && Default.EndsWith("-novid"), "关键参数齐全且有 -novid");
            chk(CountSwitches(Default) == 8, "开关数 8（实际 " + CountSwitches(Default) + "）");
            chk(Clean(Default) == Default, "默认值本身是干净的");
            chk(CountSwitches("") == 0, "空串开关数 0");
            bad += Work.SelfTestLaunchVdf();        // localconfig.vdf 改写：只碰临时文件
            return bad;
        }
    }

    /// <summary>
    /// 启动项放大编辑。一行输入框装不下 130 个字符，实际也没人能在 300px 里改参数。
    /// 主界面只显示结果，真正的编辑在这里做：可换行、可一键恢复默认、可把 0/1 换成 0 或 1。
    /// </summary>
    public class LaunchForm : Form
    {
        private TextBox box;
        private Label preview;
        private ToolTip tips = new ToolTip();
        private static bool askedSlot;      // 「0/1 还没定」只提醒一次

        /// <summary>确定后这里是清洗过的单行参数。</summary>
        public string Result;

        // 打开时文本会整段处于选中态（蓝底），看着像等你覆盖输入，光标退回开头
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            box.SelectionStart = 0;
            box.SelectionLength = 0;
        }

        public LaunchForm(string current)
        {
            Text = "启动项参数";
            Theme.UseDpi(this);
            ClientSize = Dpi.Sz(660, 258);
            Font = Theme.UI;
            BackColor = Theme.Bg;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;

            Label hint = new Label();
            hint.SetBounds(14, 10, 632, 34);
            hint.Font = Theme.UISmall;
            hint.ForeColor = Theme.Ink2;
            hint.Text = "这串会写进 Steam → 库 → 右键《求生之路2》→ 属性 → 启动选项。\r\n"
                      + "可以换行写，保存时会把换行和多余空格合成一个空格（Steam 只认单行）。";
            Controls.Add(hint);

            box = new TextBox();
            box.SetBounds(14, 48, 632, 92);
            box.Multiline = true;
            box.ScrollBars = ScrollBars.Vertical;
            box.WordWrap = true;
            box.AcceptsReturn = true;
            box.Font = Theme.Mono;
            box.BorderStyle = BorderStyle.FixedSingle;
            box.BackColor = Theme.Surface;
            box.ForeColor = Theme.Ink;
            box.Text = LaunchArgs.Clean(current);
            box.TextChanged += delegate { UpdatePreview(); };
            Controls.Add(box);

            Label warn = new Label();
            warn.SetBounds(14, 146, 632, 32);
            warn.Font = Theme.UISmall;
            warn.ForeColor = Theme.Warn;
            warn.Text = "-skeeto_particle_platform 后面只能跟一个数（0 或 1）。写成 0/1 游戏会当成一个字符串，等于没设置。\r\n"
                      + "不确定就用下面两个按钮换成确定值；其余参数原样保留。";
            Controls.Add(warn);

            preview = new Label();
            preview.SetBounds(14, 180, 632, 20);
            preview.Font = Theme.UISmall;
            preview.ForeColor = Theme.Ink3;
            preview.AutoEllipsis = true;
            Controls.Add(preview);

            FlatButton bdef = new FlatButton(); bdef.Text = "恢复默认"; bdef.SetBounds(14, 214, 96, Theme.H); bdef.Font = Theme.UISmall;
            bdef.Click += delegate { box.Text = LaunchArgs.Default; }; Controls.Add(bdef);

            FlatButton b0 = new FlatButton(); b0.Text = "用 0"; b0.SetBounds(118, 214, 68, Theme.H); b0.Font = Theme.UISmall;
            b0.Click += delegate { box.Text = LaunchArgs.Resolve(LaunchArgs.Clean(box.Text), "0"); }; Controls.Add(b0);

            FlatButton b1 = new FlatButton(); b1.Text = "用 1"; b1.SetBounds(192, 214, 68, Theme.H); b1.Font = Theme.UISmall;
            b1.Click += delegate { box.Text = LaunchArgs.Resolve(LaunchArgs.Clean(box.Text), "1"); }; Controls.Add(b1);

            FlatButton ok = new FlatButton(); ok.Text = "确定"; ok.Primary = true;
            ok.SetBounds(462, 214, 80, Theme.H);
            ok.Click += delegate { Accept(); }; Controls.Add(ok);

            FlatButton no = new FlatButton(); no.Text = "取消"; no.SetBounds(550, 214, 96, Theme.H);
            no.DialogResult = DialogResult.Cancel; Controls.Add(no);

            AcceptButton = ok;
            CancelButton = no;
            UpdatePreview();
        }

        private void UpdatePreview()
        {
            string s = LaunchArgs.Clean(box.Text);
            if (s.Length == 0)
            {
                preview.Text = "结果：不写启动项（会清空这台机器 Steam 里的启动选项）";
                preview.ForeColor = Theme.Ink3;
            }
            else if (LaunchArgs.HasSlot(s))
            {
                preview.Text = "结果：单行 " + s.Length + " 字符 · " + LaunchArgs.CountSwitches(s) + " 个开关 · 里面还有 0/1，要换成 0 或 1";
                preview.ForeColor = Theme.Warn;
            }
            else
            {
                preview.Text = "结果：单行 " + s.Length + " 字符 · " + LaunchArgs.CountSwitches(s) + " 个开关 · 悬停看全文";
                preview.ForeColor = Theme.Ink3;
            }
            tips.SetToolTip(preview, s.Length == 0 ? "不会写入任何参数" : s);
        }

        private void Accept()
        {
            Result = LaunchArgs.Clean(box.Text);
            if (Result.Length == 0 &&
                MessageBox.Show("参数是空的，这样会把 Steam 里的启动项清空。仍然确定？", "确认",
                                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            // 占位符没落地就问一句：0/1 这种写法游戏会当成一个字符串，等于没设置。
            // 同一进程只问一次 —— 装机时反复点确定不该反复弹。
            if (LaunchArgs.HasSlot(Result) && !askedSlot)
            {
                askedSlot = true;
                DialogResult r = MessageBox.Show(
                    "参数里还留着 -skeeto_particle_platform 0/1。\r\n\r\n"
                    + "游戏会把它当成一个字符串，这个参数等于没设置。\r\n\r\n"
                    + "是 = 改成 1 保存\r\n否 = 原样保存", "0/1 还没定",
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel) return;
                if (r == DialogResult.Yes) Result = LaunchArgs.Resolve(Result, "1");
            }
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}

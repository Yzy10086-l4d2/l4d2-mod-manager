using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Installer
{
    /// <summary>
    /// 参数存档：把机器上调好的 L4N 按键/网络参数（left4dead2\cfg\autoexec.cfg）
    /// 和 Skeeto 设置（skeeto_setting.ini）存到 U 盘，
    /// 换台新机器装机时直接推过去，不用再一项项点一遍。
    /// </summary>
    public static class Tweaks
    {
        public const string AutoexecName = "autoexec.cfg";       // 装在 left4dead2\cfg\
        public const string SkeetoName = "skeeto_setting.ini";   // 装在游戏根目录
        private const string InfoName = "存档信息.txt";

        /// <summary>自测时把存档根指到临时目录，别污染 U 盘上的真存档。</summary>
        public static string RootOverride;

        public static string Root
        {
            get { return RootOverride != null ? RootOverride : Path.Combine(Core.PkgDir, "_参数存档"); }
        }

        public static string AutoexecInGame(string gameRoot)
        {
            return Path.Combine(Path.Combine(Path.Combine(gameRoot, "left4dead2"), "cfg"), AutoexecName);
        }

        public static string SkeetoInGame(string gameRoot)
        {
            return Path.Combine(gameRoot, SkeetoName);
        }

        // ------------------------------------------------------------ 存档列表

        /// <summary>U 盘上已有的存档名（按名字排）。</summary>
        public static List<string> List()
        {
            List<string> names = new List<string>();
            try
            {
                if (!Directory.Exists(Root)) return names;
                foreach (string d in Directory.GetDirectories(Root))
                {
                    string n = Path.GetFileName(d);
                    if (n.StartsWith(".")) continue;
                    // 旧版「删除」其实是改名成 .删除-时间戳、「覆盖」会留 .bak-时间戳：
                    // 这些历史目录里同样有 autoexec.cfg，不过滤的话会被当成一套存档列出来
                    if (n.IndexOf(".删除-", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (n.IndexOf(".bak-", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (File.Exists(Path.Combine(d, AutoexecName)) || File.Exists(Path.Combine(d, SkeetoName)))
                        names.Add(n);
                }
            }
            catch (Exception ex) { Core.Log("WARN", "读参数存档失败: " + ex.Message); }
            names.Sort(StringComparer.CurrentCulture);
            return names;
        }

        public static string DirOf(string name) { return Path.Combine(Root, name); }

        public static bool Exists(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (string n in List()) if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>这套存档里有什么：文件名 + 大小 + 保存时间。</summary>
        public static string Info(string name)
        {
            StringBuilder sb = new StringBuilder();
            string dir = DirOf(name);
            sb.AppendLine("存档：" + name);
            sb.AppendLine("位置：" + dir);
            try
            {
                string meta = Path.Combine(dir, InfoName);
                if (File.Exists(meta))
                    foreach (string l in File.ReadAllLines(meta, Encoding.UTF8))
                        if (l.Trim().Length > 0) sb.AppendLine(l);
            }
            catch { }
            sb.AppendLine();
            sb.AppendLine("内容：");
            AppendFileInfo(sb, Path.Combine(dir, AutoexecName), @"→ left4dead2\cfg\autoexec.cfg（L4N 按键 / 网络 / 亮度）");
            AppendFileInfo(sb, Path.Combine(dir, SkeetoName), @"→ skeeto_setting.ini（Skeeto 全部面板设置）");
            return sb.ToString();
        }

        private static void AppendFileInfo(StringBuilder sb, string path, string where)
        {
            if (!File.Exists(path)) { sb.AppendLine("  （没有）" + where); return; }
            FileInfo fi = new FileInfo(path);
            sb.AppendLine("  " + fi.Name + "  " + Core.Human(fi.Length) + "  " + fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm"));
            sb.AppendLine("     " + where);
        }

        // ------------------------------------------------------------ 保存

        /// <summary>把游戏目录里当前生效的两份配置存成一套存档。返回 null 表示成功。</summary>
        public static string Capture(string gameRoot, string name, out List<string> lines)
        {
            lines = new List<string>();
            name = (name ?? "").Trim();
            if (name.Length == 0) return "名字不能为空";
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "名字里有不能用作文件名的字符";
            if (name.StartsWith(".")) return "名字不能以点开头";

            string a = AutoexecInGame(gameRoot);
            string s = SkeetoInGame(gameRoot);
            bool hasA = File.Exists(a), hasS = File.Exists(s);
            if (!hasA && !hasS)
                return "游戏目录里既没有 left4dead2\\cfg\\autoexec.cfg，也没有 skeeto_setting.ini。\r\n先把游戏装上、参数调好，再来保存。";

            string dir = DirOf(name);
            try
            {
                // 同名直接覆盖：旧的那套不另存副本
                Directory.CreateDirectory(dir);
                string dstA = Path.Combine(dir, AutoexecName);
                string dstS = Path.Combine(dir, SkeetoName);
                // 这次游戏里没有的那份，必须把存档里的旧副本也清掉：
                // 否则用户已经把 autoexec 删了，存档里那份还躺着，下次 Apply 又给推回去。
                if (!hasA && File.Exists(dstA))
                {
                    try { File.Delete(dstA); lines.Add("游戏里已没有 autoexec.cfg，存档里的旧副本已清掉"); }
                    catch (Exception ex) { Core.Log("WARN", "旧的 autoexec 副本删不掉: " + ex.Message); }
                }
                if (!hasS && File.Exists(dstS))
                {
                    try { File.Delete(dstS); lines.Add("游戏里已没有 skeeto_setting.ini，存档里的旧副本已清掉"); }
                    catch (Exception ex) { Core.Log("WARN", "旧的 skeeto 副本删不掉: " + ex.Message); }
                }
                if (hasA) { Core.CopyOne(a, dstA); lines.Add("已存 autoexec.cfg（" + Core.Human(new FileInfo(a).Length) + "）"); }
                if (hasS) { Core.CopyOne(s, dstS); lines.Add("已存 skeeto_setting.ini（" + Core.Human(new FileInfo(s).Length) + "）"); }

                StringBuilder meta = new StringBuilder();
                meta.AppendLine("保存时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                meta.AppendLine("来源机器：" + Environment.MachineName);
                meta.AppendLine("来源目录：" + gameRoot);
                if (hasA) meta.AppendLine("autoexec.cfg：" + new FileInfo(a).Length + " 字节，" + File.ReadAllLines(a).Length + " 行");
                if (hasS) meta.AppendLine("skeeto_setting.ini：" + new FileInfo(s).Length + " 字节，" + File.ReadAllLines(s).Length + " 行");
                Core.WriteAllTextAtomic(Path.Combine(dir, InfoName), meta.ToString(), new UTF8Encoding(true));
                Core.Log("OK", "参数存档「" + name + "」已保存: " + ((hasA ? "autoexec " : "") + (hasS ? "skeeto " : "")).Trim());
                return null;
            }
            catch (Exception ex)
            {
                Core.Log("FAIL", "保存参数存档失败: " + ex.Message);
                return ex.Message;
            }
        }

        // ------------------------------------------------------------ 套用 / 还原

        /// <summary>把存档推到游戏目录（直接覆盖，不留副本）。返回 null 表示成功。</summary>
        public static string Apply(string gameRoot, string name, out List<string> lines)
        {
            lines = new List<string>();
            string dir = DirOf(name);
            if (!Directory.Exists(dir)) return "找不到存档「" + name + "」";

            string srcA = Path.Combine(dir, AutoexecName);
            string srcS = Path.Combine(dir, SkeetoName);
            if (!File.Exists(srcA) && !File.Exists(srcS)) return "存档「" + name + "」里没有可用文件";

            try
            {
                if (File.Exists(srcA)) Push(srcA, AutoexecInGame(gameRoot), "left4dead2\\cfg\\autoexec.cfg", lines);
                if (File.Exists(srcS)) Push(srcS, SkeetoInGame(gameRoot), "skeeto_setting.ini", lines);
                Core.Log("OK", "参数已按存档「" + name + "」写入");
                return null;
            }
            catch (Exception ex)
            {
                Core.Log("FAIL", "套用参数失败: " + ex.Message);
                return ex.Message;
            }
        }

        private static void Push(string src, string dst, string label, List<string> lines)
        {
            string dir = Path.GetDirectoryName(dst);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.Copy(src, dst, true);
            long len = new FileInfo(dst).Length;
            if (len != new FileInfo(src).Length) throw new IOException(label + " 写入后大小不对");
            lines.Add(label + "  已写入 " + Core.Human(len));
            Core.Log("OK", label + " 已按存档写入（覆盖）");
        }

        public static bool Delete(string name, out string err)
        {
            err = null;
            try
            {
                string dir = DirOf(name);
                if (!Directory.Exists(dir)) { err = "没有这个存档"; return false; }
                // 真删。U 盘上从别的机器拷来的文件可能带只读属性，先清掉再删
                foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(dir, true);
                Core.Log("OK", "参数存档「" + name + "」已删除（" + dir + "）");
                return true;
            }
            catch (Exception ex)
            {
                Core.Log("FAIL", "删除参数存档「" + name + "」失败: " + ex.Message);
                err = "删不掉：" + ex.Message + "\r\n（多半是文件被别的程序开着，或 U 盘写保护）";
                return false;
            }
        }

        // ------------------------------------------------------------ 自测

        /// <summary>
        /// 在临时目录里造一个假游戏，把 保存 → 套用 → 再套用 走一遍。
        /// 不碰真游戏目录，也不碰 U 盘上的真存档。返回失败项数。
        /// </summary>
        public static int SelfTest()
        {
            int bad = 0;
            string baseDir = Path.Combine(Path.GetTempPath(), "l4d2-tw-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            string game = Path.Combine(baseDir, "game");
            string stock = Path.GetTempPath() + "l4d2-tw-stock-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            RootOverride = stock;
            try
            {
                Directory.CreateDirectory(Path.Combine(Path.Combine(game, "left4dead2"), "cfg"));
                Directory.CreateDirectory(game);
                string cfg = AutoexecInGame(game), sk = SkeetoInGame(game);
                string original = "// 原版\r\nbind tab +showscores\r\n";
                File.WriteAllText(cfg, original, new UTF8Encoding(false));
                File.WriteAllText(sk, "old_setting=1\r\n", new UTF8Encoding(false));

                List<string> lines;
                string err = Capture(game, "自测套", out lines);
                if (err != null) { Core.Log("FAIL", "TWEAKSELF 保存失败: " + err); bad++; }
                if (!File.Exists(Path.Combine(DirOf("自测套"), AutoexecName))) { Core.Log("FAIL", "TWEAKSELF autoexec 没存进去"); bad++; }
                if (!File.Exists(Path.Combine(DirOf("自测套"), SkeetoName))) { Core.Log("FAIL", "TWEAKSELF skeeto 没存进去"); bad++; }

                // 模拟装机后：游戏里两份都被改成"存档里的样子"
                File.WriteAllText(cfg, "// 装机后\r\nbind tab +showscores\r\n", new UTF8Encoding(false));
                File.WriteAllText(sk, "old_setting=0\r\n", new UTF8Encoding(false));

                err = Apply(game, "自测套", out lines);
                if (err != null) { Core.Log("FAIL", "TWEAKSELF 套用失败: " + err); bad++; }
                if (File.ReadAllText(cfg) != original) { Core.Log("FAIL", "TWEAKSELF 套用后 autoexec 内容不对"); bad++; }
                if (File.ReadAllText(sk) != "old_setting=1\r\n") { Core.Log("FAIL", "TWEAKSELF 套用后 skeeto 内容不对"); bad++; }
                if (File.Exists(cfg + ".装之前")) { Core.Log("FAIL", "TWEAKSELF 还在留 .装之前 备份"); bad++; }

                // 第二次套用：直接覆盖，不留任何副本
                File.WriteAllText(cfg, "// 第二次装机前\r\n", new UTF8Encoding(false));
                Apply(game, "自测套", out lines);
                if (File.ReadAllText(cfg) != original) { Core.Log("FAIL", "TWEAKSELF 第二次套用没覆盖上"); bad++; }
                if (File.Exists(cfg + ".装之前") || File.Exists(cfg + ".上一次") || File.Exists(cfg + ".还原前"))
                { Core.Log("FAIL", "TWEAKSELF 套用后留下了副本"); bad++; }

                // 删除：必须是真删（目录没了），不是改个名留在原地
                string derr;
                if (!Delete("自测套", out derr)) { Core.Log("FAIL", "TWEAKSELF 删除失败: " + derr); bad++; }
                if (Exists("自测套")) { Core.Log("FAIL", "TWEAKSELF 删除后还能列出"); bad++; }
                if (Directory.Exists(DirOf("自测套"))) { Core.Log("FAIL", "TWEAKSELF 删除后目录还在：" + DirOf("自测套")); bad++; }

                Core.Log(bad == 0 ? "OK" : "FAIL", "TWEAKSELF " + (bad == 0 ? "参数保存/套用 全部正常" : (bad + " 项不对")));
            }
            catch (Exception ex) { Core.Log("FAIL", "TWEAKSELF 异常: " + ex.Message); bad++; }
            finally
            {
                RootOverride = null;
                try { Directory.Delete(baseDir, true); } catch { }
                try { Directory.Delete(stock, true); } catch { }
            }
            return bad;
        }

        /// <summary>
        /// 兼容用户以前手工建的「我的Skeeto设置」：第一次打开时把它导入成一套存档，
        /// 免得他还要再存一遍。
        /// </summary>
        public static void ImportLegacy()
        {
            ImportLegacy(null);
        }

        /// <summary>
        /// 第一次运行时把用户手工建的「我的Skeeto设置」导入成一套存档；
        /// 顺手从当前游戏目录把 autoexec.cfg 补进去 —— 光有 skeeto 的那套是半成品。
        /// </summary>
        public static void ImportLegacy(string gameRoot)
        {
            try
            {
                string legacy = Path.Combine(Core.PkgDir, "我的Skeeto设置");
                string dir = DirOf("我的设置");
                bool fresh = !(Directory.Exists(Root) && Directory.GetDirectories(Root).Length > 0);

                if (fresh && Directory.Exists(legacy))
                {
                    string ini = Path.Combine(legacy, SkeetoName);
                    if (File.Exists(ini))
                    {
                        Directory.CreateDirectory(dir);
                        File.Copy(ini, Path.Combine(dir, SkeetoName), true);
                        StringBuilder meta = new StringBuilder();
                        meta.AppendLine("保存时间：" + new FileInfo(ini).LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss") + "（从「我的Skeeto设置」导入）");
                        meta.AppendLine("来源机器：" + Environment.MachineName);
                        File.WriteAllText(Path.Combine(dir, InfoName), meta.ToString(), new UTF8Encoding(true));
                        Core.Log("INFO", "已把「我的Skeeto设置」导入为参数存档「我的设置」");
                        fresh = false;
                    }
                }

                // 这套只有 skeeto 没有 autoexec：从当前游戏目录补一份，否则下次装机 L4N 参数还是空的
                if (gameRoot != null && Directory.Exists(dir))
                {
                    string cfgInGame = AutoexecInGame(gameRoot);
                    string cfgInStock = Path.Combine(dir, AutoexecName);
                    if (File.Exists(cfgInGame) && !File.Exists(cfgInStock))
                    {
                        File.Copy(cfgInGame, cfgInStock, true);
                        try { File.AppendAllText(Path.Combine(dir, InfoName), "autoexec.cfg：" + new FileInfo(cfgInGame).Length + " 字节（从 " + gameRoot + " 补入）\r\n", new UTF8Encoding(true)); } catch { }
                        Core.Log("INFO", "参数存档「我的设置」补上了 autoexec.cfg（" + Core.Human(new FileInfo(cfgInGame).Length) + "）");
                    }
                }
            }
            catch (Exception ex) { Core.Log("WARN", "导入旧设置失败: " + ex.Message); }
        }
    }
}

namespace Installer
{
    /// <summary>
    /// 参数存档管理：存当前机器、套用到本机、还原成装机前、删掉不要的。
    /// 目的只有一个 —— 换台机器装机不用再手调一遍参数。
    /// </summary>
    public class TweakForm : Form
    {
        private string gameRoot;
        private ListBox lb;
        private TextBox txtPreview;
        private Label lblHead;
        private FlatButton btnApply, btnSave, btnDel;

        /// <summary>关窗时选中的存档名（给主界面记忆用）。</summary>
        public string Chosen = "";
        /// <summary>这次有没有真的往游戏目录写过东西。</summary>
        public bool Applied = false;

        public TweakForm(string root, string current)
        {
            gameRoot = root;
            Text = "参数配置（L4N 按键 / Skeeto 设置）";
            Theme.UseDpi(this);
            ClientSize = Dpi.Sz(664, 452);
            Font = Theme.UI;
            BackColor = Theme.Bg;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;

            lblHead = new Label();
            lblHead.SetBounds(12, 10, 640, 44);
            lblHead.Font = Theme.UISmall;
            lblHead.ForeColor = Theme.Ink2;
            lblHead.Text = "这个窗口管的是 U 盘上的参数存档（一个文件夹一套，位置在下面写着）。\r\n"
                + "「从这台机器存」= 把游戏目录里调好的两个文件抄进 U 盘；「套用到本机」= 反过来推回游戏目录。\r\n"
                + "存档位置：" + Tweaks.Root;
            Controls.Add(lblHead);

            lb = new ListBox();
            lb.SetBounds(12, 58, 200, 292);
            lb.BorderStyle = BorderStyle.FixedSingle;
            lb.BackColor = Theme.Surface;
            lb.ForeColor = Theme.Ink;
            lb.Font = Theme.UI;
            lb.IntegralHeight = false;
            lb.SelectedIndexChanged += delegate { ShowInfo(); lblHead.Refresh(); };
            Controls.Add(lb);

            txtPreview = new TextBox();
            txtPreview.SetBounds(224, 58, 428, 292);
            txtPreview.Multiline = true;
            txtPreview.ReadOnly = true;
            txtPreview.ScrollBars = ScrollBars.Vertical;
            txtPreview.WordWrap = true;
            txtPreview.BorderStyle = BorderStyle.FixedSingle;
            txtPreview.BackColor = Theme.Surface;
            txtPreview.ForeColor = Theme.Ink2;
            txtPreview.Font = Theme.UISmall;
            Controls.Add(txtPreview);

            btnSave = new FlatButton(); btnSave.Text = "把本机参数存进U盘"; btnSave.SetBounds(12, 362, 152, Theme.H);
            btnSave.Click += delegate { DoSave(); }; Controls.Add(btnSave);

            btnApply = new FlatButton(); btnApply.Text = "套用到本机"; btnApply.Primary = true; btnApply.SetBounds(170, 362, 108, Theme.H);
            btnApply.Click += delegate { DoApply(); }; Controls.Add(btnApply);

            btnDel = new FlatButton(); btnDel.Text = "删除存档"; btnDel.SetBounds(284, 362, 88, Theme.H);
            btnDel.Click += delegate { DoDelete(); }; Controls.Add(btnDel);

            FlatButton btnOpen = new FlatButton(); btnOpen.Text = "打开文件夹"; btnOpen.SetBounds(488, 362, 92, Theme.H);
            btnOpen.Click += delegate { try { Directory.CreateDirectory(Tweaks.Root); System.Diagnostics.Process.Start("explorer.exe", Tweaks.Root); } catch { } }; Controls.Add(btnOpen);

            FlatButton btnClose = new FlatButton(); btnClose.Text = "关闭"; btnClose.SetBounds(586, 362, 54, Theme.H);
            btnClose.DialogResult = DialogResult.Cancel; Controls.Add(btnClose);
            CancelButton = btnClose;

            Label tip = new Label();
            tip.SetBounds(12, 396, 640, 52);
            tip.Font = Theme.UISmall;
            tip.ForeColor = Theme.Ink3;
            tip.Text = "左边列表 = U 盘上的存档文件夹（" + Tweaks.Root + "）。\r\n"
                + "「套用到本机」直接覆盖游戏目录里的那两个参数文件，不留备份 —— 覆盖就是覆盖了。\r\n"
                + "「删除存档」是把 U 盘上那套文件夹整个删掉；同名再存一次会直接覆盖旧的那套。";
            Controls.Add(tip);

            Reload(current);
        }

        private void Reload(string select)
        {
            Tweaks.ImportLegacy(gameRoot);
            lb.Items.Clear();
            foreach (string n in Tweaks.List()) lb.Items.Add(n);
            if (lb.Items.Count == 0)
            {
                txtPreview.Text = "U 盘上还没有参数存档。\r\n\r\n先把游戏装好、参数调成你想要的样子，\r\n再点下面的「把本机参数存进U盘」，\r\n就会在\r\n" + Tweaks.Root + "\r\n下面建一个文件夹存起来。\r\n之后换机器装机时勾上「写参数配置」就会自动写进去。";
                Selected(null);
                return;
            }
            int idx = 0;
            for (int i = 0; i < lb.Items.Count; i++)
                if (string.Equals(lb.Items[i].ToString(), select, StringComparison.OrdinalIgnoreCase)) idx = i;
            lb.SelectedIndex = idx;
            ShowInfo();
        }

        private string Sel()
        {
            return lb.SelectedItem == null ? null : lb.SelectedItem.ToString();
        }

        private void Selected(string name)
        {
            Chosen = name ?? "";
            bool has = name != null;
            btnApply.Enabled = has;
            btnDel.Enabled = has;
        }

        private void ShowInfo()
        {
            string name = Sel();
            Selected(name);
            if (name == null) return;
            txtPreview.Text = Tweaks.Info(name);
        }

        private void DoSave()
        {
            if (gameRoot == null || gameRoot.Length == 0 || !Core.TestGame(gameRoot))
            {
                MessageBox.Show("先在上面选对游戏目录。", "没有游戏目录");
                return;
            }
            string def = Sel();
            if (def == null || def.Length == 0) def = "机器-" + Environment.MachineName;
            string name = PromptForm.Ask(
                "会把这台机器游戏目录里的两份参数抄进 U 盘：\r\n"
                + "  left4dead2\\cfg\\autoexec.cfg（L4N 按键 / 网络 / 亮度）\r\n"
                + "  skeeto_setting.ini（Skeeto 面板设置）\r\n\r\n"
                + "给这套起个名字，U 盘存档里会建一个同名文件夹：\r\n"
                + Tweaks.Root + "\\<名字>\r\n"
                + "（同名再存一次会直接覆盖旧的那套）",
                "把本机参数存进 U 盘", def);
            if (string.IsNullOrEmpty(name)) return;
            List<string> lines;
            string err = Tweaks.Capture(gameRoot, name.Trim(), out lines);
            if (err != null) { MessageBox.Show(err, "没存成"); return; }
            Reload(name.Trim());
            MessageBox.Show("已存进 U 盘：\r\n" + Tweaks.DirOf(name.Trim()) + "\r\n\r\n" + string.Join("\r\n", lines.ToArray()), "存好了");
        }

        private void DoApply()
        {
            string name = Sel();
            if (name == null) return;
            if (gameRoot == null || !Core.TestGame(gameRoot)) { MessageBox.Show("先选对游戏目录。"); return; }
            if (MessageBox.Show("把「" + name + "」里的参数写入这台机器？\r\n\r\nleft4dead2\\cfg\\autoexec.cfg\r\nskeeto_setting.ini\r\n\r\n现在这两份会被直接覆盖，不留副本。", "套用参数",
                                MessageBoxButtons.OKCancel, MessageBoxIcon.Question,
                                MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            List<string> lines;
            string err = Tweaks.Apply(gameRoot, name, out lines);
            if (err != null) { MessageBox.Show(err, "没套上"); return; }
            Applied = true;
            MessageBox.Show("已套用：\r\n" + string.Join("\r\n", lines.ToArray()), "好了");
            ShowInfo();
        }

        private void DoDelete()
        {
            string name = Sel();
            if (name == null) return;
            string dir = Tweaks.DirOf(name);
            if (MessageBox.Show("彻底删掉存档「" + name + "」？\r\n\r\n会删掉 U 盘上的整个目录（autoexec.cfg + skeeto_setting.ini）：\r\n" + dir
                + "\r\n\r\n删了没法恢复。", "删除参数存档", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            string err;
            if (!Tweaks.Delete(name, out err)) { MessageBox.Show(err); return; }
            Reload(null);
            if (lb.Items.Count > 0) lb.SelectedIndex = 0; else Selected(null);
        }
    }
}

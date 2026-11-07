using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace Installer
{
    /// <summary>
    /// 「体积完全相同」到底是不是同一个文件：统一比 CRC32（就是 rar/zip 里存的那个校验值，算的是解压后的内容）。
    /// 独立 vpk 现算、rar 里的直接从包目录读、zip 里的解压流上算 —— 三者可直接比较。
    /// 早先版本对「文件 vs 包内」用不同算法（SHA256 vs CRC32），结果永远显示"内容不同"，别再用。
    /// </summary>
    internal static class DupVerify
    {
        static readonly Dictionary<string, Dictionary<string, string>> crcCache =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        static readonly uint[] tbl = BuildTable();

        static uint[] BuildTable()
        {
            uint[] t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
                t[i] = c;
            }
            return t;
        }

        static string Crc32(Stream s)
        {
            uint c = 0xFFFFFFFFu;
            byte[] buf = new byte[1 << 20];      // 1 MB：U 盘上这个块大小最划算
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0)
                for (int i = 0; i < n; i++) c = tbl[(c ^ buf[i]) & 0xFF] ^ (c >> 8);
            return (c ^ 0xFFFFFFFFu).ToString("X8");
        }

        /// <summary>返回内容指纹（CRC32）；算不出来（文件被占用/包有问题）返回 null 并给出说明。</summary>
        public static string Fingerprint(ModItem m, out string note)
        {
            note = "";
            try
            {
                if (!m.IsArchive)
                {
                    using (FileStream fs = new FileStream(m.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        return Crc32(fs);
                }

                string ext = Path.GetExtension(m.Container).ToLowerInvariant();
                if (ext == ".rar" || ext == ".7z")
                {
                    Dictionary<string, string> table;
                    if (!crcCache.TryGetValue(m.FullPath, out table))
                    {
                        table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        string err;
                        foreach (ArcEntry e in Catalog.ListArchive(m.FullPath, out err))
                            if (e.Crc != null) table[e.Inner] = e.Crc;
                        crcCache[m.FullPath] = table;
                    }
                    string crc;
                    if (table.TryGetValue(m.Inner, out crc)) return crc;
                    // tar -tvf 不吐 CRC32，7z 的目录表里没有这一项：解出来自己算一遍。
                    // 解压很贵，所以算完回填 table，同一次运行里不再解第二遍。
                    if (ext == ".7z")
                    {
                        string tmp = Path.Combine(Path.GetTempPath(), "l4d2crc-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                        try
                        {
                            Directory.CreateDirectory(tmp);
                            string one = Path.Combine(tmp, "entry.vpk");
                            string why;
                            if (!Catalog.ExtractEntryTo(m.FullPath, m.Inner, one, out why))
                            {
                                note = "7z 解不开：" + why;
                                return null;
                            }
                            using (FileStream fs = new FileStream(one, FileMode.Open, FileAccess.Read, FileShare.Read))
                                crc = Crc32(fs);
                            table[m.Inner] = crc;
                            Core.Log("INFO", "DUPFP 7z 无目录表 CRC，解出来算：" + m.Name + " = CRC32 " + crc);
                            return crc;
                        }
                        catch (Exception ex) { note = ex.Message; return null; }
                        finally { try { Directory.Delete(tmp, true); } catch { } }
                    }
                    note = "包里读不到 CRC32";
                    return null;
                }

                using (System.IO.Compression.ZipArchive za = System.IO.Compression.ZipFile.OpenRead(m.FullPath))
                {
                    System.IO.Compression.ZipArchiveEntry e = za.GetEntry(m.Inner.Replace('\\', '/'));
                    if (e == null) { note = "zip 里找不到这个条目"; return null; }
                    using (Stream s = e.Open()) return Crc32(s);
                }
            }
            catch (Exception ex) { note = ex.Message; return null; }
        }

        /// <summary>核对一组：所有能算的指纹都一样 → same；出现不同 → diff；都算不出来 → unknown。</summary>
        public static string Check(DupGroup g, out string note)
        {
            note = "";
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            int unknown = 0;
            foreach (ModItem m in g.Items)
            {
                string why;
                string fp = Fingerprint(m, out why);
                Core.Log("INFO", "DUPFP " + m.Name + " = " + (fp == null ? "（算不出：" + why + "）" : "CRC32 " + fp)
                    + "  [" + (m.IsArchive ? Path.GetFileName(m.Container) + " › " + m.Inner : m.RelPath) + "]");
                if (fp == null) { unknown++; if (note.Length == 0) note = m.Name + "：" + why; continue; }
                seen.Add(fp);
            }
            if (seen.Count == 0) { note = "都算不出来：" + note; return "unknown"; }
            if (seen.Count > 1) return "diff";
            // 只核对了其中一部分，不能把“能读的几份一样”升级成“整组完全相同”。
            // 这是删除前置判断，宁可多一次人工确认，也不能给错结论。
            if (unknown > 0) { note = "有 " + unknown + " 个没核对成功"; return "partial"; }
            return "same";
        }

        /// <summary>自测：两个文件内容一样要判 same，差一个字节要判 diff。返回失败条数。</summary>
        public static int SelfTest()
        {
            int bad = 0;
            string dir = Path.Combine(Path.GetTempPath(), "dupself-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                byte[] data = new byte[1200000];
                new Random(7).NextBytes(data);
                File.WriteAllBytes(Path.Combine(dir, "a.vpk"), data);
                File.WriteAllBytes(Path.Combine(dir, "b.vpk"), data);

                DupGroup g = new DupGroup();
                g.Size = data.Length;
                g.Items.Add(TempItem(dir, "a.vpk", data.Length));
                g.Items.Add(TempItem(dir, "b.vpk", data.Length));

                string note;
                string v1 = Check(g, out note);
                if (v1 != "same") { bad++; Core.Log("FAIL", "DUPSELF 同样的两份应该判 same，实际 " + v1); }
                else Core.Log("OK", "DUPSELF 同样的两份 → same");

                // 有一份读不出来时，绝不能把“能读的两份一样”当成整组相同；这是删除前判断。
                ModItem missing = TempItem(dir, "missing.vpk", data.Length);
                g.Items.Add(missing);
                string vPartial = Check(g, out note);
                if (vPartial != "partial") { bad++; Core.Log("FAIL", "DUPSELF 有文件读不出应判 partial，实际 " + vPartial); }
                else Core.Log("OK", "DUPSELF 部分文件读不出 → partial");
                g.Items.Remove(missing);

                data[100] ^= 0xFF;
                File.WriteAllBytes(Path.Combine(dir, "b.vpk"), data);
                string v2 = Check(g, out note);
                if (v2 != "diff") { bad++; Core.Log("FAIL", "DUPSELF 差一个字节应该判 diff，实际 " + v2); }
                else Core.Log("OK", "DUPSELF 差一个字节 → diff");

                // 大小不足 1 MB 的组本来就不该出现在重复嫌疑里
                List<ModItem> tiny = new List<ModItem>();
                tiny.Add(TempItem(dir, "a.vpk", 100));
                if (ConflictCheck.DupGroups(tiny).Count != 0) { bad++; Core.Log("FAIL", "DUPSELF 小文件不该报重复"); }
                else Core.Log("OK", "DUPSELF 小文件不报重复");
            }
            catch (Exception ex) { bad++; Core.Log("FAIL", "DUPSELF 异常: " + ex.Message); }
            finally { try { Directory.Delete(dir, true); } catch { } }
            return bad;
        }

        static ModItem TempItem(string dir, string name, long size)
        {
            ModItem m = new ModItem();
            m.Name = name;
            m.RelPath = name;
            m.FullPath = Path.Combine(dir, name);
            m.Size = size;
            return m;
        }
    }

    /// <summary>
    /// 重复嫌疑清单。原来只能弹一个文本框，看得见却动不了；现在每一组能：
    /// 核对内容 / 这次不装 / 直接删掉 / 在列表里定位。
    /// 压缩包里的 vpk 本工具不改压缩包，所以只能「这次不装」。
    /// </summary>
    public class DupForm : Form
    {
        public Func<ModItem, string> OnDelete;         // 真删；返回 null = 成功，否则是错误说明
        public Action<List<ModItem>> OnSkip;           // 这次不装（只影响本次勾选）
        public Action<ModItem> OnLocate;               // 在 MOD 列表里定位

        private List<DupGroup> groups;
        private List<string> others;
        private ListView lv;
        private Label status;
        private Label lblOthers;
        private long freedomBytes;

        public DupForm(List<DupGroup> dupGroups, List<string> otherMsgs)
        {
            groups = dupGroups;
            others = otherMsgs == null ? new List<string>() : otherMsgs;

            Text = "重复嫌疑 · " + groups.Count + " 组";
            ClientSize = new Size(820, 560);
            Font = Theme.UI;
            BackColor = Theme.Bg;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            MaximizeBox = true;

            Label hint = new Label();
            hint.SetBounds(14, 10, 792, 36);
            hint.Font = Theme.UISmall;
            hint.ForeColor = Theme.Ink2;
            hint.Text = "体积完全相同只是「嫌疑」，不一定是同一个文件 —— 先点「核对内容」再看结论，一样就可以删。\r\n"
                      + "勾选要处理的（可多选）：这次不装 = 文件还留在 U 盘，只是这次不铺；直接删掉 = 从库里真删，删了没法恢复。";
            Controls.Add(hint);

            lv = new ListView();
            lv.SetBounds(14, 50, 792, 388);
            lv.View = View.Details;
            lv.CheckBoxes = true;
            lv.FullRowSelect = true;
            lv.HideSelection = false;
            lv.GridLines = false;
            lv.Font = Theme.UI;
            lv.BackColor = Theme.Surface;
            lv.ForeColor = Theme.Ink;
            lv.Columns.Add("名字", 300);
            lv.Columns.Add("大小", 76);
            lv.Columns.Add("分类", 150);
            lv.Columns.Add("位置", 258);
            lv.ItemCheck += delegate(object s, ItemCheckEventArgs e)
            {
                if (e.Index < 0 || e.Index >= lv.Items.Count) return;
                ModItem it = lv.Items[e.Index].Tag as ModItem;
                if (it == null) return;
                // 压缩包里的勾上也没用（不能删），但允许「这次不装」，所以不拦，只给提示
                if (it.IsArchive && e.NewValue == CheckState.Checked)
                    status.Text = "「" + it.Name + "」在压缩包里，只能「这次不装」，不能删（本工具不改压缩包）";
            };
            lv.ItemSelectionChanged += delegate { UpdateButtons(); };
            Controls.Add(lv);

            status = new Label();
            status.SetBounds(14, 444, 792, 20);
            status.Font = Theme.UISmall;
            status.ForeColor = Theme.Ink3;
            Controls.Add(status);

            FlatButton bVerify = new FlatButton(); bVerify.Text = "核对内容";
            bVerify.SetBounds(14, 470, 96, Theme.H);
            bVerify.Click += delegate { Verify(); };
            tipsSet(bVerify, "算一遍内容指纹（独立 vpk、zip / rar 内条目统一用 CRC32）。\r\n只有整组都核对成功且一致，才会标成“内容完全相同”。");
            Controls.Add(bVerify);

            FlatButton bSkip = new FlatButton(); bSkip.Text = "这次不装";
            bSkip.SetBounds(118, 470, 96, Theme.H);
            bSkip.Click += delegate { SkipChecked(); };
            tipsSet(bSkip, "把勾选的从这次的勾选里去掉，文件原样留在 U 盘。");
            Controls.Add(bSkip);

            FlatButton bDel = new FlatButton(); bDel.Text = "直接删掉";
            bDel.SetBounds(222, 470, 110, Theme.H);
            bDel.Click += delegate { DeleteChecked(); };
            tipsSet(bDel, "从 U 盘库里真删（删了没法恢复）。\r\n压缩包里的 vpk 不支持，请用「这次不装」。");
            Controls.Add(bDel);

            FlatButton bLoc = new FlatButton(); bLoc.Text = "在列表里定位";
            bLoc.SetBounds(370, 470, 112, Theme.H);
            bLoc.Click += delegate { Locate(); };
            Controls.Add(bLoc);

            FlatButton bClose = new FlatButton(); bClose.Text = "关闭";      // 不用 Primary：这屏的主动作是核对/处理，不是关窗
            bClose.SetBounds(716, 470, 90, Theme.H);
            bClose.DialogResult = DialogResult.Cancel;
            Controls.Add(bClose);
            CancelButton = bClose;

            lblOthers = new Label();
            lblOthers.SetBounds(14, 506, 792, 32);
            lblOthers.Font = Theme.UISmall;
            lblOthers.ForeColor = Theme.Ink3;
            Controls.Add(lblOthers);

            Rebuild();
        }

        private ToolTip tp = new ToolTip();
        private void tipsSet(Control c, string s) { tp.SetToolTip(c, s); }

        // 列表里所有项都是 ModItem；组头显示体积和个数
        private void Rebuild()
        {
            lv.BeginUpdate();
            lv.Items.Clear();
            lv.Groups.Clear();
            freedomBytes = 0;
            int rows = 0;
            foreach (DupGroup g in groups)
            {
                if (g.Items.Count < 2) continue;
                string head = Core.Human(g.Size) + " · " + g.Items.Count + " 个 · "
                            + (g.Verdict == "same" ? "内容完全相同（可以删）"
                               : g.Verdict == "diff" ? "内容不同（不是重复）"
                               : g.Verdict == "partial" ? "只核对了部分文件（不能判定相同）"
                               : g.Verdict == "unknown" ? "没核对成功"
                               : "可能重复");
                ListViewGroup lg = new ListViewGroup(head);
                lv.Groups.Add(lg);
                foreach (ModItem m in g.Items)
                {
                    ListViewItem li = new ListViewItem(m.Name);
                    li.SubItems.Add(Core.Human(m.Size));
                    li.SubItems.Add(m.Cat + (m.Sub == null || m.Sub.Length == 0 ? "" : " · " + m.Sub));
                    li.SubItems.Add(Where(m));
                    li.Tag = m;
                    li.Group = lg;
                    if (g.Verdict == "diff") li.ForeColor = Theme.Ink3;      // 不是重复的，灰掉
                    lv.Items.Add(li);
                    rows++;
                }
                if (g.Items.Count > 1) freedomBytes += g.Size * (g.Items.Count - 1);
            }
            lv.EndUpdate();

            lblOthers.Text = others.Count == 0
                ? "另有同名文件 / 已装文件等提示见日志（右键菜单「清理同名重复」可批量处理同名）。"
                : ("另外还有 " + others.Count + " 条其它提示：" + others[0] + (others.Count > 1 ? " 等" : ""));

            if (rows == 0)
            {
                status.Text = "已经没有重复嫌疑了。";
                status.ForeColor = Theme.Ok;
            }
            else
            {
                status.Text = "共 " + rows + " 个文件、" + groups.Count + " 组；全删掉重复那份最多能省 "
                            + Core.Human(freedomBytes) + "。";
                status.ForeColor = Theme.Ink3;
            }
            UpdateButtons();
        }

        private static string Where(ModItem m)
        {
            if (!m.IsArchive) return m.RelPath;
            return Path.GetFileName(m.Container) + " › " + m.Inner;
        }

        private void UpdateButtons()
        {
            // 只做提示，不做禁用：按钮禁用会让人不知道为什么点不动
        }

        private List<ModItem> CheckedItems()
        {
            List<ModItem> list = new List<ModItem>();
            foreach (ListViewItem li in lv.CheckedItems)
            {
                ModItem m = li.Tag as ModItem;
                if (m != null) list.Add(m);
            }
            return list;
        }

        private List<DupGroup> TargetGroups()
        {
            // 有勾选就只核对勾选所在的组，没勾选就全核对
            List<DupGroup> res = new List<DupGroup>();
            List<ModItem> picked = CheckedItems();
            if (picked.Count == 0) return new List<DupGroup>(groups);
            foreach (DupGroup g in groups)
                foreach (ModItem m in g.Items)
                    if (picked.Contains(m)) { res.Add(g); break; }
            return res;
        }

        private void Verify()
        {
            List<DupGroup> targets = TargetGroups();
            if (targets.Count == 0) return;
            Cursor = Cursors.WaitCursor;
            try
            {
                int same = 0, diff = 0, unk = 0, partial = 0, i = 0;
                foreach (DupGroup g in targets)
                {
                    i++;
                    // 大文件要读几十秒，得让人知道在干什么
                    status.Text = "正在核对第 " + i + "/" + targets.Count + " 组（" + Core.Human(g.Size) + " × " + g.Items.Count + "）…";
                    status.ForeColor = Theme.Ink3;
                    status.Refresh();
                    Application.DoEvents();
                    string note;
                    g.Verdict = DupVerify.Check(g, out note);
                    g.VerdictNote = note;
                    if (g.Verdict == "same") same++;
                    else if (g.Verdict == "diff") diff++;
                    else if (g.Verdict == "partial") partial++;
                    else unk++;
                    Core.Log("INFO", "DUPCHECK " + Core.Human(g.Size) + " x" + g.Items.Count + " → "
                        + (g.Verdict == "same" ? "内容完全相同"
                            : g.Verdict == "diff" ? "内容不同"
                            : g.Verdict == "partial" ? "部分文件未核对成功"
                            : "没核对成功")
                        + (note.Length > 0 ? "（" + note + "）" : ""));
                }
                Rebuild();
                status.Text = "核对完 " + targets.Count + " 组：完全相同 " + same + " 组，内容不同 " + diff
                            + " 组" + (partial > 0 ? "，" + partial + " 组只核对了部分文件" : "")
                            + (unk > 0 ? "，" + unk + " 组没核对成功" : "") + "。";
                status.ForeColor = diff > 0 ? Theme.Warn : Theme.Ok;
            }
            finally { Cursor = Cursors.Default; }
        }

        private void SkipChecked()
        {
            List<ModItem> list = CheckedItems();
            if (list.Count == 0) { MessageBox.Show(this, "先勾选要处理的文件。", "没勾选"); return; }
            if (OnSkip != null) OnSkip(list);
            // CheckedItems 是活集合，边遍历边改勾选会抛「集合已修改」，先抄一份
            List<ListViewItem> rows = new List<ListViewItem>();
            foreach (ListViewItem li in lv.CheckedItems) rows.Add(li);
            foreach (ListViewItem li in rows) li.Checked = false;
            status.Text = "已设为这次不装 " + list.Count + " 个（文件还在 U 盘，下次还能选）。";
            status.ForeColor = Theme.Ok;
        }

        private void DeleteChecked()
        {
            List<ModItem> picked = CheckedItems();
            if (picked.Count == 0) { MessageBox.Show(this, "先勾选要处理的文件。", "没勾选"); return; }

            List<ModItem> ok = new List<ModItem>();
            int blocked = 0;
            foreach (ModItem m in picked)
            {
                if (m.IsArchive) blocked++;
                else ok.Add(m);
            }
            if (ok.Count == 0)
            {
                MessageBox.Show(this,
                    "勾选的全在压缩包里（" + blocked + " 个）。\r\n\r\n"
                    + "本工具不改 U 盘上的压缩包，所以删不掉里面的单个 vpk。\r\n"
                    + "能用的是「这次不装」；真要删，只能解开压缩包重新打一个。", "不能删除",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            StringBuilder sb = new StringBuilder();
            long total = 0;
            foreach (ModItem m in ok) { sb.AppendLine("· " + m.Name + "    " + Where(m)); total += m.Size; }

            int unverified = 0;
            foreach (DupGroup g in groups)
            {
                if (g.Verdict == "same") continue;
                foreach (ModItem m in ok) if (g.Items.Contains(m)) { unverified++; break; }
            }

            // 危险操作：把要动的每一个文件列出来，让人看清楚再点
            if (MessageBox.Show(this,
                (unverified > 0 ? "注意：其中 " + unverified + " 组还没有确认内容完全相同。\r\n\r\n" : "")
                + "把下面 " + ok.Count + " 个文件（合计 " + Core.Human(total) + "）从 U 盘库直接删掉？\r\n\r\n"
                + sb.ToString() + "\r\n"
                + (blocked > 0 ? "另有 " + blocked + " 个在压缩包里，这次不会动。\r\n\r\n" : "")
                + "删了没法恢复。",
                unverified > 0 ? "确认删除 · 有未核对项" : "确认删除",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

            int done = 0;
            List<string> errs = new List<string>();
            foreach (ModItem m in ok)
            {
                string err = OnDelete == null ? "没有删除回调" : OnDelete(m);
                if (err == null) { done++; RemoveFromGroups(m); }
                else errs.Add(m.Name + "：" + err);
            }
            Rebuild();
            status.Text = "已删除 " + done + " 个" + (errs.Count > 0 ? "，失败 " + errs.Count + " 个（见日志）" : "") + "。";
            status.ForeColor = errs.Count > 0 ? Theme.Warn : Theme.Ok;
        }

        private void RemoveFromGroups(ModItem m)
        {
            foreach (DupGroup g in groups) g.Items.Remove(m);
            groups.RemoveAll(delegate(DupGroup g) { return g.Items.Count < 2; });
        }

        private void Locate()
        {
            if (lv.SelectedItems.Count == 0) { MessageBox.Show(this, "先在列表里点一行。", "没选中"); return; }
            ModItem m = lv.SelectedItems[0].Tag as ModItem;
            if (m == null) return;
            if (OnLocate != null) OnLocate(m);
            // 定位完就关掉：主列表在底下，留着这个窗口反而挡住刚选中的那一行
            DialogResult = DialogResult.Cancel;
            Close();
        }

        /// <summary>自测用：把当前所有组都核一遍内容。</summary>
        public void VerifyAll()
        {
            Verify();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Installer
{
    /// <summary>
    /// 一个 mod 由多个 vpk 组成时，让用户挑这次装哪几个。
    /// 主体/必装默认勾上，颜色这种互斥的每套只勾一个，可选件默认不勾。
    /// </summary>
    public class PartsForm : Form
    {
        private CheckedListBox lb;
        private Label lblHint;
        private List<Row> rows = new List<Row>();
        private ModGroup g;

        public List<ModItem> Result = new List<ModItem>();

        private sealed class Row
        {
            public ModItem It;
            public string Text;
            public override string ToString() { return Text; }
        }

        public PartsForm(ModGroup group, HashSet<string> checkedKeys)
        {
            g = group;
            Text = "选择部件 — " + group.Name;
            Theme.UseDpi(this);
            ClientSize = Dpi.Sz(620, 468);
            Font = Theme.UI;
            BackColor = Theme.Bg;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;

            lblHint = new Label();
            lblHint.SetBounds(12, 10, 596, 42);
            bool hasChoice = false;
            foreach (ModItem p in group.Parts) if (p.Role == "可选" || p.Role == "选配") hasChoice = true;
            lblHint.Text = "分类：" + group.Where + "　·　这套由 " + group.Parts.Count + " 个 vpk 组成\r\n" +
                           (hasChoice
                                ? "主体和必装件默认勾上；颜色（选配）每套只勾一个；可选件按需勾。取消必装件可能导致模型不完整。"
                                : "这套没有可选项：所有文件必须一起装，少一个就会缺件（分卷地图、分段模型都是这样）。");
            lblHint.Font = Theme.UISmall;
            lblHint.ForeColor = Theme.Ink2;
            Controls.Add(lblHint);

            lb = new CheckedListBox();
            lb.SetBounds(12, 56, 596, 344);
            lb.BorderStyle = BorderStyle.FixedSingle;
            lb.BackColor = Theme.Surface;
            lb.ForeColor = Theme.Ink;
            lb.Font = Theme.UI;
            lb.CheckOnClick = true;
            lb.IntegralHeight = false;
            Controls.Add(lb);

            bool any = false;
            foreach (ModItem p in group.Parts) if (checkedKeys.Contains(p.Key)) any = true;
            List<ModItem> preset = any ? null : Catalog.DefaultParts(group);

            foreach (ModItem p in group.Parts)
            {
                Row r = new Row();
                r.It = p;
                r.Text = "[" + p.Role + "]  " + p.Name + "    " + Core.Human(p.Size)
                       + (p.IsArchive ? "    （压缩包内）" : "");
                rows.Add(r);
                bool on = any ? checkedKeys.Contains(p.Key) : preset.Contains(p);
                lb.Items.Add(r, on);
            }

            FlatButton all = new FlatButton(); all.Text = "全选"; all.SetBounds(12, 410, 76, Theme.H);
            all.Click += delegate { SetAll(true); }; Controls.Add(all);
            FlatButton def = new FlatButton(); def.Text = "只留默认"; def.SetBounds(96, 410, 88, Theme.H);
            def.Click += delegate { SetPreset(); }; Controls.Add(def);
            FlatButton none = new FlatButton(); none.Text = "都不选"; none.SetBounds(192, 410, 76, Theme.H);
            none.Click += delegate { SetAll(false); }; Controls.Add(none);

            FlatButton ok = new FlatButton(); ok.Text = "确定"; ok.Primary = true;
            ok.SetBounds(432, 410, 80, Theme.H);
            ok.Click += delegate { Accept(); }; Controls.Add(ok);
            FlatButton no = new FlatButton(); no.Text = "取消"; no.SetBounds(520, 410, 88, Theme.H);
            no.DialogResult = DialogResult.Cancel; Controls.Add(no);
            AcceptButton = ok;
            CancelButton = no;
        }

        private void SetAll(bool on)
        {
            for (int i = 0; i < lb.Items.Count; i++) lb.SetItemChecked(i, on);
        }

        private void SetPreset()
        {
            List<ModItem> preset = Catalog.DefaultParts(g);
            for (int i = 0; i < rows.Count; i++) lb.SetItemChecked(i, preset.Contains(rows[i].It));
        }

        private void Accept()
        {
            Result.Clear();
            for (int i = 0; i < rows.Count; i++)
                if (lb.GetItemChecked(i)) Result.Add(rows[i].It);
            if (Result.Count == 0 &&
                MessageBox.Show("一个部件都没选，这样装不进去任何东西。仍然确定？", "确认",
                                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}

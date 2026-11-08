using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Installer
{
    // ============================================================
    //  「我的分支」：程序认不出改的是什么的 MOD，用户自己拉一个分组装进去。
    //
    //  只干三件事：记住有哪些分支、每个分支里有哪几个 vpk、某个 vpk 在哪一支。
    //  不碰文件、不改自动分类、不影响 addonlist —— 装机时照常按「勾没勾」走。
    //  一个 MOD 只归一个分支（再放一次就当换分支），免得同一个东西在两个分支里都被勾着。
    // ============================================================

    internal static class MyBranch
    {
        private static bool loaded;
        private static readonly List<string> names = new List<string>();
        private static readonly Dictionary<string, List<string>> items =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> ofMod =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // 子分支 → 父分支。没有这一项 = 顶层分支。名字全局唯一，所以挂在哪一层只影响显示。
        private static readonly Dictionary<string, string> parentOf =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string FileOf()
        {
            return string.IsNullOrEmpty(Core.PkgDir) ? "" : Path.Combine(Core.PkgDir, "我的分支.ini");
        }

        public static void Ensure() { if (!loaded) Load(); }

        /// <summary>自测 / 外部改过文件之后重读。</summary>
        public static void Reload() { loaded = false; Ensure(); }

        private static void Load()
        {
            loaded = true;
            names.Clear(); items.Clear(); ofMod.Clear(); parentOf.Clear();
            try
            {
                string f = FileOf();
                if (File.Exists(f))
                    foreach (string raw in File.ReadAllLines(f, Encoding.UTF8))
                    {
                        string s = raw.TrimEnd();
                        if (s.Length == 0 || s[0] == '#') continue;
                        if (s.StartsWith("branch=", StringComparison.Ordinal))
                        {
                            string v = s.Substring(7).Trim();
                            if (v.Length > 0 && !names.Contains(v)) names.Add(v);
                        }
                        else if (s.StartsWith("mod=", StringComparison.Ordinal))
                        {
                            string[] p = s.Substring(4).Split('\t');
                            if (p.Length < 2) continue;
                            string b = p[0].Trim(), vpk = p[1].Trim();
                            if (b.Length == 0 || vpk.Length == 0) continue;
                            if (!names.Contains(b)) names.Add(b);
                            ListOf(b).Add(vpk);
                            ofMod[vpk] = b;
                        }
                        else if (s.StartsWith("parent=", StringComparison.Ordinal))
                        {
                            string[] p = s.Substring(7).Split('\t');
                            if (p.Length < 2) continue;
                            string c = p[0].Trim(), pa = p[1].Trim();
                            if (c.Length == 0 || pa.Length == 0 || c == pa) continue;
                            parentOf[c] = pa;
                        }
                    }
            }
            catch { }
            NormalizeParents();
        }

        /// <summary>父分支不存在、或绕成圈了，就当顶层处理 —— 坏文件不能让整棵树崩掉。</summary>
        private static void NormalizeParents()
        {
            List<string> keys = new List<string>(parentOf.Keys);
            foreach (string k in keys)
            {
                if (!names.Contains(k)) { parentOf.Remove(k); continue; }
                string p;
                if (!parentOf.TryGetValue(k, out p)) continue;
                if (!names.Contains(p) || p == k) { parentOf.Remove(k); continue; }
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                seen.Add(k);
                string cur = p;
                bool bad = false;
                for (int guard = 0; guard < 32; guard++)
                {
                    if (!seen.Add(cur)) { bad = true; break; }
                    string up;
                    if (!parentOf.TryGetValue(cur, out up)) break;
                    cur = up;
                }
                if (bad) parentOf.Remove(k);
            }
        }

        private static void Save()
        {
            string f = FileOf();
            if (string.IsNullOrEmpty(f)) return;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# 我的分支：用户自己拉的分组（本工具自己写，手改也行）");
            sb.AppendLine("# branch=分支名 / parent=子分支 <TAB> 父分支 / mod=分支名 <TAB> vpk 文件名");
            foreach (string n in Names()) sb.AppendLine("branch=" + n);
            foreach (KeyValuePair<string, string> kv in parentOf) sb.AppendLine("parent=" + kv.Key + "\t" + kv.Value);
            foreach (string n in Names())
            {
                List<string> l;
                if (!items.TryGetValue(n, out l)) continue;
                foreach (string v in l) sb.AppendLine("mod=" + n + "\t" + v);
            }
            try { Core.WriteAllTextAtomic(f, sb.ToString(), new UTF8Encoding(true)); }
            catch (Exception ex) { Core.Log("WARN", "我的分支保存失败: " + ex.Message); }
        }

        private static List<string> ListOf(string branch)
        {
            List<string> l;
            if (!items.TryGetValue(branch, out l)) { l = new List<string>(); items[branch] = l; }
            return l;
        }

        // ---------------------------------------------------------- 查询

        public static List<string> Names()
        {
            Ensure();
            List<string> list = new List<string>(names.Count);
            HashSet<string> done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // 先顶层，再把子分支紧跟到父后面 —— 树里就是这个顺序
            foreach (string n in names) if (!HasParent(n)) Walk(n, list, done);
            foreach (string n in names) Walk(n, list, done);      // 兜底：绕圈的也一个不丢
            return list;
        }

        private static void Walk(string n, List<string> list, HashSet<string> done)
        {
            if (!done.Add(n)) return;
            list.Add(n);
            foreach (string c in names) if (ParentOf(c) == n) Walk(c, list, done);
        }

        /// <summary>父分支名；顶层返回空串。</summary>
        public static string ParentOf(string name)
        {
            Ensure();
            if (string.IsNullOrEmpty(name)) return "";
            string p;
            return parentOf.TryGetValue(name, out p) ? p : "";
        }

        public static bool HasParent(string name) { return ParentOf(name).Length > 0; }

        /// <summary>某个分支下面挂了哪些子分支（parent 传空 = 顶层）。</summary>
        public static List<string> ChildrenOf(string parent)
        {
            Ensure();
            string want = parent ?? "";
            List<string> l = new List<string>();
            foreach (string n in names)
                if (string.Equals(ParentOf(n), want, StringComparison.OrdinalIgnoreCase)) l.Add(n);
            return l;
        }

        /// <summary>缩进层数：顶层 0，子分支 1，孙分支 2 …（树里按这个缩进）</summary>
        public static int Depth(string name)
        {
            Ensure();
            int d = 0;
            string cur = name;
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (d < 32)
            {
                if (!seen.Add(cur)) break;
                string p = ParentOf(cur);
                if (p.Length == 0) break;
                d++;
                cur = p;
            }
            return d;
        }

        public static int Count { get { Ensure(); return names.Count; } }

        public static List<string> ItemsOf(string branch)
        {
            Ensure();
            List<string> l;
            if (!items.TryGetValue(branch ?? "", out l)) return new List<string>();
            return new List<string>(l);
        }

        /// <summary>这个 vpk 在哪个分支里；不在任何分支返回 ""。</summary>
        public static string BranchOf(string vpk)
        {
            Ensure();
            if (string.IsNullOrEmpty(vpk)) return "";
            string b;
            return ofMod.TryGetValue(vpk, out b) ? b : "";
        }

        public static bool Has(string branch)
        {
            Ensure();
            return !string.IsNullOrEmpty(branch) && names.Contains(branch);
        }

        // ---------------------------------------------------------- 修改

        /// <summary>新建分支。名字空 / 重名返回错误文字，成功返回 null。</summary>
        public static string Create(string name) { return Create(name, null); }

        /// <summary>新建分支，挂在 parent 下面（parent 不存在就当顶层）。</summary>
        public static string Create(string name, string parent)
        {
            Ensure();
            name = CleanName(name);
            if (name.Length == 0) return "分支名不能为空";
            if (names.Contains(name)) return "已经有叫这个名字的分支了";
            names.Add(name);
            ListOf(name);
            string p = CleanName(parent);
            if (p.Length > 0 && p != name && names.Contains(p)) parentOf[name] = p;
            Save();
            return null;
        }

        /// <summary>换个父分支；parent 传空 = 移到顶层。成环就别动。</summary>
        public static void SetParent(string name, string parent)
        {
            Ensure();
            if (string.IsNullOrEmpty(name) || !names.Contains(name)) return;
            string p = CleanName(parent);
            if (p.Length == 0 || !names.Contains(p) || p == name) { parentOf.Remove(name); Save(); return; }
            // 不能挂到自己的子孙下面
            string cur = p;
            for (int guard = 0; guard < 32 && cur.Length > 0; guard++)
            {
                if (string.Equals(cur, name, StringComparison.OrdinalIgnoreCase)) return;
                cur = ParentOf(cur);
            }
            parentOf[name] = p;
            Save();
        }

        /// <summary>
        /// 拖动排序 / 换层级：把 name 挪到 newParent 底下、排在 before 前面（before 传空 = 排到这一层最后）。
        /// 顺序就靠 names 这个列表本身——树里父子、兄弟的先后都是顺着它走的，挪列表 = 挪树。
        /// 挂到自己或自己的子孙下面直接拒绝，否则整枝会从树上断开。
        /// </summary>
        public static string Move(string name, string newParent, string before)
        {
            Ensure();
            if (string.IsNullOrEmpty(name) || !names.Contains(name)) return "这个分支不在了";
            string p = CleanName(newParent);
            if (string.Equals(p, name, StringComparison.OrdinalIgnoreCase)) return "不能挂到自己下面";
            if (p.Length > 0 && !names.Contains(p)) p = "";
            string cur = p;
            for (int guard = 0; guard < 32 && cur.Length > 0; guard++)
            {
                if (string.Equals(cur, name, StringComparison.OrdinalIgnoreCase)) return "不能挂到自己的子分支下面";
                cur = ParentOf(cur);
            }
            string b = CleanName(before);
            if (string.Equals(b, name, StringComparison.OrdinalIgnoreCase) || !names.Contains(b)) b = "";

            names.Remove(name);
            int at;
            if (b.Length > 0) at = names.IndexOf(b);
            else if (p.Length == 0) at = names.Count;
            else
            {
                // 排到父分支整枝的最后：父 + 它下面所有子分支之后
                at = Math.Max(0, names.IndexOf(p) + 1);
                for (int i = 0; i < names.Count; i++) if (IsUnder(names[i], p)) at = i + 1;
            }
            if (at < 0 || at > names.Count) at = names.Count;
            names.Insert(at, name);
            if (p.Length == 0) parentOf.Remove(name); else parentOf[name] = p;
            Save();
            return null;
        }

        /// <summary>name 是不是挂在 parent 这棵子树里（不含 parent 自己）。</summary>
        private static bool IsUnder(string name, string parent)
        {
            string cur = name;
            for (int guard = 0; guard < 32; guard++)
            {
                string up = ParentOf(cur);
                if (up.Length == 0) return false;
                if (string.Equals(up, parent, StringComparison.OrdinalIgnoreCase)) return true;
                cur = up;
            }
            return false;
        }

        /// <summary>同一层的下一个分支（拖到某行后面时，用它当「插在谁前面」）。没有了返回空串。</summary>
        public static string NextSibling(string name)
        {
            Ensure();
            if (string.IsNullOrEmpty(name)) return "";
            List<string> sib = ChildrenOf(ParentOf(name));
            for (int i = 0; i < sib.Count; i++)
                if (string.Equals(sib[i], name, StringComparison.OrdinalIgnoreCase))
                    return i + 1 < sib.Count ? sib[i + 1] : "";
            return "";
        }

        public static string Rename(string oldName, string newName)
        {
            Ensure();
            newName = CleanName(newName);
            if (!names.Contains(oldName)) return "这个分支不在了";
            if (newName.Length == 0) return "分支名不能为空";
            if (newName == oldName) return null;
            if (names.Contains(newName)) return "已经有叫这个名字的分支了";
            int i = names.IndexOf(oldName);
            names[i] = newName;
            string up;
            if (parentOf.TryGetValue(oldName, out up)) { parentOf.Remove(oldName); parentOf[newName] = up; }
            // 挂在它下面的子分支跟着改名走，不然一起变孤儿
            List<string> kids = new List<string>(parentOf.Keys);
            foreach (string k in kids)
                if (string.Equals(parentOf[k], oldName, StringComparison.OrdinalIgnoreCase)) parentOf[k] = newName;
            List<string> l;
            if (items.TryGetValue(oldName, out l))
            {
                items.Remove(oldName);
                items[newName] = l;
                foreach (string v in l) ofMod[v] = newName;
            }
            Save();
            return null;
        }

        /// <summary>解散分支：只删分组，vpk 文件和勾选一个都不动。子分支提到上一层，不跟着消失。</summary>
        public static void Delete(string name)
        {
            Ensure();
            if (!names.Contains(name)) return;
            string up = ParentOf(name);
            List<string> keys = new List<string>(parentOf.Keys);
            foreach (string k in keys)
                if (string.Equals(parentOf[k], name, StringComparison.OrdinalIgnoreCase))
                {
                    if (up.Length > 0) parentOf[k] = up; else parentOf.Remove(k);
                }
            parentOf.Remove(name);
            names.Remove(name);
            List<string> l;
            if (items.TryGetValue(name, out l))
            {
                foreach (string v in l) if (BranchOf(v) == name) ofMod.Remove(v);
                items.Remove(name);
            }
            Save();
        }

        /// <summary>把 vpk 放进分支（原来在别的分支里就搬过来）。</summary>
        public static void Put(string branch, string vpk)
        {
            Ensure();
            if (string.IsNullOrEmpty(branch) || string.IsNullOrEmpty(vpk)) return;
            if (!names.Contains(branch)) { names.Add(branch); ListOf(branch); }
            string old;
            if (ofMod.TryGetValue(vpk, out old))
            {
                if (old == branch) return;
                List<string> o;
                if (items.TryGetValue(old, out o)) o.RemoveAll(delegate(string x) { return string.Equals(x, vpk, StringComparison.OrdinalIgnoreCase); });
            }
            ListOf(branch).Add(vpk);
            ofMod[vpk] = branch;
            Save();
        }

        /// <summary>从分支里移出（不删文件）。</summary>
        public static void Take(string vpk)
        {
            Ensure();
            string b;
            if (!ofMod.TryGetValue(vpk, out b)) return;
            ofMod.Remove(vpk);
            List<string> l;
            if (items.TryGetValue(b, out l))
                l.RemoveAll(delegate(string x) { return string.Equals(x, vpk, StringComparison.OrdinalIgnoreCase); });
            Save();
        }

        /// <summary>分支名清理：去掉 tab / 换行（文件里用 tab 分隔），限长 24 字。</summary>
        public static string CleanName(string s)
        {
            if (s == null) return "";
            s = s.Replace("\t", " ").Replace("\r", " ").Replace("\n", " ").Trim();
            if (s.Length > 24) s = s.Substring(0, 24);
            return s;
        }
    }

    /// <summary>
    /// 选一个分支（右键「放进我的分支」时弹）。列表最后一行永远是「＋ 新建分支…」，
    /// 返回值用 "+" 开头表示用户要新建，调用方自己去问名字。
    /// </summary>
    internal sealed class BranchPickForm : Form
    {
        private readonly ListBox box;
        private readonly List<string> keys = new List<string>();   // 和 box 一一对应：显示带缩进，取的是真名
        public string Picked;                       // 选中的分支名，或 "＋ 新建分支…"

        public BranchPickForm(List<string> names)
        {
            Text = "放进我的分支";
            Theme.UseDpi(this);
            ClientSize = Dpi.Sz(360, 340);
            Font = Theme.UI;
            BackColor = Theme.Bg;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false;

            Label l = new Label();
            l.Text = "选一个分支（一组里能同时开好几个；文件不会被移动）";
            l.SetBounds(12, 12, 336, 20);
            Theme.Dress(l);
            Controls.Add(l);

            box = new ListBox();
            box.SetBounds(12, 36, 336, 250);
            box.Font = Theme.UI;
            box.BorderStyle = BorderStyle.FixedSingle;
            if (names != null)
                foreach (string n in names)
                {
                    keys.Add(n);
                    // 子分支缩进显示，选的时候一眼看出层级（取回来的是不带缩进的真名）
                    box.Items.Add(new string(' ', MyBranch.Depth(n) * 4) + n);
                }
            keys.Add("＋ 新建分支…");
            box.Items.Add("＋ 新建分支…");
            if (box.Items.Count > 0) box.SelectedIndex = 0;
            box.DoubleClick += delegate { Accept(); };
            Controls.Add(box);

            FlatButton ok = new FlatButton();
            ok.Text = "确定"; ok.Primary = true; ok.SetBounds(180, 296, 80, Theme.H);
            ok.Click += delegate { Accept(); };
            Controls.Add(ok);

            FlatButton no = new FlatButton();
            no.Text = "取消"; no.SetBounds(268, 296, 80, Theme.H);
            no.DialogResult = DialogResult.Cancel;
            Controls.Add(no);

            AcceptButton = ok; CancelButton = no;
            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (DialogResult == DialogResult.OK && string.IsNullOrEmpty(Picked)) e.Cancel = true;
            };
        }

        private void Accept()
        {
            if (box.SelectedItem == null) return;
            int i = box.SelectedIndex;
            Picked = (i >= 0 && i < keys.Count) ? keys[i] : box.SelectedItem.ToString();
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}

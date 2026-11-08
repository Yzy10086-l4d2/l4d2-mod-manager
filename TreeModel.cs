using System;
using System.Collections.Generic;

namespace Installer
{
    /// <summary>
    /// 科技树上的一个节点。三级：大类 → 位置 → 候选 MOD，根节点只是把它们挂起来。
    /// 这里只管「有什么」，不管画成什么样、摆在哪儿（那是 TreeLayout / TreeCanvas 的事）。
    /// </summary>
    internal sealed class TreeNode
    {
        public int Level;                       // 0 根 / 1 大类 / 2 位置 / 3 候选
        public string Title;
        public string Sub;                      // 副行：小分类 / 几个 vpk
        public string Badge;                    // 右侧数字：下面有几个子节点
        public bool On;                         // 已装（这个位置 / 这个候选被勾了）
        public bool IsNone;                     // 「留原版」这种不算 MOD 的节点
        public Slot Slot;                       // Level 2 才有
        public ModGroup Group;                   // Level 3 才有
        public readonly List<TreeNode> Kids = new List<TreeNode>();

        // 布局产物（TreeLayout 写，TreeCanvas 读）
        public float X, Y, W, H;
        public float CapH;                  // 这一行上面留的小分类标题高度（行高压缩时会跟着变）

        public bool HasKids { get { return Kids.Count > 0; } }
    }

    /// <summary>
    /// 把方案树的数据整理成科技树。不新增数据源：位置来自 SlotIndex，
    /// 勾选状态由外部传进来的 counted 判断（界面上是 MainForm.CountChecked）。
    /// </summary>
    internal static class TreeModel
    {
        public static TreeNode Build(List<Slot> slots, Func<ModGroup, int> counted)
        {
            TreeNode root = new TreeNode();
            root.Level = 0;
            root.Title = "MOD 库";
            root.Sub = Catalog.Groups.Count + " 个 MOD";
            if (slots == null) return root;

            Dictionary<string, List<Slot>> byCat = new Dictionary<string, List<Slot>>(StringComparer.Ordinal);
            foreach (Slot s in slots)
            {
                if (s == null) continue;
                string cat = string.IsNullOrEmpty(s.Cat) ? "杂项" : s.Cat;
                List<Slot> list;
                if (!byCat.TryGetValue(cat, out list)) { list = new List<Slot>(); byCat[cat] = list; }
                list.Add(s);
            }

            List<string> order = SlotIndex.CatOrder(slots);
            foreach (string cat in order)
            {
                List<Slot> list;
                if (!byCat.TryGetValue(cat, out list) || list.Count == 0) continue;

                TreeNode c = new TreeNode();
                c.Level = 1;
                c.Title = cat;
                c.Badge = list.Count + " 个";
                root.Kids.Add(c);

                foreach (Slot s in list)
                {
                    TreeNode n = new TreeNode();
                    n.Level = 2;
                    n.Title = s.Name;
                    n.Sub = s.Sub;
                    n.Slot = s;
                    // 位置上的数字：有本体候选就写几个，空位置写「空」
                    n.Badge = s.Items.Count > 0 ? s.Items.Count + " 个" : "空";
                    c.Kids.Add(n);
                    if (AnyChecked(s, counted)) c.On = true;

                    foreach (ModGroup g in s.Items)
                    {
                        TreeNode k = new TreeNode();
                        k.Level = 3;
                        k.Title = g.Name;
                        k.Sub = PartText(g);
                        k.Group = g;
                        k.On = counted(g) > 0;
                        n.Kids.Add(k);
                        if (k.On) n.On = true;
                    }

                    // 尾节点：这个位置不装
                    TreeNode none = new TreeNode();
                    none.Level = 3;
                    none.IsNone = true;
                    none.Title = "留原版（这个位置不装）";
                    none.Sub = "装上候选就把这里的都取消";
                    n.Kids.Add(none);
                }
            }
            return root;
        }

        private static bool AnyChecked(Slot s, Func<ModGroup, int> counted)
        {
            foreach (ModGroup g in s.Items) if (counted(g) > 0) return true;
            return false;
        }

        private static string PartText(ModGroup g)
        {
            if (g == null) return "";
            int n = g.Parts == null ? 0 : g.Parts.Count;
            if (n <= 1) return "1 个 vpk";
            return n + " 个 vpk 套装";
        }

        /// <summary>自检 / 导出用：把树摊成文本，方便核对层级和数字对不对。</summary>
        public static string Dump(TreeNode root)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            Walk(root, sb, 0);
            return sb.ToString();
        }

        private static void Walk(TreeNode n, System.Text.StringBuilder sb, int depth)
        {
            string pad = new string(' ', depth * 2);
            sb.Append(pad).Append(n.Title);
            if (!string.IsNullOrEmpty(n.Badge)) sb.Append("  (").Append(n.Badge).Append(")");
            if (n.On) sb.Append("  ·已装");
            if (n.IsNone) sb.Append("  ·留原版");
            sb.Append("\r\n");
            foreach (TreeNode k in n.Kids) Walk(k, sb, depth + 1);
        }
    }
}

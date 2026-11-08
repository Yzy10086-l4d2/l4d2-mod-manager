using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Installer
{
    // ============================================================
    //  方案树（A 版）
    //  左边按「能替换的东西」列成横向树：枪械 → AK-47 / M16 …
    //  右边是这件东西的候选 MOD，单选。
    //  规则：一个位置只留一个本体，选新的就把同位置原来的取消；
    //        只改第一人称模型 / 音效 / HUD 这类不换本体的，单独一栏，各管各的开关。
    // ============================================================

    /// <summary>一个「能替换的东西」：AK-47 / 医疗包 / 教练 …</summary>
    internal sealed class Slot
    {
        public string Key;                       // 被替换的模型名，如 w_rifle_ak47
        public string Name;                      // 显示名：AK-47
        public string Cat;                       // 大类：枪械 / 医疗 / 人物 …
        public string Sub = "其他";              // 小分类：步枪 / 霰弹枪 / 一代 / 医疗包 …
        public bool Manual;                      // 这个位置是人工指定出来的（可能还要并回自动的同名位置）
        // 「一堆」而不是「一个位置」：地图 / 喷漆 / 用户自建分支 —— 里面能同时开好几个，
        // 点一行是开 / 关，不是把别的替换掉。Items 里装的就是这一堆的全部候选。
        public bool Pile;
        // 库里还没有 MOD 的「标准位置」：照常占一行，让人看出还缺什么。
        // 它没有任何候选（Items / Extras 都空着），点开只有「留原版」。
        public bool Empty;
        // 同一大类同一小分类里的排序权重（小的在前）。目前只有「我的分支」用它按父子顺序排，
        // 别的槽位都是 0，照旧按 PosOrder / 名字排。
        public int Rank;
        // 换本体的：一个位置只能有一个（同一把枪两套模型会打架）
        public readonly List<ModGroup> Items = new List<ModGroup>();
        // 叠加的扩展：开火音效 / 只改第一人称模型 / HUD 图标 —— 不换本体，可以和上面的同时装
        public readonly List<ModGroup> Extras = new List<ModGroup>();
    }

    /// <summary>把 MOD 库按「改的是什么」聚成槽位。认不出改什么的，不进树。</summary>
    internal static class SlotIndex
    {
        public static List<Slot> Build()
        {
            Dictionary<string, Slot> map = new Dictionary<string, Slot>(StringComparer.OrdinalIgnoreCase);
            List<Slot> list = new List<Slot>();
            foreach (ModGroup g in Catalog.Groups)
            {
                ModItem m = g.Main;
                if (m == null) continue;
                // 地图能叠好几张、喷漆不占位置 —— 这两种没有「只能一个」的说法，不进树
                if (g.Cat == "地图" || g.Cat == "喷漆") continue;
                // 人工指定过就以人工为准：大类、位置都能改
                string cat = CatPrefs.CatOf(m, g.Cat);
                string hand = CatPrefs.SlotOf(m).Trim();
                string key, nm, hint = "";
                if (hand.Length > 0)
                {
                    key = "slot:" + cat + "/" + hand;
                    nm = hand;
                }
                else
                {
                    key = (m.SubjectKey ?? "").Trim();
                    nm = (m.Subject ?? "").Trim();
                    if (IsCountName(nm)) continue;                 // 「4 件」是「一个包里改了好几样」的上报格式，不是东西
                    nm = SplitKind(nm, out hint);                  // 「霰弹枪/木喷」→ 木喷（枪种当小分类）
                    // 一个包同时改了两件（UZI + MAC-10 共用一套动画，作者常一起换），位置只能占一个：
                    // 用户把它放在哪个目录，就归哪个 —— 目录名比「A/B」这种并列表述有用。
                    if (nm.IndexOf('/') >= 0)
                    {
                        string c0 = Concrete("", g, m);
                        if (c0.Length > 0 && HasHalf(nm, c0)) { nm = c0; key = "c:" + c0; }
                    }
                    // 只靠材质认出来的名字不能定死：铁喷的贴图就放在 materials/.../shotgun/ 下，
                    // 会被认成木喷。这种时候听目录名。
                    if (m.SubjectLevel == "材质" && cat != "杂项" && cat != "地图" && cat != "喷漆")
                    {
                        string c1 = Concrete(nm, g, m);
                        if (c1.Length > 0) { nm = c1; key = "c:" + c1; }
                    }
                    // 只认出「步枪 / 霰弹枪 / 幸存者」这种泛称时，用目录名或 MOD 名里的具体型号补回来。
                    // 用户自己的目录名（sg552 / 铁喷 / 黑胖Coach）比内容识别准得多。
                    if (Vague(nm))
                    {
                        string c = Concrete(nm, g, m);
                        if (c.Length > 0) { nm = c; key = "c:" + c; }
                    }
                    if (key.Length == 0)
                    {
                        // 什么都没认出来：看目录名是不是个具体型号（「铁喷」这种），是就照它归位
                        if (nm.Length == 0)
                        {
                            string c = Concrete("", g, m);
                            if (c.Length > 0) { nm = c; key = "c:" + c; }
                        }
                        // 人物、投掷物这类没有「模型名」，只有认出来的名字 —— 就用名字当位置
                        if (key.Length == 0)
                        {
                            if (nm.Length == 0) continue;              // 名字都认不出：不进树
                            key = "sub:" + nm;
                        }
                    }
                }
                // 近战 / 投掷物 / 武器升级不算枪：提成和枪械平级的大类。
                // 只动自动归类的（人工指定过大类的以人工为准）；位置键带大类，不会和旧槽撞。
                string sub0 = "";
                if (hand.Length == 0 && cat == "枪械")
                {
                    sub0 = SubOf(cat, nm.Length > 0 ? nm : key, key, hint, g.Sub);
                    if (sub0 == "近战" || sub0 == "投掷物" || sub0 == "武器升级") cat = sub0;
                }
                // 弹药堆（Ammo pile）：识别按模型名把它归成「杂项 · 可搬道具」，
                // 但它跟燃烧 / 爆破 / 激光那几个升级箱是一路货 —— 都是给枪补弹药的东西，
                // 用户 2026-11-09 定：归「武器升级」，别在杂项里单独蹲一行。
                if (hand.Length == 0 && cat == "杂项" && IsAmmoPile(nm.Length > 0 ? nm : key)) cat = "武器升级";
                Slot s;
                // 位置键永远带上大类：枪械的「消音冲锋枪」模型和杂项的「UZI 改视角」用的是同一个模型名，
                // 但它们不是「二选一」的关系（改视角是叠加的），混进一个槽就错了。
                string mapKey = cat + "\u0001" + key;
                if (!map.TryGetValue(mapKey, out s))
                {
                    s = new Slot();
                    s.Key = mapKey;
                    s.Name = nm.Length > 0 ? nm : key;
                    s.Cat = cat;
                    s.Manual = hand.Length > 0;
                    s.Sub = sub0.Length > 0 ? sub0 : SubOf(cat, s.Name, key, hint, g.Sub);
                    map[mapKey] = s;
                    list.Add(s);
                }
                else if (Vague(s.Name) && nm.Length > 0 && !Vague(nm))
                {
                    s.Name = nm;                               // 「步枪」这种泛称，换成更具体的名字
                    s.Sub = SubOf(cat, s.Name, key, hint, g.Sub);
                }
                if (IsExtra(g)) s.Extras.Add(g); else s.Items.Add(g);
            }
            MergeManual(list);
            MergeSame(list);
            Disambiguate(list);
            EnsureStandard(list);               // 库里没有的枪 / 人物 / 药品也占一行
            // 被拖到别的小分类的位置，以手工归属为准。放在最后做：名字已经被 Disambiguate 定下来了
            foreach (Slot s in list)
            {
                string ov = CatPrefs.SubOverride(s.Cat, s.Name);
                if (ov.Length > 0) s.Sub = ov;
            }
            return list;
        }

        /// <summary>
        /// 人工写的位置名如果和自动认出来的位置同名（同一个大类），并成一行 ——
        /// 否则「AK-47」会分成人工一个、自动一个，看着像两个位置。
        /// </summary>
        private static void MergeManual(List<Slot> list)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                Slot a = list[i];
                if (!a.Manual) continue;
                for (int j = 0; j < list.Count; j++)
                {
                    if (j == i) continue;
                    Slot b = list[j];
                    if (b.Cat != a.Cat || b.Name != a.Name) continue;
                    if (b.Manual) continue;                   // 两个都是人工的：key 相同，不会发生
                    foreach (ModGroup g in a.Items) if (!b.Items.Contains(g)) b.Items.Add(g);
                    foreach (ModGroup g in a.Extras) if (!b.Extras.Contains(g)) b.Extras.Add(g);
                    list.RemoveAt(i);
                    break;
                }
            }
        }

        /// <summary>
        /// 名字洗干净之后可能撞车：「霰弹枪/木喷」洗成「木喷」，就该和另一个「木喷」并成一个位置。
        /// 同一个大类、同一个小分类、同一个名字 —— 并。
        /// </summary>
        private static void MergeSame(List<Slot> list)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                Slot a = list[i];
                for (int j = 0; j < i; j++)
                {
                    Slot b = list[j];
                    if (b.Cat != a.Cat || b.Sub != a.Sub || b.Name != a.Name) continue;
                    foreach (ModGroup g in a.Items) if (!b.Items.Contains(g)) b.Items.Add(g);
                    foreach (ModGroup g in a.Extras) if (!b.Extras.Contains(g)) b.Extras.Add(g);
                    if (a.Manual) b.Manual = true;
                    list.RemoveAt(i);
                    break;
                }
            }
        }

        // ------------------------------------------------ 小分类

        /// <summary>「枪种/具体名」「人物名/泛称」这种识别残渣，拆开只留具体那一半。</summary>
        internal static string SplitKind(string nm, out string hint)
        {
            hint = "";
            if (string.IsNullOrEmpty(nm)) return "";
            int i = nm.IndexOf('/');
            if (i <= 0 || i >= nm.Length - 1) return nm;
            string a = nm.Substring(0, i).Trim();
            string b = nm.Substring(i + 1).Trim();
            bool ka = KindWord(a), kb = KindWord(b);
            if (ka && !kb) { hint = a; return b; }
            if (kb && !ka) { hint = b; return a; }
            return nm;
        }

        private static readonly string[] KindWords =
        {
            "步枪", "冲锋枪", "霰弹枪", "狙击枪", "手枪", "机枪", "近战", "枪械", "爆炸物",
            "幸存者", "特感", "投掷物", "医疗包", "止痛药", "肾上腺素", "药品", "其他"
        };

        private static bool KindWord(string s)
        {
            // 「消音冲锋枪」这种带前缀的也算枪种词，否则「UZI/消音冲锋枪」拆不开
            foreach (string k in KindWords) if (s.IndexOf(k, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>小分类：大类下面再分一层（枪械 → 步枪 → AK-47）。</summary>
        public static string SubOf(string cat, string name, string key, string hint, string gsub)
        {
            string n = name ?? "";
            switch (cat)
            {
                case "枪械":
                {
                    string k = GunKindOf(n, key);
                    if (k != "其他枪械") return k;
                    if (hint != null && hint.Length > 0) return hint;   // 名字推不出来，才用「/」前面那个枪种
                    // 还是推不出来时，用识别出来的细分兜一层（人工改名之后常这样）
                    if (IsGunKindName(gsub)) return gsub.Trim();
                    return k;
                }
                case "人物": return CharKindOf(n);
                case "药品": return MedKindOf(n, key);
                // 这三个大类和枪械平级，但名字识别用的还是枪械那套词表（斧 / 燃烧瓶 / 升级箱都在 GunKindOf 里）
                case "近战": case "投掷物": case "武器升级":
                {
                    string k = GunKindOf(n, key);
                    return k == "其他枪械" ? cat : k;
                }
                case "特感":
                    // 普通感染者（尸潮小僵尸）不是特感，单独一层。其余特感一个就是一个位置
                    return n.IndexOf("普通", StringComparison.Ordinal) >= 0 ? "普通感染者" : "特感";
                // 杂项里「能搬能扔」的那些（油桶 / 煤气罐 / 弹药堆）单独归一层，
                // 别和特效、火焰这类真·杂项混在一堆
                case "杂项":
                    if (IsPropName(n)) return "可搬道具";
                    break;
            }
            string s = (gsub ?? "").Trim();
            if (s.Length > 0 && s != "其他") return s;
            return "其他杂项";
        }

        /// <summary>是不是 PosOrder 里登记过的「可搬道具」。</summary>
        private static bool IsPropName(string nm)
        {
            if (string.IsNullOrEmpty(nm)) return false;
            for (int i = 0; i < PosOrder.GetLength(0); i++)
                if (PosOrder[i, 0] == "可搬道具" && PosOrder[i, 1] == nm) return true;
            return false;
        }

        internal static string GunKindOf(string nm, string key)
        {
            string s = ((nm ?? "") + " " + (key ?? "")).ToLowerInvariant();
            // 顺序有讲究：带 rifle 的「15发木狙/鸟狙」得先被狙击认走，否则会被步枪抢
            if (HasAny(s, "awp", "scout", "military", "hunting", "sniper", "鸟狙", "猎枪", "狙击", "狙"))
                return "狙击枪";
            if (HasAny(s, "pump", "chrome", "spas", "autoshotgun", "shotgun", "木喷", "铁喷", "连喷", "霰弹"))
                return "霰弹枪";
            if (HasAny(s, "uzi", "mp5", "mac10", "mac-10", "silenced", "消音", "smg", "冲锋"))
                return "冲锋枪";
            if (HasAny(s, "ak47", "ak-47", "m16", "scar", "sg552", "galil", "m4", "ar15", "ar-15", "desert", "rifle", "步枪"))
                return "步枪";
            // 榴弹发射器带 grenade，必须排在投掷物前面，否则会被当成手雷
            if (HasAny(s, "grenade_launcher", "launcher", "榴弹", "m60", "machinegun", "机枪", "特殊武器"))
                return "特殊武器";
            if (HasAny(s, "molotov", "pipebomb", "vomitjar", "vomit", "grenade", "投掷", "燃烧瓶", "土制", "胆汁"))
                return "投掷物";
            if (HasAny(s, "magnum", "pistol", "手枪", "马格南"))
                return "手枪";
            // 近战：14 种游戏自带的 + CS 小刀。只认得出「近战」的（没写型号）留在最后那条兜底
            if (HasAny(s, "katana", "machete", "crowbar", "fireaxe", "chainsaw", "nightstick", "tonfa",
                          "cricket", "baseball", "golf", "pitchfork", "shovel", "knife",
                          "武士刀", "砍刀", "撬棍", "消防斧", "电锯", "警棍", "板球", "棒球",
                          "高尔夫", "干草叉", "铁锹", "铁铲", "小刀",
                          "平底锅", "斧", "锤", "吉他", "球棍", "球棒", "近战"))
                return "近战";
            // 升级箱：给枪加燃烧 / 爆破 / 激光。带「榴弹」的早在特殊武器那条被接走了
            if (HasAny(s, "incendiary", "explosive", "laser", "燃烧弹药", "爆破弹药", "弹药包",
                          "高爆", "激光", "升级"))
                return "武器升级";
            return "其他枪械";
        }

        private static string CharKindOf(string nm)
        {
            string n = nm ?? "";
            if (HasAny(n, "tank", "坦克", "witch", "女巫", "hunter", "smoker", "boomer", "spitter",
                          "jockey", "charger", "特感"))
                return "特感";
            if (HasAny(n, "比尔", "佐伊", "路易斯", "弗朗西斯")) return "一代";
            if (HasAny(n, "教练", "尼克", "埃利斯", "罗谢尔")) return "二代";
            return "其他人物";
        }

        private static string MedKindOf(string nm, string key)
        {
            string s = ((nm ?? "") + " " + (key ?? "")).ToLowerInvariant();
            if (HasAny(s, "medkit", "医疗包", "除颤", "电击", "defib")) return "治疗与复活";
            if (HasAny(s, "pill", "止痛", "adrenaline", "肾上腺", "针")) return "暂时增益";
            return "其他药品";
        }

        private static readonly string[] GunKindNames =
        { "步枪", "冲锋枪", "霰弹枪", "狙击枪", "手枪", "特殊武器", "近战", "投掷物" };

        private static bool IsGunKindName(string s)
        {
            s = (s ?? "").Trim();
            foreach (string k in GunKindNames) if (s == k) return true;
            return false;
        }

        private static bool HasAny(string s, params string[] keys)        {
            foreach (string k in keys)
                if (s.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static readonly string[] SubOrder =
        {
            "步枪", "冲锋枪", "霰弹枪", "狙击枪", "手枪", "特殊武器", "近战", "投掷物", "武器升级", "其他枪械",
            "一代", "二代", "特感", "普通感染者", "其他人物",
            "治疗与复活", "暂时增益", "其他药品",
            "可搬道具", "特效杂项", "其他杂项"
        };

        public static int SubRank(string sub)
        {
            for (int i = 0; i < SubOrder.Length; i++) if (SubOrder[i] == sub) return i;
            return 500;
        }

        // 同一小分类里谁排前面：照游戏里武器的默认顺序来（用户也是这么记的）。
        // 这张表同时也是「游戏里一共有哪些东西」的清单 —— 空位（库里没有 MOD）就照着它补。
        private static readonly string[,] PosOrder = new string[,]
        {
            {"步枪","AK-47"},{"步枪","M16"},{"步枪","sg552"},{"步枪","SCAR-L"},
            {"冲锋枪","UZI"},{"冲锋枪","MAC-10"},{"冲锋枪","MP5"},
            {"霰弹枪","木喷"},{"霰弹枪","铁喷"},{"霰弹枪","一代连喷"},{"霰弹枪","二代连喷"},
            {"狙击枪","AWP"},{"狙击枪","鸟狙"},{"狙击枪","15发木狙"},{"狙击枪","30发军用狙"},
            {"特殊武器","M60"},{"特殊武器","榴弹发射器"},
            {"投掷物","燃烧瓶"},{"投掷物","土制炸弹"},{"投掷物","胆汁"},
            {"手枪","手枪"},{"手枪","马格南"},
            // 近战：游戏里 14 种（干草叉 / 铁锹是 PC 独占），加「近战武器」兜底。
            // 名字必须跟 Identify 的识别表逐字一致 —— 差一个字，真出现那个包时树上就会
            // 同时蹲着一个「铁铲」空位和一个「铁锹」，看着像两个位置。
            {"近战","近战武器"},{"近战","消防斧"},{"近战","武士刀"},{"近战","砍刀"},{"近战","撬棍"},
            {"近战","电锯"},{"近战","平底锅"},{"近战","棒球棍"},{"近战","板球拍"},{"近战","高尔夫球杆"},
            {"近战","电吉他"},{"近战","警棍"},{"近战","干草叉"},{"近战","铁锹"},{"近战","小刀"},
            // 升级箱：捡到就往枪里塞效果，模型也有人换（燃烧弹药包 ≠ 燃烧瓶，别混）
            // 弹药堆（Ammo pile）也算这一类：都是给枪补弹药的东西（2026-11-09 用户定）
            {"武器升级","燃烧弹药包"},{"武器升级","爆破弹药包"},{"武器升级","激光瞄准"},{"武器升级","弹药"},
            {"治疗与复活","医疗包"},{"治疗与复活","电击器"},
            {"暂时增益","肾上腺素"},{"暂时增益","止痛药"},
            // 只认得出「是药」、说不清哪种的包，识别表给的显示名就是「药丸」
            {"其他药品","药丸"},
            {"一代","比尔"},{"一代","佐伊"},{"一代","路易斯"},{"一代","弗朗西斯"},
            {"二代","教练"},{"二代","埃利斯"},{"二代","尼克"},{"二代","罗谢尔"},
            // 不指定是谁、四套一起换的「通用幸存者包」—— 识别表里就叫「幸存者」
            {"其他人物","幸存者"},
            {"特感","Boomer"},{"特感","Charger"},{"特感","Hunter"},{"特感","Jockey"},
            {"特感","Smoker"},{"特感","Spitter"},{"特感","Tank"},{"特感","Witch"},
            // 尸潮里的普通小僵尸：换皮肤最常见，和特感分开一层（它不占「特感」那 8 个位置）
            {"普通感染者","普通感染者"},
            // 能搬能扔的场景道具：油桶 / 煤气罐这些也是常见改模对象
            {"可搬道具","氧气瓶"},{"可搬道具","煤气罐"},{"可搬道具","油桶"},
            {"可搬道具","燃料桶"},{"可搬道具","烟花盒"},{"可搬道具","地精"},{"可搬道具","可乐"},
            // 天空盒换的是整张天，同一时间只能有一个
            {"其他杂项","天空盒"},
        };

        public static int NameRank(string sub, string name)
        {
            for (int i = 0; i < PosOrder.GetLength(0); i++)
                if (PosOrder[i, 0] == sub && PosOrder[i, 1] == name) return i;
            return 1000;
        }

        /// <summary>两把不同的枪可能都叫「鸟狙」，加个尾巴区分开，否则树里看着像重复项。</summary>
        private static void Disambiguate(List<Slot> list)
        {
            Dictionary<string, int> freq = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Slot s in list)
            {
                string k = s.Cat + "\u0001" + s.Name;      // 不同大类下同名不算重复
                int n;
                freq.TryGetValue(k, out n);
                freq[k] = n + 1;
            }
            foreach (Slot s in list)
            {
                int n;
                if (!freq.TryGetValue(s.Cat + "\u0001" + s.Name, out n) || n < 2) continue;
                s.Name = s.Name + " · " + Tag(s.Key);
            }
        }

        private static string Tag(string key)
        {
            string k = key;
            if (k.StartsWith("w_", StringComparison.OrdinalIgnoreCase)) k = k.Substring(2);
            else if (k.StartsWith("v_", StringComparison.OrdinalIgnoreCase)) k = k.Substring(2);
            int us = k.LastIndexOf('_');
            if (us >= 0 && us < k.Length - 1) k = k.Substring(us + 1);
            return k.ToUpperInvariant();
        }

        // ------------------------------------------------ 空位置

        /// <summary>小分类 → 大类。跟 SubOf 反着来，只覆盖 PosOrder 里那些标准位置用到的。</summary>
        private static string CatOfSub(string sub)
        {
            switch (sub)
            {
                case "步枪": case "冲锋枪": case "霰弹枪": case "狙击枪":
                case "手枪": case "特殊武器":
                    return "枪械";
                // 这三样不算枪：各自一个大类，和枪械平级（撬棍 / 燃烧瓶 / 弹药升级箱）
                case "近战": return "近战";
                case "投掷物": return "投掷物";
                case "武器升级": return "武器升级";
                case "治疗与复活": case "暂时增益": case "其他药品":
                    return "药品";
                case "可搬道具": case "其他杂项":
                    return "杂项";
                case "特感": case "普通感染者":
                    return "特感";
                case "其他人物":
                    return "人物";
            }
            return "人物";                       // 一代 / 二代
        }

        /// <summary>
        /// 把「标准位置」补齐：库里没有对应 MOD 的枪 / 人物 / 药品也各占一行。
        /// 树本来就是一张「这个游戏里能换什么」的清单 —— 空着的那几行正好告诉人还缺什么，
        /// 也能先认位置再去找包，而不是对着一片空白猜自己少装了哪把枪。
        /// 名字和顺序都取 PosOrder 那一套（跟游戏里武器排列一致）；已经有位置的不重复加。
        /// </summary>
        private static void EnsureStandard(List<Slot> list)
        {
            List<string> cats = CatPrefs.Cats();
            for (int i = 0; i < PosOrder.GetLength(0); i++)
            {
                string sub = PosOrder[i, 0], nm = PosOrder[i, 1];
                string cat = CatOfSub(sub);
                // 大类被改名或删掉了：别拿旧名字再造一个空分类回来
                if (cats.IndexOf(cat) < 0) continue;
                if (HasStandard(list, cat, nm)) continue;

                Slot s = new Slot();
                s.Key = cat + "\u0001#empty:" + nm;
                s.Name = nm;
                s.Cat = cat;
                s.Sub = sub;
                s.Empty = true;
                list.Add(s);
            }

            string miss = UncoveredNames();
            if (miss.Length > 0)
                Core.Log("WARN", "TREE 识别表里这些名字没有位置，真出现会掉进未归类：" + miss);
        }

        /// <summary>这个大类下是不是已经有叫这个名字的位置（Disambiguate 加过「· AK47」尾巴的也算）。</summary>
        private static bool HasSlot(List<Slot> list, string cat, string nm)
        {
            foreach (Slot s in list)
            {
                if (s.Cat != cat) continue;
                if (s.Name == nm) return true;
                if (s.Name.StartsWith(nm + " · ", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>别的大类里已经有同名的真实位置（比如「燃烧弹药」被识别成了杂项）。</summary>
        private static bool NamedAnywhere(List<Slot> list, string nm)
        {
            foreach (Slot s in list)
            {
                if (s.Empty) continue;                    // 只看真货，空位不算
                if (s.Name == nm) return true;
                if (s.Name.StartsWith(nm + " · ", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>这个标准位置算不算「已经有了」：自己那类里有，或者别处已经有了同名真位置。</summary>
        private static bool HasStandard(List<Slot> list, string cat, string nm)
        {
            return HasSlot(list, cat, nm) || NamedAnywhere(list, nm);
        }

        /// <summary>自检用：标准位置里还有几个没进树（既没有 MOD 也没占位）。正常是 0。</summary>
        public static int MissingStandard()
        {
            List<string> cats = CatPrefs.Cats();
            List<Slot> list = Build();
            int miss = 0;
            for (int i = 0; i < PosOrder.GetLength(0); i++)
            {
                string cat = CatOfSub(PosOrder[i, 0]), nm = PosOrder[i, 1];
                if (cats.IndexOf(cat) < 0) continue;
                if (!HasStandard(list, cat, nm)) miss++;
            }
            return miss;
        }

        /// <summary>自检用：树里一共有几个空位置。</summary>
        public static int EmptyCount(List<Slot> list)
        {
            int n = 0;
            foreach (Slot s in list) if (s.Empty) n++;
            return n;
        }

        // ------------------------------------------------ 识别表对账

        /// <summary>
        /// 识别得出、但故意不给空位的名字，后面写理由。
        /// 有了这张豁免表，核对就能做到「整张识别表 100% 有着落」，同时不往树里塞没意义的空行。
        /// </summary>
        private static readonly string[] NoSlotNames =
        {
            "石头",   // concrete_spawnchunk：地图里碎混凝土块的共用模型，不是一个能单独换的道具
        };

        /// <summary>位置表里有没有这个名字（不管在哪个小分类下）。</summary>
        private static bool HasPosition(string nm)
        {
            for (int i = 0; i < PosOrder.GetLength(0); i++)
                if (PosOrder[i, 1] == nm) return true;
            return false;
        }

        /// <summary>位置表登记了多少个位置。</summary>
        public static int PositionCount { get { return PosOrder.GetLength(0); } }

        /// <summary>
        /// 识别表里的名字，有几个在位置表里没有家（返回「名字/名字」，正常是空串）。
        /// 不是空 = 识别表新加了一种东西、位置表忘了跟 —— 那种包真出现时只会掉进未归类，
        /// 树上看着像「没装这个 MOD」，其实装了没认出来。
        /// </summary>
        public static string UncoveredNames()
        {
            List<string> miss = new List<string>();
            foreach (string n in ModIdentify.Subjects())
            {
                if (HasPosition(n)) continue;
                if (Array.IndexOf(NoSlotNames, n) >= 0) continue;
                miss.Add(n);
            }
            return string.Join("/", miss.ToArray());
        }

        /// <summary>识别表里一共登记了多少种东西。</summary>
        public static int SubjectCount { get { return ModIdentify.Subjects().Count; } }

        /// <summary>
        /// 位置表和分类规则对不对得上：把每个标准位置反推一遍小分类，应该正好回到它自己那一层。
        /// 对不上 = 同一个东西会被分成两行（位置表写「铁锹」、规则却按「铁铲」另起一行那种）。
        /// </summary>
        public static string MisfiledNames()
        {
            List<string> bad = new List<string>();
            for (int i = 0; i < PosOrder.GetLength(0); i++)
            {
                string sub = PosOrder[i, 0], nm = PosOrder[i, 1];
                string got = SubOf(CatOfSub(sub), nm, "", "", "");
                if (got != sub) bad.Add(nm + "→" + got + "（表里写的是 " + sub + "）");
            }
            return string.Join("，", bad.ToArray());
        }

        /// <summary>这个 MOD 替换的位置名（人工优先，其次识别结果）。搜索和树共用一套说法。</summary>
        public static string SlotNameOf(ModGroup g)
        {
            ModItem m = g == null ? null : g.Main;
            if (m == null) return "";
            string hand = CatPrefs.SlotOf(m).Trim();
            if (hand.Length > 0) return hand;
            string nm = (m.Subject ?? "").Trim();
            return IsCountName(nm) ? "" : nm;
        }

        /// <summary>弹药堆：识别表给的显示名是「弹药」，名字里带「弹药堆」的也算。</summary>
        internal static bool IsAmmoPile(string nm)
        {
            string n = (nm ?? "").Trim();
            return n == "弹药" || n.IndexOf("弹药堆", StringComparison.Ordinal) >= 0;
        }

        internal static bool IsCountName(string s)
        {
            if (s == null || s.Length < 2 || s[s.Length - 1] != '件') return false;
            for (int i = 0; i < s.Length - 1; i++)
                if (!char.IsDigit(s[i]) && s[i] != ' ') return false;
            return true;
        }

        internal static bool Vague(string s)
        {
            return s == "步枪" || s == "冲锋枪" || s == "霰弹枪" || s == "投掷物"
                || s == "手枪" || s == "机枪" || s == "狙击枪" || s == "特殊武器"
                || s == "爆炸物" || s == "其他枪械" || s == "其他枪"
                || s == "幸存者" || s == "人物" || s == "近战" || s == "近战武器"
                || s == "其他" || s.Length == 0;
        }

        /// <summary>
        /// 这件东西是「叠加的扩展」还是「换本体的」。
        /// 依据是识别出来的「改了什么」，外加两条事实：
        ///   Only1P = 包里的模型全是第一人称（v_），没动世界模型；
        ///   AnimOnly = 包里的模型全是 anim_/gestures_ 表情骨架（KSEP 表情平台这类陪套）。
        ///   只换第一人称模型、表情动画、音效、HUD、特效、脚本 = 不和模型本体抢位置，可以作为扩展；
        ///   完整模型、材质、地图 = 换本体，一个位置只能有一套，两套会互相覆盖。
        /// </summary>
        public static bool IsExtra(ModGroup g)
        {
            if (g == null) return false;
            ModItem m = g.Main;
            if (m == null) return false;
            if (m.Only1P) return true;                                         // 只动了第一人称，和整套模型不抢
            if (m.AnimOnly) return true;                                       // 表情平台：不换人，谁都能叠
            string k = (m.Kinds ?? "").Trim();
            if (k.Length == 0) return false;                                   // 什么都读不出来：当本体，别乱叠
            if (k.IndexOf("模型", StringComparison.Ordinal) >= 0) return false;
            if (k.IndexOf("材质", StringComparison.Ordinal) >= 0) return false;  // 贴图替换也是换外观
            if (k.IndexOf("地图", StringComparison.Ordinal) >= 0) return false;
            return k.IndexOf("音效", StringComparison.Ordinal) >= 0
                || k.IndexOf("HUD", StringComparison.Ordinal) >= 0
                || k.IndexOf("特效", StringComparison.Ordinal) >= 0
                || k.IndexOf("脚本", StringComparison.Ordinal) >= 0;
        }

        // 泛称补全用：关键词 → 标准位置名。长的排前面，免得 "M4" 抢走 "M4A1"。
        private static readonly string[,] GunPairs = new string[,]
        {
            {"30发连狙","30发军用狙"},{"30连","30发军用狙"},{"军用狙","30发军用狙"},
            {"15发连狙","15发木狙"},{"15连","15发木狙"},{"木狙","15发木狙"},
            {"一代连喷","一代连喷"},{"二代连喷","二代连喷"},
            {"sg552","sg552"},
            {"AK-47","AK-47"},{"AK47","AK-47"},
            {"SCAR","SCAR-L"},{"三连发","SCAR-L"},
            {"AR15","AR15"},{"M4A1","M4"},{"M249","M249"},
            {"M16","M16"},{"AWP","AWP"},{"MP5","MP5"},{"M60","M60"},
            {"UZI","UZI"},{"MAC-10","MAC-10"},{"MAC10","MAC-10"},{"P90","P90"},
            {"铁喷","铁喷"},{"铁盆","铁喷"},{"木喷","木喷"},
            {"鸟狙","鸟狙"},{"猎枪","15发木狙"},{"连狙","30发军用狙"},
            {"榴弹","榴弹发射器"},
            {"燃烧瓶","燃烧瓶"},{"土制","土制炸弹"},{"胆汁","胆汁"},
            // 升级箱：名字跟 Identify 对齐（弹药包 / 激光瞄准）
            {"燃烧弹药","燃烧弹药包"},{"燃烧弹","燃烧弹药包"},
            {"爆破弹药","爆破弹药包"},{"高爆","爆破弹药包"},
            {"激光瞄准","激光瞄准"},{"激光","激光瞄准"},
            // 近战：长名字排前面，免得「板球拍」被「球棍」先接走
            {"武士刀","武士刀"},{"katana","武士刀"},
            {"砍刀","砍刀"},{"machete","砍刀"},
            {"撬棍","撬棍"},{"crowbar","撬棍"},
            {"消防斧","消防斧"},{"fireaxe","消防斧"},{"fire_axe","消防斧"},
            {"电锯","电锯"},{"chainsaw","电锯"},
            {"平底锅","平底锅"},{"frying","平底锅"},
            {"高尔夫","高尔夫球杆"},{"golf","高尔夫球杆"},
            {"板球","板球拍"},{"cricket","板球拍"},
            {"棒球棍","棒球棍"},{"球棍","棒球棍"},{"baseball","棒球棍"},
            {"电吉他","电吉他"},{"吉他","电吉他"},{"guitar","电吉他"},
            {"警棍","警棍"},{"nightstick","警棍"},{"tonfa","警棍"},
            {"干草叉","干草叉"},{"pitchfork","干草叉"},
            {"铁锹","铁锹"},{"铁铲","铁锹"},{"shovel","铁锹"},
            {"小刀","小刀"},{"knife","小刀"},
            {"马格南","马格南"},
        };

        private static readonly string[,] CharPairs = new string[,]
        {
            {"弗朗西斯","弗朗西斯"},{"Francis","弗朗西斯"},
            {"埃利斯","埃利斯"},{"Ellis","埃利斯"},
            {"路易斯","路易斯"},{"Louis","路易斯"},
            {"罗谢尔","罗谢尔"},{"Rochelle","罗谢尔"},
            {"教练","教练"},{"Coach","教练"},
            {"尼克","尼克"},{"Nick","尼克"},
            {"佐伊","佐伊"},{"Zoey","佐伊"},
            {"比尔","比尔"},{"Bill","比尔"},
        };

        /// <summary>「UZI/MAC-10」里是不是正好有 MAC-10 这一半。</summary>
        private static bool HasHalf(string nm, string one)
        {
            if (string.IsNullOrEmpty(nm) || string.IsNullOrEmpty(one)) return false;
            foreach (string p in nm.Split('/'))
                if (p.Trim() == one) return true;
            return false;
        }

        /// <summary>泛称 → 具体位置名。先看 MOD 所在目录，再看 MOD 自己的名字。</summary>
        private static string Concrete(string vague, ModGroup g, ModItem m)
        {
            string dir = "";
            string rel = m == null ? "" : (m.RelPath ?? "");
            int i = rel.LastIndexOf('\\');
            if (i > 0)
            {
                int j = rel.LastIndexOf('\\', i - 1);
                dir = j >= 0 ? rel.Substring(j + 1, i - j - 1) : rel.Substring(0, i);
            }
            string text = dir + " " + (g.Name ?? "") + " " + (m == null ? "" : (m.Name ?? ""));
            string hit = null;
            if (vague == "幸存者" || vague == "人物") hit = Find(CharPairs, text);
            if (hit == null) hit = Find(GunPairs, text);
            if (hit == null && vague.Length == 0) hit = Find(CharPairs, text);   // A/B 拆不开时再试人物
            return hit ?? "";
        }

        private static string Find(string[,] pairs, string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            for (int i = 0; i < pairs.GetLength(0); i++)
                if (text.IndexOf(pairs[i, 0], StringComparison.OrdinalIgnoreCase) >= 0) return pairs[i, 1];
            return null;
        }

        /// <summary>把方案树摊平成文本，方便不开界面核对分类对不对（--dumptree）。</summary>
        public static string DumpTree()
        {
            List<Slot> slots = Build();
            List<string> order = CatOrder(slots);
            slots.Sort(delegate(Slot a, Slot b)
            {
                int ia = order.IndexOf(a.Cat), ib = order.IndexOf(b.Cat);
                if (ia != ib) return ia.CompareTo(ib);
                int sa = SubRank(a.Sub), sb2 = SubRank(b.Sub);
                if (sa != sb2) return sa.CompareTo(sb2);
                int na = NameRank(a.Sub, a.Name), nb = NameRank(b.Sub, b.Name);
                if (na != nb) return na.CompareTo(nb);
                int c = string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
                return c != 0 ? c : string.Compare(a.Key, b.Key, StringComparison.Ordinal);
            });
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (string cat in order)
            {
                List<Slot> inCat = new List<Slot>();
                foreach (Slot s in slots) if (s.Cat == cat) inCat.Add(s);
                if (inCat.Count == 0) continue;
                sb.AppendLine();
                sb.AppendLine("【" + cat + "】");
                // 只有一层分组的大类不画那行标题，跟界面里一致
                int subCount = 0;
                string prev = null;
                foreach (Slot s in inCat) if (s.Sub != prev) { prev = s.Sub; subCount++; }
                bool showSub = subCount > 1;
                string lastSub = null;
                foreach (Slot s in inCat)
                {
                    if (showSub && s.Sub != lastSub) { sb.AppendLine("  〈" + s.Sub + "〉"); lastSub = s.Sub; }
                    sb.AppendLine("    " + s.Name + "  (" + s.Items.Count + " 个"
                        + (s.Extras.Count > 0 ? " + 扩展 " + s.Extras.Count : "") + ")"
                        + (s.Manual ? " [人工]" : ""));
                }
            }
            string f = System.IO.Path.Combine(Core.LogDir, "方案树.txt");
            System.IO.File.WriteAllText(f, sb.ToString(), new System.Text.UTF8Encoding(true));
            return f;
        }

        /// <summary>树里的大类顺序：跟着分类顺序走，地图/喷漆不进树。</summary>
        /// <summary>
        /// 方案树 / 总览共用的排序：大类 → 小分类 → 手工拖过的位置 → 名字。
        /// isOn 只是平局时让已装的排前面，传 null 表示不看勾选状态。
        /// </summary>
        public static void SortForTree(List<Slot> slots, Func<Slot, bool> isOn)
        {
            List<string> order = CatOrder(slots);
            slots.Sort(delegate(Slot a, Slot b)
            {
                int ia = order.IndexOf(a.Cat), ib = order.IndexOf(b.Cat);
                if (ia != ib) return ia.CompareTo(ib);
                int sa = CatTree.SubSortKey(a), sb = CatTree.SubSortKey(b);
                if (sa != sb) return sa.CompareTo(sb);
                int pa = CatTree.PosSortKey(a), pbb = CatTree.PosSortKey(b);
                bool ha = pa >= 0, hb = pbb >= 0;
                if (ha != hb) return ha ? -1 : 1;
                if (ha && pa != pbb) return pa.CompareTo(pbb);
                if (a.Rank != b.Rank) return a.Rank.CompareTo(b.Rank);
                int na = NameRank(a.Sub, a.Name), nb = NameRank(b.Sub, b.Name);
                if (na != nb) return na.CompareTo(nb);
                int ra = CatPrefs.SlotRank(a.Cat, a.Name), rb = CatPrefs.SlotRank(b.Cat, b.Name);
                if (ra != rb) return ra.CompareTo(rb);
                if (isOn != null)
                {
                    bool x = isOn(a), y = isOn(b);
                    if (x != y) return x ? -1 : 1;
                }
                int c = string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
                return c != 0 ? c : string.Compare(a.Key, b.Key, StringComparison.Ordinal);
            });
        }

        public static List<string> CatOrder(List<Slot> slots)        {
            List<string> order = new List<string>();
            foreach (string c in CatPrefs.Cats())
                if (c != "地图" && c != "喷漆" && !order.Contains(c)) order.Add(c);
            foreach (string c in Catalog.Cats)
                if (c != "地图" && c != "喷漆" && !order.Contains(c)) order.Add(c);
            foreach (Slot s in slots) if (!order.Contains(s.Cat)) order.Add(s.Cat);
            return order;
        }
    }

    /// <summary>树里的一行：大类标题、小分类标题，或者一个槽位。</summary>
    internal sealed class TreeRow
    {
        public bool IsCat;
        public bool IsSub;       // 小分类标题：枪械 →「步枪」
        public bool IsNote;      // 「未归类」那一行：只是个说明入口
        public string Cat;
        public string Sub;
        public Slot Slot;
    }

    // ============================================================
    //  方案树视图
    // ============================================================
    internal sealed class CatTree : UserControl
    {
        private readonly MainForm mf;
        private readonly ScrollHost scroll;
        private ColHead headMain, headExtra;
        private readonly SlotTree body;
        private readonly ListView lv;
        private readonly ListView lvExtra;                 // 下半：可叠加的扩展
        private readonly Label lblTitle, lblCur, lblFoot;
        private readonly Label lblExtraTitle;
        private readonly FlatButton btnPick;
        private readonly FlatButton btnCat;                // 未归类页的手动入口：指定「算哪类 + 替换什么」
        private readonly FlatButton btnDeploy;             // 「只装这里」：只铺当前分支勾上的 vpk
        private readonly FlatButton btnApply;               // 「应用改动」：把攒下的开关改动写进 addonlist
        private readonly ToolTip tipApply;                 // 「应用改动」的悬停说明（按钮常驻，得让人知道它管什么）

        private List<Slot> slots = new List<Slot>();
        private int unmatched;                       // 一个位置都归不上的 MOD 数
        private readonly List<ModGroup> unmatchedGroups = new List<ModGroup>();   // 具体是哪几组（未归类页列出来）
        private readonly HashSet<string> folded = new HashSet<string>(StringComparer.Ordinal);
        // 界面上点了开关、但还没写进 addonlist.txt 的改动：vpk 名 → 是否开。
        // 攒着不写是为了「点错能反悔」，也避免每点一次就动一次游戏文件；最后统一按「应用改动」落地。
        private readonly Dictionary<string, bool> pending = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private string selKey = "";

        public string SelectedKey { get { return selKey; } }
        public bool IsFolded(string cat) { return folded.Contains(cat); }
        /// <summary>自检 / 截图用：树本体（行号、拖动预览这些都在它身上）。</summary>
        internal SlotTree Body { get { return body; } }
        /// <summary>台面用：树排好的位置表（顺序也在这上面，台面照它的顺序建卡）。</summary>
        internal List<Slot> Slots { get { return slots; } }
        /// <summary>按分支名找分支槽（台面右键重命名用）。</summary>
        internal Slot BranchByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (Slot s in slots)
                if (IsBranchSlot(s) && string.Equals(s.Name, name, StringComparison.Ordinal)) return s;
            return null;
        }

        /// <summary>换主题后重建状态点：位图里存的是旧颜色，不重建会留在深底上看不清。</summary>
        internal void OnThemeChanged()
        {
            try
            {
                if (lv != null)
                {
                    ImageList old = lv.StateImageList;
                    lv.StateImageList = Dots();
                    if (old != null) old.Dispose();
                }
                if (lvExtra != null)
                {
                    ImageList old = lvExtra.StateImageList;
                    lvExtra.StateImageList = Dots();
                    if (old != null) old.Dispose();
                }
                if (scroll != null) scroll.BackColor = Theme.TreeBg;
                BackColor = Theme.Surface;
                body.BackColor = Theme.TreeBg;
                Invalidate(true);
            }
            catch { }
        }

        public CatTree(MainForm owner)
        {
            mf = owner;
            BackColor = Theme.Surface;
            Font = Theme.UI;

            // ---- 左：树 ----
            scroll = new ScrollHost();
            scroll.BackColor = Theme.TreeBg;
            Controls.Add(scroll);

            body = new SlotTree(this);
            body.BackColor = Theme.TreeBg;
            scroll.Attach(body);
            // 树比可视区矮的时候，下面这块空白是 ScrollHost 的，不接这个就「右键空白处没菜单」
            scroll.BlankMenu = delegate(Point at) { TreeMenuFor(null, at); };

            // ---- 右上：换本体的候选 ----
            lv = NewCandList("MOD");
            lv.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;      // 右键只是弹菜单，别顺手换掉 MOD
                ListViewItem li = lv.GetItemAt(e.X, e.Y);
                if (li == null) return;
                // 点「开关」那一列 = 只翻 addonlist 的 1/0，不换 MOD；点别处还是老行为（换掉这个位置）
                if (ColAt(lv, e.X) == 4) { ToggleSwitch(li.Tag as ModGroup); return; }
                ModGroup g = li.Tag as ModGroup;
                if (selKey == "#search") { RevealGroup(g); return; }        // 搜索结果：跳回它在树里的位置
                Slot cur = Current;
                if (cur != null && cur.Pile) { TogglePile(g); return; }     // 地图 / 我的分支：开 / 关，不替换
                Choose(g);
            };
            lv.MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right) SelectRow(lv, e);
            };
            // 双击套装行 = 挑部件（医疗包那种「模型 + 基础材质 + 选一个颜色」的组合）。
            // 单击已经套了默认部件；想换颜色只能走这里，不能让这条路只剩右键菜单里那条。
            lv.DoubleClick += delegate
            {
                if (lv.SelectedItems.Count == 0) return;
                if (selKey == "#unmatched") { mf.SetModCatIn(lv); return; }   // 未归类页：双击 = 手动指定
                ModGroup g = lv.SelectedItems[0].Tag as ModGroup;
                if (g == null || !g.IsSet) return;
                mf.PickPartsIn(lv);
                FillDetail();
            };
            lv.ContextMenuStrip = mf.BuildTreeMenu(lv);
            lv.Resize += delegate { LayoutCols(); };
            lv.AccessibleName = "当前替换位置的 MOD 候选";
            lv.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter && e.KeyCode != Keys.Space) return;
                if (lv.SelectedItems.Count == 0) return;
                ModGroup g = lv.SelectedItems[0].Tag as ModGroup;
                if (g == null) return;
                if (selKey == "#search") RevealGroup(g);
                else if (Current != null && Current.Pile) TogglePile(g);
                else Choose(g);
                FillDetail();
                e.Handled = true;
                e.SuppressKeyPress = true;
            };
            Controls.Add(lv);
            headMain = new ColHead(lv);
            Controls.Add(headMain);

            // ---- 右下：可叠加的扩展（开火音效 / 只改第一人称模型 / HUD）----
            lvExtra = NewCandList("扩展");
            lvExtra.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                ListViewItem li = lvExtra.GetItemAt(e.X, e.Y);
                if (li == null) return;
                if (ColAt(lvExtra, e.X) == 4) { ToggleSwitch(li.Tag as ModGroup); return; }   // 开关列：只翻开关
                ToggleExtra(li.Tag as ModGroup);
            };
            lvExtra.Resize += delegate { LayoutCols(); };
            lvExtra.AccessibleName = "可叠加扩展";
            lvExtra.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter && e.KeyCode != Keys.Space) return;
                if (lvExtra.SelectedItems.Count == 0) return;
                ToggleExtra(lvExtra.SelectedItems[0].Tag as ModGroup);
                e.Handled = true;
                e.SuppressKeyPress = true;
            };
            Controls.Add(lvExtra);
            headExtra = new ColHead(lvExtra);
            Controls.Add(headExtra);

            lblTitle = new Label();
            lblTitle.Font = Theme.UIBold;
            lblTitle.ForeColor = Theme.Ink;
            lblTitle.BackColor = Color.Transparent;
            lblTitle.AutoEllipsis = true;
            Controls.Add(lblTitle);

            lblCur = new Label();
            lblCur.Font = Theme.UISmall;
            lblCur.ForeColor = Theme.Ink2;
            lblCur.BackColor = Color.Transparent;
            lblCur.AutoEllipsis = true;
            Controls.Add(lblCur);

            lblFoot = new Label();
            lblFoot.Font = Theme.UISmall;
            lblFoot.ForeColor = Theme.Ink3;
            lblFoot.BackColor = Color.Transparent;
            lblFoot.AutoEllipsis = true;
            Controls.Add(lblFoot);

            lblExtraTitle = new Label();
            lblExtraTitle.Font = Theme.UISmall;
            lblExtraTitle.ForeColor = Theme.Ink3;
            lblExtraTitle.BackColor = Color.Transparent;
            lblExtraTitle.AutoEllipsis = true;
            lblExtraTitle.Visible = false;
            Controls.Add(lblExtraTitle);

            btnPick = new FlatButton();
            btnPick.Text = "留原版";
            btnPick.SetBounds(0, 0, 66, Theme.H);
            btnPick.Click += delegate { Choose(null); };
            btnPick.Visible = false;          // 列表里已经有「留原版」那一行，按钮就多余了
            Controls.Add(btnPick);

            // 未归类页的手动入口：选中一行点它（或双击那行），告诉程序这个 MOD 算哪一类、替换什么。
            // 用的是和「设置 MOD 分类…」同一个窗口，保存后立刻重新归组，认出位置就排进树里。
            btnCat = new FlatButton();
            btnCat.Text = "指定位置…";
            btnCat.SetBounds(0, 0, 96, Theme.H);
            btnCat.Click += delegate { mf.SetModCatIn(lv); };
            btnCat.Visible = false;
            Controls.Add(btnCat);

            // 「只装这里」：方案树是按位置看的，经常只想试一处（换个枪模看看像不像），
            // 不想为这一个 vpk 把整套重铺一遍。这个按钮只动当前位置勾上的文件。
            btnDeploy = new FlatButton();
            btnDeploy.Text = "只装这里";
            btnDeploy.SetBounds(0, 0, 92, Theme.H);
            btnDeploy.Visible = false;
            btnDeploy.Click += delegate { DeployHere(); };
            Controls.Add(btnDeploy);

            // 「应用改动」：常驻右下角（以前是攒了改动才冒出来，结果没人找得到它）。
            // 有改动时变实心、标题带条数；没改动时是灰的，点了直接告诉你先点「开关」列。
            // 不自动写是为了点错能反悔，也避免每点一下都动一次游戏文件。
            btnApply = new FlatButton();
            btnApply.Text = "应用改动";
            btnApply.SetBounds(0, 0, 104, Theme.H);
            btnApply.Click += delegate { ApplyHere(); };
            Controls.Add(btnApply);

            tipApply = new ToolTip();
            tipApply.SetToolTip(btnApply, "把「开关」列里点出来的改动一次性写进 left4dead2\\addonlist.txt（只改 1/0，不动任何 vpk 文件）");
            // 小标题在窄窗口放不下整句话，完整的解释挂在这里
            tipApply.SetToolTip(lblExtraTitle,
                "可叠加的扩展 = 不换本体模型、能和模型同时生效的东西：开火音效、第一人称手部模型、HUD 图标。\r\n"
                + "这个位置目前还没有这类 MOD；有的话会列在下面，点一下开 / 关，可以多选。");

            Resize += delegate { LayoutSelf(); };
            UpdateApplyButton();
            LayoutSelf();
        }

        /// <summary>候选列表的共同外观：三列、圆点状态、不显示表头焦点。</summary>
        private ListView NewCandList(string firstCol)
        {
            ListView v = new ListView();
            v.View = View.Details;
            v.FullRowSelect = true;
            v.MultiSelect = false;
            v.BorderStyle = BorderStyle.None;
            v.BackColor = Theme.Surface;
            v.ForeColor = Theme.Ink;
            v.Font = Theme.UI;
            v.HeaderStyle = ColumnHeaderStyle.None;   // 原生表头在深色下永远是一块白，改由 ColHead 自绘
            v.ShowItemToolTips = true;
            v.HideSelection = false;
            ImageList pad = new ImageList();
            pad.ImageSize = new Size(1, 28);
            pad.ColorDepth = ColorDepth.Depth32Bit;
            v.SmallImageList = pad;
            v.StateImageList = Dots();
            v.Columns.Add(firstCol, 380);
            v.Columns.Add("改了什么", 100);
            v.Columns.Add("大小", 74).TextAlign = HorizontalAlignment.Right;
            v.Columns.Add("已装", 84);                        // 游戏 addons 里有没有这一份
            v.Columns.Add("开关 ⇄", 62);                        // addonlist 里是 1 还是 0（点一下能翻，⇄ 是这个意思）
            return v;
        }

        /// <summary>两列状态：3=已装（在不在游戏里）、4=开关（1/0，可点）。同一条判定给分类列表也用了。</summary>
        private void AddStateCells(ListViewItem li, ModGroup g)
        {
            // 每一列保留自己的状态色和字号；否则整行样式会盖住「已装 / 开关」列。
            li.UseItemStyleForSubItems = false;
            li.SubItems.Add("");
            li.SubItems.Add("");
            ApplyStateCells(li, g);
        }

        /// <summary>只改两列的文字与颜色，不动整行结构 —— 点开关时用它，列表不重排、滚动位置不跳。</summary>
        private void ApplyStateCells(ListViewItem li, ModGroup g)
        {
            if (li.SubItems.Count < 5) return;
            int total, inst, on, game;
            MainForm.CountState(g, out total, out inst, out on, out game);
            int shownOn = 0, ins = 0;
            foreach (ModItem p in g.Parts)
            {
                if (!(p.FromGame || p.Installed)) continue;
                ins++;
                if (PendingOf(p)) shownOn++;
            }

            // 本地模式：游戏 addons 自己就是库，「已装 / 仅游戏内」这一维度恒为真、一点信息量都没有。
            // 那一列已经在 Cols 里收成 0 宽，剩下的唯一状态就是 addonlist 的开关 → 开启 / 关闭。
            bool local = Core.IsLocal;

            // 已装列（本地模式下宽度为 0，看不见，只留空串）
            string instText; Color instInk;
            if (local) { instText = ""; instInk = Theme.Ink3; }
            else if (inst == 0) { instText = "未装"; instInk = Theme.Ink3; }
            else if (inst < total) { instText = "部分 " + inst + "/" + total; instInk = Theme.Info; }
            else if (game == total) { instText = "仅游戏内"; instInk = Theme.Ink2; }
            else { instText = "已装"; instInk = Theme.Ok; }

            // 开关列：只有装进去的才谈得上开关。
            // 本地模式下这列就是唯一的状态列，所以用「开启 / 关闭」说全称，不再用「开 / 关」。
            string swText; Color swInk; Color rowBg;
            if (ins == 0) { swText = "—"; swInk = Theme.Ink3; rowBg = Theme.Surface; }
            else if (shownOn == ins) { swText = local ? "开启" : "开"; swInk = Theme.Ok; rowBg = Theme.RowOn; }
            else if (shownOn == 0) { swText = local ? "关闭" : "关"; swInk = Theme.Warn; rowBg = Theme.RowOff; }
            else { swText = (local ? "开启 " : "") + shownOn + "/" + ins; swInk = Theme.Warn; rowBg = Theme.RowOff; }

            // 攒着还没落盘的（pending 里有、且跟文件里的实际值不一样）加个 *：
            // 一眼就能看出「这三行是我点过但还没写进游戏的」
            if (ins > 0)
            {
                bool dirty = false;
                foreach (ModItem p in g.Parts)
                {
                    if (!(p.FromGame || p.Installed)) continue;
                    bool v;
                    if (pending.TryGetValue(p.Name, out v) && v != p.AddonOn) { dirty = true; break; }
                }
                if (dirty) swText += " *";
            }

            li.SubItems[3].Text = instText;
            li.SubItems[3].ForeColor = instInk;
            li.SubItems[3].Font = Theme.UISmall;
            li.SubItems[4].Text = swText;
            li.SubItems[4].ForeColor = swInk;
            li.SubItems[4].Font = Theme.UIBold;
            if (li.BackColor != rowBg) li.BackColor = rowBg;
        }

        /// <summary>这个 vpk 现在的开关值：还没应用的改动优先，否则读扫描到的实际值。</summary>
        private bool PendingOf(ModItem p)
        {
            bool v;
            if (pending.TryGetValue(p.Name, out v)) return v;
            return p.AddonOn;
        }

        private static void SelectRow(ListView v, MouseEventArgs e)
        {
            ListViewItem hit = v.GetItemAt(e.X, e.Y);
            if (hit == null || hit.Selected) return;
            v.SelectedItems.Clear();
            hit.Selected = true;
            hit.Focused = true;
        }

        private static ImageList Dots()
        {
            ImageList il = new ImageList();
            il.ImageSize = new Size(14, 14);
            il.ColorDepth = ColorDepth.Depth32Bit;
            il.TransparentColor = Color.Transparent;
            il.Images.Add(Dot(false));
            il.Images.Add(Dot(true));
            return il;
        }

        private static Bitmap Dot(bool on)
        {
            Bitmap b = new Bitmap(14, 14);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (Pen p = new Pen(on ? Theme.Mark : (Theme.IsDark ? Color.FromArgb(96, 103, 112) : Color.FromArgb(184, 192, 200)), 1.5f))
                    g.DrawEllipse(p, 2.5f, 2.5f, 8f, 8f);
                if (on)
                    using (SolidBrush br = new SolidBrush(Theme.Mark))
                        g.FillEllipse(br, 5f, 5f, 4f, 4f);
            }
            return b;
        }

        // ---------------------------------------------------------- 构建

        /// <summary>重新分组并铺树。库变了才需要调用。</summary>
        /// <summary>只在数量变化时记一条，免得每改一次分类就刷一遍日志。</summary>
        void LogUnmatched()
        {
            if (unmatched == _lastUnmatchedLogged) return;
            _lastUnmatchedLogged = unmatched;
            if (unmatched <= 0) return;
            List<string> names = new List<string>();
            for (int i = 0; i < unmatchedGroups.Count && i < 5; i++) names.Add(unmatchedGroups[i].Name);
            Core.Log("INFO", "有 " + unmatched + " 个 MOD 没认出位置，收在方案树最底部「未归类」："
                + string.Join("、", names.ToArray()) + (unmatched > names.Count ? " 等" : ""));
        }
        int _lastUnmatchedLogged = -1;

        public void Rebuild()
        {
            slots = SlotIndex.Build();
            SortSlots();
            BuildPiles();                       // 地图 / 喷漆 / 我的分支：能叠着开的那些，排在最后
            // 未归类 = 库里剩下的、没进任何位置的 MOD，一个不漏全收进列表。
            HashSet<ModGroup> placed = new HashSet<ModGroup>();
            foreach (Slot s0 in slots)
            {
                foreach (ModGroup g0 in s0.Items) placed.Add(g0);
                foreach (ModGroup g0 in s0.Extras) placed.Add(g0);
            }
            unmatchedGroups.Clear();
            foreach (ModGroup g in Catalog.Groups)
            {
                if (g.Cat == "地图" || g.Cat == "喷漆") continue;
                if (placed.Contains(g)) continue;
                unmatchedGroups.Add(g);
            }
            unmatchedGroups.Sort(delegate(ModGroup a, ModGroup b)
            { return string.Compare(a.Name, b.Name, StringComparison.CurrentCulture); });
            unmatched = unmatchedGroups.Count;
            LogUnmatched();
            LoadFold();                          // 折叠状态：第一次打开 = 小分类全收起，只留目录
            if (selKey.Length > 0 && selKey != "#unmatched")
            {
                bool ok = false;
                foreach (Slot s in slots) if (s.Key == selKey) { ok = true; break; }
                if (!ok) selKey = "";                       // 分类一改，原来那个位置可能没了
            }
            if (selKey.Length == 0 && slots.Count > 0)
            {
                // 首选「有 MOD 的位置」：树头几行常常是空位，一进来就停在「还没有 MOD」上像出了事
                Slot first = null;
                foreach (Slot s in slots) if (!s.Empty) { first = s; break; }
                selKey = (first != null ? first : slots[0]).Key;
            }
            RevealFold(selKey);                  // 收起状态下，选中的那行也得看得见
            BuildRows();
            FillDetail();
        }

        /// <summary>小分类排序键：手工拖过就用手工序号，没拖过用识别层的固有顺序（步枪 → 霰弹枪 …）。</summary>
        internal static int SubSortKey(Slot s)
        {
            int m = CatPrefs.SubRankOf(s.Cat, s.Sub);
            return m >= 0 ? m : SlotIndex.SubRank(s.Sub);
        }

        /// <summary>位置排序键：-1 = 这个小分类没被手工排过，交给后面的自动规则。</summary>
        internal static int PosSortKey(Slot s)
        {
            if (!CatPrefs.HasPosOrder(s.Cat, s.Sub)) return -1;
            int m = CatPrefs.PosRankOf(s.Cat, s.Sub, s.Name);
            return m >= 0 ? m : 9000 + SlotIndex.NameRank(s.Sub, s.Name);
        }

        /// <summary>某个大类当前的小分类顺序（就是树里看到的顺序，落底稿时用）。</summary>
        internal List<string> CurrentSubs(string cat)
        {
            List<string> r = new List<string>();
            foreach (Slot s in slots)
                if (s.Cat == cat && !r.Contains(s.Sub)) r.Add(s.Sub);
            return r;
        }

        /// <summary>某个小分类当前的位置顺序（树里看到的顺序）。</summary>
        internal List<string> CurrentPositions(string cat, string sub)
        {
            List<string> r = new List<string>();
            foreach (Slot s in slots)
                if (s.Cat == cat && s.Sub == sub) r.Add(s.Name);
            return r;
        }

        private void SortSlots()
        {
            // 排序规则搬到 SlotIndex 里：总览也要用同一套顺序，
            // 两边各写一份迟早会飘（第一次做白模就因为顺序不同，19 个位置每个都触发分组标题）
            SlotIndex.SortForTree(slots, HasChecked);
        }

        /// <summary>
        /// 排在最后的三类「一堆」：
        ///   地图 / 喷漆 —— 能叠好几张，没有「一个位置只能一个」的说法，所以不做槽位互斥；
        ///   我的分支 —— 程序认不出改的是什么的，用户自己拉个分组装进去，同样允许叠着开。
        /// 它们都是 Slot.Pile=true，点一行 = 开 / 关，不会把同堆里别的顶掉。
        /// </summary>
        private void BuildPiles()
        {
            AddPile("地图", "三方图", "地图", delegate(ModGroup g) { return g.Cat == "地图"; });
            AddPile("喷漆", "喷漆", "喷漆", delegate(ModGroup g) { return g.Cat == "喷漆"; });

            // 我的分支：就算一个分支都没有，也留一行「＋ 新建分支…」，
            // 否则用户永远不知道有这个功能（没有别的入口）。
            MyBranch.Ensure();
            int rank = 0;
            foreach (string nm in MyBranch.Names())
            {
                Slot s = new Slot();
                s.Key = "#mine:" + nm;
                s.Name = nm;
                s.Cat = "我的分支";
                s.Sub = "";
                s.Pile = true;
                s.Rank = rank++;                 // Names() 是父子顺序，Rank 保住这个顺序
                foreach (ModGroup g in Catalog.Groups)
                    if (g.Main != null && MyBranch.BranchOf(g.Main.Name) == nm) s.Items.Add(g);
                slots.Add(s);
            }
            Slot add = new Slot();
            add.Key = "#mine+new";
            add.Name = "＋ 新建分支…";
            add.Cat = "我的分支";
            add.Sub = "";
            add.Pile = true;
            add.Rank = 100000;                   // 永远排在分支后面
            slots.Add(add);
        }

        private void AddPile(string cat, string name, string key, Predicate<ModGroup> pick)
        {
            Slot s = new Slot();
            s.Key = "#pile:" + key;
            s.Name = name;
            s.Cat = cat;
            s.Sub = "";
            s.Pile = true;
            foreach (ModGroup g in Catalog.Groups) if (pick(g)) s.Items.Add(g);
            if (s.Items.Count > 0) slots.Add(s);
        }

        private void BuildRows()
        {
            List<TreeRow> rows = new List<TreeRow>();
            List<string> order = SlotIndex.CatOrder(slots);
            foreach (string cat in order)
            {
                List<Slot> inCat = new List<Slot>();
                foreach (Slot s in slots) if (s.Cat == cat) inCat.Add(s);
                if (inCat.Count == 0) continue;

                TreeRow h = new TreeRow();
                h.IsCat = true; h.Cat = cat;
                rows.Add(h);
                if (folded.Contains(cat)) continue;

                // 小分类（枪械 → 步枪 → AK-47）。slots 已按小分类排好，这里顺着切段。
                // 一大类底下只有一个分组时不画那行标题 ——「特感 → 特感 → Tank」这种重复层没意义。
                int subCount = 0;
                string prevSub = null;
                foreach (Slot s in inCat) if (s.Sub != prevSub) { prevSub = s.Sub; subCount++; }
                bool showSub = subCount > 1;
                string lastSub = null;
                foreach (Slot s in inCat)
                {
                    if (showSub && s.Sub != lastSub)
                    {
                        lastSub = s.Sub;
                        TreeRow sh = new TreeRow();
                        sh.IsSub = true; sh.Cat = cat; sh.Sub = s.Sub;
                        rows.Add(sh);
                    }
                    if (showSub && folded.Contains(SubKey(cat, s.Sub))) continue;        // 这一段整段收起
                    TreeRow r = new TreeRow();
                    r.Slot = s; r.Cat = cat; r.Sub = s.Sub;
                    rows.Add(r);
                }
            }
            if (unmatched > 0)
            {
                TreeRow h = new TreeRow();
                h.IsCat = true; h.Cat = "未归类";
                rows.Add(h);
                if (!folded.Contains("未归类"))
                {
                    TreeRow n = new TreeRow();
                    n.IsNote = true; n.Cat = "未归类";
                    rows.Add(n);
                }
            }
            body.SetRows(rows);
        }

        // ---------------------------------------------------------- 状态

        internal bool HasChecked(Slot s)
        {
            foreach (ModGroup g in s.Items) if (mf.CountChecked(g) > 0) return true;
            return false;
        }

        /// <summary>这个位置当前勾了几件（正常是 0 或 1；>1 说明用户手工叠了）。</summary>
        internal int CheckedGroups(Slot s)
        {
            int n = 0;
            foreach (ModGroup g in s.Items) if (mf.CountChecked(g) > 0) n++;
            return n;
        }

        /// <summary>这个位置勾了几个可叠加的扩展（音效 / 只改第一人称模型 / HUD），可以和模型同时生效。</summary>
        internal int CheckedExtras(Slot s)
        {
            int n = 0;
            foreach (ModGroup g in s.Extras) if (mf.CountChecked(g) > 0) n++;
            return n;
        }

        /// <summary>
        /// 扩展分小类用：直接用识别出来的属性名（模型/音效/HUD/特效/脚本）当组名。
        /// 新加的 MOD 属性是什么就自动进什么组，不用再加分类规则。
        /// 多属性的取排序靠前的那个；扩展里带「模型」的必然是只改第一人称的（完整模型当不了扩展）。
        /// </summary>
        internal static string ExtraCat(ModGroup g)
        {
            if (g == null || g.Main == null) return "其他";
            if (g.Main.AnimOnly) return "表情";                                 // KSEP 这类表情平台
            string k = (g.Main.Kinds ?? "").Trim();
            if (g.Main.Only1P && k.IndexOf("模型", StringComparison.Ordinal) >= 0) return "模型";
            if (k.IndexOf("音效", StringComparison.Ordinal) >= 0) return "音效";
            if (k.IndexOf("HUD", StringComparison.Ordinal) >= 0) return "HUD";
            if (k.IndexOf("特效", StringComparison.Ordinal) >= 0) return "特效";
            if (k.IndexOf("脚本", StringComparison.Ordinal) >= 0) return "脚本";
            return k.Length > 0 ? k.Replace(",", "·") : "其他";
        }

        /// <summary>扩展小类的固定排序（就是属性顺序），界面上从上到下按这个出。</summary>
        private static readonly string[] ExtraCatOrder = { "模型", "表情", "音效", "HUD", "特效", "脚本" };
        private static int ExtraCatRank(string cat)
        {
            for (int i = 0; i < ExtraCatOrder.Length; i++)
                if (cat == ExtraCatOrder[i]) return i;
            return ExtraCatOrder.Length;
        }

        /// <summary>点扩展行：开着的关掉，关着的打开 —— 和模型不是二选一，各管各的。</summary>
        internal void ToggleExtra(ModGroup g)
        {
            if (g == null || Current == null) return;
            if (mf.CountChecked(g) > 0) mf.UncheckGroup(g);
            else mf.CheckGroupDefault(g);
            mf.AfterTreePick();
            FillDetail();
        }

        /// <summary>这个位置当前用了哪个 MOD（多件时取第一件）。</summary>
        internal ModGroup ChosenOf(Slot s)
        {
            foreach (ModGroup g in s.Items) if (mf.CountChecked(g) > 0) return g;
            return null;
        }

        // ---------------- 「一堆」：地图 / 喷漆 / 我的分支 ----------------

        /// <summary>点「一堆」里的行：开着的关掉，关着的打开 —— 同堆别的照旧，不顶掉。</summary>
        internal void TogglePile(ModGroup g)
        {
            if (g == null) return;
            if (mf.CountChecked(g) > 0) mf.UncheckGroup(g);
            else mf.CheckGroupDefault(g);
            mf.AfterTreePick();
            FillDetail();
        }

        /// <summary>分支位置？（键以 #mine: 开头）</summary>
        internal static bool IsBranchSlot(Slot s)
        {
            return s != null && s.Key != null && s.Key.StartsWith("#mine:", StringComparison.Ordinal);
        }

        /// <summary>这个 MOD 在树里哪一格：搜索结果点一下、托盘定位都走它。</summary>
        internal bool RevealGroup(ModGroup g)
        {
            if (g == null) return false;
            foreach (Slot s in slots)
            {
                if (s.Pile) continue;
                if (s.Items.Contains(g) || s.Extras.Contains(g)) return GoSlot(s.Key);
            }
            foreach (Slot s in slots)
                if (s.Pile && s.Items.Contains(g)) return GoSlot(s.Key);
            selKey = "#unmatched";
            body.Invalidate();
            FillDetail();
            return false;
        }

        private bool GoSlot(string key)
        {
            RevealFold(key);                     // 目标被折叠着就先展开，不然跳过去是空白
            selKey = key;
            body.Invalidate();
            FillDetail();
            body.ScrollToSlot(key);
            return true;
        }

        /// <summary>新建分支：树上「＋ 新建分支…」那行、右键菜单都走这里。</summary>
        internal void NewBranch()
        {
            NewBranch(null);
        }

        /// <summary>新建分支。parent 不为空 = 建在它下面（子分支）。</summary>
        internal void NewBranch(Slot parent)
        {
            NewBranchUnder(parent == null ? "" : parent.Name);
        }

        /// <summary>按父分支名新建（右键「新建同级分支」、拖到某行末位时也走这里）。</summary>
        internal void NewBranchUnder(string into)
        {
            into = into ?? "";
            string tip = into.Length > 0
                ? "子分支名（会缩进挂在「" + into + "」下面）。建好后右键任意 MOD 行 →「放进我的分支」。"
                : "分支名（例：音效 / 整活 / 收藏）。建好后右键任意 MOD 行，把它放进来。";
            string nm = PromptForm.Ask(tip, into.Length > 0 ? "在「" + into + "」下新建分支" : "新建分支");
            if (nm == null) return;
            string err = MyBranch.Create(nm, into);
            if (err != null) { MessageBox.Show(err, "建不了这个分支"); return; }
            string final = MyBranch.CleanName(nm);
            Rebuild();
            GoSlot("#mine:" + final);
            lblFoot.Text = into.Length > 0
                ? "子分支「" + final + "」建好了（在「" + into + "」下面）· 右键任意 MOD 行 →「放进我的分支」"
                : "分支「" + final + "」建好了 · 右键任意 MOD 行 →「放进我的分支」";
        }

        /// <summary>菜单文字里放 MOD 名用：太长就截断，免得菜单横到屏幕外。</summary>
        private static string Shorten(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        /// <summary>建一个和它同级的兄弟分支（右键分支 →「新建同级分支…」）。</summary>
        internal void NewBranchSibling(Slot s)
        {
            if (!IsBranchSlot(s)) return;
            NewBranchUnder(MyBranch.ParentOf(s.Name));
        }

        /// <summary>拖动松手后真正挪位置：只动分组记录，vpk 文件和勾选一个都不碰。</summary>
        internal bool MoveBranch(string name, string newParent, string before)
        {
            string err = MyBranch.Move(name, newParent, before);
            if (err != null)
            {
                lblFoot.Text = "没挪成：" + err;
                return false;
            }
            Rebuild();
            GoSlot("#mine:" + name);
            lblFoot.Text = "「" + name + "」已移到"
                + (string.IsNullOrEmpty(newParent) ? "顶层" : "「" + newParent + "」下面")
                + " · 拖住分支行还能再换位置";
            return true;
        }

        /// <summary>拖动大类排序：移到 target 大类前面；after=true 移到它后面。顺序写进分类表，重启也在。</summary>
        internal bool MoveCatTo(string name, string target, bool after)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(target)) return false;
            // 已经在那个位置上 = 明确告诉用户「没挪」，别让他以为拖动坏了
            List<string> cur = CatPrefs.Cats();
            int ci = cur.IndexOf(name), ti = cur.IndexOf(target);
            if (ci >= 0 && ti >= 0 && ((after && ci == ti + 1) || (!after && ci == ti - 1)))
            {
                lblFoot.Text = "「" + name + "」本来就在「" + target + "」" + (after ? "后面" : "前面")
                    + " —— 位置没变。拖到隔一个以上的大类（比如 枪械 / 杂项）才看得出挪动";
                Core.Log("INFO", "DRAGAPPLY 「" + name + "」本来就在「" + target + "」" + (after ? "后面" : "前面") + "，位置没变");
                return true;
            }
            // 「我的分支」这类不在默认大类表里的，先登记再挪 —— 否则 CatPrefs 里查不到它，移动直接失败
            CatPrefs.EnsureCat(name);
            CatPrefs.EnsureCat(target);
            if (!CatPrefs.MoveCatTo(name, target, after)) return false;
            Rebuild();
            // 把刚拖的那个大类滚回视野内
            body.ScrollToRow(FindCatRow(name));
            lblFoot.Text = "「" + name + "」已移到「" + target + "」" + (after ? "后面" : "前面")
                + " · 拖住大类行还能再换";
            return true;
        }

        /// <summary>拖动小分类排序：第一次拖时把当前顺序落成底稿，再挪 —— 重启后还在。</summary>
        internal bool MoveSubTo(string cat, string sub, string target, bool after)
        {
            if (string.IsNullOrEmpty(cat) || string.IsNullOrEmpty(sub) || string.IsNullOrEmpty(target)) return false;
            List<string> cur = CurrentSubs(cat);
            if (!CatPrefs.HasSubOrder(cat)) CatPrefs.SetSubOrder(cat, cur);
            List<string> now = CatPrefs.SubOrderOf(cat) ?? cur;
            int ci = now.IndexOf(sub), ti = now.IndexOf(target);
            if (ci >= 0 && ti >= 0 && ((after && ci == ti + 1) || (!after && ci == ti - 1)))
            {
                lblFoot.Text = "「" + sub + "」本来就在「" + target + "」" + (after ? "后面" : "前面")
                    + " —— 位置没变。拖到隔一个的小分类上才看得出挪动";
                Core.Log("INFO", "DRAGAPPLY 小分类「" + sub + "」本来就在「" + target + "」" + (after ? "后面" : "前面") + "，位置没变");
                return true;
            }
            if (!CatPrefs.MoveSubTo(cat, sub, target, after)) return false;
            Rebuild();
            body.ScrollToRow(body.RowIndexOfSubOrFirst(cat, sub));
            lblFoot.Text = "「" + sub + "」已挪到「" + target + "」" + (after ? "后面" : "前面")
                + " · 拖住小分类行还能再换";
            return true;
        }

        /// <summary>
        /// 拖动位置：换顺序，或拖进另一个小分类（那一步会把归属一起记下来）。
        /// 两个小分类的顺序都先落成底稿 —— 否则只有被拖的那个有手工顺序，其余还会按自动顺序插进来。
        /// </summary>
        internal bool MovePosTo(string cat, string fromSub, string name, string toSub, string target, bool after)
        {
            if (string.IsNullOrEmpty(cat) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(toSub)) return false;
            bool cross = !string.Equals(fromSub, toSub, StringComparison.Ordinal);
            if (!CatPrefs.HasPosOrder(cat, fromSub)) CatPrefs.SetPosOrder(cat, fromSub, CurrentPositions(cat, fromSub));
            if (cross && !CatPrefs.HasPosOrder(cat, toSub)) CatPrefs.SetPosOrder(cat, toSub, CurrentPositions(cat, toSub));

            if (!cross)
            {
                List<string> now = CatPrefs.PosOrderOf(cat, toSub) ?? new List<string>();
                int ci = now.IndexOf(name), ti = string.IsNullOrEmpty(target) ? -1 : now.IndexOf(target);
                if (ci >= 0 && ti >= 0 && ((after && ci == ti + 1) || (!after && ci == ti - 1)))
                {
                    lblFoot.Text = "「" + name + "」本来就在「" + target + "」" + (after ? "后面" : "前面")
                        + " —— 位置没变。拖到隔一个的位置上才看得出挪动";
                    Core.Log("INFO", "DRAGAPPLY 位置「" + name + "」本来就在「" + target + "」" + (after ? "后面" : "前面") + "，位置没变");
                    return true;
                }
            }
            if (!CatPrefs.MovePosTo(cat, fromSub, name, toSub, target, after)) return false;
            if (cross) CatPrefs.SetSlotSub(cat, name, toSub);      // 归属跟着走，重扫之后也还在这一段里
            Rebuild();
            int at = body.RowIndexOfPos(cat, toSub, name);
            if (at >= 0) body.ScrollToRow(at);
            lblFoot.Text = cross
                ? "「" + name + "」已放进「" + toSub + "」" + (string.IsNullOrEmpty(target) ? (after ? "末尾" : "开头") : "「" + target + "」" + (after ? "后面" : "前面"))
                : "「" + name + "」已挪到「" + target + "」" + (after ? "后面" : "前面") + " · 拖住位置行还能再换";
            return true;
        }

        /// <summary>大类标题行在树里是第几行（没有返回 -1）。</summary>
        private int FindCatRow(string cat)        {
            for (int i = 0; i < body.RowCount; i++)
                if (body.RowIsCat(i) && string.Equals(body.RowCat(i), cat, StringComparison.Ordinal)) return i;
            return -1;
        }

        /// <summary>新建分支，并直接把某个 MOD 放进去（右键 MOD 行 →「放进新分支…」）。</summary>
        internal void PutIntoNewBranch(ModGroup g)
        {
            if (g == null || g.Main == null) return;
            string nm = PromptForm.Ask("新分支名（例：音效 / 整活 / 收藏）。建好后「" + g.Name + "」就在里面。", "放进新分支");
            if (nm == null) return;
            string err = MyBranch.Create(nm);
            if (err != null) { MessageBox.Show(err, "建不了这个分支"); return; }
            string pick = MyBranch.CleanName(nm);
            MyBranch.Put(pick, g.Main.Name);
            Rebuild();
            GoSlot("#mine:" + pick);
            lblFoot.Text = "分支「" + pick + "」建好了，「" + g.Name + "」已经在里面 · 点一行 = 开 / 关";
        }

        internal void RenameBranch(Slot s)
        {
            if (!IsBranchSlot(s)) return;
            string nm = PromptForm.Ask("新名字（原来是「" + s.Name + "」）", "重命名分支", s.Name);
            if (nm == null) return;
            string err = MyBranch.Rename(s.Name, nm);
            if (err != null) { MessageBox.Show(err, "改不了"); return; }
            Rebuild();
            GoSlot("#mine:" + MyBranch.CleanName(nm));
        }

        internal void DeleteBranch(Slot s)
        {
            if (!IsBranchSlot(s)) return;
            if (MessageBox.Show("解散分支「" + s.Name + "」？\r\n\r\n"
                + "· 只删这个分组，vpk 文件一个都不动\r\n"
                + "· 里面的 MOD 回到各自原来的分类（认不出的回「未归类」）\r\n"
                + "· 已经勾着的不会被取消",
                "解散分支", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            MyBranch.Delete(s.Name);
            Rebuild();
            GoSlot("#mine+new");
        }

        /// <summary>把这个 MOD 放进某个分支（还没分支就先起名）。</summary>
        internal void PutIntoBranch(ModGroup g)
        {
            if (g == null || g.Main == null) return;
            string pick;
            List<string> names = MyBranch.Names();
            if (names.Count == 0)
            {
                pick = PromptForm.Ask("还没有分支。先起个名字（例：音效 / 整活 / 收藏）：", "放进我的分支");
                if (pick == null) return;
                string err = MyBranch.Create(pick);
                if (err != null) { MessageBox.Show(err, "建不了这个分支"); return; }
                pick = MyBranch.CleanName(pick);
            }
            else
            {
                BranchPickForm f = new BranchPickForm(names);
                if (f.ShowDialog() != DialogResult.OK) return;
                pick = f.Picked;
                if (pick == null) return;
                if (pick != null && pick.StartsWith("＋", StringComparison.Ordinal))   // 选了「＋ 新建分支…」
                {
                    string nm = PromptForm.Ask("新分支名（例：音效 / 整活 / 收藏）：", "新建分支");
                    if (nm == null) return;
                    string err2 = MyBranch.Create(nm);
                    if (err2 != null) { MessageBox.Show(err2, "建不了这个分支"); return; }
                    pick = MyBranch.CleanName(nm);
                }
            }
            MyBranch.Put(pick, g.Main.Name);
            Rebuild();
            GoSlot("#mine:" + pick);
            lblFoot.Text = "「" + g.Name + "」已放进分支「" + pick + "」· 点一行 = 开 / 关";
        }

        /// <summary>右键菜单用：这个 MOD 是不是在当前打开的分支里。</summary>
        internal bool InOpenBranch(ModGroup g)
        {
            Slot s = Current;
            return g != null && g.Main != null && IsBranchSlot(s)
                && string.Equals(MyBranch.BranchOf(g.Main.Name), s.Name, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>从当前分支里把这个 MOD 移出（不删文件、不改勾选）。</summary>
        internal void TakeOutOfBranch(ModGroup g)
        {
            Slot s = Current;
            if (g == null || g.Main == null || !IsBranchSlot(s)) return;
            MyBranch.Take(g.Main.Name);
            Rebuild();
            GoSlot(s.Key);
            lblFoot.Text = "已从分支「" + s.Name + "」移出（文件和勾选都没动）";
        }

        /// <summary>树的右键菜单。点在空白处也能出（r 为 null）—— 建分支从此不用先找到「我的分支」那几行。</summary>
        internal void TreeMenuFor(TreeRow r, Point at)
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Font = Theme.UI;
            Slot s = r == null ? null : r.Slot;
            if (IsBranchSlot(s))
            {
                Slot ss = s;
                m.Items.Add("在这个分支下新建子分支…", null, delegate { NewBranch(ss); });
                m.Items.Add("新建同级分支…", null, delegate { NewBranchSibling(ss); });
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add("重命名分支…", null, delegate { RenameBranch(ss); });
                if (MyBranch.HasParent(ss.Name))
                    m.Items.Add("移到顶层", null, delegate
                    {
                        MoveBranch(ss.Name, "", "");
                    });
                m.Items.Add("解散分支", null, delegate { DeleteBranch(ss); });
                m.Items.Add(new ToolStripSeparator());
            }
            else
            {
                m.Items.Add("新建分支…", null, delegate { NewBranch(); });
                // 已有分支时再给一层：不用先滚到那个分支行，右键哪儿都能挑一个分支往它下面建
                if (MyBranch.Names().Count > 0)
                {
                    ToolStripMenuItem under = new ToolStripMenuItem("在已有分支下新建子分支…");
                    foreach (string bn in MyBranch.Names())
                    {
                        string target = bn;
                        under.DropDownItems.Add(MyBranch.HasParent(target)
                            ? "在「" + Shorten(target, 16) + "」下（现挂在「" + Shorten(MyBranch.ParentOf(target), 12) + "」里）"
                            : "在「" + Shorten(target, 20) + "」下",
                            null, delegate { NewBranchUnder(target); });
                    }
                    m.Items.Add(under);
                }
                // 位置行上再给一条捷径：不用先点开位置、再去列表里翻那个 MOD
                if (s != null && s.Items.Count > 0)
                {
                    ModGroup pick = ChosenOf(s);
                    if (pick == null && s.Items.Count == 1) pick = s.Items[0];
                    if (pick != null)
                    {
                        ModGroup pg = pick;
                        m.Items.Add("新建分支，并把「" + Shorten(pg.Name, 18) + "」放进去…",
                            null, delegate { PutIntoNewBranch(pg); });
                    }
                }
                m.Items.Add(new ToolStripSeparator());
            }
            // 树有 70 多个位置，一屏看不完：先收成一页目录，找哪层再点开
            m.Items.Add("全部收起", null, delegate { FoldEverything(); });
            m.Items.Add("全部展开", null, delegate { UnfoldEverything(); });
            m.Items.Add(new ToolStripSeparator());

            // 库本身的活儿（导入、扫描范围、识别、去重）原来只在「分类页」的右键菜单里，
            // 分类页撤掉后搬到这儿，不然这些功能就没入口了。
            ToolStripMenuItem lib = new ToolStripMenuItem("MOD 库…");
            lib.DropDownItems.Add("从文件夹导入…", null, delegate { mf.BulkImport(); });
            lib.DropDownItems.Add("库文件夹管理…", null, delegate { mf.ManageModDirs(); });
            lib.DropDownItems.Add("识别压缩包内容", null, delegate { mf.ProbeArchivesUi(); });
            lib.DropDownItems.Add("清理同名重复", null, delegate { mf.CleanDupMods(); });
            lib.DropDownItems.Add(new ToolStripSeparator());
            lib.DropDownItems.Add("重新扫描 MOD 库", null, delegate { mf.RescanMods(); });
            m.Items.Add(lib);

            m.Show(at);
        }

        /// <summary>给树里的小动作（拖动取消之类）留一句脚注，不用专门弹窗。</summary>
        internal void KeepFoot(string s)
        {
            if (lblFoot != null && s != null) lblFoot.Text = s;
        }

        internal int Unmatched { get { return unmatched; } }

        /// <summary>自测用：未归类页列表当前列了几行（先 SelectUnmatched 再读）。</summary>
        internal int UnmatchedRows
        {
            get
            {
                int n = 0;
                foreach (ListViewItem li in lv.Items)
                    if (li.Tag is ModGroup) n++;      // 「同类包」伙头行不算
                return n;
            }
        }
        /// <summary>自测用：「指定位置…」手动入口是否可见。</summary>
        internal bool UnmatchedButtonVisible { get { return btnCat != null && btnCat.Visible; } }

        internal List<ModGroup> UnmatchedGroups { get { return unmatchedGroups; } }

        // ---------------- 同类圈伙（AI 聚类的落地用法） ----------------
        // 不给聚类起名、不拿它认槽位（实验证明那只有 75% 准、答不出"替换目标"）。
        // 它干一件查表干不了的事：把"内容确实像"的包（库一份+游戏一份的重复、
        // 同款不同名的变体）在未归类页圈成一伙，提示"挑一个装就行"。
        // 相似度 = 包内路径 token 集合的 Jaccard 系数，阈值取自聚类实验（0.6 时同伙纯度 93%）。
        private const double ClusterThreshold = 0.6;

        private static HashSet<string> ClusterTokens(ModItem m)
        {
            HashSet<string> t = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (m == null || string.IsNullOrEmpty(m.AssetPaths)) return t;
            foreach (string p in m.AssetPaths.Split(';'))
            {
                if (p.Length == 0) continue;
                string[] parts = p.Split('/');
                for (int i = 0; i < parts.Length; i++)
                {
                    string seg = parts[i];
                    if (seg.Length == 0) continue;
                    int dot = seg.LastIndexOf('.');
                    if (dot > 0)
                    {
                        t.Add("ext:" + seg.Substring(dot + 1));
                        string stem = seg.Substring(0, dot);
                        if (stem.StartsWith("v_", StringComparison.OrdinalIgnoreCase)
                            || stem.StartsWith("w_", StringComparison.OrdinalIgnoreCase))
                            stem = stem.Substring(2);
                        t.Add("name:" + stem);
                    }
                    else t.Add("dir:" + seg);
                }
                t.Add("top:" + parts[0]);
            }
            return t;
        }

        private static double Jaccard(HashSet<string> a, HashSet<string> b)
        {
            if (a.Count == 0 || b.Count == 0) return 0;
            int inter = 0;
            foreach (string s in a) if (b.Contains(s)) inter++;
            return (double)inter / (a.Count + b.Count - inter);
        }

        /// <summary>把未归类的组按内容相似度圈伙：返回「伙 -> 成员」，只含有 2 个及以上成员的伙。</summary>
        internal static Dictionary<ModGroup, List<ModGroup>> ClusterUnmatched(List<ModGroup> groups, out int clustered)
        {
            clustered = 0;
            Dictionary<ModGroup, List<ModGroup>> result = new Dictionary<ModGroup, List<ModGroup>>();
            int n = groups.Count;
            if (n < 2) return result;
            List<HashSet<string>> tok = new List<HashSet<string>>(n);
            for (int i = 0; i < n; i++) tok.Add(ClusterTokens(groups[i].Main));
            int[] parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            System.Func<int, int> find = null;
            find = delegate(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; };
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                    if (Jaccard(tok[i], tok[j]) >= ClusterThreshold)
                    { int pi = find(i), pj = find(j); if (pi != pj) parent[pi] = pj; }
            Dictionary<int, List<ModGroup>> byRoot = new Dictionary<int, List<ModGroup>>();
            for (int i = 0; i < n; i++)
            {
                int r = find(i);
                if (!byRoot.ContainsKey(r)) byRoot[r] = new List<ModGroup>();
                byRoot[r].Add(groups[i]);
            }
            foreach (List<ModGroup> members in byRoot.Values)
            {
                if (members.Count < 2) continue;
                clustered += members.Count;
                foreach (ModGroup m in members) result[m] = members;
            }
            return result;
        }
        internal int RowCount { get { return body == null ? 0 : body.RowCount; } }
        internal int SlotCount { get { return slots.Count; } }
        internal int SlotCheckedCount
        {
            get { int n = 0; foreach (Slot s in slots) if (HasChecked(s)) n++; return n; }
        }

        /// <summary>外部勾选变了：只重画，不重建行。</summary>
        public void Sync()
        {
            body.Invalidate();
            FillDetail();
        }

        /// <summary>
        /// 重扫过后 Catalog.Groups 是新建的一批对象，槽位里还攥着旧的 —— 那样状态列会一直显示旧值。
        /// 这里探一下引用是否还新鲜：换了就整棵重建，没换就只重画（搜索打字不该重排列表）。
        /// </summary>
        public void SyncIfSame()
        {
            if (slots.Count == 0) { Rebuild(); return; }
            ModGroup probe = null;
            foreach (Slot s in slots)
            {
                if (s.Items.Count > 0) { probe = s.Items[0]; break; }
                if (s.Extras.Count > 0) { probe = s.Extras[0]; break; }
            }
            if (probe != null && Catalog.Groups.IndexOf(probe) < 0) { Rebuild(); return; }
            Sync();
        }

        public void SelectUnmatched()
        {
            if (selKey == "#unmatched") return;
            selKey = "#unmatched";
            body.Invalidate();
            FillDetail();
        }

        public void SelectSlot(string key)
        {
            SelectSlot(key, false);
        }

        /// <summary>选中一个位置。reveal=true 时顺带把它滚进视野（搜索跳转 / 诊断用 --uiselect 走这条）。</summary>
        public void SelectSlot(string key, bool reveal)
        {
            if (key == null) return;
            if (key == "#mine+new") { NewBranch(); return; }   // 点「＋ 新建分支…」直接弹输入框
            if (key == selKey)
            {
                if (reveal) body.ScrollToSlot(key);
                return;
            }
            RevealFold(key);                     // 折叠着的小分类先展开，选中的行才看得见
            selKey = key;
            body.Invalidate();
            FillDetail();
            if (reveal) body.ScrollToSlot(key);
        }

        /// <summary>自测用：把第 n 个槽位选出来。</summary>
        public Slot SlotAt(int n)
        {
            return (n >= 0 && n < slots.Count) ? slots[n] : null;
        }

        /// <summary>自测用：按显示名找槽位。</summary>
        public Slot SlotByName(string name)
        {
            foreach (Slot s in slots) if (s.Name == name) return s;
            return null;
        }

        /// <summary>
        /// 自测：只走键盘路径 —— ↑↓ 移动焦点、Home/End/PageUp/PageDown 跳转、
        /// Enter 与空格选中槽位、←→ 折叠展开大类。返回不通过的条数。
        /// 之所以要单独跑一遍：鼠标路径一直有测，键盘路径以前只有代码没有证据。
        /// </summary>
        internal int SelfTestKeys()
        {
            int bad = 0;
            if (slots.Count == 0 || body.RowCount == 0)
            {
                Core.Log("WARN", "KEY 库里没有槽位，跳过键盘自测");
                return 0;
            }
            string orig = selKey;
            Core.Log("INFO", "KEY 开始：行数 " + body.RowCount + "，槽位 " + slots.Count + " 个");

            // 0) 空位：库里没有 MOD 的标准位置也要占一行（「每种物品都显示」）
            int empty = SlotIndex.EmptyCount(slots), dirty = 0;
            foreach (Slot s in slots) if (s.Empty && (s.Items.Count > 0 || s.Extras.Count > 0)) dirty++;
            int missing = SlotIndex.MissingStandard();
            Core.Log(empty > 0 && missing == 0 && dirty == 0 ? "INFO" : (dirty == 0 && missing == 0 ? "WARN" : "FAIL"),
                "KEY 空位置 " + empty + " 个，标准位置漏掉的 " + missing + " 个，空位里混进候选的 " + dirty + " 个");
            if (missing > 0) bad++;
            if (dirty > 0) bad++;

            // 0b) 识别表对账：表里认得出来的每一种东西，树上都得有位置。
            //     不盯这条的话，往识别表加个新型号、忘了加位置，那个包装上以后只会掉进「未归类」。
            string uncovered = SlotIndex.UncoveredNames();
            Core.Log(uncovered.Length == 0 ? "INFO" : "FAIL",
                "KEY 识别表覆盖：认出 " + SlotIndex.SubjectCount + " 种，位置表 " + SlotIndex.PositionCount
                + " 个位置，没着落的 " + (uncovered.Length == 0 ? "0 个" : uncovered));
            if (uncovered.Length > 0) bad++;

            // 0c) 位置表反推：每个标准位置按分类规则重新分一次，得回到原来那一层。
            //     不然真出现那种包时会另起一行，看着像两个不同的东西。
            string misfiled = SlotIndex.MisfiledNames();
            Core.Log(misfiled.Length == 0 ? "INFO" : "FAIL",
                "KEY 位置归类：" + (misfiled.Length == 0 ? "全部对得上" : misfiled));
            if (misfiled.Length > 0) bad++;
            Slot firstFilled = null;
            foreach (Slot s in slots) if (!s.Empty) { firstFilled = s; break; }
            Core.Log(firstFilled != null && selKey == firstFilled.Key ? "INFO" : "WARN",
                "KEY 默认选中：" + (firstFilled == null ? "（库里没有 MOD）" : firstFilled.Name));

            // 1) 首行 / 上下移动
            body.TestKey(Keys.Home);
            if (body.FocusIndex != 0) { bad++; Core.Log("FAIL", "KEY Home 之后焦点在第 " + body.FocusIndex + " 行，应该是第 0 行"); }
            else Core.Log("INFO", "KEY Home → 第 0 行");

            if (body.RowCount > 2)
            {
                body.TestKey(Keys.Down);
                if (body.FocusIndex != 1) { bad++; Core.Log("FAIL", "KEY ↓ 之后焦点在第 " + body.FocusIndex + " 行，应该是第 1 行"); }
                else Core.Log("INFO", "KEY ↓ → 第 1 行");

                body.TestKey(Keys.Up);
                if (body.FocusIndex != 0) { bad++; Core.Log("FAIL", "KEY ↑ 没有回到第 0 行（现在第 " + body.FocusIndex + " 行）"); }
                else Core.Log("INFO", "KEY ↑ → 第 0 行");

                // 2) End / PageUp / PageDown（越界要夹住，不能跑到行外面）
                int last = body.RowCount - 1;
                body.TestKey(Keys.End);
                if (body.FocusIndex != last) { bad++; Core.Log("FAIL", "KEY End 焦点在第 " + body.FocusIndex + " 行，应该是最后一行 " + last); }
                else Core.Log("INFO", "KEY End → 最后一行（" + last + "）");

                body.TestKey(Keys.Down);
                if (body.FocusIndex != last) { bad++; Core.Log("FAIL", "KEY 最后一行再按 ↓ 跑出了范围（第 " + body.FocusIndex + " 行）"); }
                else Core.Log("INFO", "KEY 最后一行再按 ↓ 仍停在最后一行");

                body.TestKey(Keys.PageUp);
                int pu = body.FocusIndex;
                if (pu >= last) { bad++; Core.Log("FAIL", "KEY PageUp 没有往上走（仍在第 " + pu + " 行）"); }
                else Core.Log("INFO", "KEY PageUp → 第 " + pu + " 行");

                body.TestKey(Keys.PageDown);
                if (body.FocusIndex <= pu) { bad++; Core.Log("FAIL", "KEY PageDown 没有往下走（仍在第 " + body.FocusIndex + " 行）"); }
                else Core.Log("INFO", "KEY PageDown → 第 " + body.FocusIndex + " 行");
            }

            // 3) ←→ 折叠展开：第 0 行是大类
            body.TestKey(Keys.Home);
            if (body.FocusedRowIsCat)
            {
                int n0 = body.RowCount;
                body.TestKey(Keys.Left);
                int n1 = body.RowCount;
                if (n1 >= n0) { bad++; Core.Log("FAIL", "KEY ← 没折叠第 0 行的大类（行数 " + n0 + " → " + n1 + "）"); }
                else Core.Log("INFO", "KEY ← 折叠大类：行数 " + n0 + " → " + n1);

                body.TestKey(Keys.Right);
                int n2 = body.RowCount;
                if (n2 != n0) { bad++; Core.Log("FAIL", "KEY → 展开后行数是 " + n2 + "，应该回到 " + n0); }
                else Core.Log("INFO", "KEY → 展开回来：行数 " + n2);
            }
            else Core.Log("WARN", "KEY 第 0 行不是大类，跳过折叠测试（树结构可能变了）");

            // 3b) 小分类层也要能收：同一条代码路径，但走的是 SetSubFolded
            body.TestKey(Keys.Home);
            bool sawSub = false;
            for (int i = 0; i < body.RowCount; i++)
            {
                if (body.FocusedRowIsSub) { sawSub = true; break; }
                body.TestKey(Keys.Down);
            }
            if (!sawSub) Core.Log("WARN", "KEY 树里没有小分类行，跳过小分类折叠测试");
            else
            {
                int s0 = body.RowCount;
                body.TestKey(Keys.Left);
                int s1 = body.RowCount;
                if (s1 >= s0) { bad++; Core.Log("FAIL", "KEY ← 没折叠小分类（行数 " + s0 + " → " + s1 + "）"); }
                else Core.Log("INFO", "KEY ← 折叠小分类：行数 " + s0 + " → " + s1);
                Core.Log("INFO", "KEY 折叠后焦点在第 " + body.FocusIndex + " 行" + (body.FocusedRowIsSub ? "（仍是小分类行）" : "（不是小分类行了）"));

                body.TestKey(Keys.Right);
                int s2 = body.RowCount;
                if (s2 != s0) { bad++; Core.Log("FAIL", "KEY → 小分类展开后行数 " + s2 + "，应该回到 " + s0); }
                else Core.Log("INFO", "KEY → 小分类展开回来：行数 " + s2);
            }

            // 4) Enter 选中：一路 ↓ 找第一个槽位行
            body.TestKey(Keys.Home);
            string k1 = "";
            for (int i = 0; i < body.RowCount; i++)
            {
                if (body.FocusedRowIsSlot) { k1 = body.FocusedSlotKey; break; }
                body.TestKey(Keys.Down);
            }
            if (body.FocusedRowIsSlot && k1.Length == 0) k1 = body.FocusedSlotKey;
            if (k1.Length == 0)
            {
                bad++;
                Core.Log("FAIL", "KEY 一路按 ↓ 都没落到槽位行上");
            }
            else
            {
                body.TestKey(Keys.Enter);
                if (selKey != k1) { bad++; Core.Log("FAIL", "KEY Enter 之后选中「" + selKey + "」，应该是「" + k1 + "」"); }
                else Core.Log("INFO", "KEY Enter → 选中槽位「" + k1 + "」");
            }

            // 5) 空格选中另一件：两条键都得能选，不能只有 Enter 好使
            if (k1.Length > 0)
            {
                string k2 = "";
                for (int i = 0; i < body.RowCount; i++)
                {
                    body.TestKey(Keys.Down);
                    if (body.FocusedRowIsSlot && body.FocusedSlotKey != k1) { k2 = body.FocusedSlotKey; break; }
                }
                if (k2.Length == 0) Core.Log("WARN", "KEY 后面没有第二个槽位，跳过空格键测试");
                else
                {
                    body.TestKey(Keys.Space);
                    if (selKey != k2) { bad++; Core.Log("FAIL", "KEY 空格之后选中「" + selKey + "」，应该是「" + k2 + "」"); }
                    else Core.Log("INFO", "KEY 空格 → 选中槽位「" + k2 + "」");
                }
            }

            // 6) 收尾：把选中恢复成测试前的位置，别改动用户看到的界面状态
            if (orig.Length > 0 && orig != "#unmatched" && selKey != orig) SelectSlot(orig);
            // 7) 我的分支的父子关系（自测会建两个临时分支，结束时删掉）
            bad += BranchSelfTest();
            Core.Log("INFO", "KEY 结束：不通过 " + bad + " 条");
            return bad;
        }

        /// <summary>
        /// 自检：分支能不能挂在分支下面、树里顺序对不对、父分支解散后子分支会不会提上来。
        /// 会临时建两个名字带「自检」的分支，测完删掉，用户的真分支不动。
        /// </summary>
        internal int BranchSelfTest()
        {
            int bad = 0;
            string pa = "自检父" + Environment.TickCount.ToString();
            string ch = "自检子" + Environment.TickCount.ToString();
            string s1 = "自检甲" + Environment.TickCount.ToString();
            string s2 = "自检乙" + Environment.TickCount.ToString();
            List<string> catBackup = CatPrefs.Cats();          // 大类拖动自检会改顺序，测完按原名次摆回去
            List<string> foldBackup = new List<string>(folded);   // 自检要展开「我的分支」，测完按原折叠状态恢复
            SetFolded("我的分支", false);
            try
            {
                string err = MyBranch.Create(pa);
                if (err != null) { Core.Log("FAIL", "BRANCH 建父分支失败：" + err); bad++; return bad; }
                err = MyBranch.Create(ch, pa);
                if (err != null) { Core.Log("FAIL", "BRANCH 在父分支下建子分支失败：" + err); bad++; return bad; }

                if (MyBranch.ParentOf(ch) != pa) { bad++; Core.Log("FAIL", "BRANCH 子分支的父不是「" + pa + "」，是「" + MyBranch.ParentOf(ch) + "」"); }
                else Core.Log("INFO", "BRANCH 子分支「" + ch + "」挂在「" + pa + "」下面，层级 " + MyBranch.Depth(ch));

                List<string> ns = MyBranch.Names();
                int ia = ns.IndexOf(pa), ib = ns.IndexOf(ch);
                if (ia < 0 || ib < 0 || ib != ia + 1) { bad++; Core.Log("FAIL", "BRANCH 树顺序不对：父在第 " + ia + "，子在第 " + ib + "（应该紧跟在后面）"); }
                else Core.Log("INFO", "BRANCH 树顺序：父第 " + ia + "、子第 " + ib + "（紧挨着）");

                // 换成树里的槽位：子分支的行号必须紧跟父分支
                Rebuild();
                int si = -1, sj = -1;
                for (int i = 0; i < slots.Count; i++)
                {
                    if (slots[i].Key == "#mine:" + pa) si = i;
                    if (slots[i].Key == "#mine:" + ch) sj = i;
                }
                if (si < 0 || sj < 0 || sj <= si) { bad++; Core.Log("FAIL", "BRANCH 槽位顺序不对：父 " + si + "、子 " + sj); }
                else Core.Log("INFO", "BRANCH 槽位顺序：父 " + si + "、子 " + sj);

                // 右侧列表「在当前分支下新建子分支」的入口：打开分支页后要能拿到当前分支
                GoSlot("#mine:" + ch);
                Slot curB = CurrentBranch;
                if (curB == null || !string.Equals(curB.Name, ch, StringComparison.Ordinal))
                { bad++; Core.Log("FAIL", "BRANCH 打开「" + ch + "」后 CurrentBranch 取不到它（" + (curB == null ? "null" : curB.Name) + "）"); }
                else Core.Log("INFO", "BRANCH 打开「" + ch + "」后 CurrentBranch 正确，右键菜单能建它的子分支");
                GoSlot("#mine:" + pa);

                // 父分支解散 → 子分支提回顶层，不能跟着消失
                MyBranch.Delete(pa);
                if (!MyBranch.Has(ch)) { bad++; Core.Log("FAIL", "BRANCH 父分支解散后子分支「" + ch + "」也没了"); }
                else if (MyBranch.HasParent(ch)) { bad++; Core.Log("FAIL", "BRANCH 父分支解散后子分支仍挂在「" + MyBranch.ParentOf(ch) + "」上"); }
                else Core.Log("INFO", "BRANCH 父分支解散后子分支提回顶层");

                // 拖动管理：换顺序 / 改成子分支 / 拒绝成环 —— 和鼠标松手走同一条 ApplyDrop
                string e1 = MyBranch.Create(s1), e2 = MyBranch.Create(s2);
                if (e1 != null || e2 != null)
                {
                    bad++;
                    Core.Log("FAIL", "BRANCH 拖动自检：临时分支没建起来（" + (e1 ?? e2) + "）");
                }
                else
                {
                    Rebuild();
                    int r1 = body.RowIndexOfSlot("#mine:" + s1);
                    if (r1 < 0) { bad++; Core.Log("FAIL", "BRANCH 拖动自检：树里找不到「" + s1 + "」那一行"); }
                    else if (!body.ApplyDrop(s2, r1, 1)) { bad++; Core.Log("FAIL", "BRANCH 拖动自检：拖到「" + s1 + "」前面没执行"); }
                    else
                    {
                        List<string> nn = MyBranch.Names();
                        int a = nn.IndexOf(s1), b = nn.IndexOf(s2);
                        if (a < 0 || b < 0 || b > a) { bad++; Core.Log("FAIL", "BRANCH 拖动排序没生效：甲第 " + a + "、乙第 " + b); }
                        else Core.Log("INFO", "BRANCH 拖动排序：乙第 " + b + "、甲第 " + a + "（乙挪到前面了）");
                    }

                    Rebuild();
                    int ra = body.RowIndexOfSlot("#mine:" + s1);
                    if (ra < 0 || !body.ApplyDrop(s2, ra, 3)) { bad++; Core.Log("FAIL", "BRANCH 拖动自检：拖成子分支没执行"); }
                    else if (MyBranch.ParentOf(s2) != s1)
                    {
                        bad++;
                        Core.Log("FAIL", "BRANCH 拖成子分支失败：乙的父是「" + MyBranch.ParentOf(s2) + "」");
                    }
                    else Core.Log("INFO", "BRANCH 拖成子分支：乙挂在甲下面，层级 " + MyBranch.Depth(s2));

                    Rebuild();
                    int rb = body.RowIndexOfSlot("#mine:" + s2);
                    if (rb >= 0)
                    {
                        body.ApplyDrop(s1, rb, 3);      // 乙是甲的子孙，这一步必须被拒绝
                        if (MyBranch.ParentOf(s1).Length > 0)
                        {
                            bad++;
                            Core.Log("FAIL", "BRANCH 拖到自己的子分支下面居然成了（父=" + MyBranch.ParentOf(s1) + "）");
                        }
                        else Core.Log("INFO", "BRANCH 拖到自己的子分支下面被拒绝，甲还在顶层");
                    }

                    // 子分支拖回顶层：走真实鼠标路径（按下乙 → 挪到甲行上半 → 松手），
                    // 乙必须脱出甲回到顶层 —— 用户反馈「大分支下的小分支不能拖」，这条必须有
                    Rebuild();
                    int rc = body.RowIndexOfSlot("#mine:" + s2);
                    int rd = body.RowIndexOfSlot("#mine:" + s1);
                    if (rc < 0 || rd < 0) { bad++; Core.Log("FAIL", "BRANCH 树里找不到子分支行（乙=" + rc + " 甲=" + rd + "）"); }
                    else
                    {
                        string stD;
                        body.SimulateRealDrag(rc, rd, 1, out stD);
                        Core.Log("INFO", "BRANCH 子分支拖出路径 " + stD);
                        if (!stD.Contains("pressArm=True")) { bad++; Core.Log("FAIL", "BRANCH 子分支行按下去没有拖动资格（pressArm=False）"); }
                        else if (MyBranch.ParentOf(s2).Length > 0)
                        { bad++; Core.Log("FAIL", "BRANCH 子分支拖回顶层没生效，父还是「" + MyBranch.ParentOf(s2) + "」"); }
                        else Core.Log("INFO", "BRANCH 子分支拖回顶层生效（真实鼠标路径）");
                    }

                    // 大类拖动：挪到另一个大类后面，走和鼠标松手一样的 ApplyDrop，
                    // 顺序必须真的变、且写进分类表（重启还在）
                    List<string> before = CatPrefs.Cats();
                    if (before.Count >= 2)
                    {
                        string a = before[0], b = before[1];
                        Rebuild();
                        int tb = FindCatRow(b);
                        if (tb < 0) { bad++; Core.Log("FAIL", "CATDRAG 树里找不到大类「" + b + "」那一行"); }
                        else if (!body.ApplyDrop(a, 2, tb, 2)) { bad++; Core.Log("FAIL", "CATDRAG 拖到「" + b + "」后面没执行"); }
                        else
                        {
                            List<string> after = CatPrefs.Cats();
                            int ca = after.IndexOf(a), cb = after.IndexOf(b);
                            if (ca != cb + 1) { bad++; Core.Log("FAIL", "CATDRAG 顺序没生效：" + a + " 第 " + ca + "、" + b + " 第 " + cb); }
                            else Core.Log("INFO", "CATDRAG 「" + a + "」挪到「" + b + "」后面，分类表顺序已更新");
                            // 拖到自己身上必须被拒绝
                            Rebuild();
                            int ta = FindCatRow(a);
                            if (ta >= 0 && body.ApplyDrop(a, 2, ta, 1))
                            {
                                bad++;
                                Core.Log("FAIL", "CATDRAG 大类拖到自己身上居然成功了");
                            }
                        }
                    }

                    // ---- 位置 / 小分类也能拖（kind 3 / 4）：用户要「全树任意行都能拖」------------
                    // 这几条会动 方案树分类.ini，所以先把原文整个拷走，测完一字不差写回去
                    string prefFile = CatPrefs.FileOf(), prefRaw = null;
                    try { if (File.Exists(prefFile)) prefRaw = Convert.ToBase64String(File.ReadAllBytes(prefFile)); } catch { }
                    try
                    {
                        Rebuild();
                        int p1 = -1, p2 = -1;
                        for (int i = 0; i + 1 < body.RowCount; i++)
                        {
                            Slot x1 = body.RowSlot(i), x2 = body.RowSlot(i + 1);
                            if (x1 == null || x2 == null) continue;
                            if (CatTree.IsBranchSlot(x1) || x1.Pile) continue;
                            if (x1.Cat != x2.Cat || x1.Sub != x2.Sub) continue;
                            p1 = i; p2 = i + 1; break;
                        }
                        if (p1 < 0) Core.Log("WARN", "ORDER 没有同一小分类下相邻的两个位置，跳过位置换序用例");
                        else
                        {
                            string c = body.RowSlot(p1).Cat, sb = body.RowSlot(p1).Sub;
                            string nmA = body.RowSlot(p1).Name, nmB = body.RowSlot(p2).Name;
                            if (!body.ApplyDrop(nmA, 4, p2, 2, c, sb)) { bad++; Core.Log("FAIL", "ORDER 位置「" + nmA + "」拖到「" + nmB + "」后面没执行"); }
                            else
                            {
                                int q1 = body.RowIndexOfPos(c, sb, nmA), q2 = body.RowIndexOfPos(c, sb, nmB);
                                if (q1 < 0 || q2 < 0 || q1 <= q2) { bad++; Core.Log("FAIL", "ORDER 位置换序没生效：" + nmA + "@" + q1 + "、" + nmB + "@" + q2); }
                                else Core.Log("INFO", "ORDER 位置「" + nmA + "」拖到「" + nmB + "」后面生效（" + q2 + " → " + q1 + "）");
                            }

                            // 同一件事再走一遍真实鼠标路径：按下必须有拖资格、起拖必须是 kind=4、落点必须有模式
                            Rebuild();
                            int ra1 = -1, ra2 = -1;
                            for (int i = 0; i + 1 < body.RowCount; i++)
                            {
                                Slot x1 = body.RowSlot(i), x2 = body.RowSlot(i + 1);
                                if (x1 == null || x2 == null) continue;
                                if (CatTree.IsBranchSlot(x1) || x1.Pile) continue;
                                if (x1.Cat != x2.Cat || x1.Sub != x2.Sub) continue;
                                ra1 = i; ra2 = i + 1; break;
                            }
                            if (ra1 < 0) Core.Log("WARN", "ORDER 没有相邻位置行，跳过鼠标路径用例");
                            else
                            {
                                string stP;
                                body.SimulateRealDrag(ra1, ra2, 2, out stP);
                                Core.Log("INFO", "ORDER 位置鼠标路径 " + stP);
                                if (!stP.Contains("pressArm=True")) { bad++; Core.Log("FAIL", "ORDER 位置行按下去没有拖动资格：" + stP); }
                                else if (!stP.Contains("kind=4")) { bad++; Core.Log("FAIL", "ORDER 位置行起拖的 kind 不是 4：" + stP); }
                                else if (!stP.Contains("dropMode=2")) { bad++; Core.Log("FAIL", "ORDER 位置行落点没解析出来：" + stP); }
                                else if (!stP.Contains("pv=" + body.RowCount + "/" + body.RowCount))
                                { bad++; Core.Log("FAIL", "ORDER 位置行拖动预览没画出来（行数应与原树一致）：" + stP); }
                                else Core.Log("INFO", "ORDER 位置行真实鼠标路径全程走通（含拖动预览）");
                            }
                        }

                        // 跨小分类：把一个位置拖进另一个小分类，归属必须跟着改
                        Rebuild();
                        string catX = null, subFrom = null, subTo = null;
                        int rowFrom = -1, rowTo = -1;
                        for (int i = 0; i < body.RowCount; i++)
                        {
                            Slot s = body.RowSlot(i);
                            if (s == null || CatTree.IsBranchSlot(s) || s.Pile) continue;
                            if (catX == null) { catX = s.Cat; subFrom = s.Sub; rowFrom = i; continue; }
                            if (s.Cat == catX && s.Sub != subFrom) { subTo = s.Sub; rowTo = i; break; }
                        }
                        if (catX == null || subTo == null) Core.Log("WARN", "ORDER 没有跨小分类的样本，跳过 kind=4 跨类用例");
                        else
                        {
                            string nmM = body.RowSlot(rowFrom).Name;
                            if (!body.ApplyDrop(nmM, 4, rowTo, 2, catX, subFrom)) { bad++; Core.Log("FAIL", "ORDER 位置「" + nmM + "」拖进「" + subTo + "」没执行"); }
                            else if (body.RowIndexOfPos(catX, subTo, nmM) < 0) { bad++; Core.Log("FAIL", "ORDER 位置「" + nmM + "」没进「" + subTo + "」这一段"); }
                            else Core.Log("INFO", "ORDER 位置「" + nmM + "」跨小分类拖进「" + subTo + "」生效");
                        }

                        // 小分类换顺序：把最后一个拖到第一个前面
                        Rebuild();
                        if (catX == null) Core.Log("WARN", "ORDER 没有大类样本，跳过 kind=3 用例");
                        else
                        {
                            List<string> subsNow = CurrentSubs(catX);
                            if (subsNow.Count < 2) Core.Log("WARN", "ORDER 「" + catX + "」只有一个小分类，跳过 kind=3 用例");
                            else
                            {
                                string sA = subsNow[subsNow.Count - 1], sB = subsNow[0];
                                int rA = body.RowIndexOfSub(catX, sA), rB = body.RowIndexOfSub(catX, sB);
                                if (rA < 0 || rB < 0) Core.Log("WARN", "ORDER 单分组大类不画小标题行，跳过 kind=3 用例");
                                else
                                {
                                    if (!body.ApplyDrop(sA, 3, rB, 1, catX, "")) { bad++; Core.Log("FAIL", "ORDER 小分类「" + sA + "」拖到「" + sB + "」前面没执行"); }
                                    else
                                    {
                                        List<string> after2 = CatPrefs.SubOrderOf(catX);
                                        int xa = after2 == null ? -1 : after2.IndexOf(sA);
                                        int xb = after2 == null ? -1 : after2.IndexOf(sB);
                                        if (xa < 0 || xb < 0 || xa > xb) { bad++; Core.Log("FAIL", "ORDER 小分类顺序没生效：" + sA + "@" + xa + "、" + sB + "@" + xb); }
                                        else Core.Log("INFO", "ORDER 小分类「" + sA + "」拖到「" + sB + "」前面生效");
                                    }
                                    // 跨大类必须被拒绝：小分类不属于别的大类
                                    int rOther = -1;
                                    for (int i = 0; i < body.RowCount; i++)
                                    {
                                        Slot s = body.RowSlot(i);
                                        if (s != null && s.Cat != catX && !CatTree.IsBranchSlot(s)) { rOther = i; break; }
                                    }
                                    if (rOther >= 0 && body.ApplyDrop(sA, 3, rOther, 1, catX, ""))
                                    {
                                        bad++;
                                        Core.Log("FAIL", "ORDER 小分类居然被拖进了别的大类");
                                    }

                                    // 小分类行也走一遍真实鼠标路径
                                    Rebuild();
                                    List<string> subsM = CurrentSubs(catX);
                                    int m1 = subsM.Count >= 2 ? body.RowIndexOfSub(catX, subsM[0]) : -1;
                                    int m2 = subsM.Count >= 2 ? body.RowIndexOfSub(catX, subsM[subsM.Count - 1]) : -1;
                                    if (m1 < 0 || m2 < 0) Core.Log("WARN", "ORDER 找不到两个小分类标题行，跳过小分类鼠标路径用例");
                                    else
                                    {
                                        string stQ;
                                        body.SimulateRealDrag(m2, m1, 1, out stQ);
                                        Core.Log("INFO", "ORDER 小分类鼠标路径 " + stQ);
                                        if (!stQ.Contains("pressArm=True")) { bad++; Core.Log("FAIL", "ORDER 小分类行按下去没有拖动资格：" + stQ); }
                                        else if (!stQ.Contains("kind=3")) { bad++; Core.Log("FAIL", "ORDER 小分类行起拖的 kind 不是 3：" + stQ); }
                                        else if (!stQ.Contains("dropMode=1")) { bad++; Core.Log("FAIL", "ORDER 小分类行落点没解析出来：" + stQ); }
                                        else if (!stQ.Contains("pv=" + body.RowCount + "/" + body.RowCount))
                                        { bad++; Core.Log("FAIL", "ORDER 小分类拖动预览没画出来：" + stQ); }
                                        else Core.Log("INFO", "ORDER 小分类行真实鼠标路径全程走通（含拖动预览）");
                                    }

                                    // 「我的分支」不在默认大类表里，拖它要先登记 —— 这条保证它也能拖到别的大类前面
                                    Rebuild();
                                    int rMine = FindCatRow("我的分支");
                                    List<string> catsNow = CatPrefs.Cats();
                                    int rFirst = catsNow.Count > 0 ? FindCatRow(catsNow[0]) : -1;
                                    if (rMine < 0 || rFirst < 0) Core.Log("WARN", "ORDER 树里没有「我的分支」，跳过特殊大类用例");
                                    else
                                    {
                                        string firstCat = catsNow[0];
                                        if (!body.ApplyDrop("我的分支", 2, rFirst, 1)) { bad++; Core.Log("FAIL", "ORDER 「我的分支」拖到「" + firstCat + "」前面没执行"); }
                                        else
                                        {
                                            List<string> after3 = CatPrefs.Cats();
                                            int im = after3.IndexOf("我的分支"), i0 = after3.IndexOf(firstCat);
                                            if (im < 0 || i0 < 0 || im > i0) { bad++; Core.Log("FAIL", "ORDER 「我的分支」没挪到最前：mine=" + im + "、" + firstCat + "=" + i0); }
                                            else Core.Log("INFO", "ORDER 「我的分支」拖到「" + firstCat + "」前面生效");
                                        }
                                    }
                                }
                            }
                        }
                    }
                    finally
                    {
                        try
                        {
                            if (prefRaw != null) File.WriteAllBytes(prefFile, Convert.FromBase64String(prefRaw));
                            else if (File.Exists(prefFile)) File.Delete(prefFile);
                            CatPrefs.Reload();
                            Rebuild();
                        }
                        catch (Exception ex) { Core.Log("WARN", "ORDER 还原 方案树分类.ini 失败: " + ex.Message); }
                    }

                    // 展开状态下把大类拖到别的分类的内容行上：必须自动归到所属大类再落位
                    // （用户实测：树展开后满屏内容行，旧版松在内容行上无声取消）
                    int tk = FindCatRow("人物");
                    if (tk < 0) Core.Log("WARN", "CATDRAG 树里没有「人物」，跳过内容行落点用例");
                    else
                    {
                        SetFolded("人物", false);
                        Rebuild();
                        // 人物的小分类（一代/二代/其他人物）可能整段收起，内容行可能全是小标题行 ——
                        // 落点解析对小标题行和位置行一视同仁（都向上归到大类），所以随便挑一行非大类行就够
                        int contentRow = (tk + 1 < body.RowCount && !body.RowIsCat(tk + 1)) ? tk + 1 : -1;
                        if (contentRow < 0) Core.Log("WARN", "CATDRAG 「人物」展开后没有内容行，跳过");
                        else
                        {
                            List<string> beforeC = CatPrefs.Cats();
                            string src = beforeC[0] == "人物" ? beforeC[1] : beforeC[0];
                            int fromRow = FindCatRow(src);
                            if (fromRow < 0) { bad++; Core.Log("FAIL", "CATDRAG 树里找不到源大类「" + src + "」"); }
                            else
                            {
                                string stC;
                                body.SimulateRealDrag(fromRow, contentRow, 2, out stC);
                                Core.Log("INFO", "CATDRAG 内容行路径 " + stC);
                                List<string> afterC = CatPrefs.Cats();
                                int xa = afterC.IndexOf(src), xb = afterC.IndexOf("人物");
                                if (xb < 0) { bad++; Core.Log("FAIL", "CATDRAG 内容行落点后找不到「人物」"); }
                                // 大类的地盘按整块对半分：落点在内容行的上/下半决定它前面 / 后面 ——
                                // 只要紧贴着「人物」就是归对了大类
                                else if (xa != xb + 1 && xa != xb - 1)
                                { bad++; Core.Log("FAIL", "CATDRAG 松在内容行上没归到大类：源=" + src + "(" + xa + ") 人物(" + xb + ")"); }
                                else Core.Log("INFO", "CATDRAG 松在「人物」的内容行上 → 自动归到「人物」旁边，顺序生效");

                                // 「本来就在」用例：先把 src 挪到人物后面，紧接着再挪一次 ——
                                // 第二次必须明确返回且顺序不变（用户实测场景：拖紧邻邻居看起来像「没反应」）
                                bool m1 = MoveCatTo(src, "人物", true);
                                List<string> snapB = CatPrefs.Cats();
                                bool m2 = MoveCatTo(src, "人物", true);
                                List<string> snapA = CatPrefs.Cats();
                                bool same = snapB.Count == snapA.Count;
                                if (same)
                                    for (int i = 0; i < snapB.Count; i++)
                                        if (snapB[i] != snapA[i]) { same = false; break; }
                                if (m1 && m2 && same) Core.Log("INFO", "CATDRAG 「本来就在」用例：重复挪到同一位置 → 明确提示没挪，顺序不变");
                                else { bad++; Core.Log("FAIL", "CATDRAG 「本来就在」用例异常 m1=" + m1 + " m2=" + m2 + " 顺序变了=" + !same); }

                                // 「拖紧邻邻居 = 对调」用例：人物在上、近战紧贴其下，把人物拖到近战行的上半
                                // —— 解析成「移到近战前面」= 原地不动，必须自动翻到后面 = 两者对调
                                Rebuild();
                                int qa = FindCatRow("人物"), qb = FindCatRow("近战");
                                if (qa >= 0 && qb >= 0)
                                {
                                string stS;
                                body.SimulateRealDrag(qa, qb, 1, out stS);
                                Core.Log("INFO", "CATDRAG 邻居对调路径 " + stS);
                                if (!stS.Contains("pv=" + body.RowCount + "/" + body.RowCount))
                                { bad++; Core.Log("FAIL", "CATDRAG 拖动预览行数不对（应与原树行数一致）：" + stS); }
                                List<string> afterS = CatPrefs.Cats();
                                    if (afterS.IndexOf("近战") == afterS.IndexOf("人物") - 1)
                                        Core.Log("INFO", "CATDRAG 拖紧邻邻居 → 自动对调位置");
                                    else { bad++; Core.Log("FAIL", "CATDRAG 拖紧邻邻居没对调：人物=" + afterS.IndexOf("人物") + " 近战=" + afterS.IndexOf("近战")); }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                bad++;
                Core.Log("FAIL", "BRANCH 自检抛异常：" + ex.Message);
            }
            finally
            {
                try { MyBranch.Delete(ch); MyBranch.Delete(pa); MyBranch.Delete(s1); MyBranch.Delete(s2); } catch { }
                // 大类顺序恢复原样：逐个挪回备份里的位置
                try
                {
                    List<string> cats = CatPrefs.Cats();
                    for (int i = 0; i < catBackup.Count && i < cats.Count; i++)
                    {
                        if (cats[i] == catBackup[i]) continue;
                        CatPrefs.MoveCatTo(catBackup[i], cats[i], false);
                        cats = CatPrefs.Cats();
                    }
                }
                catch { }
                try { Rebuild(); } catch { }
                // 折叠状态按测试前恢复，并落盘（SetFolded 已经把测试期间的展开写进文件了）
                try
                {
                    folded.Clear();
                    foreach (string f in foldBackup) folded.Add(f);
                    SaveFold();
                    BuildRows();
                }
                catch { }
            }
            return bad;
        }

        public void SetFolded(string cat, bool on)
        {
            bool ch = on ? folded.Add(cat) : folded.Remove(cat);
            BuildRows();
            if (ch) SaveFold();
        }

        internal static string SubKey(string cat, string sub) { return cat + "\u0001" + sub; }

        public bool IsSubFolded(string cat, string sub) { return folded.Contains(SubKey(cat, sub)); }

        public void SetSubFolded(string cat, string sub, bool on)
        {
            bool ch = on ? folded.Add(SubKey(cat, sub)) : folded.Remove(SubKey(cat, sub));
            BuildRows();
            if (ch) SaveFold();
        }

        // ---------------- 折叠状态的记忆 ----------------
        // 树一屏放不下 70 多个位置。默认按小分类收起，先看到一张目录（大类 + 小分类），
        // 点到哪一层再展开 —— 下次打开还停在上次的展开状态，不用重新找。

        private bool foldLoaded;

        private static string FoldFile
        {
            get { return string.IsNullOrEmpty(Core.PkgDir) ? "" : Path.Combine(Core.PkgDir, "方案树折叠.ini"); }
        }

        private void LoadFold()
        {
            if (foldLoaded) return;
            foldLoaded = true;
            folded.Clear();
            bool had = false;
            try
            {
                string f = FoldFile;
                if (f.Length > 0 && File.Exists(f))
                {
                    had = true;
                    foreach (string raw in File.ReadAllLines(f, Encoding.UTF8))
                    {
                        string s = raw.TrimEnd();
                        if (s.Length == 0 || s[0] == '#') continue;
                        if (s.StartsWith("cat=", StringComparison.Ordinal))
                        {
                            string v = s.Substring(4).Trim();
                            if (v.Length > 0) folded.Add(v);
                        }
                        else if (s.StartsWith("sub=", StringComparison.Ordinal))
                        {
                            string[] p = s.Substring(4).Split('\t');
                            if (p.Length >= 2 && p[0].Trim().Length > 0) folded.Add(SubKey(p[0].Trim(), p[1].Trim()));
                        }
                    }
                }
            }
            catch { }
            if (!had) FoldAllSubs();          // 第一次打开：只留目录，别一上来就铺 90 行
        }

        /// <summary>所有小分类都收起（大类标题还留着），文件里也记下来。</summary>
        private void FoldAllSubs()
        {
            folded.Clear();
            Dictionary<string, bool> keys = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (Slot s in slots)
            {
                if (string.IsNullOrEmpty(s.Sub)) continue;
                keys[SubKey(s.Cat, s.Sub)] = true;
            }
            foreach (string k in keys.Keys) folded.Add(k);
        }

        private void SaveFold()
        {
            string f = FoldFile;
            if (f.Length == 0) return;
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# 方案树折叠状态：cat=大类 / sub=大类<TAB>小分类（本工具自己写，手改也行）");
                List<string> cats = new List<string>();
                List<string> subs = new List<string>();
                foreach (string k in folded)
                {
                    int us = k.IndexOf('\u0001');
                    if (us < 0) cats.Add(k); else subs.Add(k);
                }
                cats.Sort(StringComparer.Ordinal);
                subs.Sort(StringComparer.Ordinal);
                foreach (string c in cats) sb.AppendLine("cat=" + c);
                foreach (string k in subs) sb.AppendLine("sub=" + k.Substring(0, k.IndexOf('\u0001')) + "\t" + k.Substring(k.IndexOf('\u0001') + 1));
                File.WriteAllText(f, sb.ToString(), new UTF8Encoding(true));
            }
            catch (Exception ex) { Core.Log("WARN", "折叠状态保存失败: " + ex.Message); }
        }

        /// <summary>右键「全部收起」：只留大类标题，一屏能看完整棵树。</summary>
        internal void FoldEverything()
        {
            folded.Clear();
            foreach (string c in SlotIndex.CatOrder(slots)) folded.Add(c);
            BuildRows();
            SaveFold();
            lblFoot.Text = "全部收起 · 点大类或小分类那行就展开";
        }

        /// <summary>右键「全部展开」。</summary>
        internal void UnfoldEverything()
        {
            folded.Clear();
            BuildRows();
            SaveFold();
            lblFoot.Text = "全部展开";
        }

        /// <summary>
        /// 选中某个位置时，把它所在的大类 / 小分类打开 —— 否则跳过去以后
        /// 树上看不到自己选中的那一行，像点了个空。
        /// </summary>
        private void RevealFold(string key)
        {
            if (string.IsNullOrEmpty(key) || key[0] == '#') return;
            Slot hit = null;
            foreach (Slot s in slots) if (s.Key == key) { hit = s; break; }
            if (hit == null) return;
            bool ch = folded.Remove(hit.Cat);
            if (!string.IsNullOrEmpty(hit.Sub)) ch |= folded.Remove(SubKey(hit.Cat, hit.Sub));
            if (!ch) return;
            BuildRows();
            SaveFold();
        }

        // ---------------------------------------------------------- 右侧

        private Slot Current
        {
            get
            {
                foreach (Slot s in slots) if (s.Key == selKey) return s;
                return null;
            }
        }

        /// <summary>右侧列表当前打开的是哪个分支；不在分支页上返回 null。建子分支的菜单入口要用。</summary>
        internal Slot CurrentBranch
        {
            get { return IsBranchSlot(Current) ? Current : null; }
        }

        /// <summary>
        /// 「只装这里」：只把这个分支勾上的 vpk 铺进游戏（含同位置的扩展），
        /// 这个位置以前装、这次没选的候选只翻 addonlist 的开关（文件留着，能开回来）。
        /// 其它位置、L4N / dxvk / 配置一律不碰。
        /// </summary>
        private void DeployHere()
        {
            Slot s = Current;
            if (s == null) return;

            List<ModItem> sel = new List<ModItem>();
            List<ModItem> off = new List<ModItem>();
            foreach (ModGroup g in s.Items)
            {
                bool on = mf.CountChecked(g) > 0;
                if (on)
                {
                    foreach (ModItem p in g.Parts) sel.Add(p);
                }
                else if (!s.Pile)
                {
                    // 同一位置互斥：以前装过的（库里的、或只在游戏目录里的）只把开关写成 0，文件一律不动。
                    // 只在游戏目录里的那些（FromGame）以前漏了，会导致同一位置两个 MOD 同时开着。
                    // 「一堆」（地图 / 我的分支）不互斥：没勾就是没勾，不能顺手把别的地图关掉。
                    foreach (ModItem p in g.Parts) if (p.Installed || p.FromGame) off.Add(p);
                }
            }
            // 扩展可叠加：没勾就不动，别把玩家原来在用的音效 / HUD 顺手关了
            foreach (ModGroup g in s.Extras)
                if (mf.CountChecked(g) > 0)
                    foreach (ModItem p in g.Parts) sel.Add(p);

            if (sel.Count == 0)
            {
                MessageBox.Show("这个位置现在没选 MOD。\r\n\r\n先在右边点一个候选（组合装可以双击挑部件），再点「只装这里」。",
                    "没有可装的东西", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            mf.DeploySlot(sel, off, s.Name, new Dictionary<string, bool>(pending), delegate
            {
                // 「只装这里」把攒下的开关改动一起写了，这里清账
                pending.Clear();
                RefreshSwitchCells();
                UpdateApplyButton();
            });
        }

        /// <summary>鼠标 x 落在第几列（客户区坐标）。列宽是自适应的，没有横向滚动。</summary>
        private static int ColAt(ListView v, int x)
        {
            int acc = 0;
            for (int i = 0; i < v.Columns.Count; i++)
            {
                acc += v.Columns[i].Width;
                if (x < acc) return i;
            }
            return -1;
        }

        /// <summary>点「开关」列：把这个 MOD 在 addonlist 里的 1/0 翻过来（先攒在 pending，不立刻写文件）。</summary>
        private void ToggleSwitch(ModGroup g)
        {
            if (g == null) return;
            List<ModItem> ins = new List<ModItem>();
            foreach (ModItem p in g.Parts) if (p.FromGame || p.Installed) ins.Add(p);
            if (ins.Count == 0)
            {
                MessageBox.Show("这个 MOD 还没进游戏目录，「开关」列是 —，没得开关。\r\n\r\n"
                    + "想让它进游戏：先点这一行（选中它），再点「只装这里」。\r\n"
                    + "已经在游戏里的 MOD（这一列会显示 开 / 关）才谈得上开 / 关。",
                    "还没装进游戏", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            bool allOn = true;
            foreach (ModItem p in ins) if (!PendingOf(p)) { allOn = false; break; }
            bool target = !allOn;
            foreach (ModItem p in ins) pending[p.Name] = target;
            RefreshSwitchCells();
            UpdateApplyButton();
            lblFoot.Text = (target ? "已标记为「开」" : "已标记为「关」")
                + "（" + pending.Count + " 处待应用，开关后面带 * ）· 点右下角「应用改动」才写进游戏";
        }

        /// <summary>给右键菜单用：把这一组 MOD 的开关翻过来（和点「开关 ⇄」列同一个动作）。</summary>
        internal void ToggleFromMenu(ModGroup g) { ToggleSwitch(g); }

        /// <summary>右边那半重画一次（搜索词变了、外部改了勾选之后调）。</summary>
        internal void RefreshPane() { FillDetail(); }

        /// <summary>只重画受影响的两列文字和底色，不重建列表 —— 点开关时滚动位置不跳。</summary>
        private void RefreshSwitchCells()
        {
            if (lv != null)
                foreach (ListViewItem li in lv.Items)
                {
                    ModGroup g = li.Tag as ModGroup;
                    if (g != null) ApplyStateCells(li, g);
                }
            if (lvExtra != null)
                foreach (ListViewItem li in lvExtra.Items)
                {
                    ModGroup g = li.Tag as ModGroup;
                    if (g != null) ApplyStateCells(li, g);
                }
        }

        /// <summary>
        /// 「应用改动」常驻：有改动就变实心、标题带条数；没改动是白底细线（照样能点，
        /// 点了会告诉你「先去开关列点一下」）。以前是「攒了改动才出现」，第一次用的人根本不知道有这个按钮。
        /// 没改动时不禁用：禁用的按钮点不动，也就没法解释它管什么。
        /// </summary>
        private void UpdateApplyButton()
        {
            if (btnApply == null) return;
            int n = pending.Count;
            string want = n > 0 ? "应用改动 " + n : "应用改动";
            if (btnApply.Text != want) btnApply.Text = want;
            bool on = n > 0;
            btnApply.Primary = on;
        }

        private void ApplyHere()
        {
            if (pending.Count == 0)
            {
                MessageBox.Show(
                    "现在还没有要应用的开关改动。\r\n\r\n"
                    + "用法：在右边列表最右边的「开关」列点一下，那一行就会被标成「开」或「关」"
                    + "（行底色跟着变，开关文字后面带 * 表示还没写进去）。\r\n\r\n"
                    + "标好一个或几个，再点这个按钮，一次性写进 left4dead2\\addonlist.txt。\r\n"
                    + "已经装在游戏目录里的 MOD 只改 1/0，文件一个都不复制、不移动。",
                    "还没有改动", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!mf.ApplySwitches(new Dictionary<string, bool>(pending))) return;
            pending.Clear();
            RefreshSwitchCells();
            UpdateApplyButton();
            FillDetail();                      // 底部说明与统计回到正常态
        }

        private void FillDetail()
        {
            UpdateApplyButton();               // 攒着的开关改动跨位置都算，跟着按钮显示数量
            // 搜索优先：搜了词就只看结果（搜索只剩这一条路可走）
            string kw = mf.SearchText;
            if (kw.Length > 0) { ShowSearch(kw); return; }
            if (selKey == "#unmatched")
            {
                if (btnDeploy != null) btnDeploy.Visible = false;
                ShowUnmatched();
                return;
            }
            if (selKey == "#mine+new")
            {
                if (btnDeploy != null) btnDeploy.Visible = false;
                ShowNewBranch();
                return;
            }
            lv.Visible = true;
            Slot s = Current;
            // 分支页也给「指定位置…」：放进分支的包多半是认不出位置的，
            // 在这里选中它就能直接告诉程序「它替换的是什么」，不用再绕回未归类页。
            if (btnCat != null) btnCat.Visible = IsBranchSlot(s);
            // 这个位置有选中东西才给「只装这里」；空着就是「留原版」，没什么可装的
            if (btnDeploy != null)
                btnDeploy.Visible = s != null && (CheckedGroups(s) + CheckedExtras(s)) > 0;
            lv.BeginUpdate();
            lvExtra.BeginUpdate();
            try
            {
                lv.Items.Clear();
                lvExtra.Items.Clear();
                if (s == null)
                {
                    lblTitle.Text = "选一个位置";
                    lblCur.Text = "左边点一个武器 / 药品 / 人物，这里列它的 MOD";
                    lblExtraTitle.Visible = false;
                    lvExtra.Visible = false;
                    return;
                }
                int n = CheckedGroups(s);
                int ne = CheckedExtras(s);
                lblTitle.Text = s.Name;
                if (s.Empty)
                {
                    // 空位：不是「忘了选」，是这个位置压根还没有包 —— 话说清楚，别让人以为坏了
                    lblCur.Text = "这个位置还没有 MOD · 游戏里就是原版";
                }
                else if (s.Pile)
                {
                    // 一堆：能同时开好几个，「叠了 N 件」这种话在这里是正常的，不是警告
                    lblCur.Text = n == 0
                        ? "这一堆还没勾 · 共 " + s.Items.Count + " 个，能同时开好几个"
                        : "这一堆已勾 " + n + " 个 · 会一起装，冲不冲突自己确认";
                }
                else if (n == 0 && ne == 0) lblCur.Text = "还没选 · 游戏里还是原版";
                else if (n == 1)
                {
                    ModGroup c = ChosenOf(s);
                    lblCur.Text = "当前：" + (c == null ? "" : c.Name) + (ne > 0 ? "　+ " + ne + " 个扩展" : "");
                }
                else if (n > 1) lblCur.Text = "这个位置叠了 " + n + " 件（自己确认会不会打架）";
                else lblCur.Text = "模型还是原版 · 只装了 " + ne + " 个扩展";

                foreach (ModGroup g in s.Items)
                {
                    bool on = mf.CountChecked(g) > 0;
                    ListViewItem li = new ListViewItem(g.Name);
                    li.SubItems.Add(KindsOf(g));
                    li.SubItems.Add(Core.Human(g.Size));
                    AddStateCells(li, g);
                    li.StateImageIndex = on ? 1 : 0;
                    if (on) li.Font = Theme.UIBold;
                    li.Tag = g;
                    if (g.IsSet)
                    {
                        // 组合装：改了什么列蓝字标出来，不用悬停也知道这行能挑部件
                        li.UseItemStyleForSubItems = false;
                        li.SubItems[1].Text = KindsOf(g) + "·组合装" + g.Parts.Count + "件";
                        li.SubItems[1].ForeColor = Theme.Info;
                        li.SubItems[1].Font = Theme.UIBold;
                        li.ToolTipText = PartsTip(g);
                    }
                    lv.Items.Add(li);
                }

                if (!s.Pile)
                {
                    ListViewItem none = new ListViewItem("留原版（不换模型）");
                    none.SubItems.Add("");
                    none.SubItems.Add("—");
                    none.ForeColor = Theme.Ink3;
                    none.StateImageIndex = n == 0 ? 1 : 0;
                    none.Tag = null;
                    lv.Items.Add(none);
                }

                if (s.Pile)
                {
                    lblFoot.Text = s.Items.Count + " 个 · 点一行 = 开 / 关（不是替换，能同时开好几个）"
                                 + (s.Key.StartsWith("#mine:", StringComparison.Ordinal)
                                    ? " · 选中一行可「指定位置」" : "");
                }
                else if (s.Empty)
                {
                    // 窄窗口下这一行只剩一百多像素，长句子必被省略号切 —— 怎么进货交给悬停提示（UpdateTip）
                    lblFoot.Text = "还没有这个位置的 MOD";
                }
                else lblFoot.Text = s.Items.Count == 0
                    ? "这个位置没有换模型的包，只有下面那些扩展"
                    : s.Items.Count + " 个模型候选 · 点一个就换掉原来的"
                      + (HasSet(s) ? " · 组合装双击可挑部件" : "")
                      + " · 「开关 ⇄」列点一下=把这个 MOD 开 / 关";

                // ---- 下半：可叠加的扩展 ----
                lblExtraTitle.Visible = true;
                if (s.Extras.Count == 0)
                {
                    lvExtra.Visible = false;
                    // 窄窗口下这一行只有 330px 左右，写太长会被省略号切掉；
                    // 完整的说法放在悬停提示里（见 lblExtraTitle 的 ToolTip）。
                    lblExtraTitle.Text = "可叠加的扩展：这个位置没有（音效 / 第一人称 / HUD）";
                }
                else
                {
                    lvExtra.Visible = true;
                    lblExtraTitle.Text = "可叠加的扩展 · " + s.Extras.Count
                                       + " 个（不换模型，点一下开 / 关，可多选）";
                    // 按小类排：第一人称模型 / 音效 / HUD / 特效 / 脚本，组与组之间插一行灰色标题
                    List<ModGroup> ex = new List<ModGroup>(s.Extras);
                    ex.Sort(delegate(ModGroup a, ModGroup b)
                    {
                        int ra = ExtraCatRank(ExtraCat(a)), rb = ExtraCatRank(ExtraCat(b));
                        if (ra != rb) return ra.CompareTo(rb);
                        return string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
                    });
                    string lastCat = null;
                    foreach (ModGroup g in ex)
                    {
                        string cat = ExtraCat(g);
                        if (cat != lastCat)
                        {
                            ListViewItem head = new ListViewItem("── " + cat + " ──");
                            head.SubItems.Add("");
                            head.SubItems.Add("");
                            head.ForeColor = Theme.Ink3;
                            head.Font = Theme.UISmall;
                            head.UseItemStyleForSubItems = false;
                            head.Tag = "header";                 // 不是 MOD：点击进来 as ModGroup 得 null，直接返回
                            head.StateImageIndex = -1;           // 标题行不显示圆点复选框
                            lvExtra.Items.Add(head);
                            lastCat = cat;
                        }
                        bool on = mf.CountChecked(g) > 0;
                        ListViewItem li = new ListViewItem(g.Name);
                        li.SubItems.Add(KindsOf(g));
                        li.SubItems.Add(Core.Human(g.Size));
                        AddStateCells(li, g);
                        li.StateImageIndex = on ? 1 : 0;
                        if (on) li.Font = Theme.UIBold;
                        li.Tag = g;
                        if (g.IsSet)
                        {
                            li.UseItemStyleForSubItems = false;
                            li.SubItems[1].Text = KindsOf(g) + "·组合装" + g.Parts.Count + "件";
                            li.SubItems[1].ForeColor = Theme.Info;
                            li.SubItems[1].Font = Theme.UIBold;
                            li.ToolTipText = PartsTip(g);
                        }
                        else li.ToolTipText = "和上面的模型同时生效，互不影响";
                        lvExtra.Items.Add(li);
                    }
                }
            }
            finally { lv.EndUpdate(); lvExtra.EndUpdate(); }
            LayoutSelf();
            LayoutCols();
        }

        /// <summary>
        /// 搜索结果页：搜了词就只列名字里带这个词的 MOD，点一行跳回它在树里的位置。
        /// 搜索框搜出来的东西直接铺在树右边，点一行跳回它的位置。
        /// </summary>
        private void ShowSearch(string kw)
        {
            lblExtraTitle.Visible = false;
            lvExtra.Visible = false;
            if (btnDeploy != null) btnDeploy.Visible = false;
            if (btnCat != null) btnCat.Visible = false;
            lv.Visible = true;
            lblTitle.Text = "搜索：" + kw;
            lv.BeginUpdate();
            try
            {
                lv.Items.Clear();
                int n = 0;
                foreach (ModGroup g in Catalog.Groups)
                {
                    if (string.IsNullOrEmpty(g.Name) || !mf.MatchGroup(g, kw)) continue;
                    ListViewItem li = new ListViewItem(g.Name);
                    ModItem mi = g.Main;
                    li.SubItems.Add(g.Cat + (mi == null || string.IsNullOrEmpty(mi.Sub) ? "" : " · " + mi.Sub));
                    li.SubItems.Add(Core.Human(g.Size));
                    AddStateCells(li, g);
                    li.StateImageIndex = mf.CountChecked(g) > 0 ? 1 : 0;
                    li.Tag = g;
                    lv.Items.Add(li);
                    n++;
                }
                lblCur.Text = n + " 个匹配「" + kw + "」的 MOD · 名称 / 替换内容 / 位置都参与搜索 · 点一行跳到他该在的位置";
            }
            finally { lv.EndUpdate(); }
            lblFoot.Text = "清空搜索框回到方案树；这一页只找东西，不改勾选。";
            LayoutSelf();
            LayoutCols();
        }

        /// <summary>「＋ 新建分支…」那行：右半边只放说明（真正的输入框在点上那一行时弹）。</summary>
        private void ShowNewBranch()
        {
            lblExtraTitle.Visible = false;
            lvExtra.Visible = false;
            lv.Visible = true;
            lv.Items.Clear();
            lblTitle.Text = "我的分支";
            lblCur.Text = "程序认不出改的是什么的 MOD，自己拉一组装进去（一组里能同时开好几个）";
            lblFoot.Text = "这一行 = 新建顶层分支 · 右键一个已有分支 = 在它下面建子分支";
            LayoutSelf();
            LayoutCols();
        }

        /// <summary>
        /// 未归类页：把没进任何位置的 MOD 全部列出来（一个不漏），
        /// 选中一行点「指定位置…」或直接双击，就打开「设置 MOD 分类」窗口手动归位。
        /// </summary>
        private void ShowUnmatched()
        {
            lblExtraTitle.Visible = false;
            lvExtra.Visible = false;
            lblTitle.Text = "未归类";
            lblCur.Text = unmatched + " 个 MOD 没认出改的是哪件东西";
            lv.BeginUpdate();
            try
            {
                lv.Items.Clear();
                // 内容相似的圈成一伙、排在相邻位置，伙与伙之间不穿插；单干的保持原顺序跟在后面
                int clustered;
                Dictionary<ModGroup, List<ModGroup>> clan = ClusterUnmatched(unmatchedGroups, out clustered);
                HashSet<ModGroup> emitted = new HashSet<ModGroup>();
                foreach (ModGroup g in unmatchedGroups)
                {
                    if (emitted.Contains(g)) continue;
                    List<ModGroup> members;
                    bool inClan = clan.TryGetValue(g, out members);
                    if (inClan)
                    {
                        ListViewItem head = new ListViewItem("同类包 " + members.Count + " 个 · 内容高度相似，挑一个装就行");
                        head.Tag = null;
                        head.ForeColor = Theme.Ink3;
                        head.Font = Theme.UIBold;
                        lv.Items.Add(head);
                    }
                    foreach (ModGroup mg in inClan ? members : new List<ModGroup> { g })
                    {
                        if (emitted.Contains(mg)) continue;
                        emitted.Add(mg);
                        AddUnmatchedRow(mg, inClan);
                    }
                }
            }
            finally { lv.EndUpdate(); }
            lblFoot.Text = "不影响装机，照常能勾。选中一行 →「指定位置…」；认不出的也可以右键 →「放进我的分支」。";
            if (btnCat != null) btnCat.Visible = true;
            LayoutSelf();
            LayoutCols();
        }

        /// <summary>未归类页的一行。<paramref name="inClan"/>=true 表示上面还有「同类包」伙头，名字前加 └。</summary>
        private void AddUnmatchedRow(ModGroup g, bool inClan)
        {
            bool on = mf.CountChecked(g) > 0;
            ListViewItem li = new ListViewItem((inClan ? "└ " : "") + g.Name);
            li.SubItems.Add(KindsOf(g));
            li.SubItems.Add(Core.Human(g.Size));
            AddStateCells(li, g);
            li.StateImageIndex = on ? 1 : 0;
            if (on) li.Font = Theme.UIBold;
            li.Tag = g;
            if (g.IsSet)
            {
                li.UseItemStyleForSubItems = false;
                li.SubItems[1].Text = KindsOf(g) + "·组合装" + g.Parts.Count + "件";
                li.SubItems[1].ForeColor = Theme.Info;
                li.SubItems[1].Font = Theme.UIBold;
                li.ToolTipText = PartsTip(g);
            }
            else li.ToolTipText = "双击这行（或点右下角「指定位置…」），告诉程序它算哪一类、替换什么";
            lv.Items.Add(li);
        }

        private static string KindsOf(ModGroup g)
        {
            ModItem m = g.Main;
            if (m == null) return "";
            string k = m.Kinds;
            if (string.IsNullOrEmpty(k)) k = m.SubjectLevel;
            return k == null ? "" : k.Replace(",", "·");
        }

        /// <summary>组合装行的悬停说明：部件一个一个念出来，外加怎么挑。</summary>
        private static string PartsTip(ModGroup g)
        {
            string t = "";
            foreach (ModItem p in g.Parts)
                t += (t.Length > 0 ? "\r\n　+ " : "") + Catalog.CleanName(p.Name)
                     + "（" + p.Role + "）";
            return g.Parts.Count + " 个 vpk 组成：" + t
                 + "\r\n点一下=装默认部件，双击=挑部件（材质 / 颜色）";
        }

        /// <summary>点了一个候选（null = 留原版）：清掉这个位置的旧件，再勾新件。</summary>
        private void Choose(ModGroup g)
        {
            Slot s = Current;
            if (s == null) return;
            ChooseInSlot(s, g);
            FillDetail();
        }

        /// <summary>给某个位置换候选（台面共用）：先清空这个位置，再勾这一个。</summary>
        internal void ChooseInSlot(Slot s, ModGroup g)
        {
            if (s == null) return;
            foreach (ModGroup o in s.Items) mf.UncheckGroup(o);
            if (g != null) mf.CheckGroupDefault(g);
            mf.AfterTreePick();
        }

        /// <summary>这个位置的候选里有没有组合装（模型+材质+颜色的套装）。</summary>
        private static bool HasSet(Slot s)
        {
            foreach (ModGroup g in s.Items) if (g.IsSet) return true;
            return false;
        }

        /// <summary>右键菜单里的「用这个替换」：拿这一行的 Tag 去换，位置用当前选中的那个。</summary>
        internal void PickRow(ListViewItem li)
        {
            if (li == null) return;
            Choose(li.Tag as ModGroup);
        }

        /// <summary>自测用：方案树右边那张表的右键菜单，一路念出来。</summary>
        internal string TreeMenuText()
        {
            if (lv.ContextMenuStrip == null) return "（没有菜单）";
            string all = "";
            foreach (ToolStripItem it in lv.ContextMenuStrip.Items)
                all += (it is ToolStripSeparator ? " | " : it.Text + " · ");
            return all;
        }

        /// <summary>自测用：直接选某个槽位里的第 i 个候选。</summary>
        public string ChooseAt(int slotIdx, int itemIdx)
        {
            Slot s = SlotAt(slotIdx);
            if (s == null) return "没有槽位";
            selKey = s.Key;
            if (itemIdx < 0) { Choose(null); return "留原版"; }
            if (itemIdx >= s.Items.Count) return "候选越界";
            ModGroup g = s.Items[itemIdx];
            Choose(g);
            return g.Name;
        }

        // ---------------------------------------------------------- 布局

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (scroll == null) return;
            using (Pen p = new Pen(Theme.Line))
                e.Graphics.DrawLine(p, scroll.Right, 0, scroll.Right, Height);
        }

        private void LayoutSelf()
        {
            int W = ClientSize.Width, H = ClientSize.Height;
            if (W <= 0 || H <= 0) return;
            // 三层之后每行多缩了一级，树比以前宽一点，否则物品名和「选了哪个」会挤在一起
            int treeW = Math.Min(430, Math.Max(260, W * 46 / 100));
            if (scroll != null) scroll.SetBounds(0, 0, treeW, H);
            // Dock 让树体宽度自动跟着可视区走（含竖直滚动条出现/消失），不然右边「n 个」会被裁掉
            if (scroll != null) scroll.Relayout();

            int x = treeW + 1;
            int rightW = Math.Max(120, W - x);
            if (lblTitle != null) lblTitle.SetBounds(x + 16, 12, Math.Max(60, rightW - 32 - 100), 22);
            if (lblCur != null) lblCur.SetBounds(x + 16, 34, Math.Max(60, rightW - 32), 18);

            // 右半分上下：上半换本体（单选），下半可叠加的扩展（多选）。
            // 没有扩展时下半只留一行说明，上半占满 —— 空列表摆在那儿只是浪费地方。
            int top = 60, bottom = H - 42;
            int avail = Math.Max(120, bottom - top);
            const int gap = 26;                                   // 中间那行小标题占的高度
            bool hasEx = lvExtra != null && lvExtra.Visible;
            int mainH = hasEx ? Math.Max(110, avail * 56 / 100) : Math.Max(110, avail - gap);
            int exTop = top + mainH + gap;
            int exH = Math.Max(0, bottom - exTop);
            const int headH = 26;
            if (headMain != null)
            {
                headMain.SetBounds(x + 4, top, rightW - 8, headH);
                headMain.Visible = lv != null && lv.Visible;
            }
            if (lv != null) lv.SetBounds(x + 4, top + headH, rightW - 8, Math.Max(40, mainH - headH));
            if (lblExtraTitle != null)
                lblExtraTitle.SetBounds(x + 16, Math.Max(top + 8, exTop - 20), Math.Max(80, rightW - 32), 18);
            if (lvExtra != null)
            {
                if (exH >= 40)
                {
                    lvExtra.SetBounds(x + 4, exTop + headH, rightW - 8, Math.Max(40, exH - headH));
                    lvExtra.Visible = true;
                    if (headExtra != null)
                    {
                        headExtra.SetBounds(x + 4, exTop, rightW - 8, headH);
                        headExtra.Visible = true;
                    }
                }
                else
                {
                    lvExtra.Visible = false;
                    if (headExtra != null) headExtra.Visible = false;
                }
            }

            if (btnPick != null) btnPick.SetBounds(x + 16, H - 34, 76, Theme.H);
            // 右下角从右往左排：「指定位置…」只在未归类页，「只装这里」在这个位置有勾选时；
            // 「应用改动」常驻最右（位置固定，不然没人找得到）。脚注按实际占用让位。
            bool catBtn = btnCat != null && btnCat.Visible;
            bool depBtn = btnDeploy != null && btnDeploy.Visible;
            bool appBtn = btnApply != null;                 // 常驻：位置固定，不让它在有改动时才挪出来
            int edge = x + rightW - 8;
            int used = 0;
            if (catBtn) { btnCat.SetBounds(edge - 100, H - 34, 100, Theme.H); edge -= 108; used += 108; }
            if (appBtn) { btnApply.SetBounds(edge - 104, H - 34, 104, Theme.H); edge -= 112; used += 112; }
            if (depBtn) { btnDeploy.SetBounds(edge - 92, H - 34, 92, Theme.H); edge -= 100; used += 100; }
            if (lblFoot != null)
                lblFoot.SetBounds(x + 16, H - 30, Math.Max(60, rightW - 32 - used), 18);
            LayoutCols();
        }

        private void LayoutCols()
        {
            Cols(lv);
            Cols(lvExtra);
            // 列宽已经按客户区算好，不需要横向滚动；系统那条亮色滚动条只会破坏深色
            if (lv != null && lv.IsHandleCreated) Native.HideHScroll(lv.Handle);
            if (lvExtra != null && lvExtra.IsHandleCreated) Native.HideHScroll(lvExtra.Handle);
            if (headMain != null) headMain.Invalidate();
            if (headExtra != null) headExtra.Invalidate();
        }

        private static void Cols(ListView v)
        {
            if (v == null || v.Columns.Count < 3) return;
            int w = Math.Max(120, v.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 8);
            int kindW, sizeW, instW, swW;
            if (w >= 440)
            {
                kindW = 100; sizeW = 74; instW = 84; swW = 62;
            }
            else
            {
                // 最小窗口下先把信息量最低的“改了什么”收掉，保住名称、大小、已装、开关。
                // 不横向滚动：这一栏本来就是窄窗口里最容易被裁掉的区域。
                kindW = 0;
                if (w >= 340) { sizeW = 64; instW = 70; swW = 54; }
                else { sizeW = 56; instW = 60; swW = 46; }
            }

            // 本地模式：没有「库」可比，「已装」恒为真 —— 这一列纯属噪音，收成 0 宽、把宽度并给开关列。
            // 于是本地模式下能看到的只剩「开启 / 关闭」两个状态（开关列同时也是点击入口）。
            bool local = Core.IsLocal && v.Columns.Count >= 5;
            if (local) { swW += instW; instW = 0; }

            int nameW = Math.Max(90, w - kindW - sizeW - instW - swW);
            v.Columns[0].Width = nameW;
            v.Columns[1].Width = kindW;
            v.Columns[2].Width = sizeW;
            if (v.Columns.Count >= 5)
            {
                v.Columns[3].Width = instW;
                v.Columns[4].Width = swW;
                // 表头跟着模式换：本地模式下没有「已装」，那一列叫「已装」会误导
                string t3 = local ? "" : "已装";
                string t4 = local ? "开启 ⇄" : "开关 ⇄";
                if (v.Columns[3].Text != t3) v.Columns[3].Text = t3;
                if (v.Columns[4].Text != t4) v.Columns[4].Text = t4;
            }
        }
    }

    /// <summary>
    /// 候选列表的表头。原生 ListView 表头在深色主题下始终是白色的一条
    /// （给表头窗口换主题也不生效），所以干脆不用它，按列宽自己画一条。
    /// </summary>
    internal sealed class ColHead : Control
    {
        private readonly ListView lv;

        public ColHead(ListView owner)
        {
            lv = owner;
            TabStop = false;
            Font = Theme.UISmall;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Surface);
            if (lv == null || lv.Columns.Count == 0) return;

            int x = 2;
            for (int i = 0; i < lv.Columns.Count; i++)
            {
                ColumnHeader c = lv.Columns[i];
                int w = c.Width;
                if (w <= 0) continue;
                TextFormatFlags f = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                                  | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
                f |= c.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left;
                Rectangle r = new Rectangle(x + 4, 1, Math.Max(8, w - 8), Height - 2);
                TextRenderer.DrawText(g, c.Text, Font, r, Theme.Ink3, f);
                x += w;
            }

            using (Pen p = new Pen(Theme.Line))
                g.DrawLine(p, 0, Height - 1, Width, Height - 1);
        }
    }

    /// <summary>
    /// 自己画的滚动容器。系统的滚动条在深色主题下始终是一条白轨（换窗口主题、
    /// 打 DWM 深色标记都不管用），树本身又是自绘的，索性把整条滚动条也自己画：
    /// 10px 宽、跟主题同色、能滚轮、能拖、点轨道翻页。
    /// </summary>
    internal sealed class ScrollHost : Panel
    {
        private const int BarW = 10;
        private Control body;
        private int offset;
        private bool dragging, hoverBar;
        private int dragY, dragOffset;

        /// <summary>树下面的空白被右键时调它（树比可视区矮时，那块的宿主是 Panel，不是树）。</summary>
        internal Action<Point> BlankMenu;

        public ScrollHost()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public void Attach(Control b)
        {
            body = b;
            Controls.Add(b);
            Relayout();
        }

        private int MaxOffset { get { return body == null ? 0 : Math.Max(0, body.Height - ClientSize.Height); } }
        internal bool CanScroll { get { return MaxOffset > 0; } }
        internal int Offset { get { return offset; } }

        internal void ScrollTo(int v)
        {
            int nv = Math.Max(0, Math.Min(MaxOffset, v));
            if (nv == offset) return;
            offset = nv;
            if (body != null) body.Top = -offset;
            Invalidate();
        }

        internal void Relayout()
        {
            if (body == null) return;
            offset = Math.Max(0, Math.Min(offset, MaxOffset));
            bool bar = MaxOffset > 0;
            int w = Math.Max(20, ClientSize.Width - (bar ? BarW : 0));
            body.SetBounds(0, -offset, w, body.Height);
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Relayout();
        }

        private Rectangle Thumb()
        {
            int h = ClientSize.Height;
            if (body == null || h <= 0) return new Rectangle(Width - BarW, 0, BarW, 0);
            int th = Math.Max(36, (int)((long)h * h / Math.Max(1, body.Height)));
            th = Math.Min(h - 2, th);
            int travel = Math.Max(0, h - th - 2);
            int pos = MaxOffset == 0 ? 0 : (int)((long)travel * offset / MaxOffset);
            return new Rectangle(Width - BarW + 3, 1 + pos, BarW - 6, th);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            if (!CanScroll) return;
            Rectangle t = Thumb();
            using (GraphicsPath p = Theme.Round(t, (BarW - 6) / 2))
            using (SolidBrush b = new SolidBrush(dragging ? Theme.Ink3 : (hoverBar ? Theme.Ink3 : Theme.Line)))
                g.FillPath(b, p);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging)
            {
                int travel = Math.Max(1, ClientSize.Height - Thumb().Height - 2);
                int d = e.Y - dragY;
                ScrollTo(dragOffset + (int)((long)d * MaxOffset / travel));
                return;
            }
            bool hot = e.X >= Width - BarW && CanScroll;
            if (hot != hoverBar) { hoverBar = hot; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverBar = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right && e.X < Width - BarW && BlankMenu != null)
            {
                BlankMenu(PointToScreen(e.Location));
                return;
            }
            if (!CanScroll || e.X < Width - BarW) return;
            Rectangle t = Thumb();
            if (t.Contains(e.Location))
            {
                dragging = true;
                dragY = e.Y;
                dragOffset = offset;
            }
            else
            {
                // 点轨道 = 翻一屏
                ScrollTo(offset + (e.Y < t.Top ? -ClientSize.Height : ClientSize.Height));
            }
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!dragging) return;
            dragging = false;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (CanScroll)
            {
                ScrollTo(offset - e.Delta);
                return;
            }
            base.OnMouseWheel(e);
        }
    }

    // ============================================================
    //  树体：自绘，三层（大类 / 小分类 / 槽位）
    // ============================================================
    internal sealed class SlotTree : Control
    {
        private const int RowH = 28;
        private readonly CatTree owner;
        private readonly List<TreeRow> rows = new List<TreeRow>();
        private int hover = -1;
        private int focus = -1;
        private readonly ToolTip tip = new ToolTip();
        private int tipRow = -2;

        // 拖动分支：按住分支行上下拖，松手就换顺序 / 换层级。
        // 行高只有 28px，所以每一行分三段命中：上 30% = 插到它前面，下 30% = 插到它后面，中间 = 变成它的子分支。
        private bool pressArm;        // 这次按下的是「能拖」的行（大类 / 小分类 / 分支 / 位置）
        private int pressRow = -1;
        private Point pressPt;
        private bool dragging;
        private string dragName = "";
        private string dragCat = "";  // 拖的东西在哪个大类（小分类行 / 位置行要用）
        private string dragSub = "";  // 位置行原来在哪个小分类
        private int dragKind;         // 1=分支行 2=大类标题行 3=小分类行 4=位置行
        private int dropRow = -1;     // 落点行（-1 = 没有落点，松手就当放弃）
        private int dropMode;         // 1=插前面 2=插后面 3=放进它下面
        // 拖动预览：树直接画成「松手后」的样子 —— 搬的块从原地消失、出现在新位置
        private List<TreeRow> previewRows;   // null = 没有合法落点，照常画
        private int pvIns = -1;       // 搬的块在预览里的起始行
        private int pvB1 = -1;        // 搬的块在预览里的结束行（含）
        private Point dragPt;         // 跟随鼠标的小标签画在这儿
        private int lastMoveLog;      // DRAGIN 按住未起拖时的移动日志节流

        public SlotTree(CatTree o)
        {
            owner = o;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Font = Theme.UI;
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleRole = AccessibleRole.List;
            AccessibleName = "MOD 方案树";
            AccessibleDescription = "用上下方向键移动，左右键展开或折叠，Enter 或空格选择";
        }

        public int RowCount { get { return rows.Count; } }
        internal int FocusIndex { get { return focus; } }

        public void SetRows(List<TreeRow> r)
        {
            // 重建行（折叠 / 展开 / 重新分组）时按住键盘焦点原来那一行。
            // 以前一律 focus = -1，结果按 ← 收一段之后焦点跳回第 0 行，
            // 紧接着按 → 展开的就不是刚才那一段了 —— 键盘用户会直接迷路。
            string keepSlot = null, keepCat = null, keepSub = null;
            bool keepNote = false;
            if (focus >= 0 && focus < rows.Count)
            {
                TreeRow old = rows[focus];
                if (old.Slot != null) keepSlot = old.Slot.Key;
                else if (old.IsSub) { keepCat = old.Cat; keepSub = old.Sub; }
                else if (old.IsCat) keepCat = old.Cat;
                else if (old.IsNote) keepNote = true;
            }

            rows.Clear();
            if (r != null) rows.AddRange(r);
            tipRow = -2;
            focus = -1;
            if (keepSlot != null || keepCat != null || keepNote)
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    TreeRow x = rows[i];
                    // 四种行分别按各自的身份找，别串：小分类行也带着大类名，
                    // 混着判会先撞上大类标题，焦点就被拽到顶上了。
                    if (keepSlot != null) { if (x.Slot != null && x.Slot.Key == keepSlot) { focus = i; break; } }
                    else if (keepSub != null) { if (x.IsSub && x.Cat == keepCat && x.Sub == keepSub) { focus = i; break; } }
                    else if (keepNote) { if (x.IsNote) { focus = i; break; } }
                    else if (keepCat != null) { if (x.IsCat && x.Cat == keepCat) { focus = i; break; } }
                }
                // 那一行被折叠掉了（子项全收起来），就退回它所属的大类标题，别让焦点凭空消失
                if (focus < 0 && keepCat != null)
                    for (int i = 0; i < rows.Count; i++)
                        if (rows[i].IsCat && rows[i].Cat == keepCat) { focus = i; break; }
            }
            Height = Math.Max(12, rows.Count * RowH + 6);
            ScrollHost host = Parent as ScrollHost;
            if (host != null) host.Relayout();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.TreeBg);
            int W = Width;
            if (W <= 0) return;

            using (Pen branch = new Pen(Theme.TreeBranch))
            using (SolidBrush selBg = new SolidBrush(Theme.TreeSel))
            using (SolidBrush hovBg = new SolidBrush(Theme.TreeHover))
            {
                // 拖动且有预览时，画的就是「松手后」的树 —— 搬的块已出现在新位置
                List<TreeRow> view = (dragging && previewRows != null) ? previewRows : rows;
                int hov = dragging ? -1 : hover;
                // 大类 / 小分类右边那个数字：按完整位置表算，收起子分类也不变
                List<Slot> all = owner.Slots;
                for (int i = 0; i < view.Count; i++)
                {
                    TreeRow r = view[i];
                    int y = i * RowH + 4;
                    bool focused = Focused && !dragging && i == focus;

                    if (r.IsCat)
                    {
                        bool open = !owner.IsFolded(r.Cat);
                        if (i == hov) g.FillRectangle(hovBg, 0, y, W, RowH);
                        DrawTri(g, 10, y + RowH / 2, open);
                        Glyph.Draw(g, Glyph.ForCat(r.Cat), new Rectangle(18, y + (RowH - 18) / 2, 18, 18), Theme.Ink2);
                        TextRenderer.DrawText(g, r.Cat, Theme.UIBold, new Rectangle(42, y, W - 106, RowH), Theme.Ink,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                        // 计数按整个大类算，不受折叠影响 —— 收起来只是看不见行，
                        // 位置数不该跟着变（桌面上那张卡写的也是这个数）
                        int n = 0;
                        if (all != null) foreach (Slot sl in all) if (sl.Cat == r.Cat) n++;
                        string num = r.Cat == "未归类" ? owner.Unmatched + " 个" : n + " 个";
                        TextRenderer.DrawText(g, num, Theme.UISmall, new Rectangle(W - 62, y, 50, RowH), Theme.Ink3,
                            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                        continue;
                    }

                    if (r.IsSub)
                    {
                        bool open = !owner.IsSubFolded(r.Cat, r.Sub);
                        if (i == hov) g.FillRectangle(hovBg, 0, y, W, RowH);
                        g.DrawLine(branch, 17, y, 17, y + RowH);
                        g.DrawLine(branch, 17, y + RowH / 2, 26, y + RowH / 2);
                        DrawTri(g, 32, y + RowH / 2, open);
                        Glyph.Draw(g, Glyph.ForSub(r.Sub, r.Cat), new Rectangle(40, y + (RowH - 17) / 2, 17, 17), Theme.Ink2);
                        TextRenderer.DrawText(g, r.Sub, Theme.UIBold, new Rectangle(62, y, W - 126, RowH), Theme.Ink2,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                        int k = 0;
                        if (all != null) foreach (Slot sl in all) if (sl.Cat == r.Cat && sl.Sub == r.Sub) k++;
                        string num = k + " 个";
                        TextRenderer.DrawText(g, num, Theme.UISmall, new Rectangle(W - 62, y, 50, RowH), Theme.Ink3,
                            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                        // 往下连一小段，免得和位置行的竖线断开
                        if (open && k > 0) g.DrawLine(branch, 39, y + RowH / 2, 39, y + RowH);
                        continue;
                    }

                    if (r.IsNote)
                    {
                        if (i == hov) g.FillRectangle(hovBg, 0, y, W, RowH);
                        g.DrawLine(branch, 17, y + RowH / 2, 26, y + RowH / 2);
                        TextRenderer.DrawText(g, "认不出改的是什么（点开看全部 · 可手动指定）", Theme.UI,
                            new Rectangle(33, y, W - 46, RowH), Theme.Ink3,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                        continue;
                    }

                    Slot s = r.Slot;
                    if (s == null) continue;

                    // 「＋ 新建分支…」不是位置：只画一行灰字，点了弹输入框
                    if (s.Key == "#mine+new")
                    {
                        if (i == hov) g.FillRectangle(hovBg, 0, y, W, RowH);
                        g.DrawLine(branch, 39, y, 39, y + RowH / 2);
                        g.DrawLine(branch, 39, y + RowH / 2, 48, y + RowH / 2);
                        TextRenderer.DrawText(g, s.Name, Theme.UI, new Rectangle(54, y, W - 66, RowH), Theme.Ink3,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                        continue;
                    }

                    bool sel = s.Key == owner.SelectedKey;
                    if (sel) g.FillRectangle(selBg, 0, y, W, RowH);
                    else if (i == hov) g.FillRectangle(hovBg, 0, y, W, RowH);
                    if (sel) using (SolidBrush b = new SolidBrush(Theme.Mark)) g.FillRectangle(b, 0, y + 3, 2, RowH - 6);

                    // 分支线：竖线 + 一根横线（位置层缩进到小分类下面）
                    bool nextSame = (i + 1 < view.Count) && !view[i + 1].IsCat && !view[i + 1].IsSub
                                    && view[i + 1].Cat == r.Cat && view[i + 1].Sub == r.Sub;
                    int bx = 39;
                    // 子分支按层级往右缩进：父子关系一眼看得出来，不用点开猜
                    if (CatTree.IsBranchSlot(s)) bx += MyBranch.Depth(s.Name) * 12;
                    g.DrawLine(branch, bx, y, bx, y + (nextSame ? RowH : RowH / 2 + 1));
                    g.DrawLine(branch, bx, y + RowH / 2, bx + 9, y + RowH / 2);

                    // 状态点：绿=选了本体，橙=叠了好几件本体，蓝=只有扩展（音效/只改第一人称模型），灰=空
                    // 「一堆」（地图 / 我的分支）里勾好几个是正常事，别用橙色报警
                    int cnt = owner.CheckedGroups(s);
                    int ecnt = owner.CheckedExtras(s);
                    Color dot = s.Pile ? (cnt > 0 ? Theme.Ok : Theme.TreeBranch)
                              : cnt > 1 ? Theme.Warn
                              : cnt == 1 ? Theme.Ok
                              : ecnt > 0 ? Theme.Info
                              : Theme.TreeBranch;
                    using (SolidBrush b = new SolidBrush(dot)) g.FillEllipse(b, bx + 17, y + RowH / 2 - 3, 6, 6);

                    int nx = bx + 30;
                    int nameW = 86;
                    // 「步枪」小分类下又有个位置叫「步枪」= 认不出具体型号的那一堆，改个说法免得看着像重复。
                    // 空位不套这条：空位本来就叫得准（手枪 / 近战武器），改成「未分型号」反而看不懂。
                    string showName = (s.Name == s.Sub && !s.Empty) ? "未分型号" : s.Name;
                    // 空位的名字压暗一档：一眼分得出「这里还缺 MOD」和「这里有包没选」
                    TextRenderer.DrawText(g, showName, sel ? Theme.UIBold : Theme.UI,
                        new Rectangle(nx, y, nameW, RowH), s.Empty ? Theme.Ink3 : Theme.Ink,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                        | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

                    // 「扩」角标：名字后面挂一个蓝底小图标，有可叠扩展的位置不用看数字就能扫出来
                    if (s.Extras.Count > 0) DrawTag(g, nx + nameW + 4, y);

                    ModGroup chosen = owner.ChosenOf(s);
                    string sub = chosen == null ? (ecnt > 0 ? "原版" : "未选") : chosen.Name;
                    if (s.Empty) sub = "还没有 MOD";
                    else if (s.Pile)
                    {
                        sub = cnt == 0 ? "未勾 · 共 " + s.Items.Count + " 个" : "已勾 " + cnt + " 个";
                    }
                    else
                    {
                        if (cnt > 1) sub = "叠了 " + cnt + " 件";
                        if (ecnt > 0) sub += "  +" + ecnt + " 扩展";
                        else if (s.Extras.Count > 0) sub += "  ·" + s.Extras.Count + " 扩展待选";
                    }

                    // 右端数字：本体候选数；有扩展时「+N」单独用信息蓝 —— 扫一眼，带蓝色 +N 的就是有扩展可叠
                    string plus = s.Extras.Count > 0 ? "+" + s.Extras.Count : "";
                    int pw = 0;
                    if (plus.Length > 0)
                    {
                        Size sz = TextRenderer.MeasureText(g, plus, Theme.UISmall,
                            new Size(80, RowH), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                        pw = sz.Width;
                        TextRenderer.DrawText(g, plus, Theme.UISmall,
                            new Rectangle(W - 8 - pw, y, pw + 2, RowH), Theme.Info,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                            | TextFormatFlags.NoPrefix);
                    }
                    TextRenderer.DrawText(g, s.Items.Count.ToString(), Theme.UISmall,
                        new Rectangle(W - 42 - pw, y, 32, RowH), Theme.Ink3,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

                    int subX = nx + nameW + 26;      // 角标固定占一列，没角标的行文字也对得齐
                    int subW = W - subX - 46 - pw;
                    string subText = sub;
                    // 窄窗口下这一列只剩三四十像素，「还没有 MOD」会被截成「还没有 M…」，
                    // 看着像排版坏了。空位换成两个字的短说法，宁可少说一个词。
                    if (s.Empty && subW > 20)
                    {
                        Size need = TextRenderer.MeasureText(g, sub, Theme.UISmall,
                            new Size(subW, RowH), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                        if (need.Width > subW) subText = "空位";
                    }
                    if (subW > 20)
                        TextRenderer.DrawText(g, subText, Theme.UISmall, new Rectangle(subX, y, subW, RowH), Theme.Ink3,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                }
            }

            // 键盘焦点必须看得见；虚线画在整行外沿，不与“已选”左边的实条混在一起。
            if (Focused && focus >= 0 && focus < rows.Count)
            {
                int fy = focus * RowH + 4;
                using (Pen p = new Pen(Theme.Ink, 1f))
                {
                    p.DashStyle = DashStyle.Dot;
                    g.DrawRectangle(p, 1, fy + 1, Math.Max(1, W - 3), RowH - 2);
                }
            }

            // 按下还没拖动的瞬间：先把要搬的那行描出来 —— 用户按下去就知道抓住的是哪行，
            // 而不是拖了一半啥都没有
            if (pressArm && !dragging && pressRow >= 0 && pressRow < rows.Count)
            {
                int py = pressRow * RowH + 4;
                using (SolidBrush fill = new SolidBrush(Color.FromArgb(40, Theme.Info)))
                using (Pen p = new Pen(Theme.Info, 2f))
                {
                    g.FillRectangle(fill, 1, py, Math.Max(1, W - 3), RowH);
                    g.DrawRectangle(p, 1, py + 1, Math.Max(1, W - 4), RowH - 2);
                }
            }

            // 拖动中的反馈：有预览 = 树已是松手后的样子，搬的块垫淡色、插入处一条线加箭头；
            // 没预览（还没找到合法落点）= 退回旧画法：落点行铺底色、源行压暗
            if (dragging)
            {
                if (previewRows != null && pvIns >= 0)
                {
                    using (SolidBrush fill = new SolidBrush(Color.FromArgb(48, Theme.Info)))
                    using (Pen pl = new Pen(Theme.Info, 2f))
                    using (SolidBrush b = new SolidBrush(Theme.Info))
                    {
                        int y0 = pvIns * RowH + 4, y1 = pvB1 * RowH + 4 + RowH;
                        g.FillRectangle(fill, 1, y0, Math.Max(1, W - 3), y1 - y0);
                        g.DrawLine(pl, 2, y0 - 2, W - 6, y0 - 2);
                        g.FillPolygon(b, new Point[] { new Point(2, y0 - 2), new Point(9, y0 - 7), new Point(9, y0 + 3) });
                    }
                }
                else
                {
                    if (dropRow >= 0 && dropRow < rows.Count && dropMode > 0)
                    {
                        int dy = dropRow * RowH + 4;
                        Rectangle rowRc = new Rectangle(1, dy, Math.Max(1, W - 3), RowH);
                        using (SolidBrush fill = new SolidBrush(Color.FromArgb(64, Theme.Info)))
                        using (Pen p = new Pen(Theme.Info, 2f))
                        using (SolidBrush b = new SolidBrush(Theme.Info))
                        {
                            if (dropMode == 1)                    // 上半行：插到这一行前面
                            {
                                g.FillRectangle(fill, rowRc.X, rowRc.Y, rowRc.Width, rowRc.Height / 2);
                                g.DrawLine(p, 2, dy - 1, W - 6, dy - 1);
                                g.FillPolygon(b, new Point[] { new Point(2, dy - 1), new Point(9, dy - 6), new Point(9, dy + 4) });
                            }
                            else if (dropMode == 2)               // 下半行：插到这一行后面
                            {
                                int half = rowRc.Height / 2;
                                g.FillRectangle(fill, rowRc.X, dy + half, rowRc.Width, rowRc.Height - half);
                                g.DrawLine(p, 2, dy + RowH - 1, W - 6, dy + RowH - 1);
                                g.FillPolygon(b, new Point[] { new Point(2, dy + RowH - 1), new Point(9, dy + RowH - 6), new Point(9, dy + RowH + 4) });
                            }
                            else                                  // 整行：放进它下面（成为子分支）
                            {
                                g.FillRectangle(fill, rowRc);
                                g.DrawRectangle(p, 2, dy + 1, Math.Max(1, W - 8), RowH - 3);
                            }
                        }
                    }
                    // 拖起来的那一行压暗：明确「搬的是这一行」，不然拖到一半就分不清了
                    if (pressRow >= 0 && pressRow < rows.Count)
                    {
                        int sy = pressRow * RowH + 4;
                        using (SolidBrush mask = new SolidBrush(Color.FromArgb(150, Theme.TreeBg)))
                            g.FillRectangle(mask, 1, sy, Math.Max(1, W - 3), RowH);
                    }
                }
                DrawDragTag(g, W);
            }
        }

        /// <summary>拖动时跟着鼠标的小标签：写着搬的是哪个分支、松手会落到哪儿。</summary>
        private void DrawDragTag(Graphics g, int W)
        {
            string to;
            bool bad = dropMode <= 0;          // 没有合法落点：标签转红，把话说透
            if (dropRow >= 0 && dropRow < rows.Count)
            {
                TreeRow dr = rows[dropRow];
                if (dr.IsCat)
                    to = dropMode == 1 ? " → 移到「" + dr.Cat + "」前面"
                       : dropMode == 2 ? " → 移到「" + dr.Cat + "」后面" : "";
                else if (dr.Slot != null)
                {
                    string tn = dr.Slot.Name;
                    to = dropMode == 1 ? " → 插到「" + tn + "」前面"
                       : dropMode == 2 ? " → 插到「" + tn + "」后面"
                       : dropMode == 3 ? " → 放进「" + tn + "」"
                       : "";
                }
                else to = " → 松手取消";
            }
            else to = " → 松手取消";
            if (bad) to = " → 松手取消（这里放不了，看底部提示）";
            string txt = dragName + to;
            Size sz = TextRenderer.MeasureText(txt, Theme.UISmall, new Size(2000, 40),
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            int w = Math.Min(Math.Max(60, W - 12), sz.Width + 18), h = 22;
            int x = Math.Max(2, Math.Min(Math.Max(2, W - w - 4), dragPt.X + 14));
            int y = Math.Max(2, Math.Min(Math.Max(2, Height - h - 2), dragPt.Y + 10));
            Rectangle box = new Rectangle(x, y, w, h);
            using (GraphicsPath p = Theme.Round(box, 4))
            using (SolidBrush b = new SolidBrush(Theme.Surface))
            using (Pen pen = new Pen(bad ? Theme.Accent : Theme.Info, bad ? 2f : 1f))
            {
                g.FillPath(b, p);
                g.DrawPath(pen, p);
            }
            TextRenderer.DrawText(g, txt, Theme.UISmall,
                new Rectangle(box.X + 8, box.Y, box.Width - 12, box.Height), Theme.Ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        private static void DrawTri(Graphics g, int cx, int cy, bool open)
        {
            using (SolidBrush b = new SolidBrush(Theme.Ink3))
            {
                if (open)
                {
                    Point[] p = { new Point(cx - 4, cy - 2), new Point(cx + 4, cy - 2), new Point(cx, cy + 3) };
                    g.FillPolygon(b, p);
                }
                else
                {
                    Point[] p = { new Point(cx - 2, cy - 4), new Point(cx - 2, cy + 4), new Point(cx + 3, cy) };
                    g.FillPolygon(b, p);
                }
            }
        }

        /// <summary>「扩」小角标：蓝底白字，挂在名字后面，表示这个位置有可叠加的扩展（信息蓝，不是冲突红）。</summary>
        private static void DrawTag(Graphics g, int x, int y)
        {
            const int w = 17, h = 16;
            int iy = y + (RowH - h) / 2;
            using (SolidBrush b = new SolidBrush(Theme.Info))
            {
                using (GraphicsPath p = new GraphicsPath())
                {
                    int r = 3;
                    p.AddArc(x, iy, r * 2, r * 2, 180, 90);
                    p.AddArc(x + w - r * 2, iy, r * 2, r * 2, 270, 90);
                    p.AddArc(x + w - r * 2, iy + h - r * 2, r * 2, r * 2, 0, 90);
                    p.AddArc(x, iy + h - r * 2, r * 2, r * 2, 90, 90);
                    p.CloseFigure();
                    g.FillPath(b, p);
                }
            }
            TextRenderer.DrawText(g, "扩", Theme.UISmall, new Rectangle(x, iy - 1, w, h + 2), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private int IndexAt(int y)
        {
            // 绘制时第一行从 y=4 开始，命中区也必须扣除同样的顶部偏移。
            if (y < 4) return -1;
            int i = (y - 4) / RowH;
            return (i >= 0 && i < rows.Count) ? i : -1;
        }

        /// <summary>这一行能不能按住拖：大类 / 小分类 / 分支 / 位置都能，只有说明行和「一堆」不行。</summary>
        private static bool CanDragRow(TreeRow r)
        {
            if (r == null) return false;
            // 「未归类」是兜底桶，位置由 BuildRows 钉在最后，拖它没有落点 —— 干脆别给拖动的光标
            if (r.IsCat) return r.Cat != "未归类";
            if (r.IsSub) return true;
            if (r.Slot == null) return false;
            if (CatTree.IsBranchSlot(r.Slot)) return true;
            return !r.Slot.Pile;          // 「一堆」「＋ 新建分支…」这类不参与排序
        }

        /// <summary>日志里怎么称呼这一行。</summary>
        private static string RowDesc(TreeRow r)
        {
            if (r == null) return "（空）";
            if (r.IsCat) return "大类「" + r.Cat + "」";
            if (r.IsSub) return "小分类「" + r.Cat + " / " + r.Sub + "」";
            if (r.Slot != null) return "位置「" + r.Slot.Name + "」";
            return "说明行";
        }

        /// <summary>某一行属于哪个大类 / 小分类。大类标题行只给得出大类。</summary>
        private static void SubOfRow(TreeRow r, out string cat, out string sub)
        {
            cat = null; sub = null;
            if (r == null) return;
            if (r.Slot != null) { cat = r.Slot.Cat; sub = r.Slot.Sub; return; }
            if (r.IsSub) { cat = r.Cat; sub = r.Sub; return; }
            if (r.IsCat) cat = r.Cat;
        }

        /// <summary>小分类标题行的下标；只有一个分组时树里不画那行标题，返回 -1 由调用方兜底。</summary>
        internal int RowIndexOfSub(string cat, string sub)
        {
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].IsSub && rows[i].Cat == cat && rows[i].Sub == sub) return i;
            return -1;
        }

        /// <summary>小分类标题行；没有标题行（单分组大类）就退回这一段的第一行位置。</summary>
        internal int RowIndexOfSubOrFirst(string cat, string sub)
        {
            int i = RowIndexOfSub(cat, sub);
            if (i >= 0) return i;
            for (int k = 0; k < rows.Count; k++)
                if (rows[k].Slot != null && rows[k].Slot.Cat == cat && rows[k].Slot.Sub == sub) return k;
            return -1;
        }

        internal int RowIndexOfPos(string cat, string sub, string name)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                Slot s = rows[i].Slot;
                if (s != null && s.Cat == cat && s.Sub == sub && s.Name == name) return i;
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging)
            {
                DragAutoScroll(e.Y);
                dragPt = e.Location;
                // 命中按「看到的树」算：有预览时用户看到的是松手后的行序
                List<TreeRow> view = previewRows != null ? previewRows : rows;
                int p = (e.Y < 4) ? -1 : Math.Min((e.Y - 4) / RowH, view.Count - 1);
                // 悬在自己（源行 / 预览里的幽灵块）上 = 不解析，免得起拖瞬间乱跳
                bool onSelf = (previewRows == null && p == pressRow)
                    || (previewRows != null && p >= pvIns && p <= pvB1);
                int t = -1, mode = 0;
                // 目标用名字记，最后再翻译回 rows 下标（拖完 Rebuild 会换掉整个 rows）
                string tCat = null, tBr = null, tSub = null, tPos = null;
                if (p >= 0 && !onSelf)
                {
                    TreeRow vr = view[p];
                    if (dragKind == 2)
                    {
                        // 大类拖大类：整个大类的地盘都是落点，上半地盘 = 它前面，下半地盘 = 它后面
                        // （按整块高度对半分，展开的大类不再只会判「后面」）
                        int ct = p;
                        while (ct >= 0 && !view[ct].IsCat) ct--;
                        if (ct >= 0 && view[ct].Cat != "未归类"
                            && !string.Equals(view[ct].Cat, dragName, StringComparison.OrdinalIgnoreCase))
                        {
                            tCat = view[ct].Cat;
                            int be = ct;
                            while (be + 1 < view.Count && !view[be + 1].IsCat) be++;
                            int midY = 4 + (ct + (be - ct + 1) / 2) * RowH;
                            mode = e.Y < midY ? 1 : 2;
                        }
                    }
                    else if (dragKind == 3)
                    {
                        // 小分类：只能在同一个大类里换顺序。落点是小分类标题行、或它底下的位置行都算
                        string c, sb;
                        SubOfRow(vr, out c, out sb);
                        if (sb != null && string.Equals(c, dragCat, StringComparison.Ordinal)
                            && !string.Equals(sb, dragName, StringComparison.Ordinal))
                        {
                            tSub = sb;
                            mode = e.Y - 4 - p * RowH < RowH * 30 / 100 ? 1 : 2;
                        }
                    }
                    else if (dragKind == 4)
                    {
                        // 位置：落在位置行上 = 插到它前 / 后（跨小分类时顺便换过去）；
                        // 落在小分类标题上 = 放到那一段的开头 / 末尾。
                        string c, sb;
                        SubOfRow(vr, out c, out sb);
                        if (sb != null && string.Equals(c, dragCat, StringComparison.Ordinal))
                        {
                            int ry = e.Y - 4 - p * RowH;
                            bool onSlot = vr.Slot != null && !CatTree.IsBranchSlot(vr.Slot);
                            if (onSlot && string.Equals(vr.Slot.Name, dragName, StringComparison.Ordinal))
                            {
                                tSub = null;                       // 落回自己身上：不解析
                            }
                            else
                            {
                                tSub = sb;
                                if (onSlot) tPos = vr.Slot.Name;
                                mode = ry < RowH * (onSlot ? 30 : 50) / 100 ? 1 : 2;
                            }
                        }
                    }
                    else if (dragKind == 1 && vr.Slot != null && CatTree.IsBranchSlot(vr.Slot)
                        && !string.Equals(vr.Slot.Name, dragName, StringComparison.OrdinalIgnoreCase)
                        && !IsDescendantBranch(vr.Slot.Name, dragName))          // 自己的子分支接不住（成环）
                    {
                        tBr = vr.Slot.Name;
                        int ry = e.Y - 4 - p * RowH;
                        mode = ry < RowH * 30 / 100 ? 1 : ry > RowH * 70 / 100 ? 2 : 3;
                    }
                    else if (dragKind == 1 && vr.Slot != null && string.Equals(vr.Slot.Cat, "我的分支", StringComparison.Ordinal)
                        && !string.Equals(vr.Slot.Name, dragName, StringComparison.OrdinalIgnoreCase)
                        && !IsDescendantBranch(vr.Slot.Name, dragName))
                    {
                        // 分支拖到「我的分支」地盘的非分支行上：找上面最近的分支行，插到它后面
                        int bt = p;
                        while (bt >= 0 && !(view[bt].Slot != null && CatTree.IsBranchSlot(view[bt].Slot))) bt--;
                        if (bt >= 0 && !string.Equals(view[bt].Slot.Name, dragName, StringComparison.OrdinalIgnoreCase))
                        {
                            tBr = view[bt].Slot.Name;
                            mode = 2;
                        }
                    }
                    // 都没接住：吸附到视图里最近的合法落点，让随便松哪儿都有个确定的结果
                    if (mode == 0)
                    {
                        if (dragKind == 2)
                        {
                            int up = -1, down = -1;
                            for (int k = p - 1; k >= 0; k--)
                                if (view[k].IsCat && view[k].Cat != "未归类"
                                    && !string.Equals(view[k].Cat, dragName, StringComparison.OrdinalIgnoreCase)) { up = k; break; }
                            for (int k = p + 1; k < view.Count; k++)
                                if (view[k].IsCat && view[k].Cat != "未归类"
                                    && !string.Equals(view[k].Cat, dragName, StringComparison.OrdinalIgnoreCase)) { down = k; break; }
                            if (up >= 0 && (down < 0 || p - up <= down - p)) { tCat = view[up].Cat; mode = 2; }
                            else if (down >= 0) { tCat = view[down].Cat; mode = 1; }
                        }
                        else if (dragKind == 1)
                        {
                            int up = -1, down = -1;
                            for (int k = p - 1; k >= 0; k--)
                                if (view[k].Slot != null && CatTree.IsBranchSlot(view[k].Slot)
                                    && !string.Equals(view[k].Slot.Name, dragName, StringComparison.OrdinalIgnoreCase)) { up = k; break; }
                            for (int k = p + 1; k < view.Count; k++)
                                if (view[k].Slot != null && CatTree.IsBranchSlot(view[k].Slot)
                                    && !string.Equals(view[k].Slot.Name, dragName, StringComparison.OrdinalIgnoreCase)) { down = k; break; }
                            if (up >= 0 && (down < 0 || p - up <= down - p)) { tBr = view[up].Slot.Name; mode = 2; }
                            else if (down >= 0) { tBr = view[down].Slot.Name; mode = 1; }
                        }
                        else
                        {
                            // 小分类 / 位置：吸附到同一个大类里最近的一段，别让「松在空白处」变成没反应
                            for (int k = p; k >= 0 && mode == 0; k--)
                            {
                                string c, sb;
                                SubOfRow(view[k], out c, out sb);
                                if (sb == null || !string.Equals(c, dragCat, StringComparison.Ordinal)) continue;
                                if (dragKind == 4 && view[k].Slot != null && !CatTree.IsBranchSlot(view[k].Slot))
                                {
                                    if (string.Equals(view[k].Slot.Name, dragName, StringComparison.Ordinal)) continue;
                                    tPos = view[k].Slot.Name;
                                }
                                tSub = sb; mode = 2;
                            }
                            for (int k = p + 1; k < view.Count && mode == 0; k++)
                            {
                                string c, sb;
                                SubOfRow(view[k], out c, out sb);
                                if (sb == null || !string.Equals(c, dragCat, StringComparison.Ordinal)) continue;
                                if (dragKind == 4 && view[k].Slot != null && !CatTree.IsBranchSlot(view[k].Slot))
                                {
                                    if (string.Equals(view[k].Slot.Name, dragName, StringComparison.Ordinal)) continue;
                                    tPos = view[k].Slot.Name;
                                }
                                tSub = sb; mode = 1;
                            }
                        }
                    }
                }
                // 原地不动 → 翻到目标的另一侧：拖紧邻的邻居本来就在那个位置，
                // 用户要的是「换位置」，直接对调才看得出挪动（用户实测踩了两次）
                if (mode != 0)
                {
                    if (dragKind == 2)
                    {
                        List<string> cur = CatPrefs.Cats();
                        int ci = cur.IndexOf(dragName), ti = cur.IndexOf(tCat);
                        if (ci >= 0 && ti >= 0 && ((mode == 2 && ci == ti + 1) || (mode == 1 && ci == ti - 1)))
                            mode = mode == 2 ? 1 : 2;
                    }
                    else if (dragKind == 1)
                    {
                        List<string> cur = MyBranch.Names();
                        int ci = cur.IndexOf(dragName), ti = cur.IndexOf(tBr);
                        if (ci >= 0 && ti >= 0 && ((mode == 2 && ci == ti + 1) || (mode == 1 && ci == ti - 1)))
                            mode = mode == 2 ? 1 : 2;
                    }
                    else if (dragKind == 3)
                    {
                        List<string> cur = owner.CurrentSubs(dragCat);
                        int ci = cur.IndexOf(dragName), ti = cur.IndexOf(tSub);
                        if (ci >= 0 && ti >= 0 && ((mode == 2 && ci == ti + 1) || (mode == 1 && ci == ti - 1)))
                            mode = mode == 2 ? 1 : 2;
                    }
                    else if (dragKind == 4)
                    {
                        // 同一个小分类里拖紧邻的邻居，本来就在那个位置 → 翻到另一侧才对调得动
                        if (!string.IsNullOrEmpty(tPos))
                        {
                            List<string> cur = owner.CurrentPositions(dragCat, tSub);
                            int ci = cur.IndexOf(dragName), ti = cur.IndexOf(tPos);
                            if (ci >= 0 && ti >= 0 && ((mode == 2 && ci == ti + 1) || (mode == 1 && ci == ti - 1)))
                                mode = mode == 2 ? 1 : 2;
                        }
                    }
                }
                // 名字 → rows 下标（松手落位、小标签都吃 rows 空间）
                if (mode != 0)
                {
                    if (dragKind == 2) t = RowIndexOfCat(tCat);
                    else if (dragKind == 1) t = RowIndexOfBranch(tBr);
                    else if (dragKind == 3) t = RowIndexOfSubOrFirst(dragCat, tSub);
                    else t = string.IsNullOrEmpty(tPos)
                        ? RowIndexOfSubOrFirst(dragCat, tSub)
                        : RowIndexOfPos(dragCat, tSub, tPos);
                    if (t < 0) mode = 0;
                }
                if (t != dropRow || mode != dropMode)
                {
                    dropRow = t; dropMode = mode;
                    previewRows = null; pvIns = -1; pvB1 = -1;
                    if (mode > 0)
                        BuildPreview(dragKind, tCat, tSub, tPos, tBr, mode);
                }
                Invalidate();                 // 小标签跟着手走，整块重绘最省事
                return;
            }
            // 按住分支行 / 大类标题行挪开几个像素 = 开始拖动（不是点击）
            if (pressArm && pressRow >= 0 && pressRow < rows.Count
                && (e.Button & MouseButtons.Left) == MouseButtons.Left
                && Math.Abs(e.X - pressPt.X) + Math.Abs(e.Y - pressPt.Y) >= 4)
            {
                dragging = true;
                TreeRow src = rows[pressRow];
                dragKind = src.IsCat ? 2 : src.IsSub ? 3 : CatTree.IsBranchSlot(src.Slot) ? 1 : 4;
                dragCat = src.IsCat || src.IsSub ? src.Cat : src.Slot.Cat;
                dragSub = src.IsSub ? src.Sub : (src.Slot != null ? src.Slot.Sub : "");
                dragName = src.IsCat ? src.Cat : src.IsSub ? src.Sub : src.Slot.Name;
                dragPt = e.Location;
                dropRow = pressRow;
                dropMode = 0;
                Cursor = Cursors.SizeAll;
                tip.RemoveAll();
                tipRow = -2;
                Core.Log("INFO", "DRAGIN 开始拖动 " + RowDesc(src) + "（kind=" + dragKind + "）");
                if (dragKind == 1 && MyBranch.Names().Count < 2)
                    owner.KeepFoot("「我的分支」里只有这一个分支，拖它没有可去的位置 —— 想试拖动，先右键空白处新建一个分支，或者拖上面的大类试试");
                Invalidate();
                return;
            }
            // 诊断：按住还没拖成时，每 ~150ms 记一次真实移动 —— 真机输入到没到，一看便知
            if (pressArm && !dragging && (e.Button & MouseButtons.Left) == MouseButtons.Left)
            {
                int now = Environment.TickCount;
                if (now - lastMoveLog >= 150)
                {
                    lastMoveLog = now;
                    Core.Log("INFO", "DRAGIN 按住未起拖 位移="
                        + (Math.Abs(e.X - pressPt.X) + Math.Abs(e.Y - pressPt.Y)) + " 键=" + e.Button);
                }
            }
            int i = IndexAt(e.Y);
            if (i != hover)
            {
                hover = i;
                // 悬停在可拖行上时给个十字光标：不用按下去就知道这儿能拖
                bool canDrag = i >= 0 && i < rows.Count && CanDragRow(rows[i]);
                Cursor = canDrag ? Cursors.SizeAll : Cursors.Hand;
                Invalidate(); UpdateTip(i);
            }
            base.OnMouseMove(e);
        }

        /// <summary>拖到上下边缘时自动滚动，不然长树里搬东西得先松手、滚一屏、再拖一次。</summary>
        private void DragAutoScroll(int y)
        {
            ScrollHost p = Parent as ScrollHost;
            if (p == null || !p.CanScroll) return;
            if (y < RowH / 2) p.ScrollTo(p.Offset - RowH);
            else if (y > Height - RowH / 2) p.ScrollTo(p.Offset + RowH);
        }

        // 悬停说明：这行有几个本体候选、有没有可叠的扩展、有没有组合装。
        // 右端的「+2」再显眼也得有个地方讲清它是什么意思，就放这里。
        private void UpdateTip(int i)
        {
            if (i == tipRow) return;
            tipRow = i;
            string t = "";
            if (i >= 0 && i < rows.Count)
            {
                Slot s = rows[i].Slot;
                if (s != null && s.Empty)
                {
                    t = "这个位置还没有 MOD\r\n把 vpk 放进 MOD 库（或游戏 addons），重新扫描就会出现在这里";
                }
                else if (s != null)
                {
                    t = "本体候选 " + s.Items.Count + " 个";
                    if (s.Extras.Count > 0)
                    {
                        string names = "";
                        foreach (ModGroup g in s.Extras)
                            names += (names.Length > 0 ? "、" : "") + g.Name;
                        t += "，可叠扩展 " + s.Extras.Count + " 个（开火音效 / 只改第一人称模型这类，不换本体，可多开）\r\n扩展：" + names;
                    }
                    else t += "，没有可叠的扩展";
                    bool hasSet = false;
                    foreach (ModGroup g in s.Items) if (g.IsSet) { hasSet = true; break; }
                    if (hasSet) t += "\r\n候选里有组合装：双击那行可挑部件（材质 / 颜色）";
                }
            }
            if (t.Length == 0) tip.RemoveAll(); else tip.SetToolTip(this, t);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (!dragging && !pressArm) Cursor = Cursors.Hand;
            if (hover != -1) { hover = -1; Invalidate(); }
            tipRow = -2;
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int i = IndexAt(e.Y);
            if (i < 0)
            {
                // 空白处也能出菜单：建分支不该要求先滚到「我的分支」那几行
                if (e.Button == MouseButtons.Right) owner.TreeMenuFor(null, PointToScreen(e.Location));
                return;
            }
            TreeRow r = rows[i];
            Focus();
            focus = i;
            Invalidate();
            // 右键：分支的重命名 / 解散 / 新建子分支，以及任意地方「新建分支…」
            if (e.Button == MouseButtons.Right)
            {
                owner.TreeMenuFor(r, PointToScreen(e.Location));
                return;
            }
            if (e.Button == MouseButtons.Left)
            {
                // 记下起点：大类 / 小分类 / 分支 / 位置行都能接着拖，别的行（「未归类」说明行）照旧只当点击
                pressArm = CanDragRow(r);
                pressRow = pressArm ? i : -1;
                pressPt = e.Location;
                // 激活（折叠 / 选中）推迟到 MouseUp：按下就做的话行会立刻重建，拖动根本起不来
                if (!pressArm) ActivateRow(i);
                // 可拖行按下立刻高亮（OnPaint 里画），并把真实输入记进日志：
                // 有用户反馈「拖不动又看不出抓没抓住」，这两样是远程定位的唯一手段
                if (pressArm)
                {
                    Core.Log("INFO", "DRAGIN 按下 行=" + i + " " + RowDesc(r) + " 键=" + e.Button);
                    Cursor = Cursors.SizeAll;
                    InvalidateRow(i);
                }
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (dragging)
            {
                // 先把落点抄下来：ApplyDrop 里会 Rebuild，rows 整个换掉
                string nm = dragName, fcat = dragCat, fsub = dragSub;
                int kind = dragKind, row = dropRow, mode = dropMode;
                ResetDrag();
                Core.Log("INFO", "DRAGIN 松手 「" + nm + "」 落点行=" + row + " 模式=" + mode + " kind=" + kind);
                if (!ApplyDrop(nm, kind, row, mode, fcat, fsub))
                    owner.KeepFoot(kind == 2
                        ? "没挪动：拖到别的大类的地盘上（上半 = 移到它前面，下半 = 移到它后面）"
                        : kind == 3
                        ? "没挪动：小分类只能在同一大类里换顺序（拖到另一个小分类的上 / 下半）"
                        : kind == 4
                        ? "没挪动：位置拖到另一个位置的上 / 下半换顺序；拖到别的小分类标题上 = 放进那一段"
                        : "没挪动：拖到另一个分支行附近（上半 / 下半 = 换顺序，中间 = 变成子分支）");
                return;
            }
            // 没拖成 = 普通的一下点击：折叠 / 展开 / 选中，走的还是同一套激活逻辑
            if (pressArm && pressRow >= 0 && pressRow < rows.Count)
                ActivateRow(pressRow);
            if (pressArm) { Cursor = Cursors.Hand; InvalidateRow(pressRow); }   // 撤掉按下高亮
            pressArm = false;
            pressRow = -1;
            base.OnMouseUp(e);
        }

        /// <summary>
        /// 松手落位：按落点行和模式把分支挪过去。上 30% / 下 30% = 插到这一行前 / 后，中间 = 变成它的子分支。
        /// 鼠标松手和自检走的是同一条路 —— 免得「手能拖、测不到」。
        /// </summary>
        internal bool ApplyDrop(string name, int row, int mode)
        {
            return ApplyDrop(name, 1, row, mode);
        }

        /// <summary>rows 空间里某个大类标题行的下标（没有返回 -1）。</summary>
        private int RowIndexOfCat(string cat)
        {
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].IsCat && rows[i].Cat == cat) return i;
            return -1;
        }

        /// <summary>rows 空间里某个分支行的下标（没有返回 -1）。</summary>
        private int RowIndexOfBranch(string name)
        {
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].Slot != null && CatTree.IsBranchSlot(rows[i].Slot) && rows[i].Slot.Name == name) return i;
            return -1;
        }

        /// <summary>分支 name 是不是 anc 的后代（防成环、防拖进自己的地盘）。</summary>
        private static bool IsDescendantBranch(string name, string anc)
        {
            string par = MyBranch.ParentOf(name);
            int guard = 0;
            while (par.Length > 0 && guard++ < 32)
            {
                if (par == anc) return true;
                par = MyBranch.ParentOf(par);
            }
            return false;
        }

        private static int PreviewRowOfCat(List<TreeRow> pv, string cat)
        {
            for (int i = 0; i < pv.Count; i++) if (pv[i].IsCat && pv[i].Cat == cat) return i;
            return -1;
        }

        private static int PreviewRowOfBranch(List<TreeRow> pv, string name)
        {
            for (int i = 0; i < pv.Count; i++)
                if (pv[i].Slot != null && CatTree.IsBranchSlot(pv[i].Slot) && pv[i].Slot.Name == name) return i;
            return -1;
        }

        private static int PreviewRowOfSub(List<TreeRow> pv, string cat, string sub)
        {
            for (int i = 0; i < pv.Count; i++)
                if (pv[i].IsSub && pv[i].Cat == cat && pv[i].Sub == sub) return i;
            for (int i = 0; i < pv.Count; i++)
                if (pv[i].Slot != null && pv[i].Slot.Cat == cat && pv[i].Slot.Sub == sub) return i;
            return -1;
        }

        private static int PreviewRowOfPos(List<TreeRow> pv, string cat, string sub, string name)
        {
            for (int i = 0; i < pv.Count; i++)
            {
                Slot s = pv[i].Slot;
                if (s != null && s.Cat == cat && s.Sub == sub && s.Name == name) return i;
            }
            return -1;
        }

        /// <summary>
        /// 拖动预览：把 rows 复制一份，把搬的块（大类整块地盘 / 小分类整段 / 分支连同子分支 / 单个位置）
        /// 从原地取出、插到目标位置 —— 树画出来就是松手后的样子。失败（目标不在了）就退回普通画法。
        /// </summary>
        private void BuildPreview(int kind, string tCat, string tSub, string tPos, string tBr, int mode)
        {
            try
            {
                previewRows = null; pvIns = -1; pvB1 = -1;
                int b0;
                if (kind == 2) b0 = RowIndexOfCat(dragName);
                else if (kind == 3) b0 = RowIndexOfSub(dragCat, dragName);
                else if (kind == 1) b0 = RowIndexOfBranch(dragName);
                else b0 = RowIndexOfPos(dragCat, dragSub, dragName);
                if (b0 < 0) return;

                int n = 1;
                while (b0 + n < rows.Count)
                {
                    TreeRow x = rows[b0 + n];
                    if (kind == 4) break;                                   // 位置只搬一行
                    if (x.IsCat || x.IsNote) break;
                    if (kind == 3 && x.IsSub) break;
                    if (kind == 1 && !(x.Slot != null && CatTree.IsBranchSlot(x.Slot)
                        && IsDescendantBranch(x.Slot.Name, dragName))) break;
                    n++;
                }

                List<TreeRow> pv = new List<TreeRow>(rows);
                List<TreeRow> block = pv.GetRange(b0, n);
                pv.RemoveRange(b0, n);

                int ti;
                if (kind == 2) ti = PreviewRowOfCat(pv, tCat);
                // 小分类 / 位置：落点一定和源在同一个大类里，用 dragCat 找（tCat 只在拖大类时有值）
                else if (kind == 3) ti = PreviewRowOfSub(pv, dragCat, tSub);
                else if (kind == 4) ti = string.IsNullOrEmpty(tPos)
                    ? PreviewRowOfSub(pv, dragCat, tSub)
                    : PreviewRowOfPos(pv, dragCat, tSub, tPos);
                else ti = PreviewRowOfBranch(pv, tBr);
                if (ti < 0) return;

                int ins;
                if (mode == 1) ins = ti;
                else if (mode == 3) ins = ti + 1;                            // 变成第一个子分支
                else if (kind == 4 && !string.IsNullOrEmpty(tPos)) ins = ti + 1;
                else
                {
                    ins = ti + 1;                                            // 目标那一整块之后
                    while (ins < pv.Count)
                    {
                        TreeRow x = pv[ins];
                        if (x.IsCat || x.IsNote) break;
                        if (kind != 2 && x.IsSub) break;
                        if (kind == 1 && !(x.Slot != null && CatTree.IsBranchSlot(x.Slot)
                            && IsDescendantBranch(x.Slot.Name, tBr))) break;
                        ins++;
                    }
                }
                pv.InsertRange(ins, block);
                previewRows = pv;
                pvIns = ins;
                pvB1 = ins + n - 1;
            }
            catch { previewRows = null; pvIns = -1; pvB1 = -1; }
        }

        internal bool ApplyDrop(string name, int kind, int row, int mode, string fromCat = null, string fromSub = null)
        {
            try
            {
                if (string.IsNullOrEmpty(name) || row < 0 || row >= rows.Count || mode <= 0)
                {
                    Core.Log("INFO", "DRAGAPPLY 拒绝 「" + name + "」 行=" + row + " 模式=" + mode + "（行号/模式无效）");
                    return false;
                }
                TreeRow tr = rows[row];
                Core.Log("INFO", "DRAGAPPLY 「" + name + "」 → 行=" + row
                    + (tr.IsCat ? " 大类「" + tr.Cat + "」" : " 行「" + (tr.Slot != null ? tr.Slot.Name : "?") + "」")
                    + " 模式=" + mode);
                if (kind == 2)
                {
                    // 大类：只能插到别的大类前 / 后
                    if (!tr.IsCat || string.Equals(tr.Cat, name, StringComparison.OrdinalIgnoreCase))
                    {
                        Core.Log("FAIL", "DRAGAPPLY 大类落点无效：行 " + row + " 不是别的大类标题行");
                        return false;
                    }
                    bool ok = owner.MoveCatTo(name, tr.Cat, mode == 2);
                    Core.Log(ok ? "INFO" : "FAIL", "DRAGAPPLY 大类「" + name + "」挪到「" + tr.Cat + "」" + (mode == 2 ? "后面" : "前面") + (ok ? " 完成" : " 被 CatPrefs 拒绝"));
                    return ok;
                }
                Slot t = tr.Slot;
                if (kind == 3)
                {
                    // 小分类换顺序：落点必须是同一个大类里的行
                    string c, sb;
                    SubOfRow(tr, out c, out sb);
                    if (sb == null || !string.Equals(c, fromCat, StringComparison.Ordinal))
                    {
                        Core.Log("FAIL", "DRAGAPPLY 小分类落点无效：行 " + row + " 不在「" + fromCat + "」里");
                        return false;
                    }
                    if (string.Equals(sb, name, StringComparison.Ordinal)) { Core.Log("INFO", "DRAGAPPLY 小分类落点是自己 → 取消"); return false; }
                    return owner.MoveSubTo(fromCat, name, sb, mode == 2);
                }
                if (kind == 4)
                {
                    // 位置换顺序 / 换小分类：落点是小分类标题行（放到那一段头/尾）或某个位置行
                    string c, sb;
                    SubOfRow(tr, out c, out sb);
                    if (sb == null || !string.Equals(c, fromCat, StringComparison.Ordinal))
                    {
                        Core.Log("FAIL", "DRAGAPPLY 位置落点无效：行 " + row + " 不在「" + fromCat + "」里");
                        return false;
                    }
                    bool onSlot = tr.Slot != null && !CatTree.IsBranchSlot(tr.Slot);
                    string tPos = onSlot ? tr.Slot.Name : null;
                    return owner.MovePosTo(fromCat, fromSub, name, sb, tPos, mode == 2);
                }
                if (t == null || !CatTree.IsBranchSlot(t))
                {
                    Core.Log("FAIL", "DRAGAPPLY 分支落点无效：行 " + row + " 不是分支行");
                    return false;
                }
                if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    Core.Log("INFO", "DRAGAPPLY 分支落点是自己 → 取消");
                    return false;
                }
                string parent = mode == 3 ? t.Name : MyBranch.ParentOf(t.Name);
                string before = mode == 1 ? t.Name : mode == 2 ? MyBranch.NextSibling(t.Name) : "";
                bool okb = owner.MoveBranch(name, parent, before);
                Core.Log(okb ? "INFO" : "FAIL", "DRAGAPPLY 分支「" + name + "」→ 父=" + (parent.Length == 0 ? "(顶层)" : parent) + " 前插=" + before + (okb ? " 完成" : " 被拒绝"));
                return okb;
            }
            catch (Exception ex)
            {
                Core.Log("FAIL", "DRAGAPPLY 异常：" + ex.GetType().Name + " " + ex.Message + " @ " + ex.StackTrace);
                return false;
            }
        }

        /// <summary>自检用：某个分支在树里是第几行（没有返回 -1）。</summary>
        internal int RowIndexOfSlot(string key)
        {
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].Slot != null && rows[i].Slot.Key == key) return i;
            return -1;
        }

        /// <summary>自检用：第 i 行是哪个槽位（标题行返回 null）。</summary>
        internal Slot RowSlot(int i)
        {
            return (i >= 0 && i < rows.Count) ? rows[i].Slot : null;
        }

        /// <summary>截图专用：摆成「正拖着某个分支、落点在第 row 行、模式 mode」的样子，不动数据。</summary>
        internal void TestDragVisual(string name, int row, int mode, Point at)
        {
            dragging = true;
            dragName = name ?? "";
            dragKind = 1;
            pressRow = row;
            dropRow = row;
            dropMode = mode;
            dragPt = at;
            pressArm = false;
            previewRows = null; pvIns = -1; pvB1 = -1;
            if (row >= 0 && row < rows.Count && rows[row].Slot != null && CatTree.IsBranchSlot(rows[row].Slot))
                BuildPreview(1, null, null, null, rows[row].Slot.Name, mode);   // 截图也走新预览画法
            Cursor = Cursors.SizeAll;
            Invalidate();
        }

        /// <summary>截图专用：摆成「正拖着某个大类、落点在第 row 行」的样子，不动数据。</summary>
        internal void TestCatDragVisual(string name, int row, int mode, Point at)
        {
            dragging = true;
            dragName = name ?? "";
            dragKind = 2;
            pressRow = row;
            dropRow = row;
            dropMode = mode;
            dragPt = at;
            pressArm = false;
            previewRows = null; pvIns = -1; pvB1 = -1;
            if (row >= 0 && row < rows.Count && rows[row].IsCat)
                BuildPreview(2, rows[row].Cat, null, null, null, mode);      // 截图也走新预览画法
            Cursor = Cursors.SizeAll;
            Invalidate();
        }

        internal void TestDragEnd() { ResetDrag(); }

        /// <summary>
        /// 自检用：按真实鼠标事件路径模拟一次拖动（OnMouseDown → 挪 6px → 挪到落点 → OnMouseUp）。
        /// 直接调受保护的 On* 方法，和真鼠标走的是同一段代码 —— 手能拖、这个必须过；手拖不动、这个能复现。
        /// part：1=落点行上半（插前） 2=下半（插后） 3=中间（放进）。
        /// </summary>
        internal bool SimulateRealDrag(int fromRow, int toRow, int part, out string steps)
        {
            steps = "";
            if (fromRow < 0 || fromRow >= rows.Count || toRow < 0 || toRow >= rows.Count)
            { steps = "行号越界 from=" + fromRow + " to=" + toRow; return false; }
            int fromY = fromRow * RowH + 4 + RowH / 2;
            int ty = toRow * RowH + 4 + (part == 1 ? 4 : part == 2 ? RowH - 4 : RowH / 2);
            const int x = 30;
            OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, x, fromY, 0));
            steps += "down: pressArm=" + pressArm + " pressRow=" + pressRow + "; ";
            OnMouseMove(new MouseEventArgs(MouseButtons.Left, 1, x, fromY + 3, 0));
            steps += "move3: dragging=" + dragging + "; ";
            OnMouseMove(new MouseEventArgs(MouseButtons.Left, 1, x, fromY + 6, 0));
            steps += "move6: dragging=" + dragging + " kind=" + dragKind + " name=" + dragName + "; ";
            OnMouseMove(new MouseEventArgs(MouseButtons.Left, 1, x, ty, 0));
            steps += "to: dropRow=" + dropRow + " dropMode=" + dropMode
                + " pv=" + (previewRows != null ? previewRows.Count : -1) + "/" + rows.Count + "; ";
            OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, x, ty, 0));
            steps += "up: dragging=" + dragging;
            return true;
        }

        private void ResetDrag()
        {
            dragging = false;
            pressArm = false;
            pressRow = -1;
            dropRow = -1;
            dropMode = 0;
            dragKind = 0;
            dragName = "";
            previewRows = null;
            pvIns = -1;
            pvB1 = -1;
            Cursor = Cursors.Hand;
            Invalidate();
        }

        protected override void OnGotFocus(EventArgs e)
        {
            if (focus < 0)
            {
                for (int i = 0; i < rows.Count; i++)
                    if (rows[i].Slot != null && rows[i].Slot.Key == owner.SelectedKey) { focus = i; break; }
                if (focus < 0 && rows.Count > 0) focus = 0;
            }
            Invalidate();
            base.OnGotFocus(e);
        }

        protected override void OnLostFocus(EventArgs e)
        {
            Invalidate();
            base.OnLostFocus(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            if (dragging && k == Keys.Escape) return true;      // 拖到一半按 Esc 取消（平时 Esc 照旧交给窗口）
            if (k == Keys.Up || k == Keys.Down || k == Keys.Left || k == Keys.Right
                || k == Keys.PageUp || k == Keys.PageDown || k == Keys.Home || k == Keys.End
                || k == Keys.Enter || k == Keys.Space) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (dragging && e.KeyCode == Keys.Escape) { ResetDrag(); e.Handled = true; return; }
            if (rows.Count == 0) { base.OnKeyDown(e); return; }
            if (focus < 0 || focus >= rows.Count) focus = 0;

            Keys k = e.KeyCode;
            if (k == Keys.Up) MoveFocus(focus - 1);
            else if (k == Keys.Down) MoveFocus(focus + 1);
            else if (k == Keys.PageUp) MoveFocus(focus - Math.Max(1, Parent.ClientSize.Height / RowH));
            else if (k == Keys.PageDown) MoveFocus(focus + Math.Max(1, Parent.ClientSize.Height / RowH));
            else if (k == Keys.Home) MoveFocus(0);
            else if (k == Keys.End) MoveFocus(rows.Count - 1);
            else if (k == Keys.Enter || k == Keys.Space) ActivateRow(focus);
            else if (k == Keys.Left || k == Keys.Right)
            {
                TreeRow r = rows[focus];
                bool open = r.IsCat ? !owner.IsFolded(r.Cat)
                          : r.IsSub ? !owner.IsSubFolded(r.Cat, r.Sub)
                          : false;
                // 注意：SetFolded 的第二个参数是「折叠」，不是「展开」。
                // 当前是展开的（open=true）要折叠 → 传 true。以前写成 !open，等于每次都在写回原值，
                // 按左右键看着像没反应（键盘自测就是这么抓出来的）。
                if (r.IsCat && ((k == Keys.Left && open) || (k == Keys.Right && !open)))
                    owner.SetFolded(r.Cat, open);
                else if (r.IsSub && ((k == Keys.Left && open) || (k == Keys.Right && !open)))
                    owner.SetSubFolded(r.Cat, r.Sub, open);
            }
            else { base.OnKeyDown(e); return; }

            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        private void MoveFocus(int i)
        {
            if (rows.Count == 0) return;
            int old = focus;
            focus = Math.Max(0, Math.Min(rows.Count - 1, i));
            if (old != focus)
            {
                InvalidateRow(old);
                InvalidateRow(focus);
            }
            EnsureRowVisible(focus);
        }

        private void InvalidateRow(int i)
        {
            if (i >= 0 && i < rows.Count) Invalidate(new Rectangle(0, i * RowH, Width, RowH + 4));
        }

        private void EnsureRowVisible(int i)
        {
            ScrollHost p = Parent as ScrollHost;
            if (p == null || !p.CanScroll) return;
            int y = i * RowH;
            int h = p.ClientSize.Height;
            int top = p.Offset;
            int bottom = top + h;
            int v = top;
            if (y < top + 4) v = Math.Max(0, y - RowH);
            else if (y + RowH > bottom - 4) v = y - h + RowH * 2;
            if (v == top) return;
            p.ScrollTo(v);
        }

        private void ActivateRow(int i)
        {
            if (i < 0 || i >= rows.Count) return;
            TreeRow r = rows[i];
            if (r.IsCat) owner.SetFolded(r.Cat, !owner.IsFolded(r.Cat));
            else if (r.IsSub) owner.SetSubFolded(r.Cat, r.Sub, !owner.IsSubFolded(r.Cat, r.Sub));
            else if (r.IsNote) owner.SelectUnmatched();
            else if (r.Slot != null) owner.SelectSlot(r.Slot.Key);
        }

        // 自测钩子：验证焦点移动和 Enter 激活走的是同一套键盘路径。
        internal bool FocusedRowIsSlot
        {
            get { return focus >= 0 && focus < rows.Count && rows[focus].Slot != null; }
        }
        internal bool FocusedRowIsCat
        {
            get { return focus >= 0 && focus < rows.Count && rows[focus].IsCat; }
        }
        internal bool FocusedRowIsSub
        {
            get { return focus >= 0 && focus < rows.Count && rows[focus].IsSub; }
        }
        internal string FocusedSlotKey
        {
            get { return FocusedRowIsSlot ? rows[focus].Slot.Key : ""; }
        }
        internal void TestKey(Keys k)
        {
            OnKeyDown(new KeyEventArgs(k));
        }

        /// <summary>把某个位置滚进可视区（搜索跳转、定位用）。</summary>
        public void ScrollToSlot(string key)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Slot == null || rows[i].Slot.Key != key) continue;
                ScrollHost p = Parent as ScrollHost;
                if (p == null || !p.CanScroll) return;
                int y = i * RowH;
                int h = p.ClientSize.Height;
                int top = p.Offset;
                if (y >= top && y + RowH <= top + h) return;              // 已经在视野里
                p.ScrollTo(Math.Max(0, y - h / 3));
                return;
            }
        }

        /// <summary>自检 / 拖动大类后用：把第 i 行滚进视野（-1 忽略）。</summary>
        public void ScrollToRow(int i)
        {
            if (i < 0 || i >= rows.Count) return;
            ScrollHost p = Parent as ScrollHost;
            if (p == null || !p.CanScroll) return;
            int y = i * RowH;
            int h = p.ClientSize.Height;
            int top = p.Offset;
            if (y >= top && y + RowH <= top + h) return;              // 已经在视野里
            p.ScrollTo(Math.Max(0, y - h / 3));
        }

        /// <summary>第 i 行是不是大类标题行。</summary>
        internal bool RowIsCat(int i) { return i >= 0 && i < rows.Count && rows[i].IsCat; }

        /// <summary>第 i 行的大类名（不是大类行返回空串）。</summary>
        internal string RowCat(int i) { return RowIsCat(i) ? rows[i].Cat : ""; }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            ScrollHost p = Parent as ScrollHost;
            if (p != null && p.CanScroll)
            {
                p.ScrollTo(p.Offset - e.Delta);
                return;
            }
            base.OnMouseWheel(e);
        }
    }
}

namespace Installer
{
    public partial class MainForm
    {
        /// <summary>自测：切到方案树，只走键盘路径跑一遍。返回不通过条数。</summary>
        internal int UiTestKeys()
        {
            if (viewHost == null) { Core.Log("FAIL", "KEY 没有 viewHost"); return 1; }
            ShowTree();
            Application.DoEvents();
            return treeView.SelfTestKeys();
        }

        /// <summary>
        /// 截图专用：把树摆成「正拖着某个分支」的样子（落点选它下面第一个分支行），看完调 UiTestDragEnd 收掉。
        /// 只改绘制状态，不动任何数据 —— 拖拽的画面对不对，靠这个看。
        /// </summary>
        internal void UiTestDrag(string branchName)
        {
            if (viewHost == null) { Core.Log("FAIL", "DRAG 没有 viewHost"); return; }
            ShowTree();
            Application.DoEvents();
            Slot s = treeView.SlotByName(branchName);
            if (s == null || !CatTree.IsBranchSlot(s)) { Core.Log("WARN", "DRAG 没找到分支: " + branchName); return; }
            treeView.SelectSlot(s.Key, true);
            Application.DoEvents();
            SlotTree body = treeView.Body;
            int from = body.RowIndexOfSlot(s.Key);
            int to = -1;
            for (int i = from + 1; i < body.RowCount; i++)
            {
                Slot t = body.RowSlot(i);
                if (t != null && CatTree.IsBranchSlot(t)) { to = i; break; }
            }
            if (from < 0 || to < 0) { Core.Log("WARN", "DRAG 旁边没有别的分支行，画不出落点"); return; }
            body.TestDragVisual(s.Name, to, 3, new Point(120, to * 28 + 20));
            Core.Log("INFO", "DRAG 预览：「" + s.Name + "」拖到第 " + to + " 行（成为子分支）");
        }

        /// <summary>截图收尾：把拖动状态清掉，免得留在真界面上。</summary>
        internal void UiTestDragEnd() { if (treeView != null) treeView.Body.TestDragEnd(); }

        /// <summary>截图用：摆成「正拖着某大类、落点在另一大类行」的样子，不动数据。</summary>
        internal void UiTestCatDrag(string catName, string ontoCat)
        {
            if (treeView == null) { Core.Log("FAIL", "DRAGCAT 没有 viewHost"); return; }
            ShowTree();
            Application.DoEvents();
            int from = -1, to = -1;
            for (int i = 0; i < treeView.Body.RowCount; i++)
            {
                if (!treeView.Body.RowIsCat(i)) continue;
                string c = treeView.Body.RowCat(i);
                if (string.Equals(c, catName, StringComparison.Ordinal)) from = i;
                else if (string.Equals(c, ontoCat, StringComparison.Ordinal)) to = i;
            }
            if (from < 0 || to < 0) { Core.Log("WARN", "DRAGCAT 没找到大类: " + catName + " / " + ontoCat); return; }
            treeView.Body.TestCatDragVisual(catName, to, 2, new Point(120, to * 28 + 20));
            Core.Log("INFO", "DRAGCAT 预览：「" + catName + "」拖到「" + ontoCat + "」后面");
        }

        /// <summary>
        /// 自检：用真实鼠标事件路径拖一次（区别于 UiTestCatDrag 的画图预览，这里真挪数据）。
        /// 自动识别 fromName 是大类还是分支；做完把顺序还原，不污染用户数据。
        /// </summary>
        internal void UiTestRealDrag(string fromName, string toName)
        {
            if (treeView == null) { Core.Log("FAIL", "DRAGREAL 没有 viewHost"); return; }
            ShowTree();
            Application.DoEvents();

            int from, to; bool isCat;
            if (!FindDragRows(fromName, toName, out from, out to, out isCat)) return;

            List<string> before = isCat ? CatPrefs.Cats() : MyBranch.Names();
            string steps;
            treeView.Body.SimulateRealDrag(from, to, isCat ? 2 : 3, out steps);
            Core.Log("INFO", "DRAGREAL 路径 " + steps);
            CheckAndRestoreDrag(fromName, toName, isCat, before, "DRAGREAL");
            treeView.Rebuild();
        }

        /// <summary>找拖动起点 / 落点行：大类按大类行找，分支按分支槽位找。</summary>
        private bool FindDragRows(string fromName, string toName, out int from, out int to, out bool isCat)
        {
            from = -1; to = -1; isCat = false;
            for (int i = 0; i < treeView.Body.RowCount; i++)
                if (treeView.Body.RowIsCat(i) && string.Equals(treeView.Body.RowCat(i), fromName, StringComparison.Ordinal))
                { from = i; isCat = true; break; }
            if (from < 0)
            {
                for (int i = 0; i < treeView.Body.RowCount; i++)
                {
                    Slot s = treeView.Body.RowSlot(i);
                    if (s != null && CatTree.IsBranchSlot(s) && string.Equals(s.Name, fromName, StringComparison.Ordinal)) { from = i; break; }
                }
            }
            if (isCat)
            {
                for (int i = 0; i < treeView.Body.RowCount; i++)
                    if (treeView.Body.RowIsCat(i) && string.Equals(treeView.Body.RowCat(i), toName, StringComparison.Ordinal)) { to = i; break; }
            }
            else
            {
                for (int i = 0; i < treeView.Body.RowCount; i++)
                {
                    Slot s = treeView.Body.RowSlot(i);
                    if (s != null && CatTree.IsBranchSlot(s) && string.Equals(s.Name, toName, StringComparison.Ordinal)) { to = i; break; }
                }
            }
            if (from < 0 || to < 0)
                Core.Log("WARN", "没找到拖动行: " + fromName + "(" + from + ") / " + toName + "(" + to + ")" + (isCat ? " [大类]" : " [分支]"));
            return from >= 0 && to >= 0;
        }

        /// <summary>比对拖动前后顺序，变了记 OK、没变记 FAIL（复现「手拖不动」），最后还原。</summary>
        private void CheckAndRestoreDrag(string fromName, string toName, bool isCat, List<string> before, string tag)
        {
            List<string> after = isCat ? CatPrefs.Cats() : MyBranch.Names();
            bool moved = false;
            if (after.Count == before.Count)
                for (int i = 0; i < after.Count; i++)
                    if (after[i] != before[i]) { moved = true; break; }
            if (!moved) Core.Log("FAIL", tag + " 「" + fromName + "」拖到「" + toName + "」顺序没变 —— 复现了拖不动");
            else Core.Log("INFO", tag + " 「" + fromName + "」已挪过");

            if (isCat)
            {
                for (int i = 0; i < before.Count; i++)
                    if (i >= after.Count || after[i] != before[i])
                        CatPrefs.MoveCatTo(before[i], i == 0 ? "" : before[i - 1], true);
            }
            else
            {
                for (int i = 0; i < before.Count; i++)
                    if (i >= after.Count || after[i] != before[i])
                        MyBranch.Move(before[i], i == 0 ? "" : MyBranch.ParentOf(before[i - 1]),
                            i + 1 < before.Count ? before[i + 1] : "");
            }
        }

        /// <summary>
        /// 端到端自检：用 Win32 真实鼠标输入（SetCursorPos + mouse_event）拖一次。
        /// 连 OS 层的输入路径一起测 —— 事件模拟过了、这个还不过，那问题就在真机和模拟的差异上。
        /// </summary>
        internal void UiTestMouseDrag(string fromName, string toName)
        {
            if (treeView == null) { Core.Log("FAIL", "MOUSEDRAG 没有 viewHost"); return; }
            ShowTree();
            Application.DoEvents();

            int from, to; bool isCat;
            if (!FindDragRows(fromName, toName, out from, out to, out isCat)) return;

            // 窗口可能被记住的位置摆在屏幕外（多显示器拔过线之类），真实鼠标点不到 —— 拉回可见区再测
            WindowState = FormWindowState.Normal;
            SetBounds(80, 80, Math.Max(900, Width), Math.Max(620, Height));
            Activate();
            BringToFront();
            Application.DoEvents();

            // 保证两行都在可视区（大类行挨着，分支选相邻的也够用）：滚到靠上那一行
            treeView.Body.ScrollToRow(Math.Min(from, to));
            Application.DoEvents();

            Native.ForceForeground(Handle);
            Native.ShowWindow(Handle, 9);          // SW_RESTORE
            Native.ForceForeground(Handle);
            Application.DoEvents();

            SlotTree body = treeView.Body;
            Point start = body.PointToScreen(new Point(40, from * 28 + 4 + 14));
            Point end = body.PointToScreen(new Point(40, to * 28 + 4 + (isCat ? 24 : 14)));
            Native.POINT cp;
            Native.GetCursorPos(out cp);
            Core.Log("INFO", "MOUSEDRAG 屏幕坐标 起点(" + start.X + "," + start.Y + ") 落点(" + end.X + "," + end.Y + ")"
                + " 起点处窗口=" + Native.GetWindowText(Native.WindowFromPoint(new Native.POINT { X = start.X, Y = start.Y }))
                + " 当前前台=" + Native.GetWindowText(Native.GetForegroundWindow()) + " 光标(" + cp.X + "," + cp.Y + ")");

            List<string> before = isCat ? CatPrefs.Cats() : MyBranch.Names();
            Native.SetCursorPos(start.X, start.Y);
            System.Threading.Thread.Sleep(120);
            Native.mouse_event(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
            System.Threading.Thread.Sleep(60);
            for (int i = 1; i <= 8; i++)
            {
                Native.SetCursorPos(start.X + (end.X - start.X) * i / 8, start.Y + (end.Y - start.Y) * i / 8);
                System.Threading.Thread.Sleep(40);
                Application.DoEvents();
            }
            System.Threading.Thread.Sleep(60);
            Native.mouse_event(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
            System.Threading.Thread.Sleep(150);
            Application.DoEvents();

            CheckAndRestoreDrag(fromName, toName, isCat, before, "MOUSEDRAG");
            treeView.Rebuild();
        }

        /// <summary>自检：切到方案树并选某个槽位（按显示名）；传「未归类」就打开未归类页。</summary>
        internal void UiTestSelect(string slotName)
        {
            if (viewHost == null) { Core.Log("FAIL", "TREE 没有 viewHost"); return; }
            ShowTree();
            Application.DoEvents();
            if (slotName == "未归类")
            {
                treeView.SelectUnmatched();
                Core.Log("INFO", "SELECT 已打开未归类页（" + treeView.Unmatched + " 个）");
                return;
            }
            Slot s = treeView.SlotByName(slotName);
            if (s == null) { Core.Log("WARN", "SELECT 没找到槽位: " + slotName); return; }
            treeView.SelectSlot(s.Key, true);      // 诊断截图要看得到那一行，顺带滚进视野
            Core.Log("INFO", "SELECT 已选中槽位 " + s.Name + (s.Empty ? "（空位）" : ""));
        }

        /// <summary>自测：切到方案树、数槽位、验证「一个位置只留一个」的替换逻辑。</summary>
        internal void UiTestTree()
        {
            if (viewHost == null) { Core.Log("FAIL", "TREE 没有 viewHost"); return; }
            // 自测会改勾选，先留一份底，测完原样放回去 —— 不能把用户的方案改坏
            List<string> backup = new List<string>(checkedKeys);
            ShowTree();
            Application.DoEvents();

            Core.Log("INFO", "TREE 候选表右键菜单：" + treeView.TreeMenuText());
            int slots = treeView.SlotCount;
            int used = treeView.SlotCheckedCount;
            Core.Log("INFO", "TREE 槽位 " + slots + " 个，其中已选 " + used + " 个");

            // 分类分布
            System.Collections.Generic.Dictionary<string, int> byCat =
                new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);
            System.Collections.Generic.Dictionary<string, int> bySub =
                new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < slots; i++)
            {
                Slot s = treeView.SlotAt(i);
                if (s == null) continue;
                int n;
                byCat.TryGetValue(s.Cat, out n);
                byCat[s.Cat] = n + 1;
                string sk = s.Cat + " \u0001 " + s.Sub;
                bySub.TryGetValue(sk, out n);
                bySub[sk] = n + 1;
                Core.Log("INFO", "TREE  " + s.Cat + " / " + s.Sub + " / " + s.Name + "  候选 " + s.Items.Count
                                            + (s.Extras.Count > 0 ? "  扩展 " + ExtrasText(s) : "")
                                            + (s.Empty ? "  [空位]" : "")
                                            + "  已选 " + (treeView.HasChecked(s) ? "是" : "否"));
            }
            foreach (KeyValuePair<string, int> kv in byCat)
                Core.Log("INFO", "TREE 分类 " + kv.Key + " = " + kv.Value + " 个位置");
            foreach (KeyValuePair<string, int> kv in bySub)
            {
                string[] pp = kv.Key.Split('\u0001');
                Core.Log("INFO", "TREE 小分类 " + pp[0].Trim() + " → " + pp[1].Trim() + " = " + kv.Value + " 个位置");
            }

            // 覆盖诊断：每类有多少个 mod 认得出来
            foreach (string c in Catalog.Cats)
            {
                int tot = 0, known = 0;
                foreach (ModGroup g in Catalog.Groups)
                {
                    if (g.Cat != c) continue;
                    tot++;
                    ModItem mi = g.Main;
                    if (mi != null && ((mi.Subject ?? "").Trim().Length > 0 || (mi.SubjectKey ?? "").Trim().Length > 0)) known++;
                }
                Core.Log("INFO", "TREE 覆盖 " + c + "  " + known + "/" + tot + " 认得出来");
            }

            // 找一个候选多于 3 的槽位来测替换
            int idx = -1;
            for (int i = 0; i < slots; i++)
            {
                Slot s = treeView.SlotAt(i);
                if (s != null && s.Items.Count >= 3) { idx = i; break; }
            }
            if (idx < 0) { Core.Log("FAIL", "TREE 没有候选 >=3 的槽位，测不了替换"); return; }

            Slot t = treeView.SlotAt(idx);
            Core.Log("INFO", "TREE 试验槽位 " + t.Name + "（" + t.Items.Count + " 个候选）");

            int n0 = TotalCheckedParts();
            string pick1 = treeView.ChooseAt(idx, 1);
            Application.DoEvents();
            int n1 = TotalCheckedParts();
            Core.Log("INFO", "TREE 选第 2 个候选「" + pick1 + "」：勾选数 " + n0 + " → " + n1);

            string pick2 = treeView.ChooseAt(idx, 2);
            Application.DoEvents();
            int n2 = TotalCheckedParts();
            Core.Log("INFO", "TREE 换成第 3 个「" + pick2 + "」：勾选数 " + n1 + " → " + n2
                            + "（应该只换不增）");
            if (n2 != n1) Core.Log("FAIL", "TREE 替换后勾选数变了 " + n1 + " → " + n2);

            int groupsInSlot = treeView.CheckedGroups(treeView.SlotAt(idx));
            Core.Log("INFO", "TREE 这个位置现在有 " + groupsInSlot + " 件");
            if (groupsInSlot != 1) Core.Log("FAIL", "TREE 一个位置出现了 " + groupsInSlot + " 件");

            treeView.ChooseAt(idx, -1);
            Application.DoEvents();
            int n3 = TotalCheckedParts();
            Core.Log("INFO", "TREE 点「留原版」：勾选数 " + n2 + " → " + n3);
            if (n3 != n2 - treeView.SlotAt(idx).Items.Count && n3 >= n2) Core.Log("FAIL", "TREE 留原版没清掉");

            // 折叠 / 展开：大类一层、小分类一层都要能收
            int rowsBefore = treeView.RowCount;
            treeView.SetFolded("枪械", true);
            Application.DoEvents();
            int rowsAfter = treeView.RowCount;
            Core.Log("INFO", "TREE 折叠「枪械」：行数 " + rowsBefore + " → " + rowsAfter);
            if (rowsAfter >= rowsBefore) Core.Log("FAIL", "TREE 折叠没生效");
            treeView.SetFolded("枪械", false);
            Application.DoEvents();
            Core.Log("INFO", "TREE 展开回去：行数 " + treeView.RowCount);

            string cat1 = null, sub1 = null;
            for (int i = 0; i < treeView.SlotCount; i++)
            {
                Slot s1 = treeView.SlotAt(i);
                if (s1 == null) continue;
                if (cat1 == null) { cat1 = s1.Cat; sub1 = s1.Sub; }
                if (s1.Cat == cat1 && s1.Sub == sub1) continue;
                break;
            }
            if (cat1 != null && sub1 != null)
            {
                int r0 = treeView.RowCount;
                int inSub = 0;
                for (int i = 0; i < treeView.SlotCount; i++)
                {
                    Slot s1 = treeView.SlotAt(i);
                    if (s1 != null && s1.Cat == cat1 && s1.Sub == sub1) inSub++;
                }
                treeView.SetSubFolded(cat1, sub1, true);
                Application.DoEvents();
                int r1 = treeView.RowCount;
                Core.Log("INFO", "TREE 折叠小分类「" + cat1 + " / " + sub1 + "」（" + inSub + " 个位置）：行数 "
                                + r0 + " → " + r1);
                if (r1 >= r0) Core.Log("FAIL", "TREE 小分类折叠没生效");
                treeView.SetSubFolded(cat1, sub1, false);
                Application.DoEvents();
                if (treeView.RowCount != r0) Core.Log("FAIL", "TREE 小分类展开后行数对不上");
            }

            // 未归类入口：剩下的 MOD 全部列出来，且提供「指定位置…」手动入口
            treeView.SelectUnmatched();
            Application.DoEvents();
            Core.Log("INFO", "TREE 未归类 " + treeView.Unmatched + " 个，列表 " + treeView.UnmatchedRows
                            + " 行，「指定位置…」可见=" + treeView.UnmatchedButtonVisible);
            if (treeView.UnmatchedRows != treeView.Unmatched)
                Core.Log("FAIL", "TREE 未归类列表行数 " + treeView.UnmatchedRows + " 和计数 " + treeView.Unmatched + " 对不上");
            if (!treeView.UnmatchedButtonVisible)
                Core.Log("FAIL", "TREE 未归类页没有「指定位置…」手动入口");
            // 名单跟着日志走：未归类数一波动，直接看日志就知道是哪几组
            {
                List<string> names = new List<string>();
                foreach (ModGroup g in treeView.UnmatchedGroups)
                {
                    ModItem mm = g.Main;
                    names.Add((mm == null ? g.Name : mm.Name) + "(" + (mm == null ? "?" : (mm.Subject == "" ? "无主体" : mm.Subject)) + ")");
                    if (names.Count >= 30) break;
                }
                Core.Log("INFO", "TREE 未归类名单: " + string.Join("、", names.ToArray()));
                int clu;
                Dictionary<ModGroup, List<ModGroup>> clans = CatTree.ClusterUnmatched(treeView.UnmatchedGroups, out clu);
                HashSet<List<ModGroup>> clanSet = new HashSet<List<ModGroup>>();
                foreach (List<ModGroup> mm2 in clans.Values) clanSet.Add(mm2);
                Core.Log("INFO", "TREE 未归类圈伙: " + (clu == 0 ? "没有相似包" : clu + " 个包圈成 " + clanSet.Count + " 伙"));
            }

            // 扩展（音效 / 只改第一人称模型 / HUD）是叠上去的：自己能开关，换模型时不该被一起关掉
            int exIdx = -1;
            for (int i = 0; i < slots; i++)
            {
                Slot sx = treeView.SlotAt(i);
                if (sx != null && sx.Extras.Count > 0 && sx.Items.Count > 1) { exIdx = i; break; }
            }
            if (exIdx < 0) Core.Log("INFO", "TREE 没有「有扩展且有多个模型」的位置，跳过扩展测试");
            else
            {
                Slot sx = treeView.SlotAt(exIdx);
                ModGroup eg = sx.Extras[0];
                treeView.SelectSlot(sx.Key);
                Application.DoEvents();
                if (CountChecked(eg) == 0) { treeView.ToggleExtra(eg); Application.DoEvents(); }
                int on0 = CountChecked(eg);                         // 先确保是开着的
                treeView.ToggleExtra(eg);
                Application.DoEvents();
                int off1 = CountChecked(eg);
                treeView.ToggleExtra(eg);                           // 再开回来
                Application.DoEvents();
                int on1 = CountChecked(eg);
                Core.Log("INFO", "TREE 扩展「" + eg.Name + "」开关：" + on0 + " → " + off1 + " → " + on1);
                if (off1 != 0 || on1 != on0) Core.Log("FAIL", "TREE 扩展开关不对");

                treeView.ChooseAt(exIdx, 0);                        // 换一个模型
                Application.DoEvents();
                int kept = CountChecked(eg);
                Core.Log("INFO", "TREE 换模型后扩展「" + eg.Name + "」勾选：" + on1 + " → " + kept
                                + "（必须不变）");
                if (kept != on1) Core.Log("FAIL", "TREE 换模型把扩展一起关了");

                treeView.ChooseAt(exIdx, -1);                       // 点「留原版」
                Application.DoEvents();
                int kept2 = CountChecked(eg);
                Core.Log("INFO", "TREE 留原版后扩展还在：" + kept2 + "（必须不变）");
                if (kept2 != on1) Core.Log("FAIL", "TREE 留原版把扩展一起关了");
            }

            // 原样放回：按备份逐条还原，不是「再点一下」—— 点一下只会换成另一个候选
            checkedKeys.Clear();
            foreach (string k in backup) checkedKeys.Add(k);
            AfterTreePick();
            treeView.Sync();
            Application.DoEvents();
            int n4 = TotalCheckedParts();
            Core.Log("INFO", "TREE 还原后勾选数 " + n4 + "（原 " + n0 + "）");
            if (n4 != n0) Core.Log("FAIL", "TREE 还原后勾选数对不上 " + n0 + " → " + n4);

            // 截图停在「有扩展」的位置上，这样右半边的上下两栏都能看清
            Slot shot = null;
            for (int i = 0; i < slots; i++)
            {
                Slot sx = treeView.SlotAt(i);
                if (sx != null && sx.Extras.Count > 0 && sx.Items.Count > 0) { shot = sx; break; }
            }
            if (shot == null) shot = treeView.SlotAt(0);
            if (shot != null) treeView.SelectSlot(shot.Key);
        }

        /// <summary>把勾选恢复成某个方案的样子并存盘（自测把方案改坏时用来救回来）。</summary>
        internal void UiFixProfile(string name)
        {
            ApplyProfileByName(name);
            Application.DoEvents();
            Core.Cfg.Mods = new List<string>(checkedKeys);
            Core.SaveConfig();
            Core.Log("INFO", "FIX 已按方案「" + name + "」恢复勾选，共 " + checkedKeys.Count + " 个文件");
        }

        /// <summary>自测日志用：把一个位置的扩展名字串起来。</summary>
        private static string ExtrasText(Slot s)
        {
            string t = "";
            foreach (ModGroup g in s.Extras) t += (t.Length > 0 ? "、" : "") + g.Name;
            return t;
        }

        private int TotalCheckedParts()        {
            int n = 0;
            foreach (ModGroup g in Catalog.Groups) n += CountChecked(g);
            return n;
        }
    }
}

namespace Installer
{
    public partial class MainForm
    {
        /// <summary>自测：「设置 MOD 分类」这条路走一遍 —— 人工归类要真的进树，测完原样还原。</summary>
        internal void UiTestSetCat()
        {
            ShowTree();
            Application.DoEvents();
            if (treeView == null) { Core.Log("FAIL", "SETCAT 没有树"); return; }

            // 分类是持久化的：先留底，自测结束原样写回，不能给用户留一个「测试分类」
            string f = CatPrefs.FileOf();
            string backup = System.IO.File.Exists(f) ? System.IO.File.ReadAllText(f) : null;
            int before = treeView.SlotCount;
            Core.Log("INFO", "SETCAT 起始槽位 " + before + " 个，分类顺序 " + string.Join(" / ", CatPrefs.Cats().ToArray()));

            List<Slot> now = SlotIndex.Build();
            ModGroup miss = null, known = null;
            foreach (ModGroup g in Catalog.Groups)
            {
                if (g.Cat == "地图" || g.Cat == "喷漆" || g.Main == null) continue;
                bool inTree = false;
                // 扩展也算进树：Extras 里的（音效 / 只改第一人称）不缺位置
                foreach (Slot s in now) if (s.Items.Contains(g) || s.Extras.Contains(g)) { inTree = true; break; }
                if (!inTree) { if (miss == null) miss = g; }
                else if (known == null && (g.Main.Subject ?? "").Trim().Length > 0) known = g;
            }
            if (miss != null) Core.Log("INFO", "SETCAT 未归类样本：" + miss.Name);
            if (known == null) { Core.Log("FAIL", "SETCAT 找不到已知位置的样本"); return; }

            ModGroup t1 = miss != null ? miss : known;
            string autoCatOfT1 = t1.Cat;
            CatPrefs.Assign(t1, "测试分类", "测试位置");
            Catalog.ApplyCats();                 // 分类落到 ModGroup.Cat 上：树 / 搜索一起变
            treeView.Rebuild();
            bool inSlot = false, catOk = false;
            foreach (Slot s in SlotIndex.Build())
                if (s.Name == "测试位置" && (s.Items.Contains(t1) || s.Extras.Contains(t1)))
                { inSlot = true; catOk = s.Cat == "测试分类"; }
            Core.Log(inSlot && catOk ? "OK" : "FAIL", "SETCAT 全人工：" + t1.Name + " → 测试分类 / 测试位置，进树=" + inSlot + " 归类=" + catOk);
            Core.Log(t1.Cat == "测试分类" ? "OK" : "FAIL",
                "SETCAT 分类落到数据上：ModGroup.Cat=" + t1.Cat + "（树跟着走）");
            Core.Log(Array.IndexOf(Catalog.Cats, "测试分类") >= 0 ? "OK" : "FAIL",
                "SETCAT 分类表里有「测试分类」：" + string.Join(" / ", Catalog.Cats));

            List<string> order = CatPrefs.Cats();
            Core.Log(order[order.Count - 1] == "测试分类" ? "OK" : "FAIL",
                "SETCAT 新建分类排在末尾：" + string.Join(" / ", order.ToArray()));
            Core.Log(treeView.SlotCount > before ? "OK" : "FAIL",
                "SETCAT 槽位数 " + before + " → " + treeView.SlotCount + "（未归类的进来了）");

            string autoName = (known.Main.Subject ?? "").Trim();
            CatPrefs.Assign(known, "测试分类", "");
            Catalog.ApplyCats();
            treeView.Rebuild();
            bool moved = false;
            foreach (Slot s in SlotIndex.Build())
                if (s.Items.Contains(known) || s.Extras.Contains(known))
                    moved = s.Cat == "测试分类";   // 位置名可能带后缀，只认「这个 MOD 在哪一类的槽里」
            Core.Log(moved ? "OK" : "FAIL", "SETCAT 只改归类：" + known.Name + " → 测试分类（位置仍是 " + autoName + "）");

            CatPrefs.Reset(t1);
            CatPrefs.Reset(known);
            Catalog.ApplyCats();
            treeView.Rebuild();
            bool gone = true;
            foreach (Slot s in SlotIndex.Build()) if (s.Name == "测试位置") { gone = false; break; }
            Core.Log(gone ? "OK" : "FAIL", "SETCAT 恢复自动后「测试位置」已消失");
            Core.Log(t1.Cat == autoCatOfT1 ? "OK" : "FAIL",
                "SETCAT 恢复自动后 ModGroup.Cat 回到 " + t1.Cat + "（原 " + autoCatOfT1 + "）");
            Core.Log(treeView.SlotCount == before ? "OK" : "FAIL",
                "SETCAT 槽位数回到 " + treeView.SlotCount + "（原 " + before + "）");

            // 大类改名：整类跟着搬，不能因为旧名字从分类表里消失就掉出树
            int medBefore = 0;
            foreach (ModGroup x in Catalog.Groups) if (x.Cat == "药品") medBefore++;
            CatPrefs.RenameCat("药品", "测试改名类");
            Catalog.AdoptCatRename("药品", "测试改名类");
            int medAfter = 0;
            foreach (ModGroup x in Catalog.Groups) if (x.Cat == "测试改名类") medAfter++;
            bool inTabs = Array.IndexOf(Catalog.Cats, "测试改名类") >= 0 && Array.IndexOf(Catalog.Cats, "药品") < 0;
            Core.Log(medBefore == medAfter && inTabs ? "OK" : "FAIL",
                "SETCAT 大类改名：药品 " + medBefore + " 个 → 测试改名类 " + medAfter + " 个，树跟上了=" + inTabs);

            if (backup == null) { try { System.IO.File.Delete(f); } catch { } }
            else System.IO.File.WriteAllText(f, backup, new System.Text.UTF8Encoding(true));
            CatPrefs.Reload();
            Catalog.ApplyCats();
            treeView.Rebuild();
            Core.Log(CatPrefs.Cats().Contains("测试分类") ? "FAIL" : "OK",
                "SETCAT 配置已还原：" + string.Join(" / ", CatPrefs.Cats().ToArray()));
        }

        /// <summary>自测：把「设置 MOD 分类」窗口拉起来截一张，人眼过一遍排版。</summary>
        internal void UiTestCatForm()
        {
            ModGroup g = null;
            foreach (ModGroup x in Catalog.Groups)
                if (x.Cat != "地图" && x.Cat != "喷漆" && x.Main != null) { g = x; break; }
            if (g == null) { Core.Log("FAIL", "CATFORM 没有样本"); return; }
            using (ModCatForm f = new ModCatForm(g))
            {
                f.Show();
                Application.DoEvents();
                f.PerformLayout();
                Application.DoEvents();
                using (System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, f.Width, f.Height));
                    bmp.Save(System.IO.Path.Combine(Core.LogDir, "modcat.png"),
                             System.Drawing.Imaging.ImageFormat.Png);
                }
                Core.Log("INFO", "CATFORM " + g.Name + "（" + g.Cat + "）截图已存 modcat.png");
                f.Close();
            }
        }

        /// <summary>自测：拿个假游戏目录打开「已装 MOD 开关」，截图看按钮排版。</summary>
        internal void UiTestAddonSwitch()
        {
            string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "l4d2-switch-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            string addons = System.IO.Path.Combine(tmp, "left4dead2", "addons");
            try
            {
                System.IO.Directory.CreateDirectory(addons);
                List<string> names = new List<string>();
                foreach (ModGroup x in Catalog.Groups)
                {
                    if (x.Main == null || string.IsNullOrEmpty(x.Main.Name)) continue;
                    names.Add(x.Main.Name);
                    if (names.Count >= 6) break;
                }
                int i = 0;
                foreach (string n in names)
                {
                    System.IO.File.WriteAllText(System.IO.Path.Combine(addons, n), "x");
                    i++;
                }
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.AppendLine("\"AddonList\"");
                sb.AppendLine("{");
                for (int k = 0; k < names.Count; k++)
                    sb.AppendLine("\t\"" + names[k] + "\"\t\t\"" + (k % 3 == 0 ? "1" : "0") + "\"");
                sb.AppendLine("}");
                System.IO.File.WriteAllText(AddonList.FileOf(tmp), sb.ToString(), System.Text.Encoding.Default);

                using (AddonSwitchForm f = new AddonSwitchForm(tmp))
                {
                    f.Show();
                    Application.DoEvents();
                    f.PerformLayout();
                    Application.DoEvents();
                    using (System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(f.Width, f.Height))
                    {
                        f.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, f.Width, f.Height));
                        bmp.Save(System.IO.Path.Combine(Core.LogDir, "addonswitch.png"),
                                 System.Drawing.Imaging.ImageFormat.Png);
                    }
                    Core.Log("INFO", "ADDONSW " + i + " 个假 MOD，截图已存 addonswitch.png");
                    f.Close();
                }
            }
            catch (Exception ex) { Core.Log("FAIL", "ADDONSW 截图失败: " + ex.Message); }
            finally { try { System.IO.Directory.Delete(tmp, true); } catch { } }
        }
    }
}

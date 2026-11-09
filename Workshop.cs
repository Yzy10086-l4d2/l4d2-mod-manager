using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace Installer
{
    /// <summary>创意工坊物品的元数据（GetPublishedFileDetails 的一行）。</summary>
    internal sealed class WsItem
    {
        public string Id = "";
        public string Title = "";
        public long Size;
        public string Url = "";
        public string FileName = "";        // 接口给的内部存储路径，不是文件名
        public bool IsCollection;
        public string Note = "";            // 「没有直链」这类说明
        public string State = "待下载";
        public bool Ok;                     // 已经落盘
    }

    /// <summary>
    /// 创意工坊：查元数据 + 直链下载。
    ///
    /// L4D2 的物品都是单个 vpk，GetPublishedFileDetails 会给出 file_url，可以直接下，
    /// 不需要 steamcmd、也不需要登录。两点必须记住：
    ///   1) 接口本身不要 API key，但一次最多 100 个 ID；
    ///   2) 下载那一步必须带浏览器 UA —— 裸请求（curl 默认 UA）会被 CDN 回 404，
    ///      地址明明是对的，看起来像「物品不存在」，很容易被误导。
    /// 文件夹式的物品（别的游戏居多）file_url 是空的，这类只能走 Steam 客户端订阅，
    /// 所以这里留着 Note 字段把它说清楚，而不是报一个看不懂的错。
    /// </summary>
    internal static class Workshop
    {
        public const string AppId = "550";
        const string ApiRoot = "https://api.steampowered.com/ISteamRemoteStorage/";
        const string Ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) L4D2Setup/2.51";

        static readonly Regex RxLink = new Regex(@"sharedfiles/filedetails/?\?id=(\d{4,})", RegexOptions.IgnoreCase);
        static readonly Regex RxNum = new Regex(@"^\d{4,}$");

        // ---------------------------------------------------------- 解析输入

        /// <summary>
        /// 从一段文本里抠出物品 ID：完整链接、纯数字、逗号/空格/换行分隔都能认。
        /// 先按分隔符切开逐个判断，再对整段兜一次「链接连着贴」的情况，最后按出现顺序去重。
        /// </summary>
        public static List<string> ParseIds(string text)
        {
            List<string> ids = new List<string>();
            if (text == null) return ids;
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

            char[] sep = new char[] { '\r', '\n', ',', '，', ';', '；', ' ', '\t', '"', '\'', '|' };
            foreach (string raw in text.Split(sep, StringSplitOptions.RemoveEmptyEntries))
            {
                string s = raw.Trim();
                if (s.Length == 0) continue;
                Match m = RxLink.Match(s);
                if (m.Success) s = m.Groups[1].Value;
                else if (!RxNum.IsMatch(s)) continue;
                if (seen.Add(s)) ids.Add(s);
            }

            foreach (Match m in RxLink.Matches(text))
            {
                string id = m.Groups[1].Value;
                if (seen.Add(id)) ids.Add(id);
            }
            return ids;
        }

        // ---------------------------------------------------------- 浏览 / 搜索

        static readonly Regex RxAnchor = new Regex(@"<a\s[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        static readonly Regex RxId = new Regex(@"[?&]id=(\d+)");
        // 新版工坊页是 JS 渲染：结果在一段转义 JSON 里，形如 \"publishedfileid\":\"3832210318\"
        static readonly Regex RxJsonId = new Regex(@"publishedfileid[\\""]*:[\\""]*(\d+)");

        static string Get(string url)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.UserAgent = Ua;
            req.Timeout = 25000;
            req.ReadWriteTimeout = 25000;
            using (HttpWebResponse r = (HttpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        /// <summary>
        /// 按关键词浏览工坊。走的是公开网页（workshop/browse），不要 API key，
        /// 因为 QueryFiles 那个接口要 key —— 为了看个列表不值得让用户去申请。
        /// 一页 30 个，id 从结果卡片里取，别把整个页面的 ?id= 都抓来（侧栏推荐会混进去）。
        /// </summary>
        public static List<string> Search(string query, string sort, int page)
        {
            StringBuilder u = new StringBuilder();
            u.Append("https://steamcommunity.com/workshop/browse/?appid=").Append(AppId);
            u.Append("&section=readytouseitems");
            if (!string.IsNullOrEmpty(sort)) u.Append("&browsesort=").Append(sort);
            if (!string.IsNullOrEmpty(query)) u.Append("&searchtext=").Append(Uri.EscapeDataString(query));
            if (page > 1) u.Append("&p=").Append(page);
            return ParseBrowse(Get(u.ToString()));
        }

        internal static List<string> ParseBrowse(string html)
        {
            List<string> ids = new List<string>();
            if (string.IsNullOrEmpty(html)) return ids;
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

            // 新版页面是 JS 渲染，<a> 里根本没有链接；结果在一整段转义 JSON 里，
            // publishedfileid 键值对按文档顺序出现，顺序就是页面上的排序。
            foreach (Match m in RxJsonId.Matches(html))
                if (seen.Add(m.Groups[1].Value)) ids.Add(m.Groups[1].Value);
            if (ids.Count > 0) return ids;

            // 老版页面兜底：结果卡片 <a class="workshopItem..."> 的链接在标签里。
            foreach (Match a in RxAnchor.Matches(html))
            {
                string tag = a.Value;
                if (tag.IndexOf("workshopItem", StringComparison.Ordinal) < 0) continue;
                Match m = RxId.Match(tag);
                if (!m.Success) continue;
                if (seen.Add(m.Groups[1].Value)) ids.Add(m.Groups[1].Value);
            }
            return ids;
        }

        /// <summary>
        /// 槽位 → 默认搜索词。槽位名本身带拉丁字母（AK-47 / UZI / AWP）就直接用；
        /// 中文名（木喷 / 佐伊）退回模型键的尾段（pump / zoey），不准用户自己改。
        /// </summary>
        internal static string SearchWord(string slotName, string slotKey)
        {
            string n = (slotName ?? "").Trim();
            bool latin = false;
            foreach (char c in n)
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) { latin = true; break; }
            if (latin) return n;

            string k = slotKey ?? "";
            int bar = k.IndexOf('\u0001');
            if (bar >= 0) k = k.Substring(bar + 1);
            foreach (string p in new string[] { "slot:", "#empty:", "c:", "sub:" })
                if (k.StartsWith(p, StringComparison.OrdinalIgnoreCase)) { k = k.Substring(p.Length); break; }
            if (k.StartsWith("w_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("v_", StringComparison.OrdinalIgnoreCase)) k = k.Substring(2);
            int us = k.LastIndexOf('_');
            if (us >= 0 && us < k.Length - 1) k = k.Substring(us + 1);
            return k;
        }

        /// <summary>大类 → 搜索词。太泛的大类给空串（那就是浏览全部）。</summary>
        internal static string CatWord(string cat)
        {
            switch (cat ?? "")
            {
                case "枪械": return "";
                case "近战": return "melee";
                case "投掷物": return "grenade";
                case "弹药升级": return "ammo";
                case "人物": return "survivor";
                case "医疗": return "medkit";
                case "地图": return "map";
                case "喷漆": return "spray";
                default: return "";
            }
        }

        // ---------------------------------------------------------- 网络

        static string Post(string path, string body)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(ApiRoot + path);
            req.Method = "POST";
            req.ContentType = "application/x-www-form-urlencoded";
            req.UserAgent = Ua;
            req.Timeout = 20000;
            req.ReadWriteTimeout = 20000;
            byte[] data = Encoding.UTF8.GetBytes(body);
            req.ContentLength = data.Length;
            using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);
            using (HttpWebResponse r = (HttpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        /// <summary>批量查详情。接口一次最多 100 个，这里按 100 分组。</summary>
        public static List<WsItem> Fetch(IList<string> ids)
        {
            List<WsItem> outp = new List<WsItem>();
            if (ids == null || ids.Count == 0) return outp;

            for (int from = 0; from < ids.Count; from += 100)
            {
                int n = Math.Min(100, ids.Count - from);
                StringBuilder sb = new StringBuilder();
                sb.Append("itemcount=").Append(n);
                for (int k = 0; k < n; k++)
                    sb.Append("&publishedfileids[").Append(k).Append("]=").Append(ids[from + k]);

                string json = Post("GetPublishedFileDetails/v1/", sb.ToString());
                List<string> objs = SplitObjects(json, "publishedfiledetails");

                Dictionary<string, string> byId = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string o in objs)
                {
                    string id = JStr(o, "publishedfileid");
                    if (id.Length > 0) byId[id] = o;
                }

                for (int k = 0; k < n; k++)
                {
                    string id = ids[from + k];
                    WsItem it = new WsItem();
                    it.Id = id;
                    string o;
                    if (!byId.TryGetValue(id, out o))
                    {
                        it.Note = "接口没有返回这个 ID";
                        outp.Add(it);
                        continue;
                    }
                    long res = JNum(o, "result");
                    it.Title = JStr(o, "title").Trim();
                    it.Size = JNum(o, "file_size");
                    it.Url = JStr(o, "file_url");
                    it.FileName = JStr(o, "filename");
                    if (res != 1) it.Note = "物品不存在或已删除";
                    outp.Add(it);
                }
            }
            return outp;
        }

        /// <summary>合集 → 子项 ID。不是合集时返回空表。</summary>
        public static List<string> CollectionChildren(string collectionId, out string title)
        {
            title = "";
            List<string> kids = new List<string>();
            string json = Post("GetCollectionDetails/v1/", "collectioncount=1&publishedfileids[0]=" + collectionId);
            List<string> objs = SplitObjects(json, "collectiondetails");
            if (objs.Count == 0) return kids;
            title = JStr(objs[0], "title").Trim();
            foreach (string ko in SplitObjects(objs[0], "children"))
            {
                string id = JStr(ko, "publishedfileid");
                if (id.Length > 0 && !kids.Contains(id)) kids.Add(id);
            }
            return kids;
        }

        /// <summary>
        /// 判断是不是合集。**不能靠「有没有直链」**：合集的 file_url 也非空，
        /// 但那指向的是合集的缩略图（filename 形如 xxx_new_preview_标题 Thumbnail.jpg，
        /// 域名是 images.steamusercontent.com），当成正常物品下下来就是一张 JPG。
        /// 普通物品的 filename 一定以 .vpk 结尾，拿这个当主判据最稳。
        /// </summary>
        static bool LooksLikeCollection(WsItem it)
        {
            string fn = it.FileName ?? "";
            if (fn.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase)) return false;
            if (fn.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) return true;
            if (fn.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)) return true;
            if (fn.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return true;
            if (it.Url.IndexOf("images.steamusercontent.com", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return it.Url.Length == 0 && it.Size == 0;
        }

        /// <summary>查询 + 展开合集，返回最终要下载的清单（合集会先占一行，再跟上它的子项）。</summary>
        public static List<WsItem> Resolve(IList<string> ids)
        {
            const int MaxChildren = 200;
            List<WsItem> first = Fetch(ids);
            List<WsItem> final = new List<WsItem>();
            List<string> more = new List<string>();

            foreach (WsItem it in first)
            {
                if (it.Note.Length > 0) { final.Add(it); continue; }

                if (LooksLikeCollection(it))
                {
                    string title = "";
                    List<string> kids;
                    try { kids = CollectionChildren(it.Id, out title); }
                    catch { kids = new List<string>(); }

                    if (kids.Count > 0)
                    {
                        it.IsCollection = true;
                        it.Title = title.Length > 0 ? title : ("合集 " + it.Id);
                        it.State = kids.Count + " 个子项" + (kids.Count > MaxChildren ? "（只取前 " + MaxChildren + "）" : "");
                        if (kids.Count > MaxChildren) kids = kids.GetRange(0, MaxChildren);
                        final.Add(it);
                        foreach (string k in kids) if (!more.Contains(k)) more.Add(k);
                        continue;
                    }
                    // 不像合集又没内容：大概率是文件夹式物品
                    if (it.Url.Length == 0 || it.Size == 0)
                        it.Note = "没有直链（文件夹式物品），要用 Steam 客户端订阅下载";
                    else
                        it.Note = "拿不到可用的文件（可能是合集封面）";
                }
                final.Add(it);
            }

            if (more.Count > 0)
            {
                // 子项自己也可能是个合集，但实际嵌套很少见，这里只展开一层，避免死循环
                List<WsItem> kids = Fetch(more);
                foreach (WsItem k in kids) if (k.Note.Length == 0 && k.Url.Length == 0 && k.Size == 0)
                    k.Note = "没有直链（文件夹式物品），要用 Steam 客户端订阅下载";
                final.AddRange(kids);
            }
            return final;
        }

        /// <summary>
        /// 下一个物品。先写 .part，校验大小后再改名 —— 半截文件绝不能出现在库里，
        /// 否则扫描会把一个坏 vpk 当成正常 MOD 收进去。
        /// </summary>
        public static void Download(WsItem it, string dest, Action<long, long> progress)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(it.Url);
            req.UserAgent = Ua;                 // 不加这个 CDN 直接 404
            req.Timeout = 30000;
            req.ReadWriteTimeout = 60000;
            req.AllowAutoRedirect = true;

            string part = dest + ".part";
            try
            {
                if (File.Exists(part)) File.Delete(part);
            }
            catch { }

            using (HttpWebResponse r = (HttpWebResponse)req.GetResponse())
            {
                long total = r.ContentLength > 0 ? r.ContentLength : it.Size;
                using (Stream src = r.GetResponseStream())
                using (FileStream fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
                {
                    byte[] buf = new byte[65536];
                    long got = 0;
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        fs.Write(buf, 0, n);
                        got += n;
                        if (progress != null) progress(got, total);
                    }
                    fs.Flush();
                }
            }

            long real = 0;
            try { real = new FileInfo(part).Length; } catch { }
            if (it.Size > 0 && real != it.Size)
            {
                try { File.Delete(part); } catch { }
                throw new IOException("大小不一致：收到 " + real + " 字节，应为 " + it.Size);
            }
            if (real == 0)
            {
                try { File.Delete(part); } catch { }
                throw new IOException("下载到 0 字节");
            }
            // 最后一道闸：只认库能用的格式。合集封面、HTML 错误页这类东西绝不能以 .vpk 落进库，
            // 否则扫描会把它当成一个正常 MOD 收进去，后面读 addoninfo 一直失败也看不出原因。
            if (!LooksLikeModFile(part))
            {
                try { File.Delete(part); } catch { }
                throw new IOException("下到的不是 vpk / 压缩包（多半是合集封面图）");
            }

            if (File.Exists(dest)) File.Delete(dest);
            File.Move(part, dest);
        }

        /// <summary>库认的三种容器：vpk（55 AA 12 34）、zip（PK）、rar（Rar!）。</summary>
        static bool LooksLikeModFile(string path)
        {
            try
            {
                using (FileStream fs = File.OpenRead(path))
                {
                    byte[] h = new byte[4];
                    int n = fs.Read(h, 0, 4);
                    if (n < 4) return false;
                    if (h[0] == 0x34 && h[1] == 0x12 && h[2] == 0xAA && h[3] == 0x55) return true;
                    if (h[0] == 0x50 && h[1] == 0x4B) return true;
                    if (h[0] == 0x52 && h[1] == 0x61 && h[2] == 0x72 && h[3] == 0x21) return true;
                    return false;
                }
            }
            catch { return false; }
        }

        // ---------------------------------------------------------- JSON（只够用）

        /// <summary>
        /// 从 JSON 里切出某个数组下的所有对象。没有引入 JSON 库：接口返回的结构固定，
        /// 按括号深度扫描比引一个依赖更省事，也不会被版本差异影响。
        /// </summary>
        internal static List<string> SplitObjects(string json, string arrayKey)
        {
            List<string> list = new List<string>();
            if (string.IsNullOrEmpty(json)) return list;

            int at = json.IndexOf("\"" + arrayKey + "\"", StringComparison.Ordinal);
            if (at < 0) return list;
            int i = json.IndexOf('[', at);
            if (i < 0) return list;

            int depth = 0, start = -1;
            bool inStr = false, esc = false;
            for (; i < json.Length; i++)
            {
                char c = json[i];
                if (inStr)
                {
                    if (esc) esc = false;
                    else if (c == '\\') esc = true;
                    else if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') { inStr = true; continue; }
                if (c == '{')
                {
                    if (depth == 0) start = i;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        list.Add(json.Substring(start, i - start + 1));
                        start = -1;
                    }
                }
                else if (c == ']' && depth == 0) break;
            }
            return list;
        }

        internal static string JStr(string obj, string key)
        {
            string pat = "\"" + key + "\":";
            int i = obj.IndexOf(pat, StringComparison.Ordinal);
            if (i < 0) return "";
            i += pat.Length;
            while (i < obj.Length && (obj[i] == ' ' || obj[i] == '\t')) i++;
            if (i >= obj.Length || obj[i] != '"') return "";      // null 或数字

            i++;
            StringBuilder sb = new StringBuilder();
            while (i < obj.Length)
            {
                char c = obj[i];
                if (c == '\\' && i + 1 < obj.Length)
                {
                    char n = obj[i + 1];
                    if (n == 'u' && i + 5 < obj.Length)
                    {
                        int code;
                        if (int.TryParse(obj.Substring(i + 2, 4), System.Globalization.NumberStyles.HexNumber,
                                         System.Globalization.CultureInfo.InvariantCulture, out code))
                            sb.Append((char)code);
                        i += 6;
                        continue;
                    }
                    if (n == 'n') sb.Append('\n');
                    else if (n == 't') sb.Append('\t');
                    else if (n == 'r') sb.Append('\r');
                    else sb.Append(n);
                    i += 2;
                    continue;
                }
                if (c == '"') break;
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        internal static long JNum(string obj, string key)
        {
            string pat = "\"" + key + "\":";
            int i = obj.IndexOf(pat, StringComparison.Ordinal);
            if (i < 0) return 0;
            i += pat.Length;
            while (i < obj.Length && (obj[i] == ' ' || obj[i] == '\t')) i++;
            // Steam 的数字字段一半是数字、一半是字符串：file_size 是 "12345"（带引号），
            // result 是 1（不带）。两种都得认。
            if (i < obj.Length && obj[i] == '"') i++;
            int j = i;
            if (j < obj.Length && obj[j] == '-') j++;
            while (j < obj.Length && char.IsDigit(obj[j])) j++;
            long v;
            long.TryParse(obj.Substring(i, j - i), out v);
            return v;
        }

        // ---------------------------------------------------------- 落盘

        /// <summary>下载目录。U 盘模式放主库，本地模式直接放游戏 addons（本地模式就是直接管游戏目录）。</summary>
        public static string DestDir()
        {
            string root;
            if (Core.IsLocal)
            {
                root = Core.GameAddonsDir();
                if (string.IsNullOrEmpty(root))
                    throw new InvalidOperationException("本地模式下没找到游戏 addons 目录，先在设置里指定游戏目录");
            }
            else
            {
                if (string.IsNullOrEmpty(Core.ModRoot))
                    throw new InvalidOperationException("还没选主库，先在顶部选一下 U 盘");
                root = Core.ModRoot;
            }
            string dir = Path.Combine(root, "_创意工坊");
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>
        /// 给下载下来的 vpk 起名。用标题而不是 ID：库里按名字浏览，一串数字没法看。
        /// 非法字符换成空格，过长截断，全空时退回 ID。
        /// </summary>
        public static string SafeFileName(string title, string id)
        {
            string t = (title ?? "").Trim();
            if (t.Length == 0) t = "workshop_" + id;

            char[] bad = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder();
            foreach (char c in t)
            {
                if (Array.IndexOf(bad, c) >= 0 || c == '\r' || c == '\n' || c == '\t') sb.Append(' ');
                else sb.Append(c);
                if (sb.Length >= 70) break;
            }
            string s = sb.ToString().Trim().TrimEnd('.');
            while (s.IndexOf("  ", StringComparison.Ordinal) >= 0) s = s.Replace("  ", " ");
            if (s.Length == 0) s = "workshop_" + id;
            return s + ".vpk";
        }

        /// <summary>
        /// 下载完自动改名：读 vpk 自己的 addoninfo.txt，用**游戏里显示的那个名字**当文件名。
        /// 工坊标题常带一堆【】标签、作者前缀和表情，游戏内名才是这个 MOD 真正的身份
        /// （库里显示、方案树归类、搜索别名全都跟着它走）。
        /// 读不出 addoninfo（没有 / 分卷包 / 坏包）就保留工坊标题名 —— 改名只是锦上添花，不许失败。
        /// 撞名加 (2) (3)…序号，谁也不覆盖谁。
        /// </summary>
        /// <returns>改完的文件名；没改（读不到 / 名字没变 / 出错）返回 null。</returns>
        public static string RenameToAddonTitle(string dest)
        {
            try
            {
                if (!string.Equals(Path.GetExtension(dest), ".vpk", StringComparison.OrdinalIgnoreCase)) return null;
                Vpk.AddonMeta m = Vpk.ReadAddonInfo(dest);
                string t = (m == null || m.Title == null) ? "" : m.Title.Trim();
                if (t.Length == 0) return null;

                string want = SafeFileName(t, Path.GetFileNameWithoutExtension(dest));
                string cur = Path.GetFileName(dest);
                if (string.Equals(want, cur, StringComparison.OrdinalIgnoreCase)) return null;

                string dir = Path.GetDirectoryName(dest);
                string cand = Path.Combine(dir, want);
                if (File.Exists(cand))
                {
                    string stem = Path.GetFileNameWithoutExtension(want);
                    for (int n = 2; n < 100; n++)
                    {
                        cand = Path.Combine(dir, stem + " (" + n + ").vpk");
                        if (!File.Exists(cand)) break;
                    }
                }
                File.Move(dest, cand);
                return Path.GetFileName(cand);
            }
            catch { return null; }
        }

        // ---------------------------------------------------------- 离线自检

        /// <summary>
        /// 只测不联网的部分：链接解析、文件名清理、JSON 提取。
        /// 网络波动不该让自检变红，所以这里一个字节都不发出去。
        /// </summary>
        internal static int SelfTest()
        {
            int bad = 0;

            List<string> a = ParseIds("https://steamcommunity.com/sharedfiles/filedetails/?id=3833236101");
            if (a.Count != 1 || a[0] != "3833236101")
            { bad++; Core.Log("FAIL", "WS 解析单个链接得到 " + string.Join(",", a.ToArray())); }
            else Core.Log("INFO", "WS 解析单个链接 = " + a[0]);

            List<string> b = ParseIds("3832308538, https://steamcommunity.com/sharedfiles/filedetails/?id=3834851144\r\n3832308538");
            string bj = string.Join("/", b.ToArray());
            if (bj != "3832308538/3834851144")
            { bad++; Core.Log("FAIL", "WS 解析混合输入得到 " + bj + "（应去重且保序）"); }
            else Core.Log("INFO", "WS 解析混合输入 = " + bj);

            List<string> c = ParseIds("这不是链接 abc 12");
            if (c.Count != 0)
            { bad++; Core.Log("FAIL", "WS 把垃圾文本认成了 ID：" + string.Join(",", c.ToArray())); }
            else Core.Log("INFO", "WS 垃圾文本被正确忽略");

            CheckName(ref bad, "Hello/World: test?", "Hello World test.vpk");
            CheckName(ref bad, "", "workshop_123.vpk");
            CheckName(ref bad, "  ..  ", "workshop_123.vpk");
            CheckName(ref bad, new string('x', 90), new string('x', 70) + ".vpk");

            string json = "{\"response\":{\"publishedfiledetails\":["
                + "{\"publishedfileid\":\"111\",\"result\":1,\"title\":\"first\",\"file_size\":\"12345\",\"file_url\":\"https://x/y/\",\"filename\":\"a/b.vpk\"},"
                + "{\"publishedfileid\":\"222\",\"result\":9,\"title\":\"\\u4e2d\\u6587\\u6807\\u9898\",\"file_size\":\"0\"}]}}";

            List<string> objs = SplitObjects(json, "publishedfiledetails");
            if (objs.Count != 2)
            { bad++; Core.Log("FAIL", "WS JSON 切出 " + objs.Count + " 个对象，应为 2"); }
            else
            {
                string t1 = JStr(objs[0], "title");
                long s1 = JNum(objs[0], "file_size");
                string u1 = JStr(objs[0], "file_url");
                string t2 = JStr(objs[1], "title");
                long r2 = JNum(objs[1], "result");
                if (t1 != "first" || s1 != 12345 || u1 != "https://x/y/")
                { bad++; Core.Log("FAIL", "WS JSON 字段解析错：" + t1 + " / " + s1 + " / " + u1); }
                if (t2 != "中文标题" || r2 != 9)
                { bad++; Core.Log("FAIL", "WS JSON 中文或结果码解析错：" + t2 + " / " + r2); }
                Core.Log("INFO", "WS JSON：" + t1 + " / " + s1 + " / " + t2 + " / result=" + r2);
            }

            // file_url 为空 = 文件夹式物品，这个判断决定了列表里显示「需客户端下载」而不是下载失败
            string emptyUrl = "{\"response\":{\"publishedfiledetails\":[{\"publishedfileid\":\"333\",\"result\":1,\"title\":\"folder item\",\"file_size\":\"4840892\",\"file_url\":\"\"}]}}";
            List<string> eo = SplitObjects(emptyUrl, "publishedfiledetails");
            if (eo.Count == 1 && JStr(eo[0], "file_url").Length == 0 && JNum(eo[0], "file_size") == 4840892)
                Core.Log("INFO", "WS 无直链物品识别正常（file_url 空、大小仍有值）");
            else
            { bad++; Core.Log("FAIL", "WS 无直链物品没被正确识别"); }

            // 合集识别：合集本身也有 file_url，指向缩略图 JPG。曾经按「没有直链才算合集」判断，
            // 结果粘贴一个合集链接，下下来的是一张封面图被存成 .vpk。这条断言就是防这个回归。
            WsItem col = new WsItem();
            col.FileName = "1791607851_new_preview_Outland Necrotics Thumbnail.jpg";
            col.Url = "https://images.steamusercontent.com/ugc/13962854076826563678/FEA2A020/";
            col.Size = 116242;
            WsItem modItem = new WsItem();
            modItem.FileName = "myl4d2addons/gordon as louis.vpk";
            modItem.Url = "https://cdn.steamusercontent.com/ugc/1812/B48E/";
            modItem.Size = 35557542;

            if (!LooksLikeCollection(col))
            { bad++; Core.Log("FAIL", "WS 没把「缩略图 jpg + images 域名」认成合集"); }
            else Core.Log("INFO", "WS 合集识别：缩略图 jpg → 是合集");
            if (LooksLikeCollection(modItem))
            { bad++; Core.Log("FAIL", "WS 把正常的 .vpk 物品误判成合集了"); }
            else Core.Log("INFO", "WS 合集识别：.vpk 物品 → 不是合集");

            // 浏览页解析：新版是 JS 渲染，结果在转义 JSON 里（\"publishedfileid\":\"123\"）；
            // 老版才是 <a class="workshopItem">。两条路都得能出 id，且按文档顺序、去重。
            string newPage = "\\\"publishedfileid\\\":\\\"111\\\",\\\"title\\\":\\\"a\\\"}"
                           + " \\\"publishedfileid\\\":\\\"222\\\" ... \\\"publishedfileid\\\":\\\"111\\\"";
            List<string> nb = ParseBrowse(newPage);
            if (nb.Count != 2 || nb[0] != "111" || nb[1] != "222")
            { bad++; Core.Log("FAIL", "WS 新版浏览页解析得到 " + nb.Count + " 个: " + string.Join(",", nb.ToArray())); }
            else Core.Log("INFO", "WS 新版浏览页（转义 JSON）解析 = 111,222（去重保序）");

            string oldPage = "<a class=\"workshopItem\" href=\"https://steamcommunity.com/sharedfiles/filedetails/?id=333\">"
                           + "<a class=\"workshopItemTitle\" href=\"...?id=444&searchtext=x\">";
            List<string> ob = ParseBrowse(oldPage);
            if (ob.Count != 2 || ob[0] != "333" || ob[1] != "444")
            { bad++; Core.Log("FAIL", "WS 老版浏览页解析得到 " + string.Join(",", ob.ToArray())); }
            else Core.Log("INFO", "WS 老版浏览页（workshopItem 链接）解析 = 333,444");

            // 槽位 → 搜索词：拉丁名直接用，中文名退回模型键尾段
            if (SearchWord("AK-47", "c:AK-47") != "AK-47" || SearchWord("木喷", "枪械\u0001w_shotgun_pump") != "pump")
            { bad++; Core.Log("FAIL", "WS 槽位搜索词推错: " + SearchWord("AK-47", "c:AK-47") + " / " + SearchWord("木喷", "枪械\u0001w_shotgun_pump")); }
            else Core.Log("INFO", "WS 槽位搜索词：AK-47 → AK-47，木喷 → pump");

            // 下载完自动改名：addoninfo 的游戏内名 > 工坊标题。读不出保留原名，撞名加序号不覆盖。
            string rnDir = Path.Combine(Path.GetTempPath(), "ws-rename-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            try
            {
                Directory.CreateDirectory(rnDir);
                string v1 = Path.Combine(rnDir, "【工坊标题】一长串.vpk");
                File.WriteAllBytes(v1, Work.BuildTinyVpk("\"AddonInfo\"\r\n{\r\n\tAddonTitle \"黑色AK103\"\r\n}\r\n", true));
                string r1 = RenameToAddonTitle(v1);
                if (r1 == "黑色AK103.vpk" && File.Exists(Path.Combine(rnDir, r1)) && !File.Exists(v1))
                    Core.Log("INFO", "WS 改名：addoninfo 标题生效 → " + r1);
                else { bad++; Core.Log("FAIL", "WS 改名：应为「黑色AK103.vpk」，实际「" + r1 + "」"); }

                string r2 = RenameToAddonTitle(Path.Combine(rnDir, "黑色AK103.vpk"));
                if (r2 == null) Core.Log("INFO", "WS 改名：名字已一致，不动");
                else { bad++; Core.Log("FAIL", "WS 改名：名字一致却返回了「" + r2 + "」"); }

                string v3 = Path.Combine(rnDir, "没有addoninfo.vpk");
                File.WriteAllBytes(v3, Work.BuildTinyVpk(null, false));
                string r3 = RenameToAddonTitle(v3);
                if (r3 == null && File.Exists(v3)) Core.Log("INFO", "WS 改名：没有 addoninfo，保留原文件名");
                else { bad++; Core.Log("FAIL", "WS 改名：没有 addoninfo 却动了文件（r3=" + r3 + "）"); }

                string v4 = Path.Combine(rnDir, "另一个工坊名.vpk");
                File.WriteAllBytes(v4, Work.BuildTinyVpk("\"AddonInfo\"\r\n{\r\n\tAddonTitle \"黑色AK103\"\r\n}\r\n", true));
                string r4 = RenameToAddonTitle(v4);
                if (r4 == "黑色AK103 (2).vpk" && File.Exists(Path.Combine(rnDir, "黑色AK103.vpk")))
                    Core.Log("INFO", "WS 改名：撞名加序号 " + r4 + "，没覆盖别人");
                else { bad++; Core.Log("FAIL", "WS 改名：撞名应得「黑色AK103 (2).vpk」，实际「" + r4 + "」"); }
            }
            catch (Exception ex) { bad++; Core.Log("FAIL", "WS 改名自检抛异常：" + ex.Message); }
            finally { try { Directory.Delete(rnDir, true); } catch { } }

            return bad;
        }

        static void CheckName(ref int bad, string title, string want)
        {
            string got = SafeFileName(title, "123");
            if (got != want)
            {
                string t = title == null ? "" : (title.Length > 20 ? title.Substring(0, 20) + "…" : title);
                bad++;
                Core.Log("FAIL", "WS 文件名「" + t + "」得到「" + got + "」，应为「" + want + "」");
            }
            else Core.Log("INFO", "WS 文件名 = " + got);
        }

        /// <summary>
        /// 联网端到端自检（--workshopnet=ID）：真的查一次、下一次，落到临时目录校验魔数和大小后删掉。
        /// 不落库、不改配置 —— 只用来确认「UA、流读取、大小校验」这条链路是通的。
        /// </summary>
        internal static int NetTest(string id)
        {
            int bad = 0;
            List<WsItem> items = Resolve(new List<string> { id });
            WsItem it = null;
            foreach (WsItem x in items)
                if (!x.IsCollection && x.Url.Length > 0) { it = x; break; }
            if (it == null)
            {
                Core.Log("FAIL", "WSNET 解析不到可下载的物品 " + id);
                return 1;
            }
            Core.Log("INFO", "WSNET " + it.Id + "「" + it.Title + "」" + Core.Human(it.Size));

            string tmp = Path.Combine(Path.GetTempPath(), "ws_" + id + ".vpk");
            string rn = null;
            try
            {
                long last = -1;
                Download(it, tmp, delegate(long got, long total)
                {
                    if (total > 0 && got == total && last != got) last = got;
                });

                long real = new FileInfo(tmp).Length;
                Core.Log("INFO", "WSNET 落盘 " + real + " 字节（接口报 " + it.Size + "）");
                if (it.Size > 0 && real != it.Size)
                {
                    bad++;
                    Core.Log("FAIL", "WSNET 大小对不上");
                }

                using (FileStream fs = File.OpenRead(tmp))
                {
                    byte[] h = new byte[4];
                    int n = fs.Read(h, 0, 4);
                    bool vpk = n == 4 && h[0] == 0x34 && h[1] == 0x12 && h[2] == 0xAA && h[3] == 0x55;
                    if (!vpk) { bad++; Core.Log("FAIL", "WSNET 下到的不是 vpk（魔数不是 55AA1234）"); }
                    else Core.Log("INFO", "WSNET vpk 魔数正确");
                }

                // 下载完自动改名（与对话框同一条路径）：addoninfo 游戏内名 > 工坊标题
                rn = RenameToAddonTitle(tmp);
                if (rn != null) Core.Log("INFO", "WSNET 已按游戏内名改名 → " + rn);
                else Core.Log("INFO", "WSNET 没改名（addoninfo 没有标题或名字一致），保留「" + Path.GetFileName(tmp) + "」");
                string gone = File.Exists(tmp) ? Path.GetFileName(tmp) : rn;
                if (gone != null) Core.Log("INFO", "WSNET 最终文件名「" + gone + "」");
            }
            catch (Exception ex)
            {
                bad++;
                Core.Log("FAIL", "WSNET 下载失败: " + ex.Message);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                try { if (File.Exists(tmp + ".part")) File.Delete(tmp + ".part"); } catch { }
                try { if (rn != null) File.Delete(Path.Combine(Path.GetDirectoryName(tmp), rn)); } catch { }
            }
            return bad;
        }

        /// <summary>
        /// 联网搜索自检（--workshopsearch=词）：走真实的 browse 页。
        /// 页面是 JS 渲染、结果在转义 JSON 里 —— 解析器改版后必须拿真页面验一遍才作数。
        /// </summary>
        internal static int SearchTest(string query)
        {
            int bad = 0;
            List<string> ids = Search(query, "mostsubscribed", 1);
            Core.Log("INFO", "WSS 搜「" + query + "」第 1 页 = " + ids.Count + " 个");
            if (ids.Count < 5)
            {
                Core.Log("FAIL", "WSS 结果少于 5 个，浏览页解析多半断了");
                return 1;
            }
            if (ids.Count > 30)
            {
                bad++;
                Core.Log("FAIL", "WSS 一页不该超过 30 个（侧栏推荐混进来了？），实际 " + ids.Count);
            }

            // 前 3 个再查一次元数据：标题不为空、result==1
            List<WsItem> meta = Fetch(ids.GetRange(0, Math.Min(3, ids.Count)));
            foreach (WsItem it in meta)
            {
                if (string.IsNullOrEmpty(it.Title)) { bad++; Core.Log("FAIL", "WSS " + it.Id + " 没有标题"); }
                else Core.Log("INFO", "WSS " + it.Id + "「" + (it.Title.Length <= 46 ? it.Title : it.Title.Substring(0, 46) + "…") + "」");
            }
            return bad;
        }
    }

    /// <summary>
    /// 创意工坊导入窗口：粘贴链接 → 解析 → 下载到库。
    /// 下载在后台线程跑，UI 每 100ms 更新一次，关窗口会取消。
    /// </summary>
    internal sealed class WorkshopForm : Form
    {
        private readonly MainForm owner;
        private readonly TextBox box = new TextBox();
        private readonly ListView lv = new ListView();
        private readonly ProgressBar bar = new ProgressBar();
        private readonly Label lblStatus = new Label();
        private readonly FlatButton btnParse = new FlatButton();
        private readonly FlatButton btnStart = new FlatButton();
        private readonly FlatButton btnClose = new FlatButton();

        private List<WsItem> items = new List<WsItem>();
        private volatile bool busy;
        private volatile bool cancel;
        private DateTime lastUi = DateTime.MinValue;

        // 浏览模式：选分类 → 搜工坊。和「粘贴链接」共用下面这张表和下载队列
        private readonly ComboBox cboCat = new ComboBox();
        private readonly TextBox txtQuery = new TextBox();
        private readonly ComboBox cboSort = new ComboBox();
        private readonly FlatButton btnSearch = new FlatButton();
        private readonly FlatButton btnMore = new FlatButton();
        private string lastQuery = "";
        private int page = 1;
        private int lastResultCount;

        /// <summary>分类下拉的一项：显示名 + 默认搜索词。</summary>
        private sealed class CatChoice
        {
            public string Label;
            public string Query;
            public string Sort;
            public override string ToString() { return Label; }
        }

        /// <summary>这次成功下载了几个（主窗口据此决定要不要重扫）。</summary>
        public int DownloadedCount { get; private set; }

        public WorkshopForm(MainForm o)
        {
            owner = o;
            Text = "从创意工坊导入";
            Theme.UseDpi(this);
            ClientSize = Dpi.Sz(680, 520);
            Font = Theme.UI;
            BackColor = Theme.Bg;
            StartPosition = FormStartPosition.CenterScreen;   // 独立窗口，不从主窗口取位置
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;                             // 独立窗口，任务栏里得有自己的格子

            Label tip = new Label();
            tip.SetBounds(16, 10, 648, 20);
            tip.Font = Theme.UISmall;
            tip.ForeColor = Theme.Ink2;
            tip.Text = "选分类直接浏览工坊，或粘贴物品 / 合集链接（...?id=123456）。下到库里后自动归纳位置。";
            Controls.Add(tip);

            // 浏览行：分类来自本地方案树的位置表，选中就把默认搜索词填上（可自己改）
            Label lc = new Label();
            lc.Text = "分类";
            lc.SetBounds(16, 40, 36, 20);
            lc.Font = Theme.UISmall;
            lc.ForeColor = Theme.Ink2;
            Controls.Add(lc);

            cboCat.SetBounds(56, 36, 176, 24);
            cboCat.DropDownStyle = ComboBoxStyle.DropDownList;
            Theme.Dress(cboCat);
            cboCat.SelectedIndexChanged += delegate
            {
                CatChoice c = cboCat.SelectedItem as CatChoice;
                if (c != null) txtQuery.Text = c.Query;
            };
            Controls.Add(cboCat);

            txtQuery.SetBounds(240, 36, 224, 24);
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
            btnSearch.SetBounds(472, 32, 72, Theme.H);
            btnSearch.Click += delegate { DoSearch(1); };
            Controls.Add(btnSearch);

            cboSort.SetBounds(552, 36, 112, 24);
            cboSort.DropDownStyle = ComboBoxStyle.DropDownList;
            Theme.Dress(cboSort);
            cboSort.Items.Add(new CatChoice { Label = "最多订阅", Sort = "mostsubscribed" });
            cboSort.Items.Add(new CatChoice { Label = "最高评价", Sort = "toprated" });
            cboSort.Items.Add(new CatChoice { Label = "最新", Sort = "newestfirst" });
            cboSort.Items.Add(new CatChoice { Label = "趋势", Sort = "trend" });
            cboSort.SelectedIndex = 0;
            Controls.Add(cboSort);

            box.SetBounds(16, 76, 648, 60);
            box.Multiline = true;
            box.ScrollBars = ScrollBars.Vertical;
            box.AcceptsReturn = true;
            Theme.Dress(box);
            Controls.Add(box);

            btnParse.Text = "解析";
            btnParse.Primary = true;
            btnParse.SetBounds(16, 144, 96, Theme.H);
            btnParse.Click += delegate { Parse(); };
            Controls.Add(btnParse);

            FlatButton btnPaste = new FlatButton();
            btnPaste.Text = "从剪贴板";
            btnPaste.SetBounds(120, 144, 96, Theme.H);
            btnPaste.Click += delegate
            {
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        string t = box.Text.Trim();
                        box.Text = (t.Length > 0 ? t + "\r\n" : "") + Clipboard.GetText().Trim();
                        box.SelectionStart = box.TextLength;
                    }
                }
                catch { }
            };
            Controls.Add(btnPaste);

            FlatButton btnClear = new FlatButton();
            btnClear.Text = "清空";
            btnClear.SetBounds(224, 144, 72, Theme.H);
            btnClear.Click += delegate
            {
                if (busy) return;
                box.Clear();
                items.Clear();
                lv.Items.Clear();
                SetStatus("就绪");
                btnStart.Enabled = false;
            };
            Controls.Add(btnClear);

            lv.SetBounds(16, 184, 648, 232);
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            lv.BorderStyle = BorderStyle.FixedSingle;
            lv.BackColor = Theme.Surface;
            lv.ForeColor = Theme.Ink;
            lv.Font = Theme.UISmall;
            lv.GridLines = false;
            lv.MultiSelect = true;
            lv.Columns.Add("物品 ID", 110);
            lv.Columns.Add("标题", 300);
            lv.Columns.Add("大小", 76, HorizontalAlignment.Right);
            lv.Columns.Add("状态", 156);
            Controls.Add(lv);

            lblStatus.SetBounds(16, 424, 648, 20);
            lblStatus.Font = Theme.UISmall;
            lblStatus.ForeColor = Theme.Ink2;
            lblStatus.Text = "就绪。先粘贴链接，再点「解析」。";
            lblStatus.AutoEllipsis = true;
            Controls.Add(lblStatus);

            // 原生进度条在深色主题下永远是浅色的一条，平时不下载就别摆在那里
            bar.SetBounds(16, 448, 648, 12);
            bar.Minimum = 0;
            bar.Maximum = 100;
            bar.Visible = false;
            Controls.Add(bar);

            btnStart.Text = "开始下载";
            btnStart.Primary = true;
            btnStart.Enabled = false;
            btnStart.SetBounds(16, 472, 112, Theme.H);
            btnStart.Click += delegate { StartDownload(); };
            Controls.Add(btnStart);

            btnMore.Text = "下一页";
            btnMore.SetBounds(136, 472, 88, Theme.H);
            btnMore.Enabled = false;
            btnMore.Click += delegate { DoSearch(page + 1); };
            Controls.Add(btnMore);

            btnClose.Text = "关闭";
            btnClose.SetBounds(564, 472, 100, Theme.H);
            btnClose.DialogResult = DialogResult.Cancel;
            Controls.Add(btnClose);
            CancelButton = btnClose;

            FormClosing += delegate { cancel = true; };

            // 标题栏和表头/滚动条交给主题：这两个必须在句柄建好之后打招呼
            Load += delegate { Theme.ApplyChrome(this); Theme.DeepNative(this); };
            Shown += delegate { Theme.ApplyChrome(this); Theme.DeepNative(this); };

            FillCats();
            cboCat.SelectedIndex = 0;
        }

        private void Ui(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); } catch { }
        }

        private void SetStatus(string s)
        {
            if (lblStatus.IsDisposed) return;
            lblStatus.Text = s;
        }

        private void FillList()
        {
            lv.BeginUpdate();
            try
            {
                lv.Items.Clear();
                foreach (WsItem it in items)
                {
                    ListViewItem li = new ListViewItem(it.Id);
                    li.SubItems.Add(it.Title.Length == 0 ? "(无标题)" : it.Title);
                    li.SubItems.Add(it.Size > 0 ? Core.Human(it.Size) : "-");
                    li.SubItems.Add(it.IsCollection ? it.State : (it.Note.Length > 0 ? it.Note : it.State));
                    li.Tag = it;
                    if (it.IsCollection) li.ForeColor = Theme.Ink3;
                    else if (it.Note.Length > 0) li.ForeColor = Theme.Warn;
                    lv.Items.Add(li);
                }
            }
            finally { lv.EndUpdate(); }
        }

        private void SetRowState(WsItem it, string s)
        {
            it.State = s;
            Ui(delegate
            {
                foreach (ListViewItem li in lv.Items)
                {
                    if (li.Tag != it) continue;
                    li.SubItems[3].Text = s;
                    break;
                }
            });
        }

        /// <summary>分类下拉 = 本地方案树的大类 + 位置。选中只负责填搜索词，不自动搜。</summary>
        private void FillCats()
        {
            cboCat.Items.Clear();
            cboCat.Items.Add(new CatChoice { Label = "全部物品", Query = "" });

            List<Slot> slots = null;
            try { if (owner.Tree != null) slots = owner.Tree.Slots; } catch { }
            if (slots == null || slots.Count == 0) return;

            HashSet<string> cats = new HashSet<string>(StringComparer.Ordinal);
            foreach (Slot s in slots)
            {
                if (s == null || string.IsNullOrEmpty(s.Cat)) continue;
                if (cats.Add(s.Cat))
                    cboCat.Items.Add(new CatChoice { Label = "【" + s.Cat + "】", Query = Workshop.CatWord(s.Cat) });
            }
            foreach (Slot s in slots)
            {
                if (s == null || string.IsNullOrEmpty(s.Name)) continue;
                cboCat.Items.Add(new CatChoice
                {
                    Label = s.Cat + " · " + s.Name,
                    Query = Workshop.SearchWord(s.Name, s.Key)
                });
            }
        }

        /// <summary>按当前搜索词和排序翻一页。搜索结果是物品，不涉及合集展开。</summary>
        private void DoSearch(int p)
        {
            if (busy) return;
            string q = txtQuery.Text.Trim();
            CatChoice sortChoice = cboSort.SelectedItem as CatChoice;
            string sort = sortChoice == null ? "mostsubscribed" : sortChoice.Sort;

            lastQuery = q;
            page = p;
            busy = true;
            btnSearch.Enabled = false;
            btnMore.Enabled = false;
            btnStart.Enabled = false;
            btnParse.Enabled = false;
            lv.Items.Clear();
            items.Clear();
            SetStatus("正在搜索「" + (q.Length == 0 ? "全部" : q) + "」第 " + p + " 页…");

            Thread t = new Thread(delegate()
            {
                List<WsItem> got = null;
                string err = null;
                int count = 0;
                try
                {
                    List<string> ids = Workshop.Search(q, sort, p);
                    count = ids.Count;
                    got = ids.Count > 0 ? Workshop.Fetch(ids) : new List<WsItem>();
                }
                catch (Exception ex) { err = ex.Message; Core.Log("FAIL", "工坊搜索失败: " + ex); }

                Ui(delegate
                {
                    busy = false;
                    btnSearch.Enabled = true;
                    btnParse.Enabled = true;
                    if (err != null) { SetStatus("搜索失败：" + err); return; }
                    items = got;
                    lastResultCount = count;
                    FillList();

                    int can = 0;
                    foreach (WsItem it in items) if (it.Url.Length > 0) can++;
                    SetStatus("第 " + p + " 页：" + items.Count + " 个结果，可下载 " + can + " 个"
                            + (count >= 30 ? "。还有下一页" : "。没有更多了"));
                    btnMore.Enabled = count >= 30;
                    btnStart.Enabled = can > 0;
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void Parse()
        {
            if (busy) return;
            List<string> ids = Workshop.ParseIds(box.Text);
            if (ids.Count == 0)
            {
                SetStatus("没认出物品 ID：粘贴形如 ...sharedfiles/filedetails/?id=123456 的链接，或直接贴数字。");
                return;
            }

            busy = true;
            btnParse.Enabled = false;
            btnStart.Enabled = false;
            lv.Items.Clear();
            items.Clear();
            SetStatus("正在查询 " + ids.Count + " 个物品…");
            Core.Log("INFO", "工坊：开始解析 " + ids.Count + " 个 ID");

            Thread t = new Thread(delegate()
            {
                List<WsItem> got = null;
                string err = null;
                try { got = Workshop.Resolve(ids); }
                catch (Exception ex) { err = ex.Message; Core.Log("FAIL", "工坊查询失败: " + ex); }

                Ui(delegate
                {
                    busy = false;
                    btnParse.Enabled = true;
                    if (err != null)
                    {
                        SetStatus("查询失败：" + err);
                        return;
                    }
                    items = got;
                    FillList();

                    int can = 0, col = 0, bad = 0;
                    foreach (WsItem it in items)
                    {
                        if (it.Url.Length > 0) can++;
                        if (it.IsCollection) col++;
                        if (it.Note.Length > 0) bad++;
                    }
                    SetStatus("解析到 " + items.Count + " 条" + (col > 0 ? "（" + col + " 个合集已展开）" : "")
                            + "，可下载 " + can + " 个" + (bad > 0 ? "，" + bad + " 条拿不到直链" : "") + "。");
                    btnStart.Enabled = can > 0;
                    Core.Log("INFO", "工坊：解析完成，可下载 " + can + " 个");
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void StartDownload()
        {
            if (busy) return;
            List<WsItem> todo = new List<WsItem>();
            // 有选中行就只下选中的：浏览模式一页 30 个结果，通常只想要其中几个。
            // 一个都没选才退化成「全部可下载的」（粘贴模式下的老行为）。
            if (lv.SelectedItems.Count > 0)
            {
                foreach (ListViewItem li in lv.SelectedItems)
                {
                    WsItem it = li.Tag as WsItem;
                    if (it != null && !it.IsCollection && it.Url.Length > 0 && !it.Ok) todo.Add(it);
                }
            }
            else
            {
                foreach (WsItem it in items)
                    if (!it.IsCollection && it.Url.Length > 0 && !it.Ok) todo.Add(it);
            }
            if (todo.Count == 0)
            {
                SetStatus("没有可下载的物品：要么都下完了，要么都没有直链。");
                return;
            }

            string dir;
            try { dir = Workshop.DestDir(); }
            catch (Exception ex) { SetStatus(ex.Message); return; }

            busy = true;
            cancel = false;
            btnStart.Enabled = false;
            btnParse.Enabled = false;
            bar.Value = 0;
            bar.Visible = true;
            SetStatus("开始下载 " + todo.Count + " 个文件到 " + dir);

            Thread t = new Thread(delegate()
            {
                int ok = 0, fail = 0, skip = 0;
                for (int i = 0; i < todo.Count; i++)
                {
                    if (cancel) break;
                    WsItem it = todo[i];
                    string dest = Path.Combine(dir, Workshop.SafeFileName(it.Title, it.Id));

                    // 同名同大小视为已经下过：重复点「开始下载」不该再拉一遍
                    try
                    {
                        if (File.Exists(dest) && it.Size > 0 && new FileInfo(dest).Length == it.Size)
                        {
                            it.Ok = true;
                            skip++;
                            // 之前下的还是工坊标题名？顺手改成游戏内名（读不出就原样保留）
                            string rn0 = Workshop.RenameToAddonTitle(dest);
                            SetRowState(it, "已存在，跳过" + (rn0 != null ? "，已改名 " + Clip(rn0, 16) : ""));
                            if (rn0 != null) Core.Log("OK", "工坊：改名 " + Path.GetFileName(dest) + " → " + rn0);
                            continue;
                        }
                    }
                    catch { }

                    int idx = i;
                    SetRowState(it, "下载中…");
                    try
                    {
                        Workshop.Download(it, dest, delegate(long got, long total)
                        {
                            if ((DateTime.Now - lastUi).TotalMilliseconds < 100) return;
                            lastUi = DateTime.Now;
                            int pct = total > 0 ? (int)Math.Min(100, got * 100 / total) : 0;
                            Ui(delegate
                            {
                                if (pct >= bar.Minimum && pct <= bar.Maximum) bar.Value = pct;
                                SetStatus("正在下载 " + (idx + 1) + "/" + todo.Count + "：" + Clip(it.Title, 30)
                                        + "  " + Core.Human(got) + " / " + Core.Human(total));
                            });
                        });
                        it.Ok = true;
                        ok++;
                        // 自动改名：addoninfo 里的游戏内名 > 工坊标题。读不出就保留原名的
                        string rn = Workshop.RenameToAddonTitle(dest);
                        if (rn != null)
                        {
                            Core.Log("OK", "工坊：改名 " + Path.GetFileName(dest) + " → " + rn
                                + "（游戏内名，工坊标题是「" + Clip(it.Title, 40) + "」）");
                            dest = Path.Combine(dir, rn);
                        }
                        SetRowState(it, "已下载 " + Core.Human(it.Size) + (rn != null ? "，已改名 " + Clip(rn, 16) : ""));
                        Core.Log("OK", "工坊：已下载 " + it.Id + " → " + dest);
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        string why = ex.Message;
                        if (why.Length > 24) why = why.Substring(0, 24) + "…";
                        SetRowState(it, "失败：" + why);
                        Core.Log("FAIL", "工坊下载失败 " + it.Id + ": " + ex.Message);
                    }
                }

                int okFinal = ok, failFinal = fail, skipFinal = skip;
                bool cancelled = cancel;
                Ui(delegate
                {
                    busy = false;
                    btnParse.Enabled = true;
                    btnStart.Enabled = true;
                    bar.Value = 0;
                    bar.Visible = false;
                    DownloadedCount += okFinal;
                    SetStatus((cancelled ? "已停止：" : "完成：") + "新下载 " + okFinal + " 个"
                            + (skipFinal > 0 ? "，已存在 " + skipFinal + " 个" : "")
                            + (failFinal > 0 ? "，失败 " + failFinal + " 个（原因见状态列）" : "")
                            + "。关闭窗口后会重扫库。");
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private static string Clip(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }

        /// <summary>
        /// 诊断截图用：塞几条假数据把界面填满，不发任何请求。
        /// 数据取自真实物品，长标题 / 无直链 / 合集三种行都能看到。
        /// </summary>
        internal void FillSample()
        {
            box.Text = "https://steamcommunity.com/sharedfiles/filedetails/?id=3816864462\r\n"
                     + "3833236101\r\n3832308538";
            txtQuery.Text = "AK47";

            items = new List<WsItem>();

            WsItem col = new WsItem();
            col.Id = "3816864462";
            col.Title = "Outland Necrotics";
            col.IsCollection = true;
            col.State = "237 个子项（只取前 200）";
            items.Add(col);

            WsItem a = new WsItem();
            a.Id = "3833236101";
            a.Title = "The After - Tank / Bloater (The Last Of Us)";
            a.Size = 45190588;
            a.Url = "https://cdn.steamusercontent.com/ugc/x/";
            a.FileName = "myl4d2addons/tank bloater.vpk";
            a.State = "待下载";
            items.Add(a);

            WsItem b = new WsItem();
            b.Id = "3832308538";
            b.Title = "Chargers get staggered by blunt melee [vscript]";
            b.Size = 2622;
            b.Url = "https://cdn.steamusercontent.com/ugc/y/";
            b.FileName = "new folder (4).vpk";
            b.State = "已下载 3 KB";
            items.Add(b);

            FillList();
            SetStatus("解析到 239 条（1 个合集展开 237 个子项），可下载 2 个，0 条拿不到直链。");
            btnStart.Enabled = true;
        }
    }
}

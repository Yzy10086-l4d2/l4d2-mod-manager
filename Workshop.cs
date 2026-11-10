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

        /// <summary>详情页要用的字段：同一个响应里本来就有，以前一个都没解析。</summary>
        public string Desc = "";            // file_description，原文是 BBCode
        public string Preview = "";         // preview_url
        public long Created, Updated;       // time_created / time_updated（Unix 秒）
        public long Subs, Views;            // subscriptions / views
        public string Creator = "";         // 作者 steamid
        public string Tags = "";            // "枪械 · 模型" 这种拼好的标签串
        public string Where = "";           // 下载后识别出来的「替换 AK-47 · 模型」
        public string SavedAs = "";         // 落盘后的文件名（改名之后），用来回查识别结果

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
                    // 详情字段：同一个响应里本来就有，顺手全取下来（详情面板 + 中文翻译要用）
                    // 注意字段名：Steam 现在返回的是 description，早期文档里的 file_description 已经不再下发。
                    // 只认后者的话，真实数据里所有物品的正文都会是空的（详情页统一显示「没有写介绍」）。
                    it.Desc = JStr(o, "description");
                    if (it.Desc.Length == 0) it.Desc = JStr(o, "file_description");
                    it.Preview = JStr(o, "preview_url");
                    it.Created = JNum(o, "time_created");
                    it.Updated = JNum(o, "time_updated");
                    it.Subs = JNum(o, "subscriptions");
                    it.Views = JNum(o, "views");
                    it.Creator = JStr(o, "creator");
                    it.Tags = TagsOf(o);
                    if (res != 1) it.Note = "物品不存在或已删除";
                    outp.Add(it);
                }
            }
            return outp;
        }

        static readonly Regex RxTag = new Regex("\"tag\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);

        /// <summary>
        /// 标签：接口给的是 [{"tag":"Action"},…] 这种对象数组，手写解析器没有数组支持，
        /// 这里用一条正则把 tag 值抠出来拼成一句。没有标签返回空串。
        /// </summary>
        internal static string TagsOf(string obj)
        {
            List<string> tags = new List<string>();
            if (string.IsNullOrEmpty(obj)) return "";
            foreach (Match m in RxTag.Matches(obj))
            {
                string t = m.Groups[1].Value.Trim();
                if (t.Length == 0 || tags.Contains(t)) continue;
                tags.Add(t);
                if (tags.Count >= 8) break;
            }
            return string.Join(" · ", tags.ToArray());
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
        /// <summary>
        /// 下载记录：工坊 ID → 落盘文件名。画廊打开时预扫它给卡片标「已下载」。
        /// 只增不改；旧的按标题改名的历史下载没有 ID，标不出来（诚实胜过猜）。
        /// </summary>
        internal static string IdMapPath { get { return Path.Combine(Core.LogDir, "workshop-idmap.txt"); } }

        internal static void RememberDownload(string id, string fileName)
        {
            try
            {
                Directory.CreateDirectory(Core.LogDir);
                File.AppendAllText(IdMapPath, (id ?? "").Trim() + "|" + (fileName ?? "").Trim() + "\r\n", Encoding.UTF8);
            }
            catch { }
        }

        internal static HashSet<string> LoadDownloadedIds()
        {
            HashSet<string> set = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                if (!File.Exists(IdMapPath)) return set;
                foreach (string line in File.ReadAllLines(IdMapPath, Encoding.UTF8))
                {
                    int bar = line.IndexOf('|');
                    if (bar > 0) set.Add(line.Substring(0, bar).Trim());
                }
            }
            catch { }
            return set;
        }

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

            // 详情字段：同一个响应里就带着 file_description / preview_url / tags / time_updated，
            // 以前一个都没取 —— 现在右侧详情面板和中文翻译全靠它们，断了就是一片空白。
            string detJson = "{\"response\":{\"publishedfiledetails\":[{\"publishedfileid\":\"444\",\"result\":1,"
                           + "\"title\":\"Tank\",\"file_size\":\"1024\",\"file_url\":\"https://x/z.vpk\","
                           + "\"file_description\":\"[b]Bold[/b] text\",\"preview_url\":\"https://img/p.jpg\","
                           + "\"time_created\":\"1700000000\",\"time_updated\":\"1710000000\","
                           + "\"subscriptions\":\"12400\",\"views\":\"99\","
                           + "\"tags\":[{\"tag\":\"Tank\"},{\"tag\":\"角色\"}]}]}}";
            List<string> dd = SplitObjects(detJson, "publishedfiledetails");
            if (dd.Count == 1)
            {
                string desc = JStr(dd[0], "file_description");
                string prev = JStr(dd[0], "preview_url");
                string tags = TagsOf(dd[0]);
                long upd = JNum(dd[0], "time_updated");
                long subs = JNum(dd[0], "subscriptions");
                if (desc != "[b]Bold[/b] text" || prev != "https://img/p.jpg" || tags != "Tank · 角色"
                    || upd != 1710000000 || subs != 12400)
                {
                    bad++;
                    Core.Log("FAIL", "WS 详情字段解析错：desc=" + desc + " prev=" + prev
                        + " tags=" + tags + " upd=" + upd + " subs=" + subs);
                }
                else Core.Log("INFO", "WS 详情字段：描述 / 预览图 / 标签 / 更新时间 / 订阅数 全部解析正常");
            }
            else { bad++; Core.Log("FAIL", "WS 详情字段：夹具没解析出来"); }

            // 回归：Steam 现在下发的字段名是 description，不是 file_description。
            // 上面那个夹具写的是老名字，所以「只认 file_description」的 bug 离线全绿、真实数据全空
            // （详情页统一显示「这个物品没有写介绍」）。这里按真实字段名再验一遍，并且要求优先用它。
            string realJson = "{\"response\":{\"publishedfiledetails\":[{\"publishedfileid\":\"445\",\"result\":1,"
                            + "\"title\":\"AK\",\"description\":\"[b]正文[/b] 带换行\\n第二行\","
                            + "\"preview_url\":\"\",\"tags\":[]}]}}";
            List<string> rl = SplitObjects(realJson, "publishedfiledetails");
            if (rl.Count == 1)
            {
                string d1 = JStr(rl[0], "description");
                if (d1.Length == 0) d1 = JStr(rl[0], "file_description");
                if (d1.IndexOf("正文") < 0 || d1.IndexOf("\n") < 0)
                {
                    bad++;
                    Core.Log("FAIL", "WS 真实字段名 description 解析不对：" + d1.Replace("\n", "\\n"));
                }
                else Core.Log("INFO", "WS 真实字段名 description 解析正常（含 \\n 转义）");
            }
            else { bad++; Core.Log("FAIL", "WS description 夹具没解析出来"); }

            // BBCode 清洗：标签清干净、小标题与列表符号保留、链接只留文字
            string cleaned = WsText.Clean("[h1]Title[/h1][b]Bold[/b][list][*]one\n[*]two[/list][url=http://x]link[/url]");
            if (cleaned.IndexOf('[') >= 0 || cleaned.IndexOf("■ Title") < 0
                || cleaned.IndexOf("· one") < 0 || cleaned.IndexOf("link") < 0)
            {
                bad++;
                Core.Log("FAIL", "WS 正文清洗不对：" + cleaned.Replace("\n", " / "));
            }
            else Core.Log("INFO", "WS 正文清洗：标签清空、小标题 / 列表 / 链接文字保留");

            // 中文正文不该再白跑一趟翻译
            if (!WsText.MostlyChinese("这是一个中文介绍") || WsText.MostlyChinese("This is an English description"))
            { bad++; Core.Log("FAIL", "WS 中英判定不对"); }
            else Core.Log("INFO", "WS 中英判定：中文不翻、英文要翻");

            // 译文缓存：键里带 time_updated —— 作者改过介绍，旧译文必须失效
            {
                string cid = "999001";
                bool hitOld, missNew;
                try
                {
                    WsTranslate.WriteCacheForTest(cid, 111, "旧译文");
                    hitOld = WsTranslate.ReadCache(cid, 111) == "旧译文";
                    missNew = WsTranslate.ReadCache(cid, 222) == null;
                    string cp = WsTranslate.CachePathForTest(cid);
                    if (File.Exists(cp)) File.Delete(cp);
                }
                catch (Exception ex) { hitOld = false; missNew = false; Core.Log("WARN", "WS 缓存测试异常: " + ex.Message); }
                if (hitOld && missNew) Core.Log("INFO", "WS 译文缓存：命中同版本、介绍更新后失效");
                else { bad++; Core.Log("FAIL", "WS 译文缓存行为不对 (hit=" + hitOld + " miss=" + missNew + ")"); }
            }

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

            // 正文是详情页的主体，必须拿真实数据验一次：上一版字段名写错（file_description），
            // 离线自检全绿，但真实数据里全都是空串，详情页统一显示「这个物品没有写介绍」。
            string rawDesc = it.Desc == null ? "" : it.Desc;
            string cleanDesc = WsText.Clean(rawDesc);
            Core.Log("INFO", "WSNET 正文 原文 " + rawDesc.Length + " 字 / 清洗后 " + cleanDesc.Length + " 字");
            if (rawDesc.Length == 0)
            {
                bad++;
                Core.Log("FAIL", "WSNET 正文是空的 —— 接口字段名可能又变了");
            }
            else if (cleanDesc.Length == 0)
            {
                bad++;
                Core.Log("FAIL", "WSNET 正文清洗后变空 —— BBCode 清洗器把内容吃掉了");
            }
            else Core.Log("INFO", "WSNET 正文前 40 字：" + cleanDesc.Substring(0, Math.Min(40, cleanDesc.Length)).Replace("\n", " "));

            // 翻译通道：只记录不判失败（翻译是锦上添花，挂了不该让下载自检变红），
            // 但必须能看见这次走的是哪条路 —— 上一条路（微软鉴权）已经 404 了。
            if (cleanDesc.Length > 0)
            {
                try
                {
                    string terr;
                    string zh = WsTranslate.Translate(it.Id, it.Updated, cleanDesc, out terr);
                    Core.Log("INFO", "WSNET 翻译 通道=" + (WsTranslate.LastProvider == null ? "" : WsTranslate.LastProvider)
                        + " 译文 " + zh.Length + " 字" + (terr != null ? "（" + terr + "）" : ""));
                    if (zh.Length > 0)
                        Core.Log("INFO", "WSNET 译文前 40 字：" + zh.Substring(0, Math.Min(40, zh.Length)).Replace("\n", " "));
                }
                catch (Exception tex) { Core.Log("WARN", "WSNET 翻译异常: " + tex.Message); }
            }

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

            // 翻页：画廊现在是「往下滑就往后追加」，全靠这两点 —— 第 2 页取得到、且和第 1 页不重复。
            // 顺带验一下「不足 30 个 = 到底」这个判断的前提（满页）。
            List<string> p2 = Search(query, "mostsubscribed", 2);
            Core.Log("INFO", "WSS 第 2 页 = " + p2.Count + " 个");
            if (p2.Count == 0)
            {
                bad++;
                Core.Log("FAIL", "WSS 第 2 页是空的 —— 翻页没生效，滑动加载会卡在第一页");
            }
            else
            {
                HashSet<string> s1 = new HashSet<string>(ids, StringComparer.Ordinal);
                int dup = 0;
                foreach (string x in p2) if (s1.Contains(x)) dup++;
                if (dup > 0)
                {
                    bad++;
                    Core.Log("FAIL", "WSS 第 1/2 页重复 " + dup + " 个 —— 追加后会出现重复卡片");
                }
                else Core.Log("INFO", "WSS 第 1/2 页无重复，追加安全");
            }
            if (ids.Count < 30)
                Core.Log("WARN", "WSS 第 1 页只有 " + ids.Count + " 个（不足 30），界面会判成「已到底」—— 这个词本身结果就少");
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
        private readonly FlatButton btnPaste = new FlatButton();
        private readonly FlatButton btnClear = new FlatButton();
        private readonly FlatButton btnStart = new FlatButton();
        private readonly FlatButton btnClose = new FlatButton();

        // ---- 详情面板（右侧）----
        private readonly PictureBox pic = new PictureBox();
        private readonly Label lblPicHint = new Label();   // 没有预览图时的占位说明（空白块看不出是坏还是没图）
        private readonly Label lblTitle2 = new Label();
        private readonly Label lblMeta = new Label();
        private readonly Label lblTags = new Label();
        private readonly Label lblWhere = new Label();
        private readonly TextBox txtDesc = new TextBox();
        private readonly FlatButton btnLang = new FlatButton();
        private readonly FlatButton btnOpen = new FlatButton();
        private readonly Label lblTip = new Label();
        private readonly Label lblCat = new Label();
        private WsItem detailItem;                   // 详情面板当前展示的物品
        private bool showZh = true;                  // true = 显示译文（默认），false = 原文
        private int detailToken;                     // 切行递增：旧线程回来时发现号不对就不改界面
        private bool filling;                        // 正在批量填表：这时的勾选变化别去刷状态栏
        private bool sampleMode;                     // 截图自检用的样例数据：不联网

        // ---- 下载队列 ----
        private readonly FlatButton btnAll = new FlatButton();
        private readonly FlatButton btnNone = new FlatButton();
        private readonly FlatButton btnRetry = new FlatButton();
        private readonly List<WsItem> failed = new List<WsItem>();

        private const int M = 16;                    // 设计值：外边距
        private const int Gap = 12;
        private const int DetailW = 384;             // 详情栏定宽

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
        private readonly FlatButton btnPrev = new FlatButton();     // 翻回去：原来只能往后翻，翻过头就回不来
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
            Text = "创意工坊 · 求生之路 2";
            Theme.UseDpi(this);
            ClientSize = Dpi.Sz(1080, 700);
            MinimumSize = Dpi.Sz(940, 600);
            Font = Theme.UI;
            BackColor = Theme.Bg;
            StartPosition = FormStartPosition.CenterScreen;   // 独立窗口，不从主窗口取位置
            MinimizeBox = false;
            ShowInTaskbar = true;                             // 独立窗口，任务栏里得有自己的格子

            // ---------------- 左栏：找 + 选 + 下 ----------------
            lblTip.Font = Theme.UISmall;
            lblTip.ForeColor = Theme.Ink2;
            lblTip.AutoSize = false;
            lblTip.AutoEllipsis = true;
            lblTip.Text = "选分类直接浏览，或粘贴物品 / 合集链接（...?id=123456）。"
                        + "点一行看右边介绍（自动翻中文），勾上再下载。";
            Controls.Add(lblTip);

            lblCat.Text = "分类";
            lblCat.Font = Theme.UISmall;
            lblCat.ForeColor = Theme.Ink2;
            lblCat.AutoSize = false;
            Controls.Add(lblCat);

            cboCat.DropDownStyle = ComboBoxStyle.DropDownList;
            Theme.Dress(cboCat);
            cboCat.SelectedIndexChanged += delegate
            {
                CatChoice c = cboCat.SelectedItem as CatChoice;
                if (c != null) txtQuery.Text = c.Query;
            };
            Controls.Add(cboCat);

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
            cboSort.Items.Add(new CatChoice { Label = "最多订阅", Sort = "mostsubscribed" });
            cboSort.Items.Add(new CatChoice { Label = "最高评价", Sort = "toprated" });
            cboSort.Items.Add(new CatChoice { Label = "最新", Sort = "newestfirst" });
            cboSort.Items.Add(new CatChoice { Label = "趋势", Sort = "trend" });
            cboSort.SelectedIndex = 0;
            Controls.Add(cboSort);

            box.Multiline = true;
            box.ScrollBars = ScrollBars.Vertical;
            box.AcceptsReturn = true;
            Theme.Dress(box);
            Controls.Add(box);

            btnParse.Text = "解析";
            btnParse.Primary = true;
            btnParse.Click += delegate { Parse(); };
            Controls.Add(btnParse);

            btnPaste.Text = "从剪贴板";
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

            btnClear.Text = "清空";
            btnClear.Click += delegate
            {
                if (busy) return;
                box.Clear();
                items.Clear();
                lv.Items.Clear();
                ClearDetail();
                SetStatus("就绪");
                btnStart.Enabled = false;
            };
            Controls.Add(btnClear);

            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.CheckBoxes = true;                    // 下载队列 = 勾选的那些
            lv.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            lv.BorderStyle = BorderStyle.FixedSingle;
            lv.BackColor = Theme.Surface;
            lv.ForeColor = Theme.Ink;
            lv.Font = Theme.UISmall;
            lv.GridLines = false;
            lv.MultiSelect = true;
            lv.ShowItemToolTips = true;
            lv.Columns.Add("标题", 296);
            lv.Columns.Add("订阅", 58, HorizontalAlignment.Right);
            lv.Columns.Add("更新", 84);
            lv.Columns.Add("大小", 70, HorizontalAlignment.Right);
            lv.Columns.Add("状态", 150);
            lv.SelectedIndexChanged += delegate { FillDetail(); };
            lv.ItemCheck += delegate(object s, ItemCheckEventArgs e)
            {
                // 合集行不是可下载项（它的子项已经展开成行了），勾它只会让人以为能下
                WsItem w = (e.Index >= 0 && e.Index < lv.Items.Count) ? lv.Items[e.Index].Tag as WsItem : null;
                if (w != null && (w.IsCollection || (w.Url.Length == 0 && w.Note.Length == 0)))
                    e.NewValue = CheckState.Unchecked;
            };
            lv.ItemChecked += delegate { UpdatePickCount(); };
            Controls.Add(lv);

            lblStatus.Font = Theme.UISmall;
            lblStatus.ForeColor = Theme.Ink2;
            lblStatus.AutoSize = false;
            lblStatus.AutoEllipsis = true;
            lblStatus.Text = "就绪。先粘贴链接，再点「解析」。";
            Controls.Add(lblStatus);

            // 原生进度条在深色主题下永远是浅色的一条，平时不下载就别摆在那里
            bar.Minimum = 0;
            bar.Maximum = 100;
            bar.Visible = false;
            Controls.Add(bar);

            btnStart.Text = "开始下载";
            btnStart.Primary = true;
            btnStart.Enabled = false;
            btnStart.Click += delegate { StartDownload(); };
            Controls.Add(btnStart);

            btnAll.Text = "全选";
            btnAll.Click += delegate { SetAllChecked(true); };
            Controls.Add(btnAll);

            btnNone.Text = "全不选";
            btnNone.Click += delegate { SetAllChecked(false); };
            Controls.Add(btnNone);

            btnRetry.Text = "重试失败";
            btnRetry.Enabled = false;
            btnRetry.Click += delegate { RetryFailed(); };
            Controls.Add(btnRetry);

            btnMore.Text = "下一页";
            btnMore.Enabled = false;
            btnMore.Click += delegate { DoSearch(page + 1); };
            Controls.Add(btnMore);

            btnPrev.Text = "上一页";
            btnPrev.Enabled = false;
            btnPrev.Click += delegate { if (page > 1) DoSearch(page - 1); };
            Controls.Add(btnPrev);

            btnClose.Text = "关闭";
            btnClose.DialogResult = DialogResult.Cancel;
            Controls.Add(btnClose);
            CancelButton = btnClose;

            // ---------------- 右栏：详情 ----------------
            pic.SizeMode = PictureBoxSizeMode.Zoom;
            pic.BackColor = Theme.SurfaceAlt;
            pic.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(pic);

            lblPicHint.Text = "（左边选一条看介绍）";
            lblPicHint.TextAlign = ContentAlignment.MiddleCenter;
            lblPicHint.Font = Theme.UISmall;
            lblPicHint.ForeColor = Theme.Ink2;
            lblPicHint.BackColor = Color.Transparent;
            lblPicHint.AutoSize = false;
            pic.Controls.Add(lblPicHint);           // 挂在 pic 里，父控件总会把它画在自己的图上面

            lblTitle2.Font = Theme.UIBold;
            lblTitle2.ForeColor = Theme.Ink;
            lblTitle2.AutoSize = false;
            lblTitle2.Text = "（左边选一条看介绍）";
            Controls.Add(lblTitle2);

            lblMeta.Font = Theme.UISmall;
            lblMeta.ForeColor = Theme.Ink2;
            lblMeta.AutoSize = false;
            lblMeta.AutoEllipsis = true;
            Controls.Add(lblMeta);

            lblTags.Font = Theme.UISmall;
            lblTags.ForeColor = Theme.Info;
            lblTags.AutoSize = false;
            lblTags.AutoEllipsis = true;
            Controls.Add(lblTags);

            lblWhere.Font = Theme.UISmall;
            lblWhere.ForeColor = Theme.Ok;
            lblWhere.AutoSize = false;
            lblWhere.AutoEllipsis = true;
            Controls.Add(lblWhere);

            txtDesc.Multiline = true;
            txtDesc.ReadOnly = true;
            txtDesc.ScrollBars = ScrollBars.Vertical;
            txtDesc.WordWrap = true;
            txtDesc.BorderStyle = BorderStyle.FixedSingle;
            Theme.Dress(txtDesc);
            Controls.Add(txtDesc);

            btnLang.Text = "看原文";
            btnLang.Click += delegate { showZh = !showZh; FillDetail(); };
            Controls.Add(btnLang);

            btnOpen.Text = "在浏览器打开";
            btnOpen.Click += delegate { OpenInBrowser(); };
            Controls.Add(btnOpen);

            FormClosing += delegate { cancel = true; };

            // 标题栏和表头/滚动条交给主题：这两个必须在句柄建好之后打招呼
            Load += delegate { Theme.ApplyChrome(this); Theme.DeepNative(this); };
            Shown += delegate { Theme.ApplyChrome(this); Theme.DeepNative(this); };
            Resize += delegate { ApplyLayout(); };

            ApplyLayout();
            FillCats();
            cboCat.SelectedIndex = 0;
        }

        /// <summary>设计值 → 物理像素。C# 5 没有局部函数，摊成一个小方法，省得每行写 Dpi.S。</summary>
        private static int S(int v) { return Dpi.S(v); }

        /// <summary>
        /// 全部坐标集中在这里算：左栏吃剩余宽度、右栏定宽 384。
        /// 窗口可缩放（原来是写死 680x520 的固定框，合集一展开就只能在 7 行里翻）。
        /// 用物理像素 + Dpi.S 换算，和主窗口 ApplyResponsive 同一套路子。
        /// </summary>
        private void ApplyLayout()
        {
            int W = ClientSize.Width, H = ClientSize.Height;
            int m = S(M), gap = S(Gap);
            int dw = S(DetailW);
            int detailX = W - m - dw;
            int leftW = Math.Max(S(320), detailX - gap - m);

            int y = S(10);
            lblTip.SetBounds(m, y, leftW, S(20));

            y = S(36);
            lblCat.SetBounds(m, y + S(4), S(36), S(20));
            cboCat.SetBounds(m + S(40), y, S(150), S(24));
            int sortW = S(96), searchW = S(64);
            int queryX = m + S(40) + S(150) + S(8);
            int sortX = detailX - gap - sortW;
            int searchX = sortX - S(8) - searchW;
            int queryW = Math.Max(S(110), searchX - S(8) - queryX);
            txtQuery.SetBounds(queryX, y, queryW, S(24));
            btnSearch.SetBounds(searchX, S(32), searchW, S(32));
            cboSort.SetBounds(sortX, y, sortW, S(24));

            box.SetBounds(m, S(76), leftW, S(56));

            int by = S(140), bh = S(32);
            btnParse.SetBounds(m, by, S(88), bh);
            btnPaste.SetBounds(m + S(96), by, S(96), bh);
            btnClear.SetBounds(m + S(200), by, S(64), bh);

            // 底栏：左起主操作，右边收尾
            int btnY = H - S(16) - S(32);
            // 纵向从下往上排，别用「减一个常数」硬凑：按钮行 → 进度条 → 状态行 → 列表
            int barTop = btnY - S(8) - S(10);
            int statusTop = barTop - S(8) - S(18);
            int contentBottom = btnY - S(8);            // 左右两栏共用的那条底线
            btnStart.SetBounds(m, btnY, S(112), bh);
            btnAll.SetBounds(m + S(120), btnY, S(56), bh);
            btnNone.SetBounds(m + S(184), btnY, S(72), bh);
            btnRetry.SetBounds(m + S(264), btnY, S(88), bh);
            // 两个翻页键在左栏里右对齐：左栏多窄都不会压到「重试失败」上
            int pageW = S(64), pageGap = S(6);
            btnMore.SetBounds(detailX - gap - pageW, btnY, pageW, bh);
            btnPrev.SetBounds(detailX - gap - pageW - pageGap - pageW, btnY, pageW, bh);
            btnClose.SetBounds(W - m - S(88), btnY, S(88), bh);

            lblStatus.SetBounds(m, statusTop, leftW, S(18));
            bar.SetBounds(m, barTop, leftW, S(10));

            int listTop = S(180);
            int listH = Math.Max(S(120), statusTop - S(8) - listTop);
            lv.SetBounds(m, listTop, leftW, listH);

            // 右栏详情
            pic.SetBounds(detailX, S(36), dw, S(196));
            lblPicHint.SetBounds(0, 0, pic.Width, pic.Height);   // 子控件坐标：相对 pic
            lblTitle2.SetBounds(detailX, S(240), dw, S(40));
            lblMeta.SetBounds(detailX, S(282), dw, S(18));
            lblTags.SetBounds(detailX, S(302), dw, S(18));
            lblWhere.SetBounds(detailX, S(322), dw, S(18));
            int descTop = S(346);
            txtDesc.SetBounds(detailX, descTop, dw, Math.Max(S(80), contentBottom - descTop));
            btnLang.SetBounds(detailX, btnY, S(88), bh);
            btnOpen.SetBounds(detailX + S(96), btnY, S(120), bh);

            // 列宽随左栏重算：标题吃掉剩余，其余定宽
            int fixedW = S(58) + S(84) + S(70) + S(150) + S(4);
            if (lv.Columns.Count == 5)
                lv.Columns[0].Width = Math.Max(S(140), leftW - fixedW - S(24));
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
            filling = true;
            lv.BeginUpdate();
            try
            {
                lv.Items.Clear();
                foreach (WsItem it in items)
                {
                    ListViewItem li = new ListViewItem(it.Title.Length == 0 ? "(无标题)" : it.Title);
                    li.SubItems.Add(WsText.Compact(it.Subs));
                    li.SubItems.Add(WsText.Ago(it.Updated));
                    li.SubItems.Add(it.Size > 0 ? Core.Human(it.Size) : "-");
                    string st = it.IsCollection ? it.State : (it.Note.Length > 0 ? it.Note : it.State);
                    li.SubItems.Add(st);
                    li.Tag = it;

                    if (it.IsCollection) li.ForeColor = Theme.Ink3;
                    else if (it.Note.Length > 0) li.ForeColor = Theme.Warn;

                    // 默认一个都不勾：整页默认勾上会让「开始下载」一点就下一屏
                    li.Checked = false;

                    string tipText = "ID " + it.Id;
                    if (it.Tags.Length > 0) tipText += "\r\n标签：" + it.Tags;
                    if (it.Where.Length > 0) tipText += "\r\n会装到：" + it.Where;
                    else tipText += "\r\n点一行，右边看介绍（自动翻中文）";
                    li.ToolTipText = tipText;

                    lv.Items.Add(li);
                }
            }
            finally
            {
                lv.EndUpdate();
                filling = false;
            }
            UpdatePickCount();
        }

        private void SetRowState(WsItem it, string s)
        {
            it.State = s;
            Ui(delegate
            {
                foreach (ListViewItem li in lv.Items)
                {
                    if (li.Tag != it) continue;
                    li.SubItems[4].Text = s;
                    break;
                }
            });
        }

        // ------------------------------------------------ 下载队列

        /// <summary>全选 / 全不选：只勾真能下的（合集行、没直链的、已经下过的都跳过）。</summary>
        private void SetAllChecked(bool on)
        {
            if (busy) return;
            filling = true;
            lv.BeginUpdate();
            try
            {
                foreach (ListViewItem li in lv.Items)
                {
                    WsItem it = li.Tag as WsItem;
                    if (it == null || it.IsCollection || it.Url.Length == 0) continue;
                    li.Checked = on;
                }
            }
            finally
            {
                lv.EndUpdate();
                filling = false;
            }
            UpdatePickCount();
        }

        /// <summary>按钮文案跟着勾选数走：「开始下载」说清到底下几个。</summary>
        private void UpdatePickCount()
        {
            int picked = 0;
            foreach (ListViewItem li in lv.CheckedItems)
            {
                WsItem it = li.Tag as WsItem;
                if (it != null && !it.IsCollection && it.Url.Length > 0 && !it.Ok) picked++;
            }
            int can = 0;
            foreach (WsItem it in items)
                if (!it.IsCollection && it.Url.Length > 0 && !it.Ok) can++;

            btnStart.Enabled = can > 0 && !busy;
            btnStart.Text = picked > 0 ? "下载勾选的 " + picked + " 个" : "开始下载";
            btnRetry.Enabled = failed.Count > 0 && !busy;

            if (!filling && !busy && can > 0 && picked == 0)
                SetStatus("可下载 " + can + " 个：勾上要下的（不勾 = 全部下载）；点一行看右侧介绍。");
        }

        private void RetryFailed()
        {
            if (busy || failed.Count == 0) return;
            foreach (WsItem it in failed) it.State = "待下载";
            failed.Clear();
            btnRetry.Enabled = false;
            StartDownload(true);
        }

        // ------------------------------------------------ 详情面板

        /// <summary>
        /// 右侧详情：先上已经有的东西（标题 / 元信息 / 缓存译文），
        /// 预览图和翻译各走后台 —— 网络慢的时候不能把界面卡在那儿等。
        /// </summary>
        private void FillDetail()
        {
            WsItem it = lv.SelectedItems.Count > 0 ? lv.SelectedItems[0].Tag as WsItem : null;
            if (it == null) { ClearDetail(); return; }

            detailItem = it;
            int token = ++detailToken;                 // 旧线程回来时号不对就不动界面

            lblTitle2.Text = it.Title.Length > 0 ? it.Title : "(无标题)";
            string meta = "";
            if (it.Subs > 0) meta += "订阅 " + WsText.Compact(it.Subs);
            if (it.Updated > 0) meta += (meta.Length > 0 ? "  ·  " : "") + "更新 " + WsText.Ago(it.Updated);
            if (it.Size > 0) meta += (meta.Length > 0 ? "  ·  " : "") + Core.Human(it.Size);
            if (it.IsCollection) meta += (meta.Length > 0 ? "  ·  " : "") + "这是合集";
            if (meta.Length == 0) meta = "物品 " + it.Id;
            lblMeta.Text = meta;
            lblTags.Text = it.Tags;
            lblWhere.Text = it.Where.Length > 0 ? "会装到：" + it.Where : "";
            btnOpen.Enabled = true;

            string clean = WsText.Clean(it.Desc);
            string cached = WsTranslate.ReadCache(it.Id, it.Updated);
            txtDesc.Text = WsText.ToDisplay((showZh && cached != null) ? cached : (clean.Length > 0 ? clean : "（这个物品没有写介绍）"));
            btnLang.Text = showZh ? "看原文" : "看译文";
            btnLang.Enabled = clean.Length > 0;

            if (pic.Image != null) { Image old = pic.Image; pic.Image = null; old.Dispose(); }
            lblPicHint.Visible = true;
            lblPicHint.Text = sampleMode ? "（样例模式，不加载预览图）" : "（正在加载预览图…）";

            if (sampleMode) return;                 // 截图自检：样例数据不联网，免得截图等网络

            // 控件尺寸要在 UI 线程先取好：后台线程里读控件属性属于跨线程访问
            int pw = pic.Width, ph = pic.Height;

            Thread th = new Thread(delegate()
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
                    if (token != detailToken) { if (img != null) img.Dispose(); return; }   // 已经切走
                    if (img != null)
                    {
                        if (pic.Image != null) pic.Image.Dispose();
                        pic.Image = img;
                        lblPicHint.Visible = false;
                    }
                    else lblPicHint.Text = "（没有预览图）";
                    if (showZh && zh != null && zh.Length > 0) txtDesc.Text = WsText.ToDisplay(zh);
                    else if (showZh && err != null) SetStatus(err);
                });
            });
            th.IsBackground = true;
            th.Priority = ThreadPriority.BelowNormal;
            th.Start();
        }

        private void ClearDetail()
        {
            detailItem = null;
            detailToken++;
            if (pic.Image != null) { Image old = pic.Image; pic.Image = null; old.Dispose(); }
            lblTitle2.Text = "（左边选一条看介绍）";
            lblMeta.Text = "";
            lblTags.Text = "";
            lblWhere.Text = "";
            txtDesc.Text = "";
            lblPicHint.Visible = true;
            lblPicHint.Text = "（左边选一条看介绍）";
            btnLang.Enabled = false;
            btnOpen.Enabled = false;
        }

        private void OpenInBrowser()
        {
            string id = detailItem != null ? detailItem.Id : "";
            if (id.Length == 0) return;
            try
            {
                System.Diagnostics.Process.Start("https://steamcommunity.com/sharedfiles/filedetails/?id=" + id);
            }
            catch (Exception ex) { SetStatus("打不开浏览器：" + ex.Message); }
        }

        /// <summary>
        /// 下载完认一下这些包改了什么：复用库里的识别结果，把「会装到哪」写回行提示和右栏。
        /// 只做说明，不改任何文件 —— 「只装这个」还是回主界面的树上点，免得这里绕开单选取代的规则。
        /// </summary>
        private void Annotate(List<WsItem> done)
        {
            if (done == null || done.Count == 0) return;
            try { Core.ScanMods(); }
            catch (Exception ex) { Core.Log("WARN", "工坊下载后扫库失败: " + ex.Message); return; }

            Dictionary<string, ModItem> byName = new Dictionary<string, ModItem>(StringComparer.OrdinalIgnoreCase);
            foreach (ModItem m in Core.ModList)
                if (!string.IsNullOrEmpty(m.Name) && !byName.ContainsKey(m.Name)) byName[m.Name] = m;

            int hit = 0;
            foreach (WsItem it in done)
            {
                if (string.IsNullOrEmpty(it.SavedAs)) continue;
                ModItem m;
                if (!byName.TryGetValue(it.SavedAs, out m)) continue;
                hit++;
                string where = m.Cat;
                if (!string.IsNullOrEmpty(m.Sub)) where += " · " + m.Sub;
                if (!string.IsNullOrEmpty(m.Subject)) where += " → " + m.Subject;
                it.Where = where;
                string shortWhere = m.Subject.Length > 0 ? m.Subject : m.Cat;
                SetRowState(it, "已下载 · " + shortWhere);
                foreach (ListViewItem li in lv.Items)
                {
                    if (li.Tag != it) continue;
                    li.ToolTipText = "ID " + it.Id + "\r\n会装到：" + where;
                    break;
                }
            }
            Core.Log("INFO", "工坊：识别了 " + hit + "/" + done.Count + " 个下载物，写回「会装到哪」");
            if (detailItem != null) lblWhere.Text = detailItem.Where.Length > 0 ? "会装到：" + detailItem.Where : "";
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
            // 骨架行：搜索中列表不再是空的，进度看得见
            for (int i = 0; i < 8; i++)
            {
                ListViewItem ph = new ListViewItem("…………");
                ph.SubItems.Add("");
                ph.SubItems.Add("");
                ph.SubItems.Add("加载中");
                ph.ForeColor = Theme.Ink3;
                lv.Items.Add(ph);
            }
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
                    btnPrev.Enabled = p > 1;
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

        private void StartDownload() { StartDownload(false); }

        /// <summary>retryOnly=true 时只跑上次失败的那几个（状态列里有原因，重试不用重新解析）。</summary>
        private void StartDownload(bool retryOnly)
        {
            if (busy) return;
            List<WsItem> todo = new List<WsItem>();
            if (!retryOnly) failed.Clear();

            if (retryOnly)
            {
                foreach (WsItem it in failed)
                    if (!it.IsCollection && it.Url.Length > 0) todo.Add(it);
            }
            else
            {
                // 勾选优先（这才是「下载队列」）：勾了就是要下这几个，哪怕没选中行。
                // 一个都没勾 → 退到选中行 → 再退到「全部可下载的」（粘贴模式的老行为）。
                HashSet<WsItem> picked = new HashSet<WsItem>();
                foreach (ListViewItem li in lv.CheckedItems)
                {
                    WsItem w = li.Tag as WsItem;
                    if (w != null) picked.Add(w);
                }
                if (picked.Count > 0)
                {
                    foreach (WsItem it in items)
                        if (picked.Contains(it) && !it.IsCollection && it.Url.Length > 0 && !it.Ok) todo.Add(it);
                }
                else if (lv.SelectedItems.Count > 0)
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
            }
            if (todo.Count == 0)
            {
                SetStatus(retryOnly ? "上次失败的都已经下完了。" : "没有可下载的物品：要么都下完了，要么都没有直链。");
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
                List<WsItem> done = new List<WsItem>();
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
                        it.SavedAs = Path.GetFileName(dest);
                        Workshop.RememberDownload(it.Id, it.SavedAs);
                        done.Add(it);
                        SetRowState(it, "已下载 " + Core.Human(it.Size) + (rn != null ? "，已改名 " + Clip(rn, 16) : ""));
                        Core.Log("OK", "工坊：已下载 " + it.Id + " → " + dest);
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        if (!failed.Contains(it)) failed.Add(it);
                        string why = ex.Message;
                        if (why.Length > 24) why = why.Substring(0, 24) + "…";
                        SetRowState(it, "失败：" + why);
                        Core.Log("FAIL", "工坊下载失败 " + it.Id + ": " + ex.Message);
                    }
                }

                int okFinal = ok, failFinal = fail, skipFinal = skip;
                bool cancelled = cancel;
                List<WsItem> doneFinal = done;
                Ui(delegate
                {
                    busy = false;
                    btnParse.Enabled = true;
                    btnStart.Enabled = true;
                    bar.Value = 0;
                    bar.Visible = false;
                    DownloadedCount += okFinal;
                    Annotate(doneFinal);
                    UpdatePickCount();
                    btnStart.Text = "开始下载";
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
            a.Subs = 12400;
            a.Updated = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds - 86400 * 23;
            a.Tags = "Tank · Bloater · 角色模型";
            a.Desc = "[h1]The After[/h1]\n[b]Tank and Bloater[/b] replacement based on The Last of Us.\n\n"
                   + "[list][*]Replaces both tank and boomer with clicker-style models\n"
                   + "[*]Custom sounds included\n[*]Works with most server mods[/list]\n\n"
                   + "Credits to the original ripper. Report bugs in the comments.";
            a.Url = "https://cdn.steamusercontent.com/ugc/x/";
            a.FileName = "myl4d2addons/tank bloater.vpk";
            a.State = "待下载";
            items.Add(a);

            WsItem b = new WsItem();
            b.Id = "3832308538";
            b.Title = "Chargers get staggered by blunt melee [vscript]";
            b.Size = 2622;
            b.Subs = 860;
            b.Updated = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds - 86400 * 120;
            b.Tags = "vscript · 玩法";
            b.Desc = "[b]Small vscript tweak[/b] that makes chargers flinch when hit with a blunt weapon.";
            b.Url = "https://cdn.steamusercontent.com/ugc/y/";
            b.FileName = "new folder (4).vpk";
            b.State = "已下载 3 KB";
            items.Add(b);

            sampleMode = true;
            FillList();
            if (lv.Items.Count > 1) lv.Items[1].Selected = true;     // 截图里右栏要有内容
            SetStatus("解析到 239 条（1 个合集展开 237 个子项），可下载 2 个，0 条拿不到直链。");
            btnStart.Enabled = true;
        }
    }
}

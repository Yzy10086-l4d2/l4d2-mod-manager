using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Installer
{
    /// <summary>
    /// 工坊正文清洗：Steam 的 file_description 是 BBCode（[b] [h1] [url] [img] [list] [*]），
    /// 偶尔混着一点 HTML。直接往控件里塞会看到一堆方括号标签，所以先洗成可读纯文本。
    ///
    /// 刻意不做富文本渲染：正文常有几百行、还带表格，自绘富文本的收益远小于它的坑。
    /// 保留的只有「段落 / 列表项 / 小标题」三种结构，够读就行。
    /// </summary>
    internal static class WsText
    {
        public static string Clean(string bb)
        {
            if (string.IsNullOrEmpty(bb)) return "";
            string s = bb.Replace("\r\n", "\n").Replace("\r", "\n");

            // 图片：详情面板已经单独显示预览图了，正文里只留个占位，别让一屏全是空行
            s = Regex.Replace(s, @"\[img\][^\[\]]*\[/img\]", "［图］", RegexOptions.IgnoreCase);

            // 链接：留文字，去地址
            s = Regex.Replace(s, @"\[url=[^\]]*\](.*?)\[/url\]", "$1", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            s = Regex.Replace(s, @"\[url\](.*?)\[/url\]", "$1", RegexOptions.IgnoreCase | RegexOptions.Singleline);

            // 小标题：[h1]xxx[/h1] → ■ xxx
            s = Regex.Replace(s, @"\[h([1-6])\](.*?)\[/h\1\]", "\n■ $2\n",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            // 列表：[*] → ·，[list] 本身换成换行
            s = Regex.Replace(s, @"\[\*\]", "· ", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\[/?list[^\]]*\]", "\n", RegexOptions.IgnoreCase);

            // 成对样式标签：去标签留内容
            s = Regex.Replace(s, @"\[/?(b|i|u|s|strike|quote|code|noparse|center|spoiler|pre|sub|sup|table|tr|td|th|hr)\]",
                "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\[/?(b|i|u|s|strike|quote|code|center|spoiler|size|color|font)(=[^\]]*)?\]",
                "", RegexOptions.IgnoreCase);

            // 剩下认不出来的方括号标签一并清掉（宁可少显示，也别把标签当正文）
            s = Regex.Replace(s, @"\[/?[a-zA-Z][a-zA-Z0-9_]{0,15}(\s*=[^\]]*)?\]", "");

            // HTML 残留
            s = Regex.Replace(s, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"</p\s*>", "\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<[^>]{1,300}>", "");
            s = s.Replace("&nbsp;", " ").Replace("&amp;", "&")
                 .Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"");

            // 收尾：行尾空白、连续空行压成一个空行
            s = Regex.Replace(s, @"[ \t]+\n", "\n");
            s = Regex.Replace(s, @"\n{3,}", "\n\n");
            return s.Trim();
        }

        /// <summary>正文是不是本来就是中文（中文比英文多就不用翻）。</summary>
        public static bool MostlyChinese(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            int cjk = 0, latin = 0;
            foreach (char c in s)
            {
                if (c >= 0x4E00 && c <= 0x9FFF) cjk++;
                else if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) latin++;
            }
            return cjk > 0 && cjk * 2 > latin;
        }

        /// <summary>
        /// 给界面显示的换行处理：WinForms 的 TextBox 只认 CRLF。
        /// 只塞 LF 的话整段会挤成一行，列表符号和段落全丢 —— 详情面版看着像一段乱码。
        /// </summary>
        public static string ToDisplay(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\r\n", "\n").Replace("\n", "\r\n");
        }

        /// <summary>缓存文件名用的 ID：工坊 ID 本身就是纯数字，这里只做一次保险。</summary>
        public static string SafeId(string id)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in (id ?? ""))
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            if (sb.Length == 0) return "x";
            return sb.Length <= 24 ? sb.ToString() : sb.ToString(0, 24);
        }

        /// <summary>把 Unix 秒变成「3 天前 / 2 个月前」这种，比一串时间戳好读。</summary>
        public static string Ago(long unix)
        {
            if (unix <= 0) return "";
            double days = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(unix)).TotalDays;
            if (days < 0) return "刚刚";
            if (days < 1) return "今天";
            if (days < 2) return "昨天";
            if (days < 30) return (int)days + " 天前";
            if (days < 365) return (int)(days / 30) + " 个月前";
            double y = days / 365.0;
            return (y < 2 ? "1 年前" : (int)y + " 年前");
        }

        /// <summary>订阅数：3000 → 3.0k，省得一列数字挤成一团。</summary>
        public static string Compact(long n)
        {
            if (n <= 0) return "";
            if (n < 1000) return n.ToString();
            if (n < 1000000) return (n / 1000.0).ToString("0.#") + "k";
            return (n / 1000000.0).ToString("0.#") + "M";
        }
    }

    /// <summary>
    /// 在线翻译（微软 Edge 的免 key 端点）+ 磁盘缓存。
    ///
    /// 为什么走这个：Google 那个免费端点在部分网络下直接被墙，而 Steam 用户多半在国内；
    /// 这个端点取 token 不要 key，翻出来的中文也够读。风险是它非官方，所以：
    ///   · 任何失败都静默退回原文，界面只标一句「翻译服务不可用」，绝不让详情开不出来；
    ///   · 译文落盘按「物品 ID + 上次更新时间」缓存，作者改过介绍会自动失效重翻；
    ///   · 断网时缓存仍然能看，第二次点同一行不会再发请求。
    /// </summary>
    internal static class WsTranslate
    {
        const string AuthUrl = "https://edge.microsoft.com/translate/auth";
        const string ApiUrl = "https://api-edge.cognitive.microsofttranslator.com/translate"
                            + "?api-version=3.0&from=en&to=zh-Hans";
        const string Ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) L4D2Setup/2.52";
        const int MaxChunk = 2600;          // 一段一次请求，长介绍拆开翻，避免一个请求几万字符超时

        static string token = "";
        static DateTime tokenAt = DateTime.MinValue;

        public static string CacheDir { get { return Path.Combine(Core.LogDir, "workshop-zh"); } }

        static string CachePath(string id)
        {
            return Path.Combine(CacheDir, "zh-" + WsText.SafeId(id) + ".txt");
        }

        // 自检用：离线验缓存行为，不碰网络
        internal static void WriteCacheForTest(string id, long updated, string zh) { WriteCache(id, updated, zh); }
        internal static string CachePathForTest(string id) { return CachePath(id); }

        /// <summary>读缓存；作者改过介绍（time_updated 变了）就当没缓存。</summary>
        public static string ReadCache(string id, long updated)
        {
            try
            {
                string p = CachePath(id);
                if (!File.Exists(p)) return null;
                string s = File.ReadAllText(p, Encoding.UTF8);
                int nl = s.IndexOf('\n');
                if (nl <= 0) return null;
                if (!s.StartsWith("v1|" + updated, StringComparison.Ordinal)) return null;
                string body = s.Substring(nl + 1).Trim();
                return body.Length > 0 ? body : null;
            }
            catch { return null; }
        }

        static void WriteCache(string id, long updated, string zh)
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                File.WriteAllText(CachePath(id), "v1|" + updated + "\n" + zh, new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>
        /// 翻成中文。返回空串 = 没翻成（err 里给原因），调用方该显示原文。
        /// 已经是中文的正文直接原样返回。
        /// </summary>
        public static string Translate(string id, long updated, string text, out string err)
        {
            err = null;
            if (string.IsNullOrEmpty(text)) return "";
            string hit = ReadCache(id, updated);
            if (hit != null) return hit;
            if (WsText.MostlyChinese(text)) return text;

            try
            {
                List<string> parts = Chunk(text, MaxChunk);
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < parts.Count; i++)
                {
                    string one = Call(parts[i]);
                    if (one == null)
                    {
                        err = i == 0 ? "翻译服务暂时不可用，下面显示原文" : "后面几段没翻完";
                        return sb.Length > 0 ? sb.ToString() : "";
                    }
                    if (sb.Length > 0) sb.Append("\n\n");
                    sb.Append(one.Trim());
                }
                string all = sb.ToString().Trim();
                if (all.Length > 0) WriteCache(id, updated, all);
                return all;
            }
            catch (Exception ex) { err = ex.Message; return ""; }
        }

        /// <summary>按段落切块：尽量在空行处断开，单段超长才硬切。</summary>
        static List<string> Chunk(string text, int max)
        {
            List<string> outp = new List<string>();
            string[] paras = text.Replace("\r\n", "\n").Split(new string[] { "\n\n" }, StringSplitOptions.None);
            StringBuilder cur = new StringBuilder();
            foreach (string raw in paras)
            {
                string p = raw.Trim();
                if (p.Length == 0) continue;
                if (p.Length > max)
                {
                    if (cur.Length > 0) { outp.Add(cur.ToString()); cur.Length = 0; }
                    for (int i = 0; i < p.Length; i += max)
                        outp.Add(p.Substring(i, Math.Min(max, p.Length - i)));
                    continue;
                }
                if (cur.Length > 0 && cur.Length + p.Length + 2 > max)
                {
                    outp.Add(cur.ToString());
                    cur.Length = 0;
                }
                if (cur.Length > 0) cur.Append("\n\n");
                cur.Append(p);
            }
            if (cur.Length > 0) outp.Add(cur.ToString());
            return outp;
        }

        static string Token()
        {
            if (token.Length > 0 && (DateTime.UtcNow - tokenAt).TotalMinutes < 8) return token;
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(AuthUrl);
            req.Method = "GET";
            req.UserAgent = Ua;
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;
            using (HttpWebResponse r = (HttpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
                token = sr.ReadToEnd().Trim().Trim('"');
            tokenAt = DateTime.UtcNow;
            return token;
        }

        static string Call(string text)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    string tk = Token();
                    if (tk.Length < 20) return null;

                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(ApiUrl);
                    req.Method = "POST";
                    req.ContentType = "application/json; charset=UTF-8";
                    req.UserAgent = Ua;
                    req.Headers.Add("Authorization", "Bearer " + tk);
                    req.Timeout = 25000;
                    req.ReadWriteTimeout = 25000;
                    byte[] body = Encoding.UTF8.GetBytes("[{\"Text\":\"" + JEsc(text) + "\"}]");
                    req.ContentLength = body.Length;
                    using (Stream s = req.GetRequestStream()) s.Write(body, 0, body.Length);

                    string json;
                    using (HttpWebResponse r = (HttpWebResponse)req.GetResponse())
                    using (StreamReader sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
                        json = sr.ReadToEnd();

                    List<string> objs = Workshop.SplitObjects(json, "translations");
                    if (objs.Count == 0) return null;
                    string outText = Workshop.JStr(objs[0], "text");
                    return outText.Length > 0 ? outText : null;
                }
                catch (WebException we)
                {
                    HttpWebResponse hr = we.Response as HttpWebResponse;
                    // token 过期（401）重取一次就够，别循环
                    if (hr != null && (int)hr.StatusCode == 401 && attempt == 0) { token = ""; continue; }
                    Core.Log("WARN", "工坊翻译失败: " + we.Message);
                    return null;
                }
                catch (Exception ex)
                {
                    Core.Log("WARN", "工坊翻译失败: " + ex.Message);
                    return null;
                }
            }
            return null;
        }

        static string JEsc(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length + 16);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>工坊预览图：下载一次、落盘缓存，之后秒开。失败返回 null，界面画占位色块。</summary>
    internal static class WsThumb
    {
        const string Ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) L4D2Setup/2.52";

        public static string CacheDir { get { return Path.Combine(Core.LogDir, "workshop-img"); } }

        static string CachePath(string id)
        {
            return Path.Combine(CacheDir, "img-" + WsText.SafeId(id) + ".bin");
        }

        public static Image Load(string url, string id, int maxW, int maxH)
        {
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(id) || maxW <= 0 || maxH <= 0) return null;
            try
            {
                string p = CachePath(id);
                byte[] data = null;
                if (File.Exists(p))
                {
                    try { data = File.ReadAllBytes(p); }
                    catch { data = null; }
                }
                if (data == null || data.Length < 64)
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                    req.Method = "GET";
                    req.UserAgent = Ua;
                    req.Timeout = 15000;
                    req.ReadWriteTimeout = 15000;
                    using (HttpWebResponse r = (HttpWebResponse)req.GetResponse())
                    using (Stream s = r.GetResponseStream())
                    using (MemoryStream ms = new MemoryStream())
                    {
                        byte[] buf = new byte[8192];
                        int n;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                        data = ms.ToArray();
                    }
                    try
                    {
                        Directory.CreateDirectory(CacheDir);
                        File.WriteAllBytes(p, data);
                    }
                    catch { }
                }

                // Image.FromStream 要求流活着，所以就地复制成独立 Bitmap 再交给界面
                using (MemoryStream ms2 = new MemoryStream(data))
                using (Image raw = Image.FromStream(ms2))
                {
                    double k = Math.Min((double)maxW / raw.Width, (double)maxH / raw.Height);
                    if (k > 1) k = 1;
                    int w = Math.Max(1, (int)(raw.Width * k));
                    int h = Math.Max(1, (int)(raw.Height * k));
                    return new Bitmap(raw, w, h);
                }
            }
            catch (Exception ex)
            {
                Core.Log("WARN", "工坊预览图失败: " + ex.Message);
                return null;
            }
        }
    }
}

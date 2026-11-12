using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace Installer
{
    /// <summary>
    /// 中英双语。中文是源码里的母语，英文是显示层翻译：
    ///
    /// - 所有面向用户的文案都过 <see cref="S"/>，词表里没有就原样返回中文（不会出现空白或乱码）；
    /// - **词表只用于显示**：分类名、位置名同时是内部键（CatPrefs 用它存偏好、去重用它比较），
    ///   所以只在画到屏幕上那一刻翻译，绝不能在数据层翻译；
    /// - 日志、自检输出、控制台保持中文 —— 那是给排障用的，翻了反而不好和源码对照。
    ///
    /// 切换语言不重启进程：保存配置 → 主窗口重建（见 Program 里的循环）。
    /// </summary>
    internal static class Lang
    {
        public const string ZH = "zh";
        public const string EN = "en";

        public static string Cur = ZH;

        public static bool IsEn { get { return Cur == EN; } }

        /// <summary>启动时读配置。配置里是脏值时退回中文。</summary>
        public static void Load()
        {
            Cur = (Core.Cfg != null && string.Equals(Core.Cfg.Lang, EN, StringComparison.OrdinalIgnoreCase)) ? EN : ZH;
        }

        /// <summary>切语言（只改内存与配置，界面重建由调用方负责）。</summary>
        public static void Set(string lang)
        {
            Cur = string.Equals(lang, EN, StringComparison.OrdinalIgnoreCase) ? EN : ZH;
            if (Core.Cfg != null) { Core.Cfg.Lang = Cur; Core.SaveConfig(); }
        }

        // 记录英文模式下依然没翻到的中文，供 --langcover 出清单，一条一条补
        private static readonly HashSet<string> missing = new HashSet<string>(StringComparer.Ordinal);
        public static IEnumerable<string> Missing { get { return missing; } }
        public static int MissingCount { get { return missing.Count; } }
        public static void ClearMissing() { missing.Clear(); }

        /// <summary>取显示文案：英文模式下查词表，查不到就原样返回中文。</summary>
        public static string S(string zh)
        {
            if (string.IsNullOrEmpty(zh) || !IsEn) return zh;
            string en;
            if (En.TryGetValue(zh, out en)) return en;

            // 带前缀/后缀的常见形态（"⚠ 警告"、"名字 · 备注"）单独放行，不当作漏翻
            if (HasCjk(zh)) missing.Add(zh);
            return zh;
        }

        /// <summary>格式化：先翻译模板，再填参数。词表里的英文模板同样用 {0} {1}。</summary>
        public static string F(string zhFmt, params object[] args)
        {
            if (string.IsNullOrEmpty(zhFmt)) return zhFmt;
            string f = S(zhFmt);
            if (args == null || args.Length == 0) return f;
            try { return string.Format(f, args); }
            catch { return f; }        // 模板里花括号不配对时宁可少填，也不要抛异常炸界面
        }

        /// <summary>是否含中文。顺序敏感的判断别用这个，只用于统计漏翻。</summary>
        public static bool HasCjk(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c >= 0x4E00 && c <= 0x9FFF) return true;
            }
            return false;
        }

        /// <summary>下拉框/单选项这类「值 + 显示名」不同的地方，用这个把显示名换掉。</summary>
        public static string[] All(string[] zh) { return zh; }

        // ---------------------------------------------------------------- 词表

        /// <summary>
        /// 词表：键就是源码里的中文原句。这样不用另发明 key，
        /// 搜「某个界面上的中文」就能直接找到对应英文，改起来不用两头对。
        /// </summary>
        private static readonly Dictionary<string, string> En = new Dictionary<string, string>(StringComparer.Ordinal);
        private static bool built;

        public static void EnsureBuilt() { if (!built) { built = true; Build(); } }

        private static void Add(Dictionary<string, string> d, string zh, string en) { d[zh] = en; }

        private static void Build()
        {
            Dictionary<string, string> d = En;

            // ---------- 主窗口：顶栏 ----------
            Add(d, "游戏目录", "Game folder");
            Add(d, "浏览…", "Browse…");
            Add(d, "自动找", "Auto-detect");
            Add(d, "面板", "Panel");
            Add(d, "总览", "Overview");
            Add(d, "工坊", "Workshop");
            Add(d, "设置", "Settings");
            Add(d, "目录不对：里面没有 left4dead2.exe", "Wrong folder: left4dead2.exe not found");

            // ---------- 主窗口：底栏 ----------
            Add(d, "开始安装 →", "Install");
            Add(d, "停止剩余", "Stop");
            Add(d, "管理已装 MOD", "Installed");
            Add(d, "打开游戏目录", "Game folder");
            Add(d, "还原", "Restore");
            Add(d, "就绪", "Ready");
            Add(d, "正在准备…", "Preparing…");
            Add(d, "正在扫库… ", "Scanning library… ");
            Add(d, "正在停止…", "Stopping…");

            // ---------- 左栏：这套会装什么 ----------
            Add(d, "这套会装什么（勾/取消 = 加减 MOD）", "What this set installs (tick = add / untick = remove)");
            Add(d, "已选 0 个", "0 selected");
            Add(d, "搜索", "Search");
            Add(d, "搜 MOD 名字或分类…", "Search name or category…");
            Add(d, "只看已选", "Selected only");
            Add(d, "全选", "Select all");
            Add(d, "全不选", "Select none");
            Add(d, "已装的", "Installed");
            Add(d, "未装的", "Not installed");
            Add(d, "库里还没有", "Not in library");

            // ---------- 左栏：安装选项 ----------
            Add(d, "安装选项", "Install options");
            Add(d, "跳过已有 / 全部重写", "Skip existing / overwrite all");
            Add(d, "跳过", "Skip");
            Add(d, "覆盖重装", "Overwrite");
            Add(d, "换整套（把不在这套里的 MOD 关掉）", "Switch whole set (disable mods outside this one)");
            Add(d, "这一个 vpk：", "This vpk only:");

            // ---------- 左栏：方案 ----------
            Add(d, "方案", "Set");
            Add(d, "方案管理", "Manage sets");
            Add(d, "方案管理…", "Manage sets…");
            Add(d, "存为方案", "Save as set");
            Add(d, "另存为…", "Save as…");
            Add(d, "套用", "Apply");
            Add(d, "复制", "Duplicate");
            Add(d, "改名", "Rename");
            Add(d, "移除所选", "Remove selected");
            Add(d, "清空", "Clear");
            Add(d, "导入…", "Import…");
            Add(d, "导出…", "Export…");
            Add(d, "添加文件夹…", "Add folder…");
            Add(d, "库文件夹管理", "Library folders");
            Add(d, "没选方案：挑一套，列表里就会标出它在这一类里装了什么",
                "No set selected: pick one and the list marks what it installs in each category");

            // ---------- 左栏：日志 ----------
            Add(d, "显示日志 ▾", "Show log ▾");
            Add(d, "收起日志 ▴", "Hide log ▴");
            Add(d, "打开日志", "Open log");
            Add(d, "日志", "Log");
            Add(d, "确定", "OK");
            Add(d, "关闭", "Close");
            Add(d, "取消", "Cancel");
            Add(d, "是", "Yes");
            Add(d, "否", "No");

            // ---------- 方案树 / 列表 ----------
            Add(d, "位置", "Slot");
            Add(d, "名称", "Name");
            Add(d, "大小", "Size");
            Add(d, "开关", "On/Off");
            Add(d, "启用", "Enabled");
            Add(d, "停用", "Disabled");
            Add(d, "已装", "Installed");
            Add(d, "未装", "Empty");
            Add(d, "未归类", "Unsorted");
            Add(d, "多个", "Multiple");
            Add(d, "＋ 新建分支…", "+ New branch…");
            Add(d, "原版", "Vanilla");
            Add(d, "我的分支", "My branches");

            // ---------- 大分类 ----------
            Add(d, "枪械", "Firearms");
            Add(d, "近战", "Melee");
            Add(d, "投掷物", "Throwables");
            Add(d, "弹药升级", "Ammo upgrades");
            Add(d, "人物", "Survivors");
            Add(d, "特感", "Special infected");
            Add(d, "药品", "Medical");
            Add(d, "杂项", "Misc");
            Add(d, "地图", "Maps");
            Add(d, "喷漆", "Sprays");

            // ---------- 小分类 ----------
            Add(d, "步枪", "Rifles");
            Add(d, "冲锋枪", "SMGs");
            Add(d, "霰弹枪", "Shotguns");
            Add(d, "狙击枪", "Snipers");
            Add(d, "手枪", "Pistols");
            Add(d, "特殊武器", "Heavy weapons");
            Add(d, "近战武器", "Melee weapons");
            Add(d, "治疗与复活", "Healing & revival");
            Add(d, "暂时增益", "Temporary buffs");
            Add(d, "其他药品", "Other medical");
            Add(d, "可搬道具", "Carryables");
            Add(d, "其他杂项", "Other misc");
            Add(d, "一代", "L4D1");
            Add(d, "二代", "L4D2");
            Add(d, "其他人物", "Other survivors");
            Add(d, "普通感染者", "Common infected");
            Add(d, "其他", "Other");
            Add(d, "弹药", "Ammo");

            Add(d, "未分型号", "Unspecified");
            Add(d, "认不出改的是什么（点开看全部 · 可手动指定）", "Cannot tell what this changes (open to see all · assign manually)");
            Add(d, "{0} 个", "{0}");

            // ---------- 位置名（按游戏里的英文叫法） ----------
            Add(d, "木喷", "Pump Shotgun");
            Add(d, "铁喷", "Chrome Shotgun");
            Add(d, "一代连喷", "Auto Shotgun");
            Add(d, "二代连喷", "Combat Shotgun");
            Add(d, "鸟狙", "Hunting Rifle");
            Add(d, "15发木狙", "Hunting Rifle (15)");
            Add(d, "30发军用狙", "Military Sniper");
            Add(d, "马格南", "Magnum");
            Add(d, "榴弹发射器", "Grenade Launcher");
            Add(d, "消防斧", "Fire Axe");
            Add(d, "武士刀", "Katana");
            Add(d, "砍刀", "Machete");
            Add(d, "撬棍", "Crowbar");
            Add(d, "电锯", "Chainsaw");
            Add(d, "平底锅", "Frying Pan");
            Add(d, "棒球棍", "Baseball Bat");
            Add(d, "干草叉", "Pitchfork");
            Add(d, "铁锹", "Shovel");
            Add(d, "小刀", "Knife");
            Add(d, "燃烧瓶", "Molotov");
            Add(d, "土制炸弹", "Pipe Bomb");
            Add(d, "胆汁", "Bile Jar");
            Add(d, "燃烧弹药包", "Incendiary Ammo");
            Add(d, "爆破弹药包", "Explosive Ammo");
            Add(d, "激光瞄准", "Laser Sight");
            Add(d, "医疗包", "Medkit");
            Add(d, "电击器", "Defibrillator");
            Add(d, "肾上腺素", "Adrenaline");
            Add(d, "止痛药", "Pain Pills");
            Add(d, "药丸", "Pills");
            Add(d, "氧气瓶", "Oxygen Tank");
            Add(d, "煤气罐", "Gas Can");
            Add(d, "油桶", "Oil Barrel");
            Add(d, "燃料桶", "Fuel Barrel");
            Add(d, "烟花盒", "Fireworks Crate");
            Add(d, "地精", "Gnome");
            Add(d, "可乐", "Cola Bottles");
            Add(d, "天空盒", "Skybox");
            Add(d, "比尔", "Bill");
            Add(d, "佐伊", "Zoey");
            Add(d, "路易斯", "Louis");
            Add(d, "弗朗西斯", "Francis");
            Add(d, "教练", "Coach");
            Add(d, "埃利斯", "Ellis");
            Add(d, "尼克", "Nick");
            Add(d, "罗谢尔", "Rochelle");
            Add(d, "幸存者", "Survivor");

            // ---------- 第二批：语言自检扫出来的漏项 ----------
            Add(d, "收起", "Collapse");
            Add(d, "L4N 平台（只有 MQCam 相机要用它）", "L4N platform (used by MQCam only)");
            Add(d, "Skeeto 增强框架（命中反馈 / 准星 / HUD）", "Skeeto framework (hit feedback / crosshair / HUD)");
            Add(d, "Skeeto 素材包 177 MB", "Skeeto asset pack (177 MB)");
            Add(d, "MQCam 相机（F2 / F3 / F4 视角）", "MQCam camera (F2 / F3 / F4 views)");
            Add(d, "dxvk 防闪退（d3d9.dll）", "dxvk crash fix (d3d9.dll)");
            Add(d, "写 Steam 启动项", "Write Steam launch options");
            Add(d, "写参数配置（L4N 按键 + Skeeto 设置）", "Write config (L4N keys + Skeeto)");
            Add(d, "启动项", "Launch options");
            Add(d, "严格校验（慢一倍，更保险）", "Strict verify (2× slower, safer)");
            Add(d, "参数", "Params");
            Add(d, "参数配置…", "Config file…");
            Add(d, "只装这里", "Install here only");
            Add(d, "留原版", "Keep vanilla");
            Add(d, "指定位置…", "Choose slot…");
            Add(d, "应用改动", "Apply changes");
            Add(d, "一键还原", "Full restore");
            Add(d, "常规", "General");
            Add(d, "U 盘模式扫所选主库，本地模式只看游戏目录", "USB mode scans the chosen library; local mode only the game folder");
            Add(d, "减少动态效果", "Reduce motion");
            Add(d, "关掉按钮与状态切换的过渡，低配机器更轻快", "Turn off button/state transitions; lighter on low-end PCs");
            Add(d, "跟随系统 = 跟 Windows 走；选了立即生效", "Follow system = match Windows; picked value applies at once");
            Add(d, "切换后界面立即重建，其它设置不会丢", "UI rebuilds right away; other settings are kept");
            Add(d, "游戏根目录", "Game folder");
            Add(d, "MOD 主库", "MOD library");
            Add(d, "库所在盘", "Library drive");
            Add(d, "选择文件夹…", "Choose folder…");
            Add(d, "本地模式不使用外部库，只读游戏目录里的 addons", "Local mode uses no external library; it reads addons in the game folder");
            Add(d, "附加库目录…", "Extra library folders…");
            Add(d, "主库和附加目录都会扫描；只读，不复制、不移动。", "Library and extra folders are scanned read-only; nothing is copied or moved.");
            Add(d, "位置与诊断", "Paths & diagnostics");
            Add(d, "打开配置文件夹", "Open config folder");
            Add(d, "环境自检", "Self-check");
            Add(d, "保存后立即生效，不改已经装进游戏的文件。", "Takes effect right after saving; installed game files are untouched.");
            Add(d, "（点卡片看介绍）", "(Click a card for details)");
            Add(d, "← 返回", "← Back");
            Add(d, "下载这个 MOD", "Download this mod");
            Add(d, "在浏览器打开", "Open in browser");
            Add(d, "看原文", "Show original");
            Add(d, "回到顶部", "Back to top");
            Add(d, "◀ 上一页", "◀ Prev");
            Add(d, "下一页 ▶", "Next ▶");
            Add(d, "页", "Page");
            Add(d, "选里面有 left4dead2.exe 的那一层；本地模式只扫这里的 left4dead2\\addons。",
                "Pick the folder that contains left4dead2.exe; local mode only scans its left4dead2\\addons.");

            // ---------- 动态模板：用 Lang.F 调用，{0} 是参数 ----------
            Add(d, "已选 {0} 个文件 ｜ 列表显示 {1} 行", "Selected {0} files ｜ {1} rows shown");
            Add(d, "主库：{0}", "Library: {0}");
            Add(d, "主库：未找到有效目录，请重新选盘或选择文件夹", "Library: no valid folder found — pick a drive or folder again");
            Add(d, " 等", " etc.");
            Add(d, "附加目录：{0} 个 · {1}{2}", "Extra folders: {0} · {1}{2}");
            Add(d, "配置：{0}", "Config: {0}");
            Add(d, "日志：{0}", "Logs: {0}");
            Add(d, "● {0}{1}", "● {0}{1}");
            Add(d, "U 盘库 · {0} 个 MOD · {1}", "USB library · {0} mods · {1}");
            Add(d, "当前：{0}", "Current: {0}");
            Add(d, "可叠加的扩展：{0}", "Stackable extras: {0}");
            Add(d, "{0} 页", "Page {0}");
            Add(d, "下载列表 ({0})", "Downloads ({0})");

            // ---------- 第三批：自检剩余项 ----------
            Add(d, "● 可以开始安装", "● Ready to install");
            Add(d, "已识别：left4dead2.exe 在里面，可以扫描和安装", "Found left4dead2.exe — ready to scan and install");
            Add(d, "附加目录：无", "Extra folders: none");
            Add(d, "Language / 语言", "Language");

            Add(d, "还没从工坊下过东西。\r\n在左边卡片上点「下载这个 MOD」，下完就会出现在这里。",
                "Nothing downloaded from the Workshop yet.\r\nClick \"Download this mod\" on a card and it shows up here.");
            Add(d, "    默认位置（跟随主库）", "    default (follows library)");
            Add(d, "已选 {0} 个 MOD{1}  ·  {2}  ·  约 {3} 秒（按 {4:0} MB/s）",
                "Selected {0} mods{1}  ·  {2}  ·  ~{3}s (at {4:0} MB/s)");
            Add(d, "当前：{0}{1}", "Current: {0}{1}");
            Add(d, "　+ {0} 个扩展", "  + {0} extras");
            Add(d, "{0} 个模型候选 · 点一个就换掉原来的", "{0} model candidates · click one to replace");
            Add(d, "⚠ {0} 组体积重复 · 最多白占 {1} · {2}\r\n", "⚠ {0} duplicate groups · up to {1} wasted · {2}\r\n");
            Add(d, "点这里处理：先核对内容是不是同一个文件，再决定这次不装 / 直接删掉",
                "Click to review: check whether they are really the same file, then skip or delete");
            Add(d, "（{0} 个文件）", " ({0} files)");
            Add(d, "今天 {0}", "today {0}");
            Add(d, " 装过", "");
            Add(d, "还没装过", "never installed");
            Add(d, "可叠加的扩展：这个位置没有（音效 / 第一人称 / HUD）",
                "Stackable extras: none for this slot (sound / first person / HUD)");
            Add(d, "「开关 ⇄」列点一下=把这个 MOD 开 / 关", "click the On/Off column to toggle this mod");
            Add(d, "开", "On");
            Add(d, "开启", "On");
            Add(d, "关", "Off");
            Add(d, "关闭", "Off");
            Add(d, "当前选择", "Current selection");
            Add(d, "方案树 · {0} 个位置 · 已选 {1}{2}", "Set tree · {0} slots · {1} selected{2}");
            Add(d, "装什么", "What to install");
            Add(d, "{0} 个未归类", "{0} unsorted");
            // ---------- 状态 / 提示 ----------
            Add(d, "已选", "Selected");
            Add(d, "分类", "Category");
            Add(d, "全部物品", "All items");
            Add(d, "正在搜工坊…", "Searching the Workshop…");
            Add(d, "正在加载…", "Loading…");
            Add(d, "下载列表", "Downloads");
            Add(d, "批量下载", "Batch download");
            Add(d, "下载", "Download");
            Add(d, "已下载", "Downloaded");
            Add(d, "失败", "Failed");
            Add(d, "中断", "Interrupted");
            Add(d, "下载中", "Downloading");
            Add(d, "时间", "Time");
            Add(d, "状态", "Status");
            Add(d, "重新下载", "Download again");
            Add(d, "打开所在位置", "Show in folder");
            Add(d, "从磁盘删除", "Delete from disk");
            Add(d, "清空历史", "Clear history");
            Add(d, "改位置", "Change folder");
            Add(d, "用默认", "Use default");
            Add(d, "打开文件夹", "Open folder");
            Add(d, "保存到", "Save to");

            // ---------- 设置 ----------
            Add(d, "保存设置", "Save settings");
            Add(d, "外观", "Appearance");
            Add(d, "深色", "Dark");
            Add(d, "浅色", "Light");
            Add(d, "语言 / Language", "Language");
            Add(d, "中文", "中文 (Chinese)");
            Add(d, "English", "English");
            Add(d, "减少动效", "Reduce motion");
            Add(d, "工作模式", "Mode");
            Add(d, "本地模式", "Local mode");
            Add(d, "U 盘模式", "USB drive mode");
            Add(d, "工坊下载位置", "Workshop download folder");
        }
    }
}

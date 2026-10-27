using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace TodoCalendar;

public sealed partial class MainForm : Form
{
    private const string VirtualHost = "app.local";
    private const string ResourceName = "TodoCalendar.web.index.html";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AutostartValueName = "TodoCalendar";
    // 加引号：exe 路径含空格时裸路径会被 Windows 截断；--tray 让它开机时静默起在托盘
    private static string AutostartCommand => "\"" + ExePath + "\" --tray";
    private const int HotkeyId = 1;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2;
    private const uint VK_T = 0x54;
    private const int WM_HOTKEY = 0x0312;

    private readonly WebView2 _web;
    private readonly string _dataDir;
    private readonly string _webDir;
    private readonly string _importFile;
    private readonly EventWaitHandle _showEvent;
    private bool _webReady;
    private bool _imported;
    private bool _forceExit;
    private bool _closing;
    private bool _startHidden;
    // 区分两个用途：_startHidden 只用来"拦一次"首次可见化（拦完即清）；
    // _silentMode 记录本次是否静默启动，后面初始化时还要用，不能清。
    private bool _silentMode;

    private NotifyIcon _tray;
    private ContextMenuStrip _menu;
    private ToolStripMenuItem _autoStartItem;
    private ToolStripMenuItem _topmostItem;
    private System.Windows.Forms.Timer _reminderTimer;
    private Thread _showThread;
    private string _lastReminderDay;
    private string _pendingUpdatePage;   // 后台查到新版本后，气泡被点击时打开的地址

    public MainForm(string importFile = null, EventWaitHandle showEvent = null, bool startHidden = false)
    {
        _dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TodoCalendar");
        _webDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TodoCalendar", "web");
        _importFile = importFile;
        _showEvent = showEvent;
        _startHidden = startHidden;
        _silentMode = startHidden;

        Text = "日历待办 · 甘特视图";
        BackColor = ColorTranslator.FromHtml("#EEF1F8");
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(900, 600);
        KeyPreview = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { /* 忽略 */ }

        ApplyWindowBounds();

        _web = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = ColorTranslator.FromHtml("#EEF1F8")
        };
        Controls.Add(_web);

        if (_importFile == null)
        {
            HealAutostartIfStale();                       // 挪过文件夹也能自愈（要在建托盘菜单之前）
            SetupTray();
            SetupReminder();
            _lastReminderDay = LoadLastReminderDay();     // 重启后不重复弹当天提醒
        }
    }

    // ---------------- 窗口尺寸 ----------------

    private string StateFile => Path.Combine(_dataDir, "window-state.json");

    // 本机共享待办文件：exe 与 AI（MCP 服务）之间的"权威数据源"
    private string TodoFile => Path.Combine(_dataDir, "todos.json");

    private sealed class WinState
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool Maximized { get; set; }
    }

    private static Rectangle FitInside(Rectangle r, Rectangle area)
    {
        int w = Math.Min(r.Width, area.Width);
        int h = Math.Min(r.Height, area.Height);
        int x = Math.Max(area.X, Math.Min(r.X, area.Right - w));
        int y = Math.Max(area.Y, Math.Min(r.Y, area.Bottom - h));
        return new Rectangle(x, y, w, h);
    }

    private void ApplyWindowBounds()
    {
        var wa = Screen.PrimaryScreen.WorkingArea;
        WinState st = null;
        try
        {
            if (File.Exists(StateFile))
                st = JsonSerializer.Deserialize<WinState>(File.ReadAllText(StateFile));
        }
        catch { /* 状态文件损坏则用默认 */ }

        Rectangle target;
        if (st != null && st.Width >= 300 && st.Height >= 200)
        {
            var screen = Screen.FromPoint(new Point(st.X + st.Width / 2, st.Y + st.Height / 2));
            target = FitInside(new Rectangle(st.X, st.Y, st.Width, st.Height), screen.WorkingArea);
        }
        else
        {
            int w = Math.Max(900, (int)(wa.Width * 0.92));
            int h = Math.Max(620, (int)(wa.Height * 0.94));
            w = Math.Min(w, wa.Width);
            h = Math.Min(h, wa.Height);
            target = new Rectangle(wa.X + (wa.Width - w) / 2, wa.Y + (wa.Height - h) / 2, w, h);
        }

        Bounds = target;
        if (st?.Maximized == true) WindowState = FormWindowState.Maximized;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (WindowState == FormWindowState.Normal)
        {
            var screen = Screen.FromControl(this);
            if (!screen.WorkingArea.Contains(Bounds))
                Bounds = FitInside(Bounds, screen.WorkingArea);
        }
        StartWebOnce();
    }

    // 开机自启时窗口不显示（只留托盘），此时 OnShown 不会触发，
    // 所以 WebView2 初始化由 OnHandleCreated 兜住（见下方全局热键区）。
    private bool _initStarted;
    private void StartWebOnce()
    {
        if (_initStarted) return;
        _initStarted = true;
        _ = InitWebAsync();
    }

    // 静默启动：Application.Run 首次把窗口设为可见时拦一次，不显示也不抢焦点
    protected override void SetVisibleCore(bool value)
    {
        if (_startHidden)
        {
            _startHidden = false;
            if (!IsHandleCreated) CreateHandle();
            base.SetVisibleCore(false);
            return;
        }
        base.SetVisibleCore(value);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_forceExit)
        {
            // 关闭 = 最小化到托盘（不再弹气泡提示，避免打扰；说明见托盘悬停提示）
            e.Cancel = true;
            Hide();
            return;
        }
        try
        {
            var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            var st = new WinState
            {
                X = b.X, Y = b.Y, Width = b.Width, Height = b.Height,
                Maximized = WindowState == FormWindowState.Maximized
            };
            Directory.CreateDirectory(_dataDir);
            WriteAtomic(StateFile, JsonSerializer.Serialize(st));
        }
        catch { /* 忽略保存失败 */ }
        base.OnFormClosing(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized) Hide();
    }

    // ---------------- 系统托盘 ----------------

    private void SetupTray()
    {
        _menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("打开窗口");
        open.Click += (_, _) => ShowAndActivate();
        _autoStartItem = new ToolStripMenuItem("开机启动");
        _autoStartItem.CheckOnClick = true;
        _autoStartItem.Checked = IsAutostartEnabled();
        // CheckOnClick 已把 Checked 翻成新状态，直接按它写注册表
        _autoStartItem.Click += (_, _) => SetAutostart(_autoStartItem.Checked);
        _topmostItem = new ToolStripMenuItem("窗口置顶");
        _topmostItem.CheckOnClick = true;
        _topmostItem.Checked = false;
        _topmostItem.Click += (_, _) => { TopMost = _topmostItem.Checked; };
        var checkUpdate = new ToolStripMenuItem("检查更新…");
        checkUpdate.Click += async (_, _) => await CheckUpdateAsync(true);
        var about = new ToolStripMenuItem("关于");
        about.Click += (_, _) => MessageBox.Show(this,
            "日历待办 · 甘特视图 v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?") + "\n"
            + "个人待办工具：日历甘特、拖拽改期、优先级、标签、进度、重复、依赖、导出与提醒。\n\n"
            + "下载页：" + UpdateService.DefaultPage + "\n"
            + "数据文件：" + TodoFile + "\n自动备份：" + TodoFile + ".bak",
            "关于", MessageBoxButtons.OK, MessageBoxIcon.Information);
        var exit = new ToolStripMenuItem("退出");
        exit.Click += (_, _) => { _forceExit = true; Close(); };

        _menu.Items.AddRange(new ToolStripItem[] { open, _autoStartItem, _topmostItem, checkUpdate, about, exit });

        _tray = new NotifyIcon
        {
            Icon = Icon ?? SystemIcons.Application,
            // 悬停即说明，替代原来每次缩小都弹的气泡
            Text = "日历待办 · 单击恢复窗口，右键可退出",
            ContextMenuStrip = _menu,
            Visible = true
        };
        // 左键单击直接打开主页面（不再需要先右键进菜单）
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowAndActivate();
        };
        _tray.DoubleClick += (_, _) => ShowAndActivate();
        // 点提醒气泡直接打开窗口
        _tray.BalloonTipClicked += (_, _) =>
        {
            // 更新提示的气泡点了要开下载页，其余（如到期提醒）点了打开窗口
            var page = _pendingUpdatePage;
            if (!string.IsNullOrEmpty(page)) { _pendingUpdatePage = null; OpenUrl(page); return; }
            ShowAndActivate();
        };
    }

    private static string ExePath => Application.ExecutablePath;

    private static string ReadAutostartValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(AutostartValueName)?.ToString();
        }
        catch { return null; }
    }

    private static bool IsAutostartEnabled()
    {
        var v = ReadAutostartValue();
        if (string.IsNullOrEmpty(v)) return false;
        // 只要记的是这个 exe 就算开启（老版本写的是不带引号的裸路径，也认）
        return v.IndexOf(ExePath, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void SetAutostart(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key == null) return;
            if (on) key.SetValue(AutostartValueName, AutostartCommand);
            else key.DeleteValue(AutostartValueName, false);
        }
        catch (Exception ex)
        {
            MessageBox.Show("设置开机启动失败：" + ex.Message, "日历待办", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /* 已经是开启状态、但记录的路径不是当前 exe（把文件夹挪走了 / 换了版本目录），
       或者老格式没带 --tray —— 静默改写，避免"看着开着其实早就失效"。 */
    private static void HealAutostartIfStale()
    {
        var v = ReadAutostartValue();
        if (string.IsNullOrEmpty(v)) return;
        if (v.IndexOf(ExePath, StringComparison.OrdinalIgnoreCase) >= 0
            && v.IndexOf("--tray", StringComparison.OrdinalIgnoreCase) >= 0) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            key?.SetValue(AutostartValueName, AutostartCommand);
        }
        catch { /* 忽略 */ }
    }

    private void ShowAndActivate()
    {
        if (InvokeRequired) { Invoke((Action)ShowAndActivate); return; }
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    // ---------------- 全局热键 / 唤醒线程 ----------------

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // 开机自启静默启动时 OnShown 不会触发，这里兜住 WebView2 初始化。
        // 注意必须判 _silentMode：_startHidden 在 SetVisibleCore 里已经被清掉了。
        if (_silentMode) BeginInvoke(new Action(StartWebOnce));
        if (_importFile == null)
        {
            try { RegisterHotKey(Handle, HotkeyId, MOD_ALT | MOD_CONTROL, VK_T); } catch { /* 部分环境不支持 */ }
            if (_showEvent != null)
            {
                _showThread = new Thread(WaitForShow) { IsBackground = true, Name = "ShowWaiter" };
                _showThread.Start();
            }
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        try { UnregisterHotKey(Handle, HotkeyId); } catch { /* 忽略 */ }
        _closing = true;
        try { _showEvent?.Set(); } catch { /* 忽略 */ }
        _tray?.Dispose();
        base.OnHandleDestroyed(e);
    }

    private void WaitForShow()
    {
        while (!_closing)
        {
            if (_showEvent == null) break;
            if (_showEvent.WaitOne(500)) ShowAndActivate();
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
        {
            ShowAndActivate();
            return;
        }
        base.WndProc(ref m);
    }

    // ---------------- 到期提醒 ----------------

    private void SetupReminder()
    {
        _reminderTimer = new System.Windows.Forms.Timer { Interval = 60000 };
        _reminderTimer.Tick += async (_, _) => await CheckReminders();
    }

    /* 提醒「当天只弹一次」的日期要落盘：否则重启一次（含开机自启）就再弹一遍 */
    private string ReminderDayFile => Path.Combine(_dataDir, "last-reminder.txt");
    private string LoadLastReminderDay()
    {
        try { return File.Exists(ReminderDayFile) ? File.ReadAllText(ReminderDayFile).Trim() : null; }
        catch { return null; }
    }
    private void SaveLastReminderDay(string day)
    {
        try { Directory.CreateDirectory(_dataDir); File.WriteAllText(ReminderDayFile, day); }
        catch { /* 忽略 */ }
    }

    private async System.Threading.Tasks.Task CheckReminders()
    {
        if (_web.CoreWebView2 == null) return;
        try
        {
            // 优先读取本机共享文件（host 模式权威源），回退到 localStorage
            string todosJson = null;
            if (File.Exists(TodoFile))
            {
                try
                {
                    using var fdoc = JsonDocument.Parse(File.ReadAllText(TodoFile));
                    if (fdoc.RootElement.TryGetProperty("todos", out var tEl) && tEl.ValueKind == JsonValueKind.Array)
                        todosJson = tEl.GetRawText();
                }
                catch { todosJson = null; }
            }
            if (string.IsNullOrEmpty(todosJson))
            {
                var raw = await _web.CoreWebView2.ExecuteScriptAsync("localStorage.getItem('todo.v2')");
                if (string.IsNullOrEmpty(raw) || raw == "null") return;
                todosJson = JsonSerializer.Deserialize<string>(raw);
            }
            if (string.IsNullOrEmpty(todosJson)) return;
            using var doc = JsonDocument.Parse(todosJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

            var now = DateTime.Now;
            var tasks = new List<ReminderTask>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var t = Reminder.ParseTask(el);
                if (t != null) tasks.Add(t);
            }

            var (urgent, nudge, stale) = Reminder.Pick(tasks, now, LoadNudged());
            var title = Reminder.Title(urgent, nudge);
            if (title == null) return;

            var dayKey = now.ToString("yyyy-MM-dd");
            if (_lastReminderDay == dayKey) return;   // 当天已提醒，避免刷屏
            _lastReminderDay = dayKey;
            SaveLastReminderDay(dayKey);

            // 逾期用警告图标，其余用提示图标
            var icon = Reminder.AnyOverdue(urgent) ? ToolTipIcon.Warning : ToolTipIcon.Info;
            var body = Reminder.Body(urgent, nudge, stale, 3, 20);
            LogBridge("reminder: " + title + " || " + (body ?? "").Replace("\n", " / "));
            _tray?.ShowBalloonTip(6000, title, body, icon);
            foreach (var n in nudge) MarkNudged(n.Id);   // 过半提醒每任务只发一次
        }
        catch { /* 提醒失败不影响主流程 */ }
    }

    /* 「过半程提醒」每任务只发一次的账本：一行一个任务 id，追加重写成本最低 */
    private string NudgeFile => Path.Combine(_dataDir, "reminder-nudged.txt");

    private HashSet<string> LoadNudged()
    {
        var set = new HashSet<string>();
        try
        {
            if (!File.Exists(NudgeFile)) return set;
            foreach (var line in File.ReadAllLines(NudgeFile))
            {
                var s = line.Trim();
                if (s.Length > 0) set.Add(s);
            }
        }
        catch { /* 读不到就当没提醒过 */ }
        return set;
    }

    private void MarkNudged(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        try
        {
            Directory.CreateDirectory(_dataDir);
            File.AppendAllText(NudgeFile, id + Environment.NewLine);
        }
        catch { /* 忽略 */ }
    }

    // ---------------- 检查更新（只提示，不自动替换） ----------------

    private string UpdateFile => Path.Combine(_dataDir, "update.json");

    private void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { LogBridge("open url failed: " + ex.Message); }
    }

    /// <summary>
    /// manual = 用户主动点「检查更新」：无论结果都给反馈；
    /// manual = false 则用于启动时的静默检查，只在新版本可用时弹一次气泡，失败不打扰。
    /// </summary>
    private async System.Threading.Tasks.Task CheckUpdateAsync(bool manual)
    {
        var cfg = UpdateService.Load(UpdateFile);
        var cur = UpdateService.CurrentVersion;
        var today = DateTime.Today.ToString("yyyy-MM-dd");

        if (string.IsNullOrWhiteSpace(cfg.Url))
        {
            // 没配更新源时必须诚实说明，不能假装"已是最新"
            if (!manual) return;
            var r = MessageBox.Show(this,
                "还没有配置「更新源」，所以没法自动比对版本。\n\n"
                + "当前版本：v" + cur + "\n\n"
                + "要现在打开下载页，自己去看看有没有新版吗？",
                "检查更新", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (r == DialogResult.Yes) OpenUrl(UpdateService.DefaultPage);
            return;
        }

        UpdateFeed feed;
        try
        {
            feed = await UpdateService.FetchAsync(cfg.Url).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (manual)
                MessageBox.Show(this,
                    "检查更新失败：" + ex.Message + "\n\n当前版本：v" + cur,
                    "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        cfg.LastCheck = today;
        bool newer = UpdateService.IsNewer(feed.Version, cur);
        bool ignored = string.Equals(UpdateService.Normalize(cfg.Ignored),
            UpdateService.Normalize(feed.Version), StringComparison.OrdinalIgnoreCase);

        if (!newer)
        {
            UpdateService.Save(UpdateFile, cfg);
            if (manual)
                MessageBox.Show(this, "已经是最新版本：v" + cur, "检查更新",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!manual)
        {
            UpdateService.Save(UpdateFile, cfg);
            if (ignored) return;
            _pendingUpdatePage = feed.Page;
            _tray?.ShowBalloonTip(8000, "发现新版本 v" + feed.Version,
                "当前 v" + cur + "，点击这里打开下载页。", ToolTipIcon.Info);
            return;
        }

        var body = "当前版本：v" + cur + "\n最新版本：v" + feed.Version + "\n";
        if (!string.IsNullOrWhiteSpace(feed.Notes))
        {
            var notes = feed.Notes.Trim();
            if (notes.Length > 400) notes = notes.Substring(0, 400) + "…";
            body += "\n更新说明：\n" + notes + "\n";
        }
        body += "\n是 = 打开下载页　　否 = 本版不再提醒　　取消 = 什么也不做";

        var ans = MessageBox.Show(this, body, "发现新版本", MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Information);
        if (ans == DialogResult.Yes) OpenUrl(feed.Page);
        else if (ans == DialogResult.No)
        {
            cfg.Ignored = feed.Version;
            UpdateService.Save(UpdateFile, cfg);
        }
        else UpdateService.Save(UpdateFile, cfg);
    }

    /// <summary>启动后静默查一次，每天最多一次，避免每次开程序都发请求</summary>
    private async System.Threading.Tasks.Task CheckUpdateOnStartupAsync()
    {
        try
        {
            var cfg = UpdateService.Load(UpdateFile);
            if (string.IsNullOrWhiteSpace(cfg.Url)) return;
            if (cfg.LastCheck == DateTime.Today.ToString("yyyy-MM-dd")) return;
            await CheckUpdateAsync(false);
        }
        catch { /* 更新检查失败绝不影响启动 */ }
    }


    // ---------------- 数据落盘（原子写 + 自动备份） ----------------

    private static readonly object WriteLock = new object();

    /// <summary>
    /// 原子写：先写 .tmp，再用 File.Replace 顶替，旧文件自动留成 .bak。
    /// 直接 File.WriteAllText 覆盖时，写到一半掉电/崩溃会留下截断的 JSON，
    /// 而前端读到损坏文件就会走「无数据」分支 —— 这是最危险的单点。
    /// </summary>
    private void WriteAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        var bak = path + ".bak";
        lock (WriteLock)
        {
            File.WriteAllText(tmp, content);
            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, bak, true); }
                catch
                {
                    // 某些文件系统（部分网络盘/同步盘）不支持 Replace，退回覆盖但保留 .bak
                    try { File.Copy(path, bak, true); } catch { }
                    File.Move(tmp, path, true);
                }
            }
            else
            {
                File.Move(tmp, path);
            }
        }
    }

    /// <summary>内容没变就不写：避免 mtime 抖动让前端 2 秒轮询反复整页重渲染。</summary>
    private void WriteJsonIfChanged(string path, string json)
    {
        try
        {
            if (File.Exists(path) && File.ReadAllText(path) == json) return;
        }
        catch { /* 读失败就走正常写入 */ }
        WriteAtomic(path, json);
    }


    // ---------------- WebView2 ----------------

    private static void EnsureWebAssets(string dir)
    {
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "index.html");
        var stamp = Path.Combine(dir, "version.txt");

        var asm = Assembly.GetExecutingAssembly();
        using var res = asm.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("缺少内嵌页面资源");
        using var ms = new MemoryStream();
        res.CopyTo(ms);
        var bytes = ms.ToArray();

        var version = asm.GetName().Version?.ToString() ?? "0";
        var oldVersion = File.Exists(stamp) ? File.ReadAllText(stamp).Trim() : "";
        var stale = !File.Exists(target) || oldVersion != version
                    || new FileInfo(target).Length != bytes.Length;

        if (stale)
        {
            File.WriteAllBytes(target, bytes);
            File.WriteAllText(stamp, version);
        }
    }

    private async System.Threading.Tasks.Task InitWebAsync()
    {
        try
        {
            EnsureWebAssets(_webDir);
            LoadAiSettings();   // AI 设置（key 经 DPAPI 解密，只留在宿主内存）

            // WebView2 缓存/用户数据放 LocalAppData（本机磁盘）。
            // 原实现放 %APPDATA%（Roaming），域环境下 Roaming 常被重定向到网络/同步位置，
            // 加上 ~100MB 缓存，导致每次启动 WebView2 初始化要好几秒。改到本地盘可显著加快。
            var wv2Dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TodoCalendar", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, wv2Dir);
            await _web.EnsureCoreWebView2Async(env);

            var core = _web.CoreWebView2;
            LogBridge("host bridge ready (CoreWebView2 ok)");
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.IsZoomControlEnabled = true;
            core.Settings.AreBrowserAcceleratorKeysEnabled = true;

            core.SetVirtualHostNameToFolderMapping(
                VirtualHost, _webDir, CoreWebView2HostResourceAccessKind.Allow);

            // ---- AI 桥接：把本机共享文件注入为初始状态，并建立双向消息通道 ----
            try
            {
                string initialJson = "null";
                if (File.Exists(TodoFile))
                {
                    var txt = File.ReadAllText(TodoFile);
                    if (!string.IsNullOrWhiteSpace(txt)) initialJson = txt;
                }
                await core.AddScriptToExecuteOnDocumentCreatedAsync("window.__INITIAL__ = " + initialJson + ";");
            }
            catch { /* 注入失败则回退 localStorage */ }

            core.WebMessageReceived += (_, args) =>
            {
                try
                {
                    // 页面用 postMessage(JSON.stringify(x)) 发送，WebView2 交给这里的是「再包一层的 JSON 字符串」，
                    // 必须先解一层字符串再解析，否则 JsonElement 是 String、TryGetProperty 会抛异常，
                    // 而异常会被下面的 catch 静默吞掉 —— 表现为 save/poll 全部失效（只有启动时的 PullOnce 兜底）。
                    var rawMsg = args.WebMessageAsJson ?? "null";
                    var inner = (rawMsg.Length > 0 && rawMsg[0] == '"')
                        ? (JsonSerializer.Deserialize<string>(rawMsg) ?? "null")
                        : rawMsg;
                    using var doc = JsonDocument.Parse(inner);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) return;
                    if (!root.TryGetProperty("type", out var typeEl)) return;
                    var type = typeEl.GetString();
                    if (type == "save")
                    {
                        if (root.TryGetProperty("state", out var stateEl))
                        {
                            WriteJsonIfChanged(TodoFile, stateEl.GetRawText());
                            LogBridge("wrote todos.json");
                            var mt = File.GetLastWriteTimeUtc(TodoFile).ToBinary();
                            core.PostWebMessageAsJson("{\"type\":\"saved\",\"mtime\":" + mt + "}");
                        }
                    }
                    else if (type == "llm")
                    {
                        HandleAiMessage(core, root);
                    }
                    else if (type == "poll")
                    {
                        var last = (root.TryGetProperty("last", out var lastEl) && lastEl.ValueKind == JsonValueKind.Number)
                            ? lastEl.GetInt64() : 0;
                        if (File.Exists(TodoFile))
                        {
                            var mt = File.GetLastWriteTimeUtc(TodoFile).ToBinary();
                            if (mt != last)
                            {
                                var txt = File.ReadAllText(TodoFile);
                                core.PostWebMessageAsJson("{\"type\":\"state\",\"state\":" + txt + ",\"mtime\":" + mt + "}");
                            }
                            else
                            {
                                core.PostWebMessageAsJson("{\"type\":\"noop\"}");
                            }
                        }
                        else
                        {
                            core.PostWebMessageAsJson("{\"type\":\"noop\"}");
                        }
                    }
                    else if (type == "autostart")
                    {
                        var op = root.TryGetProperty("op", out var opEl) ? opEl.GetString() : "get";
                        if (op == "set")
                        {
                            var on = root.TryGetProperty("on", out var onEl) && onEl.ValueKind == JsonValueKind.True;
                            SetAutostart(on);
                            if (_autoStartItem != null) _autoStartItem.Checked = IsAutostartEnabled();
                        }
                        core.PostWebMessageAsJson("{\"type\":\"autostartState\",\"on\":"
                            + (IsAutostartEnabled() ? "true" : "false") + "}");
                    }
                    else if (type == "checkUpdate")
                    {
                        _ = CheckUpdateAsync(true);
                    }
                }
                catch { /* 忽略桥接错误 */ }
            };

            core.NavigationCompleted += async (_, args) =>
            {
                LogBridge("navigation completed: success=" + args.IsSuccess + " http=" + args.HttpStatusCode);
                if (!args.IsSuccess)
                {
                    MessageBox.Show(this, "页面加载失败：" + args.WebErrorStatus,
                        "日历待办", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                _ = PullOnceAsync();   // 页面就绪即拉取一次（此时 CoreWebView2 有效），作为 page→host 推送之外的兜底迁移

                // 首轮提醒不等满 1 分钟：开机自启后应该几秒内就把到期项告诉用户
                if (_importFile != null) return;
                await System.Threading.Tasks.Task.Delay(2500);
                await CheckReminders();
                _ = CheckUpdateOnStartupAsync();   // 联网查新版本，失败静默
            };

            core.NavigationCompleted += async (_, args) =>
            {
                if (!args.IsSuccess || _imported || string.IsNullOrEmpty(_importFile)) return;
                _imported = true;
                await ImportAsync(core);
            };

            _web.Source = new Uri($"https://{VirtualHost}/index.html");
            _webReady = true;
            _reminderTimer?.Start();

            // 启动探针：3 秒后回报页面真实状态（排障用，正常情况只写一行日志）
            var probe = new System.Windows.Forms.Timer { Interval = 3000 };
            probe.Tick += async (_, _) =>
            {
                probe.Stop(); probe.Dispose();
                try
                {
                    var info = await core.ExecuteScriptAsync(
                        "document.readyState + '|' + location.href + '|' + (document.body ? document.body.innerText.length : -1)"
                        + " + '|' + (window.__getState ? 'state-ok' : 'no-state') + '|' + (document.querySelectorAll('.week .bar').length)");
                    LogBridge("probe: " + info);
                }
                catch (Exception ex) { LogBridge("probe FAILED: " + ex.GetType().Name + " " + ex.Message); }

                // AI 桥接自检：不填 key 也能验证 页面→宿主→回包 这条链路
                try
                {
                    await core.ExecuteScriptAsync(
                        "window.__aiProbe='pending';"
                        + "(function(){try{window.__aiCall('scan').then(function(r){window.__aiProbe='ok:'+JSON.stringify(r).slice(0,100);})"
                        + ".catch(function(e){window.__aiProbe='err:'+JSON.stringify(e).slice(0,100);});}catch(e){window.__aiProbe='throw:'+e.message;}})();");
                    await System.Threading.Tasks.Task.Delay(1200);
                    var aiProbe = await core.ExecuteScriptAsync("window.__aiProbe");
                    LogBridge("ai bridge: " + aiProbe);
                }
                catch (Exception ex) { LogBridge("ai bridge probe FAILED: " + ex.Message); }
            };
            probe.Start();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this,
                "缺少 WebView2 运行时，请安装 Microsoft Edge WebView2 Runtime 后重试。",
                "日历待办", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _forceExit = true;
            Close();
        }
        catch (Exception ex)
        {
            LogBridge("init FAILED: " + ex.GetType().Name + " " + ex.Message);
            MessageBox.Show(this, "初始化失败：" + ex.Message,
                "日历待办", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _forceExit = true;
            Close();
        }
    }

    // ---------------- 宿主桥接诊断日志 ----------------

    // 仅供排障：记录桥接初始化与落盘事件到 %TEMP%/todocal.log（非高频，不污染协议）。
    private void LogBridge(string s)
    {
        try
        {
            var p = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "todocal.log");
            var fi = new FileInfo(p);
            if (fi.Exists && fi.Length > 256 * 1024) fi.Delete();   // 每次保存都会写，必须限长，否则无限增长
            File.AppendAllText(p, DateTime.Now.ToString("HH:mm:ss") + " " + s + "\n");
        }
        catch { }
    }

    // 页面就绪后的一次性兜底拉取：此时 CoreWebView2 刚初始化、句柄尚未重建，ExecuteScriptAsync 可靠。
    // 与页面主动推送（hostPush）互为备份，确保本机 todos.json 一定被创建（首迁）。
    private async Task PullOnceAsync()
    {
        try
        {
            var core = _web?.CoreWebView2;
            if (core == null) return;
            var result = await core.ExecuteScriptAsync(
                "window.__getState ? window.__getState() : null");
            if (string.IsNullOrWhiteSpace(result) || result == "null")
            {
                LogBridge("pull aborted: page state not ready (result=" + (result == null ? "null" : result.Length + " chars") + ")");
                return;
            }
            using var doc = JsonDocument.Parse(result);
            if (!doc.RootElement.TryGetProperty("todos", out var todosEl) || todosEl.ValueKind != JsonValueKind.Array)
            {
                LogBridge("pull aborted: no todos array in page state");
                return;
            }
            // 页面状态为空时不回写：缓存被清 + 文件读失败时，这里的无条件写入会把权威数据一次抹平
            if (todosEl.GetArrayLength() == 0)
            {
                LogBridge("pull skipped: page state has 0 todos (protect todos.json)");
                return;
            }
            var raw = doc.RootElement.GetRawText();
            WriteJsonIfChanged(TodoFile, raw);
            LogBridge("pulled+write todos.json (" + raw.Length + " bytes)");
        }
        catch (Exception ex)
        {
            LogBridge("pull FAILED: " + ex.GetType().Name + " " + ex.Message);   // 以前这里全静默，排障时看不到任何线索
        }
    }

    // ---------------- 批量导入 ----------------

    private const string ImportScript = @"
(function (txt) {
  var KEY = 'todo.v2';
  var cur = [];
  try { cur = JSON.parse(localStorage.getItem(KEY) || '[]'); } catch (e) { cur = []; }
  if (!Array.isArray(cur)) cur = [];
  var seen = {};
  cur.forEach(function (t) { if (t && t.text) seen[t.text + '|' + t.start + '|' + t.end] = 1; });
  var arr = JSON.parse(txt);
  var added = 0, skipped = 0;
  arr.forEach(function (t) {
    if (!t || typeof t.text !== 'string' || !t.text.trim()) { skipped++; return; }
    if (!/^\d{4}-\d{2}-\d{2}$/.test(t.start) || !/^\d{4}-\d{2}-\d{2}$/.test(t.end)) { skipped++; return; }
    var k = t.text + '|' + t.start + '|' + t.end;
    if (seen[k]) { skipped++; return; }
    seen[k] = 1;
    cur.push({
      id: 'imp' + Date.now() + Math.random().toString(36).slice(2, 6),
      text: t.text, start: t.start, end: t.end,
      done: !!t.done, color: (typeof t.color === 'number' ? t.color : 0),
      prio: t.prio || '', tags: Array.isArray(t.tags) ? t.tags : [],
      progress: parseInt(t.progress,10)||0, repeat: t.repeat || 'none', note: t.note || '', deps: Array.isArray(t.deps)?t.deps:[]
    });
    added++;
  });
  localStorage.setItem(KEY, JSON.stringify(cur));
  return JSON.stringify({ added: added, skipped: skipped, total: cur.length });
})";

    private async System.Threading.Tasks.Task ImportAsync(CoreWebView2 core)
    {
        var resultPath = Path.Combine(_dataDir, "import-result.json");
        try
        {
            if (!File.Exists(_importFile))
                throw new FileNotFoundException("导入文件不存在：" + _importFile);

            var raw = File.ReadAllText(_importFile);
            using (var probe = JsonDocument.Parse(raw))
            {
                if (probe.RootElement.ValueKind != JsonValueKind.Array)
                    throw new FormatException("导入文件必须是 JSON 数组");
            }

            var script = ImportScript + "(" + JsonSerializer.Serialize(raw) + ")";
            var res = await core.ExecuteScriptAsync(script);
            File.WriteAllText(resultPath, res ?? "null");
            core.Reload();

            await System.Threading.Tasks.Task.Delay(1500);
            _forceExit = true;
            Close();
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(resultPath, "ERR " + ex); } catch { /* 忽略 */ }
            MessageBox.Show(this, "导入失败：" + ex.Message,
                "日历待办", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // F5 刷新、F12 开发者工具、F6 置顶
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_webReady)
        {
            if (keyData == Keys.F5) { _web.CoreWebView2?.Reload(); return true; }
            if (keyData == Keys.F12) { _web.CoreWebView2?.OpenDevToolsWindow(); return true; }
            if (keyData == Keys.F6) { TopMost = !TopMost; if (_topmostItem != null) _topmostItem.Checked = TopMost; return true; }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

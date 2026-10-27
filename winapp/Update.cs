using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace TodoCalendar;

/// <summary>更新源返回的信息（兼容自定义 version.json 与 GitHub Release API 两种格式）</summary>
internal sealed class UpdateFeed
{
    public string Version = "";   // 2.6.1
    public string Page = "";      // 下载页
    public string Notes = "";     // 更新说明，可空
}

/// <summary>更新检查的配置与状态，落在 data 目录的 update.json</summary>
internal sealed class UpdateConfig
{
    public string Url = "";            // 更新源地址，空 = 未配置
    public string Ignored = "";        // 用户选择「忽略此版本」
    public string LastCheck = "";      // yyyy-MM-dd，控制每天只查一次
}

/// <summary>
/// 只做「提示型」更新：查版本 → 比对 → 告诉用户去哪下。
/// 不做静默替换，以免出问题时用户不知道程序为什么变了。
/// </summary>
internal static class UpdateService
{
    /// <summary>仓库的下载页，未配置更新源时用它兜底</summary>
    public const string DefaultPage = "https://github.com/Jasper-Li2002/gantt-todo/releases";

    public static string CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "0.0.0" : v.ToString(3);
        }
    }

    public static string Normalize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
        return s.Trim();
    }

    /// <summary>candidate 是否比 current 新。任一侧解析失败时按字符串比较，宁可多提示一次</summary>
    public static bool IsNewer(string candidate, string current)
    {
        var a = Normalize(candidate);
        var b = Normalize(current);
        if (a.Length == 0) return false;
        if (Version.TryParse(a, out var va) && Version.TryParse(b, out var vb))
        {
            // 补齐到四段：2.6 与 2.6.0 视为相同
            return Normalize4(va) > Normalize4(vb);
        }
        return string.CompareOrdinal(a, b) > 0;
    }

    private static Version Normalize4(Version v) =>
        new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    public static UpdateConfig Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var c = JsonSerializer.Deserialize<UpdateConfig>(File.ReadAllText(path));
                if (c != null) return c;
            }
        }
        catch { /* 读坏了就当没配置 */ }
        return new UpdateConfig();
    }

    public static void Save(string path, UpdateConfig c)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(c,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 写不进去不影响主流程 */ }
    }

    /// <summary>
    /// 拉取更新源。支持两种返回：
    ///   1) {"version":"2.6.1","page":"https://...","notes":"..."}
    ///   2) GitHub Release API：{"tag_name":"v2.6.1","html_url":"...","body":"..."}
    /// url 若以 http(s) 开头走网络，否则当本地文件路径（便于离线自测）。
    /// </summary>
    public static async Task<UpdateFeed> FetchAsync(string url, int timeoutSeconds = 8)
    {
        string raw;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("gantt-todo/" + CurrentVersion);
            using var resp = await http.GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new Exception("HTTP " + (int)resp.StatusCode);
            raw = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        else
        {
            raw = File.ReadAllText(url);
        }

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var feed = new UpdateFeed();

        if (root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
            feed.Version = v.GetString();
        else if (root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String)
            feed.Version = t.GetString();

        if (root.TryGetProperty("page", out var p) && p.ValueKind == JsonValueKind.String)
            feed.Page = p.GetString();
        else if (root.TryGetProperty("html_url", out var h) && h.ValueKind == JsonValueKind.String)
            feed.Page = h.GetString();

        if (root.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String)
            feed.Notes = n.GetString();
        else if (root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String)
            feed.Notes = b.GetString();

        if (string.IsNullOrWhiteSpace(feed.Page)) feed.Page = DefaultPage;
        feed.Version = Normalize(feed.Version);
        return feed;
    }
}

/// <summary>
/// --check-update 自检入口：把「此刻会不会提示、提示什么」算成文本写文件。
/// 弹窗没法自动测，所以把判定逻辑抽出来离线跑。
/// </summary>
internal static class UpdatePreview
{
    public static void Run(string[] args)
    {
        var outp = Path.Combine(Path.GetTempPath(), "todocal-update.txt");
        try
        {
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TodoCalendar");
            var cfg = UpdateService.Load(Path.Combine(dataDir, "update.json"));

            var url = ArgValue(args, "--url");
            if (string.IsNullOrEmpty(url)) url = cfg.Url;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("当前版本 " + UpdateService.CurrentVersion);
            sb.AppendLine("更新源   " + (string.IsNullOrEmpty(url) ? "(未配置)" : url));

            if (string.IsNullOrEmpty(url))
            {
                sb.AppendLine("RESULT 未配置更新源 → 提示型兜底：打开下载页");
                sb.AppendLine("PAGE   " + UpdateService.DefaultPage);
            }
            else
            {
                var feed = UpdateService.FetchAsync(url).GetAwaiter().GetResult();
                sb.AppendLine("远端版本 " + feed.Version);
                sb.AppendLine("下载页   " + feed.Page);
                if (feed.Notes.Length > 0)
                    sb.AppendLine("说明     " + feed.Notes.Replace("\r", "").Replace("\n", " / "));
                bool newer = UpdateService.IsNewer(feed.Version, UpdateService.CurrentVersion);
                bool ignored = string.Equals(UpdateService.Normalize(cfg.Ignored),
                    UpdateService.Normalize(feed.Version), StringComparison.OrdinalIgnoreCase);
                sb.AppendLine("RESULT " + (newer ? (ignored ? "有新版本但已被忽略" : "提示有新版本") : "已是最新"));
            }
            File.WriteAllText(outp, sb.ToString(), System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(outp, "ERROR " + ex.Message); } catch { }
        }
    }

    private static string ArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == name && i + 1 < args.Length) return args[i + 1];
            var prefix = name + "=";
            if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return args[i].Substring(prefix.Length);
        }
        return null;
    }
}

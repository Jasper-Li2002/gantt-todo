using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TodoCalendar;

/// <summary>提醒计算所需的待办快照（只取与提醒相关的字段）</summary>
internal sealed class ReminderTask
{
    public string Id = "";
    public string Text = "";
    public DateTime Start;
    public DateTime End;
    public int Progress;
}

internal sealed class ReminderLine
{
    public string Id;
    public string State;       // 「今天截止 · 剩 6 小时」
    public string Text;
    public string Tail;        // 「 · 进度 0%」，单日任务为空
    public int DaysLeft;       // 距截止的天数，负数 = 逾期
    public bool IsNudge;       // true = 过半程提醒，false = 临期提醒

    public string Render(int maxText)
    {
        var t = Text ?? "";
        if (maxText > 0 && t.Length > maxText) t = t.Substring(0, maxText) + "…";
        return "【" + State + "】" + t + Tail;
    }
}

/// <summary>
/// 提醒文案的全部计算逻辑（纯函数，不碰 UI，可离线自检）。
///
/// 设计要点：
/// 1. 「截止」以当天 24:00 为准（end 是日期，没有时刻），剩余量按小时算，所以
///    「今天截止」会明确告诉你还剩几个小时，而不是含糊的「今天到期」。
/// 2. 跨天任务不只看死线，还看进度：过半仍没动就发一次「已过半程」，避免
///    前六天毫无声音、最后一天突然催命。
/// 3. 过半提醒每任务只发一次（id 记在 reminder-nudged.txt），不会天天刷。
/// </summary>
internal static class Reminder
{
    /// <summary>跨度不足这么多天的任务不做过半提醒（单日/两三天的事天天催反而烦）</summary>
    public const int NudgeMinSpan = 4;
    /// <summary>进度已到这个数就视为在推进，不催</summary>
    public const int NudgeProgressBelow = 50;
    /// <summary>临期范围：今天截止 + 明天截止（逾期照常提醒）</summary>
    public const int UrgentDays = 1;
    /// <summary>逾期超过这么多天就不再逐条列，只给一行汇总，避免把当天的活挤掉</summary>
    public const int StaleOverdueDays = 3;

    public static DateTime Deadline(DateTime endDate) => endDate.Date.AddDays(1); // 视为当日 24:00

    /// <summary>剩余时长的中文说法："剩 6 小时" / "剩 1 天 15 小时" / "已过期"</summary>
    public static string HumanLeft(TimeSpan r)
    {
        if (r <= TimeSpan.Zero) return "已过期";
        if (r.TotalHours < 1) return "剩 " + Math.Max(1, (int)Math.Round(r.TotalMinutes)) + " 分钟";
        if (r.TotalHours < 24) return "剩 " + (int)Math.Floor(r.TotalHours) + " 小时";
        int d = (int)(r.TotalHours / 24);
        int h = (int)Math.Floor(r.TotalHours - d * 24);
        return h == 0 ? "剩 " + d + " 天" : "剩 " + d + " 天 " + h + " 小时";
    }

    /// <summary>临期状态：逾期 N 天 / 今天截止 · 剩 X / 明天截止 · 剩 X / 还剩 N 天</summary>
    public static string StateOf(DateTime end, DateTime now)
    {
        int dl = (end.Date - now.Date).Days;
        if (dl < 0) return "逾期 " + (-dl) + " 天";
        if (dl == 0) return "今天截止 · " + HumanLeft(Deadline(end) - now);
        if (dl == 1) return "明天截止 · " + HumanLeft(Deadline(end) - now);
        return "还剩 " + dl + " 天";
    }

    /// <summary>任务跨度（含首尾）：10-21 到 10-27 = 7 天</summary>
    public static int Span(ReminderTask t) => (t.End.Date - t.Start.Date).Days + 1;

    public static ReminderTask ParseTask(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (el.TryGetProperty("done", out var d) && d.ValueKind == JsonValueKind.True) return null;
        if (!el.TryGetProperty("end", out var endEl) || endEl.ValueKind != JsonValueKind.String) return null;
        var end = ParseDate(endEl.GetString());
        if (end == null) return null;

        var t = new ReminderTask
        {
            End = end.Value,
            Start = end.Value,
            Id = Str(el, "id"),
            Text = Str(el, "text"),
        };
        if (t.Text.Length == 0) t.Text = "(未命名)";
        var start = ParseDate(Str(el, "start"));
        if (start != null) t.Start = start.Value;

        if (el.TryGetProperty("progress", out var pEl) && pEl.ValueKind == JsonValueKind.Number)
        {
            if (!pEl.TryGetInt32(out t.Progress))
            {
                try { t.Progress = (int)Math.Round(pEl.GetDouble()); } catch { t.Progress = 0; }
            }
        }
        return t;
    }

    private static string Str(JsonElement el, string name)
    {
        return (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String) ? (v.GetString() ?? "") : "";
    }

    public static DateTime? ParseDate(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var p = s.Split('-');
        if (p.Length != 3 || !int.TryParse(p[0], out var y) || !int.TryParse(p[1], out var m) || !int.TryParse(p[2], out var d))
            return null;
        try { return new DateTime(y, m, d); } catch { return null; }
    }

    /// <summary>挑出该提醒的项：临期（含逾期）+ 跨天过半。
    /// staleOverdue = 逾期过久、只计数不逐条列的数量。</summary>
    public static (List<ReminderLine> urgent, List<ReminderLine> nudge, int staleOverdue) Pick(
        IEnumerable<ReminderTask> tasks, DateTime now, ISet<string> nudged)
    {
        var urgent = new List<ReminderLine>();
        var nudge = new List<ReminderLine>();
        int stale = 0;
        foreach (var t in tasks)
        {
            if (t.End == default(DateTime)) continue;
            int dl = (t.End.Date - now.Date).Days;
            int span = Span(t);
            bool multiDay = span > 1;
            string tail = multiDay ? " · 进度 " + t.Progress + "%" : "";

            if (dl <= UrgentDays)
            {
                // 逾期太久的不逐条列：它们天天占前几行，反而把今天该交的挤没了
                if (dl < -StaleOverdueDays) { stale++; continue; }
                urgent.Add(new ReminderLine
                {
                    Id = t.Id, Text = t.Text, DaysLeft = dl,
                    State = StateOf(t.End, now), Tail = tail,
                });
                continue;   // 已经在催了，不再叠一条过半提醒
            }
            if (!multiDay || span < NudgeMinSpan) continue;
            if (nudged != null && t.Id.Length > 0 && nudged.Contains(t.Id)) continue;
            if (t.Progress >= NudgeProgressBelow) continue;

            int elapsed = (now.Date - t.Start.Date).Days + 1;
            if (elapsed <= 0) continue;               // 还没开始
            if (elapsed * 2 < span) continue;         // 还没过半
            if (elapsed > span) elapsed = span;

            nudge.Add(new ReminderLine
            {
                Id = t.Id, Text = t.Text, DaysLeft = dl, IsNudge = true,
                State = "已过半程 · 第 " + elapsed + "/" + span + " 天", Tail = tail,
            });
        }
        urgent.Sort((a, b) =>
        {
            int ra = Rank(a.DaysLeft), rb = Rank(b.DaysLeft);
            if (ra != rb) return ra.CompareTo(rb);
            if (a.DaysLeft != b.DaysLeft) return a.DaysLeft.CompareTo(b.DaysLeft);
            return string.CompareOrdinal(a.Text, b.Text);
        });
        nudge.Sort((a, b) => a.DaysLeft.CompareTo(b.DaysLeft));
        return (urgent, nudge, stale);
    }

    /// <summary>排序层级：刚逾期的先（还救得回来）→ 今天截止 → 明天截止</summary>
    private static int Rank(int daysLeft)
    {
        if (daysLeft < 0) return 0;
        if (daysLeft == 0) return 1;
        return 2;
    }

    public static string Title(List<ReminderLine> urgent, List<ReminderLine> nudge)
    {
        var parts = new List<string>();
        int overdue = urgent.Count(x => x.DaysLeft < 0);
        int today = urgent.Count(x => x.DaysLeft == 0);
        int tomorrow = urgent.Count(x => x.DaysLeft == 1);
        if (overdue > 0) parts.Add(overdue + " 项已逾期");
        if (today > 0) parts.Add(today + " 项今天截止");
        if (tomorrow > 0) parts.Add(tomorrow + " 项明天截止");
        if (nudge.Count > 0) parts.Add(nudge.Count + " 项已过半程");
        if (parts.Count == 0) return null;
        return "待办提醒 · " + string.Join("，", parts);
    }

    public static string Body(List<ReminderLine> urgent, List<ReminderLine> nudge, int staleOverdue, int maxLines, int maxText)
    {
        var all = new List<ReminderLine>();
        all.AddRange(urgent);
        all.AddRange(nudge);
        if (all.Count == 0 && staleOverdue == 0) return null;

        var sb = new List<string>();
        sb.AddRange(all.Take(maxLines).Select(x => x.Render(maxText)));
        if (all.Count > maxLines) sb.Add("…等共 " + all.Count + " 项");
        if (staleOverdue > 0) sb.Add("另有 " + staleOverdue + " 项逾期超 " + StaleOverdueDays + " 天");
        return string.Join("\n", sb);
    }

    public static bool AnyOverdue(List<ReminderLine> urgent) => urgent.Any(x => x.DaysLeft < 0);
}

/// <summary>
/// --reminder-preview 自检入口：把「某个时刻会弹什么」算成文本写文件。
/// 提醒是弹窗，没法截图验证，所以把文案逻辑抽出来离线跑。
/// </summary>
internal static class ReminderPreview
{
    public static void Run(string[] args)
    {
        var outp = Path.Combine(Path.GetTempPath(), "todocal-reminder.txt");
        try
        {
            var dataFile = ArgValue(args, "--data");
            if (string.IsNullOrEmpty(dataFile))
                dataFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "TodoCalendar", "todos.json");

            var now = DateTime.Now;
            var nowArg = ArgValue(args, "--now");
            if (!string.IsNullOrEmpty(nowArg) && DateTime.TryParse(nowArg.Replace('T', ' '), out var parsed)) now = parsed;

            var tasks = new List<ReminderTask>();
            if (File.Exists(dataFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(dataFile));
                if (doc.RootElement.TryGetProperty("todos", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var el in arr.EnumerateArray())
                    {
                        var t = Reminder.ParseTask(el);
                        if (t != null) tasks.Add(t);
                    }
            }

            var (urgent, nudge, stale) = Reminder.Pick(tasks, now, null);
            var title = Reminder.Title(urgent, nudge);
            var body = Reminder.Body(urgent, nudge, stale, 3, 20);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("ASOF " + now.ToString("yyyy-MM-dd HH:mm") + "   tasks=" + tasks.Count);
            sb.AppendLine("TITLE " + (title ?? "(无提醒)"));
            sb.AppendLine("BODY");
            sb.AppendLine(body ?? "(无)");
            File.WriteAllText(outp, sb.ToString(), System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(outp, "ERROR " + ex); } catch { }
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

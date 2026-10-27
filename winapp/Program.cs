using System;
using System.IO;
using System.Threading;

namespace TodoCalendar;

internal static class Program
{
    // 跨实例「唤醒」事件：第二个实例以此通知已运行的实例显示窗口
    private const string ShowEventName = @"Local\TodoCalendar.ShowEvent";

    [STAThread]
    private static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            T("UNHANDLED: " + e.ExceptionObject);
        var importFile = ParseArg(args, "--import");

        // 自检模式：把「此刻会弹出什么提醒」算成文本写文件就退出，不开窗口、不抢单实例
        foreach (var a in args)
            if (string.Equals(a, "--reminder-preview", StringComparison.OrdinalIgnoreCase))
            {
                ReminderPreview.Run(args);
                return;
            }

        // 开机自启用：静默起在托盘，不弹窗、不抢焦点
        var startHidden = HasFlag(args, "--tray") || HasFlag(args, "--minimized");

        // 单实例：重复启动不直接退出，而是唤醒已在运行的实例并让其置前
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName, out bool createdNew);
        if (!createdNew)
        {
            try { showEvent.Set(); } catch { /* 忽略 */ }
            return;
        }

        // 导入模式需要独立进程读写同一份数据，不抢全局热键/托盘
        if (importFile == null)
        {
            using var mutex = new Mutex(true, @"Local\TodoCalendar.SingleInstance", out bool owned);
            if (!owned)
            {
                try { showEvent.Set(); } catch { /* 忽略 */ }
                return;
            }
        }

        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm(importFile, showEvent, startHidden));
        }
        catch (Exception ex)
        {
            T("EXCEPTION in Run: " + ex);
            throw;
        }
        finally
        {
            showEvent.Set(); // 退出时释放等待线程
        }
    }

    internal static void T(string s)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "todocal-boot.log"),
                DateTime.Now.ToString("HH:mm:ss.fff ") + s + Environment.NewLine);
        }
        catch { /* 忽略 */ }
    }

    private static bool HasFlag(string[] args, string name)
    {
        foreach (var a in args)
            if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string ParseArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == name && i + 1 < args.Length) return Path.GetFullPath(args[i + 1]);
            if (args[i].StartsWith(name + "=")) return Path.GetFullPath(args[i].Substring(name.Length + 1));
        }
        return null;
    }
}

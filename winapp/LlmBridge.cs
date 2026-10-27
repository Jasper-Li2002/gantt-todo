using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace TodoCalendar;

/// <summary>
/// AI 桥接：API Key 只存在于宿主进程，页面永远拿不到明文。
/// - 落盘用 Windows DPAPI（按当前用户）加密到 settings.dat
/// - 由宿主代发 HTTPS 请求，避免浏览器 CORS / 页面内存泄露 key
/// </summary>
public sealed partial class MainForm
{
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("TodoCalendar/2.4");
        return h;
    }

    private string SettingsFile => Path.Combine(_dataDir, "settings.dat");

    private sealed class AiSettings
    {
        public string provider { get; set; } = "deepseek";
        public string baseUrl { get; set; } = "https://api.deepseek.com/v1";
        public string model { get; set; } = "deepseek-chat";
        public string apiKey { get; set; } = "";   // 仅在内存中为明文，落盘前加密
    }

    private AiSettings _ai = new AiSettings();

    // ---------------- DPAPI（P/Invoke，避免额外 NuGet 包） ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob pDataIn, string szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static byte[] TakeBlob(DataBlob b)
    {
        var arr = new byte[b.cbData];
        Marshal.Copy(b.pbData, arr, 0, b.cbData);
        return arr;
    }

    private static string ProtectKey(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var bytes = Encoding.UTF8.GetBytes(plain);
        var input = new DataBlob { cbData = bytes.Length, pbData = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, input.pbData, bytes.Length);
            var output = new DataBlob();
            if (!CryptProtectData(ref input, "TodoCalendar.ApiKey", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref output))
                throw new InvalidOperationException("DPAPI 加密失败，错误码 " + Marshal.GetLastWin32Error());
            try { return Convert.ToBase64String(TakeBlob(output)); }
            finally { LocalFree(output.pbData); }
        }
        finally { Marshal.FreeHGlobal(input.pbData); }
    }

    private static string UnprotectKey(string cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return "";
        var bytes = Convert.FromBase64String(cipher);
        var input = new DataBlob { cbData = bytes.Length, pbData = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, input.pbData, bytes.Length);
            var output = new DataBlob();
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref output))
                return "";
            try { return Encoding.UTF8.GetString(TakeBlob(output)); }
            finally { LocalFree(output.pbData); }
        }
        finally { Marshal.FreeHGlobal(input.pbData); }
    }

    // ---------------- 设置读写 ----------------

    private static string Str(JsonElement e, string name, string dflt)
    {
        return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? dflt) : dflt;
    }

    private void LoadAiSettings()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsFile));
            var r = doc.RootElement;
            _ai.provider = Str(r, "provider", _ai.provider);
            _ai.baseUrl = Str(r, "baseUrl", _ai.baseUrl);
            _ai.model = Str(r, "model", _ai.model);
            _ai.apiKey = UnprotectKey(Str(r, "apiKeyEnc", ""));
        }
        catch (Exception ex) { LogBridge("settings load failed: " + ex.Message); }
    }

    private void SaveAiSettings()
    {
        var obj = new Dictionary<string, object>
        {
            ["provider"] = _ai.provider ?? "",
            ["baseUrl"] = _ai.baseUrl ?? "",
            ["model"] = _ai.model ?? "",
            ["apiKeyEnc"] = ProtectKey(_ai.apiKey ?? ""),
            ["savedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        };
        WriteAtomic(SettingsFile, JsonSerializer.Serialize(obj));
    }

    private static string MaskKey(string k)
    {
        if (string.IsNullOrEmpty(k)) return "";
        if (k.Length <= 8) return new string('*', k.Length);
        return k.Substring(0, Math.Min(4, k.Length)) + "••••••" + k.Substring(k.Length - 4);
    }

    private object Snapshot() => new
    {
        provider = _ai.provider,
        baseUrl = _ai.baseUrl,
        model = _ai.model,
        hasKey = !string.IsNullOrEmpty(_ai.apiKey),
        keyMask = MaskKey(_ai.apiKey)
    };

    // ---------------- 环境变量探测 ----------------

    private static readonly string[] KeyEnvNames =
    {
        "DEEPSEEK_API_KEY", "DASHSCOPE_API_KEY", "QWEN_API_KEY", "ALIYUN_API_KEY",
        "MOONSHOT_API_KEY", "KIMI_API_KEY",
        "ZHIPUAI_API_KEY", "ZHIPU_API_KEY", "GLM_API_KEY",
        "OPENAI_API_KEY"
    };

    private static List<object> ScanEnvKeys()
    {
        var found = new List<object>();
        foreach (var name in KeyEnvNames)
        {
            string v = null;
            foreach (var scope in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
            {
                try { v = Environment.GetEnvironmentVariable(name, scope); } catch { v = null; }
                if (!string.IsNullOrWhiteSpace(v)) break;
            }
            if (!string.IsNullOrWhiteSpace(v))
                found.Add(new { name, mask = MaskKey(v.Trim()) });
        }
        return found;
    }

    private static string ReadEnvKey(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        foreach (var scope in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            try
            {
                var v = Environment.GetEnvironmentVariable(name, scope);
                if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
            }
            catch { }
        }
        return "";
    }

    // ---------------- 消息处理 ----------------

    private void Post(CoreWebView2 core, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        void Send()
        {
            try { core.PostWebMessageAsJson(json); LogBridge("post ok: " + Clip(json, 90)); }
            catch (Exception ex) { LogBridge("post FAILED: " + ex.GetType().Name + " " + ex.Message + " | " + Clip(json, 80)); }
        }
        try
        {
            if (InvokeRequired) BeginInvoke((Action)Send);
            else Send();
        }
        catch { /* 窗口已销毁 */ }
    }

    private void ReplyErr(CoreWebView2 core, string id, string op, string kind, string msg, long ms = 0)
        => Post(core, new { type = "aiResult", id, op, ok = false, errKind = kind, msg, ms });

    private static string Endpoint(string baseUrl)
    {
        var u = (baseUrl ?? "").Trim().TrimEnd('/');
        if (u.Length == 0) u = "https://api.deepseek.com/v1";
        if (u.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return u;
        if (u.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) || u.EndsWith("/v4", StringComparison.OrdinalIgnoreCase)
            || u.Contains("/compatible-mode/v1", StringComparison.OrdinalIgnoreCase))
            return u + "/chat/completions";
        return u + "/v1/chat/completions";
    }

    private static string Classify(int code) => code switch
    {
        401 or 403 => "auth",
        404 => "notfound",
        429 => "quota",
        >= 500 => "server",
        _ => "http"
    };

    private static string Clip(string s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length <= n ? s : s.Substring(0, n) + "…";
    }

    private void HandleAiMessage(CoreWebView2 core, JsonElement root)
    {
        var id = Str(root, "id", "");
        var op = Str(root, "op", "");
        LogBridge("ai op=" + op + " id=" + id);
        try
        {
            switch (op)
            {
                case "get":
                    LoadAiSettings();
                    Post(core, new { type = "aiResult", id, op, ok = true, data = Snapshot() });
                    break;

                case "save":
                    {
                        if (root.TryGetProperty("payload", out var p))
                        {
                            _ai.provider = Str(p, "provider", _ai.provider);
                            _ai.baseUrl = Str(p, "baseUrl", _ai.baseUrl);
                            _ai.model = Str(p, "model", _ai.model);
                            var incoming = Str(p, "apiKey", "");
                            if (!string.IsNullOrWhiteSpace(incoming)) _ai.apiKey = incoming.Trim();
                            var envName = Str(p, "fromEnv", "");
                            if (envName.Length > 0)
                            {
                                var v = ReadEnvKey(envName);
                                if (v.Length == 0)
                                {
                                    ReplyErr(core, id, op, "env", "环境变量 " + envName + " 读不到（改完环境变量需要重启本程序）");
                                    return;
                                }
                                _ai.apiKey = v;
                            }
                        }
                        SaveAiSettings();
                        LogBridge("ai settings saved: " + _ai.provider + " / " + _ai.model);
                        Post(core, new { type = "aiResult", id, op, ok = true, data = Snapshot() });
                    }
                    break;

                case "clearkey":
                    _ai.apiKey = "";
                    SaveAiSettings();
                    Post(core, new { type = "aiResult", id, op, ok = true, data = Snapshot() });
                    break;

                case "scan":
                    Post(core, new { type = "aiResult", id, op, ok = true, data = new { keys = ScanEnvKeys() } });
                    break;

                case "test":
                case "gen":
                    {
                        string system = Str(root, "system", "");
                        string user = Str(root, "user", "");
                        double temperature = 0.6;   // 太低会照抄原文，改写类任务 0.5~0.7 比较合适
                        if (root.TryGetProperty("payload", out var pl))
                        {
                            system = Str(pl, "system", system);
                            user = Str(pl, "user", user);
                            if (pl.TryGetProperty("temperature", out var tp) && tp.ValueKind == JsonValueKind.Number)
                                temperature = Math.Clamp(tp.GetDouble(), 0, 1.5);
                        }
                        _ = Task.Run(() => CallLlmAsync(core, id, op, system, user, temperature));
                    }
                    break;

                default:
                    ReplyErr(core, id, op, "internal", "未知操作：" + op);
                    break;
            }
        }
        catch (Exception ex)
        {
            LogBridge("ai op failed: " + op + " → " + ex.Message);
            ReplyErr(core, id, op, "internal", ex.Message);
        }
    }

    private async Task CallLlmAsync(CoreWebView2 core, string id, string op, string system, string user, double temperature = 0.6)
    {
        var sw = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(_ai.apiKey))
        {
            ReplyErr(core, id, op, "nokey", "还没填 API Key —— 点右上角灯泡看怎么填");
            return;
        }
        var url = Endpoint(_ai.baseUrl);
        var body = new
        {
            model = string.IsNullOrWhiteSpace(_ai.model) ? "deepseek-chat" : _ai.model,
            messages = new object[]
            {
                new { role = "system", content = string.IsNullOrWhiteSpace(system) ? "你好，请只回复 ok。" : system },
                new { role = "user", content = string.IsNullOrWhiteSpace(user) ? "ok" : user }
            },
            temperature,
            max_tokens = 1200,
            stream = false
        };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _ai.apiKey);
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            sw.Stop();

            if (!resp.IsSuccessStatusCode)
            {
                var kind = Classify((int)resp.StatusCode);
                LogBridge("llm http " + (int)resp.StatusCode + " " + kind);
                ReplyErr(core, id, op, kind,
                    "HTTP " + (int)resp.StatusCode + "：" + Clip(text, 220), sw.ElapsedMilliseconds);
                return;
            }

            string content = "";
            int inTok = 0, outTok = 0;
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.TryGetProperty("choices", out var ch) && ch.ValueKind == JsonValueKind.Array && ch.GetArrayLength() > 0)
                {
                    var msg = ch[0].GetProperty("message");
                    content = msg.TryGetProperty("content", out var c) ? (c.GetString() ?? "") : "";
                }
                if (root.TryGetProperty("usage", out var us))
                {
                    if (us.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number) inTok = pt.GetInt32();
                    if (us.TryGetProperty("completion_tokens", out var ct) && ct.ValueKind == JsonValueKind.Number) outTok = ct.GetInt32();
                }
            }
            catch (Exception ex)
            {
                ReplyErr(core, id, op, "badjson", "返回不是预期的格式：" + ex.Message + " · " + Clip(text, 160), sw.ElapsedMilliseconds);
                return;
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                ReplyErr(core, id, op, "empty", "模型返回了空内容", sw.ElapsedMilliseconds);
                return;
            }

            LogBridge("llm ok: " + _ai.model + " " + sw.ElapsedMilliseconds + "ms in=" + inTok + " out=" + outTok);
            Post(core, new
            {
                type = "aiResult", id, op, ok = true,
                text = content.Trim(),
                ms = sw.ElapsedMilliseconds,
                model = _ai.model,
                tokens = new { @in = inTok, @out = outTok }
            });
        }
        catch (TaskCanceledException)
        {
            ReplyErr(core, id, op, "timeout", "请求超时（40 秒），请检查网络或换一个模型", sw.ElapsedMilliseconds);
        }
        catch (HttpRequestException ex)
        {
            ReplyErr(core, id, op, "network", "网络不通：" + ex.Message, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            LogBridge("llm failed: " + ex.GetType().Name + " " + ex.Message);
            ReplyErr(core, id, op, "internal", ex.Message, sw.ElapsedMilliseconds);
        }
    }
}

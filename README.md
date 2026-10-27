# gantt-todo · 日历待办（甘特视图）

Windows 桌面待办 / 排期工具。WinForms 外壳 + WebView2 承载单页前端，数据全部存在本机。

- 甘特式排期视图：跨天任务拖拽建条、条宽即周期
- 月视图 / 列表视图切换，标签与日历分组筛选
- 托盘常驻 + 开机自启，到期待办每天提醒一次（含剩余时间与跨天过半提醒）
- AI 日报 / 周报：支持 DeepSeek / 通义千问 / Kimi / 智谱，Key 用 Windows DPAPI 加密存本机
- 数据：`%APPDATA%\TodoCalendar\todos.json`，与主程序、AI、MCP 同源

## 仓库

- 远端：`https://github.com/Jasper-Li2002/gantt-todo`（私有）
- 主分支 `main`，版本以 tag 标记（当前 `v2.5.10`）
- 推 tag 自动触发 `.github/workflows/release.yml` 出包并发 Release

## 目录结构

```
todo.html                    前端唯一开发源（构建时同步进 winapp/web/index.html 并内嵌为资源）
winapp/
  TodoCalendar.csproj        项目文件，版本号在这里改
  MainForm.cs                宿主窗体：WebView2、托盘、提醒、开机自启、文件读写
  LlmBridge.cs               AI 桥接：DPAPI 存 Key、OpenAI 兼容协议调用
  Reminder.cs                提醒计算（纯函数，可离线自检）
  Program.cs                 入口：单实例、--tray、--import、--reminder-preview
  build/icon.ico             应用图标
docs/
  使用说明.txt                完整变更记录 + 用户手册
  待做-桌面便签.md            已定稿未实现的需求
tools/
  pack.ps1                   一条命令出包（框架依赖版 + 自包含单文件 zip）
  先看这个.txt                分发包里的用户引导，@VERSION@ 会被替换
```

## 环境

- .NET SDK 10（`dotnet --version` ≥ 10.0）
- WebView2 Runtime（Win11 / 正常更新的 Win10 自带）

## 日常开发

```bash
dotnet build winapp/TodoCalendar.csproj -c Release        # 编译检查
dotnet publish winapp/TodoCalendar.csproj -c Release -o release/fd   # 自用（框架依赖，1.6MB）
```

部署到本机：关闭正在运行的实例，把 `release/fd/TodoCalendar.exe` 覆盖到程序目录。
**注意**：改了前端必须同时升 `winapp/TodoCalendar.csproj` 的 `Version` 与 `FileVersion`，
否则启动时不会重新释放内嵌页面（判断依据是版本号）。

## 出分发包

```powershell
powershell -ExecutionPolicy Bypass -File tools\pack.ps1
```

产出 `dist/日历待办-v<版本>-win64.zip`（自包含单文件，目标机器不需要装 .NET）。

## 版本约定

- **改功能 / 改文案 = 升版本号**，格式 `主.次.修订`，当前线在 2.5.x
- 每版在 `docs/使用说明.txt` 顶部加一节 `【vX.Y.Z 做了什么】日期`
- 出包后打 tag：`git tag -a v2.5.10 -m "..."`，tag 与 zip 里的版本必须一致

## 自检手段

```bash
# 提醒文案（气泡没法截图，只能算出来看）
release/fd/TodoCalendar.exe --reminder-preview --now "2026-10-24 10:00"
# → 结果写到 %TEMP%\todocal-reminder.txt

# 更新检查（只提示，不自动替换）
release/fd/TodoCalendar.exe --check-update --url "更新源地址"
# → 结果写到 %TEMP%\todocal-update.txt；更新源配置在 %APPDATA%\TodoCalendar\update.json

# 前端渲染：用 playwright-core 驱动系统 Edge 直接加载 todo.html
# 运行时日志：%TEMP%\todocal.log（reminder: / probe: / post ok: 等）
```

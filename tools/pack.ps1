param(
  [switch]$SkipSelfContained,
  [switch]$NoDeploy,
  [string]$InstallDir = 'C:\App\TodoCalendar'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # 关掉 Compress-Archive 的进度条刷屏
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$csproj = Join-Path $root 'winapp\TodoCalendar.csproj'
$ver = [regex]::Match((Get-Content $csproj -Raw), '<Version>([^<]+)</Version>').Groups[1].Value
if (-not $ver) { throw '读不到版本号' }
Write-Host "版本 v$ver" -ForegroundColor Cyan

Write-Host '1/5 编译自用版（框架依赖）'
dotnet publish winapp\TodoCalendar.csproj -c Release -o release\fd | Out-Null

Write-Host '2/5 编译分发包（自带运行时，单文件）'
if (-not $SkipSelfContained) {
  dotnet publish winapp\TodoCalendar.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -o release\sc | Out-Null
}

Write-Host '3/5 打包'
# 不用 Remove-Item：某些环境会拦截删除。目录存在就复用，文件用 -Force 覆盖
$out = Join-Path $root "dist\日历待办-v$ver"
New-Item -ItemType Directory -Path $out -Force | Out-Null

Copy-Item 'release\sc\TodoCalendar.exe' $out -Force
(Get-Content 'tools\先看这个.txt' -Raw -Encoding UTF8).Replace('@VERSION@', $ver) | Set-Content (Join-Path $out '先看这个.txt') -Encoding utf8
Copy-Item 'docs\使用说明.txt' (Join-Path $out '更新记录.txt')

$zip = Join-Path $root "dist\日历待办-v$ver-win64.zip"
Compress-Archive -Path $out -DestinationPath $zip -CompressionLevel Optimal -Force

$h = (Get-FileHash $zip -Algorithm SHA256).Hash
$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
"日历待办-v$ver-win64.zip`r`nSHA256 $h`r`n大小 $mb MB" | Set-Content (Join-Path $root 'dist\SHA256.txt') -Encoding utf8

# ---------- 部署到本机安装目录（以后更新只认这一个位置） ----------
if ($NoDeploy) {
  Write-Host '4/5 跳过部署（-NoDeploy）'
} else {
  Write-Host "4/5 部署到 $InstallDir"

  $srcExe = Join-Path $root 'release\sc\TodoCalendar.exe'
  if (-not (Test-Path $srcExe)) { $srcExe = Join-Path $root 'release\fd\TodoCalendar.exe' }
  if (-not (Test-Path $srcExe)) { throw '找不到刚编译出来的 TodoCalendar.exe' }

  New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
  $target = Join-Path $InstallDir 'TodoCalendar.exe'

  # exe 正在运行会锁文件：先停，部署完再按原状态拉起
  $running = @(Get-Process -Name 'TodoCalendar' -ErrorAction SilentlyContinue)
  if ($running.Count -gt 0) {
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 800
  }

  # 刚停掉的进程有时还攥着句柄几百毫秒，硬拷会报『正由另一进程使用』
  $copied = $false
  for ($i = 1; $i -le 10; $i++) {
    try { Copy-Item $srcExe $target -Force -ErrorAction Stop; $copied = $true; break }
    catch { Start-Sleep -Milliseconds 500 }
  }
  if (-not $copied) { throw "部署失败：$target 被占用，请先手动退出日历待办再重跑" }
  Copy-Item 'docs\使用说明.txt' (Join-Path $InstallDir '使用说明.txt') -Force

  $h1 = (Get-FileHash $srcExe -Algorithm SHA256).Hash
  $h2 = (Get-FileHash $target -Algorithm SHA256).Hash
  if ($h1 -ne $h2) { throw "部署后哈希不一致：$target" }

  # 开机自启指向的路径跟着换，避免程序搬家后自启失效
  $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
  $cur = (Get-ItemProperty -Path $runKey -ErrorAction SilentlyContinue).TodoCalendar
  if ($cur) {
    $want = '"' + $target + '" --tray'
    if ($cur -ne $want) {
      Set-ItemProperty -Path $runKey -Name 'TodoCalendar' -Value $want
      Write-Host '  · 已修正开机自启路径'
    }
  }

  if ($running.Count -gt 0) { Start-Process $target }
  Write-Host "  · 已部署 $target（$([math]::Round((Get-Item $target).Length / 1MB, 1)) MB）" -ForegroundColor Green
}

# ---------- 清理 dist：只保留最近两个版本 ----------
# 每个版本是 42MB 的 zip + 47MB 的解压目录，攒几个就上百兆。
# 源码在 git 里、历史的还躺在 GitHub Release 上，本地留最近两个够回退就行。
function Get-VerKey($name) {
  $m = [regex]::Match($name, 'v(\d+)\.(\d+)\.(\d+)')
  if (-not $m.Success) { return $null }
  '{0}.{1}.{2}' -f $m.Groups[1].Value, $m.Groups[2].Value, $m.Groups[3].Value
}

$dist = Join-Path $root 'dist'
$items = @()
Get-ChildItem $dist -Directory -ErrorAction SilentlyContinue | ForEach-Object {
  $k = Get-VerKey $_.Name
  if ($k) { $items += [pscustomobject]@{ Key = $k; Path = $_.FullName } }
}
Get-ChildItem $dist -File -Filter '*.zip' -ErrorAction SilentlyContinue | ForEach-Object {
  $k = Get-VerKey $_.Name
  if ($k) { $items += [pscustomobject]@{ Key = $k; Path = $_.FullName } }
}

$keepVer = @($items | Sort-Object { [version]$_.Key } -Descending |
  Select-Object -ExpandProperty Key -Unique | Select-Object -First 2)
$drop = @($items | Where-Object { $keepVer -notcontains $_.Key })

if ($drop.Count) {
  Write-Host ("5/5 清理 dist：保留 " + ($keepVer -join ' / ') + "，删除 $($drop.Count) 项旧产物")
  foreach ($it in $drop) {
    try { Remove-Item $it.Path -Recurse -Force -ErrorAction Stop }
    catch { Write-Host ("  · 删不掉（可能被占用）：" + (Split-Path $it.Path -Leaf)) -ForegroundColor Yellow }
  }
} else {
  Write-Host '5/5 dist 已只剩最近两个版本，无需清理'
}

Write-Host ''
Write-Host "完成：$zip" -ForegroundColor Green
Write-Host "大小：$mb MB   SHA256：$h"
Write-Host ''
Write-Host '别忘了两件事：'
Write-Host "  git tag -a v$ver -m 'v$ver'"
Write-Host '  在 docs\使用说明.txt 顶部补一节变更记录'

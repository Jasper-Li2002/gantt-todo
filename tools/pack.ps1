param([switch]$SkipSelfContained)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # 关掉 Compress-Archive 的进度条刷屏
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$csproj = Join-Path $root 'winapp\TodoCalendar.csproj'
$ver = [regex]::Match((Get-Content $csproj -Raw), '<Version>([^<]+)</Version>').Groups[1].Value
if (-not $ver) { throw '读不到版本号' }
Write-Host "版本 v$ver" -ForegroundColor Cyan

Write-Host '1/3 编译自用版（框架依赖）'
dotnet publish winapp\TodoCalendar.csproj -c Release -o release\fd | Out-Null

Write-Host '2/3 编译分发包（自带运行时，单文件）'
if (-not $SkipSelfContained) {
  dotnet publish winapp\TodoCalendar.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -o release\sc | Out-Null
}

Write-Host '3/3 打包'
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

Write-Host ''
Write-Host "完成：$zip" -ForegroundColor Green
Write-Host "大小：$mb MB   SHA256：$h"
Write-Host ''
Write-Host '别忘了两件事：'
Write-Host "  git tag -a v$ver -m 'v$ver'"
Write-Host '  在 docs\使用说明.txt 顶部补一节变更记录'

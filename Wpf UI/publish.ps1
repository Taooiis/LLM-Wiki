#!/usr/bin/env pwsh
<#
.SYNOPSIS
  一键打包 LLM Wiki：自包含发布 + 生成 Windows 安装包。

.DESCRIPTION
  1. 结束正在运行的 LLM Wiki，避免输出文件被占用
  2. dotnet publish（Release / win-x64 / self-contained，含 .NET 10 运行时）
  3. 调用 Inno Setup 编译安装包（中文界面）

  产物：
    可运行目录  Wpf UI\bin\Release\net10.0-windows\publish\
    安装包      Wpf UI\install\Output\LLM Wiki Setup.exe

.PARAMETER Version
  安装包版本号，默认 1.0.0。会覆盖 LlmWiki.iss 里的 MyAppVersion。

.PARAMETER SkipInstaller
  只发布可运行目录，不生成安装包。

.EXAMPLE
  .\publish.ps1
  .\publish.ps1 -Version 1.0.1
  .\publish.ps1 -SkipInstaller
#>
[CmdletBinding()]
param(
    [string]$Version = "1.0.0",
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'

$root     = $PSScriptRoot
$proj     = Join-Path $root 'LLM Wiki.csproj'
$publish  = Join-Path $root 'bin\Release\net10.0-windows\publish'
$iss      = Join-Path $root 'install\LlmWiki.iss'
$setupDir = Join-Path $root 'install\Output'
$setupExe = Join-Path $setupDir 'LLM Wiki Setup.exe'

$sw = [System.Diagnostics.Stopwatch]::StartNew()

function Write-Step([string]$text) {
    Write-Host ""
    Write-Host "==> $text" -ForegroundColor Cyan
}

# ---------- 1. 环境检查 ----------

Write-Step "检查构建环境"

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { throw "未找到 dotnet CLI，请先安装 .NET 10 SDK。" }
Write-Host "  dotnet : $($dotnet.Source)"

$iscc = $null
if (-not $SkipInstaller) {
    $isccCmd = Get-Command iscc -ErrorAction SilentlyContinue
    if ($isccCmd) {
        $iscc = $isccCmd.Source
    } else {
        $candidates = @(
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
        )
        $iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    }
    if (-not $iscc) {
        throw "未找到 Inno Setup 的 ISCC.exe。安装：winget install JRSoftware.InnoSetup"
    }
    Write-Host "  ISCC   : $iscc"
    if (-not (Test-Path $iss)) { throw "缺少安装脚本：$iss" }
}

# ---------- 2. 结束占用进程 ----------

Write-Step "结束运行中的 LLM Wiki"
$running = Get-Process "LLM Wiki" -ErrorAction SilentlyContinue
if ($running) {
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 800
    Write-Host "  已结束 $($running.Count) 个进程"
} else {
    Write-Host "  无需处理"
}

# ---------- 3. 清理旧产物 ----------

Write-Step "清理旧产物"
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
if (-not $SkipInstaller -and (Test-Path $setupExe)) { Remove-Item -Force $setupExe }
Write-Host "  完成"

# ---------- 4. 发布 ----------

Write-Step "发布自包含目录（win-x64）"
Write-Host "  项目: $proj"
Write-Host "  输出: $publish"

dotnet publish $proj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o $publish `
    --nologo

if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败 (exit $LASTEXITCODE)" }

$exe = Join-Path $publish 'LLM Wiki.exe'
if (-not (Test-Path $exe)) { throw "发布结果缺少 LLM Wiki.exe" }
Write-Host "  发布成功: $exe" -ForegroundColor Green

# ---------- 5. 安装包 ----------

if ($SkipInstaller) {
    Write-Step "跳过安装包（-SkipInstaller）"
} else {
    Write-Step "编译安装包（版本 $Version）"
    if (-not (Test-Path $setupDir)) { New-Item -ItemType Directory -Path $setupDir | Out-Null }

    & $iscc "/DMyAppVersion=$Version" $iss
    if ($LASTEXITCODE -ne 0) { throw "ISCC 编译失败 (exit $LASTEXITCODE)" }
    if (-not (Test-Path $setupExe)) { throw "未生成安装包: $setupExe" }

    $sizeMB = [math]::Round((Get-Item $setupExe).Length / 1MB, 1)
    Write-Host "  安装包: $setupExe ($sizeMB MB)" -ForegroundColor Green
}

# ---------- 6. 汇总 ----------

$sw.Stop()
Write-Host ""
Write-Host "打包完成，用时 $([math]::Round($sw.Elapsed.TotalSeconds,1)) 秒" -ForegroundColor Green
Write-Host "  可运行目录: $publish"
if (-not $SkipInstaller) { Write-Host "  安装包    : $setupExe" }

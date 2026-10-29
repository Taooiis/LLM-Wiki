#!/usr/bin/env pwsh
<#
.SYNOPSIS
  一键发布 LLM Wiki 桌面客户端为自包含可跑目录。

.DESCRIPTION
  输出 self-contained, win-x64，含 .NET 10 + WPF + Kestrel 运行时。
  产物目录: Wpf UI/bin/Release/net10.0-windows/publish/
  从任意工作目录直接运行 "LLM Wiki.exe" 即可。

.EXAMPLE
  .\publish.ps1          # Release 构建 + 发布
#>
$ErrorActionPreference = 'Stop'

$root  = Split-Path $PSScriptRoot -Parent      # Wpf UI/
$proj  = Join-Path $root 'LLM Wiki.csproj'
$out   = Join-Path $root 'bin\Release\net10.0-windows\publish'

Write-Host "发布 LLM Wiki (win-x64, self-contained)..." -ForegroundColor Cyan
Write-Host "  项目: $proj"
Write-Host "  输出: $out"

# 清理旧产物
if (Test-Path $out) { Remove-Item -Recurse -Force $out }

dotnet publish $proj `
  -c Release `
  -r win-x64 `
  -p:SelfContained=true `
  -p:PublishTrimmed=true `
  -o $out `
  --nologo

if ($LASTEXITCODE -ne 0) { throw "publish 失败 (exit $LASTEXITCODE)" }

Write-Host ""
Write-Host "发布成功！" -ForegroundColor Green
Write-Host "  入口: $out\LLM Wiki.exe"
Write-Host "  从任意目录运行：cd '$out'; .\LLM Wiki.exe"
Write-Host ""
Write-Host "安装包："
Write-Host "  ISCC Wpf UI\install\LlmWiki.iss"
Write-Host "  输出: Wpf UI\install\LLM Wiki Setup.exe"

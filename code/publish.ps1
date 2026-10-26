#!/usr/bin/env pwsh
<#
.SYNOPSIS
  一键发布 LLM Wiki 桌面客户端为自包含可跑目录。

.DESCRIPTION
  输出 self-contained, win-x64, 含 WPF + ASP.NET Core (Kestrel 分享服务) 运行时。
  产物目录: code\publish\LlmWiki\
  从任意工作目录直接运行 LlmWiki.App.exe 即可。

.EXAMPLE
  .\publish.ps1          # Release 构建 + 发布
#>
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent          # code/
$proj = Join-Path $root 'src/LlmWiki.App/LlmWiki.App.csproj'
$out  = Join-Path $root 'publish/LlmWiki'

Write-Host "发布 LLM Wiki (win-x64, self-contained)..." -ForegroundColor Cyan
Write-Host "  项目: $proj"
Write-Host "  输出: $out"

# 清理旧产物
if (Test-Path $out) { Remove-Item -Recurse -Force $out }

dotnet publish $proj `
  -c Release `
  -r win-x64 `
  -p:SelfContained=true `
  -o $out `
  --nologo

if ($LASTEXITCODE -ne 0) { throw "publish 失败 (exit $LASTEXITCODE)" }

Write-Host ""
Write-Host "发布成功！" -ForegroundColor Green
Write-Host "  入口: $out\LlmWiki.App.exe"
Write-Host "  从任意目录运行: cd '$out'; .\LlmWiki.App.exe"
Write-Host "  首次建库 → 上传 .md/.docx/.xlsx → 工作流页复制提示词 → 左下角分享页开启只读浏览"

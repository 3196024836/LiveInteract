# 兼容原完整包入口，避免继续构建硬编码的0.5包或漏掉新的Mod依赖。
param([switch]$SkipBuild,[string]$Channel='preview')
& (Join-Path $PSScriptRoot 'package-full.ps1') -SkipBuild:$SkipBuild -Channel $Channel

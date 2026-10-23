# 完整包入口：自带 .NET 与固定 WebView2；保留旧版调用方式。
param([switch]$SkipBuild,[string]$Channel='preview')
& (Join-Path $PSScriptRoot 'new-distribution.ps1') -Format Full -SkipBuild:$SkipBuild -Channel $Channel

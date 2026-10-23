# 精简包入口；版本与内容统一由 new-distribution.ps1 处理。
param([switch]$SkipBuild,[string]$Channel='preview')
& (Join-Path $PSScriptRoot 'new-distribution.ps1') -Format Slim -SkipBuild:$SkipBuild -Channel $Channel

param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') }
# 发布前明确使用非 QA 编译，隔离测试控制入口不得进入用户安装。
dotnet build src/LiveInteract.Silksong/LiveInteract.Silksong.csproj -c Release -p:QaBuild=false -t:Rebuild --nologo
if ($LASTEXITCODE -ne 0) { throw 'Production Mod build failed.' }
$out = Join-Path $root 'dist\LiveInteract-0.5.0-win-x64'
if ((Test-Path -LiteralPath $out) -and (Get-ChildItem -LiteralPath $out -Force | Select-Object -First 1)) { throw 'Preserve the existing distribution and use a new version directory.' }
dotnet publish src/LiveInteract.Desktop/LiveInteract.Desktop.csproj -c Release -r win-x64 --self-contained true -p:DebugType=None -o $out --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
# 程序用中文入口，内部托管程序集名称保持稳定。
$originalExe = Join-Path $out 'LiveInteract.exe'
$chineseExe = Join-Path $out '直播互动工具箱.exe'
if (Test-Path -LiteralPath $chineseExe) { throw 'Distribution already exists. Preserve it and use a new version directory.' }
Move-Item -LiteralPath $originalExe -Destination $chineseExe
$payload = Join-Path $out 'payload'
$mod = Join-Path $payload 'mod'
$loader = Join-Path $payload 'loader'
New-Item -ItemType Directory -Path $mod,(Join-Path $loader 'BepInEx\core'),(Join-Path $loader 'BepInEx\config') -Force | Out-Null
Copy-Item -LiteralPath 'src\LiveInteract.Silksong\bin\Release\netstandard2.1\LiveInteract.Silksong.dll','src\LiveInteract.Contracts\bin\Release\netstandard2.1\LiveInteract.Contracts.dll' -Destination $mod
$source = Join-Path $root 'artifacts\loader-package\BepInExPack'
Copy-Item -LiteralPath (Join-Path $source 'winhttp.dll'),(Join-Path $source 'doorstop_config.ini') -Destination $loader
Get-ChildItem -LiteralPath (Join-Path $source 'BepInEx\core') -File -Filter '*.dll' | Copy-Item -Destination (Join-Path $loader 'BepInEx\core')
Copy-Item -LiteralPath (Join-Path $source 'BepInEx\config\BepInEx.cfg') -Destination (Join-Path $loader 'BepInEx\config')
# 固定 WebView2 随包携带，不要求主播安装浏览器或开发环境。
$cab = Join-Path $root 'artifacts\downloads\WebView2-155.0.4283.62-x64.cab'
if (-not (Test-Path -LiteralPath $cab)) { Invoke-WebRequest -UseBasicParsing -Uri 'https://msedge.sf.dl.delivery.mp.microsoft.com/filestreamingservice/files/232cf50b-3980-4531-afbb-779f446d7de1/Microsoft.WebView2.FixedVersionRuntime.155.0.4283.62.x64.cab' -OutFile $cab }
$fixed = Join-Path $root 'artifacts\webview2-fixed'
New-Item -ItemType Directory -Path $fixed -Force | Out-Null
if (-not (Get-ChildItem -LiteralPath $fixed -Recurse -Filter 'msedgewebview2.exe')) { & expand.exe '-F:*' $cab $fixed | Out-Null; if ($LASTEXITCODE -ne 0) { throw 'WebView2 extraction failed.' } }
$runtime = Get-ChildItem -LiteralPath $fixed -Recurse -File -Filter 'msedgewebview2.exe' | Select-Object -First 1
$signature = Get-AuthenticodeSignature -LiteralPath $runtime.FullName
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') { throw 'WebView2 publisher signature verification failed.' }
Copy-Item -LiteralPath $runtime.Directory.FullName -Destination (Join-Path $out 'WebView2Runtime') -Recurse
Copy-Item -LiteralPath 'docs\使用说明.md','docs\兼容与验收.md','docs\THIRD_PARTY_NOTICES.md','docs\0.5默认玩法说明.md','docs\效果验收清单.csv' -Destination $out
New-Item -ItemType Directory -Path (Join-Path $out '直播素材') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root '直播素材\丝之歌-友军整活') -Destination (Join-Path $out '直播素材') -Recurse
Copy-Item -LiteralPath 'README.md','CHANGELOG.md','docs\快速开始.md' -Destination $out
& (Join-Path $PSScriptRoot 'fetch-licenses.ps1')
if (Test-Path -LiteralPath 'artifacts\licenses') { Copy-Item -LiteralPath 'artifacts\licenses' -Destination (Join-Path $out 'ThirdPartyLicenses') -Recurse }
# 清单检查只检查角色和分发边界，不复制游戏引用。
if (Get-ChildItem -LiteralPath $out -Recurse -Filter 'Assembly-CSharp.dll') { throw 'Game assemblies must not be distributed.' }
Write-Output "Portable distribution: $out"

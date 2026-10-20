$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
$root=Split-Path -Parent $PSScriptRoot
$folder=Join-Path $root 'artifacts\licenses'
New-Item -ItemType Directory -Path $folder -Force | Out-Null
# 固定上游来源，只下载不执行；保留未修改组件的许可与源码入口。
$sources=@{
 'Microsoft.Data.Sqlite-LICENSE.txt'='https://raw.githubusercontent.com/dotnet/efcore/main/LICENSE.txt'
 'SQLitePCLRaw-LICENSE.txt'='https://raw.githubusercontent.com/ericsink/SQLitePCL.raw/v2.1.12/LICENSE.TXT'
 'BepInEx-LICENSE.txt'='https://raw.githubusercontent.com/BepInEx/BepInEx/v5.4.23.4/LICENSE'
 'Newtonsoft.Json-LICENSE.txt'='https://raw.githubusercontent.com/JamesNK/Newtonsoft.Json/13.0.3/LICENSE.md'
 'Google.Protobuf-LICENSE.txt'='https://raw.githubusercontent.com/protocolbuffers/protobuf/v32.1/LICENSE'
 'HarmonyX-LICENSE.txt'='https://raw.githubusercontent.com/BepInEx/HarmonyX/master/LICENSE'
 'Mono.Cecil-LICENSE.txt'='https://raw.githubusercontent.com/jbevain/cecil/master/LICENSE.txt'
 'UnityDoorstop-LICENSE.txt'='https://raw.githubusercontent.com/NeighTools/UnityDoorstop/master/LICENSE'
 'MonoMod-LICENSE.txt'='https://raw.githubusercontent.com/MonoMod/MonoMod/reorganize/LICENSE'
 'BepInEx-v5.4.23.4-source.zip'='https://github.com/BepInEx/BepInEx/archive/refs/tags/v5.4.23.4.zip'
}
foreach($entry in $sources.GetEnumerator()){ $target=Join-Path $folder $entry.Key; if(-not(Test-Path -LiteralPath $target)){Invoke-WebRequest -UseBasicParsing -Uri $entry.Value -OutFile $target} }
$cache=if($env:NUGET_PACKAGES){$env:NUGET_PACKAGES}else{Join-Path $HOME '.nuget\packages'}
Copy-Item -LiteralPath (Join-Path $cache 'microsoft.web.webview2\1.0.3856.49\LICENSE.txt') -Destination (Join-Path $folder 'WebView2-SDK-LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $cache 'microsoft.web.webview2\1.0.3856.49\NOTICE.txt') -Destination (Join-Path $folder 'WebView2-SDK-NOTICE.txt') -Force

# 自包含运行环境的许可直接来自本次实际发布使用的本机 NuGet 包。
foreach ($item in @(
 @('microsoft.netcore.app.runtime.win-x64','10.0.12','LICENSE.TXT','DotNet-Runtime-LICENSE.txt'),
 @('microsoft.netcore.app.runtime.win-x64','10.0.12','THIRD-PARTY-NOTICES.TXT','DotNet-Runtime-NOTICES.txt'),
 @('microsoft.windowsdesktop.app.runtime.win-x64','10.0.12','LICENSE','WindowsDesktop-Runtime-LICENSE.txt')
)) { $source = Join-Path $cache ($item[0]+'\'+$item[1]+'\'+$item[2]); if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $folder $item[3]) -Force } }

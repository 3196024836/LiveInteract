param([switch]$SkipBuild)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root
$out=Join-Path $root 'dist\LiveInteract-0.7.8-slim-win-x64'
if(Test-Path -LiteralPath $out){throw 'Preserve the existing distribution; use a new output directory.'}
dotnet build src/LiveInteract.Silksong/LiveInteract.Silksong.csproj -c Release -p:QaBuild=false -t:Rebuild --nologo
if($LASTEXITCODE -ne 0){throw 'Production Mod build failed'}
dotnet publish src/LiveInteract.Desktop/LiveInteract.Desktop.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $out --nologo
if($LASTEXITCODE -ne 0){throw 'Slim publish failed'}
Move-Item -LiteralPath (Join-Path $out 'LiveInteract.exe') -Destination (Join-Path $out '直播互动工具箱.exe')
$loader=Join-Path $out 'payload\loader'
$mod=Join-Path $out 'payload\mod'
New-Item -ItemType Directory -Path $mod,(Join-Path $loader 'BepInEx\core'),(Join-Path $loader 'BepInEx\config') -Force | Out-Null
Copy-Item -LiteralPath 'src\LiveInteract.Silksong\bin\Release\netstandard2.1\LiveInteract.Silksong.dll','src\LiveInteract.Contracts\bin\Release\netstandard2.1\LiveInteract.Contracts.dll' -Destination $mod
$source=Join-Path $root 'artifacts\loader-package\BepInExPack'
Copy-Item -LiteralPath (Join-Path $source 'winhttp.dll'),(Join-Path $source 'doorstop_config.ini') -Destination $loader
Get-ChildItem -LiteralPath (Join-Path $source 'BepInEx\core') -File -Filter '*.dll' | Copy-Item -Destination (Join-Path $loader 'BepInEx\core')
Copy-Item -LiteralPath (Join-Path $source 'BepInEx\config\BepInEx.cfg') -Destination (Join-Path $loader 'BepInEx\config')
Copy-Item -LiteralPath 'docs\0.7使用说明.md' -Destination (Join-Path $out '使用说明.md')
Copy-Item -LiteralPath 'docs\THIRD_PARTY_NOTICES.md' -Destination $out
$art=Join-Path $out '直播图'
New-Item -ItemType Directory -Path $art -Force | Out-Null
Copy-Item -LiteralPath '直播素材\0.6新版\直播互动说明-透明.png' -Destination $art
$licenses=Join-Path $root 'artifacts\licenses'
Copy-Item -LiteralPath $licenses -Destination (Join-Path $out '第三方许可') -Recurse
Write-Output ('Slim distribution: '+$out)

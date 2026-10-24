# 统一精简/完整包构建；永远保留旧包，不安装到游戏，不执行远端发布。
param(
    [ValidateSet('Slim','Full')][string]$Format='Slim',
    [ValidatePattern('^([a-z0-9]+(-[a-z0-9]+)*)?$')][string]$Channel='preview',
    [switch]$SkipBuild
)
$ErrorActionPreference='Stop'

# 只解析文档清单，不构建或写包；0.9.0缺本版材料时提前停止。
function Get-DistributionDocuments {
    param([string]$Root,[string]$Version)
    $usage=Join-Path $Root ("docs\"+$Version+'使用说明.md')
    $acceptance=Join-Path $Root ("docs\"+$Version+'验收结果.md')
    if($Version -eq '0.9.0'){
        $documents=@(
            [pscustomobject]@{Source=$usage;FileName='使用说明.md'}
            [pscustomobject]@{Source=$acceptance;FileName='0.9.0验收结果.md'}
            [pscustomobject]@{Source=(Join-Path $Root 'docs\0.9.0怪物分类.csv');FileName='0.9.0怪物分类.csv'}
            [pscustomobject]@{Source=(Join-Path $Root 'docs\0.9.0召唤筛选表.csv');FileName='0.9.0召唤筛选表.csv'}
            [pscustomobject]@{Source=(Join-Path $Root 'docs\0.9.0召唤验收.csv');FileName='0.9.0召唤验收.csv'}
            # 最终逐只入池结论：友方/敌方是否开放及原因。
            [pscustomobject]@{Source=(Join-Path $Root 'docs\0.9.0召唤池结论.csv');FileName='0.9.0召唤池结论.csv'}
        )
        $missing=@($documents | Where-Object{-not(Test-Path -LiteralPath $_.Source -PathType Leaf)})
        if($missing.Count -gt 0){throw ('0.9.0发布文档缺失，停止装包，不回退旧版：'+(($missing | ForEach-Object{$_.Source}) -join '；'))}
    }else{
        # 其他版本保留既有说明/结果回退及0.8.2逐只验收表。
        if(-not(Test-Path -LiteralPath $usage)){$usage=Join-Path $Root 'docs\0.8.2使用说明.md'}
        if(-not(Test-Path -LiteralPath $acceptance)){$acceptance=Join-Path $Root 'docs\0.8.2验收结果.md'}
        $documents=@(
            [pscustomobject]@{Source=$usage;FileName='使用说明.md'}
            [pscustomobject]@{Source=$acceptance;FileName=[IO.Path]::GetFileName($acceptance)}
            [pscustomobject]@{Source=(Join-Path $Root 'docs\0.8.2召唤验收.csv');FileName='0.8.2召唤验收.csv'}
        )
    }
    return $documents
}
$root=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root
[xml]$props=Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
$version=[string]$props.Project.PropertyGroup.Version
if($version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$'){throw 'Invalid version in Directory.Build.props'}
$documents=@(Get-DistributionDocuments -Root $root -Version $version)
$label=if($Channel.Length -gt 0){"-$Channel"}else{''}
$variant=if($Format -eq 'Slim'){'-slim'}else{''}
$name="LiveInteract-$version$label$variant-win-x64"
$out=Join-Path $root "dist\$name"
$zip="$out.zip"
if((Test-Path -LiteralPath $out) -or (Test-Path -LiteralPath $zip)){throw 'Preserve existing distribution; choose another channel/version.'}
if(-not $SkipBuild){
    dotnet test tests/LiveInteract.Tests/LiveInteract.Tests.csproj -c Release --nologo -v q
    if($LASTEXITCODE -ne 0){throw 'Tests failed; package not created'}
}
# 即使 SkipBuild 也重建正式 Mod，绝不复制上次 QA 编译产物。
dotnet build src/LiveInteract.Silksong/LiveInteract.Silksong.csproj -c Release -p:QaBuild=false -t:Rebuild --nologo -v q
if($LASTEXITCODE -ne 0){throw 'Production Mod build failed'}
$selfContained=if($Format -eq 'Full'){'true'}else{'false'}
dotnet publish src/LiveInteract.Desktop/LiveInteract.Desktop.csproj -c Release -r win-x64 --self-contained $selfContained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $out --nologo -v q
if($LASTEXITCODE -ne 0){throw 'Desktop publish failed'}
& (Join-Path $PSScriptRoot 'build-bootstrap.ps1') -ManagedExe (Join-Path $out 'LiveInteract.exe') -OutputExe (Join-Path $out '直播互动工具箱.exe') -SelfContained:($Format -eq 'Full')
# 主程序已经作为资源嵌入，不再把重复EXE放入用户包。
Remove-Item -LiteralPath (Join-Path $out 'LiveInteract.exe')
# SDK XML 是开发文档，不是运行依赖。
Get-ChildItem -LiteralPath $out -File -Filter '*.xml' | ForEach-Object { Remove-Item -LiteralPath $_.FullName }
[ordered]@{Version=$version;SelfContained=($Format -eq 'Full');Channel=$Channel;Bootstrap=$true} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'distribution.json') -Encoding UTF8
$loader=Join-Path $out 'payload\loader'
$mod=Join-Path $out 'payload\mod'
New-Item -ItemType Directory -Path $mod,(Join-Path $loader 'BepInEx\core'),(Join-Path $loader 'BepInEx\config') -Force | Out-Null
foreach($name in 'LiveInteract.Silksong.dll','LiveInteract.Contracts.dll','AssetHelperLib.dll','AssetsTools.NET.dll'){
    $source=Join-Path $root "src\LiveInteract.Silksong\bin\Release\netstandard2.1\$name"
    if(-not(Test-Path -LiteralPath $source)){throw "Missing Mod dependency: $name"}
    Copy-Item -LiteralPath $source -Destination $mod
}
$source=Join-Path $root 'artifacts\loader-package\BepInExPack'
Copy-Item -LiteralPath (Join-Path $source 'winhttp.dll'),(Join-Path $source 'doorstop_config.ini') -Destination $loader
Get-ChildItem -LiteralPath (Join-Path $source 'BepInEx\core') -File -Filter '*.dll' | Copy-Item -Destination (Join-Path $loader 'BepInEx\core')
Copy-Item -LiteralPath (Join-Path $source 'BepInEx\config\BepInEx.cfg') -Destination (Join-Path $loader 'BepInEx\config')
if($Format -eq 'Full'){
    $browser=Get-ChildItem -LiteralPath (Join-Path $root 'artifacts\webview2-fixed') -Recurse -File -Filter 'msedgewebview2.exe' | Select-Object -First 1
    if($null -eq $browser){throw 'Missing fixed WebView2 runtime'}
    $signature=Get-AuthenticodeSignature -LiteralPath $browser.FullName
    if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation'){throw 'Invalid WebView2 publisher signature'}
    Copy-Item -LiteralPath $browser.Directory.FullName -Destination (Join-Path $out 'WebView2Runtime') -Recurse
}
foreach($document in $documents){
    Copy-Item -LiteralPath $document.Source -Destination (Join-Path $out $document.FileName)
}
Copy-Item -LiteralPath 'docs\THIRD_PARTY_NOTICES.md','docs\热更使用说明.md' -Destination $out
$art=Join-Path $out '直播图'
New-Item -ItemType Directory -Path $art -Force | Out-Null
dotnet run --project tools/UiSmoke/UiSmoke.csproj -c Release -- $art '--poster' (Join-Path $root 'profiles\丝之歌-弹幕助力.json')
if($LASTEXITCODE -ne 0){throw 'Current-profile poster render failed'}
Copy-Item -LiteralPath (Join-Path $root 'artifacts\licenses') -Destination (Join-Path $out '第三方许可') -Recurse
if(Get-ChildItem -LiteralPath $out -Recurse -Filter 'Assembly-CSharp.dll'){throw 'Game assemblies must not be distributed'}
if(Get-ChildItem -LiteralPath $out -Recurse -Filter 'liveinteract-qa.json'){throw 'QA configuration must not be distributed'}
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($out,$zip,[IO.Compression.CompressionLevel]::Optimal,$false)
Write-Output "Distribution: $out"
Write-Output "ZIP: $zip"

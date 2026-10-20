$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root
# 下载与解压仅发生在本项目产物目录，不安装到游戏或系统。
$downloads = Join-Path $root 'artifacts\downloads'
New-Item -ItemType Directory -Path $downloads -Force | Out-Null
$loaderZip = Join-Path $downloads 'BepInExPack_Silksong-1.0.3.zip'
if (-not (Test-Path -LiteralPath $loaderZip)) { Invoke-WebRequest -UseBasicParsing -Uri 'https://thunderstore.io/package/download/silksong_modding/BepInExPack_Silksong/1.0.3/' -OutFile $loaderZip }
$loader = Join-Path $root 'artifacts\loader-package'
if (-not (Test-Path -LiteralPath $loader)) { Expand-Archive -LiteralPath $loaderZip -DestinationPath $loader }
# 使用本机游戏引用编译，不将游戏程序集分发。
dotnet build LiveInteract.sln -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
dotnet test tests/LiveInteract.Tests/LiveInteract.Tests.csproj -c Release --no-build --nologo
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
Write-Output 'Build and tests passed. Use scripts/package.ps1 to create the portable distribution.'

# 系统自带CLR的轻量引导入口；内嵌.NET10单文件主程序，不要求机器先装.NET10。
param([Parameter(Mandatory=$true)][string]$ManagedExe,[Parameter(Mandatory=$true)][string]$OutputExe,[bool]$SelfContained=$false)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$ManagedExe=[IO.Path]::GetFullPath($ManagedExe);$OutputExe=[IO.Path]::GetFullPath($OutputExe)
if(-not(Test-Path -LiteralPath $ManagedExe)){throw 'Managed payload missing'}
if(Test-Path -LiteralPath $OutputExe){throw 'Preserve previous bootstrap output'}
[xml]$props=Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
$version=[string]$props.Project.PropertyGroup.Version
$id=$version+'-'+(Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmssfff')
$work=Join-Path $root ('artifacts\bootstrap\'+$id)
New-Item -ItemType Directory -Path $work -Force | Out-Null
$flag=if($SelfContained){'true'}else{'false'}
$identity='using System.Reflection;[assembly:AssemblyVersion("'+$version+'.0")][assembly:AssemblyFileVersion("'+$version+'.0")][assembly:AssemblyTitle("花花直播互动")][assembly:AssemblyProduct("花花直播互动")][assembly:AssemblyCompany("LiveInteract")]
namespace LiveInteract.Bootstrap{internal static class BootstrapIdentity{public const string Version="'+$version+'";public const string BuildId="'+$id+'";public const bool SelfContained='+$flag+';}}'
[IO.File]::WriteAllText((Join-Path $work 'Identity.cs'),$identity,[Text.UTF8Encoding]::new($false))
$csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$source=Join-Path $root 'src\LiveInteract.Bootstrap'
$arguments=@('/nologo','/target:winexe','/platform:x64','/optimize+','/debug-','/codepage:65001',('/out:'+$OutputExe),('/win32icon:'+(Join-Path $root 'src\LiveInteract.Desktop\Assets\LiveInteract.ico')),('/win32manifest:'+(Join-Path $root 'src\LiveInteract.Desktop\app.manifest')),
'/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Runtime.Serialization.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll',('/resource:'+$ManagedExe+',LiveInteract.ManagedPayload'),('/resource:'+(Join-Path $source 'runtime-manifest.json')+',LiveInteract.RuntimeCatalog'),(Join-Path $work 'Identity.cs'),(Join-Path $source 'DependencyEngine.cs'),(Join-Path $source 'Program.cs'))
& $csc @arguments
if($LASTEXITCODE -ne 0){throw 'Bootstrap build failed'}
Write-Output ('Bootstrap: '+$OutputExe)

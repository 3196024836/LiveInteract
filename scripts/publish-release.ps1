# 默认只准备本地包/清单预览。只有显式 -Publish 才执行外部发布，且要求源码已提交。
param([string]$Notes='', [switch]$Publish, [string]$FullUpdateZip='')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root
$repo='3196024836/LiveInteract'
[xml]$props=Get-Content -LiteralPath 'Directory.Build.props' -Raw
$version=[string]$props.Project.PropertyGroup.Version
if($version -notmatch '^\d+\.\d+\.\d+$'){throw 'Invalid release version'}
$tag="v$version"
$zipName="LiveInteract-$version-slim-win-x64.zip"
$zip=Join-Path $root "dist\$zipName"
if($Publish){
    $dirty=git status --porcelain
    if($LASTEXITCODE -ne 0){throw 'Cannot inspect git worktree'}
    if($dirty){throw 'Commit/review source changes before publishing. No files staged or uploaded.'}
    # 客户端只读 main 上的清单；从其他分支发布会让用户永远收不到更新。
    $branch=git rev-parse --abbrev-ref HEAD
    if($branch -ne 'main'){throw 'Publish from main only; clients read update/latest.json on main.'}
    $remote=git remote get-url origin
    if($LASTEXITCODE -ne 0 -or $remote -notmatch 'github.com[:/]3196024836/LiveInteract(\.git)?$'){throw 'Origin does not match release repository'}
}
if(-not(Test-Path -LiteralPath $zip)){
    & (Join-Path $PSScriptRoot 'package-slim.ps1') -Channel ''
    if(-not(Test-Path -LiteralPath $zip)){throw 'Package was not created'}
}
if($Notes.Length -eq 0){
    $lines=[IO.File]::ReadAllLines((Join-Path $root 'CHANGELOG.md'))
    $body=New-Object System.Collections.Generic.List[string]
    $inside=$false
    foreach($line in $lines){
        if($line -match "^## $([regex]::Escape($version))(\s|—|$)"){$inside=$true;continue}
        if($inside -and $line -match '^## '){break}
        if($inside -and $line.Trim().Length -gt 0){$body.Add($line)}
    }
    $Notes=($body -join "`n")
    if($Notes.Length -eq 0){$Notes="LiveInteract $version"}
}
# SHA-256 是客户端传输完整性契约；不以包名或文件存在代替验证。
$manifest=[ordered]@{
    version=$version;notes=$Notes.Trim();
    url="https://github.com/$repo/releases/download/$tag/$zipName";
    sha256=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant();
    size=(Get-Item -LiteralPath $zip).Length
}
$assets=@($zip)
if($FullUpdateZip.Length -gt 0){
    $FullUpdateZip=(Resolve-Path -LiteralPath $FullUpdateZip).Path
    if((Get-Item -LiteralPath $FullUpdateZip).Length -gt 128MB){throw 'Full update payload must omit the unchanged WebView2 runtime and fit within 128 MiB'}
    $fullName=[IO.Path]::GetFileName($FullUpdateZip)
    $manifest.fullPackage=[ordered]@{url="https://github.com/$repo/releases/download/$tag/$fullName";sha256=(Get-FileHash -LiteralPath $FullUpdateZip -Algorithm SHA256).Hash.ToLowerInvariant();size=(Get-Item -LiteralPath $FullUpdateZip).Length}
    $assets+= $FullUpdateZip
}
$preview=Join-Path $root "artifacts\release-$version-preview.json"
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $preview -Encoding UTF8
if(-not $Publish){Write-Output "Local package: $zip";Write-Output "Manifest preview: $preview";Write-Output 'Not published. Review source and use -Publish explicitly when authorized.';return}
$head=git rev-parse HEAD
if($LASTEXITCODE -ne 0){throw 'Cannot read source commit'}
$notesFile=Join-Path $env:TEMP ('liveinteract-notes-'+[guid]::NewGuid().ToString('N')+'.md')
try{
    Set-Content -LiteralPath $notesFile -Value $Notes -Encoding UTF8
    # 版本资产不可覆盖：已有 tag/release 必须人工核对，不能 clobber 正被客户端下载的 ZIP。
    gh release create $tag @assets --repo $repo --target $head --title "LiveInteract $version" --notes-file $notesFile
    if($LASTEXITCODE -ne 0){throw 'Release creation/upload failed. Inspect the existing release before retrying; manifest was not advanced.'}
}finally{Remove-Item -LiteralPath $notesFile -Force -ErrorAction SilentlyContinue}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $root 'update\latest.json') -Encoding UTF8
# 只提交清单；不再 git add -A 把用户的其他工作一并带入发布。
git add -- update/latest.json
if($LASTEXITCODE -ne 0){throw 'Cannot stage manifest'}
git commit -m "Publish update manifest $version" -- update/latest.json
if($LASTEXITCODE -ne 0){throw 'Cannot commit manifest; release exists, inspect before retrying'}
git push origin HEAD
if($LASTEXITCODE -ne 0){throw 'Manifest push failed; release exists. Retry the push, do not recreate or overwrite release assets.'}
Write-Output "Published $tag"

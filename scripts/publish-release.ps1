param([string]$Notes = "")
# 发布一个版本：构建精简包 → 创建 GitHub Release 并上传 → 写 update/latest.json → 提交并推送。
# 精简包作为更新载荷；完整包体积大，按需单独上传。
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root
# 宿主进程可能带有不完整的 GIT_CONFIG_* 覆盖，git 会直接报错，这里清掉。
Remove-Item Env:GIT_CONFIG_COUNT -ErrorAction SilentlyContinue
Remove-Item Env:GIT_CONFIG_KEY_0 -ErrorAction SilentlyContinue
$repo = '3196024836/LiveInteract'

$version = ([regex]'<Version>([^<]+)</Version>').Match([System.IO.File]::ReadAllText((Join-Path $root 'Directory.Build.props'))).Groups[1].Value
if ($version.Length -eq 0) { throw '未能从 Directory.Build.props 读取版本号' }
$tag = "v$version"
$zipName = "LiveInteract-$version-slim-win-x64.zip"
$zip = Join-Path $root ("dist\$zipName")
Write-Output "发布版本：$version"

if (-not (Test-Path $zip)) {
    $folder = Join-Path $root "dist\LiveInteract-$version-slim-win-x64"
    if (-not (Test-Path $folder)) {
        Write-Output '精简包不存在，先构建…'
        & (Join-Path $PSScriptRoot 'package-slim.ps1')
    }
    if (-not (Test-Path $folder)) { throw "构建后仍未找到 $folder" }
    Write-Output '打包为 zip…'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path $zip) { Remove-Item $zip -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($folder, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true)
}

# 更新说明取 CHANGELOG 里最新一节。
if ($Notes.Length -eq 0) {
    $lines = [System.IO.File]::ReadAllLines((Join-Path $root 'CHANGELOG.md'))
    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match "^## $([regex]::Escape($version))(\s|—|$)") { $start = $i; break } }
    if ($start -ge 0) {
        $body = New-Object System.Collections.Generic.List[string]
        for ($i = $start + 1; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^## ') { break }; if ($lines[$i].Trim().Length -gt 0) { $body.Add($lines[$i]) } }
        $Notes = ($body -join "`n")
    }
    if ($Notes.Length -eq 0) { $Notes = "LiveInteract $version" }
}

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item $zip).Length
Write-Output ("包大小 {0:N1} MB，SHA-256 {1}" -f ($size / 1MB), $hash)

# GitHub Release（存在则只补传资产）。
$probe = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
gh release view $tag --repo $repo 2>$null | Out-Null
$releaseExists = $LASTEXITCODE -eq 0
$ErrorActionPreference = $probe
if ($releaseExists) {
    Write-Output "Release $tag 已存在，替换资产…"
    gh release upload $tag $zip --repo $repo --clobber | Out-Null
} else {
    $notesFile = Join-Path $env:TEMP "liveinteract-notes-$version.md"
    Set-Content -LiteralPath $notesFile -Value $Notes -Encoding UTF8
    gh release create $tag $zip --repo $repo --title "LiveInteract $version" --notes-file $notesFile | Out-Null
    Remove-Item $notesFile -Force -ErrorAction SilentlyContinue
}
if ($LASTEXITCODE -ne 0) { throw 'GitHub Release 创建或上传失败' }

# 更新清单：客户端只读这一个文件。
$manifest = [ordered]@{
    version = $version
    notes   = ($Notes -replace "\s*`n\s*", ' ').Trim()
    url     = "https://github.com/$repo/releases/download/$tag/$zipName"
    sha256  = $hash
    size    = $size
}
$updateDir = Join-Path $root 'update'
New-Item -ItemType Directory -Path $updateDir -Force | Out-Null
$manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $updateDir 'latest.json') -Encoding UTF8
Write-Output "已写出 update/latest.json"

# 提交并推送（源码 + 清单）。git 的进度输出走 stderr，这里按退出码判断。
$gitSetting = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
git add -A 2>$null
if ($LASTEXITCODE -ne 0) { throw 'git add 失败' }
git -c user.name="hua" -c user.email="3196024836@qq.com" commit -m "Release $version" --allow-empty 2>$null | Out-Null
git push origin HEAD 2>&1 | Select-Object -Last 3
$pushExit = $LASTEXITCODE
$ErrorActionPreference = $gitSetting
if ($pushExit -ne 0) { throw '推送失败，请检查凭据' }
Write-Output "已推送 $tag"

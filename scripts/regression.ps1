param([string]$Output = "", [string]$Package = "")
# 一键回归：单元测试 + 界面/队列/负载/浏览器冒烟 + 打包程序自检。
# 不启动游戏、不连接直播间、不写真实用户配置；所有产物写入 _work\regression-*。
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root
if ($Output.Length -eq 0) { $Output = Join-Path $root ('_work\regression-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
if ((Test-Path -LiteralPath $Output) -and (Get-ChildItem -LiteralPath $Output -Force | Select-Object -First 1)) { throw 'Preserve previous regression evidence; choose a new output directory.' }
New-Item -ItemType Directory -Path $Output -Force | Out-Null
[xml]$buildProps=Get-Content -LiteralPath 'Directory.Build.props' -Raw
$version=[string]$buildProps.Project.PropertyGroup.Version
if ($Package.Length -eq 0) { $Package=Join-Path $root ("dist\LiveInteract-$version-preview-slim-win-x64\直播互动工具箱.exe") }
$Package=[IO.Path]::GetFullPath($Package)
$results = [ordered]@{}
function Record([string]$name, [bool]$ok, [string]$detail = "") { $results[$name] = [ordered]@{ Passed = $ok; Detail = $detail }; Write-Output (("{0,-34} {1}" -f $name, $(if ($ok) { "PASS" } else { "FAIL $detail" }))) }

Write-Output '== 1. 编译整个解决方案 =='
dotnet build LiveInteract.sln -c Release --nologo -v q 2>&1 | Out-Null
Record 'BuildSolution' ($LASTEXITCODE -eq 0)

Write-Output '== 2. 单元与集成测试 =='
$trx = Join-Path $Output 'tests.trx'
dotnet test tests\LiveInteract.Tests\LiveInteract.Tests.csproj -c Release --nologo --logger "trx;LogFileName=$trx" -v q 2>&1 | Out-Null
$testExit = $LASTEXITCODE
$passed = $failed = 0
if (Test-Path $trx) { [xml]$doc = Get-Content $trx; $passed = ($doc.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq 'Passed' }).Count; $failed = ($doc.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq 'Failed' }).Count }
Record 'UnitTests' ($testExit -eq 0) "通过 $passed / 失败 $failed"

Write-Output '== 3. 界面与队列冒烟 =='
foreach ($tool in 'UiSmoke', 'QueueUiSmoke') {
    $folder = Join-Path $Output ($tool.ToLower())
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    dotnet build "tools\$tool" -c Release --nologo -v q 2>&1 | Out-Null
    $exe = Get-ChildItem "tools\$tool\bin\Release" -Recurse -Filter "$tool.exe" | Select-Object -First 1
    if ($exe -eq $null) { Record $tool $false '未生成可执行文件'; continue }
    $process = Start-Process -FilePath $exe.FullName -ArgumentList $folder -WindowStyle Hidden -PassThru -Wait
    $reportName = if ($tool -eq 'UiSmoke') { 'ui-interaction-smoke.json' } else { 'queue-ui-smoke.json' }
    $report = Join-Path $folder $reportName
    $ok = $process.ExitCode -eq 0 -and (Test-Path $report) -and ((Get-Content $report -Raw) -match '"Passed":\s*true')
    Record $tool $ok "exit=$($process.ExitCode)"
}

Write-Output '== 4. 负载冒烟（20 秒合成事件）=='
$load = Join-Path $Output 'load-test.json'
dotnet run --project tools\LoadTest -c Release -- 20 $load 2>&1 | Out-Null
Record 'LoadTest' ($LASTEXITCODE -eq 0 -and (Test-Path $load)) ''

Write-Output '== 4b. 热更替换冒烟 =='
$updateFolder = Join-Path $Output 'update-smoke'
if (Test-Path -LiteralPath $Package) {
    $oldProbe=Join-Path $Output 'probe-old'
    $newProbe=Join-Path $Output 'probe-new'
    dotnet publish tools\UpdateProbe -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -p:Version=0.8.0 -p:AssemblyVersion=0.8.0.0 -p:FileVersion=0.8.0.0 -o $oldProbe -v q 2>&1 | Out-Null
    $oldBuild=$LASTEXITCODE
    # 当前探针作为0.8.1更新目标；目录和版本都显式指定，避免同目录混用构建。
    dotnet publish tools\UpdateProbe -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -p:Version=0.8.1 -p:AssemblyVersion=0.8.1.0 -p:FileVersion=0.8.1.0 -o $newProbe -v q 2>&1 | Out-Null
    if ($oldBuild -ne 0 -or $LASTEXITCODE -ne 0) { Record 'UpdateProbes' $false '探针构建失败' }
    else { dotnet run --project tools\UpdateSmoke -c Release -- $updateFolder $Package (Join-Path $oldProbe 'UpdateProbe.exe') (Join-Path $newProbe 'UpdateProbe.exe') 2>&1 | Out-Null }
} else { Record 'UpdateProcess' $false '缺少当前发行EXE，无法验证辅助进程' }
$updateReport = Join-Path $updateFolder 'update-smoke.json'
Record 'UpdateSmoke' ($LASTEXITCODE -eq 0 -and (Test-Path $updateReport) -and ((Get-Content $updateReport -Raw) -match '"Passed":\s*true')) ''

Write-Output '== 5. 内嵌浏览器收包冒烟 =='
$runtime = Get-ChildItem 'artifacts\webview2-fixed' -Directory -ErrorAction SilentlyContinue | Select-Object -First 1
if ($runtime -eq $null) { Record 'BrowserSmoke' $false '缺少固定 WebView2' }
else {
    $folder = Join-Path $Output 'browser'
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    dotnet build tools\BrowserSmoke -c Release --nologo -v q 2>&1 | Out-Null
    $exe = Get-ChildItem 'tools\BrowserSmoke\bin\Release' -Recurse -Filter 'BrowserSmoke.exe' | Select-Object -First 1
    $process = Start-Process -FilePath $exe.FullName -ArgumentList $folder, $runtime.FullName -WindowStyle Hidden -PassThru -Wait
    $report = Join-Path $folder 'browser-smoke.json'
    $ok = $process.ExitCode -eq 0 -and (Test-Path $report) -and (Get-Content $report -Raw) -match '"Passed":\s*true'
    Record 'BrowserSmoke' $ok "exit=$($process.ExitCode)"
}

Write-Output '== 6. 打包程序自检与出图 =='
$exe = $Package
if (-not (Test-Path $exe)) { Record 'PackagedApp' $false '未找到打包程序' }
else {
    $doctor = Join-Path $Output 'doctor.json'
    Start-Process -FilePath $exe -ArgumentList '--doctor', $doctor -WindowStyle Hidden -Wait
    $doctorOk = (Test-Path $doctor) -and (Get-Content $doctor -Raw) -match '"GameFound":\s*true' -and (Get-Content $doctor -Raw) -match '"PayloadFound":\s*true'
    Record 'Doctor' $doctorOk ''

    $poster = Join-Path $Output 'poster.png'
    Start-Process -FilePath $exe -ArgumentList '--poster', $poster, (Join-Path $root 'profiles\丝之歌-弹幕助力.json') -WindowStyle Hidden -Wait
    $posterOk = $false
    if (Test-Path $poster) {
        $bytes = [System.IO.File]::ReadAllBytes($poster)
        $png = $bytes.Length -gt 1024 -and $bytes[0] -eq 0x89 -and $bytes[1] -eq 0x50
        Add-Type -AssemblyName System.Drawing
        $image = [System.Drawing.Image]::FromFile($poster)
        $posterOk = $png -and $image.Width -ge 400 -and $image.Height -ge 800 -and $image.Height -gt $image.Width # 细长条：高度必须大于宽度。
        $image.Dispose()
    }
    Record 'PosterGenerate' $posterOk ''

    $preview = Join-Path $Output 'preview'
    Start-Process -FilePath $exe -ArgumentList '--render-preview', $preview -WindowStyle Hidden -Wait
    $smoke = Join-Path $preview 'desktop-smoke.json'
    $previewOk = (Test-Path $smoke) -and ((Get-Content $smoke -Raw) -match '"PagesRendered":\s*6') -and ((Get-Content $smoke -Raw) -match '"NoGameFilesChanged":\s*true')
    Record 'UiPreview' $previewOk ''
}

$failedCount = @($results.Keys | Where-Object { -not $results[$_].Passed }).Count
$summary = [ordered]@{ Passed = ($failedCount -eq 0); At = (Get-Date).ToString('s'); Checks = $results }
$summaryFile = Join-Path $Output 'regression.json'
$summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $summaryFile -Encoding UTF8
Write-Output ''
Write-Output ("回归结果：{0}，失败 {1} 项；明细 {2}" -f $(if ($failedCount -eq 0) { "全部通过" } else { "存在失败" }), $failedCount, $summaryFile)
exit $(if ($failedCount -eq 0) { 0 } else { 1 })

# publish-keep-data.ps1 —— 构建便携版应用（保留数据版）
#
# 与 publish.ps1 的唯一区别：发布**不清空**运行期数据。
#   config.json / run.log / WebView2Profile / temp 会在发布前搬到
#   artifacts\.publish-keep 暂存，发布成功后原样搬回；即使发布失败，
#   也会尽力把数据搬回 portable，不让数据留在暂存目录里成孤岛。
# 直接双击运行即可，无任何参数；结束时等待回车（窗口不自动关闭）。
# 删除仍是永久删除（不进回收站），被删的只有过期的构建产物。

$ErrorActionPreference = "Continue"
$project = Join-Path $PSScriptRoot "src/DshDesktop.App/DshDesktop.App.csproj"
$outDir  = Join-Path $PSScriptRoot "artifacts/portable"
$keepDir = Join-Path $PSScriptRoot "artifacts/.publish-keep"
$exeName = "DSH-Desktop-Webview.exe"
# 应用首次运行在 exe 同目录生成的运行期数据（与 publish.ps1 结尾提示同源）
$dataItems = @("config.json", "run.log", "WebView2Profile", "temp")

# 结束前停一下，防止双击运行时窗口一闪而过自动关闭。
function Exit-Script([int]$code) {
    Write-Host ""
    try { Read-Host "按回车键退出" | Out-Null } catch { }
    exit $code
}

# 尽力把暂存目录里的数据搬回 portable。返回 $true = 全部搬回（或本就无事可做）。
function Restore-KeepData {
    if (-not (Test-Path $keepDir)) { return $true }
    if (-not (Test-Path $outDir)) {
        New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    }
    $ok = $true
    foreach ($name in $dataItems) {
        $src = Join-Path $keepDir $name
        if (Test-Path $src) {
            try {
                Move-Item -LiteralPath $src -Destination (Join-Path $outDir $name) -Force
            }
            catch {
                Write-Host ("    数据搬回失败: {0} -> {1}" -f $name, $_.Exception.Message) -ForegroundColor Red
                $ok = $false
            }
        }
    }
    if ($ok) {
        try { [System.IO.Directory]::Delete($keepDir, $true) } catch { }
    }
    else {
        Write-Host ("    残留数据仍在暂存目录 {0} ，请手动搬回 portable。" -f $keepDir) -ForegroundColor Yellow
    }
    return $ok
}

# 失败出口：先把数据搬回 portable，再退出。
function Fail([int]$code, [string[]]$lines) {
    Write-Host ""
    foreach ($m in $lines) { Write-Host $m -ForegroundColor Red }
    Write-Host ""
    Write-Host "==> 正在把运行期数据从暂存目录搬回 portable ..." -ForegroundColor Yellow
    Restore-KeepData | Out-Null
    Exit-Script $code
}

try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    $OutputEncoding = [System.Text.Encoding]::UTF8
} catch { }

Write-Host ""
Write-Host "==> DSH-Desktop-Webview 便携版发布（保留数据版）" -ForegroundColor Cyan
Write-Host "    模式: 依赖框架（约 26 MB，目标机需装 .NET 8 桌面运行时）"
Write-Host ("    保留: {0}" -f ($dataItems -join " / "))

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host ""
    Write-Host "未找到 dotnet SDK，请先安装 .NET 8 SDK。" -ForegroundColor Red
    Exit-Script 1
}

# 运行中的实例会锁住 WebView2Profile / exe。数据版必须在搬数据**之前**就拦下，
# 否则搬一半失败会把数据撕在两处。
$running = @(Get-Process -Name 'DSH-Desktop-Webview' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Host ""
    Write-Host ("检测到 {0} 个运行中的实例 (pid {1})，请先关闭它们再发布。" -f `
        $running.Count, (($running | ForEach-Object { $_.Id }) -join ', ')) -ForegroundColor Red
    Exit-Script 1
}

# 上次运行若有未搬回的残留，先让用户处理 —— 绝不静默覆盖别人的数据。
if ((Test-Path $keepDir) -and @((Get-ChildItem $keepDir -Force -ErrorAction SilentlyContinue)).Count -gt 0) {
    Write-Host ""
    Write-Host ("暂存目录 {0} 非空（上次发布可能中途失败）。" -f $keepDir) -ForegroundColor Yellow
    Write-Host "请确认其中的数据已不需要，手动删除该目录后重试；本脚本不会覆盖它。" -ForegroundColor Yellow
    Exit-Script 1
}
if (Test-Path $keepDir) { [System.IO.Directory]::Delete($keepDir) }  # 空目录直接删

# ---- 第 1 步：把运行期数据搬进暂存目录 ----
$hasPortable = Test-Path $outDir
$moved = @()
if ($hasPortable) {
    New-Item -ItemType Directory -Path $keepDir -Force | Out-Null
    foreach ($name in $dataItems) {
        $src = Join-Path $outDir $name
        if (Test-Path $src) {
            try {
                Move-Item -LiteralPath $src -Destination (Join-Path $keepDir $name) -Force
                $moved += $name
            }
            catch {
                Write-Host ""
                Write-Host ("搬出 {0} 失败: {1}" -f $name, $_.Exception.Message) -ForegroundColor Red
                Write-Host "多半被资源管理器窗口或其他进程占用。已搬出的项目会先搬回。" -ForegroundColor Red
                Restore-KeepData | Out-Null
                Exit-Script 1
            }
        }
    }
}
if ($moved.Count -gt 0) {
    Write-Host ("    已暂存 {0} 个数据项 -> artifacts\.publish-keep" -f $moved.Count)
}

# ---- 第 2 步：清空旧构建产物（数据已不在里面，删的只是过期 dll/exe）----
if ($hasPortable) {
    $wiped = $false
    try {
        [System.IO.Directory]::Delete($outDir, $true)
        $wiped = $true
    }
    catch {
        # 目录被占用（资源管理器窗口）时到这里，交给兜底路径后再统一判定
    }
    if (-not $wiped) {
        & cmd.exe /c rmdir /s /q "$outDir" 2>$null
    }
    if (Test-Path $outDir) {
        Fail 1 @(
            ("清空 {0} 失败: 目录仍存在。" -f $outDir),
            "多半是资源管理器窗口占用了该目录，关掉后重试。")
    }
}
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

# ---- 第 3 步：clean + publish（与 publish.ps1 相同的流程与校验）----
$started = Get-Date

$cleanOut = & dotnet clean "$project" -c Release --nologo 2>&1 | Out-String
$cleanCode = $LASTEXITCODE
if ($cleanOut.Trim().Length -gt 0) { Write-Host $cleanOut }

Write-Host ""
Write-Host "==> dotnet publish -> artifacts/portable" -ForegroundColor Cyan
$publishOut = & dotnet publish "$project" -c Release -r win-x64 --self-contained false -o "$outDir" --nologo 2>&1 | Out-String
$code = $LASTEXITCODE
Write-Host $publishOut
Write-Host ("    dotnet clean   退出码 = {0}" -f $cleanCode)
Write-Host ("    dotnet publish 退出码 = {0}" -f $code)

# ---- 校验 1：publish 退出码 ----
if ($code -ne 0) {
    Fail 1 @("发布失败（dotnet publish 退出码 $code）。")
}

# ---- 校验 2：exe 必须存在 ----
$exe = Join-Path $outDir $exeName
if (-not (Test-Path $exe)) {
    Fail 1 @("退出码为 0 但 $exeName 不存在 —— 不视为成功。")
}

$item = Get-Item $exe
Write-Host ("    {0} = {1:N0} 字节，写入时间 {2}" -f $exeName, $item.Length, $item.LastWriteTime)

# ---- 校验 3：exe 必须是本次运行写出的（新鲜度，防“锁文件留下旧 exe”假成功）----
if ($item.LastWriteTime -lt $started) {
    Fail 1 @(
        "过期产物: exe 不是本次发布写出的 —— 视为失败。",
        "运行中的实例锁住了 exe；请关闭后重试。")
}

# ---- 校验 4：WinRT 投影程序集必须在位（合成宿主缺它直接初始化失败）----
$sdkNet = @(Get-ChildItem -Path $outDir -Recurse -Filter "Microsoft.Windows.SDK.NET.dll" -ErrorAction SilentlyContinue)
if ($sdkNet.Count -eq 0) {
    Fail 1 @(
        "输出中缺少 Microsoft.Windows.SDK.NET.dll。",
        "合成（composition）宿主会初始化失败并回退到 hwnd。请检查 TFM 是否仍带 Windows SDK 版本。")
}

# ---- 第 4 步：把数据搬回 portable ----
Write-Host ""
Write-Host "==> 把运行期数据搬回 portable ..." -ForegroundColor Cyan
if (-not (Restore-KeepData)) {
    Write-Host ""
    Write-Host "发布本身成功，但有数据未能搬回（见上方红字），请按提示手动处理。" -ForegroundColor Yellow
    Exit-Script 1
}

$total = (Get-ChildItem -Path $outDir -Recurse -File | Measure-Object -Property Length -Sum).Sum

Write-Host ""
Write-Host "发布成功（数据已保留）: artifacts\portable\$exeName" -ForegroundColor Green
Write-Host ("    目录大小 : {0:N1} MB（{1} 个文件）" -f ($total / 1MB), (Get-ChildItem -Path $outDir -Recurse -File).Count)
if ($moved.Count -gt 0) {
    Write-Host ("    已还原   : {0}" -f ($moved -join " / "))
}
Write-Host ""
Write-Host "config.json / run.log / WebView2Profile / temp 均为发布前的原数据。"
Exit-Script 0

# build.ps1 —— 构建便携版（**保留数据**）
#
# 参数：
#   -SelfContained    自包含（约 180 MB，目标机免装 .NET 8 运行时）
#                     默认 = 依赖框架（约 27 MB，目标机需装 .NET 8 桌面运行时）
#   -Pause            结束前停留等回车（默认不停）
#
# 行为：
#   - **不清空运行数据**：config.json / run.log / WebView2Profile / temp 会先搬到
#     artifacts\.publish-keep 暂存；只删旧构建产物。
#
# 编码要求： UTF-8 with BOM。

param(
    [switch]$SelfContained,
    [switch]$Pause,
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Continue"

$project  = Join-Path $PSScriptRoot "src/DshDesktop.App/DshDesktop.App.csproj"
$outDir   = Join-Path $PSScriptRoot "artifacts/portable"
$keepDir  = Join-Path $PSScriptRoot "artifacts/.publish-keep"
$exeName  = "DSH-Desktop-Webview.exe"
# 应用首次运行在 exe 同目录生成的运行期数据
$dataItems = @("config.json", "run.log", "WebView2Profile", "temp")

# ============================ 停留判定 ============================
# 只有显式 -Pause 才停
function Exit-DshBuild([int]$Code) {
    Write-Host ""

    if ($Pause) {
        Write-Host ""
        Write-Host "按回车键退出…" -ForegroundColor DarkGray
        try { Read-Host | Out-Null } catch { }
    }

    exit $Code
}

# ============================ 运行中实例（按路径判定） ============================
# 不能只按进程名判断：本机可能同时装着另一份发布产物（例如 D:\Program Files\...），
# 进程名相同、也可能正在运行，但与我们要覆盖的 artifacts\portable 毫无关系。
# 只有可执行文件路径落在目标目录内的实例才会锁住输出 exe / 数据目录。
function Get-DshRunningInstances {
    $prefix = $null
    try { $prefix = [System.IO.Path]::GetFullPath($outDir).TrimEnd('\') + '\' } catch { }

    $list = @()
    $usedCim = $false
    try {
        foreach ($p in @(Get-CimInstance Win32_Process -Filter "Name='$exeName'" -ErrorAction Stop)) {
            $usedCim = $true
            $list += [pscustomobject]@{ Id = [int]$p.ProcessId; Path = [string]$p.ExecutablePath }
        }
    }
    catch { $usedCim = $false }

    if (-not $usedCim) {
        $bare = [System.IO.Path]::GetFileNameWithoutExtension($exeName)
        foreach ($p in @(Get-Process -Name $bare -ErrorAction SilentlyContinue)) {
            $path = $null
            try { $path = $p.Path } catch { }
            $list += [pscustomobject]@{ Id = [int]$p.Id; Path = [string]$path }
        }
    }

    foreach ($item in $list) {
        $inTarget = $false
        if ($prefix -and -not [string]::IsNullOrWhiteSpace($item.Path)) {
            try {
                $inTarget = ([System.IO.Path]::GetFullPath($item.Path)).StartsWith(
                    $prefix, [System.StringComparison]::OrdinalIgnoreCase)
            }
            catch { $inTarget = $false }
        }
        $item | Add-Member -NotePropertyName InTargetDir -NotePropertyValue $inTarget -Force
    }

    return @($list)
}

# ============================ 数据暂存 / 搬回 ============================
function Restore-KeepData {
    if (-not (Test-Path $keepDir)) { return $true }
    if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

    $ok = $true
    foreach ($name in $dataItems) {
        $src = Join-Path $keepDir $name
        if (Test-Path $src) {
            try { Move-Item -LiteralPath $src -Destination (Join-Path $outDir $name) -Force }
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
        Write-Host ("    残留数据仍在暂存目录 {0}，请手动搬回 portable。" -f $keepDir) -ForegroundColor Yellow
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
    Exit-DshBuild $code
}

# ============================ 主流程 ============================
try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    $OutputEncoding = [System.Text.Encoding]::UTF8
} catch { }

Write-Host ""
Write-Host "==> DSH-Desktop-Webview 便携版构建（保留数据）" -ForegroundColor Cyan
Write-Host ("    模式: {0}" -f $(if ($SelfContained) { "自包含（约 180 MB，目标机免装 .NET）" } else { "依赖框架（约 27 MB，目标机需装 .NET 8 桌面运行时）" }))
Write-Host ("    保留: {0}" -f ($dataItems -join " / "))

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host ""
    Write-Host "未找到 dotnet SDK，请先安装 .NET 8 SDK。" -ForegroundColor Red
    Exit-DshBuild 1
}

# --- 运行中实例：只拦"占用本目录"的那些 ---
$instances = @(Get-DshRunningInstances)
$blocking  = @($instances | Where-Object { $_.InTargetDir })
$elsewhere = @($instances | Where-Object { -not $_.InTargetDir })

if ($elsewhere.Count -gt 0) {
    Write-Host ""
    Write-Host ("    注: 另有 {0} 个同名实例不在本目录，不影响本次构建：" -f $elsewhere.Count) -ForegroundColor DarkGray
    foreach ($e in $elsewhere) {
        $shown = if ([string]::IsNullOrWhiteSpace($e.Path)) { "(路径未知)" } else { $e.Path }
        Write-Host ("        pid {0}  {1}" -f $e.Id, $shown) -ForegroundColor DarkGray
    }
}

if ($blocking.Count -gt 0) {
    Write-Host ""
    Write-Host ("检测到 {0} 个运行中的实例正占用本目录 (pid {1})，请先关闭它们再构建。" -f `
        $blocking.Count, (($blocking | ForEach-Object { $_.Id }) -join ', ')) -ForegroundColor Red
    Exit-DshBuild 1
}

# --- 暂存目录残留：绝不静默覆盖 ---
if ((Test-Path $keepDir) -and @((Get-ChildItem $keepDir -Force -ErrorAction SilentlyContinue)).Count -gt 0) {
    Write-Host ""
    Write-Host ("暂存目录 {0} 非空（上次构建可能中途失败）。" -f $keepDir) -ForegroundColor Yellow
    Write-Host "请确认其中的数据已不需要，手动删除该目录后重试；本脚本不会覆盖它。" -ForegroundColor Yellow
    Exit-DshBuild 1
}
if (Test-Path $keepDir) { [System.IO.Directory]::Delete($keepDir) }

# --- 第 1 步：运行期数据搬进暂存目录 ---
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
                Exit-DshBuild 1
            }
        }
    }
}
if ($moved.Count -gt 0) {
    Write-Host ("    已暂存 {0} 个数据项 -> artifacts\.publish-keep" -f $moved.Count)
}

# --- 第 2 步：清空旧构建产物（数据已不在里面）---
if ($hasPortable) {
    $wiped = $false
    try {
        [System.IO.Directory]::Delete($outDir, $true)
        $wiped = $true
    }
    catch { }

    if (-not $wiped) { & cmd.exe /c rmdir /s /q "$outDir" 2>$null }

    if (Test-Path $outDir) {
        Fail 1 @(
            ("清空 {0} 失败: 目录仍存在。" -f $outDir),
            "多半是资源管理器窗口占用了该目录，关掉后重试。")
    }
}
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

# --- 第 3 步：clean + publish ---
$started = Get-Date

$cleanOut = & dotnet clean "$project" -c "$Configuration" --nologo 2>&1 | Out-String
$cleanCode = $LASTEXITCODE
if ($cleanOut.Trim().Length -gt 0) { Write-Host $cleanOut }

Write-Host ""
Write-Host "==> dotnet publish -> artifacts/portable" -ForegroundColor Cyan
$selfContainedFlag = if ($SelfContained) { "true" } else { "false" }
$publishOut = & dotnet publish "$project" -c "$Configuration" -r "$Runtime" `
    --self-contained $selfContainedFlag -o "$outDir" --nologo 2>&1 | Out-String
$code = $LASTEXITCODE
Write-Host $publishOut
Write-Host ("    dotnet clean   退出码 = {0}" -f $cleanCode)
Write-Host ("    dotnet publish 退出码 = {0}" -f $code)

# --- 校验 1：publish 退出码 ---
if ($code -ne 0) {
    Fail 1 @("构建失败（dotnet publish 退出码 $code）。")
}

# --- 校验 2：exe 必须存在 ---
$exe = Join-Path $outDir $exeName
if (-not (Test-Path $exe)) {
    Fail 1 @("退出码为 0 但 $exeName 不存在 —— 不视为成功。")
}

$item = Get-Item $exe
Write-Host ("    {0} = {1:N0} 字节，写入时间 {2}" -f $exeName, $item.Length, $item.LastWriteTime)

# --- 校验 3：exe 必须是本次写出的（防"锁文件留下旧 exe"假成功）---
if ($item.LastWriteTime -lt $started) {
    Fail 1 @(
        "过期产物: exe 不是本次构建写出的 —— 视为失败。",
        "运行中的实例锁住了 exe；请关闭后重试。")
}

# --- 校验 4：WinRT 投影程序集必须在位（合成宿主缺它直接初始化失败）---
$sdkNet = @(Get-ChildItem -Path $outDir -Recurse -Filter "Microsoft.Windows.SDK.NET.dll" -ErrorAction SilentlyContinue)
if ($sdkNet.Count -eq 0) {
    Fail 1 @(
        "输出中缺少 Microsoft.Windows.SDK.NET.dll。",
        "合成（composition）宿主会初始化失败并回退到 hwnd。请检查 TFM 是否仍带 Windows SDK 版本。")
}

# --- 第 4 步：数据搬回 portable ---
Write-Host ""
Write-Host "==> 把运行期数据搬回 portable ..." -ForegroundColor Cyan
if (-not (Restore-KeepData)) {
    Write-Host ""
    Write-Host "构建本身成功，但有数据未能搬回（见上方红字），请按提示手动处理。" -ForegroundColor Yellow
    Exit-DshBuild 1
}

$total = (Get-ChildItem -Path $outDir -Recurse -File | Measure-Object -Property Length -Sum).Sum

Write-Host ""
Write-Host "构建成功（数据已保留）: artifacts\portable\$exeName" -ForegroundColor Green
Write-Host ("    目录大小 : {0:N1} MB（{1} 个文件）" -f ($total / 1MB), (Get-ChildItem -Path $outDir -Recurse -File).Count)
Write-Host ("    模式     : {0}" -f $(if ($SelfContained) { "自包含" } else { "依赖框架" }))
if ($moved.Count -gt 0) {
    Write-Host ("    已还原   : {0}" -f ($moved -join " / "))
}
Write-Host ""
Exit-DshBuild 0

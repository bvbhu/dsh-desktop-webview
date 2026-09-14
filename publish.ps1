# publish.ps1 —— 构建便携版应用
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File .\publish.ps1
#       默认依赖框架模式（约 26 MB，目标机需装 .NET 8 桌面运行时）
#   powershell -ExecutionPolicy Bypass -File .\publish.ps1 -SelfContained
#       自包含模式（约 180 MB，目标机无需装 .NET）
#   powershell -ExecutionPolicy Bypass -File .\publish.ps1 -NoPause
#       自动化调用：结束时不停留（默认会等待按回车，窗口不自动关闭）

param(
    [switch]$SelfContained,
    [switch]$NoPause,
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Continue"
$project = Join-Path $PSScriptRoot "src/DshDesktop.App/DshDesktop.App.csproj"
$outDir  = Join-Path $PSScriptRoot "artifacts/portable"
$exeName = "DSH-Desktop-Webview.exe"

# 结束前停一下，防止双击运行时窗口一闪而过自动关闭。-NoPause 可跳过。
function Exit-Script([int]$code) {
    if (-not $NoPause) {
        Write-Host ""
        try { Read-Host "按回车键退出" | Out-Null } catch { }
    }
    exit $code
}

try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    $OutputEncoding = [System.Text.Encoding]::UTF8
} catch { }

Write-Host ""
Write-Host "==> DSH-Desktop-Webview 便携版发布" -ForegroundColor Cyan
Write-Host ("    模式: {0}" -f $(if ($SelfContained) { "自包含（附带运行时，约 180 MB）" } else { "依赖框架（约 26 MB，目标机需装 .NET 8 桌面运行时）" }))

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "    未找到 dotnet SDK，请先安装 .NET 8 SDK。" -ForegroundColor Red
    Exit-Script 1
}

# 运行中的实例会锁住目标 exe，导致发布失败并留下旧版本。
$running = @(Get-Process -Name 'DSH-Desktop-Webview' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Host ""
    Write-Host ("警告: 检测到 {0} 个运行中的实例 (pid {1})，输出 exe 被锁定无法覆盖。" -f $running.Count, (($running | ForEach-Object { $_.Id }) -join ', ')) -ForegroundColor Yellow
    Write-Host "      请先关闭它们，或执行: Get-Process DSH-Desktop-Webview | Stop-Process" -ForegroundColor Yellow
}

# 清空输出目录（理由见文件头规则 1）。
if (Test-Path (Join-Path $outDir "config.json")) {
    Write-Host ""
    Write-Host "提示: 输出目录里有运行期生成的 config.json，将随目录一起清空。" -ForegroundColor Yellow
    Write-Host "      想保留设置，请先把 portable 目录复制到别处再运行本脚本。" -ForegroundColor Yellow
}
# 永久删除输出目录（不进回收站）
if (Test-Path $outDir) {
    $wiped = $false
    try {
        [System.IO.Directory]::Delete($outDir, $true)
        $wiped = $true
    }
    catch {
        # 目录被占用（运行中的实例 / 资源管理器窗口）时到这里，交给兜底路径后再统一判定
    }
    if (-not $wiped) {
        & cmd.exe /c rmdir /s /q "$outDir" 2>$null
    }
    if (Test-Path $outDir) {
        Write-Host ""
        Write-Host ("清空 {0} 失败: 目录仍存在。" -f $outDir) -ForegroundColor Red
        Write-Host "多半是运行中的实例或资源管理器窗口占用了该目录，关掉后重试。" -ForegroundColor Red
        Exit-Script 1
    }
}
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

# 原生命令的输出先捕获再 Write-Host
$started = Get-Date

$cleanOut = & dotnet clean "$project" -c "$Configuration" --nologo 2>&1 | Out-String
$cleanCode = $LASTEXITCODE
if ($cleanOut.Trim().Length -gt 0) { Write-Host $cleanOut }

Write-Host ""
Write-Host "==> dotnet publish -> artifacts/portable" -ForegroundColor Cyan
$selfContainedFlag = if ($SelfContained) { "true" } else { "false" }
$publishOut = & dotnet publish "$project" -c "$Configuration" -r "$Runtime" --self-contained $selfContainedFlag -o "$outDir" --nologo 2>&1 | Out-String
$code = $LASTEXITCODE
Write-Host $publishOut
Write-Host ("    dotnet clean   退出码 = {0}" -f $cleanCode)
Write-Host ("    dotnet publish 退出码 = {0}" -f $code)

# ---- 校验 1：publish 退出码 ----
if ($code -ne 0) {
    Write-Host ""
    Write-Host "发布失败（dotnet publish 退出码 $code）。" -ForegroundColor Red
    Exit-Script 1
}

# ---- 校验 2：exe 必须存在 ----
$exe = Join-Path $outDir $exeName
if (-not (Test-Path $exe)) {
    Write-Host ""
    Write-Host "退出码为 0 但 $exe 不存在 —— 不视为成功。" -ForegroundColor Red
    Exit-Script 1
}

$item = Get-Item $exe
Write-Host ("    {0} = {1:N0} 字节，写入时间 {2}" -f $exeName, $item.Length, $item.LastWriteTime)

# ---- 校验 3：exe 必须是本次运行写出的（新鲜度，防“锁文件留下旧 exe”假成功）----
if ($item.LastWriteTime -lt $started) {
    Write-Host ""
    Write-Host "过期产物: exe 不是本次发布写出的 —— 视为失败。" -ForegroundColor Red
    Write-Host "运行中的实例锁住了 exe；请关闭后重试。" -ForegroundColor Yellow
    Exit-Script 1
}

# ---- 校验 4：WinRT 投影程序集必须在位（合成宿主缺它直接初始化失败）----
$sdkNet = @(Get-ChildItem -Path $outDir -Recurse -Filter "Microsoft.Windows.SDK.NET.dll" -ErrorAction SilentlyContinue)
if ($sdkNet.Count -eq 0) {
    Write-Host ""
    Write-Host "输出中缺少 Microsoft.Windows.SDK.NET.dll。" -ForegroundColor Red
    Write-Host "合成（composition）宿主会初始化失败并回退到 hwnd。请检查 TFM 是否仍带 Windows SDK 版本。" -ForegroundColor Red
    Exit-Script 1
}

$total = (Get-ChildItem -Path $outDir -Recurse -File | Measure-Object -Property Length -Sum).Sum

Write-Host ""
Write-Host "发布成功: artifacts\portable\$exeName" -ForegroundColor Green
Write-Host ("    目录大小 : {0:N1} MB（{1} 个文件）" -f ($total / 1MB), (Get-ChildItem -Path $outDir -Recurse -File).Count)
Write-Host ("    模式     : {0}" -f $(if ($SelfContained) { "自包含" } else { "依赖框架" }))
Write-Host ""
Write-Host "首次运行会在 exe 同目录生成 config.json / run.log / WebView2Profile / temp。"
Exit-Script 0
